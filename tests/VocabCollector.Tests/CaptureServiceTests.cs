using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class CaptureServiceTests
{
    private static AppSettings Settings(TempVault vault) => new(new("https://host/v1","unit-key","model"),vault.Options);
    [Fact] public async Task Capture_CreatesCompleteNoteWithFreshHistoricalLinkAndProgress()
    {
        using var vault=new TempVault(); var analyzer=new FakeWordAnalyzer { Analyze = _ => { vault.Note("英语单词/名词/resilience.md"); return Task.FromResult(TestData.Analysis); } };
        List<CaptureStage> stages=[];
        var result=await new CaptureService(analyzer,new VaultStore(),TimeProvider.System).CaptureAsync(new("Resilient","source sentence"),Settings(vault),new InlineProgress<CaptureStage>(stages.Add),default);
        Assert.Equal(CaptureStatus.Saved,result.Status); Assert.Single(result.Notes); Assert.True(File.Exists(result.Notes[0].AbsolutePath));
        Assert.Contains("[[英语单词/名词/resilience|resilience]]",result.Markdown); Assert.Contains("source sentence",result.Markdown);
        Assert.Equal(new[]{CaptureStage.Validating,CaptureStage.CallingAi,CaptureStage.Saving},stages);
    }
    [Fact] public async Task ExistingWordsDoNotCallAiOrOverwrite()
    {
        using var vault=new TempVault(); var note=vault.Note("英语单词/名词/record.md","my handwritten notes"); vault.Note("英语单词/动词/record.md"); var analyzer=new FakeWordAnalyzer();
        var result=await new CaptureService(analyzer,new VaultStore(),TimeProvider.System).CaptureAsync(new("record",""),Settings(vault),null,default);
        Assert.Equal(CaptureStatus.AlreadyExists,result.Status); Assert.Equal(2,result.Notes.Count); Assert.Equal(0,analyzer.Calls);
        Assert.Equal("my handwritten notes",await File.ReadAllTextAsync(note.AbsolutePath));
    }
    [Theory][InlineData("two words",false)][InlineData("word",true)]
    public async Task InvalidInputDoesNotCallAi(string word,bool longContext)
    {
        using var vault=new TempVault(); var analyzer=new FakeWordAnalyzer();
        var result=await new CaptureService(analyzer,new VaultStore(),TimeProvider.System).CaptureAsync(new(word,longContext?new string('x',2001):""),Settings(vault),null,default);
        Assert.Equal(CaptureStatus.InvalidInput,result.Status); Assert.Equal(0,analyzer.Calls);
    }
    [Fact] public async Task LateResponseAfterCancellationCannotSave()
    {
        using var vault=new TempVault(); using var cts=new CancellationTokenSource();
        var analyzer=new FakeWordAnalyzer{Analyze=_=> {cts.Cancel(); return Task.FromResult(TestData.Analysis);}};
        var result=await new CaptureService(analyzer,new VaultStore(),TimeProvider.System).CaptureAsync(new("resilient",""),Settings(vault),null,cts.Token);
        Assert.Equal(CaptureStatus.Cancelled,result.Status); Assert.Empty(Directory.GetFiles(vault.Root,"*.md",SearchOption.AllDirectories));
    }
    [Fact] public async Task ExistingNoteAppearingDuringApiWins()
    {
        using var vault=new TempVault(); var analyzer=new FakeWordAnalyzer{Analyze=_=>{vault.Note("英语单词/其他/resilient.md","keep me");return Task.FromResult(TestData.Analysis);}};
        var result=await new CaptureService(analyzer,new VaultStore(),TimeProvider.System).CaptureAsync(new("resilient",""),Settings(vault),null,default);
        Assert.Equal(CaptureStatus.AlreadyExists,result.Status); Assert.Equal("keep me",await File.ReadAllTextAsync(Assert.Single(result.Notes).AbsolutePath));
    }
    [Fact] public async Task StorageFailure_PreservesGeneratedMarkdown()
    {
        using var vault=new TempVault(); Directory.CreateDirectory(Path.Combine(vault.Root,"英语单词","形容词","resilient.md"));
        var result=await new CaptureService(new FakeWordAnalyzer(),new VaultStore(),TimeProvider.System).CaptureAsync(new("resilient",""),Settings(vault),null,default);
        Assert.Equal(CaptureStatus.Failed,result.Status); Assert.Contains("## 词性与释义",result.Markdown); Assert.Empty(result.Notes);
    }
    [Fact] public async Task ModelErrorCreatesNoFile()
    {
        using var vault=new TempVault(); var analyzer=new FakeWordAnalyzer{Analyze=_=>throw new AnalysisException("网络连接失败")};
        var result=await new CaptureService(analyzer,new VaultStore(),TimeProvider.System).CaptureAsync(new("resilient",""),Settings(vault),null,default);
        Assert.Equal(CaptureStatus.Failed,result.Status); Assert.Empty(Directory.GetFiles(vault.Root,"*.md",SearchOption.AllDirectories));
    }
    [Fact] public async Task CancellationAfterCommitStillReportsSaved()
    {
        using var vault=new TempVault(); using var cts=new CancellationTokenSource();
        var result=await new CaptureService(new FakeWordAnalyzer(),new CancelAfterCommitStore(cts),TimeProvider.System).CaptureAsync(new("resilient",""),Settings(vault),null,cts.Token);
        Assert.Equal(CaptureStatus.Saved,result.Status); Assert.True(File.Exists(Assert.Single(result.Notes).AbsolutePath));
    }
    private sealed class CancelAfterCommitStore(CancellationTokenSource cts) : IVocabularyStore
    {
        private readonly VaultStore real=new();
        public Task<VaultIndex> ScanAsync(VaultOptions options,CancellationToken ct)=>real.ScanAsync(options,ct);
        public async Task<NoteLocation> SaveNewAsync(string word,PartOfSpeech pos,string markdown,VaultOptions options,CancellationToken ct)
        {var result=await real.SaveNewAsync(word,pos,markdown,options,ct);cts.Cancel();return result;}
    }
}
