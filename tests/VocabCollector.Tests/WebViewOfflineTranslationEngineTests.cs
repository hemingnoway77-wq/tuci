using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop.Reading;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public sealed class WebViewOfflineTranslationEngineTests
{
    [Fact]
    public void MissingPackageFailsBeforeStartingABrowserAndCanBeRetried() => MarkdownPreviewTests.OnSta(() =>
    {
        using var files = new TempVault();
        var host = new Grid();
        using var engine = new WebViewOfflineTranslationEngine(host, files.Root, Path.Combine(files.Root, "profile"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<WordTranslationException>(() => Background(engine));
            Assert.Contains("完整", error.Message);
            Assert.Contains("重新解压", error.Message);
            Assert.Empty(host.Children);
        }
    });

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("en-zh/../../outside.bin")]
    [InlineData("C:/outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("en-zh/model.bin:other")]
    [InlineData("en-zh\\..\\outside.bin")]
    public void ManifestCannotReadPathsOutsideItsAssetDirectory(string path) => WithAssets((root, host) =>
    {
        WriteManifest(root, "models.manifest.json", path, 1, new string('0', 64));
        using var engine = new WebViewOfflineTranslationEngine(host, root, Path.Combine(root, "profile"));
        Assert.Throws<WordTranslationException>(() => Background(engine));
        Assert.Empty(host.Children);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("size")]
    [InlineData("hash")]
    public void MissingOrChangedFilesAreRejectedBeforeLoadingUnverifiedCode(string damage) => WithAssets((root, host) =>
    {
        var model = Path.Combine(root, "model.bin");
        if (damage == "missing") File.Delete(model);
        else if (damage == "size") File.AppendAllText(model, "extra");
        else File.WriteAllBytes(model, [9, 8, 7]);
        using var engine = new WebViewOfflineTranslationEngine(host, root, Path.Combine(root, "profile"));
        Assert.Throws<WordTranslationException>(() => Background(engine));
        Assert.Empty(host.Children);
    });

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"formatVersion\":2,\"files\":[]}")]
    [InlineData("{\"formatVersion\":1,\"files\":{}}")]
    [InlineData("{\"formatVersion\":1,\"files\":[]}")]
    [InlineData("{\"formatVersion\":1,\"files\":[{\"path\":\"model.bin\",\"bytes\":3,\"sha256\":\"bad\"}]}")]
    public void InvalidManifestsProduceAPackageErrorWithoutRawParserDetails(string manifest) => WithAssets((root, host) =>
    {
        File.WriteAllText(Path.Combine(root, "models.manifest.json"), manifest);
        using var engine = new WebViewOfflineTranslationEngine(host, root, Path.Combine(root, "profile"));
        var error = Assert.Throws<WordTranslationException>(() => Background(engine));
        Assert.Contains("重新解压", error.Message);
        Assert.DoesNotContain("BytePosition", error.Message);
        Assert.Empty(host.Children);
    });

    [Fact]
    public void CancellationAndDisposalDoNotCreateABrowserOrRequireAnyAssets() => MarkdownPreviewTests.OnSta(() =>
    {
        using var files = new TempVault();
        var host = new Grid();
        var engine = new WebViewOfflineTranslationEngine(host, files.Root, Path.Combine(files.Root, "profile"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => Background(engine, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        engine.Dispose(); engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Background(engine));
        Assert.Empty(host.Children);
    });

    private static void WithAssets(Action<string, Grid> test) => MarkdownPreviewTests.OnSta(() =>
    {
        using var files = new TempVault();
        File.WriteAllBytes(Path.Combine(files.Root, "model.bin"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(files.Root, "index.html"), "local entry");
        WriteManifest(files.Root, "models.manifest.json", "model.bin", 3, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })));
        var entry = File.ReadAllBytes(Path.Combine(files.Root, "index.html"));
        WriteManifest(files.Root, "runtime.manifest.json", "index.html", entry.Length, Convert.ToHexString(SHA256.HashData(entry)));
        test(files.Root, new Grid());
    });
    private static void WriteManifest(string root, string file, string path, long bytes, string sha256) =>
        File.WriteAllText(Path.Combine(root, file), JsonSerializer.Serialize(new { formatVersion = 1, files = new[] { new { path, bytes, sha256 } } }));
    // Invalid packages must fail on a caller thread before any dispatcher/COM work.
    private static string Background(WebViewOfflineTranslationEngine engine, CancellationToken ct = default) =>
        Task.Run(() => engine.TranslateAsync("apple", ct)).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
}
