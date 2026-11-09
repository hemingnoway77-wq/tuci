using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VocabCollector.Core.Updates;

/// <summary>Use an HttpClient handler with AllowAutoRedirect=false. The caller owns the client.</summary>
public sealed class UpdateClient
{
    private readonly HttpClient _client;
    private readonly Uri _manifestUri;
    private readonly UpdateManifestVerifier _verifier;

    public UpdateClient(HttpClient client, Uri manifestUri, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(client);
        _verifier = new UpdateManifestVerifier(publicKeyPem, manifestUri);
        _client = client;
        _manifestUri = manifestUri;
    }

    public async Task<VerifiedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var response = await GetAsync(_manifestUri, cancellationToken);
        if (response.Content.Headers.ContentLength > UpdatePolicy.MaximumManifestBytes)
            throw new InvalidDataException("更新清单超过 64 KiB 限制。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(bytes, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > UpdatePolicy.MaximumManifestBytes) throw new InvalidDataException("更新清单超过 64 KiB 限制。");
            buffer.Write(bytes, 0, read);
        }
        string manifest;
        try { manifest = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("更新清单不是有效的 UTF-8。", ex); }
        var update = _verifier.Verify(manifest);
        // ProductVersion may have a fourth assembly component; compare the three release components.
        var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        return UpdatePolicy.ParseVersion(update.Release.Version) > current ? update : null;
    }

    public async Task<string> DownloadAsync(VerifiedUpdate update, string downloadDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var authenticated = _verifier.Verify(update.SignedManifest);
        if (update.Release != authenticated.Release && !Equivalent(update.Release, authenticated.Release))
            throw new InvalidDataException("更新信息与签名清单不一致。");
        var release = authenticated.Release;
        Directory.CreateDirectory(downloadDirectory);
        var finalPath = Path.Combine(Path.GetFullPath(downloadDirectory), $"tuci-{release.Version}-{Guid.NewGuid():N}.zip");
        var partialPath = finalPath + ".partial";
        try
        {
            using var response = await GetAsync(release.PackageUrl, cancellationToken);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.SizeBytes)
                throw new InvalidDataException("更新包下载大小与签名清单不符。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long received = 0;
                progress?.Report(new UpdateDownloadProgress(0, release.SizeBytes));
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    received += read;
                    if (received > release.SizeBytes) throw new InvalidDataException("更新包下载超过签名清单大小。");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(new UpdateDownloadProgress(received, release.SizeBytes));
                }
                if (received != release.SizeBytes) throw new InvalidDataException("更新包下载不完整。");
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("更新包 SHA-256 校验失败。");
                await destination.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch
        {
            try { File.Delete(partialPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if ((int)response.StatusCode is >= 300 and <= 399
                || response.RequestMessage?.RequestUri is { } finalUri && finalUri.AbsoluteUri != uri.AbsoluteUri)
                throw new InvalidDataException("更新下载不允许 HTTP 重定向。");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("更新下载不允许额外的内容编码。");
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static bool Equivalent(UpdateRelease a, UpdateRelease b) => a.Version == b.Version && a.Platform == b.Platform
        && a.PackageUrl == b.PackageUrl && a.SizeBytes == b.SizeBytes && a.Sha256 == b.Sha256
        && a.PublishedAt == b.PublishedAt && a.ReleaseNotes is not null && a.ReleaseNotes.SequenceEqual(b.ReleaseNotes);
}
