using System.Windows;
using System.IO;
using System.Net.Http;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
namespace VocabCollector.Desktop;
public partial class App : Application
{
    private readonly bool _runProductionStartup;
    public App() : this(true) { }
    public App(bool runProductionStartup) => _runProductionStartup = runProductionStartup;
    private HttpClient? _client;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_runProductionStartup) return;
        var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector");
        var themes = new ThemeManager(Resources, new AppearanceSettingsStore(settingsDirectory));
        await themes.LoadAsync();
        var store = new WindowsSettingsStore(settingsDirectory);
        var loaded = await store.LoadAsync(CancellationToken.None);
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        var vaultStore = new VaultStore();
        var diagnostics = new FileAnalysisDiagnostics(Path.Combine(settingsDirectory, "logs"));
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(_client, diagnostics), vaultStore, TimeProvider.System), loaded.Settings, vaultStore);
        if (loaded.Warning.Length > 0) vm.Message = loaded.Warning;
        MainWindow = new MainWindow(vm, store, new WindowsNavigator(), themeManager: themes); MainWindow.Show();
        await vm.InitializeAsync(loaded.Warning);
    }
    protected override void OnExit(ExitEventArgs e) { _client?.Dispose(); base.OnExit(e); }
}
