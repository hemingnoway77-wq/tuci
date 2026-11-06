using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace VocabCollector.Core.Reading;

public static class ArticleExtractor
{
    internal const int MaxParagraphs = 140, MaxParagraphLength = 6000, MaxBodyLength = 90_000;

    public static Task<IReadOnlyList<string>> ExtractAsync(string html, Uri source, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArticleContentService.IsAllowed(source)) throw new ArticleContentException("此文章来源暂不支持纯净阅读。");
            if (html.Length > 2 * 1024 * 1024) throw new ArticleContentException("文章页面过大，无法安全提取全文。");
            // A parser only: no resource loader, JavaScript engine, or browser context.
            using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
            var elements = document.QuerySelectorAll("*").Take(100_001).ToArray();
            if (elements.Length > 100_000) throw new ArticleContentException("文章页面结构过大，无法安全提取全文。");
            foreach (var element in elements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsNoise(element)) element.Remove();
            }
            foreach (var lineBreak in document.QuerySelectorAll("br"))
                lineBreak.Replace(document.CreateTextNode(" "));
            var selector = source.IdnHost.ToLowerInvariant() switch
            {
                "www.sciencedaily.com" => "#story_text",
                "blog.cloudflare.com" => ".article-content, .post-content",
                _ => "[class*='RichTextContainer'], [data-component='text-block']"
            };
            var roots = document.QuerySelectorAll(selector);
            if (roots.Length == 0) throw new ArticleContentException("暂未找到这篇文章的正文。网页结构可能已变化，未使用摘要替代全文，请稍后重试。");
            var paragraphs = new List<string>();
            var seen = new HashSet<IElement>();
            var total = 0;
            foreach (var root in roots)
            {
                foreach (var paragraph in root.QuerySelectorAll("p"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seen.Add(paragraph)) continue;
                    var text = Plain(paragraph.TextContent);
                    if (text.Length < 12 || IsPromotional(text)) continue;
                    if (text.Length > MaxParagraphLength || paragraphs.Count >= MaxParagraphs || total + text.Length > MaxBodyLength)
                        throw new ArticleContentException("文章正文超出纯净阅读长度限制，未截断冒充全文。请改读其他推荐文章。");
                    paragraphs.Add(text);
                    total += text.Length;
                }
            }
            if (!IsValidBody(paragraphs)) throw new ArticleContentException("未能提取足够的文章正文，可能是摘要、访问提示或来源暂不可用。未将其当作全文，请稍后重试。");
            return (IReadOnlyList<string>)paragraphs.ToArray();
        }, cancellationToken);

    internal static bool IsValidBody(IReadOnlyList<string> paragraphs) => paragraphs.Count is >= 2 and <= MaxParagraphs &&
        paragraphs.All(paragraph => !string.IsNullOrWhiteSpace(paragraph) && paragraph.Length is >= 12 and <= MaxParagraphLength) &&
        paragraphs.Sum(paragraph => (long)paragraph.Length) is >= 300 and <= MaxBodyLength;

    private static bool IsNoise(IElement element)
    {
        if (element.LocalName is "script" or "style" or "noscript" or "nav" or "header" or "footer" or "aside" or "form" or
            "button" or "input" or "textarea" or "select" or "iframe" or "video" or "audio" or "canvas" or "svg" or "template" or "object" or "embed") return true;
        if (element.HasAttribute("hidden") || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        var style = element.GetAttribute("style") is { } rawStyle
            ? string.Concat(rawStyle.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant() : null;
        if (style?.Contains("display:none", StringComparison.Ordinal) == true || style?.Contains("visibility:hidden", StringComparison.Ordinal) == true) return true;
        var marker = string.Join(' ', element.Id, element.ClassName, element.GetAttribute("role"), element.GetAttribute("data-component"), element.GetAttribute("data-testid")).ToLowerInvariant();
        if (new[] { "advertisement", "ad-container", "ad-slot", "ad-unit", "google-auto-placed", "newsletter", "subscribe", "related-stor", "related-artic", "recommend", "social-share", "cookie-banner", "promo-block", "complementary" }.Any(marker.Contains)) return true;
        return marker.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Any(token => token is "ad" or "ads" or "advert" or "recirculation");
    }

    private static bool IsPromotional(string text) => new[] { "Advertisement", "Sign up for", "Subscribe to", "Follow us on", "Related stories", "Read more:" }
        .Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string Plain(string text)
    {
        var result = new StringBuilder(Math.Min(text.Length, MaxParagraphLength + 1));
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) { space = result.Length > 0; continue; }
            if (space) result.Append(' ');
            result.Append(character);
            space = false;
            if (result.Length > MaxParagraphLength) break;
        }
        return result.ToString();
    }
}
