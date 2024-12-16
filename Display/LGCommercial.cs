using System.Globalization;
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
    private static readonly Dictionary<Input, string> InputDictionary = new()
    {
        { Input.Hdmi1, "90" },
        { Input.Hdmi2, "91" },
        { Input.Hdmi3, "92" },
        { Input.Hdmi4, "93" },
        { Input.DvbtTuner, "00" }
    };
    private static readonly Dictionary<MuteState, string> MuteDictionary = new()
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
        { RemoteButton.Subtitle, "39"},
        { RemoteButton.Back, "5B"},
        { RemoteButton.Power, "08"},
        { RemoteButton.VolumeUp, "02"},
        { RemoteButton.VolumeDown, "03"},
        { RemoteButton.Mute, "09"},
        { RemoteButton.ChannelUp, "00"},
        { RemoteButton.ChannelDown, "01"},
        { RemoteButton.Play, "B0"},
        { RemoteButton.Pause, "01"},
        { RemoteButton.Stop, "B1"},
        { RemoteButton.Rewind, "8F"},
        { RemoteButton.FastForward, "8E"},
        { RemoteButton.Previous, "8F"},
        { RemoteButton.Next, "8E"},
        { RemoteButton.Home, "42"},
        { RemoteButton.Blue, "61"},
        { RemoteButton.Yellow, "63"},
        { RemoteButton.Green, "71"},
        { RemoteButton.Red, "73"},
        { RemoteButton.Guide, "AB"},
        { RemoteButton.Menu, "43"},
    };

    public LGCommercial(CommunicationClient comms, string name, string? mac, Input defaultInput, int setId = 1) : 
        base(InputDictionary.Keys.ToList(), name, defaultInput, 12)
    {
        _comms = comms;
        _comms.ResponseHandlers += HandleResponse;
        _comms.ConnectionStateHandlers += HandleConnectionState;
        _setId = setId;
        if (mac != null)
            _wolPacket = BuildMagicPacket(ParseMacAddress(mac));
        
        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected) 
            return;
        Poll(new CancellationToken());
    }

    private void HandleResponse(string response)
    {
        if (!response.Contains($" {_setId:d2} OK"))
            return;
        var data = response.Split("OK");
        
        if (data[0].Contains($"a {_setId:d2} "))
        {
            PowerState = data[1] switch
            {
                "01x" => PowerState.On,
                "00x" => PowerState.Off,
                _ => PowerState
            };
            ProcessPowerResponse();
        }
        else if (data[0].Contains($"b {_setId:d2} "))
        {
            Input = data[1] switch
            {
                "90x" => Input.Hdmi1,
                "91x" => Input.Hdmi2,
                "92x" => Input.Hdmi3,
                "93x" => Input.Hdmi4,
                "00x" => Input.DvbtTuner,
                _ => Input
            };
            ProcessInputResponse();
        }
        else if (data[0].Contains($"f {_setId:d2} "))
        {
            int.TryParse(data[1].Substring(0, 2), NumberStyles.HexNumber, null, out Volume);
            Log($"The current volume is {Volume}");
            VolumeLevelHandlers?.Invoke(Volume);
        }
        else if (data[0].Contains($"e {_setId:d2} "))
        {
            AudioMute = data[1] switch
            {
                "01x" => MuteState.Off,
                "00x" => MuteState.On,
                _ => AudioMute
            };
            Log($"The current mute state is {AudioMute.ToString()}");
            MuteStateHandlers?.Invoke(AudioMute);
        }
    }

    private void SendCommand(string header, string value) => _comms.Send($"{header} {_setId:d2} {value}\r");

    protected override Task Poll(CancellationToken token)
    {
        PowerState = _comms.GetConnectionState() switch
        {
            ConnectionState.Connected => PowerState.On,
            _ => PowerState.Off
        };
        if (PowerState != DesiredPowerState)
        {
            ProcessPowerResponse();
        }

        if (PowerState != PowerState.On)
        {
            Log("Not Polling");
            return Task.CompletedTask;
        }
        
        Log("Polling");
        SendCommand(_powerHeader, _pollArgument);
        Task.Delay(1000, token);
        SendCommand(_inputHeader, _pollArgument);
        Log("Polling input");
        Task.Delay(1000, token);
        SendCommand(_volumeHeader, _pollArgument);
        Log("Polling volume");
        Task.Delay(1000, token);
        SendCommand(_muteHeader,  _pollArgument);
        Log("Polling mute");
        
        return Task.CompletedTask;
    }

    private void SendWol()
    {
        if (_wolPacket == null)
            return;
        using var client = new UdpClient();
        for (int i = 0; i < 3; i++)
        {
            client.Send(_wolPacket, new IPEndPoint(IPAddress.Broadcast, 7));
            Task.Delay(75);
            client.Send(_wolPacket, new IPEndPoint(IPAddress.Broadcast, 9));
            Task.Delay(300);
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

    protected override void DoSetInput(Input input) => SendCommand(_inputHeader, InputDictionary[input]);

    protected override void DoSetVolume(int percentage) => SendCommand(_volumeHeader, $"{percentage:x2}");

    protected override void DoSetAudioMute(MuteState state) => SendCommand(_muteHeader, MuteDictionary[state]);

    public void ChannelUp() => SendCommand(_irccHeader, "00");

    public void ChannelDown() => SendCommand(_irccHeader, "01");

    public void SendIRCode(RemoteButton button)
    {
        SendCommand(_irccHeader, RemoteButtonMap[button]);

        if (button == RemoteButton.Power)
            DesiredPowerState = PowerState.Unknown;
    }

    public void SetChannel(int channel) => SendCommand(_channelHeader, $"00 {channel:X2} 10");
    public void ToggleSubtitles()
    {
        SendIRCode(RemoteButton.Subtitle);
        Thread.Sleep(100);
        SendIRCode(RemoteButton.Subtitle);
        Thread.Sleep(100);
        SendIRCode(RemoteButton.Back);
    }
}
