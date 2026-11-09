using System.Globalization;
using System.Text.RegularExpressions;

namespace VocabCollector.Core.Updates;

internal static partial class UpdatePolicy
{
    internal const int MaximumManifestBytes = 64 * 1024;
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    internal const long MaximumExtractedBytes = 1024L * 1024 * 1024;
    internal const int MaximumArchiveEntries = 10_000;

    internal static void ValidateManifestUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.EndsWith('/')
            || uri.AbsolutePath.Contains('%') || uri.OriginalString.Contains('\\') || HasDotSegments(uri))
            throw new InvalidDataException("更新清单地址无效。只允许无查询参数的 HTTP/HTTPS 文件地址。");
    }

    internal static Version ParseVersion(string? value)
    {
        if (value is null || !Version.TryParse(value, out var version) || version.Build < 0
            || version.Revision >= 0 || value != string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}"))
            throw new InvalidDataException("更新版本必须是三段数字，例如 1.1.0。");
        return version;
    }

    internal static void ValidateRelease(UpdateRelease release, Uri? manifestUri = null)
    {
        ParseVersion(release.Version);
        if (release.Platform != UpdateConfiguration.Platform)
            throw new InvalidDataException("更新包平台不受支持。");
        if (release.SizeBytes <= 0 || release.SizeBytes > MaximumPackageBytes)
            throw new InvalidDataException("更新包大小超出允许范围。");
        if (release.Sha256 is null || !Sha256Pattern().IsMatch(release.Sha256))
            throw new InvalidDataException("更新包 SHA-256 格式无效。");
        if (release.ReleaseNotes is null || release.ReleaseNotes.Length > 100
            || release.ReleaseNotes.Any(note => note is null || note.Length > 4000))
            throw new InvalidDataException("更新说明无效或过长。");
        if (release.PackageUrl is not { IsAbsoluteUri: true } url
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment) || url.OriginalString.Contains('\\') || HasDotSegments(url))
            throw new InvalidDataException("更新包地址无效。");
        if (manifestUri is null) return;
        var directory = manifestUri.AbsolutePath[..(manifestUri.AbsolutePath.LastIndexOf('/') + 1)];
        var prefix = directory + "packages/";
        if (url.Scheme != manifestUri.Scheme || !string.Equals(url.IdnHost, manifestUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            || url.Port != manifestUri.Port || !url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            || !PackageFilePattern().IsMatch(url.AbsolutePath[prefix.Length..]))
            throw new InvalidDataException("更新包必须位于清单同源的 packages 目录，不能跨域或跳转目录。");
    }

    private static bool HasDotSegments(Uri uri) => uri.OriginalString.Split('/').Any(segment =>
        Uri.UnescapeDataString(segment) is "." or "..");

    [GeneratedRegex("\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]*\\.zip\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageFilePattern();
}
