using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
namespace VocabCollector.Desktop.Smoke;

public static class Program
{
    [STAThread] public static int Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var exitCode = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await new SmokeRunner(app).RunChecksAsync(); Console.WriteLine("PASS: real WPF controls, encrypted settings, HTTP capture, existing links, duplicate, 401, malformed JSON, cancellation and screenshots"); exitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle); }
        }));
        // Pump the real application's dispatcher without invoking its production startup/configuration path.
        Dispatcher.Run(); return exitCode;
    }
}
public sealed class SmokeRunner(App app)
{
    public async Task RunChecksAsync()
    {
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "smoke"); Directory.CreateDirectory(output);
        var run = Path.Combine(output, Guid.NewGuid().ToString("N")); var vault = Path.Combine(run, "中文 vault #复习"); Directory.CreateDirectory(vault);
        var configDir = Path.Combine(run, "config"); var settingsStore = new WindowsSettingsStore(configDir);
        var bindings = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(bindings); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        await using var server = new MockApiServer();
        using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), new VaultStore(), TimeProvider.System), AppSettings.Default);
        var navigator = new RecordingNavigator(); var main = new MainWindow(vm, settingsStore, navigator); app.MainWindow = main; main.Show();
        Invoke(Control<Button>(main, "SettingsButton"));
        await Until(() => app.Windows.OfType<SettingsWindow>().Any(w => w.IsVisible));
        var dialog = app.Windows.OfType<SettingsWindow>().Single(w => w.IsVisible);
        Control<TextBox>(dialog,"BaseUrlBox").Text = server.BaseUrl;
        Control<PasswordBox>(dialog,"KeyBox").Password = "sk-smoke-canary-secret";
        Control<TextBox>(dialog,"ModelBox").Text = "mock-model";
        Control<TextBox>(dialog,"VaultBox").Text = vault;
        Control<TextBox>(dialog,"SubfolderBox").Text = "英语单词";
        Control<CheckBox>(dialog,"ClassifyBox").IsChecked = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(dialog, Path.Combine(output,"settings.png"));
        Invoke(Control<Button>(dialog,"SaveButton"));
        await Until(() => !dialog.IsVisible && vm.Settings.Api.Model == "mock-model");
        var loaded = await settingsStore.LoadAsync(default);
        Check(loaded.Settings == vm.Settings, "saved settings persist");
        Check(!File.ReadAllText(Path.Combine(configDir,"settings.json")).Contains("sk-smoke-canary-secret"), "key is encrypted on disk");
        var prior = Path.Combine(vault,"英语单词","名词","resilience.md"); Directory.CreateDirectory(Path.GetDirectoryName(prior)!); await File.WriteAllTextAsync(prior,"handwritten historical notes");
        Control<TextBox>(main,"WordBox").Text = "resilient";
        Control<TextBox>(main,"ContextBox").Text = "She remained resilient after the setback.";
        Invoke(Control<Button>(main,"GenerateButton"));
        await Until(() => !vm.IsBusy && vm.Notes.Count == 1);
        var note = vm.Notes[0]; var text = await File.ReadAllTextAsync(note.AbsolutePath);
        Check(note.VaultRelativePath == "英语单词/形容词/resilient.md", "part of speech directory");
        Check(text.Contains("[[英语单词/名词/resilience|resilience]]") && text.Contains("She remained resilient after the setback."), "existing link and source context");
        Check(text == vm.Markdown && !text.Contains('\r'), "displayed note matches UTF-8 LF file");
        await File.WriteAllTextAsync(Path.Combine(output,"sample.md"), text);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Snapshot(main,Path.Combine(output,"main.png"));
        main.Width=main.MinWidth; main.Height=main.MinHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        main.UpdateLayout(); Snapshot(main,Path.Combine(output,"main-minimum-before-scroll.png"));
        Check(!VisibleBounds(Control<TextBox>(main,"ContextBox"),main).IntersectsWith(VisibleBounds(Control<Button>(main,"GenerateButton"),main)),"minimum-size input must not overlap generate button");
        var inputs=Control<ScrollViewer>(main,"InputScroll"); inputs.ScrollToBottom();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(VisibleBounds(Control<TextBox>(main,"ContextBox"),main).Height >= Control<TextBox>(main,"ContextBox").ActualHeight-1,"source context remains reachable at minimum size");
        Snapshot(main,Path.Combine(output,"main-minimum.png")); main.Width=1100; main.Height=760;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Invoke(Control<Button>(main,"OpenNoteButton")); await Until(() => navigator.Opened == note.AbsolutePath);
        var before = server.Requests; Invoke(Control<Button>(main,"GenerateButton"));
        await Until(() => !vm.IsBusy && vm.Message.Contains("已经收录"));
        Check(server.Requests == before && await File.ReadAllTextAsync(note.AbsolutePath) == text, "duplicate avoids API and keeps handwritten content");
        server.Mode = ApiMode.Unauthorized; Control<TextBox>(main,"WordBox").Text = "denied"; Invoke(Control<Button>(main,"GenerateButton"));
        await Until(() => !vm.IsBusy && vm.Message.Contains("鉴权")); Check(!vm.Message.Contains("sk-smoke-canary-secret"), "401 secret redaction");
        server.Mode = ApiMode.Malformed; Control<TextBox>(main,"WordBox").Text = "malformed"; Invoke(Control<Button>(main,"GenerateButton"));
        await Until(() => !vm.IsBusy && vm.Message.Contains("兼容性"));
        server.Mode = ApiMode.Slow; Control<TextBox>(main,"WordBox").Text = "cancelled"; before = server.Requests; Invoke(Control<Button>(main,"GenerateButton"));
        await Until(() => vm.IsBusy && server.Requests > before); Check(!Control<Button>(main,"SettingsButton").IsEnabled, "settings disabled during request");
        Invoke(Control<Button>(main,"CancelButton")); await Until(() => !vm.IsBusy && vm.Message.Contains("取消"));
        Check(vm.Word == "cancelled" && Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Length == 2, "errors and cancellation create no note");
        Check(await File.ReadAllTextAsync(prior) == "handwritten historical notes", "historical file unchanged");
        Check(bindings.Errors.Count == 0, "no WPF binding errors: " + string.Join(" | ",bindings.Errors));
        await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),$"PASS\nVault: {vault}\nSample: {note.AbsolutePath}\nRequests: {server.Requests}\nBinding errors: 0\n");
        main.Close(); Console.WriteLine("Artifacts: " + output);
    }
    private static T Control<T>(Window window,string name) where T : FrameworkElement => (T)(window.FindName(name) ?? throw new Exception("Missing control " + name));
    private static void Invoke(Button button)
    {
        Check(button.IsEnabled,"button enabled: " + button.Name);
        var provider = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!; provider.Invoke();
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch=Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timed out waiting for UI state"); await Task.Delay(20); }
    }
    private static void Check(bool valid,string message) { if (!valid) throw new InvalidOperationException(message); }
    private static Rect VisibleBounds(FrameworkElement element,Window root)
    {
        var result=element.TransformToAncestor(root).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight));
        for(DependencyObject? current=element; current!=null && current!=root; current=VisualTreeHelper.GetParent(current))
        {
            if(current is FrameworkElement frame && frame.ClipToBounds)
                result.Intersect(frame.TransformToAncestor(root).TransformBounds(new Rect(0,0,frame.ActualWidth,frame.ActualHeight)));
            if(current is Visual visual && VisualTreeHelper.GetClip(visual) is Geometry clip)
                result.Intersect(visual.TransformToAncestor(root).TransformBounds(clip.Bounds));
        }
        return result;
    }
    private static void Snapshot(Window window,string path)
    {
        window.UpdateLayout(); var visual=(FrameworkElement)window.Content;
        var margin=visual.Margin; var width=visual.ActualWidth+margin.Left+margin.Right; var height=visual.ActualHeight+margin.Top+margin.Bottom;
        var drawing=new DrawingVisual();
        using(var context=drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? Brushes.White,null,new Rect(0,0,width,height));
            context.DrawRectangle(new VisualBrush(visual),null,new Rect(margin.Left,margin.Top,visual.ActualWidth,visual.ActualHeight));
        }
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(width),(int)Math.Ceiling(height),96,96,PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); encoder.Save(stream);
    }
    private sealed class RecordingNavigator : IAppNavigator
    {
        public string? Opened { get; private set; }
        public void OpenNote(NoteLocation note) => Opened=note.AbsolutePath;
        public void OpenFolder(NoteLocation note) => Opened=note.AbsolutePath;
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; }=[];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message)=>Write(message);
    }
}
