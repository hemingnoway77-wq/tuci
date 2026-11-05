using System.IO;
using System.Windows;
using Microsoft.Win32;
using VocabCollector.Core;
using VocabCollector.Core.Storage;

namespace VocabCollector.Desktop;

public partial class SyncWindow : Window
{
    private readonly DesktopSync _sync;
    private readonly VaultOptions _default;
    public SyncWindow(DesktopSync sync, VaultOptions configured)
    {
        InitializeComponent(); _sync = sync; _default = configured;
        try { FolderBox.Text = VaultPaths.WordRoot(configured); }
        catch (ArgumentException) { }
        _sync.Updated += Updated;
        Closed += (_, _) => _sync.Updated -= Updated;
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Refresh();
    }
    private void Updated(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        BoundPanel.Visibility = _sync.Connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = _sync.Connected ? Visibility.Collapsed : Visibility.Visible;
        Details.Text = _sync.Folder + Environment.NewLine + _sync.Server;
        StatusText.Text = _sync.Status;
    }
    private void Choose_Click(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFolderDialog { Title = "选择要同步的单词目录", Multiselect = false };
        if (picker.ShowDialog(this) == true) FolderBox.Text = picker.FolderName;
    }
    private VaultOptions Selected()
    {
        var selected = FolderBox.Text.Trim();
        if (!Path.IsPathFullyQualified(selected)) throw new ArgumentException("请选择单词目录。");
        selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        if (Directory.Exists(_default.RootPath) && string.Equals(selected, VaultPaths.WordRoot(_default), StringComparison.OrdinalIgnoreCase)) return _default;
        return new(Path.GetDirectoryName(selected) ?? throw new ArgumentException("请选择单词目录。"), Path.GetFileName(selected), true);
    }
    private async Task Perform(Func<Task> work)
    {
        IsEnabled = false;
        try { await work(); }
        catch (Exception e) { StatusText.Text = DesktopSync.Friendly(e); }
        finally { IsEnabled = true; }
    }
    private async void Bind_Click(object sender, RoutedEventArgs e) => await Perform(async () => { await _sync.Bind(UrlBox.Text, CodeBox.Password, Selected()); CodeBox.Clear(); });
    private async void Sync_Click(object sender, RoutedEventArgs e) => await Perform(_sync.Tick);
    private async void Pair_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        PairBox.Text = await _sync.Pair(); PairBox.Visibility = Visibility.Visible; PairBox.SelectAll(); PairBox.Focus();
    });
    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        await _sync.Unbind(); PairBox.Clear(); PairBox.Visibility = Visibility.Collapsed;
    });
}
