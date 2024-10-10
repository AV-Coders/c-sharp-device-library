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


    public SamsungMdc(CommunicationClient communicationClient, byte displayId) : base(new List<Input>
    {
        Input.Hdmi1, Input.Hdmi2, Input.Hdmi3, Input.Hdmi4, Input.DvbtTuner
    })
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

    protected override Task Poll(CancellationToken token)
    {
        if (CommunicationClient.GetConnectionState() != ConnectionState.Connected)
        {
            Log("Not polling");
            return Task.CompletedTask;
        }
        
        Log("Polling Power");
        
        CommunicationClient.Send(_pollPowerCommand);
        if (PowerState == PowerState.On)
        {
            Task.Delay(1000, token);
            CommunicationClient.Send(_pollInputCommand);
            Task.Delay(1000, token);
            CommunicationClient.Send(_pollVolumeCommand);
            Task.Delay(1000, token);
            CommunicationClient.Send(_pollMuteCommand);
        } 
        return Task.CompletedTask;
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

    protected override void DoPowerOn() => PowerCommand(0x01);

    protected override void DoPowerOff() => PowerCommand(0x00);
    
    private byte GenerateChecksum(byte[] input)
    {
        IEnumerable<byte> source = (input[0] == 0xAA) ? input.Skip(1) : input;

        var value = source.Sum(v => v).ToString("X2");

        var sum = Convert.ToByte(value.Substring(value.Length - 2, 2), 16);

        return sum;
    }

    public void HandleResponse(byte[] response)
    {
        // Log($"Response received, bytes: {BitConverter.ToString(response)}");

        if (response[0] != 0xAA && response[1] != 0xFF)
        {
            Log("The response does not have the correct header");
            return;
        }

        if (response[4] == 0x4E)
        {
            Log("NAK Received");
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
                ProcessPowerResponse();
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
                ProcessInputResponse();
                break;
            case VolumeControlCommand:
                Volume = response[6];
                Log($"The current volume is {Volume}");
                VolumeLevelHandlers?.Invoke(Volume);
                break;
            case MuteControlCommand:
                AudioMute = response[6] switch
                {
                    0x00 => MuteState.Off,
                    0x01 => MuteState.On,
                    _ => MuteState.Unknown
                };
                Log($"The current mute state is {AudioMute.ToString()}");
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

    protected override void DoSetInput(Input input) => sendCommandWithOneDataLength(InputControlCommand, _inputDictionary[input]);

    protected override void DoSetVolume(int volume) => sendCommandWithOneDataLength(VolumeControlCommand, (byte)volume);

    protected override void DoSetAudioMute(MuteState state)
    {
        Log($"Setting mute to {state.ToString()}");
        sendCommandWithOneDataLength(MuteControlCommand, _muteDictionary[state]);
    }
}