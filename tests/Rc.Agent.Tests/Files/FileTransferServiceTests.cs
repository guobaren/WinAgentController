using System.Security.Cryptography;
using System.Text;
using Rc.Agent.Configuration;
using Rc.Agent.Files;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Files;

public sealed class FileTransferServiceTests
{
    [Fact]
    public async Task BasicOperationsAreRootedAndWritesAreAtomic()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        using var service = CreateService(store, directory.Path);

        await service.WriteAsync(new FileWriteRequest("docs/a.txt", Encoding.UTF8.GetBytes("hello"), false));
        var read = await service.ReadAsync(new FileReadRequest("docs/a.txt", 1, 3));
        var stat = await service.StatAsync(new FileStatRequest("docs/a.txt"));
        var list = await service.ListAsync(new FileListRequest("docs"));

        Assert.Equal("ell", Encoding.UTF8.GetString(read.Chunk.Data));
        Assert.False(read.Chunk.IsFinal);
        Assert.Equal(5, stat.Entry.Length);
        Assert.Single(list.Entries);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StatAsync(new FileStatRequest("..\\outside.txt")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StatAsync(new FileStatRequest("CON")));
        await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(new FileWriteRequest("docs/a.txt", [1], false)));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(directory.Path, "docs", "a.txt")));
    }

    [Fact]
    public async Task UploadPersistsReceiptsResumesAndVerifiesFinalHash()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var data = Encoding.UTF8.GetBytes("abcdefgh");
        var manifest = new FileManifest("local", [new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(data)))]);
        string sessionId;
        using (var first = CreateService(store, directory.Path))
        {
            var started = await first.StartTransferAsync(new TransferStartRequest(TransferDirection.Upload, "local", "uploaded.bin", manifest, 4));
            sessionId = started.Session.SessionId;
            var chunk = new FileChunk(sessionId, string.Empty, 0, data[..4], false);
            await first.WriteChunkAsync(new TransferWriteChunkRequest(chunk, Convert.ToHexString(SHA256.HashData(data[..4]))));
        }
        using (var resumed = CreateService(store, directory.Path))
        {
            var status = await resumed.StatusAsync(new TransferStatusRequest(sessionId));
            Assert.Single(status.Session.CompletedChunks);
            var bad = new FileChunk(sessionId, string.Empty, 4, data[4..], true);
            await Assert.ThrowsAsync<InvalidDataException>(() => resumed.WriteChunkAsync(new TransferWriteChunkRequest(bad, new string('0', 64))));
            await resumed.WriteChunkAsync(new TransferWriteChunkRequest(bad, Convert.ToHexString(SHA256.HashData(data[4..]))));
            var completed = await resumed.CompleteAsync(new TransferCompleteRequest(sessionId));
            Assert.Equal(TransferSessionState.Completed, completed.Session.State);
        }
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "uploaded.bin")));
    }

    [Fact]
    public async Task DownloadReturnsVerifiedRangesAndQuotaRejectsOversizedManifest()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "source.txt"), "download");
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        using var service = CreateService(store, directory.Path, quota: 8);
        var started = await service.StartTransferAsync(new TransferStartRequest(TransferDirection.Download, "source.txt", "local", new FileManifest("x", []), 4));
        var first = await service.ReadChunkAsync(new TransferReadChunkRequest(started.Session.SessionId, string.Empty, 0, 4));
        var second = await service.ReadChunkAsync(new TransferReadChunkRequest(started.Session.SessionId, string.Empty, 4, 4));
        Assert.Equal("down", Encoding.UTF8.GetString(first.Chunk.Data));
        Assert.Equal("load", Encoding.UTF8.GetString(second.Chunk.Data));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.Chunk.Data)), first.ChunkSha256);
        var tooLarge = new FileManifest("x", [new FileManifestEntry("x", 9, DateTimeOffset.UtcNow, new string('A', 64))]);
        await Assert.ThrowsAsync<ResourceExhaustedException>(() => service.StartTransferAsync(new TransferStartRequest(TransferDirection.Upload, "x", "dest", tooLarge, 4)));
    }

    [Fact]
    public async Task DirectoryUploadPreservesFilesEmptyDirectoriesAndDuplicateChunksAreIdempotent()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var firstData = Encoding.UTF8.GetBytes("alpha");
        var secondData = Encoding.UTF8.GetBytes("beta");
        var manifest = new FileManifest("local", [
            new FileManifestEntry("empty", 0, DateTimeOffset.UtcNow, null),
            new FileManifestEntry("nested", 0, DateTimeOffset.UtcNow, null),
            new FileManifestEntry("nested/a.txt", firstData.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(firstData))),
            new FileManifestEntry("b.txt", secondData.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(secondData))),
        ]);
        using var service = CreateService(store, directory.Path);
        var session = (await service.StartTransferAsync(new TransferStartRequest(TransferDirection.Upload, "local", "tree", manifest, 4))).Session;

        var firstChunk = new FileChunk(session.SessionId, "nested/a.txt", 0, firstData[..4], false);
        var firstHash = Convert.ToHexString(SHA256.HashData(firstData[..4]));
        await service.WriteChunkAsync(new TransferWriteChunkRequest(firstChunk, firstHash));
        var duplicate = await service.WriteChunkAsync(new TransferWriteChunkRequest(firstChunk, firstHash));
        Assert.Single(duplicate.Session.CompletedChunks);
        await service.WriteChunkAsync(new TransferWriteChunkRequest(
            new FileChunk(session.SessionId, "nested/a.txt", 4, firstData[4..], true),
            Convert.ToHexString(SHA256.HashData(firstData[4..]))));
        await service.WriteChunkAsync(new TransferWriteChunkRequest(
            new FileChunk(session.SessionId, "b.txt", 0, secondData, true),
            Convert.ToHexString(SHA256.HashData(secondData))));

        var completed = await service.CompleteAsync(new TransferCompleteRequest(session.SessionId));

        Assert.Equal(TransferSessionState.Completed, completed.Session.State);
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "tree", "empty")));
        Assert.Equal(firstData, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "tree", "nested", "a.txt")));
        Assert.Equal(secondData, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "tree", "b.txt")));
    }

    [Fact]
    public async Task CompletionRejectsTamperedPersistedChunkData()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var data = Encoding.UTF8.GetBytes("data");
        var manifest = new FileManifest("local", [new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(data)))]);
        using var service = CreateService(store, directory.Path);
        var session = (await service.StartTransferAsync(new TransferStartRequest(TransferDirection.Upload, "local", "tampered.bin", manifest, 4))).Session;
        await service.WriteChunkAsync(new TransferWriteChunkRequest(
            new FileChunk(session.SessionId, string.Empty, 0, data, true),
            Convert.ToHexString(SHA256.HashData(data))));
        var transferDirectory = Path.Combine(store.DataRoot, "transfers", session.SessionId);
        var part = Assert.Single(Directory.GetFiles(transferDirectory, "*.part"));
        await File.WriteAllBytesAsync(part, Encoding.UTF8.GetBytes("evil"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CompleteAsync(new TransferCompleteRequest(session.SessionId)));
        Assert.False(File.Exists(Path.Combine(directory.Path, "tampered.bin")));
    }

    [Fact]
    public async Task StreamingIntegrityUploadDoesNotRequirePrecomputedFileOrChunkHashes()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var data = Encoding.UTF8.GetBytes("streamed-without-a-pre-scan");
        var manifest = new FileManifest("local", [
            new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, null, FileEntryKind.File),
        ]);
        using var service = CreateService(store, directory.Path, quota: 1024, maximumChunk: data.Length);
        var session = (await service.StartTransferAsync(new TransferStartRequest(
            TransferDirection.Upload, "local", "streamed.bin", manifest, data.Length, StreamingIntegrity: true))).Session;
        TransferBinaryReadyResponse? ready = null;

        var written = await service.WriteBinaryChunkAsync(
            new TransferBinaryWriteRequest(session.SessionId, string.Empty, 0, data.Length, null),
            new MemoryStream(data, writable: false),
            response => { ready = response; return Task.CompletedTask; });
        var completed = await service.CompleteAsync(new TransferCompleteRequest(session.SessionId));

        Assert.Equal(data.Length, ready?.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), written.Receipt.Sha256);
        Assert.Equal(TransferSessionState.Completed, completed.Session.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "streamed.bin")));
    }

    [Fact]
    public async Task InterruptedStreamingChunkIsRetransmittedAndRemainsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var data = Encoding.UTF8.GetBytes("abcdefgh");
        var manifest = new FileManifest("local", [
            new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, null, FileEntryKind.File),
        ]);
        using var service = CreateService(store, directory.Path, maximumChunk: data.Length);
        var session = (await service.StartTransferAsync(new TransferStartRequest(
            TransferDirection.Upload, "local", "recovered.bin", manifest, data.Length, StreamingIntegrity: true))).Session;
        var request = new TransferBinaryWriteRequest(session.SessionId, string.Empty, 0, data.Length, null);

        await Assert.ThrowsAsync<IOException>(() => service.WriteBinaryChunkAsync(
            request,
            new InterruptingReadStream(data, interruptAfterBytes: 3),
            _ => Task.CompletedTask));
        Assert.Empty((await service.StatusAsync(new TransferStatusRequest(session.SessionId))).Session.CompletedChunks);

        var recovered = await service.WriteBinaryChunkAsync(request, new MemoryStream(data), _ => Task.CompletedTask);
        TransferBinaryReadyResponse? repeatedReady = null;
        var repeated = await service.WriteBinaryChunkAsync(
            request,
            Stream.Null,
            ready => { repeatedReady = ready; return Task.CompletedTask; });
        await service.CompleteAsync(new TransferCompleteRequest(session.SessionId));

        Assert.Equal(Hash(data), recovered.Receipt.Sha256);
        Assert.True(repeatedReady?.AlreadyCompleted);
        Assert.Equal(recovered.Receipt, repeated.Receipt);
        var finalPath = Path.Combine(directory.Path, "recovered.bin");
        Assert.Equal(data, await File.ReadAllBytesAsync(finalPath));
        Assert.Equal(Hash(data), await HashFileAsync(finalPath));
    }

    [Fact]
    public async Task StreamingDirectoryUploadIndexesManySingleChunkFiles()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        const int fileCount = 2048;
        var manifest = new FileManifest("local", Enumerable.Range(0, fileCount)
            .Select(index => new FileManifestEntry($"files/{index:D4}.bin", 1, DateTimeOffset.UtcNow, null, FileEntryKind.File))
            .ToArray());
        using var service = CreateService(store, directory.Path, quota: fileCount, maximumChunk: 1);
        var session = (await service.StartTransferAsync(new TransferStartRequest(
            TransferDirection.Upload, "local", "many", manifest, 1, StreamingIntegrity: true))).Session;

        for (var index = 0; index < fileCount; index++)
        {
            await service.WriteBinaryChunkAsync(
                new TransferBinaryWriteRequest(session.SessionId, $"files/{index:D4}.bin", 0, 1, null),
                new MemoryStream([(byte)(index % 251)]),
                _ => Task.CompletedTask);
        }

        var status = await service.StatusAsync(new TransferStatusRequest(session.SessionId));
        Assert.Equal(fileCount, status.Session.CompletedChunks.Count);
        Assert.Equal(fileCount, status.Session.CompletedRelativePaths.Count);

        var completed = await service.CompleteAsync(new TransferCompleteRequest(session.SessionId));

        Assert.Equal(fileCount, completed.Session.CompletedChunks.Count);
        Assert.Equal(fileCount, completed.Session.CompletedRelativePaths.Count);
        Assert.Equal(new byte[] { 0 }, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "many", "files", "0000.bin")));
        Assert.Equal(new byte[] { (byte)((fileCount - 1) % 251) }, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "many", "files", "2047.bin")));
    }

    [Fact]
    public async Task StreamingCheckpointPersistsCompletedChunksAtConfiguredBoundary()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var data = Encoding.UTF8.GetBytes("abcdefghijkl");
        var manifest = new FileManifest("local", [
            new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, null, FileEntryKind.File),
        ]);
        string sessionId;
        var options = new AgentOptions
        {
            FileRoot = directory.Path,
            TransferQuotaBytes = 1024,
            MaximumTransferChunkBytes = 4,
            MaximumAtomicWriteBytes = 1024,
            StreamingTransferCheckpointBytes = 8,
        };
        using (var first = new FileTransferService(store, options))
        {
            var session = (await first.StartTransferAsync(new TransferStartRequest(
                TransferDirection.Upload, "local", "checkpoint.bin", manifest, 4, StreamingIntegrity: true))).Session;
            sessionId = session.SessionId;
            await first.WriteBinaryChunkAsync(
                new TransferBinaryWriteRequest(sessionId, string.Empty, 0, 4, null),
                new MemoryStream(data[..4]),
                _ => Task.CompletedTask);
        }
        using (var beforeCheckpoint = new FileTransferService(store, options))
        {
            Assert.Empty((await beforeCheckpoint.StatusAsync(new TransferStatusRequest(sessionId))).Session.CompletedChunks);
            await beforeCheckpoint.WriteBinaryChunkAsync(
                new TransferBinaryWriteRequest(sessionId, string.Empty, 0, 4, null),
                new MemoryStream(data[..4]),
                _ => Task.CompletedTask);
            await beforeCheckpoint.WriteBinaryChunkAsync(
                new TransferBinaryWriteRequest(sessionId, string.Empty, 4, 4, null),
                new MemoryStream(data[4..8]),
                _ => Task.CompletedTask);
        }
        using (var afterCheckpoint = new FileTransferService(store, options))
        {
            Assert.Equal(2, (await afterCheckpoint.StatusAsync(new TransferStatusRequest(sessionId))).Session.CompletedChunks.Count);
            await afterCheckpoint.WriteBinaryChunkAsync(
                new TransferBinaryWriteRequest(sessionId, string.Empty, 8, 4, null),
                new MemoryStream(data[8..]),
                _ => Task.CompletedTask);
            await afterCheckpoint.CompleteAsync(new TransferCompleteRequest(sessionId));
        }

        var finalPath = Path.Combine(directory.Path, "checkpoint.bin");
        Assert.Equal(data, await File.ReadAllBytesAsync(finalPath));
        Assert.Equal(Hash(data), await HashFileAsync(finalPath));
    }

    [Fact]
    public async Task DefaultStreamingCheckpointPersistsAtActualTwoHundredFiftySixMiB()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        const int chunkSize = 64 * 1024 * 1024;
        const long totalBytes = 256L * 1024 * 1024;
        var manifest = new FileManifest("local", [
            new FileManifestEntry(string.Empty, totalBytes, DateTimeOffset.UtcNow, null, FileEntryKind.File),
        ]);
        var options = new AgentOptions { FileRoot = directory.Path };
        string sessionId;
        using (var first = new FileTransferService(store, options))
        {
            var session = (await first.StartTransferAsync(new TransferStartRequest(
                TransferDirection.Upload, "local", "checkpoint-256mib.bin", manifest, chunkSize, StreamingIntegrity: true))).Session;
            sessionId = session.SessionId;
            for (var offset = 0L; offset < totalBytes; offset += chunkSize)
            {
                await first.WriteBinaryChunkAsync(
                    new TransferBinaryWriteRequest(sessionId, string.Empty, offset, chunkSize, null),
                    new FixedLengthZeroStream(chunkSize),
                    _ => Task.CompletedTask);
            }
        }
        using (var resumed = new FileTransferService(store, options))
        {
            var status = await resumed.StatusAsync(new TransferStatusRequest(sessionId));
            Assert.Equal(4, status.Session.CompletedChunks.Count);
            TransferBinaryReadyResponse? ready = null;
            await resumed.WriteBinaryChunkAsync(
                new TransferBinaryWriteRequest(sessionId, string.Empty, 0, chunkSize, status.Session.CompletedChunks[0].Sha256),
                Stream.Null,
                response => { ready = response; return Task.CompletedTask; });
            Assert.True(ready?.AlreadyCompleted);
            await resumed.CompleteAsync(new TransferCompleteRequest(sessionId));
        }

        var finalPath = Path.Combine(directory.Path, "checkpoint-256mib.bin");
        Assert.Equal(totalBytes, new FileInfo(finalPath).Length);
        await using var expected = new FixedLengthZeroStream(totalBytes);
        Assert.Equal(
            Convert.ToHexString(await SHA256.HashDataAsync(expected)),
            await HashFileAsync(finalPath));
    }

    [Fact]
    public async Task ExpiredSessionCannotResumeAndRemovesPartialData()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        var data = Encoding.UTF8.GetBytes("abcdefgh");
        var manifest = new FileManifest("local", [new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(data)))]);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var service = CreateService(store, directory.Path, lifetime: TimeSpan.FromMinutes(1), timeProvider: timeProvider);
        var session = (await service.StartTransferAsync(new TransferStartRequest(TransferDirection.Upload, "local", "expired.bin", manifest, 4))).Session;
        await service.WriteChunkAsync(new TransferWriteChunkRequest(
            new FileChunk(session.SessionId, string.Empty, 0, data[..4], false),
            Convert.ToHexString(SHA256.HashData(data[..4]))));
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        var status = await service.StatusAsync(new TransferStatusRequest(session.SessionId));

        Assert.Equal(TransferSessionState.Expired, status.Session.State);
        Assert.False(Directory.Exists(Path.Combine(store.DataRoot, "transfers", session.SessionId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteChunkAsync(new TransferWriteChunkRequest(
            new FileChunk(session.SessionId, string.Empty, 4, data[4..], true),
            Convert.ToHexString(SHA256.HashData(data[4..])))));
    }

    [Fact]
    public async Task ConfiguredAtomicWriteAndChunkLimitsAreEnforced()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        using var service = CreateService(store, directory.Path, atomicWriteLimit: 3);

        await Assert.ThrowsAsync<ResourceExhaustedException>(() => service.WriteAsync(new FileWriteRequest("too-large.bin", [1, 2, 3, 4], true)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync(new FileReadRequest("missing.bin", 0, 5)));
        var manifest = new FileManifest("local", [new FileManifestEntry(string.Empty, 1, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData([1]))) ]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.StartTransferAsync(new TransferStartRequest(TransferDirection.Upload, "local", "dest.bin", manifest, 5)));
    }

    [Fact]
    public async Task DefaultLimitsAllowOneGiBTransferWithSixtyFourMiBChunks()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(Path.Combine(directory.Path, "state"));
        await store.InitializeAsync();
        using var service = new FileTransferService(store, new AgentOptions
        {
            FileRoot = directory.Path,
        });
        var oneGiB = 1024L * 1024 * 1024;
        var manifest = new FileManifest("local", [
            new FileManifestEntry(string.Empty, oneGiB, DateTimeOffset.UtcNow, new string('A', 64)),
        ]);

        var started = await service.StartTransferAsync(new TransferStartRequest(
            TransferDirection.Upload,
            "local",
            "large.bin",
            manifest,
            64 * 1024 * 1024));

        Assert.Equal(oneGiB, started.Session.Manifest.Entries.Single().Length);
        Assert.Equal(64 * 1024 * 1024, started.Session.ChunkSize);
    }

    private static FileTransferService CreateService(AgentStateStore store, string root, long quota = 1024, int atomicWriteLimit = 1024, TimeSpan? lifetime = null, TimeProvider? timeProvider = null, int maximumChunk = 4) => new(store, new AgentOptions
    {
        FileRoot = root,
        TransferQuotaBytes = quota,
        MaximumTransferChunkBytes = maximumChunk,
        MaximumAtomicWriteBytes = atomicWriteLimit,
        TransferSessionLifetime = lifetime ?? TimeSpan.FromHours(1),
    }, timeProvider);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtc = utcNow;

        public override DateTimeOffset GetUtcNow() => currentUtc;

        public void Advance(TimeSpan duration) => currentUtc = currentUtc.Add(duration);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
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

    private sealed class FixedLengthZeroStream(long length) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var actual = checked((int)Math.Min(count, length - position));
            if (actual <= 0) return 0;
            Array.Clear(buffer, offset, actual);
            position += actual;
            return actual;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = checked((int)Math.Min(buffer.Length, length - position));
            if (actual <= 0) return ValueTask.FromResult(0);
            buffer.Span[..actual].Clear();
            position += actual;
            return ValueTask.FromResult(actual);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
