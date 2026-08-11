using System.Collections.ObjectModel;

namespace Rc.Contracts;

public sealed class UpdatePackageManifest
{
    public UpdatePackageManifest(string product, string version, IReadOnlyList<UpdatePackageFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(files);
        Product = product;
        Version = version;
        Files = new ReadOnlyCollection<UpdatePackageFile>(files.ToArray());
    }

    public string Product { get; }

    public string Version { get; }

    public IReadOnlyList<UpdatePackageFile> Files { get; }
}

public sealed record UpdatePackageFile(string RelativePath, long Length, string Sha256);

public sealed record UpdateStartRequest(Guid UpdateId, UpdatePackageManifest Manifest);

public sealed class UpdateWriteChunkRequest
{
    private readonly byte[] data;

    public UpdateWriteChunkRequest(Guid updateId, string relativePath, long offset, byte[] data, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        UpdateId = updateId;
        RelativePath = relativePath;
        Offset = offset;
        this.data = data.ToArray();
        Sha256 = sha256;
    }

    public Guid UpdateId { get; }

    public string RelativePath { get; }

    public long Offset { get; }

    public byte[] Data => data.ToArray();

    public string Sha256 { get; }
}

public sealed record UpdateBinaryWriteRequest(
    Guid UpdateId,
    string RelativePath,
    long Offset,
    int Length,
    string Sha256);

public sealed record UpdateBinaryReadyResponse(int Length, bool AlreadyCompleted);

public sealed record UpdateBinaryWriteResponse(UpdateStatusResponse Status, string Sha256);

public sealed record UpdateCompleteRequest(Guid UpdateId);

public sealed record UpdateStatusRequest(Guid UpdateId);

public enum UpdateState
{
    Receiving,
    Applying,
    Succeeded,
    Failed,
}

public sealed record UpdateStatusResponse(
    Guid UpdateId,
    UpdateState State,
    string Version,
    long ReceivedBytes,
    long TotalBytes,
    string? InstallationJobId,
    string? FailureMessage,
    DateTimeOffset UpdatedAtUtc);

public interface IUpdateServiceV1
{
    ValueTask<UpdateStatusResponse> StartAsync(UpdateStartRequest request, CancellationToken cancellationToken = default);

    ValueTask<UpdateStatusResponse> WriteChunkAsync(UpdateWriteChunkRequest request, CancellationToken cancellationToken = default);

    ValueTask<UpdateStatusResponse> CompleteAsync(UpdateCompleteRequest request, CancellationToken cancellationToken = default);

    ValueTask<UpdateStatusResponse> GetStatusAsync(UpdateStatusRequest request, CancellationToken cancellationToken = default);
}
