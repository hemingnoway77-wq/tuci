using VocabCollector.Core.Storage;
namespace VocabCollector.Core;

public sealed record CaptureRequest(string Word, string Context);
public enum CaptureStage { Validating, CallingAi, ReceivingAi, Saving }
public enum CaptureStatus { Saved, AlreadyExists, InvalidInput, Failed, Cancelled }
public sealed record CaptureResult(CaptureStatus Status, string Word, string Markdown, IReadOnlyList<NoteLocation> Notes, string Message);
public sealed record SettingsLoadResult(AppSettings Settings, string Warning);
public interface ICaptureService
{
    Task<CaptureResult> CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct);
}
public interface IWordAnalyzer
{
    Task<WordAnalysis> AnalyzeAsync(string word, string context, ApiOptions options, CancellationToken ct);
}
public interface IProgressWordAnalyzer : IWordAnalyzer
{
    Task<WordAnalysis> AnalyzeAsync(string word, string context, ApiOptions options, IProgress<int>? receivedCharacters, CancellationToken ct);
}
public interface IVocabularyStore
{
    Task<VaultIndex> ScanAsync(VaultOptions options, CancellationToken ct);
    Task<SavedNote> SaveNewAsync(string word, PartOfSpeech primaryPos, Func<VaultIndex, string> render, VaultOptions options, CancellationToken ct);
}
public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}
public interface IAppNavigator
{
    void OpenNote(NoteLocation note);
    void OpenFolder(NoteLocation note);
}
