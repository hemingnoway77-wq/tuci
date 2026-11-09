using System.Collections;
using System.IO;
using System.Windows;

namespace VocabCollector.Desktop;

public sealed class ThemeManager
{
    private readonly ResourceDictionary resources;
    private AppearanceSettingsStore? store;
    private long _storeScope, _pendingPreferenceRevision;

    public ThemeManager(ResourceDictionary resources, AppearanceSettingsStore? store = null)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.store = store;
        Apply(AppTheme.WarmNeutral);
    }

    public AppTheme CurrentTheme { get; private set; }
    public long PreferenceRevision { get; private set; }
    public bool HasPendingPreferenceSave => _pendingPreferenceRevision != 0;

    /// <summary>Raised after a palette is applied so every open surface can sync its own selector.</summary>
    public event EventHandler<AppTheme>? ThemeChanged;
    public event EventHandler<AppTheme>? PreferenceSaved;

    public async Task SwitchStoreAsync(AppearanceSettingsStore nextStore, CancellationToken cancellationToken = default)
    {
        store = nextStore;
        var scope = ++_storeScope;
        _pendingPreferenceRevision = 0;
        var revision = PreferenceRevision;
        var theme = await nextStore.LoadAsync(cancellationToken);
        if (_storeScope == scope && PreferenceRevision == revision && ReferenceEquals(store, nextStore)) Apply(theme);
    }

    public async Task ApplyRemoteAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        if (HasPendingPreferenceSave) return;
        var target = store;
        var scope = _storeScope;
        var revision = PreferenceRevision;
        if (target is not null) await target.SaveAsync(theme, cancellationToken);
        if (_storeScope == scope && PreferenceRevision == revision && ReferenceEquals(store, target)) Apply(theme);
    }

    public void Apply(AppTheme theme)
    {
        var selected = theme switch
        {
            AppTheme.Pink => AppTheme.Pink,
            AppTheme.Mono => AppTheme.Mono,
            _ => AppTheme.WarmNeutral
        };
        foreach (DictionaryEntry entry in ThemePalette.Create(selected)) resources[entry.Key] = entry.Value;
        CurrentTheme = selected;
        ThemeChanged?.Invoke(this, selected);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = store;
        var scope = _storeScope;
        var revision = PreferenceRevision;
        var theme = target is null ? AppTheme.WarmNeutral : await target.LoadAsync(cancellationToken);
        if (_storeScope == scope && PreferenceRevision == revision && ReferenceEquals(store, target)) Apply(theme);
    }

    public async Task<bool> SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = store;
        var scope = _storeScope;
        var revision = ++PreferenceRevision;
        _pendingPreferenceRevision = revision;
        Apply(theme);
        var selected = CurrentTheme;
        try
        {
            if (target is not null) await target.SaveAsync(selected, cancellationToken);
            if (_storeScope == scope && PreferenceRevision == revision && ReferenceEquals(store, target))
                PreferenceSaved?.Invoke(this, selected);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (_storeScope == scope && _pendingPreferenceRevision == revision) _pendingPreferenceRevision = 0;
        }
    }
}
