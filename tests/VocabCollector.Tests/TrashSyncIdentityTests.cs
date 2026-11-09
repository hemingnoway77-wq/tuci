using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Sync;

namespace VocabCollector.Tests;

public sealed class TrashSyncIdentityTests
{
    [Fact]
    public async Task TrashSyncRestartAndRestoreReuseCanonicalIdentityAndDeletedHeadVersion()
    {
        using var fixture = new LibraryFixture();
        var remote = new VersionedRemote(); var canonical = Guid.NewGuid().ToString();
        remote.Seed(canonical, "apple.md", "# apple\n\nDownloaded content.");
        var options = new VaultOptions(fixture.Root, "英语单词", true); var state = Path.Combine(fixture.Base, "sync.json");
        var initial = await new VaultSync(options, state, remote).RunAsync(default);
        var catalog = fixture.Catalog;
        catalog.SetCloudIdentities(new Dictionary<string, string> { ["apple.md"] = canonical },
            new Dictionary<string, string> { ["apple.md"] = Hash(File.ReadAllText(Path.Combine(fixture.WordRoot, "apple.md"))) });
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        Assert.NotEqual(canonical, note.Id);
        var trash = await catalog.TrashNoteAsync(note);
        var deleted = await new VaultSync(options, state, remote).RunAsync(default);
        var deletion = Assert.Single(remote.Operations);
        Assert.Equal(canonical, deletion.NoteId); Assert.True(deletion.Deleted); Assert.Equal(1, deletion.BaseVersion);
        Assert.Empty(deleted.Notes!);
        // Recreate the catalog after sync has removed its live identity map.
        catalog = fixture.Catalog; catalog.SetCloudIdentities(new Dictionary<string, string>());
        Assert.Empty((await catalog.ScanAsync()).Notes);
        var restored = await catalog.RestoreNoteAsync(trash);
        var same = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        Assert.Equal(note.Id, restored.Id); Assert.Equal(note.Id, same.Id);
        catalog = fixture.Catalog; await catalog.ScanAsync();
        Assert.Equal(canonical, catalog.CloudNoteIds[same.Id]);
        var identities = new Dictionary<string, string> { [same.RelativePath] = catalog.CloudNoteIds[same.Id] };
        var uploaded = await new VaultSync(options, state, remote, noteIdentities: identities).RunAsync(default);
        Assert.Equal(0, uploaded.Conflicts); Assert.Equal(1, uploaded.Uploaded);
        var operation = remote.Operations.Last();
        Assert.Equal(canonical, operation.NoteId); Assert.Equal(2, operation.BaseVersion); Assert.False(operation.Deleted);
        Assert.Equal(canonical, Assert.Single(uploaded.Notes!).Id);
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(state));
        Assert.False(checkpoint.RootElement.GetProperty("State").TryGetProperty("Tombstones", out _));
    }

    [Fact]
    public async Task ANewNoteAtTheDeletedPathDoesNotClaimTheTombstone()
    {
        using var fixture = new LibraryFixture(); var remote = new VersionedRemote();
        var canonical = Guid.NewGuid().ToString(); remote.Seed(canonical, "apple.md", "original");
        var options = new VaultOptions(fixture.Root, "英语单词", true); var state = Path.Combine(fixture.Base, "sync.json");
        await new VaultSync(options, state, remote).RunAsync(default);
        File.Delete(Path.Combine(fixture.WordRoot, "apple.md"));
        await new VaultSync(options, state, remote).RunAsync(default);
        fixture.Write("apple.md", "A completely separate note at the old path.");
        var result = await new VaultSync(options, state, remote).RunAsync(default);
        var created = remote.Operations.Last();
        Assert.NotEqual(canonical, created.NoteId); Assert.Equal(0, created.BaseVersion); Assert.False(created.Deleted);
        Assert.NotEqual(canonical, Assert.Single(result.Notes!).Id);
    }

    [Fact]
    public async Task RestoringAnOlderNoteWaitsUntilASamePathReplacementDeletionHasSynced()
    {
        using var fixture = new LibraryFixture(); var remote = new VersionedRemote();
        var canonical = Guid.NewGuid().ToString(); remote.Seed(canonical, "apple.md", "original A");
        var options = new VaultOptions(fixture.Root, "英语单词", true); var state = Path.Combine(fixture.Base, "sync.json");
        await new VaultSync(options, state, remote).RunAsync(default);
        var catalog = fixture.Catalog;
        catalog.SetCloudIdentities(new Dictionary<string, string> { ["apple.md"] = canonical });
        var original = Assert.Single((await catalog.ScanAsync()).Notes);
        var trashA = await catalog.TrashNoteAsync(original);
        await new VaultSync(options, state, remote).RunAsync(default);
        catalog.SetCloudIdentities(new Dictionary<string, string>()); await catalog.ScanAsync();
        var replacement = await catalog.CreateNoteAsync("apple.md", "different B");
        await new VaultSync(options, state, remote, noteIdentities: new Dictionary<string, string> { ["apple.md"] = replacement.Id }).RunAsync(default);
        catalog.SetCloudIdentities(new Dictionary<string, string> { ["apple.md"] = replacement.Id });
        replacement = Assert.Single((await catalog.ScanAsync()).Notes);
        await catalog.TrashNoteAsync(replacement);
        var blocked = await Assert.ThrowsAsync<IOException>(() => catalog.RestoreNoteAsync(trashA));
        Assert.Contains("先同步", blocked.Message); Assert.False(File.Exists(original.AbsolutePath));
        Assert.Equal(2, (await catalog.GetTrashAsync()).Count);
        await new VaultSync(options, state, remote).RunAsync(default);
        catalog.SetCloudIdentities(new Dictionary<string, string>()); await catalog.ScanAsync();
        var restored = await catalog.RestoreNoteAsync(trashA); await catalog.ScanAsync();
        var result = await new VaultSync(options, state, remote, noteIdentities: new Dictionary<string, string> { ["apple.md"] = catalog.CloudNoteIds[restored.Id] }).RunAsync(default);
        Assert.Equal(0, result.Conflicts); Assert.Equal(canonical, Assert.Single(result.Notes!).Id);
        Assert.Equal("original A", File.ReadAllText(original.AbsolutePath));
        Assert.Single(await catalog.GetTrashAsync());
    }

    [Fact]
    public async Task ACheckpointWrittenBeforeTombstonesKeepsItsChecksumAndDoesNotReconcile()
    {
        using var fixture = new LibraryFixture(); var remote = new VersionedRemote();
        var options = new VaultOptions(fixture.Root, "英语单词", true); var state = Path.Combine(fixture.Base, "sync.json");
        // Construct exactly the old State shape, without a Tombstones property.
        var oldState = new { Cursor = 0L, LastSignature = (string?)null, Notes = new Dictionary<string, object>(), Pending = (object?)null,
            Root = fixture.WordRoot };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(oldState);
        File.WriteAllBytes(state, JsonSerializer.SerializeToUtf8Bytes(new { Checksum = Convert.ToHexStringLower(SHA256.HashData(bytes)), State = oldState }));
        var result = await new VaultSync(options, state, remote).RunAsync(default);
        Assert.False(result.Reconciled); Assert.Empty(Directory.GetFiles(fixture.Base, "sync.json.recover-*.json"));
        Assert.Empty(remote.Operations);
    }

    [Fact]
    public async Task LegacyTrashWithAnUnknownCloudIdentityDefersUntilTheLivePathIsSyncedAway()
    {
        using var fixture = new LibraryFixture(); fixture.Write("apple.md", "The older note A.");
        var catalog = fixture.Catalog; var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var trash = await catalog.TrashNoteAsync(note);
        var recordPath = Path.Combine(catalog.LocalStateDirectory, "trash", trash.Id, "record.json");
        var record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
        record.Remove("CloudId"); File.WriteAllText(recordPath, record.ToJsonString());
        catalog.SetCloudIdentities(new Dictionary<string, string> { ["apple.md"] = "another-live-canonical-note" });
        var blocked = await Assert.ThrowsAsync<IOException>(() => catalog.RestoreNoteAsync(trash));
        Assert.Contains("先同步", blocked.Message); Assert.False(File.Exists(note.AbsolutePath));
        Assert.Equal(trash, Assert.Single(await catalog.GetTrashAsync()));
        Assert.Equal(note.Markdown, File.ReadAllText(Path.Combine(catalog.LocalStateDirectory, "trash", trash.Id, "note.md")));
        catalog.SetCloudIdentities(new Dictionary<string, string>());
        var restored = await catalog.RestoreNoteAsync(trash);
        Assert.Equal(note.Id, restored.Id); Assert.Equal(note.Markdown, File.ReadAllText(note.AbsolutePath));
    }

    [Fact]
    public async Task OldTrashRecordsWithoutCloudIdentityStillRestoreTheirExactLocalNote()
    {
        using var fixture = new LibraryFixture(); fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog; var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var trash = await catalog.TrashNoteAsync(note);
        var path = Path.Combine(catalog.LocalStateDirectory, "trash", trash.Id, "record.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); json.Remove("CloudId"); File.WriteAllText(path, json.ToJsonString());
        var restored = await fixture.Catalog.RestoreNoteAsync(trash);
        Assert.Equal(note.Id, restored.Id); Assert.Equal(note.Markdown, File.ReadAllText(restored.AbsolutePath));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class VersionedRemote : ISyncRemote
    {
        private readonly List<SyncChange> _changes = [];
        private readonly Dictionary<string, SyncChange> _heads = [];
        public List<SyncOperation> Operations { get; } = [];
        public void Seed(string id, string path, string body)
        {
            var change = new SyncChange(_changes.Count + 1, id, 1, path, body, false); _changes.Add(change); _heads[id] = change;
        }
        public Task<SyncReceipt> PushAsync(SyncOperation operation, CancellationToken ct)
        {
            var version = _heads.GetValueOrDefault(operation.NoteId)?.Version ?? 0;
            if (operation.BaseVersion != version) throw new InvalidOperationException("The server would treat this as a stale write.");
            var change = new SyncChange(_changes.Count + 1, operation.NoteId, version + 1, operation.Path, operation.Markdown, operation.Deleted);
            _heads[operation.NoteId] = change; _changes.Add(change); Operations.Add(operation);
            return Task.FromResult(new SyncReceipt(change.NoteId, change.Version, change.Path, false));
        }
        public Task<SyncPage> ChangesAsync(long cursor, CancellationToken ct) => Task.FromResult(new SyncPage(_changes.Where(change => change.Seq > cursor).ToArray(), _changes.Count, false));
    }
}
