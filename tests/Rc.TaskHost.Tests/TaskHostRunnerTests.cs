using System.Diagnostics;
using System.Text;
using Rc.Contracts;
using Rc.TaskHost;
using Xunit;

namespace Rc.TaskHost.Tests;

public sealed class TaskHostRunnerTests
{
    [Fact]
    public async Task DirectArgvCapturesRawStdoutAndStderrAtTheirOwnOffsets()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForDirectArgv(
            ["cmd.exe", "/d", "/c", "echo hello & echo warning 1>&2"]));

        var status = await runner.RunAsync();

        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.Equal(0, status.Job.ExitCode);
        Assert.Equal("hello \r\n", await fixture.ReadOutputAsync("stdout"));
        Assert.Equal("warning \r\n", await fixture.ReadOutputAsync("stderr"));
        Assert.Equal(0, await fixture.FirstOffsetAsync("stdout"));
        Assert.Equal(0, await fixture.FirstOffsetAsync("stderr"));
        Assert.Equal(status.StdoutLength, Encoding.UTF8.GetByteCount("hello \r\n"));
    }

    [Fact]
    public async Task PowerShellShellModeRunsWithoutStringJoining()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(ShellKind.PowerShell, "[Console]::Out.Write('shell works')"));

        var status = await runner.RunAsync();

        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.Equal("shell works", await fixture.ReadOutputAsync("stdout"));
    }

    [Fact]
    public async Task OutputSegmentsUseContiguousOffsetsWhenOutputExceedsOneBuffer()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(
            ShellKind.PowerShell,
            "[Console]::Out.Write(('x' * 40000))"));

        var status = await runner.RunAsync();
        var segments = await fixture.SegmentsAsync("stdout");

        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.True(segments.Count >= 2);
        Assert.Equal(0, segments[0].Offset);
        Assert.Equal(40000, segments.Sum(segment => segment.Length));
        Assert.Equal(40000, status.StdoutLength);
        Assert.Equal(segments.Sum(segment => segment.Length), segments[^1].Offset + segments[^1].Length);
        Assert.All(segments.Skip(1).Zip(segments), pair => Assert.Equal(pair.Second.Offset + pair.Second.Length, pair.First.Offset));
    }

    [Fact]
    public async Task OutputCaptureCompletesWithoutAControllerReadingLogs()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(
            ShellKind.PowerShell,
            "[Console]::Out.Write(('z' * 524288))"));

        var status = await runner.RunAsync().WaitAsync(TimeSpan.FromSeconds(45));

        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.Equal(524288, status.StdoutLength);
        Assert.Equal(524288, (await fixture.SegmentsAsync("stdout")).Sum(segment => segment.Length));
    }
    [Fact]
    public async Task NamedPipeAcceptsTwoStandardInputWritesThenEof()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(
            ShellKind.PowerShell,
            "$first = [Console]::In.ReadLine(); $second = [Console]::In.ReadLine(); [Console]::Out.Write($first + '-' + $second)"));
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));

        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.StandardInput, Encoding.UTF8.GetBytes("one\n")));
        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.StandardInput, Encoding.UTF8.GetBytes("two\n")));
        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.CloseStandardInput));
        var status = await completion;

        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.Equal("one-two", await fixture.ReadOutputAsync("stdout"));
    }

    [Fact]
    public async Task RejectedControlCommandDoesNotChangeTheTaskResultOrStopStatusQueries()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(
            ShellKind.PowerShell,
            "Start-Sleep -Milliseconds 500"));
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));

        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.CloseStandardInput));
        var rejected = await fixture.SendAsync(new TaskControlMessage(
            TaskControlKind.StandardInput,
            Encoding.UTF8.GetBytes("late input")));
        var running = await fixture.SendAsync(new TaskControlMessage(TaskControlKind.GetStatus));
        var final = await completion;

        Assert.NotNull(rejected.Error);
        Assert.Equal(ErrorCode.FailedPrecondition, rejected.Error!.Code);
        Assert.Equal(JobState.Running, running.Status.Job.State);
        Assert.Null(running.Error);
        Assert.Equal(JobState.Exited, final.Job.State);
        Assert.Null(final.Job.Error);
    }

    [Fact]
    public async Task StatusReportsRunningProcessAndCancellationTerminatesTreeAfterGracePeriod()
    {
        await using var fixture = new TaskHostFixture(cancellationGracePeriod: TimeSpan.FromMilliseconds(100));
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(ShellKind.PowerShell, "Start-Sleep -Seconds 30"));
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));

        var running = await fixture.SendAsync(new TaskControlMessage(TaskControlKind.GetStatus));
        Assert.Equal(JobState.Running, running.Status.Job.State);
        Assert.NotNull(running.Status.ProcessId);

        var cancelled = await fixture.SendAsync(new TaskControlMessage(TaskControlKind.Cancel));
        var final = await completion;

        Assert.Equal(JobState.Cancelled, cancelled.Status.Job.State);
        Assert.Equal(JobState.Cancelled, final.Job.State);
    }

    [Fact]
    public async Task PseudoConsoleRunsInteractiveShellAndAcceptsResize()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForDirectArgv(
            ["cmd.exe", "/d", "/q"],
            terminal: new TerminalOptions(100, 32)));
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));
        var initial = runner.GetStatus();
        Assert.True(initial.Job.State == JobState.Running, $"state={initial.Job.State} exit={initial.Job.ExitCode} error={initial.Job.Error?.Message}");

        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.StandardInput, Encoding.UTF8.GetBytes("echo pty-ready\r")));
        var terminalOutput = string.Empty;
        for (var attempt = 0; attempt < 100 && !terminalOutput.Contains("pty-ready", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(20);
            terminalOutput = await fixture.ReadOutputAsync("stdout");
        }
        var beforeResize = runner.GetStatus();
        Assert.True(beforeResize.Job.State == JobState.Running, $"state={beforeResize.Job.State} exit={beforeResize.Job.ExitCode} output={terminalOutput}");
        var resized = await fixture.SendAsync(new TaskControlMessage(TaskControlKind.ResizeTerminal, columns: 132, rows: 43));
        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.StandardInput, Encoding.UTF8.GetBytes("echo pty-resized\r")));
        for (var attempt = 0; attempt < 100 && !terminalOutput.Contains("pty-resized", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(20);
            terminalOutput = await fixture.ReadOutputAsync("stdout");
        }
        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.StandardInput, Encoding.UTF8.GetBytes("exit\r")));
        var status = await completion;

        Assert.Null(resized.Error);
        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.Contains("pty-ready", terminalOutput, StringComparison.Ordinal);
        Assert.Contains("pty-resized", terminalOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await fixture.ReadOutputAsync("stderr"));
    }

    [Fact]
    public async Task PseudoConsoleCancellationSendsInterruptBeforeProcessTreeKill()
    {
        await using var fixture = new TaskHostFixture(cancellationGracePeriod: TimeSpan.FromSeconds(20));
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(
            ShellKind.Cmd,
            "ping 127.0.0.1 -n 30 > nul",
            terminal: new TerminalOptions()));
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));
        var initial = runner.GetStatus();
        Assert.True(initial.Job.State == JobState.Running, $"state={initial.Job.State} exit={initial.Job.ExitCode} error={initial.Job.Error?.Message}");
        var startedAt = DateTimeOffset.UtcNow;

        var cancelled = await fixture.SendAsync(new TaskControlMessage(TaskControlKind.Cancel));
        var status = await completion;

        Assert.Equal(JobState.Cancelled, cancelled.Status.Job.State);
        Assert.Equal(JobState.Cancelled, status.Job.State);
        Assert.True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(4), "ConPTY Ctrl+C did not terminate before the force-kill grace period.");
    }
    [Fact]
    public async Task PseudoConsoleCancellationForceKillsAfterGraceWhenInterruptIsIgnored()
    {
        const string readyMarker = "ctrl-c-ignored-ready";
        await using var fixture = new TaskHostFixture(cancellationGracePeriod: TimeSpan.FromMilliseconds(200));
        await using var runner = fixture.CreateRunner(ExecRequest.ForDirectArgv(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", $"[Console]::TreatControlCAsInput=$true; [Console]::Out.WriteLine('{readyMarker}'); [Console]::Out.Flush(); Start-Sleep -Seconds 30"],
            terminal: new TerminalOptions()));
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(JobState.Running, runner.GetStatus().Job.State);

        var terminalOutput = string.Empty;
        for (var attempt = 0; attempt < 100 && !terminalOutput.Contains(readyMarker, StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(20);
            terminalOutput = await fixture.ReadOutputAsync("stdout");
        }
        Assert.Contains(readyMarker, terminalOutput, StringComparison.Ordinal);

        var stopwatch = Stopwatch.StartNew();

        await fixture.SendAsync(new TaskControlMessage(TaskControlKind.Cancel));
        var status = await completion;
        stopwatch.Stop();

        Assert.Equal(JobState.Cancelled, status.Job.State);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150), $"Cancellation returned before the configured grace period: {stopwatch.Elapsed}.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Force-kill fallback took too long: {stopwatch.Elapsed}.");
    }
    [Fact]
    public async Task OutputBeyondConfiguredLimitIsDrainedAndMarkedTruncated()
    {
        await using var fixture = new TaskHostFixture(maximumOutputBytes: 1024);
        await using var runner = fixture.CreateRunner(ExecRequest.ForShell(
            ShellKind.PowerShell,
            "[Console]::Out.Write(('x' * 5000))"));

        var status = await runner.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobState.Exited, status.Job.State);
        Assert.True(status.OutputTruncated);
        Assert.Equal(1024, status.StdoutLength + status.StderrLength);
        Assert.Equal(1024, (await fixture.SegmentsAsync("stdout")).Sum(segment => segment.Length));
    }
    [Fact]
    public async Task FailedStartProducesDurableFailureStatus()
    {
        await using var fixture = new TaskHostFixture();
        await using var runner = fixture.CreateRunner(ExecRequest.ForDirectArgv(["does-not-exist-rc.exe"]));

        var status = await runner.RunAsync();

        Assert.Equal(JobState.FailedToStart, status.Job.State);
        Assert.NotNull(status.Job.Error);
        Assert.Equal(ErrorCode.Internal, status.Job.Error!.Code);
    }

    private sealed class TaskHostFixture : IAsyncDisposable
    {
        private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "rc-taskhost-tests", Guid.NewGuid().ToString("N"));
        private readonly string pipeName = "rc-taskhost-test-" + Guid.NewGuid().ToString("N");
        private readonly TimeSpan cancellationGracePeriod;
        private readonly long maximumOutputBytes;
        private readonly string jobId = "job-" + Guid.NewGuid().ToString("N");

        public TaskHostFixture(TimeSpan? cancellationGracePeriod = null, long maximumOutputBytes = 200L * 1024 * 1024)
        {
            this.cancellationGracePeriod = cancellationGracePeriod ?? TimeSpan.FromSeconds(1);
            this.maximumOutputBytes = maximumOutputBytes;
        }

        public TaskHostRunner CreateRunner(ExecRequest execution) => new(new TaskLaunchRequest(
            jobId,
            execution,
            ExecutionIdentity.CurrentUser,
            dataRoot,
            pipeName,
            cancellationGracePeriod,
            maximumOutputBytes: maximumOutputBytes));

        public Task<TaskControlResponse> SendAsync(TaskControlMessage message) =>
            TaskHostControlClient.SendAsync(pipeName, message, TimeSpan.FromSeconds(20));

        public async Task<string> ReadOutputAsync(string stream)
        {
            var directory = Path.Combine(dataRoot, "segments", jobId, stream);
            if (!Directory.Exists(directory))
            {
                return string.Empty;
            }

            var output = new MemoryStream();
            foreach (var file in Directory.GetFiles(directory, "*.seg").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                await output.WriteAsync(await File.ReadAllBytesAsync(file));
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }

        public Task<long> FirstOffsetAsync(string stream)
        {
            var directory = Path.Combine(dataRoot, "segments", jobId, stream);
            var file = Directory.GetFiles(directory, "*.seg").OrderBy(Path.GetFileName, StringComparer.Ordinal).First();
            return Task.FromResult(long.Parse(Path.GetFileName(file).Split('-', 2)[0], System.Globalization.CultureInfo.InvariantCulture));
        }

        public Task<IReadOnlyList<(long Offset, long Length)>> SegmentsAsync(string stream)
        {
            var directory = Path.Combine(dataRoot, "segments", jobId, stream);
            IReadOnlyList<(long Offset, long Length)> segments = Directory.GetFiles(directory, "*.seg")
                .Select(file => (
                    long.Parse(Path.GetFileName(file).Split('-', 2)[0], System.Globalization.CultureInfo.InvariantCulture),
                    new FileInfo(file).Length))
                .OrderBy(segment => segment.Item1)
                .ToArray();
            return Task.FromResult(segments);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
