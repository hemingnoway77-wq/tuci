namespace VocabCollector.Core.Ai;
public static class ChatEndpoint
{
    public static Uri Resolve(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("API 地址须为 HTTP 或 HTTPS，不含账户、查询参数或片段。");
        var path = uri.AbsoluteUri.TrimEnd('/');
        return new Uri(path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? path : path + "/chat/completions");
    }
}
