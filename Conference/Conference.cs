using AVCoders.Core;
using AVCoders.Dsp;

namespace AVCoders.Conference;

public abstract class Conference : IDevice
{
    protected PowerState PowerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public Fader OutputVolume;
    public Mute OutputMute;
    public Mute MicrophoneMute;

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    protected readonly ThreadWorker PollWorker;
    

    protected Conference(int pollTime = 52)
    {
        OutputVolume = new Fader(_ => {}, false);
        MicrophoneMute = new Mute(_ => {});
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        PollWorker.Restart();
    }
    
    protected abstract void Poll();

    protected void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {message}");

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    public abstract void PowerOff();

    public abstract void PowerOn();

    public abstract void SendDtmf(char number);

    public abstract void Dial(string number);
}