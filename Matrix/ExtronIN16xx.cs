using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public partial class ExtronIn16Xx : VideoMatrix
{
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public readonly List<ExtronMatrixOutput> ComposedOutputs = [];
    public readonly List<ExtronMatrixInput> Inputs = [];
    public List<ExtronMatrixEndpoint> Outputs => ComposedOutputs
        .SelectMany(output => new[] { output.Primary, output.Secondary })
        .Where(endpoint => endpoint.InUse)
        .ToList();

    private const string EscapeHeader = "\x1b";
    private const string SignalDetected = "1";
    private const int MaximumSyncTimeout = 501;
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private readonly int _numberOfInputs;
    private readonly TimeProvider _timeProvider;
    private readonly ThreadWorker _pollWorker;
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name)
        : this(communicationClient, numberOfInputs, name, TimeProvider.System)
    {
    }

    internal ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name, TimeProvider timeProvider)
        : base(1, communicationClient, name)
    {
        _numberOfInputs = numberOfInputs;
        _timeProvider = timeProvider;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, _timeProvider);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        communicationClient.ResponseHandlers += HandleResponse;
    }

    [GeneratedRegex(@"^In00 ([01](?:\*[01])*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SignalPresenceRegex();

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (SignalPresenceRegex().Match(line.Trim()) is { Success: true } presence)
                    HandleSignalPresence(presence.Groups[1].Value);
            }
        }
    }

    private void HandleSignalPresence(string statuses)
    {
        var values = statuses.Split('*');
        SetInputCount(values.Length);
        for (var index = 0; index < values.Length; index++)
            Inputs[index].SetInputStatus(values[index] == SignalDetected ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void SetInputCount(int count)
    {
        if (Inputs.Count == count)
            return;
        while (Inputs.Count > count)
        {
            LogBaseRegistry.Deregister(Inputs[^1]);
            Inputs.RemoveAt(Inputs.Count - 1);
        }
        while (Inputs.Count < count)
        {
            var number = Inputs.Count + 1;
            Inputs.Add(new ExtronMatrixInput($"Input {number}", number));
        }
        EndpointsChangedHandlers?.Invoke();
    }
    
    private void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState != ConnectionState.Connected)
            return;
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(async _ =>
            {
                try
                {
                    await Task.Delay(ConnectStepGap, _timeProvider);
                    WrapAndSendCommand("3CV");
                    await Task.Delay(ConnectStepGap, _timeProvider);
                    SendCommand("I");
                    WrapAndSendCommand("0LS");
                }
                catch (Exception e)
                {
                    LogException(e, "The connection sequence failed");
                }
            }, TaskScheduler.Default).Unwrap();
    }
    
    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        WrapAndSendCommand("CV");
        WrapAndSendCommand("0LS");
        return Task.CompletedTask;
    }

    private void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}!");
            AddEvent(EventType.Input, $"Switched output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteAV"))
                LogWarning("Not switching output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }

    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override int NumberOfOutputs => 1;
    public override int NumberOfInputs => Inputs.Count;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}%");
            AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching video output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteVideo"))
                LogWarning("Not switching video output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public override bool SupportsAudioBreakaway => true;

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}$");
            AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching audio output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteAudio"))
                LogWarning("Not switching audio output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds <= MaximumSyncTimeout)
        {
            WrapAndSendCommand($"T{seconds}SSAV");
            AddEvent(EventType.VideoMute, $"Set sync timeout to {seconds} seconds");
        }
        else
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than {MaximumSyncTimeout} seconds");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout can't be longer than {MaximumSyncTimeout} seconds", MaximumSyncTimeout);
        }
    }

    public void SetVideoMute(MuteState state)
    {
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(async _ =>
            {
                try
                {
                    SendCommand(state == MuteState.On ? "1*2B" : "1*0B");
                    await Task.Delay(VideoMuteStepGap, _timeProvider);
                    SendCommand(state == MuteState.On ? "2*2B" : "2*0B");
                    await Task.Delay(VideoMuteStepGap, _timeProvider);
                    SendCommand(state == MuteState.On ? "3*2B" : "3*0B");
                }
                catch (Exception e)
                {
                    LogException(e, "The video mute sends failed");
                }
            }, TaskScheduler.Default).Unwrap();
        AddEvent(EventType.VideoMute, $"Switched video mute to {state}");
    }
}