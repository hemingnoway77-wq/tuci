namespace VocabCollector.Core.Updates;

public sealed record UpdateRelease(string Version, string Platform, Uri PackageUrl, long SizeBytes,
    string Sha256, string[] ReleaseNotes, DateTimeOffset PublishedAt);

public sealed record VerifiedUpdate(UpdateRelease Release, string SignedManifest);

public sealed record UpdateDownloadProgress(long ReceivedBytes, long TotalBytes);
