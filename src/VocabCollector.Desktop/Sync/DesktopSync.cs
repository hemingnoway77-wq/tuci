using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;
using VocabCollector.Core.Library;

namespace VocabCollector.Desktop;

public enum AccountAccess { LoginRequired, Checking, Online, Offline }

public sealed class DesktopSync : IDisposable
{
    private sealed record Binding(string Server, string VaultId, VaultOptions Vault, string EncryptedToken,
        string? UserId = null, string? Username = null, string? DeviceId = null, DateTimeOffset? ExpiresAt = null, bool CanInvite = false);
    private readonly string _dir;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _debounce;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HashSet<Task> _revocations = [];
    private CancellationTokenSource _sessionOperations = new();
    private readonly HttpClient _http;
    private Binding? _binding;
    private FileSystemWatcher? _watcher;
    private AccountStateSync? _stateSync;
    private IReadOnlyList<SyncedNote> _notes = [];
    private string ConfigPath => Path.Combine(_dir, "connection.json");
    private string LocalLockPath => Path.Combine(_dir, "signed-out");
    public string Server => _binding?.Server ?? "";
    public string Folder => _binding == null ? "" : Path.Combine(_binding.Vault.RootPath, _binding.Vault.OutputSubfolder);
    public bool Connected => _binding != null;
    public bool SignedIn => _binding?.UserId is not null;
    public AccountAccess Access { get; private set; } = AccountAccess.LoginRequired;
    public bool CanUseAccount => SignedIn && Access is AccountAccess.Online or AccountAccess.Offline;
    public string Username => _binding?.Username ?? "";
    public bool CanInvite => _binding?.CanInvite == true;
    public string DeviceId => _binding?.DeviceId ?? "";
    public VaultOptions? ActiveVault => _binding?.Vault;
    public string? AccountStateDirectory => _binding is { UserId: not null } b ? Path.Combine(_dir, "accounts", AccountKey(b)) : null;
    public IReadOnlyList<SyncedNote> Notes => _notes;
    public bool LastBindingWasRegistration { get; private set; }
    public IReadOnlyList<SyncStateEntry> StateEntries { get; private set; } = [];
    public long StateSyncGeneration { get; private set; }
    public Func<IReadOnlyList<SyncedNote>, CancellationToken, Task<IEnumerable<SyncStateEntry>>>? ExportStateAsync { get; set; }
    public Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? GetNoteIdentitiesAsync { get; set; }
    public Func<IReadOnlyList<SyncStateEntry>, CancellationToken, Task>? ImportStateAsync { get; set; }
    public Func<IReadOnlyList<CloudPracticeBank>, CancellationToken, Task>? ImportPracticeBanksAsync { get; set; }
    public bool IsBusy => _gate.CurrentCount == 0;
    public string Status { get; private set; } = "尚未绑定词库。";
    public event EventHandler? Updated;
    public event EventHandler? BindingChanged;
    public event EventHandler? AccessChanged;

    public DesktopSync(string directory, Dispatcher dispatcher, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _dir = Path.Combine(directory, "sync");
        _dispatcher = dispatcher;
        _poll = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _debounce = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += async (_, _) => await Tick();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Tick(); };
        try
        {
            if (File.Exists(ConfigPath) && !File.Exists(LocalLockPath))
            {
                _binding = JsonSerializer.Deserialize<Binding>(File.ReadAllText(ConfigPath)) ?? throw new JsonException();
                SyncClient.Server(_binding.Server);
                if (_binding.UserId is not null && _binding.ExpiresAt is null)
                    _binding = _binding with { ExpiresAt = new DateTimeOffset(File.GetLastWriteTimeUtc(ConfigPath)).AddDays(90) };
                InitializeState(_binding);
                Access = SignedIn ? AccountAccess.Checking : AccountAccess.LoginRequired;
                SetStatus("等待同步。");
                BeginWatching();
            }
        }
        catch (Exception) { _binding = null; _stateSync = null; SetStatus("同步设置暂不可用，请检查目录或重新绑定。"); }
        _poll.Start();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string AccountKey(Binding b) => Hash(b.Server + "\n" + b.VaultId);
    private string ProfileDirectory(Binding b) => Path.Combine(_dir, "accounts", AccountKey(b));
    private string StateFile(Binding b) => Path.Combine(_dir, Hash(AccountKey(b) + "\n" + VaultPaths.WordRoot(b.Vault).ToUpperInvariant()) + ".json");
    private static string MarkerPath(string root)
    {
        var marker = Path.Combine(root, ".vocabcollector", "sync-binding");
        VaultPaths.EnsureInside(root, marker);
        return marker;
    }

    private void InitializeState(Binding binding)
    {
        _notes = [];
        StateEntries = [];
        StateSyncGeneration = 0;
        var index = StateFile(binding) + ".notes.json";
        try { if (File.Exists(index)) _notes = JsonSerializer.Deserialize<SyncedNote[]>(File.ReadAllText(index)) ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        _stateSync = binding.UserId is null ? null : new AccountStateSync(Path.Combine(AccountStateDirectory!, "state.json"), Client(binding));
    }

    private async Task InstallBinding(Binding binding)
    {
        var root = VaultPaths.WordRoot(binding.Vault);
        Directory.CreateDirectory(root);
        var marker = MarkerPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        var key = AccountKey(binding);
        if (File.Exists(marker) && File.ReadAllText(marker) != key)
            throw new InvalidOperationException("此目录属于另一个云端词库。请为当前账号选择独立目录；原词库会保留。");
        if (!File.Exists(marker))
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes(key)); stream.Flush(true);
        }
        if (binding.UserId is not null) await SaveJson(Path.Combine(ProfileDirectory(binding), "library-binding.json"), binding.Vault, _stop.Token);
        await SaveJson(ConfigPath, binding, _stop.Token);
        if (File.Exists(LocalLockPath)) File.Delete(LocalLockPath);
        _binding = binding;
        InitializeState(binding);
        BeginWatching();
        SetAccess(binding.UserId is null ? AccountAccess.LoginRequired : AccountAccess.Online);
        BindingChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task SaveJson<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task SignIn(string url, string username, string password, VaultOptions vault, bool register = false, string? inviteCode = null) =>
        SignInCore(url, username, password, vault, register, privateCache: false, inviteCode);

    public Task SignInPrivate(string url, string username, string password, bool register = false, string? inviteCode = null) =>
        SignInCore(url, username, password, null, register, privateCache: true, inviteCode);

    private VaultOptions PrivateVault(string server, AccountIdentity identity)
    {
        var directory = Path.Combine(_dir, "accounts", Hash(server + "\n" + identity.VaultId));
        var saved = Path.Combine(directory, "library-binding.json");
        if (File.Exists(saved))
        {
            var vault = JsonSerializer.Deserialize<VaultOptions>(File.ReadAllText(saved)) ?? throw new InvalidDataException("本机账号词库记录损坏，原目录已保留。");
            // Only the exact directory previously recorded for this cloud vault can be reused.
            var root = VaultPaths.WordRoot(vault);
            var marker = MarkerPath(root);
            if (!File.Exists(marker) || File.ReadAllText(marker) != Hash(server + "\n" + identity.VaultId))
                throw new InvalidDataException("本机账号词库绑定记录不符，原目录已保留，请检查本机记录。");
            return vault;
        }
        var parent = Path.Combine(directory, "vault");
        Directory.CreateDirectory(parent);
        return new(parent, "英语单词", true);
    }

    private async Task SignInCore(string url, string username, string password, VaultOptions? vault, bool register, bool privateCache, string? inviteCode = null)
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            if (SignedIn || _binding is not null && !register && !privateCache) throw new InvalidOperationException("请先退出当前账号或断开配对，再登录其他账号。");
            var server = SyncClient.Server(url);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("请填写用户名和密码。");
            if (password.Length is < 8 or > 128) throw new ArgumentException("密码长度应为 8–128 位。");
            var client = !privateCache && register && _binding is { } old && old.Server == server.AbsoluteUri ? Client(old) : new SyncClient(_http, server);
            var identity = register
                ? await client.RegisterAsync(username.Trim(), password, Environment.MachineName, _stop.Token, inviteCode)
                : await client.LoginAsync(username.Trim(), password, Environment.MachineName, _stop.Token);
            if (string.IsNullOrWhiteSpace(identity.Token) || string.IsNullOrWhiteSpace(identity.VaultId) || string.IsNullOrWhiteSpace(identity.UserId) ||
                string.IsNullOrWhiteSpace(identity.Username) || string.IsNullOrWhiteSpace(identity.DeviceId))
                throw new InvalidDataException("服务器未返回完整账号身份。");
            var encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(identity.Token), null, DataProtectionScope.CurrentUser));
            // The login grant carries no role information; ask the profile once so the
            // account window knows whether this account may mint invite codes.
            var canInvite = false;
            try { canInvite = (await new SyncClient(_http, server, identity.Token).AccountAsync(_stop.Token)).CanInvite; }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
            var binding = new Binding(server.AbsoluteUri, identity.VaultId, privateCache ? PrivateVault(server.AbsoluteUri, identity) : vault!, encrypted,
                identity.UserId, identity.Username, identity.DeviceId, DateTimeOffset.UtcNow.AddDays(90), canInvite);
            LastBindingWasRegistration = register && !privateCache;
            try { await InstallBinding(binding); }
            catch
            {
                try { await new SyncClient(_http, server, identity.Token).LogoutAsync(_stop.Token); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
                throw;
            }
            SetStatus("已登录 " + identity.Username + "，正在同步词库与学习状态。");
        }
        finally { _gate.Release(); }
        Request();
    }

    private void SetAccess(AccountAccess access)
    {
        if (Access == access) return;
        Access = access; AccessChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> VerifySessionAsync(CancellationToken ct = default)
    {
        using var linked = LinkSession(ct);
        try
        {
            await _gate.WaitAsync(linked.Token);
            try { return await VerifySessionCore(linked.Token); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !ct.IsCancellationRequested && !_stop.IsCancellationRequested)
        { return false; }
    }

    private CancellationTokenSource LinkSession(CancellationToken ct = default) =>
        CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, _sessionOperations.Token);

    private async Task<bool> VerifySessionCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_binding is not { UserId: not null } binding)
        {
            SetAccess(AccountAccess.LoginRequired); return false;
        }
        if (binding.ExpiresAt is not { } expires || expires <= DateTimeOffset.UtcNow)
        {
            await RequireLogin("账号会话已到期，请重新登录。", ct); return false;
        }
        try
        {
            var profile = await Client(binding).AccountAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (profile.UserId != binding.UserId || profile.VaultId != binding.VaultId || profile.DeviceId != binding.DeviceId ||
                string.IsNullOrWhiteSpace(profile.Username))
            {
                await RequireLogin("账号身份与本机缓存不符，请重新登录。", ct); return false;
            }
            if (binding.CanInvite != profile.CanInvite)
            {
                binding = binding with { CanInvite = profile.CanInvite };
                _binding = binding;
                await SaveJson(ConfigPath, binding, ct);
                ct.ThrowIfCancellationRequested();
                Updated?.Invoke(this, EventArgs.Empty);
            }
            await SaveJson(Path.Combine(ProfileDirectory(binding), "library-binding.json"), binding.Vault, ct);
            ct.ThrowIfCancellationRequested();
            SetAccess(AccountAccess.Online); return true;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await RequireLogin("账号会话已失效，请重新登录。本机词库与未同步改动已保留。", ct); return false;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            SetAccess(AccountAccess.Offline);
            SetStatus("暂时无法连接服务器，正在使用此账号的本机缓存。");
            return true;
        }
    }

    private async Task RequireLogin(string message, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        SetAccess(AccountAccess.LoginRequired);
        if (_binding is { UserId: not null } binding)
        {
            try { await SaveJson(Path.Combine(ProfileDirectory(binding), "library-binding.json"), binding.Vault, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (ct.IsCancellationRequested) return;
        Disconnect(); SetStatus(message);
    }

    public async Task<InviteIssued> IssueInvite(CancellationToken ct = default)
    {
        using var linked = LinkSession(ct);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (!SignedIn || _binding is null) throw new InvalidOperationException("请先登录账号。");
            if (!CanInvite) throw new InvalidOperationException("当前账号没有生成邀请码的权限。");
            var issued = await Client(_binding).IssueInviteAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            SetStatus("已生成邀请码，7 天内有效，仅可使用一次。");
            return issued;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RefreshInvites(CancellationToken ct = default)
    {
        using var linked = LinkSession(ct);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (!SignedIn || _binding is null) throw new InvalidOperationException("请先登录账号。");
            if (!CanInvite) throw new InvalidOperationException("当前账号没有查看邀请码的权限。");
            var invites = await Client(_binding).InvitesAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (invites.Length == 0) return "还没有生成过邀请码。";
            return string.Join(Environment.NewLine, invites.Select(Describe));
        }
        finally { _gate.Release(); }
    }

    private static string Describe(InviteSummary invite)
    {
        static string Local(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("MM-dd HH:mm");
        var created = Local(invite.Created);
        var expires = Local(invite.Expires);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var state = invite.Used ? "已使用" : invite.Expires <= now ? "已过期" : "未使用";
        return created + " 生成 · " + expires + " 过期 · " + state;
    }

    public async Task ChangePassword(string currentPassword, string newPassword)
    {
        using var linked = LinkSession();
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (!SignedIn) throw new InvalidOperationException("请先登录账号。");
            if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword)) throw new ArgumentException("请填写当前密码和新密码。");
            if (newPassword.Length is < 8 or > 128) throw new ArgumentException("新密码长度应为 8–128 位。");
            await Client(_binding!).ChangePasswordAsync(currentPassword, newPassword, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            SetStatus("密码已更新。");
        }
        finally { _gate.Release(); }
    }

    public async Task Logout()
    {
        if (_binding is not { } binding) return;
        Disconnect();
        var signedOut = _sessionOperations.Token;
        SetStatus("已退出账号，本地词库与学习记录保留。");
        if (binding.UserId is null) return;

        var revoke = RevokeSessionAsync(binding, signedOut);
        _revocations.Add(revoke);
        try { await revoke; }
        finally { _revocations.Remove(revoke); }
    }

    internal Task FinishRevocationsAsync() => Task.WhenAll(_revocations.ToArray());

    private async Task RevokeSessionAsync(Binding binding, CancellationToken signedOut)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // Older installations can have only a connection record. Preserve their
            // vault mapping before a new login needs to read it.
            var profile = Path.Combine(ProfileDirectory(binding), "library-binding.json");
            if (!File.Exists(profile))
            {
                await _gate.WaitAsync(deadline.Token);
                try
                {
                    if (!File.Exists(profile)) await SaveJson(profile, binding.Vault, deadline.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                finally { _gate.Release(); }
            }
            await Client(binding).LogoutAsync(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or CryptographicException or FormatException)
        {
            if (_binding is null && !signedOut.IsCancellationRequested && !_stop.IsCancellationRequested)
                SetStatus("本机已退出。服务器上的此设备会话未能撤销。");
        }
    }

    public async Task QueueStateAsync(string key, object value)
    {
        var sync = _stateSync;
        if (sync is null) return;
        await sync.EnqueueAsync([new SyncStateEntry(key, JsonSerializer.SerializeToElement(value))], _stop.Token);
        Request();
    }

    public async Task RestoreLocalStateAsync(CancellationToken ct = default)
    {
        var states = _stateSync;
        if (states is null) return;
        var entries = await states.ReadAsync(ct);
        if (!ReferenceEquals(states, _stateSync)) return;
        StateEntries = entries;
        if (ImportStateAsync is { } import) await import(entries, ct);
    }

    public async Task Bind(string url, string code, VaultOptions vault)
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            if (_binding != null) throw new InvalidOperationException("请先解除当前设备绑定。");
            var uri = SyncClient.Server(url);
            var root = VaultPaths.WordRoot(vault);
            Directory.CreateDirectory(root);
            var marker = MarkerPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            var identity = await new SyncClient(_http, uri).RedeemAsync(code.Trim(), Environment.MachineName, _stop.Token);
            if (string.IsNullOrWhiteSpace(identity.Token) || string.IsNullOrWhiteSpace(identity.VaultId))
                throw new InvalidDataException("服务器未返回有效的设备身份。");
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(identity.Token), null, DataProtectionScope.CurrentUser);
            var b = new Binding(uri.AbsoluteUri, identity.VaultId, vault, Convert.ToBase64String(encrypted));
            var key = AccountKey(b);
            if (File.Exists(marker) && File.ReadAllText(marker) != key)
                throw new InvalidOperationException("此目录已有其他云端词库的绑定记录，请为当前词库选择独立目录。");
            if (!File.Exists(marker))
            {
                using var markerStream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                markerStream.Write(Encoding.UTF8.GetBytes(key));
                markerStream.Flush(true);
            }
            Directory.CreateDirectory(_dir);
            var temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(b), _stop.Token); File.Move(temp, ConfigPath, true); }
            finally { File.Delete(temp); }
            _binding = b;
            InitializeState(b);
            BeginWatching();
            BindingChanged?.Invoke(this, EventArgs.Empty);
            SetStatus("设备已绑定，正在准备同步。");
        }
        finally { _gate.Release(); }
        Request();
    }

    public async Task<string> Pair()
    {
        await _gate.WaitAsync(_stop.Token);
        try { return await Client(_binding ?? throw new InvalidOperationException("请先绑定本机。")).PairAsync(_stop.Token); }
        finally { _gate.Release(); }
    }

    private SyncClient Client(Binding b)
    {
        var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b.EncryptedToken), null, DataProtectionScope.CurrentUser));
        return new(_http, SyncClient.Server(b.Server), token, b.VaultId);
    }

    public async Task Unbind()
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            Disconnect();
            SetStatus("本机已断开，笔记保留在原目录。");
        }
        finally { _gate.Release(); }
    }
    private void Disconnect()
    {
        var oldSession = _sessionOperations;
        _sessionOperations = new CancellationTokenSource();
        oldSession.Cancel();
        oldSession.Dispose();
        // A separate local tombstone prevents a locked connection.json from
        // reopening a deliberately signed-out session during an offline restart.
        try { Directory.CreateDirectory(_dir); File.WriteAllText(LocalLockPath, "signed-out"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        _binding = null; _stateSync = null; _notes = []; StateEntries = [];
        LastBindingWasRegistration = false;
        _debounce.Stop(); _watcher?.Dispose(); _watcher = null;
        SetAccess(AccountAccess.LoginRequired);
        BindingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginWatching()
    {
        _watcher?.Dispose(); _watcher = null;
        if (!Directory.Exists(Folder)) return;
        var w = new FileSystemWatcher(Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        w.Changed += Changed; w.Created += Changed; w.Deleted += Changed;
        w.Renamed += (_, e) => { if (IsVisiblePath(w.Path, e.FullPath) || IsVisiblePath(w.Path, e.OldFullPath)) DispatchRequest(); };
        w.Error += (_, _) => DispatchRequest();
        _watcher = w;
        w.EnableRaisingEvents = true;
    }

    private static bool IsVisiblePath(string root, string path) => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'));
    private void Changed(object sender, FileSystemEventArgs e) { if (sender is FileSystemWatcher watcher && IsVisiblePath(watcher.Path, e.FullPath)) DispatchRequest(); }
    private void DispatchRequest()
    {
        if (!_dispatcher.HasShutdownStarted && !_stop.IsCancellationRequested)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Request));
    }
    public void Request()
    {
        if (_binding == null || _stop.IsCancellationRequested) return;
        _debounce.Stop(); _debounce.Start();
    }

    public async Task Tick()
    {
        if (!CanUseAccount || _stop.IsCancellationRequested || !_gate.Wait(0)) return;
        using var linked = LinkSession();
        var ct = linked.Token;
        try
        {
            if (!await VerifySessionCore(ct)) return;
            ct.ThrowIfCancellationRequested();
            var b = _binding!;
            SetStatus("正在同步…");
            var identities = GetNoteIdentitiesAsync is { } getIds ? await getIds(ct) : null;
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(async () =>
            {
                void Verify(string root)
                {
                    var marker = MarkerPath(root);
                    if (!File.Exists(marker) || File.ReadAllText(marker) != AccountKey(b))
                        throw new InvalidOperationException("词库绑定记录缺失或与当前设备不符，同步已暂停。");
                }
                return await new VaultSync(b.Vault, StateFile(b), Client(b), Verify, identities).RunAsync(ct);
            }, ct);
            ct.ThrowIfCancellationRequested();
            _notes = result.Notes ?? [];
            await SaveJson(StateFile(b) + ".notes.json", _notes, ct);
            ct.ThrowIfCancellationRequested();
            if (_stateSync is { } states)
            {
                if (ExportStateAsync is { } export) await states.EnqueueAsync(await export(_notes, ct), ct);
                var entries = await states.RunAsync(ct);
                ct.ThrowIfCancellationRequested();
                StateEntries = entries;
                StateSyncGeneration++;
                if (ImportStateAsync is { } import) await import(entries, ct);
            }
            if (ImportPracticeBanksAsync is { } importBanks)
                await ImportCloudPracticeBanksAsync(b, importBanks, _notes.ToArray(), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(b, _binding)) return;
            var message = result.Reconciled ? "已重新合并本地与云端词库，旧同步记录已备份" : "已完成";
            if (result.Conflicts > 0) message += " · 已保留冲突版本";
            SetStatus("上次同步 " + DateTime.Now.ToString("HH:mm") + " · " + message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { await RequireLogin("账号会话已失效，请重新登录。本机词库与未同步改动已保留。", ct); }
        catch (Exception e) { if (!ct.IsCancellationRequested) SetStatus(Friendly(e)); }
        finally { _gate.Release(); }
    }
    private async Task ImportCloudPracticeBanksAsync(Binding binding,
        Func<IReadOnlyList<CloudPracticeBank>, CancellationToken, Task> import, IReadOnlyList<SyncedNote> notes, CancellationToken ct)
    {
        bool CurrentScope() => !ct.IsCancellationRequested && ReferenceEquals(binding, _binding) &&
            ReferenceEquals(import, ImportPracticeBanksAsync);
        foreach (var ids in notes.Select(note => note.Id).Distinct(StringComparer.Ordinal).Chunk(32))
        {
            var pending = ids;
            while (pending.Length > 0)
            {
                if (!CurrentScope()) return;
                try
                {
                    var page = await Client(binding).PracticeBanksAsync(pending, ct);
                    if (!CurrentScope()) return;
                    if (page.Banks.Count == 0) break;
                    await import(page.Banks, ct);
                    if (!CurrentScope()) return;
                    // The response cap can omit whole banks. Remove those already delivered so
                    // a large earlier bank cannot permanently crowd out the rest of this batch.
                    var delivered = page.Banks.Select(bank => bank.NoteId).ToHashSet(StringComparer.Ordinal);
                    var remaining = pending.Where(id => !delivered.Contains(id)).ToArray();
                    if (remaining.Length == pending.Length) return;
                    pending = remaining;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                { if (CurrentScope()) throw; return; }
                catch (Exception)
                {
                    // Banks are optional. Unsupported servers, network errors and local import failures
                    // never roll back or relabel the note/state exchange that has already completed.
                    return;
                }
            }
        }
    }
    internal static string Friendly(Exception error) => error switch
    {
        // The service explains account refusals itself (wrong password, spent invite code),
        // so prefer its wording over a generic message.
        SyncRequestException explained => explained.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "账号、密码或设备凭证无效，请核对后重新登录或配对。",
        HttpRequestException { StatusCode: HttpStatusCode.Conflict } => "用户名已使用，或此词库已属于其他账号；请核对账号与词库。",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "尝试过于频繁，请稍后重试。",
        HttpRequestException { StatusCode: not null } h => $"服务器拒绝了请求（{(int)h.StatusCode!}），本机笔记已保留。",
        HttpRequestException or TaskCanceledException => "同步服务暂不可达，联网后会自动重试。",
        InvalidOperationException or ArgumentException or InvalidDataException => error.Message,
        _ => "同步暂未完成，请检查服务地址、词库路径及文件权限。"
    };
    private void SetStatus(string text) { Status = text; Updated?.Invoke(this, EventArgs.Empty); }
    public void Dispose()
    {
        _stop.Cancel();
        _sessionOperations.Dispose();
        _poll.Stop(); _debounce.Stop();
        _watcher?.Dispose(); _http.Dispose();
    }
}
