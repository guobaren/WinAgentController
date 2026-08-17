using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rc.Cli.Security;

/// <summary>
/// A short-lived TLS connection which accepts exactly one known agent certificate.
/// Discovery and pairing both use this implementation so their trust decision cannot drift.
/// </summary>
internal sealed class PinnedTlsConnection : IAsyncDisposable
{
    private readonly TcpClient client;

    private PinnedTlsConnection(TcpClient client, SslStream stream)
    {
        this.client = client;
        Stream = stream;
    }

    public SslStream Stream { get; }

    public static async Task<PinnedTlsConnection> ConnectAsync(IPEndPoint endpoint, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);

        var client = new TcpClient(endpoint.AddressFamily)
        {
            // 控制协议会交替发送小型 JSON 帧、单字节就绪信号和二进制数据。
            // 若启用 Nagle，它会与延迟 ACK 叠加，让每个小文件分片多等待一个网络定时周期。
            NoDelay = true,
        };
        try
        {
            await client.ConnectAsync(endpoint.Address, endpoint.Port);
            string? presentedFingerprint = null;
            var certificateMatched = false;
            var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                presentedFingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                certificateMatched = string.Equals(presentedFingerprint, expectedFingerprint, StringComparison.Ordinal);
                return certificateMatched;
            });
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Address.ToString(),
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });
                return new PinnedTlsConnection(client, stream);
            }
            catch (AuthenticationException exception) when (presentedFingerprint is not null && !certificateMatched)
            {
                await stream.DisposeAsync();
                throw new AuthenticationException(
                    $"The agent presented TLS fingerprint {presentedFingerprint}, which does not match the supplied fingerprint.",
                    exception);
            }
            catch (AuthenticationException exception) when (presentedFingerprint is null)
            {
                await stream.DisposeAsync();
                throw new AuthenticationException(
                    $"TLS negotiation did not reach agent certificate validation: {exception.InnerException?.Message ?? exception.Message}",
                    exception);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        client.Dispose();
    }
}
