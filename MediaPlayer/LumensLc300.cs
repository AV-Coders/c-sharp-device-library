using AVCoders.Core;

namespace AVCoders.MediaPlayer;

/// <summary>
/// Represents the Lumens LC300 recording device, inheriting from the <see cref="Recorder"/> base class.
/// Provides functionality for managing the power state, recording operations, and layout configurations.
/// </summary>
/// <remarks>
/// This device supports serial communication with default configurations specified in <see cref="DefaultSerialSpec"/>.
/// It operates with limited power on/off functionality, utilizing standby mode instead.
/// </remarks>
public class LumensLc300 : Recorder
{
    public const ushort DefaultPort = 5080;
    public const string DefaultUser = "admin";
    public const string DefaultPassword = "admin";
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    public IntHandler? LayoutChanged;
    
    private readonly ThreadWorker _pollWorker;
    private int _currentLayout = 0;

    private const byte Header = 0x55;
    private const byte EventResponseHeader = 0x23;
    private const byte ExtendedHeader = 0xF0;
    private const byte Address = 0x01; // Doc says the value doesn't matter but 0x00 is reserved
    private const byte GetAction = 0x67;
    private const byte SetAction = 0x73;
    private const byte AckAction = 0x06;
    private const byte NakAction = 0x15;
    private const byte End = 0x0D;
    private const int LengthIndex = 2;
    private const int ActionIndex = 4;
    private const int FrameOverhead = 4;
    private const int NakFrameLength = 2;
    private const int StateEventLength = 5;
    private const int ReplyLength = 9;
    private const int GatherLimit = 1024;
    private const int CommandLength = 2;
    private const int EventCommandIndex = 1;
    private const int EventStateIndex = 3;
    private const int ReplyCommandIndex = 5;
    private const int ReplyValueIndex = 7;
    private static readonly byte[] RecordStatus = "ST"u8.ToArray();
    private static readonly byte[] Layout = "LO"u8.ToArray();

    private readonly List<byte> _gather = [];

    public int CurrentLayout
    {
        get => _currentLayout;
        protected set
        {
            if (_currentLayout == value)
                return;
            _currentLayout = value;
            LayoutChanged?.Invoke(value);
        }
    }

    public LumensLc300(string name, CommunicationClient client) : base(name, client)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(10));
        _pollWorker.Restart();
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties("HandleResponse"))
        {
            _gather.AddRange(response);
            while (true)
            {
                DropBytesBeforeAFrame();
                var frameLength = CompleteFrameLength();
                if (frameLength == 0)
                    break;
                var frame = _gather.GetRange(0, frameLength).ToArray();
                _gather.RemoveRange(0, frameLength);
                HandleFrame(frame);
            }

            if (_gather.Count <= GatherLimit)
                return;
            LogWarning("Gather buffer exceeded {GatherLimit} bytes, clearing {Gather}", GatherLimit,
                BitConverter.ToString(_gather.ToArray()));
            _gather.Clear();
        }
    }

    private void DropBytesBeforeAFrame()
    {
        var start = _gather.FindIndex(value => value is Header or EventResponseHeader or NakAction);
        var unframed = start < 0 ? _gather.Count : start;
        if (unframed == 0)
            return;
        LogDebug("Unhandled response {response}", BitConverter.ToString(_gather.GetRange(0, unframed).ToArray()));
        _gather.RemoveRange(0, unframed);
    }

    private int CompleteFrameLength()
    {
        if (_gather.Count == 0)
            return 0;
        var frameLength = _gather[0] switch
        {
            Header => _gather.Count > LengthIndex ? _gather[LengthIndex] + FrameOverhead : 0,
            EventResponseHeader => _gather.IndexOf(End) + 1,
            _ => NakFrameLength
        };
        return frameLength <= _gather.Count ? frameLength : 0;
    }

    private void HandleFrame(byte[] frame)
    {
        switch (frame[0])
        {
            case EventResponseHeader:
                HandleEvent(frame);
                break;
            case Header:
                HandleReply(frame);
                break;
            default:
                CommunicationState = CommunicationState.Error;
                LogError("Response {response} was a NAK", BitConverter.ToString(frame));
                break;
        }
    }

    private void HandleEvent(byte[] frame)
    {
        if (frame.Length != StateEventLength
            || !frame.AsSpan(EventCommandIndex, CommandLength).SequenceEqual(RecordStatus))
        {
            LogDebug("Unhandled event {response}", BitConverter.ToString(frame));
            return;
        }

        CommunicationState = CommunicationState.Okay;
        TransportState = DecodeTransportState(frame[EventStateIndex]);
    }

    private void HandleReply(byte[] frame)
    {
        if (frame.Length <= ActionIndex)
        {
            LogWarning("Response {response} is too short", BitConverter.ToString(frame));
            return;
        }

        if (frame[ActionIndex] != AckAction)
        {
            CommunicationState = CommunicationState.Error;
            LogError("Response {response} did not have an ack", BitConverter.ToString(frame));
            return;
        }

        CommunicationState = CommunicationState.Okay;
        if (frame.Length != ReplyLength)
            return;

        var command = frame.AsSpan(ReplyCommandIndex, CommandLength);
        if (command.SequenceEqual(RecordStatus))
        {
            TransportState = DecodeTransportState(frame[ReplyValueIndex]);
            return;
        }

        if (command.SequenceEqual(Layout))
        {
            CurrentLayout = frame[ReplyValueIndex];
        }
    }

    private TransportState DecodeTransportState(byte responseByte)
    {
        return responseByte switch
        {
            0x31 => TransportState.Ready,
            0x32 => TransportState.Stopped,
            0x33 => TransportState.Recording,
            0x34 => TransportState.RecordingPaused,
            0x36 => TransportState.Stopping,
            _ => TransportState
        };
    }

    private Task Poll(CancellationToken arg)
    {
        using (PushProperties("Poll"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            {
                return Task.CompletedTask;
            }
        
            SendCommand(GetAction, RecordStatus, []);
            SendCommand(GetAction, Layout, []);
        
            return Task.CompletedTask;
        }
    }

    private void 
        SendCommand(byte action, byte[] command, List<byte> parameters)
    {
        byte length =  (byte)(2 + command.Length + parameters.Count);
        List<byte> bytes = [Header, ExtendedHeader, length, Address, action];
        bytes.AddRange(command);
        parameters.ForEach(bytes.Add);
        bytes.Add(End);
        CommunicationClient.Send(bytes.ToArray());
    }

    /// <summary>
    /// Powers on the device by disabling its standby mode, as the full power-on/off functionality is restricted.
    /// Sends the appropriate command to transition the device to its powered-on state.
    /// </summary>
    /// <remarks>
    /// This method overrides the base PowerOn method and utilizes standby instead of a typical power on/off approach.
    /// </remarks>
    public override void PowerOn()
    {
        SendCommand(SetAction, "SR"u8.ToArray(), [0x32]);
        PowerState = PowerState.On;
    }

    /// <summary>
    /// Powers off the device by engaging its standby mode rather than fully shutting it down,
    /// due to limitations in the power on/off functionality.
    /// Sends the appropriate command to transition the device into standby mode.
    /// </summary>
    public override void PowerOff()
    {
        SendCommand(SetAction, "SR"u8.ToArray(), [0x31]);
        PowerState = PowerState.Off;
    }

    public override void AddMarker()
    {
        throw new NotImplementedException();
    }

    protected override void DoRecord()
    {
        SendCommand(SetAction, "RC"u8.ToArray(), []);
    }

    protected override void DoPause()
    {
        SendCommand(SetAction, "PS"u8.ToArray(), []);
    }

    protected override void DoStop()
    {
        SendCommand(SetAction, "SP"u8.ToArray(), []);
    }

    public void SetLayout(uint layoutIndex)
    {
        using (PushProperties("SetLayout"))
        {
            if (layoutIndex is 0 or > 18)
            {
                LogError("Layout number {layoutNumber} is invalid.  It must be between 1 and 18", layoutIndex);
                return;
            }

            SendCommand(SetAction, Layout, [(byte)layoutIndex]);
        }
    }

    public void SetVideoSourceId(int channel, byte index)
    {
        if (channel is < 0 or > 4)
        {
            using (PushProperties("SetVideoSourceId"))
                LogError("Channel {channel} is invalid.  It must be between 0 and 4", channel);
            return;
        }

        byte channelbyte = (byte)channel.ToString()[0];
        
        SendCommand(SetAction, "CH"u8.ToArray(), [channelbyte, index]);
    }
}