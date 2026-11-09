using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Updates;

namespace VocabCollector.Tests;

public sealed class UpdateManifestTests
{
    [Fact]
    public void RealRsaPssSignatureAuthenticatesRelease()
    {
        var update = UpdateTestData.Verifier().Verify(UpdateTestData.Manifest());
        Assert.Equal("1.1.0", update.Release.Version);
        Assert.Equal(UpdateTestData.PackageUri, update.Release.PackageUrl);
        Assert.Equal(["修复更新"], update.Release.ReleaseNotes);
    }

    [Fact]
    public void ModifiedPayloadAndWrongSigningKeyAreRejected()
    {
        var manifest = UpdateTestData.Manifest();
        using var envelope = JsonDocument.Parse(manifest);
        var tampered = JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(UpdateTestData.Payload().Replace("1.1.0", "9.9.9"))),
            signature = envelope.RootElement.GetProperty("signature").GetString()
        });
        Assert.Throws<InvalidDataException>(() => UpdateTestData.Verifier().Verify(tampered));
        using var otherKey = RSA.Create(3072);
        Assert.Throws<InvalidDataException>(() => new UpdateManifestVerifier(otherKey.ExportSubjectPublicKeyInfoPem(), UpdateTestData.ManifestUri).Verify(manifest));
    }

    [Theory]
    [InlineData("https://evil.example/tuci-updates/packages/update.zip")]
    [InlineData("http://updates.example/tuci-updates/packages/update.zip")]
    [InlineData("https://updates.example:444/tuci-updates/packages/update.zip")]
    [InlineData("https://updates.example/tuci-updates/other/update.zip")]
    [InlineData("https://updates.example/tuci-updates/packages/subdir/update.zip")]
    [InlineData("https://updates.example/tuci-updates/packages/%2fupdate.zip")]
    [InlineData("https://updates.example/tuci-updates/packages/../packages/update.zip")]
    [InlineData("https://updates.example/tuci-updates/packages/update.zip?download=1")]
    [InlineData("https://updates.example/tuci-updates/packages/update.zip#fragment")]
    [InlineData("https://user:password@updates.example/tuci-updates/packages/update.zip")]
    public void SignedPackageAddressesMustStayInSameOriginAndPackageDirectory(string packageUrl)
    {
        var manifest = UpdateTestData.Sign(UpdateTestData.Payload(packageUrl: packageUrl));
        Assert.Throws<InvalidDataException>(() => UpdateTestData.Verifier().Verify(manifest));
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("1.1.0.0")]
    [InlineData("1.1.0-preview")]
    [InlineData("01.1.0")]
    [InlineData("-1.1.0")]
    public void SignedInvalidVersionIsRejected(string version) => Assert.Throws<InvalidDataException>(() =>
        UpdateTestData.Verifier().Verify(UpdateTestData.Manifest(version: version)));

    [Theory]
    [InlineData("{\"payload\":\"!\",\"signature\":\"!\"}")]
    [InlineData("{\"payload\":\"\",\"signature\":\"\",\"signature\":\"\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void MalformedEnvelopeIsRejected(string manifest) => Assert.Throws<InvalidDataException>(() => UpdateTestData.Verifier().Verify(manifest));

    [Fact]
    public void SignedUnsupportedPlatformDuplicateFieldsAndOversizedManifestAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => UpdateTestData.Verifier().Verify(UpdateTestData.Sign(UpdateTestData.Payload().Replace("win-x64", "linux-x64"))));
        Assert.Throws<InvalidDataException>(() => UpdateTestData.Verifier().Verify(UpdateTestData.Sign(UpdateTestData.Payload().Replace("\"version\":", "\"version\":\"1.0.0\",\"version\":"))));
        Assert.Throws<InvalidDataException>(() => UpdateTestData.Verifier().Verify(new string(' ', 65537)));
    }
}

internal static class UpdateTestData
{
    private static readonly RSA Key = RSA.Create(3072);
    internal static readonly Uri ManifestUri = new("https://updates.example/tuci-updates/stable.json");
    internal static readonly Uri PackageUri = new("https://updates.example/tuci-updates/packages/tuci-win-x64-1.1.0.zip");
    internal static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("signed package test data");
    internal static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();
    internal static UpdateManifestVerifier Verifier() => new(PublicKeyPem, ManifestUri);
    internal static string Payload(byte[]? package = null, string version = "1.1.0", string? packageUrl = null, long? size = null, string? sha = null) =>
        JsonSerializer.Serialize(new
        {
            version,
            platform = "win-x64",
            packageUrl = packageUrl ?? PackageUri.AbsoluteUri,
            sizeBytes = size ?? (package ?? PackageBytes).LongLength,
            sha256 = sha ?? Convert.ToHexString(SHA256.HashData(package ?? PackageBytes)),
            releaseNotes = new[] { "修复更新" },
            publishedAt = "2026-11-07T07:00:00Z"
        });
    internal static string Manifest(byte[]? package = null, string version = "1.1.0", long? size = null, string? sha = null) => Sign(Payload(package, version, size: size, sha: sha));
    internal static string Sign(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature;
        lock (Key) signature = Key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new { payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(signature) });
    }
}

internal sealed class UpdateTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TuciUpdateTests", Guid.NewGuid().ToString("N"));
    internal UpdateTestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
