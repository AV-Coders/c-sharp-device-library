using AVCoders.Core;
using AVCoders.Dsp;

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
    Unknown, Dialling, Connected, Disconnecting, Ringing, Idle
}

public abstract class Conference : IDevice
{
    protected PowerState PowerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    protected string Uri = String.Empty;
    protected Dictionary<int, Call> ActiveCalls = new ();
    public LogHandler? LogHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public readonly Fader OutputVolume;
    public readonly Mute OutputMute;
    public readonly Mute MicrophoneMute;
    public List<Call> GetActiveCalls() => ActiveCalls.Values.ToList().FindAll(x => x.Status != CallStatus.Idle);

    public PowerState GetCurrentPowerState() => PowerState;
    
    public string GetUri() => Uri;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    protected readonly ThreadWorker PollWorker;
    

    protected Conference(int pollTimeInSeconds = 52)
    {
        OutputVolume = new Fader(_ => {}, false);
        MicrophoneMute = new Mute(_ => {});
        OutputMute = new Mute(_ => {});
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTimeInSeconds), true);
        PollWorker.Restart();
    }

    protected void ProcessPowerResponse()
    {
        PowerStateHandlers?.Invoke(PowerState);
        if (PowerState == DesiredPowerState)
            return;
        if (DesiredPowerState == PowerState.Unknown)
            return;
        Log("Forcing Power");
        if (DesiredPowerState == PowerState.Off)
            PowerOff();
        else if (DesiredPowerState == PowerState.On) 
            PowerOn();
    }

    protected abstract Task Poll(CancellationToken token);

    protected void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {message}");

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }
    public void PowerOn()
    {
        DoPowerOn();
        Log("Turning On");
        PowerState = PowerState.On;
        DesiredPowerState = PowerState.On;
        PowerStateHandlers?.Invoke(DesiredPowerState);
    }

    protected abstract void DoPowerOn();

    public void PowerOff()
    {
        DoPowerOff();
        Log("Turning Off");
        PowerState = PowerState.Off;
        DesiredPowerState = PowerState.Off;
        PowerStateHandlers?.Invoke(DesiredPowerState);
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

    public void ToggleOutputMute()
    {
        SetOutputMute(OutputMute.MuteState == MuteState.On ? MuteState.Off : MuteState.On);
    }

    public void ToggleMicrophoneMute()
    {
        SetMicrophoneMute(MicrophoneMute.MuteState == MuteState.On ? MuteState.Off : MuteState.On);
    }
    
}