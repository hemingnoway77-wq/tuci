using System.Text.Json;

namespace VocabCollector.Core.Library;

internal static class LocalLibraryState
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("路径含有目录或文件链接，无法安全访问：" + current);
        }
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var absolute = Path.GetFullPath(path);
        EnsureNoLinks(absolute);
        var directory = Path.GetDirectoryName(absolute)!;
        Directory.CreateDirectory(directory);
        EnsureNoLinks(absolute);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(absolute);
            File.Move(temporary, absolute, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
