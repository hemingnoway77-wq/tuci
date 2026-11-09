using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class AccountLoginWindowTests
{
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
            Assert.Equal("创建账号", Find<Button>(window, "RegisterButton").Content);
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
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<TextBox>(window, "InviteBox").Text = "TEST-INVT-CODE";
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<TextBox>(window, "InviteBox").Text = "";
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Find<TextBox>(window, "UsernameBox").Text = "alice";
            Find<PasswordBox>(window, "PasswordBox").Password = "private-window-password";
            Find<TextBox>(window, "InviteBox").Text = "ABCD-EFGH-IJKL";
            Find<Button>(window, "RegisterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !window.IsVisible);
            Assert.Single(fixture.Remote.Requests, request => request.InviteCode == "ABCD-EFGH-IJKL");
        }
        finally { window.Close(); }
    });

    private static T Find<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
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
