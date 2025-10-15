using AVCoders.Core;
using Serilog;
using Serilog.Context;
using System.Text;

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

public class CBusSerialInterface : LogBase
{
    private readonly CommunicationClient _comms;
    private bool _checksumRequired;
    private const byte PointToMultipointHeader = 0x05;
    // private const byte PointToPointHeader = 0x06;
    private const char Delimiter = '\r';
    // private const byte ClearBuffer = 63; // ASCII ?
    private const char BeginPacket = '\\';
    public const byte LightingApplication = 0x38;
    public const byte SceneApplication = 0xCA;
    private List<byte> _gather = [];

    public CBusSerialInterface(CommunicationClient comms, bool checksumRequired = true) : base("Cbus Interface")
    {
        _comms = comms;
        _comms.ResponseByteHandlers += GatherResponse;
        _checksumRequired = checksumRequired;
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
        using (PushProperties("ProcessResponse"))
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

    private static string ToAsciiHexString(byte[] bytes, bool uppercase = true)
    {
        var hex = Convert.ToHexString(bytes); // uppercase by default
        return uppercase ? hex : hex.ToLowerInvariant();
    }

    private string BuildWireString(byte[] data, bool uppercase, bool askForResponse)
    {
        string checksum = ToAsciiHexString([CalculateChecksum(data)], uppercase);
        string body = ToAsciiHexString(data, uppercase);
        StringBuilder sb = new StringBuilder("\\");
        sb.Append(body);

        if (_checksumRequired)
            sb.Append(checksum);
        if(askForResponse)
            sb.Append('r');
        sb.Append(Delimiter);
        return sb.ToString();
    }

    private void Send(byte[] data, bool askForResponse)
    {
        string wire = BuildWireString(data, true, askForResponse);
        _comms.Send(wire);
    }

    public void SendPointToMultipointPayload(byte application, byte[] data, bool askForResponse)
    {
        byte[] payload = new byte[3 + data.Length];
        payload[0] = PointToMultipointHeader;
        payload[1] = application;
        payload[2] = 0x00;
        Array.Copy(data, 0, payload, 3, data.Length);
        Send(payload, askForResponse);
    }
}