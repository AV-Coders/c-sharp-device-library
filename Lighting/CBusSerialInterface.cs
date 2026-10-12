using AVCoders.Core;
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

public record CBusGroupMessage(byte SourceUnit, byte Application, byte Command, byte Group, byte Level);

public record CBusChannelLevel(byte Unit, byte Channel, byte Level);

public delegate void CBusGroupMessageHandler(CBusGroupMessage message);

public delegate void CBusChannelLevelHandler(CBusChannelLevel channelLevel);

public class CBusSerialInterface : LogBase
{
    public const byte LightingApplication = 0x38;
    public const byte SceneApplication = 0xCA;
    public const byte OnCommand = 0x79;
    public const byte OffCommand = 0x01;
    public const byte FullLevel = 0xFF;
    public const byte ZeroLevel = 0x00;
    public const int MaxQueuedCommands = 95;
    public const int MaxLineLength = 150;
    public static readonly SerialSpec SerialSpec = new(SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan SetupStepDelay = TimeSpan.FromMilliseconds(300);

    private const byte PointToMultipointHeader = 0x05;
    private const byte PointToPointHeader = 0x06;
    private const byte StatusReplyHeader = 0x86;
    private const byte StatusReplyCommand = 0x82;
    private const byte StatusRequestCommand = 0x2A;
    private const byte StatusRequestChannelCount = 0x01;
    private const byte LocalNetwork = 0x00;
    private const byte LongFormFlag = 0x80;
    private const byte LongFormLengthMask = 0x1F;
    private const byte ShortFormLengthMask = 0x07;
    private const int RampArgumentCount = 2;
    private const int MultipointHeaderLength = 3;
    private const int HeaderIndex = 0;
    private const int SourceUnitIndex = 1;
    private const int ApplicationIndex = 2;
    private const int RoutingIndex = 3;
    private const int SalHeaderLength = 4;
    private const int StatusUnitIndex = 1;
    private const int StatusCommandIndex = 4;
    private const int StatusChannelIndex = 5;
    private const int StatusLevelIndex = 6;
    private const int StatusReplyLength = 8;
    private const int HexCharactersPerByte = 2;
    private const byte ConfirmationCharacter = (byte)'r';
    private const byte LineFeed = 0x0A;
    private const byte HighestHexCharacter = (byte)'F';
    private const char Delimiter = '\r';
    private const char BeginPacket = '\\';

    private static readonly string[] PciSetupStrings =
    [
        "~~~\r",
        "~~~p\r",
        "@A330005100p\r",
        "@A3410051p\r",
        "@A3420007p\r",
        "@A32100FF00p\r",
    ];

    private readonly CommunicationClient _comms;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<string> _queue = new();
    private readonly object _queueLock = new();
    private readonly List<byte> _line = [];
    private ITimer? _confirmationTimer;
    private string? _inFlight;

    public CBusGroupMessageHandler? GroupMessageHandlers;
    public CBusChannelLevelHandler? ChannelLevelHandlers;

    public CBusSerialInterface(CommunicationClient comms, TimeProvider? timeProvider = null) : base("Cbus Interface")
    {
        _comms = comms;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _comms.ResponseByteHandlers += HandleResponse;
    }

    public int QueuedCommands
    {
        get
        {
            lock (_queueLock)
                return _queue.Count;
        }
    }

    public async Task SetupPci()
    {
        lock (_queueLock)
        {
            _queue.Clear();
            _confirmationTimer?.Dispose();
            _inFlight = null;
        }

        foreach (var setupString in PciSetupStrings)
        {
            _comms.Send(setupString);
            await Task.Delay(SetupStepDelay, _timeProvider).ConfigureAwait(false);
        }
    }

    public void SendPointToMultipointPayload(byte application, byte[] data)
    {
        byte[] payload = new byte[MultipointHeaderLength + data.Length];
        payload[0] = PointToMultipointHeader;
        payload[1] = application;
        payload[2] = LocalNetwork;
        Array.Copy(data, 0, payload, MultipointHeaderLength, data.Length);
        Enqueue(BuildWireString(payload));
    }

    public void RequestChannelStatus(byte unit, byte channel)
    {
        Enqueue(BuildWireString([PointToPointHeader, unit, LocalNetwork, StatusRequestCommand, channel, StatusRequestChannelCount]));
    }

    private static string BuildWireString(byte[] data)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(BeginPacket);
        sb.Append(Convert.ToHexString(data));
        sb.Append((char)ConfirmationCharacter);
        sb.Append(Delimiter);
        return sb.ToString();
    }

    private void Enqueue(string wire)
    {
        lock (_queueLock)
        {
            if (_inFlight == null)
            {
                Transmit(wire);
                return;
            }

            if (_queue.Count >= MaxQueuedCommands)
            {
                LogWarning("{Count} commands waiting for the PCI, clearing the queue", _queue.Count);
                _queue.Clear();
            }

            _queue.Enqueue(wire);
        }
    }

    private void Transmit(string wire)
    {
        _inFlight = wire;
        _confirmationTimer = _timeProvider.CreateTimer(HandleConfirmationTimeout, wire, ConfirmationTimeout, Timeout.InfiniteTimeSpan);
        _comms.Send(wire);
    }

    private void SendNext()
    {
        lock (_queueLock)
        {
            _confirmationTimer?.Dispose();
            if (_queue.Count == 0)
            {
                _inFlight = null;
                return;
            }

            Transmit(_queue.Dequeue());
        }
    }

    private void HandleConfirmationTimeout(object? wire)
    {
        lock (_queueLock)
        {
            if (!ReferenceEquals(wire, _inFlight))
                return;
            LogWarning("No confirmation from the PCI within {Timeout} ms, sending the next command", ConfirmationTimeout.TotalMilliseconds);
            SendNext();
        }
    }

    private void HandleResponse(byte[] response)
    {
        foreach (byte b in response)
        {
            switch (b)
            {
                case ConfirmationCharacter:
                    _line.Clear();
                    SendNext();
                    break;
                case LineFeed:
                    byte[] line = _line.ToArray();
                    _line.Clear();
                    ProcessLine(line);
                    break;
                case >= (byte)'0' and <= (byte)'9':
                case >= (byte)'A' and <= HighestHexCharacter:
                    if (_line.Count == MaxLineLength)
                    {
                        LogWarning("No line feed within {Length} characters, clearing the line", MaxLineLength);
                        _line.Clear();
                    }
                    _line.Add(b);
                    break;
                case > HighestHexCharacter:
                    _line.Clear();
                    break;
            }
        }
    }

    private void ProcessLine(byte[] line)
    {
        if (line.Length < HexCharactersPerByte || line.Length % HexCharactersPerByte != 0)
            return;

        byte[] message = Convert.FromHexString(Encoding.ASCII.GetString(line));
        using (PushProperties("ProcessLine"))
        {
            LogVerbose("{Message}", BitConverter.ToString(message));
        }

        switch (message[HeaderIndex])
        {
            case PointToMultipointHeader:
                ProcessMonitoredSal(message);
                break;
            case StatusReplyHeader:
                ProcessStatusReply(message);
                break;
        }
    }

    private void ProcessMonitoredSal(byte[] message)
    {
        if (message.Length <= SalHeaderLength)
            return;

        byte sourceUnit = message[SourceUnitIndex];
        byte application = message[ApplicationIndex];
        int checksumIndex = message.Length - 1;
        int index = SalHeaderLength + message[RoutingIndex];

        while (index < checksumIndex)
        {
            byte command = message[index];
            int argumentCount = (command & LongFormFlag) != 0 ? command & LongFormLengthMask : command & ShortFormLengthMask;
            if (index + argumentCount >= checksumIndex)
                return;

            switch (command)
            {
                case OnCommand:
                    GroupMessageHandlers?.Invoke(new CBusGroupMessage(sourceUnit, application, command, message[index + 1], FullLevel));
                    break;
                case OffCommand:
                    GroupMessageHandlers?.Invoke(new CBusGroupMessage(sourceUnit, application, command, message[index + 1], ZeroLevel));
                    break;
                default:
                    if ((command & LongFormFlag) == 0 && argumentCount == RampArgumentCount)
                        GroupMessageHandlers?.Invoke(new CBusGroupMessage(sourceUnit, application, command, message[index + 1], message[index + 2]));
                    break;
            }

            index += 1 + argumentCount;
        }
    }

    private void ProcessStatusReply(byte[] message)
    {
        if (message.Length < StatusReplyLength || message[StatusCommandIndex] != StatusReplyCommand)
            return;

        ChannelLevelHandlers?.Invoke(new CBusChannelLevel(message[StatusUnitIndex], message[StatusChannelIndex], message[StatusLevelIndex]));
    }
}
