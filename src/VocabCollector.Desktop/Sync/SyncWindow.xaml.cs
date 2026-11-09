using System.IO;
using System.Windows;
using Microsoft.Win32;
using VocabCollector.Core;
using VocabCollector.Core.Storage;

namespace VocabCollector.Desktop;

public partial class SyncWindow : Window
{
    private readonly DesktopSync _sync;
    private readonly VaultOptions _default;
    public SyncWindow(DesktopSync sync, VaultOptions configured)
    {
        if (!sync.CanUseAccount) throw new InvalidOperationException("请先登录账号，再管理同步。");
        InitializeComponent(); _sync = sync; _default = configured;
        UrlBox.Text = sync.Server.Length > 0 ? sync.Server : "https://101.133.134.128/tuci-sync/";
        try { FolderBox.Text = VaultPaths.WordRoot(configured); }
        catch (ArgumentException) { }
        _sync.Updated += Updated;
        Closed += (_, _) => { _sync.Updated -= Updated; ClearPasswords(); };
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Refresh();
    }
    private void Updated(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        BoundPanel.Visibility = _sync.CanUseAccount ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = Visibility.Collapsed;
        Details.Text = _sync.Folder + Environment.NewLine + _sync.Server;
        AccountLabel.Text = _sync.SignedIn ? "已登录 · " + _sync.Username : "已通过设备配对连接";
        LogoutButton.Visibility = ChangePasswordPanel.Visibility = _sync.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        InvitePanel.Visibility = _sync.CanInvite ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.Visibility = ClaimPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = _sync.Status;
    }
    private async void Invite_Expanded(object sender, RoutedEventArgs e) => await Perform(async () =>
        InviteListText.Text = await _sync.RefreshInvites());
    private async void Invite_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        var issued = await _sync.IssueInvite();
        InviteCodeBox.Text = issued.Code;
        InviteCodeBox.Visibility = Visibility.Visible;
        InviteCodeBox.SelectAll();
        InviteCodeBox.Focus();
        InviteListText.Text = await _sync.RefreshInvites();
    });
    private async void Invites_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
        InviteListText.Text = await _sync.RefreshInvites());
    private void Choose_Click(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFolderDialog { Title = "选择要同步的单词目录", Multiselect = false };
        if (picker.ShowDialog(this) == true) FolderBox.Text = picker.FolderName;
    }
    private VaultOptions Selected()
    {
        var selected = FolderBox.Text.Trim();
        if (!Path.IsPathFullyQualified(selected)) throw new ArgumentException("请选择单词目录。");
        selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        if (Directory.Exists(_default.RootPath) && string.Equals(selected, VaultPaths.WordRoot(_default), StringComparison.OrdinalIgnoreCase)) return _default;
        return new(Path.GetDirectoryName(selected) ?? throw new ArgumentException("请选择单词目录。"), Path.GetFileName(selected), true);
    }
    private async Task Perform(Func<Task> work)
    {
        IsEnabled = false;
        try { await work(); }
        catch (Exception e) { StatusText.Text = DesktopSync.Friendly(e); }
        finally { IsEnabled = true; }
    }
    private async void Bind_Click(object sender, RoutedEventArgs e) => await Perform(async () => { await _sync.Bind(UrlBox.Text, CodeBox.Password, Selected()); CodeBox.Clear(); });
    private async void Login_Click(object sender, RoutedEventArgs e) => await SignIn(false);
    private async void Register_Click(object sender, RoutedEventArgs e) => await SignIn(true);
    private Task SignIn(bool register) => Perform(async () =>
    {
        await _sync.SignIn(UrlBox.Text, UsernameBox.Text, PasswordBox.Password, Selected(), register);
        ClearPasswords(); Refresh();
    });
    private async void Claim_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        await _sync.SignIn(_sync.Server, ClaimUsernameBox.Text, ClaimPasswordBox.Password,
            _sync.ActiveVault ?? throw new InvalidOperationException("当前词库已断开。"), true);
        ClearPasswords(); Refresh();
    });
    private async void Logout_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        await _sync.Logout(); PairBox.Clear(); ClearPasswords(); Refresh();
    });
    private async void ChangePassword_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        await _sync.ChangePassword(CurrentPasswordBox.Password, NewPasswordBox.Password);
        CurrentPasswordBox.Clear(); NewPasswordBox.Clear(); ChangePasswordPanel.IsExpanded = false;
    });
    private void ClearPasswords()
    {
        PasswordBox.Clear(); CodeBox.Clear(); ClaimPasswordBox.Clear(); CurrentPasswordBox.Clear(); NewPasswordBox.Clear();
    }
    private async void Sync_Click(object sender, RoutedEventArgs e) => await Perform(_sync.Tick);
    private async void Pair_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        PairBox.Text = await _sync.Pair(); PairBox.Visibility = Visibility.Visible; PairBox.SelectAll(); PairBox.Focus();
    });
    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        await _sync.Unbind(); PairBox.Clear(); PairBox.Visibility = Visibility.Collapsed;
    });
}
