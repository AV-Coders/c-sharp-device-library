using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using SnmpTimeoutException = Lextm.SharpSnmpLib.Messaging.TimeoutException;

namespace AVCoders.CommunicationClients;

public class AvCodersSnmpV3Client : CommunicationClient
{
    public const ushort DefaultPort = 161;
    private readonly OctetString _username;
    private readonly SHA1AuthenticationProvider _auth;
    private readonly AESPrivacyProvider _priv;
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
        : base(name, host, port, CommandStringFormat.Ascii)
    {
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
        var addresses = Dns.GetHostAddresses(Host);
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

    // A stale cached report (agent rebooted, engine boots changed) makes the agent answer
    // with a report PDU or nothing; rediscover once and retry before failing the operation.
    private ISnmpMessage GetResponseWithRetry(IPEndPoint host, Func<ReportMessage, ISnmpMessage> createRequest)
    {
        try
        {
            var reply = createRequest(GetEngineReport(host)).GetResponse(DefaultRequestTimeout, host);
            if (reply is ReportMessage)
                throw new SnmpException("The agent returned a report message");
            return reply;
        }
        catch
        {
            InvalidateEngineReport();
            var reply = createRequest(GetEngineReport(host)).GetResponse(DefaultRequestTimeout, host);
            if (reply is ReportMessage)
                throw new SnmpException("The agent returned a report message after engine rediscovery");
            return reply;
        }
    }

    private List<Variable> Set(string oid, ISnmpData value)
    {
        try
        {
            var host = ResolveHost();
            InvokeRequestHandlers($"SET OID: {oid}, Value: {value}");
            var reply = GetResponseWithRetry(host, report => new SetRequestMessage(
                VersionCode.V3,
                Messenger.NextMessageId,
                Messenger.NextRequestId,
                _username,
                OctetString.Empty,
                [new Variable(new ObjectIdentifier(oid), value)],
                _priv,
                Messenger.MaxMessageSize,
                report));
            if (reply.Pdu().ErrorStatus != Integer32.Zero)
            {
                LogError("Error in Set response for OID {oid}: {status}, index: {index}", oid, reply.Pdu().ErrorStatus, reply.Pdu().ErrorIndex);
                ConnectionState = ConnectionState.Error;
                return [];
            }
            ConnectionState = ConnectionState.Connected;
            var result = reply.Pdu().Variables;
            var variableStrings = string.Join(", ", result.Select(v => $"{v.Id}={v.Data}\n"));
            InvokeResponseHandlers($"SET OID: {oid}, Values: {variableStrings}");
            return result.ToList();
        }
        catch (Exception e)
        {
            LogException(e, $"SNMP SET failed for OID {oid}");
            ReportConnectionFailure(DescribeSnmpConnectionError(e));
            ConnectionState = ConnectionState.Error;
            return [];
        }
    }

    // The ErrorStatus paths deliberately do not report a connection failure - the device
    // responded; those are SNMP-level errors, not reachability problems.
    private string DescribeSnmpConnectionError(Exception e) => e switch
    {
        SnmpTimeoutException => $"The SNMP request to {Host}:{Port} timed out",
        _ => DescribeConnectionError(e)
    };
    
    public virtual List<Variable> Set(string oid, string value) => Set(oid, new OctetString(value));

    public virtual List<Variable> Set(string oid, int value) => Set(oid, new Integer32(value));

    public virtual List<Variable> Get(string oid)
    {
        try
        {
            var host = ResolveHost();
            InvokeRequestHandlers($"GET OID: {oid}");
            var reply = GetResponseWithRetry(host, report => new GetRequestMessage(
                VersionCode.V3,
                Messenger.NextMessageId,
                Messenger.NextRequestId,
                _username,
                OctetString.Empty,
                [new Variable(new ObjectIdentifier(oid))],
                _priv,
                Messenger.MaxMessageSize,
                report));
            if (reply.Pdu().ErrorStatus != Integer32.Zero)
            {
                LogError("Error in response {status}, {index}", reply.Pdu().ErrorStatus, reply.Pdu().ErrorIndex);
                ConnectionState = ConnectionState.Error;
                return [];
            }

            ConnectionState = ConnectionState.Connected;
            var result = reply.Pdu().Variables;
            var variableStrings = string.Join(", ", result.Select(v => $"{v.Id}={v.Data}\n"));
            InvokeResponseHandlers($"GET OID: {oid}, Values: {variableStrings}");
            return result.ToList();
        }
        catch (Exception e)
        {
            LogException(e, $"SNMP GET failed for OID {oid}");
            ReportConnectionFailure(DescribeSnmpConnectionError(e));
            ConnectionState = ConnectionState.Error;
            return [];
        }
    }

    public virtual List<Variable> Walk(string oid)
    {
        try
        {
            var host = ResolveHost();
            List<Variable> results;
            try
            {
                results = DoWalk(host, oid);
            }
            catch
            {
                InvalidateEngineReport();
                results = DoWalk(host, oid);
            }
            ConnectionState = ConnectionState.Connected;
            return results;
        }
        catch (Exception e)
        {
            LogException(e, $"SNMP WALK failed for OID {oid}");
            ReportConnectionFailure(DescribeSnmpConnectionError(e));
            ConnectionState = ConnectionState.Error;
            return [];
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