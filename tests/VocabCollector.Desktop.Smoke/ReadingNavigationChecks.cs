using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    private async Task CheckReadingNavigationPerformanceAsync(string output)
    {
        var article = new ReadingArticle("Local long article", "Not the body", "https://www.bbc.com/news/local-performance-fixture", "Local fixture", ReadingCategory.General);
        var paragraphs = Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("An apple fell. Another apple grew nearby!", 5)), 80).ToArray();
        var service = new RetainedArticleFixture(paragraphs);
        var translations = new RecordingWordTranslationService();
        using var page = new ReadingPage(new DailyReadingViewModel(new RecordingReadingService()), _ => throw new InvalidOperationException("No external page may open in this fixture."), service, translations);
        var owner = new Window { Content = page, Width = 900, Height = 680, ShowInTaskbar = false };
        owner.Show(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        try
        {
            var watch = Stopwatch.StartNew(); await page.OpenArticleAsync(article);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var firstOpenMs = watch.Elapsed.TotalMilliseconds;
            var reader = ReadingControl<ArticleTextReader>(page, "ArticleBodyReader"); var document = reader.Document;
            var surface = ReadingControl<Grid>(page, "ArticleSurface");
            Check(document.Blocks.Count == paragraphs.Length && document.Blocks.OfType<Paragraph>().All(p => p.Inlines.Count == 1),
                "long articles initially use one plain text run per paragraph instead of a visual tree for every word");
            Check(!surface.HasAnimatedProperties && surface.Opacity == 1 && surface.RenderTransform.Value.IsIdentity,
                "ready article content is immediately opaque and stationary without a delayed entrance clock");

            var first = document.Blocks.OfType<Paragraph>().First(); var run = first.Inlines.OfType<Run>().Single();
            var offset = paragraphs[0].IndexOf("Another apple", StringComparison.Ordinal) + "Another ".Length;
            var left = run.ContentStart.GetPositionAtOffset(offset + 1)!.GetCharacterRect(LogicalDirection.Forward);
            var right = run.ContentStart.GetPositionAtOffset(offset + 2)!.GetCharacterRect(LogicalDirection.Forward);
            var point = new Point((left.Left + right.Left) / 2, left.Top + left.Height / 2);
            var find = typeof(ArticleTextReader).GetMethod("FindPlainWord", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var activate = typeof(ArticleTextReader).GetMethod("ActivatePlainWord", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var index = 0; index < 12; index++)
            {
                var hit = find.Invoke(reader, [point]); Check(hit is not null, "long-article glyph hit-testing locates the selected occurrence");
                activate.Invoke(reader, [hit]); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(page.WordActionsPopup.IsOpen && ReadingControl<TextBlock>(page, "ReadingSelectedWord").Text == "apple",
                    "a long article opens word actions without rebuilding its FlowDocument");
                page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page)!, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(!page.WordActionsPopup.IsOpen, "Escape dismisses long-article word actions");
            }
            Check(first.Inlines.Count <= 3 && ReferenceEquals(reader.Document, document), "repeated word actions do not grow the long document tree");
            reader.ScrollToVerticalOffset(240); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(reader.VerticalOffset > 100, "long navigation fixture has a real reading offset");
            var transitions = new List<double>();
            for (var index = 0; index < 32; index++)
            {
                watch.Restart(); page.ReturnToDirectory(); await page.OpenArticleAsync(article);
                transitions.Add(watch.Elapsed.TotalMilliseconds);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(ReferenceEquals(reader.Document, document) && reader.VerticalOffset > 100 && !surface.HasAnimatedProperties && surface.RenderTransform.Value.IsIdentity,
                    "rapid directory/article turns retain the document, reading anchor and immediate final visuals");
                await Task.Delay(8);
            }
            Check(service.Calls == 1 && translations.Calls == 0, "repeated article turns never refetch content or translate without an explicit action");
            File.WriteAllText(Path.Combine(output, "article-navigation-performance.json"), JsonSerializer.Serialize(new
            {
                paragraphs = paragraphs.Length, words = paragraphs.Sum(p => ArticleWords.Tokenize(p).Count), firstOpenMs,
                transitions = transitions.Count, transitionMs = transitions, contentRequests = service.Calls,
                retainedDocument = true, settledVisuals = true, repeatedWordActions = 12
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { page.Dispose(); owner.Close(); }
    }

    private sealed class RetainedArticleFixture(IReadOnlyList<string> paragraphs) : IArticleContentService
    {
        public int Calls { get; private set; }
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new ArticleContent(article, paragraphs, DateTimeOffset.UtcNow)); }
    }
}
