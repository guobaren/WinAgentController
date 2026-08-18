using Rc.Cli.Commands;
using Rc.Cli.Targets;
using System.Text.Json;
using Rc.Contracts;

var textMode = args.Contains("--text", StringComparer.Ordinal);
using var capturedOutput = textMode ? null : new StringWriter();
using var capturedError = textMode ? null : new StringWriter();
var output = textMode ? Console.Out : capturedOutput!;
var error = textMode ? Console.Error : capturedError!;
var dispatchArguments = args;
var resolutionExitCode = 0;
if (args.Length > 0)
{
    try
    {
        var resolution = await new TargetArgumentResolver(new ControllerTargetStore()).ResolveAsync(args);
        if (resolution.Success)
        {
            dispatchArguments = resolution.Arguments;
        }
        else
        {
            await error.WriteLineAsync(resolution.Error);
            resolutionExitCode = 2;
        }
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        await error.WriteLineAsync($"Unable to read controller target profiles: {exception.Message}");
        resolutionExitCode = 1;
    }
}
var exitCode = resolutionExitCode != 0
    ? resolutionExitCode
    : dispatchArguments.Length == 0
        ? await WriteUsageAndReturnAsync(error)
        : await DispatchAsync(dispatchArguments, output, error);

if (!textMode)
{
    if (exitCode == 0 || IsSuccessEnvelope(capturedOutput!.ToString()))
    {
        await Console.Error.WriteAsync(capturedError!.ToString());
        await Console.Out.WriteAsync(capturedOutput!.ToString());
    }
    else
    {
        var remoteError = TryGetFailure(capturedOutput!.ToString()) ?? CreateFailure(exitCode, capturedError!.ToString());
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(Result.Failure<object>(remoteError), ContractJson.Options));
    }
}

return exitCode;

static Task<int> DispatchAsync(string[] arguments, TextWriter output, TextWriter error) => arguments[0] switch
{
    "discover" => DiscoverCommand.RunAsync(arguments[1..], output, error),
    "target" => TargetCommand.RunAsync(arguments[1..], output, error),
    "probe" => ProbeCommand.RunAsync(arguments[1..], output, error),
    "pair" => PairCommand.RunAsync(arguments[1..], Console.In, output, error),
    "exec" => ExecCommand.RunAsync(arguments[1..], output, error),
    "health" => HealthCommand.RunAsync(arguments[1..], output, error),
    "job" => JobCommand.RunAsync(arguments[1..], output, error),
    "fs" => FileCommand.RunFsAsync(arguments[1..], output, error),
    "copy" => FileCommand.RunCopyAsync(arguments[1..], output, error),
    "ui" => UiCommand.RunAsync(arguments[1..], output, error),
    "update" => UpdateCommand.RunAsync(arguments[1..], output, error),
    _ => WriteUsageAndReturnAsync(error, arguments[0]),
};

static Task<int> WriteUsageAndReturnAsync(TextWriter error, string? command = null)
{
    var prefix = command is null ? string.Empty : $"Unknown command: {command}. ";
    error.WriteLine($"{prefix}Usage: rcctl target ... | rcctl discover ... | rcctl probe ... | rcctl pair ... | rcctl exec ... | rcctl health ... | rcctl job ... | rcctl fs ... | rcctl copy ... | rcctl ui ... | rcctl update ...");
    return Task.FromResult(2);
}

static ErrorCode GetErrorCode(int exitCode, string message)
{
    if (exitCode == 2) return ErrorCode.InvalidRequest;
    if (exitCode == 130) return ErrorCode.Cancelled;
    if (message.Contains("TLS authentication", StringComparison.OrdinalIgnoreCase)) return ErrorCode.Unauthenticated;
    if (message.Contains("rejected", StringComparison.OrdinalIgnoreCase)) return ErrorCode.FailedPrecondition;
    return ErrorCode.Unavailable;
}

static RemoteError CreateFailure(int exitCode, string capturedMessage)
{
    var message = capturedMessage.Trim();
    return new RemoteError(
        GetErrorCode(exitCode, message),
        string.IsNullOrEmpty(message) ? "The command failed without additional details." : message,
        exitCode is 1 or 130);
}

static RemoteError? TryGetFailure(string output)
{
    try
    {
        var envelope = JsonSerializer.Deserialize<ResultEnvelope<JsonElement>>(output.Trim(), ContractJson.Options);
        return envelope is { Ok: false, Error: not null } ? envelope.Error : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static bool IsSuccessEnvelope(string output)
{
    try
    {
        var envelope = JsonSerializer.Deserialize<ResultEnvelope<JsonElement>>(output.Trim(), ContractJson.Options);
        return envelope is { Ok: true };
    }
    catch (JsonException)
    {
        return false;
    }
}
