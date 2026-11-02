using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersSnmpV3ClientTest
{
    private readonly AvCodersSnmpV3Client _client =
        new("TestSnmp", "127.0.0.1", GetUnusedUdpPort(), "user", "authpass", "privpass");

    private static ushort GetUnusedUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public static TheoryData<string, Func<AvCodersSnmpV3Client, SnmpReply>> Operations => new()
    {
        { "Get", client => client.Get("1.3.6.1.2.1.1.1.0") },
        { "Set", client => client.Set("1.3.6.1.2.1.1.5.0", "name") },
        { "Walk", client => client.Walk("1.3.6.1.2.1.1") },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void Operation_AgainstAnUnreachableDevice_ReturnsEmptyInsteadOfThrowing(string operation, Func<AvCodersSnmpV3Client, SnmpReply> run)
    {
        var result = run(_client);

        Assert.True(result.Variables.Count == 0, $"{operation} returned values from an unreachable device");
        Assert.Equal(ConnectionState.Error, _client.ConnectionState);
    }

    [Fact]
    public void Constructor_WithAHostname_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            new AvCodersSnmpV3Client("TestSnmp", "pdu-rack1.local", 161, "user", "authpass", "privpass"));

        Assert.Null(exception);
    }

    [Fact]
    public void Get_WithAnUnresolvableHostname_ReturnsEmptyInsteadOfThrowing()
    {
        var client = new AvCodersSnmpV3Client("TestSnmp", "device.invalid", 161, "user", "authpass", "privpass",
            _ => throw new SocketException((int)SocketError.HostNotFound));

        var result = client.Get("1.3.6.1.2.1.1.1.0");

        Assert.Empty(result.Variables);
        Assert.Equal(ConnectionState.Error, client.ConnectionState);
    }

    [Fact]
    public void Get_WithAnUnresolvableHostname_IsHostNotFound()
    {
        var client = new AvCodersSnmpV3Client("TestSnmp", "device.invalid", 161, "user", "authpass", "privpass",
            _ => throw new SocketException((int)SocketError.HostNotFound));

        client.Get("1.3.6.1.2.1.1.1.0");

        Assert.Equal(ConnectionReasonCategory.HostNotFound, client.ConnectionReason!.Category);
        Assert.Equal("The host device.invalid was not found", client.ConnectionReason.Description);
    }

    [Fact]
    public void Get_WithAHostnameThatResolvesToNoAddresses_IsHostNotFound()
    {
        var client = new AvCodersSnmpV3Client("TestSnmp", "device.invalid", 161, "user", "authpass", "privpass", _ => []);

        client.Get("1.3.6.1.2.1.1.1.0");

        Assert.Equal(ConnectionReasonCategory.HostNotFound, client.ConnectionReason!.Category);
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset, "The connection to 127.0.0.1:{0} was reset")]
    [InlineData(SocketError.ConnectionRefused, "127.0.0.1:{0} refused the connection")]
    public void APortUnreachableReply_IsRefused(SocketError error, string description)
    {
        var explained = typeof(AvCodersSnmpV3Client)
            .GetMethod("ExplainConnectionError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, [new SocketException((int)error)])!;

        Assert.Equal((ConnectionReasonCategory.Refused, string.Format(description, _client.Port)), explained);
    }
}

public sealed class AvCodersSnmpV3ClientErrorStatusTest : IDisposable
{
    private const string User = "user";
    private const string AuthPassword = "authpass";
    private const string PrivPassword = "privpass";

    private readonly LoopbackAgent _agent = new(User, AuthPassword, PrivPassword);
    private readonly List<ConnectionState> _states = [];
    private readonly AvCodersSnmpV3Client _client;

    public AvCodersSnmpV3ClientErrorStatusTest()
    {
        _client = new AvCodersSnmpV3Client("TestSnmp", "127.0.0.1", _agent.Port, User, AuthPassword, PrivPassword);
        _client.ConnectionStateHandlers += state => _states.Add(state);
    }

    public void Dispose() => _agent.Dispose();

    public static TheoryData<ErrorCode, int, Func<AvCodersSnmpV3Client, SnmpReply>> ErrorReplies => new()
    {
        { ErrorCode.GenError, 1, client => client.Get("1.3.6.1.4.1.850.1.2.1.1.2.0") },
        { ErrorCode.NoAccess, 1, client => client.Set("1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.6.1.1", 2) },
        { ErrorCode.GenError, 1, client => client.Walk("1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.4.1") },
    };

    [Theory]
    [MemberData(nameof(ErrorReplies))]
    public void Operation_WithAnErrorStatusReply_ReturnsTheStatusAndStaysConnected(ErrorCode status, int index,
        Func<AvCodersSnmpV3Client, SnmpReply> run)
    {
        _agent.Answer(status, index);

        var reply = run(_client);

        Assert.Equal(status, reply.ErrorStatus);
        Assert.Equal(index, reply.ErrorIndex);
        Assert.Empty(reply.Variables);
        Assert.Equal(ConnectionState.Connected, _client.ConnectionState);
        Assert.DoesNotContain(ConnectionState.Error, _states);
        Assert.Empty(_client.GetIssues());
    }

    [Theory]
    [InlineData("1.3.6.1.6.3.15.1.1.1.0", "The agent rejected the SNMPv3 security level")]
    [InlineData("1.3.6.1.6.3.15.1.1.3.0", "The agent does not know the SNMPv3 username")]
    [InlineData("1.3.6.1.6.3.15.1.1.5.0", "SNMPv3 authentication failed - the auth password is wrong")]
    [InlineData("1.3.6.1.6.3.15.1.1.6.0", "The agent could not decrypt the request - the privacy password is wrong")]
    public void Get_RejectedForItsCredentials_IsCredentialsRejected(string usmStatsOid, string rejection)
    {
        _agent.Reject(usmStatsOid);

        var reply = _client.Get("1.3.6.1.4.1.850.1.2.1.1.2.0");

        Assert.Empty(reply.Variables);
        Assert.Equal(ConnectionState.Error, _client.ConnectionState);
        var reason = _client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.CredentialsRejected, reason.Category);
        Assert.Equal($"The connection to 127.0.0.1:{_agent.Port} failed: {rejection}", reason.Description);
        Assert.Equal(reason.Description, Assert.Single(_client.GetIssues()).Message);
    }

    [Fact]
    public void Get_OutsideTheAgentsTimeWindow_IsOther()
    {
        _agent.Reject("1.3.6.1.6.3.15.1.1.2.0");

        _client.Get("1.3.6.1.4.1.850.1.2.1.1.2.0");

        Assert.Equal(ConnectionReasonCategory.Other, _client.ConnectionReason!.Category);
    }

    [Fact]
    public void Get_WithANoErrorReply_ReturnsTheVariables()
    {
        var reply = _client.Get("1.3.6.1.4.1.850.1.2.1.1.2.0");

        Assert.Equal(ErrorCode.NoError, reply.ErrorStatus);
        Assert.Equal("1.3.6.1.4.1.850.1.2.1.1.2.0", Assert.Single(reply.Variables).Id.ToString());
        Assert.Equal(ConnectionState.Connected, _client.ConnectionState);
    }

    private sealed class LoopbackAgent : IDisposable
    {
        private const int EngineBoots = 1;
        private const int EngineTime = 1;
        private const string UnknownEngineIdsOid = "1.3.6.1.6.3.15.1.1.4.0";
        private static readonly OctetString EngineId = new([0x80, 0x00, 0x03, 0x52, 0x05, 0x0A, 0x01, 0x03, 0xBB]);

        private readonly System.Net.Sockets.UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UserRegistry _users = new();
        private readonly IPrivacyProvider _privacy;
        private readonly CancellationTokenSource _stop = new();
        private volatile ErrorCode _status = ErrorCode.NoError;
        private volatile int _index;
        private volatile string? _rejection;

        public LoopbackAgent(string user, string authPassword, string privPassword)
        {
            _privacy = new AESPrivacyProvider(new OctetString(privPassword), new SHA1AuthenticationProvider(new OctetString(authPassword)));
            _users.Add(new OctetString(user), _privacy);
            _ = Task.Run(Serve);
        }

        public ushort Port => (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public void Answer(ErrorCode status, int index)
        {
            _status = status;
            _index = index;
        }

        public void Reject(string usmStatsOid) => _rejection = usmStatsOid;

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                var request = MessageFactory.ParseMessages(received.Buffer, _users)[0];
                var reply = OctetString.Empty.Equals(request.Parameters.EngineId) ? Report(request)
                    : _rejection is { } rejection ? Rejection(request, rejection)
                    : Response(request);
                await _udp.SendAsync(reply.ToBytes(), received.RemoteEndPoint);
            }
        }

        private static ISnmpMessage Report(ISnmpMessage discovery) => new ReportMessage(
            VersionCode.V3,
            new Header(new Integer32(discovery.MessageId()), new Integer32(Messenger.MaxMessageSize), 0),
            new SecurityParameters(EngineId, new Integer32(EngineBoots), new Integer32(EngineTime),
                OctetString.Empty, OctetString.Empty, OctetString.Empty),
            new Scope(EngineId, OctetString.Empty, new ReportPdu(discovery.RequestId(), ErrorCode.NoError, 0,
                [new Variable(new ObjectIdentifier(UnknownEngineIdsOid), new Counter32(1))])),
            DefaultPrivacyProvider.DefaultPair,
            null);

        private static ISnmpMessage Rejection(ISnmpMessage request, string usmStatsOid) => new ReportMessage(
            VersionCode.V3,
            new Header(new Integer32(request.MessageId()), new Integer32(Messenger.MaxMessageSize), 0),
            new SecurityParameters(EngineId, new Integer32(EngineBoots), new Integer32(EngineTime),
                request.Parameters.UserName, OctetString.Empty, OctetString.Empty),
            new Scope(EngineId, OctetString.Empty, new ReportPdu(request.RequestId(), ErrorCode.NoError, 0,
                [new Variable(new ObjectIdentifier(usmStatsOid), new Counter32(1))])),
            DefaultPrivacyProvider.DefaultPair,
            null);

        private ISnmpMessage Response(ISnmpMessage request) => new ResponseMessage(
            VersionCode.V3,
            new Header(new Integer32(request.MessageId()), new Integer32(Messenger.MaxMessageSize), _privacy.ToSecurityLevel()),
            new SecurityParameters(EngineId, new Integer32(EngineBoots), new Integer32(EngineTime),
                request.Parameters.UserName, _privacy.AuthenticationProvider.CleanDigest, _privacy.Salt),
            new Scope(EngineId, OctetString.Empty, new ResponsePdu(request.RequestId(), _status, _index, request.Pdu().Variables)),
            _privacy,
            true,
            null);
    }
}
