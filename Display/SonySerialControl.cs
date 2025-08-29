using AVCoders.Core;

namespace AVCoders.Display;

public class SonySerialControl : Display
{
    public static readonly SerialBaud DefaultBaud = SerialBaud.Rate9600;
    public static readonly SerialDataBits DefaultBits = SerialDataBits.DataBits8;
    public static readonly SerialParity DefaultParity = SerialParity.None;
    public static readonly SerialStopBits DefaultStopBits = SerialStopBits.Bits1;

    private const char HeaderControl = '\u008C';
    private const char HeaderQuery = '\u0083';
    private const char HeaderAnswer = '\u0070';

    private const char Category = '\u0000';
    private const char FunctionPower = '\u0000';
    private const char FunctionInput = '\u0002';
    private const char FunctionVolume = '\u0005';
    private const char FunctionMute = '\u0006';
    private const char Length2IncludingChecksum = '\u0002';
    private const char Length3IncludingChecksum = '\u0003';
    private const char Direct = '\u0001';

    // 8c+00+00+02 = 8e
    private readonly char[] _powerOnCommand = [HeaderControl, Category, FunctionPower, Length2IncludingChecksum, '\u0001', '\u008f'
    ];
    private readonly char[] _powerOffCommand = [HeaderControl, Category, FunctionPower, Length2IncludingChecksum, '\u0000', '\u008e'
    ];

    // 8c + 00+05+03+1 = 95
    private readonly char[] _volumeHeader = [HeaderControl, Category, FunctionVolume, Length3IncludingChecksum, Direct];
    private readonly char _volumeHeaderChecksum = '\u0095';

    private static readonly Dictionary<Input, char[]> InputDictionary = new ()
    {
        // 8c+00+02+03+04 = 95
        { Input.Hdmi1, [HeaderControl, Category, FunctionInput, Length3IncludingChecksum, '\u0004', '\u0001', '\u0096']
        },
        { Input.Hdmi2, [HeaderControl, Category, FunctionInput, Length3IncludingChecksum, '\u0004', '\u0002', '\u0097']
        },
        { Input.Hdmi3, [HeaderControl, Category, FunctionInput, Length3IncludingChecksum, '\u0004', '\u0003', '\u0098']
        },
        { Input.Hdmi4, [HeaderControl, Category, FunctionInput, Length3IncludingChecksum, '\u0004', '\u0004', '\u0099']
        },
    };

    private static readonly Dictionary<MuteState, char[]> MuteDictionary = new()
    {
        { MuteState.On , [HeaderControl, Category, FunctionMute, Length3IncludingChecksum, Direct, '\u0001', '\u0097'] },
        { MuteState.Off, [HeaderControl, Category, FunctionMute, Length3IncludingChecksum, Direct, '\u0000', '\u0096'] }
    };

    private readonly SerialClient _client;

    public SonySerialControl(SerialClient client, string name, Input? defaultInput) 
        : base(InputDictionary.Keys.ToList(), name, defaultInput, client, CommandStringFormat.Hex)
    {
        _client = client;
    }

    protected override void HandleConnectionState(ConnectionState connectionState) { }

    protected override Task DoPoll(CancellationToken token) => PollWorker.Stop();

    private void SendCommand(char [] command) => _client.Send(command);

    protected override void DoPowerOn() => SendCommand(_powerOnCommand);

    protected override void DoPowerOff() => SendCommand(_powerOffCommand);

    protected override void DoSetInput(Input input) => SendCommand(InputDictionary[input]);

    protected override void DoSetAudioMute(MuteState state) => SendCommand(MuteDictionary[state]);

    private byte CalculateChecksum(char[] command)
    {
        char checksum = '\u0000';
        foreach (char value in command)
        {
            checksum += value;
        }
        return (byte)checksum;
    }

    protected override void DoSetVolume(int volume)
    {
        char volumeChar = (char)volume;
        char checksum = (char)(_volumeHeaderChecksum + volumeChar);
        var commandWithoutChecksum = _volumeHeader.Append(volumeChar);
        var command = commandWithoutChecksum.Append(checksum);
        SendCommand(command.ToArray());
    }
}