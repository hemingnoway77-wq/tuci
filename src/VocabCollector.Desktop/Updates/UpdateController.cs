using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VocabCollector.Core.Updates;

namespace VocabCollector.Desktop.Updates;

public interface IDesktopUpdateService
{
    Task<VerifiedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken);
    Task<string> DownloadAsync(VerifiedUpdate update, string directory, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken);
}

public sealed class DesktopUpdateService(UpdateClient client) : IDesktopUpdateService
{
    public Task<VerifiedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken) => client.CheckAsync(currentVersion, cancellationToken);
    public Task<string> DownloadAsync(VerifiedUpdate update, string directory, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
        => client.DownloadAsync(update, directory, progress, cancellationToken);
}

public enum UpdateState { Idle, Checking, Current, Available, Downloading, Ready, Failed, Installing }

/// <summary>Owns update work independently of a particular window. It never installs during a check.</summary>
public sealed class UpdateController : IDisposable
{
    private readonly IDesktopUpdateService _service;
    private readonly string _stageRoot, _installDirectory;
    private readonly Func<string, string, Task> _launch;
    private CancellationTokenSource? _operation;
    private int _generation;
    private bool _disposed, _launched;
    private string? _stageDirectory, _packagePath;
    public static Version ApplicationVersion
    {
        get
        {
            var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }
    public UpdateController(IDesktopUpdateService service, Version? currentVersion = null, string? stageRoot = null,
        string? installDirectory = null, Func<string, string, Task>? launcher = null)
    {
        _service = service;
        CurrentVersion = currentVersion ?? ApplicationVersion;
        _stageRoot = stageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "updates");
        _installDirectory = installDirectory ?? AppContext.BaseDirectory;
        _launch = launcher ?? LaunchUpdaterAsync;
    }
    public Version CurrentVersion { get; }
    public VerifiedUpdate? Update { get; private set; }
    public UpdateState State { get; private set; }
    public string Message { get; private set; } = "检查是否有可用的新版本。";
    public UpdateDownloadProgress? Progress { get; private set; }
    public bool IsWorking => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;
    public event EventHandler? Changed;

    public async Task CheckAsync()
    {
        if (_disposed || IsWorking) return;
        var (generation, token) = BeginOperation();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Progress = null;
        SetState(UpdateState.Checking, "正在检查更新…");
        try
        {
            var update = await _service.CheckAsync(CurrentVersion, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation, token)) return;
            if (update?.SignedManifest != Update?.SignedManifest) { CleanStage(); _packagePath = null; }
            Update = update;
            SetState(update is null ? UpdateState.Current : _packagePath is null ? UpdateState.Available : UpdateState.Ready,
                update is null ? "当前已是最新版本。" : $"发现新版本 {update.Release.Version}。下载后将重启兔词。词库与设置会保留。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { if (IsCurrent(generation, token)) SetState(UpdateState.Failed, "检查更新超时，请检查网络后重试。"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (IsCurrent(generation, token)) SetState(UpdateState.Failed, "检查更新失败，请检查网络后重试。"); }
        finally { EndOperation(generation); }
    }

    // This method is only called by an explicit click in the update window.
    public async Task<bool> DownloadAndInstallAsync(Func<string?> installationBlockReason)
    {
        if (_disposed || IsWorking || Update is not { } update) return false;
        if (installationBlockReason() is { } before) { SetState(_packagePath is null ? UpdateState.Available : UpdateState.Ready, before); return false; }
        var (generation, token) = BeginOperation();
        try
        {
            if (_packagePath is null || !File.Exists(_packagePath))
            {
                CleanStage();
                _stageDirectory = Path.Combine(_stageRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_stageDirectory);
                Progress = null;
                SetState(UpdateState.Downloading, "正在下载更新，可以取消并稍后重试。");
                var progress = new Progress<UpdateDownloadProgress>(value =>
                {
                    if (!IsCurrent(generation, token) || State != UpdateState.Downloading) return;
                    Progress = value; Changed?.Invoke(this, EventArgs.Empty);
                });
                var packagePath = await _service.DownloadAsync(update, _stageDirectory, progress, token);
                if (!IsCurrent(generation, token)) return false;
                _packagePath = packagePath;
            }
            if (!IsCurrent(generation, token)) return false;
            if (installationBlockReason() is { } after) { SetState(UpdateState.Ready, after + " 更新包已下载，可稍后继续。"); return false; }
            _stageDirectory ??= Path.GetDirectoryName(_packagePath!)!;
            var manifestPath = Path.Combine(_stageDirectory, "signed-manifest.json");
            await File.WriteAllTextAsync(manifestPath, update.SignedManifest, token);
            using var parent = Process.GetCurrentProcess();
            var request = new UpdateInstallRequest
            {
                InstallDirectory = Path.GetFullPath(_installDirectory), PackagePath = Path.GetFullPath(_packagePath!),
                ManifestPath = manifestPath, ExpectedVersion = update.Release.Version,
                ParentProcessId = parent.Id, ParentProcessStartTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks, Restart = true
            };
            var requestPath = Path.Combine(_stageDirectory, "install-request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), token);
            var stagedUpdater = Path.Combine(_stageDirectory, UpdateConfiguration.UpdaterExecutableName);
            File.Copy(Path.Combine(_installDirectory, UpdateConfiguration.UpdaterExecutableName), stagedUpdater, true);
            // File I/O yielded above: do not discard anything typed while downloading or staging.
            if (!IsCurrent(generation, token)) return false;
            if (installationBlockReason() is { } last) { SetState(UpdateState.Ready, last + " 更新包已下载，可稍后继续。"); return false; }
            SetState(UpdateState.Installing, "更新器已准备就绪，正在重启…");
            await _launch(stagedUpdater, requestPath);
            _launched = true;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            if (IsCurrent(generation, token)) SetState(UpdateState.Failed, "更新未能完成。请检查网络或安装目录权限后重试；词库与设置未被修改。");
            return false;
        }
        finally { EndOperation(generation); }
    }
    public void Cancel()
    {
        if (_disposed || _launched || State == UpdateState.Installing) return;
        ++_generation;
        _operation?.Cancel(); _operation?.Dispose(); _operation = null;
        SetState(Update is null ? UpdateState.Idle : _packagePath is null ? UpdateState.Available : UpdateState.Ready, "操作已取消，可稍后重试。");
    }
    private (int Generation, CancellationToken Token) BeginOperation()
    {
        _operation?.Dispose(); _operation = new(); return (++_generation, _operation.Token);
    }
    private bool IsCurrent(int generation, CancellationToken token) => !_disposed && generation == _generation && !token.IsCancellationRequested;
    private void EndOperation(int generation) { if (generation == _generation) { _operation?.Dispose(); _operation = null; } }
    private void SetState(UpdateState state, string message) { State = state; Message = message; Changed?.Invoke(this, EventArgs.Empty); }
    private static Task LaunchUpdaterAsync(string updater, string request)
    {
        var start = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(updater)! };
        start.ArgumentList.Add("--install"); start.ArgumentList.Add(request);
        using var process = Process.Start(start) ?? throw new IOException("更新器未能启动。");
        return Task.CompletedTask;
    }
    private void CleanStage()
    {
        if (_stageDirectory is null || _launched) return;
        // Only remove this controller's random child directory, never an arbitrary package path.
        var root = Path.GetFullPath(_stageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_stageDirectory);
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _stageDirectory = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_generation;
        _operation?.Cancel(); _operation?.Dispose(); _operation = null;
        Changed = null;
        CleanStage();
    }
}
