using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Reading;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public sealed class OfflineDictionaryTests
{
    [Fact]
    public async Task DictionaryDetailsAndOldThreeValueContractSurviveNormalizedCacheHits()
    {
        var meanings = new List<WordMeaning> { new("n.", "书；书籍"), new("vt.&vi.", "预订；登记") };
        var dictionary = new DictionaryFake((word, _) => Task.FromResult<WordTranslation?>(new(word, "n. 书；书籍\nvt.&vi. 预订；登记")
            { Phonetic = "buk", Meanings = meanings, Lemma = "book" }));
        var fallback = new Fallback();
        using var service = new OfflineDictionaryWordTranslationService(dictionary, fallback);
        var first = await service.TranslateAsync(" BOOK ", default);
        var (word, text, cached) = first;
        Assert.Equal("book", word); Assert.Equal(first.TranslatedText, text); Assert.False(cached);
        meanings.Clear();
        var repeat = await service.TranslateAsync("Book", default);
        Assert.True(repeat.IsFromCache); Assert.Equal("buk", repeat.Phonetic); Assert.Equal("book", repeat.Lemma);
        Assert.Collection(repeat.Meanings!, meaning => Assert.Equal(new WordMeaning("n.", "书；书籍"), meaning),
            meaning => Assert.Equal(new WordMeaning("vt.&vi.", "预订；登记"), meaning));
        Assert.Equal(1, dictionary.Calls); Assert.Equal(0, fallback.Calls);
        Assert.Equal(new WordTranslation("old", "旧译文"), new WordTranslation("old", "旧译文", false));
    }

    [Fact]
    public async Task MissCallsTheOfflineFallbackWithoutInventingMetadata()
    {
        var dictionary = new DictionaryFake((_, _) => Task.FromResult<WordTranslation?>(null));
        var fallback = new Fallback();
        using var service = new OfflineDictionaryWordTranslationService(dictionary, fallback);
        var result = await service.TranslateAsync(" Don’t ", default);
        Assert.Equal("don't", dictionary.LastWord); Assert.Equal("don't", fallback.LastWord);
        Assert.Equal(new WordTranslation("don't", "本地模型译文"), result);
        Assert.Null(result.Phonetic); Assert.Null(result.Meanings); Assert.Null(result.Lemma);
    }

    [Fact]
    public async Task DetailCacheHonorsCallerCancellationAndRetainsOnly128RecentWords()
    {
        var dictionary = new DictionaryFake((word, _) => Task.FromResult<WordTranslation?>(new(word, "n. 释义")
            { Phonetic = "source", Meanings = [new("n.", "释义")] }));
        using var service = new OfflineDictionaryWordTranslationService(dictionary, new Fallback());
        static string Word(int index) => "word" + (char)('a' + index / 26) + (char)('a' + index % 26);
        for (var index = 0; index < 128; index++) await service.TranslateAsync(Word(index), default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(Word(0), cancellation.Token));
        Assert.True((await service.TranslateAsync(Word(0), default)).IsFromCache);
        Assert.False((await service.TranslateAsync(Word(128), default)).IsFromCache);
        Assert.True((await service.TranslateAsync(Word(0), default)).IsFromCache);
        Assert.False((await service.TranslateAsync(Word(1), default)).IsFromCache);
        Assert.Equal(130, dictionary.Calls);
    }

    [Fact]
    public async Task CancelledLateDictionaryResultIsNotCachedAndFailureDoesNotSilentlyBecomeAModelGuess()
    {
        var late = new TaskCompletionSource<WordTranslation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dictionary = new DictionaryFake((_, _) => late.Task);
        var fallback = new Fallback();
        using var service = new OfflineDictionaryWordTranslationService(dictionary, fallback);
        using var cancellation = new CancellationTokenSource();
        var pending = service.TranslateAsync("apple", cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        late.SetResult(new("apple", "旧苹果"));
        dictionary.Reply = (_, _) => Task.FromException<WordTranslation?>(new IOException("private path canary"));
        var error = await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("apple", default));
        Assert.DoesNotContain("private path canary", error.Message); Assert.Equal(0, fallback.Calls);
        dictionary.Reply = (word, _) => Task.FromResult<WordTranslation?>(new(word, "新苹果"));
        Assert.Equal("新苹果", (await service.TranslateAsync("apple", default)).TranslatedText);
        Assert.Equal(3, dictionary.Calls);
    }

    [Fact]
    public async Task BusyRequestsDisposeBothOwnedServicesExactlyOnceAndCannotRefillAfterDisposal()
    {
        var late = new TaskCompletionSource<WordTranslation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dictionary = new DictionaryFake((_, _) => late.Task);
        var fallback = new Fallback();
        var service = new OfflineDictionaryWordTranslationService(dictionary, fallback);
        var pending = service.TranslateAsync("apple", default);
        Parallel.For(0, 8, _ => service.Dispose());
        late.SetResult(new("apple", "过期"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateAsync("apple", default));
        Assert.Equal(1, dictionary.Disposals); Assert.Equal(1, fallback.Disposals);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("../private")]
    [InlineData("中文")]
    public async Task InvalidWordsNeverReachDictionaryOrFallback(string word)
    {
        var dictionary = new DictionaryFake((_, _) => throw new InvalidOperationException());
        var fallback = new Fallback(); using var service = new OfflineDictionaryWordTranslationService(dictionary, fallback);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TranslateAsync(word, default));
        Assert.Equal(0, dictionary.Calls); Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task JsonLinesKeepsExactInflectedSensesAndRecognizesOnlyExplicitSourcePartOfSpeech()
    {
        using var package = new Package();
        const string raw = "a. 左边的\nn. 左侧\nvt.&vi. 留下；离开\n[网络] 保留原文，不猜词性";
        package.Set('l', ["left", "left", raw, "0:leave/p:left"]);
        package.Seal(); using var dictionary = new JsonLinesWordDictionary(package.Root);
        var result = Assert.IsType<WordTranslation>(await dictionary.LookupAsync("left", default));
        Assert.Equal(raw, result.TranslatedText); Assert.Equal("left", result.Word); Assert.Equal("leave", result.Lemma); Assert.Equal("left", result.Phonetic);
        Assert.Collection(result.Meanings!,
            meaning => Assert.Equal(new WordMeaning("a.", "左边的"), meaning),
            meaning => Assert.Equal(new WordMeaning("n.", "左侧"), meaning),
            meaning => Assert.Equal(new WordMeaning("vt.&vi.", "留下；离开"), meaning),
            meaning => Assert.Equal(new WordMeaning("", "[网络] 保留原文，不猜词性"), meaning));
    }

    [Fact]
    public async Task ExactMissDoesNotStripSuffixesAndEmptyMetadataDoesNotMakeUpDetails()
    {
        using var package = new Package(); package.Set('w', ["walk", "", "行走\n走路", "0:walk"]); package.Seal();
        using var dictionary = new JsonLinesWordDictionary(package.Root);
        Assert.Null(await dictionary.LookupAsync("walked", default));
        var result = Assert.IsType<WordTranslation>(await dictionary.LookupAsync("walk", default));
        Assert.Null(result.Phonetic); Assert.Null(result.Lemma);
        Assert.All(result.Meanings!, meaning => Assert.Empty(meaning.PartOfSpeech));
    }

    [Fact]
    public async Task OnlyThreeShardsRemainCachedAndTheLeastRecentlyUsedShardIsReloaded()
    {
        using var package = new Package();
        foreach (var letter in "abcd") package.Set(letter, [letter + "word", "", "n. " + letter, ""]);
        package.Seal(); using var dictionary = new JsonLinesWordDictionary(package.Root);
        foreach (var letter in "abca") Assert.NotNull(await dictionary.LookupAsync(letter + "word", default));
        Assert.NotNull(await dictionary.LookupAsync("dword", default));
        File.Delete(Path.Combine(package.Root, "b.jsonl"));
        Assert.NotNull(await dictionary.LookupAsync("aword", default));
        await Assert.ThrowsAsync<WordTranslationException>(() => dictionary.LookupAsync("bword", default));
    }

    [Theory]
    [InlineData("../outside.jsonl")]
    [InlineData("C:/outside.jsonl")]
    [InlineData("a.jsonl:stream")]
    public async Task ManifestTraversalAndAlternativeStreamsAreRejected(string path)
    {
        using var package = new Package(); package.Seal();
        File.WriteAllText(Path.Combine(package.Root, "dictionary.manifest.json"), JsonSerializer.Serialize(new
            { formatVersion = 1, files = new[] { new { path, bytes = 0, sha256 = new string('0', 64) } } }));
        using var dictionary = new JsonLinesWordDictionary(package.Root);
        var error = await Assert.ThrowsAsync<WordTranslationException>(() => dictionary.LookupAsync("apple", default));
        Assert.Contains("重新解压", error.Message);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("row")]
    [InlineData("schema")]
    public async Task BrokenDictionaryCanRecoverAfterThePackageIsRepaired(string damage)
    {
        using var package = new Package(); package.Set('a', ["apple", "", "n. 苹果", ""]); package.Seal();
        if (damage == "hash") File.AppendAllText(Path.Combine(package.Root, "a.jsonl"), "changed");
        if (damage == "row") { File.WriteAllText(Path.Combine(package.Root, "a.jsonl"), "[\"apple\"]\n"); package.Seal(); }
        if (damage == "schema") File.WriteAllText(Path.Combine(package.Root, "dictionary.manifest.json"), "{\"formatVersion\":2,\"files\":[]}");
        using var dictionary = new JsonLinesWordDictionary(package.Root);
        await Assert.ThrowsAsync<WordTranslationException>(() => dictionary.LookupAsync("apple", default));
        package.Set('a', ["apple", "", "n. 新苹果", ""]); package.Seal();
        Assert.Equal("n. 新苹果", (await dictionary.LookupAsync("apple", default))!.TranslatedText);
    }

    [Fact]
    public async Task PreCancellationAndDisposalRequireNoPackageAndRetainCallerCancellation()
    {
        using var package = new Package(); var dictionary = new JsonLinesWordDictionary(package.Root);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dictionary.LookupAsync("apple", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        dictionary.Dispose(); dictionary.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dictionary.LookupAsync("apple", default));
    }

    private sealed class Package : IDisposable
    {
        private readonly TempVault _vault = new();
        public string Root => _vault.Root;
        public Package() { foreach (var letter in "abcdefghijklmnopqrstuvwxyz") File.WriteAllText(Path.Combine(Root, letter + ".jsonl"), "", new UTF8Encoding(false)); }
        public void Set(char letter, params string[][] rows) => File.WriteAllLines(Path.Combine(Root, letter + ".jsonl"), rows.Select(row => JsonSerializer.Serialize(row)), new UTF8Encoding(false));
        public void Seal()
        {
            var files = Directory.GetFiles(Root, "*.jsonl").Order().Select(path => new { path = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
            File.WriteAllText(Path.Combine(Root, "dictionary.manifest.json"), JsonSerializer.Serialize(new { formatVersion = 1, files }));
        }
        public void Dispose() => _vault.Dispose();
    }
    private sealed class DictionaryFake(Func<string, CancellationToken, Task<WordTranslation?>> reply) : IOfflineWordDictionary
    {
        public Func<string, CancellationToken, Task<WordTranslation?>> Reply = reply;
        public int Calls, Disposals; public string? LastWord;
        public Task<WordTranslation?> LookupAsync(string word, CancellationToken ct) { Calls++; LastWord = word; return Reply(word, ct); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private sealed class Fallback : IWordTranslationService, IDisposable
    {
        public int Calls, Disposals; public string? LastWord;
        public Task<WordTranslation> TranslateAsync(string word, CancellationToken ct) { Calls++; LastWord = word; return Task.FromResult(new WordTranslation(word, "本地模型译文")); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
