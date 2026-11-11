using System.IO;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Desktop;

public partial class MainWindow
{
    private readonly IPracticeContextGenerator? _automaticPracticeGenerator;
    private Task _capturedPracticePreparation = Task.CompletedTask;
    private long _capturedPracticeVersion;
    public Task CapturedPracticePreparation => _capturedPracticePreparation;

    private void CapturedWordSaved(object? sender, SavedWordCapture capture)
    {
        if (_closed || _accountLocked || _automaticPracticeGenerator is null || capture.Result.Status != CaptureStatus.Saved ||
            capture.Result.Notes.Count != 1 || !ReferenceEquals(capture.Settings, _viewModel.Settings)) return;
        var generation = _accountGeneration;
        var version = ++_capturedPracticeVersion;
        var token = _accountCancellation.Token;
        var savedMessage = _viewModel.Message;
        var pendingMessage = savedMessage + " 正在后台准备语境题…";
        _viewModel.Message = pendingMessage;
        var stateRoot = _sync?.AccountStateDirectory is { } account ? Path.Combine(account, "library") : _learningStateDirectory;
        _capturedPracticePreparation = PrepareCapturedPracticeAsync(_capturedPracticePreparation, capture, stateRoot,
            generation, version, savedMessage, pendingMessage, token);
    }

    private async Task PrepareCapturedPracticeAsync(Task previous, SavedWordCapture capture, string? stateRoot,
        long generation, long version, string savedMessage, string pendingMessage, CancellationToken token)
    {
        // The successful capture returns immediately; background work neither
        // holds its busy state nor starts a roaming exposure.
        await Task.Yield();
        try
        {
            await previous.WaitAsync(token);
            if (!CaptureScopeCurrent()) return;
            var vault = capture.Settings.Vault;
            var saved = capture.Result.Notes[0];
            VaultPaths.EnsureInside(VaultPaths.WordRoot(vault), saved.AbsolutePath);
            var markdown = await File.ReadAllTextAsync(saved.AbsolutePath, token);
            var relativePath = Path.GetRelativePath(VaultPaths.WordRoot(vault), saved.AbsolutePath).Replace('\\', '/');
            var note = new LibraryNote("captured", relativePath, saved.AbsolutePath, markdown, DateTimeOffset.Now);
            if (!note.IsWord || WordInput.Normalize(note.Title) != WordInput.Normalize(capture.Result.Word))
                throw new InvalidDataException("Saved word does not match its note.");
            var senses = PracticeQuestionBank.Build(note);
            if (senses.Count == 0) throw new InvalidDataException("Saved note has no parsed meanings.");
            if (senses.All(sense => string.IsNullOrWhiteSpace(sense.Lemma)))
            {
                using var dictionary = new JsonLinesWordDictionary(Path.Combine(AppContext.BaseDirectory, "Assets", "OfflineDictionary"));
                try
                {
                    var lemma = (await dictionary.LookupAsync(note.Title, token))?.Lemma;
                    senses = PracticeQuestionBank.Build(note, lemma);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { /* Exact saved spelling remains usable without optional morphology. */ }
            }
            if (!CaptureScopeCurrent()) return;
            var cachePath = Path.Combine(LearningPage.ResolveLocalStateDirectory(vault, stateRoot), "practice-contexts.json");
            await new PracticeContextPreparation(cachePath, _automaticPracticeGenerator!)
                .PrepareAsync(note.Title, senses, capture.Settings.Api, token);
            if (!CaptureScopeCurrent()) return;
            if (_learningPage is { } page) await page.RefreshPreparedPracticeAsync(note.Title);
            CompleteNotice(" 语境练习题已准备。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AnalysisException error)
        {
            CompleteNotice(" 语境题未准备。" + error.Message + " 可在漫游中重新准备。");
        }
        catch (Exception)
        {
            // The Markdown commit succeeded before this optional work began.
            CompleteNotice(" 练习题暂未准备，可在漫游中重试。");
        }

        bool CaptureScopeCurrent() => !_closed && !_accountLocked && !token.IsCancellationRequested &&
            generation == _accountGeneration && capture.Settings.Vault == _viewModel.Settings.Vault;
        void CompleteNotice(string suffix)
        {
            if (CaptureScopeCurrent() && version == _capturedPracticeVersion && _viewModel.Message == pendingMessage)
                _viewModel.Message = savedMessage + suffix;
        }
    }
}
