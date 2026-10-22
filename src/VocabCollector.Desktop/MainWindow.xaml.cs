using System.ComponentModel;
using System.Windows;
using System.Diagnostics;
using VocabCollector.Core;
using VocabCollector.Desktop.Preview;
namespace VocabCollector.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _store;
    private readonly IAppNavigator _navigator;
    public MainWindow(MainViewModel viewModel, ISettingsStore settingsStore, IAppNavigator navigator)
    {
        InitializeComponent(); _viewModel = viewModel; _store = settingsStore; _navigator = navigator; DataContext = viewModel;
        Loaded += (_, _) => WordBox.Focus();
        Closing += OnClosing;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var dialog = new SettingsWindow(_viewModel.Settings, _store) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SavedSettings != null) _viewModel.ApplySettings(dialog.SavedSettings);
    }
    private void OpenNote_Click(object sender, RoutedEventArgs e) => Open(true);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(false);
    private void Preview_LinkRequested(object? sender, PreviewLinkEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        try
        {
            if (!e.IsWikiLink && Uri.TryCreate(e.Target, UriKind.Absolute, out var web) && web.Scheme is "https" or "http")
            {
                Process.Start(new ProcessStartInfo(web.AbsoluteUri) { UseShellExecute = true });
                return;
            }
            var note = PreviewLinkResolver.Resolve(e.Target, _viewModel.Settings.Vault, _viewModel.SelectedNote);
            if (note is null) { _viewModel.Message = "这张关联词卡暂时无法打开，可以在 Obsidian 中查看。"; return; }
            _navigator.OpenNote(note);
        }
        catch (Exception) { _viewModel.Message = "无法打开这个链接，请确认目标笔记仍在词库中。"; }
    }
    private void Open(bool note)
    {
        if (_viewModel.SelectedNote == null) return;
        try { if (note) _navigator.OpenNote(_viewModel.SelectedNote); else _navigator.OpenFolder(_viewModel.SelectedNote); }
        catch (Exception) { _viewModel.Message = note ? "无法唤起 Obsidian。请确认已安装，并在 Obsidian 中打开此仓库；笔记已经保存在显示的位置。" : "无法打开文件夹，请检查它是否仍然存在。"; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true; _viewModel.CancelCommand.Execute(null); _viewModel.Message = "正在取消本次操作，完成后可以关闭窗口。";
    }
}
