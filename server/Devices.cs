namespace Tuci.Sync;

internal sealed partial class Database
{
    public (string VaultId, string Code) Invite(string? existingVault)
    {
        using var db = Open();
        db.StartWrite();
        string vault = existingVault ?? Wire.Id();
        if (existingVault is null)
            db.Execute("INSERT INTO vaults(id) VALUES($p0)", vault);
        else if (db.Scalar("SELECT 1 FROM vaults WHERE id=$p0", vault) is null)
            throw new RequestFault(404, "词库不存在。");
        string code = AddCode(db, vault, null);
        db.Commit();
        return (vault, code);
    }
    public string Pair(Device device)
    {
        using var db = Open();
        db.StartWrite();
        // Only the latest outstanding code from this device remains usable.
        db.Execute("DELETE FROM codes WHERE issuer=$p0", device.Id);
        string code = AddCode(db, device.VaultId, device.Id);
        db.Commit();
        return code;
    }
    private static string AddCode(DbSession db, string vault, string? issuer)
    {
        var now = DateTimeOffset.UtcNow;
        db.Execute("DELETE FROM codes WHERE expires <= $p0", now.ToUnixTimeSeconds());
        string code = Wire.Secret(issuer is null ? 16 : 6);
        var expires = issuer is null ? now.AddHours(24) : now.AddMinutes(10);
        db.Execute("INSERT INTO codes VALUES($p0,$p1,$p2,$p3)", Wire.Digest(code), vault, expires.ToUnixTimeSeconds(), issuer);
        return code;
    }
    public DeviceGrant Redeem(RedeemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 128 ||
            string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 80 || request.DeviceName.Any(char.IsControl))
            throw new RequestFault(400, "请输入有效凭证和设备名。");
        string hash = Wire.Digest(request.Code.Trim().ToUpperInvariant());
        using var db = Open();
        db.StartWrite();
        var vault = db.Scalar("DELETE FROM codes WHERE hash=$p0 AND expires>$p1 RETURNING vault",
            hash, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) as string;
        if (vault is null) throw new RequestFault(401, "凭证无效或已过期。");
        string token = Wire.Secret(32);
        db.Execute("INSERT INTO devices VALUES($p0,$p1,$p2,$p3)", Wire.Id(), vault, request.DeviceName.Trim(), Wire.Digest(token));
        db.Commit();
        return new(token, vault);
    }
    public Device Authenticate(string? authorization)
    {
        if (authorization is { Length: 71 } && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            using var db = Open();
            using var cmd = db.Cmd("SELECT id,vault FROM devices WHERE tokenhash=$p0", Wire.Digest(authorization[7..]));
            using var row = cmd.ExecuteReader();
            if (row.Read()) return new(row.GetString(0), row.GetString(1));
        }
        throw new RequestFault(401, "请重新绑定设备。");
    }
    public List<object> Devices(string vault)
    {
        using var db = Open(); using var cmd = db.Cmd("SELECT id,name FROM devices WHERE vault=$p0 ORDER BY name,id", vault);
        using var rows = cmd.ExecuteReader();
        List<object> result = [];
        while (rows.Read()) result.Add(new { deviceId = rows.GetString(0), name = rows.GetString(1) });
        return result;
    }
    public void Revoke(string id)
    {
        using var db = Open();
        if (db.Execute("DELETE FROM devices WHERE id=$p0", id) == 0) throw new RequestFault(404, "设备不存在。");
    }
}
