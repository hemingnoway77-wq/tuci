using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Library;

namespace Tuci.Sync;

/// <summary>Operator-only, resumable preparation. Never called by an authenticated user endpoint.</summary>
internal static class PracticeBackfill
{
    private const int MaxJobBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static async Task<object> RunAsync(Database db, string[] args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();
        if (args is ["practice-plan"])
        {
            var plan = Inventory(db);
            return new { mode = "plan", vaults = VaultCount(db), wordNotes = plan.Words,
                senses = plan.Senses, validSenses = plan.Valid, missingWords = plan.MissingWords,
                missingSenses = plan.Missing, estimatedRequests = plan.Requests };
        }
        var input = Arguments(args);
        var options = await Configuration(input.Config, ct);
        PrepareDirectory(input.State);
        await using var jobLock = LockJob(input.State);
        var job = await ReadJob(input.State, ct);
        var before = Inventory(db);
        var calls = 0; var memoReuses = 0; long added = 0; var failed = 0; var stale = 0;
        var cancelled = false; string? stopped = null;
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var failedKeys = new HashSet<string>(StringComparer.Ordinal);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(90) };
        var generator = new OpenAiPracticeContextGenerator(client);
        try
        {
            foreach (var source in before.Sources.OrderBy(source => source.VaultId, StringComparer.Ordinal)
                .ThenBy(source => source.NoteId, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                PracticeSense[] missing;
                try { missing = Missing(db, source); }
                catch (Exception) { Fail("store_read"); continue; }
                if (missing.Length == 0) continue;
                var contexts = new List<PracticeContext>();
                var needed = new List<PracticeSense>();
                foreach (var sense in missing)
                {
                    if (!job.Generated.TryGetValue(Key(source.Word, [sense]), out var memo)) { needed.Add(sense); continue; }
                    // Even a matching receipt digest is not trusted without validating its content.
                    try { contexts.AddRange(Validate([sense], memo)); }
                    catch (ArgumentException) { throw new IOException("practice_state_invalid"); }
                }
                if (contexts.Count > 0) memoReuses++;
                var key = Key(source.Word, needed);
                if (failedKeys.Contains(key)) { Fail("generation"); continue; }
                if (needed.Count > 0)
                {
                    if (calls >= input.Limit) continue;
                    calls++;
                    PracticeContext[] generated;
                    try
                    {
                        generated = Validate(needed, await generator.GenerateAsync(source.Word, needed, options, ct));
                        ct.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        failedKeys.Add(key); Fail("generation"); continue;
                    }
                    // Pay once, checkpoint once, then CAS into each separate vault. A stale
                    // note or failed database write must not discard a successful generation.
                    var entries = new Dictionary<string, PracticeContext[]>(job.Generated, StringComparer.Ordinal);
                    foreach (var sense in needed)
                        entries[Key(source.Word, [sense])] = [generated.Single(context => context.SenseId == sense.Id)];
                    var candidate = new JobState(1, entries);
                    try { await WriteJob(input.State, candidate, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception)
                    { Fail("checkpoint_write"); stopped = "checkpoint_write"; break; }
                    job = candidate;
                    contexts.AddRange(generated);
                }
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stored = db.StorePractice(source, contexts);
                    if (stored.Stale) stale++;
                    else added += stored.Added;
                }
                catch (Exception) { Fail("store"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        var after = Inventory(db);
        return new { mode = "backfill", vaults = VaultCount(db), wordNotes = after.Words,
            senses = after.Senses, existingSenses = before.Valid, calls, memoReuses, addedSenses = added,
            failedWords = failed, staleWords = stale, cancelled, stoppedCode = stopped,
            validSenses = after.Valid, missingWords = after.MissingWords, missingSenses = after.Missing,
            estimatedRequests = after.Requests, failureCodes = failures };

        void Fail(string code) { failed++; failures[code] = failures.GetValueOrDefault(code) + 1; }
    }

    private static Coverage Inventory(Database db)
    {
        try
        {
            var sources = db.PracticeSources();
            long senses = 0, valid = 0, words = 0, missingCount = 0;
            var requests = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var missing = Missing(db, source);
                senses += source.Senses.Count; valid += source.Senses.Count - missing.Length;
                if (missing.Length == 0) continue;
                words++; missingCount += missing.Length; requests.Add(Key(source.Word, missing));
            }
            return new(sources, sources.Count, senses, valid, words, missingCount, requests.Count);
        }
        catch (Exception) { throw new IOException("practice_inventory_failed"); }
    }

    private static PracticeSense[] Missing(Database db, PracticeSource source)
    {
        var present = db.ExistingPractice(source).Select(context => context.SenseId).ToHashSet(StringComparer.Ordinal);
        return source.Senses.Where(sense => !present.Contains(sense.Id)).OrderBy(sense => sense.Id, StringComparer.Ordinal).ToArray();
    }

    private static long VaultCount(Database db)
    {
        try { return db.PracticeVaultCount(); }
        catch (Exception) { throw new IOException("practice_inventory_failed"); }
    }

    private static PracticeContext[] Validate(IReadOnlyList<PracticeSense> senses, IReadOnlyList<PracticeContext> contexts) =>
        PracticeQuestionBank.WithContexts(senses, contexts)
            .Select(sense => new PracticeContext(sense.Id, sense.Context, sense.ContextTranslation, sense.AnswerForm)).ToArray();

    private static string Key(string word, IReadOnlyList<PracticeSense> senses) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, word = WordInput.Normalize(word),
            senses = senses.OrderBy(sense => sense.Id, StringComparer.Ordinal).Select(sense => new
            { id = sense.Id, meaning = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sense.Meaning.Normalize(NormalizationForm.FormC)))) })
        })));

    private static Input Arguments(string[] args)
    {
        const string usage = "practice-plan | practice-backfill --config <secure-json> --state <job-json> [--limit <calls>]";
        if (args.Length < 5 || args[0] != "practice-backfill" || args.Length % 2 != 1) throw new ArgumentException(usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
            if (args[index] is not ("--config" or "--state" or "--limit") ||
                string.IsNullOrWhiteSpace(args[index + 1]) || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException(usage);
        if (!values.TryGetValue("--config", out var config) || !values.TryGetValue("--state", out var state)) throw new ArgumentException(usage);
        var limit = int.MaxValue;
        if (values.TryGetValue("--limit", out var text) &&
            (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)) throw new ArgumentException(usage);
        try
        {
            config = Path.GetFullPath(config); state = Path.GetFullPath(state);
            if (string.Equals(config, state, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException();
            return new(config, state, limit);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new ArgumentException("practice_paths_invalid"); }
    }

    private static async Task<ApiOptions> Configuration(string path, CancellationToken ct)
    {
        try
        {
            EnsureNoLinks(path);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~OwnerFile) != 0) throw new IOException();
            if (new FileInfo(path).Length > 64 * 1024) throw new IOException();
            var options = JsonSerializer.Deserialize<ApiOptions>(await File.ReadAllBytesAsync(path, ct), Json) ?? throw new JsonException();
            SettingsValidation.ValidateApi(options); return options;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new ArgumentException("practice_config_invalid"); }
    }

    private static async Task<JobState> ReadJob(string path, CancellationToken ct)
    {
        try
        {
            EnsureNoLinks(path);
            if (!File.Exists(path)) return new(1, new(StringComparer.Ordinal));
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~OwnerFile) != 0) throw new IOException();
            if (new FileInfo(path).Length > MaxJobBytes) throw new IOException();
            var job = JsonSerializer.Deserialize<JobState>(await File.ReadAllBytesAsync(path, ct), Json);
            if (job is null || job.FormatVersion != 1 || job.Generated is null || job.Generated.Any(pair =>
                pair.Key.Length != 64 || !pair.Key.All(Uri.IsHexDigit) || pair.Value is null || pair.Value.Length is < 1 or > 128 ||
                pair.Value.Any(context => context is null || string.IsNullOrWhiteSpace(context.SenseId) || context.SenseId.Length > 256 ||
                    string.IsNullOrWhiteSpace(context.English) || context.English.Length > 2000 || string.IsNullOrWhiteSpace(context.Chinese) || context.Chinese.Length > 2000) ||
                pair.Value.Select(context => context.SenseId).Distinct(StringComparer.Ordinal).Count() != pair.Value.Length)) throw new JsonException();
            return job;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new IOException("practice_state_invalid"); }
    }

    private static void PrepareDirectory(string path)
    {
        try
        {
            EnsureNoLinks(path);
            var directory = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            EnsureNoLinks(path);
        }
        catch (Exception) { throw new IOException("practice_state_unavailable"); }
    }

    private static FileStream LockJob(string path)
    {
        try
        {
            var lockPath = path + ".lock"; EnsureNoLinks(lockPath);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerFile;
            return new FileStream(lockPath, options);
        }
        catch (Exception) { throw new IOException("practice_job_busy"); }
    }

    private static async Task WriteJob(string path, JobState value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxJobBytes) throw new IOException("practice_state_full");
        EnsureNoLinks(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".practice-job-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerFile;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested(); EnsureNoLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("practice_path_link");
    }

    private sealed record Input(string Config, string State, int Limit);
    private sealed record JobState(int FormatVersion, Dictionary<string, PracticeContext[]> Generated);
    private sealed record Coverage(IReadOnlyList<PracticeSource> Sources, int Words, long Senses, long Valid,
        long MissingWords, long Missing, int Requests);
}
