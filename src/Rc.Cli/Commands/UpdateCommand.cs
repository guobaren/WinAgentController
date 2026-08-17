using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

internal static class UpdateCommand
{
    private const int PreferredBinaryChunkSize = 64 * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 2 || !IPEndPoint.TryParse(args[1], out var endpoint))
        {
            return await FailAsync(error, Usage()).ConfigureAwait(false);
        }
        var operation = args[0].ToLowerInvariant();
        if (!TryParseOptions(args[2..], out var options, out var parseError))
        {
            return await FailAsync(error, parseError!).ConfigureAwait(false);
        }
        var fingerprint = NormalizeFingerprint(options.GetValueOrDefault("fingerprint"));
        if (fingerprint is null)
        {
            return await FailAsync(error, "--fingerprint <SHA256> is required.").ConfigureAwait(false);
        }

        try
        {
            return operation switch
            {
                "apply" => await ApplyAsync(endpoint, fingerprint, options, output, error).ConfigureAwait(false),
                "status" => await StatusAsync(endpoint, fingerprint, options, output).ConfigureAwait(false),
                _ => await FailAsync(error, Usage()).ConfigureAwait(false),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or CryptographicException or TimeoutException)
        {
            return await FailAsync(error, exception.Message).ConfigureAwait(false);
        }
    }

    private static async Task<int> ApplyAsync(IPEndPoint endpoint, string fingerprint, Dictionary<string, string?> options, TextWriter output, TextWriter error)
    {
        var packagePath = options.GetValueOrDefault("package") ?? throw new ArgumentException("--package <directory> is required.");
        var timeout = TimeSpan.FromSeconds(GetPositiveInt(options, "timeout-seconds", 180, maximum: 3600));
        var manifest = await BuildManifestAsync(packagePath, options.GetValueOrDefault("version")).ConfigureAwait(false);
        var updateId = Guid.NewGuid();

        await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        EnsureCurrentAgentCapability(connection.SupportsBinaryUpdate);
        var chunkSize = ResolveChunkSize(connection.MaximumBinaryUpdateChunkBytes, options.GetValueOrDefault("chunk-size"));
        using var identity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName).ConfigureAwait(false);
        using var privateKey = identity.GetPrivateKey();
        var started = await SendStartAsync(connection, identity.ControllerId, privateKey, new UpdateStartRequest(updateId, manifest)).ConfigureAwait(false);
        if (started.State != UpdateState.Receiving)
        {
            return await WriteResultAsync(started, options.ContainsKey("text"), output).ConfigureAwait(false);
        }

        var total = manifest.Files.Sum(file => file.Length);
        await error.WriteLineAsync($"[rcctl] updateId={updateId} transport=binary-update-v1 chunkBytes={chunkSize}").ConfigureAwait(false);
        await error.FlushAsync().ConfigureAwait(false);
        var speed = new TransferSpeedReporter(error, totalBytes: total);
        foreach (var file in manifest.Files)
        {
            var fullPath = Path.Combine(Path.GetFullPath(packagePath), file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            for (var offset = 0L; offset < file.Length; offset += chunkSize)
            {
                var count = checked((int)Math.Min(chunkSize, file.Length - offset));
                var hash = await HashRangeAsync(stream, offset, count).ConfigureAwait(false);
                stream.Position = offset;
                var request = new UpdateBinaryWriteRequest(updateId, file.RelativePath, offset, count, hash);
                await SendBinaryChunkAsync(connection, identity.ControllerId, privateKey, request, stream, speed.RecordBytesAsync).ConfigureAwait(false);
            }
        }
        speed.Stop();
        await speed.CompleteAsync(total).ConfigureAwait(false);

        var completed = await SendCompleteAsync(connection, identity.ControllerId, privateKey, new UpdateCompleteRequest(updateId)).ConfigureAwait(false);
        if (!options.ContainsKey("wait") || completed.State is UpdateState.Succeeded or UpdateState.Failed)
        {
            return await WriteResultAsync(completed, options.ContainsKey("text"), output).ConfigureAwait(false);
        }
        var final = await WaitForResultAsync(endpoint, fingerprint, identity.ControllerId, privateKey, updateId, timeout, error).ConfigureAwait(false);
        return await WriteResultAsync(final, options.ContainsKey("text"), output).ConfigureAwait(false);
    }

    private static async Task<int> StatusAsync(IPEndPoint endpoint, string fingerprint, Dictionary<string, string?> options, TextWriter output)
    {
        if (!Guid.TryParse(options.GetValueOrDefault("update"), out var updateId))
        {
            throw new ArgumentException("--update <GUID> is required.");
        }
        await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        using var identity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName).ConfigureAwait(false);
        using var privateKey = identity.GetPrivateKey();
        var status = await SendStatusAsync(connection, identity.ControllerId, privateKey, new UpdateStatusRequest(updateId)).ConfigureAwait(false);
        return await WriteResultAsync(status, options.ContainsKey("text"), output).ConfigureAwait(false);
    }

    private static async Task<UpdateStatusResponse> WaitForResultAsync(
        IPEndPoint endpoint,
        string fingerprint,
        string controllerId,
        ECDsa privateKey,
        Guid updateId,
        TimeSpan timeout,
        TextWriter error)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            try
            {
                await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
                var status = await SendStatusAsync(connection, controllerId, privateKey, new UpdateStatusRequest(updateId)).ConfigureAwait(false);
                if (status.State is UpdateState.Succeeded or UpdateState.Failed)
                {
                    return status;
                }
                var percent = status.TotalBytes > 0 ? 100d * status.ReceivedBytes / status.TotalBytes : 0d;
                await error.WriteLineAsync($"[rcctl] update state={status.State} received={status.ReceivedBytes}/{status.TotalBytes} ({percent:F1}%)").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or AuthenticationException or InvalidOperationException)
            {
                lastError = exception;
            }
        }
        throw new TimeoutException(lastError is null ? "The update did not complete before the timeout." : $"The update did not complete before the timeout: {lastError.Message}");
    }

    private static async Task<UpdateStatusResponse> SendStartAsync(AuthenticatedControlConnection connection, string controllerId, ECDsa privateKey, UpdateStartRequest request)
    {
        var signature = ControlRequestAuthentication.SignUpdateStart(connection.AgentDeviceId, controllerId, request, privateKey);
        try { return await connection.SendAsync<UpdateStatusResponse>(new ControlUpdateStartRequest(1, controllerId, request, signature)).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private static async Task<UpdateStatusResponse> SendCompleteAsync(AuthenticatedControlConnection connection, string controllerId, ECDsa privateKey, UpdateCompleteRequest request)
    {
        var signature = ControlRequestAuthentication.SignUpdateComplete(connection.AgentDeviceId, controllerId, request, privateKey);
        try { return await connection.SendAsync<UpdateStatusResponse>(new ControlUpdateCompleteRequest(1, controllerId, request, signature)).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private static async Task<UpdateStatusResponse> SendStatusAsync(AuthenticatedControlConnection connection, string controllerId, ECDsa privateKey, UpdateStatusRequest request)
    {
        var signature = ControlRequestAuthentication.SignUpdateStatus(connection.AgentDeviceId, controllerId, request, privateKey);
        try { return await connection.SendAsync<UpdateStatusResponse>(new ControlUpdateStatusRequest(1, controllerId, request, signature)).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private static async Task<UpdatePackageManifest> BuildManifestAsync(string packagePath, string? version)
    {
        var root = Path.GetFullPath(packagePath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The update package directory was not found: {root}");
        }
        var files = new List<UpdatePackageFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(new UpdatePackageFile(relativePath, info.Length, await HashFileAsync(path).ConfigureAwait(false)));
        }
        var agentPath = Path.Combine(root, "Rc.Agent.exe");
        var fileVersion = File.Exists(agentPath) ? FileVersionInfo.GetVersionInfo(agentPath) : null;
        var resolvedVersion = NormalizePackageVersion(version ?? fileVersion?.FileVersion ?? fileVersion?.ProductVersion);
        if (resolvedVersion is null)
        {
            throw new ArgumentException("--version is required when the package does not expose a valid Rc.Agent.exe version.");
        }
        return new UpdatePackageManifest("RemoteController", resolvedVersion, files);
    }

    private static async Task<UpdateStatusResponse> SendBinaryChunkAsync(
        AuthenticatedControlConnection connection,
        string controllerId,
        ECDsa privateKey,
        UpdateBinaryWriteRequest request,
        Stream source,
        Func<int, ValueTask> reportProgress)
    {
        var signature = ControlRequestAuthentication.SignUpdateWriteBinary(connection.AgentDeviceId, controllerId, request, privateKey);
        try
        {
            var response = await connection.SendBinaryUpdateUploadAsync(request, signature, source, reportProgress).ConfigureAwait(false);
            return response.Status;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    internal static string? NormalizePackageVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        var metadataSeparator = candidate.IndexOf('+');
        if (metadataSeparator > 0) candidate = candidate[..metadataSeparator];
        return Version.TryParse(candidate, out _) ? candidate : null;
    }

    internal static void EnsureCurrentAgentCapability(bool supportsBinaryUpdate)
    {
        if (!supportsBinaryUpdate)
        {
            throw new InvalidOperationException("The target Agent is outdated. Upgrade it to a version that supports binary-update-v1.");
        }
    }

    internal static int ResolveChunkSize(int advertisedMaximum, string? requested)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(advertisedMaximum, 1);
        var defaultValue = Math.Min(PreferredBinaryChunkSize, advertisedMaximum);
        if (requested is null) return defaultValue;
        return int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 && parsed <= advertisedMaximum
            ? parsed
            : throw new ArgumentException($"--chunk-size must be between 1 and {advertisedMaximum}.");
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static async Task<string> HashRangeAsync(Stream stream, long offset, int length)
    {
        stream.Position = offset;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(length, 1024 * 1024)];
        var remaining = length;
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The update package file ended before the declared chunk length.");
            hasher.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static async Task<int> WriteResultAsync(UpdateStatusResponse status, bool text, TextWriter output)
    {
        if (text)
        {
            await output.WriteLineAsync($"updateId: {status.UpdateId}").ConfigureAwait(false);
            await output.WriteLineAsync($"state: {status.State}").ConfigureAwait(false);
            await output.WriteLineAsync($"version: {status.Version}").ConfigureAwait(false);
            await output.WriteLineAsync($"received: {status.ReceivedBytes}/{status.TotalBytes}").ConfigureAwait(false);
            if (status.InstallationJobId is not null) await output.WriteLineAsync($"installationJobId: {status.InstallationJobId}").ConfigureAwait(false);
            if (status.FailureMessage is not null) await output.WriteLineAsync($"failure: {status.FailureMessage}").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(status), ContractJson.Options)).ConfigureAwait(false);
        }
        return status.State == UpdateState.Failed ? 1 : 0;
    }

    private static bool TryParseOptions(string[] args, out Dictionary<string, string?> options, out string? error)
    {
        options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                error = Usage();
                return false;
            }
            var key = option[2..];
            if (key is "text" or "wait")
            {
                options[key] = null;
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{option} requires a value.";
                return false;
            }
            options[key] = args[++index];
        }
        return true;
    }

    private static int GetPositiveInt(Dictionary<string, string?> options, string name, int defaultValue, int maximum)
    {
        if (!options.TryGetValue(name, out var value)) return defaultValue;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 && parsed <= maximum
            ? parsed
            : throw new ArgumentException($"--{name} must be between 1 and {maximum}.");
    }

    private static string? NormalizeFingerprint(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized.ToUpperInvariant() : null;
    }

    private static Task<int> FailAsync(TextWriter error, string message)
    {
        error.WriteLine(message);
        return Task.FromResult(2);
    }

    private static string Usage() =>
        "Usage: rcctl update apply <IP:port> --fingerprint <SHA256> --package <directory> [--version <version>] [--chunk-size <bytes>] [--wait] [--timeout-seconds <1-3600>] [--text] | rcctl update status <IP:port> --fingerprint <SHA256> --update <GUID> [--text]";
}
