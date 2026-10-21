using VocabCollector.Core.Ai;
namespace VocabCollector.Tests;
public class ChatEndpointTests
{
    [Theory]
    [InlineData("https://host/prefix/v1/", "https://host/prefix/v1/chat/completions")]
    [InlineData("https://host/custom", "https://host/custom/chat/completions")]
    [InlineData("https://host/v1/chat/completions", "https://host/v1/chat/completions")]
    [InlineData("http://127.0.0.1:2345/v1", "http://127.0.0.1:2345/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://192.168.1.100:1234/v1", "http://192.168.1.100:1234/v1/chat/completions")]
    [InlineData("http://my-model.local:11434/v1/", "http://my-model.local:11434/v1/chat/completions")]
    [InlineData("http://host/v1/chat/completions", "http://host/v1/chat/completions")]
    public void Endpoint_AppendsOnlyTheCompletionSuffix(string input, string expected) => Assert.Equal(expected, ChatEndpoint.Resolve(input).AbsoluteUri);
    [Theory][InlineData("ftp://host/v1")][InlineData("file:///C:/models")][InlineData("http://user:pass@host/v1")][InlineData("http://host/v1?key=abc")][InlineData("http://host/v1#frag")][InlineData("https://user:pass@host/v1")][InlineData("https://host/v1?key=abc")][InlineData("https://host/v1#frag")][InlineData("not a url")]
    public void Endpoint_RejectsUnsafeConfiguration(string input) => Assert.Throws<ArgumentException>(() => ChatEndpoint.Resolve(input));
}
