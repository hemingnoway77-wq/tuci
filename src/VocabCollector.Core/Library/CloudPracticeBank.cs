namespace VocabCollector.Core.Library;

public sealed record CloudPracticeBank(string NoteId, string Word, IReadOnlyList<PracticeContext> Contexts);
public sealed record CloudPracticeBankPage(IReadOnlyList<CloudPracticeBank> Banks);
