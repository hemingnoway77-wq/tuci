using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Updates;

namespace VocabCollector.Updater;

public sealed record UpdateInstallResult(bool Success, string Version, string Message,
    bool Restarted, bool Restored, string? BackupDirectory, DateTimeOffset CompletedAt);

public sealed class UpdateInstaller
{
    public async Task<UpdateInstallResult> RunAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        // Do not write a log or result until the transaction root itself has been validated.
        requestPath = ValidateRequestLocation(requestPath);
        var transactionDirectory = Path.GetDirectoryName(requestPath)!;
        var logPath = Path.Combine(transactionDirectory, "update.log");
        var resultPath = Path.Combine(transactionDirectory, "update-result.json");
        InstallPathSafety.RequireNoLinks(logPath);
        InstallPathSafety.RequireNoLinks(resultPath);
        UpdateInstallRequest? request = null;
        string? backupDirectory = null;
        string? executable = null;
        var authenticatedVersion = "";
        var parentExited = false;
        var parentWaitAttempted = false;
        var restarted = false;
        var attemptedInstall = false;
        using var installationLease = new InstallationLease();
        var phase = "读取更新请求";
        UpdateInstallResult result;
        try
        {
            Log(logPath, "开始安装更新。");
            if (new FileInfo(requestPath).Length > 64 * 1024) throw new InvalidDataException("更新请求过大。");
            request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken))
                ?? throw new InvalidDataException("更新请求为空。");
            executable = ValidatePlan(request, transactionDirectory);
            installationLease.Acquire(request.InstallDirectory, Path.GetDirectoryName(transactionDirectory)!);
            phase = "验证更新签名和完整性";
            if (new FileInfo(request.ManifestPath).Length > 64 * 1024)
                throw new InvalidDataException("更新清单过大。");
            var manifest = await File.ReadAllTextAsync(request.ManifestPath, cancellationToken);
            var verified = new UpdateManifestVerifier(UpdateConfiguration.PublicKeyPem, UpdateConfiguration.ManifestUri).Verify(manifest);
            authenticatedVersion = verified.Release.Version;
            if (!string.Equals(verified.Release.Version, request.ExpectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("更新请求版本与签名清单不一致。");
            RequireNewerVersion(executable, verified.Release.Version);

            // A read-only sharing lock prevents modification between verification and extraction on Windows.
            using var packageLock = new FileStream(request.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await UpdatePackageVerifier.VerifyAsync(request.PackagePath, verified, cancellationToken);
            phase = "解压更新文件";
            var extractDirectory = Path.Combine(transactionDirectory, "extracted-" + Guid.NewGuid().ToString("N"));
            await UpdateArchive.ExtractAsync(request.PackagePath, extractDirectory, cancellationToken);
            if (ReadVersion(Path.Combine(extractDirectory, UpdateConfiguration.AppExecutableName)) !=
                NormalizeVersion(Version.Parse(verified.Release.Version)))
                throw new InvalidDataException("更新包中的应用版本与签名清单不一致。");
            phase = "等待兔词退出";
            parentWaitAttempted = true;
            await new ParentProcessGuard().WaitAsync(request.ParentProcessId, request.ParentProcessStartTimeUtcTicks,
                executable, TimeSpan.FromSeconds(60), cancellationToken);
            parentExited = true;
            // Revalidate after waiting: an independently launched installer may have changed the application.
            ValidatePlan(request, transactionDirectory);
            RequireNewerVersion(executable, verified.Release.Version);
            phase = "替换应用文件";
            backupDirectory = Path.Combine(transactionDirectory, "backup-" + Guid.NewGuid().ToString("N"));
            attemptedInstall = true;
            new FileInstallTransaction().Install(extractDirectory, request.InstallDirectory, backupDirectory,
                afterInstall: request.Restart ? () => { StartApplication(executable); restarted = true; } : null,
                cancellationToken: cancellationToken);
            result = new UpdateInstallResult(true, verified.Release.Version, "更新已安装。", restarted, false,
                backupDirectory, DateTimeOffset.UtcNow);
            Log(logPath, "更新安装完成，原文件备份已保留。");
        }
        catch (Exception error)
        {
            var restored = attemptedInstall && error is not UpdateRollbackException;
            // A rollback failure requires manual recovery. Never run a partially restored application.
            if (request?.Restart == true && executable is not null && error is not UpdateRollbackException &&
                installationLease.IsHeld && (parentExited || !parentWaitAttempted))
            {
                try
                {
                    if (!parentExited)
                    {
                        await new ParentProcessGuard().WaitAsync(request.ParentProcessId,
                            request.ParentProcessStartTimeUtcTicks, executable, TimeSpan.FromSeconds(60),
                            CancellationToken.None);
                        parentExited = true;
                    }
                    InstallPathSafety.RequireNoLinks(executable);
                    if (parentExited) { StartApplication(executable); restarted = true; }
                }
                catch (Exception restartError)
                {
                    Log(logPath, "未能重新启动应用：" + restartError.GetType().Name + "。");
                }
            }
            var message = error is UpdateRollbackException
                ? "更新失败，自动恢复未全部完成。请使用备份目录恢复应用，详情见 update.log。"
                : error is TimeoutException
                    ? "兔词未在 60 秒内退出，更新已取消。请关闭兔词后重试。"
                    : attemptedInstall
                        ? "更新失败，应用文件已恢复。请关闭其他占用应用文件的程序后重试。"
                        : "更新未安装：" + phase + "失败。请重新打开兔词后重试。";
            result = new UpdateInstallResult(false, authenticatedVersion, message,
                restarted, restored, backupDirectory, DateTimeOffset.UtcNow);
            // Error types and phases are useful for recovery without copying untrusted URLs or credentials into logs.
            Log(logPath, phase + "失败：" + error.GetType().Name + "。");
            Log(logPath, message);
        }
        InstallPathSafety.RequireNoLinks(resultPath);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        return result;
    }

    public static string ValidateRequestLocation(string requestPath)
    {
        requestPath = InstallPathSafety.FullPath(requestPath);
        InstallPathSafety.RequireNoLinks(requestPath);
        var updatesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VocabCollector", "updates");
        InstallPathSafety.RequireInside(requestPath, updatesRoot);
        var transactionDirectory = Path.GetDirectoryName(requestPath)!;
        var relative = Path.GetRelativePath(updatesRoot, transactionDirectory);
        if (relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar) ||
            !Guid.TryParse(relative, out _) || !string.Equals(Path.GetFileName(requestPath), "install-request.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新请求必须位于独立的更新事务目录中。");
        if (!File.Exists(requestPath)) throw new FileNotFoundException("更新请求不存在。");
        return requestPath;
    }

    private static string ValidatePlan(UpdateInstallRequest request, string transactionDirectory)
    {
        var installDirectory = InstallPathSafety.FullPath(request.InstallDirectory);
        InstallPathSafety.RequireNonRootDirectory(installDirectory);
        var executable = Path.Combine(installDirectory, UpdateConfiguration.AppExecutableName);
        InstallPathSafety.RequireNoLinks(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("安装目录中没有兔词应用。");
        if (request.ParentProcessId <= 0 || request.ParentProcessStartTimeUtcTicks <= 0)
            throw new InvalidDataException("更新请求缺少应用进程信息。");
        var updater = InstallPathSafety.FullPath(Environment.ProcessPath ?? throw new InvalidDataException("无法获取更新器路径。"));
        InstallPathSafety.RequireNoLinks(updater);
        if (InstallPathSafety.IsInside(updater, installDirectory) ||
            string.Equals(updater, executable, StringComparison.OrdinalIgnoreCase) ||
            InstallPathSafety.IsInside(transactionDirectory, installDirectory) ||
            InstallPathSafety.IsInside(installDirectory, transactionDirectory) ||
            string.Equals(installDirectory, transactionDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新器和事务目录必须位于应用安装目录之外。");
        InstallPathSafety.RequireInside(request.PackagePath, transactionDirectory);
        InstallPathSafety.RequireInside(request.ManifestPath, transactionDirectory);
        InstallPathSafety.RequireNoLinks(request.PackagePath);
        InstallPathSafety.RequireNoLinks(request.ManifestPath);
        if (!File.Exists(request.PackagePath) || !File.Exists(request.ManifestPath) ||
            string.Equals(InstallPathSafety.FullPath(request.PackagePath), InstallPathSafety.FullPath(request.ManifestPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包或签名清单路径无效。");
        // Probe write access while the original application is still intact.
        var probe = Path.Combine(installDirectory, ".tuci-writecheck-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        File.Delete(probe);
        return executable;
    }

    private static void RequireNewerVersion(string executable, string releaseVersion)
    {
        if (NormalizeVersion(Version.Parse(releaseVersion)) <= ReadVersion(executable))
            throw new InvalidDataException("更新版本必须高于已安装版本。");
    }

    private static Version ReadVersion(string executable)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(executable);
        var versionText = (fileVersion.ProductVersion ?? fileVersion.FileVersion ?? "").Split('+')[0];
        if (!Version.TryParse(versionText, out var version))
            throw new InvalidDataException("无法验证已安装应用的版本。");
        return NormalizeVersion(version);
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static void StartApplication(string executable)
    {
        InstallPathSafety.RequireNoLinks(executable);
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new IOException("无法启动兔词。");
    }

    private static void Log(string path, string message)
    {
        try
        {
            InstallPathSafety.RequireNoLinks(path);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Logging must never turn a committed installation into a reported rollback.
        }
    }

    private sealed class InstallationLease : IDisposable
    {
        private FileStream? stream;
        public bool IsHeld => stream is not null;

        public void Acquire(string installDirectory, string updatesRoot)
        {
            var identity = Encoding.UTF8.GetBytes(InstallPathSafety.FullPath(installDirectory).ToUpperInvariant());
            var path = Path.Combine(updatesRoot, "install-" + Convert.ToHexString(SHA256.HashData(identity)) + ".lock");
            InstallPathSafety.RequireNoLinks(path);
            // Keep this stable empty lock file. Deleting it after releasing the handle would race a second updater.
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public void Dispose() => stream?.Dispose();
    }
}
