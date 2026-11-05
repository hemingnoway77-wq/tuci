using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using VocabCollector.Core.Library;

namespace VocabCollector.Desktop.Learning;

public sealed record NotebookSearchHit(LibraryNote Note, string Excerpt);

public static class NotebookSearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static IReadOnlyList<NotebookSearchHit> Search(IReadOnlyList<LibraryNote> notes, string query)
    {
        if (TrySearch(notes, query, out var results, out var message)) return results;
        throw new ArgumentException(message, nameof(query));
    }

    public static bool TrySearch(IReadOnlyList<LibraryNote> notes, string query, out IReadOnlyList<NotebookSearchHit> results, out string message)
    {
        ArgumentNullException.ThrowIfNull(notes);
        results = [];
        message = "";
        try
        {
            var watch = Stopwatch.StartNew();
            var groups = Parse(query ?? "");
            var found = new List<NotebookSearchHit>();
            foreach (var note in notes)
            {
                if (watch.Elapsed > TimeSpan.FromMilliseconds(600)) throw new TimeoutException();
                var document = new SearchDocument(note.Title, note.RelativePath.Replace('\\', '/'), note.Body);
                var excerptAt = -1;
                var matches = groups.Count == 0;
                foreach (var group in groups)
                {
                    var groupMatches = true;
                    var groupExcerptAt = -1;
                    foreach (var term in group)
                    {
                        var match = term.Matches(document, out var bodyIndex);
                        if (term.Negated ? match : !match) { groupMatches = false; break; }
                        if (!term.Negated && bodyIndex >= 0 && groupExcerptAt < 0) groupExcerptAt = bodyIndex;
                    }
                    if (!groupMatches) continue;
                    matches = true;
                    excerptAt = groupExcerptAt;
                    break;
                }
                if (matches) found.Add(new(note, Excerpt(document.Body, excerptAt)));
            }
            results = found;
            return true;
        }
        catch (RegexMatchTimeoutException) { message = "正则表达式搜索超时。请缩小搜索范围，或使用更简单的表达式。"; }
        catch (TimeoutException) { message = "搜索超时。请增加关键词，或用 file:、path: 缩小搜索范围。"; }
        catch (ArgumentException error) { message = error.Message; }
        return false;
    }

    public static bool MatchFileName(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrEmpty(name)) return false;
        var position = 0;
        foreach (var character in query.Trim())
        {
            if (char.IsWhiteSpace(character)) continue;
            var found = name.IndexOf(character.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            position = found + 1;
        }
        return true;
    }

    private static List<List<SearchTerm>> Parse(string query)
    {
        if (query.Length > 4096) throw new ArgumentException("搜索内容过长，请减少关键词。最多支持 4096 个字符。");
        var groups = new List<List<SearchTerm>>();
        var group = new List<SearchTerm>();
        var index = 0;
        var terms = 0;
        while (index < query.Length)
        {
            while (index < query.Length && char.IsWhiteSpace(query[index])) index++;
            if (index >= query.Length) break;
            if (query.AsSpan(index).StartsWith("OR", StringComparison.Ordinal) && (index + 2 == query.Length || char.IsWhiteSpace(query[index + 2])))
            {
                if (group.Count == 0) throw new ArgumentException("OR 两侧都需要搜索内容。");
                groups.Add(group);
                group = [];
                index += 2;
                continue;
            }
            var negated = query[index] == '-';
            if (negated) index++;
            var field = SearchField.Any;
            foreach (var (prefix, value) in new[] { ("file:", SearchField.File), ("path:", SearchField.Path), ("content:", SearchField.Content) })
            {
                if (!query.AsSpan(index).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                index += prefix.Length;
                field = value;
                break;
            }
            if (index >= query.Length || char.IsWhiteSpace(query[index])) throw new ArgumentException("搜索内容不能为空；请在字段或排除符号后填写关键词。");
            Regex? expression = null;
            string text;
            if (query[index] == '"') text = ReadDelimited(query, ref index, '"', "引号未闭合，请补上结尾的双引号。", regex: false);
            else if (query[index] == '/')
            {
                text = ReadDelimited(query, ref index, '/', "正则表达式未闭合，请补上结尾的 /。", regex: true);
                try { expression = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
                catch (ArgumentException) { throw new ArgumentException("正则表达式无效，请检查括号、字符范围和转义符。"); }
            }
            else
            {
                var start = index;
                while (index < query.Length && !char.IsWhiteSpace(query[index])) index++;
                text = query[start..index];
            }
            if (text.Length == 0) throw new ArgumentException("搜索内容不能为空。");
            if (++terms > 64) throw new ArgumentException("搜索条件过多，请减少到 64 个以内。");
            group.Add(new(field, negated, text, expression));
        }
        if (group.Count > 0) groups.Add(group);
        else if (groups.Count > 0) throw new ArgumentException("OR 两侧都需要搜索内容。");
        return groups;
    }

    private static string ReadDelimited(string query, ref int index, char delimiter, string error, bool regex)
    {
        index++;
        var builder = new StringBuilder();
        while (index < query.Length)
        {
            var character = query[index++];
            if (character == delimiter)
            {
                if (index < query.Length && !char.IsWhiteSpace(query[index])) throw new ArgumentException("引号短语或正则表达式后请用空格分隔下一个条件。");
                return builder.ToString();
            }
            if (character == '\\' && index < query.Length)
            {
                var next = query[index++];
                if (regex || (next != '"' && next != '\\')) builder.Append('\\');
                builder.Append(next);
            }
            else builder.Append(character);
        }
        throw new ArgumentException(error);
    }

    private static string Excerpt(string body, int matchAt)
    {
        var start = Math.Max(0, matchAt - 70);
        var length = Math.Min(240, body.Length - start);
        var text = Regex.Replace(body.Substring(start, length), @"\s+", " ").Trim();
        return (start > 0 ? "…" : "") + text + (start + length < body.Length ? "…" : "");
    }

    private sealed record SearchDocument(string File, string Path, string Body);
    private enum SearchField { Any, File, Path, Content }
    private sealed record SearchTerm(SearchField Field, bool Negated, string Text, Regex? Expression)
    {
        public bool Matches(SearchDocument document, out int bodyIndex)
        {
            bodyIndex = -1;
            if (Field == SearchField.File) return Index(document.File) >= 0;
            if (Field == SearchField.Path) return Index(document.Path) >= 0;
            bodyIndex = Index(document.Body);
            return bodyIndex >= 0 || (Field == SearchField.Any && (Index(document.File) >= 0 || Index(document.Path) >= 0));
        }
        private int Index(string value) => Expression is null ? value.IndexOf(Text, StringComparison.OrdinalIgnoreCase) : Expression.Match(value) is { Success: true } match ? match.Index : -1;
    }
}
