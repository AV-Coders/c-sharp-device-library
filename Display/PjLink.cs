using System.Security.Cryptography;
using System.Text;
using AVCoders.Core;
using Serilog;

namespace AVCoders.Display;

public class PjLink : Display
{
    public static readonly ushort DefaultPort = 4352;
    public const string DefaultPassword = "JBMIAProjectorLink";
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

    public PjLink(TcpClient tcpClient, string name, Input? defaultInput, string password = DefaultPassword) : base(InputDictionary.Keys.ToList(), name, defaultInput, tcpClient)
    {
        _password = password;
        _pollTask = PollTask.Power;
        DesiredAudioMute = MuteState.Off;
        DesiredVideoMute = MuteState.Off;
        
        CommunicationClient.ResponseHandlers += HandleResponse;
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState == ConnectionState.Connected)
            PollWorker.Restart();
    }

    protected override Task DoPoll(CancellationToken token)
    {
        if (CommunicationClient.GetConnectionState() != ConnectionState.Connected)
        {
            Log.Warning("Not polling");
            return Task.CompletedTask;
        }
        
        PollProjector(_pollTask);
        
        _pollTask = _pollTask switch
        {
            PollTask.Power => PollTask.Input,
            PollTask.Input => PollTask.AudioMute,
            PollTask.AudioMute => PollTask.Power,
            _ => _pollTask
        }; 
        return Task.CompletedTask;
    }

    private void PollProjector(PollTask pollTask)
    {
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
        using (PushProperties("HandleResponse"))
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

            if (response.Contains("ERRA"))
            {
                CommunicationState = CommunicationState.Error;
                Log.Error("Password not accepted");
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
                if (!response.Contains('1'))
                    return;

                string[] loginParams = response.Split(' ');
                byte[] answer = GetMd5Hash(loginParams[2] + _password);
                byte[] poll = { 0x25, 0x31, 0x50, 0x4f, 0x57, 0x52, 0x20, 0x3f, 0x0d };
                byte[] combined = new byte[answer.Length + poll.Length];

                Buffer.BlockCopy(answer, 0, combined, 0, answer.Length);
                Buffer.BlockCopy(poll, 0, combined, answer.Length, poll.Length);

                CommunicationClient.Send(combined);
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
    }
    
    public byte[] GetMd5Hash(string input) 
    {
        List<byte> resultBytes = new List<byte>();
        byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(input));
        
        foreach (byte b in hash)
        {
            foreach (byte b1 in Bytes.AsciiRepresentationOfHexEquivalentOf(b, 0, false))
            {
                resultBytes.Add(b1);
            }
        }
        return resultBytes.ToArray();
    }

    private void Send(string command) => CommunicationClient.Send($"%1{command}\r");

    private void SetPowerState(PowerState desiredPowerState)
    {
        using (PushProperties("SetPowerState"))
        {
            if (!PowerStateDictionary.TryGetValue(desiredPowerState, out var value))
            {
                Log.Error("Desired PowerState {DesiredPowerState} is not appropriate", desiredPowerState);
                return;
            }

            Send($"POWR {value}");
            DesiredPowerState = desiredPowerState;
        }
    }

    protected override void DoPowerOn() => SetPowerState(PowerState.On);

    protected override void DoPowerOff() => SetPowerState(PowerState.Off);

    protected override void DoSetInput(Input input) => Send($"INPT {InputDictionary[input]}");

    protected override void DoSetVolume(int volume)
    {
        using (PushProperties("DoSetVolume"))
        {
            Log.Error("Volume control is not supported");   
        }
    }

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