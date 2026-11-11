using System.Text.Json;
using VocabCollector.Core.Sync;

namespace VocabCollector.Core.Library;

public sealed record ReviewRecord(DateTimeOffset? LastShown = null, DateOnly? Day = null, int Remembered = 0, int Skipped = 0)
{
    public (int Remembered, int Skipped) Counts(DateTimeOffset now, TimeZoneInfo? timeZone = null) =>
        Day == DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local).DateTime) ? (Remembered, Skipped) : (0, 0);

    public double Weight(DateTimeOffset added, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        static double Days(DateTimeOffset earlier, DateTimeOffset later) => Math.Max(0, (later - earlier).TotalSeconds / 86400);
        var (remembered, skipped) = Counts(now, timeZone);
        var ratio = (remembered + 1.0) / (remembered + (double)skipped + 2);
        return 0.5 + 3 * double.LogP1(Days(LastShown ?? added, now)) + Math.Exp(-Days(added, now) / 3) +
            2 * (1 - ratio) + Math.Min(skipped, 3) / 3.0;
    }
}

public sealed record RoamingExposure(Guid Id, LibraryNote Note, bool RememberedToday, double Weight, double WeightLevel = 0.5);

public sealed class RoamingSession
{
    // Local replay protection only. Keep a bounded window without extending the
    // cloud's existing shown/completed(bool) schema with a third grade.
    private const int NeutralHistoryLimit = 4096;
    private readonly string _stateFile;
    private readonly TimeProvider _clock;
    private readonly Func<double> _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReviewState _state;
    public RoamingExposure? Current { get; private set; }
    /// <summary>Raised only after a local learning action is durably committed. Sync replay does not raise it.</summary>
    public event EventHandler? StateChanged;

    public RoamingStatistics GetStatistics(IReadOnlyList<LibraryNote> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        // Published states are never mutated; a single snapshot is safe during an awaited save.
        var state = _state;
        var today = LocalDate(_clock.GetUtcNow(), _clock.LocalTimeZone);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var gridStart = DateOnly.FromDayNumber(Math.Max(0, weekStart.DayNumber - 77));
        var words = notes.Where(note => note.IsWord).Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
        var activity = state.Activity ?? [];
        int CoveredSince(DateOnly start) => activity.Where(day => day.Key >= start && day.Key <= today)
            .SelectMany(day => day.Value.Keys).Where(words.Contains).Distinct(StringComparer.Ordinal).Count();
        var days = Enumerable.Range(0, 84).Select(index =>
        {
            var day = gridStart.AddDays(index);
            var future = day > today;
            var counts = !future ? activity.GetValueOrDefault(day) : null;
            return new RoamingDayCount(day, counts?.Values.Sum() ?? 0, future, counts?.Count ?? 0);
        }).ToArray();
        return new(CoveredSince(weekStart), CoveredSince(monthStart), words.Count, today, weekStart, monthStart,
            gridStart, days, state.HasLegacyHistory, state.TrackingStartedOn);
    }

    public RoamingSession(string stateFile, TimeProvider? clock = null, Func<double>? random = null)
    {
        _stateFile = Path.GetFullPath(stateFile);
        _clock = clock ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
        LocalLibraryState.EnsureNoLinks(_stateFile);
        try
        {
            _state = File.Exists(_stateFile) ? JsonSerializer.Deserialize<ReviewState>(File.ReadAllText(_stateFile))
                ?? throw new JsonException("学习记录为空。") : new([], null, null);
            if (_state.Records is null || _state.Records.Any(pair => pair.Value is null || pair.Value.Remembered < 0 || pair.Value.Skipped < 0) ||
                _state.Records.Keys.Any(string.IsNullOrWhiteSpace) ||
                (_state.CurrentExposure is null) != (_state.CurrentNoteId is null) ||
                (_state.CurrentNoteId is not null && string.IsNullOrWhiteSpace(_state.CurrentNoteId)) ||
                (_state.CurrentWeight is { } weight && (!double.IsFinite(weight) || weight <= 0)) ||
                (_state.CurrentWeightLevel is { } level && (!double.IsFinite(level) || level < 0 || level > 1)) ||
                (_state.NeutralCompletions is { } neutral && (neutral.Count > NeutralHistoryLimit ||
                    neutral.Contains(Guid.Empty) || neutral.Distinct().Count() != neutral.Count)))
                throw new JsonException("学习记录格式无效。");
            if (_state.Activity is not null)
                foreach (var day in _state.Activity)
                    if (day.Value is null || day.Value.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) ||
                        day.Value.Values.Sum(count => (long)count) > int.MaxValue)
                        throw new JsonException("漫游活动记录计数无效。");
            if (_state.Activity is null && (_state.Records.Count > 0 || _state.CurrentExposure is not null))
                _state = _state with { HasLegacyHistory = true };
            if (_state.SyncEntries is not null)
                foreach (var entry in _state.SyncEntries) AccountStateSync.Validate(new(entry.Key, entry.Value));
            if (_state.PendingBaseline is not null && _state.PendingBaseline.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.NoteId) || item.Remembered < 0 || item.Skipped < 0 || item.Shown < 0))
                throw new JsonException("学习迁移记录格式无效。");
            _state = PrepareSync(_state);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new IOException("无法读取本机学习记录，请检查文件：" + _stateFile, error); }
    }

    public Task<RoamingExposure?> NextAsync(IReadOnlyList<LibraryNote> notes, Guid? finishingExposure = null,
        bool remembered = false, CancellationToken cancellationToken = default) =>
        NextCoreAsync(notes, finishingExposure, remembered, avoidRepeat: false, cancellationToken);

    /// <summary>Finish a practice card. A null grade is neutral and is never exported as a completed(bool) event.</summary>
    public Task<RoamingExposure?> NextPracticeAsync(IReadOnlyList<LibraryNote> notes, Guid finishingExposure,
        bool? remembered, CancellationToken cancellationToken = default) =>
        NextCoreAsync(notes, finishingExposure, remembered, avoidRepeat: true, cancellationToken);

    private async Task<RoamingExposure?> NextCoreAsync(IReadOnlyList<LibraryNote> notes, Guid? finishingExposure,
        bool? remembered, bool avoidRepeat, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            var timeZone = _clock.LocalTimeZone;
            var today = LocalDate(now, timeZone);
            var existing = notes.FirstOrDefault(note => note.Id == _state.CurrentNoteId && note.IsWord);
            if (finishingExposure is not null && finishingExposure != _state.CurrentExposure)
                return Current ?? RestoreExposure(existing, now, timeZone);
            if (finishingExposure is null && existing is not null)
            {
                Current = Current is null ? RestoreExposure(existing, now, timeZone) : Current with
                { Note = existing, RememberedToday = Record(existing.Id).Counts(now, timeZone).Remembered > 0 };
                return Current;
            }

            var records = new Dictionary<string, ReviewRecord>(_state.Records);
            var syncEntries = new Dictionary<string, JsonElement>(_state.SyncEntries!, StringComparer.Ordinal);
            var neutralCompletions = new List<Guid>(_state.NeutralCompletions ?? []);
            if (finishingExposure is not null && finishingExposure == _state.CurrentExposure && _state.CurrentNoteId is { } completedId)
            {
                if (remembered is { } grade)
                {
                    var record = Record(completedId);
                    var (m, s) = record.Counts(now, timeZone);
                    records[completedId] = record with
                    {
                        Day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime),
                        Remembered = checked(m + (grade ? 1 : 0)),
                        Skipped = checked(s + (grade ? 0 : 1))
                    };
                    syncEntries[EventKey(finishingExposure.Value, "completed")] = JsonSerializer.SerializeToElement(new
                    { noteId = completedId, at = now, day = today.ToString("yyyy-MM-dd"), remembered = grade });
                }
                else
                {
                    neutralCompletions.Add(finishingExposure.Value);
                    if (neutralCompletions.Count > NeutralHistoryLimit) neutralCompletions.RemoveAt(0);
                }
            }

            var eligible = notes.Where(note => note.IsWord).ToArray();
            if (avoidRepeat && eligible.Any(note => note.Id != _state.CurrentNoteId))
                eligible = eligible.Where(note => note.Id != _state.CurrentNoteId).ToArray();
            var choices = eligible.Select(note =>
                (Note: note, Weight: records.GetValueOrDefault(note.Id, new ReviewRecord()).Weight(note.AddedAt, now, timeZone))).ToArray();
            var oldest = DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - 399));
            var activity = (_state.Activity ?? []).Where(day => day.Key >= oldest && day.Key <= today)
                .ToDictionary(day => day.Key, day => new Dictionary<string, int>(day.Value, StringComparer.Ordinal));
            var trackingStartedOn = _state.TrackingStartedOn;
            RoamingExposure? next = null;
            if (choices.Length > 0)
            {
                var unit = _random();
                var needle = (double.IsNaN(unit) ? 0 : Math.Clamp(unit, 0, 1)) * choices.Sum(choice => choice.Weight);
                var selected = choices[^1];
                foreach (var choice in choices)
                {
                    needle -= choice.Weight;
                    if (needle < 0) { selected = choice; break; }
                }
                var record = records.GetValueOrDefault(selected.Note.Id, new ReviewRecord());
                var weightLevel = Math.Clamp(selected.Weight / choices.Max(choice => choice.Weight), 0, 1);
                next = new(Guid.NewGuid(), selected.Note, record.Counts(now, timeZone).Remembered > 0, selected.Weight, weightLevel);
                records[selected.Note.Id] = record with { LastShown = now };
                if (!activity.TryGetValue(today, out var counts)) activity[today] = counts = new(StringComparer.Ordinal);
                if (counts.Values.Sum(count => (long)count) >= int.MaxValue)
                    throw new IOException("当天漫游计数已达到上限，当前卡片尚未推进。");
                counts[selected.Note.Id] = checked(counts.GetValueOrDefault(selected.Note.Id) + 1);
                trackingStartedOn ??= today;
                syncEntries[EventKey(next.Id, "shown")] = JsonSerializer.SerializeToElement(new
                { noteId = selected.Note.Id, at = now, day = today.ToString("yyyy-MM-dd"), weight = selected.Weight, weightLevel });
                syncEntries["roaming/current"] = JsonSerializer.SerializeToElement(new
                { noteId = selected.Note.Id, exposureId = next.Id.ToString("D"), at = now, weight = selected.Weight, weightLevel });
            }

            if (avoidRepeat && next is null) syncEntries.Remove("roaming/current");

            if (next is null && _state.CurrentExposure is null && finishingExposure is null) return null;
            var candidate = _state with { Records = records, CurrentExposure = next?.Id, CurrentNoteId = next?.Note.Id,
                Activity = activity, TrackingStartedOn = trackingStartedOn, CurrentWeight = next?.Weight,
                CurrentWeightLevel = next?.WeightLevel, CurrentAt = next is null ? null : now, SyncEntries = syncEntries,
                NeutralCompletions = neutralCompletions };
            try { await LocalLibraryState.WriteAsync(_stateFile, candidate, cancellationToken); }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error))
            { throw new IOException("无法保存本机学习记录，当前卡片尚未推进。" + error.Message, error); }
            _state = candidate;
            Current = next;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return next;
        }
        finally { _gate.Release(); }
    }

    private ReviewRecord Record(string id) => _state.Records.GetValueOrDefault(id, new ReviewRecord());

    /// <summary>Resolve the persisted current card after sync without drawing or recording an exposure.</summary>
    public async Task<RoamingExposure?> RefreshCurrentAsync(IReadOnlyList<LibraryNote> notes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notes);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            Current = RestoreExposure(notes.FirstOrDefault(note => note.Id == _state.CurrentNoteId && note.IsWord), now, _clock.LocalTimeZone);
            return Current;
        }
        finally { _gate.Release(); }
    }

    public Task<RoamingExposure?> RestoreCurrentAsync(IReadOnlyList<LibraryNote> notes, CancellationToken cancellationToken = default) =>
        RefreshCurrentAsync(notes, cancellationToken);

    private RoamingExposure? RestoreExposure(LibraryNote? note, DateTimeOffset now, TimeZoneInfo timeZone) =>
        note is null || _state.CurrentExposure is not { } exposure ? null : new(exposure, note,
            Record(note.Id).Counts(now, timeZone).Remembered > 0,
            _state.CurrentWeight ?? Record(note.Id).Weight(note.AddedAt, now, timeZone), _state.CurrentWeightLevel ?? 0.5);

    private static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);

    /// <summary>Export immutable events using cloud note IDs. Unmapped notes wait locally until their Markdown is synced.</summary>
    public async Task<IReadOnlyList<SyncStateEntry>> ExportSyncAsync(string deviceId, Func<string, string?> localToCloud,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (deviceId.Contains('/') || deviceId.Any(char.IsControl)) throw new ArgumentException("设备标识无效。", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(localToCloud);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = _state.SourceDeviceId ?? deviceId;
            var entries = new Dictionary<string, JsonElement>(_state.SyncEntries!, StringComparer.Ordinal);
            var remaining = new List<LegacyBaseline>();
            foreach (var baseline in _state.PendingBaseline!)
            {
                var cloudId = localToCloud(baseline.NoteId);
                if (string.IsNullOrWhiteSpace(cloudId)) { remaining.Add(baseline); continue; }
                if (cloudId.Contains('/')) throw new InvalidDataException("云端笔记标识无效。");
                var key = $"legacy/{source}/{cloudId}/{baseline.Day:yyyy-MM-dd}";
                entries.TryAdd(key, JsonSerializer.SerializeToElement(new
                {
                    noteId = baseline.NoteId, day = baseline.Day.ToString("yyyy-MM-dd"), remembered = baseline.Remembered,
                    skipped = baseline.Skipped, shown = baseline.Shown, lastShown = baseline.LastShown
                }));
            }
            var candidate = _state with { SourceDeviceId = source, SyncEntries = entries, PendingBaseline = remaining };
            // Freeze the baseline before enqueueing anything. Restarts must never export merged aggregates again.
            await LocalLibraryState.WriteAsync(_stateFile, candidate, cancellationToken);
            _state = candidate;
            var result = new List<SyncStateEntry>();
            foreach (var (key, value) in entries)
            {
                var localId = value.GetProperty("noteId").GetString()!;
                var cloudId = localToCloud(localId);
                if (string.IsNullOrWhiteSpace(cloudId)) continue;
                var converted = ReplaceNoteId(value, cloudId);
                var entry = new SyncStateEntry(key, converted);
                AccountStateSync.Validate(entry);
                result.Add(entry);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Merge by exposure/legacy identity under the same gate as NextAsync; never replay a display as another visit.</summary>
    public async Task MergeSyncAsync(IEnumerable<SyncStateEntry> incoming, Func<string, string?> cloudToLocal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(cloudToLocal);
        var changes = incoming.Where(entry => entry.Key.StartsWith("event/", StringComparison.Ordinal) ||
            entry.Key.StartsWith("legacy/", StringComparison.Ordinal) || entry.Key == "roaming/current").ToArray();
        foreach (var entry in changes) AccountStateSync.Validate(entry);
        if (changes.Length == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = new Dictionary<string, JsonElement>(_state.SyncEntries!, StringComparer.Ordinal);
            var neutralCompletions = (_state.NeutralCompletions ?? []).ToHashSet();
            foreach (var change in changes)
            {
                if (change.Deleted) continue;
                var cloudId = change.Value.GetProperty("noteId").GetString()!;
                var localId = cloudToLocal(cloudId);
                if (string.IsNullOrWhiteSpace(localId)) continue;
                var key = change.Key;
                if (key.StartsWith("event/", StringComparison.Ordinal))
                {
                    var parts = key.Split('/');
                    var exposure = Guid.Parse(parts[1]);
                    if (parts[2] == "completed" && neutralCompletions.Contains(exposure)) continue;
                    key = EventKey(exposure, parts[2]);
                }
                if (key == "roaming/current" && neutralCompletions.Contains(Guid.Parse(change.Value.GetProperty("exposureId").GetString()!)))
                    continue;
                // A newer locally drawn card can be committed while the network request is in flight.
                if (key == "roaming/current" && entries.TryGetValue(key, out var localCurrent) &&
                    localCurrent.GetProperty("at").GetDateTimeOffset() > change.Value.GetProperty("at").GetDateTimeOffset()) continue;
                entries[key] = ReplaceNoteId(change.Value, localId);
            }
            var now = _clock.GetUtcNow();
            var candidate = Rebuild(_state with { SyncEntries = entries }, now);
            await LocalLibraryState.WriteAsync(_stateFile, candidate, cancellationToken);
            _state = candidate;
            Current = null; // NextAsync restores the existing ID/weight against the latest catalog snapshot.
        }
        catch (OverflowException error) { throw new IOException("合并后的学习计数超过上限，原记录已保留。", error); }
        finally { _gate.Release(); }
    }

    private ReviewState PrepareSync(ReviewState state)
    {
        if (state.SyncEntries is not null && state.PendingBaseline is not null) return state;
        var baseline = new Dictionary<(string Id, DateOnly Day), LegacyBaseline>();
        foreach (var (day, counts) in state.Activity ?? [])
            foreach (var (id, count) in counts) baseline[(id, day)] = new(id, day, 0, 0, count, null);
        foreach (var (id, record) in state.Records)
        {
            if (record.Day is { } day)
            {
                var old = baseline.GetValueOrDefault((id, day)) ?? new(id, day, 0, 0, 0, null);
                baseline[(id, day)] = old with { Remembered = record.Remembered, Skipped = record.Skipped };
            }
            if (record.LastShown is { } lastShown)
            {
                var shownDay = LocalDate(lastShown, _clock.LocalTimeZone);
                var old = baseline.GetValueOrDefault((id, shownDay)) ?? new(id, shownDay, 0, 0, 0, null);
                baseline[(id, shownDay)] = old with { LastShown = lastShown };
            }
        }
        var entries = new Dictionary<string, JsonElement>(state.SyncEntries ?? [], StringComparer.Ordinal);
        var at = state.CurrentAt;
        if (state.CurrentExposure is { } exposure && state.CurrentNoteId is { } noteId)
        {
            at ??= state.Records.GetValueOrDefault(noteId)?.LastShown ?? _clock.GetUtcNow();
            entries["roaming/current"] = JsonSerializer.SerializeToElement(new
            { noteId, exposureId = exposure.ToString("D"), at, weight = state.CurrentWeight ?? 2.5, weightLevel = state.CurrentWeightLevel ?? 0.5 });
        }
        return state with { SyncEntries = entries, PendingBaseline = baseline.Values.ToList(), CurrentAt = at };
    }

    private ReviewState Rebuild(ReviewState state, DateTimeOffset now)
    {
        var neutralCompletions = (state.NeutralCompletions ?? []).ToHashSet();
        var totals = new Dictionary<(string Id, DateOnly Day), (int Remembered, int Skipped, int Shown)>();
        var lastShown = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        void Add(string id, DateOnly day, int remembered, int skipped, int shown, DateTimeOffset? at)
        {
            var old = totals.GetValueOrDefault((id, day));
            totals[(id, day)] = (checked(old.Remembered + remembered), checked(old.Skipped + skipped), checked(old.Shown + shown));
            if (at is { } timestamp && (!lastShown.TryGetValue(id, out var previous) || timestamp > previous)) lastShown[id] = timestamp;
        }
        foreach (var baseline in state.PendingBaseline!) Add(baseline.NoteId, baseline.Day, baseline.Remembered, baseline.Skipped, baseline.Shown, baseline.LastShown);
        foreach (var (key, value) in state.SyncEntries!)
        {
            if (key == "roaming/current") continue;
            var id = value.GetProperty("noteId").GetString()!;
            var day = DateOnly.ParseExact(value.GetProperty("day").GetString()!, "yyyy-MM-dd");
            if (key.StartsWith("legacy/", StringComparison.Ordinal))
                Add(id, day, value.GetProperty("remembered").GetInt32(), value.GetProperty("skipped").GetInt32(), value.GetProperty("shown").GetInt32(),
                    value.TryGetProperty("lastShown", out var last) && last.ValueKind != JsonValueKind.Null ? last.GetDateTimeOffset() : null);
            else if (key.EndsWith("/shown", StringComparison.Ordinal)) Add(id, day, 0, 0, 1, value.GetProperty("at").GetDateTimeOffset());
            else if (key.EndsWith("/completed", StringComparison.Ordinal))
            {
                if (neutralCompletions.Contains(Guid.Parse(key.Split('/')[1]))) continue;
                var remembered = value.GetProperty("remembered").GetBoolean();
                Add(id, day, remembered ? 1 : 0, remembered ? 0 : 1, 0, null);
            }
        }
        var records = new Dictionary<string, ReviewRecord>(StringComparer.Ordinal);
        foreach (var group in totals.GroupBy(pair => pair.Key.Id))
        {
            // Days containing only appearances must not erase remembered/skipped counts from the same day.
            var latest = group.Max(pair => pair.Key.Day);
            var counts = totals[(group.Key, latest)];
            records[group.Key] = new(lastShown.TryGetValue(group.Key, out var shownAt) ? shownAt : null, latest, counts.Remembered, counts.Skipped);
        }
        var today = LocalDate(now, _clock.LocalTimeZone);
        var oldest = DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - 399));
        var activity = totals.Where(pair => pair.Key.Day >= oldest && pair.Key.Day <= today && pair.Value.Shown > 0)
            .GroupBy(pair => pair.Key.Day).ToDictionary(group => group.Key,
                group => group.ToDictionary(pair => pair.Key.Id, pair => pair.Value.Shown, StringComparer.Ordinal));
        foreach (var counts in activity.Values)
            if (counts.Values.Sum(count => (long)count) > int.MaxValue) throw new OverflowException();
        Guid? currentExposure = state.CurrentExposure;
        string? currentNote = state.CurrentNoteId;
        var currentAt = state.CurrentAt;
        var weight = state.CurrentWeight;
        var level = state.CurrentWeightLevel;
        if (state.SyncEntries.TryGetValue("roaming/current", out var current))
        {
            currentExposure = Guid.Parse(current.GetProperty("exposureId").GetString()!);
            currentNote = current.GetProperty("noteId").GetString();
            currentAt = current.GetProperty("at").GetDateTimeOffset();
            weight = current.GetProperty("weight").GetDouble();
            level = current.GetProperty("weightLevel").GetDouble();
            if (state.SyncEntries.ContainsKey(EventKey(currentExposure.Value, "completed")) || neutralCompletions.Contains(currentExposure.Value))
            { currentExposure = null; currentNote = null; currentAt = null; weight = null; level = null; }
        }
        var firstActivity = totals.Where(pair => pair.Value.Shown > 0).Select(pair => (DateOnly?)pair.Key.Day).Min();
        var tracking = state.TrackingStartedOn;
        if (firstActivity is { } first && (tracking is null || first < tracking)) tracking = first;
        return state with { Records = records, Activity = activity, CurrentExposure = currentExposure, CurrentNoteId = currentNote,
            CurrentAt = currentAt, CurrentWeight = weight, CurrentWeightLevel = level, TrackingStartedOn = tracking };
    }

    private static string EventKey(Guid exposureId, string kind) => $"event/{exposureId:D}/{kind}";
    private static JsonElement ReplaceNoteId(JsonElement value, string noteId)
    {
        var fields = value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        fields["noteId"] = JsonSerializer.SerializeToElement(noteId);
        return JsonSerializer.SerializeToElement(fields);
    }

    private sealed record LegacyBaseline(string NoteId, DateOnly Day, int Remembered, int Skipped, int Shown, DateTimeOffset? LastShown);

    private sealed record ReviewState(Dictionary<string, ReviewRecord> Records, Guid? CurrentExposure, string? CurrentNoteId,
        Dictionary<DateOnly, Dictionary<string, int>>? Activity = null, bool HasLegacyHistory = false,
        DateOnly? TrackingStartedOn = null, double? CurrentWeight = null, double? CurrentWeightLevel = null,
        Dictionary<string, JsonElement>? SyncEntries = null, List<LegacyBaseline>? PendingBaseline = null,
        string? SourceDeviceId = null, DateTimeOffset? CurrentAt = null, List<Guid>? NeutralCompletions = null);
}
