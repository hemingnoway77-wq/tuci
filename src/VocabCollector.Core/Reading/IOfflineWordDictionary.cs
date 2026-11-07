namespace VocabCollector.Core.Reading;

public interface IOfflineWordDictionary : IDisposable
{
    Task<WordTranslation?> LookupAsync(string word, CancellationToken ct);
}
