using System.IO;
using VocabCollector.Core.Library;
using VocabCollector.Core.Reading;

namespace VocabCollector.Desktop;

public partial class MainWindow
{
    private async Task ImportCloudPracticeAsync(IReadOnlyList<CloudPracticeBank> banks, CancellationToken cancellationToken)
    {
        if (_closed || _accountLocked || _sync?.CanUseAccount != true || banks.Count == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _accountCancellation.Token);
        var token = linked.Token;
        var generation = _accountGeneration;
        var settings = _viewModel.Settings;
        var learning = EnsureLearningPage();
        await learning.EnsureLoadedAsync();
        if (!CurrentScope() || learning.LocalStateDirectory is not { } directory) return;
        var cache = new PracticeContextCache(Path.Combine(directory, "practice-contexts.json"));
        using var dictionary = new JsonLinesWordDictionary(Path.Combine(AppContext.BaseDirectory, "Assets", "OfflineDictionary"));
        foreach (var bank in banks)
        {
            if (!CurrentScope()) return;
            var note = learning.AllNotes.FirstOrDefault(note => learning.CloudNoteIds.GetValueOrDefault(note.Id) == bank.NoteId);
            if (note is null || !string.Equals(note.Title, bank.Word, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var senses = PracticeQuestionBank.Build(note);
                if (senses.Count == 0) continue;
                if (senses.All(sense => string.IsNullOrWhiteSpace(sense.Lemma)))
                {
                    try { senses = PracticeQuestionBank.Build(note, (await dictionary.LookupAsync(note.Title, token))?.Lemma); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { /* Optional morphology does not prevent exact-form practice. */ }
                }
                if (!CurrentScope()) return;
                await cache.ImportMissingAsync(note.Title, senses, bank.Contexts, token);
                if (!CurrentScope()) return;
                await learning.RefreshPreparedPracticeAsync(note.Title);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
            { /* Invalid/stale banks never replace a valid local cache or block note sync. */ }
        }

        bool CurrentScope() => !_closed && !_accountLocked && !token.IsCancellationRequested &&
            generation == _accountGeneration && settings.Vault == _viewModel.Settings.Vault && ReferenceEquals(learning, _learningPage);
    }
}
