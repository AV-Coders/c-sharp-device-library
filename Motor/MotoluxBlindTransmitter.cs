using AVCoders.Core;

namespace AVCoders.Motor;

public class MotoluxBlindTransmitter : Motor
{
    private readonly SerialClient _client;

    private readonly byte _commandHeader = 0x9a;
    private readonly byte _controlCommand = 0x0a;
    private readonly byte _up = 0xDD;
    private readonly byte _down = 0xEE;
    private readonly byte _stop = 0xCC;

    private readonly byte[] _lowerCommand;
    private readonly byte[] _raiseCommand;
    private readonly byte[] _stopCommand;

    public MotoluxBlindTransmitter(string name, byte deviceId, byte blindId, RelayAction powerOnAction, int moveSeconds, SerialClient client)
        : base(name, powerOnAction, moveSeconds)
    {
        _client = client;
        (byte blindIdLow, byte blindIdHigh) = GetIdBytes(blindId);

        byte lowerCommandChecksum = CalculateChecksum(new List<byte> { deviceId, blindIdLow, blindIdHigh, _controlCommand, _down });
        _lowerCommand = new[] { _commandHeader, deviceId, blindIdLow, blindIdHigh, _controlCommand, _down, lowerCommandChecksum };
        
        byte raiseCommandChecksum = CalculateChecksum(new List<byte> { deviceId, blindIdLow, blindIdHigh, _controlCommand, _up });
        _raiseCommand = new[] { _commandHeader, deviceId, blindIdLow, blindIdHigh, _controlCommand, _up, raiseCommandChecksum };
        
        byte stopCommandChecksum = CalculateChecksum(new List<byte> { deviceId, blindIdLow, blindIdHigh, _controlCommand, _stop });
        _stopCommand = new[] { _commandHeader, deviceId, blindIdLow, blindIdHigh, _controlCommand, _stop, stopCommandChecksum };
    }
    
    public (byte, byte) GetIdBytes(byte value)
    {
        byte highByte = 0;
        byte lowByte = 0;

        if (value < 8)
        {
            lowByte = (byte)(1 << value - 1);
        }
        else
        {
            highByte = (byte)(1 << (value - 9));
        }

        return (lowByte, highByte);
    }

    private byte CalculateChecksum(List<byte> command)
    {
        byte checksum = 0;
        command.ForEach(x => checksum ^= x);
        return checksum;
    }

    public override void Raise()
    {
        _client.Send(_raiseCommand);
    }

    public override void Lower()
    {
        _client.Send(_lowerCommand);
    }

    public override void Stop()
    {
        _client.Send(_stopCommand);
    }
}