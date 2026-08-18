using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

/// <summary>CLI surface for non-blocking TaskHost jobs.</summary>
public static class JobCommand
{
    private const int MaximumLineLength = 1024 * 1024;

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        args.Length == 0
            ? WriteUsageAsync(error)
            : args[0] switch
            {
                "start" => StartAsync(args[1..], output, error),
                "status" => StatusAsync(args[1..], output, error),
                "list" => ListAsync(args[1..], output, error),
                "logs" => JobExtendedCommands.LogsAsync(args[1..], output, error),
                "input" => JobExtendedCommands.InputAsync(args[1..], output, error),
                "close-input" => JobExtendedCommands.CloseInputAsync(args[1..], output, error),
                "cancel" => JobExtendedCommands.CancelAsync(args[1..], output, error),
                "wait" => JobExtendedCommands.WaitAsync(args[1..], output, error),
                "resize" => JobExtendedCommands.ResizeAsync(args[1..], output, error),
                _ => WriteUsageAsync(error),
            };

    private static async Task<int> StartAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParseStart(args, out var endpoint, out var fingerprint, out var execution, out var text, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }
        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint!, fingerprint!);
            var response = await connection.SendAsync<ControlJobStartResponse>(new ControlJobStartRequest(1, connection.ControllerId, execution!, []));
            if (text)
            {
                await WriteRuntimeStatusAsync(output, response.Status, "started");
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
            }
            // 启动后立即终态且零输出时给出诊断提示（命令可能被转义破坏、默认
            // shell 不支持 &&/||、或 shell 启动即失败）。
            if (response.Status.Job.State is JobState.Exited or JobState.FailedToStart
                && response.Status.StdoutLength == 0 && response.Status.StderrLength == 0)
            {
                await error.WriteLineAsync("[rcctl] note: the job ended immediately with zero output; check the command for nested quotes/escaping, '&&'/'||' under PowerShell 5.1, and use 'rcctl job logs --job <jobId> --stream stderr' to inspect diagnostics.");
            }
            return response.Status.Job.State == JobState.FailedToStart ? 1 : 0;
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            await error.WriteLineAsync(ToUserMessage(exception));
            return 1;
        }
    }
    private static async Task<int> StatusAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParseStatus(args, out var endpoint, out var fingerprint, out var jobId, out var text, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }
        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint!, fingerprint!);
            var response = await connection.SendAsync<ControlJobStatusResponse>(new ControlJobStatusRequest(1, connection.ControllerId, jobId!, []));
            if (text)
            {
                await WriteRuntimeStatusAsync(output, response.Status, response.IsActive ? "active" : "stored");
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
            }
            return 0;
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            await error.WriteLineAsync(ToUserMessage(exception));
            return 1;
        }
    }
    private static async Task<int> ListAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParseList(args, out var endpoint, out var fingerprint, out var state, out var text, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }
        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint!, fingerprint!);
            var response = await connection.SendAsync<ControlJobListResponse>(new ControlJobListRequest(1, connection.ControllerId, state, []));
            if (text)
            {
                foreach (var job in response.Jobs)
                {
                    await output.WriteLineAsync($"{job.JobId} state={job.State} exitCode={job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} created={job.CreatedAtUtc:O}");
                }
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
            }
            return 0;
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            await error.WriteLineAsync(ToUserMessage(exception));
            return 1;
        }
    }
    private static async Task WriteRuntimeStatusAsync(TextWriter output, TaskRuntimeStatus status, string source)
    {
        var job = status.Job;
        await output.WriteLineAsync($"[rcctl] jobId={job.JobId} state={job.State} exitCode={job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} source={source}");
        await output.WriteLineAsync($"[rcctl] pid={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} stdoutBytes={status.StdoutLength} stderrBytes={status.StderrLength} lastOutput={status.LastOutputAtUtc?.ToString("O") ?? "n/a"}");
        if (job.Error is not null)
        {
            await output.WriteLineAsync($"[rcctl] error={job.Error.Code}: {job.Error.Message}");
        }
    }

    private static bool TryParseStart(string[] args, out IPEndPoint? endpoint, out string? fingerprint, out ExecRequest? execution, out bool text, out string? error)
    {
        endpoint = null;
        fingerprint = null;
        execution = null;
        text = false;
        error = null;
        if (!TryParseEndpoint(args, out endpoint))
        {
            error = Usage();
            return false;
        }

        var shell = ShellKind.PowerShell;
        string? command = null;
        string? workingDirectory = null;
        var usePseudoConsole = false;
        var elevated = false;
        var columns = 120;
        var rows = 30;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--shell" when index + 1 < args.Length:
                    if (!Enum.TryParse<ShellKind>(args[++index], true, out shell))
                    {
                        error = "--shell must be powershell or cmd.";
                        return false;
                    }
                    break;
                case "--command" when index + 1 < args.Length:
                    command = args[++index];
                    break;
                case "--workdir" when index + 1 < args.Length:
                    workingDirectory = args[++index];
                    break;
                case "--pty":
                    usePseudoConsole = true;
                    break;
                case "--cols" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out columns) || columns is < 1 or > 1000)
                    {
                        error = "--cols must be between 1 and 1000.";
                        return false;
                    }
                    usePseudoConsole = true;
                    break;
                case "--rows" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out rows) || rows is < 1 or > 1000)
                    {
                        error = "--rows must be between 1 and 1000.";
                        return false;
                    }
                    usePseudoConsole = true;
                    break;
                case "--elevated":
                    elevated = true;
                    break;
                case "--text":
                    text = true;
                    break;
                default:
                    error = Usage();
                    return false;
            }
        }

        if (fingerprint is null || string.IsNullOrWhiteSpace(command))
        {
            error = fingerprint is null ? "A SHA-256 TLS fingerprint is required for job commands." : "--command is required.";
            return false;
        }

        // 默认远程 shell 为 Windows PowerShell 5.1：`&&`/`||` 不是有效分隔符，
        // 提前提示，避免命令在远端解析失败且零输出时难以定位。
        if (shell == ShellKind.PowerShell && (command.Contains("&&", StringComparison.Ordinal) || command.Contains("||", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("[rcctl] warning: the default remote shell is Windows PowerShell 5.1, which does not support '&&'/'||'; use --shell cmd or ';' instead.");
        }

        try
        {
            execution = new ExecRequest(
                directArgv: null,
                shell: new ShellLaunch(shell, command),
                workingDirectory,
                environment: null,
                elevated ? ExecutionIdentity.ElevatedBroker : ExecutionIdentity.CurrentUser,
                usePseudoConsole ? new TerminalOptions(columns, rows) : null);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryParseStatus(string[] args, out IPEndPoint? endpoint, out string? fingerprint, out string? jobId, out bool text, out string? error)
    {
        endpoint = null;
        fingerprint = null;
        jobId = null;
        text = false;
        error = null;
        if (!TryParseEndpoint(args, out endpoint))
        {
            error = Usage();
            return false;
        }

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--job" when index + 1 < args.Length:
                    jobId = args[++index];
                    break;
                case "--text":
                    text = true;
                    break;
                default:
                    error = Usage();
                    return false;
            }
        }

        if (fingerprint is null || string.IsNullOrWhiteSpace(jobId))
        {
            error = fingerprint is null ? "A SHA-256 TLS fingerprint is required for job commands." : "--job is required.";
            return false;
        }

        return true;
    }

    private static bool TryParseList(string[] args, out IPEndPoint? endpoint, out string? fingerprint, out JobState? state, out bool text, out string? error)
    {
        endpoint = null;
        fingerprint = null;
        state = null;
        text = false;
        error = null;
        if (!TryParseEndpoint(args, out endpoint))
        {
            error = Usage();
            return false;
        }

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--state" when index + 1 < args.Length:
                    if (!Enum.TryParse<JobState>(args[++index], true, out var parsedState))
                    {
                        error = "--state must be Queued, Running, Exited, FailedToStart, Cancelled, InterruptedByReboot, or HostCrashed.";
                        return false;
                    }
                    state = parsedState;
                    break;
                case "--text":
                    text = true;
                    break;
                default:
                    error = Usage();
                    return false;
            }
        }

        if (fingerprint is null)
        {
            error = "A SHA-256 TLS fingerprint is required for job commands.";
            return false;
        }

        return true;
    }

    private static bool TryParseEndpoint(string[] args, out IPEndPoint? endpoint)
    {
        endpoint = null;
        return args.Length > 0 && IPEndPoint.TryParse(args[0], out endpoint);
    }

    private static bool IsExpectedConnectionException(Exception exception) =>
        exception is AuthenticationException or SocketException or InvalidOperationException or CryptographicException;

    private static string ToUserMessage(Exception exception) => exception switch
    {
        AuthenticationException authentication => $"TLS authentication failed: {authentication.Message}",
        SocketException socket => $"Unable to connect: {socket.Message}",
        CryptographicException cryptographic => $"Controller identity failed: {cryptographic.Message}",
        _ => $"Command was rejected: {exception.Message}",
    };

    private static Task<int> WriteUsageAsync(TextWriter error)
    {
        error.WriteLine(Usage());
        return Task.FromResult(2);
    }

    private static string Usage() =>
        "Usage: rcctl job start <IP:port> --fingerprint <SHA256> --command <command> [--shell powershell|cmd] [--workdir <path>] [--elevated] [--pty] [--cols <1-1000>] [--rows <1-1000>] [--text] | rcctl job status <IP:port> --fingerprint <SHA256> --job <jobId> [--text] | rcctl job list <IP:port> --fingerprint <SHA256> [--state <JobState>] [--text]";

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized.ToUpperInvariant() : null;
    }
}
