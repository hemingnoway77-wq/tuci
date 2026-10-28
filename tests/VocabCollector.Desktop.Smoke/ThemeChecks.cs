using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    private async Task CheckThemeBeforeConfigurationAsync(MainWindow main, ThemeManager themes, string config,
        Func<int> modelRequests)
    {
        var before = modelRequests();
        var toggle = Control<ToggleButton>(main, "ThemeToggleButton");
        Check(themes.CurrentTheme == AppTheme.WarmNeutral && toggle.IsChecked == false, "new preference defaults to warm neutral");
        await SwitchThemeAsync(toggle, themes, AppTheme.Pink);
        Check(await new AppearanceSettingsStore(config).LoadAsync() == AppTheme.Pink, "theme saves without configured API or vault");
        Check(!File.Exists(Path.Combine(config, "settings.json")), "theme preference does not create API settings");
        await SwitchThemeAsync(toggle, themes, AppTheme.WarmNeutral);
        Check(modelRequests() == before, "theme switching before configuration makes zero model requests");
    }

    private async Task CheckThemeSurfacesAsync(MainWindow main, ThemeManager themes, string config, string output,
        Func<int> modelRequests)
    {
        var before = modelRequests();
        var toggle = Control<ToggleButton>(main, "ThemeToggleButton");
        var word = Control<TextBox>(main, "WordBox").Text;
        var context = Control<TextBox>(main, "ContextBox").Text;
        var preview = Control<MarkdownPreview>(main, "NotePreview");
        var document = preview.Document;
        var text = preview.RenderedText;
        var neutralInk = ((SolidColorBrush)preview.Document.Foreground).Color;
        var apiConfigPath = Path.Combine(config, "settings.json");
        var apiConfig = await File.ReadAllTextAsync(apiConfigPath);

        Invoke(Control<Button>(main, "DailyReadingButton"));
        await Until(() => app.Windows.OfType<DailyReadingWindow>().Any(window => window.IsVisible));
        var reading = app.Windows.OfType<DailyReadingWindow>().Single(window => window.IsVisible);
        await Until(() => !((DailyReadingViewModel)reading.DataContext).IsBusy);
        var articles = ((DailyReadingViewModel)reading.DataContext).Articles.ToArray();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(main, Path.Combine(output, "theme-neutral-main.png"));
        Snapshot(reading, Path.Combine(output, "theme-neutral-reading.png"));

        await SwitchThemeAsync(toggle, themes, AppTheme.Pink);
        Check(ReferenceEquals(document, preview.Document) && preview.RenderedText == text, "theme switch preserves the rendered document and text");
        Check(((SolidColorBrush)preview.Document.Foreground).Color == Color.FromRgb(0x49, 0x33, 0x3F)
            && ((SolidColorBrush)preview.Document.Foreground).Color != neutralInk, "existing note changes color immediately to original pink ink");
        Check(ReferenceEquals(main.Background, app.Resources["ShellBackground"])
            && ReferenceEquals(reading.Background, app.Resources["ShellBackground"]), "already-open main and reading windows share the current palette");
        Check(((DailyReadingViewModel)reading.DataContext).Articles.SequenceEqual(articles), "live theme switch preserves reading articles");
        Snapshot(main, Path.Combine(output, "theme-pink-main.png"));
        Snapshot(reading, Path.Combine(output, "theme-pink-reading.png"));

        Invoke(Control<Button>(main, "SettingsButton"));
        await Until(() => app.Windows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        var settings = app.Windows.OfType<SettingsWindow>().Single(window => window.IsVisible);
        var model = Control<TextBox>(settings, "ModelBox").Text;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(settings, Path.Combine(output, "theme-pink-settings.png"));
        themes.Apply(AppTheme.WarmNeutral);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(settings.Background, app.Resources["ShellBackground"]), "open settings window changes theme immediately");
        Check(Control<TextBox>(settings, "ModelBox").Text == model, "theme change preserves settings draft");
        Snapshot(settings, Path.Combine(output, "theme-neutral-settings.png"));
        themes.Apply(AppTheme.Pink);
        settings.Close();

        await SwitchThemeAsync(toggle, themes, AppTheme.WarmNeutral);
        Check(((SolidColorBrush)preview.Document.Foreground).Color == neutralInk, "existing note returns to neutral palette without recreation");
        Check(Control<TextBox>(main, "WordBox").Text == word && Control<TextBox>(main, "ContextBox").Text == context,
            "theme toggles preserve word and context input");
        Check(await File.ReadAllTextAsync(apiConfigPath) == apiConfig, "theme toggles never rewrite encrypted API settings");
        Check(await new AppearanceSettingsStore(config).LoadAsync() == AppTheme.WarmNeutral, "neutral selection survives preference reload");
        Check(modelRequests() == before, "all live theme switches use zero additional model requests");
        reading.Close();
        main.Activate();
    }

    private static async Task SwitchThemeAsync(ToggleButton toggle, ThemeManager themes, AppTheme target)
    {
        if (toggle.IsChecked == (target == AppTheme.Pink)) return;
        Check(toggle.IsEnabled, "theme switch is enabled");
        var provider = (IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
        provider.Toggle();
        await Until(() => themes.CurrentTheme == target && toggle.IsEnabled);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
