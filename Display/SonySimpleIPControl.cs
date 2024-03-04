using System.Diagnostics;
using System.Text;
using AVCoders.Core;

namespace AVCoders.Display;

public class SonySimpleIpControl : Display
{
    public static readonly ushort DefaultPort = 20060;
    public readonly TcpClient TcpClient;
    private readonly Dictionary<string, Input> _inputMap;
    private readonly Dictionary<string, PowerState> _powerStateMap;

    public SonySimpleIpControl(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
        TcpClient.SetPort(DefaultPort);
        TcpClient.ResponseHandlers += HandleResponse;
        _inputMap = new Dictionary<string, Input>
        {
            { "0000000100000001", Input.Hdmi1 },
            { "0000000100000002", Input.Hdmi2 },
            { "0000000100000003", Input.Hdmi3 },
            { "0000000100000004", Input.Hdmi4 },
            { "0000000000000000", Input.DvbtTuner }
        };
        _powerStateMap = new Dictionary<string, PowerState>
        {
            { "*SNPOWR0000000000000001", PowerState.On },
            { "*SNPOWR0000000000000000", PowerState.Off }
        };

        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

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

    public override void PowerOff()
    {
        SendCommand(WrapMessage($"CPOWR{0:D16}"));
        PowerState = PowerState.Off;
    }

    public override void PowerOn()
    {
        SendCommand(WrapMessage($"CPOWR{1:D16}"));
        PowerState = PowerState.On;
    }

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
                PowerState = _powerStateMap.GetValueOrDefault(trimmedResponse, PowerState.Unknown);
                PowerStateHandlers?.Invoke(PowerState);
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
                Input = _inputMap.GetValueOrDefault(trimmedResponse.Remove(0, 7), Input.Unknown);
                InputHandlers?.Invoke(Input);
            }
        }
    }

    public override void SetInput(Input input)
    {
        string inputCommand = _inputMap.FirstOrDefault(x => x.Value == input).Key;
        SendCommand(WrapMessage($"CINPT{inputCommand}"));
        Input = input;
        InputHandlers?.Invoke(Input);
    }

    public override void SetVolume(int volume)
    {
        if (volume >= 0 && volume <= 100)
        {
            SendCommand(WrapMessage($"CVOLU{volume:D16}"));
            Volume = volume;
            VolumeLevelHandlers?.Invoke(Volume);
        }
    }

    public override void SetAudioMute(MuteState state)
    {
        SendCommand(WrapMessage($"CAMUT{(state == MuteState.On ? 1 : 0):D16}"));
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

    public void SetVideoMute(MuteState desiredState)
    {
        SendCommand(WrapMessage($"CPMUT{(desiredState == MuteState.On ? 1 : 0):D16}"));
        VideoMute = desiredState;
    }

    public void SendIrCode(int irCode)
    {
        SendCommand(WrapMessage($"CIRCC{irCode:D16}"));
    }
}