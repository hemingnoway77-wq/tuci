using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class AccountLoginWindowTests
{
    [Fact]
    public void LoginUsesItsOwnMonoPaletteAndRegistrationCanScrollToServiceSettingsAtMinimumSize() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var application = Application.Current.Resources;
        var pink = ThemePalette.Create(AppTheme.Pink);
        var mono = ThemePalette.Create(AppTheme.Mono);
        var keys = pink.Keys.Cast<object>().ToArray();
        var previous = application.Keys.Cast<object>().Where(keys.Contains).ToDictionary(key => key, key => application[key]);
        AccountLoginWindow? window = null;
        try
        {
            foreach (var key in keys) application[key] = pink[key];
            window = new AccountLoginWindow(sync);
            window.Show();
            window.UpdateLayout();
            var submit = Find<Button>(window, "LoginButton");
            Assert.Equal(ColorOf(mono["Sheet"]), ColorOf(window.Background));
            Assert.Equal(ColorOf(mono["Accent"]), ColorOf(submit.Background));
            Assert.Equal(ColorOf(pink["Accent"]), ColorOf(Application.Current.FindResource("Accent")));
            Assert.NotEqual(ColorOf(application["Accent"]), ColorOf(submit.Background));
            UiRegressionSnapshot.Save(window, "account-login-mono.png");

            window.SizeToContent = SizeToContent.Manual;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            Assert.InRange(window.ActualWidth, window.MinWidth - 1, window.MinWidth + 1);
            Assert.InRange(window.ActualHeight, window.MinHeight - 1, window.MinHeight + 1);
            Assert.True(FitsInViewport(submit), "Login must be fully visible at the minimum window size.");

            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var advanced = Find<Expander>(window, "AdvancedOptions");
            advanced.IsExpanded = true;
            window.UpdateLayout();
            var invite = Find<TextBox>(window, "InviteBox");
            var service = Find<TextBox>(window, "UrlBox");
            var scroll = Ancestors(service).OfType<ScrollViewer>().First();
            Assert.True(scroll.ScrollableHeight > 0);
            invite.BringIntoView();
            Until(() => FitsInViewport(invite));
            service.BringIntoView();
            Until(() => FitsInViewport(service));
            Assert.True(scroll.VerticalOffset > 0);
            UiRegressionSnapshot.Save(window, "account-registration-minimum.png");
            Assert.Empty(fixture.Remote.Requests);
        }
        finally
        {
            window?.Close();
            foreach (var key in keys)
            {
                if (previous.TryGetValue(key, out var value)) application[key] = value;
                else application.Remove(key);
            }
        }
    });

    [Fact]
    public void InitialGateHasOnlyAccountActionsAndNeverContainsAPassword() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            Assert.False(window.Authenticated);
            Assert.Empty(Find<PasswordBox>(window, "PasswordBox").Password);
            Assert.Equal("https://101.133.134.128/tuci-sync/", Find<TextBox>(window, "UrlBox").Text);
            Assert.False(Find<Expander>(window, "AdvancedOptions").IsExpanded);
            Assert.Equal("登录", Find<Button>(window, "LoginButton").Content);
            Assert.Equal("用邀请码注册", Find<Button>(window, "RegisterButton").Content);
            foreach (var name in new[] { "FolderBox", "ChooseButton", "PairButton", "CodeBox", "SkipButton", "GuestButton" })
                Assert.Null(window.FindName(name));
            Assert.Empty(fixture.Remote.Requests);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SuccessfulModelessLoginClosesAndClearsThePasswordWithoutPersistingPlainSecrets() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            window.Show();
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<Button>(window, "LoginButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !window.IsVisible);
            Assert.True(window.Authenticated);
            Assert.Empty(Find<PasswordBox>(window, "PasswordBox").Password);
            var request = Assert.Single(fixture.Remote.Requests, value => value.Path.EndsWith("/account/login"));
            Assert.Null(request.Token);
            Assert.False(request.Browser);
            Assert.Equal("alice", request.Username);
            var config = File.ReadAllText(Path.Combine(fixture.Directory, "sync", "connection.json"));
            Assert.DoesNotContain("private-window-password", config);
            Assert.DoesNotContain("window-session-token", config);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SuccessfulModalLoginReturnsTrueToTheApplicationGate() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                Find<Button>(window, "LoginButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.True(window.ShowDialog());
            Assert.True(window.Authenticated);
            Assert.Empty(Find<PasswordBox>(window, "PasswordBox").Password);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BusyGateCannotCloseAndFailedRegistrationCanBeCorrectedThenClosedWithoutKeepingThePassword() => OnUi(() =>
    {
        using var fixture = new Fixture();
        fixture.Remote.Hold = true; fixture.Remote.Failure = true;
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            window.Show();
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<PasswordBox>(window, "ConfirmPasswordBox").Password = "private-window-password";
            Find<TextBox>(window, "InviteBox").Text = "TEST-INVT-CODE";
            Find<Button>(window, "LoginButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => fixture.Remote.Started.Task.IsCompleted);
            Assert.False(Find<Button>(window, "LoginButton").IsEnabled);
            Assert.False(Find<Button>(window, "RegisterButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<ProgressBar>(window, "BusyIndicator").Visibility);
            window.Close(); Assert.True(window.IsVisible);
            Assert.False(window.Authenticated);
            fixture.Remote.Release.TrySetResult();
            Until(() => Find<Button>(window, "LoginButton").IsEnabled);
            Assert.False(window.Authenticated);
            Assert.NotEmpty(Find<TextBlock>(window, "StatusText").Text);
            Assert.Equal(Visibility.Collapsed, Find<ProgressBar>(window, "BusyIndicator").Visibility);
            window.Close();
            Assert.Empty(Find<PasswordBox>(window, "PasswordBox").Password);
            Assert.Single(fixture.Remote.Requests, request => request.Path.EndsWith("/account/register"));
            Assert.DoesNotContain(fixture.Remote.Requests, request => request.Path.EndsWith("/redeem") || request.Path.EndsWith("/pair"));
        }
        finally { fixture.Remote.Release.TrySetResult(); window.Close(); }
    });

    [Fact]
    public void RegistrationWithoutAnInviteIsRefusedBeforeAnyRequest() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            window.Show();
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<PasswordBox>(window, "ConfirmPasswordBox").Password = "private-window-password";
            Find<TextBox>(window, "InviteBox").Text = "";
            Find<Button>(window, "LoginButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => Find<TextBlock>(window, "StatusText").Text.Length > 0);
            Assert.Contains("邀请码", Find<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            Assert.Empty(fixture.Remote.Requests);
            Assert.False(window.Authenticated);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RegistrationSendsTheInviteCodeTypedIntoTheGate() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            window.Show();
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<PasswordBox>(window, "ConfirmPasswordBox").Password = "private-window-password";
            Find<TextBox>(window, "InviteBox").Text = "ABCD-EFGH-IJKL";
            Find<Button>(window, "LoginButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !window.IsVisible);
            Assert.Single(fixture.Remote.Requests, request => request.InviteCode == "ABCD-EFGH-IJKL");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RegisterModeRevealsInviteAndConfirmationFields() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(window, "InvitePanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(window, "ConfirmPanel").Visibility);
            Assert.Equal("登录", Find<Button>(window, "LoginButton").Content);
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "InvitePanel").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "ConfirmPanel").Visibility);
            Assert.Equal("创建账号", Find<Button>(window, "LoginButton").Content);
            Assert.Equal("返回登录", Find<Button>(window, "RegisterButton").Content);
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(window, "InvitePanel").Visibility);
            Assert.Equal("登录", Find<Button>(window, "LoginButton").Content);
            Assert.Empty(fixture.Remote.Requests);
            Assert.False(window.Authenticated);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RegistrationWithMismatchedConfirmationNeverReachesTheServer() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Directory, Dispatcher.CurrentDispatcher, fixture.Remote);
        var window = new AccountLoginWindow(sync);
        try
        {
            window.Show();
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<PasswordBox>(window, "ConfirmPasswordBox").Password = "private-window-passwerd";
            Find<TextBox>(window, "InviteBox").Text = "ABCD-EFGH-IJKL";
            Find<Button>(window, "LoginButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("不一致", Find<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            Assert.Empty(fixture.Remote.Requests);
            Assert.False(window.Authenticated);
        }
        finally { window.Close(); }
    });

    private static T Find<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }
    private static bool FitsInViewport(FrameworkElement element)
    {
        if (!element.IsVisible || Ancestors(element).OfType<ScrollContentPresenter>().FirstOrDefault() is not { } viewport) return false;
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        return bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
            bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }
    private static void OnUi(Action action) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(action);
    private static void Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(condition(), "The login operation did not complete.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tuci-login-window-" + Guid.NewGuid().ToString("N"));
        public LoginRemote Remote { get; } = new();
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    private sealed record Request(string Path, string? Token, string? Username, bool Browser, string? InviteCode = null);
    private sealed class LoginRemote : HttpMessageHandler
    {
        public bool Hold, Failure;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/account/login") || path.EndsWith("/account/register"))
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var invite = body.TryGetProperty("inviteCode", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
                Requests.Add(new(path, request.Headers.Authorization?.Parameter, body.GetProperty("username").GetString(), body.GetProperty("browser").GetBoolean(), invite));
                Started.TrySetResult();
                if (Hold) await Release.Task.WaitAsync(ct);
                if (Failure) return new(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = "invalid credentials" }) };
                return Reply(new { token = "window-session-token", vaultId = "window-vault", userId = "window-user", username = "alice", deviceId = "window-device" });
            }
            Requests.Add(new(path, request.Headers.Authorization?.Parameter, null, false));
            if (path.EndsWith("/account/me")) return Reply(new { vaultId = "window-vault", userId = "window-user", username = "alice", deviceId = "window-device" });
            if (path.EndsWith("/account/logout")) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/changes")) return Reply(new { changes = Array.Empty<object>(), cursor = 0, hasMore = false });
            throw new InvalidOperationException("Unexpected request: " + path);
        }
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
