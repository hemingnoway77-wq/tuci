using System.Security.Cryptography;
using System.Text;
using System.Windows;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

public sealed class LearningPageTests
{
    [Fact]
    public void CorruptReviewStateDoesNotPreventReadingAndIsNotOverwritten() => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var sandbox = Path.Combine(Path.GetTempPath(), "learning-page-" + Guid.NewGuid().ToString("N"));
        var vault = new VaultOptions(Directory.CreateDirectory(Path.Combine(sandbox, "vault")).FullName, "notes", false);
        var wordRoot = Directory.CreateDirectory(VaultPaths.WordRoot(vault)).FullName;
        File.WriteAllText(Path.Combine(wordRoot, "apple.md"), "# apple\n\nReadable original.");
        var state = Path.Combine(sandbox, "state");
        var catalogState = Path.Combine(state, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wordRoot.ToUpperInvariant()))));
        Directory.CreateDirectory(catalogState);
        var broken = Path.Combine(catalogState, "roaming.json");
        File.WriteAllText(broken, "broken review data");
        using var page = new LearningPage(vault, state);
        page.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try
        {
            Await(page.ReloadAsync());
            var note = Assert.Single(page.AllNotes);
            page.OpenNote(note);
            Assert.Equal(note, page.ReaderControl.Note);
            Await(page.ShowRoamingAsync());
            Assert.False(page.HasExposure);
            Assert.Contains("复习记录", page.Message);
            Assert.Equal("broken review data", File.ReadAllText(broken));
        }
        finally { page.Dispose(); Directory.Delete(sandbox, recursive: true); }
    });

    [Fact]
    public async Task FirstLaunchLocalLibraryCanImportAndReadWithoutApiConfiguration()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "local-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = LocalLibrarySettings.Resolve(AppSettings.Default, Path.Combine(sandbox, "app"));
            var store = new WindowsSettingsStore(Path.Combine(sandbox, "config"));
            await store.SaveAsync(settings, default);
            var loaded = await store.LoadAsync(default);
            var source = Path.Combine(sandbox, "阅读摘记.md");
            await File.WriteAllTextAsync(source, "# 阅读摘记\n\n不需要 AI 配置即可阅读。");
            var library = new VocabCollector.Core.Library.LibraryCatalog(loaded.Settings.Vault, Path.Combine(sandbox, "state"));
            var note = Assert.Single(await library.ImportAsync([source]));
            Assert.Contains("不需要 AI", note.Markdown);
            Assert.False(note.IsWord);
            Assert.Empty(loaded.Settings.Api.ApiKey);
            Assert.NotEqual(source, note.AbsolutePath);
            Assert.Equal(note.Markdown, await File.ReadAllTextAsync(source));
        }
        finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
    }

    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(task.IsCompleted, "Learning page timed out.");
        task.GetAwaiter().GetResult();
    }
}
