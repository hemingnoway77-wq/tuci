using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VocabCollector.Core.Updates;

namespace VocabCollector.Tests;

public sealed class UpdateArchiveTests
{
    [Fact]
    public async Task PortablePackageExtractsExecutablesAssetsAndReadme()
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path, ("Assets/icon.png", "image"), ("README.txt", "说明"));
        var destination = Path.Combine(directory.Path, "extracted");
        await UpdateArchive.ExtractAsync(package, destination);
        Assert.Equal("app", await File.ReadAllTextAsync(Path.Combine(destination, UpdateConfiguration.AppExecutableName)));
        Assert.Equal("image", await File.ReadAllTextAsync(Path.Combine(destination, "Assets", "icon.png")));
        Assert.True(File.Exists(Path.Combine(destination, UpdateConfiguration.UpdaterExecutableName)));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("Assets/../../outside.txt")]
    [InlineData("Assets\\outside.txt")]
    [InlineData("Assets//outside.txt")]
    [InlineData("Assets/./outside.txt")]
    [InlineData("Assets/file.txt:stream")]
    [InlineData("Assets/CON.txt")]
    [InlineData("Assets/COM1")]
    [InlineData("Assets/name. ")]
    [InlineData("Assets/trailing.")]
    public async Task UnsafePathsAreRejectedBeforeAnyExtraction(string entryPath)
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path, (entryPath, "attack"));
        var destination = Path.Combine(directory.Path, "extracted");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, destination));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(directory.Path, "outside.txt")));
    }

    [Theory]
    [InlineData("README.txt", "readme.TXT")]
    [InlineData("README.txt", "README.txt")]
    [InlineData("Assets", "Assets/file.txt")]
    [InlineData("Assets/file.txt", "assets")]
    [InlineData("Assets/one.txt", "assets/two.txt")]
    public async Task DuplicatesCaseConflictsAndFileDirectoryConflictsAreRejected(string first, string second)
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path, (first, "one"), (second, "two"));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, Path.Combine(directory.Path, "out")));
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData(unchecked((int)0x11FF0000))]
    public async Task LinksAndSpecialFilesAreRejected(int attributes)
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = attributes;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../outside");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, Path.Combine(directory.Path, "out")));
    }

    [Fact]
    public async Task NonemptyDestinationIsPreservedAndIncompletePackageIsRejected()
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path);
        var destination = Directory.CreateDirectory(Path.Combine(directory.Path, "out")).FullName;
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, destination));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update)) archive.GetEntry(UpdateConfiguration.UpdaterExecutableName)!.Delete();
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, Path.Combine(directory.Path, "new")));
    }

    [Fact]
    public async Task DeclaredDecompressionBombIsRejectedWithoutAllocatingPayload()
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path, ("Assets/one", ""), ("Assets/two", ""), ("Assets/three", ""));
        foreach (var name in new[] { "Assets/one", "Assets/two", "Assets/three" }) PatchUncompressedSize(package, name, 400 * 1024 * 1024);
        var destination = Path.Combine(directory.Path, "out");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExcessiveEntryCountAndCancellationAreRejected()
    {
        using var directory = new UpdateTestDirectory();
        var package = CreatePackage(directory.Path);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
            for (var i = 0; i < 9999; i++) archive.CreateEntry($"Assets/file{i}");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(package, Path.Combine(directory.Path, "out")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateArchive.ExtractAsync(package, Path.Combine(directory.Path, "cancelled"), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "cancelled")));
    }

    private static string CreatePackage(string directory, params (string Path, string Content)[] extras)
    {
        var package = Path.Combine(directory, "package.zip");
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        foreach (var (path, content) in new[] { (UpdateConfiguration.AppExecutableName, "app"), (UpdateConfiguration.UpdaterExecutableName, "updater") }.Concat(extras))
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return package;
    }

    private static void PatchUncompressedSize(string package, string name, uint size)
    {
        var bytes = File.ReadAllBytes(package);
        for (var i = 0; i <= bytes.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
            if (Encoding.UTF8.GetString(bytes, i + 46, nameLength) != name) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), size);
            File.WriteAllBytes(package, bytes);
            return;
        }
        throw new InvalidOperationException("ZIP entry not found.");
    }
}
