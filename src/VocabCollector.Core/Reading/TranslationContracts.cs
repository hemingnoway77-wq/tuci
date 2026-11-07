namespace VocabCollector.Core.Reading;

public sealed record WordMeaning(string PartOfSpeech, string Definition);
public sealed record WordTranslation(string Word, string TranslatedText, bool IsFromCache = false)
{
    public string? Phonetic { get; init; }
    public IReadOnlyList<WordMeaning>? Meanings { get; init; }
    public string? Lemma { get; init; }
}
public interface IWordTranslationService
{
    Task<WordTranslation> TranslateAsync(string word, CancellationToken ct);
}
public sealed class WordTranslationException : IOException
{
    public WordTranslationException(string message) : base(message) { }
    public WordTranslationException(string message, Exception innerException) : base(message, innerException) { }
}
