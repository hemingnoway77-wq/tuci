using System.Windows;
using System.IO;
using System.Net.Http;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
namespace VocabCollector.Desktop;
public partial class App : Application
{
    private HttpClient? _client;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var store = new WindowsSettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector"));
        var loaded = await store.LoadAsync(CancellationToken.None);
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        var vaultStore = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(_client), vaultStore, TimeProvider.System), loaded.Settings, vaultStore);
        if (loaded.Warning.Length > 0) vm.Message = loaded.Warning;
        MainWindow = new MainWindow(vm, store, new WindowsNavigator()); MainWindow.Show();
        await vm.InitializeAsync(loaded.Warning);
    }
    protected override void OnExit(ExitEventArgs e) { _client?.Dispose(); base.OnExit(e); }
}
