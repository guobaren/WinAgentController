using System.Diagnostics;
using System.Text;
using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Jobs;

public sealed class ManagedTaskRegistrySchedulingAndRecoveryTests
{
    [Fact]
    public async Task NormalConcurrencyLimitQueuesTheSecondJobUntilTheFirstFinishes()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store, normalConcurrency: 1);

        var first = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));
        await WaitForStateAsync(registry, first.Job.JobId, JobState.Running);
        var second = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));

        Assert.Equal(JobState.Queued, second.Job.State);
        Assert.Equal(JobState.Queued, (await registry.GetStatusAsync(second.Job.JobId)).Status.Job.State);

        await registry.CloseStandardInputAsync(first.Job.JobId);
        Assert.True((await registry.WaitAsync(first.Job.JobId, TimeSpan.FromSeconds(30))).Completed);
        await WaitForStateAsync(registry, second.Job.JobId, JobState.Running);
        await registry.CloseStandardInputAsync(second.Job.JobId);
        Assert.True((await registry.WaitAsync(second.Job.JobId, TimeSpan.FromSeconds(30))).Completed);
    }

    [Fact]
    public async Task QueuedCancellationIsIdempotentAndNeverStartsTheTaskHost()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store, normalConcurrency: 1);

        var first = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));
        await WaitForStateAsync(registry, first.Job.JobId, JobState.Running);
        var queued = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));
        Assert.Equal(JobState.Queued, queued.Job.State);

        Assert.Equal(JobState.Cancelled, (await registry.CancelAsync(queued.Job.JobId)).Job.State);
        Assert.Equal(JobState.Cancelled, (await registry.CancelAsync(queued.Job.JobId)).Job.State);

        await registry.CloseStandardInputAsync(first.Job.JobId);
        Assert.True((await registry.WaitAsync(first.Job.JobId, TimeSpan.FromSeconds(30))).Completed);
        await Task.Delay(200);

        Assert.Equal(JobState.Cancelled, (await registry.CancelAsync(queued.Job.JobId)).Job.State);
        Assert.Equal(JobState.Cancelled, (await registry.GetStatusAsync(queued.Job.JobId)).Status.Job.State);
        Assert.Empty(await store.ListTaskHostRegistrationsAsync());
    }
    [Fact]
    public async Task MissingTaskHostAfterRestartIsMarkedInterruptedWithoutReplay()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-missing", JobState.Running, null, now, now, null, null));
        await store.SaveTaskHostRegistrationAsync(new TaskHostRegistrationInfo("job-missing", "missing-" + Guid.NewGuid().ToString("N"), null, now));
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-queued", JobState.Queued, null, now.AddSeconds(1), null, null, null));
        await using var registry = new ManagedTaskRegistry(store);

        await registry.EnsureRecoveryAsync();
        var recovered = await registry.GetStatusAsync("job-missing");

        Assert.Equal(JobState.InterruptedByReboot, recovered.Status.Job.State);
        Assert.Equal(JobState.InterruptedByReboot, (await registry.GetStatusAsync("job-queued")).Status.Job.State);
        Assert.False(recovered.IsActive);
        Assert.Empty(await store.ListTaskHostRegistrationsAsync());
    }

    [Fact]
    public async Task AbruptExternalTaskHostExitConvergesToDurableTerminalState()
    {
        var taskHostPath = Path.Combine(AppContext.BaseDirectory, "Rc.TaskHost.exe");
        Assert.True(File.Exists(taskHostPath), $"TaskHost executable not found at {taskHostPath}");
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store, normalConcurrency: 1, launcher: new ExternalTaskHostLauncher(taskHostPath));

        var started = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));
        await WaitForStateAsync(registry, started.Job.JobId, JobState.Running);
        var registration = Assert.Single(await store.ListTaskHostRegistrationsAsync());
        Assert.NotNull(registration.ProcessId);

        using (var taskHost = Process.GetProcessById(registration.ProcessId.Value))
        {
            taskHost.Kill(entireProcessTree: true);
            await taskHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }

        var terminal = await registry.WaitAsync(started.Job.JobId, TimeSpan.FromSeconds(45));

        Assert.True(terminal.Completed);
        Assert.Equal(JobState.HostCrashed, terminal.Status.Job.State);
        Assert.Equal(ErrorCode.Internal, terminal.Status.Job.Error?.Code);
        Assert.Empty(await store.ListTaskHostRegistrationsAsync());
    }
    [Fact]
    public async Task ExternalTaskHostSurvivesRegistryRestartAndReconnects()
    {
        var taskHostPath = Path.Combine(AppContext.BaseDirectory, "Rc.TaskHost.exe");
        Assert.True(File.Exists(taskHostPath), $"TaskHost executable not found at {taskHostPath}");
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var launcher = new ExternalTaskHostLauncher(taskHostPath);
        string jobId;

        await using (var firstRegistry = new ManagedTaskRegistry(store, normalConcurrency: 1, launcher: launcher))
        {
            var started = await firstRegistry.StartAsync(ExecRequest.ForShell(ShellKind.PowerShell, "Start-Sleep -Seconds 3; Write-Output survived"));
            jobId = started.Job.JobId;
            await WaitForStateAsync(firstRegistry, jobId, JobState.Running);
        }

        await using var secondRegistry = new ManagedTaskRegistry(store, normalConcurrency: 1, launcher: launcher);
        await secondRegistry.EnsureRecoveryAsync();
        var reconnected = await secondRegistry.GetStatusAsync(jobId);
        Assert.True(reconnected.IsActive);

        var terminal = await secondRegistry.WaitAsync(jobId, TimeSpan.FromSeconds(45));
        var logs = await secondRegistry.ReadLogsAsync(jobId, JobOutputKind.Stdout, 0, 4096);
        Assert.True(terminal.Completed);
        Assert.Equal(JobState.Exited, terminal.Status.Job.State);
        Assert.Contains("survived", Encoding.UTF8.GetString(logs.Chunk.Data), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForStateAsync(ManagedTaskRegistry registry, string jobId, JobState expected)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var current = await registry.GetStatusAsync(jobId);
            if (current.Status.Job.State == expected)
            {
                return;
            }
            if (current.Status.Job.State is JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot)
            {
                throw new InvalidOperationException($"Job entered {current.Status.Job.State} before {expected}.");
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job did not enter {expected} state.");
    }
}