using System.Collections.Concurrent;
using System.Text.Json;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;

namespace VocabCollector.Core.Sync;

public sealed record SyncStateEntry(string Key, JsonElement Value, bool Deleted = false);
public sealed record StateOperation(string OperationId, string Key, JsonElement Value, bool Deleted = false);
public sealed record StateChange(long Seq, string Key, JsonElement Value, bool Deleted = false);
public sealed record StatePage(StateChange[] Changes, long Cursor, bool HasMore);
public sealed record StateReceipt(string Key, long Seq);

public interface ISyncStateRemote
{
    Task<StatePage> StateChangesAsync(long cursor, CancellationToken cancellationToken);
    Task<StateReceipt> PushStateAsync(StateOperation operation, CancellationToken cancellationToken);
}

/// <summary>An account/vault-scoped durable outbox. The caller supplies a different file for every account.</summary>
public sealed class AccountStateSync(string stateFile, ISyncStateRemote remote)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = Path.GetFullPath(stateFile);
    private sealed record Journal(int Version, long Cursor, Dictionary<string, SyncStateEntry> Values, List<StateOperation> Pending);

    public async Task EnqueueAsync(IEnumerable<SyncStateEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var incoming = entries.Select(Clone).ToArray();
        foreach (var entry in incoming) Validate(entry);
        var gate = Gates.GetOrAdd(_path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var storageLock = await Lock(cancellationToken).ConfigureAwait(false);
            var journal = await Load(cancellationToken).ConfigureAwait(false);
            var pending = new List<StateOperation>(journal.Pending);
            foreach (var entry in incoming)
            {
                var previous = pending.LastOrDefault(operation => operation.Key == entry.Key);
                var value = previous is null ? journal.Values.GetValueOrDefault(entry.Key) : new SyncStateEntry(previous.Key, previous.Value, previous.Deleted);
                if (value is not null && Same(value, entry)) continue;
                // Never discard an operation whose reply may have been lost. A newer preference follows it.
                pending.Add(new(Guid.NewGuid().ToString("D"), entry.Key, entry.Value, entry.Deleted));
            }
            if (pending.Count != journal.Pending.Count)
                await Save(journal with { Pending = pending }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task EnqueueAsync(SyncStateEntry entry, CancellationToken cancellationToken = default) =>
        EnqueueAsync([entry], cancellationToken);

    public async Task<IReadOnlyList<SyncStateEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(_path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var storageLock = await Lock(cancellationToken).ConfigureAwait(false);
            var journal = await Load(cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, SyncStateEntry>(journal.Values, StringComparer.Ordinal);
            foreach (var operation in journal.Pending) values[operation.Key] = new(operation.Key, operation.Value, operation.Deleted);
            return values.Values.Select(Clone).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<SyncStateEntry>> RunAsync(CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(_path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var storageLock = await Lock(cancellationToken).ConfigureAwait(false);
            var journal = await Load(cancellationToken).ConfigureAwait(false);
            // The complete immutable batch is already on disk before the first network write.
            foreach (var operation in journal.Pending)
            {
                var receipt = await remote.PushStateAsync(operation, cancellationToken).ConfigureAwait(false);
                if (receipt.Key != operation.Key || receipt.Seq <= 0)
                    throw new InvalidDataException("账号状态写入回执无效。");
            }
            var values = new Dictionary<string, SyncStateEntry>(journal.Values, StringComparer.Ordinal);
            var cursor = journal.Cursor;
            while (true)
            {
                var page = await remote.StateChangesAsync(cursor, cancellationToken).ConfigureAwait(false);
                if (page.Changes is null || page.Cursor < cursor || page.HasMore && page.Cursor <= cursor)
                    throw new InvalidDataException("账号状态游标无效。");
                var sequence = cursor;
                foreach (var change in page.Changes)
                {
                    if (change.Seq <= sequence || change.Seq > page.Cursor)
                        throw new InvalidDataException("账号状态顺序无效。");
                    var entry = Clone(new(change.Key, change.Value, change.Deleted));
                    Validate(entry);
                    values[entry.Key] = entry;
                    sequence = change.Seq;
                }
                if (sequence != page.Cursor) throw new InvalidDataException("账号状态变更页不完整。");
                cursor = page.Cursor;
                if (!page.HasMore) break;
            }
            // Commit cursor and acknowledgements together. A failed pull/save replays the same IDs safely.
            await Save(new(1, cursor, values, []), cancellationToken).ConfigureAwait(false);
            return values.Values.Select(Clone).ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<Journal> Load(CancellationToken cancellationToken)
    {
        LocalLibraryState.EnsureNoLinks(_path);
        if (!File.Exists(_path)) return new(1, 0, new(StringComparer.Ordinal), []);
        try
        {
            var journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false));
            if (journal is null || journal.Version != 1 || journal.Cursor < 0 || journal.Values is null || journal.Pending is null)
                throw new JsonException("账号状态记录不完整。");
            foreach (var pair in journal.Values)
            {
                if (pair.Value is null || pair.Key != pair.Value.Key) throw new JsonException("账号状态键无效。");
                Validate(pair.Value);
            }
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in journal.Pending)
            {
                if (operation is null || !Guid.TryParse(operation.OperationId, out _) || !operationIds.Add(operation.OperationId))
                    throw new JsonException("账号状态操作标识无效。");
                Validate(new(operation.Key, operation.Value, operation.Deleted));
            }
            return journal;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new IOException("无法读取账号同步记录，原记录已保留：" + _path, error); }
    }

    private Task Save(Journal journal, CancellationToken cancellationToken) => LocalLibraryState.WriteAsync(_path, journal, cancellationToken);
    private Task<FileStream> Lock(CancellationToken cancellationToken)
    {
        var lockPath = _path + ".lock";
        LocalLibraryState.EnsureNoLinks(lockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        LocalLibraryState.EnsureNoLinks(lockPath);
        return VaultStore.AcquireLockAsync(lockPath, cancellationToken);
    }
    private static SyncStateEntry Clone(SyncStateEntry entry) => entry with
    { Value = entry.Value.ValueKind == JsonValueKind.Undefined ? default : entry.Value.Clone() };
    private static bool Same(SyncStateEntry a, SyncStateEntry b) => a.Deleted == b.Deleted && JsonElement.DeepEquals(a.Value, b.Value);

    public static void Validate(SyncStateEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 512 || entry.Key.Any(char.IsControl))
            throw new InvalidDataException("账号状态键无效。");
        var parts = entry.Key.Split('/');
        var eventKey = parts is ["event", var exposure, "shown" or "completed"] && Guid.TryParse(exposure, out _);
        var legacyKey = parts is ["legacy", var source, var note, var date] && !string.IsNullOrWhiteSpace(source) &&
            !string.IsNullOrWhiteSpace(note) && DateOnly.TryParseExact(date, "yyyy-MM-dd", out _);
        var readingKey = parts is ["reading", var hash] && hash.Length == 64 && hash.All(Uri.IsHexDigit);
        var noteReadingKey = parts is ["note-reading", var readingNote] && !string.IsNullOrWhiteSpace(readingNote);
        if (!eventKey && !legacyKey && !readingKey && !noteReadingKey && entry.Key is not ("roaming/current" or "preference/theme"))
            throw new InvalidDataException("账号状态类型无效。");
        if (entry.Deleted)
        {
            if (eventKey || legacyKey) throw new InvalidDataException("学习历史不能删除。");
            return;
        }
        var value = entry.Value;
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 64 * 1024)
            throw new InvalidDataException("账号状态内容无效。");
        try
        {
            if (entry.Key == "preference/theme")
            {
                if (value.GetProperty("name").GetString() is not ("pink" or "mono" or "warmNeutral")) throw new FormatException();
                return;
            }
            if (readingKey || noteReadingKey)
            {
                if (readingKey && (!Uri.TryCreate(value.GetProperty("url").GetString(), UriKind.Absolute, out var url) ||
                    url.Scheme is not ("https" or "http") || url.UserInfo.Length != 0)) throw new FormatException();
                if (noteReadingKey && value.GetProperty("noteId").GetString() != parts[1]) throw new FormatException();
                var progress = value.GetProperty("progress").GetDouble();
                if (!double.IsFinite(progress) || progress < 0 || progress > 1) throw new FormatException();
                if (value.TryGetProperty("anchor", out var anchor) && anchor.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new FormatException();
                return;
            }
            if (string.IsNullOrWhiteSpace(value.GetProperty("noteId").GetString())) throw new FormatException();
            if (legacyKey)
            {
                var day = value.GetProperty("day").GetString();
                if (day != parts[3] || !DateOnly.TryParseExact(day, "yyyy-MM-dd", out _)) throw new FormatException();
                foreach (var name in new[] { "remembered", "skipped", "shown" }) if (value.GetProperty(name).GetInt32() < 0) throw new FormatException();
                if (value.TryGetProperty("lastShown", out var last) && last.ValueKind != JsonValueKind.Null && !last.TryGetDateTimeOffset(out _)) throw new FormatException();
                return;
            }
            if (!value.GetProperty("at").TryGetDateTimeOffset(out _)) throw new FormatException();
            if (eventKey && !DateOnly.TryParseExact(value.GetProperty("day").GetString(), "yyyy-MM-dd", out _)) throw new FormatException();
            if (parts[^1] == "completed")
            {
                if (value.GetProperty("remembered").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException();
                return;
            }
            if (entry.Key == "roaming/current" && !Guid.TryParse(value.GetProperty("exposureId").GetString(), out _)) throw new FormatException();
            var weight = value.GetProperty("weight").GetDouble();
            var level = value.GetProperty("weightLevel").GetDouble();
            if (!double.IsFinite(weight) || weight <= 0 || !double.IsFinite(level) || level < 0 || level > 1) throw new FormatException();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("账号状态内容无效。", error); }
    }
}
