using System.ComponentModel;
using System.Windows;

namespace VocabCollector.Desktop;

public partial class AccountLoginWindow : Window
{
    private const string DefaultServer = "https://101.133.134.128/tuci-sync/";
    private readonly DesktopSync _sync;
    private bool _busy, _closed, _registerMode;

    public AccountLoginWindow(DesktopSync sync)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        InitializeComponent();
        Resources.MergedDictionaries.Add(ThemePalette.Create(AppTheme.Mono));
        MaxHeight = Math.Clamp(SystemParameters.WorkArea.Height, MinHeight, MaxHeight);
        UrlBox.Text = string.IsNullOrWhiteSpace(sync.Server) ? DefaultServer : sync.Server;
        UsernameBox.Text = sync.Username;
        PasswordBox.Clear();
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Loaded += (_, _) => { if (UsernameBox.Text.Length == 0) UsernameBox.Focus(); else PasswordBox.Focus(); };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; PasswordBox.Clear(); ConfirmPasswordBox.Clear(); InviteBox.Clear(); };
    }

    public bool Authenticated => _sync.CanUseAccount;

    private async void Login_Click(object sender, RoutedEventArgs args) => await AuthenticateAsync(_registerMode);

    private void Register_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _closed) return;
        SetMode(!_registerMode);
    }

    private void SetMode(bool register)
    {
        _registerMode = register;
        var visible = register ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = InvitePanel.Visibility = visible;
        ModeTitle.Text = register ? "创建账号" : "登录账号";
        PasswordLabel.Text = register ? "密码（8–128 位）" : "密码";
        LoginButton.Content = register ? "创建账号" : "登录";
        RegisterButton.Content = register ? "返回登录" : "用邀请码注册";
        StatusText.Text = register ? "已有账号可返回登录。" : "登录后，词库与学习记录会随账号同步。";
        if (register) InviteBox.Focus(); else PasswordBox.Focus();
    }

    private async Task AuthenticateAsync(bool register)
    {
        if (_busy || _closed) return;
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            StatusText.Text = "请填写用户名和密码。";
            if (string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Focus(); else PasswordBox.Focus();
            return;
        }
        if (register && PasswordBox.Password.Length is < 8 or > 128)
        {
            StatusText.Text = "密码长度应为 8–128 位。";
            PasswordBox.Focus();
            return;
        }
        if (register && PasswordBox.Password != ConfirmPasswordBox.Password)
        {
            StatusText.Text = "两次输入的密码不一致。";
            ConfirmPasswordBox.Focus();
            return;
        }
        // Registration is invite-only; check here so nobody spends a round trip to learn it.
        if (register && string.IsNullOrWhiteSpace(InviteBox.Text))
        {
            StatusText.Text = "创建账号需要邀请码，请向管理员索取。已有账号请直接登录。";
            InviteBox.Focus();
            return;
        }
        SetBusy(true);
        StatusText.Text = register ? "正在创建账号…" : "正在登录…";
        var authenticated = false;
        try
        {
            await _sync.SignInPrivate(UrlBox.Text, UsernameBox.Text.Trim(), PasswordBox.Password, register,
                register ? InviteBox.Text.Trim() : null);
            authenticated = _sync.CanUseAccount;
            if (!authenticated) StatusText.Text = "账号尚未准备好，请重新登录。";
        }
        catch (Exception error) { if (!_closed) StatusText.Text = DesktopSync.Friendly(error); }
        finally { if (!_closed) SetBusy(false); }
        if (!authenticated || _closed) return;
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        InviteBox.Clear();
        // App uses a modal gate; modeless hosts and UI tests can also use this window.
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        CredentialsPanel.IsEnabled = !value;
        BusyIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!_busy) return;
        args.Cancel = true;
        StatusText.Text = "正在处理账号请求，请稍候。";
    }
}
