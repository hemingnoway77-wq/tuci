using System.ComponentModel;
using System.Windows;
using VocabCollector.Core;
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
    private void Open(bool note)
    {
        if (_viewModel.SelectedNote == null) return;
        try { if (note) _navigator.OpenNote(_viewModel.SelectedNote); else _navigator.OpenFolder(_viewModel.SelectedNote); }
        catch (Exception) { _viewModel.Message = note ? "无法唤起 Obsidian。请确认已安装，并在 Obsidian 中打开此仓库；笔记已经保存在显示的位置。" : "无法打开文件夹，请检查它是否仍然存在。"; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true; _viewModel.CancelCommand.Execute(null); _viewModel.Message = "正在取消本次解析，完成后可以关闭窗口。";
    }
}
