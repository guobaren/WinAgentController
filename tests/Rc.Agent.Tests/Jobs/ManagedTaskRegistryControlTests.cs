using System.Text;
using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Jobs;

public sealed class ManagedTaskRegistryControlTests
{
    [Fact]
    public async Task InteractiveJobAcceptsInputClosesWaitsAndReturnsDurableLogs()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store);

        var started = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));
        await WaitUntilRunningAsync(registry, started.Job.JobId);
        await registry.WriteStandardInputAsync(started.Job.JobId, Encoding.UTF8.GetBytes("hello\r\n"));
        await registry.CloseStandardInputAsync(started.Job.JobId);
        var waited = await registry.WaitAsync(started.Job.JobId, TimeSpan.FromSeconds(30));
        var logs = await registry.ReadLogsAsync(started.Job.JobId, JobOutputKind.Stdout, 0, 4096);

        Assert.True(waited.Completed);
        Assert.Equal(JobState.Exited, waited.Status.Job.State);
        Assert.Contains("hello", Encoding.UTF8.GetString(logs.Chunk.Data), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(logs.Chunk.Data.Length, logs.NextOffset);
        Assert.True(logs.Chunk.IsFinal);
    }

    [Fact]
    public async Task WaitCanTimeOutAndCancellationProducesTerminalState()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store);

        var started = await registry.StartAsync(ExecRequest.ForShell(ShellKind.PowerShell, "Start-Sleep -Seconds 30"));
        await WaitUntilRunningAsync(registry, started.Job.JobId);
        var timed = await registry.WaitAsync(started.Job.JobId, TimeSpan.FromMilliseconds(1));
        Assert.False(timed.Completed);

        await registry.CancelAsync(started.Job.JobId);
        var cancelled = await registry.WaitAsync(started.Job.JobId, TimeSpan.FromSeconds(30));
        Assert.True(cancelled.Completed);
        Assert.Equal(JobState.Cancelled, cancelled.Status.Job.State);
    }

    [Fact]
    public async Task CancelAfterNaturalExitReturnsTheDurableTerminalState()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store);

        var started = await registry.StartAsync(ExecRequest.ForShell(ShellKind.PowerShell, "Write-Output done"));
        var terminal = await registry.WaitAsync(started.Job.JobId, TimeSpan.FromSeconds(30));
        var repeatedCancel = await registry.CancelAsync(started.Job.JobId);

        Assert.True(terminal.Completed);
        Assert.Equal(JobState.Exited, terminal.Status.Job.State);
        Assert.Equal(JobState.Exited, repeatedCancel.Job.State);
        Assert.Equal(terminal.Status.Job.ExitCode, repeatedCancel.Job.ExitCode);
    }
    [Fact]
    public async Task ResizeRejectsRunningTasksWithoutPseudoConsole()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store);

        var started = await registry.StartAsync(ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]));
        await WaitUntilRunningAsync(registry, started.Job.JobId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ResizeTerminalAsync(started.Job.JobId, 120, 30));

        await registry.CloseStandardInputAsync(started.Job.JobId);
        Assert.True((await registry.WaitAsync(started.Job.JobId, TimeSpan.FromSeconds(30))).Completed);
    }
    [Fact]
    public async Task PersistedTerminalLogsOnlyMarkTheLastPageFinal()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-stored", JobState.Exited, 0, now, now, now, null));
        await store.AppendOutputSegmentAsync("job-stored", JobOutputKind.Stdout, 0, Encoding.UTF8.GetBytes("abcdefghij"));
        await using var registry = new ManagedTaskRegistry(store);

        var first = await registry.ReadLogsAsync("job-stored", JobOutputKind.Stdout, 0, 4);
        var second = await registry.ReadLogsAsync("job-stored", JobOutputKind.Stdout, first.NextOffset, 10);

        Assert.Equal("abcd", Encoding.UTF8.GetString(first.Chunk.Data));
        Assert.False(first.Chunk.IsFinal);
        Assert.Equal("efghij", Encoding.UTF8.GetString(second.Chunk.Data));
        Assert.True(second.Chunk.IsFinal);
    }
    private static async Task WaitUntilRunningAsync(ManagedTaskRegistry registry, string jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var current = await registry.GetStatusAsync(jobId);
            if (current.Status.Job.State == JobState.Running)
            {
                return;
            }
            if (current.Status.Job.State is JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot)
            {
                throw new InvalidOperationException($"Job entered {current.Status.Job.State} before running.");
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Job did not enter Running state.");
    }
}