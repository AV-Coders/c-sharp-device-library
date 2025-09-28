using AVCoders.Core;
using Serilog;
using Serilog.Context;

namespace AVCoders.Display;

public delegate void InputHandler(Input input);

public abstract class Display : VolumeControl, IDevice
{
    public IReadOnlyList<Event> Events => _events;
    public List<Input> SupportedInputs { get; }
    public readonly CommunicationClient CommunicationClient;
    public readonly CommandStringFormat CommandStringFormat;
    private readonly Dictionary<string, string> _logProperties = new ();
    private Input _input = Input.Unknown;
    private Input _desiredInput = Input.Unknown;
    private PowerState _powerState = PowerState.Unknown;
    private PowerState _desiredPowerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;
    public readonly Input? DefaultInput;
    public CommunicationStateHandler CommunicationStateHandlers;
    public PowerStateHandler PowerStateHandlers;
    public PowerStateHandler DesiredPowerStateHandlers;
    public InputHandler InputHandlers;
    public InputHandler DesiredInputHandlers;
    private MuteState _audioMute = MuteState.Unknown;
    private readonly List<Event> _events = [];
    protected MuteState DesiredAudioMute = MuteState.Unknown;
    protected MuteState DesiredVideoMute = MuteState.Unknown;
    protected event ActionHandler? EventsUpdated;

    protected readonly ThreadWorker PollWorker;

    protected Display(List<Input> supportedInputs, string name, Input? defaultInput, CommunicationClient communicationClient, CommandStringFormat commandStringFormat, int pollTime = 23)
        : base(name, VolumeType.Speaker)
    {
        CommunicationStateHandlers = x => AddEvent(EventType.DriverState,  x.ToString());
        PowerStateHandlers = x => AddEvent(EventType.Power, x.ToString());
        DesiredPowerStateHandlers = x => AddEvent(EventType.Power, $"Desired power state is now {x.ToString()}");
        InputHandlers = x => AddEvent(EventType.Input, x.ToString());
        DesiredInputHandlers = x => AddEvent(EventType.Input, $"Desired input is now {x.ToString()}");
        VolumeLevelHandlers = x => AddEvent(EventType.Volume,  x.ToString());
        MuteStateHandlers = x => AddEvent(EventType.Volume, $"Mute: {x.ToString()}");
        
        SupportedInputs = supportedInputs;
        DefaultInput = defaultInput;
        CommunicationClient = communicationClient;
        CommandStringFormat = commandStringFormat;
        CommunicationClient.ConnectionStateHandlers += x => AddEvent(EventType.Connection, x.ToString());
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        CommunicationState = CommunicationState.NotAttempted;
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        new Thread(_ =>
        {
            Thread.Sleep(1000);
            PollWorker.Restart();
        }).Start();
    }

    protected abstract void HandleConnectionState(ConnectionState connectionState);

    public Input Input
    {
        get => _input;
        protected set
        {
            if (_input == value) 
                return;
            _input = value;
            InputHandlers.Invoke(value);
        }
    }

    public Input DesiredInput
    {
        get => _desiredInput;
        protected set
        {
            if (_desiredInput == value)
                return;
            _desiredInput = value;
            DesiredInputHandlers.Invoke(value);
        }
    }

    public PowerState PowerState
    {
        get => _powerState;
        protected set
        {
            if(_powerState == value)
                return;
            _powerState = value;
            PowerStateHandlers.Invoke(value);
        }
    }

    public PowerState DesiredPowerState
    {
        get => _desiredPowerState;
        protected set
        {
            if(_desiredPowerState == value)
                return;
            _desiredPowerState = value;
            DesiredPowerStateHandlers.Invoke(value);
        }
    }


    public MuteState AudioMute
    {
        get => _audioMute;
        protected set
        {
            if (_audioMute == value)
                return;
            _audioMute = value;
            MuteState = value;
            MuteStateHandlers?.Invoke(AudioMute);
        }
    }

    public MuteState VideoMute { get; protected set; } = MuteState.Unknown;

    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if(_communicationState == value)
                return;
            _communicationState = value;
            CommunicationStateHandlers.Invoke(value);
        }
    }

    protected Task Poll(CancellationToken token)
    {
        using (LogContext.PushProperty(LogBase.MethodProperty, "Poll"))
        {
            return DoPoll(token);
        }
    }
    protected abstract Task DoPoll(CancellationToken token);

    protected void ProcessPowerResponse()
    {
        using (PushProperties("ProcessPowerResponse"))
        {
            if (PowerState == DesiredPowerState)
                return;
            if (DesiredPowerState == PowerState.Unknown)
                return;
            Log.Information("{Name} has the incorrect power state - Forcing Power", Name);
            AddEvent(EventType.Power, $"The power state is incorrect, setting to desired power state {_desiredPowerState.ToString()}");
            if (DesiredPowerState == PowerState.Off)
                PowerOff();
            else if (DesiredPowerState == PowerState.On)
                PowerOn();
        }
    }
    
    protected void ProcessInputResponse()
    {
        using (PushProperties("ProcessInputResponse"))
        {
            InputHandlers.Invoke(Input);
            if (Input == DesiredInput)
                return;
            if (DesiredInput == Input.Unknown)
                return;
            Log.Information("{Name} has the incorrect input - Forcing Input", Name);
            AddEvent(EventType.Input, $"The input is incorrect, setting to desired input {_desiredInput.ToString()}");
            SetInput(DesiredInput);
        }
    }

    public void TogglePower()
    {
        if(PowerState == PowerState.On)
            PowerOff();
        else
            PowerOn();
    }
    
    public void PowerOn()
    {
        DoPowerOn();
        PowerState = PowerState.On;
        DesiredPowerState = PowerState.On;
        if(DefaultInput != null)
            DesiredInput = DefaultInput.Value;
    }

    protected abstract void DoPowerOn();

    public void PowerOff()
    {
        DoPowerOff();
        PowerState = PowerState.Off;
        DesiredPowerState = PowerState.Off;
    }

    protected abstract void DoPowerOff();

    public void SetInput(Input input)
    {
        if (!SupportedInputs.Contains(input))
        {
            Log.Error("Requested Input {Input} is not available", input);
            AddEvent(EventType.Error, $"Requested Input {input.ToString()} is not available");
            return;
        }
        DoSetInput(input);
        DesiredInput = input;
        Input = input;
    }

    protected abstract void DoSetInput(Input input);

    public void SetVolume(int volume)
    {
        if (volume is > 100 or < 0)
        {
            Log.Error("Volume needs to be a value between 0 and 100, it's {Volume}", volume);
            AddEvent(EventType.Error, $"Volume needs to be a value between 0 and 100, it's {volume}");
            return;
        }
        DoSetVolume(volume);
        Volume = volume;
    }

    protected abstract void DoSetVolume(int percentage);

    public override void LevelUp(int amount)
    {
        int newVolume = Volume + amount;
        if(newVolume > 100)
            newVolume = 100;
        SetVolume(newVolume);
    }

    public override void LevelDown(int amount)
    {
        int newVolume = Volume - amount;
        if(newVolume < 0)
            newVolume = 0;
        SetVolume(newVolume);
    }

    public override void SetLevel(int level) => SetVolume(level);

    public override void SetAudioMute(MuteState state)
    {
        DesiredAudioMute = state;
        DoSetAudioMute(state);
        AudioMute = state;
    }

    protected abstract void DoSetAudioMute(MuteState state);

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

    public void ClearEvents()
    {
        _events.Clear();
        EventsUpdated?.Invoke();
    }

    protected void AddEvent(EventType type, string info)
    {
        Log.Verbose(info);
        _events.Add(new Event(DateTimeOffset.Now, type, info, LogContext.Clone()));
        LimitEvents();
        EventsUpdated?.Invoke();
    }

    private void LimitEvents()
    {
        if (_events.Count > 300)
        {
            _events.RemoveRange(0, _events.Count - 300);
        }
    }
}