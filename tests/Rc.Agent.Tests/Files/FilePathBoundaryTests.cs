using System.Diagnostics;
using Rc.Agent.Files;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Cli.Commands;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Files;

public sealed class FilePathBoundaryTests
{
    [Fact]
    public async Task AgentRecursiveOperationsRejectJunctions()
    {
        using var outside = new TemporaryDirectory();
        using var root = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "secret");
        var junction = Path.Combine(root.Path, "escape");
        await CreateJunctionAsync(junction, outside.Path);
        await using var store = new AgentStateStore(Path.Combine(root.Path, "state"));
        await store.InitializeAsync();
        using var service = new FileTransferService(store, new Rc.Agent.Configuration.AgentOptions { FileRoot = root.Path });

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.ListAsync(new FileListRequest(".", Recursive: true)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.GetManifestAsync(new FileManifestRequest(".")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void DownloadManifestRejectsPathsOutsideDestination()
    {
        using var destination = new TemporaryDirectory();
        var manifest = new FileManifest("remote", [
            new FileManifestEntry("../escape.txt", 1, DateTimeOffset.UtcNow, new string('A', 64)),
        ]);

        Assert.Throws<InvalidDataException>(() => LocalTransferPaths.ResolveManifest(destination.Path, manifest));

        var absoluteManifest = new FileManifest("remote", [
            new FileManifestEntry(Path.Combine(destination.Path, "absolute.txt"), 1, DateTimeOffset.UtcNow, new string('A', 64)),
        ]);
        Assert.Throws<InvalidDataException>(() => LocalTransferPaths.ResolveManifest(destination.Path, absoluteManifest));
    }

    [Fact]
    public async Task LocalUploadManifestRejectsJunctions()
    {
        using var outside = new TemporaryDirectory();
        using var root = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "secret");
        var junction = Path.Combine(root.Path, "escape");
        await CreateJunctionAsync(junction, outside.Path);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => FileCommand.BuildLocalManifestAsync(root.Path));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task LocalStreamingManifestUsesMetadataWithoutReadingFileHash()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "payload.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

        var manifest = await FileCommand.BuildLocalManifestAsync(path, includeHashes: false);

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(FileEntryKind.File, entry.Kind);
        Assert.Null(entry.Sha256);
        Assert.Equal(4, entry.Length);
    }

    private static async Task CreateJunctionAsync(string path, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start mklink.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"mklink exited with {process.ExitCode}.\nSTDOUT:\n{output}\nSTDERR:\n{error}");
    }
}
