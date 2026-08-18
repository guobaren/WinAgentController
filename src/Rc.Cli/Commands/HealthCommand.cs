using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

/// <summary>
/// 运维健康探测：组合服务状态、控制端口监听与磁盘空间三条只读探测，
/// 避免逐一拼装 exec 命令。探测均为短命令，单条受 Agent 侧 exec 超时保护。
/// </summary>
public static class HealthCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 1 || !IPEndPoint.TryParse(args[0], out var endpoint))
        {
            await error.WriteLineAsync(Usage());
            return 2;
        }

        var text = args.Contains("--text", StringComparer.Ordinal);
        var fingerprintIndex = Array.FindIndex(args, value => string.Equals(value, "--fingerprint", StringComparison.Ordinal));
        var fingerprint = fingerprintIndex >= 0 && fingerprintIndex + 1 < args.Length
            ? NormalizeFingerprint(args[fingerprintIndex + 1])
            : null;
        if (fingerprint is null)
        {
            await error.WriteLineAsync("A SHA-256 TLS fingerprint is required for health.");
            return 2;
        }

        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint);
            var services = await RunProbeAsync(connection,
                ExecRequest.ForShell(ShellKind.Cmd, "sc query RemoteControllerAgent & sc query RemoteControllerBroker"), error);
            var port = await RunProbeAsync(connection,
                ExecRequest.ForShell(ShellKind.Cmd, "netstat -ano | findstr :43001"), error);
            var disk = await RunProbeAsync(connection,
                ExecRequest.ForShell(ShellKind.PowerShell,
                    "[IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady } | ForEach-Object { '{0} total={1:N1}GB free={2:N1}GB' -f $_.Name, ($_.TotalSize/1GB), ($_.AvailableFreeSpace/1GB) }"), error);

            var (runningServices, listening, healthy) = Evaluate(services, port);

            if (text)
            {
                await output.WriteLineAsync($"[services] running={runningServices}/2");
                await output.WriteLineAsync(services.Trim());
                await output.WriteLineAsync($"[port 43001] listening={listening}");
                await output.WriteLineAsync(port.Trim());
                await output.WriteLineAsync("[disk]");
                await output.WriteLineAsync(disk.Trim());
                await output.WriteLineAsync($"[health] {(healthy ? "OK" : "FAIL")}");
                return healthy ? 0 : 1;
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(new
            {
                services = services.Trim(),
                runningServices,
                portListening = listening,
                port = port.Trim(),
                disk = disk.Trim(),
                healthy,
            }), ContractJson.Options));
            return healthy ? 0 : 1;
        }
        catch (AuthenticationException exception)
        {
            await error.WriteLineAsync($"TLS authentication failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Health check failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>判定健康状态：Agent/Broker 两个服务均 RUNNING 且控制端口 LISTENING 才算 OK。</summary>
    internal static (int RunningServices, bool PortListening, bool Healthy) Evaluate(string servicesOutput, string portOutput)
    {
        var runningServices = servicesOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase));
        var listening = portOutput.Contains("LISTENING", StringComparison.OrdinalIgnoreCase);
        return (runningServices, listening, runningServices >= 2 && listening);
    }

    private static async Task<string> RunProbeAsync(AuthenticatedControlConnection connection, ExecRequest execution, TextWriter error)
    {
        var request = new ControlExecuteOnceRequest(1, connection.ControllerId, execution, []);
        var response = await connection.SendAsync<ControlExecuteOnceResponse>(request);
        var combined = TextDecoding.Decode(response.StandardOutput) + TextDecoding.Decode(response.StandardError);
        return string.IsNullOrWhiteSpace(combined) ? "(no output)" : combined;
    }

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }

    private static string Usage() =>
        "Usage: rcctl health <IP:port> --fingerprint <SHA256> [--text]";
}
