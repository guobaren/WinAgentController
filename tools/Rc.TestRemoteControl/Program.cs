using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

const string TargetIp = "192.168.3.50";
const string AllowedControllerIp = "192.168.3.47";
const int Port = 43002;
const int MaximumMessageBytes = 1024 * 1024;
const int MaximumOutputCharacters = 128 * 1024;
const string ProtocolMarker = "RC_TEST_REMOTE_V1";

const string Usage = """
Rc.TestRemoteControl.exe listen
Rc.TestRemoteControl.exe status
Rc.TestRemoteControl.exe recover
Rc.TestRemoteControl.exe exec --shell <powershell|cmd> --command <command> [--timeout-seconds <1-300>]
Rc.TestRemoteControl.exe self-test

控制端目标固定为 192.168.3.50:43002；listen 仅接受 192.168.3.47。
这是可信测试网专用工具，不属于 RemoteController 发布件。
""";

try
{
    if (args.Length == 1 && args[0].Equals("listen", StringComparison.OrdinalIgnoreCase))
    {
        if (!IsAdministrator())
        {
            RelaunchListenerElevated();
            return 0;
        }
        await RunListenerAsync();
        return 0;
    }
    if (args.Length == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
    {
        return await SendAsync(new TestRequest(ProtocolMarker, "status", "powershell", Shared.StatusCommand, 30));
    }
    if (args.Length == 1 && args[0].Equals("recover", StringComparison.OrdinalIgnoreCase))
    {
        return await SendAsync(new TestRequest(ProtocolMarker, "recover", "powershell", Shared.RecoverCommand, 60));
    }
    if (args.Length >= 1 && args[0].Equals("exec", StringComparison.OrdinalIgnoreCase))
    {
        var options = ParseOptions(args[1..]);
        var shell = options.GetValueOrDefault("shell") ?? "powershell";
        var command = options.GetValueOrDefault("command") ?? throw new ArgumentException("--command is required.");
        var timeout = int.TryParse(options.GetValueOrDefault("timeout-seconds"), out var parsed) ? parsed : 60;
        ValidateRequest(shell, command, timeout);
        return await SendAsync(new TestRequest(ProtocolMarker, "exec", shell, command, timeout));
    }
    if (args.Length == 1 && args[0].Equals("self-test", StringComparison.OrdinalIgnoreCase))
    {
        var result = await RunSelfTestAsync();
        var passed = result.Ok && result.ExitCode == 0 && result.StandardOutput.Contains("RC_TEST_REMOTE_OK", StringComparison.Ordinal);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = passed, result }, Shared.JsonOptions));
        return passed ? 0 : 1;
    }

    Console.Error.WriteLine(Usage);
    return 2;
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or SocketException or OperationCanceledException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task RunListenerAsync()
{
    var listener = new TcpListener(IPAddress.Any, Port);
    listener.Start();
    Console.WriteLine($"Test listener ready on TCP {Port}; allowed controller: {AllowedControllerIp}.");
    Console.WriteLine("Keep this elevated window open during remote tests.");
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = HandleClientSafelyAsync(client);
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static void RelaunchListenerElevated()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
    var start = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = true,
        Verb = "runas",
    };
    start.ArgumentList.Add("listen");
    try
    {
        Process.Start(start);
        Console.WriteLine("已请求管理员权限；请在对端确认 UAC 提示。");
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        throw new InvalidOperationException("管理员权限请求被取消或启动失败。", exception);
    }
}

static async Task HandleClientSafelyAsync(TcpClient client)
{
    using (client)
    {
        try
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString();
            if (!string.Equals(remote, AllowedControllerIp, StringComparison.Ordinal))
            {
                return;
            }

            var request = await ReadMessageAsync<TestRequest>(client.GetStream());
            if (request.Marker != ProtocolMarker)
            {
                await WriteMessageAsync(client.GetStream(), new TestResponse(false, null, false, "", "", "Protocol marker mismatch."));
                return;
            }
            ValidateRequest(request.Shell, request.Command, request.TimeoutSeconds);
            var result = await ExecuteAsync(request.Shell, request.Command, request.TimeoutSeconds, CancellationToken.None);
            var ok = !result.TimedOut && result.ExitCode == 0;
            await WriteMessageAsync(client.GetStream(), new TestResponse(
                ok,
                result.ExitCode,
                result.TimedOut,
                result.StandardOutput,
                result.StandardError,
                ok ? null : result.TimedOut ? "Command timed out." : "Command returned a non-zero exit code."));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            try { await WriteMessageAsync(client.GetStream(), new TestResponse(false, null, false, "", "", exception.Message)); }
            catch { }
        }
    }
}

static async Task<int> SendAsync(TestRequest request)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var client = new TcpClient(AddressFamily.InterNetwork);
    await client.ConnectAsync(IPAddress.Parse(TargetIp), Port, timeout.Token);
    await WriteMessageAsync(client.GetStream(), request, timeout.Token);
    using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds + 10));
    var response = await ReadMessageAsync<TestResponse>(client.GetStream(), responseTimeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(response, Shared.JsonOptions));
    return response.Ok ? 0 : 1;
}

static async Task<TestResponse> RunSelfTestAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    var server = Task.Run(async () =>
    {
        using var accepted = await listener.AcceptTcpClientAsync();
        var request = await ReadMessageAsync<TestRequest>(accepted.GetStream());
        var execution = await ExecuteAsync(request.Shell, request.Command, request.TimeoutSeconds, CancellationToken.None);
        var response = new TestResponse(
            !execution.TimedOut && execution.ExitCode == 0,
            execution.ExitCode,
            execution.TimedOut,
            execution.StandardOutput,
            execution.StandardError,
            null);
        await WriteMessageAsync(accepted.GetStream(), response);
    });
    try
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(endpoint.Address, endpoint.Port);
        await WriteMessageAsync(client.GetStream(), new TestRequest(ProtocolMarker, "self-test", "cmd", "echo RC_TEST_REMOTE_OK", 10));
        var response = await ReadMessageAsync<TestResponse>(client.GetStream());
        await server;
        return response;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task<ExecutionResult> ExecuteAsync(string shell, string command, int timeoutSeconds, CancellationToken cancellationToken)
{
    ValidateRequest(shell, command, timeoutSeconds);
    var start = new ProcessStartInfo
    {
        FileName = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    if (shell.Equals("cmd", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(command);
    }
    else
    {
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Command process did not start.");
    var stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
    var stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
    var timedOut = false;
    try
    {
        await process.WaitForExitAsync(timeout.Token);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        timedOut = true;
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await process.WaitForExitAsync(CancellationToken.None);
    }
    return new ExecutionResult(
        timedOut ? null : process.ExitCode,
        timedOut,
        await stdoutTask,
        await stderrTask);
}

static void ValidateRequest(string shell, string command, int timeoutSeconds)
{
    if (!shell.Equals("cmd", StringComparison.OrdinalIgnoreCase) && !shell.Equals("powershell", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Shell must be cmd or powershell.");
    }
    if (string.IsNullOrWhiteSpace(command)) { throw new ArgumentException("Command cannot be empty."); }
    if (timeoutSeconds is < 1 or > 300) { throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be 1-300 seconds."); }
}

static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
{
    var buffer = new char[8192];
    var output = new StringBuilder();
    while (true)
    {
        var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (count == 0) { break; }
        var remaining = MaximumOutputCharacters - output.Length;
        if (remaining > 0) { output.Append(buffer, 0, Math.Min(remaining, count)); }
    }
    return output.ToString();
}

static async Task WriteMessageAsync<T>(NetworkStream stream, T value, CancellationToken cancellationToken = default)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(value, Shared.JsonOptions);
    if (payload.Length > MaximumMessageBytes) { throw new InvalidDataException("Message is too large."); }
    var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
    await stream.WriteAsync(length, cancellationToken);
    await stream.WriteAsync(payload, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<T> ReadMessageAsync<T>(NetworkStream stream, CancellationToken cancellationToken = default)
{
    var lengthBytes = new byte[sizeof(int)];
    await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
    var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes));
    if (length is < 1 or > MaximumMessageBytes) { throw new InvalidDataException("Invalid message length."); }
    var payload = new byte[length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return JsonSerializer.Deserialize<T>(payload, Shared.JsonOptions) ?? throw new InvalidDataException("Invalid JSON message.");
}

static Dictionary<string, string?> ParseOptions(string[] arguments)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
        {
            throw new ArgumentException(Usage);
        }
        result[arguments[index][2..]] = arguments[++index];
    }
    return result;
}

sealed record TestRequest(string Marker, string Operation, string Shell, string Command, int TimeoutSeconds);
sealed record TestResponse(bool Ok, int? ExitCode, bool TimedOut, string StandardOutput, string StandardError, string? Error);
sealed record ExecutionResult(int? ExitCode, bool TimedOut, string StandardOutput, string StandardError);

static class Shared
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
    public const string StatusCommand = "$services=Get-Service RemoteControllerBroker,RemoteControllerAgent -ErrorAction SilentlyContinue | ForEach-Object {[pscustomobject]@{Name=$_.Name;Status=[string]$_.Status}}; $task=Get-ScheduledTask RemoteControllerUiAgent -ErrorAction SilentlyContinue; [pscustomobject]@{Services=@($services);UiTask=if($task){[string]$task.State}else{$null}} | ConvertTo-Json -Depth 4 -Compress";
    public const string RecoverCommand = "$ErrorActionPreference='Stop'; Start-Service RemoteControllerBroker; Start-Service RemoteControllerAgent; $task=Get-ScheduledTask RemoteControllerUiAgent -ErrorAction SilentlyContinue; if($task){Start-ScheduledTask RemoteControllerUiAgent}; Get-Service RemoteControllerBroker,RemoteControllerAgent | ForEach-Object {[pscustomobject]@{Name=$_.Name;Status=[string]$_.Status}} | ConvertTo-Json -Compress";
}
