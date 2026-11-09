using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Tuci.Sync;

public sealed record AccountRequest(string? Username, string? Password, string? DeviceName, bool Browser = false);
public sealed record PasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record AccountGrant(string Token, string VaultId, string UserId, string Username, string DeviceId);
public sealed record AccountProfile(string UserId, string Username, string VaultId, string DeviceId);

internal sealed partial class Database
{
    private const int PasswordRounds = 600_000;
    private static readonly byte[] DummySalt = SHA256.HashData("Tuci account password check"u8);
    private static string PasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(24);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordRounds, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${PasswordRounds}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    private static bool VerifyPassword(string password, string? stored)
    {
        var parts = stored?.Split('$');
        var salt = parts is { Length: 4 } ? Convert.FromBase64String(parts[2]) : DummySalt;
        var rounds = parts is { Length: 4 } ? int.Parse(parts[1], CultureInfo.InvariantCulture) : PasswordRounds;
        var expected = parts is { Length: 4 } ? Convert.FromBase64String(parts[3]) : new byte[32];
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, rounds, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && stored is not null;
    }
    private static (string Name, string Key) Username(string? value)
    {
        string name = (value ?? "").Trim().Normalize(NormalizationForm.FormKC);
        if (name.Length is < 2 or > 40 || name.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new RequestFault(400, "用户名需为 2–40 位中文、字母、数字或 . _ -。");
        return (name, name.ToUpperInvariant());
    }
    private static string Password(string? value)
    {
        if (value is null || value.Length is < 8 or > 128 || value.Any(char.IsControl))
            throw new RequestFault(400, "密码需为 8–128 位，不能包含控制字符。");
        return value;
    }
    private static string DeviceName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new RequestFault(400, "请输入有效的设备名。");
        return name;
    }
    public AccountGrant Register(AccountRequest request, Device? claim)
    {
        var (name, key) = Username(request.Username);
        var password = PasswordHash(Password(request.Password));
        var deviceName = DeviceName(request.DeviceName);
        using var db = Open(); db.StartWrite();
        if (db.Scalar("SELECT 1 FROM accounts WHERE namekey=$p0", key) is not null)
            throw new RequestFault(409, "这个用户名已被使用，请换一个。");
        string vault = claim?.VaultId ?? Wire.Id(), user = Wire.Id();
        if (claim is not null && db.Scalar("SELECT 1 FROM accounts WHERE vault=$p0", vault) is not null)
            throw new RequestFault(409, "这个词库已有账号，请使用原账号登录。");
        if (claim is not null && db.Scalar("SELECT 1 FROM devices WHERE id=$p0 AND vault=$p1", claim.Id, vault) is null)
            throw new RequestFault(401, "原设备凭证已失效，请重新绑定。");
        if (claim is null) db.Execute("INSERT INTO vaults(id) VALUES($p0)", vault);
        db.Execute("INSERT INTO accounts VALUES($p0,$p1,$p2,$p3,$p4,$p5)", user, name, key, vault, password, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // Claiming a legacy vault authorizes this device only. Other old tokens must log in or pair again.
        db.Execute("DELETE FROM codes WHERE vault=$p0", vault);
        var grant = NewSession(db, user, name, vault, deviceName);
        db.Commit(); return grant;
    }
    public AccountGrant Login(AccountRequest request)
    {
        var (_, key) = Username(request.Username);
        var password = Password(request.Password);
        var deviceName = DeviceName(request.DeviceName);
        string? user = null, name = null, vault = null, stored = null;
        using (var read = Open())
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var failures = read.Cmd("SELECT failures,since FROM auth_failures WHERE namekey=$p0", key))
            using (var row = failures.ExecuteReader())
                if (row.Read() && row.GetInt32(0) >= 10 && row.GetInt64(1) > now - 900)
                    throw new RequestFault(429, "该账号尝试过于频繁，请 15 分钟后再试。");
            using var cmd = read.Cmd("SELECT id,username,vault,password FROM accounts WHERE namekey=$p0", key);
            using var account = cmd.ExecuteReader();
            if (account.Read()) { user=account.GetString(0); name=account.GetString(1); vault=account.GetString(2); stored=account.GetString(3); }
        }
        bool valid = VerifyPassword(password, stored);
        using var db = Open(); db.StartWrite();
        if (!valid || db.Scalar("SELECT password FROM accounts WHERE id=$p0", user) as string != stored)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            db.Execute("DELETE FROM auth_failures WHERE since<$p0", now - 86400);
            db.Execute("""
                INSERT INTO auth_failures VALUES($p0,1,$p1)
                ON CONFLICT(namekey) DO UPDATE SET
                failures=CASE WHEN since<$p1-900 THEN 1 ELSE failures+1 END,
                since=CASE WHEN since<$p1-900 THEN $p1 ELSE since END
                """, key, now);
            db.Commit(); throw new RequestFault(401, "用户名或密码不正确。");
        }
        db.Execute("DELETE FROM auth_failures WHERE namekey=$p0", key);
        var grant = NewSession(db, user!, name!, vault!, deviceName);
        db.Commit(); return grant;
    }
    private static AccountGrant NewSession(DbSession db, string user, string name, string vault, string deviceName)
    {
        var token = Wire.Secret(32); var device = Wire.Id();
        db.Execute("INSERT INTO devices VALUES($p0,$p1,$p2,$p3)", device, vault, deviceName, Wire.Digest(token));
        db.Execute("INSERT INTO account_sessions VALUES($p0,$p1,$p2)", device, user, DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds());
        return new(token, vault, user, name, device);
    }
    public AccountProfile Profile(Device device)
    {
        using var db = Open();
        using var cmd = db.Cmd("SELECT id,username FROM accounts WHERE vault=$p0", device.VaultId);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new RequestFault(404, "当前词库还没有账号，请先注册认领。");
        return new(row.GetString(0), row.GetString(1), device.VaultId, device.Id);
    }
    public void ChangePassword(Device device, PasswordRequest request)
    {
        var current = Password(request.CurrentPassword);
        var next = Password(request.NewPassword);
        string? stored;
        using (var read = Open()) stored = read.Scalar("SELECT password FROM accounts WHERE vault=$p0", device.VaultId) as string;
        if (!VerifyPassword(current, stored)) throw new RequestFault(401, "原密码不正确。");
        var replacement = PasswordHash(next);
        using var db = Open(); db.StartWrite();
        if (db.Execute("UPDATE accounts SET password=$p0 WHERE vault=$p1 AND password=$p2", replacement, device.VaultId, stored) != 1)
            throw new RequestFault(409, "密码已发生变化，请重新登录。");
        db.Execute("DELETE FROM codes WHERE vault=$p0", device.VaultId);
        db.Execute("DELETE FROM devices WHERE vault=$p0 AND id!=$p1", device.VaultId, device.Id);
        db.Commit();
    }
}
