using Rc.Agent.Configuration;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Agent.Updates;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Updates;

public sealed class UpdateServiceTests
{
    [Fact]
    public void UpdateApplierCommandLaunchesDetachedSystemTask()
    {
        var command = TaskRegistryUpdateApplier.BuildUpdaterCommand(
            @"C:\staged package\Update-RemoteController.ps1",
            @"C:\staged package",
            "C:\\Program Files\\RemoteController\\",
            "C:\\ProgramData\\RemoteController\\",
            43001);

        Assert.Contains("Register-ScheduledTask", command, StringComparison.Ordinal);
        Assert.Contains("Start-ScheduledTask", command, StringComparison.Ordinal);
        Assert.Contains("-UserId 'SYSTEM'", command, StringComparison.Ordinal);
        Assert.Contains("Invoke-RemoteControllerDetachedUpdate.ps1", command, StringComparison.Ordinal);
        Assert.Contains("update-ready", command, StringComparison.Ordinal);
        Assert.Contains("update-result.json", command, StringComparison.Ordinal);
        Assert.Contains("update-stdout.log", command, StringComparison.Ordinal);
        Assert.Contains("update-stderr.log", command, StringComparison.Ordinal);
        Assert.Contains("-ExecutionPolicy Bypass", command, StringComparison.Ordinal);
        Assert.Contains("-InstallPath \"C:\\Program Files\\RemoteController\"", command, StringComparison.Ordinal);
        Assert.Contains("-DataRoot \"C:\\ProgramData\\RemoteController\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteController\\\\\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteStagesValidatedPackageAndStartsElevatedApplication()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var applier = new RecordingApplier();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumUpdatePackageBytes = 1024,
        }, applier);
        var files = RequiredFiles().Select((path, index) => new UpdatePackageFile(path, 1, Hash([(byte)index]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));

        var started = await service.StartAsync(request);
        Assert.Equal(UpdateState.Receiving, started.State);
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(request.UpdateId, file.RelativePath, 0, [(byte)index], file.Sha256));
        }

        var completed = await service.CompleteAsync(new UpdateCompleteRequest(request.UpdateId));

        Assert.Equal(UpdateState.Applying, completed.State);
        Assert.Equal("job-update", completed.InstallationJobId);
        Assert.NotNull(applier.PackagePath);
        Assert.True(File.Exists(Path.Combine(applier.PackagePath!, "Rc.Agent.exe")));
        var sessionDirectory = Path.Combine(directory.Path, "updates", request.UpdateId.ToString("N"));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "update-ready")));
        Assert.Contains("\"state\":\"applying\"", await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "update-state.json")), StringComparison.Ordinal);

        await store.SaveJobSnapshotAsync(new JobSnapshot(
            "job-update", JobState.Exited, 0, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow, null, ExecutionIdentity.ElevatedBroker));

        var finished = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));
        Assert.Equal(UpdateState.Succeeded, finished.State);
    }

    [Fact]
    public async Task DetachedUpdaterCompletionSurvivesBrokerRestart()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumUpdatePackageBytes = 1024,
        }, new RecordingApplier());
        var files = RequiredFiles().Select((path, index) => new UpdatePackageFile(path, 1, Hash([(byte)index]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));

        await service.StartAsync(request);
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(request.UpdateId, file.RelativePath, 0, [(byte)index], file.Sha256));
        }
        var applying = await service.CompleteAsync(new UpdateCompleteRequest(request.UpdateId));
        Assert.Equal(UpdateState.Applying, applying.State);

        var sessionDirectory = Path.Combine(directory.Path, "updates", request.UpdateId.ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "update-result.json"),
            """{"succeeded":true,"exitCode":0,"failureMessage":null}""");
        await store.SaveJobSnapshotAsync(new JobSnapshot(
            "job-update", JobState.InterruptedByReboot, null, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow, new RemoteError(ErrorCode.Unavailable, "Broker restarted.", false), ExecutionIdentity.ElevatedBroker));

        var finished = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));

        Assert.Equal(UpdateState.Succeeded, finished.State);
        Assert.Null(finished.FailureMessage);
    }

    [Fact]
    public async Task DetachedUpdaterInProgressSurvivesBrokerRestart()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumUpdatePackageBytes = 1024,
        }, new RecordingApplier());
        var files = RequiredFiles().Select((path, index) => new UpdatePackageFile(path, 1, Hash([(byte)index]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));

        await service.StartAsync(request);
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(request.UpdateId, file.RelativePath, 0, [(byte)index], file.Sha256));
        }
        await service.CompleteAsync(new UpdateCompleteRequest(request.UpdateId));
        var sessionDirectory = Path.Combine(directory.Path, "updates", request.UpdateId.ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "update-detached"), "true");
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "update-started"), DateTimeOffset.UtcNow.ToString("O"));
        await store.SaveJobSnapshotAsync(new JobSnapshot(
            "job-update", JobState.InterruptedByReboot, null, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow, new RemoteError(ErrorCode.Unavailable, "Broker restarted.", false), ExecutionIdentity.ElevatedBroker));

        var status = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));

        Assert.Equal(UpdateState.Applying, status.State);
        Assert.Null(status.FailureMessage);
    }

    [Fact]
    public async Task DetachedBootstrapExitDoesNotCompleteUpdateBeforeRunnerResult()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumUpdatePackageBytes = 1024,
        }, new RecordingApplier());
        var files = RequiredFiles().Select((path, index) => new UpdatePackageFile(path, 1, Hash([(byte)index]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));

        await service.StartAsync(request);
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(request.UpdateId, file.RelativePath, 0, [(byte)index], file.Sha256));
        }
        await service.CompleteAsync(new UpdateCompleteRequest(request.UpdateId));
        var sessionDirectory = Path.Combine(directory.Path, "updates", request.UpdateId.ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "update-detached"), "true");
        await store.SaveJobSnapshotAsync(new JobSnapshot(
            "job-update", JobState.Exited, 0, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow, null, ExecutionIdentity.ElevatedBroker));

        var status = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));

        Assert.Equal(UpdateState.Applying, status.State);
    }

    [Fact]
    public async Task ChunkWithWrongHashIsRejectedWithoutWritingData()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions { MaximumUpdateChunkBytes = 4, MaximumUpdatePackageBytes = 1024 }, new RecordingApplier());
        var files = RequiredFiles().Select(path => new UpdatePackageFile(path, 1, Hash([0]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await service.StartAsync(request);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.WriteChunkAsync(
            new UpdateWriteChunkRequest(request.UpdateId, "Rc.Agent.exe", 0, [0], new string('A', 64))).AsTask());

        var status = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));
        Assert.Equal(0, status.ReceivedBytes);
    }

    [Fact]
    public async Task BinaryChunkStreamsIntoUpdateStagingAndIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumBinaryUpdateChunkBytes = 8,
            MaximumUpdatePackageBytes = 1024,
        }, new RecordingApplier());
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var files = RequiredFiles().Select(path => new UpdatePackageFile(
            path,
            path == "Rc.Agent.exe" ? data.Length : 1,
            path == "Rc.Agent.exe" ? Hash(data) : Hash([0]))).ToArray();
        var start = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await service.StartAsync(start);
        var request = new UpdateBinaryWriteRequest(start.UpdateId, "Rc.Agent.exe", 0, data.Length, Hash(data));
        var ready = new List<UpdateBinaryReadyResponse>();

        var written = await service.WriteBinaryChunkAsync(
            request,
            new MemoryStream(data),
            response => { ready.Add(response); return Task.CompletedTask; });

        Assert.False(Assert.Single(ready).AlreadyCompleted);
        Assert.Equal(data.Length, written.Status.ReceivedBytes);
        Assert.Equal(Hash(data), written.Sha256);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "updates", start.UpdateId.ToString("N"), "payload", "Rc.Agent.exe")));

        ready.Clear();
        var repeated = await service.WriteBinaryChunkAsync(
            request,
            Stream.Null,
            response => { ready.Add(response); return Task.CompletedTask; });

        Assert.True(Assert.Single(ready).AlreadyCompleted);
        Assert.Equal(data.Length, repeated.Status.ReceivedBytes);
    }

    [Fact]
    public async Task BinaryChunkWithWrongHashIsTruncatedAndNotCounted()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumBinaryUpdateChunkBytes = 8,
            MaximumUpdatePackageBytes = 1024,
        }, new RecordingApplier());
        var data = new byte[] { 1, 2, 3, 4 };
        var files = RequiredFiles().Select(path => new UpdatePackageFile(path, path == "Rc.Agent.exe" ? data.Length : 1, Hash([0]))).ToArray();
        var start = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await service.StartAsync(start);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.WriteBinaryChunkAsync(
            new UpdateBinaryWriteRequest(start.UpdateId, "Rc.Agent.exe", 0, data.Length, Hash([0])),
            new MemoryStream(data),
            _ => Task.CompletedTask).AsTask());

        var staged = Path.Combine(directory.Path, "updates", start.UpdateId.ToString("N"), "payload", "Rc.Agent.exe");
        Assert.True(File.Exists(staged));
        Assert.Equal(0, new FileInfo(staged).Length);
        Assert.Equal(0, (await service.GetStatusAsync(new UpdateStatusRequest(start.UpdateId))).ReceivedBytes);
    }

    [Fact]
    public async Task InterruptedBinaryChunkCanBeRetransmittedAndCompletesValidatedManifest()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var applier = new RecordingApplier();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumBinaryUpdateChunkBytes = 8,
            MaximumUpdatePackageBytes = 1024,
        }, applier);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var files = RequiredFiles().Select(path => new UpdatePackageFile(
            path,
            path == "Rc.Agent.exe" ? data.Length : 1,
            path == "Rc.Agent.exe" ? Hash(data) : Hash([0]))).ToArray();
        var start = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await service.StartAsync(start);
        var request = new UpdateBinaryWriteRequest(start.UpdateId, "Rc.Agent.exe", 0, data.Length, Hash(data));

        await Assert.ThrowsAsync<IOException>(() => service.WriteBinaryChunkAsync(
            request,
            new InterruptingReadStream(data, interruptAfterBytes: 3),
            _ => Task.CompletedTask).AsTask());
        Assert.Equal(0, (await service.GetStatusAsync(new UpdateStatusRequest(start.UpdateId))).ReceivedBytes);

        var recovered = await service.WriteBinaryChunkAsync(request, new MemoryStream(data), _ => Task.CompletedTask);
        UpdateBinaryReadyResponse? repeatedReady = null;
        await service.WriteBinaryChunkAsync(
            request,
            Stream.Null,
            ready => { repeatedReady = ready; return Task.CompletedTask; });
        foreach (var file in files.Where(file => file.RelativePath != "Rc.Agent.exe"))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(start.UpdateId, file.RelativePath, 0, [0], file.Sha256));
        }
        var completed = await service.CompleteAsync(new UpdateCompleteRequest(start.UpdateId));

        Assert.Equal(data.Length, recovered.Status.ReceivedBytes);
        Assert.True(repeatedReady?.AlreadyCompleted);
        Assert.Equal(UpdateState.Applying, completed.State);
        Assert.NotNull(applier.PackagePath);
        var staged = Path.Combine(applier.PackagePath!, "Rc.Agent.exe");
        Assert.Equal(data, await File.ReadAllBytesAsync(staged));
        Assert.Equal(Hash(data), await HashFileAsync(staged));
    }

    [Fact]
    public async Task DetachedUpdateWithoutDurableResultFailsAfterConfiguredTimeout()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumUpdatePackageBytes = 1024,
            DetachedUpdateResultTimeout = TimeSpan.FromSeconds(30),
        }, new RecordingApplier(usesDetachedCompletion: true), clock);
        var files = RequiredFiles().Select((path, index) => new UpdatePackageFile(path, 1, Hash([(byte)index]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await service.StartAsync(request);
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(request.UpdateId, file.RelativePath, 0, [(byte)index], file.Sha256));
        }
        Assert.Equal(UpdateState.Applying, (await service.CompleteAsync(new UpdateCompleteRequest(request.UpdateId))).State);
        clock.Advance(TimeSpan.FromSeconds(31));

        var timedOut = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));

        Assert.Equal(UpdateState.Failed, timedOut.State);
        Assert.Contains("30 seconds", timedOut.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("update-stdout.log", timedOut.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("update-stderr.log", timedOut.FailureMessage, StringComparison.Ordinal);
    }

    private static string[] RequiredFiles() =>
    [
        "Install-RemoteController.ps1",
        "Update-RemoteController.ps1",
        "Invoke-RemoteControllerDetachedUpdate.ps1",
        "Rc.Agent.exe",
        "Rc.PrivilegedBroker.exe",
        "Rc.TaskHost.exe",
        "Rc.UiAgent.exe",
        "Rc.UiTestApp.exe",
        "Rc.InteractiveTestApp.exe",
        "Rc.Cli.exe",
    ];

    private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }

    private sealed class RecordingApplier(bool usesDetachedCompletion = false) : IAgentUpdateApplier
    {
        public string? PackagePath { get; private set; }

        public bool UsesDetachedCompletion { get; } = usesDetachedCompletion;

        public Task<string> ApplyAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            PackagePath = packagePath;
            return Task.FromResult("job-update");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtc = utcNow;

        public override DateTimeOffset GetUtcNow() => currentUtc;

        public void Advance(TimeSpan duration) => currentUtc = currentUtc.Add(duration);
    }

    private sealed class InterruptingReadStream(byte[] data, int interruptAfterBytes) : MemoryStream(data, writable: false)
    {
        private bool interrupted;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (interrupted) throw new IOException("Injected transport interruption.");
            var remainingBeforeInterrupt = interruptAfterBytes - checked((int)Position);
            if (remainingBeforeInterrupt <= 0)
            {
                interrupted = true;
                throw new IOException("Injected transport interruption.");
            }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, remainingBeforeInterrupt)], cancellationToken);
        }
    }
}
