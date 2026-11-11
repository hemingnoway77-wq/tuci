using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Tuci.Sync;

internal static class AccountWeb
{
    internal const string CookieName = "__Secure-tuci-session";
    private const string ProofCookie = "__Host-tuci-origin-proof";
    private const string ProofHeader = "X-Tuci-Origin-Proof";
    private const string OriginErrorHeader = "X-Tuci-Origin-Error";
    private const int ProofLifetime = 120;
    private const int ProofPayloadLength = 8 + 32;
    private const int ProofTokenLength = 96;
    private static readonly byte[] ProofKey = RandomNumberGenerator.GetBytes(32);
    private static string PublicOrigin => (Environment.GetEnvironmentVariable("TUCI_PUBLIC_ORIGIN") ?? "https://101.133.134.128").TrimEnd('/');
    private static RequestFault OriginFault() => new(403, "请从兔词自己的网页发起请求。");
    private static CookieOptions Cookie => new() { HttpOnly=true, Secure=true, SameSite=SameSiteMode.Strict,
        Path="/tuci-sync/", MaxAge=TimeSpan.FromDays(90), IsEssential=true };
    internal static string Authorization(HttpContext ctx) =>
        !string.IsNullOrEmpty(ctx.Request.Headers.Authorization) ? ctx.Request.Headers.Authorization.ToString() :
        ctx.Request.Cookies.TryGetValue(CookieName, out var token) ? "Bearer " + token : "";
    internal static void GuardOrigin(HttpContext ctx)
    {
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) return;
        var origin = ctx.Request.Headers.Origin.ToString();
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site == "cross-site") throw OriginFault();
        if (origin.Length > 0 && string.Equals(origin, PublicOrigin, StringComparison.OrdinalIgnoreCase)) return;
        if (site == "same-site") throw OriginFault();
        if (origin.Length == 0) return;
        if (origin == "null" && site == "same-origin") return;
        // Legacy WebKit can lack Fetch Metadata. A same-origin page can read
        // the JSON proof and send its matching host-only cookie and header.
        if (origin == "null" && site.Length == 0 && HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.HasJsonContentType())
        {
            var submitted = ctx.Request.Headers[ProofHeader].ToString();
            if (submitted.Length == ProofTokenLength && ctx.Request.Cookies.TryGetValue(ProofCookie, out var cookie) &&
                ProofRemaining(cookie) > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(submitted))) return;
            ctx.Response.Headers[OriginErrorHeader] = "proof-required";
        }
        throw OriginFault();
    }

    private static int ProofRemaining(string? token)
    {
        if (token?.Length != ProofTokenLength) return 0;
        byte[] bytes;
        try { bytes = WebEncoders.Base64UrlDecode(token); }
        catch (FormatException) { return 0; }
        if (bytes.Length != ProofPayloadLength + 32) return 0;
        var remaining = BinaryPrimitives.ReadInt64BigEndian(bytes) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining is <= 0 or > ProofLifetime) return 0;
        var signature = HMACSHA256.HashData(ProofKey, bytes.AsSpan(0, ProofPayloadLength));
        return CryptographicOperations.FixedTimeEquals(bytes.AsSpan(ProofPayloadLength), signature) ? (int)remaining : 0;
    }

    private static object IssueOriginProof(HttpContext ctx)
    {
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        var origin = ctx.Request.Headers.Origin.ToString();
        if (site is not ("" or "same-origin") || origin.Length > 0 && origin != "null" &&
            !string.Equals(origin, PublicOrigin, StringComparison.OrdinalIgnoreCase)) throw OriginFault();
        ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        var previous = ctx.Request.Cookies[ProofCookie];
        var remaining = ProofRemaining(previous);
        if (remaining > 5) return new { proof = previous, expiresIn = remaining };
        var bytes = new byte[ProofPayloadLength + 32];
        BinaryPrimitives.WriteInt64BigEndian(bytes, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ProofLifetime);
        RandomNumberGenerator.Fill(bytes.AsSpan(8, 32));
        HMACSHA256.HashData(ProofKey, bytes.AsSpan(0, ProofPayloadLength), bytes.AsSpan(ProofPayloadLength));
        var proof = WebEncoders.Base64UrlEncode(bytes);
        ctx.Response.Cookies.Append(ProofCookie, proof, new CookieOptions { HttpOnly = true, Secure = true,
            SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromSeconds(ProofLifetime), IsEssential = true });
        return new { proof, expiresIn = ProofLifetime };
    }
    private static object Response(HttpContext ctx, Database db, AccountRequest input, AccountGrant grant)
    {
        if (!input.Browser) return grant;
        ctx.Response.Cookies.Append(CookieName, grant.Token, Cookie);
        return new AccountProfile(grant.UserId, grant.Username, grant.VaultId, grant.DeviceId, db.CanInvite(grant.VaultId));
    }
    public static void Map(WebApplication app, Database db)
    {
        app.MapGet("/v1/account/origin-proof", IssueOriginProof).RequireRateLimiting("account");
        app.MapPost("/v1/account/register", (HttpContext ctx, AccountRequest input) =>
        {
            // Only an explicit legacy-device token can claim a vault. A browser's
            // expired or unrelated shared cookie must never silently select a vault.
            var authorization = ctx.Request.Headers.Authorization.ToString();
            var claim = authorization.Length == 0 ? null : db.Authenticate(authorization);
            return Response(ctx, db, input, db.Register(input, claim));
        }).RequireRateLimiting("account");
        app.MapPost("/v1/account/login", (HttpContext ctx, AccountRequest input) => Response(ctx, db, input, db.Login(input)))
            .RequireRateLimiting("account");
        app.MapPost("/v1/invites", (HttpContext ctx) => db.IssueInvite((Device)ctx.Items["device"]!))
            .WithMetadata(new DeviceRequired()).RequireRateLimiting("account");
        app.MapGet("/v1/invites", (HttpContext ctx) => new { invites = db.Invites((Device)ctx.Items["device"]!) })
            .WithMetadata(new DeviceRequired());
        app.MapGet("/v1/account/me", (HttpContext ctx) => db.Profile((Device)ctx.Items["device"]!)).WithMetadata(new DeviceRequired());
        app.MapPost("/v1/account/logout", (HttpContext ctx) =>
        {
            db.Revoke(((Device)ctx.Items["device"]!).Id);
            ctx.Response.Cookies.Delete(CookieName, Cookie);
            return new { ok=true };
        }).WithMetadata(new DeviceRequired());
        app.MapPost("/v1/account/password", (HttpContext ctx, PasswordRequest input) =>
        {
            db.ChangePassword((Device)ctx.Items["device"]!, input);
            return new { ok=true };
        }).WithMetadata(new DeviceRequired()).RequireRateLimiting("account");
        app.MapGet("/v1/account/devices", (HttpContext ctx) => db.Devices(((Device)ctx.Items["device"]!).VaultId))
            .WithMetadata(new DeviceRequired());
    }
}
