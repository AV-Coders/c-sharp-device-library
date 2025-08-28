using AVCoders.Core;
using Serilog;
using Serilog.Context;

namespace AVCoders.Display;

public delegate void InputHandler(Input input);

public abstract class Display : VolumeControl, IDevice
{
    public List<Input> SupportedInputs { get; }
    public readonly CommunicationClient CommunicationClient;
    public readonly string InstanceUid;
    private readonly Dictionary<string, string> _logProperties = new ();
    private Input _input = Input.Unknown;
    private Input _desiredInput = Input.Unknown;
    private PowerState _powerState = PowerState.Unknown;
    private PowerState _desiredPowerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;
    private readonly Input? _defaultInput;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public PowerStateHandler? DesiredPowerStateHandlers;
    public InputHandler? InputHandlers;
    public InputHandler? DesiredInputHandlers;
    private MuteState _audioMute = MuteState.Unknown;
    protected MuteState DesiredAudioMute = MuteState.Unknown;
    private MuteState _videoMute = MuteState.Unknown;
    protected MuteState DesiredVideoMute = MuteState.Unknown;

    protected readonly ThreadWorker PollWorker;

    protected Display(List<Input> supportedInputs, string name, Input? defaultInput, CommunicationClient communicationClient, int pollTime = 23) : base(name, VolumeType.Speaker)
    {
        SupportedInputs = supportedInputs;
        _defaultInput = defaultInput;
        CommunicationClient = communicationClient;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        CommunicationState = CommunicationState.NotAttempted;
        InstanceUid = Guid.NewGuid().ToString();
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
            InputHandlers?.Invoke(value);
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
            DesiredInputHandlers?.Invoke(value);
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
            PowerStateHandlers?.Invoke(value);
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
            DesiredPowerStateHandlers?.Invoke(value);
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

    public MuteState VideoMute
    {
        get => _videoMute;
        protected set => _videoMute = value;
    }
    
    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if(_communicationState == value)
                return;
            _communicationState = value;
            CommunicationStateHandlers?.Invoke(value);
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
            Log.Information("{Name} has hte incorrect power state - Forcing Power", Name);
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
            InputHandlers?.Invoke(Input);
            if (Input == DesiredInput)
                return;
            if (DesiredInput == Input.Unknown)
                return;
            Log.Information("{Name} has hte incorrect input - Forcing Input", Name);
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
        Log.Verbose("Turning On");
        PowerState = PowerState.On;
        DesiredPowerState = PowerState.On;
        if(_defaultInput != null)
            DesiredInput = _defaultInput.Value;
    }

    protected abstract void DoPowerOn();

    public void PowerOff()
    {
        DoPowerOff();
        Log.Verbose("Turning Off");
        PowerState = PowerState.Off;
        DesiredPowerState = PowerState.Off;
    }

    protected abstract void DoPowerOff();

    public void SetInput(Input input)
    {
        if (!SupportedInputs.Contains(input))
        {
            Log.Error("Requested Input {Input} is not available", input);
            return;
        }
        DoSetInput(input);
        Log.Verbose("Setting input to {ToString}", input.ToString());
        DesiredInput = input;
        Input = input;
    }

    protected abstract void DoSetInput(Input input);

    public void SetVolume(int volume)
    {
        if (volume is > 100 or < 0)
        {
            Log.Error("Volume needs to be a value between 0 and 100, it's {Volume}", volume);
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
        Log.Verbose("Setting audio mute to {ToString}", state.ToString());
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

    public void AddLogProperty(string name, string value)
    {
        _logProperties[name] = value;
    }
    protected IDisposable PushProperties(string? methodName = null)
    {
        var disposables = new List<IDisposable>();

        foreach (var property in _logProperties)
        {
            disposables.Add(LogContext.PushProperty(property.Key, property.Value));
        }
        
        disposables.Add(LogContext.PushProperty("InstanceUid", InstanceUid));
        disposables.Add(LogContext.PushProperty("Class", GetType().Name));
        disposables.Add(LogContext.PushProperty("InstanceName", Name));
        if(methodName != null)
            disposables.Add(LogContext.PushProperty(LogBase.MethodProperty, methodName));

        return new DisposableItems(disposables);
    }
    
    protected void LogException(Exception e)
    {
        using (PushProperties())
        {
            Log.Error("{ExceptionType} \r\n{ExceptionMessage}\r\n{StackTrace}",
                e.GetType().Name, e.Message, e.StackTrace);
            if (e.InnerException == null)
                return;
            Log.Error("Caused by: {InnerExceptionType} \r\n{InnerExceptionMessage}\r\n{InnerStackTrace}",
                e.InnerException.GetType().Name, e.InnerException.Message, e.InnerException.StackTrace);
        }
    }
}