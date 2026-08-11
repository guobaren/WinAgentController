using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class FileCommand
{
    private const int DefaultFileReadSize = 256 * 1024;
    private const int PreferredBinaryTransferChunkSize = 64 * 1024 * 1024;

    public static async Task<int> RunFsAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryCommon(args, out var operation, out var endpoint, out var fingerprint, out var path, out var options, out var message)) return await FailAsync(error, message!, 2);
        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint!, fingerprint!);
            object result = operation switch
            {
                "list" => await connection.SendAsync<FileListResponse>(new ControlFileListRequest(1, connection.ControllerId, new FileListRequest(path!, options.ContainsKey("recursive")))),
                "stat" => await connection.SendAsync<FileStatResponse>(new ControlFileStatRequest(1, connection.ControllerId, new FileStatRequest(path!))),
                "read" => await connection.SendAsync<FileReadResponse>(new ControlFileReadRequest(1, connection.ControllerId, new FileReadRequest(path!, GetLong(options, "offset", 0), GetInt(options, "max-bytes", DefaultFileReadSize)))),
                "write" => await WriteAsync(connection, path!, options),
                _ => throw new ArgumentException(UsageFs()),
            };
            if (result is FileReadResponse read && options.ContainsKey("text")) await output.WriteAsync(Encoding.UTF8.GetString(read.Chunk.Data));
            else await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(result), ContractJson.Options));
            return 0;
        }
        catch (Exception exception) { return await FailAsync(error, exception.Message); }
    }

    public static async Task<int> RunCopyAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryCommon(args, out var operation, out var endpoint, out var fingerprint, out var path, out var options, out var message)) return await FailAsync(error, message!, 2);
        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint!, fingerprint!);
            EnsureCurrentAgentCapabilities(connection.SupportsBinaryTransfer, connection.SupportsStreamingIntegrity);
            if (operation == "status")
            {
                var sessionId = options.GetValueOrDefault("session") ?? path;
                var status = await connection.SendAsync<TransferStatusResponse>(new ControlTransferStatusRequest(1, connection.ControllerId, new TransferStatusRequest(sessionId!)));
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(status), ContractJson.Options));
                return 0;
            }
            var destination = options.GetValueOrDefault("to") ?? throw new ArgumentException("--to is required.");
            var defaultChunkSize = Math.Min(PreferredBinaryTransferChunkSize, connection.MaximumBinaryTransferChunkBytes);
            var chunkSize = GetInt(options, "chunk-size", defaultChunkSize);
            var sessionIdOption = options.GetValueOrDefault("session");
            TransferSessionSnapshot session;
            if (sessionIdOption is not null)
            {
                session = (await connection.SendAsync<TransferStatusResponse>(new ControlTransferStatusRequest(1, connection.ControllerId, new TransferStatusRequest(sessionIdOption)))).Session;
            }
            else if (operation == "upload")
            {
                var manifest = await BuildLocalManifestAsync(path!, includeHashes: false);
                session = (await connection.SendAsync<TransferStartResponse>(new ControlTransferStartRequest(1, connection.ControllerId,
                    new TransferStartRequest(TransferDirection.Upload, Path.GetFullPath(path!), destination, manifest, chunkSize,
                        StreamingIntegrity: true)))).Session;
            }
            else if (operation == "download")
            {
                session = (await connection.SendAsync<TransferStartResponse>(new ControlTransferStartRequest(1, connection.ControllerId,
                    new TransferStartRequest(TransferDirection.Download, path!, Path.GetFullPath(destination), new FileManifest(path!, []), chunkSize,
                        StreamingIntegrity: true)))).Session;
            }
            else throw new ArgumentException(UsageCopy());

            await error.WriteLineAsync($"[rcctl] transferSession={session.SessionId}");
            await error.FlushAsync();
            var speed = new TransferSpeedReporter(error);
            var transferredBytes = operation == "upload"
                ? await UploadAsync(connection, session, path!, speed)
                : await DownloadAsync(connection, session, destination, speed);
            speed.Stop();
            var completed = await connection.SendAsync<TransferCompleteResponse>(new ControlTransferCompleteRequest(1, connection.ControllerId, new TransferCompleteRequest(session.SessionId)));
            await speed.CompleteAsync(transferredBytes);
            await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(completed), ContractJson.Options));
            return 0;
        }
        catch (Exception exception) { return await FailAsync(error, exception.Message); }
    }

    private static async Task<FileWriteResponse> WriteAsync(AuthenticatedControlConnection connection, string path, Dictionary<string, string?> options)
    {
        byte[] data = options.TryGetValue("source", out var source) && source is not null
            ? await File.ReadAllBytesAsync(source)
            : Encoding.UTF8.GetBytes(options.GetValueOrDefault("data") ?? throw new ArgumentException("--data or --source is required."));
        return await connection.SendAsync<FileWriteResponse>(new ControlFileWriteRequest(1, connection.ControllerId, new FileWriteRequest(path, data, options.ContainsKey("overwrite"))));
    }

    private static async Task<long> UploadAsync(
        AuthenticatedControlConnection connection,
        TransferSessionSnapshot session,
        string localRoot,
        TransferSpeedReporter speed)
    {
        var root = Path.GetFullPath(localRoot);
        var transferredBytes = 0L;
        foreach (var entry in session.Manifest.Entries.Where(IsFile))
        {
            var file = string.IsNullOrEmpty(entry.RelativePath) ? root : Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            for (long offset = 0; offset < entry.Length; offset += session.ChunkSize)
            {
                var length = checked((int)Math.Min(session.ChunkSize, entry.Length - offset));
                if (session.CompletedChunks.Any(r => r.RelativePath == entry.RelativePath && r.Offset == offset && r.Length == length)) continue;
                stream.Position = offset;
                await connection.SendBinaryUploadAsync(
                    new TransferBinaryWriteRequest(session.SessionId, entry.RelativePath, offset, length, null),
                    stream,
                    speed.RecordBytesAsync);
                transferredBytes += length;
            }
        }
        return transferredBytes;
    }

    private static async Task<long> DownloadAsync(
        AuthenticatedControlConnection connection,
        TransferSessionSnapshot session,
        string localDestination,
        TransferSpeedReporter speed)
    {
        var root = Path.GetFullPath(localDestination);
        var transferredBytes = 0L;
        var destinations = LocalTransferPaths.ResolveManifest(root, session.Manifest);
        foreach (var item in destinations.Where(item => !IsFile(item.Entry))) Directory.CreateDirectory(item.Path);
        foreach (var item in destinations.Where(item => IsFile(item.Entry)))
        {
            var entry = item.Entry;
            var final = item.Path;
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            var part = final + ".rc-part";
            var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (offset > entry.Length || offset % session.ChunkSize != 0) { File.Delete(part); offset = 0; }
            await using (var stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                stream.Position = offset;
                while (offset < entry.Length)
                {
                    var length = checked((int)Math.Min(session.ChunkSize, entry.Length - offset));
                    var response = await connection.SendBinaryDownloadAsync(
                        new TransferBinaryReadRequest(session.SessionId, entry.RelativePath, offset, length),
                        stream,
                        speed.RecordBytesAsync);
                    offset += response.Length;
                    transferredBytes += response.Length;
                }
            }
            if (entry.Sha256 is not null && !string.Equals(await HashFileAsync(part), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Final hash mismatch for '{entry.RelativePath}'.");
            File.Move(part, final, true);
        }
        return transferredBytes;
    }

    internal static void EnsureCurrentAgentCapabilities(bool supportsBinaryTransfer, bool supportsStreamingIntegrity)
    {
        if (!supportsBinaryTransfer || !supportsStreamingIntegrity)
        {
            throw new InvalidOperationException("The target Agent is outdated. Upgrade it to a version that supports binary-transfer-v1 and streaming-integrity-v2.");
        }
    }

    internal static async Task<FileManifest> BuildLocalManifestAsync(string path, bool includeHashes = true)
    {
        var full = Path.GetFullPath(path); var entries = new List<FileManifestEntry>();
        if (File.Exists(full)) { var f = new FileInfo(full); entries.Add(new FileManifestEntry(string.Empty, f.Length, f.LastWriteTimeUtc, includeHashes ? await HashFileAsync(full) : null, FileEntryKind.File)); }
        else if (Directory.Exists(full))
        {
            foreach (var entry in LocalTransferPaths.Enumerate(full))
            {
                var relativePath = Path.GetRelativePath(full, entry).Replace('\\', '/');
                if (Directory.Exists(entry)) entries.Add(new FileManifestEntry(relativePath, 0, Directory.GetLastWriteTimeUtc(entry), null, FileEntryKind.Directory));
                else { var file = new FileInfo(entry); entries.Add(new FileManifestEntry(relativePath, file.Length, file.LastWriteTimeUtc, includeHashes ? await HashFileAsync(entry) : null, FileEntryKind.File)); }
            }
        }
        else throw new FileNotFoundException(path);
        return new FileManifest(full, entries);
    }

    private static async Task<string> HashFileAsync(string path) { await using var s = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(s)); }

    private static bool IsFile(FileManifestEntry entry) =>
        entry.Kind == FileEntryKind.File || entry.Kind is null && entry.Sha256 is not null;

    private static bool TryCommon(string[] args, out string? op, out IPEndPoint? endpoint, out string? fingerprint, out string? path, out Dictionary<string, string?> options, out string? error)
    {
        op=null; endpoint=null; fingerprint=null; path=null; error=null; options=new(StringComparer.OrdinalIgnoreCase);
        if (args.Length < 3 || !IPEndPoint.TryParse(args[1], out endpoint)) { error="Operation, IP:port, and path/session are required."; return false; }
        op=args[0]; path=args[2];
        for(int i=3;i<args.Length;i++) { var key=args[i].TrimStart('-'); if(key is "recursive" or "text" or "overwrite") options[key]=null; else if(i+1<args.Length) options[key]=args[++i]; else {error=$"Missing value for {args[i]}.";return false;} }
        fingerprint=NormalizeFingerprint(options.GetValueOrDefault("fingerprint")); if(fingerprint is null){error="--fingerprint <SHA256> is required.";return false;} return true;
    }
    private static int GetInt(Dictionary<string,string?> o,string k,int d)=>o.TryGetValue(k,out var v)&&int.TryParse(v,NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:d;
    private static long GetLong(Dictionary<string,string?> o,string k,long d)=>o.TryGetValue(k,out var v)&&long.TryParse(v,NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:d;
    private static string? NormalizeFingerprint(string? value){if(value is null)return null;var n=value.Replace(":","").Trim();return n.Length==64&&n.All(Uri.IsHexDigit)?n.ToUpperInvariant():null;}
    private static async Task<int> FailAsync(TextWriter error, string message, int exitCode = 1)
    {
        await error.WriteLineAsync(message);
        return exitCode;
    }
    private static string UsageFs()=>"Usage: rcctl fs list|stat|read|write <IP:port> <path> --fingerprint <SHA256> ...";
    private static string UsageCopy()=>"Usage: rcctl copy upload|download|status <IP:port> <path|session> --fingerprint <SHA256> ...";
}

internal sealed class TransferSpeedReporter
{
    private const double BytesPerMebibyte = 1024d * 1024d;
    private readonly TextWriter output;
    private readonly TimeSpan reportInterval;
    private readonly Func<TimeSpan> elapsedProvider;
    private readonly Stopwatch? stopwatch;
    private readonly List<double> samples = [];
    private TimeSpan lastSampleAt;
    private long lastSampleBytes;

    public TransferSpeedReporter(
        TextWriter output,
        TimeSpan? reportInterval = null,
        Func<TimeSpan>? elapsedProvider = null)
    {
        this.output = output;
        this.reportInterval = reportInterval ?? TimeSpan.FromSeconds(1);
        if (elapsedProvider is null)
        {
            stopwatch = Stopwatch.StartNew();
            this.elapsedProvider = () => stopwatch.Elapsed;
        }
        else
        {
            this.elapsedProvider = elapsedProvider;
        }
    }

    public long TotalBytes { get; private set; }

    public void Stop() => stopwatch?.Stop();

    public async ValueTask RecordBytesAsync(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        TotalBytes += byteCount;
        var elapsed = elapsedProvider();
        if (elapsed - lastSampleAt < reportInterval)
        {
            return;
        }

        await WriteSampleAsync(elapsed).ConfigureAwait(false);
    }

    public async Task CompleteAsync(long? logicalBytes = null)
    {
        Stop();
        var elapsed = elapsedProvider();
        if (TotalBytes > lastSampleBytes)
        {
            await WriteSampleAsync(elapsed).ConfigureAwait(false);
        }

        var completedBytes = logicalBytes ?? TotalBytes;
        if (completedBytes < 0 || completedBytes > TotalBytes)
        {
            throw new InvalidDataException("Logical transfer bytes cannot exceed bytes sent on the wire.");
        }
        var retransmittedBytes = TotalBytes - completedBytes;
        var average = ToMebibytesPerSecond(TotalBytes, elapsed.TotalSeconds);
        var minimum = samples.Count == 0 ? average : samples.Min();
        var maximum = samples.Count == 0 ? average : samples.Max();
        await output.WriteLineAsync(
            $"[rcctl] bytes={completedBytes} wireBytes={TotalBytes} retransmittedBytes={retransmittedBytes} elapsed={elapsed.TotalSeconds:F3}s MiB/s={average:F2} minMiB/s={minimum:F2} maxMiB/s={maximum:F2} avgMiB/s={average:F2}").ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteSampleAsync(TimeSpan elapsed)
    {
        var deltaBytes = TotalBytes - lastSampleBytes;
        var deltaSeconds = (elapsed - lastSampleAt).TotalSeconds;
        if (deltaBytes <= 0)
        {
            return;
        }

        var speed = ToMebibytesPerSecond(deltaBytes, deltaSeconds);
        samples.Add(speed);
        lastSampleBytes = TotalBytes;
        lastSampleAt = elapsed;
        await output.WriteLineAsync(
            $"[rcctl] progress bytes={TotalBytes} elapsed={elapsed.TotalSeconds:F3}s currentMiB/s={speed:F2}").ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static double ToMebibytesPerSecond(long bytes, double seconds) =>
        bytes / BytesPerMebibyte / Math.Max(seconds, 0.001d);
}

internal static class LocalTransferPaths
{
    internal sealed record ManifestDestination(FileManifestEntry Entry, string Path);

    public static IReadOnlyList<ManifestDestination> ResolveManifest(string root, FileManifest manifest)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new List<ManifestDestination>(manifest.Entries.Count);
        foreach (var entry in manifest.Entries)
        {
            if (Path.IsPathFullyQualified(entry.RelativePath))
            {
                throw new InvalidDataException("Download manifest paths must be relative.");
            }

            var path = string.IsNullOrEmpty(entry.RelativePath)
                ? root
                : Path.GetFullPath(Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if ((!string.Equals(path, root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || !seen.Add(path))
            {
                throw new InvalidDataException("Download manifest contains an unsafe or duplicate path.");
            }
            destinations.Add(new ManifestDestination(entry, path));
        }
        return destinations;
    }

    public static IReadOnlyList<string> Enumerate(string root)
    {
        var entries = new List<string>();
        EnumerateDirectory(root, entries);
        return entries;
    }

    private static void EnumerateDirectory(string directory, List<string> entries)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Upload paths cannot contain reparse points.");
            }
            entries.Add(entry);
            if (Directory.Exists(entry)) EnumerateDirectory(entry, entries);
        }
    }
}
