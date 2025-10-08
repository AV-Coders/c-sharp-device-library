using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Serilog;

namespace AVCoders.CommunicationClients;

public class AvCodersSnmpV3Client : CommunicationClient
{
    public const ushort DefaultPort = 161;
    private readonly OctetString _username;
    private readonly SHA1AuthenticationProvider _auth;
    private readonly AESPrivacyProvider _priv;
    private readonly IPEndPoint _host;
    
    public AvCodersSnmpV3Client(string name, string host, ushort port, string username, string auth, string priv) 
        : base(name, host, port, CommandStringFormat.Ascii)
    {
        _username = new OctetString(username);
        _auth = new SHA1AuthenticationProvider(new OctetString(auth));
        _priv = new AESPrivacyProvider(new OctetString(priv), _auth);
        _host = new IPEndPoint(IPAddress.Parse(host), port);
    }
    
    public List<Variable> Set(string oid, string value)
    {
        Discovery discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        ReportMessage reportMessage = discovery.GetResponse(100, _host);
        SetRequestMessage request = new SetRequestMessage(
            VersionCode.V3,
            Messenger.NextMessageId, 
            Messenger.NextRequestId,
            _username,
            OctetString.Empty,
            [new Variable(new ObjectIdentifier(oid), new OctetString(value))],
            _priv, 
            Messenger.MaxMessageSize,
            reportMessage);
        var reply = request.GetResponse(100, _host);
        if (reply.Pdu().ErrorStatus != Integer32.Zero)
        {
            Log.Error("Error in response {status}, {index}", reply.Pdu().ErrorStatus, reply.Pdu().ErrorIndex);
            ConnectionState = ConnectionState.Error;
            return [];
        }
        ConnectionState = ConnectionState.Connected;
        return reply.Pdu().Variables.ToList();
    }
    
    public List<Variable> Set(string oid, int value)
    {
        Discovery discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        ReportMessage reportMessage = discovery.GetResponse(100, _host);
        SetRequestMessage request = new SetRequestMessage(
            VersionCode.V3,
            Messenger.NextMessageId, 
            Messenger.NextRequestId,
            _username,
            OctetString.Empty,
            [new Variable(new ObjectIdentifier(oid), new Integer32(value))],
            _priv, 
            Messenger.MaxMessageSize,
            reportMessage);
        var reply = request.GetResponse(100, _host);
        if (reply.Pdu().ErrorStatus != Integer32.Zero)
        {
            Log.Error("Error in response {status}, {index}", reply.Pdu().ErrorStatus, reply.Pdu().ErrorIndex);
            ConnectionState = ConnectionState.Error;
            return [];
        }
        ConnectionState = ConnectionState.Connected;
        return reply.Pdu().Variables.ToList();
    }

    public List<Variable> Get(string oid)
    {
        Discovery discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        ReportMessage reportMessage = discovery.GetResponse(100, _host);
        GetRequestMessage request = new GetRequestMessage(
            VersionCode.V3,
            Messenger.NextMessageId, 
            Messenger.NextRequestId,
            _username,
            OctetString.Empty,
            [new Variable(new ObjectIdentifier(oid))],
            _priv, 
            Messenger.MaxMessageSize,
            reportMessage);
        var reply = request.GetResponse(100, _host);
        if (reply.Pdu().ErrorStatus != Integer32.Zero)
        {
            Log.Error("Error in response {status}, {index}", reply.Pdu().ErrorStatus, reply.Pdu().ErrorIndex);
            ConnectionState = ConnectionState.Error;
            return [];
        }

        ConnectionState = ConnectionState.Connected;
        var result = reply.Pdu().Variables;
        return result.ToList();
    }

    public List<Variable> Walk(string oid)
    {
        Discovery discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        ReportMessage report = discovery.GetResponse(100, _host);
        
        ObjectIdentifier startOid = new ObjectIdentifier(oid);
        
        IList<Variable> results = new List<Variable>();
        Messenger.BulkWalk(
            VersionCode.V3,
            _host,
            _username,
            OctetString.Empty,  // contextName
            startOid,
            results,
            60000,  // timeout in milliseconds
            10,     // maxRepetitions (how many variables to retrieve per request)
            WalkMode.WithinSubtree,
            _priv,
            report);
        
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