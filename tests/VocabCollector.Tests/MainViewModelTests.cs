using VocabCollector.Core;
using VocabCollector.Desktop;
namespace VocabCollector.Tests;
public class MainViewModelTests
{
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
    private sealed class TestService(Func<CaptureRequest,CancellationToken,Task<CaptureResult>> action):ICaptureService
    {public Task<CaptureResult> CaptureAsync(CaptureRequest r,AppSettings s,IProgress<CaptureStage>? p,CancellationToken ct)=>action(r,ct);}
}
