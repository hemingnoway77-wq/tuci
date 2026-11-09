namespace VocabCollector.Updater;

/// <summary>Replaces only packaged files. Backups survive both commit and rollback.</summary>
public sealed class FileInstallTransaction
{
    public void Install(string sourceDirectory, string installDirectory, string backupDirectory,
        Action? afterInstall = null, Action<string>? beforeReplace = null,
        CancellationToken cancellationToken = default)
    {
        sourceDirectory = InstallPathSafety.FullPath(sourceDirectory);
        installDirectory = InstallPathSafety.FullPath(installDirectory);
        backupDirectory = InstallPathSafety.FullPath(backupDirectory);
        InstallPathSafety.RequireNonRootDirectory(sourceDirectory);
        InstallPathSafety.RequireNonRootDirectory(installDirectory);
        InstallPathSafety.RequireNoLinks(backupDirectory);
        RequireSeparate(sourceDirectory, installDirectory);
        RequireSeparate(backupDirectory, installDirectory);
        RequireSeparate(sourceDirectory, backupDirectory);
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
            throw new InvalidDataException("备份目录必须是全新的目录。");

        var files = EnumerateFiles(sourceDirectory).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidDataException("更新包中没有应用文件。");
        // Validate the complete destination set before making the first change.
        foreach (var source in files)
        {
            var destination = Destination(sourceDirectory, source, installDirectory);
            InstallPathSafety.RequireNoLinks(destination);
            if (Directory.Exists(destination)) throw new InvalidDataException("更新目标与已有目录冲突。");
        }

        Directory.CreateDirectory(backupDirectory);
        var changes = new List<Change>();
        var createdDirectories = new List<string>();
        try
        {
            foreach (var source in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Destination(sourceDirectory, source, installDirectory);
                var backup = Destination(sourceDirectory, source, backupDirectory);
                InstallPathSafety.RequireNoLinks(source);
                InstallPathSafety.RequireNoLinks(destination);
                EnsureDirectory(Path.GetDirectoryName(destination)!, createdDirectories);
                string? originalBackup = null;
                if (File.Exists(destination))
                {
                    InstallPathSafety.RequireNoLinks(backup);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                    originalBackup = backup;
                }
                beforeReplace?.Invoke(destination);
                // Rename is the only mutation of the destination. A failed rename leaves its original
                // untouched; do not try to overwrite a still-locked original during rollback.
                ReplaceFrom(source, destination, () => changes.Add(new Change(destination, originalBackup)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            afterInstall?.Invoke();
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    InstallPathSafety.RequireNoLinks(change.Path);
                    if (change.Backup is not null) ReplaceFrom(change.Backup, change.Path);
                    else if (File.Exists(change.Path)) File.Delete(change.Path);
                }
                catch (Exception error) { rollbackFailures.Add(error); }
            }
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
            {
                try
                {
                    InstallPathSafety.RequireNoLinks(directory);
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception error) { rollbackFailures.Add(error); }
            }
            if (rollbackFailures.Count != 0)
                throw new UpdateRollbackException(failure, rollbackFailures);
            throw;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        InstallPathSafety.RequireNoLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            InstallPathSafety.RequireNoLinks(entry);
            if (Directory.Exists(entry))
            {
                foreach (var child in EnumerateFiles(entry)) yield return child;
            }
            else yield return entry;
        }
    }

    private static void RequireSeparate(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
            InstallPathSafety.IsInside(first, second) || InstallPathSafety.IsInside(second, first))
            throw new InvalidDataException("安装、解压和备份目录必须互相独立。");
    }

    private static string Destination(string root, string source, string destinationRoot)
    {
        var destination = InstallPathSafety.FullPath(Path.Combine(destinationRoot, Path.GetRelativePath(root, source)));
        InstallPathSafety.RequireInside(destination, destinationRoot);
        return destination;
    }

    private static void EnsureDirectory(string path, List<string> created)
    {
        InstallPathSafety.RequireNoLinks(path);
        if (Directory.Exists(path)) return;
        EnsureDirectory(Path.GetDirectoryName(path)!, created);
        Directory.CreateDirectory(path);
        created.Add(path);
    }

    private static void ReplaceFrom(string source, string destination, Action? committed = null)
    {
        InstallPathSafety.RequireNoLinks(source);
        InstallPathSafety.RequireNoLinks(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".tuci-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            InstallPathSafety.RequireNoLinks(destination);
            File.Move(temporary, destination, overwrite: true);
            committed?.Invoke();
        }
        finally
        {
            InstallPathSafety.RequireNoLinks(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Change(string Path, string? Backup);
}

public sealed class UpdateRollbackException(Exception installFailure, IReadOnlyList<Exception> rollbackFailures)
    : IOException("更新未能完成自动恢复，请使用事务目录中的备份恢复应用。", installFailure)
{
    public IReadOnlyList<Exception> RollbackFailures { get; } = rollbackFailures;
}
