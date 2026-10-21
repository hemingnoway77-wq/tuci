using System.Windows;
using Microsoft.Win32;
using VocabCollector.Core;
namespace VocabCollector.Desktop;
public partial class SettingsWindow : Window
{
    private readonly ISettingsStore _store;
    public AppSettings? SavedSettings { get; private set; }
    public SettingsWindow(AppSettings draft, ISettingsStore store)
    {
        InitializeComponent(); _store = store;
        BaseUrlBox.Text = draft.Api.BaseUrl; KeyBox.Password = draft.Api.ApiKey; ModelBox.Text = draft.Api.Model;
        VaultBox.Text = draft.Vault.RootPath; SubfolderBox.Text = draft.Vault.OutputSubfolder; ClassifyBox.IsChecked = draft.Vault.ClassifyByPartOfSpeech;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择 Obsidian 仓库根目录", Multiselect = false };
        if (picker.ShowDialog(this) == true) VaultBox.Text = picker.FolderName;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false; ErrorText.Text = "";
        var settings = new AppSettings(new(BaseUrlBox.Text.Trim(), KeyBox.Password.Trim(), ModelBox.Text.Trim()), new(VaultBox.Text.Trim(), SubfolderBox.Text.Trim(), ClassifyBox.IsChecked == true));
        try { await _store.SaveAsync(settings, CancellationToken.None); SavedSettings = settings; Close(); }
        catch (ArgumentException error) { ErrorText.Text = error.Message; }
        catch (Exception) { ErrorText.Text = "设置保存失败，请检查本机文件权限后重试。"; }
        finally { SaveButton.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
