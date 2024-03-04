using System.Diagnostics;
using AVCoders.Core;

namespace AVCoders.Display;

public class SamsungMdc : Display
{
    public static readonly ushort DefaultPort = 1515;
    private readonly byte _displayId;
    public readonly TcpClient TcpClient;
    private readonly Dictionary<Input, byte> _inputDictionary;
    private readonly Dictionary<MuteState, byte> _muteDictionary;

    private const byte Header = 0xAA;
    private const byte PowerControlCommand = 0x11;
    private const byte VolumeControlCommand = 0x12;
    private const byte MuteControlCommand = 0x13;
    private const byte InputControlCommand = 0x14;
    private const byte DataLength1 = 0x01;


    public SamsungMdc(byte displayId, TcpClient tcpClient)
    {
        _displayId = displayId;

        TcpClient = tcpClient;
        TcpClient.SetPort(DefaultPort);
        TcpClient.ResponseHandlers += HandleResponse;

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
    }

    private void SendByteArray(byte[] bytes)
    {
        try
        {
            TcpClient.Send(bytes);
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
        PowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        PowerCommand(0x00);
        PowerState = PowerState.Off;
    }
    
    private byte GenerateChecksum(byte[] input)
    {
        IEnumerable<byte> source = (input[0] == 0xAA) ? input.Skip(1) : input;

        var value = source.Sum(v => v).ToString("X2");

        var sum = Convert.ToByte(value.Substring(value.Length - 2, 2), 16);

        return sum;
    }

    public void HandleResponse(string input)
    {
        //TODO: Implement this
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