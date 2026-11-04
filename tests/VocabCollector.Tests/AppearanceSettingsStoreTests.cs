using System.Text.Json;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

public sealed class AppearanceSettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vocab-appearance-tests-" + Guid.NewGuid().ToString("N"));
    private string AppearanceFile => Path.Combine(directory, "appearance.json");

    [Fact]
    public async Task MissingPreferenceDefaultsToWarmWithoutCreatingFiles()
    {
        Assert.Equal(AppTheme.WarmNeutral, await new AppearanceSettingsStore(directory).LoadAsync());
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(AppTheme.Mono, "mono")]
    [InlineData(AppTheme.Pink, "pink")]
    [InlineData(AppTheme.WarmNeutral, "warmNeutral")]
    public async Task SavedThemeRoundTripsIndependentlyOfApiSettings(AppTheme theme, string storedName)
    {
        Directory.CreateDirectory(directory);
        var apiFile = Path.Combine(directory, "settings.json");
        byte[] originalApiSettings = [0, 24, 255, 63, 91];
        await File.WriteAllBytesAsync(apiFile, originalApiSettings);
        var store = new AppearanceSettingsStore(directory);

        await store.SaveAsync(theme);

        Assert.Equal(theme, await new AppearanceSettingsStore(directory).LoadAsync());
        Assert.Equal(originalApiSettings, await File.ReadAllBytesAsync(apiFile));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(AppearanceFile));
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(storedName, json.RootElement.GetProperty("theme").GetString());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{\"theme\":\"warmNeutral\"}", AppTheme.WarmNeutral)]
    [InlineData("{\"theme\":\"pink\"}", AppTheme.Pink)]
    [InlineData("{\"theme\":\"mono\"}", AppTheme.Mono)]
    public async Task PreviousPreferenceNamesRemainValid(string json, AppTheme expected)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(AppearanceFile, json);

        Assert.Equal(expected, await new AppearanceSettingsStore(directory).LoadAsync());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"theme\":\"future-theme\"}")]
    [InlineData("{\"theme\":2}")]
    [InlineData("{\"theme\":null}")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task InvalidPreferenceDefaultsToWarmAndPreservesFile(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(AppearanceFile, json);

        Assert.Equal(AppTheme.WarmNeutral, await new AppearanceSettingsStore(directory).LoadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(AppearanceFile));
    }

    [Fact]
    public async Task UnreadablePreferenceDefaultsToWarm()
    {
        var store = new AppearanceSettingsStore(directory);
        await store.SaveAsync(AppTheme.Pink);
        using var locked = new FileStream(AppearanceFile, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(AppTheme.WarmNeutral, await store.LoadAsync());
    }

    [Fact]
    public async Task FailedReplacementPreservesPreviousPreferenceAndRemovesTemporaryFile()
    {
        var store = new AppearanceSettingsStore(directory);
        await store.SaveAsync(AppTheme.Pink);
        var original = await File.ReadAllTextAsync(AppearanceFile);
        using (var locked = new FileStream(AppearanceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.SaveAsync(AppTheme.Mono));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal(original, await File.ReadAllTextAsync(AppearanceFile));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task CanceledWritePreservesPreferenceAndRemovesTemporaryFiles()
    {
        var store = new AppearanceSettingsStore(directory);
        await store.SaveAsync(AppTheme.Pink);
        var original = await File.ReadAllTextAsync(AppearanceFile);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(AppTheme.Mono, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(canceled.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(AppearanceFile));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task InvalidEnumIsSavedAsSafeWarm()
    {
        var store = new AppearanceSettingsStore(directory);
        await store.SaveAsync((AppTheme)123);
        Assert.Equal(AppTheme.WarmNeutral, await store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
