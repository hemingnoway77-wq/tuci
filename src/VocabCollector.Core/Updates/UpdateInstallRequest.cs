namespace VocabCollector.Core.Updates;

public sealed record UpdateInstallRequest
{
    public required string InstallDirectory { get; init; }
    public required string PackagePath { get; init; }
    public required string ManifestPath { get; init; }
    public required string ExpectedVersion { get; init; }
    public int ParentProcessId { get; init; }
    public long ParentProcessStartTimeUtcTicks { get; init; }
    public bool Restart { get; init; } = true;
}
