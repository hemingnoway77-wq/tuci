namespace VocabCollector.Core.Reading;

public sealed record ArticleContent(ReadingArticle Article, IReadOnlyList<string> Paragraphs, DateTimeOffset FetchedAt,
    bool IsFromCache = false, bool IsOffline = false, string Message = "");

public interface IArticleContentService
{
    Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken);
}

public sealed class ArticleContentException(string message, Exception? inner = null) : IOException(message, inner);
