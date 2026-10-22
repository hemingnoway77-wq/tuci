namespace VocabCollector.Core;

public static class RootInput
{
    public static string Normalize(string raw)
    {
        var root = (raw ?? "").Trim().Trim('-');
        if (root.Length is < 1 or > 32 || root.Any(c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z')))
            throw new ArgumentException("词根必须由 1 至 32 个英文字母组成，可在两端使用连字符。");
        return root.ToLowerInvariant();
    }
}
