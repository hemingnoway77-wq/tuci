using System.IO;
using System.Text;
using System.Text.Json;

namespace VocabCollector.Desktop;

public sealed class AppearanceSettingsStore(string settingsDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private string AppearanceFile => Path.Combine(settingsDirectory, "appearance.json");
    private sealed record AppearancePreference(string? Theme);

    public async Task<AppTheme> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var json = await File.ReadAllTextAsync(AppearanceFile, cancellationToken).ConfigureAwait(false);
            var preference = JsonSerializer.Deserialize<AppearancePreference>(json, JsonOptions);
            return preference?.Theme == "pink" ? AppTheme.Pink : AppTheme.WarmNeutral;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return AppTheme.WarmNeutral;
        }
    }

    public async Task SaveAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        await saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = Path.Combine(settingsDirectory, ".appearance-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(settingsDirectory);
            var preference = new AppearancePreference(theme == AppTheme.Pink ? "pink" : "warmNeutral");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preference, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(AppearanceFile)) File.Replace(temporary, AppearanceFile, null);
            else File.Move(temporary, AppearanceFile);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { saveLock.Release(); }
        }
    }
}
