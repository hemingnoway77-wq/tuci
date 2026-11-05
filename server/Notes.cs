using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tuci.Sync;

internal sealed partial class Database
{
    private const string Columns = "c.seq,c.note,c.ver,c.path,c.body,c.deleted,c.regions";
    private const string Heads = " FROM heads h JOIN changes c ON c.vault=h.vault AND c.seq=h.seq WHERE h.vault=$p0 AND ";
    private static Change Read(SqliteDataReader row)
        => new(row.GetInt64(0), row.GetString(1), row.GetInt64(2), row.GetString(3), row.GetString(4),
            row.GetBoolean(5), row.IsDBNull(6) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(row.GetString(6)));
    private static Change? Head(DbSession db, string vault, string idOrPath, bool byPath = false)
    {
        string filter = byPath ? "h.live=1 AND h.pathkey=$p1" : "h.note=$p1";
        using var cmd = db.Cmd("SELECT " + Columns + Heads + filter, vault, byPath ? idOrPath.ToUpperInvariant() : idOrPath);
        using var row = cmd.ExecuteReader();
        return row.Read() ? Read(row) : null;
    }
    public ChangePage Changes(string vault, long after)
    {
        if (after < 0) throw new RequestFault(400, "游标无效。");
        using var db = Open();
        if (after > Convert.ToInt64(db.Scalar("SELECT seq FROM vaults WHERE id=$p0", vault)))
            throw new RequestFault(409, "游标超出该词库历史，请核对备份后重新关联。");
        using var cmd = db.Cmd("SELECT " + Columns + " FROM changes c WHERE vault=$p0 AND seq>$p1 ORDER BY seq LIMIT 65", vault, after);
        using var rows = cmd.ExecuteReader();
        List<Change> page = []; int bytes = 0;
        while (rows.Read())
        {
            if (page.Count == 64) return new(page, page[^1].Seq, true);
            var change = Read(rows);
            bytes += Encoding.UTF8.GetByteCount(change.Markdown);
            if (bytes > 2 * Wire.NoteBytes && page.Count > 0) return new(page, page[^1].Seq, true);
            page.Add(change);
        }
        return new(page, page.Count == 0 ? after : page[^1].Seq, false);
    }
    public Applied Apply(string vault, Operation raw)
    {
        var op = Wire.Validate(raw);
        string digest = Wire.OpDigest(op);
        using var db = Open();
        db.StartWrite();
        using (var cmd = db.Cmd("SELECT digest,result FROM receipts WHERE vault=$p0 AND op=$p1", vault, op.OperationId))
        using (var row = cmd.ExecuteReader())
        {
            if (row.Read())
            {
                if (row.GetString(0) != digest) throw new RequestFault(409, "操作标识已用于另一项修改。");
                return JsonSerializer.Deserialize<Applied>(row.GetString(1))!;
            }
        }
        var current = Head(db, vault, op.NoteId);
        Applied result;
        if (op.Deleted)
        {
            if (current is null || op.BaseVersion == 0) throw new RequestFault(409, "无法删除未经同步的笔记。");
            if (current.Deleted) result = Echo(db, vault, current, false);
            else if (current.Version != op.BaseVersion) result = Echo(db, vault, current, true);
            else result = Write(db, vault, current with { Version = current.Version + 1, Markdown = "", Deleted = true, Regions = null }, false);
        }
        else result = Save(db, vault, op, current);
        db.Execute("INSERT INTO receipts VALUES($p0,$p1,$p2,$p3)", vault, op.OperationId, digest, JsonSerializer.Serialize(result));
        db.Commit();
        return result;
    }
    private static Applied Save(DbSession db, string vault, Operation op, Change? current)
    {
        if (current is null && op.BaseVersion != 0)
            throw new RequestFault(409, "原笔记版本不在此词库，请核对绑定。");
        var other = Head(db, vault, op.Path, byPath: true);
        bool occupied = other is not null && other.NoteId != op.NoteId;
        if (current is null && occupied && other!.Markdown == op.Markdown)
            return Echo(db, vault, other, false);
        if (current is { Deleted: false } && current.Path == op.Path && current.Markdown == op.Markdown &&
            (current.Version != op.BaseVersion || Wire.SameRegions(current.Regions, op.Regions)))
            return Echo(db, vault, current, false);
        bool stale = current is not null && current.Version != op.BaseVersion;
        bool hierarchyCollision = CrossesFile(db, vault, op.Path, current?.NoteId);
        if (!stale && !occupied && !hierarchyCollision)
            return Write(db, vault, new(0, op.NoteId, (current?.Version ?? 0) + 1, op.Path, op.Markdown, false, op.Regions), false);

        // Republish the authoritative originals as well as the preserved local edit. A client that
        // already pulled the originals before going offline must be able to materialize both.
        if (current is not null) Echo(db, vault, current, true);
        if (occupied) Echo(db, vault, other!, true);
        string conflictPath = ConflictPath(db, vault, op.Path);
        return Write(db, vault, new(0, Wire.Id(), 1, conflictPath, op.Markdown, false, op.Regions), true);
    }
    private static bool CrossesFile(DbSession db, string vault, string path, string? exceptId)
    {
        string key = path.ToUpperInvariant();
        return db.Scalar("""
            SELECT 1 FROM heads WHERE vault=$p0 AND live=1 AND note!=COALESCE($p2,'')
            AND (substr(pathkey,1,length($p1)+1)=$p1||'/' OR substr($p1,1,length(pathkey)+1)=pathkey||'/') LIMIT 1
            """, vault, key, exceptId) is not null;
    }
    private static string ConflictPath(DbSession db, string vault, string path)
    {
        var parent = path[..(path.LastIndexOf('/') + 1)];
        var basename = path[parent.Length..^3];
        // Retain a recognizable name within UTF-8 path limits, even for imported long Unicode names.
        var prefix = string.Concat(basename.EnumerateRunes().Take(36).Select(x => x.ToString()));
        while (true)
        {
            var name = prefix + " (冲突 " + Wire.Secret(5) + ").md";
            var candidate = parent + name;
            if (candidate.Length > 1024 || CrossesFile(db, vault, candidate, null)) candidate = "同步冲突/" + name;
            if (Head(db, vault, candidate, byPath: true) is null && !CrossesFile(db, vault, candidate, null))
                return Wire.ValidatePath(candidate);
        }
    }
    private static Applied Echo(DbSession db, string vault, Change state, bool conflict) => Write(db, vault, state, conflict);
    private static Applied Write(DbSession db, string vault, Change state, bool conflict)
    {
        long seq = Convert.ToInt64(db.Scalar("UPDATE vaults SET seq=seq+1 WHERE id=$p0 RETURNING seq", vault));
        db.Execute("INSERT INTO changes VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)", vault, seq, state.NoteId,
            state.Version, state.Path, state.Markdown, state.Deleted, state.Regions is null ? null : JsonSerializer.Serialize(state.Regions));
        db.Execute("""
            INSERT INTO heads VALUES($p0,$p1,$p2,$p3,$p4)
            ON CONFLICT(vault,note) DO UPDATE SET seq=excluded.seq,pathkey=excluded.pathkey,live=excluded.live
            """, vault, state.NoteId, seq, state.Path.ToUpperInvariant(), !state.Deleted);
        return new(state.NoteId, state.Version, state.Path, conflict);
    }
}
