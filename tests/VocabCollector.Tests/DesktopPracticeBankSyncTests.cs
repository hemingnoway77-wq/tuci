using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Sync;
using VocabCollector.Desktop;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class DesktopPracticeBankSyncTests
{
    [Fact]
    public void DefaultSyncNeverQueriesTheOptionalBankEndpoint() => OnUi(() =>
    {
        using var fixture = new Fixture(1);
        Await(fixture.Sync.Tick());
        Assert.Equal(0, fixture.Remote.BankCalls);
        Assert.StartsWith("上次同步", fixture.Sync.Status);
        Assert.Single(fixture.Sync.Notes);
    });

    [Fact]
    public void BanksFollowNotesAndStateInBatchesOfAtMost32ForTheCurrentVault() => OnUi(() =>
    {
        using var fixture = new Fixture(65);
        var imported = new List<CloudPracticeBank>(); var stateImported = false;
        fixture.Sync.ImportStateAsync = (_, _) => { stateImported = true; return Task.CompletedTask; };
        fixture.Sync.ImportPracticeBanksAsync = (banks, token) =>
        {
            Assert.True(stateImported && fixture.Sync.StateSyncGeneration > 0);
            Assert.Equal(65, fixture.Sync.Notes.Count);
            Assert.False(token.IsCancellationRequested);
            imported.AddRange(banks); return Task.CompletedTask;
        };
        Await(fixture.Sync.Tick());
        Assert.Equal(new[] { 32, 32, 1 }, fixture.Remote.Batches.Select(batch => batch.Length));
        Assert.Equal(fixture.Sync.Notes.Select(note => note.Id).Order(), imported.Select(bank => bank.NoteId).Order());
        Assert.StartsWith("上次同步", fixture.Sync.Status);
        Assert.All(fixture.Remote.BankCredentials, credentials => Assert.Equal(("token-fixture", "vault-fixture"), credentials));
    });

    [Fact]
    public void PartialPagesContinueWithOnlyMissingIdsUntilTheServerReturnsNoBank() => OnUi(() =>
    {
        using var fixture = new Fixture(4);
        var imported = new List<CloudPracticeBank>();
        fixture.Sync.ImportPracticeBanksAsync = (banks, _) => { imported.AddRange(banks); return Task.CompletedTask; };
        fixture.Remote.SelectBanks = ids => fixture.Remote.BankCalls switch
        {
            1 => ids.Take(2).Select(PracticeBankClientTests.Bank).ToArray(),
            2 => ids.Where(id => id == "note-2").Select(PracticeBankClientTests.Bank).ToArray(),
            _ => [] // The fourth note has no complete bank; this must terminate the batch.
        };
        Await(fixture.Sync.Tick());
        Assert.Equal(new[] { "note-0", "note-1", "note-2" }, imported.Select(bank => bank.NoteId));
        Assert.Equal(3, fixture.Remote.BankCalls);
        Assert.Equal(new[] { "note-0", "note-1", "note-2", "note-3" }, fixture.Remote.Batches[0]);
        Assert.Equal(new[] { "note-2", "note-3" }, fixture.Remote.Batches[1]);
        Assert.Equal(new[] { "note-3" }, fixture.Remote.Batches[2]);
        Assert.All(fixture.Remote.Batches, batch => Assert.InRange(batch.Length, 1, 32));
        Assert.StartsWith("上次同步", fixture.Sync.Status);
    });

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void OptionalBankFailureDoesNotInvalidateSuccessfulNoteAndStateSync(HttpStatusCode status) => OnUi(() =>
    {
        using var fixture = new Fixture(1); fixture.Remote.BankStatus = status;
        fixture.Sync.ImportPracticeBanksAsync = (_, _) => throw new InvalidOperationException("No bank should be imported");
        Await(fixture.Sync.Tick());
        Assert.Equal(1, fixture.Remote.BankCalls);
        Assert.True(fixture.Sync.CanUseAccount);
        Assert.Single(fixture.Sync.Notes);
        Assert.True(fixture.Sync.StateSyncGeneration > 0);
        Assert.StartsWith("上次同步", fixture.Sync.Status);
    });

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BankAuthenticationRefusalsStillUseTheExistingSessionInvalidationPolicy(HttpStatusCode status) => OnUi(() =>
    {
        using var fixture = new Fixture(1); fixture.Remote.BankStatus = status;
        fixture.Sync.ImportPracticeBanksAsync = (_, _) => Task.CompletedTask;
        Await(fixture.Sync.Tick());
        Assert.False(fixture.Sync.Connected);
        Assert.Equal(AccountAccess.LoginRequired, fixture.Sync.Access);
        Assert.True(File.Exists(Path.Combine(fixture.Files.Root, "words", "word0.md")));
    });

    [Fact]
    public void ALocalImporterFailureDoesNotConvertCommittedNoteSyncIntoFailure() => OnUi(() =>
    {
        using var fixture = new Fixture(1);
        fixture.Sync.ImportPracticeBanksAsync = (_, _) => throw new IOException("local-import-canary");
        Await(fixture.Sync.Tick());
        Assert.StartsWith("上次同步", fixture.Sync.Status);
        Assert.DoesNotContain("local-import-canary", fixture.Sync.Status);
        Assert.Single(fixture.Sync.Notes);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReplyForAnOldBindingOrReplacedImporterIsNotAppliedToTheNewScope(bool changeBinding) => OnUi(() =>
    {
        using var fixture = new Fixture(1); fixture.Remote.BankGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCalls = 0; var newCalls = 0;
        fixture.Sync.ImportPracticeBanksAsync = (_, _) => { oldCalls++; return Task.CompletedTask; };
        var running = fixture.Sync.Tick(); Await(Task.WhenAny(running, fixture.Remote.BankStarted.Task));
        Assert.True(fixture.Remote.BankStarted.Task.IsCompleted, "Sync must start its configured bank request after notes and state.");
        if (changeBinding)
        {
            // Disconnect is the same scope invalidation used by RequireLogin/Logout; no production account is involved.
            typeof(DesktopSync).GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Sync, null);
        }
        else fixture.Sync.ImportPracticeBanksAsync = (_, _) => { newCalls++; return Task.CompletedTask; };
        fixture.Remote.BankGate.SetResult(); Await(running);
        Assert.Equal(0, oldCalls); Assert.Equal(0, newCalls);
    });

    private static void OnUi(Action test) => ((Lazy<Dispatcher>)typeof(ReadingPageTests).GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(test);
    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(task.IsCompleted, "The isolated sync operation did not finish."); task.GetAwaiter().GetResult();
    }
    private sealed class Fixture : IDisposable
    {
        public TempVault Files { get; } = new();
        public BankRemote Remote { get; }
        public DesktopSync Sync { get; }
        public Fixture(int notes)
        {
            Remote = new BankRemote(notes);
            Sync = new DesktopSync(Files.Root, Dispatcher.CurrentDispatcher, Remote);
            Await(Sync.SignIn("https://sync.fixture.invalid", "fixture", "fixture-password", new VaultOptions(Files.Root, "words", false)));
        }
        public void Dispose() { Sync.Dispose(); Files.Dispose(); }
    }
    private sealed class BankRemote(int count) : HttpMessageHandler
    {
        public HttpStatusCode BankStatus { get; set; } = HttpStatusCode.OK;
        public int BankCalls { get; private set; }
        public List<string[]> Batches { get; } = [];
        public List<(string Token, string Vault)> BankCredentials { get; } = [];
        public TaskCompletionSource? BankGate { get; set; }
        public Func<string[], IReadOnlyList<CloudPracticeBank>>? SelectBanks { get; set; }
        public TaskCompletionSource BankStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/account/login") return Reply(new AccountIdentity("token-fixture", "vault-fixture", "user-fixture", "fixture", "device-fixture"));
            if (path == "/v1/account/me") return Reply(new AccountProfile("user-fixture", "fixture", "vault-fixture", "device-fixture"));
            if (path == "/v1/changes")
            {
                var after = long.Parse(request.RequestUri.Query.Split('=')[1]);
                return Reply(new SyncPage(Enumerable.Range(0, count).Select(index => new SyncChange(index + 1, "note-" + index, 1,
                    "word" + index + ".md", "# introduced\n\nA local sync fixture.", false)).Where(note => note.Seq > after).ToArray(), count, false));
            }
            if (path == "/v1/state/changes") return Reply(new StatePage([], 0, false));
            if (path == "/v1/practice/banks")
            {
                BankCalls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var ids = body.RootElement.GetProperty("noteIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
                Batches.Add(ids);
                BankCredentials.Add((request.Headers.Authorization!.Parameter!, request.Headers.GetValues("X-Tuci-Vault").Single()));
                BankStarted.TrySetResult();
                if (BankGate is { } gate) await gate.Task.WaitAsync(ct);
                return BankStatus == HttpStatusCode.OK ? Reply(new CloudPracticeBankPage(SelectBanks?.Invoke(ids) ?? ids.Select(PracticeBankClientTests.Bank).ToArray())) : new(BankStatus);
            }
            throw new InvalidOperationException("Unexpected isolated sync request: " + path);
        }
        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
