using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Threading;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;
using VocabCollector.Desktop;
using VocabCollector.Tests.Fakes;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class DesktopAccountSyncTests
{
    [Fact]
    public void ClaimedApiSettingsSurviveImmediateLogoutWhileTheNewGuestSettingsAreSaving() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        var settings = new AppSettings(new("https://api.example.test", "claimed-key", "private-model"), fixture.First);
        var store = new MemoryStore(settings) { SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new MainViewModel(new CaptureService(new FakeWordAnalyzer(), new VaultStore(), TimeProvider.System), settings);
        var window = new MainWindow(vm, store, new WindowsNavigator(), sync: sync, settingsDirectory: fixture.Root);
        try
        {
            Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First, true));
            var accountDirectory = sync.AccountStateDirectory!;
            Await(store.SaveStarted.Task);
            Await(sync.Logout());
            store.SaveGate.SetResult();
            Await(window.AccountTransition);
            var saved = new WindowsSettingsStore(accountDirectory).LoadAsync(default); Await(saved);
            Assert.Equal("claimed-key", saved.Result.Settings.Api.ApiKey);
            Assert.Empty(vm.Settings.Api.ApiKey);
            Assert.Empty(store.Settings.Api.ApiKey);
        }
        finally { store.SaveGate.TrySetResult(); window.Close(); }
    });
    [Fact]
    public void LocalThemeAndReadingChangesDuringImportRemainVisibleAndQueuedWithoutRemoteEcho() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var notesRoot = Directory.CreateDirectory(Path.Combine(fixture.First.RootPath, fixture.First.OutputSubfolder)).FullName;
        File.WriteAllText(Path.Combine(notesRoot, "apple.md"), "# apple\n\nA word to review.");
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        var settings = new AppSettings(new("", "", ""), fixture.First);
        var store = new MemoryStore(settings);
        var vm = new MainViewModel(new CaptureService(new FakeWordAnalyzer(), new VaultStore(), TimeProvider.System), settings);
        var themes = new ThemeManager(new ResourceDictionary(), new AppearanceSettingsStore(fixture.Root));
        var window = new MainWindow(vm, store, new WindowsNavigator(), sync: sync, themeManager: themes, settingsDirectory: fixture.Root);
        SemaphoreSlim? sessionGate = null;
        try
        {
            Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First));
            Await(window.AccountTransition); Await(sync.Tick());
            Assert.StartsWith("上次同步", sync.Status);
            var learning = Field<LearningPage>(window, "_learningPage");
            var note = Assert.Single(learning.AllNotes);
            var cloudId = learning.CloudNoteIds[note.Id];
            var session = Field<RoamingSession>(learning, "_session");
            sessionGate = Field<SemaphoreSlim>(session, "_gate");
            sessionGate.Wait();
            var url = "https://example.test/article";
            var articleKey = "reading/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            var incoming = new[]
            {
                Entry("preference/theme", new { name = "mono" }),
                Entry(articleKey, new { url, progress = .2, anchor = "p:1" }),
                Entry("note-reading/" + cloudId, new { noteId = cloudId, progress = .2, anchor = "p:1" })
            };
            var importing = sync.ImportStateAsync!(incoming, default);
            Assert.False(importing.IsCompleted);
            Await(themes.SetThemeAsync(AppTheme.Pink));
            Invoke(window, "ArticleReadingPositionChanged", null, new ArticleReadingPosition(url, .8, "p:8", "Article"));
            Invoke(window, "NoteReadingPositionChanged", null, new NoteReadingPosition(note.Id, .9, "p:9", "apple"));
            var engine = Field<AccountStateSync>(sync, "_stateSync");
            var localRead = engine.ReadAsync(); Await(localRead);
            Assert.Equal("pink", localRead.Result.Single(entry => entry.Key == "preference/theme").Value.GetProperty("name").GetString());
            Assert.Equal(.8, localRead.Result.Single(entry => entry.Key == articleKey).Value.GetProperty("progress").GetDouble());
            Assert.Equal(.9, localRead.Result.Single(entry => entry.Key == "note-reading/" + cloudId).Value.GetProperty("progress").GetDouble());
            sessionGate.Release(); sessionGate = null;
            Await(importing);
            Assert.Equal(AppTheme.Pink, themes.CurrentTheme);
            var visible = Field<IReadOnlyList<SyncStateEntry>>(window, "_accountEntries");
            Assert.Equal(.8, visible.Single(entry => entry.Key == articleKey).Value.GetProperty("progress").GetDouble());
            Assert.Equal(.9, visible.Single(entry => entry.Key == "note-reading/" + cloudId).Value.GetProperty("progress").GetDouble());
            Await(sync.Tick());
            Assert.StartsWith("上次同步", sync.Status);
            var before = fixture.Remote.Requests.Count(request => request.Path == "/v1/state/operations");
            Await(sync.Tick());
            Assert.StartsWith("上次同步", sync.Status);
            Assert.Equal(before, fixture.Remote.Requests.Count(request => request.Path == "/v1/state/operations"));
            Await(themes.SetThemeAsync(AppTheme.Pink));
            sessionGate = Field<SemaphoreSlim>(session, "_gate"); sessionGate.Wait();
            var exporting = sync.ExportStateAsync!(sync.Notes, default);
            Assert.False(exporting.IsCompleted);
            Await(themes.SetThemeAsync(AppTheme.Mono));
            Invoke(window, "ArticleReadingPositionChanged", null, new ArticleReadingPosition(url, .85, "p:85", "Article"));
            Invoke(window, "NoteReadingPositionChanged", null, new NoteReadingPosition(note.Id, .95, "p:95", "apple"));
            sessionGate.Release(); sessionGate = null;
            Await(exporting);
            var exported = exporting.Result.ToArray();
            Assert.Equal("mono", exported.Single(entry => entry.Key == "preference/theme").Value.GetProperty("name").GetString());
            Assert.Equal(.85, exported.Single(entry => entry.Key == articleKey).Value.GetProperty("progress").GetDouble());
            Assert.Equal(.95, exported.Single(entry => entry.Key == "note-reading/" + cloudId).Value.GetProperty("progress").GetDouble());
            Await(engine.EnqueueAsync(exported));
            var exchanged = engine.RunAsync(); Await(exchanged);
            Assert.Equal("mono", exchanged.Result.Single(entry => entry.Key == "preference/theme").Value.GetProperty("name").GetString());
        }
        finally { sessionGate?.Release(); window.Close(); }
    });
    [Fact]
    public void RegistrationKeepsOldHistoryOnceAndLogoutClearsTheCaptureAndUsesACleanGuestLibrary() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var notesRoot = Directory.CreateDirectory(Path.Combine(fixture.First.RootPath, fixture.First.OutputSubfolder)).FullName;
        File.WriteAllText(Path.Combine(notesRoot, "apple.md"), "# apple\n\nAn existing local word.");
        var guestState = Path.Combine(fixture.Root, "guest-state");
        var rootHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(notesRoot.ToUpperInvariant())));
        var metadata = Path.Combine(guestState, rootHash);
        var catalog = new LibraryCatalog(fixture.First, metadata);
        var scan = catalog.ScanAsync(); Await(scan);
        var roaming = new RoamingSession(Path.Combine(metadata, "roaming.json"), random: () => 0);
        Await(roaming.NextAsync(scan.Result.Notes));
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        var settings = new AppSettings(new("", "", ""), fixture.First);
        var store = new MemoryStore(settings);
        var vm = new MainViewModel(new CaptureService(new FakeWordAnalyzer(), new VaultStore(), TimeProvider.System), settings);
        var window = new MainWindow(vm, store, new WindowsNavigator(), sync: sync, learningStateDirectory: guestState,
            settingsDirectory: fixture.Root);
        try
        {
            vm.PrepareDraft("private", "A draft from the previous local session.");
            Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First, true));
            Await(window.AccountTransition);
            Assert.Empty(vm.Word); Assert.Empty(vm.Context); Assert.Null(vm.SelectedNote);
            Assert.True(File.Exists(Path.Combine(sync.AccountStateDirectory!, "library", rootHash, "roaming.json")));
            Await(sync.Logout());
            Await(window.AccountTransition);
            Assert.NotEqual(fixture.First.RootPath, vm.Settings.Vault.RootPath);
            Assert.Equal(vm.Settings.Vault, store.Settings.Vault);
            Assert.True(Directory.Exists(vm.Settings.Vault.RootPath));
            Assert.True(File.Exists(Path.Combine(notesRoot, "apple.md")));
            Assert.False(Directory.Exists(Path.Combine(vm.Settings.Vault.RootPath, vm.Settings.Vault.OutputSubfolder)));
            var bVault = vm.Settings.Vault;
            var bWordRoot = Directory.CreateDirectory(Path.Combine(bVault.RootPath, bVault.OutputSubfolder)).FullName;
            File.WriteAllText(Path.Combine(bWordRoot, "private-b.md"), "# Private notes claimed by B");
            Await(sync.SignIn("https://sync.example.test", "bob", "private-password", bVault, true));
            Await(window.AccountTransition);
            Await(sync.Logout()); Await(window.AccountTransition);
            Assert.NotEqual(bVault.RootPath, vm.Settings.Vault.RootPath);
            Assert.Equal(vm.Settings.Vault, store.Settings.Vault);
            Assert.True(File.Exists(Path.Combine(bWordRoot, "private-b.md")));
            Assert.False(Directory.Exists(Path.Combine(vm.Settings.Vault.RootPath, vm.Settings.Vault.OutputSubfolder)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ClaimingGuestPreservesItsApiSettingsOnlyInThatAccountAndOtherAccountsStartEmpty() => OnUi(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        var settings = new AppSettings(new("https://api.example.test", "guest-owned-key", "private-model"), fixture.First);
        var store = new MemoryStore(settings);
        var vm = new MainViewModel(new CaptureService(new FakeWordAnalyzer(), new VaultStore(), TimeProvider.System), settings);
        var window = new MainWindow(vm, store, new WindowsNavigator(), sync: sync, settingsDirectory: fixture.Root);
        try
        {
            Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First, true));
            Await(window.AccountTransition);
            var aDirectory = sync.AccountStateDirectory!;
            Assert.Equal("guest-owned-key", vm.Settings.Api.ApiKey);
            Assert.Empty(store.Settings.Api.ApiKey);
            Assert.DoesNotContain("guest-owned-key", File.ReadAllText(Path.Combine(aDirectory, "settings.json")));
            Await(sync.Logout()); Await(window.AccountTransition);
            Assert.Empty(vm.Settings.Api.ApiKey);
            Await(sync.SignIn("https://sync.example.test", "bob", "private-password", fixture.Second));
            Await(window.AccountTransition);
            Assert.Empty(vm.Settings.Api.ApiKey); Assert.Empty(vm.Settings.Api.BaseUrl); Assert.Empty(vm.Settings.Api.Model);
            Await(sync.Logout()); Await(window.AccountTransition);
            Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First));
            Await(window.AccountTransition);
            Assert.Equal("guest-owned-key", vm.Settings.Api.ApiKey);
            Assert.Equal("private-model", vm.Settings.Api.Model);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ColdStartupLoadsOnlyTheSelectedAccountApiScopeAndAccountStoreDoesNotRewriteGuest() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var guest = new AppSettings(new("https://api.example.test", "guest-key", "guest-model"), fixture.First);
        var guestStore = new MemoryStore(guest);
        var aDirectory = Path.Combine(fixture.Root, "account-a");
        var bDirectory = Path.Combine(fixture.Root, "account-b");
        var aStore = LocalAccountSettings.Store(guestStore, aDirectory);
        var aSettings = new AppSettings(new("https://api.example.test", "account-a-key", "a-model"), fixture.First);
        Await(aStore.SaveAsync(aSettings, default));
        var aLoad = LocalAccountSettings.LoadAsync(guest, fixture.First, aDirectory); Await(aLoad);
        var bLoad = LocalAccountSettings.LoadAsync(guest, fixture.Second, bDirectory); Await(bLoad);
        Assert.Equal("account-a-key", aLoad.Result.Api.ApiKey);
        Assert.Empty(bLoad.Result.Api.ApiKey);
        Assert.Equal(fixture.Second, bLoad.Result.Vault);
        Assert.Equal("guest-key", guestStore.Settings.Api.ApiKey);
        Assert.DoesNotContain("account-a-key", File.ReadAllText(Path.Combine(aDirectory, "settings.json")));
    });
    [Fact]
    public void LoginPersistsOnlyEncryptedCredentialsAndAnotherAccountNeedsItsOwnDirectory() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First));
        var firstProfile = sync.AccountStateDirectory;
        Assert.True(sync.SignedIn); Assert.Equal("alice", sync.Username);
        Await(sync.QueueStateAsync("preference/theme", new { name = "pink" }));
        var config = File.ReadAllText(Path.Combine(fixture.Root, "sync", "connection.json"));
        Assert.DoesNotContain("token-alice", config); Assert.DoesNotContain("private-password", config);
        Await(sync.Logout());
        Assert.False(sync.Connected); Assert.False(File.Exists(Path.Combine(fixture.Root, "sync", "connection.json")));
        var forbidden = sync.SignIn("https://sync.example.test", "bob", "other-password", fixture.First);
        Assert.Throws<InvalidOperationException>(() => Await(forbidden));
        Assert.False(sync.Connected);
        Assert.Contains(fixture.Remote.Requests, request => request.Path == "/v1/account/logout" && request.Token == "token-bob");
        Await(sync.SignIn("https://sync.example.test", "bob", "other-password", fixture.Second));
        Assert.NotEqual(firstProfile, sync.AccountStateDirectory);
        Assert.False(File.Exists(Path.Combine(sync.AccountStateDirectory!, "state.json")));
        Assert.True(File.Exists(Path.Combine(firstProfile!, "state.json")));
        Assert.Equal("device-private-key", File.ReadAllText(Path.Combine(fixture.Root, "settings.json")));
    });

    [Fact]
    public void ExistingPairCanRegisterWithoutChangingItsVaultAndPasswordChangeUsesTheSession() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new Fixture();
        using var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote);
        Await(sync.Bind("https://sync.example.test", "old-pair", fixture.First));
        Assert.False(sync.SignedIn);
        Await(sync.SignIn(sync.Server, "alice", "account-password", fixture.First, true));
        var registration = fixture.Remote.Requests.Single(request => request.Path == "/v1/account/register");
        Assert.Equal("paired-token", registration.Token);
        Assert.True(sync.SignedIn);
        Await(sync.ChangePassword("account-password", "updated-password"));
        var change = fixture.Remote.Requests.Single(request => request.Path == "/v1/account/password");
        Assert.Equal("token-alice", change.Token);
        Assert.Equal("vault-alice", change.Vault);
        Assert.DoesNotContain("updated-password", File.ReadAllText(Path.Combine(fixture.Root, "sync", "connection.json")));
    });

    [Fact]
    public void NotesReachTheServerBeforeStateAndRestartCanReadTheDurableStateOffline() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.First.RootPath, fixture.First.OutputSubfolder);
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "apple.md"), "# apple");
        using (var sync = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, fixture.Remote))
        {
            Await(sync.SignIn("https://sync.example.test", "alice", "private-password", fixture.First));
            Await(sync.QueueStateAsync("preference/theme", new { name = "mono" }));
            Await(sync.Tick());
            Assert.StartsWith("上次同步", sync.Status);
            Assert.Single(sync.Notes);
            Assert.True(fixture.Remote.Requests.FindIndex(request => request.Path == "/v1/operations") <
                fixture.Remote.Requests.FindIndex(request => request.Path == "/v1/state/operations"));
        }
        var offline = new AccountRemote { Offline = true };
        using var restored = new DesktopSync(fixture.Root, Dispatcher.CurrentDispatcher, offline);
        IReadOnlyList<SyncStateEntry>? entries = null;
        restored.ImportStateAsync = (state, _) => { entries = state; return Task.CompletedTask; };
        Await(restored.RestoreLocalStateAsync());
        Assert.Equal("mono", Assert.Single(entries!).Value.GetProperty("name").GetString());
        Assert.Empty(offline.Requests);
    });

    private static void Await(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < until) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(task.IsCompleted, "The account operation did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void OnUi(Action action) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(action);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string name, params object?[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static SyncStateEntry Entry(string key, object value) => new(key, JsonSerializer.SerializeToElement(value));
    private sealed class MemoryStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings Settings { get; private set; } = settings;
        public TaskCompletionSource? SaveGate { get; init; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SettingsLoadResult> LoadAsync(CancellationToken ct) => Task.FromResult(new SettingsLoadResult(Settings, ""));
        public async Task SaveAsync(AppSettings value, CancellationToken ct)
        {
            SaveStarted.TrySetResult();
            if (SaveGate is not null) await SaveGate.Task;
            Settings = value;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tuci-account-tests-" + Guid.NewGuid().ToString("N"));
        public VaultOptions First => new(Path.Combine(Root, "first"), "notes", false);
        public VaultOptions Second => new(Path.Combine(Root, "second"), "notes", false);
        public AccountRemote Remote { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(First.RootPath); Directory.CreateDirectory(Second.RootPath);
            File.WriteAllText(Path.Combine(Root, "settings.json"), "device-private-key");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed record Request(string Path, string? Token, string? Vault);
    private sealed class AccountRemote : HttpMessageHandler
    {
        public bool Offline { get; init; }
        public List<Request> Requests { get; } = [];
        private readonly List<SyncChange> _notes = [];
        private readonly List<StateChange> _states = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(path, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("X-Tuci-Vault", out var values) ? values.Single() : null));
            if (path == "/v1/redeem") return Reply(new SyncIdentity("paired-token", "vault-alice"));
            if (path is "/v1/account/login" or "/v1/account/register")
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var name = body.GetProperty("username").GetString()!;
                Assert.False(body.GetProperty("browser").GetBoolean());
                return Reply(new AccountIdentity("token-" + name, "vault-" + name, "user-" + name, name, "device-" + name));
            }
            if (path is "/v1/account/logout" or "/v1/account/password") return new(HttpStatusCode.NoContent);
            if (path == "/v1/account/me")
            {
                var name = request.Headers.Authorization!.Parameter!["token-".Length..];
                return Reply(new AccountProfile("user-" + name, name, "vault-" + name, "device-" + name));
            }
            if (path == "/v1/operations")
            {
                var operation = (await request.Content!.ReadFromJsonAsync<SyncOperation>(ct))!;
                _notes.Add(new(_notes.Count + 1, operation.NoteId, 1, operation.Path, operation.Markdown, operation.Deleted));
                return Reply(new SyncReceipt(operation.NoteId, 1, operation.Path, false));
            }
            if (path == "/v1/changes")
            {
                var after = long.Parse(request.RequestUri.Query.Split('=')[1]);
                return Reply(new SyncPage(_notes.Where(note => note.Seq > after).ToArray(), _notes.Count, false));
            }
            if (path == "/v1/state/operations")
            {
                var operation = (await request.Content!.ReadFromJsonAsync<StateOperation>(ct))!;
                _states.Add(new(_states.Count + 1, operation.Key, operation.Value, operation.Deleted));
                return Reply(new StateReceipt(operation.Key, _states.Count));
            }
            if (path == "/v1/state/changes")
            {
                var after = long.Parse(request.RequestUri.Query.Split('=')[1]);
                return Reply(new StatePage(_states.Where(state => state.Seq > after).ToArray(), _states.Count, false));
            }
            throw new InvalidOperationException("Unexpected request: " + path);
        }
        private static HttpResponseMessage Reply<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
