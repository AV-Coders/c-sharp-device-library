using AVCoders.Core;
using Serilog;

namespace AVCoders.Matrix;

public class ExtronIn16Xx : VideoMatrix
{
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public readonly List<ExtronMatrixOutput> ComposedOutputs = [];
    public readonly List<ExtronMatrixInput> Inputs = [];

    private const string EscapeHeader = "\x1b";
    private readonly int _numberOfInputs;
    private readonly ThreadWorker _pollWorker;

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name) 
        : base(1, communicationClient, name)
    {
        _numberOfInputs = numberOfInputs;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
    }
    
    private void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState != ConnectionState.Connected)
            return;
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        WrapAndSendCommand("3CV");
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        SendCommand("I"); // To get the input and output count, resets the lists and all data
    }
    
    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private Task Poll(CancellationToken arg)
    {
        if(CommunicationClient.ConnectionState == ConnectionState.Connected)
            WrapAndSendCommand("CV");
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
            Log.Error("Not switching output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
        
    }

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
            Log.Error("Not switching video output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public override bool SupportsAudioBreakaway { get; }

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
            Log.Error("Not switching audio output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds < 502)
        {
            SendCommand($"\u001bT{seconds}SSAV\u0027");
            AddEvent(EventType.VideoMute, $"Set sync timeout to {seconds} seconds");
        }
        else
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than 502 seconds");
            Log.Error("The sync timeout can't be longer than 502 seconds");
        }
    }

    public void SetVideoMute(MuteState state)
    {
        SendCommand(state == MuteState.On ? "1*2B" : "1*0B");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        SendCommand(state == MuteState.On ? "2*2B" : "2*0B");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        SendCommand(state == MuteState.On ? "3*2B" : "3*0B");
        AddEvent(EventType.VideoMute, $"Switched video mute to {state}");
    }
}