using AVCoders.Core;

namespace AVCoders.Display;

public class PjLink : Display
{
    public static readonly ushort DefaultPort = 4352;
    public const string DefaultPassword = "JBMIAProjectorLink";
    public readonly TcpClient TcpClient;
    private readonly string _password;
    private static readonly Dictionary<Input, int> InputDictionary = new ()
    {
        { Input.Hdmi1, 31 },
        { Input.Hdmi2, 32 },
        { Input.Hdmi3, 33 },
        { Input.Hdmi4, 34 },
        { Input.Network6, 56 }
    };
    
    private static readonly Dictionary<PowerState, int> PowerStateDictionary = new ()
    {
        { PowerState.Off, 0 },
        { PowerState.On, 1 },
        { PowerState.Cooling, 2 },
        { PowerState.Warming, 3 }
    };
    
    private PollTask _pollTask;

    public PjLink(TcpClient tcpClient, string password = DefaultPassword) : base(InputDictionary.Keys.ToList())
    {
        _password = password;
        _pollTask = PollTask.Power;
        DesiredAudioMute = MuteState.Off;
        DesiredVideoMute = MuteState.Off;
        
        TcpClient = tcpClient;
        TcpClient.SetPort(DefaultPort);
        TcpClient.ResponseHandlers += HandleResponse;
    }

    protected override void Poll()
    {
        if (TcpClient.GetConnectionState() != ConnectionState.Connected)
        {
            Log("Not polling");
            return;
        }
        
        Log("Polling");
        PollProjector(_pollTask);
        
        _pollTask = _pollTask switch
        {
            PollTask.Power => PollTask.Input,
            PollTask.Input => PollTask.AudioMute,
            PollTask.AudioMute => PollTask.Power,
            _ => _pollTask
        };
    }

    private void PollProjector(PollTask pollTask)
    {
        Log("Polling");
        switch (pollTask)
        {
            case PollTask.Power:
                Send("POWR ?");
                break;
            case PollTask.Input:
                Send("INPT ?");
                break;
            case PollTask.AudioMute:
                Send("AVMT ?");
                break;
        }
    }

    private void HandleResponse(string response)
    {
        if (response.Contains("OK"))
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }

        if (response.Contains("ERR3")) // It's just not the right time for this command
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }

        if (response.Contains("ERR")) // ERR2 and ERR4
        {
            CommunicationState = CommunicationState.Error;
            return;
        }

        if (response.Contains("PJLINK"))
        {
            CommunicationState = CommunicationState.Okay;
            if (response.Contains('1'))
            {
                TcpClient.Send($"{_password}\r");
            }

            return;
        }

        var responses = response.Split('=');
        var value = Int32.Parse(responses[1]);

        if (responses[0].Contains("POWR"))
        {
            PowerState = PowerStateDictionary.FirstOrDefault(x => x.Value == value).Key;
            ProcessPowerResponse();
        }
        else if (responses[0].Contains("INPT"))
        {
            Input = InputDictionary.FirstOrDefault(x => x.Value == value).Key;
            ProcessInputResponse();
        }
        else if (responses[0].Contains("AVMT"))
        {
            switch (value)
            {
                case 11:
                    VideoMute = MuteState.On;
                    AudioMute = MuteState.Off;
                    break;
                case 21:
                    VideoMute = MuteState.Off;
                    AudioMute = MuteState.On;
                    break;
                case 30:
                    VideoMute = MuteState.Off;
                    AudioMute = MuteState.Off;
                    break;
                case 31:
                    VideoMute = MuteState.On;
                    AudioMute = MuteState.On;
                    break;
            }

            SendMuteState();
        }

        CommunicationState = CommunicationState.Okay;
    }

    private void Send(string command) => TcpClient.Send($"%1{command}\r");

    private void SetPowerState(PowerState desiredPowerState)
    {
        if (!PowerStateDictionary.TryGetValue(desiredPowerState, out var value))
        {
            Error($"Desired PowerState {desiredPowerState} is not appropriate");
            return;
        }

        Send($"POWR {value}");
        DesiredPowerState = desiredPowerState;
    }

    protected override void DoPowerOn() => SetPowerState(PowerState.On);

    protected override void DoPowerOff() => SetPowerState(PowerState.Off);

    protected override void DoSetInput(Input input) => Send($"INPT {InputDictionary[input]}");

    protected override void DoSetVolume(int volume) => Error("Volume control is not supported");

    protected override void DoSetAudioMute(MuteState state) => SendMuteState();

    public void SetPictureMute(MuteState state)
    {
        DesiredVideoMute = state;
        SendMuteState();
    }
    
    private enum MuteCommandToSend
    {
        None = 30,
        AudioOnly = 21,
        VideoOnly = 11,
        Both = 31
    }

    private void SendMuteState()
    {
        MuteCommandToSend commandToSend;
        if (DesiredAudioMute == MuteState.On)
        {
            commandToSend = DesiredVideoMute == MuteState.On ? MuteCommandToSend.Both : MuteCommandToSend.AudioOnly;
        }
        else
        {
            commandToSend = DesiredVideoMute == MuteState.On ? MuteCommandToSend.VideoOnly : MuteCommandToSend.None;
        }
        Send($"AVMT {(int)commandToSend}");
    }
}