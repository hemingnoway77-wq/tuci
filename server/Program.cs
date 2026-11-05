using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Tuci.Sync;

var db = new Database(Environment.GetEnvironmentVariable("TUCI_DB") ?? Path.Combine(AppContext.BaseDirectory, "data", "tuci.sqlite3"));
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    try
    {
        object output = args switch
        {
            ["invite"] => Invitation(null),
            ["invite", var vault] => Invitation(vault),
            ["devices", var vault] => db.Devices(vault),
            ["revoke", var device] => Revoke(device),
            ["backup", var file] => Backup(file),
            _ => throw new ArgumentException("invite [vaultId] | devices <vaultId> | revoke <deviceId> | backup <new-file>")
        };
        Console.WriteLine(JsonSerializer.Serialize(output));
        return 0;
    }
    catch (Exception e) when (e is ArgumentException or RequestFault or IOException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}
object Invitation(string? vault)
{
    var issued = db.Invite(vault);
    return new { vaultId = issued.VaultId, code = issued.Code };
}
object Revoke(string id) { db.Revoke(id); return new { revoked = true }; }
object Backup(string path) { db.Backup(path); return new { backup = path }; }

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    // At the HTTPS proxy all requests may share an address. These generous authenticated
    // limits are independent of the stricter short-code guessing limit.
    options.AddPolicy("redeem", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("pair", context => RateLimitPartition.GetFixedWindowLimiter(
        ((Device)context.Items["device"]!).Id,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (Environment.GetEnvironmentVariable("TUCI_TRUST_LOOPBACK_PROXY") == "1")
{
    // The local TLS gateway must overwrite X-Forwarded-For with the actual peer address.
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    forwarded.KnownProxies.Add(IPAddress.Loopback);
    forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
    app.UseForwardedHeaders(forwarded);
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DeviceRequired>() is not null)
            context.Items["device"] = db.Authenticate(context.Request.Headers.Authorization.ToString());
        await next(context);
    }
    catch (RequestFault problem)
    {
        context.Response.StatusCode = problem.Status;
        await context.Response.WriteAsJsonAsync(new { error = problem.Message });
    }
});
app.UseRateLimiter();
app.MapGet("/health", () => new { ok = true });
app.MapPost("/v1/redeem", (RedeemRequest input) => db.Redeem(input)).RequireRateLimiting("redeem");
app.MapPost("/v1/pair", (HttpContext ctx) => new { code = db.Pair((Device)ctx.Items["device"]!) })
    .WithMetadata(new DeviceRequired()).RequireRateLimiting("pair");
app.MapGet("/v1/changes", (HttpContext ctx, long? after) => db.Changes(((Device)ctx.Items["device"]!).VaultId, after ?? 0))
    .WithMetadata(new DeviceRequired());
app.MapPost("/v1/operations", (HttpContext ctx, Operation op) => db.Apply(((Device)ctx.Items["device"]!).VaultId, op))
    .WithMetadata(new DeviceRequired());
app.Run();
return 0;
internal sealed class DeviceRequired;
