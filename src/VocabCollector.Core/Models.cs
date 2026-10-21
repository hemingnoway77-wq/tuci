namespace VocabCollector.Core;

public sealed record ApiOptions(string BaseUrl, string ApiKey, string Model);
public sealed record VaultOptions(string RootPath, string OutputSubfolder, bool ClassifyByPartOfSpeech);
public sealed record AppSettings(ApiOptions Api, VaultOptions Vault)
{
    public static AppSettings Default => new(new("", "", ""), new("", "英语单词", true));
}
public enum PartOfSpeech { Noun, Verb, Adjective, Adverb, Pronoun, Preposition, Conjunction, Interjection, Determiner, Numeral, Other }
public enum RelationKind { SameRoot, Synonym, Antonym, Confusable }
public sealed record Meaning(PartOfSpeech PartOfSpeech, string Definition);
public sealed record BilingualText(string English, string Chinese);
public sealed record RelatedCandidate(string Word, RelationKind Kind, string Explanation = "");
public sealed record GrammarPattern(string Pattern, string Explanation);
public sealed record NoteLocation(string Word, string AbsolutePath, string VaultRelativePath);
public sealed record SavedNote(NoteLocation Note, string Markdown)
{
    public string Warning { get; init; } = "";
}
public sealed record ResolvedRelation(RelationKind Kind, NoteLocation Note);
public sealed record WordAnalysis(bool IsValidWord, string Error, string UkPhonetic, string UsPhonetic,
    PartOfSpeech PrimaryPartOfSpeech, IReadOnlyList<Meaning> Meanings, IReadOnlyList<BilingualText> Examples,
    IReadOnlyList<BilingualText> Collocations, IReadOnlyList<RelatedCandidate> Related)
{
    public string Lemma { get; init; } = "";
    public string FormNote { get; init; } = "";
    public IReadOnlyList<GrammarPattern> Grammar { get; init; } = [];
}
