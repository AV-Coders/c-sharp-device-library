using AVCoders.Core;
using AVCoders.Dsp;
using Serilog;

namespace AVCoders.Conference;

public record Call
{
    public Call(CallStatus Status, string Name, string Number)
    {
        this.Status = Status;
        this.Name = Name;
        this.Number = Number;
    }

    public CallStatus Status { get; set; }
    public string Name { get; set; }
    public string Number { get; set; }

    public void Deconstruct(out CallStatus status, out string name, out string number)
    {
        status = Status;
        name = Name;
        number = Number;
    }
}

public enum CallStatus
{
    Unknown, Dialling, Connected, Disconnecting, Ringing, Idle, EarlyMedia, Connecting, OnHold
}
public delegate void CallStatusHandler(CallStatus status);

public delegate void ActiveCallHandler(List<Call> activeCalls);

public abstract class Conference : DeviceBase
{
    public CallStatusHandler? CallStatusHandlers;
    public ActiveCallHandler? ActiveCallHandlers;
    public readonly Fader OutputVolume;
    public readonly Mute OutputMute;
    public readonly Mute MicrophoneMute;
    public List<Call> GetActiveCalls() => ActiveCalls.Values.ToList().FindAll(x => x.Status != CallStatus.Idle);
    public string GetUri() => Uri;
    
    protected string Uri = string.Empty;
    protected readonly Dictionary<int, Call> ActiveCalls = new ();
    protected readonly ThreadWorker PollWorker;

    private CallStatus _callStatus;
    public CallStatus CallStatus
    {
        get => _callStatus;
        protected set
        {
            if (value == _callStatus)
                return;
            _callStatus = value;
            CallStatusHandlers?.Invoke(value);
        }
    }
    
    protected Conference(CommunicationClient client, string name = "Main Codec", int pollTimeInSeconds = 52) 
        : base(name, client)
    {
        OutputVolume = new Fader(_ => {}, false);
        MicrophoneMute = new Mute(_ => {});
        OutputMute = new Mute(_ => {});
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTimeInSeconds), true);
        PollWorker.Restart();
    }

    protected abstract Task Poll(CancellationToken token);

    public override void PowerOn()
    {
        DoPowerOn();
        Log.Verbose("Turning On");
        DesiredPowerState = PowerState.On;
        PowerState = PowerState.On;
    }

    protected abstract void DoPowerOn();

    public override void PowerOff()
    {
        DoPowerOff();
        Log.Verbose("Turning Off");
        DesiredPowerState = PowerState.Off;
        PowerState = PowerState.Off;
        ActiveCalls.Keys.ToList().ForEach(x =>
        {
            if (ActiveCalls[x].Status == CallStatus.Idle)
            {
                ActiveCalls.Remove(x);
            }
        });
    }

    public abstract void HangUp(Call? call);

    protected abstract void DoPowerOff();

    public abstract void SendDtmf(char number);

    public abstract void Dial(string number);
    
    public abstract void SetOutputVolume(int percentage);
    
    public abstract void SetOutputMute(MuteState state);
    
    public abstract void SetMicrophoneMute(MuteState state);

    public void ToggleOutputMute() => SetOutputMute(OutputMute.MuteState == MuteState.On ? MuteState.Off : MuteState.On);

    public void ToggleMicrophoneMute() => SetMicrophoneMute(MicrophoneMute.MuteState == MuteState.On ? MuteState.Off : MuteState.On);
    
}