using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rc.Contracts;

/// <summary>
/// Canonical signing and verification for privileged control requests. The agent
/// certificate pin protects the transport; this proof binds every request to the
/// controller identity established during J-PAKE pairing.
/// </summary>
public static class ControlRequestAuthentication
{
    private static readonly byte[] SessionAuthenticationDomain = "RemoteController/session_authenticate/v1"u8.ToArray();
    private static readonly byte[] ExecuteOnceDomain = "RemoteController/exec_once/v1"u8.ToArray();
    private static readonly byte[] JobStartDomain = "RemoteController/job_start/v1"u8.ToArray();
    private static readonly byte[] JobStatusDomain = "RemoteController/job_status/v1"u8.ToArray();
    private static readonly byte[] JobListDomain = "RemoteController/job_list/v1"u8.ToArray();
    private static readonly byte[] JobLogsDomain = "RemoteController/job_logs/v1"u8.ToArray();
    private static readonly byte[] JobInputDomain = "RemoteController/job_input/v1"u8.ToArray();
    private static readonly byte[] JobCloseInputDomain = "RemoteController/job_close_input/v1"u8.ToArray();
    private static readonly byte[] JobCancelDomain = "RemoteController/job_cancel/v1"u8.ToArray();
    private static readonly byte[] JobWaitDomain = "RemoteController/job_wait/v1"u8.ToArray();
    private static readonly byte[] JobResizeDomain = "RemoteController/job_resize/v1"u8.ToArray();
    private static readonly byte[] UpdateStartDomain = "RemoteController/update_start/v1"u8.ToArray();
    private static readonly byte[] UpdateWriteChunkDomain = "RemoteController/update_write_chunk/v1"u8.ToArray();
    private static readonly byte[] UpdateCompleteDomain = "RemoteController/update_complete/v1"u8.ToArray();
    private static readonly byte[] UpdateWriteBinaryDomain = "RemoteController/update_write_binary/v1"u8.ToArray();
    private static readonly byte[] UpdateStatusDomain = "RemoteController/update_status/v1"u8.ToArray();

    public static byte[] SignSessionAuthentication(
        string agentDeviceId,
        string controllerId,
        Guid sessionId,
        byte[] challenge,
        DateTimeOffset expiresAtUtc,
        ECDsa privateKey) =>
        Sign(SessionAuthenticationDomain, agentDeviceId, controllerId, new SessionAuthenticationPayload(sessionId, challenge, expiresAtUtc), privateKey);

    public static bool VerifySessionAuthentication(
        string agentDeviceId,
        string controllerId,
        Guid sessionId,
        byte[] challenge,
        DateTimeOffset expiresAtUtc,
        ReadOnlySpan<byte> signature,
        ECDsa publicKey) =>
        Verify(SessionAuthenticationDomain, agentDeviceId, controllerId, new SessionAuthenticationPayload(sessionId, challenge, expiresAtUtc), signature, publicKey);
    public static byte[] SignExecuteOnce(string agentDeviceId, string controllerId, ExecRequest execution, ECDsa privateKey) =>
        Sign(ExecuteOnceDomain, agentDeviceId, controllerId, execution, privateKey);

    public static bool VerifyExecuteOnce(string agentDeviceId, string controllerId, ExecRequest execution, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(ExecuteOnceDomain, agentDeviceId, controllerId, execution, signature, publicKey);

    public static byte[] SignJobStart(string agentDeviceId, string controllerId, ExecRequest execution, ECDsa privateKey) =>
        Sign(JobStartDomain, agentDeviceId, controllerId, execution, privateKey);

    public static bool VerifyJobStart(string agentDeviceId, string controllerId, ExecRequest execution, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobStartDomain, agentDeviceId, controllerId, execution, signature, publicKey);

    public static byte[] SignJobStatus(string agentDeviceId, string controllerId, string jobId, ECDsa privateKey) =>
        Sign(JobStatusDomain, agentDeviceId, controllerId, new JobIdPayload(jobId), privateKey);

    public static bool VerifyJobStatus(string agentDeviceId, string controllerId, string jobId, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobStatusDomain, agentDeviceId, controllerId, new JobIdPayload(jobId), signature, publicKey);

    public static byte[] SignJobList(string agentDeviceId, string controllerId, JobState? state, ECDsa privateKey) =>
        Sign(JobListDomain, agentDeviceId, controllerId, new JobListPayload(state), privateKey);

    public static bool VerifyJobList(string agentDeviceId, string controllerId, JobState? state, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobListDomain, agentDeviceId, controllerId, new JobListPayload(state), signature, publicKey);

    public static byte[] SignJobLogs(string agentDeviceId, string controllerId, string jobId, JobOutputKind stream, long afterOffset, int maximumBytes, ECDsa privateKey) =>
        Sign(JobLogsDomain, agentDeviceId, controllerId, new JobLogsPayload(jobId, stream, afterOffset, maximumBytes), privateKey);

    public static bool VerifyJobLogs(string agentDeviceId, string controllerId, string jobId, JobOutputKind stream, long afterOffset, int maximumBytes, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobLogsDomain, agentDeviceId, controllerId, new JobLogsPayload(jobId, stream, afterOffset, maximumBytes), signature, publicKey);

    public static byte[] SignJobInput(string agentDeviceId, string controllerId, string jobId, byte[] data, ECDsa privateKey) =>
        Sign(JobInputDomain, agentDeviceId, controllerId, new JobInputPayload(jobId, data), privateKey);

    public static bool VerifyJobInput(string agentDeviceId, string controllerId, string jobId, byte[] data, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobInputDomain, agentDeviceId, controllerId, new JobInputPayload(jobId, data), signature, publicKey);

    public static byte[] SignJobCloseInput(string agentDeviceId, string controllerId, string jobId, ECDsa privateKey) =>
        Sign(JobCloseInputDomain, agentDeviceId, controllerId, new JobIdPayload(jobId), privateKey);

    public static bool VerifyJobCloseInput(string agentDeviceId, string controllerId, string jobId, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobCloseInputDomain, agentDeviceId, controllerId, new JobIdPayload(jobId), signature, publicKey);

    public static byte[] SignJobCancel(string agentDeviceId, string controllerId, string jobId, ECDsa privateKey) =>
        Sign(JobCancelDomain, agentDeviceId, controllerId, new JobIdPayload(jobId), privateKey);

    public static bool VerifyJobCancel(string agentDeviceId, string controllerId, string jobId, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobCancelDomain, agentDeviceId, controllerId, new JobIdPayload(jobId), signature, publicKey);

    public static byte[] SignJobWait(string agentDeviceId, string controllerId, string jobId, TimeSpan? timeout, ECDsa privateKey) =>
        Sign(JobWaitDomain, agentDeviceId, controllerId, new JobWaitPayload(jobId, timeout), privateKey);

    public static bool VerifyJobWait(string agentDeviceId, string controllerId, string jobId, TimeSpan? timeout, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobWaitDomain, agentDeviceId, controllerId, new JobWaitPayload(jobId, timeout), signature, publicKey);
    public static byte[] SignJobResize(string agentDeviceId, string controllerId, string jobId, int columns, int rows, ECDsa privateKey) =>
        Sign(JobResizeDomain, agentDeviceId, controllerId, new JobResizePayload(jobId, columns, rows), privateKey);

    public static bool VerifyJobResize(string agentDeviceId, string controllerId, string jobId, int columns, int rows, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(JobResizeDomain, agentDeviceId, controllerId, new JobResizePayload(jobId, columns, rows), signature, publicKey);

    public static byte[] SignUpdateStart(string agentDeviceId, string controllerId, UpdateStartRequest request, ECDsa privateKey) =>
        Sign(UpdateStartDomain, agentDeviceId, controllerId, request, privateKey);

    public static bool VerifyUpdateStart(string agentDeviceId, string controllerId, UpdateStartRequest request, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(UpdateStartDomain, agentDeviceId, controllerId, request, signature, publicKey);

    public static byte[] SignUpdateWriteChunk(string agentDeviceId, string controllerId, UpdateWriteChunkRequest request, ECDsa privateKey) =>
        Sign(UpdateWriteChunkDomain, agentDeviceId, controllerId, new UpdateChunkPayload(request.UpdateId, request.RelativePath, request.Offset, request.Sha256), privateKey);

    public static bool VerifyUpdateWriteChunk(string agentDeviceId, string controllerId, UpdateWriteChunkRequest request, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(UpdateWriteChunkDomain, agentDeviceId, controllerId, new UpdateChunkPayload(request.UpdateId, request.RelativePath, request.Offset, request.Sha256), signature, publicKey);

    public static byte[] SignUpdateWriteBinary(string agentDeviceId, string controllerId, UpdateBinaryWriteRequest request, ECDsa privateKey) =>
        Sign(UpdateWriteBinaryDomain, agentDeviceId, controllerId, request, privateKey);

    public static bool VerifyUpdateWriteBinary(string agentDeviceId, string controllerId, UpdateBinaryWriteRequest request, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(UpdateWriteBinaryDomain, agentDeviceId, controllerId, request, signature, publicKey);

    public static byte[] SignUpdateComplete(string agentDeviceId, string controllerId, UpdateCompleteRequest request, ECDsa privateKey) =>
        Sign(UpdateCompleteDomain, agentDeviceId, controllerId, request, privateKey);

    public static bool VerifyUpdateComplete(string agentDeviceId, string controllerId, UpdateCompleteRequest request, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(UpdateCompleteDomain, agentDeviceId, controllerId, request, signature, publicKey);

    public static byte[] SignUpdateStatus(string agentDeviceId, string controllerId, UpdateStatusRequest request, ECDsa privateKey) =>
        Sign(UpdateStatusDomain, agentDeviceId, controllerId, request, privateKey);

    public static bool VerifyUpdateStatus(string agentDeviceId, string controllerId, UpdateStatusRequest request, ReadOnlySpan<byte> signature, ECDsa publicKey) =>
        Verify(UpdateStatusDomain, agentDeviceId, controllerId, request, signature, publicKey);
    private static byte[] Sign<T>(byte[] domain, string agentDeviceId, string controllerId, T payload, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var payloadHash = ComputePayloadHash(domain, agentDeviceId, controllerId, payload);
        try
        {
            return privateKey.SignHash(payloadHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadHash);
        }
    }

    private static bool Verify<T>(byte[] domain, string agentDeviceId, string controllerId, T payload, ReadOnlySpan<byte> signature, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var payloadHash = ComputePayloadHash(domain, agentDeviceId, controllerId, payload);
        try
        {
            return publicKey.VerifyHash(payloadHash, signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadHash);
        }
    }

    private static byte[] ComputePayloadHash<T>(byte[] domain, string agentDeviceId, string controllerId, T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        ArgumentNullException.ThrowIfNull(payload);

        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options);
        try
        {
            using var stream = new MemoryStream();
            WriteField(stream, domain);
            WriteField(stream, Encoding.UTF8.GetBytes(agentDeviceId));
            WriteField(stream, Encoding.UTF8.GetBytes(controllerId));
            WriteField(stream, payloadJson);
            return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadJson);
        }
    }

    private static void WriteField(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private sealed record SessionAuthenticationPayload(Guid SessionId, byte[] Challenge, DateTimeOffset ExpiresAtUtc);

    private sealed record JobIdPayload(string JobId);

    private sealed record JobListPayload(JobState? State);
    private sealed record JobLogsPayload(string JobId, JobOutputKind Stream, long AfterOffset, int MaximumBytes);
    private sealed record JobInputPayload(string JobId, byte[] Data);
    private sealed record JobWaitPayload(string JobId, TimeSpan? Timeout);
    private sealed record JobResizePayload(string JobId, int Columns, int Rows);
    private sealed record UpdateChunkPayload(Guid UpdateId, string RelativePath, long Offset, string Sha256);
}
