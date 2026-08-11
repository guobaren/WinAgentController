using System.Runtime.InteropServices;

namespace Rc.Agent.Configuration;

public sealed record AgentOptions
{
    public IReadOnlyList<string> SupportedOperatingSystems { get; init; } = ["Windows 10", "Windows 11"];

    public Architecture RequiredArchitecture { get; init; } = Architecture.X64;

    public int NormalTaskLimit { get; init; } = checked((int)ReadLong("RC_NORMAL_TASK_LIMIT", 8));

    public int ElevatedTaskLimit { get; init; } = checked((int)ReadLong("RC_ELEVATED_TASK_LIMIT", 2));

    public string BrokerPipeName { get; init; } = Environment.GetEnvironmentVariable("RC_BROKER_PIPE_NAME") ?? "rc-privileged-broker";

    public string? BrokerSecretPath { get; init; } = Environment.GetEnvironmentVariable("RC_BROKER_SECRET_PATH");

    public string UiRegistrationPipeName { get; init; } = Environment.GetEnvironmentVariable("RC_UI_REGISTRATION_PIPE") ?? "rc-ui-registration";

    public string? UiAgentClientSid { get; init; } = Environment.GetEnvironmentVariable("RC_UI_AGENT_CLIENT_SID");

    public long LogQuotaBytes { get; init; } = ReadLong("RC_LOG_QUOTA_BYTES", 200L * 1024 * 1024);
    public long TaskOutputLimitBytes { get; init; } = ReadLong("RC_TASK_OUTPUT_LIMIT_BYTES", 200L * 1024 * 1024);

    public long AuditQuotaBytes { get; init; } = ReadLong("RC_AUDIT_QUOTA_BYTES", 16L * 1024 * 1024);

    public int PairingFailureLimit { get; init; } = checked((int)ReadLong("RC_PAIRING_FAILURE_LIMIT", 5));

    public TimeSpan PairingFailureWindow { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PairingCooldown { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromMilliseconds(ReadLong("RC_CANCELLATION_GRACE_MS", 10_000));

    public string FileRoot { get; init; } = Environment.GetEnvironmentVariable("RC_AGENT_FILE_ROOT")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public long TransferQuotaBytes { get; init; } = ReadLong("RC_TRANSFER_QUOTA_BYTES", 2L * 1024 * 1024 * 1024);

    public int MaximumTransferChunkBytes { get; init; } = checked((int)ReadLong("RC_TRANSFER_MAX_CHUNK_BYTES", 64L * 1024 * 1024));

    public long MaximumUpdatePackageBytes { get; init; } = ReadLong("RC_UPDATE_MAX_PACKAGE_BYTES", 1024L * 1024 * 1024);

    public int MaximumUpdateChunkBytes { get; init; } = checked((int)ReadLong("RC_UPDATE_MAX_CHUNK_BYTES", 256 * 1024));

    public int MaximumAtomicWriteBytes { get; init; } = checked((int)ReadLong("RC_FILE_MAX_WRITE_BYTES", 16L * 1024 * 1024));

    public TimeSpan TransferSessionLifetime { get; init; } = TimeSpan.FromHours(24);

    private static long ReadLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
