using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.TaskHost;

public sealed class TaskHostRunner : IAsyncDisposable
{
    private readonly object stateGate = new();
    private readonly TaskLaunchRequest request;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskHostSegmentWriter segmentWriter;
    private readonly SemaphoreSlim standardInputGate = new(1, 1);
    private readonly bool allowElevatedExecution;
    private Process? process;
    private PseudoConsoleProcess? pseudoConsole;
    private JobState state = JobState.Queued;
    private DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? startedAtUtc;
    private DateTimeOffset? finishedAtUtc;
    private int? exitCode;
    private RemoteError? error;
    private long stdoutLength;
    private long stderrLength;
    private long reservedOutputBytes;
    private DateTimeOffset? lastOutputAtUtc;
    private bool cancellationRequested;
    private bool standardInputClosed;
    private bool outputTruncated;
    private Task? runTask;

    public TaskHostRunner(TaskLaunchRequest request, bool allowElevatedExecution = false)
    {
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        this.allowElevatedExecution = allowElevatedExecution;
        segmentWriter = new TaskHostSegmentWriter(request.DataRoot);
    }

    public Task Started => started.Task;

    public TaskRuntimeStatus GetStatus()
    {
        lock (stateGate)
        {
            TimeSpan? processorTime = null;
            long? workingSetBytes = null;
            long? peakWorkingSetBytes = null;
            int? processId = null;
            if (process is not null)
            {
                try
                {
                    process.Refresh();
                    processId = process.Id;
                    processorTime = process.TotalProcessorTime;
                    workingSetBytes = process.WorkingSet64;
                    peakWorkingSetBytes = process.PeakWorkingSet64;
                }
                catch (InvalidOperationException)
                {
                    // The process exited between status samples.
                }
            }

            return new TaskRuntimeStatus(
                new JobSnapshot(request.JobId, state, exitCode, createdAtUtc, startedAtUtc, finishedAtUtc, error, request.ExecutionIdentity, outputTruncated),
                processId,
                processorTime,
                workingSetBytes,
                peakWorkingSetBytes,
                stdoutLength,
                stderrLength,
                lastOutputAtUtc,
                outputTruncated);
        }
    }

    public Task<TaskRuntimeStatus> RunAsync(CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            if (runTask is not null)
            {
                throw new InvalidOperationException("A task host can run only one task.");
            }

            runTask = RunCoreAsync(cancellationToken);
            return (Task<TaskRuntimeStatus>)runTask;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        Process? runningProcess;
        lock (stateGate)
        {
            if (IsTerminal(state))
            {
                return;
            }

            runningProcess = process;
        }

        if (runningProcess is null || runningProcess.HasExited)
        {
            return;
        }

        lock (stateGate)
        {
            if (IsTerminal(state))
            {
                return;
            }
            cancellationRequested = true;
        }

        await TryWriteInterruptByteAsync(runningProcess, cancellationToken).ConfigureAwait(false);

        var remainingGrace = request.CancellationGracePeriod;
        var terminal = pseudoConsole;
        var terminalCloseDelay = TimeSpan.FromSeconds(1);
        if (terminal is not null && remainingGrace > terminalCloseDelay)
        {
            try
            {
                await runningProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(terminalCloseDelay, cancellationToken).ConfigureAwait(false);
                MarkCancellationObserved(runningProcess);
                return;
            }
            catch (TimeoutException)
            {
                // Ctrl+C was not observed promptly by this pseudo-console child.
            }

            terminal.CloseConsole();
            remainingGrace -= terminalCloseDelay;
        }

        try
        {
            await runningProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(remainingGrace, cancellationToken).ConfigureAwait(false);
            MarkCancellationObserved(runningProcess);
            return;
        }
        catch (TimeoutException)
        {
            // Explicit cancellation is allowed to terminate the complete process tree.
        }

        if (!runningProcess.HasExited)
        {
            runningProcess.Kill(entireProcessTree: true);
        }
        await runningProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        MarkCancellationObserved(runningProcess);
    }

    private void MarkCancellationObserved(Process runningProcess)
    {
        lock (stateGate)
        {
            exitCode = runningProcess.ExitCode;
            finishedAtUtc ??= DateTimeOffset.UtcNow;
            state = JobState.Cancelled;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetimeCancellation.Cancel();
        try
        {
            await CancelAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process can race with disposal after exiting.
        }

        lifetimeCancellation.Dispose();
        standardInputGate.Dispose();
        pseudoConsole?.Dispose();
        process?.Dispose();
    }

    private async Task<TaskRuntimeStatus> RunCoreAsync(CancellationToken cancellationToken)
    {
        if (request.ExecutionIdentity != ExecutionIdentity.CurrentUser &&
            !(request.ExecutionIdentity == ExecutionIdentity.ElevatedBroker && allowElevatedExecution))
        {
            SetTerminalFailure(new RemoteError(ErrorCode.FailedPrecondition, "The requested execution identity is not authorized by this TaskHost.", false));
            started.TrySetResult();
            return GetStatus();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
        try
        {
            var startedProcess = StartProcess();
            lock (stateGate)
            {
                process = startedProcess;
                state = JobState.Running;
                startedAtUtc = DateTimeOffset.UtcNow;
            }

            started.TrySetResult();
            var pipeTask = ServeControlPipeAsync(lifetimeCancellation.Token);
            var terminalProcess = pseudoConsole;
            var stdoutTask = CaptureOutputAsync(terminalProcess?.Output ?? startedProcess.StandardOutput.BaseStream, JobOutputKind.Stdout, CancellationToken.None);
            var stderrTask = terminalProcess is null
                ? CaptureOutputAsync(startedProcess.StandardError.BaseStream, JobOutputKind.Stderr, CancellationToken.None)
                : Task.CompletedTask;

            try
            {
                await startedProcess.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                await CancelAsync(CancellationToken.None).ConfigureAwait(false);
                await startedProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (terminalProcess is not null)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                terminalProcess.CloseConsole();
            }
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            lock (stateGate)
            {
                exitCode = startedProcess.ExitCode;
                finishedAtUtc = DateTimeOffset.UtcNow;
                state = cancellationRequested ? JobState.Cancelled : JobState.Exited;
            }

            lifetimeCancellation.Cancel();
            try
            {
                await pipeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                // The pipe listener is intentionally stopped after the task ends.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(CancellationToken.None).ConfigureAwait(false);
            lock (stateGate)
            {
                state = JobState.Cancelled;
                finishedAtUtc ??= DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception)
        {
            SetTerminalFailure(new RemoteError(ErrorCode.Internal, $"Task execution failed: {exception.Message}", false));
            started.TrySetResult();
        }

        return GetStatus();
    }

    private Process StartProcess()
    {
        if (request.Execution.Terminal is { } terminal)
        {
            pseudoConsole = PseudoConsoleProcess.Start(request.Execution, terminal, request.Environment);
            return pseudoConsole.Process;
        }

        var startInfo = BuildStartInfo();
        var startedProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!startedProcess.Start())
        {
            throw new InvalidOperationException("The process did not start.");
        }

        return startedProcess;
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrWhiteSpace(request.Execution.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.Execution.WorkingDirectory;
        }

        ApplyEnvironment(startInfo.Environment, request.Execution.Environment);
        ApplyEnvironment(startInfo.Environment, request.Environment);

        if (request.Execution.DirectArgv is { } argv)
        {
            startInfo.FileName = argv[0];
            foreach (var argument in argv.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        var shell = request.Execution.Shell!;
        switch (shell.Kind)
        {
            case ShellKind.PowerShell:
                startInfo.FileName = "powershell.exe";
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(shell.Command);
                break;
            case ShellKind.Cmd:
                startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(shell.Command);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shell.Kind));
        }

        return startInfo;
    }

    private async Task CaptureOutputAsync(Stream source, JobOutputKind stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            int captured;
            long offset;
            lock (stateGate)
            {
                var remaining = Math.Max(0, request.MaximumOutputBytes - reservedOutputBytes);
                captured = checked((int)Math.Min(read, remaining));
                reservedOutputBytes += captured;
                if (captured < read)
                {
                    outputTruncated = true;
                }
                offset = stream == JobOutputKind.Stdout ? stdoutLength : stderrLength;
            }

            if (captured == 0)
            {
                continue;
            }

            var data = buffer.AsSpan(0, captured).ToArray();
            await segmentWriter.WriteAsync(request.JobId, stream, offset, data, cancellationToken).ConfigureAwait(false);

            lock (stateGate)
            {
                if (stream == JobOutputKind.Stdout)
                {
                    stdoutLength += data.Length;
                }
                else
                {
                    stderrLength += data.Length;
                }

                lastOutputAtUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private NamedPipeServerStream CreateControlPipeServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("TaskHost control-pipe ACLs require Windows.");
        }

        if (string.IsNullOrWhiteSpace(request.ControlClientSid))
        {
            return new NamedPipeServerStream(
                request.ControlPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The TaskHost account SID is unavailable."),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(request.ControlClientSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            request.ControlPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }
    private async Task ServeControlPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {

                using var server = CreateControlPipeServer();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var message = await TaskHostPipeProtocol.ReadAsync<TaskControlMessage>(server, cancellationToken).ConfigureAwait(false);
                var response = await HandleControlMessageSafelyAsync(message).ConfigureAwait(false);
                await TaskHostPipeProtocol.WriteAsync(server, response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (EndOfStreamException)
            {
                // A local client disconnected before sending a complete command; keep serving the task.
            }
            catch (IOException)
            {
                // A local client disconnected while a response was being written; keep serving the task.
            }
        }
    }

    private async Task<TaskControlResponse> HandleControlMessageSafelyAsync(TaskControlMessage message)
    {
        try
        {
            return await HandleControlMessageAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return new TaskControlResponse(
                GetStatus(),
                null,
                new RemoteError(ErrorCode.FailedPrecondition, exception.Message, false));
        }
        catch (ArgumentException exception)
        {
            return new TaskControlResponse(
                GetStatus(),
                null,
                new RemoteError(ErrorCode.InvalidRequest, exception.Message, false));
        }
        catch (Exception)
        {
            return new TaskControlResponse(
                GetStatus(),
                null,
                new RemoteError(ErrorCode.Internal, "Task control command failed.", false));
        }
    }

    private async Task<TaskControlResponse> HandleControlMessageAsync(TaskControlMessage message, CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case TaskControlKind.StandardInput:
                await WriteStandardInputAsync(message.Data!, cancellationToken).ConfigureAwait(false);
                break;
            case TaskControlKind.CloseStandardInput:
                await CloseStandardInputAsync(cancellationToken).ConfigureAwait(false);
                break;
            case TaskControlKind.Cancel:
                await CancelAsync(cancellationToken).ConfigureAwait(false);
                break;
            case TaskControlKind.ResizeTerminal:
                ResizeTerminal(message.Columns!.Value, message.Rows!.Value);
                break;
            case TaskControlKind.GetStatus:
                break;
            default:
                throw new InvalidOperationException($"Unsupported task control message: {message.Kind}.");
        }

        return new TaskControlResponse(GetStatus(), null);
    }

    private async Task WriteStandardInputAsync(byte[] data, CancellationToken cancellationToken)
    {
        await standardInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? runningProcess;
            lock (stateGate)
            {
                if (standardInputClosed)
                {
                    throw new InvalidOperationException("Standard input is closed.");
                }

                runningProcess = process;
            }

            if (runningProcess is null || runningProcess.HasExited)
            {
                throw new InvalidOperationException("The task is not running.");
            }

            var input = pseudoConsole?.Input ?? runningProcess.StandardInput.BaseStream;
            await input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            standardInputGate.Release();
        }
    }

    private async Task CloseStandardInputAsync(CancellationToken cancellationToken)
    {
        await standardInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (standardInputClosed)
                {
                    return;
                }

                standardInputClosed = true;
                if (pseudoConsole is not null)
                {
                    pseudoConsole.Input.Close();
                }
                else
                {
                    process?.StandardInput.Close();
                }
            }
        }
        finally
        {
            standardInputGate.Release();
        }
    }

    private async Task TryWriteInterruptByteAsync(Process runningProcess, CancellationToken cancellationToken)
    {
        await standardInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var input = pseudoConsole?.Input ?? runningProcess.StandardInput.BaseStream;
            await input.WriteAsync(new byte[] { 3 }, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Output-pipe programs may already have closed stdin. The grace-period kill remains available.
        }
        catch (InvalidOperationException)
        {
            // The process exited between the status check and the signal attempt.
        }
        finally
        {
            standardInputGate.Release();
        }
    }

    private void ResizeTerminal(int columns, int rows)
    {
        var terminal = pseudoConsole ?? throw new InvalidOperationException("The task was not started with a pseudo console.");
        terminal.Resize(columns, rows);
    }
    private static void ApplyEnvironment(IDictionary<string, string?> destination, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

    private static bool IsTerminal(JobState value) => value is JobState.Exited or JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot or JobState.HostCrashed;

    private void SetTerminalFailure(RemoteError failure)
    {
        lock (stateGate)
        {
            error = failure;
            state = startedAtUtc is null ? JobState.FailedToStart : JobState.HostCrashed;
            finishedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}

public sealed class TaskHostSegmentWriter
{
    private readonly string dataRoot;

    public TaskHostSegmentWriter(string dataRoot)
    {
        this.dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
    }

    public async Task<TaskOutputSegment> WriteAsync(
        string jobId,
        JobOutputKind stream,
        long startOffset,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || jobId.Contains(Path.DirectorySeparatorChar) || jobId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Job ID is not valid for an output segment path.", nameof(jobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);

        var streamName = stream == JobOutputKind.Stdout ? "stdout" : "stderr";
        var fileName = $"{startOffset:D20}-{Guid.NewGuid():N}.seg";
        var relativePath = $"segments/{jobId}/{streamName}/{fileName}";
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // 原子写：先写同目录临时文件再改名。此前直接写目标路径时，Agent 侧
        // RegisterTaskHostOutputSegmentsAsync 的两次长度读取可能落在写入中间
        // （枚举时半截、校验时全长），触发 "segment length does not match" 竞态。
        // 临时文件以 .tmp- 结尾，不会被 *.seg 枚举匹配，不会误注册。
        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temporaryPath, data.ToArray(), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, fullPath, overwrite: true);
        var createdAtUtc = new FileInfo(fullPath).CreationTimeUtc;
        return new TaskOutputSegment(jobId, stream, relativePath, startOffset, data.Length, createdAtUtc);
    }

    private string GetFullPath(string relativePath)
    {
        var segmentsRoot = Path.GetFullPath(Path.Combine(dataRoot, "segments"));
        var fullPath = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
        var prefix = segmentsRoot.EndsWith(Path.DirectorySeparatorChar) ? segmentsRoot : segmentsRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Output segment paths must remain under the segments root.");
        }

        return fullPath;
    }
}

public static class TaskHostPipeProtocol
{
    private const int MaxMessageBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException("Task host pipe message exceeds the size limit.");
        }

        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException("Task host pipe message has an invalid length.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, ContractJson.Options)
            ?? throw new InvalidDataException("Task host pipe message was empty.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Task host pipe closed before a full message was received.");
            }

            offset += read;
        }
    }
}

public static class TaskHostControlClient
{
    public static async Task<TaskControlResponse> SendAsync(
        string pipeName,
        TaskControlMessage message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        await TaskHostPipeProtocol.WriteAsync(client, message, timeoutSource.Token).ConfigureAwait(false);
        return await TaskHostPipeProtocol.ReadAsync<TaskControlResponse>(client, timeoutSource.Token).ConfigureAwait(false);
    }
}
