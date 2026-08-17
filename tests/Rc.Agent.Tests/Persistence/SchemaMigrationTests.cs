using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Rc.Agent.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class SchemaMigrationTests
{
    private static readonly string[] RequiredTables =
    [
        "device_identity",
        "execution_account_secret",
        "paired_controller",
        "job_snapshots",
        "output_segments",
        "transfer_sessions",
        "audit_events",
        "pairing_security_state",
    ];

    [Fact]
    public async Task InitializeAsyncMigratesAnEmptyDatabaseWithAllRequiredTables()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);

        await store.InitializeAsync();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.True(tables.IsSupersetOf(RequiredTables));
    }

    [Fact]
    public async Task InitializeAsyncAddsExecutionIdentityToExistingJobSnapshotSchema()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection("Data Source=" + store.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('job_snapshots') WHERE name = 'execution_identity';";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task InitializeAsyncAddsOutputTruncatedToExistingJobSnapshotSchema()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection("Data Source=" + store.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('job_snapshots') WHERE name = 'output_truncated';";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task InitializeAsyncCreatesTheUniqueOutputSegmentPathIndex()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection("Data Source=" + store.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [unique] FROM pragma_index_list('output_segments') WHERE name = 'ux_output_segments_relative_path';";

        var isUnique = await command.ExecuteScalarAsync();

        Assert.Equal(1L, isUnique);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new DirectoryInfo(Path).SetAccessControl(security);
    }

    public string Path { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(Path))
        {
            return;
        }

        // Agent 独立进程的 TLS 诊断写（LocalTlsHandshakeDiagnosticsFile）等可能短暂
        // 持有文件句柄；删除目录时重试等待释放（云端 CI 已复现 .tmp 偶发占用）。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
        }

        Directory.Delete(Path, recursive: true);
    }
}
