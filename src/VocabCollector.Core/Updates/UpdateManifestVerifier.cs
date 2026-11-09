using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VocabCollector.Core.Updates;

/// <summary>Validates the signed envelope before interpreting any release metadata.</summary>
public sealed class UpdateManifestVerifier
{
    private readonly string _publicKeyPem;
    private readonly Uri _manifestUri;

    public UpdateManifestVerifier(string publicKeyPem, Uri manifestUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        ArgumentNullException.ThrowIfNull(manifestUri);
        UpdatePolicy.ValidateManifestUri(manifestUri);
        _publicKeyPem = publicKeyPem;
        _manifestUri = manifestUri;
    }

    public VerifiedUpdate Verify(string signedManifest)
    {
        ArgumentNullException.ThrowIfNull(signedManifest);
        if (Encoding.UTF8.GetByteCount(signedManifest) > UpdatePolicy.MaximumManifestBytes)
            throw new InvalidDataException("更新清单超过 64 KiB 限制。");
        try
        {
            using var envelope = JsonDocument.Parse(signedManifest, new JsonDocumentOptions { MaxDepth = 8 });
            RequireProperties(envelope.RootElement, "payload", "signature");
            var payload = Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!);
            var signature = Convert.FromBase64String(envelope.RootElement.GetProperty("signature").GetString()!);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKeyPem);
            if (rsa.KeySize < 3072 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("更新清单签名无效。");

            using var body = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = body.RootElement;
            RequireProperties(root, "version", "platform", "packageUrl", "sizeBytes", "sha256", "releaseNotes", "publishedAt");
            if (!Uri.TryCreate(root.GetProperty("packageUrl").GetString(), UriKind.Absolute, out var packageUrl))
                throw new InvalidDataException("更新包地址无效。");
            var notes = root.GetProperty("releaseNotes");
            if (notes.ValueKind != JsonValueKind.Array) throw new InvalidDataException("更新说明必须是数组。");
            var release = new UpdateRelease(root.GetProperty("version").GetString()!, root.GetProperty("platform").GetString()!,
                packageUrl, root.GetProperty("sizeBytes").GetInt64(), root.GetProperty("sha256").GetString()!,
                notes.EnumerateArray().Select(note => note.GetString()!).ToArray(), root.GetProperty("publishedAt").GetDateTimeOffset());
            UpdatePolicy.ValidateRelease(release, _manifestUri);
            return new VerifiedUpdate(release, signedManifest);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or CryptographicException or OverflowException)
        {
            throw new InvalidDataException("更新清单格式或签名无效。", ex);
        }
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新清单必须是 JSON 对象。");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("更新清单包含重复或未知字段。");
        if (remaining.Count != 0) throw new InvalidDataException("更新清单缺少必需字段。");
    }
}
