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

internal static class JobExtendedCommands
{
    private const int MaximumLineLength = 1024 * 1024;
    private const int DefaultMaximumBytes = 64 * 1024;

    public static Task<int> LogsAsync(string[] args, TextWriter output, TextWriter error) => RunAsync("logs", args, output, error);
    public static Task<int> InputAsync(string[] args, TextWriter output, TextWriter error) => RunAsync("input", args, output, error);
    public static Task<int> CloseInputAsync(string[] args, TextWriter output, TextWriter error) => RunAsync("close-input", args, output, error);
    public static Task<int> CancelAsync(string[] args, TextWriter output, TextWriter error) => RunAsync("cancel", args, output, error);
    public static Task<int> WaitAsync(string[] args, TextWriter output, TextWriter error) => RunAsync("wait", args, output, error);
    public static Task<int> ResizeAsync(string[] args, TextWriter output, TextWriter error) => RunAsync("resize", args, output, error);

    private static async Task<int> RunAsync(string operation, string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParse(operation, args, out var options, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }

        try
        {
            await using var connection = await AuthenticatedControlConnection.ConnectAsync(options.Endpoint!, options.Fingerprint!);
            if (operation == "logs")
            {
                while (true)
                {
                    var logRequest = new ControlJobLogsRequest(1, connection.ControllerId, options.JobId!, options.Stream, options.AfterOffset, options.MaximumBytes, []);
                    var response = await connection.SendAsync<ControlJobLogsResponse>(logRequest, retryOnDisconnect: options.Follow);

                    if (options.Text)
                    {
                        await output.WriteAsync(TextDecoding.Decode(response.Log.Chunk.Data));
                        await output.FlushAsync();
                    }
                    else
                    {
                        await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
                    }

                    if (!options.Follow || response.Log.Chunk.IsFinal)
                    {
                        return 0;
                    }

                    var madeProgress = response.Log.NextOffset > options.AfterOffset;
                    options.AfterOffset = response.Log.NextOffset;
                    if (!madeProgress)
                    {
                        await Task.Delay(250);
                    }
                }
            }

            object request = operation switch
            {
                "input" => new ControlJobInputRequest(1, connection.ControllerId, options.JobId!, Encoding.UTF8.GetBytes(options.Data!), []),
                "close-input" => new ControlJobCloseInputRequest(1, connection.ControllerId, options.JobId!, []),
                "cancel" => new ControlJobCancelRequest(1, connection.ControllerId, options.JobId!, []),
                "wait" => new ControlJobWaitRequest(1, connection.ControllerId, options.JobId!, options.Timeout, []),
                "resize" => new ControlJobResizeRequest(1, connection.ControllerId, options.JobId!, options.Columns, options.Rows, []),
                _ => throw new InvalidOperationException("Unsupported job operation."),
            };
            var operationResponse = await connection.SendAsync<ControlJobOperationResponse>(request);
            if (options.Text)
            {
                await output.WriteLineAsync($"[rcctl] jobId={operationResponse.Status.Job.JobId} state={operationResponse.Status.Job.State} exitCode={operationResponse.Status.Job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} completed={operationResponse.Completed}");
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(operationResponse), ContractJson.Options));
            }

            return 0;
        }
        catch (Exception exception) when (exception is AuthenticationException or SocketException or IOException or InvalidOperationException or CryptographicException)
        {
            await error.WriteLineAsync($"Job {operation} failed: {exception.Message}");
            return 1;
        }
    }

    private static bool TryParse(string operation, string[] args, out Options options, out string? error)
    {
        options = new Options();
        error = null;
        if (args.Length == 0 || !IPEndPoint.TryParse(args[0], out var endpoint))
        {
            error = Usage();
            return false;
        }

        options.Endpoint = endpoint;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    options.Fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--job" when index + 1 < args.Length:
                    options.JobId = args[++index];
                    break;
                case "--stream" when operation == "logs" && index + 1 < args.Length:
                    if (!Enum.TryParse<JobOutputKind>(args[++index], true, out var stream))
                    {
                        error = "--stream must be stdout or stderr.";
                        return false;
                    }
                    options.Stream = stream;
                    break;
                case "--after-offset" when operation == "logs" && index + 1 < args.Length:
                    if (!long.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
                    {
                        error = "--after-offset must be a non-negative integer.";
                        return false;
                    }
                    options.AfterOffset = offset;
                    break;
                case "--max-bytes" when operation == "logs" && index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var maximumBytes) || maximumBytes is < 1 or > 256 * 1024)
                    {
                        error = "--max-bytes must be between 1 and 262144.";
                        return false;
                    }
                    options.MaximumBytes = maximumBytes;
                    break;
                case "--data" when operation == "input" && index + 1 < args.Length:
                    options.Data = args[++index];
                    break;
                case "--timeout-ms" when operation == "wait" && index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs is < 0 or > 86_400_000)
                    {
                        error = "--timeout-ms must be between 0 and 86400000.";
                        return false;
                    }
                    options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                    break;
                case "--cols" when operation == "resize" && index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var columns) || columns is < 1 or > 1000)
                    {
                        error = "--cols must be between 1 and 1000.";
                        return false;
                    }
                    options.Columns = columns;
                    break;
                case "--rows" when operation == "resize" && index + 1 < args.Length:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var rows) || rows is < 1 or > 1000)
                    {
                        error = "--rows must be between 1 and 1000.";
                        return false;
                    }
                    options.Rows = rows;
                    break;
                case "--follow" when operation == "logs":
                    options.Follow = true;
                    break;
                case "--text":
                    options.Text = true;
                    break;
                default:
                    error = Usage();
                    return false;
            }
        }

        if (options.Fingerprint is null || string.IsNullOrWhiteSpace(options.JobId) || operation == "input" && options.Data is null)
        {
            error = options.Fingerprint is null ? "A SHA-256 TLS fingerprint is required for job commands." : string.IsNullOrWhiteSpace(options.JobId) ? "--job is required." : "--data is required.";
            return false;
        }

        return true;
    }

    private static string Usage() => "Usage: rcctl job logs|input|close-input|cancel|wait|resize <IP:port> --fingerprint <SHA256> --job <jobId> [operation options] [--follow] [--text]";

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized.ToUpperInvariant() : null;
    }

    private sealed class Options
    {
        public IPEndPoint? Endpoint { get; set; }
        public string? Fingerprint { get; set; }
        public string? JobId { get; set; }
        public JobOutputKind Stream { get; set; } = JobOutputKind.Stdout;
        public long AfterOffset { get; set; }
        public int MaximumBytes { get; set; } = DefaultMaximumBytes;
        public string? Data { get; set; }
        public TimeSpan? Timeout { get; set; }
        public int Columns { get; set; } = 120;
        public int Rows { get; set; } = 30;
        public bool Follow { get; set; }
        public bool Text { get; set; }
    }
}
