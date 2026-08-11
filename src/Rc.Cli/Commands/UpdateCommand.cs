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
    private const int DefaultChunkSize = 256 * 1024;

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
        var chunkSize = GetPositiveInt(options, "chunk-size", DefaultChunkSize, maximum: DefaultChunkSize);
        var timeout = TimeSpan.FromSeconds(GetPositiveInt(options, "timeout-seconds", 180, maximum: 3600));
        var manifest = await BuildManifestAsync(packagePath, options.GetValueOrDefault("version")).ConfigureAwait(false);
        var updateId = Guid.NewGuid();

        await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        using var identity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName).ConfigureAwait(false);
        using var privateKey = identity.GetPrivateKey();
        var started = await SendStartAsync(connection, identity.ControllerId, privateKey, new UpdateStartRequest(updateId, manifest)).ConfigureAwait(false);
        if (started.State != UpdateState.Receiving)
        {
            return await WriteResultAsync(started, options.ContainsKey("text"), output).ConfigureAwait(false);
        }

        var total = manifest.Files.Sum(file => file.Length);
        var uploaded = 0L;
        foreach (var file in manifest.Files)
        {
            var fullPath = Path.Combine(Path.GetFullPath(packagePath), file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            for (var offset = 0L; offset < file.Length; offset += chunkSize)
            {
                var count = checked((int)Math.Min(chunkSize, file.Length - offset));
                var data = new byte[count];
                await stream.ReadExactlyAsync(data).ConfigureAwait(false);
                var request = new UpdateWriteChunkRequest(updateId, file.RelativePath, offset, data, Convert.ToHexString(SHA256.HashData(data)));
                await SendChunkAsync(connection, identity.ControllerId, privateKey, request).ConfigureAwait(false);
                uploaded += count;
                await error.WriteLineAsync($"[rcctl] update {uploaded}/{total} bytes").ConfigureAwait(false);
            }
        }

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
                await error.WriteLineAsync($"[rcctl] update state={status.State}").ConfigureAwait(false);
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

    private static async Task<UpdateStatusResponse> SendChunkAsync(AuthenticatedControlConnection connection, string controllerId, ECDsa privateKey, UpdateWriteChunkRequest request)
    {
        var signature = ControlRequestAuthentication.SignUpdateWriteChunk(connection.AgentDeviceId, controllerId, request, privateKey);
        try { return await connection.SendAsync<UpdateStatusResponse>(new ControlUpdateWriteChunkRequest(1, controllerId, request, signature), retryOnDisconnect: true).ConfigureAwait(false); }
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

    internal static string? NormalizePackageVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        var metadataSeparator = candidate.IndexOf('+');
        if (metadataSeparator > 0) candidate = candidate[..metadataSeparator];
        return Version.TryParse(candidate, out _) ? candidate : null;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
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
        "Usage: rcctl update apply <IP:port> --fingerprint <SHA256> --package <directory> [--version <version>] [--chunk-size <1-262144>] [--wait] [--timeout-seconds <1-3600>] [--text] | rcctl update status <IP:port> --fingerprint <SHA256> --update <GUID> [--text]";
}
