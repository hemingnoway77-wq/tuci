using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VocabCollector.Core;

namespace VocabCollector.Desktop;

public partial class SettingsWindow : Window
{
    private readonly ISettingsStore _store;
    private readonly ThemeManager? _themes;
    private readonly AppSettings _initialSettings;
    private readonly (RadioButton Tab, ScrollViewer Page)[] _categories;
    private int _selectedCategory;
    private bool _updatesAvailable, _hasUpdate, _closed;

    public AppSettings? SavedSettings { get; private set; }
    public bool IsSaving { get; private set; }
    internal bool IsManagingAccount => AccountSyncPanel.IsBusy;
    public bool HasUnsavedChanges => SavedSettings is null && ReadSettings() != _initialSettings;
    public event EventHandler? UpdateRequested;

    public SettingsWindow(AppSettings draft, ISettingsStore store, ThemeManager? themes = null)
    {
        InitializeComponent();
        _store = store;
        _themes = themes;
        _categories = [(AccountTab, AccountPage), (LibraryTab, LibraryPage), (ApiTab, ApiPage), (UpdateTab, UpdatePage)];
        foreach (var (tab, _) in _categories)
        {
            tab.Checked += Category_Checked;
            tab.PreviewKeyDown += Category_KeyDown;
        }
        Loaded += (_, _) => AlignCategoryIndicator(false);
        CategoryNavigation.SizeChanged += (_, _) => AlignCategoryIndicator(false);
        AccountSyncPanel.BusyChanged += (_, _) => RefreshActions();
        BaseUrlBox.Text = draft.Api.BaseUrl;
        KeyBox.Password = draft.Api.ApiKey;
        ModelBox.Text = draft.Api.Model;
        VaultBox.Text = draft.Vault.RootPath;
        SubfolderBox.Text = draft.Vault.OutputSubfolder;
        ClassifyBox.IsChecked = draft.Vault.ClassifyByPartOfSpeech;
        _initialSettings = ReadSettings();
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        if (_themes is not null) _themes.ThemeChanged += Themes_ThemeChanged;
    }

    internal void ConfigureServices(DesktopSync? sync, Func<bool> canManageAccount, Version? currentVersion)
    {
        if (_closed) return;
        _updatesAvailable = currentVersion is not null;
        AccountSyncPanel.Configure(sync, canManageAccount);
        CurrentVersionText.Text = currentVersion is null ? "更新检查当前不可用" :
            "当前版本 " + (currentVersion.Build >= 0 ? currentVersion.ToString(3) : currentVersion.ToString());
        UpdateAvailability(_hasUpdate);
        RefreshActions();
    }

    public void UpdateAvailability(bool hasUpdate)
    {
        if (_closed) return;
        _hasUpdate = hasUpdate;
        var showUpdate = _updatesAvailable && hasUpdate;
        var visibility = showUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateCategoryDot.Visibility = CheckUpdateDot.Visibility = UpdateAvailableText.Visibility = visibility;
        var helpText = showUpdate ? "有可用更新" : "";
        AutomationProperties.SetHelpText(UpdateTab, helpText);
        AutomationProperties.SetHelpText(CheckUpdateButton, helpText);
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        var index = Array.FindIndex(_categories, category => ReferenceEquals(category.Tab, sender));
        if (index < 0 || index == _selectedCategory) return;
        _categories[_selectedCategory].Page.Visibility = Visibility.Collapsed;
        _selectedCategory = index;
        _categories[index].Page.Visibility = Visibility.Visible;
        AlignCategoryIndicator(true);
    }

    private void Category_KeyDown(object sender, KeyEventArgs e)
    {
        var index = e.Key switch
        {
            Key.Up => Math.Max(0, _selectedCategory - 1),
            Key.Down => Math.Min(_categories.Length - 1, _selectedCategory + 1),
            Key.Home => 0,
            Key.End => _categories.Length - 1,
            _ => -1
        };
        if (index < 0) return;
        var tab = _categories[index].Tab;
        tab.Focus();
        tab.IsChecked = true;
        e.Handled = true;
    }

    private void AlignCategoryIndicator(bool animate)
    {
        var selected = _categories[_selectedCategory].Tab;
        if (_closed || selected.ActualHeight <= 0) return;
        var center = selected.TranslatePoint(new Point(0, selected.ActualHeight / 2), CategoryNavigation).Y;
        CategoryIndicator.MoveTo(center - 11, 22, animate);
    }

    private void RefreshActions()
    {
        CategoryNavigation.IsEnabled = CategoryPages.IsEnabled = !IsSaving;
        SaveButton.IsEnabled = !IsSaving && !IsManagingAccount;
        CancelButton.IsEnabled = !IsSaving;
        CheckUpdateButton.IsEnabled = _updatesAvailable && !IsSaving;
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed && _updatesAvailable && !IsSaving) UpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Themes_ThemeChanged(object? sender, AppTheme theme) => TitleBarTheme.Apply(this);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择本地词库位置", Multiselect = false };
        if (picker.ShowDialog(this) == true) VaultBox.Text = picker.FolderName;
    }

    private AppSettings ReadSettings() => new(
        new(BaseUrlBox.Text.Trim(), KeyBox.Password.Trim(), ModelBox.Text.Trim()),
        new(VaultBox.Text.Trim(), SubfolderBox.Text.Trim(), ClassifyBox.IsChecked == true));

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || IsSaving || IsManagingAccount) return;
        var settings = ReadSettings();
        IsSaving = true;
        RefreshActions();
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _store.SaveAsync(settings, CancellationToken.None);
            if (!_closed) SavedSettings = settings;
        }
        catch (ArgumentException error) { ShowSaveError(error.Message); }
        catch (Exception) { ShowSaveError("设置保存失败，请检查本机文件权限后重试。"); }
        finally
        {
            IsSaving = false;
            if (!_closed) RefreshActions();
        }
        if (!_closed && SavedSettings is not null) Close();
    }

    private void ShowSaveError(string message)
    {
        if (_closed) return;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (IsSaving) e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        AccountSyncPanel.Dispose();
        CategoryIndicator.Dispose();
        if (_themes is not null) _themes.ThemeChanged -= Themes_ThemeChanged;
        UpdateRequested = null;
        base.OnClosed(e);
    }
}
