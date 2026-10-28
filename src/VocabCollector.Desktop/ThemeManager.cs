using System.Collections;
using System.IO;
using System.Windows;

namespace VocabCollector.Desktop;

public sealed class ThemeManager
{
    private readonly ResourceDictionary resources;
    private readonly AppearanceSettingsStore? store;

    public ThemeManager(ResourceDictionary resources, AppearanceSettingsStore? store = null)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.store = store;
        Apply(AppTheme.WarmNeutral);
    }

    public AppTheme CurrentTheme { get; private set; }

    public void Apply(AppTheme theme)
    {
        var selected = theme == AppTheme.Pink ? AppTheme.Pink : AppTheme.WarmNeutral;
        foreach (DictionaryEntry entry in ThemePalette.Create(selected)) resources[entry.Key] = entry.Value;
        CurrentTheme = selected;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Apply(store is null ? AppTheme.WarmNeutral : await store.LoadAsync(cancellationToken));
    }

    public async Task<bool> SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Apply(theme);
        if (store is null) return true;
        try
        {
            await store.SaveAsync(CurrentTheme, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
