using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tuci.Sync;

public sealed record StateOperation(string OperationId, string Key, JsonElement Value, bool Deleted = false);
public sealed record StateChange(long Seq, string Key, JsonElement Value, bool Deleted);
public sealed record StatePage(List<StateChange> Changes, long Cursor, bool HasMore);
public sealed record StateReceipt(string Key, long Seq);

internal sealed partial class Database
{
    private static void ValidateState(StateOperation op)
    {
        if (!Wire.IsId(op.OperationId) || op.Key is null || op.Key.Length > 300 ||
            op.Value.ValueKind is JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(op.Value.GetRawText()) > 16 * 1024)
            throw new RequestFault(400, "学习状态格式无效或过长。");
        var parts = op.Key.Split('/');
        bool eventKey = parts is ["event", var exposure, "shown" or "completed"] && Guid.TryParse(exposure, out _);
        bool legacyKey = parts is ["legacy", var source, var note, var day] && Wire.IsId(source) && Wire.IsId(note) && IsDay(day);
        bool currentKey = op.Key == "roaming/current";
        bool themeKey = op.Key == "preference/theme";
        bool readingKey = parts is ["reading", { Length:64 } hash] && hash.All(Uri.IsHexDigit);
        bool noteReadingKey = parts is ["note-reading", var readingNote] && Wire.IsId(readingNote);
        if (!eventKey && !legacyKey && !currentKey && !themeKey && !readingKey && !noteReadingKey)
            throw new RequestFault(400, "不支持的学习状态类型。");
        if (op.Deleted)
        {
            if (eventKey || legacyKey) throw new RequestFault(400, "复习记录不能被覆盖或删除。");
            return;
        }
        if (op.Value.ValueKind != JsonValueKind.Object) throw new RequestFault(400, "学习状态必须是对象。");
        string? Text(string name) => op.Value.TryGetProperty(name, out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() : null;
        bool Number(string name, double min, double max) => op.Value.TryGetProperty(name, out var value) && value.ValueKind==JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n>=min && n<=max;
        bool Date(string name) => op.Value.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String && value.TryGetDateTimeOffset(out _);
        bool Count(string name) => op.Value.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out var n) && n>=0 && n<=1_000_000;
        bool OptionalText(string name,int max) => !op.Value.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null || value.ValueKind==JsonValueKind.String && value.GetString()!.Length<=max;
        bool good = true;
        if (eventKey || legacyKey || currentKey || noteReadingKey) good &= Wire.IsId(Text("noteId"));
        if (eventKey || currentKey) good &= Date("at");
        if (eventKey || legacyKey) good &= IsDay(Text("day"));
        if (eventKey && parts[2]=="completed") good &= op.Value.TryGetProperty("remembered", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (currentKey) good &= Guid.TryParse(Text("exposureId"), out _);
        if (eventKey && parts[2]=="shown" || currentKey) good &= Number("weight",double.Epsilon,1e9) && Number("weightLevel",0,1);
        if (legacyKey) good &= parts[2]==Text("noteId") && parts[3]==Text("day") && Count("remembered") && Count("skipped") && Count("shown") &&
            (!op.Value.TryGetProperty("lastShown",out var last) || last.ValueKind==JsonValueKind.Null || Date("lastShown"));
        if (themeKey) good &= Text("name") is "pink" or "mono" or "warmNeutral";
        if (noteReadingKey) good &= parts[1]==Text("noteId") && Number("progress",0,1);
        if (noteReadingKey || readingKey) good &= OptionalText("anchor",2048) && OptionalText("title",512);
        if (readingKey) good &= Uri.TryCreate(Text("url"),UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.UserInfo.Length==0 &&
            op.Key[8..].Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Text("url")!))), StringComparison.OrdinalIgnoreCase) && Number("progress",0,1);
        if (!good) throw new RequestFault(400, "学习状态内容无效。");
        string[] allowed = themeKey ? ["name"] : noteReadingKey ? ["noteId","progress","anchor","title"] : readingKey ? ["url","progress","anchor","title"] : legacyKey ? ["noteId","day","remembered","skipped","shown","lastShown"] :
            currentKey ? ["noteId","exposureId","at","weight","weightLevel"] : parts[2]=="shown" ? ["noteId","at","day","weight","weightLevel"] : ["noteId","at","day","remembered"];
        if (op.Value.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new RequestFault(400, "学习状态包含不支持的字段。");
    }
    private static bool IsDay(string? value) => DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
    public StateReceipt ApplyState(string vault, StateOperation op)
    {
        ValidateState(op);
        string digest = Wire.Digest(JsonSerializer.Serialize(op));
        using var db=Open(); db.StartWrite();
        using (var cmd=db.Cmd("SELECT digest,result FROM state_receipts WHERE vault=$p0 AND op=$p1",vault,op.OperationId))
        using (var row=cmd.ExecuteReader())
            if (row.Read())
            {
                if (row.GetString(0)!=digest) throw new RequestFault(409,"操作标识已用于另一项修改。");
                return JsonSerializer.Deserialize<StateReceipt>(row.GetString(1))!;
            }
        if (!op.Deleted && op.Value.TryGetProperty("noteId", out var note) &&
            db.Scalar("SELECT 1 FROM heads WHERE vault=$p0 AND note=$p1",vault,note.GetString()) is null)
            throw new RequestFault(409,"请先同步这张词卡，再同步学习记录。");
        var prior = db.Scalar("SELECT seq FROM state_heads WHERE vault=$p0 AND key=$p1",vault,op.Key);
        StateReceipt result;
        if (prior is not null && (op.Key.StartsWith("event/",StringComparison.Ordinal) || op.Key.StartsWith("legacy/",StringComparison.Ordinal)))
            result=new(op.Key,Convert.ToInt64(prior));
        else
        {
            db.Execute("INSERT INTO state_sequences(vault) VALUES($p0) ON CONFLICT DO NOTHING",vault);
            long seq=Convert.ToInt64(db.Scalar("UPDATE state_sequences SET seq=seq+1 WHERE vault=$p0 RETURNING seq",vault));
            db.Execute("INSERT INTO state_changes VALUES($p0,$p1,$p2,$p3,$p4)",vault,seq,op.Key,op.Value.GetRawText(),op.Deleted);
            db.Execute("INSERT INTO state_heads VALUES($p0,$p1,$p2) ON CONFLICT(vault,key) DO UPDATE SET seq=excluded.seq",vault,op.Key,seq);
            result=new(op.Key,seq);
        }
        db.Execute("INSERT INTO state_receipts VALUES($p0,$p1,$p2,$p3)",vault,op.OperationId,digest,JsonSerializer.Serialize(result));
        db.Commit(); return result;
    }
    public StatePage StateChanges(string vault,long after)
    {
        if (after<0) throw new RequestFault(400,"状态游标无效。");
        using var db=Open();
        long max=Convert.ToInt64(db.Scalar("SELECT seq FROM state_sequences WHERE vault=$p0",vault) ?? 0L);
        if (after>max) throw new RequestFault(409,"状态历史已恢复，请重新合并本机记录。");
        using var cmd=db.Cmd("SELECT seq,key,value,deleted FROM state_changes WHERE vault=$p0 AND seq>$p1 ORDER BY seq LIMIT 129",vault,after);
        using var rows=cmd.ExecuteReader(); List<StateChange> page=[];
        while(rows.Read())
        {
            if(page.Count==128) return new(page,page[^1].Seq,true);
            page.Add(new(rows.GetInt64(0),rows.GetString(1),JsonSerializer.Deserialize<JsonElement>(rows.GetString(2)),rows.GetBoolean(3)));
        }
        return new(page,page.Count==0?after:page[^1].Seq,false);
    }
}
