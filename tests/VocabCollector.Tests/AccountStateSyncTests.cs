using System.Text.Json;
using VocabCollector.Core.Sync;

namespace VocabCollector.Tests;

public sealed class AccountStateSyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "account-state-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_directory, "state.json");
    private static SyncStateEntry Theme(string name) => new("preference/theme", JsonSerializer.SerializeToElement(new { name }));

    [Fact]
    public async Task LostReplyReplaysThePersistedOperationIdAcrossRestart()
    {
        var remote = new FakeRemote { LoseReply = true };
        var sync = new AccountStateSync(Journal, remote);
        await sync.EnqueueAsync(Theme("pink"));
        var checkpoint = await File.ReadAllTextAsync(Journal);
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.RunAsync());
        Assert.Equal(checkpoint, await File.ReadAllTextAsync(Journal));
        var result = await new AccountStateSync(Journal, remote).RunAsync();
        Assert.Equal("pink", Assert.Single(result).Value.GetProperty("name").GetString());
        Assert.Equal(2, remote.Attempts.Count);
        Assert.Single(remote.Attempts.Distinct());
        Assert.Single(remote.Changes);
        await new AccountStateSync(Journal, remote).EnqueueAsync(Theme("pink"));
        await new AccountStateSync(Journal, remote).RunAsync();
        Assert.Equal(2, remote.Attempts.Count);
    }

    [Fact]
    public async Task ANewPreferenceFollowsAnUnacknowledgedOperationWithoutChangingItsId()
    {
        var remote = new FakeRemote { LoseReply = true };
        var sync = new AccountStateSync(Journal, remote);
        await sync.EnqueueAsync(Theme("pink"));
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.RunAsync());
        await new AccountStateSync(Journal, remote).EnqueueAsync(Theme("mono"));
        var result = await new AccountStateSync(Journal, remote).RunAsync();
        Assert.Equal("mono", Assert.Single(result).Value.GetProperty("name").GetString());
        Assert.Equal(3, remote.Attempts.Count);
        Assert.Equal(remote.Attempts[0], remote.Attempts[1]);
        Assert.NotEqual(remote.Attempts[1], remote.Attempts[2]);
        Assert.Equal(2, remote.Changes.Count);
    }

    [Fact]
    public async Task InvalidRemoteValueDoesNotAdvanceCursorOrDiscardOutbox()
    {
        var remote = new FakeRemote { InvalidPage = true };
        var sync = new AccountStateSync(Journal, remote);
        await sync.EnqueueAsync(Theme("pink"));
        var original = await File.ReadAllTextAsync(Journal);
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.RunAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(Journal));
        remote.InvalidPage = false;
        Assert.Equal("pink", Assert.Single(await sync.RunAsync()).Value.GetProperty("name").GetString());
        Assert.Single(remote.Attempts.Distinct());
    }

    [Fact]
    public async Task CorruptJournalIsReportedAndPreserved()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Journal, "damaged");
        var sync = new AccountStateSync(Journal, new FakeRemote());
        await Assert.ThrowsAsync<IOException>(() => sync.EnqueueAsync(Theme("pink")));
        Assert.Equal("damaged", await File.ReadAllTextAsync(Journal));
    }

    [Fact]
    public async Task OfflineReadReturnsLatestPendingPreferenceWithoutContactingRemote()
    {
        var remote = new FakeRemote();
        var sync = new AccountStateSync(Journal, remote);
        await sync.EnqueueAsync(Theme("pink"));
        await sync.RunAsync();
        await sync.EnqueueAsync(Theme("mono"));
        var before = await File.ReadAllTextAsync(Journal);
        Assert.Equal("mono", Assert.Single(await new AccountStateSync(Journal, remote).ReadAsync()).Value.GetProperty("name").GetString());
        Assert.Single(remote.Attempts);
        Assert.Equal(before, await File.ReadAllTextAsync(Journal));
    }

    [Fact]
    public async Task NoteReadingStateRequiresMatchingCanonicalNoteIdentity()
    {
        var sync = new AccountStateSync(Journal, new FakeRemote());
        await sync.EnqueueAsync(new SyncStateEntry("note-reading/cloud-note", JsonSerializer.SerializeToElement(new
        { noteId = "cloud-note", progress = .5, anchor = "paragraph:3", title = "apple" })));
        Assert.Equal("note-reading/cloud-note", Assert.Single(await sync.ReadAsync()).Key);
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.EnqueueAsync(new SyncStateEntry("note-reading/cloud-note",
            JsonSerializer.SerializeToElement(new { noteId = "someone-else", progress = .5, anchor = "" }))));
    }

    [Theory]
    [InlineData("event/not-an-id/shown", "{}")]
    [InlineData("preference/theme", "{\"name\":\"unknown\"}")]
    [InlineData("reading/0123456789012345678901234567890123456789012345678901234567890123", "{\"url\":\"file:///etc/passwd\",\"progress\":0.2}")]
    public async Task InvalidEntryNeverCreatesAnOutbox(string key, string json)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AccountStateSync(Journal, new FakeRemote())
            .EnqueueAsync(new SyncStateEntry(key, document.RootElement)));
        Assert.False(File.Exists(Journal));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class FakeRemote : ISyncStateRemote
    {
        public bool LoseReply, InvalidPage;
        public List<string> Attempts { get; } = [];
        public List<StateChange> Changes { get; } = [];
        private readonly Dictionary<string, StateReceipt> _receipts = [];
        public Task<StatePage> StateChangesAsync(long cursor, CancellationToken cancellationToken)
        {
            if (InvalidPage) return Task.FromResult(new StatePage([new(1, "preference/theme", JsonSerializer.SerializeToElement(new { name = "invalid" }))], 1, false));
            return Task.FromResult(new StatePage(Changes.Where(change => change.Seq > cursor).ToArray(), Changes.Count, false));
        }
        public Task<StateReceipt> PushStateAsync(StateOperation operation, CancellationToken cancellationToken)
        {
            Attempts.Add(operation.OperationId);
            if (!_receipts.TryGetValue(operation.OperationId, out var receipt))
            {
                var seq = Changes.Count + 1;
                Changes.Add(new(seq, operation.Key, operation.Value.Clone(), operation.Deleted));
                _receipts[operation.OperationId] = receipt = new(operation.Key, seq);
            }
            if (LoseReply) { LoseReply = false; throw new HttpRequestException("reply lost after commit"); }
            return Task.FromResult(receipt);
        }
    }
}
