using System.Net;
using System.Net.Sockets;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.Cli.Security;

internal sealed class AuthenticatedControlConnection : IAsyncDisposable
{
    private const int ControlIoBufferSize = 64 * 1024;
    private readonly IPEndPoint endpoint;
    private readonly string fingerprint;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private PinnedTlsConnection? transport;
    private StreamWriter? writer;
    private StreamReader? reader;

    private AuthenticatedControlConnection(IPEndPoint endpoint, string fingerprint)
    {
        this.endpoint = endpoint;
        this.fingerprint = fingerprint;
    }

    public string AgentDeviceId { get; private set; } = string.Empty;
    public string ControllerId { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public bool SupportsBinaryTransfer { get; private set; }

    public bool SupportsStreamingIntegrity { get; private set; }

    public int MaximumBinaryTransferChunkBytes { get; private set; } = 4 * 1024 * 1024;

    public static async Task<AuthenticatedControlConnection> ConnectAsync(IPEndPoint endpoint, string fingerprint)
    {
        var connection = new AuthenticatedControlConnection(endpoint, fingerprint);
        try
        {
            await connection.OpenSessionAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TResponse> SendAsync<TResponse>(object request, bool retryOnDisconnect = false)
    {
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ExpiresAtUtc <= DateTimeOffset.UtcNow.AddSeconds(5))
            {
                await OpenSessionAsync().ConfigureAwait(false);
            }
            try
            {
                return await SendCoreAsync<TResponse>(request).ConfigureAwait(false);
            }
            catch (Exception exception) when (retryOnDisconnect && exception is IOException or SocketException)
            {
                await OpenSessionAsync().ConfigureAwait(false);
                return await SendCoreAsync<TResponse>(request).ConfigureAwait(false);
            }
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async Task<TransferBinaryWriteResponse> SendBinaryUploadAsync(TransferBinaryWriteRequest request, ReadOnlyMemory<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        return await SendBinaryUploadAsync(request, stream).ConfigureAwait(false);
    }

    public async Task<TransferBinaryWriteResponse> SendBinaryUploadAsync(TransferBinaryWriteRequest request, Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureSessionAsync().ConfigureAwait(false);
            var ready = await SendCoreAsync<TransferBinaryReadyResponse>(
                new ControlTransferWriteBinaryRequest(1, ControllerId, request)).ConfigureAwait(false);
            if (ready.Length != request.Length)
            {
                throw new InvalidDataException("The agent returned an invalid binary upload length.");
            }
            if (!ready.AlreadyCompleted)
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(request.Length, 1), 1024 * 1024));
                try
                {
                    var remaining = request.Length;
                    while (remaining > 0)
                    {
                        var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException("The upload source ended before the declared chunk length.");
                        hasher.AppendData(buffer, 0, count);
                        await transport!.Stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                        remaining -= count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                await transport!.Stream.FlushAsync().ConfigureAwait(false);
                var response = await ReadCoreAsync<TransferBinaryWriteResponse>().ConfigureAwait(false);
                var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                if (!string.Equals(response.Receipt.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Uploaded binary chunk hash mismatch.");
                }
                return response;
            }
            return await ReadCoreAsync<TransferBinaryWriteResponse>().ConfigureAwait(false);
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async Task<TransferBinaryReadResponse> SendBinaryDownloadAsync(TransferBinaryReadRequest request, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureSessionAsync().ConfigureAwait(false);
            var ready = await SendCoreAsync<TransferBinaryReadyResponse>(
                new ControlTransferReadBinaryRequest(1, ControllerId, request)).ConfigureAwait(false);
            if (ready.Length < 0 || ready.Length > request.MaximumBytes)
            {
                throw new InvalidDataException("The agent returned an invalid binary download length.");
            }
            await transport!.Stream.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
            await transport.Stream.FlushAsync().ConfigureAwait(false);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(ready.Length, 1), 1024 * 1024));
            try
            {
                var remaining = ready.Length;
                while (remaining > 0)
                {
                    var count = await transport.Stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("The binary download ended before the declared chunk length.");
                    hasher.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                    remaining -= count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            var response = await ReadCoreAsync<TransferBinaryReadResponse>().ConfigureAwait(false);
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (response.Length != ready.Length || !string.Equals(response.ChunkSha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded binary chunk hash mismatch.");
            }
            return response;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task OpenSessionAsync()
    {
        await DisposeTransportAsync().ConfigureAwait(false);
        transport = await PinnedTlsConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        writer = new StreamWriter(transport.Stream, new UTF8Encoding(false), ControlIoBufferSize, leaveOpen: true) { AutoFlush = true };
        reader = new StreamReader(transport.Stream, new UTF8Encoding(false), false, ControlIoBufferSize, leaveOpen: true);

        var hello = await SendCoreAsync<ControlHelloResponse>(new ControlHelloRequest(1)).ConfigureAwait(false);
        SupportsBinaryTransfer = hello.Capabilities.Contains(ControlCapabilities.BinaryTransferV1, StringComparer.Ordinal);
        SupportsStreamingIntegrity = hello.Capabilities.Contains(ControlCapabilities.StreamingIntegrityV2, StringComparer.Ordinal);
        MaximumBinaryTransferChunkBytes = hello.MaximumBinaryTransferChunkBytes is > 0
            ? hello.MaximumBinaryTransferChunkBytes.Value
            : 4 * 1024 * 1024;
        if (!hello.HasPairedController)
        {
            throw new InvalidOperationException("This agent has no paired controller. Run rcctl pair first.");
        }

        using var identity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName).ConfigureAwait(false);
        var challenge = await SendCoreAsync<ControlSessionStartResponse>(new ControlSessionStartRequest(1, identity.ControllerId)).ConfigureAwait(false);
        using var privateKey = identity.GetPrivateKey();
        var signature = ControlRequestAuthentication.SignSessionAuthentication(
            challenge.AgentDeviceId,
            identity.ControllerId,
            challenge.SessionId,
            challenge.Challenge,
            challenge.ExpiresAtUtc,
            privateKey);
        try
        {
            var authenticated = await SendCoreAsync<ControlSessionAuthenticateResponse>(
                new ControlSessionAuthenticateRequest(1, challenge.SessionId, identity.ControllerId, signature)).ConfigureAwait(false);
            AgentDeviceId = challenge.AgentDeviceId;
            ControllerId = authenticated.ControllerId;
            ExpiresAtUtc = authenticated.ExpiresAtUtc;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private async Task EnsureSessionAsync()
    {
        if (ExpiresAtUtc <= DateTimeOffset.UtcNow.AddSeconds(5))
        {
            await OpenSessionAsync().ConfigureAwait(false);
        }
    }

    private async Task<TResponse> SendCoreAsync<TResponse>(object request)
    {
        if (writer is null || reader is null)
        {
            throw new IOException("The authenticated control connection is not open.");
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Options)).ConfigureAwait(false);
        return await ReadCoreAsync<TResponse>().ConfigureAwait(false);
    }

    private async Task<TResponse> ReadCoreAsync<TResponse>()
    {
        if (reader is null)
        {
            throw new IOException("The authenticated control connection is not open.");
        }
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        var response = line is null ? null : JsonSerializer.Deserialize<ResultEnvelope<TResponse>>(line, ContractJson.Options);
        if (response is not { Ok: true, Result: not null })
        {
            throw new InvalidOperationException(response?.Error?.Message ?? "The agent did not return a valid response.");
        }
        return response.Result;
    }

    private async Task DisposeTransportAsync()
    {
        if (writer is not null)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;
        }
        reader?.Dispose();
        reader = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            transport = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            requestGate.Release();
            requestGate.Dispose();
        }
    }
}
