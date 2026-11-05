using System.IO;
using VocabCollector.Core;

namespace VocabCollector.Desktop;

/// <summary>Give new installations their own library without moving an existing one.</summary>
public static class LocalLibrarySettings
{
    public static AppSettings Resolve(AppSettings settings, string applicationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(settings.Vault.RootPath)) return settings;
        var root = Path.Combine(Path.GetFullPath(applicationDirectory), "library");
        Directory.CreateDirectory(root);
        return settings with { Vault = settings.Vault with { RootPath = root } };
    }
}
