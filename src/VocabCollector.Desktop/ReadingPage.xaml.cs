using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace VocabCollector.Desktop;

public partial class ReadingPage : UserControl
{
    private readonly DailyReadingViewModel _viewModel;
    private readonly Action<string> _openArticle;

    public ReadingPage(DailyReadingViewModel viewModel, Action<string>? openArticle = null)
    {
        _viewModel = viewModel;
        _openArticle = openArticle ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        InitializeComponent();
        DataContext = viewModel;
    }

    public DailyReadingViewModel ViewModel => _viewModel;

    public async Task EnsureLoadedAsync()
    {
        if (_viewModel.Articles.Count == 0) await _viewModel.LoadAsync();
        else await _viewModel.RefreshIfNewDayAsync();
    }

    public void Cancel() => _viewModel.Cancel();

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
