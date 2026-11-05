using System.Windows;
using System.Windows.Controls;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Tests;

public sealed class ArticleWindowTests
{
    [Theory]
    [InlineData("https://example.com/article?lang=en#section")]
    [InlineData("http://example.com:8080/read")]
    [InlineData("HTTPS://EXAMPLE.COM/read")]
    [InlineData("https://例子.测试/文章")]
    [InlineData("http://[::1]/article")]
    public void AllowsAbsoluteHttpAddressesWithAHost(string url) => Assert.True(ArticleWindow.IsAllowed(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("article.html")]
    [InlineData("//example.com/article")]
    [InlineData("https://")]
    [InlineData("http:///article")]
    [InlineData("https://user:password@example.com/article")]
    [InlineData("https://user@example.com/article")]
    [InlineData("https://@example.com/article")]
    [InlineData("https://example.com:wrong/article")]
    [InlineData("https://example.com\\@evil.invalid/article")]
    [InlineData(" https://example.com/article")]
    [InlineData("https://example.com/article\n")]
    [InlineData("file:///C:/Users/notes.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("about:blank")]
    [InlineData("mailto:hello@example.com")]
    [InlineData("ms-settings:privacy")]
    [InlineData("obsidian://open?vault=private")]
    public void RejectsMissingMalformedCredentialAndNonWebAddresses(string? url) => Assert.False(ArticleWindow.IsAllowed(url!));

    [Fact]
    public void UnloadedWindowShowsAddressAndDisabledNavigationWithoutCreatingBrowser() => MarkdownPreviewTests.OnSta(() =>
    {
        var window = new ArticleWindow("https://example.invalid/article");
        try
        {
            Assert.Contains("来源文章", window.Title);
            var address = Assert.IsType<TextBox>(window.FindName("AddressBox"));
            Assert.True(address.IsReadOnly);
            Assert.Equal("https://example.invalid/article", address.Text);
            Assert.False(Assert.IsType<Button>(window.FindName("BackButton")).IsEnabled);
            Assert.False(Assert.IsType<Button>(window.FindName("ForwardButton")).IsEnabled);
            Assert.False(Assert.IsType<Button>(window.FindName("ReloadButton")).IsEnabled);
            Assert.Empty(Assert.IsType<Grid>(window.FindName("BrowserHost")).Children);
            window.Measure(new Size(window.MinWidth, window.MinHeight));
            window.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
            Assert.Empty(Assert.IsType<Grid>(window.FindName("BrowserHost")).Children);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void InvalidAddressRemainsAnErrorAndCannotStartABrowserOnLoadOrRetry() => MarkdownPreviewTests.OnSta(() =>
    {
        var window = new ArticleWindow("file:///C:/private.html");
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Contains("HTTP", Assert.IsType<TextBlock>(window.FindName("MessageText")).Text);
            var retry = Assert.IsType<Button>(window.FindName("RetryButton"));
            Assert.False(retry.IsEnabled);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(Assert.IsType<Grid>(window.FindName("BrowserHost")).Children);
            Assert.False(Assert.IsType<Button>(window.FindName("ReloadButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ClosedWindowCannotInitializeEvenWhenALoadOrRetryWasQueued() => MarkdownPreviewTests.OnSta(() =>
    {
        var window = new ArticleWindow("https://example.invalid/article");
        window.Close();
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.IsType<Button>(window.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(Assert.IsType<Grid>(window.FindName("BrowserHost")).Children);
    });
}
