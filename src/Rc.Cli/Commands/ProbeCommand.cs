using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class ProbeCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is < 3 or > 4 || !IPEndPoint.TryParse(args[0], out var endpoint))
        {
            await error.WriteLineAsync("Usage: rcctl probe <IP:port> --fingerprint <SHA256> [--text]");
            return 2;
        }

        var text = args.Contains("--text", StringComparer.Ordinal);
        var fingerprintIndex = Array.FindIndex(args, value => string.Equals(value, "--fingerprint", StringComparison.Ordinal));
        if (fingerprintIndex < 0 || fingerprintIndex + 1 >= args.Length || (text && args.Length != 4) || (!text && args.Length != 3))
        {
            await error.WriteLineAsync("Usage: rcctl probe <IP:port> --fingerprint <SHA256> [--text]");
            return 2;
        }

        var expectedFingerprint = NormalizeFingerprint(args[fingerprintIndex + 1]);
        if (expectedFingerprint is null)
        {
            await error.WriteLineAsync("The certificate fingerprint must be a 64-character SHA-256 hexadecimal value.");
            return 2;
        }

        try
        {
            var response = await AgentProbeClient.ProbeAsync(endpoint, expectedFingerprint).ConfigureAwait(false);

            if (text)
            {
                await output.WriteLineAsync($"deviceId: {response.DeviceId}");
                await output.WriteLineAsync($"fingerprint: {response.CertificateSha256Fingerprint}");
                await output.WriteLineAsync($"paired: {response.HasPairedController}");
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
            }

            return 0;
        }
        catch (AuthenticationException exception)
        {
            await error.WriteLineAsync($"TLS authentication failed: {exception.Message}");
            return 1;
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            await error.WriteLineAsync($"Unable to connect: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            await error.WriteLineAsync($"Unable to communicate with the agent: {exception.Message}");
            return 1;
        }
        catch (ObjectDisposedException exception)
        {
            await error.WriteLineAsync($"Unable to communicate with the agent: {exception.Message}");
            return 1;
        }
    }

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }
}
