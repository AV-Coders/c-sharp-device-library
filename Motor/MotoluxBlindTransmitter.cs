using AVCoders.Core;

namespace AVCoders.Motor;

public class MotoluxBlindTransmitter : Motor
{
    private readonly SerialClient _client;

    private readonly char _commandHeader = '\u009a';
    private readonly char _controlCommand = '\u000a';
    private readonly char _up = '\u00DD';
    private readonly char _down = '\u00EE';
    private readonly char _stop = '\u00CC';

    private readonly char[] _lowerCommand;
    private readonly char[] _raiseCommand;
    private readonly char[] _stopCommand;

    public MotoluxBlindTransmitter(string name, char deviceId, char blindId, RelayAction powerOnAction, int moveSeconds, SerialClient client)
        : base(name, powerOnAction, moveSeconds)
    {
        _client = client;
        (char blindIdLow, char blindIdHigh) = GetIdBytes(blindId);

        char lowerCommandChecksum = CalculateChecksum(new List<char> { deviceId, blindIdLow, blindIdHigh, _controlCommand, _down });
        _lowerCommand = new[] { _commandHeader, deviceId, blindIdLow, blindIdHigh, _controlCommand, _down, lowerCommandChecksum };
        
        char raiseCommandChecksum = CalculateChecksum(new List<char> { deviceId, blindIdLow, blindIdHigh, _controlCommand, _up });
        _raiseCommand = new[] { _commandHeader, deviceId, blindIdLow, blindIdHigh, _controlCommand, _up, raiseCommandChecksum };
        
        char stopCommandChecksum = CalculateChecksum(new List<char> { deviceId, blindIdLow, blindIdHigh, _controlCommand, _stop });
        _stopCommand = new[] { _commandHeader, deviceId, blindIdLow, blindIdHigh, _controlCommand, _stop, stopCommandChecksum };
    }
    
    public (char, char) GetIdBytes(char value)
    {
        char highByte = '\u0000';
        char lowByte = '\u0000';

        switch (value)
        {
            case <= '\u0008':
                lowByte = (char)(1 << value - 1);
                break;
            case '\u0009':
                highByte = '\u0001';
                break;
            default:
                highByte = (char)(1 << (value - '\u000f'));
                break;
        }

        return (lowByte, highByte);
    }

    private char CalculateChecksum(List<char> command)
    {
        char checksum = '\u0000';
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