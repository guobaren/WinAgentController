using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Rc.Agent.Configuration;
using Rc.Agent.Control;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Cli.Commands;
using Rc.Cli.Security;
using Rc.Cli.Targets;
using Rc.Contracts;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Cli;

[CollectionDefinition("CLI live commands", DisableParallelization = true)]
public sealed class CliLiveCommandTestCollectionDefinition : ICollectionFixture<object>
{
}

[Collection("CLI live commands")]
public sealed class CliCommandLiveTests : IAsyncLifetime, IDisposable
{
    private readonly TemporaryDirectory agentDirectory = new();
    private readonly TemporaryDirectory controllerDirectory = new();
    private AgentStateStore agentStore = null!;
    private AgentTlsIdentity agentIdentity = null!;
    private PairingCoordinator pairingCoordinator = null!;
    private ControllerIdentity controllerIdentity = null!;
    private TlsControlListener listener = null!;
    private CancellationTokenSource cancellation = null!;
    private Task listenerTask = null!;
    private string? previousControllerRoot;
    private string fileRoot = null!;

    private string Endpoint { get; set; } = string.Empty;
    private string Fingerprint => agentIdentity.CertificateSha256Fingerprint;

    public async Task InitializeAsync()
    {
        previousControllerRoot = Environment.GetEnvironmentVariable("RC_CONTROLLER_DATA_ROOT");
        Environment.SetEnvironmentVariable("RC_CONTROLLER_DATA_ROOT", controllerDirectory.Path);

        agentStore = new AgentStateStore(agentDirectory.Path);
        await agentStore.InitializeAsync();
        var certificateManager = new AgentCertificateManager(agentStore);
        agentIdentity = await certificateManager.GetOrCreateAsync();
        pairingCoordinator = new PairingCoordinator(agentStore, certificateManager);

        controllerIdentity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName);
        await agentStore.SavePairedControllerAsync(new PairedController(
            controllerIdentity.ControllerId,
            controllerIdentity.Certificate,
            DateTimeOffset.UtcNow));

        fileRoot = Path.Combine(agentDirectory.Path, "files");
        Directory.CreateDirectory(fileRoot);
        await File.WriteAllTextAsync(Path.Combine(fileRoot, "fixture.txt"), "cli fixture");

        var port = ReservePort();
        Endpoint = $"127.0.0.1:{port}";
        listener = new TlsControlListener(
            agentIdentity,
            agentStore,
            pairingCoordinator,
            port,
            new AgentOptions { FileRoot = fileRoot });
        await listener.InitializeAsync();
        listener.Start();
        cancellation = new CancellationTokenSource();
        listenerTask = listener.RunAsync(cancellation.Token);
    }

    public async Task DisposeAsync()
    {
        cancellation.Cancel();
        await listenerTask;
        await listener.DisposeAsync();
        controllerIdentity.Dispose();
        pairingCoordinator.Dispose();
        agentIdentity.Dispose();
        await agentStore.DisposeAsync();
        cancellation.Dispose();
        Environment.SetEnvironmentVariable("RC_CONTROLLER_DATA_ROOT", previousControllerRoot);
        controllerDirectory.Dispose();
        agentDirectory.Dispose();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task LiveCliProbeTargetAndListCommandsReturnExpectedResults()
    {
        var probe = await InvokeAsync((output, error) => ProbeCommand.RunAsync(
            [Endpoint, "--fingerprint", Fingerprint], output, error));
        Assert.Equal(0, probe.ExitCode);
        var hello = Deserialize<ControlHelloResponse>(probe.Output);
        Assert.Equal(agentIdentity.DeviceId, hello.DeviceId);
        Assert.Equal(Fingerprint, hello.CertificateSha256Fingerprint);
        Assert.True(hello.HasPairedController);

        var automaticallyRemembered = await new ControllerTargetStore(controllerDirectory.Path).GetSnapshotAsync();
        var automaticTarget = Assert.Single(automaticallyRemembered.Targets);
        Assert.Equal(agentIdentity.DeviceId, automaticTarget.DeviceId);
        Assert.Equal(Endpoint, automaticTarget.Endpoint);
        Assert.Equal(Fingerprint, automaticTarget.CertificateSha256Fingerprint);

        var resolvedJobList = await new TargetArgumentResolver(
            new ControllerTargetStore(controllerDirectory.Path)).ResolveAsync(["job", "list"]);
        Assert.True(resolvedJobList.Success, resolvedJobList.Error);
        var automaticJobs = await InvokeAsync((output, error) => JobCommand.RunAsync(
            resolvedJobList.Arguments[1..], output, error));
        Assert.Equal(0, automaticJobs.ExitCode);
        Assert.Empty(Deserialize<ControlJobListResponse>(automaticJobs.Output).Jobs);

        var add = await InvokeAsync((output, error) => TargetCommand.RunAsync(
            ["fixture", "add", Endpoint, "--fingerprint", Fingerprint], output, error));
        Assert.Equal(2, add.ExitCode);

        var targetAdd = await InvokeAsync((output, error) => TargetCommand.RunAsync(
            ["add", "fixture", Endpoint, "--fingerprint", Fingerprint], output, error));
        Assert.Equal(0, targetAdd.ExitCode);
        Assert.Contains("fixture", targetAdd.Output, StringComparison.Ordinal);

        var targetList = await InvokeAsync((output, error) => TargetCommand.RunAsync(
            ["list"], output, error));
        Assert.Equal(0, targetList.ExitCode);
        Assert.Contains("fixture", targetList.Output, StringComparison.Ordinal);

        var targetUse = await InvokeAsync((output, error) => TargetCommand.RunAsync(
            ["use", "fixture"], output, error));
        Assert.Equal(0, targetUse.ExitCode);
        Assert.Contains("fixture", targetUse.Output, StringComparison.Ordinal);

        var jobs = await InvokeAsync((output, error) => JobCommand.RunAsync(
            ["list", Endpoint, "--fingerprint", Fingerprint], output, error));
        Assert.Equal(0, jobs.ExitCode);
        var jobList = Deserialize<ControlJobListResponse>(jobs.Output);
        Assert.Empty(jobList.Jobs);

        var rememberedAfterAuthenticatedConnection = await new ControllerTargetStore(controllerDirectory.Path).GetSnapshotAsync();
        Assert.Equal(2, rememberedAfterAuthenticatedConnection.Targets.Count);
        Assert.Contains(rememberedAfterAuthenticatedConnection.Targets, target =>
            target.DeviceId == agentIdentity.DeviceId && target.Endpoint == Endpoint && target.CertificateSha256Fingerprint == Fingerprint);

        var files = await InvokeAsync((output, error) => FileCommand.RunFsAsync(
            ["list", Endpoint, ".", "--fingerprint", Fingerprint], output, error));
        Assert.Equal(0, files.ExitCode);
        var fileList = Deserialize<FileListResponse>(files.Output);
        Assert.Contains(fileList.Entries, entry => entry.Path.EndsWith("fixture.txt", StringComparison.OrdinalIgnoreCase));

        var uploadPath = Path.Combine(agentDirectory.Path, "upload.txt");
        await File.WriteAllTextAsync(uploadPath, "copy fixture");
        var copy = await InvokeAsync((output, error) => FileCommand.RunCopyAsync(
            ["upload", Endpoint, uploadPath, "--fingerprint", Fingerprint, "--to", "uploaded.txt", "--chunk-size", "4"], output, error));
        Assert.Equal(0, copy.ExitCode);
        var completedCopy = Deserialize<TransferCompleteResponse>(copy.Output);
        Assert.Equal(TransferSessionState.Completed, completedCopy.Session.State);
        Assert.Contains("bytes=12", copy.Error, StringComparison.Ordinal);
        Assert.Contains("MiB/s=", copy.Error, StringComparison.Ordinal);
        Assert.Contains("currentMiB/s=", copy.Error, StringComparison.Ordinal);
        Assert.Contains("minMiB/s=", copy.Error, StringComparison.Ordinal);
        Assert.Contains("maxMiB/s=", copy.Error, StringComparison.Ordinal);
        Assert.Contains("avgMiB/s=", copy.Error, StringComparison.Ordinal);
        var uploadAudit = await agentStore.ListAuditEventsAsync();
        Assert.DoesNotContain(uploadAudit, item => item.EventType == "file.transfer_write_binary" && item.Succeeded);
        Assert.Contains(uploadAudit, item => item.EventType == "file.transfer_complete" && item.Succeeded);

        var downloadPath = Path.Combine(agentDirectory.Path, "downloaded.txt");
        var download = await InvokeAsync((output, error) => FileCommand.RunCopyAsync(
            ["download", Endpoint, "uploaded.txt", "--fingerprint", Fingerprint, "--to", downloadPath, "--chunk-size", "4"], output, error));
        Assert.Equal(0, download.ExitCode);
        Assert.Equal("copy fixture", await File.ReadAllTextAsync(downloadPath));
        Assert.Contains("currentMiB/s=", download.Error, StringComparison.Ordinal);
        Assert.Contains("minMiB/s=", download.Error, StringComparison.Ordinal);
        Assert.Contains("maxMiB/s=", download.Error, StringComparison.Ordinal);
        Assert.Contains("avgMiB/s=", download.Error, StringComparison.Ordinal);
        var downloadAudit = await agentStore.ListAuditEventsAsync();
        Assert.DoesNotContain(downloadAudit, item => item.EventType == "file.transfer_read_binary" && item.Succeeded);
        Assert.Contains(downloadAudit, item => item.EventType == "file.transfer_complete" && item.Succeeded);

        var exec = await InvokeAsync((output, error) => ExecCommand.RunAsync(
            [Endpoint, "--fingerprint", Fingerprint, "--shell", "powershell", "--command", "Write-Output cli-ok", "--text"], output, error));
        Assert.Equal(0, exec.ExitCode);
        Assert.Contains("cli-ok", exec.Output, StringComparison.Ordinal);

        var ui = await InvokeAsync((output, error) => UiCommand.RunAsync(
            ["status", Endpoint, "--fingerprint", Fingerprint, "--text"], output, error));
        Assert.Equal(1, ui.ExitCode);
        Assert.Contains("UI command failed", ui.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairCommandCompletesWithAOneTimeCodeArmedThroughTheLocalAgentInterface()
    {
        await agentStore.RemovePairedControllerAsync();
        LocalAgentIdentityFile.Write(agentStore.DataRoot, agentIdentity.DeviceId, Fingerprint);

        using var armOutput = new StringWriter(new StringBuilder());
        using var armError = new StringWriter(new StringBuilder());
        var armExitCode = await LocalAdminCommand.TryRunAsync(
            ["arm-pairing"], agentStore.DataRoot, armOutput, armError);
        Assert.Equal(0, armExitCode);
        using var armJson = JsonDocument.Parse(armOutput.ToString());
        var code = armJson.RootElement.GetProperty("result").GetProperty("oneTimeCode").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));

        var pair = await InvokeAsync((output, error) => PairCommand.RunAsync(
            [Endpoint, "--fingerprint", Fingerprint, "--code", code!],
            new StringReader(string.Empty), output, error));

        Assert.Equal(0, pair.ExitCode);
        Assert.Contains("pairedAtUtc", pair.Output, StringComparison.Ordinal);
        Assert.Empty(pair.Error);

        var remembered = await new ControllerTargetStore(controllerDirectory.Path).GetSnapshotAsync();
        var target = Assert.Single(remembered.Targets);
        Assert.Equal(agentIdentity.DeviceId, target.DeviceId);
        Assert.Equal(Endpoint, target.Endpoint);
        Assert.Equal(Fingerprint, target.CertificateSha256Fingerprint);
    }

    [Fact]
    public async Task BinaryCopyUploadReconnectsAfterControlledMidChunkDisconnect()
    {
        var data = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 17);
        var manifest = new FileManifest("local", [
            new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, null, FileEntryKind.File),
        ]);
        await using var connection = await AuthenticatedControlConnection.ConnectAsync(IPEndPoint.Parse(Endpoint), Fingerprint);
        var session = (await connection.SendAsync<TransferStartResponse>(new ControlTransferStartRequest(
            1,
            connection.ControllerId,
            new TransferStartRequest(TransferDirection.Upload, "local", "disconnect-copy.bin", manifest, data.Length, StreamingIntegrity: true)))).Session;
        var wireBytes = 0L;

        var written = await connection.SendBinaryUploadAsync(
            new TransferBinaryWriteRequest(session.SessionId, string.Empty, 0, data.Length, null),
            new InterruptOnceReadStream(data, interruptAfterBytes: 512 * 1024),
            count => { wireBytes += count; return ValueTask.CompletedTask; });
        var completed = await connection.SendAsync<TransferCompleteResponse>(
            new ControlTransferCompleteRequest(1, connection.ControllerId, new TransferCompleteRequest(session.SessionId)));

        Assert.Equal(TransferSessionState.Completed, completed.Session.State);
        Assert.Equal(data.Length + (512 * 1024), wireBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), written.Receipt.Sha256);
        var destination = Path.Combine(fileRoot, "disconnect-copy.bin");
        Assert.Equal(data, await File.ReadAllBytesAsync(destination));
        await using var destinationStream = File.OpenRead(destination);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Convert.ToHexString(await SHA256.HashDataAsync(destinationStream)));
    }

    [Fact]
    public async Task BinaryUpdateUploadReconnectsAfterControlledMidChunkDisconnect()
    {
        var data = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 17);
        var emptyHash = Convert.ToHexString(SHA256.HashData([]));
        var required = new[]
        {
            "Install-RemoteController.ps1", "Update-RemoteController.ps1", "Invoke-RemoteControllerDetachedUpdate.ps1",
            "Rc.Agent.exe", "Rc.PrivilegedBroker.exe", "Rc.TaskHost.exe", "Rc.UiAgent.exe", "Rc.UiTestApp.exe",
            "Rc.InteractiveTestApp.exe", "Rc.Cli.exe",
        };
        var files = required.Select(path => new UpdatePackageFile(
            path,
            path == "Rc.Agent.exe" ? data.Length : 0,
            path == "Rc.Agent.exe" ? Convert.ToHexString(SHA256.HashData(data)) : emptyHash)).ToArray();
        var update = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await using var connection = await AuthenticatedControlConnection.ConnectAsync(IPEndPoint.Parse(Endpoint), Fingerprint);
        using var privateKey = controllerIdentity.GetPrivateKey();
        var startSignature = ControlRequestAuthentication.SignUpdateStart(connection.AgentDeviceId, connection.ControllerId, update, privateKey);
        await connection.SendAsync<UpdateStatusResponse>(new ControlUpdateStartRequest(
            1, connection.ControllerId, update, startSignature));
        var request = new UpdateBinaryWriteRequest(update.UpdateId, "Rc.Agent.exe", 0, data.Length, Convert.ToHexString(SHA256.HashData(data)));
        var chunkSignature = ControlRequestAuthentication.SignUpdateWriteBinary(connection.AgentDeviceId, connection.ControllerId, request, privateKey);
        var wireBytes = 0L;

        var written = await connection.SendBinaryUpdateUploadAsync(
            request,
            chunkSignature,
            new InterruptOnceReadStream(data, interruptAfterBytes: 512 * 1024),
            count => { wireBytes += count; return ValueTask.CompletedTask; });

        Assert.Equal(data.Length, written.Status.ReceivedBytes);
        Assert.Equal(data.Length + (512 * 1024), wireBytes);
        var staged = Path.Combine(agentStore.DataRoot, "updates", update.UpdateId.ToString("N"), "payload", "Rc.Agent.exe");
        Assert.Equal(data, await File.ReadAllBytesAsync(staged));
        await using var stagedStream = File.OpenRead(staged);
        Assert.Equal(request.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stagedStream)));
    }

    [Fact]
    public async Task BinaryCopyDownloadReconnectsAfterControlledMidChunkDisconnect()
    {
        var data = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 17);
        await File.WriteAllBytesAsync(Path.Combine(fileRoot, "disconnect-download.bin"), data);
        await using var connection = await AuthenticatedControlConnection.ConnectAsync(IPEndPoint.Parse(Endpoint), Fingerprint);
        var session = (await connection.SendAsync<TransferStartResponse>(new ControlTransferStartRequest(
            1,
            connection.ControllerId,
            new TransferStartRequest(
                TransferDirection.Download,
                "disconnect-download.bin",
                "local",
                new FileManifest("unused", []),
                data.Length,
                StreamingIntegrity: true)))).Session;
        var wireBytes = 0L;
        await using var destination = new InterruptOnceWriteStream(interruptAfterBytes: 512 * 1024);

        var read = await connection.SendBinaryDownloadAsync(
            new TransferBinaryReadRequest(session.SessionId, string.Empty, 0, data.Length),
            destination,
            count => { wireBytes += count; return ValueTask.CompletedTask; });

        Assert.InRange(destination.InterruptedAtBytes, 512 * 1024, (512 * 1024) + (1024 * 1024));
        Assert.Equal(data.Length + destination.InterruptedAtBytes, wireBytes);
        Assert.Equal(data.Length, destination.Length);
        Assert.Equal(data, destination.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), read.ChunkSha256);
        Assert.Equal(read.ChunkSha256, Convert.ToHexString(SHA256.HashData(destination.ToArray())));
    }

    private static T Deserialize<T>(string json)
    {
        var envelope = JsonSerializer.Deserialize<ResultEnvelope<T>>(json, ContractJson.Options);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Ok, envelope.Error?.Message);
        return Assert.IsType<T>(envelope.Result);
    }

    private static async Task<CommandResult> InvokeAsync(Func<TextWriter, TextWriter, Task<int>> command)
    {
        using var output = new StringWriter(new StringBuilder());
        using var error = new StringWriter(new StringBuilder());
        var exitCode = await command(output, error);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private static int ReservePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class InterruptOnceReadStream(byte[] data, int interruptAfterBytes) : MemoryStream(data, writable: false)
    {
        private bool interrupted;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!interrupted && Position >= interruptAfterBytes)
            {
                interrupted = true;
                throw new IOException("Injected transport interruption.");
            }
            var maximum = !interrupted
                ? Math.Min(buffer.Length, interruptAfterBytes - checked((int)Position))
                : buffer.Length;
            return base.ReadAsync(buffer[..maximum], cancellationToken);
        }
    }

    private sealed class InterruptOnceWriteStream(int interruptAfterBytes) : MemoryStream
    {
        private bool interrupted;

        public long InterruptedAtBytes { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (!interrupted && Position >= interruptAfterBytes)
            {
                interrupted = true;
                InterruptedAtBytes = Position;
                throw new IOException("Injected destination interruption.");
            }
        }
    }
}
