using VocabCollector.Core.Updates;

namespace VocabCollector.Tests;

public sealed class UpdatePackageTests
{
    [Fact]
    public async Task PackageSizeAndDigestMustMatchVerifiedManifest()
    {
        using var directory = new UpdateTestDirectory();
        var path = Path.Combine(directory.Path, "update.zip");
        var update = UpdateTestData.Verifier().Verify(UpdateTestData.Manifest());
        await File.WriteAllBytesAsync(path, UpdateTestData.PackageBytes);
        await UpdatePackageVerifier.VerifyAsync(path, update);
        var changed = UpdateTestData.PackageBytes.ToArray();
        changed[0] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackageVerifier.VerifyAsync(path, update));
        await File.WriteAllBytesAsync(path, changed[..^1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackageVerifier.VerifyAsync(path, update));
    }

    [Fact]
    public async Task CancelledPackageVerificationDoesNotSucceed()
    {
        using var directory = new UpdateTestDirectory();
        var path = Path.Combine(directory.Path, "update.zip");
        await File.WriteAllBytesAsync(path, UpdateTestData.PackageBytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePackageVerifier.VerifyAsync(path,
            UpdateTestData.Verifier().Verify(UpdateTestData.Manifest()), cancellation.Token));
    }
}
