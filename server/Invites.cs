using System.Security.Cryptography;
using System.Text;

namespace Tuci.Sync;

public sealed record InviteIssued(string Code, long ExpiresAt);
public sealed record InviteSummary(long Created, long Expires, bool Used);

internal sealed partial class Database
{
    // Registration is invite-only. Only the operator account may mint codes; the
    // server CLI can always mint one so a fresh deployment or a locked-out operator
    // is never trapped behind the gate.
    private const int InviteDays = 7;
    private const string InviteAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static string AdminKey =>
        (Environment.GetEnvironmentVariable("TUCI_INVITE_ADMIN") ?? "trinhrae").Trim().ToUpperInvariant();

    private static string NewInviteCode()
    {
        var chars = new char[12];
        for (int i = 0; i < chars.Length; i++) chars[i] = InviteAlphabet[RandomNumberGenerator.GetInt32(InviteAlphabet.Length)];
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }
    // People retype codes from chat messages: ignore case, spaces and dashes.
    private static string NormalizeInvite(string? raw) =>
        new((raw ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    public bool CanInvite(string vault)
    {
        using var db = Open();
        return db.Scalar("SELECT namekey FROM accounts WHERE vault=$p0", vault) as string == AdminKey;
    }
    public InviteIssued IssueInvite(Device device)
    {
        if (!CanInvite(device.VaultId)) throw new RequestFault(403, "只有管理员账号可以生成邀请码。");
        using var db = Open();
        string? issuer = db.Scalar("SELECT id FROM accounts WHERE vault=$p0", device.VaultId) as string;
        return Create(db, issuer ?? device.VaultId);
    }
    public InviteIssued IssueOperatorInvite() 
    {
        using var db = Open();
        return Create(db, "cli");
    }
    private static InviteIssued Create(DbSession connection, string issuer)
    {
        connection.StartWrite();
        var now = DateTimeOffset.UtcNow;
        long nowSeconds = now.ToUnixTimeSeconds();
        // Keep spent codes for a week so the operator can still see who joined; the
        // list is capped and these rows are a few dozen bytes each.
        connection.Execute("DELETE FROM invites WHERE expires <= $p0", nowSeconds - 7 * 86400);
        string code = NewInviteCode();
        long expires = now.AddDays(InviteDays).ToUnixTimeSeconds();
        connection.Execute("INSERT INTO invites(hash,created,expires,issuer) VALUES($p0,$p1,$p2,$p3)",
            Wire.Digest(NormalizeInvite(code)), nowSeconds, expires, issuer);
        connection.Commit();
        return new InviteIssued(code, expires);
    }
    internal static string ClaimInvite(DbSession connection, string? raw)
    {
        string code = NormalizeInvite(raw);
        if (code.Length != 12) throw new RequestFault(400, "请填写 12 位邀请码。");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string hash = Wire.Digest(code);
        // Single use: the UPDATE is the reservation, so two simultaneous registrations
        // can never spend the same code.
        var claimed = connection.Scalar(
            "UPDATE invites SET used=$p0 WHERE hash=$p1 AND used IS NULL AND expires>$p0 RETURNING hash", now, hash) as string;
        if (claimed is null) throw new RequestFault(401, "邀请码无效、已被使用或已过期。");
        return hash;
    }
    public List<InviteSummary> Invites(Device device)
    {
        if (!CanInvite(device.VaultId)) throw new RequestFault(403, "只有管理员账号可以查看邀请码。");
        using var db = Open();
        using var cmd = db.Cmd("SELECT created,expires,used FROM invites ORDER BY created DESC LIMIT 50");
        using var rows = cmd.ExecuteReader();
        List<InviteSummary> result = [];
        while (rows.Read())
            result.Add(new(rows.GetInt64(0), rows.GetInt64(1), !rows.IsDBNull(2)));
        return result;
    }
}
