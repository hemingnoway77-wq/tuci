using System.Security.Cryptography;

namespace VocabCollector.Core.Updates;

public static class UpdatePackageVerifier
{
    /// <summary>The caller must first obtain <paramref name="update"/> from UpdateManifestVerifier.</summary>
    public static async Task VerifyAsync(string packagePath, VerifiedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        UpdatePolicy.ValidateRelease(update.Release);
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != update.Release.SizeBytes) throw new InvalidDataException("更新包大小与签名清单不符。");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(update.Release.Sha256)))
            throw new InvalidDataException("更新包 SHA-256 校验失败。");
    }
}
