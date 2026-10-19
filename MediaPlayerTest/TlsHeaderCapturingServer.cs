using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.MediaPlayer.Tests;

public sealed class TlsHeaderCapturingServer : IDisposable
{
    private const string EndOfHead = "\r\n\r\n";
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] Response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = CreateSelfSignedCertificate();
    public ushort Port { get; }

    public TlsHeaderCapturingServer()
    {
        _listener.Start();
        Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null);
#else
        return new X509Certificate2(pfx);
#endif
    }

    public async Task<string> NextRequestHeadAsync()
    {
        using var connection = await _listener.AcceptTcpClientAsync().WaitAsync(AcceptTimeout);
        await using var tls = new SslStream(connection.GetStream());
        await tls.AuthenticateAsServerAsync(_certificate);
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (!head.ToString().Contains(EndOfHead))
        {
            int read = await tls.ReadAsync(buffer);
            if (read == 0)
                break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        await tls.WriteAsync(Response);
        return head.ToString();
    }

    public void Dispose()
    {
        _listener.Stop();
        _certificate.Dispose();
    }
}
