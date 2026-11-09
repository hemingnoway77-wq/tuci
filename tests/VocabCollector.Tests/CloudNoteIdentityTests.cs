using System.Security.Cryptography;
using System.Text;
using VocabCollector.Core.Library;
using VocabCollector.Core.Sync;

namespace VocabCollector.Tests;

public sealed class CloudNoteIdentityTests
{
    [Fact]
    public async Task InboundRenameAndEditKeepTheExistingLocalLearningIdentity()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "# apple\n\nOriginal content.");
        var catalog = fixture.NewCatalog();
        var initial = Assert.Single((await catalog.ScanAsync()).Notes);
        catalog.SetCloudIdentities(new Dictionary<string, string> { ["apple.md"] = "canonical-note" },
            new Dictionary<string, string> { ["apple.md"] = Hash(initial.Markdown) });
        await catalog.ScanAsync();
        File.Delete(initial.AbsolutePath);
        var updatedText = "# apple\n\nChanged remotely while the note was renamed.";
        fixture.Write("moved/apple.md", updatedText);
        var restored = fixture.NewCatalog();
        restored.SetCloudIdentities(new Dictionary<string, string> { ["moved/apple.md"] = "canonical-note" },
            new Dictionary<string, string> { ["moved/apple.md"] = Hash(updatedText) });
        var updated = Assert.Single((await restored.ScanAsync()).Notes);
        Assert.Equal(initial.Id, updated.Id);
        Assert.Equal("canonical-note", restored.CloudNoteIds[updated.Id]);
    }

    [Fact]
    public async Task LocalMoveAndEditDoNotGiveAReusedOldPathTheCloudIdentity()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "# apple\n\nOriginal.");
        var catalog = fixture.NewCatalog();
        var original = Assert.Single((await catalog.ScanAsync()).Notes);
        catalog.SetCloudIdentities(new Dictionary<string, string> { ["apple.md"] = "canonical-note" },
            new Dictionary<string, string> { ["apple.md"] = Hash(original.Markdown) });
        await catalog.ScanAsync();
        var moved = await catalog.MoveNoteAsync(original, "moved.md");
        await catalog.SaveNoteAsync(moved, "# apple\n\nEdited after moving.");
        fixture.Write("apple.md", "# banana\n\nA separate newly created note.");
        var notes = (await catalog.ScanAsync()).Notes;
        Assert.Equal(original.Id, notes.Single(note => note.RelativePath == "moved.md").Id);
        Assert.NotEqual(original.Id, notes.Single(note => note.RelativePath == "apple.md").Id);
        Assert.Equal("canonical-note", catalog.CloudNoteIds[original.Id]);
        Assert.False(catalog.CloudNoteIds.ContainsKey(notes.Single(note => note.RelativePath == "apple.md").Id));
    }

    [Fact]
    public async Task SyncUsesCatalogIdentityToUploadAMoveAndEditWithoutDeletingItsHistory()
    {
        using var fixture = new LibraryFixture();
        var root = fixture.WordRoot;
        var first = fixture.Write("apple.md", "# apple\n\nOriginal.");
        var remote = new Remote();
        var options = new VocabCollector.Core.VaultOptions(fixture.Root, "英语单词", true);
        var state = Path.Combine(fixture.Base, "sync.json");
        await new VaultSync(options, state, remote).RunAsync(default);
        var id = remote.Operations.Single().NoteId;
        File.Delete(first); fixture.Write("moved.md", "# apple\n\nEdited after moving.");
        var result = await new VaultSync(options, state, remote, noteIdentities: new Dictionary<string, string> { ["moved.md"] = id }).RunAsync(default);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(id, remote.Operations.Last().NoteId);
        Assert.False(remote.Operations.Last().Deleted);
        Assert.Equal("moved.md", Assert.Single(result.Notes!).Path);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed class Remote : ISyncRemote
    {
        public List<SyncOperation> Operations { get; } = [];
        private readonly List<SyncChange> _changes = [];
        public Task<SyncReceipt> PushAsync(SyncOperation operation, CancellationToken ct)
        {
            Operations.Add(operation);
            var version = operation.BaseVersion + 1;
            _changes.Add(new(_changes.Count + 1, operation.NoteId, version, operation.Path, operation.Markdown, operation.Deleted));
            return Task.FromResult(new SyncReceipt(operation.NoteId, version, operation.Path, false));
        }
        public Task<SyncPage> ChangesAsync(long cursor, CancellationToken ct) =>
            Task.FromResult(new SyncPage(_changes.Where(change => change.Seq > cursor).ToArray(), _changes.Count, false));
    }
}
