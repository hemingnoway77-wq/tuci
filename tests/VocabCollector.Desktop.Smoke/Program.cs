using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Preview;
namespace VocabCollector.Desktop.Smoke;

public static class Program
{
    [STAThread] public static int Main()
    {
        var app = new App(false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var exitCode = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await new SmokeRunner(app).RunChecksAsync(); Console.WriteLine("PASS: real WPF controls, rendered note preview, local preview links, auto-hiding scrollbars, app icon, encrypted settings, HTTP capture, local link refresh without API, duplicate, 401, malformed JSON, cancellation and screenshots"); exitCode = 0; }
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
        var vaultStore = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), vaultStore, TimeProvider.System), AppSettings.Default, vaultStore);
        var navigator = new RecordingNavigator(); var main = new MainWindow(vm, settingsStore, navigator); app.MainWindow = main; main.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(main.Icon != null, "main window has the application icon");
        Check(Control<FrameworkElement>(main,"PreviewEmptyState").IsVisible, "empty preview explains what to do before a word is generated");
        var preview = Control<MarkdownPreview>(main,"NotePreview");
        Check(string.IsNullOrWhiteSpace(preview.RenderedText), "empty preview contains no raw template or placeholder code");
        Snapshot(main,Path.Combine(output,"main-empty.png"));
        Invoke(Control<Button>(main, "SettingsButton"));
        await Until(() => app.Windows.OfType<SettingsWindow>().Any(w => w.IsVisible));
        var dialog = app.Windows.OfType<SettingsWindow>().Single(w => w.IsVisible);
        Check(dialog.Icon != null, "settings window uses the same application icon");
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
        Directory.CreateDirectory(Path.Combine(vault, "英语单词", ".obsidian"));
        var prior = Path.Combine(vault,"英语单词","名词","resilience.md"); Directory.CreateDirectory(Path.GetDirectoryName(prior)!); await File.WriteAllTextAsync(prior,"handwritten historical notes");
        Control<TextBox>(main,"WordBox").Text = "resilient";
        Control<TextBox>(main,"ContextBox").Text = "She remained resilient after the setback.";
        Invoke(Control<Button>(main,"GenerateButton"));
        await Until(() => vm.IsBusy && vm.Message.Contains("AI 正在生成"));
        await Until(() => !vm.IsBusy && vm.Notes.Count == 1);
        var note = vm.Notes[0]; var text = await File.ReadAllTextAsync(note.AbsolutePath);
        Check(note.VaultRelativePath == "英语单词/形容词/resilient.md", "part of speech directory");
        Check(text.Contains("[[名词/resilience|resilience]]") && text.Contains("She remained resilient after the setback."), "links use the actual nested Obsidian vault root");
        Check(text == vm.Markdown && !text.Contains('\r'), "displayed note matches UTF-8 LF file");
        Check(text.Contains("**词根**：sal / sult（跳跃，源自拉丁语 salire）"), "root group is shown below pronunciation");
        Check(text.Contains("**释义**：\n\n> adj. 有韧性的  \n> adj. 能迅速恢复的"), "definitions use separate quoted lines");
        Check(!text.Contains("<!--") && !text.Contains("sha256=") && !text.Contains("vocabcollector:"), "notes and preview contain no program management labels");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!Control<FrameworkElement>(main,"PreviewEmptyState").IsVisible, "completed note replaces the empty preview");
        Check(preview.Markdown == text, "rendered preview receives the exact saved note");
        Check(preview.RenderedText.Contains("resilient") && preview.RenderedText.Contains("有韧性的") && preview.RenderedText.Contains("能迅速恢复的") && preview.RenderedText.Contains("sal / sult"), "preview renders the word, stacked meanings and root group");
        Check(!preview.RenderedText.Contains("type: english-word") && !preview.RenderedText.Contains("primary_pos:") && !preview.RenderedText.Contains("created:") && !preview.RenderedText.Contains("**") && !preview.RenderedText.Contains("[[") && !preview.RenderedText.Contains("####") && !preview.RenderedText.Contains("sha256="), "preview hides front matter, formatting delimiters, wiki syntax and technical metadata");
        var relationLink = Links(preview.Document).Single(link => link.Tag is PreviewLink target && target.Target.EndsWith("/resilience", StringComparison.Ordinal));
        relationLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent,relationLink));
        await Until(() => navigator.Opened == prior);
        Check(server.Requests == 1, "clicking an existing note in the preview opens locally without another API request");
        await File.WriteAllTextAsync(Path.Combine(output,"sample.md"), text);
        Check(!text.Contains("[[名词/economy|economy]]"), "a missing example-word card is plain text");
        Check(text.Contains("**词形**：resilient的副词是resiliently。"), "a missing word-form card stays plain even beside Chinese text");
        var manualTarget = Path.Combine(vault, "英语单词", "名词", "economy.md");
        await File.WriteAllTextAsync(manualTarget, "manually added target note");
        var rootTarget = Path.Combine(vault, "英语单词", "词根", "sal.md");
        Directory.CreateDirectory(Path.GetDirectoryName(rootTarget)!);
        await File.WriteAllTextAsync(rootTarget, "manually added root card");
        var formTarget = Path.Combine(vault, "英语单词", "副词", "resiliently.md");
        Directory.CreateDirectory(Path.GetDirectoryName(formTarget)!);
        await File.WriteAllTextAsync(formTarget, "manually added word-form card");
        var beforeRefresh = server.Requests;
        var legacyPath = Path.Combine(vault, "英语单词", "动词", "introduced.md");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var legacyBody = "#### 例句\n1. A teacher can help.\n   - 老师能帮忙。";
        var legacyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyBody)));
        var legacyOriginal = "---\ntype: english-word\nword: introduced\n---\n# introduced\n" + $"<!-- vocabcollector:links:v1 mode=examples sha256={legacyHash} -->\n{legacyBody}\n<!-- /vocabcollector:links -->\n\n#### 我的笔记\nMy personal note.\n";
        await File.WriteAllTextAsync(legacyPath, legacyOriginal);
        Invoke(Control<Button>(main,"RefreshLinksButton"));
        await Until(() => !vm.IsBusy && vm.Message.Contains("链接刷新完成"));
        text = await File.ReadAllTextAsync(note.AbsolutePath);
        Check(text.Contains("[[名词/economy|economy]]"), "manual refresh links an old example after a target is added");
        Check(text.Contains("**词根**：[[词根/sal|sal / sult]]（跳跃，源自拉丁语 salire）"), "future root card backfills the root group");
        Check(text.Contains("**词形**：resilient的副词是[[副词/resiliently|resiliently]]。"), "future form card backfills word-form paragraph with the actual vault-relative path");
        Check(await File.ReadAllTextAsync(formTarget) == "manually added word-form card", "manual word-form target stays unchanged");
        Check(await File.ReadAllTextAsync(rootTarget) == "manually added root card", "root card stays unchanged");
        Check(vm.Markdown == text, "manual refresh reloads the displayed note from disk");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(preview.Markdown == text && !preview.RenderedText.Contains("[["), "link refresh also updates the rendered reading preview");
        var formLink = Links(preview.Document).Single(link => link.Tag is PreviewLink target && target.Target.EndsWith("/resiliently", StringComparison.Ordinal));
        formLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent,formLink));
        await Until(() => navigator.Opened == formTarget);
        var rootLink = Links(preview.Document).Single(link => new TextRange(link.ContentStart,link.ContentEnd).Text == "sal / sult");
        rootLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent,rootLink));
        await Until(() => navigator.Opened == rootTarget);
        Check(server.Requests == beforeRefresh, "refresh uses zero model requests");
        Check(await File.ReadAllTextAsync(manualTarget) == "manually added target note", "unmanaged manual note stays unchanged");
        var migrated = await File.ReadAllTextAsync(legacyPath);
        Check(!migrated.Contains("vocabcollector:") && migrated.Contains(legacyBody) && migrated.Contains("My personal note."), "old program labels are removed without losing body or personal notes");
        Check(Directory.GetFiles(Path.Combine(vault, "英语单词", ".vocabcollector", "backups"), "*.bak").Any(path => File.ReadAllText(path) == legacyOriginal), "legacy cleanup preserves an exact original backup");
        await File.WriteAllTextAsync(Path.Combine(output,"sample-refreshed.md"), text);
        var previewScroll = Descendants<ScrollViewer>(preview).Single();
        Check(AutoHideScrollbars.GetIsEnabled(previewScroll), "reading preview enables the shared scrollbar behavior");
        Check(previewScroll.ScrollableHeight > 0, "full rendered note remains scrollable");
        var previewBar = Descendants<ScrollBar>(previewScroll).Single(bar => bar.Orientation == Orientation.Vertical);
        await Until(() => !AutoHideScrollbars.GetIsActive(previewScroll));
        Check(previewBar.Opacity == 0 || previewBar.IsMouseOver || previewBar.IsMouseCaptureWithin, "preview scrollbar is hidden while idle");
        previewScroll.ScrollToEnd();
        await Until(() => previewScroll.VerticalOffset > 0 && AutoHideScrollbars.GetIsActive(previewScroll));
        Check(previewBar.Opacity == 1, "scrolling reveals the preview scrollbar");
        Snapshot(main,Path.Combine(output,"main-scrolled.png"));
        await Until(() => !AutoHideScrollbars.GetIsActive(previewScroll));
        Check(previewBar.Opacity == 0 || previewBar.IsMouseOver || previewBar.IsMouseCaptureWithin, "preview scrollbar hides again after scrolling stops");
        previewScroll.ScrollToTop();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Until(() => !AutoHideScrollbars.GetIsActive(previewScroll));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Snapshot(main,Path.Combine(output,"main.png"));
        main.Width=main.MinWidth; main.Height=main.MinHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        main.UpdateLayout(); Snapshot(main,Path.Combine(output,"main-minimum-before-scroll.png"));
        Check(!VisibleBounds(Control<Button>(main,"RefreshLinksButton"),main).IntersectsWith(VisibleBounds(Control<Button>(main,"SettingsButton"),main)),"minimum-size refresh and settings buttons do not overlap");
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
        Check(!Control<Button>(main,"RefreshLinksButton").IsEnabled, "link refresh disabled during request");
        Invoke(Control<Button>(main,"CancelButton")); await Until(() => !vm.IsBusy && vm.Message.Contains("取消"));
        Check(vm.Word == "cancelled" && Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Length == 6, "errors and cancellation create no note");
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
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index=0; index<VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static IEnumerable<Hyperlink> Links(FlowDocument document) => Links(document.Blocks);
    private static IEnumerable<Hyperlink> Links(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph)
                foreach (var link in Links(paragraph.Inlines)) yield return link;
            if (block is Section section)
                foreach (var link in Links(section.Blocks)) yield return link;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var link in Links(item.Blocks)) yield return link;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var link in Links(cell.Blocks)) yield return link;
        }
    }
    private static IEnumerable<Hyperlink> Links(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink link) yield return link;
            if (inline is Span span)
                foreach (var nested in Links(span.Inlines)) yield return nested;
        }
    }
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
            var brush = new VisualBrush(visual) { ViewboxUnits=BrushMappingMode.Absolute, Viewbox=new Rect(margin.Left,margin.Top,visual.ActualWidth,visual.ActualHeight), Stretch=Stretch.Fill };
            context.DrawRectangle(brush,null,new Rect(margin.Left,margin.Top,visual.ActualWidth,visual.ActualHeight));
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
