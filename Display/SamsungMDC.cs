using System.Diagnostics;
using AVCoders.Core;

namespace AVCoders.Display;

public class SamsungMdc : Display
{
    public static readonly ushort DefaultPort = 1515;
    private readonly byte _displayId;
    public readonly CommunicationClient CommunicationClient;
    private readonly Dictionary<Input, byte> _inputDictionary;
    private readonly Dictionary<MuteState, byte> _muteDictionary;

    private const byte Header = 0xAA;
    private const byte PowerControlCommand = 0x11;
    private const byte VolumeControlCommand = 0x12;
    private const byte MuteControlCommand = 0x13;
    private const byte InputControlCommand = 0x14;
    private const byte DataLength1 = 0x01;
    
    private readonly byte[] _pollPowerCommand;
    private readonly byte[] _pollInputCommand;
    private readonly byte[] _pollVolumeCommand;
    private readonly byte[] _pollMuteCommand;


    public SamsungMdc(CommunicationClient communicationClient, byte displayId)
    {
        _displayId = displayId;

        CommunicationClient = communicationClient;
        CommunicationClient.ResponseByteHandlers += HandleResponse;

        UpdateCommunicationState(CommunicationState.NotAttempted);

        _inputDictionary = new Dictionary<Input, byte>
        {
            { Input.Hdmi1, 0x21 },
            { Input.Hdmi2, 0x23 },
            { Input.Hdmi3, 0x31 },
            { Input.Hdmi4, 0x33 },
            { Input.DvbtTuner, 0x40 }
        };

        _muteDictionary = new Dictionary<MuteState, byte>
        {
            { MuteState.On, 0x01 },
            { MuteState.Off, 0x00 }
        };

        byte[] pollPowerCommandWithoutChecksum = { 0xAA, PowerControlCommand, _displayId, 0x00 };
        _pollPowerCommand = new byte[]{ 0xAA, 0x11, _displayId, 0x00, GenerateChecksum(pollPowerCommandWithoutChecksum) };
        
        byte[] pollInputCommandWithoutChecksum =  { 0xAA, InputControlCommand, _displayId, 0x00 };
        _pollInputCommand = new byte[] { 0xAA, InputControlCommand, _displayId, 0x00,  GenerateChecksum(pollInputCommandWithoutChecksum)};

        byte[] pollVolumeCommandWithoutChecksum = { 0xAA, VolumeControlCommand, _displayId, 0x00 };
        _pollVolumeCommand = new byte[]{ 0xAA, VolumeControlCommand, _displayId, 0x00, GenerateChecksum(pollVolumeCommandWithoutChecksum) };
        
        byte[] pollMuteCommandWithoutChecksum =  { 0xAA, MuteControlCommand, _displayId, 0x00 };
        _pollMuteCommand = new byte[] { 0xAA, MuteControlCommand, _displayId, 0x00,  GenerateChecksum(pollMuteCommandWithoutChecksum)};
    }

    protected override void Poll()
    {
        if (CommunicationClient.GetConnectionState() != ConnectionState.Connected)
        {
            LogHandlers?.Invoke("Not polling");
            return;
        }
        
        LogHandlers?.Invoke("Polling Power");
        
        CommunicationClient.Send(_pollPowerCommand);
        if (PowerState == PowerState.On)
        {
            Thread.Sleep(1000);
            CommunicationClient.Send(_pollInputCommand);
            Thread.Sleep(1000);
            CommunicationClient.Send(_pollVolumeCommand);
            Thread.Sleep(1000);
            CommunicationClient.Send(_pollMuteCommand);
        }
    }

    private void SendByteArray(byte[] bytes)
    {
        try
        {
            CommunicationClient.Send(bytes);
            UpdateCommunicationState(CommunicationState.Okay);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    private void PowerCommand(byte data)
    {
        byte[] commandWithoutChecksum = { Header, PowerControlCommand, _displayId, DataLength1, data };
        byte[] commandWithChecksum =
            { Header, PowerControlCommand, _displayId, DataLength1, data, GenerateChecksum(commandWithoutChecksum) };
        SendByteArray(commandWithChecksum);
    }

    public override void PowerOn()
    {
        LogHandlers?.Invoke("Turning On");
        PowerCommand(0x01);
        DesiredPowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        LogHandlers?.Invoke("Turning Off");
        PowerCommand(0x00);
        DesiredPowerState = PowerState.Off;
    }
    
    private byte GenerateChecksum(byte[] input)
    {
        IEnumerable<byte> source = (input[0] == 0xAA) ? input.Skip(1) : input;

        var value = source.Sum(v => v).ToString("X2");

        var sum = Convert.ToByte(value.Substring(value.Length - 2, 2), 16);

        return sum;
    }

    public void HandleResponse(byte[] response)
    {
        // LogHandlers?.Invoke($"Response received, bytes: {BitConverter.ToString(response)}");

        if (response[0] != 0xAA && response[1] != 0xFF)
        {
            LogHandlers?.Invoke("The response does not have the correct header");
            return;
        }

        if (response[4] == 0x4E)
        {
            LogHandlers?.Invoke("NAK Received");
            UpdateCommunicationState(CommunicationState.Error);
            return;
        }
        
        UpdateCommunicationState(CommunicationState.Okay);
        
        switch (response[5])
        {
            case PowerControlCommand:
                PowerState = response[6] switch
                {
                    0x00 => PowerState.Off,
                    0x01 => PowerState.On,
                    _ => PowerState
                };
                PowerStateHandlers?.Invoke(PowerState);
                AlignPowerState();
                break;
            case InputControlCommand:
                Input = response[6] switch
                {
                    0x21 => Input.Hdmi1,
                    0x23 => Input.Hdmi2,
                    0x31 => Input.Hdmi3,
                    0x33 => Input.Hdmi4,
                    0x60 => Input.DvbtTuner,
                    _ => Input
                };
                InputHandlers?.Invoke(Input);
                AlignInput();
                break;
            case VolumeControlCommand:
                Volume = response[6];
                LogHandlers?.Invoke($"The current volume is {Volume}");
                VolumeLevelHandlers?.Invoke(Volume);
                break;
            case MuteControlCommand:
                AudioMute = response[6] switch
                {
                    0x00 => MuteState.Off,
                    0x01 => MuteState.On,
                    _ => MuteState.Unknown
                };
                LogHandlers?.Invoke($"The current mute state is {AudioMute.ToString()}");
                MuteStateHandlers?.Invoke(AudioMute);
                break;
        }
    }

    private void sendCommandWithOneDataLength(byte command, byte data)
    {
        byte[] commandWithoutChecksum = { Header, command, _displayId, DataLength1, data };
        byte[] commandWithChecksum =
            { Header, command, _displayId, DataLength1, data, GenerateChecksum(commandWithoutChecksum) };
        SendByteArray(commandWithChecksum);
    }

    public override void SetInput(Input input)
    {
        LogHandlers?.Invoke($"Setting input to {input.ToString()}");
        if (_inputDictionary.TryGetValue(input, out var inputByte))
        {
            sendCommandWithOneDataLength(InputControlCommand, inputByte);
            Input = input;
            InputHandlers?.Invoke(Input);
        }
    }

    public override void SetVolume(int volume)
    {
        if (volume >= 0 && volume <= 100)
        {
            sendCommandWithOneDataLength(VolumeControlCommand, (byte)volume);
            Volume = volume;
            VolumeLevelHandlers?.Invoke(Volume);
        }
    }

    public override void SetAudioMute(MuteState state)
    {
        LogHandlers?.Invoke($"Setting mute to {state.ToString()}");
        sendCommandWithOneDataLength(MuteControlCommand, _muteDictionary[state]);
        AudioMute = state;
        MuteStateHandlers?.Invoke(AudioMute);
    }

    public override void ToggleAudioMute()
    {
        switch (AudioMute)
        {
            case MuteState.On:
                SetAudioMute(MuteState.Off);
                break;
            default:
                SetAudioMute(MuteState.On);
                break;
        }
    }
}