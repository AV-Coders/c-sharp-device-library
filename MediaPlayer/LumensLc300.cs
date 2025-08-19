using AVCoders.Core;
using Serilog;
using Serilog.Context;

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
    
    private readonly CommunicationClient _client;
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

    public LumensLc300(string name, CommunicationClient client) : base(name)
    {
        _client = client;
        _client.ResponseByteHandlers += HandleResponse;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(10));
        _pollWorker.Restart();
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties("HandleResponse"))
        {
            switch (response[0])
            {
                case EventResponseHeader:
                    if(response[1] == 0x53 && response[2] == 0x54)
                        TransportState = DecodeTransportState(response[3]);
                    break;
                case Header:
                {
                    if (response[4] != AckAction)
                    {
                        Log.Error("Response {response} did not have an ack", BitConverter.ToString(response));
                        return;
                    }

                    if (response.Length != 9)
                        return;
            
                    if (response[5] == 'S' && response[6] == 'T')
                    {
                        TransportState = DecodeTransportState(response[7]);
                        return;
                    }
            
                    if (response[5] == 'L' && response[6] == 'O')
                    {
                        CurrentLayout = response[7];
                    }
                    break;
                }
            }
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
            if (_client.ConnectionState != ConnectionState.Connected)
            {
                return Task.CompletedTask;
            }
        
            SendCommand(GetAction, "ST"u8.ToArray()); // Record Status
            SendCommand(GetAction, "LO"u8.ToArray()); // Layout
        
            return Task.CompletedTask;
        }
    }

    private void SendCommand(byte action, byte[] command, byte? parameters = null)
    {
        byte length =  (byte)(2 + command.Length + (parameters.HasValue ? 1 : 0));
        List<byte> bytes = [Header, ExtendedHeader, length, Address, action];
        bytes.AddRange(command);
        if (parameters != null)
            bytes.Add(parameters.Value);
        bytes.Add(End);
        _client.Send(bytes.ToArray());
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
        SendCommand(SetAction, "SR"u8.ToArray(), 0x32);
        PowerState = PowerState.On;
    }

    /// <summary>
    /// Powers off the device by engaging its standby mode rather than fully shutting it down,
    /// due to limitations in the power on/off functionality.
    /// Sends the appropriate command to transition the device into standby mode.
    /// </summary>
    public override void PowerOff()
    {
        SendCommand(SetAction, "SR"u8.ToArray(), 0x31);
        PowerState = PowerState.Off;
    }

    protected override void DoRecord()
    {
        SendCommand(SetAction, "RC"u8.ToArray());
    }

    protected override void DoPause()
    {
        SendCommand(SetAction, "PS"u8.ToArray());
    }

    protected override void DoStop()
    {
        SendCommand(SetAction, "SP"u8.ToArray());
    }

    public void SetLayout(uint layoutIndex)
    {
        using (PushProperties("SetLayout"))
        {
            if (layoutIndex is 0 or > 18)
            {
                Log.Error("Layout number {layoutNumber} is invalid.  It must be between 1 and 18", layoutIndex);
                return;
            }

            SendCommand(SetAction, "LO"u8.ToArray(), (byte)layoutIndex);
        }
    }
}