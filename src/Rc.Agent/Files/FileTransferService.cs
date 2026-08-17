using System.Security.Cryptography;
using System.Buffers;
using System.Collections.Concurrent;
using Rc.Agent.Configuration;
using Rc.Agent.Persistence;
using Rc.Contracts;

namespace Rc.Agent.Files;

public sealed class FileTransferService : IDisposable
{
    private readonly AgentStateStore store;
    private readonly AgentOptions options;
    private readonly SafeFileRoot paths;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TransferSessionSnapshot> sessionCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransferSessionIndex> sessionIndexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> unpersistedStreamingBytes = new(StringComparer.Ordinal);

    public FileTransferService(AgentStateStore store, AgentOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? new AgentOptions();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        paths = new SafeFileRoot(this.options.FileRoot);
    }

    public async Task<FileListResponse> ListAsync(FileListRequest request, CancellationToken cancellationToken = default)
    {
        var entries = paths.Enumerate(request.RootPath, request.Recursive)
            .Select(GetMetadata).OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        return await Task.FromResult(new FileListResponse(entries));
    }

    public Task<FileStatResponse> StatAsync(FileStatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileStatResponse(GetMetadata(paths.Resolve(request.Path))));

    public async Task<FileReadResponse> ReadAsync(FileReadRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaximumBytes > options.MaximumTransferChunkBytes) throw new ArgumentOutOfRangeException(nameof(request));
        var path = paths.Resolve(request.Path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        if (request.Offset > stream.Length) throw new ArgumentOutOfRangeException(nameof(request));
        stream.Position = request.Offset;
        var data = new byte[Math.Min(request.MaximumBytes, checked((int)(stream.Length - request.Offset)))];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return new FileReadResponse(new FileRangeChunk(paths.ToDisplayPath(path), request.Offset, data, request.Offset + data.Length >= stream.Length));
    }

    public async Task<FileWriteResponse> WriteAsync(FileWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Data.Length > options.MaximumAtomicWriteBytes) throw new ResourceExhaustedException("Atomic write exceeds the configured byte limit.");
        var path = paths.Resolve(request.Path);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        if (!request.Overwrite && File.Exists(path)) throw new IOException("The destination already exists.");
        var temporary = Path.Combine(parent, ".rc-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, request.Data, cancellationToken);
            File.Move(temporary, path, request.Overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
        return new FileWriteResponse(GetMetadata(path));
    }

    public async Task<FileManifestResponse> GetManifestAsync(FileManifestRequest request, CancellationToken cancellationToken = default) =>
        new(await BuildManifestAsync(request.RootPath, includeHashes: true, cancellationToken));

    public async Task<TransferStartResponse> StartTransferAsync(TransferStartRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ChunkSize < 1 || request.ChunkSize > options.MaximumTransferChunkBytes) throw new ArgumentOutOfRangeException(nameof(request));
        FileManifest manifest;
        if (request.Direction == TransferDirection.Download)
        {
            paths.Resolve(request.SourcePath);
            manifest = await BuildManifestAsync(request.SourcePath, includeHashes: !request.StreamingIntegrity, cancellationToken);
        }
        else
        {
            paths.Resolve(request.DestinationPath);
            manifest = request.Manifest;
            ValidateManifest(manifest, request.DestinationPath, request.StreamingIntegrity);
        }
        EnsureQuota(manifest);
        var now = timeProvider.GetUtcNow();
        var snapshot = new TransferSessionSnapshot(
            "transfer-" + Guid.NewGuid().ToString("N"), request.Direction, TransferSessionState.Transferring,
            request.SourcePath, request.DestinationPath, manifest, request.ChunkSize, now, now.Add(options.TransferSessionLifetime),
            streamingIntegrity: request.StreamingIntegrity);
        await store.SaveTransferSessionAsync(snapshot, cancellationToken);
        sessionCache[snapshot.SessionId] = snapshot;
        sessionIndexes[snapshot.SessionId] = TransferSessionIndex.Create(snapshot);
        return new TransferStartResponse(snapshot);
    }

    public async Task<TransferWriteChunkResponse> WriteChunkAsync(TransferWriteChunkRequest request, CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetActiveSessionAsync(request.Chunk.TransferSessionId, TransferDirection.Upload, cancellationToken);
            var index = GetIndex(session);
            if (!index.TryGetFileEntry(request.Chunk.RelativePath, out var entry)) throw new KeyNotFoundException($"No file entry exists for '{request.Chunk.RelativePath}'.");
            ValidateChunk(session, entry, request.Chunk.Offset, request.Chunk.Data.Length);
            var hash = Convert.ToHexString(SHA256.HashData(request.Chunk.Data));
            if (!string.Equals(hash, NormalizeHash(request.ChunkSha256), StringComparison.Ordinal)) throw new InvalidDataException("Chunk SHA-256 mismatch.");
            if (index.TryGet(request.Chunk.RelativePath, request.Chunk.Offset, out var existing))
            {
                if (existing.Length != request.Chunk.Data.Length || !string.Equals(existing.Sha256, hash, StringComparison.Ordinal)) throw new InvalidDataException("A different chunk is already stored at this offset.");
                return new TransferWriteChunkResponse(session);
            }
            var part = GetPartPath(session.SessionId, request.Chunk.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            await using (var stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 64 * 1024, true))
            {
                if (stream.Length != entry.Length) stream.SetLength(entry.Length);
                stream.Position = request.Chunk.Offset;
                await stream.WriteAsync(request.Chunk.Data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            var receipt = new TransferChunkReceipt(request.Chunk.RelativePath, request.Chunk.Offset, request.Chunk.Data.Length, hash);
            index.Add(receipt, entry);
            var updated = Clone(session, completedChunks: index.CompletedChunks, completedPaths: index.CompletedPaths);
            await store.SaveTransferSessionAsync(updated, cancellationToken);
            sessionCache[updated.SessionId] = updated;
            return new TransferWriteChunkResponse(updated);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<TransferBinaryWriteResponse> WriteBinaryChunkAsync(
        TransferBinaryWriteRequest request,
        Stream input,
        Func<TransferBinaryReadyResponse, Task> acknowledgeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(acknowledgeAsync);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetActiveSessionAsync(request.SessionId, TransferDirection.Upload, cancellationToken);
            var index = GetIndex(session);
            if (!index.TryGetFileEntry(request.RelativePath, out var entry)) throw new KeyNotFoundException($"No file entry exists for '{request.RelativePath}'.");
            ValidateChunk(session, entry, request.Offset, request.Length);
            var expectedHash = request.ChunkSha256 is null ? null : NormalizeHash(request.ChunkSha256);
            if (index.TryGet(request.RelativePath, request.Offset, out var existing))
            {
                if (existing.Length != request.Length || expectedHash is not null && !string.Equals(existing.Sha256, expectedHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A different chunk is already stored at this offset.");
                }
                await acknowledgeAsync(new TransferBinaryReadyResponse(request.Length, true)).ConfigureAwait(false);
                return new TransferBinaryWriteResponse(existing);
            }

            var part = GetPartPath(session.SessionId, request.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            await using var stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1024 * 1024, true);
            if (stream.Length != entry.Length) stream.SetLength(entry.Length);
            stream.Position = request.Offset;
            await acknowledgeAsync(new TransferBinaryReadyResponse(request.Length, false)).ConfigureAwait(false);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(request.Length, 1024 * 1024));
            try
            {
                var remaining = request.Length;
                while (remaining > 0)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("The binary upload ended before the declared chunk length.");
                    hasher.AppendData(buffer, 0, count);
                    await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    remaining -= count;
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (expectedHash is not null && !string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Chunk SHA-256 mismatch.");
            }
            var receipt = new TransferChunkReceipt(request.RelativePath, request.Offset, request.Length, actualHash);
            index.Add(receipt, entry);
            if (!session.StreamingIntegrity)
            {
                var updated = Clone(session, completedChunks: index.CompletedChunks, completedPaths: index.CompletedPaths);
                sessionCache[updated.SessionId] = updated;
                await store.SaveTransferSessionAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var pendingBytes = unpersistedStreamingBytes.AddOrUpdate(session.SessionId, request.Length, (_, current) => current + request.Length);
                if (pendingBytes >= options.StreamingTransferCheckpointBytes)
                {
                    var updated = Clone(session, completedChunks: index.CompletedChunks, completedPaths: index.CompletedPaths);
                    await store.SaveTransferSessionAsync(updated, cancellationToken).ConfigureAwait(false);
                    sessionCache[updated.SessionId] = updated;
                    unpersistedStreamingBytes[updated.SessionId] = 0;
                }
            }
            return new TransferBinaryWriteResponse(receipt);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<TransferReadChunkResponse> ReadChunkAsync(TransferReadChunkRequest request, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(request.SessionId, TransferDirection.Download, cancellationToken);
        if (request.MaximumBytes > session.ChunkSize || request.MaximumBytes > options.MaximumTransferChunkBytes) throw new ArgumentOutOfRangeException(nameof(request));
        var entry = GetIndex(session).GetFileEntry(request.RelativePath);
        ValidateChunk(session, entry, request.Offset, Math.Min(request.MaximumBytes, checked((int)(entry.Length - request.Offset))));
        var source = paths.ResolveRelative(session.SourcePath, request.RelativePath);
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        stream.Position = request.Offset;
        var data = new byte[Math.Min(request.MaximumBytes, checked((int)(stream.Length - request.Offset)))];
        await stream.ReadExactlyAsync(data, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        return new TransferReadChunkResponse(new FileChunk(session.SessionId, request.RelativePath, request.Offset, data, request.Offset + data.Length >= stream.Length), hash);
    }

    public async Task<TransferBinaryReadResponse> ReadBinaryChunkAsync(
        TransferBinaryReadRequest request,
        Stream output,
        Func<TransferBinaryReadyResponse, Task> acknowledgeAsync,
        Func<Task> waitForClientReadyAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(acknowledgeAsync);
        ArgumentNullException.ThrowIfNull(waitForClientReadyAsync);
        var session = await GetActiveSessionAsync(request.SessionId, TransferDirection.Download, cancellationToken).ConfigureAwait(false);
        if (request.MaximumBytes > session.ChunkSize || request.MaximumBytes > options.MaximumTransferChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        var entry = GetIndex(session).GetFileEntry(request.RelativePath);
        var length = Math.Min(request.MaximumBytes, checked((int)(entry.Length - request.Offset)));
        ValidateChunk(session, entry, request.Offset, length);
        var source = paths.ResolveRelative(session.SourcePath, request.RelativePath);
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        stream.Position = request.Offset;
        await acknowledgeAsync(new TransferBinaryReadyResponse(length, false)).ConfigureAwait(false);
        await waitForClientReadyAsync().ConfigureAwait(false);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(length, 1), 1024 * 1024));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The source file ended before the declared chunk length.");
                hasher.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new TransferBinaryReadResponse(length, Convert.ToHexString(hasher.GetHashAndReset()));
    }

    public async Task<TransferCompleteResponse> CompleteAsync(TransferCompleteRequest request, CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetSessionAsync(request.SessionId, cancellationToken);
            TransferSessionIndex? uploadIndex = null;
            if (session.Direction == TransferDirection.Upload)
            {
                uploadIndex = GetIndex(session);
                if (session.Manifest.Entries.Count == 0) Directory.CreateDirectory(paths.Resolve(session.DestinationPath));
                foreach (var entry in session.Manifest.Entries.Where(IsFile))
                {
                    if (!uploadIndex.IsCompleted(entry)) throw new InvalidOperationException($"File '{entry.RelativePath}' is incomplete.");
                    var part = GetPartPath(session.SessionId, entry.RelativePath);
                    if (entry.Length == 0 && !File.Exists(part))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
                        await File.WriteAllBytesAsync(part, [], cancellationToken);
                    }
                    if (entry.Sha256 is not null)
                    {
                        var hash = await HashFileAsync(part, cancellationToken);
                        if (!string.Equals(hash, NormalizeHash(entry.Sha256), StringComparison.Ordinal)) throw new InvalidDataException($"Final SHA-256 mismatch for '{entry.RelativePath}'.");
                    }
                }
                foreach (var directory in session.Manifest.Entries.Where(e => !IsFile(e)).OrderBy(e => e.RelativePath.Length))
                    Directory.CreateDirectory(paths.ResolveRelative(session.DestinationPath, directory.RelativePath));
                foreach (var entry in session.Manifest.Entries.Where(IsFile))
                {
                    var destination = paths.ResolveRelative(session.DestinationPath, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(GetPartPath(session.SessionId, entry.RelativePath), destination, overwrite: true);
                }
            }
            var completed = Clone(
                session,
                state: TransferSessionState.Completed,
                completedChunks: uploadIndex?.CompletedChunks,
                completedPaths: uploadIndex?.CompletedPaths);
            await store.SaveTransferSessionAsync(completed, cancellationToken);
            sessionCache.TryRemove(session.SessionId, out _);
            sessionIndexes.TryRemove(session.SessionId, out _);
            unpersistedStreamingBytes.TryRemove(session.SessionId, out _);
            TryDeleteSessionFiles(session.SessionId);
            return new TransferCompleteResponse(completed);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<TransferStatusResponse> StatusAsync(TransferStatusRequest request, CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetSessionAsync(request.SessionId, cancellationToken);
            if (session.Direction == TransferDirection.Upload && session.StreamingIntegrity && session.State == TransferSessionState.Transferring)
            {
                var index = GetIndex(session);
                session = Clone(session, completedChunks: index.CompletedChunks, completedPaths: index.CompletedPaths);
            }
            return new TransferStatusResponse(session);
        }
        finally { mutationGate.Release(); }
    }

    public void Dispose()
    {
        sessionIndexes.Clear();
        mutationGate.Dispose();
    }

    private TransferSessionIndex GetIndex(TransferSessionSnapshot session) =>
        sessionIndexes.GetOrAdd(session.SessionId, _ => TransferSessionIndex.Create(session));

    private async Task<TransferSessionSnapshot> GetActiveSessionAsync(string id, TransferDirection direction, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(id, cancellationToken);
        if (session.Direction != direction || session.State != TransferSessionState.Transferring) throw new InvalidOperationException("The transfer session is not active for this operation.");
        return session;
    }

    private async Task<TransferSessionSnapshot> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        var session = sessionCache.TryGetValue(id, out var cached)
            ? cached
            : await store.GetTransferSessionAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"No transfer session exists with ID '{id}'.");
        sessionCache[id] = session;
        if (session.ExpiresAtUtc <= timeProvider.GetUtcNow() && session.State == TransferSessionState.Transferring)
        {
            session = Clone(session, state: TransferSessionState.Expired);
            await store.SaveTransferSessionAsync(session, cancellationToken);
            sessionCache.TryRemove(id, out _);
            sessionIndexes.TryRemove(id, out _);
            unpersistedStreamingBytes.TryRemove(id, out _);
            TryDeleteSessionFiles(id);
        }
        return session;
    }

    private async Task<FileManifest> BuildManifestAsync(string rootPath, bool includeHashes, CancellationToken cancellationToken)
    {
        var full = paths.Resolve(rootPath);
        var entries = new List<FileManifestEntry>();
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            entries.Add(new FileManifestEntry(string.Empty, info.Length, info.LastWriteTimeUtc,
                includeHashes ? await HashFileAsync(full, cancellationToken) : null, FileEntryKind.File));
        }
        else if (Directory.Exists(full))
        {
            foreach (var entry in paths.Enumerate(rootPath, recursive: true))
            {
                var relativePath = Path.GetRelativePath(full, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    entries.Add(new FileManifestEntry(relativePath, 0, Directory.GetLastWriteTimeUtc(entry), null, FileEntryKind.Directory));
                }
                else
                {
                    var info = new FileInfo(entry);
                    entries.Add(new FileManifestEntry(relativePath, info.Length, info.LastWriteTimeUtc,
                        includeHashes ? await HashFileAsync(entry, cancellationToken) : null, FileEntryKind.File));
                }
            }
        }
        else throw new FileNotFoundException("The file root does not exist.", rootPath);
        var manifest = new FileManifest(rootPath, entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray());
        EnsureQuota(manifest);
        return manifest;
    }

    private FileMetadata GetMetadata(string path)
    {
        if (File.Exists(path)) { var f = new FileInfo(path); return new FileMetadata(paths.ToDisplayPath(path), FileEntryKind.File, f.Length, f.LastWriteTimeUtc); }
        if (Directory.Exists(path)) { var d = new DirectoryInfo(path); return new FileMetadata(paths.ToDisplayPath(path), FileEntryKind.Directory, 0, d.LastWriteTimeUtc); }
        throw new FileNotFoundException("The path does not exist.", path);
    }

    private void ValidateManifest(FileManifest manifest, string destinationRoot, bool streamingIntegrity)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (!seen.Add(entry.RelativePath)) throw new InvalidDataException("Manifest paths must be unique.");
            paths.ResolveRelative(destinationRoot, entry.RelativePath);
            if (entry.Length < 0 || entry.Sha256 is not null && NormalizeHash(entry.Sha256).Length != 64 ||
                IsFile(entry) && entry.Sha256 is null && !streamingIntegrity)
                throw new InvalidDataException("Manifest entry is invalid.");
        }
    }

    private void EnsureQuota(FileManifest manifest)
    {
        var total = manifest.Entries.Where(IsFile).Sum(e => e.Length);
        if (total > options.TransferQuotaBytes) throw new ResourceExhaustedException("Transfer exceeds the configured byte quota.");
    }

    private static void ValidateChunk(TransferSessionSnapshot session, FileManifestEntry entry, long offset, int length)
    {
        if (offset < 0 || offset > entry.Length || length < 0 || length > session.ChunkSize || offset + length > entry.Length || offset % session.ChunkSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Chunk offset or length is invalid.");
    }

    private static TransferSessionSnapshot Clone(TransferSessionSnapshot s, TransferSessionState? state = null, IReadOnlyList<TransferChunkReceipt>? completedChunks = null, IReadOnlyList<string>? completedPaths = null) =>
        new(s.SessionId, s.Direction, state ?? s.State, s.SourcePath, s.DestinationPath, s.Manifest, s.ChunkSize, s.CreatedAtUtc, s.ExpiresAtUtc,
            completedPaths ?? s.CompletedRelativePaths, completedChunks ?? s.CompletedChunks, s.StreamingIntegrity);

    private static bool IsFile(FileManifestEntry entry) =>
        entry.Kind == FileEntryKind.File || entry.Kind is null && entry.Sha256 is not null;

    private string GetPartPath(string sessionId, string relativePath)
    {
        var name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath))) + ".part";
        return Path.Combine(store.DataRoot, "transfers", sessionId, name);
    }

    private void TryDeleteSessionFiles(string id)
    {
        var path = Path.Combine(store.DataRoot, "transfers", id);
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }

    private static string NormalizeHash(string hash) => hash.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed class TransferSessionIndex
    {
        private readonly Dictionary<string, FileManifestEntry> entries;
        private readonly Dictionary<ChunkKey, TransferChunkReceipt> receipts = [];
        private readonly List<TransferChunkReceipt> completedChunks = [];
        private readonly HashSet<string> completedPathSet = new(StringComparer.Ordinal);
        private readonly List<string> completedPaths = [];
        private readonly Dictionary<string, long> expectedChunkCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> validChunkCounts = new(StringComparer.Ordinal);
        private readonly int chunkSize;

        private TransferSessionIndex(TransferSessionSnapshot session)
        {
            chunkSize = session.ChunkSize;
            entries = session.Manifest.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
            foreach (var entry in entries.Values.Where(IsFile).Where(entry => entry.Length > 0))
            {
                expectedChunkCounts[entry.RelativePath] = (entry.Length - 1) / chunkSize + 1;
            }
        }

        public IReadOnlyList<TransferChunkReceipt> CompletedChunks => completedChunks;
        public IReadOnlyList<string> CompletedPaths => completedPaths;

        public static TransferSessionIndex Create(TransferSessionSnapshot session)
        {
            var index = new TransferSessionIndex(session);
            foreach (var receipt in session.CompletedChunks)
            {
                if (index.receipts.TryAdd(new ChunkKey(receipt.RelativePath, receipt.Offset), receipt))
                {
                    index.completedChunks.Add(receipt);
                    if (index.entries.TryGetValue(receipt.RelativePath, out var entry) && IsFile(entry) && index.HasExpectedLength(entry, receipt))
                    {
                        index.validChunkCounts[entry.RelativePath] = index.validChunkCounts.GetValueOrDefault(entry.RelativePath) + 1;
                    }
                }
            }

            // Reconstruct paths for snapshots written by older agents that only
            // persisted receipts or may have been written before this index
            // existed. Validate from receipts rather than trusting the persisted
            // path list. This is linear in the number of chunks rather than
            // rescanning every receipt for every manifest entry.
            foreach (var entry in session.Manifest.Entries.Where(IsFile))
            {
                if (entry.Length == 0 || index.validChunkCounts.GetValueOrDefault(entry.RelativePath) == index.expectedChunkCounts.GetValueOrDefault(entry.RelativePath))
                {
                    index.AddCompletedPath(entry.RelativePath);
                }
            }

            return index;
        }

        public bool TryGetFileEntry(string relativePath, out FileManifestEntry entry)
        {
            if (entries.TryGetValue(relativePath, out entry!) && IsFile(entry)) return true;
            entry = null!;
            return false;
        }

        public FileManifestEntry GetFileEntry(string relativePath) =>
            TryGetFileEntry(relativePath, out var entry)
                ? entry
                : throw new KeyNotFoundException($"No file entry exists for '{relativePath}'.");

        public bool TryGet(string relativePath, long offset, out TransferChunkReceipt receipt) =>
            receipts.TryGetValue(new ChunkKey(relativePath, offset), out receipt!);

        public void Add(TransferChunkReceipt receipt, FileManifestEntry entry)
        {
            if (!receipts.TryAdd(new ChunkKey(receipt.RelativePath, receipt.Offset), receipt)) return;
            completedChunks.Add(receipt);
            if (entry.Length == 0)
            {
                AddCompletedPath(entry.RelativePath);
            }
            else if (HasExpectedLength(entry, receipt) &&
                     validChunkCounts.GetValueOrDefault(entry.RelativePath) + 1 == expectedChunkCounts[entry.RelativePath])
            {
                validChunkCounts[entry.RelativePath] = expectedChunkCounts[entry.RelativePath];
                AddCompletedPath(entry.RelativePath);
            }
            else if (HasExpectedLength(entry, receipt))
            {
                validChunkCounts[entry.RelativePath] = validChunkCounts.GetValueOrDefault(entry.RelativePath) + 1;
            }
        }

        public bool IsCompleted(FileManifestEntry entry) =>
            entry.Length == 0 || completedPathSet.Contains(entry.RelativePath);

        private void AddCompletedPath(string relativePath)
        {
            if (completedPathSet.Add(relativePath)) completedPaths.Add(relativePath);
        }

        private bool HasExpectedLength(FileManifestEntry entry, TransferChunkReceipt receipt)
        {
            if (entry.Length == 0 || receipt.Offset < 0 || receipt.Offset >= entry.Length || receipt.Offset % chunkSize != 0) return false;
            var expectedLength = checked((int)Math.Min(chunkSize, entry.Length - receipt.Offset));
            return receipt.Length == expectedLength;
        }

        private readonly record struct ChunkKey(string RelativePath, long Offset);
    }
}
