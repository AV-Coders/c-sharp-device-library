using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using SnmpTimeoutException = Lextm.SharpSnmpLib.Messaging.TimeoutException;

namespace AVCoders.CommunicationClients;

public record SnmpReply(List<Variable> Variables, ErrorCode ErrorStatus = ErrorCode.NoError, int ErrorIndex = 0)
{
    public string ErrorText => $"error status {ErrorStatus} ({(int)ErrorStatus}), index {ErrorIndex}";
}

public class AvCodersSnmpV3Client : CommunicationClient
{
    public const ushort DefaultPort = 161;
    private readonly OctetString _username;
    private readonly SHA1AuthenticationProvider _auth;
    private readonly AESPrivacyProvider _priv;
    private readonly Func<string, IPAddress[]> _resolveHost;
    private IPEndPoint? _host;
    private readonly object _engineLock = new();
    private ReportMessage? _engineReport;
    private DateTime _engineDiscoveredAt;

    private const int DefaultDiscoveryTimeout = 1000;
    private const int DefaultRequestTimeout = 1000;

    // SNMPv3 agents accept a cached engine time for 150 seconds; refresh well inside that
    // window so a cached report never triggers a notInTimeWindow round-trip.
    private static readonly TimeSpan EngineReportLifetime = TimeSpan.FromSeconds(60);

    public AvCodersSnmpV3Client(string name, string host, ushort port, string username, string auth, string priv)
        : this(name, host, port, username, auth, priv, null)
    {
    }

    internal AvCodersSnmpV3Client(string name, string host, ushort port, string username, string auth, string priv,
        Func<string, IPAddress[]>? resolveHost)
        : base(name, host, port, CommandStringFormat.Ascii)
    {
        _resolveHost = resolveHost ?? Dns.GetHostAddresses;
        _username = new OctetString(username);
        _auth = new SHA1AuthenticationProvider(new OctetString(auth));
        _priv = new AESPrivacyProvider(new OctetString(priv), _auth);
        if (IPAddress.TryParse(host, out var ipAddress))
            _host = new IPEndPoint(ipAddress, port);
    }

    // Hostnames resolve lazily (and DNS may be unavailable while the network boots), so a
    // non-literal host degrades to a per-operation error instead of throwing out of the
    // constructor during program startup. The first successful resolution is cached.
    private IPEndPoint ResolveHost()
    {
        if (_host != null)
            return _host;
        var addresses = _resolveHost(Host);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault()
                      ?? throw new SocketException((int)SocketError.HostNotFound);
        _host = new IPEndPoint(address, Port);
        return _host;
    }

    private ReportMessage GetEngineReport(IPEndPoint host)
    {
        lock (_engineLock)
        {
            if (_engineReport != null && DateTime.UtcNow - _engineDiscoveredAt < EngineReportLifetime)
                return _engineReport;
            Discovery discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
            _engineReport = discovery.GetResponse(DefaultDiscoveryTimeout, host);
            _engineDiscoveredAt = DateTime.UtcNow;
            return _engineReport;
        }
    }

    private void InvalidateEngineReport()
    {
        lock (_engineLock)
            _engineReport = null;
    }

    private const string SysUpTimeOid = "1.3.6.1.2.1.1.3.0";

    private static readonly Dictionary<string, (ConnectionReasonCategory Category, string Reason)> UsmStatsReasons = new()
    {
        ["1.3.6.1.6.3.15.1.1.1.0"] = (ConnectionReasonCategory.CredentialsRejected, "The agent rejected the SNMPv3 security level"),
        ["1.3.6.1.6.3.15.1.1.2.0"] = (ConnectionReasonCategory.Other, "The request was outside the agent's SNMPv3 time window"),
        ["1.3.6.1.6.3.15.1.1.3.0"] = (ConnectionReasonCategory.CredentialsRejected, "The agent does not know the SNMPv3 username"),
        ["1.3.6.1.6.3.15.1.1.4.0"] = (ConnectionReasonCategory.Other, "The agent does not recognise the SNMPv3 engine id"),
        ["1.3.6.1.6.3.15.1.1.5.0"] = (ConnectionReasonCategory.CredentialsRejected, "SNMPv3 authentication failed - the auth password is wrong"),
        ["1.3.6.1.6.3.15.1.1.6.0"] = (ConnectionReasonCategory.CredentialsRejected, "The agent could not decrypt the request - the privacy password is wrong"),
    };

    private sealed class SnmpRejectedException(ConnectionReasonCategory category, string message) : SnmpException(message)
    {
        public ConnectionReasonCategory Category { get; } = category;
    }

    // A stale cached report (agent rebooted, engine boots changed) makes the agent answer
    // with a report PDU or nothing; rediscover once and retry before failing the operation.
    private ISnmpMessage GetResponseWithRetry(IPEndPoint host, string oid, Func<ReportMessage, ISnmpMessage> createRequest)
    {
        try
        {
            return Validate(createRequest(GetEngineReport(host)).GetResponse(DefaultRequestTimeout, host), oid);
        }
        catch
        {
            InvalidateEngineReport();
            return Validate(createRequest(GetEngineReport(host)).GetResponse(DefaultRequestTimeout, host), oid);
        }
    }

    // Bad credentials come back as a decodable reply whose varbind is a usmStats counter with
    // ErrorStatus 0 - without these checks that parses as a successful poll of garbage data.
    private static ISnmpMessage Validate(ISnmpMessage reply, string requestedOid)
    {
        var variables = reply.Pdu().Variables;
        var answeredOid = variables.Count > 0 ? variables[0].Id.ToString() : null;
        if (answeredOid != null && UsmStatsReasons.TryGetValue(answeredOid, out var rejection))
            throw new SnmpRejectedException(rejection.Category, rejection.Reason);
        if (reply is ReportMessage)
            throw new SnmpException("The agent rejected the request with a report message");
        if (answeredOid != null && answeredOid != requestedOid.TrimStart('.'))
            throw new SnmpException($"The agent answered for OID {answeredOid} instead of {requestedOid}");
        return reply;
    }

    private SnmpReply Set(string oid, ISnmpData value)
    {
        try
        {
            var host = ResolveHost();
            InvokeRequestHandlers($"SET OID: {oid}, Value: {value}");
            var reply = GetResponseWithRetry(host, oid, report => new SetRequestMessage(
                VersionCode.V3,
                Messenger.NextMessageId,
                Messenger.NextRequestId,
                _username,
                OctetString.Empty,
                [new Variable(new ObjectIdentifier(oid), value)],
                _priv,
                Messenger.MaxMessageSize,
                report));
            ConnectionState = ConnectionState.Connected;
            if (reply.Pdu().ErrorStatus != Integer32.Zero)
                return ErrorReply($"SET OID: {oid}", reply.Pdu());
            var result = reply.Pdu().Variables;
            var variableStrings = string.Join(", ", result.Select(v => $"{v.Id}={v.Data}\n"));
            InvokeResponseHandlers($"SET OID: {oid}, Values: {variableStrings}");
            return new SnmpReply(result.ToList());
        }
        catch (Exception e)
        {
            LogException(e, $"SNMP SET failed for OID {oid}");
            ReportConnectionFailure(e);
            ConnectionState = ConnectionState.Error;
            return new SnmpReply([]);
        }
    }

    private SnmpReply ErrorReply(string request, ISnmpPdu pdu)
    {
        var reply = new SnmpReply([], (ErrorCode)pdu.ErrorStatus.ToInt32(), pdu.ErrorIndex.ToInt32());
        InvokeResponseHandlers($"{request}, {reply.ErrorText}");
        return reply;
    }

    // The ErrorStatus paths deliberately do not report a connection failure - the device
    // responded; those are SNMP-level errors, not reachability problems.
    protected override (ConnectionReasonCategory Category, string Description) ExplainConnectionError(Exception e) => e switch
    {
        SnmpTimeoutException => (ConnectionReasonCategory.TimedOut, $"The SNMP request to {Host}:{Port} timed out"),
        SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionRefused } =>
            (ConnectionReasonCategory.Refused, base.ExplainConnectionError(e).Description),
        SnmpRejectedException rejected => (rejected.Category, base.ExplainConnectionError(e).Description),
        _ => base.ExplainConnectionError(e)
    };
    
    public virtual SnmpReply Set(string oid, string value) => Set(oid, new OctetString(value));

    public virtual SnmpReply Set(string oid, int value) => Set(oid, new Integer32(value));

    public virtual SnmpReply Get(string oid)
    {
        try
        {
            var host = ResolveHost();
            InvokeRequestHandlers($"GET OID: {oid}");
            var reply = GetResponseWithRetry(host, oid, report => new GetRequestMessage(
                VersionCode.V3,
                Messenger.NextMessageId,
                Messenger.NextRequestId,
                _username,
                OctetString.Empty,
                [new Variable(new ObjectIdentifier(oid))],
                _priv,
                Messenger.MaxMessageSize,
                report));
            ConnectionState = ConnectionState.Connected;
            if (reply.Pdu().ErrorStatus != Integer32.Zero)
                return ErrorReply($"GET OID: {oid}", reply.Pdu());
            var result = reply.Pdu().Variables;
            var variableStrings = string.Join(", ", result.Select(v => $"{v.Id}={v.Data}\n"));
            InvokeResponseHandlers($"GET OID: {oid}, Values: {variableStrings}");
            return new SnmpReply(result.ToList());
        }
        catch (Exception e)
        {
            LogException(e, $"SNMP GET failed for OID {oid}");
            ReportConnectionFailure(e);
            ConnectionState = ConnectionState.Error;
            return new SnmpReply([]);
        }
    }

    public virtual SnmpReply Walk(string oid)
    {
        try
        {
            var host = ResolveHost();
            List<Variable> results;
            try
            {
                results = DoWalk(host, oid);
            }
            catch (Exception e) when (e is not ErrorException)
            {
                InvalidateEngineReport();
                results = DoWalk(host, oid);
            }
            // BulkWalk swallows SNMPv3 rejection reports and returns an empty list, which is
            // indistinguishable from an empty subtree; a validated get separates the two.
            if (results.Count == 0)
                GetResponseWithRetry(host, SysUpTimeOid, report => new GetRequestMessage(
                    VersionCode.V3,
                    Messenger.NextMessageId,
                    Messenger.NextRequestId,
                    _username,
                    OctetString.Empty,
                    [new Variable(new ObjectIdentifier(SysUpTimeOid))],
                    _priv,
                    Messenger.MaxMessageSize,
                    report));
            ConnectionState = ConnectionState.Connected;
            return new SnmpReply(results);
        }
        catch (ErrorException e)
        {
            ConnectionState = ConnectionState.Connected;
            return ErrorReply($"WALK OID: {oid}", e.Body.Pdu());
        }
        catch (Exception e)
        {
            LogException(e, $"SNMP WALK failed for OID {oid}");
            ReportConnectionFailure(e);
            ConnectionState = ConnectionState.Error;
            return new SnmpReply([]);
        }
    }

    private List<Variable> DoWalk(IPEndPoint host, string oid)
    {
        IList<Variable> results = new List<Variable>();
        Messenger.BulkWalk(
            VersionCode.V3,
            host,
            _username,
            OctetString.Empty,  // contextName
            new ObjectIdentifier(oid),
            results,
            10000,  // timeout in milliseconds
            10,     // maxRepetitions (how many variables to retrieve per request)
            WalkMode.WithinSubtree,
            _priv,
            GetEngineReport(host));
        return results.ToList();
    }

    /// <summary>
    /// This method is not supported for SNMPv3 clients.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown when this method is called.</exception>
    [Obsolete("Send is not supported for SNMPv3 clients. This method will always throw NotSupportedException.", error: true)]
    public override void Send(string message)
    {
        throw new NotSupportedException("Send is not supported for SNMPv3");
    }

    /// <summary>
    /// This method is not supported for SNMPv3 clients.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown when this method is called.</exception>
    [Obsolete("Send is not supported for SNMPv3 clients. This method will always throw NotSupportedException.", error: true)]
    public override void Send(byte[] bytes)
    {
        throw new NotSupportedException("Send is not supported for SNMPv3");
    }
}