using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.MediaPlayer.Tests;

public class VitecHttpTest
{
    private const string DeviceStateOn = "{ \"devicestate\": \"on\" }";
    private static readonly TimeSpan AnySpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnansweredRequestTimeout = TimeSpan.FromMilliseconds(200);
    private readonly FakeTimeProvider _time = new();
    private readonly VitecHttp _vitecHttp = new("foo", "bar", "Name");
    private static readonly RemoteButton[] _excludedButtons =
    [
        RemoteButton.Display, RemoteButton.Eject,
        RemoteButton.PopupMenu, RemoteButton.TopMenu,
        RemoteButton.PowerOn, RemoteButton.PowerOff
    ];

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b), _vitecHttp.SupportedButtons.OrderBy(b => b));
    }

    [Fact]
    public async Task SendIRCode_ExcludedButtonsSendNothing()
    {
        using var server = new TlsBodyCapturingServer();
        var vitec = LoopbackVitec(server);

        foreach (var button in _excludedButtons)
            vitec.SendIRCode(button);
        vitec.SendIRCode(RemoteButton.Enter);
        await BodiesArrive(server, 1);

        Assert.Equal([Key("enter")], server.Bodies);
    }

    [Fact]
    public async Task PowerOn_ToASelfSignedDeviceUnderAnActivity_SendsNoTraceContextHeaders()
    {
        using var server = new TlsHeaderCapturingServer();
        var vitec = new VitecHttp($"127.0.0.1:{server.Port}/loopback", "bar", "Loopback");
        using var activity = new Activity("test").Start();
        activity.TraceStateString = "vendor=value";
        activity.AddBaggage("key", "value");

        var request = server.NextRequestHeadAsync();
        vitec.PowerOn();
        var head = await request;

        Assert.StartsWith("PUT /loopback:8080/devicestate", head);
        Assert.All(TraceContext.Headers,
            header => Assert.DoesNotContain($"\r\n{header}:", head, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetChannel_WithANegativeChannel_IsLogged()
    {
        var logs = LogCapture.Create(() => new VitecHttp("foo", "bar", "Logged"), out var vitec);

        vitec.SetChannel(-3);

        Assert.Contains(logs.Logs, log => log.Message == "Channel -3 is invalid, it can't be negative");
    }

    private static string Key(string key) => $"{{ \"key\": \"rm_{key}\" }}";

    private VitecHttp LoopbackVitec(TlsBodyCapturingServer server) =>
        new($"127.0.0.1:{server.Port}/loopback", "bar", "Loopback", _time);

    private Task BodiesArrive(TlsBodyCapturingServer server, int count) =>
        Wait.UntilAsync(_time, AnySpacing, () => server.Bodies.Count >= count, $"Fewer than {count} requests arrived");

    [Fact]
    public async Task SetChannel_ReturnsBeforeItsDigitsAreSent()
    {
        using var server = new TlsBodyCapturingServer();
        var vitec = LoopbackVitec(server);

        vitec.SetChannel(1234);

        await Wait.UntilAsync(() => server.Bodies.Count == 1, "The first digit was never sent");
        Assert.Equal([Key("1")], server.Bodies);
        await BodiesArrive(server, 4);
        Assert.Equal([Key("1"), Key("2"), Key("3"), Key("4")], server.Bodies);
    }

    [Fact]
    public async Task ToggleSubtitles_ReturnsBeforeTheMenuHasHadTimeToOpen()
    {
        using var server = new TlsBodyCapturingServer();
        var vitec = LoopbackVitec(server);

        vitec.ToggleSubtitles();

        await Wait.UntilAsync(() => server.Bodies.Count == 1, "The subtitle key was never sent");
        Assert.Equal([Key("subtitle")], server.Bodies);
        await BodiesArrive(server, 2);
        Assert.Equal([Key("subtitle"), Key("enter")], server.Bodies);
    }

    [Fact]
    public async Task ToggleSubtitles_Twice_PressesDownOnceTheSecondTime()
    {
        using var server = new TlsBodyCapturingServer();
        var vitec = LoopbackVitec(server);

        vitec.ToggleSubtitles();
        vitec.ToggleSubtitles();
        await BodiesArrive(server, 5);

        Assert.Equal([Key("subtitle"), Key("enter"), Key("subtitle"), Key("down"), Key("enter")], server.Bodies);
    }

    [Fact]
    public async Task SetChannel_ThenADirectSend_SendsItAfterTheDigits()
    {
        using var server = new TlsBodyCapturingServer();
        var vitec = LoopbackVitec(server);

        vitec.SetChannel(12);
        vitec.PowerOn();
        await BodiesArrive(server, 3);

        Assert.Equal([Key("1"), Key("2"), DeviceStateOn], server.Bodies);
    }

    [Fact]
    public async Task Send_AfterAFailedSend_StillSends()
    {
        using var server = new TlsBodyCapturingServer(connectionsToDrop: 1);
        var vitec = LoopbackVitec(server);

        vitec.PowerOn();
        await Wait.UntilAsync(() => vitec.Errors.Count == 1, "The failed send was never logged");
        vitec.SendIRCode(RemoteButton.Enter);
        await BodiesArrive(server, 1);

        Assert.Equal([Key("enter")], server.Bodies);
    }

    [Fact]
    public async Task Send_ToABoxThatNeverAnswers_TimesOutAndTheNextPressStillGoes()
    {
        using var server = new TlsBodyCapturingServer(connectionsToHold: 1);
        var vitec = LoopbackVitec(server);
        vitec.RequestTimeout = UnansweredRequestTimeout;

        vitec.PowerOn();
        await Wait.UntilAsync(() => vitec.Errors.Count == 1, "The unanswered send was never logged");
        vitec.SendIRCode(RemoteButton.Enter);
        await BodiesArrive(server, 1);

        Assert.Equal([Key("enter")], server.Bodies);
        Assert.Contains(vitec.Errors, error => error.Exception is TaskCanceledException { InnerException: TimeoutException });
    }

    [Fact]
    public async Task Send_ToABoxThatNeverAnswers_DropsThePressesQueuedBehindIt()
    {
        using var server = new TlsBodyCapturingServer(connectionsToHold: 1);
        var logs = LogCapture.Create(() => LoopbackVitec(server), out var vitec);
        vitec.RequestTimeout = UnansweredRequestTimeout;

        vitec.SetChannel(1234);
        vitec.SendIRCode(RemoteButton.Enter);
        vitec.PowerOn();
        await Wait.UntilAsync(
            () => logs.Logs.Any(log => log.Message == "The Vitec didn't answer, dropping 5 queued presses"),
            "The queued presses were never dropped");
        vitec.SendIRCode(RemoteButton.Back);
        await BodiesArrive(server, 1);

        Assert.Equal([Key("cancel")], server.Bodies);
        Assert.Single(vitec.Errors);
    }

    private sealed class TlsBodyCapturingServer : IDisposable
    {
        private const string EndOfHead = "\r\n\r\n";
        private static readonly byte[] Response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        private static readonly Regex ContentLength = new(@"\r\nContent-Length: (\d+)", RegexOptions.IgnoreCase);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate = CreateSelfSignedCertificate();
        private readonly List<string> _bodies = [];
        private readonly List<TcpClient> _heldConnections = [];
        private readonly int _connectionsToDrop;
        private readonly int _connectionsToHold;
        public ushort Port { get; }

        public TlsBodyCapturingServer(int connectionsToDrop = 0, int connectionsToHold = 0)
        {
            _connectionsToDrop = connectionsToDrop;
            _connectionsToHold = connectionsToHold;
            _listener.Start();
            Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Serve();
        }

        public List<string> Bodies
        {
            get
            {
                lock (_bodies)
                    return _bodies.ToList();
            }
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

        private async Task Serve()
        {
            for (var connection = 0; ; connection++)
            {
                var client = await _listener.AcceptTcpClientAsync();
                if (connection < _connectionsToHold)
                {
                    lock (_heldConnections)
                        _heldConnections.Add(client);
                    continue;
                }
                using (client)
                {
                    if (connection >= _connectionsToDrop)
                        await Answer(client);
                }
            }
        }

        private async Task Answer(TcpClient client)
        {
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(_certificate);
            var body = await ReadBody(tls);
            lock (_bodies)
                _bodies.Add(body);
            await tls.WriteAsync(Response);
        }

        private static async Task<string> ReadBody(Stream tls)
        {
            var request = "";
            while (!request.Contains(EndOfHead))
                request += await ReadSome(tls);
            var bodyStart = request.IndexOf(EndOfHead, StringComparison.Ordinal) + EndOfHead.Length;
            var contentLength = int.Parse(ContentLength.Match(request).Groups[1].Value);
            while (request.Length < bodyStart + contentLength)
                request += await ReadSome(tls);
            return request.Substring(bodyStart, contentLength);
        }

        private static async Task<string> ReadSome(Stream tls)
        {
            var buffer = new byte[1024];
            int read = await tls.ReadAsync(buffer);
            if (read == 0)
                throw new IOException("The client closed the connection mid-request");
            return Encoding.ASCII.GetString(buffer, 0, read);
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_heldConnections)
                _heldConnections.ForEach(connection => connection.Dispose());
            _certificate.Dispose();
        }
    }
}
