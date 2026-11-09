using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;

if (args is not ["seed" or "verify", var sandbox])
    throw new ArgumentException("Usage: seed|verify <sandbox>; TUCI_TEST_SERVER must be a loopback HTTP server.");
var mode = args[0];
sandbox = Path.GetFullPath(sandbox);
Directory.CreateDirectory(sandbox);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var fixtureFile = Path.Combine(sandbox, "fixture.json");
var server = new Uri(Environment.GetEnvironmentVariable("TUCI_TEST_SERVER") ?? throw new ArgumentException("TUCI_TEST_SERVER is required."));
if (!server.IsLoopback || server.Scheme is not ("http" or "https")) throw new ArgumentException("Only isolated loopback servers are allowed.");
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var ct = deadline.Token;
Fixture fixture;
AccountIdentity identity;
if (mode == "seed")
{
    var username = Environment.GetEnvironmentVariable("TUCI_TEST_USERNAME");
    var password = Environment.GetEnvironmentVariable("TUCI_TEST_PASSWORD");
    var supplied = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    username ??= "roundtrip-" + Guid.NewGuid().ToString("N")[..16];
    password ??= Guid.NewGuid().ToString("N") + "-test-only";
    var setup = new SyncClient(http, server);
    identity = supplied ? await setup.LoginAsync(username, password, "Windows roundtrip", ct)
        : await setup.RegisterAsync(username, password, "Windows roundtrip", ct);
    fixture = new(server.AbsoluteUri, username, password, identity.VaultId, "", "", "", "# apple\n\nOriginal from Windows.",
        "https://example.org/tuci-roundtrip/article");
}
else
{
    fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(fixtureFile, ct), json) ?? throw new InvalidDataException("Missing fixture.");
    if (fixture.Server != server.AbsoluteUri) throw new InvalidDataException("Fixture server differs from TUCI_TEST_SERVER.");
    identity = await new SyncClient(http, server).LoginAsync(fixture.Username, fixture.Password, "Windows verification", ct);
}
var remote = new SyncClient(http, server, identity.Token, identity.VaultId);
var root = Path.Combine(sandbox, "windows", "vault");
Directory.CreateDirectory(root);
var vault = new VaultOptions(root, "notes", false);
var catalog = new LibraryCatalog(vault, Path.Combine(sandbox, "windows", "catalog"));
var markdownState = Path.Combine(sandbox, "windows", "markdown-sync.json");
var state = new AccountStateSync(Path.Combine(sandbox, "windows", "account-state.json"), remote);
var roamingFile = Path.Combine(catalog.LocalStateDirectory, "roaming.json");

if (mode == "seed")
{
    Directory.CreateDirectory(VaultPaths.WordRoot(vault));
    await File.WriteAllTextAsync(Path.Combine(VaultPaths.WordRoot(vault), "apple.md"), fixture.OriginalMarkdown, ct);
    var initial = await catalog.ScanAsync(ct);
    var identities = initial.Notes.ToDictionary(note => note.RelativePath, note => note.Id, StringComparer.OrdinalIgnoreCase);
    var sent = await new VaultSync(vault, markdownState, remote, noteIdentities: identities).RunAsync(ct);
    SetIdentities(catalog, sent.Notes!);
    var notes = (await catalog.ScanAsync(ct)).Notes;
    var note = notes.Single();
    var session = new RoamingSession(roamingFile, random: () => 0);
    var first = (await session.NextAsync(notes, cancellationToken: ct))!;
    var current = (await session.NextAsync(notes, first.Id, true, ct))!;
    await state.EnqueueAsync(await session.ExportSyncAsync(identity.DeviceId, local => catalog.CloudNoteIds.GetValueOrDefault(local), ct), ct);
    var cloudId = catalog.CloudNoteIds[note.Id];
    var readingKey = "reading/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.ReadingUrl)));
    await state.EnqueueAsync([
        Entry("preference/theme", new { name = "pink" }),
        Entry(readingKey, new { url = fixture.ReadingUrl, progress = .25, anchor = "p:3", title = "Roundtrip article" }),
        Entry("note-reading/" + cloudId, new { noteId = cloudId, progress = .25, anchor = "p:3", title = "apple" })
    ], ct);
    await state.RunAsync(ct);
    fixture = fixture with { NoteId = cloudId, ExposureId = current.Id.ToString("D"), Day = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") };
    await File.WriteAllTextAsync(fixtureFile, JsonSerializer.Serialize(fixture, json), ct);
    Console.WriteLine("PASS Windows seed uploaded Markdown, two exposures, one remembered result, theme and reading positions.");
}
else
{
    var received = await new VaultSync(vault, markdownState, remote).RunAsync(ct);
    SetIdentities(catalog, received.Notes!);
    var notes = (await catalog.ScanAsync(ct)).Notes;
    var note = notes.Single(note => catalog.CloudNoteIds.GetValueOrDefault(note.Id) == fixture.NoteId);
    Check(note.Markdown.Contains("Edited on the phone", StringComparison.Ordinal), "Windows pulled the mobile Markdown edit");
    var entries = await state.RunAsync(ct);
    var session = new RoamingSession(roamingFile, random: () => 0);
    var localIds = catalog.CloudNoteIds.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    await session.MergeSyncAsync(entries, cloud => localIds.GetValueOrDefault(cloud), ct);
    var current = await session.RestoreCurrentAsync(notes, ct);
    Check(current is not null && current.Id.ToString("D") != fixture.ExposureId, "Windows restored the new mobile exposure without another draw");
    var count = session.GetStatistics(notes).DayCounts.Sum(day => day.Count);
    Check(count >= 3, "Windows merged mobile shown activity");
    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(roamingFile, ct));
    var record = saved.RootElement.GetProperty("Records").GetProperty(note.Id);
    Check(record.GetProperty("Remembered").GetInt32() >= 1 && record.GetProperty("Skipped").GetInt32() >= 1,
        "Windows kept its remembered result and merged the mobile skip exactly by exposure");
    Check(entries.Single(entry => entry.Key == "preference/theme").Value.GetProperty("name").GetString() == "mono", "Windows pulled the mobile theme");
    var readingKey = "reading/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.ReadingUrl)));
    Check(entries.Single(entry => entry.Key == readingKey).Value.GetProperty("progress").GetDouble() == .75, "Windows pulled mobile article reading progress");
    Check(entries.Single(entry => entry.Key == "note-reading/" + fixture.NoteId).Value.GetProperty("progress").GetDouble() == .75,
        "Windows pulled mobile note reading progress");
    var before = await File.ReadAllTextAsync(roamingFile, ct);
    await session.MergeSyncAsync(await state.RunAsync(ct), cloud => localIds.GetValueOrDefault(cloud), ct);
    Check(session.GetStatistics(notes).DayCounts.Sum(day => day.Count) == count, "Repeated state pulls did not count another exposure");
    Console.WriteLine("PASS Windows and mobile account roundtrip.");
}

static SyncStateEntry Entry(string key, object value) => new(key, JsonSerializer.SerializeToElement(value));
static void SetIdentities(LibraryCatalog catalog, IReadOnlyList<SyncedNote> notes) => catalog.SetCloudIdentities(
    notes.ToDictionary(note => note.Path, note => note.Id, StringComparer.OrdinalIgnoreCase),
    notes.ToDictionary(note => note.Path, note => note.Hash, StringComparer.OrdinalIgnoreCase));
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
sealed record Fixture(string Server, string Username, string Password, string VaultId, string NoteId, string ExposureId,
    string Day, string OriginalMarkdown, string ReadingUrl);
