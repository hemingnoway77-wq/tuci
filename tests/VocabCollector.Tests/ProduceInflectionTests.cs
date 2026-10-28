using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Markdown;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class ProduceInflectionTests
{
    // Actual public-word response captured on 2026-10-28; no API credentials or private context.
    private const string CapturedResponse = """
        {"is_valid_word":true,"error":"","spelling":{"input_word":"produce","word":"produce","status":"valid","suggestions":[]},"lemma":"produce","form_note":"原形，兼作动词和名词","phonetics":{"uk":"/prəˈdjuːs/","us":"/prəˈduːs/"},"primary_pos":"verb","meanings":[{"part_of_speech":"verb","definition":"生产；制造；出产"},{"part_of_speech":"verb","definition":"产生；引起（结果、效果）"},{"part_of_speech":"noun","definition":"农产品（尤指新鲜蔬果）"}],"examples":[{"english":"This farm produces fresh fruit and vegetables.","chinese":"这个农场生产新鲜水果和蔬菜。"},{"english":"The factory can produce over a thousand cars each day.","chinese":"这家工厂每天能生产一千多辆汽车。"}],"collocations":[{"english":"produce goods","chinese":"生产商品"},{"english":"produce results","chinese":"产生结果"},{"english":"fresh produce","chinese":"新鲜农产品"}],"grammar":[{"pattern":"produce + 宾语","explanation":"作动词时后接所生产或产生的事物，既可指具体产品也可指抽象结果。"},{"pattern":"作名词时不可数","explanation":"指农产品时通常不可数，前面可加fresh等形容词。"}],"related":[{"word":"make","relation":"synonym","explanation":"表示制造时较口语，可替换produce的部分义项。"},{"word":"create","relation":"synonym","explanation":"强调从无到有地创造，语气比produce强。"},{"word":"consume","relation":"antonym","explanation":"表示消耗、消费，与produce（生产）相对。"},{"word":"product","relation":"same_root","explanation":"名词，指产品，与动词produce同根但词性不同。"}],"roots":[{"root":"duc","variants":["duct"],"meaning":"引导，带领"}]}
        """;

    [Fact]
    public void CapturedProduceResponseAcceptsThirdPersonExampleWithoutChangingTheCardWord()
    {
        var analysis = AnalysisJson.Parse(CapturedResponse, "produce");

        Assert.Equal("produce", SpellingValidator.ResolveWord("produce", analysis));
        Assert.Equal("produce", analysis.Spelling!.Word);
        Assert.Equal("This farm produces fresh fruit and vegetables.", analysis.Examples[0].English);
        Assert.Equal("The factory can produce over a thousand cars each day.", analysis.Examples[1].English);
    }

    [Fact]
    public void CapturedProduceResponseHighlightsTheActualFormWithoutRewritingItsExample()
    {
        var analysis = AnalysisJson.Parse(CapturedResponse);
        var text = new NoteRenderer().Render("produce", "", analysis, [], DateTimeOffset.UnixEpoch);

        Assert.Contains("# produce\n", text);
        Assert.Contains("word: \"produce\"", text);
        Assert.Contains("1. This farm **produces** fresh fruit and vegetables.", text);
        Assert.Contains("2. The factory can **produce** over a thousand cars each day.", text);
    }

    [Theory]
    [InlineData("produce", "produces")]
    [InlineData("credit", "credits")]
    [InlineData("try", "tries")]
    [InlineData("watch", "watches")]
    [InlineData("mix", "mixes")]
    [InlineData("say", "says")]
    public void BaseVerbAcceptsAndHighlightsRegularThirdPerson(string word, string form)
    {
        var analysis = VerbAnalysis(word, word.ToUpperInvariant(), form.ToUpperInvariant());

        Assert.Equal(word, SpellingValidator.ResolveWord(word, analysis));
        var text = new NoteRenderer().Render(word, "", analysis, [], DateTimeOffset.UnixEpoch);
        Assert.Contains($"We see **{form.ToUpperInvariant()}** in this example.", text);
    }

    [Theory]
    [InlineData("produce", "producement")]
    [InlineData("produce", "producer")]
    [InlineData("produce", "reproduce")]
    [InlineData("produce", "non-produce")]
    [InlineData("produce", "produces2")]
    [InlineData("produce", "produces-other")]
    [InlineData("produce", "otherword")]
    [InlineData("credit", "creditable")]
    [InlineData("credit", "creditor")]
    [InlineData("credit", "non-credited")]
    [InlineData("credit", "crediting2")]
    public void BaseVerbRejectsDerivativesCompoundsAndOtherWords(string word, string form)
    {
        var analysis = VerbAnalysis(word, word, form);

        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(word, analysis));
        var text = new NoteRenderer().Render(word, "", analysis, [], DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain($"**{form}**", text);
        Assert.Contains($"We see {form} in this example.", text);
    }

    [Theory]
    [InlineData("produce", "produced")]
    [InlineData("produce", "producing")]
    [InlineData("credit", "credited")]
    [InlineData("credit", "crediting")]
    [InlineData("have", "haves")]
    [InlineData("be", "bes")]
    [InlineData("quiz", "quizes")]
    public void BroaderOrIrregularFormsRemainOutsideTheThirdPersonRule(string word, string form)
    {
        var analysis = VerbAnalysis(word, word, form);

        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(word, analysis));
        var text = new NoteRenderer().Render(word, "", analysis, [], DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain($"**{form}**", text);
    }

    [Theory]
    [InlineData("produce", "", "produces")]
    [InlineData("produce", "product", "produces")]
    [InlineData("produced", "produce", "produce")]
    [InlineData("produced", "produce", "producing")]
    [InlineData("introduced", "introduce", "introduce")]
    public void InflectionExamplesRequireTheTargetToBeTheConfirmedBase(string word, string lemma, string form)
    {
        var analysis = VerbAnalysis(word, lemma, form);

        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(word, analysis));
        var text = new NoteRenderer().Render(word, "", analysis, [], DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain($"**{form}**", text);
    }

    [Fact]
    public void AdjectiveStillRequiresItsExactSurfaceForm()
    {
        var analysis = VerbAnalysis("resilient", "resilient", "resiliently") with
        {
            PrimaryPartOfSpeech = PartOfSpeech.Adjective,
            Meanings = [new(PartOfSpeech.Adjective, "有韧性的")]
        };

        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("resilient", analysis));
        var text = new NoteRenderer().Render("resilient", "", analysis, [], DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("**resiliently**", text);
    }

    private static WordAnalysis VerbAnalysis(string word, string lemma, string form) => TestData.Analysis with
    {
        Lemma = lemma,
        Spelling = new(word, word, SpellingStatus.Valid, []),
        PrimaryPartOfSpeech = PartOfSpeech.Verb,
        Meanings = [new(PartOfSpeech.Verb, "动作")],
        Examples = [new($"We see {form} in this example.", "例句。"), new($"We use {word} in this example.", "例句。")]
    };
}
