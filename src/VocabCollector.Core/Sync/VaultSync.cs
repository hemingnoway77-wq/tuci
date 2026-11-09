using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VocabCollector.Core.Storage;

namespace VocabCollector.Core.Sync;

public sealed record SyncedNote(string Id, string Path, long Version, string Hash, Dictionary<string,string>? Regions);
public sealed record VaultSyncResult(int Uploaded, int Downloaded, int Conflicts, bool Reconciled = false,
    IReadOnlyList<SyncedNote>? Notes = null);

// The journal is scoped to one verified server, vault and local directory by its caller.
// Pending operations and their original IDs survive disconnection, lost replies and restarts.
public sealed class VaultSync(VaultOptions options, string stateFile, ISyncRemote remote, Action<string>? verifyBinding = null,
    IReadOnlyDictionary<string, string>? noteIdentities = null)
{
    private sealed class State
    {
        public long Cursor { get; set; }
        public string? LastSignature { get; set; }
        public Dictionary<string, SyncedNote> Notes { get; set; } = [];
        public List<SyncOperation>? Pending { get; set; }
        public required string Root { get; set; }
        // Null is omitted so existing checkpoints retain their exact checksum.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, long>? Tombstones { get; set; }
    }

    public async Task<VaultSyncResult> RunAsync(CancellationToken ct)
    {
        var root = VaultPaths.WordRoot(options);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        await using var session = new FileStream(stateFile + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        verifyBinding?.Invoke(root);
        State state;
        bool rebuilt = false;
        try { state = await Load(root, ct); }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            state = Restart(root);
            rebuilt = true;
        }
        try { return await Exchange(root, state, rebuilt, ct); }
        catch (HistoryChangedException) when (!rebuilt)
        {
            verifyBinding?.Invoke(root);
            return await Exchange(root, Restart(root), true, ct);
        }
    }

    private sealed record Checkpoint(string Checksum, State State);
    private sealed class HistoryChangedException() : IOException("服务器历史已发生变化，请重新核对词库。");

    private State Restart(string root)
    {
        // Recovery always starts as a fresh merge. No deletion or stale-version updates from a damaged journal
        // are sent to the restored server. Preserve the old journal including any unacknowledged body.
        if (File.Exists(stateFile))
            File.Move(stateFile, stateFile + ".recover-" + Guid.NewGuid().ToString("N") + ".json");
        return new State { Root = root };
    }

    private async Task<State> Load(string root, CancellationToken ct)
    {
        if (!File.Exists(stateFile)) return new State { Root = root };
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllBytesAsync(stateFile, ct));
        if (checkpoint?.State is not {} state || checkpoint.Checksum != Checksum(state))
            throw new InvalidDataException("同步记录校验失败。");
        if (!SyncFiles.Paths.Equals(state.Root, root)) throw new InvalidOperationException("已绑定词库的路径发生变化。");
        if (state.Cursor < 0 || state.Notes == null || state.Cursor > 0 && state.LastSignature?.Length != 64)
            throw new InvalidDataException("同步记录不完整。");
        if (state.Tombstones?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0 || state.Notes.ContainsKey(pair.Key)) == true)
            throw new InvalidDataException("同步删除记录不完整。");
        return state;
    }

    private static string Checksum<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private async Task<SyncPage> ReadPage(long after, CancellationToken ct)
    {
        try { return await remote.ChangesAsync(after, ct); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { throw new HistoryChangedException(); }
    }

    private async Task<VaultSyncResult> Exchange(string root, State state, bool rebuilt, CancellationToken ct)
    {
        verifyBinding?.Invoke(root);
        if (!Directory.Exists(root))
        {
            if (state.Notes.Count > 0 || state.Cursor > 0 || state.Pending?.Count > 0)
                throw new DirectoryNotFoundException("已绑定词库暂不可用。");
            Directory.CreateDirectory(root);
        }

        if (state.Pending == null)
        {
            await using var vaultLock = await LockVault(root, ct);
            verifyBinding?.Invoke(root);
            var files = SyncFiles.Snapshot(root, ct);
            var regions = await LinkRefresher.ExportRegions(options, ct);
            state.Pending = Plan(root, files, state.Notes.Values, regions, noteIdentities, state.Tombstones);
            if (state.Pending.Count > 0) await Save(state, ct);
        }

        SyncPage? firstPage = null;
        if (state.Cursor > 0)
        {
            // Check the last immutable revision before writing, so new uploads cannot conceal a database rollback.
            var history = await ReadPage(state.Cursor - 1, ct);
            var anchor = history.Changes?.FirstOrDefault();
            if (anchor == null || anchor.Seq != state.Cursor || Checksum(anchor) != state.LastSignature)
                throw new HistoryChangedException();
            if (state.Pending.Count == 0 && (history.Changes!.Length > 1 || !history.HasMore))
                firstPage = history with { Changes = history.Changes![1..] };
        }
        int conflicts = 0;
        foreach (var op in state.Pending)
        {
            ct.ThrowIfCancellationRequested();
            var receipt = await remote.PushAsync(op, ct);
            if (receipt.Conflict) conflicts++;
        }

        var cursor = state.Cursor;
        var updates = new Dictionary<string, SyncChange>();
        int received = 0;
        var lastSignature = state.LastSignature;
        while (true)
        {
            var page = firstPage ?? await ReadPage(cursor, ct);
            firstPage = null;
            if (page.Cursor < cursor || page.Changes == null || page.HasMore && page.Cursor <= cursor)
                throw new InvalidDataException("词库同步游标无效。");
            long sequence = cursor;
            foreach (var change in page.Changes)
            {
                if (string.IsNullOrWhiteSpace(change.NoteId) || change.Version <= 0 || change.Seq <= sequence ||
                    change.Seq > page.Cursor || change.Markdown == null || !SyncFiles.IsNotePath(change.Path))
                    throw new InvalidDataException("词库变更记录无效。");
                updates[change.NoteId] = change;
                sequence = change.Seq;
            }
            if (sequence != page.Cursor) throw new InvalidDataException("变更页缺少最后一条记录。");
            if (page.Changes.Length > 0) lastSignature = Checksum(page.Changes[^1]);
            received += page.Changes.Length;
            cursor = page.Cursor;
            if (!page.HasMore) break;
        }
        if (updates.Count != 0)
        {
            await using var vaultLock = await LockVault(root, ct);
            verifyBinding?.Invoke(root);
            await Apply(root, state, updates, ct);
        }
        var uploaded = state.Pending.Count;
        state.Pending = null;
        if (uploaded > 0 || state.Cursor != cursor || !File.Exists(stateFile))
        {
            state.Cursor = cursor;
            state.LastSignature = lastSignature;
            await Save(state, ct);
        }
        return new(uploaded, received, conflicts, rebuilt, state.Notes.Values.ToArray());
    }

    private static List<SyncOperation> Plan(string root, Dictionary<string, string> files, IEnumerable<SyncedNote> tracked,
        Dictionary<string, Dictionary<string, string>> regions, IReadOnlyDictionary<string, string>? identities,
        IReadOnlyDictionary<string, long>? tombstones)
    {
        var byPath = tracked.ToDictionary(n => n.Path, SyncFiles.Paths);
        var byId = byPath.Values.ToDictionary(note => note.Id, StringComparer.Ordinal);
        var missing = byPath.Values.Where(n => !files.ContainsKey(n.Path)).ToDictionary(n => n.Id);
        var addedHashes = files.Where(f => !byPath.ContainsKey(f.Key)).ToLookup(f => SyncFiles.Hash(f.Value), f => f.Key);
        var missingHashes = missing.Values.ToLookup(n => n.Hash);
        var ops = new List<SyncOperation>();
        foreach (var (path, markdown) in files)
        {
            var hash = SyncFiles.Hash(markdown);
            regions.TryGetValue(path, out var ownership);
            var preferredId = identities?.GetValueOrDefault(path);
            SyncedNote? old;
            if (preferredId is not null && byId.TryGetValue(preferredId, out var identified))
            {
                old = identified;
                missing.Remove(old.Id);
                if (old.Path.Equals(path, StringComparison.Ordinal) && old.Hash == hash && Same(ownership, old.Regions)) continue;
            }
            else if (byPath.TryGetValue(path, out old))
            {
                // Windows path identity is case-insensitive, even when a peer uses different display casing.
                if (old.Hash == hash && Same(ownership, old.Regions)) continue;
            }
            else if (addedHashes[hash].Count() == 1 && missingHashes[hash].Count() == 1)
            {
                old = missingHashes[hash].Single();
                missing.Remove(old.Id);
                ownership ??= old.Regions;
            }
            // Only an explicitly restored identity can reuse a deleted head. A new
            // file at its old path still starts with a new identity and version zero.
            var version = old?.Version ?? (preferredId is not null ? tombstones?.GetValueOrDefault(preferredId) : null) ?? 0;
            ops.Add(new(Guid.NewGuid().ToString(), old?.Id ?? preferredId ?? Guid.NewGuid().ToString(), version,
                path, markdown, false, ownership));
        }
        foreach (var n in missing.Values)
        {
            // A formerly normal folder might now point elsewhere. Never treat an unreadable branch as deletion.
            var expectedMissing = SyncFiles.Resolve(root, n.Path);
            if (SyncFiles.Current(expectedMissing) != null) throw new IOException("词库正在变化，稍后重新扫描。");
            ops.Add(new(Guid.NewGuid().ToString(), n.Id, n.Version, n.Path, "", true));
        }
        return ops;
    }

    private async Task Apply(string root, State state, Dictionary<string, SyncChange> updates, CancellationToken ct)
    {
        var next = new Dictionary<string, SyncedNote>(state.Notes);
        var tombstones = state.Tombstones is null ? new Dictionary<string, long>() : new(state.Tombstones);
        var expected = state.Notes.Values.ToDictionary(n => n.Path, n => (string?)n.Hash, SyncFiles.Paths);
        foreach (var op in state.Pending!) expected[op.Path] = op.Deleted ? null : SyncFiles.Hash(op.Markdown);
        var metadata = new Dictionary<string, Dictionary<string,string>?>(SyncFiles.Paths);
        foreach (var change in updates.Values)
        {
            SyncFiles.Resolve(root, change.Path);
            if (change.Deleted) { next.Remove(change.NoteId); tombstones[change.NoteId] = change.Version; }
            else { next[change.NoteId] = new(change.NoteId, change.Path, change.Version, SyncFiles.Hash(change.Markdown), change.Regions); tombstones.Remove(change.NoteId); }
        }
        var currentPaths = next.Values.ToDictionary(n => n.Path, SyncFiles.Paths);
        foreach (var path in state.Notes.Values.Select(n => n.Path).Concat(state.Pending!.Select(o => o.Path)).Distinct(SyncFiles.Paths))
        {
            ct.ThrowIfCancellationRequested();
            if (currentPaths.ContainsKey(path)) continue;
            SyncFiles.Apply(root, path, null, expected.GetValueOrDefault(path));
            metadata[path] = null;
        }
        foreach (var change in updates.Values.Where(c => !c.Deleted))
        {
            ct.ThrowIfCancellationRequested();
            SyncFiles.Apply(root, change.Path, change.Markdown, expected.GetValueOrDefault(change.Path));
            metadata[change.Path] = change.Regions;
        }
        await LinkRefresher.ImportRegions(options, metadata, ct);
        state.Notes = next;
        state.Tombstones = tombstones.Count == 0 ? null : tombstones;
    }

    private static bool Same(Dictionary<string,string>? a, Dictionary<string,string>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.All(r => b != null && b.TryGetValue(r.Key, out var v) && v == r.Value));

    private static Task<FileStream> LockVault(string root, CancellationToken ct)
    {
        var file = Path.Combine(root, ".vocabcollector.write.lock");
        VaultPaths.EnsureInside(root, file);
        return VaultStore.AcquireLockAsync(file, ct);
    }

    private async Task Save(State state, CancellationToken ct)
    {
        var temp = stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new Checkpoint(Checksum(state), state), cancellationToken: ct);
                stream.Flush(true);
            }
            File.Move(temp, stateFile, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
