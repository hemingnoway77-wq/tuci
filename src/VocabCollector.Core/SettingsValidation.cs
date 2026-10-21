using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
namespace VocabCollector.Core;
public static class SettingsValidation
{
    public static void Validate(AppSettings settings)
    {
        VaultPaths.WordRoot(settings.Vault);
        ValidateApi(settings.Api);
    }
    public static void ValidateApi(ApiOptions options)
    {
        ChatEndpoint.Resolve(options.BaseUrl);
        if (string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Length > 8192 || options.ApiKey.Any(char.IsControl)) throw new ArgumentException("请填写有效的 API Key。");
        if (string.IsNullOrWhiteSpace(options.Model) || options.Model.Length > 256 || options.Model.Any(char.IsControl)) throw new ArgumentException("请填写有效的模型名。");
    }
}
