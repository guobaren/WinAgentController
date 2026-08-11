using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Agent.Security;
using Rc.Cli.Security;
using Rc.Cli.Targets;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class PairCommand
{
    private const int MaximumLineLength = 16 * 1024;

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        if (!TryParseArguments(args, out var endpoint, out var fingerprint, out var requestedName, out var oneTimeCodeArgument, out var text, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }

        try
        {
            using var identity = await ControllerIdentity.LoadOrCreateAsync(requestedName!);
            var start = await SendAsync<ControlPairStartResponse>(
                endpoint!, fingerprint!, new ControlPairStartRequest(1, identity.ControllerId, identity.Certificate));

            var oneTimeCode = oneTimeCodeArgument;
            if (oneTimeCode is null)
            {
                await output.WriteLineAsync($"Enter the one-time code currently displayed by agent {start.Binding.AgentDeviceId}:");
                try
                {
                    oneTimeCode = await input.ReadLineAsync();
                }
                catch (IOException exception)
                {
                    await error.WriteLineAsync($"Pairing cancelled: console input is no longer available ({exception.Message}).");
                    return 130;
                }
                catch (ObjectDisposedException exception)
                {
                    await error.WriteLineAsync($"Pairing cancelled: console input is no longer available ({exception.Message}).");
                    return 130;
                }
            }

            if (string.IsNullOrWhiteSpace(oneTimeCode))
            {
                await error.WriteLineAsync("Pairing cancelled: no one-time code was provided.");
                return 2;
            }

            var transcriptHash = ControllerPairingProtocol.ComputeTranscriptHash(start.Binding);
            try
            {
                using var pairing = new JpakePairingSession(
                    start.Binding.PairingId,
                    PairingPakeRole.Controller,
                    oneTimeCode,
                    transcriptHash);
                var controllerRound1 = pairing.CreateRound1();
                ReceivePeerPayload("agent round 1", () => pairing.ReceiveRound1(start.AgentRound1));

                var round2 = await SendAsync<ControlPairRound2Response>(
                    endpoint!, fingerprint!, new ControlPairRound1Request(start.Binding.PairingId, controllerRound1));
                var controllerRound2 = pairing.CreateRound2();
                ReceivePeerPayload("agent round 2", () => pairing.ReceiveRound2(round2.AgentRound2));

                var round3 = await SendAsync<ControlPairRound3Response>(
                    endpoint!, fingerprint!, new ControlPairRound2Request(start.Binding.PairingId, controllerRound2));
                var controllerRound3 = pairing.CreateRound3();
                ReceivePeerPayload("agent round 3", () => pairing.ReceiveRound3(round3.AgentRound3));

                using var result = pairing.GetResult();
                using var privateKey = identity.GetPrivateKey();
                var sessionKey = result.SessionKey;
                try
                {
                    var proof = ControllerPairingProtocol.CreateCompletionProof(sessionKey, start.Binding, privateKey);
                    try
                    {
                        var completed = await SendAsync<ControlPairCompleteResponse>(
                            endpoint!,
                            fingerprint!,
                            new ControlPairCompleteRequest(
                                start.Binding.PairingId,
                                controllerRound3,
                                proof.ConfirmationMac,
                                proof.CertificateSignature));
                        try
                        {
                            await new ControllerTargetStore().RememberSuccessfulConnectionAsync(
                                start.Binding.AgentDeviceId,
                                endpoint!,
                                fingerprint!).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                        {
                            await error.WriteLineAsync($"Pairing succeeded but the target profile could not be saved: {exception.Message}");
                            return 1;
                        }
                        if (text)
                        {
                            await output.WriteLineAsync($"paired deviceId: {start.Binding.AgentDeviceId}");
                            await output.WriteLineAsync($"controllerId: {completed.ControllerId}");
                            await output.WriteLineAsync($"pairedAtUtc: {completed.PairedAtUtc:O}");
                        }
                        else
                        {
                            await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(completed), ContractJson.Options));
                        }

                        return 0;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(proof.ConfirmationMac);
                        CryptographicOperations.ZeroMemory(proof.CertificateSignature);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(transcriptHash);
            }
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
        catch (IOException exception)
        {
            await error.WriteLineAsync($"Unable to communicate with the agent: {exception.Message}");
            return 1;
        }
        catch (CryptographicException exception)
        {
            await error.WriteLineAsync($"Pairing failed: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync($"Pairing failed: {exception.Message}");
            return 1;
        }
    }

    private static void ReceivePeerPayload(string stage, Action receive)
    {
        try
        {
            receive();
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException($"Pairing validation failed while receiving {stage}: {exception.Message}", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Pairing protocol state was invalid while receiving {stage}: {exception.Message}", exception);
        }
    }

    private static async Task<TResponse> SendAsync<TResponse>(IPEndPoint endpoint, string fingerprint, object request)
    {
        await using var connection = await PinnedTlsConnection.ConnectAsync(endpoint, fingerprint);
        var tls = connection.Stream;

        await using var writer = new StreamWriter(tls, new UTF8Encoding(false), MaximumLineLength, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(tls, new UTF8Encoding(false), false, MaximumLineLength, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Options));
        var line = await reader.ReadLineAsync();
        var response = line is null
            ? null
            : JsonSerializer.Deserialize<ResultEnvelope<TResponse>>(line, ContractJson.Options);
        if (response is not { Ok: true, Result: not null })
        {
            throw new InvalidOperationException(response?.Error?.Message ?? "The agent did not return a valid response.");
        }

        return response.Result;
    }

    private static bool TryParseArguments(
        string[] args,
        out IPEndPoint? endpoint,
        out string? fingerprint,
        out string? name,
        out string? oneTimeCode,
        out bool text,
        out string? error)
    {
        endpoint = null;
        fingerprint = null;
        name = Environment.MachineName;
        oneTimeCode = null;
        text = false;
        error = null;
        if (args.Length == 0 || !IPEndPoint.TryParse(args[0], out endpoint))
        {
            error = "Usage: rcctl pair <IP:port> --fingerprint <SHA256> [--name <controller-name>] [--code <one-time-code>] [--text]";
            return false;
        }

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--name" when index + 1 < args.Length:
                    name = args[++index];
                    break;
                case "--code" when index + 1 < args.Length:
                    oneTimeCode = args[++index].Trim();
                    break;
                case "--text":
                    text = true;
                    break;
                default:
                    error = "Usage: rcctl pair <IP:port> --fingerprint <SHA256> [--name <controller-name>] [--code <one-time-code>] [--text]";
                    return false;
            }
        }

        if (fingerprint is null)
        {
            error = "A SHA-256 TLS fingerprint is required for pairing.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
        {
            error = "The controller name must be 1 to 128 non-control characters.";
            return false;
        }

        if (oneTimeCode is not null && string.IsNullOrWhiteSpace(oneTimeCode))
        {
            error = "The one-time pairing code cannot be empty.";
            return false;
        }

        return true;
    }

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }
}
