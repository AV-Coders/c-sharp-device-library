using System.Diagnostics;
using System.Text;
using AVCoders.Core;
using AVCoders.MediaPlayer;
using Serilog;

namespace AVCoders.Display;

public class SonySimpleIpControl : Display, ISetTopBox
{
    public static readonly ushort DefaultPort = 20060;
    private static readonly Dictionary<Input, string> InputDictionary = new ()
    {
        { Input.Hdmi1, "0000000100000001" },
        { Input.Hdmi2, "0000000100000002" },
        { Input.Hdmi3, "0000000100000003" },
        { Input.Hdmi4, "0000000100000004" },
        { Input.DvbtTuner, "0000000000000000" }
    };
    private static readonly Dictionary<string, PowerState> PowerStateMap = new()
    {
        { "*SNPOWR0000000000000001", PowerState.On },
        { "*SNPOWR0000000000000000", PowerState.Off }
    };

    private static readonly List<RemoteButton> UnsupportedButtons = new()
    {
        RemoteButton.Guide
    };

    private static readonly Dictionary<RemoteButton, int> RemoteButtonMap = new()
    {
        { RemoteButton.Button0, 27},
        { RemoteButton.Button1, 18},
        { RemoteButton.Button2, 19},
        { RemoteButton.Button3, 20},
        { RemoteButton.Button4, 21},
        { RemoteButton.Button5, 22},
        { RemoteButton.Button6, 23},
        { RemoteButton.Button7, 24},
        { RemoteButton.Button8, 25},
        { RemoteButton.Button9, 26},
        { RemoteButton.Enter, 13},
        { RemoteButton.Back, 8 },
        { RemoteButton.Up, 9},
        { RemoteButton.Down, 10},
        { RemoteButton.Left, 12},
        { RemoteButton.Right, 11},
        { RemoteButton.Subtitle, 35},
        { RemoteButton.Power, 98},
        { RemoteButton.VolumeUp, 30},
        { RemoteButton.VolumeDown, 31},
        { RemoteButton.Mute, 32},
        { RemoteButton.ChannelUp, 33},
        { RemoteButton.ChannelDown, 34},
        { RemoteButton.Play, 78},
        { RemoteButton.Pause, 84},
        { RemoteButton.Stop, 81},
        { RemoteButton.Rewind, 79},
        { RemoteButton.FastForward, 77},
        { RemoteButton.Previous, 80},
        { RemoteButton.Next, 82},
        { RemoteButton.Home, 6},
        { RemoteButton.Blue, 17},
        { RemoteButton.Yellow, 16},
        { RemoteButton.Green, 15},
        { RemoteButton.Red, 14},
        // { RemoteButton.Guide, },
        { RemoteButton.Menu, 7},
    };

    public SonySimpleIpControl(TcpClient tcpClient, string name, Input? defaultInput) : base(InputDictionary.Keys.ToList(), name, defaultInput, tcpClient)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
    }

    protected override void HandleConnectionState(ConnectionState connectionState) { }

    protected override Task DoPoll(CancellationToken token) => PollWorker.Stop();

    private void SendCommand(String command)
    {
        try
        {
            CommunicationClient.Send(command);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            CommunicationState = CommunicationState.Error;
            System.Diagnostics.Debug.WriteLine($"Sony Simple IP Control - Communication error: {e.Message}");
        }
    }

    private String WrapMessage(string message)
    {
        StringBuilder builder = new StringBuilder("*S");
        builder.Append(message);
        builder.Append('\n');
        return builder.ToString();
    }

    protected override void DoPowerOff() => SendCommand(WrapMessage($"CPOWR{0:D16}"));

    protected override void DoPowerOn() => SendCommand(WrapMessage($"CPOWR{1:D16}"));

    private void HandleResponse(String response)
    {
        Verbose("HandleResponse");
        foreach (var singleResponse in response.Split('\n'))
        {
            var trimmedResponse = singleResponse.TrimStart('\t', ' ');
            Verbose(singleResponse);
            Verbose(trimmedResponse);
            if (trimmedResponse.StartsWith("*SNPOWR"))
            {
                PowerState = PowerStateMap.GetValueOrDefault(trimmedResponse, PowerState.Unknown);
                ProcessPowerResponse();
                CommunicationState = CommunicationState.Okay;
            }
            else if (trimmedResponse.StartsWith("*SNVOLU"))
            {
                Volume = Int32.Parse(trimmedResponse.Remove(0, 7));
                CommunicationState = CommunicationState.Okay;
            }
            else if (trimmedResponse.StartsWith("*SNAMUT"))
            {
                AudioMute = trimmedResponse.EndsWith('1') ? MuteState.On : MuteState.Off;
                CommunicationState = CommunicationState.Okay;
            }
            else if (trimmedResponse.StartsWith("*SNINPT"))
            {
                string value = trimmedResponse.Remove(0, 7);
                Input = InputDictionary.Keys.FirstOrDefault(key => InputDictionary[key] == value, Input.Unknown);
                ProcessInputResponse();
                CommunicationState = CommunicationState.Okay;
            }
        }
    }

    protected override void DoSetInput(Input input) => SendCommand(WrapMessage($"CINPT{InputDictionary[input]}"));

    protected override void DoSetVolume(int volume) => SendCommand(WrapMessage($"CVOLU{volume:D16}"));

    protected override void DoSetAudioMute(MuteState state) => SendCommand(WrapMessage($"CAMUT{(state == MuteState.On ? 1 : 0):D16}"));

    public void SetVideoMute(MuteState desiredState)
    {
        SendCommand(WrapMessage($"CPMUT{(desiredState == MuteState.On ? 1 : 0):D16}"));
        VideoMute = desiredState;
    }
    public void ChannelUp() => SendIRCode(RemoteButton.ChannelUp);

    public void ChannelDown() => SendIRCode(RemoteButton.ChannelDown);

    public void SendIRCode(RemoteButton button)
    {
        if (UnsupportedButtons.Contains(button))
        {
            Log.Warning($"Unsupported button - {button.ToString()}");
            return;
        }
        SendCommand(WrapMessage($"CIRCC{RemoteButtonMap[button]:D16}"));

        if (button == RemoteButton.Power)
            DesiredPowerState = PowerState.Unknown;
    }

    public void SetChannel(int channel) => SendCommand(WrapMessage($"CCHNN{channel:D16}"));
    public void ToggleSubtitles() => SendIRCode(RemoteButton.Subtitle);
}
