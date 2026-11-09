namespace Tuci.Sync;

internal static class AccountWeb
{
    internal const string CookieName = "__Secure-tuci-session";
    private static CookieOptions Cookie => new() { HttpOnly=true, Secure=true, SameSite=SameSiteMode.Strict,
        Path="/tuci-sync/", MaxAge=TimeSpan.FromDays(90), IsEssential=true };
    internal static string Authorization(HttpContext ctx) =>
        !string.IsNullOrEmpty(ctx.Request.Headers.Authorization) ? ctx.Request.Headers.Authorization.ToString() :
        ctx.Request.Cookies.TryGetValue(CookieName, out var token) ? "Bearer " + token : "";
    internal static void GuardOrigin(HttpContext ctx)
    {
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) return;
        string expected = Environment.GetEnvironmentVariable("TUCI_PUBLIC_ORIGIN") ?? "https://101.133.134.128";
        var origin = ctx.Request.Headers.Origin.ToString();
        // Fetch 3.2 serializes a same-origin POST's Origin as null under no-referrer.
        // Older installed pages can still use that policy. Only browser-generated
        // same-origin Fetch Metadata proves this case; null alone is never trusted.
        var sameOriginOpaque = origin == "null" && ctx.Request.Headers["Sec-Fetch-Site"] == "same-origin";
        if (ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            origin.Length > 0 && !string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase) && !sameOriginOpaque)
            throw new RequestFault(403, "请从兔词自己的网页发起请求。");
    }
    private static object Response(HttpContext ctx, AccountRequest input, AccountGrant grant)
    {
        if (!input.Browser) return grant;
        ctx.Response.Cookies.Append(CookieName, grant.Token, Cookie);
        return new AccountProfile(grant.UserId, grant.Username, grant.VaultId, grant.DeviceId);
    }
    public static void Map(WebApplication app, Database db)
    {
        app.MapPost("/v1/account/register", (HttpContext ctx, AccountRequest input) =>
        {
            // Only an explicit legacy-device token can claim a vault. A browser's
            // expired or unrelated shared cookie must never silently select a vault.
            var authorization = ctx.Request.Headers.Authorization.ToString();
            var claim = authorization.Length == 0 ? null : db.Authenticate(authorization);
            return Response(ctx, input, db.Register(input, claim));
        }).RequireRateLimiting("account");
        app.MapPost("/v1/account/login", (HttpContext ctx, AccountRequest input) => Response(ctx, input, db.Login(input)))
            .RequireRateLimiting("account");
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
