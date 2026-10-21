namespace VocabCollector.Core;

public sealed record LinkRefreshResult(int Updated, int Skipped, int Failed);

public interface ILinkMaintenance
{
    Task<LinkRefreshResult> RefreshLinksAsync(VaultOptions options, CancellationToken ct);
}
