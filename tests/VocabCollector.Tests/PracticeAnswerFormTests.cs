using System.Net;
using System.Text;
using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public sealed class PracticeAnswerFormTests
{
    [Theory]
    [InlineData("remain", "remains", "n. 剩余物；遗迹（常用复数remains）", "The remains of the old building were still visible.", "那座旧建筑的遗迹仍然清晰可见。")]
    [InlineData("supply", "supplies", "n. 用品；补给（常用的东西，常用复数supplies）", "They placed the supplies inside a secure storeroom.", "他们把补给品放进了安全的储藏室。")]
    [InlineData("specification", "specifications", "n. （复数 specifications）技术规格，性能指标", "The engineer checked the specifications before ordering new parts.", "工程师在订购新零件前核对了技术规格。")]
    public async Task ExplicitSourcePluralBecomesTheExactAnswerAndBaseRemainsNeutral(string word, string plural, string meaning, string english, string chinese)
    {
        using var files = new LibraryFixture();
        var note = PracticeQuestionBankTests.Note(word, "**释义**：\n> " + meaning);
        var senses = PracticeQuestionBank.Build(note);
        var contexts = new[] { new PracticeContext(senses[0].Id, english, chinese, plural) };
        Assert.Equal(plural, Assert.Single(PracticeQuestionBank.WithContexts(senses, contexts)).AnswerForm);
        var path = Path.Combine(files.Metadata, "practice.json");
        var session = new PracticeSession(path, () => 0);
        var question = (await session.PrepareAsync(note, Guid.NewGuid(), contexts: contexts))!;
        Assert.Equal(plural, question.Word); Assert.Equal(word, question.Lemma);
        Assert.Contains("复数", question.FormNote);
        var visible = string.Join('\n', question.Prompt, question.Context, question.Meaning, question.ContextTranslation);
        Assert.DoesNotContain(plural, visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(word, visible, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("____", question.Prompt);
        Assert.Equal(PracticeOutcome.Correct, PracticeQuestionBank.Evaluate(question, plural));
        Assert.Equal(PracticeOutcome.LemmaCorrect, PracticeQuestionBank.Evaluate(question, word));
        Assert.Equal(PracticeOutcome.Incorrect, PracticeQuestionBank.Evaluate(question, plural + "s"));
        await session.CompleteAsync(question, PracticeOutcome.LemmaCorrect, word);
        Assert.Equal(new PracticeCompletion(PracticeOutcome.LemmaCorrect, word), new PracticeSession(path).GetCompletion(question.ExposureId));
        var next = (await new PracticeSession(path, () => 0).PrepareAsync(note, Guid.NewGuid(), contexts: contexts))!;
        Assert.Equal(PracticeKind.Meaning, next.Kind);
        Assert.Equal(plural, next.Word); Assert.Contains(plural, next.Prompt);
        Assert.Equal(english, next.Context);
    }

    [Theory]
    [InlineData("supply", "n. 用品；补给", "supplies", "They placed the supplies inside a secure storeroom.")]
    [InlineData("supply", "n. 用品（复数 supplies）", "cats", "They placed the cats inside a secure storeroom.")]
    [InlineData("supply", "v. 供应（复数 supplies）", "supplies", "She supplies fresh bread to the village every morning.")]
    [InlineData("supply", "n. 用品（复数 supplyes）", "supplyes", "They placed the supplyes inside a secure storeroom.")]
    [InlineData("supplies", "n. 用品", "supply", "A steady supply of water keeps the plants healthy.")]
    [InlineData("obtain", "v. 获得", "obtains", "She obtains fresh data from the public records each day.")]
    [InlineData("track", "v. 跟踪", "tracks", "She tracks the package through the delivery website daily.")]
    public void UnspecifiedUnrelatedOrVerbFormsCannotBecomeAnAnswer(string word, string meaning, string form, string english)
    {
        var senses = PracticeQuestionBank.Build(PracticeQuestionBankTests.Note(word, "**释义**：\n> " + meaning));
        Assert.Throws<ArgumentException>(() => PracticeQuestionBank.WithContexts(senses,
            [new(senses[0].Id, english, "这是与该义项对应的完整中文译句。", form)]));
    }

    [Fact]
    public void AnExplicitBaseFormCanonicalizesToNullAndKeepsTheOriginalThreeFieldWireShape()
    {
        var senses = PracticeQuestionBank.Build(PracticeQuestionBankTests.Note("supply", "**释义**：\n> n. 供给"));
        var context = new PracticeContext(senses[0].Id, "A steady supply of water keeps the plants healthy.", "稳定的水源供应使这些植物保持健康。", " SUPPLY ");
        Assert.Null(Assert.Single(PracticeQuestionBank.WithContexts(senses, [context])).AnswerForm);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(context with { AnswerForm = null }));
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
        Assert.False(document.RootElement.TryGetProperty("AnswerForm", out _));
    }

    [Fact]
    public async Task OldGoldenCacheFingerprintAndThreeFieldContextsRemainReadable()
    {
        using var files = new LibraryFixture(); Directory.CreateDirectory(files.Metadata);
        const string oldSenses = """[{"Id":"legacy-sense","Word":"introduced","Lemma":"introduce","Meaning":"v. bring someone into a group","Context":"","FormNote":"past tense","Number":1,"Count":1,"ContextTranslation":""}]""";
        const string hash = "6c7cfd089c764db5ed4acd88df60dcc10e2be8cc5808258889689382a7b9a037";
        var senses = JsonSerializer.Deserialize<PracticeSense[]>(oldSenses)!;
        Assert.Equal(oldSenses, JsonSerializer.Serialize(senses));
        var cache = "{\"FormatVersion\":1,\"Words\":{\"introduced\":{\"SenseHash\":\"" + hash + "\",\"Senses\":" + oldSenses +
            ",\"Contexts\":[{\"SenseId\":\"legacy-sense\",\"English\":\"The teacher introduced her new student to the class.\",\"Chinese\":\"老师向全班介绍了她的新学生。\"}]}}}";
        var path = Path.Combine(files.Metadata, "contexts.json"); await File.WriteAllTextAsync(path, cache);
        var context = Assert.Single(await new PracticeContextCache(path).ReadAsync("introduced", senses));
        Assert.Null(context.AnswerForm);
        Assert.Equal("The teacher introduced her new student to the class.", context.English);
        Assert.Equal(cache, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GeneratorAdvertisesOnlyTrustedFormsAndPreservesTheSelectedFormThroughValidationAndCache()
    {
        using var files = new LibraryFixture();
        var senses = PracticeQuestionBank.Build(PracticeQuestionBankTests.Note("supply", "**释义**：\n> n. 用品（复数 supplies）"));
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains("answer_form", messages[0].GetProperty("content").GetString());
            Assert.Contains("元语言", messages[0].GetProperty("content").GetString());
            using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal(new[] { "supply", "supplies" }, data.RootElement.GetProperty("senses")[0].GetProperty("allowed_answer_forms").EnumerateArray().Select(item => item.GetString()));
            return Completion(senses[0].Id, "They placed the supplies inside a secure storeroom.", "他们把补给品放进了安全的储藏室。", "supplies");
        });
        using var client = new HttpClient(handler);
        var result = await new OpenAiPracticeContextGenerator(client).GenerateAsync("supply", senses, new("https://example.test/v1", "fixture-only-key", "fake-model"), default);
        Assert.Equal("supplies", Assert.Single(result).AnswerForm);
        var path = Path.Combine(files.Metadata, "contexts.json");
        await new PracticeContextCache(path).SaveAsync("supply", senses, result);
        Assert.Equal("supplies", Assert.Single(await new PracticeContextCache(path).ReadAsync("supply", senses)).AnswerForm);
    }

    [Theory]
    [InlineData("obtain", "obtains", "She obtains fresh data from the public records each day.")]
    [InlineData("track", "tracks", "She tracks the package through the delivery website daily.")]
    public async Task GeneratorCannotPromoteAnUnallowedThirdPersonForm(string word, string form, string english)
    {
        var senses = PracticeQuestionBank.Build(PracticeQuestionBankTests.Note(word, "**释义**：\n> v. 获得或追踪"));
        string?[]? allowed = null;
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            if (data.RootElement.GetProperty("senses")[0].TryGetProperty("allowed_answer_forms", out var forms))
                allowed = forms.EnumerateArray().Select(item => item.GetString()).ToArray();
            return Completion(senses[0].Id, english, "她每天取得所需的信息。", form);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync(word, senses, new("https://example.test/v1", "fixture-only-key", "fake-model"), default));
        Assert.Equal(new[] { word }, allowed);
    }

    private static HttpResponseMessage Completion(string id, string english, string chinese, string form) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new
        { content = JsonSerializer.Serialize(new { contexts = new[] { new { sense_id = id, english, chinese, answer_form = form } } }) } } } }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
