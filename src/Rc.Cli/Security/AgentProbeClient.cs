using System.Net;
using System.Text;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.Cli.Security;

internal static class AgentProbeClient
{
    public static async Task<ControlHelloResponse> ProbeAsync(
        IPEndPoint endpoint,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await PinnedTlsConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        var tls = connection.Stream;
        await using var writer = new StreamWriter(tls, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(tls, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlHelloRequest(1), ContractJson.Options)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var response = line is null ? null : JsonSerializer.Deserialize<ResultEnvelope<ControlHelloResponse>>(line, ContractJson.Options);
        return response is { Ok: true, Result: not null }
            ? response.Result
            : throw new InvalidDataException(response?.Error?.Message ?? "The agent did not return a valid hello response.");
    }
}
