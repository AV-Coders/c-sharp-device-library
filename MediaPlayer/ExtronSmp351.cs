using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public enum ExtronSmp351UsbPort
{
    Unknown,
    Front,
    Rear,
    RCP
}

public class ExtronSmp351 : Recorder
{
    public static readonly ushort DefaultPort = 22023;
    private const int RecordingStateField = 1;
    private const int RecordTimeField = 4;
    private const int UsbTimeRemainingField = 4;
    private const int TimeParts = 3;
    private static readonly TimeSpan VerboseModeDelay = TimeSpan.FromMilliseconds(500);
    private readonly ThreadWorker _pollWorker;
    private readonly object _verboseModeTimerLock = new();
    private Timer? _verboseModeTimer;
    private readonly Regex _responseParser = new("<([^>]*)>", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private ConnectionState _frontUsbConnectionState = ConnectionState.Unknown;
    private ConnectionState _rearUsbConnectionState = ConnectionState.Unknown;
    private ConnectionState _rcpUsbConnectionState = ConnectionState.Unknown;
    private TimeSpan _frontUsbTimeRemaining = TimeSpan.Zero;
    private TimeSpan _rearUsbTimeRemaining = TimeSpan.Zero;
    private TimeSpan _rcpUsbTimeRemaining = TimeSpan.Zero;
    public const string EscapeHeader = "\x1b";
    public ConnectionStateHandler? FrontUsbConnectionStateHandlers;
    public ConnectionStateHandler? RearUsbConnectionStateHandlers;
    public ConnectionStateHandler? RcpUsbConnectionStateHandlers;
    public TimeSpanHandler? FrontUsbRemainingRecordTimeHandlers;
    public TimeSpanHandler? RearUsbRemainingRecordTimeHandlers;
    public TimeSpanHandler? RcpUsbRemainingRecordTimeHandlers;
    private uint _counter = 0;

    public ConnectionState FrontUsbConnectionState
    {
        get => _frontUsbConnectionState;
        private set
        {
            if (_frontUsbConnectionState == value)
                return;
            _frontUsbConnectionState = value;
            FrontUsbConnectionStateHandlers?.Invoke(value);
        } 
    }

    public ConnectionState RearUsbConnectionState
    {
        get => _rearUsbConnectionState;
        private set
        {
            if (_rearUsbConnectionState == value)
                return;
            _rearUsbConnectionState = value;
            RearUsbConnectionStateHandlers?.Invoke(value);
        } 
    }

    public ConnectionState RcpUsbConnectionState
    {
        get => _rcpUsbConnectionState;
        private set
        {
            if (_rcpUsbConnectionState == value)
                return;
            _rcpUsbConnectionState = value;
            RcpUsbConnectionStateHandlers?.Invoke(value);
        } 
    }

    public TimeSpan FrontUsbTimeRemaining
    {
        get => _frontUsbTimeRemaining;
        private set
        {
            if(_frontUsbTimeRemaining == value)
                return;
            _frontUsbTimeRemaining = value;
            FrontUsbRemainingRecordTimeHandlers?.Invoke(value);
        }
    }

    public TimeSpan RearUsbTimeRemaining
    {
        get => _rearUsbTimeRemaining;
        private set
        {
            if(_rearUsbTimeRemaining == value)
                return;
            _rearUsbTimeRemaining = value;
            RearUsbRemainingRecordTimeHandlers?.Invoke(value);
        }
    }

    public TimeSpan RcpUsbTimeRemaining
    {
        get => _rcpUsbTimeRemaining;
        private set
        {
            if(_rcpUsbTimeRemaining == value)
                return;
            _rcpUsbTimeRemaining = value;
            RcpUsbRemainingRecordTimeHandlers?.Invoke(value);
        }
    }

    public ExtronSmp351(CommunicationClient communicationClient, ulong memoryLowKBytes, ulong memoryFullKBytes, string name, int pollTime = 1000) 
        : base(name, communicationClient)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromMilliseconds(pollTime));
        _pollWorker.Restart();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        lock (_verboseModeTimerLock)
        {
            _verboseModeTimer?.Dispose();
            _verboseModeTimer = new Timer(_ => SetVerboseMode(), null, VerboseModeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SetVerboseMode()
    {
        try
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return;
            // This is required so all responses have a prefix.
            // Changing this will result in the device not returning the expected strings below
            CommunicationClient.Send($"{EscapeHeader}3CV\r");
        }
        catch (Exception e)
        {
            LogException(e, "Setting verbose mode failed");
        }
    }

    protected override void DoRecord() => CommunicationClient.Send($"{EscapeHeader}Y1RCDR\r");

    protected override void DoStop() => CommunicationClient.Send($"{EscapeHeader}Y0RCDR\r");
    
    protected override void DoPause() => CommunicationClient.Send($"{EscapeHeader}Y2RCDR\r");
    
    public override void AddMarker() => CommunicationClient.Send($"{EscapeHeader}BRCDR\r");

    private void HandleResponse(string response)
    {
        if (response.StartsWith("Inf*"))
        {
            ProcessInformation(response);
        }
        else if (response.StartsWith("Inf56"))
        {
            ProcessUsbStatus(ExtronSmp351UsbPort.Front, response.Remove(0,6).Trim());
            
        }
        else if (response.StartsWith("Inf57"))
        {
            ProcessUsbStatus(ExtronSmp351UsbPort.Rear, response.Remove(0,6).Trim());
        }
        else if (response.StartsWith("Inf58"))
        {
            ProcessUsbStatus(ExtronSmp351UsbPort.RCP, response.Remove(0,6).Trim());
        }
        else if (response.StartsWith("RcdrN0*"))
        {
            switch (response.Remove(0, 7).Trim())
            {
                case "usbfront":
                    FrontUsbConnectionState = ConnectionState.Disconnected;
                    FrontUsbTimeRemaining = TimeSpan.Zero;
                    break;
                case "usbrear":
                    RearUsbConnectionState = ConnectionState.Disconnected;
                    RearUsbTimeRemaining = TimeSpan.Zero;
                    break;
                case "usbrcp":
                    RcpUsbConnectionState = ConnectionState.Disconnected;
                    RcpUsbTimeRemaining = TimeSpan.Zero;
                    break;
            }
        }
        else if (response.StartsWith("RcdrN1*"))
        {
            switch (response.Remove(0, 7).Trim())
            {
                case "usbfront":
                    FrontUsbConnectionState = ConnectionState.Connected;
                    break;
                case "usbrear":
                    RearUsbConnectionState = ConnectionState.Connected;
                    break;
                case "usbrcp":
                    RcpUsbConnectionState = ConnectionState.Connected;
                    break;
            }
        }
        else if (response.StartsWith("RecStart"))
        {
            TransportState = TransportState.PreparingToRecord;
        }
        else if (response.StartsWith("RcdrY"))
        {
            ProcessRecorderStatus(response);
        }
        else
        {
            return;
        }

        CommunicationState = CommunicationState.Okay;
    }

    private void ProcessInformation(string response)
    {
        var matches = _responseParser.Matches(response.Remove(0, 4));
        if (matches.Count <= RecordTimeField)
        {
            LogWarning("Unexpected information response {Response}", response);
            return;
        }

        ProcessRecordingState(matches[RecordingStateField].Groups[1].Value);
        if (TransportState is TransportState.Recording or TransportState.RecordingPaused)
        {
            TimestampHandlers?.Invoke(matches[RecordTimeField].Groups[1].Value);
        }
    }

    private void ProcessRecorderStatus(string response)
    {
        if (!int.TryParse(response.Remove(0, 5).Trim(), out var state))
        {
            LogWarning("Unexpected recorder status {Response}", response);
            return;
        }

        switch (state)
        {
            case 0:
                TransportState = TransportState.Stopped;
                TimestampHandlers?.Invoke(string.Empty);
                break;
            case 1:
                TransportState = TransportState.Recording;
                break;
            case 2:
                TransportState = TransportState.RecordingPaused;
                break;
        }
    }

    private void ProcessUsbStatus(ExtronSmp351UsbPort port, string status)
    {
        ConnectionState connectionState;
        TimeSpan reaminingTime = TimeSpan.Zero;
        if (status == "N/A")
        {
            connectionState = ConnectionState.Disconnected;
        }
        else
        {
            connectionState = ConnectionState.Connected;
            var parts = status.Split('*');
            var hms = parts.Length > UsbTimeRemainingField ? parts[UsbTimeRemainingField].Split(':') : [];
            if (hms.Length != TimeParts
                || !int.TryParse(hms[0], out var hours)
                || !int.TryParse(hms[1], out var minutes)
                || !int.TryParse(hms[2], out var seconds))
            {
                LogWarning("Unexpected {Port} USB status {Status}", port, status);
                return;
            }

            reaminingTime = new TimeSpan(hours, minutes, seconds);
        }

        switch (port)
        {
            case ExtronSmp351UsbPort.Front:
                FrontUsbConnectionState = connectionState;
                FrontUsbTimeRemaining = reaminingTime;
                break;
            case ExtronSmp351UsbPort.Rear:
                RearUsbConnectionState = connectionState;
                RearUsbTimeRemaining = reaminingTime;
                break;
            case ExtronSmp351UsbPort.RCP:
                RcpUsbConnectionState = connectionState;
                RcpUsbTimeRemaining = reaminingTime;
                break;
        }
    }

    private void ProcessRecordingState(string state)
    {
        TransportState = state switch
        {
            "recording" => TransportState.Recording,
            "paused" => TransportState.RecordingPaused,
            "stopped" => TransportState.Stopped,
            "setup" => TransportState.PreparingToRecord,
            _ => TransportState.Unknown
        };
    }

    private Task Poll( CancellationToken token)
    {
        if(CommunicationClient.ConnectionState == ConnectionState.Connected)
        {
            CommunicationClient.Send("I");
            _counter++;
            if (_counter > 60)
            {
                _counter = 0;
                Thread.Sleep(90);
                CommunicationClient.Send("56I");
                Thread.Sleep(90);
                CommunicationClient.Send("57I");
                Thread.Sleep(90);
                CommunicationClient.Send("58I");   
            }

        }
        return Task.CompletedTask;
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
}