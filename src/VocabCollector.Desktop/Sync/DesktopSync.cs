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

namespace VocabCollector.Desktop;

public sealed class DesktopSync : IDisposable
{
    private sealed record Binding(string Server, string VaultId, VaultOptions Vault, string EncryptedToken);
    private readonly string _dir;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _debounce;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private Binding? _binding;
    private FileSystemWatcher? _watcher;
    private string ConfigPath => Path.Combine(_dir, "connection.json");
    public string Server => _binding?.Server ?? "";
    public string Folder => _binding == null ? "" : Path.Combine(_binding.Vault.RootPath, _binding.Vault.OutputSubfolder);
    public bool Connected => _binding != null;
    public string Status { get; private set; } = "尚未绑定词库。";
    public event EventHandler? Updated;

    public DesktopSync(string directory, Dispatcher dispatcher)
    {
        _dir = Path.Combine(directory, "sync");
        _dispatcher = dispatcher;
        _poll = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _debounce = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += async (_, _) => await Tick();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Tick(); };
        try
        {
            if (File.Exists(ConfigPath))
            {
                _binding = JsonSerializer.Deserialize<Binding>(File.ReadAllText(ConfigPath)) ?? throw new JsonException();
                SyncClient.Server(_binding.Server);
                SetStatus("等待同步。");
                BeginWatching();
            }
        }
        catch (Exception) { SetStatus("同步设置暂不可用，请检查目录或重新绑定。"); }
        _poll.Start();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string AccountKey(Binding b) => Hash(b.Server + "\n" + b.VaultId);
    private string StateFile(Binding b) => Path.Combine(_dir, Hash(AccountKey(b) + "\n" + VaultPaths.WordRoot(b.Vault).ToUpperInvariant()) + ".json");
    private static string MarkerPath(string root)
    {
        var marker = Path.Combine(root, ".vocabcollector", "sync-binding");
        VaultPaths.EnsureInside(root, marker);
        return marker;
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
            BeginWatching();
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
        return new(_http, SyncClient.Server(b.Server), token);
    }

    public async Task Unbind()
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            _binding = null;
            _watcher?.Dispose(); _watcher = null;
            SetStatus("本机已断开，笔记保留在原目录。");
        }
        finally { _gate.Release(); }
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
        if (_binding == null || !_gate.Wait(0)) return;
        try
        {
            var b = _binding;
            SetStatus("正在同步…");
            var result = await Task.Run(async () =>
            {
                void Verify(string root)
                {
                    var marker = MarkerPath(root);
                    if (!File.Exists(marker) || File.ReadAllText(marker) != AccountKey(b))
                        throw new InvalidOperationException("词库绑定记录缺失或与当前设备不符，同步已暂停。");
                }
                return await new VaultSync(b.Vault, StateFile(b), Client(b), Verify).RunAsync(_stop.Token);
            });
            var message = result.Reconciled ? "已重新合并本地与云端词库，旧同步记录已备份" : "已完成";
            if (result.Conflicts > 0) message += " · 已保留冲突版本";
            SetStatus("上次同步 " + DateTime.Now.ToString("HH:mm") + " · " + message);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { SetStatus(Friendly(e)); }
        finally { _gate.Release(); }
    }
    internal static string Friendly(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "设备凭证或配对码无效，请重新绑定。",
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
        _poll.Stop(); _debounce.Stop();
        _watcher?.Dispose(); _http.Dispose();
    }
}
