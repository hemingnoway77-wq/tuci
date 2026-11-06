using VocabCollector.Core.Ai;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using System.Diagnostics;
namespace VocabCollector.Core;

public sealed class CaptureService(IWordAnalyzer analyzer, IVocabularyStore store, TimeProvider timeProvider) : ICaptureService
{
    public Task<CaptureResult> CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct) =>
        Task.Run(() => CaptureCoreAsync(request, settings, progress, ct));

    private async Task<CaptureResult> CaptureCoreAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct)
    {
        var input = ""; var word = ""; var correctedFrom = ""; var markdown = "";
        try
        {
            ct.ThrowIfCancellationRequested(); progress?.Report(CaptureStage.Validating);
            input = word = WordInput.Normalize(request.Word);
            var context = request.Context ?? "";
            if (context.Length > 2000) throw new ArgumentException("原句不能超过 2,000 个字符。");
            var index = await store.ScanAsync(settings.Vault, ct);
            var existing = index.Find(word);
            if (existing.Count > 0) return Duplicate(word, existing);
            SettingsValidation.ValidateApi(settings.Api);
            progress?.Report(CaptureStage.CallingAi);
            var aiTime = Stopwatch.StartNew();
            var analysis = analyzer is IProgressWordAnalyzer streaming
                ? await streaming.AnalyzeAsync(word, context, settings.Api, new ContentProgress(progress), ct)
                : await analyzer.AnalyzeAsync(word, context, settings.Api, ct);
            aiTime.Stop();
            ct.ThrowIfCancellationRequested();
            var resolved = SpellingValidator.ResolveWord(input, analysis);
            if (!analysis.IsValidWord) return new(CaptureStatus.InvalidInput, input, "", [], RejectionMessage(analysis.Spelling));
            word = resolved;
            if (word != input) correctedFrom = input;
            var renderer = new NoteRenderer(); var created = timeProvider.GetLocalNow();
            string Render(VaultIndex current) => markdown = renderer.Render(word, context, analysis, current, created);
            markdown = Render(index);
            ct.ThrowIfCancellationRequested(); progress?.Report(CaptureStage.Saving);
            var saved = await store.SaveNewAsync(word, analysis.PrimaryPartOfSpeech, Render, settings.Vault, ct);
            var message = CorrectionPrefix(correctedFrom, word) + $"已保存。AI 解析用时 {aiTime.Elapsed.TotalSeconds:F1} 秒。";
            if (saved.Warning.Length > 0) message += " " + saved.Warning;
            // A successful commit remains saved even if the optional local maintenance is cancelled or fails.
            if (store is ILinkMaintenance maintenance)
            {
                try
                {
                    var refreshed = await maintenance.RefreshLinksAsync(settings.Vault, ct);
                    if (refreshed.Updated > 0) message += $" 已为 {refreshed.Updated} 份历史笔记更新链接（未调用 AI）。";
                    if (refreshed.Skipped > 0 || refreshed.Failed > 0) message += " 部分人工修改或不可写的内容已跳过，可稍后刷新。";
                }
                catch (Exception) { message += " 本地补链未完成，可稍后点击刷新词条链接。"; }
            }
            return new(CaptureStatus.Saved, word, saved.Markdown, [saved.Note], message) { CorrectedFrom = correctedFrom };
        }
        catch (DuplicateWordException e) { return Duplicate(word, e.Notes, correctedFrom); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(CaptureStatus.Cancelled, input, markdown, [], "已取消，未保存新的笔记。"); }
        catch (AnalysisException e) { return new(CaptureStatus.Failed, input, markdown, [], e.Message); }
        catch (ArgumentException e) { return new(CaptureStatus.InvalidInput, input, markdown, [], e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(CaptureStatus.Failed, input, markdown, [], markdown.Length > 0 ? "笔记未能保存，请检查目录权限、磁盘空间或文件占用。解析结果已保留，可复制。" : "无法读取词库，请检查文件夹权限或文件占用。"); }
        catch (Exception) { return new(CaptureStatus.Failed, input, markdown, [], "处理失败，请检查配置后重试。输入和已生成的结果已保留。"); }
    }
    private static CaptureResult Duplicate(string word, IReadOnlyList<NoteLocation> notes, string correctedFrom = "") =>
        new(CaptureStatus.AlreadyExists, word, "", notes, CorrectionPrefix(correctedFrom, word) + (notes.Count > 1 ? "发现多个已有词条，请选择要打开的笔记。本次没有覆盖或新增文件。" : "这个单词已经收录，可直接打开已有笔记。"))
        { CorrectedFrom = correctedFrom };
    private static string CorrectionPrefix(string correctedFrom, string word) =>
        correctedFrom.Length > 0 ? $"拼写已修正：{correctedFrom} → {word}。" : "";
    private static string RejectionMessage(SpellingAssessment? spelling) =>
        spelling is { Status: SpellingStatus.Uncertain, Suggestions.Count: > 0 }
            ? "无法确定输入的正确拼写，可能是：" + string.Join("、", spelling.Suggestions.Take(3).Select(WordInput.Normalize)) + "。请确认拼写或补充原句。"
            : "模型未将输入识别为有效英文单词，请检查拼写或补充原句。";
    private sealed class ContentProgress(IProgress<CaptureStage>? progress) : IProgress<int>
    {
        private int reported;
        public void Report(int value)
        {
            if (value > 0 && Interlocked.Exchange(ref reported, 1) == 0) progress?.Report(CaptureStage.ReceivingAi);
        }
    }
}
