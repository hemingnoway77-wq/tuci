using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Updates;
using VocabCollector.Desktop.Updates;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunUpdateChecksAsync()
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        using var fixture = new SignedUpdateFixture();
        using var updateHttp = new HttpClient(fixture);
        var service = new DesktopUpdateService(new UpdateClient(updateHttp, SignedUpdateFixture.ManifestUri, fixture.PublicKey));
        var install = Path.Combine(run, "安装目录 with spaces"); Directory.CreateDirectory(install);
        // The launcher is always recorded. This file is deliberately not executable.
        await File.WriteAllTextAsync(Path.Combine(install, UpdateConfiguration.UpdaterExecutableName), "recording updater fixture");
        var launches = new List<(string Updater, string Request)>();
        Task Launch(string updater, string request) { launches.Add((updater, request)); return Task.CompletedTask; }

        using (var latest = new UpdateController(service, new Version(1, 2, 0), Path.Combine(run, "latest"), install, Launch))
        {
            await latest.CheckAsync();
            var latestWindow = new UpdateWindow(latest, () => null, () => throw new InvalidOperationException("A check must never restart."));
            latestWindow.Show();
            Check(latest.State == UpdateState.Current && Control<Button>(latestWindow, "InstallUpdateButton").Visibility == Visibility.Collapsed,
                "a signed current release shows the latest-version state without an install action");
            latestWindow.Close();
        }
        using (var retry = new UpdateController(service, new Version(1, 1, 0), Path.Combine(run, "retry"), install, Launch))
        {
            fixture.FailChecks = 1;
            var retryWindow = new UpdateWindow(retry, () => null, () => throw new InvalidOperationException("Retry must never restart."));
            retryWindow.Show();
            await retry.CheckAsync();
            Check(retry.State == UpdateState.Failed && Control<TextBlock>(retryWindow, "UpdateMessageText").Text.Contains("失败"), "HTTP failure is visible and retryable");
            Invoke(Control<Button>(retryWindow, "CheckUpdateButton"));
            await Until(() => retry.State == UpdateState.Available);
            Check(launches.Count == 0 && fixture.Downloads == 0, "a retried check never downloads or installs automatically");
            retryWindow.Close();
        }

        using (var controller = new UpdateController(service, new Version(1, 1, 0), Path.Combine(run, "explicit"), install, Launch))
        using (var noNetwork = new RoamingNoNetworkHandler())
        using (var modelHttp = new HttpClient(noNetwork))
        {
            var vault = Path.Combine(run, "isolated-vault"); Directory.CreateDirectory(Path.Combine(vault, "words"));
            var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", false));
            var vaultStore = new VaultStore();
            var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(modelHttp), vaultStore, TimeProvider.System), settings, vaultStore);
            var settingsStore = new UpdateNoSettingsStore();
            var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(Path.Combine(run, "theme"))); await themes.LoadAsync();
            var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var feed = new RecordingReadingService(); var translations = new RecordingWordTranslationService();
            var checksBefore = fixture.Checks;
            var main = new MainWindow(vm, settingsStore, new RecordingNavigator(), feed, themeManager: themes,
                learningStateDirectory: Path.Combine(run, "learning"), articleContentService: new RecordingArticleContentService(), wordTranslationService: translations,
                prepareLocalData: () => prepare.Task, updateController: controller);
            app.MainWindow = main; main.Show();
            try
            {
                await Until(() => Control<FrameworkElement>(main, "PreparationOverlay").IsVisible);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(fixture.Checks == checksBefore, "production-style update check waits for the real startup preparation");
                var parentDot = Control<FrameworkElement>(main, "SettingsUpdateDot");
                Check(parentDot.Visibility == Visibility.Collapsed && main.FindName("UpdateButton") is null && main.FindName("SyncButton") is null,
                    "the header starts without a notification and exposes both actions through settings");
                Invoke(Control<Button>(main, "SettingsButton"));
                await Until(() => app.Windows.OfType<SettingsWindow>().Any(w => w.IsVisible));
                var dialog = app.Windows.OfType<SettingsWindow>().Single(w => w.IsVisible);
                var categoryDot = Control<FrameworkElement>(dialog, "UpdateCategoryDot");
                var childDot = Control<FrameworkElement>(dialog, "CheckUpdateDot");
                Check(categoryDot.Visibility == Visibility.Collapsed && childDot.Visibility == Visibility.Collapsed,
                    "settings starts without an update notification");
                prepare.SetResult(); await main.PrepareForUseAsync();
                await Until(() => controller.State == UpdateState.Available);
                Check(parentDot.IsVisible && categoryDot.IsVisible && !childDot.IsVisible && !app.Windows.OfType<UpdateWindow>().Any(w => w.IsVisible),
                    "automatic signed check shows the parent and category dots without changing the open settings page");
                Check(feed.Calls == 0 && translations.Calls == 0 && noNetwork.Requests == 0 && settingsStore.Loads == 0 && settingsStore.Saves == 0,
                    "update startup does not read real settings or call models, translations or feeds");
                await SelectSettingsCategoryAsync(dialog, "ApiTab");
                var modelBox = Control<TextBox>(dialog, "ModelBox");
                var savedModel = modelBox.Text;
                modelBox.Text = "settings-draft";
                await SelectSettingsCategoryAsync(dialog, "UpdateTab");
                Check(categoryDot.IsVisible && childDot.IsVisible, "the selected update category displays both in-settings notifications");
                Snapshot(dialog, Path.Combine(run, "settings-update-available.png"));
                Invoke(Control<Button>(dialog, "CheckUpdateButton"));
                await Until(() => app.Windows.OfType<UpdateWindow>().Any(w => w.IsVisible));
                var window = app.Windows.OfType<UpdateWindow>().Single(w => w.IsVisible);
                Check(ReferenceEquals(window.Owner, dialog), "settings opens the real update window as its child");
                Check(Control<TextBlock>(window, "VersionText").Text.Contains("1.1.0") && Control<TextBlock>(window, "ReleaseNotesText").Text.Contains("隔离升级验收"),
                    "update window shows current version and the signed release notes");
                Snapshot(main, Path.Combine(run, "main-update-available.png"));
                Snapshot(window, Path.Combine(run, "update-available.png"));
                main.Width = main.MinWidth; main.UpdateLayout();
                Snapshot(main, Path.Combine(run, "main-update-minimum.png"));
                main.Width = 1220;
                Invoke(Control<Button>(window, "InstallUpdateButton"));
                await Until(() => controller.Message.Contains("未保存"));
                Check(launches.Count == 0 && fixture.Downloads == 0 && modelBox.Text == "settings-draft",
                    "explicit installation refuses to discard an unsaved settings draft");
                modelBox.Text = savedModel;
                vm.Word = "unsaved";
                Invoke(Control<Button>(window, "InstallUpdateButton"));
                await Until(() => controller.Message.Contains("输入"));
                Check(launches.Count == 0 && fixture.Downloads == 0 && vm.Word == "unsaved" && main.IsVisible,
                    "explicit installation refuses to discard a nonempty capture draft");
                vm.Word = "";
                Invoke(Control<Button>(window, "InstallUpdateButton"));
                await Until(() => launches.Count == 1 && !main.IsVisible);
                var recorded = launches.Single();
                var request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(recorded.Request))!;
                Check(File.Exists(recorded.Updater) && File.Exists(request.PackagePath) && request.ExpectedVersion == "1.2.0" && request.Restart && request.ParentProcessId == Environment.ProcessId,
                    "only an explicit click stages the updater, signed manifest and parent-bound install request before closing");
                Check(await File.ReadAllTextAsync(request.ManifestPath) == fixture.Manifest && fixture.Downloads == 1 && noNetwork.Requests == 0,
                    "recorded launch receives the authentic signed manifest and verified package without a real process start");
            }
            finally { prepare.TrySetResult(); main.Close(); }
        }
        await CheckCancelledUpdateWindowsAsync(run, install, fixture.Verified, launches);
        await CheckDownloadedUpdateGuardAsync(run, install, fixture.Verified);
        Check(UpdateController.ApplicationVersion.Revision < 0 && UpdateController.ApplicationVersion.Build >= 0, "desktop reports a three-component assembly version");
        Console.WriteLine("Update artifacts: " + run);
    }

    private async Task CheckCancelledUpdateWindowsAsync(string run, string install, VerifiedUpdate update, List<(string Updater, string Request)> launches)
    {
        var service = new DeferredUpdateService(update);
        using var controller = new UpdateController(service, new Version(1, 1, 0), Path.Combine(run, "cancel"), install,
            (updater, request) => { launches.Add((updater, request)); return Task.CompletedTask; });
        await controller.CheckAsync();
        var window = new UpdateWindow(controller, () => null, () => throw new InvalidOperationException("Cancelled download cannot close the app."));
        window.Show();
        Invoke(Control<Button>(window, "InstallUpdateButton"));
        await Until(() => service.Downloads.Count == 1);
        var old = service.Downloads[0];
        old.Progress.Report(new UpdateDownloadProgress(25, 100));
        await Until(() => controller.Progress?.ReceivedBytes == 25);
        Check(Control<TextBlock>(window, "ProgressText").Text.Length > 0 && Control<ProgressBar>(window, "DownloadProgress").Value == 25,
            "download progress reaches the real WPF controls");
        var launchCount = launches.Count;
        Invoke(Control<Button>(window, "CancelUpdateButton"));
        await Until(() => old.Token.IsCancellationRequested && !controller.IsWorking);
        Check(old.Token.IsCancellationRequested && !controller.IsWorking, "cancel button cancels the operation and enables retry");
        window.Close();
        var replacement = new UpdateWindow(controller, () => null, () => throw new InvalidOperationException("Cancelled download cannot close the app."));
        replacement.Show();
        Invoke(Control<Button>(replacement, "InstallUpdateButton"));
        await Until(() => service.Downloads.Count == 2);
        var current = service.Downloads[1];
        current.Progress.Report(new UpdateDownloadProgress(60, 100));
        await Until(() => controller.Progress?.ReceivedBytes == 60);
        old.Progress.Report(new UpdateDownloadProgress(99, 100)); old.Completion.SetResult(Path.Combine(old.Directory, "old.zip"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(controller.State == UpdateState.Downloading && controller.Progress?.ReceivedBytes == 60 && launches.Count == launchCount,
            "a cancelled old window's late progress and completion cannot overwrite the new operation or launch an updater");
        replacement.Close();
        Check(current.Token.IsCancellationRequested, "closing the update window cancels its active download");
        current.Completion.SetResult(Path.Combine(current.Directory, "closed.zip"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(launches.Count == launchCount && !app.Windows.OfType<UpdateWindow>().Any(w => w.IsVisible), "closed windows do not reopen or install after a late download callback");
    }

    private static async Task CheckDownloadedUpdateGuardAsync(string run, string install, VerifiedUpdate update)
    {
        var service = new DeferredUpdateService(update); var blocked = false; var launches = 0; var failLaunch = true;
        using var controller = new UpdateController(service, new Version(1, 1, 0), Path.Combine(run, "guard"), install, (_, _) =>
        {
            launches++;
            if (failLaunch) throw new IOException("recorded launch failure");
            return Task.CompletedTask;
        });
        await controller.CheckAsync();
        var pending = controller.DownloadAndInstallAsync(() => blocked ? "收录仍在运行，请等待。" : null);
        await Until(() => service.Downloads.Count == 1);
        var download = service.Downloads.Single();
        var packagePath = Path.Combine(download.Directory, "package.zip"); await File.WriteAllTextAsync(packagePath, "verified download mock");
        blocked = true; download.Completion.SetResult(packagePath);
        Check(!await pending && controller.State == UpdateState.Ready && launches == 0,
            "a capture started during download blocks installation after completion and retains the package");
        blocked = false;
        Check(!await controller.DownloadAndInstallAsync(() => null) && controller.State == UpdateState.Failed && launches == 1,
            "a failed process start keeps the application available and allows another explicit retry");
        failLaunch = false;
        Check(await controller.DownloadAndInstallAsync(() => null) && service.Downloads.Count == 1 && launches == 2,
            "explicit retry after capture completion reuses the downloaded package");
    }

    private sealed class UpdateNoSettingsStore : ISettingsStore
    {
        public int Loads, Saves;
        public Task<SettingsLoadResult> LoadAsync(CancellationToken ct) { Loads++; throw new InvalidOperationException("Update smoke cannot load settings."); }
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Saves++; throw new InvalidOperationException("Update smoke cannot save settings."); }
    }
    private sealed class DeferredUpdateService(VerifiedUpdate update) : IDesktopUpdateService
    {
        public sealed record Download(string Directory, IProgress<UpdateDownloadProgress> Progress, CancellationToken Token, TaskCompletionSource<string> Completion);
        public List<Download> Downloads { get; } = [];
        public Task<VerifiedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken) => Task.FromResult<VerifiedUpdate?>(update);
        public Task<string> DownloadAsync(VerifiedUpdate release, string directory, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Downloads.Add(new(directory, progress, cancellationToken, completion));
            return completion.Task; // Intentionally ignores cancellation to exercise stale callbacks.
        }
    }
    private sealed class SignedUpdateFixture : HttpMessageHandler
    {
        public static readonly Uri ManifestUri = new("https://update-smoke.invalid/tuci/stable.json");
        private readonly byte[] _package = Enumerable.Range(0, 131072).Select(index => (byte)(index % 251)).ToArray();
        public string PublicKey { get; }
        public string Manifest { get; }
        public VerifiedUpdate Verified { get; }
        public int Checks, Downloads, FailChecks;
        public SignedUpdateFixture()
        {
            using var rsa = RSA.Create(3072); PublicKey = rsa.ExportSubjectPublicKeyInfoPem();
            var release = new UpdateRelease("1.2.0", "win-x64", new Uri(ManifestUri, "packages/tuci-1.2.0.zip"), _package.Length,
                Convert.ToHexStringLower(SHA256.HashData(_package)), ["隔离升级验收", "保留词库与设置。"], DateTimeOffset.Parse("2026-11-07T00:00:00Z"));
            var payload = JsonSerializer.SerializeToUtf8Bytes(release, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Manifest = JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) });
            Verified = new UpdateManifestVerifier(PublicKey, ManifestUri).Verify(Manifest);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri == ManifestUri)
            {
                Checks++;
                if (FailChecks-- > 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(Manifest) });
            }
            if (request.RequestUri == Verified.Release.PackageUrl)
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(_package) });
            }
            throw new InvalidOperationException("Unexpected smoke update URL: " + request.RequestUri);
        }
    }
}
