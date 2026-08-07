using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class UiCommand
{
    private const string Usage = "Usage: rcctl ui status|snapshot|displays|windows|screenshot|window|move|mouse|key|type|clipboard|elements|element|browser <IP:port> --fingerprint <SHA256> ... [--text]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 4 || !IPEndPoint.TryParse(args[1], out var endpoint))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        var text = args.Contains("--text", StringComparer.Ordinal);
        var fingerprintIndex = Array.FindIndex(args, value => string.Equals(value, "--fingerprint", StringComparison.Ordinal));
        if (fingerprintIndex < 0 || fingerprintIndex + 1 >= args.Length)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }
        var fingerprint = NormalizeFingerprint(args[fingerprintIndex + 1]);
        if (fingerprint is null)
        {
            await error.WriteLineAsync("A SHA-256 TLS fingerprint is required for UI control.");
            return 2;
        }

        var remaining = args.Where((_, index) => index != 1 && index != fingerprintIndex && index != fingerprintIndex + 1 && !string.Equals(args[index], "--text", StringComparison.Ordinal)).ToArray();
        try
        {
            var result = await ExecuteAsync(remaining, endpoint, fingerprint).ConfigureAwait(false);
            await WriteResultAsync(remaining[0], result, text, output).ConfigureAwait(false);
            return 0;
        }
        catch (AuthenticationException exception)
        {
            await error.WriteLineAsync($"TLS authentication failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"{exception.Message}{Environment.NewLine}{Usage}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            await error.WriteLineAsync($"UI command failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<object> ExecuteAsync(string[] args, IPEndPoint endpoint, string fingerprint)
    {
        var command = args[0].ToLowerInvariant();
        return command switch
        {
            "status" when args.Length == 1 => await SendStatusAsync(endpoint, fingerprint).ConfigureAwait(false),
            "snapshot" when args.Length is 1 or 2 && args.Skip(1).All(value => value == "--windows") => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.Snapshot, new UiSnapshotRequest(args.Contains("--windows"))).ConfigureAwait(false),
            "displays" when args.Length == 1 => await SendAsync<UiDisplaysResponse>(endpoint, fingerprint, UiOperationKinds.Displays, new UiDisplaysRequest()).ConfigureAwait(false),
            "windows" when args.Length == 1 => await SendAsync<UiWindowsResponse>(endpoint, fingerprint, UiOperationKinds.Windows, new UiWindowsRequest()).ConfigureAwait(false),
            "screenshot" when args.Length == 3 => await SendAsync<UiScreenshotResponse>(endpoint, fingerprint, UiOperationKinds.Screenshot, new UiScreenshotRequest(ParseTarget(args[1], args[2]))).ConfigureAwait(false),
            "window" when args.Length == 3 => await SendAsync<UiWindowActionResponse>(endpoint, fingerprint, UiOperationKinds.WindowAction, new UiWindowActionRequest(ParseWindow(args[1]), ParseEnum<WindowAction>(args[2], "window action"))).ConfigureAwait(false),
            "move" when args.Length == 6 => await SendAsync<UiMoveWindowResponse>(endpoint, fingerprint, UiOperationKinds.MoveWindow, new UiMoveWindowRequest(ParseWindow(args[1]), ParseInt(args[2], "x"), ParseInt(args[3], "y"), ParsePositiveInt(args[4], "width"), ParsePositiveInt(args[5], "height"))).ConfigureAwait(false),
            "mouse" => await ExecuteMouseAsync(args, endpoint, fingerprint).ConfigureAwait(false),
            "key" when args.Length >= 5 => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.Key, new UiKeyRequest(ParseTarget(args[1], args[2]), args.Skip(4).ToArray(), ParseKeyDirection(args[3]))).ConfigureAwait(false),
            "shortcut" when args.Length >= 5 => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.Shortcut, new UiShortcutRequest(ParseTarget(args[1], args[2]), args.Skip(3).ToArray())).ConfigureAwait(false),
            "type" when args.Length == 4 => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.Text, new UiTextRequest(ParseTarget(args[1], args[2]), args[3])).ConfigureAwait(false),
            "clipboard" when args.Length == 2 && args[1] == "read" => await SendAsync<UiClipboardReadResponse>(endpoint, fingerprint, UiOperationKinds.ClipboardRead, new UiClipboardReadRequest()).ConfigureAwait(false),
            "clipboard" when args.Length == 2 && args[1] == "clear" => await SendAsync<UiClipboardWriteResponse>(endpoint, fingerprint, UiOperationKinds.ClipboardWrite, new UiClipboardWriteRequest(Array.Empty<byte>())).ConfigureAwait(false),
            "clipboard" when args.Length == 3 && args[1] == "write" => await SendAsync<UiClipboardWriteResponse>(endpoint, fingerprint, UiOperationKinds.ClipboardWrite, new UiClipboardWriteRequest(Encoding.UTF8.GetBytes(args[2]))).ConfigureAwait(false),
            "elements" => await ExecuteElementsAsync(args, endpoint, fingerprint).ConfigureAwait(false),
            "element" => await ExecuteElementActionAsync(args, endpoint, fingerprint).ConfigureAwait(false),
            "browser" => await ExecuteBrowserAsync(args, endpoint, fingerprint).ConfigureAwait(false),
            _ => throw new ArgumentException("The UI command arguments are invalid."),
        };
    }

    private static Task<UiAutomationTreeResponse> ExecuteElementsAsync(string[] args, IPEndPoint endpoint, string fingerprint)
    {
        if (args.Length < 3 || !string.Equals(args[1], "window", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Usage: rcctl ui elements <IP:port> --fingerprint <SHA256> window <handle> [--depth <0-32>] [--limit <1-10000>].");
        }
        var depth = 8;
        var limit = 1_000;
        for (var index = 3; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException("An automation tree option value is required.");
            }
            switch (args[index])
            {
                case "--depth": depth = ParseInt(args[index + 1], "depth"); break;
                case "--limit": limit = ParseInt(args[index + 1], "limit"); break;
                default: throw new ArgumentException("Unknown automation tree option.");
            }
        }
        return SendAsync<UiAutomationTreeResponse>(endpoint, fingerprint, UiOperationKinds.AutomationTree, new UiAutomationTreeRequest(ParseWindow(args[2]), depth, limit));
    }

    private static Task<UiAutomationActionResponse> ExecuteElementActionAsync(string[] args, IPEndPoint endpoint, string fingerprint)
    {
        if (args.Length is < 5 or > 6 || !string.Equals(args[1], "window", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Usage: rcctl ui element <IP:port> --fingerprint <SHA256> window <handle> <runtime-id> <focus|invoke|setvalue|select|expand|collapse> [value].");
        }
        var action = ParseEnum<UiAutomationAction>(args[4], "automation action");
        var value = args.Length == 6 ? args[5] : null;
        return SendAsync<UiAutomationActionResponse>(endpoint, fingerprint, UiOperationKinds.AutomationAction,
            new UiAutomationActionRequest(ParseWindow(args[2]), ParseRuntimeId(args[3]), action, value));
    }

    private static async Task<object> ExecuteBrowserAsync(string[] args, IPEndPoint endpoint, string fingerprint)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("Browser command arguments are required.");
        }

        return args[1].ToLowerInvariant() switch
        {
            "launch" when args.Length == 4 => await SendAsync<UiBrowserLaunchResponse>(endpoint, fingerprint, UiOperationKinds.BrowserLaunch,
                new UiBrowserLaunchRequest(ParseEnum<BrowserKind>(args[2], "browser"), args[3])).ConfigureAwait(false),
            "navigate" when args.Length == 4 => await SendAsync<UiBrowserNavigateResponse>(endpoint, fingerprint, UiOperationKinds.BrowserNavigate,
                new UiBrowserNavigateRequest(ParseWindow(args[2]), args[3])).ConfigureAwait(false),
            "dom" => await ExecuteBrowserDomAsync(args, endpoint, fingerprint).ConfigureAwait(false),
            _ => throw new ArgumentException("Usage: rcctl ui browser <IP:port> --fingerprint <SHA256> launch <default|edge|chrome> <https-url> | navigate <handle> <https-url> | dom <handle> [--depth <0-32>] [--limit <1-10000>]."),
        };
    }

    private static Task<UiBrowserDomResponse> ExecuteBrowserDomAsync(string[] args, IPEndPoint endpoint, string fingerprint)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("A browser window handle is required.");
        }
        var depth = 8;
        var limit = 1_000;
        for (var index = 3; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException("A browser DOM option value is required.");
            }
            switch (args[index])
            {
                case "--depth": depth = ParseInt(args[index + 1], "depth"); break;
                case "--limit": limit = ParseInt(args[index + 1], "limit"); break;
                default: throw new ArgumentException("Unknown browser DOM option.");
            }
        }
        return SendAsync<UiBrowserDomResponse>(endpoint, fingerprint, UiOperationKinds.BrowserDom,
            new UiBrowserDomRequest(ParseWindow(args[2]), depth, limit));
    }

    private static async Task<object> ExecuteMouseAsync(string[] args, IPEndPoint endpoint, string fingerprint)
    {
        if (args.Length < 4)
        {
            throw new ArgumentException("The mouse command arguments are invalid.");
        }
        var target = ParseTarget(args[2], args[3]);
        return args[1].ToLowerInvariant() switch
        {
            "move" when args.Length == 6 => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.MouseMove, new UiMouseMoveRequest(target, ParseInt(args[4], "x"), ParseInt(args[5], "y"))).ConfigureAwait(false),
            "button" when args.Length == 6 => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.MouseButton, new UiMouseButtonRequest(target, ParseEnum<MouseButton>(args[4], "mouse button"), ParseKeyDirection(args[5]))).ConfigureAwait(false),
            "wheel" when args.Length == 5 => await SendAsync<UiSnapshotResponse>(endpoint, fingerprint, UiOperationKinds.MouseWheel, new UiMouseWheelRequest(target, ParseInt(args[4], "wheel delta"))).ConfigureAwait(false),
            _ => throw new ArgumentException("The mouse command arguments are invalid."),
        };
    }

    private static async Task<UiStatusResponse> SendStatusAsync(IPEndPoint endpoint, string fingerprint)
    {
        await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        return await connection.SendAsync<UiStatusResponse>(new ControlUiStatusRequest(1, connection.ControllerId)).ConfigureAwait(false);
    }

    private static async Task<TResponse> SendAsync<TResponse>(IPEndPoint endpoint, string fingerprint, string operation, object request)
    {
        await using var connection = await AuthenticatedControlConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        var response = await connection.SendAsync<UiAgentCommandResponse>(new ControlUiCommandRequest(1, connection.ControllerId, operation, JsonSerializer.SerializeToElement(request, ContractJson.Options))).ConfigureAwait(false);
        return response.Result.Deserialize<TResponse>(ContractJson.Options) ?? throw new InvalidOperationException("The Agent returned an invalid UI response.");
    }

    private static async Task WriteResultAsync(string command, object result, bool text, TextWriter output)
    {
        if (text && result is UiStatusResponse status)
        {
            await output.WriteLineAsync($"sessionId: {status.Session.SessionId}");
            await output.WriteLineAsync($"userName: {status.Session.UserName ?? "n/a"}");
            await output.WriteLineAsync($"active: {status.Session.IsActive}");
            await output.WriteLineAsync($"displays: {status.Session.Displays.Count}");
            await output.WriteLineAsync($"windows: {status.Session.Windows.Count}");
            return;
        }
        if (text && result is UiClipboardReadResponse clipboard)
        {
            await output.WriteLineAsync(Encoding.UTF8.GetString(clipboard.Data));
            return;
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(result), ContractJson.Options));
    }

    private static UiTarget ParseTarget(string kind, string value) => kind.ToLowerInvariant() switch
    {
        "display" => new DisplayTarget(ParseNonNegativeInt(value, "display index")),
        "window" => ParseWindow(value),
        _ => throw new ArgumentException("Target must be 'display <index>' or 'window <handle>'."),
    };

    private static WindowTarget ParseWindow(string value)
    {
        if (!long.TryParse(value, out var handle) || handle == 0)
        {
            throw new ArgumentException("A positive window handle is required.");
        }
        return new WindowTarget(handle);
    }

    private static int[] ParseRuntimeId(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => !int.TryParse(part, out _)))
        {
            throw new ArgumentException("A comma-separated UI Automation runtime ID is required.");
        }
        return parts.Select(int.Parse).ToArray();
    }

    private static bool ParseKeyDirection(string value) => value.ToLowerInvariant() switch
    {
        "down" => true,
        "up" => false,
        _ => throw new ArgumentException("Input direction must be 'down' or 'up'."),
    };

    private static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new ArgumentException($"'{value}' is not a valid {name}.");

    private static int ParseInt(string value, string name) => int.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"A valid {name} is required.");
    private static int ParsePositiveInt(string value, string name)
    {
        var parsed = ParseInt(value, name);
        return parsed > 0 ? parsed : throw new ArgumentException($"A positive {name} is required.");
    }
    private static int ParseNonNegativeInt(string value, string name) => ParseInt(value, name) >= 0 ? ParseInt(value, name) : throw new ArgumentException($"A non-negative {name} is required.");

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized.ToUpperInvariant() : null;
    }
}
