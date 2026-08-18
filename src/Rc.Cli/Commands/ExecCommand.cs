using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class ExecCommand
{
    private const int MaximumLineLength = 1024 * 1024;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParseArguments(args, out var endpoint, out var fingerprint, out var execution, out var text, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }

        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint!, fingerprint!);
            var request = new ControlExecuteOnceRequest(1, connection.ControllerId, execution!, []);
            var response = await connection.SendAsync<ControlExecuteOnceResponse>(request);
            if (text)
            {
                await output.WriteAsync(TextDecoding.Decode(response.StandardOutput));
                await error.WriteAsync(TextDecoding.Decode(response.StandardError));
                await error.WriteLineAsync($"[rcctl] jobId={response.Job.JobId} state={response.Job.State} exitCode={response.Job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
                if (response.StandardOutputTruncated || response.StandardErrorTruncated)
                {
                    await error.WriteLineAsync("[rcctl] Output was truncated to 256 KiB per stream. The complete captured output remains on the agent.");
                }
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
            }
            return response.Job.ExitCode ?? 1;
        }
        catch (AuthenticationException exception)
        {
            await error.WriteLineAsync($"TLS authentication failed: {exception.Message}");
            return 1;
        }
        catch (SocketException exception)
        {
            await error.WriteLineAsync($"Unable to connect: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync($"Command was rejected: {exception.Message}");
            return 1;
        }
        catch (CryptographicException exception)
        {
            await error.WriteLineAsync($"Controller identity failed: {exception.Message}");
            return 1;
        }
    }
    private static bool TryParseArguments(
        string[] args,
        out IPEndPoint? endpoint,
        out string? fingerprint,
        out ExecRequest? execution,
        out bool text,
        out string? error)
    {
        endpoint = null;
        fingerprint = null;
        execution = null;
        text = false;
        error = null;
        if (args.Length == 0 || !IPEndPoint.TryParse(args[0], out endpoint))
        {
            error = Usage();
            return false;
        }

        var shell = ShellKind.PowerShell;
        string? command = null;
        string? workingDirectory = null;
        var elevated = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--shell" when index + 1 < args.Length:
                    if (!Enum.TryParse<ShellKind>(args[++index], ignoreCase: true, out shell))
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

        if (fingerprint is null)
        {
            error = "A SHA-256 TLS fingerprint is required for exec.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "--command is required.";
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
            execution = elevated
                ? ExecRequest.ForShellWithIdentity(shell, command, ExecutionIdentity.ElevatedBroker, workingDirectory)
                : ExecRequest.ForShell(shell, command, workingDirectory);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string Usage() =>
        "Usage: rcctl exec <IP:port> --fingerprint <SHA256> --command <command> [--shell powershell|cmd] [--workdir <path>] [--elevated] [--text]";

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }
}