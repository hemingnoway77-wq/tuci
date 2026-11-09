using System.Net;
using System.Text;
using VocabCollector.Core.Updates;

namespace VocabCollector.Tests;

public sealed class UpdateClientTests
{
    [Theory]
    [InlineData("1.0.9", true)]
    [InlineData("1.1.0", false)]
    [InlineData("1.2.0", false)]
    [InlineData("1.1.0.0", false)]
    public async Task OnlyNewerSignedReleaseIsReturned(string current, bool expected)
    {
        using var http = Client(_ => new StringContent(UpdateTestData.Manifest(), Encoding.UTF8, "application/json"));
        var update = await Updater(http).CheckAsync(Version.Parse(current));
        Assert.Equal(expected, update is not null);
    }

    [Fact]
    public async Task OversizedStreamingManifestAndInvalidUtf8AreRejected()
    {
        using var oversized = Client(_ => new StreamingContent(new byte[65537]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(oversized).CheckAsync(new Version(1, 0, 0)));
        using var invalidUtf8 = Client(_ => new StreamingContent([0xFF, 0xFE]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(invalidUtf8).CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task DownloadVerifiesSizeHashAndReportsProgressBeforeAtomicRename()
    {
        using var directory = new UpdateTestDirectory();
        var update = UpdateTestData.Verifier().Verify(UpdateTestData.Manifest());
        var reports = new List<UpdateDownloadProgress>();
        using var http = Client(_ => new StreamingContent(UpdateTestData.PackageBytes));
        var package = await Updater(http).DownloadAsync(update, directory.Path, new CallbackProgress(reports.Add));
        Assert.Equal(UpdateTestData.PackageBytes, await File.ReadAllBytesAsync(package));
        Assert.EndsWith(".zip", package);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal(update.Release.SizeBytes, reports[^1].ReceivedBytes);
        await UpdatePackageVerifier.VerifyAsync(package, update);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("header")]
    public async Task InvalidDownloadsAreRejectedAndPartialFilesRemoved(string failure)
    {
        using var directory = new UpdateTestDirectory();
        var bytes = UpdateTestData.PackageBytes.ToArray();
        var update = UpdateTestData.Verifier().Verify(UpdateTestData.Manifest());
        if (failure == "hash") bytes[0] ^= 1;
        if (failure == "short") bytes = bytes[..^1];
        if (failure == "long") bytes = [.. bytes, 0];
        using var http = Client(_ => failure == "header" ? new ByteArrayContent([0]) : new StreamingContent(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(http).DownloadAsync(update, directory.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task CancellationDuringDownloadRemovesPartialFile()
    {
        using var directory = new UpdateTestDirectory();
        using var cancellation = new CancellationTokenSource();
        using var http = Client(_ => new StreamingContent(UpdateTestData.PackageBytes));
        var update = UpdateTestData.Verifier().Verify(UpdateTestData.Manifest());
        var progress = new CallbackProgress(p => { if (p.ReceivedBytes > 0) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Updater(http).DownloadAsync(update, directory.Path, progress, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task FabricatedVerifiedUpdateIsReauthenticated()
    {
        using var directory = new UpdateTestDirectory();
        using var http = Client(_ => throw new Xunit.Sdk.XunitException("No request should be sent for altered metadata."));
        var verified = UpdateTestData.Verifier().Verify(UpdateTestData.Manifest());
        var altered = verified with { Release = verified.Release with { SizeBytes = 1 } };
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(http).DownloadAsync(altered, directory.Path));
    }

    [Fact]
    public async Task RedirectResponsesAndAlreadyFollowedRedirectsAreRejected()
    {
        using var redirect = new HttpClient(new TestHandler(request => new HttpResponseMessage(HttpStatusCode.Found)
        {
            RequestMessage = request,
            Headers = { Location = UpdateTestData.PackageUri },
            Content = new StringContent("redirect")
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(redirect).CheckAsync(new Version(1, 0, 0)));
        using var followed = new HttpClient(new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/manifest.json"),
            Content = new StringContent(UpdateTestData.Manifest())
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater(followed).CheckAsync(new Version(1, 0, 0)));
    }

    private static UpdateClient Updater(HttpClient http) => new(http, UpdateTestData.ManifestUri, UpdateTestData.PublicKeyPem);
    private static HttpClient Client(Func<HttpRequestMessage, HttpContent> content) => new(new TestHandler(request =>
        new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content(request) }));

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class CallbackProgress(Action<UpdateDownloadProgress> callback) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => callback(value);
    }
}
