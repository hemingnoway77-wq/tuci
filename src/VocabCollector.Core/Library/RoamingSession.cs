using System.Text.Json;

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

public sealed record RoamingExposure(Guid Id, LibraryNote Note, bool RememberedToday, double Weight);

public sealed class RoamingSession
{
    private readonly string _stateFile;
    private readonly TimeProvider _clock;
    private readonly Func<double> _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReviewState _state;
    public RoamingExposure? Current { get; private set; }

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
                (_state.CurrentExposure is null) != (_state.CurrentNoteId is null))
                throw new JsonException("学习记录格式无效。");
        }
        catch (JsonException error) { throw new IOException("无法读取本机学习记录，请检查文件：" + _stateFile, error); }
    }

    public async Task<RoamingExposure?> NextAsync(IReadOnlyList<LibraryNote> notes, Guid? finishingExposure = null, bool remembered = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            var timeZone = _clock.LocalTimeZone;
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
            if (finishingExposure is not null && finishingExposure == _state.CurrentExposure && _state.CurrentNoteId is { } completedId)
            {
                var record = Record(completedId);
                var (m, s) = record.Counts(now, timeZone);
                records[completedId] = record with
                {
                    Day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime),
                    Remembered = checked(m + (remembered ? 1 : 0)),
                    Skipped = checked(s + (remembered ? 0 : 1))
                };
            }

            var choices = notes.Where(note => note.IsWord).Select(note =>
                (Note: note, Weight: records.GetValueOrDefault(note.Id, new ReviewRecord()).Weight(note.AddedAt, now, timeZone))).ToArray();
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
                next = new(Guid.NewGuid(), selected.Note, record.Counts(now, timeZone).Remembered > 0, selected.Weight);
                records[selected.Note.Id] = record with { LastShown = now };
            }

            if (next is null && _state.CurrentExposure is null && finishingExposure is null) return null;
            var candidate = new ReviewState(records, next?.Id, next?.Note.Id);
            try { await LocalLibraryState.WriteAsync(_stateFile, candidate, cancellationToken); }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error))
            { throw new IOException("无法保存本机学习记录，当前卡片尚未推进。" + error.Message, error); }
            _state = candidate;
            Current = next;
            return next;
        }
        finally { _gate.Release(); }
    }

    private ReviewRecord Record(string id) => _state.Records.GetValueOrDefault(id, new ReviewRecord());

    private RoamingExposure? RestoreExposure(LibraryNote? note, DateTimeOffset now, TimeZoneInfo timeZone) =>
        note is null || _state.CurrentExposure is not { } exposure ? null : new(exposure, note,
            Record(note.Id).Counts(now, timeZone).Remembered > 0, Record(note.Id).Weight(note.AddedAt, now, timeZone));

    private sealed record ReviewState(Dictionary<string, ReviewRecord> Records, Guid? CurrentExposure, string? CurrentNoteId);
}
