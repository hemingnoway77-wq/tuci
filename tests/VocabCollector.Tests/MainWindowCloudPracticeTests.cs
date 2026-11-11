using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class MainWindowCloudPracticeTests
{
    [Fact]
    public void ADownloadedBankMakesTheRealLearningPageReadyWithoutCallingAi() => OnUi(() =>
    {
        using var fixture = new Fixture();
        Await(fixture.Sync.Tick());
        var learning = fixture.Learning;
        var note = Assert.Single(learning.AllNotes);
        Assert.Equal("cloud-introduced", learning.CloudNoteIds[note.Id]);
        Assert.Equal(1, fixture.Remote.BankCalls);
        var read = new PracticeContextCache(fixture.CachePath).ReadAsync("introduced", PracticeQuestionBank.Build(note)); Await(read);
        Assert.Equal(Remote.Contexts(), read.Result);
        AssertNoExposure(fixture);
        fixture.Find<RadioButton>("RoamingTab").IsChecked = true;
        Until(() => learning.HasExposure && !learning.IsBusy);
        ((RadioButton)learning.FindName("RoamingPracticeModeButton")).IsChecked = true;
        Until(() => learning.CurrentPracticeQuestion is not null);
        var question = learning.CurrentPracticeQuestion!;
        var expected = Remote.Contexts().Single(context => context.SenseId == question.SenseId);
        Assert.Contains(question.Kind == PracticeKind.Spelling ? "____" : "introduced", question.Prompt);
        Assert.Equal(expected.Chinese, question.ContextTranslation);
        Assert.False(((Button)learning.FindName("PracticePrepareButton")).IsVisible);
        Assert.Equal(0, fixture.Generator.Calls);
    });

    [Fact]
    public void AnExistingValidLocalBankKeepsItsExactBytesDuringTheRealCloudImport() => OnUi(() =>
    {
        using var fixture = new Fixture(); fixture.Remote.BankGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncing = fixture.Sync.Tick(); Await(Task.WhenAny(syncing, fixture.Remote.BankStarted.Task));
        Assert.True(fixture.Remote.BankStarted.Task.IsCompleted, "The real window must request its optional cloud banks after note sync.");
        var note = Assert.Single(fixture.Learning.AllNotes);
        var local = Remote.Contexts().Select(context => context with { Chinese = "本机原有并已确认的自然中文译文。" }).ToArray();
        Await(new PracticeContextCache(fixture.CachePath).SaveAsync("introduced", PracticeQuestionBank.Build(note), local));
        File.AppendAllText(fixture.CachePath, "\n  \n"); var original = File.ReadAllBytes(fixture.CachePath);
        fixture.Remote.BankGate.SetResult(); Await(syncing);
        Assert.Equal(original, File.ReadAllBytes(fixture.CachePath));
        Assert.Equal(0, fixture.Generator.Calls);
        AssertNoExposure(fixture);
    });

    [Fact]
    public void WordOrSenseMismatchesAreNotWrittenByTheRealWindowImporter() => OnUi(() =>
    {
        foreach (var wrongWord in new[] { true, false })
        {
            using var fixture = new Fixture();
            fixture.Remote.Bank = wrongWord ? fixture.Remote.Bank with { Word = "adopted" }
                : fixture.Remote.Bank with { Contexts = Remote.Contexts().Select(context => context with { SenseId = "outdated-" + context.SenseId }).ToArray() };
            Await(fixture.Sync.Tick());
            Assert.False(File.Exists(fixture.CachePath));
            Assert.Equal(0, fixture.Generator.Calls);
            Assert.StartsWith("上次同步", fixture.Sync.Status);
            AssertNoExposure(fixture);
        }
    });

    private static void AssertNoExposure(Fixture fixture)
    {
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.CachePath)!, "practice.json")));
        var roaming = Path.Combine(Path.GetDirectoryName(fixture.CachePath)!, "roaming.json");
        if (!File.Exists(roaming)) return;
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(roaming));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("CurrentExposure").ValueKind);
    }
    private static void OnUi(Action test) => ((Lazy<Dispatcher>)typeof(ReadingPageTests).GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(test);
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(condition(), "The isolated cloud-practice operation did not finish.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private sealed class Fixture : IDisposable
    {
        public TempVault Files { get; } = new();
        public Remote Remote { get; } = new();
        public NoGeneration Generator { get; } = new();
        public DesktopSync Sync { get; }
        public MainWindow Window { get; }
        public LearningPage Learning => (LearningPage)typeof(MainWindow).GetField("_learningPage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        public string CachePath => Path.Combine(Learning.LocalStateDirectory!, "practice-contexts.json");
        public Fixture()
        {
            var vault = new VaultOptions(Path.Combine(Files.Root, "vault"), "words", false); Directory.CreateDirectory(vault.RootPath);
            var settings = new AppSettings(new("", "", ""), vault);
            Sync = new DesktopSync(Path.Combine(Files.Root, "device"), Dispatcher.CurrentDispatcher, Remote);
            Window = new MainWindow(new MainViewModel(new NoCapture(), settings), new Store(settings), new WindowsNavigator(),
                sync: Sync, learningStateDirectory: Path.Combine(Files.Root, "guest-state"), settingsDirectory: Path.Combine(Files.Root, "settings"),
                automaticPracticeGenerator: Generator) { Width = 1000, Height = 760, ShowInTaskbar = false };
            var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze(); Window.Icon = icon;
            Window.Show();
            Await(Sync.SignIn("https://sync.fixture.invalid", "fixture", "fixture-password", vault));
            Await(Window.AccountTransition);
        }
        public T Find<T>(string name) where T : class => Assert.IsType<T>(Window.FindName(name));
        public void Dispose() { Remote.BankGate?.TrySetResult(); Window.Close(); Sync.Dispose(); Files.Dispose(); }
    }
    private sealed class NoGeneration : IPracticeContextGenerator
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<PracticeContext>> GenerateAsync(string word, IReadOnlyList<PracticeSense> senses, ApiOptions options, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Cloud import must not call AI"); }
    }
    private sealed class NoCapture : ICaptureService
    {
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Cloud import must not capture a word");
    }
    private sealed class Store(AppSettings settings) : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken ct) => Task.FromResult(new SettingsLoadResult(settings, ""));
        public Task SaveAsync(AppSettings value, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Remote : HttpMessageHandler
    {
        private const string Markdown = "---\nlemma: introduce\n---\n# introduced\n\n**释义**：\n\n> v. 介绍；使相识\n> v. 引进；首次采用\n";
        private readonly List<StateChange> _states = [];
        public CloudPracticeBank Bank { get; set; } = new("cloud-introduced", "introduced", Contexts());
        public int BankCalls { get; private set; }
        public TaskCompletionSource? BankGate { get; set; }
        public TaskCompletionSource BankStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static PracticeContext[] Contexts() => PracticeQuestionBank.Build(new LibraryNote("fixture", "introduced.md", "unused", Markdown, DateTimeOffset.UtcNow))
            .Select(sense => sense.Meaning.Contains("介绍", StringComparison.Ordinal)
                ? new PracticeContext(sense.Id, "She introduced me to her teacher.", "她把我介绍给了她的老师。")
                : new PracticeContext(sense.Id, "The company introduced a new policy.", "公司推行了一项新政策。")).ToArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/account/login") return Reply(new AccountIdentity("token-fixture", "vault-fixture", "user-fixture", "fixture", "device-fixture"));
            if (path == "/v1/account/me") return Reply(new AccountProfile("user-fixture", "fixture", "vault-fixture", "device-fixture"));
            if (path == "/v1/changes")
            {
                var after = long.Parse(request.RequestUri.Query.Split('=')[1]);
                return Reply(new SyncPage(after < 1 ? [new SyncChange(1, "cloud-introduced", 1, "introduced.md", Markdown, false)] : [], 1, false));
            }
            if (path == "/v1/state/changes")
            {
                var after = long.Parse(request.RequestUri.Query.Split('=')[1]);
                return Reply(new StatePage(_states.Where(state => state.Seq > after).ToArray(), _states.Count, false));
            }
            if (path == "/v1/state/operations")
            {
                var operation = (await request.Content!.ReadFromJsonAsync<StateOperation>(ct))!;
                _states.Add(new(_states.Count + 1, operation.Key, operation.Value, operation.Deleted));
                return Reply(new StateReceipt(operation.Key, _states.Count));
            }
            if (path == "/v1/practice/banks")
            {
                BankCalls++; BankStarted.TrySetResult();
                if (BankGate is { } gate) await gate.Task.WaitAsync(ct);
                return Reply(new CloudPracticeBankPage([Bank]));
            }
            throw new InvalidOperationException("Unexpected isolated cloud-practice request: " + path);
        }
        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
