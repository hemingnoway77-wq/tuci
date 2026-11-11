using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VocabCollector.Core.Library;

namespace VocabCollector.Core.Sync;

/// <summary>A refusal explained by the service itself, e.g. an expired invite code.</summary>
public sealed class SyncRequestException(string message, HttpStatusCode status)
    : HttpRequestException(message, null, status);

public sealed record SyncOperation(string OperationId, string NoteId, long BaseVersion, string Path, string Markdown, bool Deleted,
    Dictionary<string, string>? Regions = null);
public sealed record SyncChange(long Seq, string NoteId, long Version, string Path, string Markdown, bool Deleted,
    Dictionary<string, string>? Regions = null);
public sealed record SyncPage(SyncChange[] Changes, long Cursor, bool HasMore);
public sealed record SyncReceipt(string NoteId, long Version, string Path, bool Conflict);
public sealed record SyncIdentity(string Token, string VaultId);
public sealed record AccountIdentity(string Token, string VaultId, string UserId, string Username, string DeviceId, bool CanInvite = false);
public sealed record AccountProfile(string UserId, string Username, string VaultId, string DeviceId, bool CanInvite = false);
/// <summary>One freshly minted invite. The plaintext code is shown once and never stored.</summary>
public sealed record InviteIssued(string Code, long ExpiresAt);
/// <summary>A previously issued invite as the server lists it: epochs in seconds.</summary>
public sealed record InviteSummary(long Created, long Expires, bool Used);
internal sealed record InviteList(InviteSummary[] Invites);

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
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct).ConfigureAwait(false);
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
    public Task<AccountIdentity> RegisterAsync(string username, string password, string deviceName, CancellationToken ct, string? inviteCode = null) =>
        SendAsync<AccountIdentity>("v1/account/register", new { username, password, deviceName, browser = false, inviteCode }, ct);
    public Task<AccountIdentity> LoginAsync(string username, string password, string deviceName, CancellationToken ct) =>
        SendAsync<AccountIdentity>("v1/account/login", new { username, password, deviceName, browser = false }, ct);
    public Task<AccountProfile> AccountAsync(CancellationToken ct) => SendAsync<AccountProfile>("v1/account/me", null, ct);
    public Task<InviteIssued> IssueInviteAsync(CancellationToken ct) => SendAsync<InviteIssued>("v1/invites", new { }, ct);
    public async Task<InviteSummary[]> InvitesAsync(CancellationToken ct) =>
        (await SendAsync<InviteList>("v1/invites", null, ct)).Invites;
    public Task<StatePage> StateChangesAsync(long cursor, CancellationToken ct) =>
        SendAsync<StatePage>("v1/state/changes?after=" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture), null, ct);
    public Task<StateReceipt> PushStateAsync(StateOperation operation, CancellationToken ct) =>
        SendAsync<StateReceipt>("v1/state/operations", operation, ct);
    public async Task<CloudPracticeBankPage> PracticeBanksAsync(IEnumerable<string> noteIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(noteIds);
        var submitted = noteIds.Take(33).ToArray();
        if (submitted.Length is < 1 or > 32 || submitted.Any(id => string.IsNullOrEmpty(id) || id.Length > 128 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            throw new ArgumentException("请一次查询 1–32 个有效笔记编号。", nameof(noteIds));
        var ids = submitted.Distinct(StringComparer.Ordinal).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "v1/practice/banks"))
        { Content = JsonContent.Create(new { noteIds = ids }) };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (vaultId != null) request.Headers.Add("X-Tuci-Vault", vaultId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        byte[] body;
        try { body = await ReadPracticeBanksBodyAsync(response.Content, ct).ConfigureAwait(false); }
        catch (InvalidDataException) when (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new SyncRequestException("账号会话已失效，请重新登录。", response.StatusCode); }
        if (!response.IsSuccessStatusCode)
        {
            using var boundedError = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(body) };
            throw await FailureAsync(boundedError, ct).ConfigureAwait(false);
        }
        var page = JsonSerializer.Deserialize<CloudPracticeBankPage>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16 });
        if (page?.Banks is null || page.Banks.Count > ids.Length) throw new InvalidDataException("云端语境响应格式无效。");
        var allowed = ids.ToHashSet(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bank in page.Banks)
        {
            ct.ThrowIfCancellationRequested();
            if (bank is null || bank.NoteId is null || !allowed.Contains(bank.NoteId) || !seen.Add(bank.NoteId) ||
                bank.Contexts is null || bank.Contexts.Count is < 1 or > 128)
                throw new InvalidDataException("云端语境响应包含范围外或不完整的词条。");
            try { _ = WordInput.Normalize(bank.Word); }
            catch (ArgumentException error) { throw new InvalidDataException("云端语境的词形无效。", error); }
            var senses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var context in bank.Contexts)
                if (context is null || string.IsNullOrWhiteSpace(context.SenseId) || context.SenseId.Length > 256 || !senses.Add(context.SenseId) ||
                    string.IsNullOrWhiteSpace(context.English) || context.English.Length > 8000 ||
                    string.IsNullOrWhiteSpace(context.Chinese) || context.Chinese.Length > 8000)
                    throw new InvalidDataException("云端语境响应包含不完整的义项。");
        }
        return page;
    }

    private static async Task<byte[]> ReadPracticeBanksBodyAsync(HttpContent content, CancellationToken ct)
    {
        const int limit = 2 * 1024 * 1024;
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("云端语境响应超过 2 MiB。");
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return body.ToArray();
            if (body.Length + read > limit) throw new InvalidDataException("云端语境响应超过 2 MiB。");
            body.Write(buffer, 0, read);
        }
    }
    public async Task LogoutAsync(CancellationToken ct) => await SendEmptyAsync("v1/account/logout", new {}, ct);
    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct) =>
        await SendEmptyAsync("v1/account/password", new { currentPassword, newPassword }, ct);
    private async Task SendEmptyAsync(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, path)) { Content = JsonContent.Create(body) };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (vaultId != null) request.Headers.Add("X-Tuci-Vault", vaultId);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct).ConfigureAwait(false);
    }
    // The account endpoints explain refusals in Chinese, which is far more useful to a
    // person than "Response status code does not indicate success".
    private static async Task<SyncRequestException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string message = $"服务器拒绝了请求（{(int)response.StatusCode}）。";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() is { Length: > 0 } text)
                message = text;
        }
        catch (JsonException) { }
        return new SyncRequestException(message, response.StatusCode);
    }
}
