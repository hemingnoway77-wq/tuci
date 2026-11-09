using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
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

    [Fact]
    public void OnlineInvalidationLocksMainAndItsPrivateLibraryMetadataLoadsOutsideTheVault() => OnUi(() =>
    {
        using var fixture = new Fixture();
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
        File.WriteAllText(Path.Combine(VaultPaths.WordRoot(sync.ActiveVault), "apple.md"), "# apple\n\nA private account word.");
        Await(learning.ReloadAsync()); Assert.Single(learning.AllNotes);
        fixture.Remote.Unauthorized = true;
        Await(sync.Tick()); Until(() => host.CurrentMain is null);
        Assert.False(main.IsVisible); Assert.False(main.IsEnabled); Assert.False(sync.CanUseAccount);
        login.SetResult(false); Await(run);
    });

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
        public bool Offline, Unauthorized, Mismatched;
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
                return Reply(new AccountProfile("user-" + name, name, "vault-" + name, "device-" + name));
            }
            if (path.EndsWith("/account/logout")) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/redeem")) return Reply(new SyncIdentity("paired-token", "unowned-vault"));
            if (path.EndsWith("/changes")) return Reply(new { changes = Array.Empty<object>(), cursor = 0, hasMore = false });
            throw new InvalidOperationException("Unexpected account fixture request: " + path);
        }
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
