namespace Rc.Contracts;

public enum FileEntryKind
{
    File,
    Directory,
}

public sealed class FileManifest
{
    public FileManifest(string rootPath, IReadOnlyList<FileManifestEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(entries);
        RootPath = rootPath;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public string RootPath { get; }

    public IReadOnlyList<FileManifestEntry> Entries { get; }
}

public sealed record FileManifestEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? Sha256,
    FileEntryKind? Kind = null);

public sealed record FileManifestRequest(string RootPath);

public sealed record FileManifestResponse(FileManifest Manifest);

public sealed record FileListRequest(string RootPath, bool Recursive = false);

public sealed class FileListResponse
{
    public FileListResponse(IReadOnlyList<FileMetadata> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public IReadOnlyList<FileMetadata> Entries { get; }
}

public sealed record FileStatRequest(string Path);

public sealed record FileMetadata(
    string Path,
    FileEntryKind Kind,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? Sha256 = null);

public sealed record FileStatResponse(FileMetadata Entry);

public sealed class FileReadRequest
{
    public FileReadRequest(string path, long offset, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        Path = path;
        Offset = offset;
        MaximumBytes = maximumBytes;
    }

    public string Path { get; }

    public long Offset { get; }

    public int MaximumBytes { get; }
}

public sealed class FileRangeChunk
{
    private readonly byte[] data;

    public FileRangeChunk(string path, long offset, byte[] data, bool isFinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(data);
        Path = path;
        Offset = offset;
        this.data = data.ToArray();
        IsFinal = isFinal;
    }

    public string Path { get; }

    public long Offset { get; }

    public byte[] Data => data.ToArray();

    public bool IsFinal { get; }
}

public sealed record FileReadResponse(FileRangeChunk Chunk);

public sealed class FileWriteRequest
{
    private readonly byte[] data;

    public FileWriteRequest(string path, byte[] data, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);
        Path = path;
        this.data = data.ToArray();
        Overwrite = overwrite;
    }

    public string Path { get; }

    public byte[] Data => data.ToArray();

    public bool Overwrite { get; }
}

public sealed record FileWriteResponse(FileMetadata Entry);

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferSessionState
{
    Preparing,
    Transferring,
    Verifying,
    Completed,
    Failed,
    Expired,
}

public sealed class TransferSessionSnapshot
{
    public TransferSessionSnapshot(
        string sessionId,
        TransferDirection direction,
        TransferSessionState state,
        string sourcePath,
        string destinationPath,
        FileManifest manifest,
        int chunkSize,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<string>? completedRelativePaths = null,
        IReadOnlyList<TransferChunkReceipt>? completedChunks = null,
        bool streamingIntegrity = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(expiresAtUtc, createdAtUtc, nameof(expiresAtUtc));

        SessionId = sessionId;
        Direction = direction;
        State = state;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Manifest = manifest;
        ChunkSize = chunkSize;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        CompletedRelativePaths = Array.AsReadOnly((completedRelativePaths ?? []).ToArray());
        CompletedChunks = Array.AsReadOnly((completedChunks ?? []).ToArray());
        StreamingIntegrity = streamingIntegrity;
    }

    public string SessionId { get; }

    public TransferDirection Direction { get; }

    public TransferSessionState State { get; }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public FileManifest Manifest { get; }

    public int ChunkSize { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public IReadOnlyList<string> CompletedRelativePaths { get; }

    public IReadOnlyList<TransferChunkReceipt> CompletedChunks { get; }

    public bool StreamingIntegrity { get; }
}

public sealed record TransferChunkReceipt(string RelativePath, long Offset, int Length, string Sha256);

public sealed record TransferStartRequest(
    TransferDirection Direction,
    string SourcePath,
    string DestinationPath,
    FileManifest Manifest,
    int ChunkSize,
    bool StreamingIntegrity = false);

public sealed record TransferStartResponse(TransferSessionSnapshot Session);

public sealed class TransferWriteChunkRequest
{
    public TransferWriteChunkRequest(FileChunk chunk, string chunkSha256)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkSha256);
        Chunk = chunk;
        ChunkSha256 = chunkSha256;
    }

    public FileChunk Chunk { get; }

    public string ChunkSha256 { get; }
}

public sealed record TransferWriteChunkResponse(TransferSessionSnapshot Session);

public sealed record TransferBinaryWriteRequest(
    string SessionId,
    string RelativePath,
    long Offset,
    int Length,
    string? ChunkSha256);

public sealed record TransferBinaryReadyResponse(int Length, bool AlreadyCompleted);

public sealed record TransferBinaryWriteResponse(TransferChunkReceipt Receipt);

public sealed record TransferBinaryReadRequest(
    string SessionId,
    string RelativePath,
    long Offset,
    int MaximumBytes);

public sealed record TransferBinaryReadResponse(int Length, string ChunkSha256);

public sealed class TransferReadChunkRequest
{
    public TransferReadChunkRequest(string sessionId, string relativePath, long offset, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        SessionId = sessionId;
        RelativePath = relativePath;
        Offset = offset;
        MaximumBytes = maximumBytes;
    }

    public string SessionId { get; }

    public string RelativePath { get; }

    public long Offset { get; }

    public int MaximumBytes { get; }
}

public sealed record TransferReadChunkResponse(FileChunk Chunk, string ChunkSha256);

public sealed record TransferCompleteRequest(string SessionId);

public sealed record TransferCompleteResponse(TransferSessionSnapshot Session);

public sealed record TransferStatusRequest(string SessionId);

public sealed record TransferStatusResponse(TransferSessionSnapshot Session);
