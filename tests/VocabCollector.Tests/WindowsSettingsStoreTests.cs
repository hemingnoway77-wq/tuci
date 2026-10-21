using System.Text.Json.Nodes;
using VocabCollector.Core;
using VocabCollector.Desktop;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class WindowsSettingsStoreTests
{
    [Fact] public async Task Settings_DoesNotPersistPlaintextKeyAndRoundTrips()
    {
        using var vault = new TempVault(); var store = new WindowsSettingsStore(Path.Combine(vault.Root,"config"));
        var settings = new AppSettings(new("https://host/v1", "sk-canary-settings-88", "model"), vault.Options);
        await store.SaveAsync(settings, default);
        Assert.DoesNotContain("sk-canary-settings-88", await File.ReadAllTextAsync(Path.Combine(vault.Root,"config","settings.json")));
        Assert.Equal(settings, (await store.LoadAsync(default)).Settings);
    }
    [Fact] public async Task DamagedCipherPreservesOtherSettingsAndRequestsKey()
    {
        using var vault = new TempVault(); var store = new WindowsSettingsStore(Path.Combine(vault.Root,"config"));
        await store.SaveAsync(new(new("https://host/v1","key","model"),vault.Options),default);
        var path=Path.Combine(vault.Root,"config","settings.json"); var node=JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["encryptedApiKey"]="not-base64"; await File.WriteAllTextAsync(path,node.ToJsonString());
        var loaded=await store.LoadAsync(default);
        Assert.Equal("",loaded.Settings.Api.ApiKey); Assert.Equal("model",loaded.Settings.Api.Model); Assert.NotEmpty(loaded.Warning);
    }
    [Fact] public async Task BrokenJsonIsPreservedWithoutCrashing()
    {
        using var vault = new TempVault(); var dir=Path.Combine(vault.Root,"config"); Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"settings.json"); await File.WriteAllTextAsync(path,"{broken");
        var result=await new WindowsSettingsStore(dir).LoadAsync(default);
        Assert.NotEmpty(result.Warning); Assert.Equal("{broken",await File.ReadAllTextAsync(path));
    }
    [Fact] public async Task FailedReplacementLeavesPreviousConfigIntact()
    {
        using var vault = new TempVault(); var dir=Path.Combine(vault.Root,"config"); var store=new WindowsSettingsStore(dir);
        var settings=new AppSettings(new("https://host/v1","key","model"),vault.Options); await store.SaveAsync(settings,default);
        var path=Path.Combine(dir,"settings.json"); var original=await File.ReadAllTextAsync(path);
        using (var locked = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var error=await Record.ExceptionAsync(()=>store.SaveAsync(settings with {Api=settings.Api with {Model="new"}},default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(original,await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }
}
