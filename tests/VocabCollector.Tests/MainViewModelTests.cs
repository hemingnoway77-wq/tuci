using VocabCollector.Core;
using VocabCollector.Desktop;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class MainViewModelTests
{
    [Fact]
    public async Task PreparingReadingDraftClearsStalePreviewWithoutGeneratingOrSaving()
    {
        var calls = 0;
        var vm = new MainViewModel(new TestService((_, _) =>
        {
            calls++;
            return Task.FromResult(new CaptureResult(CaptureStatus.Saved, "old", "# old", [], "saved"));
        }), AppSettings.Default) { Word = "old" };
        await vm.GenerateAsync();
        Assert.True(vm.PrepareDraft("apple", "An apple grows."));
        Assert.Equal("apple", vm.Word);
        Assert.Equal("An apple grows.", vm.Context);
        Assert.Empty(vm.Markdown); Assert.Empty(vm.Notes); Assert.Null(vm.SelectedNote);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreparingAnotherWordCannotOverwriteAnInFlightCapture()
    {
        var pending = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainViewModel(new TestService((_, ct) => pending.Task.WaitAsync(ct)), AppSettings.Default) { Word = "old", Context = "old context" };
        var task = vm.GenerateAsync();
        Assert.False(vm.PrepareDraft("apple", "new context"));
        Assert.Equal("old", vm.Word); Assert.Equal("old context", vm.Context);
        vm.CancelCommand.Execute(null); await task;
    }
    [Fact]
    public async Task ExistingWordLoadsItsOriginalMarkdownWithoutCallingAiAgain()
    {
        using var vault = new TempVault();
        var note = vault.Note("英语单词/word.md", "# word\n\n**释义**：\n\n> n. 原有词卡\n\n#### 我的笔记\n手写补充\n");
        var vm = new MainViewModel(new TestService((_, _) => Task.FromResult(new CaptureResult(
            CaptureStatus.AlreadyExists, "word", "", [note], "已经收录。"))), AppSettings.Default with { Vault = vault.Options });
        await vm.GenerateAsync();
        Assert.Equal(await File.ReadAllTextAsync(note.AbsolutePath), vm.Markdown);
        Assert.Contains("手写补充", vm.Markdown);
        Assert.Equal(note, vm.SelectedNote);
    }

    [Fact]
    public async Task ExistingWordPreviewNeverReadsOutsideTheLibrary()
    {
        using var vault = new TempVault(); using var outside = new TempVault();
        var note = outside.Note("英语单词/word.md", "outside-library-canary");
        var vm = new MainViewModel(new TestService((_, _) => Task.FromResult(new CaptureResult(
            CaptureStatus.AlreadyExists, "word", "", [note], "already exists"))), AppSettings.Default with { Vault = vault.Options });
        await vm.GenerateAsync();
        Assert.DoesNotContain("outside-library-canary", vm.Markdown);
    }

    [Theory]
    [InlineData(CaptureStatus.Saved)]
    [InlineData(CaptureStatus.AlreadyExists)]
    public async Task CorrectionUpdatesInputOnlyAfterSuccessfulSaveOrKnownTarget(CaptureStatus status)
    {
        var service = new TestService((_, _) => Task.FromResult(new CaptureResult(status, "introduce", "# introduce", [], "拼写已修正：introdce → introduce。") { CorrectedFrom = "introdce" }));
        var vm = new MainViewModel(service, AppSettings.Default) { Word = "introdce" };
        await vm.GenerateAsync();
        Assert.Equal("introduce", vm.Word);
        Assert.Equal("# introduce", vm.Markdown);
        Assert.Contains("introdce → introduce", vm.Message);
    }
    [Theory]
    [InlineData(CaptureStatus.Failed)]
    [InlineData(CaptureStatus.Cancelled)]
    [InlineData(CaptureStatus.InvalidInput)]
    public async Task UnsuccessfulCorrectionKeepsTheUsersOriginalInput(CaptureStatus status)
    {
        var service = new TestService((_, _) => Task.FromResult(new CaptureResult(status, "introduce", "", [], "未保存") { CorrectedFrom = "introdce" }));
        var vm = new MainViewModel(service, AppSettings.Default) { Word = "introdce" };
        await vm.GenerateAsync();
        Assert.Equal("introdce", vm.Word);
    }
    [Fact] public async Task Generate_RestoresStateAndKeepsInputAfterFailure()
    {
        var vm=new MainViewModel(new TestService((_,_)=>Task.FromResult(new CaptureResult(CaptureStatus.Failed,"resilient","generated result",[],"save failed"))),AppSettings.Default){Word="resilient",Context="source"};
        await vm.GenerateAsync();
        Assert.False(vm.IsBusy);Assert.Equal("resilient",vm.Word);Assert.Equal("source",vm.Context);Assert.Equal("generated result",vm.Markdown);Assert.Empty(vm.Notes);Assert.True(vm.GenerateCommand.CanExecute(null));
    }
    [Fact] public async Task BusyStateBlocksDuplicateGenerationAndSettingsButSupportsCancel()
    {
        var pending=new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm=new MainViewModel(new TestService(async (_,ct)=>await pending.Task.WaitAsync(ct)),AppSettings.Default){Word="word"};
        var running=vm.GenerateAsync();Assert.True(vm.IsBusy);Assert.False(vm.GenerateCommand.CanExecute(null));Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.Throws<InvalidOperationException>(()=>vm.ApplySettings(AppSettings.Default));
        vm.CancelCommand.Execute(null);await running;Assert.False(vm.IsBusy);Assert.Empty(vm.Notes);Assert.Contains("取消",vm.Message);
    }
    [Fact] public async Task UnexpectedServiceErrorDoesNotExposeItsMessage()
    {
        var vm=new MainViewModel(new TestService((_,_)=>throw new Exception("secret-key-canary")),AppSettings.Default){Word="word"};
        await vm.GenerateAsync();Assert.DoesNotContain("secret-key-canary",vm.Message);Assert.False(vm.IsBusy);
    }
    [Theory][InlineData(CaptureStatus.Saved)][InlineData(CaptureStatus.AlreadyExists)]
    public async Task ResultsSelectAnExistingPath(CaptureStatus status)
    {
        var note=new NoteLocation("word","C:\\vault\\word.md","word.md");
        var vm=new MainViewModel(new TestService((_,_)=>Task.FromResult(new CaptureResult(status,"word","",[note],"done"))),AppSettings.Default){Word="word"};
        await vm.GenerateAsync();Assert.Equal(note,vm.SelectedNote);Assert.True(vm.CanOpen);
    }
    [Fact] public async Task RefreshLinksNeedsOnlyVaultSettingsAndKeepsInputAndPreview()
    {
        var settings = AppSettings.Default with { Vault = new("C:\\test-vault", "英语单词", true) };
        var captures = 0;
        var vm = new MainViewModel(new TestService((_, _) =>
        {
            captures++;
            return Task.FromResult(new CaptureResult(CaptureStatus.Saved, "word", "existing preview", [], "saved"));
        }), settings, new TestMaintenance((options, _) =>
        {
            Assert.Equal(settings.Vault, options);
            return Task.FromResult(new LinkRefreshResult(2, 1, 0));
        })) { Word = "word", Context = "source" };
        await vm.GenerateAsync();
        await vm.RefreshLinksAsync();
        Assert.Equal(1, captures);
        Assert.Equal("word", vm.Word); Assert.Equal("source", vm.Context); Assert.Equal("existing preview", vm.Markdown);
        Assert.Contains("更新 2", vm.Message); Assert.Contains("跳过 1", vm.Message); Assert.Contains("失败 0", vm.Message);
        Assert.False(vm.IsBusy); Assert.True(vm.RefreshLinksCommand.CanExecute(null));
    }
    [Fact] public async Task MissingVaultConfigurationExplainsHowToEnableRefresh()
    {
        var refreshes = 0;
        var vm = new MainViewModel(new TestService((_, _) => throw new Exception("API must not be called")), AppSettings.Default,
            new TestMaintenance((_, _) => { refreshes++; return Task.FromResult(new LinkRefreshResult(0, 0, 0)); }));
        await vm.RefreshLinksAsync();
        Assert.Contains("设置", vm.Message); Assert.Contains("词库", vm.Message);
        Assert.Equal(0, refreshes); Assert.False(vm.IsBusy);
    }
    [Fact] public async Task RefreshReloadsDisplayedNoteAfterItsLinksChange()
    {
        using var vault = new TempVault();
        var note = vault.Note("英语单词/形容词/resilient.md", "The economy is resilient.");
        var vm = new MainViewModel(new TestService((_, _) => Task.FromResult(new CaptureResult(CaptureStatus.Saved,
            "resilient", "The economy is resilient.", [note], "saved"))),
            AppSettings.Default with { Vault = vault.Options },
            new TestMaintenance(async (_, ct) =>
            {
                await File.WriteAllTextAsync(note.AbsolutePath, "The [[英语单词/名词/economy|economy]] is resilient.", ct);
                return new LinkRefreshResult(1, 0, 0);
            }));
        await vm.GenerateAsync();
        await vm.RefreshLinksAsync();
        Assert.Equal("The [[英语单词/名词/economy|economy]] is resilient.", vm.Markdown);
        Assert.Equal(await File.ReadAllTextAsync(note.AbsolutePath), vm.Markdown);
        Assert.Contains("更新 1", vm.Message); Assert.False(vm.IsBusy);
    }
    [Fact] public async Task RefreshPreviewReadFailureKeepsCompletedResultAndPreviousPreview()
    {
        using var vault = new TempVault();
        var note = vault.Note("英语单词/word.md", "previous preview");
        var vm = new MainViewModel(new TestService((_, _) => Task.FromResult(new CaptureResult(CaptureStatus.Saved,
            "word", "previous preview", [note], "saved"))), AppSettings.Default with { Vault = vault.Options },
            new TestMaintenance((_, _) =>
            {
                File.Delete(note.AbsolutePath);
                return Task.FromResult(new LinkRefreshResult(1, 0, 0));
            }));
        await vm.GenerateAsync();
        await vm.RefreshLinksAsync();
        Assert.Equal("previous preview", vm.Markdown);
        Assert.Contains("更新 1", vm.Message); Assert.Contains("预览未更新", vm.Message); Assert.Contains("词库", vm.Message);
        Assert.DoesNotContain("刷新失败", vm.Message); Assert.False(vm.IsBusy);
    }
    [Fact] public async Task RefreshPreviewNeverReadsOutsideConfiguredVault()
    {
        using var vault = new TempVault(); using var outside = new TempVault();
        var note = outside.Note("英语单词/word.md", "outside-vault-canary");
        var vm = new MainViewModel(new TestService((_, _) => Task.FromResult(new CaptureResult(CaptureStatus.Saved,
            "word", "previous preview", [note], "saved"))), AppSettings.Default with { Vault = vault.Options },
            new TestMaintenance((_, _) => Task.FromResult(new LinkRefreshResult(1, 0, 0))));
        await vm.GenerateAsync();
        await vm.RefreshLinksAsync();
        Assert.Equal("previous preview", vm.Markdown); Assert.DoesNotContain("outside-vault-canary", vm.Markdown);
        Assert.Contains("预览未更新", vm.Message); Assert.Contains("更新 1", vm.Message);
    }
    [Fact] public async Task LinkRefreshLocksOtherOperationsAndCanBeCancelled()
    {
        var pending = new TaskCompletionSource<LinkRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0; var captures = 0;
        var vm = new MainViewModel(new TestService((_, _) => { captures++; throw new Exception("unexpected capture"); }),
            AppSettings.Default with { Vault = new("C:\\test-vault", "英语单词", true) },
            new TestMaintenance(async (_, ct) => { refreshes++; return await pending.Task.WaitAsync(ct); }));
        var running = vm.RefreshLinksAsync();
        Assert.True(vm.IsBusy); Assert.False(vm.CanConfigure); Assert.False(vm.RefreshLinksCommand.CanExecute(null));
        Assert.False(vm.GenerateCommand.CanExecute(null)); Assert.True(vm.CancelCommand.CanExecute(null));
        await vm.GenerateAsync(); await vm.RefreshLinksAsync();
        Assert.Throws<InvalidOperationException>(() => vm.ApplySettings(AppSettings.Default));
        vm.CancelCommand.Execute(null); await running;
        Assert.Equal(0, captures); Assert.Equal(1, refreshes); Assert.False(vm.IsBusy); Assert.Contains("取消", vm.Message);
    }
    [Fact] public async Task StartupRefreshKeepsSettingsWarningAndRedactsFailure()
    {
        var vm = new MainViewModel(new TestService((_, _) => throw new Exception("unexpected API call")),
            AppSettings.Default with { Vault = new("C:\\test-vault", "英语单词", true) },
            new TestMaintenance((_, _) => throw new IOException("secret-key-canary")));
        await vm.InitializeAsync("原配置有问题，请重新设置。");
        Assert.Contains("原配置有问题", vm.Message); Assert.Contains("刷新失败", vm.Message);
        Assert.DoesNotContain("secret-key-canary", vm.Message); Assert.False(vm.IsBusy);
    }
    [Fact] public async Task StartupWithoutVaultKeepsNormalWelcomeAndDoesNotRefresh()
    {
        var refreshes = 0;
        var vm = new MainViewModel(new TestService((_, _) => throw new Exception("unexpected API call")), AppSettings.Default,
            new TestMaintenance((_, _) => { refreshes++; return Task.FromResult(new LinkRefreshResult(0, 0, 0)); }));
        await vm.InitializeAsync();
        Assert.Equal(0, refreshes); Assert.Contains("粘贴", vm.Message); Assert.False(vm.IsBusy);
    }
    [Fact] public async Task Generate_ShowsReceivingStatusBeforeFinalResultWithoutRawContent()
    {
        var service = new StageService();
        var vm = new MainViewModel(service, AppSettings.Default) { Word = "word" };
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Message) && vm.Message == "AI 正在生成笔记内容…") received.TrySetResult(); };
        var running = vm.GenerateAsync();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(vm.IsBusy); Assert.Empty(vm.Markdown);
        service.Complete.SetResult(new(CaptureStatus.Saved, "word", "final note", [], "done"));
        await running; Assert.Equal("final note", vm.Markdown); Assert.Equal("done", vm.Message);
    }
    private sealed class StageService : ICaptureService
    {
        public TaskCompletionSource<CaptureResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct)
        { progress?.Report(CaptureStage.ReceivingAi); return Complete.Task; }
    }
    private sealed class TestMaintenance(Func<VaultOptions,CancellationToken,Task<LinkRefreshResult>> action):ILinkMaintenance
    { public Task<LinkRefreshResult> RefreshLinksAsync(VaultOptions options, CancellationToken ct) => action(options, ct); }
    private sealed class TestService(Func<CaptureRequest,CancellationToken,Task<CaptureResult>> action):ICaptureService
    {public Task<CaptureResult> CaptureAsync(CaptureRequest r,AppSettings s,IProgress<CaptureStage>? p,CancellationToken ct)=>action(r,ct);}
}
