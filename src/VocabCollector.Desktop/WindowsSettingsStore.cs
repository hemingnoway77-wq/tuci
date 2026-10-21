using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core;
namespace VocabCollector.Desktop;

public sealed class WindowsSettingsStore(string settingsDirectory) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private string SettingsFile => Path.Combine(settingsDirectory, "settings.json");
    private sealed record StoredSettings(string BaseUrl, string Model, string VaultRoot, string OutputSubfolder, bool ClassifyByPartOfSpeech, string EncryptedApiKey);
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(SettingsFile)) return new(AppSettings.Default, "请先点击“设置”，填写 API 和 Obsidian 仓库位置。");
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(await File.ReadAllTextAsync(SettingsFile, ct), JsonOptions) ?? throw new JsonException();
            if (stored.BaseUrl == null || stored.Model == null || stored.VaultRoot == null || stored.OutputSubfolder == null || stored.EncryptedApiKey == null) throw new JsonException();
            var key = ""; var warning = "";
            try { key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.EncryptedApiKey), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is FormatException or CryptographicException) { warning = "API Key 无法解密，请在设置中重新填写。其他配置已保留。"; }
            return new(new(new(stored.BaseUrl, key, stored.Model), new(stored.VaultRoot, stored.OutputSubfolder, stored.ClassifyByPartOfSpeech)), warning);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { return new(AppSettings.Default, "配置文件损坏或无法读取，原文件已保留；请打开设置重新填写。"); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        SettingsValidation.Validate(settings);
        var encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.Api.ApiKey.Trim()), null, DataProtectionScope.CurrentUser));
        var stored = new StoredSettings(settings.Api.BaseUrl.Trim(), settings.Api.Model.Trim(), settings.Vault.RootPath, settings.Vault.OutputSubfolder, settings.Vault.ClassifyByPartOfSpeech, encrypted);
        Directory.CreateDirectory(settingsDirectory);
        var temporary = Path.Combine(settingsDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(stored, JsonOptions), new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, SettingsFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
