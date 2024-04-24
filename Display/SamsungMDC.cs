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

    private byte _currentPoll = InputControlCommand;
    private readonly byte[] _pollPowerCommand;
    private readonly byte[] _pollInputCommand;


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
    }

    protected override void Poll()
    {
        if (_currentPoll == InputControlCommand)
        {
            // Poll power
            _currentPoll = PowerControlCommand;
            CommunicationClient.Send(_pollPowerCommand);
            LogHandlers?.Invoke("Polling Power");
        }
        else
        {
            // Poll Input
            _currentPoll = InputControlCommand;
            CommunicationClient.Send(_pollInputCommand);
            LogHandlers?.Invoke("Polling Input");
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
        PowerCommand(0x01);
        DesiredPowerState = PowerState.On;
    }

    public override void PowerOff()
    {
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
        if (response[0] != 0xAA)
            return;
        
        if(response[4] == (byte)'A')
            UpdateCommunicationState(CommunicationState.Okay);
        else
        {
            UpdateCommunicationState(CommunicationState.Error);
            return;
        }
        
        switch ((byte) response[5])
        {
            case PowerControlCommand:
                PowerState = (byte)response[6] switch
                {
                    0x00 => PowerState.Off,
                    0x01 => PowerState.On,
                    _ => PowerState
                };
                if (PowerState != DesiredPowerState)
                {
                    if(DesiredPowerState == PowerState.On)
                        PowerOn();
                    else
                        PowerOff();
                }
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