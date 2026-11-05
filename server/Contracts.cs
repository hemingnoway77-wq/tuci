using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tuci.Sync;

public sealed record RedeemRequest(string Code, string DeviceName);
public sealed record DeviceGrant(string Token, string VaultId);
public sealed record Operation(string OperationId, string NoteId, long BaseVersion, string Path,
    string Markdown, bool Deleted, Dictionary<string, string>? Regions = null);
public sealed record Applied(string NoteId, long Version, string Path, bool Conflict);
public sealed record Change(long Seq, string NoteId, long Version, string Path, string Markdown,
    bool Deleted, Dictionary<string, string>? Regions);
public sealed record ChangePage(List<Change> Changes, long Cursor, bool HasMore);
public sealed record Device(string Id, string VaultId);
public sealed class RequestFault(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

internal static class Wire
{
    public const int NoteBytes = 1024 * 1024;
    private static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$"];
    public static string Id() => Guid.NewGuid().ToString("D");
    public static string Secret(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
    public static string Digest(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public static bool IsId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static Operation Validate(Operation op)
    {
        if (!IsId(op.OperationId) || !IsId(op.NoteId) || op.BaseVersion < 0)
            throw new RequestFault(400, "无效的操作标识或版本。");
        if (op.Markdown is null || Encoding.UTF8.GetByteCount(op.Markdown) > NoteBytes ||
            (op.Deleted && op.Markdown.Length != 0))
            throw new RequestFault(400, "笔记正文过长或删除内容无效。");
        string path = ValidatePath(op.Path);
        var regions = op.Regions;
        if (regions is { Count: > 0 })
        {
            if (op.Deleted || regions.Count > 4 || regions.Any(kv => kv.Key is not ("examples" or "roots" or "forms" or "relations")
                || kv.Value is not { Length: 64 } || !kv.Value.All(Uri.IsHexDigit)))
                throw new RequestFault(400, "笔记保护记录格式有误。");
            regions = regions.OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Value.ToLowerInvariant());
        }
        else regions = null;
        return op with { Path = path, Regions = regions };
    }
    public static string ValidatePath(string? input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 1024 || !input.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new RequestFault(400, "需要词库内的 Markdown 相对路径。");
        var path = input.Normalize(NormalizationForm.FormC);
        if (path.Length > 1024) throw new RequestFault(400, "笔记路径过长。");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (part.Length == 0 || Encoding.UTF8.GetByteCount(part) > 255 || part.StartsWith('.') ||
                part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => char.IsControl(c) || "\\:*?\"<>|".Contains(c))
                || ReservedNames.Contains(stem) || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(stem[3])))
                throw new RequestFault(400, "笔记路径包含不安全或不兼容的名称。");
        }
        return path;
    }
    public static bool SameRegions(Dictionary<string, string>? left, Dictionary<string, string>? right)
        => (left?.Count ?? 0) == (right?.Count ?? 0) &&
           (left is null || left.All(kv => right?.TryGetValue(kv.Key, out var value) == true && value == kv.Value));
    public static string OpDigest(Operation operation) => Digest(JsonSerializer.Serialize(operation));
}
