using VocabCollector.Updater;

namespace VocabCollector.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TuciInstallerTests-" + Guid.NewGuid().ToString("N"));

    public UpdateInstallerTests() => Directory.CreateDirectory(root);

    [Fact]
    public void TransactionReplacesPackagedFilesAndPreservesUserFilesAndBackups()
    {
        var source = Folder("source");
        var target = Folder("installed");
        var backup = Path.Combine(root, "backup");
        Write(target, "兔词.exe", "old app");
        Write(target, "notes/my words.md", "my own words");
        Write(target, "obsolete.dll", "old extra file");
        Write(source, "兔词.exe", "new app");
        Write(source, "runtimes/native.dll", "new dependency");
        var restartCount = 0;

        new FileInstallTransaction().Install(source, target, backup, afterInstall: () => restartCount++);

        Assert.Equal("new app", Read(target, "兔词.exe"));
        Assert.Equal("new dependency", Read(target, "runtimes/native.dll"));
        Assert.Equal("my own words", Read(target, "notes/my words.md"));
        Assert.Equal("old extra file", Read(target, "obsolete.dll"));
        Assert.Equal("old app", Read(backup, "兔词.exe"));
        Assert.False(File.Exists(Path.Combine(backup, "runtimes/native.dll")));
        Assert.Equal(1, restartCount);
    }

    [Fact]
    public void MidInstallFailureRestoresReplacedFilesAndRemovesNewFiles()
    {
        var source = Folder("source");
        var target = Folder("installed");
        var backup = Path.Combine(root, "backup");
        Write(target, "a-app.exe", "old app");
        Write(target, "z-last.dll", "old last");
        Write(target, "notes.md", "user file");
        Write(source, "a-app.exe", "new app");
        Write(source, "b-new/file.dll", "new dependency");
        Write(source, "z-last.dll", "new last");

        Assert.Throws<IOException>(() => new FileInstallTransaction().Install(source, target, backup,
            beforeReplace: path =>
            {
                if (Path.GetFileName(path) == "z-last.dll") throw new IOException("Simulated disk failure.");
            }));

        Assert.Equal("old app", Read(target, "a-app.exe"));
        Assert.Equal("old last", Read(target, "z-last.dll"));
        Assert.Equal("user file", Read(target, "notes.md"));
        Assert.False(Directory.Exists(Path.Combine(target, "b-new")));
        Assert.Equal("old app", Read(backup, "a-app.exe"));
        Assert.Empty(Directory.GetFiles(target, ".tuci-update-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FailedRestartRollsBackTheWholeInstallation()
    {
        var source = Folder("source");
        var target = Folder("installed");
        Write(target, "兔词.exe", "old app");
        Write(source, "兔词.exe", "new app");
        Write(source, "new.dll", "dependency");

        Assert.Throws<IOException>(() => new FileInstallTransaction().Install(source, target,
            Path.Combine(root, "backup"), afterInstall: () => throw new IOException("Cannot launch.")));

        Assert.Equal("old app", Read(target, "兔词.exe"));
        Assert.False(File.Exists(Path.Combine(target, "new.dll")));
    }

    [Fact]
    public void LockedDependencyRestoresEarlierFilesWithoutOverwritingTheUnchangedLockedFile()
    {
        var source = Folder("source");
        var target = Folder("installed");
        Write(target, "a-app.exe", "old app");
        Write(target, "z-locked.dll", "old dependency");
        Write(source, "a-app.exe", "new app");
        Write(source, "z-locked.dll", "new dependency");
        using var locked = new FileStream(Path.Combine(target, "z-locked.dll"), FileMode.Open,
            FileAccess.Read, FileShare.Read);

        var error = Record.Exception(() => new FileInstallTransaction().Install(source, target,
            Path.Combine(root, "backup")));

        Assert.True(error is IOException or UnauthorizedAccessException,
            "A locked Windows file must fail installation with an I/O or access error.");
        Assert.IsNotType<UpdateRollbackException>(error);
        Assert.Equal("old app", Read(target, "a-app.exe"));
        Assert.Equal("old dependency", Read(target, "z-locked.dll"));
    }

    [Fact]
    public void PackageDirectoryCannotOverlapInstallationAndTraversalCannotEscapeRoot()
    {
        var target = Folder("installed");
        var source = Path.Combine(target, "package");
        Directory.CreateDirectory(source);
        Write(target, "兔词.exe", "old");
        Write(source, "兔词.exe", "new");

        Assert.Throws<InvalidDataException>(() => new FileInstallTransaction().Install(source, target,
            Path.Combine(root, "backup")));
        Assert.Throws<InvalidDataException>(() => InstallPathSafety.RequireInside(
            Path.Combine(root, "package", "..", "outside.zip"), Path.Combine(root, "package")));
        Assert.Equal("old", Read(target, "兔词.exe"));
    }

    [Fact]
    public void DirectoryLinkInDestinationIsRejectedBeforeAnyFileChanges()
    {
        var source = Folder("source");
        var target = Folder("installed");
        var outside = Folder("user-vault");
        Write(target, "a-app.exe", "old app");
        Write(outside, "vocabulary.md", "user words");
        Write(source, "a-app.exe", "new app");
        Write(source, "linked/vocabulary.md", "package file");
        var link = Path.Combine(target, "linked");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() => new FileInstallTransaction().Install(source, target,
                Path.Combine(root, "backup")));
            Assert.Equal("old app", Read(target, "a-app.exe"));
            Assert.Equal("user words", Read(outside, "vocabulary.md"));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void DirectoryLinkInSourceIsRejectedBeforeChanges()
    {
        var source = Folder("source");
        var target = Folder("installed");
        var outside = Folder("outside");
        Write(target, "a-app.exe", "old app");
        Write(source, "a-app.exe", "new app");
        Write(outside, "file.dll", "external");
        var link = Path.Combine(source, "linked");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() => new FileInstallTransaction().Install(source, target,
                Path.Combine(root, "backup")));
            Assert.Equal("old app", Read(target, "a-app.exe"));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(101L, "兔词.exe")]
    [InlineData(100L, "other.exe")]
    public async Task ReusedPidOrDifferentExecutableNeverWaitsForOrTerminatesUnrelatedProcess(long ticks, string name)
    {
        var expected = Path.Combine(root, "兔词.exe");
        var probe = new FakeProcesses(new UpdateParentProcess(123, ticks, Path.Combine(root, name)));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ParentProcessGuard(probe).WaitAsync(
            123, 100, expected, TimeSpan.FromSeconds(60)));

        Assert.Equal(0, probe.WaitCalls);
    }

    [Fact]
    public async Task WaitTimeoutAbortsAndUsesTheBoundedTimeout()
    {
        var expected = Path.Combine(root, "兔词.exe");
        var probe = new FakeProcesses(new UpdateParentProcess(123, 100, expected)) { Exits = false };

        await Assert.ThrowsAsync<TimeoutException>(() => new ParentProcessGuard(probe).WaitAsync(
            123, 100, expected, TimeSpan.FromSeconds(60)));

        Assert.Equal(TimeSpan.FromSeconds(60), probe.Timeout);
        Assert.Equal(1, probe.WaitCalls);
    }

    [Fact]
    public async Task AlreadyExitedParentAllowsInstallationWithoutWaiting()
    {
        var probe = new FakeProcesses(null);
        await new ParentProcessGuard(probe).WaitAsync(123, 100, Path.Combine(root, "兔词.exe"), TimeSpan.FromSeconds(60));
        Assert.Equal(0, probe.WaitCalls);
    }

    private sealed class FakeProcesses(UpdateParentProcess? process) : IUpdateParentProcessAccess
    {
        public bool Exits { get; init; } = true;
        public int WaitCalls { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public UpdateParentProcess? Find(int processId) => process;
        public Task<bool> WaitForExitAsync(UpdateParentProcess process, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WaitCalls++;
            Timeout = timeout;
            return Task.FromResult(Exits);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // NTFS junctions do not require Developer Mode or symlink privileges.
        var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") +
            "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Write(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string directory, string relative) => File.ReadAllText(Path.Combine(directory, relative));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
