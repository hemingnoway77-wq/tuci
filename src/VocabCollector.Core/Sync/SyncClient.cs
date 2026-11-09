using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VocabCollector.Core.Sync;

public sealed record SyncOperation(string OperationId, string NoteId, long BaseVersion, string Path, string Markdown, bool Deleted,
    Dictionary<string, string>? Regions = null);
public sealed record SyncChange(long Seq, string NoteId, long Version, string Path, string Markdown, bool Deleted,
    Dictionary<string, string>? Regions = null);
public sealed record SyncPage(SyncChange[] Changes, long Cursor, bool HasMore);
public sealed record SyncReceipt(string NoteId, long Version, string Path, bool Conflict);
public sealed record SyncIdentity(string Token, string VaultId);
public sealed record AccountIdentity(string Token, string VaultId, string UserId, string Username, string DeviceId);
public sealed record AccountProfile(string UserId, string Username, string VaultId, string DeviceId);

public interface ISyncRemote
{
    Task<SyncPage> ChangesAsync(long cursor, CancellationToken ct);
    Task<SyncReceipt> PushAsync(SyncOperation operation, CancellationToken ct);
}

public sealed class SyncClient(HttpClient http, Uri server, string? token = null, string? vaultId = null) : ISyncRemote, ISyncStateRemote
{
    public static Uri Server(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入有效的 HTTPS 同步服务地址。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private async Task<T> SendAsync<T>(string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(server, path));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (vaultId != null) request.Headers.Add("X-Tuci-Vault", vaultId);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false) ?? throw new JsonException("同步服务响应缺少内容。");
    }
    public Task<SyncIdentity> RedeemAsync(string code, string deviceName, CancellationToken ct) =>
        SendAsync<SyncIdentity>("v1/redeem", new { code, deviceName }, ct);
    private sealed record PairCode(string Code);
    public async Task<string> PairAsync(CancellationToken ct) => (await SendAsync<PairCode>("v1/pair", new {}, ct)).Code;
    public Task<SyncReceipt> PushAsync(SyncOperation operation, CancellationToken ct) =>
        SendAsync<SyncReceipt>("v1/operations", operation, ct);
    public Task<SyncPage> ChangesAsync(long cursor, CancellationToken ct) =>
        SendAsync<SyncPage>("v1/changes?after=" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture), null, ct);
    public Task<AccountIdentity> RegisterAsync(string username, string password, string deviceName, CancellationToken ct) =>
        SendAsync<AccountIdentity>("v1/account/register", new { username, password, deviceName, browser = false }, ct);
    public Task<AccountIdentity> LoginAsync(string username, string password, string deviceName, CancellationToken ct) =>
        SendAsync<AccountIdentity>("v1/account/login", new { username, password, deviceName, browser = false }, ct);
    public Task<AccountProfile> AccountAsync(CancellationToken ct) => SendAsync<AccountProfile>("v1/account/me", null, ct);
    public Task<StatePage> StateChangesAsync(long cursor, CancellationToken ct) =>
        SendAsync<StatePage>("v1/state/changes?after=" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture), null, ct);
    public Task<StateReceipt> PushStateAsync(StateOperation operation, CancellationToken ct) =>
        SendAsync<StateReceipt>("v1/state/operations", operation, ct);
    public async Task LogoutAsync(CancellationToken ct) => await SendEmptyAsync("v1/account/logout", new {}, ct);
    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct) =>
        await SendEmptyAsync("v1/account/password", new { currentPassword, newPassword }, ct);
    private async Task SendEmptyAsync(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, path)) { Content = JsonContent.Create(body) };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (vaultId != null) request.Headers.Add("X-Tuci-Vault", vaultId);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
