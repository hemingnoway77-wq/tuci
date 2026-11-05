using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Sync;
using VocabCollector.Core.Storage;

// dotnet run --project tests/VocabCollector.Sync.RoundTrip -- server/bin/Release/net10.0/Tuci.Sync.dll
if (args is [("seed" or "verify") and var mode, var sandbox])
{
    await CrossPlatformFixture.Run(mode, sandbox);
    return;
}
if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass the built sync server DLL as the only argument.");
var dll = Path.GetFullPath(args[0]);
var dir = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".artifacts", "roundtrip-" + Guid.NewGuid().ToString("N"))).FullName;
using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
var db = Path.Combine(dir, "server.db");
Process Start(params string[] arguments)
{
    var p = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    p.ArgumentList.Add(dll);
    foreach (var argument in arguments) p.ArgumentList.Add(argument);
    p.Environment["TUCI_DB"] = db;
    return Process.Start(p) ?? throw new Exception("Server process did not start.");
}

int port;
using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
var url = new Uri($"http://127.0.0.1:{port}/");
Process Serve()
{
    var host = Start("--urls", url.ToString());
    host.BeginOutputReadLine(); host.BeginErrorReadLine();
    return host;
}
static async Task Stop(Process process)
{
    if (!process.HasExited) process.Kill(true);
    await process.WaitForExitAsync();
    process.Dispose();
}
async Task Ready(CancellationToken ct)
{
    while (true)
    {
        ct.ThrowIfCancellationRequested();
        try { if ((await http.GetStringAsync(new Uri(url, "health"), ct)).Contains("ok")) break; }
        catch (HttpRequestException) { }
        await Task.Delay(50, ct);
    }
}
var server = Serve();
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var ct = deadline.Token;
    await Ready(ct);
    using var invite = Start("invite");
    using var reply = JsonDocument.Parse(await invite.StandardOutput.ReadToEndAsync(ct));
    await invite.WaitForExitAsync(ct);
    var setup = new SyncClient(http, url);
    var a = await setup.RedeemAsync(reply.RootElement.GetProperty("code").GetString()!, "Windows A", ct);
    var clientA = new SyncClient(http, url, a.Token);
    var b = await setup.RedeemAsync(await clientA.PairAsync(ct), "Windows B", ct);
    Check(a.VaultId == b.VaultId, "devices pair to same vault");
    var clientB = new SyncClient(http, url, b.Token);
    var vaultA = new VaultOptions(Directory.CreateDirectory(Path.Combine(dir, "A")).FullName, "英语单词", true);
    var vaultB = new VaultOptions(Directory.CreateDirectory(Path.Combine(dir, "B")).FullName, "英语单词", true);
    var rootA = Directory.CreateDirectory(VaultPaths.WordRoot(vaultA)).FullName;
    var rootB = Directory.CreateDirectory(VaultPaths.WordRoot(vaultB)).FullName;
    var stateA = Path.Combine(dir, "stateA.json");
    VaultSync First(ISyncRemote? remote = null) => new(vaultA, stateA, remote ?? clientA);
    VaultSync Second() => new(vaultB, Path.Combine(dir, "stateB.json"), clientB);
    var name = "阅读摘记.md";
    string Note(string root) => Path.Combine(root, name);
    File.WriteAllText(Note(rootA), "existing A");
    File.WriteAllText(Note(rootB), "existing B");
    Directory.CreateDirectory(Path.Combine(rootA, ".obsidian"));
    File.WriteAllText(Path.Combine(rootA, ".obsidian", "private.md"), "not synced");
    await First().RunAsync(ct);
    var initial = await Second().RunAsync(ct);
    await First().RunAsync(ct);
    Check(initial.Conflicts == 1, "first reconciliation retains both notes");
    Check(File.ReadAllText(Note(rootB)) == "existing A" && Directory.GetFiles(rootA,"*.md").Any(p => File.ReadAllText(p) == "existing B"), "both clients retain both versions");
    Check(!Directory.Exists(Path.Combine(rootB, ".obsidian")), "private directory excluded");

    var backupPath = Path.Combine(dir, "consistent-backup.sqlite3");
    using (var backup = Start("backup", backupPath))
    {
        await backup.WaitForExitAsync(ct);
        Check(backup.ExitCode == 0 && File.Exists(backupPath), "database backup exists");
    }
    File.WriteAllText(Note(rootA), "lost response");
    var lost = new AfterCommitDrop(clientA);
    try { await First(lost).RunAsync(ct); throw new Exception("expected network failure"); } catch (HttpRequestException) { }
    var cached = File.ReadAllText(stateA);
    Check(cached.Contains(lost.Id!), "operation ID journaled before sending");
    await First().RunAsync(ct);
    await Second().RunAsync(ct);
    Check(File.ReadAllText(Note(rootB)) == "lost response" && Directory.GetFiles(rootA,"*.md").Length == 2, "replayed write is idempotent");

    File.WriteAllText(Note(rootA), "edited offline A");
    File.WriteAllText(Note(rootB), "edited offline B");
    await Second().RunAsync(ct); await First().RunAsync(ct); await Second().RunAsync(ct);
    Check(Directory.GetFiles(rootB, "*.md").Select(File.ReadAllText).ToHashSet().IsSupersetOf(["edited offline A", "edited offline B"]), "concurrent edits preserved");

    File.WriteAllText(Note(rootB), "newer before stale delete");
    await Second().RunAsync(ct);
    File.Delete(Note(rootA));
    await First().RunAsync(ct);
    Check(File.ReadAllText(Note(rootA)) == "newer before stale delete", "stale deletion restores newer remote edit");

    var renamed = Path.Combine(rootA, "笔记目录", "移动后.md");
    Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
    File.Move(Note(rootA), renamed);
    var before = (await clientA.ChangesAsync(0,ct)).Changes.Last(e => e.Path == name && !e.Deleted).NoteId;
    await First().RunAsync(ct); await Second().RunAsync(ct);
    var after = (await clientA.ChangesAsync(0,ct)).Changes.Last(e => e.Path.EndsWith("移动后.md")).NoteId;
    Check(before == after && !File.Exists(Note(rootB)) && File.Exists(Path.Combine(rootB,"笔记目录","移动后.md")), "unique rename retains identity");

    var store = new VaultStore();
    await store.SaveNewAsync("hello", PartOfSpeech.Noun, _ => "# hello\n\n#### 例句\n1. I say hello to my friends.\n   - 我跟朋友打招呼。\n", vaultA, ct);
    await First().RunAsync(ct); await Second().RunAsync(ct);
    var exported = (await clientA.ChangesAsync(0,ct)).Changes.Last(e => e.Path.EndsWith("/hello.md"));
    Check(exported.Regions?.ContainsKey("examples") == true, "send original program region ownership");
    var stateBBody = File.ReadAllText(Path.Combine(rootB,".vocabcollector","note-state.json"));
    Check(stateBBody.Contains(exported.Regions!["examples"]), "preserve inbound registration hashes");

    var changes = (await clientB.ChangesAsync(0,ct)).Changes.Where(c => c.NoteId == before).Last();
    await clientB.PushAsync(new(Guid.NewGuid().ToString(), before, changes.Version, changes.Path, "cloud just changed", false), ct);
    await First(new BeforePull(clientA, () => File.WriteAllText(renamed, "external edit during pull"))).RunAsync(ct);
    Check(File.ReadAllText(renamed) == "cloud just changed" &&
        Directory.GetFiles(Path.GetDirectoryName(renamed)!, "本机冲突-*.md").Any(p => File.ReadAllText(p) == "external edit during pull"),
        "external edit between snapshot and pull retained in visible copy");
    await First().RunAsync(ct);
    var movedRoot = rootA + "-offline";
    Directory.Move(rootA, movedRoot);
    try { await First().RunAsync(ct); throw new Exception("missing directory accepted"); } catch (DirectoryNotFoundException) { }
    Directory.Move(movedRoot, rootA);
    Check((await First().RunAsync(ct)).Uploaded == 0, "missing directory cannot delete cloud notes");
    var beforeDelete = (await clientA.ChangesAsync(0,ct)).Changes.Last(e => e.NoteId == before);
    await clientB.PushAsync(new(Guid.NewGuid().ToString(), before, beforeDelete.Version, beforeDelete.Path, "", true), ct);
    await First(new BeforePull(clientA, () => File.WriteAllText(renamed, "local edit as remote deleted"))).RunAsync(ct);
    Check(!File.Exists(renamed) && Directory.GetFiles(Path.GetDirectoryName(renamed)!, "本机冲突-*.md")
        .Any(p => File.ReadAllText(p) == "local edit as remote deleted"), "remote deletion preserves an editor save during pull");

    var lockedNote = Path.Combine(rootA, "locked.md");
    File.WriteAllText(lockedNote, "locked body");
    await First().RunAsync(ct);
    var beforeLock = (await clientA.ChangesAsync(0, ct)).Cursor;
    using (File.Open(lockedNote, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { await First().RunAsync(ct); throw new Exception("unreadable file treated as missing"); } catch (IOException) { }
    }
    Check((await clientA.ChangesAsync(beforeLock, ct)).Changes.Length == 0, "unreadable file aborts sync without tombstone");

    if (!OperatingSystem.IsWindows())
    {
        var real = Path.Combine(dir, "outside.md");
        File.WriteAllText(real, "outside contents");
        File.Move(lockedNote, lockedNote + ".saved");
        File.CreateSymbolicLink(lockedNote, real);
        try { await First().RunAsync(ct); throw new Exception("reparse point accepted"); }
        catch (Exception error) when (error is IOException or ArgumentException) { }
        Check((await clientA.ChangesAsync(beforeLock, ct)).Changes.Length == 0 && File.ReadAllText(real) == "outside contents",
            "tracked file replaced by symlink neither uploads its target nor deletes its cloud version");
        File.Delete(lockedNote);
        File.Move(lockedNote + ".saved", lockedNote);
    }

    await Stop(server);
    File.Delete(db + "-wal");
    File.Delete(db + "-shm");
    File.Copy(backupPath, db, overwrite: true);
    server = Serve();
    await Ready(ct);
    File.WriteAllText(Note(rootA), "offline after backup");
    // Enough pending uploads to mask a rollback if the client pushes first and checks its cursor later.
    for (var n = 0; n < 18; n++) File.WriteAllText(Path.Combine(rootA, $"offline{n}.md"), $"text{n}");
    var recovery = await First().RunAsync(ct);
    Check(recovery.Reconciled && Directory.GetFiles(dir, "stateA.json.recover-*.json").Length == 1,
        "restored older server history detected before pushing queued operations");
    Check(File.ReadAllText(Note(rootA)) == "existing A" &&
        Directory.GetFiles(rootA, "*.md").Any(p => File.ReadAllText(p) == "offline after backup"),
        "restored server and independent local versions both survive");

    var previouslyCheckpointed = File.ReadAllText(stateA);
    await File.WriteAllTextAsync(stateA, previouslyCheckpointed.Replace("locked.md","edited.md"),ct);
    // A structurally valid but damaged checkpoint must never send potentially fabricated deletions.
    var repaired = await First().RunAsync(ct);
    Check(repaired.Reconciled && File.ReadAllText(lockedNote) == "locked body" &&
        Directory.GetFiles(dir,"stateA.json.recover-*.json").Length == 2, "corrupt local journal archived and reconciled from actual files");
    Console.WriteLine("Windows sync engine round-trip passed.");
}
finally
{
    await Stop(server);
    Directory.Delete(dir, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

sealed class AfterCommitDrop(ISyncRemote inner) : ISyncRemote
{
    public string? Id { get; private set; }
    public Task<SyncPage> ChangesAsync(long cursor, CancellationToken ct) => inner.ChangesAsync(cursor, ct);
    public async Task<SyncReceipt> PushAsync(SyncOperation op, CancellationToken ct)
    {
        Id = op.OperationId;
        await inner.PushAsync(op, ct);
        throw new HttpRequestException("lost reply");
    }
}


sealed class BeforePull(ISyncRemote inner, Action action) : ISyncRemote
{
    public Task<SyncReceipt> PushAsync(SyncOperation op, CancellationToken ct) => inner.PushAsync(op, ct);
    public Task<SyncPage> ChangesAsync(long cursor, CancellationToken ct) { action(); return inner.ChangesAsync(cursor, ct); }
}
