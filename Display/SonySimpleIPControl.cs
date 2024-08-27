using System.Diagnostics;
using System.Text;
using AVCoders.Core;
using AVCoders.MediaPlayer;

namespace AVCoders.Display;

public class SonySimpleIpControl : Display, ISetTopBox
{
    public static readonly ushort DefaultPort = 20060;
    public readonly TcpClient TcpClient;
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
        { RemoteButton.Up, 9},
        { RemoteButton.Down, 10},
        { RemoteButton.Left, 11},
        { RemoteButton.Right, 12},
        { RemoteButton.Subtitle, 35}
    };

    public SonySimpleIpControl(TcpClient tcpClient)  : base(InputDictionary.Keys.ToList())
    {
        TcpClient = tcpClient;
        TcpClient.SetPort(DefaultPort);
        TcpClient.ResponseHandlers += HandleResponse;

        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

    protected override void Poll() => PollWorker.Stop();

    private void SendCommand(String command)
    {
        try
        {
            TcpClient.Send(command);
            UpdateCommunicationState(CommunicationState.Okay);
        }
        catch (Exception e)
        {
            UpdateCommunicationState(CommunicationState.Error);
            Debug.WriteLine($"Sony Simple IP Control - Communication error: {e.Message}");
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
        Log("HandleResponse");
        foreach (var singleResponse in response.Split('\n'))
        {
            var trimmedResponse = singleResponse.TrimStart('\t', ' ');
            Log(singleResponse);
            Log(trimmedResponse);
            if (trimmedResponse.StartsWith("*SNPOWR"))
            {
                PowerState = PowerStateMap.GetValueOrDefault(trimmedResponse, PowerState.Unknown);
                ProcessPowerResponse();
            }
            else if (trimmedResponse.StartsWith("*SNVOLU"))
            {
                Volume = Int32.Parse(trimmedResponse.Remove(0, 7));
                VolumeLevelHandlers?.Invoke(Volume);
            }
            else if (trimmedResponse.StartsWith("*SNAMUT"))
            {
                AudioMute = trimmedResponse.EndsWith('1') ? MuteState.On : MuteState.Off;
                MuteStateHandlers?.Invoke(AudioMute);
            }
            else if (trimmedResponse.StartsWith("*SNINPT"))
            {
                string value = trimmedResponse.Remove(0, 7);
                Input = InputDictionary.Keys.FirstOrDefault(key => InputDictionary[key] == value, Input.Unknown);
                ProcessInputResponse();
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

    public void SendIrCode(int irCode) => SendCommand(WrapMessage($"CIRCC{irCode:D16}"));
    public void ChannelUp() => SendIrCode(33);

    public void ChannelDown() => SendIrCode(34);

    public void SendIRCode(RemoteButton button) => SendIrCode(RemoteButtonMap[button]);

    public void SetChannel(int channel) => SendCommand(WrapMessage($"CCHNN{channel:D16}"));
    public void ToggleSubtitles() => SendIRCode(RemoteButton.Subtitle);
}