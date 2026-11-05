using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;

internal static class CrossPlatformFixture
{
    public static async Task Run(string mode, string sandbox)
    {
        var server = new Uri(Environment.GetEnvironmentVariable("TUCI_TEST_SERVER") ?? throw new ArgumentException("TUCI_TEST_SERVER"));
        if (!server.IsLoopback) throw new ArgumentException("The integration fixture requires loopback.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var parent = Path.Combine(sandbox, "windows-client");
        Directory.CreateDirectory(parent);
        var vault = new VaultOptions(parent, "英语单词", true);
        var root = VaultPaths.WordRoot(vault);
        var path = Path.Combine(root, "动词", "introduced.md");
        var state = Path.Combine(parent, "state.json");
        var secret = Path.Combine(parent, "token");
        var example = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("TUCI_TEST_NOTE")
            ?? throw new ArgumentException("TUCI_TEST_NOTE"), ct);
        if (mode == "seed")
        {
            var code = Environment.GetEnvironmentVariable("TUCI_TEST_CODE") ?? throw new ArgumentException("TUCI_TEST_CODE");
            var identity = await new SyncClient(client, server).RedeemAsync(code, "Windows fixture", ct);
            await StoreSecret(secret, identity.Token, ct);
            var remote = new SyncClient(client, server, identity.Token);
            await new VaultStore().SaveNewAsync("introduced", PartOfSpeech.Verb, _ => example, vault, ct);
            var sent = await new VaultSync(vault, state, remote).RunAsync(ct);
            if (sent.Uploaded != 1 || sent.Conflicts != 0) throw new InvalidOperationException("Windows seed did not upload note");
            await StoreSecret(Path.Combine(sandbox, "pair-code.txt"), await remote.PairAsync(ct), ct);
        }
        else
        {
            var remote = new SyncClient(client, server, await File.ReadAllTextAsync(secret, ct));
            await new VaultSync(vault, state, remote).RunAsync(ct);
            if (await File.ReadAllTextAsync(path, ct) != example ||
                !Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Where(f => f != path)
                    .Any(f => File.ReadAllText(f).Contains("我在手机离线时写的原句。")))
                throw new InvalidOperationException("Windows pull did not preserve the original and the Swift offline edit");
            Console.WriteLine("PASS Windows client read the iPhone edit and preserved the original note");
        }
    }

    private static async Task StoreSecret(string path, string value, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value), ct);
    }
}
