using System.Windows;
using System.IO;
using System.Net.Http;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Library;
using VocabCollector.Core.Updates;
using VocabCollector.Desktop.Updates;
namespace VocabCollector.Desktop;
public partial class App : Application
{
    private readonly bool _runProductionStartup;
    public App() : this(true) { }
    public App(bool runProductionStartup) => _runProductionStartup = runProductionStartup;
    private HttpClient? _client;
    private DesktopSync? _sync;
    private HttpClient? _updateHttp;
    private UpdateController? _updates;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_runProductionStartup) return;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector");
        _sync = new DesktopSync(settingsDirectory, Dispatcher);
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        _updateHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        var host = new AccountWindowHost(_sync, _ =>
        {
            new ThemeManager(Resources).Apply(AppTheme.WarmNeutral);
            var login = new AccountLoginWindow(_sync);
            MainWindow = login;
            return Task.FromResult(login.ShowDialog() == true && _sync.CanUseAccount);
        }, async ct =>
        {
            var profile = _sync.AccountStateDirectory ?? throw new InvalidOperationException("请先登录账号。");
            var store = new WindowsSettingsStore(profile);
            var loaded = await store.LoadAsync(ct);
            var settings = loaded.Settings with { Vault = _sync.ActiveVault ?? throw new InvalidOperationException("账号词库尚未准备好。") };
            var themes = new ThemeManager(Resources, new AppearanceSettingsStore(profile));
            await themes.LoadAsync(ct);
            var vaultStore = new VaultStore();
            var diagnostics = new FileAnalysisDiagnostics(Path.Combine(profile, "logs"));
            var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(_client, diagnostics), vaultStore, TimeProvider.System), settings, vaultStore);
            _updates = new UpdateController(new DesktopUpdateService(new UpdateClient(_updateHttp, UpdateConfiguration.ManifestUri, UpdateConfiguration.PublicKeyPem)));
            return new MainWindow(vm, store, new WindowsNavigator(), themeManager: themes, sync: _sync,
                prepareLocalData: () => vm.InitializeAsync(loaded.Warning), updateController: _updates,
                settingsDirectory: settingsDirectory, requireAccount: true,
                automaticPracticeGenerator: new OpenAiPracticeContextGenerator(_client));
        }, window => MainWindow = window);
        try { await host.RunAsync(); }
        finally
        {
            try { await _sync.FinishRevocationsAsync(); }
            finally { Shutdown(); }
        }
    }
    protected override void OnExit(ExitEventArgs e) { _updates?.Dispose(); _updateHttp?.Dispose(); _sync?.Dispose(); _client?.Dispose(); base.OnExit(e); }
}
