using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Rc.Cli.Discovery;
using Rc.Cli.Security;
using Rc.Cli.Targets;
using Rc.Contracts;

namespace Rc.Cli.Commands;

internal static class TargetCommand
{
    private const string Usage = "Usage: rcctl target add <name> <IP:port> --fingerprint <SHA256> [--text] | rcctl target list [--text] | rcctl target use <name> [--text] | rcctl target refresh [name] [--timeout-ms <1-60000>] [--text]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        var store = new ControllerTargetStore();
        try
        {
            return args[0] switch
            {
                "add" => await AddAsync(args[1..], store, output, error).ConfigureAwait(false),
                "list" => await ListAsync(args[1..], store, output, error).ConfigureAwait(false),
                "use" => await UseAsync(args[1..], store, output, error).ConfigureAwait(false),
                "refresh" => await RefreshAsync(args[1..], store, output, error).ConfigureAwait(false),
                _ => await FailAsync(error, Usage, 2).ConfigureAwait(false),
            };
        }
        catch (AuthenticationException exception)
        {
            return await FailAsync(error, $"TLS authentication failed: {exception.Message}").ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            return await FailAsync(error, $"Unable to connect: {exception.Message}").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return await FailAsync(error, exception.Message).ConfigureAwait(false);
        }
    }

    private static async Task<int> AddAsync(string[] args, ControllerTargetStore store, TextWriter output, TextWriter error)
    {
        if (args.Length < 4 || !IPEndPoint.TryParse(args[1], out var endpoint))
        {
            return await FailAsync(error, Usage, 2).ConfigureAwait(false);
        }
        var text = false;
        string? fingerprintValue = null;
        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when fingerprintValue is null && index + 1 < args.Length:
                    fingerprintValue = args[++index];
                    break;
                case "--text" when !text:
                    text = true;
                    break;
                default:
                    return await FailAsync(error, Usage, 2).ConfigureAwait(false);
            }
        }
        var fingerprint = TargetValueParser.NormalizeFingerprint(fingerprintValue);
        if (fingerprint is null)
        {
            return await FailAsync(error, "The certificate fingerprint must be a 64-character SHA-256 hexadecimal value.", 2).ConfigureAwait(false);
        }

        var hello = await AgentProbeClient.ProbeAsync(endpoint, fingerprint).ConfigureAwait(false);
        if (!string.Equals(hello.CertificateSha256Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationException("The agent hello fingerprint did not match the pinned TLS fingerprint.");
        }
        var profile = await store.AddAsync(args[0], hello.DeviceId, endpoint, fingerprint).ConfigureAwait(false);
        return await WriteProfileAsync(output, profile, text, "saved").ConfigureAwait(false);
    }

    private static async Task<int> ListAsync(string[] args, ControllerTargetStore store, TextWriter output, TextWriter error)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--text"))
        {
            return await FailAsync(error, Usage, 2).ConfigureAwait(false);
        }
        var text = args.Contains("--text", StringComparer.Ordinal);
        var snapshot = await store.GetSnapshotAsync().ConfigureAwait(false);
        if (text)
        {
            foreach (var target in snapshot.Targets)
            {
                var marker = string.Equals(target.Name, snapshot.CurrentTarget, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                await output.WriteLineAsync($"{marker} {target.Name}\t{target.DeviceId}\t{target.Endpoint}\t{target.CertificateSha256Fingerprint}");
            }
        }
        else
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(snapshot), ContractJson.Options));
        }
        return 0;
    }

    private static async Task<int> UseAsync(string[] args, ControllerTargetStore store, TextWriter output, TextWriter error)
    {
        var text = args.Contains("--text", StringComparer.Ordinal);
        if (args.Length is < 1 or > 2 || args[0].StartsWith("--", StringComparison.Ordinal) || (args.Length == 2 && args[1] != "--text"))
        {
            return await FailAsync(error, Usage, 2).ConfigureAwait(false);
        }
        var profile = await store.SetCurrentAsync(args[0]).ConfigureAwait(false);
        return await WriteProfileAsync(output, profile, text, "current").ConfigureAwait(false);
    }

    private static async Task<int> RefreshAsync(string[] args, ControllerTargetStore store, TextWriter output, TextWriter error)
    {
        var text = false;
        var timeoutMilliseconds = 3000;
        string? name = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--text":
                    text = true;
                    break;
                case "--timeout-ms" when index + 1 < args.Length && int.TryParse(args[++index], out var value) && value is > 0 and <= 60_000:
                    timeoutMilliseconds = value;
                    break;
                case var targetName when name is null && !targetName.StartsWith("--", StringComparison.Ordinal):
                    name = args[index];
                    break;
                default:
                    return await FailAsync(error, Usage, 2).ConfigureAwait(false);
            }
        }

        var profile = name is null ? await store.GetCurrentAsync().ConfigureAwait(false) : await store.FindAsync(name).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException(name is null ? "No current target is selected." : $"Target '{name}' was not found.");
        }

        DiscoveryFirewallRule.EnsureEnabled(43000);
        var devices = await DiscoverCommand.ReceiveAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), CancellationToken.None).ConfigureAwait(false);
        var match = devices.SingleOrDefault(device =>
            string.Equals(device.DeviceId, profile.DeviceId, StringComparison.Ordinal) &&
            string.Equals(device.CertificateSha256Fingerprint, profile.CertificateSha256Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException($"No discovery result matched target '{profile.Name}' by device ID and TLS fingerprint.");
        }
        var endpoint = new IPEndPoint(IPAddress.Parse(match.Address), match.TcpPort);
        var hello = await AgentProbeClient.ProbeAsync(endpoint, profile.CertificateSha256Fingerprint).ConfigureAwait(false);
        if (!string.Equals(hello.DeviceId, profile.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(hello.CertificateSha256Fingerprint, profile.CertificateSha256Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationException("The discovered endpoint did not prove the saved device identity and TLS fingerprint.");
        }
        var refreshed = await store.RefreshEndpointAsync(profile.Name, endpoint).ConfigureAwait(false);
        return await WriteProfileAsync(output, refreshed, text, "refreshed").ConfigureAwait(false);
    }

    private static async Task<int> WriteProfileAsync(TextWriter output, ControllerTargetProfile profile, bool text, string action)
    {
        if (text)
        {
            await output.WriteLineAsync($"{action}: {profile.Name}");
            await output.WriteLineAsync($"deviceId: {profile.DeviceId}");
            await output.WriteLineAsync($"endpoint: {profile.Endpoint}");
            await output.WriteLineAsync($"fingerprint: {profile.CertificateSha256Fingerprint}");
        }
        else
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(profile), ContractJson.Options));
        }
        return 0;
    }

    private static async Task<int> FailAsync(TextWriter error, string message, int exitCode = 1)
    {
        await error.WriteLineAsync(message);
        return exitCode;
    }
}
