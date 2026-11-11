using System.Text.Json;
using VocabCollector.Core.Library;

namespace Tuci.Sync;

internal sealed record PracticeSource(string VaultId, string NoteId, long Version, string Word, IReadOnlyList<PracticeSense> Senses);
internal sealed record PracticeStoreResult(int Added, bool Stale);
internal sealed record PracticeBankRequest(string[] NoteIds);

internal sealed partial class Database
{
    private static readonly JsonSerializerOptions PracticeJson = new(JsonSerializerDefaults.Web);
    private const int PracticeResponseBytes = 2 * 1024 * 1024;

    public long PracticeVaultCount()
    {
        using var db = Open();
        return Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM vaults"));
    }

    public IReadOnlyList<PracticeSource> PracticeSources()
    {
        using var db = Open();
        using var command = db.Cmd("""
            SELECT h.vault,c.note,c.ver,c.path,c.body FROM heads h
            JOIN changes c ON c.vault=h.vault AND c.seq=h.seq
            WHERE h.live=1 AND c.deleted=0 ORDER BY h.vault,c.note
            """);
        using var rows = command.ExecuteReader();
        var result = new List<PracticeSource>();
        while (rows.Read())
        {
            var head = new Change(0, rows.GetString(1), rows.GetInt64(2), rows.GetString(3), rows.GetString(4), false, null);
            if (PracticeSourceFor(rows.GetString(0), head) is { } source) result.Add(source);
        }
        return result;
    }

    public IReadOnlyList<PracticeContext> ExistingPractice(PracticeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var db = Open(); db.StartRead();
        var current = PracticeSourceFor(source.VaultId, Head(db, source.VaultId, source.NoteId));
        return SamePracticeSource(source, current) ? ValidPractice(db, current!) : [];
    }

    public PracticeStoreResult StorePractice(PracticeSource expected, IReadOnlyList<PracticeContext> newContexts)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(newContexts);
        if (expected.Senses is null || expected.Senses.Count is < 1 or > 128 ||
            expected.Senses.Any(sense => sense is null || sense.Word != expected.Word || string.IsNullOrWhiteSpace(sense.Id)) ||
            expected.Senses.Select(sense => sense.Id).Distinct(StringComparer.Ordinal).Count() != expected.Senses.Count)
            throw new ArgumentException("义项快照无效。");
        var senses = expected.Senses.ToDictionary(sense => sense.Id, StringComparer.Ordinal);
        var supplied = new Dictionary<string, PracticeContext>(StringComparer.Ordinal);
        // Validate generated text before taking the database write lock.
        foreach (var item in newContexts)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.SenseId) || !senses.TryGetValue(item.SenseId, out var sense) || supplied.ContainsKey(item.SenseId))
                throw new ArgumentException("生成语境包含未知或重复义项。");
            var bound = PracticeQuestionBank.WithContexts([sense], [item])[0];
            supplied.Add(item.SenseId, new(item.SenseId, bound.Context, bound.ContextTranslation, bound.AnswerForm));
        }
        using var db = Open(); db.StartWrite();
        var current = PracticeSourceFor(expected.VaultId, Head(db, expected.VaultId, expected.NoteId));
        if (!SamePracticeSource(expected, current)) return new(0, true);
        var existing = ValidPractice(db, current!).ToDictionary(context => context.SenseId, StringComparer.Ordinal);
        var missing = current!.Senses.Where(sense => !existing.ContainsKey(sense.Id)).ToArray();
        if (missing.Any(sense => !supplied.ContainsKey(sense.Id)))
            throw new ArgumentException("尚未补齐全部缺失义项，未写入任何语境。");
        var added = 0;
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var sense in missing)
        {
            var context = supplied[sense.Id];
            added += db.Execute("""
                INSERT INTO practice_contexts(vault,note,word,sense,meaning,english,chinese,created,answer_form)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)
                ON CONFLICT(vault,note,word,sense) DO UPDATE SET
                    meaning=excluded.meaning,english=excluded.english,chinese=excluded.chinese,created=excluded.created,answer_form=excluded.answer_form
                """, current.VaultId, current.NoteId, current.Word, sense.Id, sense.Meaning, context.English, context.Chinese, created, context.AnswerForm);
        }
        // A competing job may have supplied some of the proposed rows already.
        // Only still-missing/invalid rows are touched, preserving valid text/time.
        db.Commit();
        return new(added, false);
    }

    public CloudPracticeBankPage PracticeBanks(string vault, IReadOnlyList<string> noteIds)
    {
        if (noteIds is null || noteIds.Count > 32 || noteIds.Any(id => !Wire.IsId(id)))
            throw new RequestFault(400, "请提供最多32个有效笔记编号。");
        var banks = new List<CloudPracticeBank>();
        if (noteIds.Count == 0) return new(banks);
        using var db = Open(); db.StartRead();
        foreach (var id in noteIds.Distinct(StringComparer.Ordinal))
        {
            var source = PracticeSourceFor(vault, Head(db, vault, id));
            if (source is null) continue;
            var contexts = ValidPractice(db, source);
            if (contexts.Count != source.Senses.Count) continue;
            var bank = new CloudPracticeBank(source.NoteId, source.Word, contexts);
            // The default encoder is conservative relative to ASP.NET's UTF-8
            // encoder, including when definitions contain long Chinese text.
            var candidate = new CloudPracticeBankPage(banks.Append(bank).ToArray());
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, PracticeJson).Length <= PracticeResponseBytes) banks.Add(bank);
        }
        return new(banks);
    }

    private static PracticeSource? PracticeSourceFor(string vault, Change? head)
    {
        if (head is null || head.Deleted) return null;
        // Sync paths are already relative to the word root, not the outer vault.
        var note = new LibraryNote(head.NoteId, head.Path, head.Path, head.Markdown, DateTimeOffset.UnixEpoch);
        if (!note.IsWord) return null;
        var senses = PracticeQuestionBank.Build(note);
        if (senses.Count is < 1 or > 128 || senses.Any(sense => sense.Meaning.Length > 8000)) return null;
        return new(vault, head.NoteId, head.Version, senses[0].Word, senses);
    }

    private static bool SamePracticeSource(PracticeSource expected, PracticeSource? current) =>
        current is not null && expected.VaultId == current.VaultId && expected.NoteId == current.NoteId &&
        expected.Version == current.Version && expected.Word == current.Word && expected.Senses is not null &&
        expected.Senses.OrderBy(sense => sense.Id, StringComparer.Ordinal)
            .SequenceEqual(current.Senses.OrderBy(sense => sense.Id, StringComparer.Ordinal));

    private static IReadOnlyList<PracticeContext> ValidPractice(DbSession db, PracticeSource source)
    {
        var senses = source.Senses.ToDictionary(sense => sense.Id, StringComparer.Ordinal);
        var valid = new Dictionary<string, PracticeContext>(StringComparer.Ordinal);
        using var command = db.Cmd("""
            SELECT sense,meaning,english,chinese,answer_form FROM practice_contexts
            WHERE vault=$p0 AND note=$p1 AND word=$p2
            """, source.VaultId, source.NoteId, source.Word);
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var id = rows.GetString(0);
            if (!senses.TryGetValue(id, out var sense) || rows.GetString(1) != sense.Meaning) continue;
            try
            {
                var bound = PracticeQuestionBank.WithContexts([sense], [new(id, rows.GetString(2), rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4))])[0];
                valid[id] = new(id, bound.Context, bound.ContextTranslation, bound.AnswerForm);
            }
            catch (ArgumentException) { /* Invalid legacy/operator rows remain unavailable until safely repaired. */ }
        }
        return source.Senses.Where(sense => valid.ContainsKey(sense.Id)).Select(sense => valid[sense.Id]).ToArray();
    }
}
