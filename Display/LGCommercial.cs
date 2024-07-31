using System.Net;
using AVCoders.Core;
using AVCoders.MediaPlayer;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.Display;

public class LGCommercial : Display, ISetTopBox
{
    // Source:
    // https://www.lg.com/ca_en/support/product-support/troubleshoot/help-library/cs-CT52001643-20153058982994/
    public static readonly ushort DefaultPort = 9761;
    private readonly CommunicationClient _comms;
    private readonly byte[]? _wolPacket;
    private readonly int _setId;
    private readonly string _pollArgument = "FF";
    private readonly string _powerHeader = "ka";
    private readonly string _inputHeader = "xb";
    private readonly string _volumeHeader = "kf";
    private readonly string _muteHeader = "ke";
    private readonly string _irccHeader = "mc";
    private readonly string _channelHeader = "ma";
    private readonly Dictionary<Input, string> _inputDictionary = new()
    {
        { Input.Hdmi1, "90" },
        { Input.Hdmi2, "91" },
        { Input.Hdmi3, "92" },
        { Input.Hdmi4, "93" },
        { Input.DvbtTuner, "00" }
    };
    private readonly Dictionary<MuteState, string> _muteDictionary = new()
    {
        { MuteState.On, "00" },
        { MuteState.Off, "01" }
    };

    private static readonly Dictionary<RemoteButton, string> RemoteButtonMap = new()
    {
        { RemoteButton.Button0, "10"},
        { RemoteButton.Button1, "11"},
        { RemoteButton.Button2, "12"},
        { RemoteButton.Button3, "13"},
        { RemoteButton.Button4, "14"},
        { RemoteButton.Button5, "15"},
        { RemoteButton.Button6, "16"},
        { RemoteButton.Button7, "17"},
        { RemoteButton.Button8, "18"},
        { RemoteButton.Button9, "19"},
        { RemoteButton.Enter, "44"},
        { RemoteButton.Up, "40"},
        { RemoteButton.Down, "41"},
        { RemoteButton.Left, "07"},
        { RemoteButton.Right, "06"},
        { RemoteButton.Subtitle, "39"}
    };

    public LGCommercial(CommunicationClient comms, string? mac, int setId = 1) : base(new List<Input>
    {
        Input.Hdmi1, Input.Hdmi2, Input.Hdmi3, Input.Hdmi4, Input.DvbtTuner
    })
    {
        _comms = comms;
        _setId = setId;
        if (mac != null)
            _wolPacket = BuildMagicPacket(ParseMacAddress(mac));
        
        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

    private void SendCommand(string header, string value) => _comms.Send($"{header} {_setId:d2} {value}\r");

    protected override void Poll()
    {
        PowerState = _comms.GetConnectionState() switch
        {
            ConnectionState.Connected => PowerState.On,
            _ => PowerState.Off
        };
        if (PowerState != DesiredPowerState)
        {
            ProcessPowerResponse();
            return;
        }

        if (PowerState != PowerState.On) 
            return;
        
        Thread.Sleep(1000);
        SendCommand(_inputHeader, _pollArgument);
        Thread.Sleep(1000);
        SendCommand(_volumeHeader, _pollArgument);
        Thread.Sleep(1000);
        SendCommand(_muteHeader,  _pollArgument);
    }

    private void SendWol()
    {
        if (_wolPacket == null)
            return;
        using var client = new UdpClient();
        for (int i = 0; i < 3; i++)
        {
            client.Send(_wolPacket, new IPEndPoint(IPAddress.Broadcast, 7));
            Thread.Sleep(75);
            client.Send(_wolPacket, new IPEndPoint(IPAddress.Broadcast, 9));
            Thread.Sleep(300);
        }
    }
    
        
    private byte[] BuildMagicPacket(byte[] macAddress)
    {
        if (macAddress.Length != 6) throw new ArgumentException();

        List<byte> magic = new List<byte>();
        for (int i = 0; i < 6; i++)
        {
            magic.Add(0xff);
        }

        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                magic.Add(macAddress[j]);
            }
        }
        return magic.ToArray();
    }

    private static byte[] ParseMacAddress(string text, char[]? separator = null)
    {
        if (separator == null) separator = new char[] { ':', '-' };
        string[] tokens = text.Split(separator);

        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(tokens[i], 16);
        }
        return bytes;
    }

    protected override void DoPowerOn()
    {
        SendCommand(_powerHeader, "01");
        SendWol();
    }

    protected override void DoPowerOff() => SendCommand(_powerHeader, "00");

    protected override void DoSetInput(Input input) => SendCommand(_inputHeader, _inputDictionary[input]);

    protected override void DoSetVolume(int percentage) => SendCommand(_volumeHeader, $"{percentage:x2}");

    protected override void DoSetAudioMute(MuteState state) => SendCommand(_muteHeader, _muteDictionary[state]);

    public void ChannelUp() => SendCommand(_irccHeader, "00");

    public void ChannelDown() => SendCommand(_irccHeader, "01");

    public void SendIRCode(RemoteButton button) => SendCommand(_irccHeader, RemoteButtonMap[button]);

    public void SetChannel(int channel) => SendCommand(_channelHeader, $"00 {channel:X2} 10");
}
