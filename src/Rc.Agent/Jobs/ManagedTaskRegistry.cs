using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Rc.Agent.Persistence;
using Rc.Contracts;

namespace Rc.Agent.Jobs;

/// <summary>Schedules durable jobs, controls active TaskHosts, and reconnects to surviving hosts after an agent restart.</summary>
public sealed class ManagedTaskRegistry : IAsyncDisposable
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CancelControlTimeout = TimeSpan.FromSeconds(15);
    private readonly AgentStateStore stateStore;
    private readonly TaskHostRegistration registration;
    private readonly JobScheduler scheduler;
    private readonly IManagedTaskHostLauncher normalLauncher;
    private readonly IManagedTaskHostLauncher elevatedLauncher;
    private readonly long maximumOutputBytes;
    private readonly TimeSpan cancellationGrace;
    private readonly ConcurrentDictionary<string, PendingTask> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunningTask> running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private bool recoveryComplete;

    public ManagedTaskRegistry(
        AgentStateStore stateStore,
        int normalConcurrency = 8,
        IManagedTaskHostLauncher? launcher = null,
        TimeSpan? cancellationGrace = null,
        int elevatedConcurrency = 2,
        IManagedTaskHostLauncher? elevatedLauncher = null,
        long maximumOutputBytes = 200L * 1024 * 1024)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        registration = new TaskHostRegistration(stateStore);
        scheduler = new JobScheduler(normalConcurrency, elevatedConcurrency);
        normalLauncher = launcher ?? new InProcessTaskHostLauncher();
        this.elevatedLauncher = elevatedLauncher ?? new UnavailableElevatedTaskHostLauncher();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputBytes);
        this.maximumOutputBytes = maximumOutputBytes;
        this.cancellationGrace = cancellationGrace ?? TimeSpan.FromSeconds(10);
    }

    public async Task<TaskRuntimeStatus> StartAsync(ExecRequest execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!Enum.IsDefined(execution.ExecutionIdentity))
        {
            throw new ArgumentOutOfRangeException(nameof(execution), "The execution identity is invalid.");
        }

        await EnsureRecoveryAsync(cancellationToken).ConfigureAwait(false);
        var jobId = "job-" + Guid.NewGuid().ToString("N");
        var pipeName = "rc-job-" + Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow;
        var queued = new TaskRuntimeStatus(
            new JobSnapshot(jobId, JobState.Queued, null, createdAt, null, null, null, execution.ExecutionIdentity),
            null, null, null, null, 0, 0, null);
        await stateStore.SaveJobSnapshotAsync(queued.Job, cancellationToken).ConfigureAwait(false);
        await stateStore.SaveTaskHostRegistrationAsync(new TaskHostRegistrationInfo(jobId, pipeName, null, createdAt), cancellationToken).ConfigureAwait(false);

        var cancellation = new CancellationTokenSource();
        var item = new PendingTask(pipeName, cancellation);
        if (!pending.TryAdd(jobId, item))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("The generated job ID is already scheduled.");
        }

        item.Lifetime = scheduler.EnqueueAsync(
            execution.ExecutionIdentity,
            token => RunScheduledAsync(jobId, execution, item, token),
            cancellation.Token);
        _ = ObservePendingAsync(jobId, item);
        return queued;
    }

    public async Task<(TaskRuntimeStatus Status, bool IsActive)> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await EnsureRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (running.TryGetValue(jobId, out var item))
        {
            try
            {
                var status = await registration.RefreshAsync(item.ControlPipeName, ControlTimeout, cancellationToken).ConfigureAwait(false);
                return (status, !IsTerminal(status.Job.State));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var stored = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (stored is not null)
                {
                    return (ToRuntimeStatus(stored), !IsTerminal(stored.State));
                }
                throw;
            }
        }

        var snapshot = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new KeyNotFoundException($"No job exists with ID '{jobId}'.");
        }

        return (ToRuntimeStatus(snapshot), pending.ContainsKey(jobId) || !IsTerminal(snapshot.State));
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListAsync(JobState? state, CancellationToken cancellationToken = default)
    {
        await EnsureRecoveryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var jobId in running.Keys)
        {
            try
            {
                await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
        return await stateStore.ListJobSnapshotsAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobLogReadResponse> ReadLogsAsync(string jobId, JobOutputKind stream, long afterOffset, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!Enum.IsDefined(stream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        var current = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
        await stateStore.RegisterTaskHostOutputSegmentsAsync(jobId, cancellationToken).ConfigureAwait(false);
        var segments = await stateStore.ListOutputSegmentsAsync(jobId, stream, cancellationToken).ConfigureAwait(false);
        var runtimeLength = stream == JobOutputKind.Stdout ? current.Status.StdoutLength : current.Status.StderrLength;
        var registeredLength = segments.Count == 0 ? 0 : segments.Max(segment => checked(segment.StartOffset + segment.ByteLength));
        var streamLength = Math.Max(runtimeLength, registeredLength);
        var requestedBytes = Math.Min(maximumBytes, 256 * 1024);
        await using var collected = new MemoryStream(requestedBytes);
        var nextOffset = afterOffset;
        foreach (var segment in segments)
        {
            var segmentEnd = checked(segment.StartOffset + segment.ByteLength);
            if (segmentEnd <= nextOffset)
            {
                continue;
            }
            if (segment.StartOffset > nextOffset || collected.Length >= requestedBytes)
            {
                break;
            }

            var path = Path.GetFullPath(Path.Combine(stateStore.DataRoot, segment.RelativePath));
            var dataRoot = Path.GetFullPath(stateStore.DataRoot) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A registered output segment is outside the agent data root.");
            }

            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            source.Position = nextOffset - segment.StartOffset;
            var remaining = requestedBytes - checked((int)collected.Length);
            var available = checked((int)Math.Min(segmentEnd - nextOffset, remaining));
            var buffer = new byte[Math.Min(16 * 1024, available)];
            var left = available;
            while (left > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, left)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await collected.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                nextOffset += read;
                left -= read;
            }
        }

        var isFinal = !current.IsActive && nextOffset >= streamLength;
        return new JobLogReadResponse(new ByteChunk(jobId, stream, afterOffset, collected.ToArray(), isFinal), nextOffset);
    }

    public async Task<TaskRuntimeStatus> WriteStandardInputAsync(string jobId, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 256 * 1024)
        {
            throw new ArgumentException("A single standard-input write cannot exceed 256 KiB.", nameof(data));
        }
        var item = GetRunningTask(jobId);
        return await registration.WriteStandardInputAsync(item.ControlPipeName, data, ControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskRuntimeStatus> CloseStandardInputAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var item = GetRunningTask(jobId);
        return await registration.CloseStandardInputAsync(item.ControlPipeName, ControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskRuntimeStatus> ResizeTerminalAsync(string jobId, int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (columns is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(rows));
        var item = GetRunningTask(jobId);
        return await registration.ResizeTerminalAsync(item.ControlPipeName, columns, rows, ControlTimeout, cancellationToken).ConfigureAwait(false);
    }
    public async Task<TaskRuntimeStatus> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await EnsureRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (running.TryGetValue(jobId, out var item))
        {
            return await registration.CancelAsync(item.ControlPipeName, CancelControlTimeout, cancellationToken).ConfigureAwait(false);
        }

        if (pending.TryGetValue(jobId, out var queued))
        {
            queued.Cancellation.Cancel();
            var snapshot = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"No job exists with ID '{jobId}'.");
            var cancelled = snapshot with { State = JobState.Cancelled, FinishedAtUtc = DateTimeOffset.UtcNow };
            await stateStore.SaveJobSnapshotAsync(cancelled, cancellationToken).ConfigureAwait(false);
            await stateStore.DeleteTaskHostRegistrationAsync(jobId, cancellationToken).ConfigureAwait(false);
            return ToRuntimeStatus(cancelled);
        }

        var stored = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            throw new KeyNotFoundException($"No job exists with ID '{jobId}'.");
        }
        if (IsTerminal(stored.State))
        {
            return ToRuntimeStatus(stored);
        }
        throw new InvalidOperationException($"Job '{jobId}' is not active.");
    }

    public async Task<(TaskRuntimeStatus Status, bool Completed)> WaitAsync(string jobId, TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (timeout is { } value && (value < TimeSpan.Zero || value > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        await EnsureRecoveryAsync(cancellationToken).ConfigureAwait(false);
        var initial = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!initial.IsActive)
        {
            return (initial.Status, true);
        }
        Task? completion = running.TryGetValue(jobId, out var active)
            ? active.Completion
            : pending.TryGetValue(jobId, out var queued) ? queued.Lifetime : null;
        if (completion is null)
        {
            var stored = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            return (stored.Status, true);
        }

        try
        {
            if (timeout is null)
            {
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await completion.WaitAsync(timeout.Value, cancellationToken).ConfigureAwait(false);
            }
            var terminal = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            return (terminal.Status, !terminal.IsActive);
        }
        catch (TimeoutException)
        {
            var current = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            return (current.Status, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var current = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            return (current.Status, !current.IsActive);
        }
        catch (InvalidOperationException)
        {
            // TaskHost 无终态退出（如被外部终止）：RunScheduledAsync 正将其收敛为
            // HostCrashed/FailedToStart 并落库；等待终态后返回，而不是把宿主
            // 异常直接抛给调用方。
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var current = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (!current.IsActive)
                {
                    return (current.Status, true);
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            var final = await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            return (final.Status, !final.IsActive);
        }
    }

    public async Task EnsureRecoveryAsync(CancellationToken cancellationToken = default)
    {
        if (recoveryComplete)
        {
            return;
        }
        await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (recoveryComplete)
            {
                return;
            }

            var registrations = await stateStore.ListTaskHostRegistrationsAsync(cancellationToken).ConfigureAwait(false);
            var registeredIds = registrations.Select(item => item.JobId).ToHashSet(StringComparer.Ordinal);
            foreach (var host in registrations)
            {
                var snapshot = await stateStore.GetJobSnapshotAsync(host.JobId, cancellationToken).ConfigureAwait(false);
                if (snapshot is null || snapshot.State == JobState.Queued)
                {
                    await stateStore.MarkJobInterruptedByRebootAsync(host.JobId, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (IsTerminal(snapshot.State))
                {
                    await stateStore.DeleteTaskHostRegistrationAsync(host.JobId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var status = await registration.RefreshAsync(host.ControlPipeName, TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
                    if (IsTerminal(status.Job.State))
                    {
                        await PersistTerminalAsync(status, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    ScheduleRecoveredHost(host, snapshot.ExecutionIdentity);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    var terminal = await TryReadTerminalStatusFileAsync(host.JobId, cancellationToken).ConfigureAwait(false);
                    if (terminal is not null)
                    {
                        await PersistTerminalAsync(terminal, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await stateStore.MarkJobInterruptedByRebootAsync(host.JobId, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            foreach (var snapshot in await stateStore.ListJobSnapshotsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if ((snapshot.State is JobState.Queued or JobState.Running) && !registeredIds.Contains(snapshot.JobId))
                {
                    await stateStore.MarkJobInterruptedByRebootAsync(snapshot.JobId, cancellationToken).ConfigureAwait(false);
                }
            }
            recoveryComplete = true;
        }
        finally
        {
            recoveryGate.Release();
        }
    }

    private async Task RunScheduledAsync(string jobId, ExecRequest execution, PendingTask pendingItem, CancellationToken schedulerToken)
    {
        ManagedTaskHostHandle? handle = null;
        var preserveRegistration = false;
        try
        {
            var controlClientSid = execution.ExecutionIdentity == ExecutionIdentity.ElevatedBroker
                ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                : null;
            var launch = new TaskLaunchRequest(jobId, execution, execution.ExecutionIdentity, stateStore.DataRoot, pendingItem.ControlPipeName, cancellationGrace, maximumOutputBytes: maximumOutputBytes, controlClientSid: controlClientSid);
            var selectedLauncher = execution.ExecutionIdentity == ExecutionIdentity.ElevatedBroker ? elevatedLauncher : normalLauncher;
            handle = await selectedLauncher.LaunchAsync(launch, schedulerToken).ConfigureAwait(false);
            schedulerToken.ThrowIfCancellationRequested();
            var runningItem = new RunningTask(handle.ControlPipeName, handle.Completion, handle);
            running[jobId] = runningItem;
            pending.TryRemove(jobId, out _);
            await stateStore.SaveTaskHostRegistrationAsync(new TaskHostRegistrationInfo(jobId, handle.ControlPipeName, handle.ProcessId, DateTimeOffset.UtcNow), schedulerToken).ConfigureAwait(false);
            var started = handle.InitialStatus ?? await WaitForTaskHostAsync(handle.ControlPipeName, schedulerToken).ConfigureAwait(false);
            await PersistAsync(started, schedulerToken).ConfigureAwait(false);

            TaskRuntimeStatus terminal;
            try
            {
                terminal = await handle.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested && handle.SurvivesAgentShutdown)
            {
                preserveRegistration = true;
                return;
            }
            await PersistTerminalAsync(terminal, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            preserveRegistration = handle?.SurvivesAgentShutdown == true;
        }
        catch (OperationCanceledException) when (pendingItem.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var snapshot = await stateStore.GetJobSnapshotAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (snapshot is not null && !IsTerminal(snapshot.State))
            {
                var failed = snapshot with
                {
                    State = snapshot.StartedAtUtc is null ? JobState.FailedToStart : JobState.HostCrashed,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    Error = new RemoteError(ErrorCode.Internal, snapshot.StartedAtUtc is null
                        ? $"TaskHost launch failed: {exception.Message}"
                        : $"TaskHost crashed while the task was running: {exception.Message}", false),
                };
                await stateStore.SaveJobSnapshotAsync(failed, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            running.TryRemove(jobId, out _);
            pending.TryRemove(jobId, out _);
            if (!preserveRegistration)
            {
                await stateStore.DeleteTaskHostRegistrationAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            }
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ObservePendingAsync(string jobId, PendingTask item)
    {
        try
        {
            await item.Lifetime.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested || shutdown.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            pending.TryRemove(jobId, out _);
            item.Cancellation.Dispose();
        }
    }

    private void ScheduleRecoveredHost(TaskHostRegistrationInfo host, ExecutionIdentity executionIdentity)
    {
        var completion = new TaskCompletionSource<TaskRuntimeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerLifetime = scheduler.EnqueueAsync(executionIdentity, async token =>
        {
            try
            {
                var terminal = await MonitorRecoveredHostAsync(host, token).ConfigureAwait(false);
                await PersistTerminalAsync(terminal, CancellationToken.None).ConfigureAwait(false);
                completion.TrySetResult(terminal);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                completion.TrySetCanceled(shutdown.Token);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                await stateStore.MarkJobInterruptedByRebootAsync(host.JobId, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                running.TryRemove(host.JobId, out _);
            }
        });
        running[host.JobId] = new RunningTask(host.ControlPipeName, completion.Task, null);
        _ = schedulerLifetime.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task<TaskRuntimeStatus> MonitorRecoveredHostAsync(TaskHostRegistrationInfo host, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = await registration.RefreshAsync(host.ControlPipeName, ControlTimeout, cancellationToken).ConfigureAwait(false);
                if (IsTerminal(status.Job.State))
                {
                    return status;
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var terminal = await TryReadTerminalStatusFileAsync(host.JobId, cancellationToken).ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }
                if (host.ProcessId is null || !IsProcessAlive(host.ProcessId.Value))
                {
                    throw new IOException("The recovered TaskHost is no longer available.", exception);
                }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TaskRuntimeStatus> WaitForTaskHostAsync(string pipeName, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await registration.RefreshAsync(pipeName, TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = exception;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException("TaskHost did not open its control pipe within ten seconds.", last);
    }

    private async Task<TaskRuntimeStatus?> TryReadTerminalStatusFileAsync(string jobId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stateStore.DataRoot, "task-status", jobId + ".json");
        if (!File.Exists(path))
        {
            return null;
        }
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var status = JsonSerializer.Deserialize<TaskRuntimeStatus>(json, ContractJson.Options);
        return status is not null && IsTerminal(status.Job.State) ? status : null;
    }

    private async Task PersistTerminalAsync(TaskRuntimeStatus status, CancellationToken cancellationToken)
    {
        await PersistAsync(status, cancellationToken).ConfigureAwait(false);
        await stateStore.DeleteTaskHostRegistrationAsync(status.Job.JobId, cancellationToken).ConfigureAwait(false);
        var terminalStatusPath = Path.Combine(stateStore.DataRoot, "task-status", status.Job.JobId + ".json");
        try
        {
            File.Delete(terminalStatusPath);
        }
        catch (IOException)
        {
        }
    }

    private async Task PersistAsync(TaskRuntimeStatus status, CancellationToken cancellationToken)
    {
        await stateStore.SaveJobSnapshotAsync(status.Job, cancellationToken).ConfigureAwait(false);
        await stateStore.RegisterTaskHostOutputSegmentsAsync(status.Job.JobId, cancellationToken).ConfigureAwait(false);
    }

    private RunningTask GetRunningTask(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return running.TryGetValue(jobId, out var item)
            ? item
            : throw new InvalidOperationException($"Job '{jobId}' is not active.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsTerminal(JobState state) => state is JobState.Exited or JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot or JobState.HostCrashed;

    private static TaskRuntimeStatus ToRuntimeStatus(JobSnapshot snapshot) => new(snapshot, null, null, null, null, 0, 0, null, snapshot.OutputTruncated);

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        foreach (var item in pending.Values)
        {
            item.Cancellation.Cancel();
        }
        await scheduler.DisposeAsync().ConfigureAwait(false);
        foreach (var item in running.Values)
        {
            if (item.Handle is not null)
            {
                await item.Handle.DisposeAsync().ConfigureAwait(false);
            }
        }
        running.Clear();
        recoveryGate.Dispose();
        shutdown.Dispose();
    }

    private sealed class PendingTask(string controlPipeName, CancellationTokenSource cancellation)
    {
        public string ControlPipeName { get; } = controlPipeName;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Lifetime { get; set; } = Task.CompletedTask;
    }

    private sealed record RunningTask(string ControlPipeName, Task<TaskRuntimeStatus> Completion, ManagedTaskHostHandle? Handle);
}