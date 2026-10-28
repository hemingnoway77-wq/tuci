using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace VocabCollector.Desktop;

public partial class DailyReadingWindow : Window
{
    private readonly DailyReadingViewModel _viewModel;
    private readonly Action<string> _openArticle;
    private bool _loaded;

    public DailyReadingWindow(DailyReadingViewModel viewModel, Action<string>? openArticle = null)
    {
        _viewModel = viewModel;
        _openArticle = openArticle ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try { await _viewModel.LoadAsync(); }
        catch (Exception) { _viewModel.Message = "文章获取失败，请点击“刷新文章”重试。"; }
    }

    private void Window_Closed(object? sender, EventArgs e) => _viewModel.Cancel();

    private async void Window_Activated(object? sender, EventArgs e)
    {
        if (!_loaded) return;
        try { await _viewModel.RefreshIfNewDayAsync(); }
        catch (Exception) { _viewModel.Message = "文章获取失败，请点击“刷新文章”重试。"; }
    }

    private void OpenArticle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0)
        {
            _viewModel.Message = "文章链接不可用，请刷新后重试。";
            return;
        }

        try { _openArticle(uri.AbsoluteUri); }
        catch (Exception) { _viewModel.Message = "无法打开浏览器，请检查默认浏览器设置后重试。"; }
    }
}
