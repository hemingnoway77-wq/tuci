namespace VocabCollector.Core.Reading;

public enum ReadingCategory { General, Technology }

public sealed record ReadingArticle(
    string Title,
    string Summary,
    string Url,
    string SourceName,
    ReadingCategory Category,
    DateTimeOffset? PublishedAt = null);

public sealed record DailyReadingResult(
    DateOnly Day,
    IReadOnlyList<ReadingArticle> Articles,
    bool IsFromCache,
    bool IsOffline,
    string Message);

public interface IDailyReadingService
{
    Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken);
}

public sealed record ReadingFeed(string Name, string Url, ReadingCategory Category);
