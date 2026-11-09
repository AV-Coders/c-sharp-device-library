using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public partial class ExtronIn18Xx : ExtronSisMatrix
{
    public enum ScreenSaverMode { Black = 1, BlueWithOsdText = 2, UserImage = 3 }

    public enum HdcpNotificationMode { Black = 0, GreenWithOsd = 1, UserImage = 2 }

    public enum AutoSwitchMode { Off = 0, UserPriority = 1, LastConnected = 2 }

    public enum HdcpOutputMode
    {
        DisableAuthentication = 0,
        FollowInput = 1,
        AlwaysEncryptOutput = 2,
        FollowInputContinuousTrials = 3,
        AlwaysEncryptOutputContinuousTrials = 4
    }

    private readonly int _numberOfInputs;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public readonly List<ExtronMatrixOutput> ComposedOutputs = [new("Output 1", 1)];
    public readonly List<ExtronMatrixInput> Inputs = [];
    public List<ExtronMatrixEndpoint> Outputs => ComposedOutputs
        .SelectMany(output => new[] { output.Primary, output.Secondary })
        .ToList();

    private readonly ThreadWorker _pollWorker;
    private const string EscapeHeader = "\x1b";
    private const string SignalDetected = "1";
    private const string HotplugAsserted = "1";
    private const int HdmiOutput = 1;
    private const int TwistedPairOutput = 2;
    private const int MaximumSyncTimeout = 501;
    private const string ModelTag = "Inf01*";
    private const string ModelLabel = "Model";
    private const string ModelIssueKey = "model";
    private const int TieOutput = 1;
    private const string InputHdcpReplyStart = "HdcpI";
    private const string OutputHdcpReplyStart = "HdcpO";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private static readonly ExtronSisCommand VerboseMode = new($"{EscapeHeader}3CV\r", "Vrb");
    private static readonly ExtronSisCommand ModelQuery = new("1I", ModelTag);
    private static readonly ExtronSisCommand SignalPresenceQuery = new($"{EscapeHeader}0LS\r", "In00 ");
    private static readonly ExtronSisQuery[] IdentityQueries =
    [
        new("*Q", "Bld", "Firmware", "firmware-read"),
        new("N", "Pno", "Part Number", "part-number-read"),
        new($"{EscapeHeader}CN\r", "Ipn ", "Unit Name", "unit-name-read"),
        new($"{EscapeHeader}CH\r", "Iph ", "MAC", "mac-read")
    ];
    private static readonly ExtronSisQuery TemperatureQuery = new($"{EscapeHeader}20STAT\r", "20Stat ", "Temperature", "temperature-read");
    private const string Unmuted = "0";
    private const string VideoAndSyncMuted = "2";
    private const string AutoSwitchPriorityTag = "AuswP";
    private const string AutoSwitchTimeoutTag = "AuswT";
    private static readonly ExtronSisSetting ScreenSaver = new(
        new($"{EscapeHeader}M1SSAV\r", "SsavM1*", "Screen Saver", "screen-saver-read"),
        mode => $"{EscapeHeader}M1*{mode}SSAV\r", ExtronSisSetting.Named<ScreenSaverMode>);
    private static readonly ExtronSisSetting SyncTimeout = new(
        new($"{EscapeHeader}T1SSAV\r", "SsavT1*", "Sync Timeout", "sync-timeout-read"),
        seconds => $"{EscapeHeader}T1*{seconds}SSAV\r", ExtronSisSetting.SyncTimeoutText);
    private static readonly ExtronSisSetting HdcpNotification = new(
        new($"{EscapeHeader}N1HDCP\r", "HdcpN1*", "HDCP Notification", "hdcp-notification-read"),
        mode => $"{EscapeHeader}N1*{mode}HDCP\r", ExtronSisSetting.Named<HdcpNotificationMode>);
    private static readonly ExtronSisSetting AutoSwitch = new(
        new($"{EscapeHeader}AUSW\r", "Ausw", "Auto Switch", "auto-switch-read"),
        mode => $"{EscapeHeader}{mode}AUSW\r", ExtronSisSetting.Named<AutoSwitchMode>);
    private static readonly ExtronSisSetting[] VideoMutes =
    [
        new(new("1*B", "Vmt1*", "Video Mute 1A", "video-mute-1a-read"), code => $"1*{code}B", ExtronSisSetting.VideoMuteText),
        new(new("2*B", "Vmt2*", "Video Mute 1B", "video-mute-1b-read"), code => $"2*{code}B", ExtronSisSetting.VideoMuteText),
        new(new("3*B", "Vmt3*", "Video Mute Loop", "video-mute-loop-read"), code => $"3*{code}B", ExtronSisSetting.VideoMuteText)
    ];
    private static readonly ExtronSisSetting[] HdcpModes =
    [
        new(new($"{EscapeHeader}S1HDCP\r", "HdcpS1*", "HDCP Mode 1A", "hdcp-mode-1a-read"),
            mode => $"{EscapeHeader}S1*{mode}HDCP\r", ExtronSisSetting.Named<HdcpOutputMode>),
        new(new($"{EscapeHeader}S2HDCP\r", "HdcpS2*", "HDCP Mode 1B", "hdcp-mode-1b-read"),
            mode => $"{EscapeHeader}S2*{mode}HDCP\r", ExtronSisSetting.Named<HdcpOutputMode>),
        new(new($"{EscapeHeader}S3HDCP\r", "HdcpS3*", "HDCP Mode Loop", "hdcp-mode-loop-read"),
            mode => $"{EscapeHeader}S3*{mode}HDCP\r", ExtronSisSetting.Named<HdcpOutputMode>)
    ];
    private static readonly ExtronSisSetting[] Settings = [ScreenSaver, SyncTimeout, HdcpNotification, AutoSwitch, ..VideoMutes, ..HdcpModes];
    private readonly SettingsTracker _settings;

    public ExtronIn18Xx(CommunicationClient communicationClient, int numberOfInputs, string name)
        : this(communicationClient, numberOfInputs, name, null)
    {
    }

    internal ExtronIn18Xx(CommunicationClient communicationClient, int numberOfInputs, string name, TimeProvider? timeProvider)
        : base(1, communicationClient, name, timeProvider)
    {
        _settings = new SettingsTracker(this);
        _numberOfInputs = numberOfInputs;
        ComposedOutputs[0].Primary.SetName("Output 1A");
        ComposedOutputs[0].Secondary.SetName("Output 1B");
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, TimeProvider);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        communicationClient.ResponseHandlers += HandleResponse;
        HandleConnectionState(communicationClient.ConnectionState);
    }

    public ScreenSaverMode? DesiredScreenSaver => (ScreenSaverMode?)_settings.DesiredNumber(ScreenSaver);
    public int? DesiredSyncTimeout => _settings.DesiredNumber(SyncTimeout);
    public HdcpNotificationMode? DesiredHdcpNotification => (HdcpNotificationMode?)_settings.DesiredNumber(HdcpNotification);
    public AutoSwitchMode? DesiredAutoSwitch => (AutoSwitchMode?)_settings.DesiredNumber(AutoSwitch);
    public HdcpOutputMode? DesiredHdcpMode => (HdcpOutputMode?)_settings.DesiredNumber(HdcpModes[0]);

    public MuteState DesiredVideoMute => _settings.Desired(VideoMutes[0]) switch
    {
        VideoAndSyncMuted => MuteState.On,
        Unmuted => MuteState.Off,
        _ => MuteState.Unknown
    };

    [GeneratedRegex(@"^(?:Inf01\*)?(IN180([468])(?:\s.*)?)$")]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"^Vmt([^*]*)$")]
    private static partial Regex AllOutputsVideoMuteRegex();

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                ProcessResponse(line);
        }
    }

    private void ProcessResponse(string line)
    {
        using var reply = TakeReply(line);
        using var scope = PushProperties();
        if (ModelRegex().Match(line) is { Success: true } model)
            HandleModel(model.Groups[1].Value, int.Parse(model.Groups[2].Value));
        else if (line.StartsWith(ModelTag))
            HandleUnknownModel(line);
        else if (IdentityQueries.FirstOrDefault(query => query.IsAnsweredBy(line)) is { } identity)
            ShowRead(identity, line);
        else if (TemperatureQuery.IsAnsweredBy(line))
            ShowRead(TemperatureQuery, line);
        else if (line.StartsWith("IN00", StringComparison.OrdinalIgnoreCase))
            HandleSignalPresence(line[4..]);
        else if (line.StartsWith("HdcpI"))
            HandleInputHdcp(line[5..]);
        else if (line.StartsWith("HdcpO"))
            HandleOutputHdcp(line[5..]);
        else if (line.StartsWith("HplgO"))
            HandleOutputHotplug(line[5..]);
        else if (line.StartsWith("VnamI"))
            HandleInputName(line[5..]);
        else if (line.StartsWith(AutoSwitchPriorityTag) || line.StartsWith(AutoSwitchTimeoutTag))
            LogDebug("Ignoring {Line}: only the auto switch mode is tracked", line);
        else if (Settings.FirstOrDefault(candidate => candidate.Read.IsAnsweredBy(line)) is { } setting)
            _settings.Handle(setting, line, line[setting.Read.ReplyTag.Length..].Trim());
        else if (AllOutputsVideoMuteRegex().Match(line) is { Success: true } allOutputsMute)
            foreach (var videoMute in VideoMutes)
                _settings.Handle(videoMute, line, allOutputsMute.Groups[1].Value);
        else if (IsError(line))
            HandleError(line);
    }

    private void HandleModel(string model, int inputCount)
    {
        SetDetail(ModelLabel, model);
        ResolveIssue(ModelIssueKey);
        SetInputCount(inputCount);
        QueryStatus();
        Read(IdentityQueries);
        QuerySettings();
    }

    private void HandleUnknownModel(string line)
    {
        SetDetail(ModelLabel, line[ModelTag.Length..]);
        RaiseOngoingIssue(ModelIssueKey, $"Unable to read the input count from the model {line}");
    }

    private void SetInputCount(int count)
    {
        if (count != _numberOfInputs)
            LogWarning("The model reply reports {DiscoveredInputs} inputs but the driver was constructed with {ConfiguredInputs}; routing uses the discovered count", count, _numberOfInputs);
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

    private void HandleSignalPresence(string statuses)
    {
        var values = statuses.Split('*', StringSplitOptions.TrimEntries);
        if (values.Length != Inputs.Count)
        {
            LogWarning("Ignoring the signal presence {Statuses} as it doesn't list the {InputCount} inputs", statuses, Inputs.Count);
            return;
        }
        for (var index = 0; index < values.Length; index++)
            Inputs[index].SetInputStatus(values[index] == SignalDetected ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleInputHdcp(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || number < 1 || number > Inputs.Count)
        {
            LogWarning("Ignoring the input HDCP status {Status} as it doesn't name one of the {InputCount} inputs", status, Inputs.Count);
            return;
        }
        Inputs[number - 1].SetInputHdcpStatus(ExtronSisHdcp.DecodeInput(value).Hdcp);
    }

    private void HandleOutputHdcp(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || OutputConnector(number) is not { } output)
            return;
        var (connection, hdcp) = ExtronSisHdcp.DecodeOutput(value);
        output.SetOutputStatus(connection);
        output.SetOutputHdcpStatus(hdcp);
    }

    private void HandleOutputHotplug(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || OutputConnector(number) is not { } output)
            return;
        output.SetOutputStatus(value == HotplugAsserted ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleInputName(string text)
    {
        if (!TryParsePortValue(text, out var number, out var name) || number < 1 || number > Inputs.Count)
            return;
        Inputs[number - 1].SetName(name);
    }

    private ExtronMatrixEndpoint? OutputConnector(int number) => number switch
    {
        HdmiOutput => ComposedOutputs[0].Primary,
        TwistedPairOutput => ComposedOutputs[0].Secondary,
        _ => null
    };

    private static bool TryParsePortValue(string text, out int port, out string value)
    {
        var parts = text.Split('*', 2, StringSplitOptions.TrimEntries);
        port = 0;
        value = parts.Length == 2 ? parts[1] : string.Empty;
        return parts.Length == 2 && int.TryParse(parts[0], out port);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        ForgetCommands();
        if (connectionState != ConnectionState.Connected)
        {
            ClearStatus();
            return;
        }
        SendSpaced(async () =>
        {
            await Task.Delay(ConnectStepGap, TimeProvider);
            Send(VerboseMode);
            await Task.Delay(ConnectStepGap, TimeProvider);
            Send(ModelQuery);
        }, "The connection sequence failed");
    }

    private void ClearStatus()
    {
        foreach (var input in Inputs)
        {
            input.SetInputStatus(ConnectionState.Unknown);
            input.SetInputHdcpStatus(HdcpStatus.Unknown);
        }
        foreach (var output in Outputs)
        {
            output.SetOutputStatus(ConnectionState.Unknown);
            output.SetOutputHdcpStatus(HdcpStatus.Unknown);
        }
    }

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        ReportMissedReplies();
        if (Inputs.Count == 0)
            Send(ModelQuery);
        QueryStatus();
        QuerySettings();
        ReadUnlessNotSupported(TemperatureQuery);
        ReadAgainIfUnanswered(IdentityQueries);
        return Task.CompletedTask;
    }

    private void QuerySettings()
    {
        foreach (var setting in Settings)
            ReadUnlessNotSupported(setting.Read);
    }

    private void QueryStatus()
    {
        Send(SignalPresenceQuery);
        for (var number = 1; number <= Inputs.Count; number++)
            Send(new ExtronSisCommand($"{EscapeHeader}I{number}HDCP\r", InputHdcpReplyStart));
        Send(new ExtronSisCommand($"{EscapeHeader}O{HdmiOutput}HDCP\r", OutputHdcpReplyStart));
        Send(new ExtronSisCommand($"{EscapeHeader}O{TwistedPairOutput}HDCP\r", OutputHdcpReplyStart));
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}*{TieOutput}!", new ExtronSisTie(input, TieOutput, ExtronSisTie.VideoAndAudio)));
            AddEvent(EventType.Input, $"Switched output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching output {output} to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteAV"))
                LogWarning("Not switching output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, NumberOfInputs);
        }
    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override int NumberOfOutputs => 1;
    public override int NumberOfInputs => Inputs.Count == 0 ? _numberOfInputs : Inputs.Count;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}*{TieOutput}%", new ExtronSisTie(input, TieOutput, ExtronSisTie.Video)));
            AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching video output {output} to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteVideo"))
                LogWarning("Not switching video output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, NumberOfInputs);
        }
    }

    public override bool SupportsAudioBreakaway => false;

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}*{TieOutput}$", new ExtronSisTie(input, TieOutput, ExtronSisTie.Audio)));
            AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching audio output {output} to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteAudio"))
                LogWarning("Not switching audio output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, NumberOfInputs);
        }
    }

    public void SetScreenSaver(ScreenSaverMode? mode) => _settings.SetDesired(ScreenSaver, mode is { } value ? $"{(int)value}" : null);

    public void SetSyncTimeout(int? seconds)
    {
        if (seconds > MaximumSyncTimeout)
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than {MaximumSyncTimeout} seconds");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout can't be longer than {MaximumSyncTimeout} seconds", MaximumSyncTimeout);
            return;
        }
        _settings.SetDesired(SyncTimeout, seconds?.ToString());
        if (seconds != null)
            AddEvent(EventType.VideoMute, $"Set sync timeout to {seconds} seconds");
    }

    public void SetHdcpNotification(HdcpNotificationMode? mode) =>
        _settings.SetDesired(HdcpNotification, mode is { } value ? $"{(int)value}" : null);

    public void SetAutoSwitch(AutoSwitchMode? mode) => _settings.SetDesired(AutoSwitch, mode is { } value ? $"{(int)value}" : null);

    public void SetHdcpMode(HdcpOutputMode? mode)
    {
        foreach (var hdcpMode in HdcpModes)
            _settings.SetDesired(hdcpMode, mode is { } value ? $"{(int)value}" : null);
    }

    public void SetVideoMute(MuteState state)
    {
        var code = state switch
        {
            MuteState.On => VideoAndSyncMuted,
            MuteState.Off => Unmuted,
            _ => null
        };
        foreach (var videoMute in VideoMutes)
            _settings.StoreDesired(videoMute, code);
        if (code == null)
            return;
        SendSpaced(async () =>
        {
            _settings.Send(VideoMutes[0], code);
            await Task.Delay(VideoMuteStepGap, TimeProvider);
            _settings.Send(VideoMutes[1], code);
            await Task.Delay(VideoMuteStepGap, TimeProvider);
            _settings.Send(VideoMutes[2], code);
        }, "The video mute sends failed");
        AddEvent(EventType.VideoMute, $"Switched video mute to {state}");
    }
}
