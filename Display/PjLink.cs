using AVCoders.Core;

namespace AVCoders.Display;

public class PjLink : Display
{
    public static readonly ushort DefaultPort = 4352;
    public readonly TcpClient TcpClient;
    private readonly string _password;
    private readonly Dictionary<Input, int> _inputDictionary;
    private readonly Dictionary<PowerState, int> _powerStateDictionary;
    private readonly ThreadWorker _pollWorker;
    private PollTask _pollTask;

    public PjLink(TcpClient tcpClient, string password = "JBMIAProjectorLink", int pollTime = 20000)
    {
        _password = password;
        _pollTask = PollTask.Power;
        DesiredAudioMute = MuteState.Off;
        DesiredVideoMute = MuteState.Off;
        _inputDictionary = new Dictionary<Input, int>
        {
            { Input.Hdmi1, 31 },
            { Input.Hdmi2, 32 },
            { Input.Hdmi3, 33 },
            { Input.Hdmi4, 34 },
            { Input.Network6, 56 }
        };

        _powerStateDictionary = new Dictionary<PowerState, int>
        {
            { PowerState.Off, 0 },
            { PowerState.On, 1 },
            { PowerState.Cooling, 2 },
            { PowerState.Warming, 3 }
        };
        
        _pollWorker = new ThreadWorker(PollProjectorThreadFunction, TimeSpan.FromMilliseconds(pollTime));
        _pollWorker.Restart();
        
        TcpClient = tcpClient;
        TcpClient.SetPort(DefaultPort);
        TcpClient.ResponseHandlers += HandleResponse;
        TcpClient.ConnectionStateHandlers += HandleConnectionState;
        
        HandleConnectionState(TcpClient.GetConnectionState());
    }

    protected override void Poll()
    {
        PollWorker.Stop();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
        {
            _pollWorker.Restart();
            _pollTask = PollTask.Power;
        }
        else
        {
            _pollWorker.Stop();
        }
    }

    private void PollProjectorThreadFunction()
    {
        if (TcpClient.GetConnectionState() != ConnectionState.Connected)
            return;
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
            PowerState = _powerStateDictionary.FirstOrDefault(x => x.Value == value).Key;
            if (PowerState != DesiredPowerState)
            {
                Console.WriteLine("Forcing power state");
                SetPowerState(DesiredPowerState);
            }
        }
        else if (responses[0].Contains("INPT"))
        {
            Input = _inputDictionary.FirstOrDefault(x => x.Value == value).Key;
            if (Input != DesiredInput)
            {
                Console.WriteLine("Forcing input");
                SetInput(DesiredInput);
            }
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

    private void Send(string command)
    {
        TcpClient.Send($"%1{command}\r");
    }

    private void Log(string message, EventLevel level = EventLevel.Verbose)
    {
        LogHandlers?.Invoke($"PjLink - {message}", level);
    }

    private void SetPowerState(PowerState desiredPowerState)
    {
        if (!_powerStateDictionary.ContainsKey(desiredPowerState))
        {
            Log($"Desired PowerState {desiredPowerState} is not appropriate", EventLevel.Error);
            return;
        }

        Send($"POWR {_powerStateDictionary[desiredPowerState]}");
        DesiredPowerState = desiredPowerState;
    }

    public override void PowerOn()
    {
        SetPowerState(PowerState.On);
    }

    public override void PowerOff()
    {
        SetPowerState(PowerState.Off);
    }

    public override void SetInput(Input input)
    {
        if (!_inputDictionary.ContainsKey(input))
        {
            Log($"Desired Input {input} is not appropriate", EventLevel.Error);
            return;
        }

        Send($"INPT {_inputDictionary[input]}");
        DesiredInput = input;
    }

    public override void SetVolume(int volume)
    {
        Log("Volume control is not supported", EventLevel.Error);
    }

    public override void SetAudioMute(MuteState state)
    {
        DesiredAudioMute = state;
        SendMuteState();
    }

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