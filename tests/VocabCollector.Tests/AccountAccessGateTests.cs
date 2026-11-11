using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class AccountAccessGateTests
{
    [Fact]
    public void LogoutWithALockedCredentialFileStillLocksImmediatelyAndCannotReopenOffline() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        var config = Path.Combine(fixture.Root, "sync", "connection.json");
        using (File.Open(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Await(sync.Logout()); Assert.False(sync.CanUseAccount);
            using var restarted = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, new Remote { Offline = true });
            Assert.False(restarted.SignedIn); Assert.False(restarted.CanUseAccount);
            var access = restarted.VerifySessionAsync(); Await(access); Assert.False(access.Result);
        }
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        Assert.True(sync.CanUseAccount);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "sync", "signed-out")));
    });
    [Fact]
    public void LegacyPairCannotUnlockOrSyncAndPrivateRegistrationNeverClaimsItsVaultOrImportsGuestFiles() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var guest = new VaultOptions(Directory.CreateDirectory(Path.Combine(fixture.Root, "old-guest")).FullName, "words", false);
        var guestWords = Directory.CreateDirectory(VaultPaths.WordRoot(guest)).FullName;
        File.WriteAllText(Path.Combine(guestWords, "private.md"), "An arbitrary existing guest note.");
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        Await(sync.Bind("https://sync.example.test", "legacy-pair", guest));
        Assert.True(sync.Connected); Assert.False(sync.CanUseAccount);
        var count = fixture.Remote.Requests.Count;
        Await(sync.Tick()); var access = sync.VerifySessionAsync(); Await(access);
        Assert.False(access.Result); Assert.Equal(count, fixture.Remote.Requests.Count);
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password", true));
        Assert.True(sync.CanUseAccount);
        var request = fixture.Remote.Requests.Single(request => request.Path.EndsWith("/account/register"));
        Assert.Null(request.Token);
        Assert.NotEqual(guest.RootPath, sync.ActiveVault!.RootPath);
        Assert.StartsWith(sync.AccountStateDirectory!, sync.ActiveVault.RootPath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(VaultPaths.WordRoot(sync.ActiveVault), "*.md", SearchOption.AllDirectories));
        Assert.Equal("An arbitrary existing guest note.", File.ReadAllText(Path.Combine(guestWords, "private.md")));
        var aRoot = sync.ActiveVault.RootPath;
        Await(sync.Logout());
        Await(sync.SignInPrivate("https://sync.example.test", "bob", "test-password"));
        Assert.NotEqual(aRoot, sync.ActiveVault!.RootPath);
    });

    [Fact]
    public void PersistedSessionMustBeCheckedAndOfflineCacheStopsUnlockingAfterOnlineRevocation() => OnUi(() =>
    {
        using var fixture = new Fixture();
        string root;
        using (var initial = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote))
        {
            Await(initial.SignInPrivate("https://sync.example.test", "alice", "test-password"));
            root = VaultPaths.WordRoot(initial.ActiveVault!);
            File.WriteAllText(Path.Combine(root, "apple.md"), "# apple\n\nCached by this account.");
        }
        var remote = new Remote { Offline = true };
        using var restored = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, remote);
        Assert.Equal(AccountAccess.Checking, restored.Access); Assert.False(restored.CanUseAccount);
        var offline = restored.VerifySessionAsync(); Await(offline);
        Assert.True(offline.Result); Assert.True(restored.CanUseAccount); Assert.Equal(AccountAccess.Offline, restored.Access);
        remote.Offline = false; remote.Unauthorized = true;
        var revoked = restored.VerifySessionAsync(); Await(revoked);
        Assert.False(revoked.Result); Assert.False(restored.CanUseAccount); Assert.False(restored.SignedIn);
        Assert.True(File.Exists(Path.Combine(root, "apple.md")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "sync", "connection.json")));
        remote.Offline = true;
        var again = restored.VerifySessionAsync(); Await(again); Assert.False(again.Result);
    });

    [Fact]
    public void ExpiredLocalSessionNeverUsesOfflineCacheAndMismatchedOnlineIdentityCannotUnlock() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using (var initial = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote))
            Await(initial.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        var config = Path.Combine(fixture.Root, "sync", "connection.json");
        var value = JsonNode.Parse(File.ReadAllText(config))!;
        value["ExpiresAt"] = DateTimeOffset.UtcNow.AddDays(-1); File.WriteAllText(config, value.ToJsonString());
        var offline = new Remote { Offline = true };
        using (var expired = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, offline))
        {
            var access = expired.VerifySessionAsync(); Await(access);
            Assert.False(access.Result); Assert.False(expired.CanUseAccount); Assert.Empty(offline.Requests);
        }
        using var mismatch = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, new Remote { Mismatched = true });
        Await(mismatch.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        var different = mismatch.VerifySessionAsync(); Await(different);
        Assert.False(different.Result); Assert.False(mismatch.CanUseAccount);
    });

    [Fact]
    public void PreviousAccountBindingDirectoryIsRetainedOnReauthentication() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var old = new VaultOptions(Directory.CreateDirectory(Path.Combine(fixture.Root, "existing-1.2-vault")).FullName, "words", false);
        using (var existing = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote))
        {
            Await(existing.SignIn("https://sync.example.test", "alice", "test-password", old));
            File.WriteAllText(Path.Combine(VaultPaths.WordRoot(old), "apple.md"), "A pre-existing account note.");
        }
        var profilePath = Directory.GetFiles(Path.Combine(fixture.Root, "sync", "accounts"), "library-binding.json", SearchOption.AllDirectories).Single();
        File.Delete(profilePath); // Version 1.2 had only connection.json, with no per-account directory record.
        var remote = new Remote { Unauthorized = true };
        using var restored = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, remote);
        var invalid = restored.VerifySessionAsync(); Await(invalid);
        Assert.False(invalid.Result);
        remote.Unauthorized = false;
        Await(restored.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        Assert.Equal(old, restored.ActiveVault);
        Assert.Equal("A pre-existing account note.", File.ReadAllText(Path.Combine(VaultPaths.WordRoot(old), "apple.md")));
    });

    [Fact]
    public void HostCreatesNoMainBeforeLoginAndLogoutClosesItBeforeReturningToLogin() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        var login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLogin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginCount = 0; var mainCount = 0; Window? main = null;
        var host = new AccountWindowHost(sync, _ => ++loginCount == 1 ? login.Task : secondLogin.Task,
            _ => { mainCount++; return Task.FromResult<Window>(main = new Window { Width = 400, Height = 300, ShowInTaskbar = false }); });
        var run = host.RunAsync();
        Assert.Equal(1, loginCount); Assert.Equal(0, mainCount); Assert.Null(host.CurrentMain);
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        login.SetResult(true); Until(() => host.CurrentMain is not null);
        Assert.Equal(1, mainCount); Assert.True(main!.IsVisible);
        Await(sync.Logout()); Until(() => loginCount == 2);
        Assert.False(main.IsVisible); Assert.False(main.IsEnabled); Assert.Null(host.CurrentMain); Assert.Equal(1, mainCount);
        secondLogin.SetResult(false); Await(run);
    });

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public void LogoutClosesMainBeforeDelayedSyncProfileReplyAndDoesNotResumeOldSync(HttpStatusCode reply) => OnUi(() =>
    {
        using var fixture = new Fixture();
        var remote = fixture.Remote;
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, remote);
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        var login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginCount = 0;
        var main = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
        var host = new AccountWindowHost(sync, _ => { loginCount++; return login.Task; }, _ => Task.FromResult(main));
        var run = host.RunAsync();
        var profileReply = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.AliceProfileReply = profileReply;
        remote.CanInvite = true; // A stale successful reply would also try to rewrite connection.json.
        var tick = sync.Tick();
        Task? logout = null;
        try
        {
            Until(() => remote.AliceProfileHeld);
            Assert.True(main.IsVisible);
            logout = sync.Logout();
            Assert.False(sync.CanUseAccount);
            Assert.False(sync.SignedIn);
            Assert.False(main.IsVisible);
            Assert.False(main.IsEnabled);
            Until(() => loginCount == 1);
            Assert.Null(host.CurrentMain);
            Assert.False(profileReply.Task.IsCompleted);
            Assert.True(File.Exists(Path.Combine(fixture.Root, "sync", "signed-out")));

            profileReply.SetResult(reply);
            Await(tick);
            Await(logout);
            Assert.Equal(AccountAccess.LoginRequired, sync.Access);
            Assert.False(sync.SignedIn);
            Assert.Empty(sync.Notes);
            Assert.DoesNotContain(remote.Requests, request => request.Token == "token-alice" && request.Path.EndsWith("/changes"));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "sync", "connection.json")));
        }
        finally
        {
            profileReply.TrySetResult(reply);
            try { Await(tick); if (logout is not null) Await(logout); }
            finally { login.TrySetResult(false); if (main.IsVisible) main.Close(); Await(run); }
        }
    });

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void NewLoginCompletesBeforeOldRevocationAndKeepsItsOwnAccessAndStatus(HttpStatusCode reply) => OnUi(() =>
    {
        using var fixture = new Fixture();
        var remote = fixture.Remote;
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, remote);
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        var revokeReply = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.LogoutReply = revokeReply;
        var logout = sync.Logout();
        Task? newLogin = null;
        try
        {
            Until(() => remote.Requests.Any(request => request.Path.EndsWith("/account/logout")));
            Assert.False(sync.CanUseAccount);
            newLogin = sync.SignInPrivate("https://sync.example.test", "bob", "test-password");
            Await(newLogin.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.False(revokeReply.Task.IsCompleted);
            Assert.True(sync.CanUseAccount);
            Assert.Equal("bob", sync.Username);
            var status = sync.Status;

            revokeReply.SetResult(reply);
            Await(logout);
            Assert.Equal("token-alice", Assert.Single(remote.Requests, request => request.Path.EndsWith("/account/logout")).Token);
            Assert.Equal(AccountAccess.Online, sync.Access);
            Assert.Equal("bob", sync.Username);
            Assert.Equal("device-bob", sync.DeviceId);
            Assert.Equal(status, sync.Status);
        }
        finally
        {
            revokeReply.TrySetResult(reply);
            Await(logout);
            if (newLogin is not null) Await(newLogin);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InlineAccountSettingsSupportSyncAndLockMainOnLogoutOrInvalidation(bool invalidated) => OnUi(() =>
    {
        using var fixture = new Fixture();
        fixture.Remote.CanInvite = !invalidated;
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        var settings = new AppSettings(new("", "", ""), new(Directory.CreateDirectory(Path.Combine(fixture.Root, "not-an-account")).FullName, "words", false));
        var store = new WindowsSettingsStore(fixture.Root);
        var vm = Model(settings);
        Assert.Throws<InvalidOperationException>(() => new MainWindow(vm, store, new WindowsNavigator(), sync: sync, requireAccount: true));
        Await(sync.SignInPrivate("https://sync.example.test", "alice", "test-password"));
        var login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow? main = null;
        var host = new AccountWindowHost(sync, _ => login.Task, _ =>
        {
            var profile = sync.AccountStateDirectory!;
            main = new MainWindow(Model(new(new("", "", ""), sync.ActiveVault!)), new WindowsSettingsStore(profile), new WindowsNavigator(),
                sync: sync, requireAccount: true, settingsDirectory: fixture.Root);
            return Task.FromResult<Window>(main);
        });
        var run = host.RunAsync(); Until(() => main is { IsVisible: true });
        Await(main!.PrepareForUseAsync());
        var learning = (LearningPage)typeof(MainWindow).GetField("_learningPage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        Assert.NotNull(learning.LocalStateDirectory);
        Assert.False(learning.LocalStateDirectory!.StartsWith(sync.ActiveVault!.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (invalidated)
        {
            File.WriteAllText(Path.Combine(VaultPaths.WordRoot(sync.ActiveVault), "apple.md"), "# apple\n\nA private account word.");
            Await(learning.ReloadAsync()); Assert.Single(learning.AllNotes);
        }
        Await(EndSessionFromAccountSettingsAsync(main, sync, fixture.Remote, invalidated));
        Until(() => host.CurrentMain is null);
        Assert.False(main.IsVisible); Assert.False(main.IsEnabled); Assert.False(sync.CanUseAccount);
        login.SetResult(false); Await(run);
    });

    private static async Task EndSessionFromAccountSettingsAsync(MainWindow main, DesktopSync sync, Remote remote, bool invalidated)
    {
        var dialog = await OpenOwnedWindowAsync<SettingsWindow>(main, "SettingsButton");
        try
        {
            var panel = Assert.IsAssignableFrom<UserControl>(dialog.FindName("AccountSyncPanel"));
            Assert.True(panel.IsVisible);
            Assert.Contains("alice", Assert.IsType<TextBlock>(panel.FindName("AccountLabel")).Text);
            var syncNow = Assert.IsType<Button>(panel.FindName("SyncNowButton"));
            var logout = Assert.IsType<Button>(panel.FindName("LogoutButton"));
            Assert.True(syncNow.IsVisible);
            Assert.True(logout.IsVisible);
            Assert.Empty(dialog.OwnedWindows.Cast<Window>());
            var invites = Assert.IsType<Expander>(panel.FindName("InvitePanel"));
            Assert.Equal(!invalidated, invites.IsVisible);
            if (invalidated)
            {
                remote.Unauthorized = true;
                Assert.False(await sync.VerifySessionAsync());
            }
            else
            {
                UiRegressionSnapshot.Save(dialog, "settings-account-inline.png");
                Until(() => !sync.IsBusy && syncNow.IsEnabled);
                syncNow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(syncNow.IsEnabled);
                Until(() => sync.Status.Contains("已完成", StringComparison.Ordinal) && syncNow.IsEnabled);

                var password = Assert.IsType<Expander>(panel.FindName("ChangePasswordPanel"));
                var current = Assert.IsType<PasswordBox>(panel.FindName("CurrentPasswordBox"));
                var replacement = Assert.IsType<PasswordBox>(panel.FindName("NewPasswordBox"));
                password.IsExpanded = true;
                current.Password = "test-password";
                replacement.Password = "new-fixture-password";
                Assert.IsType<Button>(panel.FindName("ChangePasswordButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => !password.IsExpanded && syncNow.IsEnabled);
                Assert.Empty(current.Password);
                Assert.Empty(replacement.Password);

                remote.InviteListDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                invites.IsExpanded = true;
                Until(() => remote.InviteListStarted);
                Assert.True(syncNow.IsEnabled);
                Assert.True(logout.IsEnabled);
                Assert.True(Assert.IsType<Button>(dialog.FindName("SaveButton")).IsEnabled);
                invites.IsExpanded = false;
                Until(() => remote.InviteListCancelled && !sync.IsBusy);
                remote.InviteListDelay = null;
                invites.IsExpanded = true;
                var inviteList = Assert.IsType<TextBlock>(panel.FindName("InviteListText"));
                Until(() => inviteList.Text.Contains("还没有", StringComparison.Ordinal) && logout.IsEnabled);
                Assert.IsType<Button>(panel.FindName("IssueInviteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var inviteCode = Assert.IsType<TextBox>(panel.FindName("InviteCodeBox"));
                Until(() => inviteCode.IsVisible && syncNow.IsEnabled);
                Assert.Equal(Remote.InviteCode, inviteCode.Text);
                Assert.DoesNotContain(Remote.InviteCode, sync.Status);

                Until(() => !sync.IsBusy);
                var heldSync = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                remote.AliceProfileReply = heldSync;
                try
                {
                    syncNow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Until(() => remote.AliceProfileHeld);
                    Assert.False(syncNow.IsEnabled);
                    Assert.True(logout.IsEnabled);
                    logout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(dialog.IsVisible);
                    Assert.False(main.IsVisible);
                }
                finally { heldSync.TrySetResult(HttpStatusCode.OK); }
                Until(() => !dialog.IsVisible && !sync.IsBusy);
            }
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
        }
    }

    private static async Task<TWindow> OpenOwnedWindowAsync<TWindow>(Window owner, string buttonName) where TWindow : Window
    {
        Invoke(owner, buttonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (owner.OwnedWindows.OfType<TWindow>().SingleOrDefault(window => window.IsVisible) is { } child) return child;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Window did not open from {buttonName}.");
    }

    private static void Invoke(Window window, string buttonName)
    {
        var button = Assert.IsType<Button>(window.FindName(buttonName));
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static MainViewModel Model(AppSettings settings) => new(new CaptureService(new FakeWordAnalyzer(), new VaultStore(), TimeProvider.System), settings);
    private static void OnUi(Action action) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(action);
    private static void Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < until) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(condition(), "Account access did not settle.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tuci-account-gate-" + Guid.NewGuid().ToString("N"));
        public Remote Remote { get; } = new();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed record Sent(string Path, string? Token);
    private sealed class Remote : HttpMessageHandler
    {
        public const string InviteCode = "WINDOWS-FIXTURE";
        public bool Offline, Unauthorized, Mismatched, CanInvite;
        public bool InviteListStarted, InviteListCancelled;
        public TaskCompletionSource? InviteListDelay;
        public bool AliceProfileHeld;
        public TaskCompletionSource<HttpStatusCode>? AliceProfileReply, LogoutReply;
        private bool _issuedInvite;
        public List<Sent> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(path, request.Headers.Authorization?.Parameter));
            if (Offline) throw new HttpRequestException("offline");
            if (path.EndsWith("/account/register") || path.EndsWith("/account/login"))
            {
                var value = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var name = value.GetProperty("username").GetString()!;
                Assert.False(value.GetProperty("browser").GetBoolean());
                return Reply(new AccountIdentity("token-" + name, "vault-" + name, "user-" + name, name, "device-" + name));
            }
            if (path.EndsWith("/account/me"))
            {
                if (Unauthorized) return new(HttpStatusCode.Unauthorized);
                var name = Mismatched ? "bob" : request.Headers.Authorization!.Parameter![6..];
                if (name == "alice" && AliceProfileReply is { } heldProfile)
                {
                    AliceProfileHeld = true;
                    // Model a reply already in flight when local cancellation occurs.
                    var status = await heldProfile.Task;
                    if (status != HttpStatusCode.OK) return new(status);
                }
                return Reply(new AccountProfile("user-" + name, name, "vault-" + name, "device-" + name, CanInvite));
            }
            if (path.EndsWith("/account/logout")) return new(LogoutReply is { } heldLogout ? await heldLogout.Task : HttpStatusCode.NoContent);
            if (path.EndsWith("/account/password")) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/invites"))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var expiry = now + 604800;
                if (request.Method == HttpMethod.Post)
                {
                    _issuedInvite = true;
                    return Reply(new InviteIssued(InviteCode, expiry));
                }
                if (InviteListDelay is { } delay)
                {
                    InviteListStarted = true;
                    try { await delay.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { InviteListCancelled = true; throw; }
                }
                return Reply(new { invites = _issuedInvite ? new[] { new InviteSummary(now, expiry, false) } : Array.Empty<InviteSummary>() });
            }
            if (path.EndsWith("/redeem")) return Reply(new SyncIdentity("paired-token", "unowned-vault"));
            if (path.EndsWith("/state/changes")) return Reply(new StatePage([], 0, false));
            if (path.EndsWith("/changes")) return Reply(new { changes = Array.Empty<object>(), cursor = 0, hasMore = false });
            throw new InvalidOperationException("Unexpected account fixture request: " + path);
        }
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
