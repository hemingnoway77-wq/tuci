namespace VocabCollector.Core.Reading;

public interface IOfflineTranslationEngine : IDisposable
{
    Task<string> TranslateAsync(string text, CancellationToken ct);
}
