using System.Text.Json.Serialization;

namespace VocabCollector.Core.Library;

public enum PracticeKind { Spelling, Meaning }
public enum PracticeOutcome { Correct, LemmaCorrect, Incorrect, TimedOut, SelfConfirmed, SelfRejected, Skipped }

public sealed record PracticeQuestion(Guid ExposureId, string NoteId, string SenseId, PracticeKind Kind,
    string Word, string? Lemma, string Meaning, string Prompt, string Context, string FormNote,
    int SenseNumber, int SenseCount, string ContextTranslation = "");

public sealed record PracticeSense(string Id, string Word, string? Lemma, string Meaning,
    string Context, string FormNote, int Number, int Count, string ContextTranslation = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AnswerForm = null);

public sealed record PracticeContext(string SenseId, string English, string Chinese,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AnswerForm = null);

public sealed record PracticeCompletion(PracticeOutcome Outcome, string Answer);
