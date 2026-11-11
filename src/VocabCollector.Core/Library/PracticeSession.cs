using System.Security.Cryptography;
using System.Text.Json;

namespace VocabCollector.Core.Library;

public sealed class PracticeSession
{
    private readonly string _stateFile;
    private readonly Func<double> _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PracticeState _state;
    private byte[]? _savedHash;

    public PracticeCompletion? GetCompletion(Guid exposureId)
    {
        // A state is published only after the atomic write; its dictionaries
        // are never mutated after publication, including during an awaited save.
        var state = _state;
        return state.Attempts.TryGetValue(exposureId, out var attempt) && attempt.Outcome is { } outcome
            ? new(outcome, attempt.Answer ?? "") : null;
    }

    public PracticeSession(string stateFile, Func<double>? random = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        _stateFile = Path.GetFullPath(stateFile); _random = random ?? Random.Shared.NextDouble;
        LocalLibraryState.EnsureNoLinks(_stateFile);
        try
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(_stateFile); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { _state = new(1, [], []); return; }
            _state = JsonSerializer.Deserialize<PracticeState>(bytes) ?? throw new JsonException();
            Validate(_state);
            _savedHash = SHA256.HashData(bytes);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new IOException("练习记录格式无效，原文件已保留。", error); }
    }

    public async Task<PracticeQuestion?> PrepareAsync(LibraryNote note, Guid exposureId, string? dictionaryLemma = null,
        CancellationToken cancellationToken = default, IReadOnlyList<PracticeContext>? contexts = null)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (exposureId == Guid.Empty || string.IsNullOrWhiteSpace(note.Id)) throw new ArgumentException("练习记录编号无效。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state.Attempts.TryGetValue(exposureId, out var previous);
            if (previous is not null && previous.Question.NoteId != note.Id) throw new ArgumentException("曝光编号属于另一篇笔记。");
            // A completed exposure is a receipt, not another exercise. Keep its
            // question and answer together while the roaming transaction retries.
            if (previous?.Outcome is not null) return previous.Question;
            var senses = PracticeQuestionBank.Build(note, dictionaryLemma);
            if (senses.Count == 0) return null;
            if (contexts is not null) senses = PracticeQuestionBank.WithContexts(senses, contexts);
            // Never turn an unbound definition into a vocabulary quiz. A bank
            // is usable only when every current sense has a verified context.
            if (senses.Any(sense => !PracticeQuestionBank.HasContext(sense))) return null;
            if (previous is not null)
            {
                var currentSense = senses.FirstOrDefault(sense => sense.Id == previous.Question.SenseId);
                if (currentSense is not null)
                {
                    var current = PracticeQuestionBank.Create(currentSense, note.Id, exposureId, previous.Question.Kind);
                    if (current == previous.Question) return current;
                    var refreshed = new Dictionary<Guid, Attempt>(_state.Attempts) { [exposureId] = previous with { Question = current } };
                    await SaveAsync(_state with { Attempts = refreshed }, cancellationToken);
                    return current;
                }
            }
            var candidates = senses.SelectMany(sense => Enum.GetValues<PracticeKind>().Select(kind =>
            {
                var key = Key(note.Id, sense.Id, kind);
                return (Sense: sense, Kind: kind, Key: key, Skill: _state.Skills.GetValueOrDefault(key, new Skill()));
            })).ToArray();
            var least = candidates.Min(item => item.Skill.Presented);
            var practiced = candidates.Where(item => item.Skill.Presented == least).ToArray();
            var weakest = practiced.Min(item => item.Skill.Completed == 0 ? 0 : item.Skill.Points / item.Skill.Completed);
            var choices = practiced.Where(item => (item.Skill.Completed == 0 ? 0 : item.Skill.Points / item.Skill.Completed) == weakest).ToArray();
            var unit = _random(); if (!double.IsFinite(unit)) unit = 0;
            var chosen = choices[(int)(Math.Clamp(unit, 0, Math.BitDecrement(1.0)) * choices.Length)];
            var question = PracticeQuestionBank.Create(chosen.Sense, note.Id, exposureId, chosen.Kind);
            var attempts = new Dictionary<Guid, Attempt>(_state.Attempts)
            { [exposureId] = new(question, previous?.Outcome, previous?.Answer) };
            var skills = new Dictionary<string, Skill>(_state.Skills)
            { [chosen.Key] = chosen.Skill with { Presented = checked(chosen.Skill.Presented + 1) } };
            await SaveAsync(_state with { Attempts = attempts, Skills = skills }, cancellationToken);
            return question;
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(PracticeQuestion question, PracticeOutcome outcome, string answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_state.Attempts.TryGetValue(question.ExposureId, out var attempt) || attempt.Question != question)
                throw new InvalidOperationException("这道练习已改变或未准备，未写入结果。");
            if (attempt.Outcome is not null) return;
            if (!ValidOutcome(question.Kind, outcome)) throw new ArgumentException("练习结果与题型不符。", nameof(outcome));
            var key = Key(question.NoteId, question.SenseId, question.Kind);
            var old = _state.Skills[key];
            var points = outcome is PracticeOutcome.Correct or PracticeOutcome.SelfConfirmed ? 1.0 : outcome == PracticeOutcome.LemmaCorrect ? 0.5 : 0;
            var skills = new Dictionary<string, Skill>(_state.Skills)
            { [key] = old with { Completed = checked(old.Completed + 1), Points = old.Points + points } };
            var attempts = new Dictionary<Guid, Attempt>(_state.Attempts)
            { [question.ExposureId] = attempt with { Outcome = outcome, Answer = answer ?? "" } };
            await SaveAsync(_state with { Skills = skills, Attempts = attempts }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(PracticeState state, CancellationToken cancellationToken)
    {
        try
        {
            LocalLibraryState.EnsureNoLinks(_stateFile);
            byte[]? existing = null;
            try { existing = await File.ReadAllBytesAsync(_stateFile, cancellationToken); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            if ((_savedHash is null) != (existing is null) || (_savedHash is not null &&
                !CryptographicOperations.FixedTimeEquals(_savedHash, SHA256.HashData(existing!))))
                throw new IOException("练习记录已被外部修改，请重新载入，未覆盖原文件。");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, LocalLibraryState.JsonOptions);
            await LocalLibraryState.WriteAsync(_stateFile, state, cancellationToken);
            _state = state; _savedHash = SHA256.HashData(bytes);
        }
        catch (Exception error) when (LocalLibraryState.IsStorageError(error))
        { throw new IOException("练习记录暂未保存，原状态未推进。", error); }
    }

    private static string Key(string noteId, string senseId, PracticeKind kind) => JsonSerializer.Serialize(new[] { noteId, senseId, ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static bool ValidOutcome(PracticeKind kind, PracticeOutcome outcome) => Enum.IsDefined(outcome) &&
        (kind == PracticeKind.Spelling ? outcome is not (PracticeOutcome.SelfConfirmed or PracticeOutcome.SelfRejected)
            : outcome is PracticeOutcome.SelfConfirmed or PracticeOutcome.SelfRejected or PracticeOutcome.Skipped or PracticeOutcome.TimedOut);

    private static void Validate(PracticeState state)
    {
        if (state.FormatVersion != 1 || state.Attempts is null || state.Skills is null) throw new JsonException();
        foreach (var pair in state.Skills)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is not { } skill || skill.Presented < 0 || skill.Completed < 0 ||
                skill.Completed > skill.Presented || !double.IsFinite(skill.Points) || skill.Points < 0 || skill.Points > skill.Completed) throw new JsonException();
        foreach (var (id, attempt) in state.Attempts)
        {
            var q = attempt?.Question;
            if (id == Guid.Empty || q is null || q.ExposureId != id || string.IsNullOrWhiteSpace(q.NoteId) ||
                string.IsNullOrWhiteSpace(q.SenseId) || !Enum.IsDefined(q.Kind) || q.SenseNumber < 1 || q.SenseCount < q.SenseNumber ||
                q.Meaning is null || q.Prompt is null || q.Context is null || q.FormNote is null || q.ContextTranslation is null ||
                (attempt!.Outcome is { } outcome && !ValidOutcome(q.Kind, outcome))) throw new JsonException();
            try
            {
                if (WordInput.Normalize(q.Word) != q.Word || (q.Lemma is not null && (WordInput.Normalize(q.Lemma) != q.Lemma || q.Lemma == q.Word))) throw new JsonException();
            }
            catch (ArgumentException error) { throw new JsonException("词形无效。", error); }
            if (!state.Skills.TryGetValue(Key(q.NoteId, q.SenseId, q.Kind), out var matching) || matching.Presented < 1) throw new JsonException();
        }
    }

    private sealed record Skill(int Presented = 0, int Completed = 0, double Points = 0);
    private sealed record Attempt(PracticeQuestion Question, PracticeOutcome? Outcome = null, string? Answer = null);
    private sealed record PracticeState(int FormatVersion, Dictionary<Guid, Attempt> Attempts, Dictionary<string, Skill> Skills);
}
