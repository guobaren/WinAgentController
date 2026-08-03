using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Rc.Cli.Discovery;
using Rc.Contracts;

namespace Rc.Cli.Commands;

internal static class DiscoverCommand
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.78.77");
    private const int MulticastPort = 43000;
    private const int DefaultTimeoutMilliseconds = 3000;

    public static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(arguments, out var timeoutMilliseconds, out var textMode, out var parseError))
        {
            await WriteFailureAsync(output, parseError!);
            return 2;
        }

        try
        {
            DiscoveryFirewallRule.EnsureEnabled(MulticastPort);
            var devices = await ReceiveAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken);
            if (textMode)
            {
                foreach (var device in devices)
                {
                    await output.WriteLineAsync($"{device.DisplayName}\t{device.DeviceId}\t{device.Address}:{device.TcpPort}\t{device.CertificateSha256Fingerprint}");
                }
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(devices), ContractJson.Options));
            }

            return 0;
        }
        catch (SocketException exception)
        {
            await WriteFailureAsync(output, new RemoteError(ErrorCode.Unavailable, $"LAN discovery socket failed: {exception.SocketErrorCode}.", Retryable: true));
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(output, new RemoteError(ErrorCode.FailedPrecondition, exception.Message, Retryable: false));
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Discovery was cancelled.");
            return 130;
        }
    }

    internal static async Task<IReadOnlyList<DiscoveryResultRow>> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        JoinAvailableMulticastInterfaces(client);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var discovered = new Dictionary<string, DiscoveryResultRow>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                var received = await client.ReceiveAsync(timeoutSource.Token);
                if (!DiscoveryAnnouncementCodec.TryDecode(received.Buffer, out var announcement))
                {
                    continue;
                }

                discovered[announcement.DeviceId] = new DiscoveryResultRow(
                    announcement.DeviceId,
                    announcement.DisplayName,
                    received.RemoteEndPoint.Address.ToString(),
                    announcement.TcpPort,
                    announcement.ProtocolVersion,
                    announcement.CertificateSha256Fingerprint);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return discovered.Values
                .OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.DeviceId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static void JoinAvailableMulticastInterfaces(UdpClient client)
    {
        var joined = false;
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                !networkInterface.SupportsMulticast ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicastAddress.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                {
                    continue;
                }

                try
                {
                    client.JoinMulticastGroup(MulticastAddress, address);
                    joined = true;
                }
                catch (SocketException)
                {
                    // A virtual adapter can report as multicast-capable while refusing
                    // membership. Continue with the remaining active interfaces.
                }
            }
        }

        if (!joined)
        {
            // Preserve the previous default-route behaviour on systems that expose no
            // usable IPv4 interface through NetworkInterface.
            client.JoinMulticastGroup(MulticastAddress);
        }
    }

    private static bool TryParseArguments(string[] arguments, out int timeoutMilliseconds, out bool textMode, out RemoteError? error)
    {
        timeoutMilliseconds = DefaultTimeoutMilliseconds;
        textMode = false;
        error = null;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--text":
                    textMode = true;
                    break;
                case "--timeout-ms" when index + 1 < arguments.Length && int.TryParse(arguments[++index], out var value) && value is > 0 and <= 60_000:
                    timeoutMilliseconds = value;
                    break;
                default:
                    error = new RemoteError(ErrorCode.InvalidRequest, "Usage: rcctl discover [--timeout-ms <1-60000>] [--text]", Retryable: false);
                    return false;
            }
        }

        return true;
    }

    private static Task WriteFailureAsync(TextWriter output, RemoteError error) =>
        output.WriteLineAsync(JsonSerializer.Serialize(Result.Failure<IReadOnlyList<DiscoveryResultRow>>(error), ContractJson.Options));

    internal sealed record DiscoveryResultRow(
        string DeviceId,
        string DisplayName,
        string Address,
        int TcpPort,
        ushort ProtocolVersion,
        string CertificateSha256Fingerprint);
}
