using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Rc.Agent.Elevation;
using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Contracts;
using Rc.PrivilegedBroker;
using Xunit;

namespace Rc.PrivilegedBroker.Tests;

public sealed class PrivilegedBrokerIntegrationTests
{
    [Fact]
    public async Task ManagedTaskRegistryRoutesElevatedJobThroughBrokerAndReadsPersistedLogs()
    {
        using var directory = new TemporaryDirectory();
        var pipeName = "rc-broker-registry-test-" + Guid.NewGuid().ToString("N");
        var secretPath = Path.Combine(directory.Path, "broker.key");
        var secret = await BrokerSecretStore.LoadOrCreateAsync(secretPath);
        var options = new BrokerOptions(pipeName, secretPath, directory.Path, 1, AllowUnelevatedForTesting: true);
        await using var server = new PrivilegedBrokerServer(options, secret);
        using var stopping = new CancellationTokenSource();
        var serverTask = server.RunAsync(stopping.Token);

        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(
            store,
            normalConcurrency: 1,
            launcher: new InProcessTaskHostLauncher(),
            elevatedConcurrency: 1,
            elevatedLauncher: new PrivilegedBrokerTaskHostLauncher(pipeName, secretPath),
            maximumOutputBytes: 1024 * 1024);

        var started = await registry.StartAsync(ExecRequest.ForShellWithIdentity(
            ShellKind.PowerShell,
            "Write-Output registry-broker-ready",
            ExecutionIdentity.ElevatedBroker));
        var completed = await registry.WaitAsync(started.Job.JobId, TimeSpan.FromSeconds(15));
        var logs = await registry.ReadLogsAsync(started.Job.JobId, JobOutputKind.Stdout, 0, 64 * 1024);
        var persisted = await store.GetJobSnapshotAsync(started.Job.JobId);

        Assert.True(completed.Completed);
        Assert.Equal(JobState.Exited, completed.Status.Job.State);
        Assert.Equal(ExecutionIdentity.ElevatedBroker, completed.Status.Job.ExecutionIdentity);
        Assert.Equal(ExecutionIdentity.ElevatedBroker, persisted!.ExecutionIdentity);
        Assert.Contains("registry-broker-ready", Encoding.UTF8.GetString(logs.Chunk.Data), StringComparison.OrdinalIgnoreCase);

        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
    }
    [Fact]
    public async Task AuthenticatedLaunchRunsElevatedIdentityAndPersistsTerminalOutput()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "rc-broker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var pipeName = "rc-broker-test-" + Guid.NewGuid().ToString("N");
            var secretPath = Path.Combine(dataRoot, "broker.key");
            var secret = await BrokerSecretStore.LoadOrCreateAsync(secretPath);
            var options = new BrokerOptions(pipeName, secretPath, dataRoot, 1, AllowUnelevatedForTesting: true);
            await using var server = new PrivilegedBrokerServer(options, secret);
            using var stopping = new CancellationTokenSource();
            var serverTask = server.RunAsync(stopping.Token);
            var client = new PrivilegedBrokerClient(pipeName, secretPath);
            var jobId = "job-elevated-" + Guid.NewGuid().ToString("N");
            var launch = new TaskLaunchRequest(
                jobId,
                ExecRequest.ForShellWithIdentity(ShellKind.PowerShell, "Write-Output broker-ready", ExecutionIdentity.ElevatedBroker),
                ExecutionIdentity.ElevatedBroker,
                dataRoot,
                "rc-broker-job-" + Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(1),
                maximumOutputBytes: 1024 * 1024);

            var initial = await client.LaunchAsync(launch);
            var terminal = await WaitForTerminalFileAsync(dataRoot, jobId);

            Assert.Equal(ExecutionIdentity.ElevatedBroker, initial.Job.ExecutionIdentity);
            Assert.Equal(JobState.Exited, terminal.Job.State);
            Assert.Equal(ExecutionIdentity.ElevatedBroker, terminal.Job.ExecutionIdentity);
            Assert.False(terminal.OutputTruncated);
            Assert.Contains("broker-ready", await ReadOutputAsync(dataRoot, jobId), StringComparison.OrdinalIgnoreCase);

            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task<TaskRuntimeStatus> WaitForTerminalFileAsync(string dataRoot, string jobId)
    {
        var path = Path.Combine(dataRoot, "task-status", jobId + ".json");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                var status = JsonSerializer.Deserialize<TaskRuntimeStatus>(await File.ReadAllTextAsync(path), ContractJson.Options);
                if (status is not null)
                {
                    return status;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The broker task did not persist a terminal status.");
    }

    private static async Task<string> ReadOutputAsync(string dataRoot, string jobId)
    {
        var directory = Path.Combine(dataRoot, "segments", jobId, "stdout");
        await using var output = new MemoryStream();
        foreach (var file in Directory.GetFiles(directory, "*.seg").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            await output.WriteAsync(await File.ReadAllBytesAsync(file));
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-broker-tests", Guid.NewGuid().ToString("N"));
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(Path))
        {
            return;
        }

        // 独立进程（Agent/Broker/TaskHost）可能短暂持有文件句柄；删除目录时重试等待释放。
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