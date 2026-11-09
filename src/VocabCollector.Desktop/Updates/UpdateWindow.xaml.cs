using System.ComponentModel;
using System.Windows;

namespace VocabCollector.Desktop.Updates;

public partial class UpdateWindow : Window
{
    private readonly UpdateController _controller;
    private readonly Func<string?> _installationBlockReason;
    private readonly Action _closeForUpdate;
    private bool _closed, _allowClose;
    public UpdateWindow(UpdateController controller, Func<string?> installationBlockReason, Action closeForUpdate)
    {
        _controller = controller; _installationBlockReason = installationBlockReason; _closeForUpdate = closeForUpdate;
        InitializeComponent();
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        _controller.Changed += ControllerChanged;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _controller.Changed -= ControllerChanged; _controller.Cancel(); };
        Refresh();
    }
    private void ControllerChanged(object? sender, EventArgs e) { if (!_closed) Refresh(); }
    private void Refresh()
    {
        var state = _controller.State;
        VersionText.Text = $"当前版本 {_controller.CurrentVersion.ToString(3)}";
        UpdateMessageText.Text = _controller.Message;
        var release = _controller.Update?.Release;
        PackageSizeText.Text = release is null ? "" : $"新版本 {release.Version} · {FormatBytes(release.SizeBytes)}";
        ReleaseNotesText.Text = release is null ? "更新会保留本地词库和设置。" : string.Join("\n\n", release.ReleaseNotes.Select(note => "· " + note));
        CheckUpdateButton.Content = state == UpdateState.Failed ? "重试检查" : "检查更新";
        CheckUpdateButton.IsEnabled = !_controller.IsWorking;
        InstallUpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.IsEnabled = !_controller.IsWorking;
        InstallUpdateButton.Content = state == UpdateState.Ready ? "重启并安装" : "下载并重启更新";
        CancelUpdateButton.Visibility = state is UpdateState.Downloading or UpdateState.Checking ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdateButton.Content = state == UpdateState.Checking ? "取消检查" : "取消下载";
        LaterButton.IsEnabled = state != UpdateState.Installing;
        DownloadProgress.Visibility = state is UpdateState.Downloading or UpdateState.Checking ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = state == UpdateState.Checking || state == UpdateState.Downloading && _controller.Progress is null;
        if (_controller.Progress is { } progress)
        {
            DownloadProgress.Value = progress.TotalBytes > 0 ? 100d * progress.ReceivedBytes / progress.TotalBytes : 0;
            ProgressText.Text = $"{FormatBytes(progress.ReceivedBytes)} / {FormatBytes(progress.TotalBytes)}";
        }
        else ProgressText.Text = "";
    }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0.0} MB";
    private async void Check_Click(object sender, RoutedEventArgs e) => await _controller.CheckAsync();
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (await _controller.DownloadAndInstallAsync(_installationBlockReason)) { _allowClose = true; _closeForUpdate(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _controller.Cancel();
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_allowClose && _controller.State == UpdateState.Installing) e.Cancel = true; }
}
