#pragma warning disable CA1822 // JSON protocol discriminator properties must remain instance members.
using Rc.Agent.Security;

namespace Rc.Contracts;

/// <summary>
/// The first request permitted on an unauthenticated TLS control connection.
/// It intentionally exposes only public identity metadata and never grants command access.
/// </summary>
public sealed record ControlHelloRequest(int ProtocolVersion)
{
    public string Kind => ControlMessageKinds.Hello;
}

public sealed record ControlHelloResponse(
    int ProtocolVersion,
    string DeviceId,
    string CertificateSha256Fingerprint,
    bool HasPairedController)
{
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public int? MaximumBinaryTransferChunkBytes { get; init; }
}

public static class ControlCapabilities
{
    public const string BinaryTransferV1 = "binary-transfer-v1";
    public const string StreamingIntegrityV2 = "streaming-integrity-v2";
}

public static class ControlMessageKinds
{
    public const string Hello = "hello";
    public const string PairStart = "pair_start";
    public const string PairRound1 = "pair_round1";
    public const string PairRound2 = "pair_round2";
    public const string PairComplete = "pair_complete";
    public const string SessionStart = "session_start";
    public const string SessionAuthenticate = "session_authenticate";
    public const string ExecOnce = "exec_once";
    public const string JobStart = "job_start";
    public const string JobStatus = "job_status";
    public const string JobList = "job_list";
    public const string JobLogs = "job_logs";
    public const string JobInput = "job_input";
    public const string JobCloseInput = "job_close_input";
    public const string JobCancel = "job_cancel";
    public const string JobWait = "job_wait";
    public const string JobResize = "job_resize";
public const string FileManifest = "file_manifest";
    public const string FileList = "file_list";
    public const string FileStat = "file_stat";
    public const string FileRead = "file_read";
    public const string FileWrite = "file_write";
    public const string TransferStart = "transfer_start";
    public const string TransferWriteChunk = "transfer_write_chunk";
    public const string TransferReadChunk = "transfer_read_chunk";
    public const string TransferWriteBinary = "transfer_write_binary";
    public const string TransferReadBinary = "transfer_read_binary";
    public const string TransferComplete = "transfer_complete";
    public const string TransferStatus = "transfer_status";
    public const string UiStatus = "ui_status";
    public const string UiCommand = "ui_command";
    public const string UpdateStart = "update_start";
    public const string UpdateWriteChunk = "update_write_chunk";
    public const string UpdateComplete = "update_complete";
    public const string UpdateStatus = "update_status";
}

public sealed record ControlUiStatusRequest(int ProtocolVersion, string ControllerId)
{
    public string Kind => ControlMessageKinds.UiStatus;
}

public sealed record ControlUiCommandRequest(int ProtocolVersion, string ControllerId, string Operation, System.Text.Json.JsonElement Request)
{
    public string Kind => ControlMessageKinds.UiCommand;
}

public sealed record ControlPairStartRequest(
    int ProtocolVersion,
    string ControllerId,
    byte[] ControllerCertificate)
{
    public string Kind => ControlMessageKinds.PairStart;
}

public sealed record ControlPairRound1Request(Guid PairingId, PairingPakeRound1 ControllerRound1)
{
    public string Kind => ControlMessageKinds.PairRound1;
}

public sealed record ControlPairRound2Request(Guid PairingId, PairingPakeRound2 ControllerRound2)
{
    public string Kind => ControlMessageKinds.PairRound2;
}

public sealed record ControlPairCompleteRequest(
    Guid PairingId,
    PairingPakeRound3 ControllerRound3,
    byte[] ConfirmationMac,
    byte[] CertificateSignature)
{
    public string Kind => ControlMessageKinds.PairComplete;
}

public sealed record ControlPairingBinding(
    Guid PairingId,
    string AgentDeviceId,
    string ControllerId,
    string AgentAddress,
    int AgentPort,
    byte[] AgentCertificateFingerprint,
    byte[] AgentSpkiFingerprint,
    byte[] ControllerCertificateFingerprint,
    byte[] ControllerSpkiFingerprint);

public sealed record ControlPairStartResponse(
    ControlPairingBinding Binding,
    DateTimeOffset ExpiresAtUtc,
    PairingPakeRound1 AgentRound1);

public sealed record ControlPairRound2Response(PairingPakeRound2 AgentRound2);

public sealed record ControlPairRound3Response(PairingPakeRound3 AgentRound3);

public sealed record ControlPairCompleteResponse(string ControllerId, DateTimeOffset PairedAtUtc);
public sealed record ControlSessionStartRequest(int ProtocolVersion, string ControllerId)
{
    public string Kind => ControlMessageKinds.SessionStart;
}

public sealed record ControlSessionStartResponse(
    Guid SessionId,
    string AgentDeviceId,
    string ControllerId,
    byte[] Challenge,
    DateTimeOffset ExpiresAtUtc);

public sealed record ControlSessionAuthenticateRequest(
    int ProtocolVersion,
    Guid SessionId,
    string ControllerId,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.SessionAuthenticate;
}

public sealed record ControlSessionAuthenticateResponse(Guid SessionId, string ControllerId, DateTimeOffset ExpiresAtUtc);
/// <summary>
/// A one-shot command request made by the paired controller. The request is signed
/// with the paired controller certificate so a TLS connection alone never grants
/// process-launch authority.
/// </summary>
public sealed record ControlExecuteOnceRequest(
    int ProtocolVersion,
    string ControllerId,
    ExecRequest Execution,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.ExecOnce;
}

/// <summary>
/// The terminal result of a one-shot command. Output is bounded per stream; when
/// a stream exceeds the response limit its complete data remains in Agent storage
/// for the later job-log protocol.
/// </summary>
public sealed record ControlExecuteOnceResponse(
    JobSnapshot Job,
    byte[] StandardOutput,
    bool StandardOutputTruncated,
    byte[] StandardError,
    bool StandardErrorTruncated);
/// <summary>Starts a task without waiting for it to finish.</summary>
public sealed record ControlJobStartRequest(
    int ProtocolVersion,
    string ControllerId,
    ExecRequest Execution,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobStart;
}

public sealed record ControlJobStartResponse(TaskRuntimeStatus Status);

/// <summary>Reads the current in-memory status when available, or the persisted terminal snapshot.</summary>
public sealed record ControlJobStatusRequest(
    int ProtocolVersion,
    string ControllerId,
    string JobId,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobStatus;
}

public sealed record ControlJobStatusResponse(TaskRuntimeStatus Status, bool IsActive);

/// <summary>Lists durable job snapshots. Active jobs are refreshed before the list is returned.</summary>
public sealed record ControlJobListRequest(
    int ProtocolVersion,
    string ControllerId,
    JobState? State,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobList;
}

public sealed record ControlJobListResponse(IReadOnlyList<JobSnapshot> Jobs);
public sealed record ControlJobLogsRequest(int ProtocolVersion, string ControllerId, string JobId, JobOutputKind Stream, long AfterOffset, int MaximumBytes, byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobLogs;
}

public sealed record ControlJobLogsResponse(JobLogReadResponse Log);

public sealed record ControlJobInputRequest(int ProtocolVersion, string ControllerId, string JobId, byte[] Data, byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobInput;
}

public sealed record ControlJobCloseInputRequest(int ProtocolVersion, string ControllerId, string JobId, byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobCloseInput;
}

public sealed record ControlJobCancelRequest(int ProtocolVersion, string ControllerId, string JobId, byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobCancel;
}

public sealed record ControlJobWaitRequest(int ProtocolVersion, string ControllerId, string JobId, TimeSpan? Timeout, byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobWait;
}

public sealed record ControlJobResizeRequest(
    int ProtocolVersion,
    string ControllerId,
    string JobId,
    int Columns,
    int Rows,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.JobResize;
}
public sealed record ControlJobOperationResponse(TaskRuntimeStatus Status, bool Completed);
public sealed record ControlFileManifestRequest(int ProtocolVersion, string ControllerId, FileManifestRequest Request) { public string Kind => ControlMessageKinds.FileManifest; }
public sealed record ControlFileListRequest(int ProtocolVersion, string ControllerId, FileListRequest Request) { public string Kind => ControlMessageKinds.FileList; }
public sealed record ControlFileStatRequest(int ProtocolVersion, string ControllerId, FileStatRequest Request) { public string Kind => ControlMessageKinds.FileStat; }
public sealed record ControlFileReadRequest(int ProtocolVersion, string ControllerId, FileReadRequest Request) { public string Kind => ControlMessageKinds.FileRead; }
public sealed record ControlFileWriteRequest(int ProtocolVersion, string ControllerId, FileWriteRequest Request) { public string Kind => ControlMessageKinds.FileWrite; }
public sealed record ControlTransferStartRequest(int ProtocolVersion, string ControllerId, TransferStartRequest Request) { public string Kind => ControlMessageKinds.TransferStart; }
public sealed record ControlTransferWriteChunkRequest(int ProtocolVersion, string ControllerId, TransferWriteChunkRequest Request) { public string Kind => ControlMessageKinds.TransferWriteChunk; }
public sealed record ControlTransferReadChunkRequest(int ProtocolVersion, string ControllerId, TransferReadChunkRequest Request) { public string Kind => ControlMessageKinds.TransferReadChunk; }
public sealed record ControlTransferWriteBinaryRequest(int ProtocolVersion, string ControllerId, TransferBinaryWriteRequest Request) { public string Kind => ControlMessageKinds.TransferWriteBinary; }
public sealed record ControlTransferReadBinaryRequest(int ProtocolVersion, string ControllerId, TransferBinaryReadRequest Request) { public string Kind => ControlMessageKinds.TransferReadBinary; }
public sealed record ControlTransferCompleteRequest(int ProtocolVersion, string ControllerId, TransferCompleteRequest Request) { public string Kind => ControlMessageKinds.TransferComplete; }
public sealed record ControlTransferStatusRequest(int ProtocolVersion, string ControllerId, TransferStatusRequest Request) { public string Kind => ControlMessageKinds.TransferStatus; }
public sealed record ControlUpdateStartRequest(int ProtocolVersion, string ControllerId, UpdateStartRequest Request, byte[] Signature) { public string Kind => ControlMessageKinds.UpdateStart; }
public sealed record ControlUpdateWriteChunkRequest(int ProtocolVersion, string ControllerId, UpdateWriteChunkRequest Request, byte[] Signature) { public string Kind => ControlMessageKinds.UpdateWriteChunk; }
public sealed record ControlUpdateCompleteRequest(int ProtocolVersion, string ControllerId, UpdateCompleteRequest Request, byte[] Signature) { public string Kind => ControlMessageKinds.UpdateComplete; }
public sealed record ControlUpdateStatusRequest(int ProtocolVersion, string ControllerId, UpdateStatusRequest Request, byte[] Signature) { public string Kind => ControlMessageKinds.UpdateStatus; }
