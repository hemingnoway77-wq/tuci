using System.Windows;
using System.Windows.Controls;

namespace VocabCollector.Desktop;

public partial class AccountSyncView : UserControl, IDisposable
{
    private DesktopSync? _sync;
    private Func<bool>? _canManage;
    private CancellationTokenSource? _inviteLoad;
    private bool _closed;

    internal bool IsBusy { get; private set; }
    internal event EventHandler? BusyChanged;

    public AccountSyncView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ = LoadInvitesAsync();
            else CancelInviteLoad();
        };
    }

    internal void Configure(DesktopSync? sync, Func<bool> canManage)
    {
        if (_closed) return;
        Detach();
        _sync = sync;
        _canManage = canManage;
        if (sync is not null)
        {
            sync.Updated += SyncChanged;
            sync.AccessChanged += SyncChanged;
        }
        Refresh();
    }

    private void SyncChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (_closed) return;
        var sync = _sync;
        var available = sync?.CanUseAccount == true;
        UnavailableText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        AccountActions.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            ClearPasswords();
            ClearInvites();
            return;
        }
        AccountLabel.Text = "已登录 · " + sync!.Username;
        SyncStatusText.Text = sync.Status;
        FolderText.Text = sync.Folder;
        ServerText.Text = sync.Server;
        InvitePanel.Visibility = sync.CanInvite ? Visibility.Visible : Visibility.Collapsed;
        if (!sync.CanInvite) ClearInvites();
    }

    private bool IsCurrent(DesktopSync sync) => !_closed && ReferenceEquals(_sync, sync) && sync.CanUseAccount;

    private async Task PerformAsync(Func<DesktopSync, Task> work, Func<Exception, string>? errorText = null)
    {
        if (_sync is not { } sync || !IsCurrent(sync) || IsBusy || !IsEnabled) return;
        if (_canManage?.Invoke() == false)
        {
            ShowError("请等待当前收录或词库操作完成。");
            return;
        }
        OperationErrorText.Visibility = Visibility.Collapsed;
        CancelInviteLoad();
        SetBusy(true);
        try { await work(sync); }
        catch (Exception error)
        {
            if (IsCurrent(sync)) ShowError(errorText?.Invoke(error) ?? DesktopSync.Friendly(error));
        }
        finally
        {
            if (!_closed)
            {
                Refresh();
                SetBusy(false);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        SyncNowButton.IsEnabled = !busy;
        ChangePasswordPanel.IsEnabled = !busy;
        InvitePanel.IsEnabled = !busy;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(string message)
    {
        OperationErrorText.Text = message;
        OperationErrorText.Visibility = Visibility.Visible;
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await PerformAsync(sync => sync.Tick());

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_sync is not { } sync || !IsCurrent(sync) || !LogoutButton.IsEnabled) return;
        if (_canManage?.Invoke() == false)
        {
            ShowError("请等待当前收录或词库操作完成。");
            return;
        }
        LogoutButton.IsEnabled = false;
        ClearPasswords();
        ClearInvites();
        try { await sync.Logout(); }
        catch (Exception error) { if (IsCurrent(sync)) ShowError(DesktopSync.Friendly(error)); }
        finally { if (!_closed) LogoutButton.IsEnabled = true; }
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentPasswordBox.Password;
        var replacement = NewPasswordBox.Password;
        await PerformAsync(async sync =>
        {
            await sync.ChangePassword(current, replacement);
            if (!IsCurrent(sync)) return;
            ClearPasswords();
            ChangePasswordPanel.IsExpanded = false;
        }, error =>
        {
            var message = DesktopSync.Friendly(error);
            if (!string.IsNullOrEmpty(current)) message = message.Replace(current, "•••", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(replacement)) message = message.Replace(replacement, "•••", StringComparison.Ordinal);
            return message;
        });
    }

    private async void Invite_Expanded(object sender, RoutedEventArgs e) => await LoadInvitesAsync();
    private void Invite_Collapsed(object sender, RoutedEventArgs e) => CancelInviteLoad();
    private async void Invites_Click(object sender, RoutedEventArgs e) => await LoadInvitesAsync();

    private async void Invite_Click(object sender, RoutedEventArgs e)
    {
        var issued = false;
        await PerformAsync(async sync =>
        {
            if (!sync.CanInvite) return;
            var invite = await sync.IssueInvite();
            if (!IsCurrent(sync) || !sync.CanInvite) return;
            InviteCodeBox.Text = invite.Code;
            InviteCodeBox.Visibility = Visibility.Visible;
            issued = true;
        });
        if (!issued) return;
        if (InviteCodeBox.IsVisible && InviteCodeBox.IsEnabled)
        {
            InviteCodeBox.Focus();
            InviteCodeBox.SelectAll();
        }
        await LoadInvitesAsync();
    }

    private async Task LoadInvitesAsync()
    {
        if (_sync is not { CanInvite: true } sync || !IsCurrent(sync) ||
            !IsVisible || !InvitePanel.IsExpanded || IsBusy || _inviteLoad is not null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _inviteLoad = request;
        RefreshInvitesButton.IsEnabled = false;
        ShowInviteStatus("正在加载邀请码…");
        try
        {
            var invites = await sync.RefreshInvites(request.Token);
            if (!ReferenceEquals(_inviteLoad, request)) return;
            InviteListText.Text = invites;
            ShowInviteStatus("");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (ReferenceEquals(_inviteLoad, request)) ShowInviteStatus("加载超时，可以刷新列表重试。");
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_inviteLoad, request)) ShowInviteStatus(DesktopSync.Friendly(error));
        }
        finally
        {
            if (ReferenceEquals(_inviteLoad, request))
            {
                _inviteLoad = null;
                RefreshInvitesButton.IsEnabled = true;
            }
        }
    }

    private void ShowInviteStatus(string text)
    {
        InviteListStatus.Text = text;
        InviteListStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelInviteLoad()
    {
        if (_inviteLoad is not { } request) return;
        _inviteLoad = null;
        request.Cancel();
        RefreshInvitesButton.IsEnabled = true;
        ShowInviteStatus("");
    }

    private void ClearPasswords()
    {
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
    }

    private void ClearInvites()
    {
        CancelInviteLoad();
        InviteCodeBox.Clear();
        InviteCodeBox.Visibility = Visibility.Collapsed;
        InviteListText.Text = "";
    }

    private void Detach()
    {
        CancelInviteLoad();
        if (_sync is not { } sync) return;
        sync.Updated -= SyncChanged;
        sync.AccessChanged -= SyncChanged;
        _sync = null;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Detach();
        _canManage = null;
        BusyChanged = null;
        ClearPasswords();
        ClearInvites();
    }
}
