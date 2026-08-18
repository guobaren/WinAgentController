using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Rc.Agent.Jobs;
using Rc.Agent.Files;
using Rc.Agent.Configuration;
using Rc.Agent.Elevation;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Agent.Ui;
using Rc.Agent.Updates;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.Agent.Control;

/// <summary>
/// TLS endpoint for public identity probing and the pre-mTLS pairing exchange.
/// Each connection accepts exactly one bounded JSON request and returns one result.
/// </summary>
public sealed class TlsControlListener : IAsyncDisposable
{
    private const int MaximumRequestCharacters = 8 * 1024 * 1024;
    private const int ControlIoBufferSize = 64 * 1024;
    private const int MaximumReturnedOutputBytesPerStream = 256 * 1024;
    private static readonly TimeSpan ControlSessionLifetime = TimeSpan.FromMinutes(10);
    private readonly TcpListener listener;
    private readonly AgentTlsIdentity identity;
    private readonly AgentStateStore stateStore;
    private readonly PairingCoordinator pairingCoordinator;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim executeOnceGate = new(1, 1);
    private volatile ExecOnceOccupant? execOnceOccupant;
    private readonly ManagedTaskRegistry taskRegistry;
    private readonly FileTransferService fileService;
    private readonly AgentOptions options;
    private readonly UiSessionRegistry? uiSessionRegistry;
    private readonly UpdateService updateService;
    private readonly string dataRoot;
    private readonly int tcpPort;
    private bool started;

    public TlsControlListener(
        AgentTlsIdentity identity,
        AgentStateStore stateStore,
        PairingCoordinator pairingCoordinator,
        int port,
        AgentOptions? options = null,
        UiSessionRegistry? uiSessionRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(pairingCoordinator);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        this.identity = identity;
        this.stateStore = stateStore;
        dataRoot = stateStore.DataRoot;
        this.pairingCoordinator = pairingCoordinator;
        this.options = options ?? new AgentOptions();
        this.uiSessionRegistry = uiSessionRegistry;
        tcpPort = port;
        var brokerSecretPath = this.options.BrokerSecretPath ?? Path.Combine(stateStore.DataRoot, "broker-auth.key");
        taskRegistry = new ManagedTaskRegistry(
            stateStore,
            this.options.NormalTaskLimit,
            new ExternalTaskHostLauncher(),
            this.options.CancellationGrace,
            this.options.ElevatedTaskLimit,
            new PrivilegedBrokerTaskHostLauncher(this.options.BrokerPipeName, brokerSecretPath),
            this.options.TaskOutputLimitBytes);
        fileService = new FileTransferService(stateStore, this.options);
        updateService = new UpdateService(stateStore, this.options,
            new TaskRegistryUpdateApplier(taskRegistry, stateStore.DataRoot, AppContext.BaseDirectory, tcpPort));
        listener = new TcpListener(IPAddress.Any, port);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => taskRegistry.EnsureRecoveryAsync(cancellationToken);

    public void Start()
    {
        if (started)
        {
            throw new InvalidOperationException("The TLS control listener is already started.");
        }

        listener.Start();
        started = true;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            throw new InvalidOperationException("Start the TLS control listener before serving requests.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(linked.Token);
                // 文件传输会交替发送微小控制/就绪帧和二进制数据；关闭 Nagle，
                // 避免小文件目录中的每个文件都承担一次延迟 ACK 定时等待。
                client.NoDelay = true;
                _ = ServeClientAsync(client, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested)
        {
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var network = client.GetStream())
        await using (var tls = new SslStream(network, leaveInnerStreamOpen: false))
        {
            var stage = "creating the server certificate context";
            try
            {
                // Pass the persisted-key certificate directly to Schannel. On Windows services,
                // wrapping an ECDSA certificate in SslStreamCertificateContext can make the
                // private key unavailable during credential acquisition.
                stage = "authenticating the TLS server";
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity.Certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, cancellationToken);
                LocalTlsHandshakeDiagnosticsFile.Clear(dataRoot);

                stage = "processing a TLS control request";
                using var reader = new StreamReader(tls, new UTF8Encoding(false), false, ControlIoBufferSize, leaveOpen: true);
                await using var writer = new StreamWriter(tls, new UTF8Encoding(false), ControlIoBufferSize, leaveOpen: true) { AutoFlush = true };
                PendingControlSession? pendingSession = null;
                AuthenticatedControlSession? authenticatedSession = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var requestLine = await reader.ReadLineAsync(cancellationToken);
                    if (requestLine is null)
                    {
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > MaximumRequestCharacters)
                    {
                        await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "A non-empty bounded control request is required.");
                        continue;
                    }

                    using var document = JsonDocument.Parse(requestLine);
                    if (!document.RootElement.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                    {
                        await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "A control request kind is required.");
                        continue;
                    }
                    if (authenticatedSession is { } expired && expired.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    {
                        authenticatedSession = null;
                    }

                    switch (kindElement.GetString())
                    {
                        case ControlMessageKinds.Hello:
                            await HandleHelloAsync(document.RootElement, writer, cancellationToken);
                            break;
                        case ControlMessageKinds.SessionStart:
                            pendingSession = await HandleSessionStartAsync(document.RootElement, writer, cancellationToken);
                            authenticatedSession = null;
                            break;
                        case ControlMessageKinds.SessionAuthenticate:
                            authenticatedSession = await HandleSessionAuthenticateAsync(document.RootElement, pendingSession, writer, cancellationToken);
                            pendingSession = null;
                            break;
                        case ControlMessageKinds.PairStart:
                            await HandlePairStartAsync(client, document.RootElement, writer, cancellationToken);
                            break;
                        case ControlMessageKinds.PairRound1:
                            await HandlePairRound1Async(document.RootElement, writer, cancellationToken);
                            break;
                        case ControlMessageKinds.PairRound2:
                            await HandlePairRound2Async(document.RootElement, writer, cancellationToken);
                            break;
                        case ControlMessageKinds.PairComplete:
                            await HandlePairCompleteAsync(document.RootElement, writer, cancellationToken);
                            break;
                        case ControlMessageKinds.ExecOnce:
                            await HandleExecuteOnceAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobStart:
                            await HandleJobStartAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobStatus:
                            await HandleJobStatusAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobList:
                            await HandleJobListAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobLogs:
                            await HandleJobLogsAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobInput:
                            await HandleJobInputAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobCloseInput:
                            await HandleJobCloseInputAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobCancel:
                            await HandleJobCancelAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobWait:
                            await HandleJobWaitAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.JobResize:
                            await HandleJobResizeAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.FileManifest:
                        case ControlMessageKinds.FileList:
                        case ControlMessageKinds.FileStat:
                        case ControlMessageKinds.FileRead:
                        case ControlMessageKinds.FileWrite:
                        case ControlMessageKinds.TransferStart:
                        case ControlMessageKinds.TransferWriteChunk:
                        case ControlMessageKinds.TransferReadChunk:
                        case ControlMessageKinds.TransferComplete:
                        case ControlMessageKinds.TransferStatus:
                            await HandleFileRequestAsync(kindElement.GetString()!, document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.TransferWriteBinary:
                            await HandleBinaryWriteAsync(document.RootElement, tls, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.TransferReadBinary:
                            await HandleBinaryReadAsync(document.RootElement, tls, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UiStatus:
                            await HandleUiStatusAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UiCommand:
                            await HandleUiCommandAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UpdateStart:
                            await HandleUpdateStartAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UpdateWriteChunk:
                            await HandleUpdateWriteChunkAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UpdateWriteBinary:
                            await HandleUpdateWriteBinaryAsync(document.RootElement, tls, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UpdateComplete:
                            await HandleUpdateCompleteAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        case ControlMessageKinds.UpdateStatus:
                            await HandleUpdateStatusAsync(document.RootElement, writer, authenticatedSession, cancellationToken);
                            break;
                        default:
                            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The control request kind is not supported.");
                            break;
                    }
                }
            }
            catch (AuthenticationException exception)
            {
                RecordTlsFailure(stage, exception);
            }
            catch (IOException exception)
            {
                RecordTlsFailure(stage, exception);
            }
            catch (Exception exception)
            {
                RecordTlsFailure(stage, exception);
            }
        }
    }

    private void RecordTlsFailure(string stage, Exception exception)
    {
        try
        {
            LocalTlsHandshakeDiagnosticsFile.Write(dataRoot, stage, exception);
        }
        catch
        {
            // Reporting must not affect the lifetime or security of a connection.
        }

        if (Environment.UserInteractive)
        {
            Console.Error.WriteLine($"TLS control connection failed while {stage}: {exception}");
        }
    }

    private async Task HandleUiStatusAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUiStatusRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || session is null || !string.Equals(request.ControllerId, session.ControllerId, StringComparison.Ordinal))
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthorized, "An authenticated paired controller is required for UI status.");
            return;
        }

        var registration = uiSessionRegistry?.GetActive(TimeSpan.FromSeconds(30));
        if (registration is null)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, "No active UiAgent session is registered.");
            return;
        }

        await ExecuteUiOperationAsync(registration, UiOperationKinds.Snapshot,
            JsonSerializer.SerializeToElement(new UiSnapshotRequest(false), ContractJson.Options), writer, cancellationToken,
            projectSnapshotAsStatus: true).ConfigureAwait(false);
    }

    private async Task HandleUiCommandAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUiCommandRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || session is null ||
            !string.Equals(request.ControllerId, session.ControllerId, StringComparison.Ordinal))
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthorized, "An authenticated paired controller is required for UI control.");
            return;
        }
        if (!UiOperationKinds.IsSupported(request.Operation) || request.Request.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The UI control request is invalid.");
            return;
        }

        var registration = uiSessionRegistry?.GetActive(TimeSpan.FromSeconds(30));
        if (registration is null)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, "No active UiAgent session is registered.");
            return;
        }

        await ExecuteUiOperationAsync(registration, request.Operation, request.Request, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateStartAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUpdateStartRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || request.Request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The update start request is invalid.");
            return;
        }
        await ExecuteUpdateAsync("update.started", request.ControllerId, request.Request.UpdateId, request.Signature,
            key => ControlRequestAuthentication.VerifyUpdateStart(identity.DeviceId, request.ControllerId, request.Request, request.Signature, key),
            () => updateService.StartAsync(request.Request, cancellationToken), session, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateWriteChunkAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUpdateWriteChunkRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || request.Request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The update chunk request is invalid.");
            return;
        }
        await ExecuteUpdateAsync("update.chunk_written", request.ControllerId, request.Request.UpdateId, request.Signature,
            key => ControlRequestAuthentication.VerifyUpdateWriteChunk(identity.DeviceId, request.ControllerId, request.Request, request.Signature, key),
            () => updateService.WriteChunkAsync(request.Request, cancellationToken), session, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateWriteBinaryAsync(JsonElement root, Stream stream, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUpdateWriteBinaryRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || request.Request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The binary update chunk request is invalid.");
            return;
        }
        if (!await VerifyPairedControllerRequestAsync(
                session,
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyUpdateWriteBinary(identity.DeviceId, request.ControllerId, request.Request, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var result = await updateService.WriteBinaryChunkAsync(
                request.Request,
                stream,
                ready => WriteSuccessAsync(writer, ready),
                cancellationToken).ConfigureAwait(false);
            await AuditAsync("update.binary_chunk_written", request.ControllerId, request.Request.UpdateId.ToString("N"), true, null,
                new Dictionary<string, string> { ["length"] = request.Request.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, result).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await AuditAsync("update.binary_chunk_written", request.ControllerId, request.Request.UpdateId.ToString("N"), false, ErrorCode.FailedPrecondition, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        {
            await AuditAsync("update.binary_chunk_written", request.ControllerId, request.Request.UpdateId.ToString("N"), false, ErrorCode.InvalidRequest, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleUpdateCompleteAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUpdateCompleteRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || request.Request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The update completion request is invalid.");
            return;
        }
        await ExecuteUpdateAsync("update.applying", request.ControllerId, request.Request.UpdateId, request.Signature,
            key => ControlRequestAuthentication.VerifyUpdateComplete(identity.DeviceId, request.ControllerId, request.Request, request.Signature, key),
            () => updateService.CompleteAsync(request.Request, cancellationToken), session, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateStatusAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlUpdateStatusRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || request.Request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The update status request is invalid.");
            return;
        }
        await ExecuteUpdateAsync("update.status_read", request.ControllerId, request.Request.UpdateId, request.Signature,
            key => ControlRequestAuthentication.VerifyUpdateStatus(identity.DeviceId, request.ControllerId, request.Request, request.Signature, key),
            () => updateService.GetStatusAsync(request.Request, cancellationToken), session, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteUpdateAsync(
        string eventType,
        string controllerId,
        Guid updateId,
        byte[] signature,
        Func<ECDsa, bool> verifySignature,
        Func<ValueTask<UpdateStatusResponse>> operation,
        AuthenticatedControlSession? session,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        if (!await VerifyPairedControllerRequestAsync(session, controllerId, signature, verifySignature, writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            var result = await operation().ConfigureAwait(false);
            var succeeded = result.State != UpdateState.Failed;
            await AuditAsync(eventType, controllerId, updateId.ToString("N"), succeeded, succeeded ? null : ErrorCode.FailedPrecondition,
                new Dictionary<string, string> { ["state"] = result.State.ToString(), ["version"] = result.Version }, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, result).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            await AuditAsync(eventType, controllerId, updateId.ToString("N"), false, ErrorCode.NotFound, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.NotFound, exception.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await AuditAsync(eventType, controllerId, updateId.ToString("N"), false, ErrorCode.FailedPrecondition, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        {
            await AuditAsync(eventType, controllerId, updateId.ToString("N"), false, ErrorCode.InvalidRequest, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, exception.Message).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteUiOperationAsync(UiAgentRegistration registration, string operation, JsonElement request, StreamWriter writer, CancellationToken cancellationToken, bool projectSnapshotAsStatus = false)
    {
        try
        {
            var result = await UiAgentControlClient.SendAsync(registration, operation, request, cancellationToken).ConfigureAwait(false);
            if (projectSnapshotAsStatus)
            {
                var snapshot = result.Deserialize<UiSnapshotResponse>(ContractJson.Options)
                    ?? throw new InvalidDataException("The UiAgent returned an invalid snapshot.");
                await WriteSuccessAsync(writer, new UiStatusResponse(snapshot.Session)).ConfigureAwait(false);
                return;
            }

            await WriteSuccessAsync(writer, new UiAgentCommandResponse(result)).ConfigureAwait(false);
        }
        catch (UiAgentUnavailableException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, exception.Message, retryable: true).ConfigureAwait(false);
        }
        catch (UiAgentRequestException exception)
        {
            await WriteFailureAsync(writer, exception.Error.Code, exception.Error.Message, exception.Error.Retryable).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleHelloAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlHelloRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The hello request protocol version is unsupported.");
            return;
        }

        var pairedController = await stateStore.GetPairedControllerAsync(cancellationToken);
        await WriteSuccessAsync(writer, new ControlHelloResponse(
            ProtocolVersion: 1,
            identity.DeviceId,
            identity.CertificateSha256Fingerprint,
            pairedController is not null)
        {
            Capabilities = [ControlCapabilities.BinaryTransferV1, ControlCapabilities.StreamingIntegrityV2, ControlCapabilities.BinaryUpdateV1],
            MaximumBinaryTransferChunkBytes = options.MaximumTransferChunkBytes,
            MaximumBinaryUpdateChunkBytes = options.MaximumBinaryUpdateChunkBytes,
        });
    }

    private async Task<PendingControlSession?> HandleSessionStartAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlSessionStartRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.ControllerId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The control session request is invalid.");
            return null;
        }

        var paired = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
        if (paired is null)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "Pair a controller before opening a control session.");
            return null;
        }
        if (!string.Equals(paired.ControllerId, request.ControllerId, StringComparison.Ordinal))
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The session controller identity does not match the paired controller.");
            return null;
        }

        var session = new PendingControlSession(
            Guid.NewGuid(),
            request.ControllerId,
            RandomNumberGenerator.GetBytes(32),
            DateTimeOffset.UtcNow.Add(ControlSessionLifetime));
        await WriteSuccessAsync(writer, new ControlSessionStartResponse(session.SessionId, identity.DeviceId, session.ControllerId, session.Challenge, session.ExpiresAtUtc));
        return session;
    }

    private async Task<AuthenticatedControlSession?> HandleSessionAuthenticateAsync(
        JsonElement root,
        PendingControlSession? pending,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlSessionAuthenticateRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || pending is null || request.SessionId != pending.SessionId ||
            !string.Equals(request.ControllerId, pending.ControllerId, StringComparison.Ordinal) || request.Signature is null || request.Signature.Length == 0)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The control session authentication request is invalid or has no matching challenge.");
            return null;
        }
        if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await AuditAsync("session.authentication_failed", pending.ControllerId, pending.SessionId.ToString("N"), false, ErrorCode.Unauthenticated, new Dictionary<string, string> { ["reason"] = "expired" }, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "The control session challenge expired.");
            return null;
        }

        var paired = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
        if (paired is null || !string.Equals(paired.ControllerId, request.ControllerId, StringComparison.Ordinal))
        {
            await AuditAsync("session.authentication_failed", request.ControllerId, request.SessionId.ToString("N"), false, ErrorCode.Unauthenticated, new Dictionary<string, string> { ["reason"] = "pairing_unavailable" }, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "The paired controller is unavailable for session authentication.");
            return null;
        }

        var certificateBytes = paired.Certificate;
        try
        {
            using var certificate = new X509Certificate2(certificateBytes);
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || !ControlRequestAuthentication.VerifySessionAuthentication(
                    identity.DeviceId, request.ControllerId, pending.SessionId, pending.Challenge, pending.ExpiresAtUtc, request.Signature, publicKey))
            {
                await AuditAsync("session.authentication_failed", request.ControllerId, request.SessionId.ToString("N"), false, ErrorCode.Unauthorized, new Dictionary<string, string> { ["reason"] = "signature" }, cancellationToken).ConfigureAwait(false);
                await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The control session signature is invalid.");
                return null;
            }

            var authenticated = new AuthenticatedControlSession(pending.SessionId, pending.ControllerId, pending.ExpiresAtUtc);
            await AuditAsync("session.authenticated", authenticated.ControllerId, authenticated.SessionId.ToString("N"), true, null, null, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlSessionAuthenticateResponse(authenticated.SessionId, authenticated.ControllerId, authenticated.ExpiresAtUtc));
            return authenticated;
        }
        catch (CryptographicException)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, "The paired controller certificate cannot validate session authentication.");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateBytes);
            CryptographicOperations.ZeroMemory(pending.Challenge);
        }
    }
    private async Task HandlePairStartAsync(
        TcpClient client, JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairStartRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing request protocol version is unsupported.");
            return;
        }

        var pairingSecurity = await stateStore.GetPairingSecurityStateAsync(cancellationToken).ConfigureAwait(false);
        if (pairingSecurity.BlockedUntilUtc is { } blockedUntil && blockedUntil > DateTimeOffset.UtcNow)
        {
            await AuditAsync("pairing.blocked", request.ControllerId, null, false, ErrorCode.ResourceExhausted,
                new Dictionary<string, string> { ["blockedUntilUtc"] = blockedUntil.ToString("O") }, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.ResourceExhausted, "Pairing is temporarily unavailable after repeated failed attempts.");
            return;
        }

        if (client.Client.LocalEndPoint is not IPEndPoint localEndpoint)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, "The agent could not determine its local pairing endpoint.");
            return;
        }

        var pairingEndpoint = new PairingEndpoint(localEndpoint.Address, localEndpoint.Port);
        var hasArmedPairingCode = LocalPairingCodeFile.TryReadCurrent(
            stateStore.DataRoot,
            DateTimeOffset.UtcNow,
            out var armedPairingCode) && armedPairingCode!.IsArmed;
        var invitation = hasArmedPairingCode
            ? await pairingCoordinator.CreateInvitationAsync(
                pairingEndpoint, armedPairingCode!.OneTimeCode, cancellationToken)
            : await pairingCoordinator.CreateInvitationAsync(pairingEndpoint, cancellationToken);
        var binding = await pairingCoordinator.BindControllerAsync(
            invitation.PairingId, request.ControllerId, request.ControllerCertificate, cancellationToken);
        LocalPairingCodeFile.Write(stateStore.DataRoot, invitation);
        if (Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine("Pairing request received. Enter this one-time code on the controller:");
            Console.WriteLine($"  {invitation.OneTimeCode}  (expires {invitation.ExpiresAtUtc.LocalDateTime:G})");
        }

        var agentRound1 = pairingCoordinator.CreateAgentRound1(invitation.PairingId);
        await AuditAsync("pairing.started", request.ControllerId, invitation.PairingId.ToString("N"), true, null, null, cancellationToken).ConfigureAwait(false);
        await WriteSuccessAsync(writer, new ControlPairStartResponse(ToContract(binding), invitation.ExpiresAtUtc, agentRound1));
    }

    private async Task HandlePairRound1Async(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairRound1Request>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing round 1 request is invalid.");
            return;
        }

        try
        {
            pairingCoordinator.ReceiveControllerRound1(request.PairingId, request.ControllerRound1);
            await WriteSuccessAsync(writer, new ControlPairRound2Response(pairingCoordinator.CreateAgentRound2(request.PairingId)));
        }
        catch (CryptographicException exception)
        {
            await RecordPairingFailureAsync("round1", request.PairingId, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, $"Pairing validation failed while receiving controller round 1: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, $"Pairing protocol state was invalid for controller round 1: {exception.Message}");
        }
    }

    private async Task HandlePairRound2Async(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairRound2Request>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing round 2 request is invalid.");
            return;
        }

        try
        {
            pairingCoordinator.ReceiveControllerRound2(request.PairingId, request.ControllerRound2);
            await WriteSuccessAsync(writer, new ControlPairRound3Response(pairingCoordinator.CreateAgentRound3(request.PairingId)));
        }
        catch (CryptographicException exception)
        {
            await RecordPairingFailureAsync("round2", request.PairingId, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, $"Pairing validation failed while receiving controller round 2: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, $"Pairing protocol state was invalid for controller round 2: {exception.Message}");
        }
    }

    private async Task HandlePairCompleteAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairCompleteRequest>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing completion request is invalid.");
            return;
        }

        try
        {
            pairingCoordinator.ReceiveControllerRound3(request.PairingId, request.ControllerRound3);
            var paired = await pairingCoordinator.CompleteAsync(
                request.PairingId,
                new PairingCompletionProof(request.ConfirmationMac, request.CertificateSignature),
                cancellationToken);
            await stateStore.ResetPairingFailuresAsync(cancellationToken).ConfigureAwait(false);
            LocalPairingCodeFile.Delete(stateStore.DataRoot);
            await AuditAsync("pairing.completed", paired.ControllerId, request.PairingId.ToString("N"), true, null, null, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlPairCompleteResponse(paired.ControllerId, paired.PairedAtUtc));
        }
        catch (CryptographicException exception)
        {
            await RecordPairingFailureAsync("round3", request.PairingId, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, $"Pairing validation failed while receiving controller round 3: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, $"Pairing protocol state was invalid for controller round 3: {exception.Message}");
        }
    }

    private async Task HandleExecuteOnceAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlExecuteOnceRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The exec request protocol version is unsupported.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(
                session,
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyExecuteOnce(identity.DeviceId, request.ControllerId, request.Execution, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await executeOnceGate.WaitAsync(0, cancellationToken))
        {
            var occupant = execOnceOccupant;
            var detail = occupant is null
                ? "Another exec_once command is still running."
                : $"Another exec_once command is still running (job={occupant.JobId ?? "starting"} command='{occupant.Command}' startedAt={occupant.StartedAtUtc:O}); cancel it with 'rcctl job cancel <jobId>'.";
            await WriteFailureAsync(writer, ErrorCode.ResourceExhausted, detail);
            return;
        }

        try
        {
            execOnceOccupant = new ExecOnceOccupant(null, DescribeExec(request.Execution), DateTimeOffset.UtcNow);
            var started = await taskRegistry.StartAsync(request.Execution, cancellationToken).ConfigureAwait(false);
            var jobId = started.Job.JobId;
            execOnceOccupant = execOnceOccupant with { JobId = jobId };
            var timeout = options.ExecTimeout > TimeSpan.Zero ? options.ExecTimeout : (TimeSpan?)null;
            var waited = timeout is null
                ? await taskRegistry.WaitAsync(jobId, timeout: null, cancellationToken).ConfigureAwait(false)
                : await taskRegistry.WaitAsync(jobId, timeout, cancellationToken).ConfigureAwait(false);
            if (!waited.Completed)
            {
                // 对端侧执行超时：取消任务并回写错误，避免通道被永久占用；
                // 任务日志仍可经 job logs 查询。
                await taskRegistry.CancelAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                var cancelled = await taskRegistry.WaitAsync(jobId, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                await WriteFailureAsync(writer, ErrorCode.ResourceExhausted,
                    $"The exec command timed out after {timeout!.Value.TotalSeconds:F0} seconds and was cancelled (job {jobId}); logs remain readable via 'rcctl job logs'.");
                return;
            }
            var status = waited.Status;
            var standardOutput = await ReadOutputAsync(jobId, JobOutputKind.Stdout, cancellationToken);
            var standardError = await ReadOutputAsync(jobId, JobOutputKind.Stderr, cancellationToken);
            await AuditAsync("job.exec_once", request.ControllerId, jobId, true, null, new Dictionary<string, string> { ["executionIdentity"] = status.Job.ExecutionIdentity.ToString() }, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlExecuteOnceResponse(
                status.Job,
                standardOutput,
                status.OutputTruncated || status.StdoutLength > standardOutput.Length,
                standardError,
                status.OutputTruncated || status.StderrLength > standardError.Length));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The command could not be executed: {exception.Message}");
        }
        finally
        {
            execOnceOccupant = null;
            executeOnceGate.Release();
        }
    }

    private static string DescribeExec(ExecRequest execution) =>
        execution.Shell is { } shell ? $"{shell.Kind}: {shell.Command}"
            : string.Join(' ', execution.DirectArgv ?? []);

    private sealed record ExecOnceOccupant(string? JobId, string Command, DateTimeOffset StartedAtUtc);

    private async Task HandleJobStartAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobStartRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job start request protocol version is unsupported.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session,
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyJobStart(identity.DeviceId, request.ControllerId, request.Execution, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var status = await taskRegistry.StartAsync(request.Execution, cancellationToken).ConfigureAwait(false);
            await AuditAsync("job.started", request.ControllerId, status.Job.JobId, true, null, new Dictionary<string, string> { ["executionIdentity"] = status.Job.ExecutionIdentity.ToString() }, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobStartResponse(status));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job could not be started: {exception.Message}");
        }
    }

    private async Task HandleJobStatusAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobStatusRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job status request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session,
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyJobStatus(identity.DeviceId, request.ControllerId, request.JobId, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var result = await taskRegistry.GetStatusAsync(request.JobId, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobStatusResponse(result.Status, result.IsActive));
        }
        catch (KeyNotFoundException)
        {
            await WriteFailureAsync(writer, ErrorCode.NotFound, $"No job exists with ID '{request.JobId}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job status could not be read: {exception.Message}");
        }
    }

    private async Task HandleJobListAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobListRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || (request.State is { } state && !Enum.IsDefined(state)))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job list request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session,
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyJobList(identity.DeviceId, request.ControllerId, request.State, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var jobs = await taskRegistry.ListAsync(request.State, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobListResponse(jobs));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job list could not be read: {exception.Message}");
        }
    }

    private async Task HandleJobLogsAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobLogsRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId) || !Enum.IsDefined(request.Stream) || request.AfterOffset < 0 || request.MaximumBytes is < 1 or > MaximumReturnedOutputBytesPerStream)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job logs request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session, request.ControllerId, request.Signature,
                key => ControlRequestAuthentication.VerifyJobLogs(identity.DeviceId, request.ControllerId, request.JobId, request.Stream, request.AfterOffset, request.MaximumBytes, request.Signature, key),
                writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var log = await taskRegistry.ReadLogsAsync(request.JobId, request.Stream, request.AfterOffset, request.MaximumBytes, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobLogsResponse(log));
        }
        catch (KeyNotFoundException)
        {
            await WriteFailureAsync(writer, ErrorCode.NotFound, $"No job exists with ID '{request.JobId}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job logs could not be read: {exception.Message}");
        }
    }

    private async Task HandleJobInputAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobInputRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId) || request.Data is null || request.Data.Length > MaximumReturnedOutputBytesPerStream)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job input request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session, request.ControllerId, request.Signature,
                key => ControlRequestAuthentication.VerifyJobInput(identity.DeviceId, request.ControllerId, request.JobId, request.Data, request.Signature, key),
                writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await HandleActiveJobOperationAsync("job.input", request.ControllerId, request.JobId, writer,
            () => taskRegistry.WriteStandardInputAsync(request.JobId, request.Data, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleJobCloseInputAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobCloseInputRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job close-input request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session, request.ControllerId, request.Signature,
                key => ControlRequestAuthentication.VerifyJobCloseInput(identity.DeviceId, request.ControllerId, request.JobId, request.Signature, key),
                writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await HandleActiveJobOperationAsync("job.input_closed", request.ControllerId, request.JobId, writer,
            () => taskRegistry.CloseStandardInputAsync(request.JobId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleJobCancelAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobCancelRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job cancel request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session, request.ControllerId, request.Signature,
                key => ControlRequestAuthentication.VerifyJobCancel(identity.DeviceId, request.ControllerId, request.JobId, request.Signature, key),
                writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await HandleActiveJobOperationAsync("job.cancelled", request.ControllerId, request.JobId, writer,
            () => taskRegistry.CancelAsync(request.JobId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleJobWaitAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobWaitRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId) || request.Timeout is { } timeout && (timeout < TimeSpan.Zero || timeout > TimeSpan.FromDays(1)))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job wait request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session, request.ControllerId, request.Signature,
                key => ControlRequestAuthentication.VerifyJobWait(identity.DeviceId, request.ControllerId, request.JobId, request.Timeout, request.Signature, key),
                writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var result = await taskRegistry.WaitAsync(request.JobId, request.Timeout, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobOperationResponse(result.Status, result.Completed));
        }
        catch (KeyNotFoundException)
        {
            await WriteFailureAsync(writer, ErrorCode.NotFound, $"No job exists with ID '{request.JobId}'.");
        }
    }

    private async Task HandleJobResizeAsync(JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobResizeRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId) || request.Columns is < 1 or > 1000 || request.Rows is < 1 or > 1000)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job resize request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(session, request.ControllerId, request.Signature,
                key => ControlRequestAuthentication.VerifyJobResize(identity.DeviceId, request.ControllerId, request.JobId, request.Columns, request.Rows, request.Signature, key),
                writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await HandleActiveJobOperationAsync("job.resized", request.ControllerId, request.JobId, writer,
            () => taskRegistry.ResizeTerminalAsync(request.JobId, request.Columns, request.Rows, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleFileRequestAsync(string kind, JsonElement root, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        try
        {
            switch (kind)
            {
                case ControlMessageKinds.FileManifest:
                    { var r = root.Deserialize<ControlFileManifestRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.GetManifestAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.FileList:
                    { var r = root.Deserialize<ControlFileListRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.ListAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.FileStat:
                    { var r = root.Deserialize<ControlFileStatRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.StatAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.FileRead:
                    { var r = root.Deserialize<ControlFileReadRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.ReadAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.FileWrite:
                    { var r = root.Deserialize<ControlFileWriteRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.WriteAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.TransferStart:
                    { var r = root.Deserialize<ControlTransferStartRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.StartTransferAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.TransferWriteChunk:
                    { var r = root.Deserialize<ControlTransferWriteChunkRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.WriteChunkAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.TransferReadChunk:
                    { var r = root.Deserialize<ControlTransferReadChunkRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.ReadChunkAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.TransferComplete:
                    { var r = root.Deserialize<ControlTransferCompleteRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.CompleteAsync(r!.Request, cancellationToken), cancellationToken); break; }
                case ControlMessageKinds.TransferStatus:
                    { var r = root.Deserialize<ControlTransferStatusRequest>(ContractJson.Options); await ExecuteFileAsync(kind, r?.ProtocolVersion, r?.ControllerId, session, writer, () => fileService.StatusAsync(r!.Request, cancellationToken), cancellationToken); break; }
            }
        }
        catch (KeyNotFoundException exception) { await WriteFailureAsync(writer, ErrorCode.NotFound, exception.Message); }
        catch (FileNotFoundException exception) { await WriteFailureAsync(writer, ErrorCode.NotFound, exception.Message); }
        catch (DirectoryNotFoundException exception) { await WriteFailureAsync(writer, ErrorCode.NotFound, exception.Message); }
        catch (UnauthorizedAccessException exception) { await WriteFailureAsync(writer, ErrorCode.Unauthorized, exception.Message); }
        catch (InvalidOperationException exception) { await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        { await WriteFailureAsync(writer, ErrorCode.InvalidRequest, exception.Message); }
    }

    private async Task HandleBinaryWriteAsync(JsonElement root, Stream stream, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlTransferWriteBinaryRequest>(ContractJson.Options);
        if (!await AuthorizeFileRequestAsync(ControlMessageKinds.TransferWriteBinary, request?.ProtocolVersion, request?.ControllerId, session, writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            var result = await fileService.WriteBinaryChunkAsync(
                request!.Request,
                stream,
                ready => WriteSuccessAsync(writer, ready),
                cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, result).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var errorCode = MapFileOperationError(exception);
            await AuditAsync("file." + ControlMessageKinds.TransferWriteBinary, request!.ControllerId, null, false, errorCode, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, errorCode, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleBinaryReadAsync(JsonElement root, Stream stream, StreamWriter writer, AuthenticatedControlSession? session, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlTransferReadBinaryRequest>(ContractJson.Options);
        if (!await AuthorizeFileRequestAsync(ControlMessageKinds.TransferReadBinary, request?.ProtocolVersion, request?.ControllerId, session, writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            var result = await fileService.ReadBinaryChunkAsync(
                request!.Request,
                stream,
                ready => WriteSuccessAsync(writer, ready),
                async () =>
                {
                    var signal = new byte[1];
                    await stream.ReadExactlyAsync(signal, cancellationToken).ConfigureAwait(false);
                    if (signal[0] != 1) throw new InvalidDataException("The binary download readiness signal is invalid.");
                },
                cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, result).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var errorCode = MapFileOperationError(exception);
            await AuditAsync("file." + ControlMessageKinds.TransferReadBinary, request!.ControllerId, null, false, errorCode, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, errorCode, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task<bool> AuthorizeFileRequestAsync(string operationKind, int? protocolVersion, string? controllerId, AuthenticatedControlSession? session, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (protocolVersion != 1 || string.IsNullOrWhiteSpace(controllerId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The file request is invalid.").ConfigureAwait(false);
            return false;
        }
        if (session is null || session.ExpiresAtUtc <= DateTimeOffset.UtcNow || !string.Equals(session.ControllerId, controllerId, StringComparison.Ordinal))
        {
            await AuditAsync("file." + operationKind, controllerId, null, false, ErrorCode.Unauthenticated, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "An authenticated control session is required for file operations.").ConfigureAwait(false);
            return false;
        }
        var paired = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
        if (paired is null || !string.Equals(paired.ControllerId, session.ControllerId, StringComparison.Ordinal))
        {
            await AuditAsync("file." + operationKind, controllerId, null, false, ErrorCode.Unauthenticated, new Dictionary<string, string> { ["reason"] = "unpaired" }, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "The authenticated control session was revoked by a local unpair operation.").ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private async Task ExecuteFileAsync<T>(string operationKind, int? protocolVersion, string? controllerId, AuthenticatedControlSession? session, StreamWriter writer, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (protocolVersion != 1 || string.IsNullOrWhiteSpace(controllerId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The file request is invalid.");
            return;
        }
        if (session is null || session.ExpiresAtUtc <= DateTimeOffset.UtcNow || !string.Equals(session.ControllerId, controllerId, StringComparison.Ordinal))
        {
            await AuditAsync("file." + operationKind, controllerId, null, false, ErrorCode.Unauthenticated, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "An authenticated control session is required for file operations.");
            return;
        }
        var paired = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
        if (paired is null || !string.Equals(paired.ControllerId, session.ControllerId, StringComparison.Ordinal))
        {
            await AuditAsync("file." + operationKind, controllerId, null, false, ErrorCode.Unauthenticated, new Dictionary<string, string> { ["reason"] = "unpaired" }, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "The authenticated control session was revoked by a local unpair operation.");
            return;
        }
        try
        {
            var result = await operation().ConfigureAwait(false);
            await AuditAsync("file." + operationKind, controllerId, null, true, null, null, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, result);
        }
        catch (Exception exception)
        {
            var errorCode = MapFileOperationError(exception);
            await AuditAsync("file." + operationKind, controllerId, null, false, errorCode, null, cancellationToken).ConfigureAwait(false);
            await WriteFailureAsync(writer, errorCode, exception.Message);
        }
    }
    private static ErrorCode MapFileOperationError(Exception exception) => exception switch
    {
        KeyNotFoundException or FileNotFoundException or DirectoryNotFoundException => ErrorCode.NotFound,
        UnauthorizedAccessException => ErrorCode.Unauthorized,
        ResourceExhaustedException => ErrorCode.ResourceExhausted,
        IOException ioException when IsDiskFull(ioException) => ErrorCode.ResourceExhausted,
        InvalidOperationException => ErrorCode.FailedPrecondition,
        ArgumentException or InvalidDataException or IOException => ErrorCode.InvalidRequest,
        _ => ErrorCode.Internal,
    };

    private static bool IsDiskFull(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 0x27 or 0x70;
    }
    private async Task HandleActiveJobOperationAsync(string eventType, string controllerId, string jobId, StreamWriter writer, Func<Task<TaskRuntimeStatus>> operation, CancellationToken cancellationToken)
    {
        try
        {
            var status = await operation().ConfigureAwait(false);
            await AuditAsync(eventType, controllerId, jobId, true, null, null, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobOperationResponse(status, IsTerminal(status.Job.State)));
        }
        catch (InvalidOperationException exception)

        {

            var snapshot = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);

            await WriteFailureAsync(writer, snapshot is null ? ErrorCode.NotFound : ErrorCode.FailedPrecondition, snapshot is null ? $"No job exists with ID '{jobId}'." : exception.Message);

        }
        catch (Exception exception) when (exception is IOException or TimeoutException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, $"The task control channel is unavailable: {exception.Message}");
        }
    }

    private static bool IsTerminal(JobState state) => state is JobState.Exited or JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot or JobState.HostCrashed;
    private async Task<bool> VerifyPairedControllerRequestAsync(
        AuthenticatedControlSession? session,
        string controllerId,
        byte[] signature,
        Func<ECDsa, bool> verifySignature,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "The authenticated control session expired.");
                return false;
            }
            if (!string.Equals(session.ControllerId, controllerId, StringComparison.Ordinal))
            {
                await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The request controller does not match the authenticated session.");
                return false;
            }
            var currentPairing = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
            if (currentPairing is null || !string.Equals(currentPairing.ControllerId, session.ControllerId, StringComparison.Ordinal))
            {
                await AuditAsync("session.revoked", session.ControllerId, session.SessionId.ToString("N"), false, ErrorCode.Unauthenticated, null, cancellationToken).ConfigureAwait(false);
                await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "The authenticated control session was revoked by a local unpair operation.");
                return false;
            }
            return true;
        }

        if (string.IsNullOrWhiteSpace(controllerId) || signature is null || signature.Length == 0)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "A controller identity and signature are required.");
            return false;
        }

        var pairedController = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
        if (pairedController is null)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "Pair a controller before making job requests.");
            return false;
        }

        if (!string.Equals(pairedController.ControllerId, controllerId, StringComparison.Ordinal))
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The request controller identity does not match the paired controller.");
            return false;
        }

        var certificateBytes = pairedController.Certificate;
        try
        {
            using var certificate = new X509Certificate2(certificateBytes);
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || !verifySignature(publicKey))
            {
                await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The paired controller signature is invalid.");
                return false;
            }

            return true;
        }
        catch (CryptographicException)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, "The paired controller certificate cannot validate job requests.");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateBytes);
        }
    }
    private async Task RecordPairingFailureAsync(string stage, Guid pairingId, CancellationToken cancellationToken)
    {
        var state = await stateStore.RecordPairingFailureAsync(
            DateTimeOffset.UtcNow,
            options.PairingFailureWindow,
            options.PairingFailureLimit,
            options.PairingCooldown,
            cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>
        {
            ["stage"] = stage,
            ["failureCount"] = state.FailureCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (state.BlockedUntilUtc is { } blockedUntil)
        {
            details["blockedUntilUtc"] = blockedUntil.ToString("O");
        }
        await AuditAsync("pairing.failed", null, pairingId.ToString("N"), false, ErrorCode.Unauthenticated, details, cancellationToken).ConfigureAwait(false);
    }

    private async Task AuditAsync(
        string eventType,
        string? controllerId,
        string? targetId,
        bool succeeded,
        ErrorCode? errorCode,
        IReadOnlyDictionary<string, string>? details,
        CancellationToken cancellationToken)
    {
        await stateStore.AppendAuditEventAsync(new AgentAuditEvent(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            eventType,
            controllerId,
            targetId,
            succeeded,
            errorCode,
            details), cancellationToken).ConfigureAwait(false);
        await stateStore.EnforceAuditQuotaAsync(options.AuditQuotaBytes, cancellationToken).ConfigureAwait(false);
    }
    private async Task<byte[]> ReadOutputAsync(string jobId, JobOutputKind stream, CancellationToken cancellationToken)
    {
        var streamDirectory = Path.Combine(
            stateStore.DataRoot,
            "segments",
            jobId,
            stream == JobOutputKind.Stdout ? "stdout" : "stderr");
        if (!Directory.Exists(streamDirectory))
        {
            return [];
        }

        await using var collected = new MemoryStream();
        foreach (var path in Directory.EnumerateFiles(streamDirectory, "*.seg").OrderBy(path => path, StringComparer.Ordinal))
        {
            var remaining = MaximumReturnedOutputBytesPerStream - checked((int)collected.Length);
            if (remaining <= 0)
            {
                break;
            }

            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
            var buffer = new byte[Math.Min(16 * 1024, remaining)];
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await collected.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }

        return collected.ToArray();
    }
    private sealed record PendingControlSession(Guid SessionId, string ControllerId, byte[] Challenge, DateTimeOffset ExpiresAtUtc);

    private sealed record AuthenticatedControlSession(Guid SessionId, string ControllerId, DateTimeOffset ExpiresAtUtc);
    private static ControlPairingBinding ToContract(PairingBinding binding) => new(
        binding.PairingId,
        binding.AgentDeviceId,
        binding.ControllerId,
        binding.AgentEndpoint.Address.ToString(),
        binding.AgentEndpoint.Port,
        binding.AgentCertificateFingerprint,
        binding.AgentSpkiFingerprint,
        binding.ControllerCertificateFingerprint,
        binding.ControllerSpkiFingerprint);

    private static Task WriteSuccessAsync<T>(StreamWriter writer, T response) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));

    private static Task WriteFailureAsync(StreamWriter writer, ErrorCode code, string message, bool retryable = false) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(
            Result.Failure<object>(new RemoteError(code, message, Retryable: retryable)), ContractJson.Options));

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        listener.Stop();
        await taskRegistry.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
        executeOnceGate.Dispose();
        fileService.Dispose();
        updateService.Dispose();
    }
}
