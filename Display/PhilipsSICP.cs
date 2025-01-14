using AVCoders.Core;

namespace AVCoders.Display;

public class PhilipsSICP : Display
{
    public const ushort DefaultPort = 5000;
    public static readonly SerialSpec DefaultSpec = new (SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232); 

    private readonly byte _monitorId;
    private readonly byte _groupId;
    private CommunicationClient _client;

    private static readonly Dictionary<Input, byte> _inputMap = new Dictionary<Input, byte>
    {
        { Input.Hdmi1, 0x0d},
        { Input.Hdmi2, 0x06},
        { Input.Hdmi3, 0x0F},
        { Input.Hdmi4, 0x19},
    };

    public PhilipsSICP(CommunicationClient client, byte monitorId, byte groupId, string name, Input? defaultInput, int pollTime = 23) : base(
        _inputMap.Keys.ToList(), name, defaultInput, pollTime)
    {
        _client = client;
        _monitorId = monitorId;
        _groupId = groupId;
    }


    protected override Task Poll(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    private void Send(byte[] data)
    {
        byte messageSize = (byte)(data.Length + 4);
        byte checksum = GenerateChecksum(messageSize, data);
        byte[] payload = new byte[messageSize];
        
        payload[0] = messageSize;
        payload[1] = _monitorId;
        payload[2] = _groupId;
        Array.Copy(data, 0, payload, 3, data.Length);
        payload[messageSize - 1] = checksum;
        _client.Send(payload);
    }

    private byte GenerateChecksum(byte messageSize, byte[] bytes)
    {
        byte checksum = messageSize;
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