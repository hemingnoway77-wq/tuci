using VocabCollector.Core;

namespace VocabCollector.Desktop;

/// <summary>API credentials remain on this computer and belong to one local account scope.</summary>
public static class LocalAccountSettings
{
    public static ISettingsStore Store(ISettingsStore guestStore, string? accountDirectory) =>
        accountDirectory is null ? guestStore : new WindowsSettingsStore(accountDirectory);

    public static async Task<AppSettings> LoadAsync(AppSettings guest, VaultOptions? activeVault, string? accountDirectory,
        CancellationToken cancellationToken = default)
    {
        if (accountDirectory is null) return guest with { Vault = activeVault ?? guest.Vault };
        var loaded = await new WindowsSettingsStore(accountDirectory).LoadAsync(cancellationToken);
        return loaded.Settings with { Vault = activeVault ?? throw new ArgumentException("账号尚未绑定词库。") };
    }
}
