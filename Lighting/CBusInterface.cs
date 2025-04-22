using AVCoders.Core;
using Serilog;
using Serilog.Context;

namespace AVCoders.Lighting;

public enum CBusRampTime : byte
{
    Instant = 0x02,
    FourSeconds = 0x0A,
    EightSeconds = 0x12,
    TwelveSeconds = 0x1A,
    TwentySeconds = 0x22,
    ThirtySeconds = 0x2A,
    FortySeconds = 0x32,
    OneMinute = 0x3A,
    OneMinute30 = 0x42,
    TwoMinutes = 0x4A,
    ThreeMinutes = 0x52,
    FiveMinutes = 0x5A,
    SevenMinutes = 0x62,
    TenMinutes = 0x6A,
    FifteenMinutes = 0x72,
    SeventeenMinutes = 0x7A,
}

public class CBusInterface : LogBase
{
    private readonly CommunicationClient _comms;
    private bool _serialCheck;
    private readonly byte _pointToMultipointHeader = 0x05;
    private readonly byte _pointToPointHeader = 0x06;
    private readonly byte _delimiter = 0x0d;
    private readonly byte _clearBuffer = 63; // ASCII ?
    private readonly byte _beginPacket = 0x5c; // ASCII /
    public const byte LightingApplication = 0x38;
    public const byte SceneApplication = 0xCA;
    private List<byte> _gather = new ();

    public CBusInterface(CommunicationClient comms, bool serialCheck = true) : base("Cbus Interface")
    {
        _comms = comms;
        _comms.ResponseByteHandlers += GatherResponse;
        _serialCheck = serialCheck;
    }

    private void GatherResponse(byte[] response)
    {
        _gather.AddRange(response);

        while (_gather.Contains(0x0A))
        {
            int endIndex = _gather.IndexOf(0x0A) + 1;
            byte[] aResponsePayload = _gather.Take(endIndex).ToArray();
            _gather = _gather.Skip(endIndex).ToList();

            ProcessResponse(aResponsePayload);
        }
    }

    private void ProcessResponse(byte[] aResponsePayload)
    {
        using (LogContext.PushProperty(MethodProperty, "ProcessResponse"))
        {
            Log.Information(BitConverter.ToString(aResponsePayload));
        }
    }

    private byte CalculateChecksum(byte[] data)
    {
        int sum = 0;
        
        foreach (byte b in data)
        {
            sum += b;
        }
        int mod256Sum = sum % 256;
        
        int twosComplement = (~mod256Sum + 1) & 0xFF;

        return (byte)twosComplement;
    }

    private void Send(byte[] data)
    {
        byte[] payload = new byte[3 + data.Length];
        payload[0] = _beginPacket;
        Array.Copy(data, 0, payload, 1, data.Length);
        payload[data.Length + 1] = CalculateChecksum(data);
        payload[data.Length + 2] = _delimiter;
        _comms.Send(payload);
    }

    public void SendPointToMultipointPayload(byte application, byte[] data)
    {
        byte[] payload = new byte[3 + data.Length];
        payload[0] = _pointToMultipointHeader;
        payload[1] = application;
        payload[2] = 0x00;
        Array.Copy(data, 0, payload, 3, data.Length);
        Send(payload);
    }
}