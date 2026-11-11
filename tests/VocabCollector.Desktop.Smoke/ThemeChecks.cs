using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
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
        Check(themes.CurrentTheme == AppTheme.WarmNeutral, "new preference defaults to warm");
        Check(Control<RadioButton>(main, "ThemeWarmOption").IsChecked == true, "warm option reflects the default theme");
        await SwitchThemeAsync(Control<RadioButton>(main, "ThemeRoseOption"), themes, AppTheme.Pink);
        Check(await new AppearanceSettingsStore(config).LoadAsync() == AppTheme.Pink, "theme saves without configured API or vault");
        Check(!File.Exists(Path.Combine(config, "settings.json")), "theme preference does not create API settings");
        await SwitchThemeAsync(Control<RadioButton>(main, "ThemeWarmOption"), themes, AppTheme.WarmNeutral);
        await SwitchThemeAsync(Control<RadioButton>(main, "ThemeMonoOption"), themes, AppTheme.Mono);
        Check(Control<RadioButton>(main, "ThemeMonoOption").IsChecked == true, "theme option follows the live palette");
        await SwitchThemeAsync(Control<RadioButton>(main, "ThemeWarmOption"), themes, AppTheme.WarmNeutral);
        Check(Control<RadioButton>(main, "ThemeWarmOption").IsChecked == true, "default warm theme is restored for later screens");
        Check(modelRequests() == before, "theme switching before configuration makes zero model requests");
    }

    private async Task CheckThemeSurfacesAsync(MainWindow main, ThemeManager themes, string config, string output,
        Func<int> modelRequests)
    {
        var before = modelRequests();
        var word = Control<TextBox>(main, "WordBox").Text;
        var context = Control<TextBox>(main, "ContextBox").Text;
        var preview = Control<MarkdownPreview>(main, "NotePreview");
        var document = preview.Document;
        var text = preview.RenderedText;
        var warmInk = ((SolidColorBrush)preview.Document.Foreground).Color;
        var apiConfigPath = Path.Combine(config, "settings.json");
        var apiConfig = await File.ReadAllTextAsync(apiConfigPath);

        await SwitchThemeAsync(Control<RadioButton>(main, "ThemeRoseOption"), themes, AppTheme.Pink);
        Check(ReferenceEquals(document, preview.Document) && preview.RenderedText == text,
            "theme switch preserves the rendered document and text");
        Check(((SolidColorBrush)preview.Document.Foreground).Color == Color.FromRgb(0x33, 0x24, 0x2B)
            && ((SolidColorBrush)preview.Document.Foreground).Color != warmInk,
            "existing note changes color immediately to the rose ink");
        Check(ReferenceEquals(main.Background, app.Resources["ShellBackground"]), "main window follows the live palette");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(main, Path.Combine(output, "theme-pink-main.png"));

        var page = await OpenReadingAsync(main);
        var reading = (DailyReadingViewModel)page.DataContext;
        var articles = reading.Articles.ToArray();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(main, Path.Combine(output, "theme-pink-reading.png"));

        await SwitchThemeAsync(Control<RadioButton>(main, "ThemeWarmOption"), themes, AppTheme.WarmNeutral);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(((SolidColorBrush)preview.Document.Foreground).Color == warmInk,
            "existing note changes back to the warm ink");
        Check(reading.Articles.SequenceEqual(articles), "live theme switch preserves reading articles");
        Snapshot(main, Path.Combine(output, "theme-warm-reading.png"));

        await SelectPageAsync(main, "CaptureTab");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(main, Path.Combine(output, "theme-warm-main.png"));

        Invoke(Control<Button>(main, "SettingsButton"));
        await Until(() => app.Windows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        var settings = app.Windows.OfType<SettingsWindow>().Single(window => window.IsVisible);
        await SelectSettingsCategoryAsync(settings, "ApiTab");
        var model = Control<TextBox>(settings, "ModelBox").Text;
        Snapshot(settings, Path.Combine(output, "theme-warm-settings.png"));

        await themes.SetThemeAsync(AppTheme.Mono);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(settings.Background, app.Resources["ShellBackground"]), "open settings window changes theme immediately");
        Check(Control<TextBox>(settings, "ModelBox").Text == model, "theme change preserves settings draft");
        Check(Control<RadioButton>(main, "ThemeMonoOption").IsChecked == true, "external palette change syncs the theme selector");
        Snapshot(settings, Path.Combine(output, "theme-mono-settings.png"));
        settings.Close();

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(((SolidColorBrush)preview.Document.Foreground).Color == Color.FromRgb(0x23, 0x23, 0x21),
            "existing note switches to mono palette without recreation");
        Check(Control<TextBox>(main, "WordBox").Text == word && Control<TextBox>(main, "ContextBox").Text == context,
            "theme toggles preserve word and context input");
        Check(await File.ReadAllTextAsync(apiConfigPath) == apiConfig, "theme toggles never rewrite encrypted API settings");
        Check(await new AppearanceSettingsStore(config).LoadAsync() == AppTheme.Mono, "mono selection survives preference reload");
        Check(modelRequests() == before, "all live theme switches use zero additional model requests");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Snapshot(main, Path.Combine(output, "theme-mono-main.png"));
    }

    private static async Task<ReadingPage> OpenReadingAsync(MainWindow main)
    {
        await SelectPageAsync(main, "ReadingTab");
        await Until(() => Descendants<ReadingPage>(main).Any(page => page.IsVisible));
        var page = Descendants<ReadingPage>(main).Single(page => page.IsVisible);
        var viewModel = (DailyReadingViewModel)page.DataContext;
        await Until(() => !viewModel.IsBusy);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        return page;
    }

    private static async Task SelectPageAsync(MainWindow main, string name) => await SelectTabAsync(Control<RadioButton>(main, name));

    private static async Task SelectTabAsync(RadioButton tab)
    {
        if (tab.IsChecked != true)
        {
            var provider = (ISelectionItemProvider)new RadioButtonAutomationPeer(tab).GetPattern(PatternInterface.SelectionItem)!;
            provider.Select();
            await Until(() => tab.IsChecked == true);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (Window.GetWindow(tab) is MainWindow main)
            await Until(() => Control<Grid>(main, "PageHost").Children.OfType<FrameworkElement>().Where(view => view.IsVisible)
                .All(view => !view.HasAnimatedProperties && !view.RenderTransform.HasAnimatedProperties));
    }

    private static async Task SwitchThemeAsync(RadioButton option, ThemeManager themes, AppTheme target)
    {
        if (themes.CurrentTheme == target) return;
        var provider = (ISelectionItemProvider)new RadioButtonAutomationPeer(option).GetPattern(PatternInterface.SelectionItem)!;
        provider.Select();
        await Until(() => themes.CurrentTheme == target && option.IsChecked == true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
