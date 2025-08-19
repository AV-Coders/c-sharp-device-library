using AVCoders.Core;
using Serilog;

namespace AVCoders.Display;

public class PhilipsSICP : Display
{
    public const ushort DefaultPort = 5000;
    public static readonly SerialSpec DefaultSpec = new (SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    
    private readonly List<byte> _gather = new();

    private readonly byte _monitorId;
    private readonly byte _groupId;

    private static readonly Dictionary<Input, byte> _inputMap = new Dictionary<Input, byte>
    {
        { Input.Hdmi1, 0x0d},
        { Input.Hdmi2, 0x06},
        { Input.Hdmi3, 0x0F},
        { Input.Hdmi4, 0x19},
    };

    public PhilipsSICP(CommunicationClient client, byte monitorId, byte groupId, string name, Input? defaultInput, int pollTime = 23) : base(
        _inputMap.Keys.ToList(), name, defaultInput, client, pollTime)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _monitorId = monitorId;
        _groupId = groupId;
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties("HandleResponse"))
        {
            if (response.Length < response[0])
            {
                Log.Error("The response was too small");
                return;
            }

            switch (response[3])
            {
                case 0x19:
                    PowerState = response[4] switch
                    {
                        0x01 => PowerState.Off,
                        0x02 => PowerState.On,
                        _ => PowerState
                    };
                    ProcessPowerResponse();
                    break;
                case 0xAD:
                    Input = response[4] switch
                    {
                        0x0d => Input.Hdmi1,
                        0x06 => Input.Hdmi2,
                        0x0f => Input.Hdmi3,
                        0x19 => Input.Hdmi4,
                        _ => Input
                    };
                    ProcessInputResponse();
                    break;
                case 0x45:
                    Volume = response[4];
                    break;
                case 0x46:
                    AudioMute = response[4] switch
                    {
                        0x00 => MuteState.Off,
                        0x01 => MuteState.On,
                        _ => MuteState.Unknown
                    };
                    break;
            }
        }
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState == ConnectionState.Connected)
            PollWorker.Restart();
    }

    protected override async Task DoPoll(CancellationToken token)
    {
        if (CommunicationClient.GetConnectionState() != ConnectionState.Connected)
            return;
        
        Send([0x19], 0x00);
        if (PowerState == PowerState.On)
        {
            await Task.Delay(3000, token);
            Send([0xAD], 0x00);
            await Task.Delay(3000, token);
            Send([0x45], 0x00);
            await Task.Delay(3000, token);
            Send([0x46], 0x00);
        } 
    }

    private void Send(byte[] data) => Send(data, _groupId);

    private void Send(byte[] data, byte groupId)
    {
        byte messageSize = (byte)(data.Length + 4);
        byte[] payload = new byte[messageSize];
        
        payload[0] = messageSize;
        payload[1] = _monitorId;
        payload[2] = groupId;
        Array.Copy(data, 0, payload, 3, data.Length);
        
        byte checksum = GenerateChecksum(payload);
        payload[messageSize - 1] = checksum;
        CommunicationClient.Send(payload);
    }

    private byte GenerateChecksum(byte[] bytes)
    {
        byte checksum = 0x00;
        foreach (var t in bytes)
        {
            checksum ^= t;
        }

        return checksum;
    }

    protected override void DoPowerOn()
    {
        Send(new byte[] { 0x18, 0x02 });
    }

    protected override void DoPowerOff()
    {
        Send(new byte[] { 0x18, 0x01 });
    }

    protected override void DoSetInput(Input input)
    {
        Send(new byte[] { 0xAC, _inputMap[input], 0x00, 0x01, 0x00 });
    }

    protected override void DoSetVolume(int percentage)
    {
        Send(new byte[] { 0x44, (byte)percentage, (byte)percentage });
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        Send(new byte[] { 0x47, (byte)(state == MuteState.On ? 0x01 : 0x00) });
    }
}