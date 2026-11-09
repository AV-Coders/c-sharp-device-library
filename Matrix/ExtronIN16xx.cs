using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public partial class ExtronIn16Xx : ExtronSisMatrix
{
    public enum ScreenSaverMode { CustomColour = 0, Black = 1, Blue = 2 }

    public enum HdcpNotificationMode { Black = 0, Green = 1 }

    public enum AutoSwitchMode { Off = 0, HighestActiveInput = 1, LowestActiveInput = 2 }

    public enum HdcpOutputMode
    {
        FollowInput = 0,
        AlwaysEncryptOutput = 1,
        FollowInputContinuousDviTrials = 2,
        AlwaysEncryptOutputContinuousDviTrials = 3,
        DisableAuthentication = 4
    }

    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public readonly List<ExtronMatrixInput> Inputs = [];
    public readonly List<ExtronMatrixEndpoint> Outputs =
    [
        new("Output 1A", 1, AVEndpointType.Decoder),
        new("Output 1B", 1, AVEndpointType.Decoder),
        new("Output 1C", 1, AVEndpointType.Decoder)
    ];

    private const string EscapeHeader = "\x1b";
    private const string SignalDetected = "1";
    private const string NotDetected = "0";
    private const string DetectedWithHdcp = "1";
    private const string DetectedWithoutHdcp = "2";
    private const string Reconfigured = "Reconfig";
    private const string UntiedAddress = "";
    private const int FirstHdmiInput = 3;
    private const int MaximumSyncTimeout = 501;
    private const int OsdOff = 0;
    private const int OsdNeverTimesOut = 501;
    private const string ModelLabel = "Model";
    private const string Unmuted = "0";
    private const string VideoAndSyncMuted = "2";
    private const string InputHdcpReplyStart = "HdcpI";
    private const string OutputHdcpReplyStart = "HdcpO";
    private const string InputNameReplyStart = "Nmi";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private static readonly ExtronSisCommand VerboseMode = new($"{EscapeHeader}3CV\r", "Vrb");
    private static readonly ExtronSisCommand VerboseModeQuery = new($"{EscapeHeader}CV\r", "Vrb");
    private static readonly ExtronSisCommand GeneralInformationQuery = new("I", "Vid");
    private static readonly ExtronSisCommand SignalPresenceQuery = new($"{EscapeHeader}0LS\r", "In00");
    private static readonly ExtronSisCommand TieQuery = new("!", new ExtronSisTie(null, ExtronSisTie.AllOutputs, ExtronSisTie.VideoAndAudio));
    private static readonly ExtronSisQuery[] IdentityQueries =
    [
        new("*Q", "Bld", "Firmware", "firmware-read"),
        new("N", "Pno", "Part Number", "part-number-read"),
        new($"{EscapeHeader}CN\r", "Ipn", "Unit Name", "unit-name-read"),
        new($"{EscapeHeader}CH\r", "Iph", "MAC", "mac-read")
    ];
    private static readonly ExtronSisQuery TemperatureQuery = new($"{EscapeHeader}20STAT\r", "20Stat", "Temperature", "temperature-read");
    private static readonly ExtronSisSetting ScreenSaver = new(
        new($"{EscapeHeader}MSSAV\r", "SsavM", "Screen Saver", "screen-saver-read"),
        mode => $"{EscapeHeader}M{mode}SSAV\r", ExtronSisSetting.Named<ScreenSaverMode>);
    private static readonly ExtronSisSetting ScreenSaverColour = new(
        new($"{EscapeHeader}CSSAV\r", "SsavC", "Screen Saver Colour", "screen-saver-colour-read"),
        colour => $"{EscapeHeader}C{colour}SSAV\r", ColourText);
    private static readonly ExtronSisSetting SyncTimeout = new(
        new($"{EscapeHeader}TSSAV\r", "SsavT", "Sync Timeout", "sync-timeout-read"),
        seconds => $"{EscapeHeader}T{seconds}SSAV\r", ExtronSisSetting.SyncTimeoutText);
    private static readonly ExtronSisSetting HdcpNotification = new(
        new($"{EscapeHeader}NHDCP\r", "HdcpN", "HDCP Notification", "hdcp-notification-read"),
        mode => $"{EscapeHeader}N{mode}HDCP\r", ExtronSisSetting.Named<HdcpNotificationMode>);
    private static readonly ExtronSisSetting OsdDuration = new(
        new($"{EscapeHeader}MDUR\r", "Mdur", "OSD Duration", "osd-duration-read"),
        seconds => $"{EscapeHeader}{seconds}MDUR\r", OsdDurationText);
    private static readonly ExtronSisSetting AutoSwitch = new(
        new($"{EscapeHeader}AUSW\r", "Ausw", "Auto Switch", "auto-switch-read"),
        mode => $"{EscapeHeader}{mode}AUSW\r", ExtronSisSetting.Named<AutoSwitchMode>);
    private static readonly ExtronSisSetting[] VideoMutes =
    [
        new(new("1*B", "Vmt1*", "Video Mute 1A", "video-mute-1a-read"), code => $"1*{code}B", ExtronSisSetting.VideoMuteText),
        new(new("2*B", "Vmt2*", "Video Mute 1B", "video-mute-1b-read"), code => $"2*{code}B", ExtronSisSetting.VideoMuteText),
        new(new("3*B", "Vmt3*", "Video Mute 1C", "video-mute-1c-read"), code => $"3*{code}B", ExtronSisSetting.VideoMuteText)
    ];
    private static readonly ExtronSisSetting HdcpMode = new(
        new($"{EscapeHeader}SHDCP\r", "HdcpS", "HDCP Mode", "hdcp-mode-read"),
        mode => $"{EscapeHeader}S{mode}HDCP\r", ExtronSisSetting.Named<HdcpOutputMode>);
    private static readonly ExtronSisSetting[] Settings =
        [ScreenSaver, ScreenSaverColour, SyncTimeout, HdcpNotification, OsdDuration, AutoSwitch, ..VideoMutes, HdcpMode];
    private readonly int _numberOfInputs;
    private readonly ThreadWorker _pollWorker;
    private readonly SettingsTracker _settings;

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name)
        : this(communicationClient, numberOfInputs, name, TimeProvider.System)
    {
    }

    internal ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name, TimeProvider timeProvider)
        : base(1, communicationClient, name, timeProvider)
    {
        _settings = new SettingsTracker(this);
        _numberOfInputs = numberOfInputs;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, TimeProvider);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        communicationClient.ResponseHandlers += HandleResponse;
    }

    public ScreenSaverMode? DesiredScreenSaver => (ScreenSaverMode?)_settings.DesiredNumber(ScreenSaver);
    public string? DesiredScreenSaverColour => _settings.Desired(ScreenSaverColour);
    public int? DesiredSyncTimeout => _settings.DesiredNumber(SyncTimeout);
    public HdcpNotificationMode? DesiredHdcpNotification => (HdcpNotificationMode?)_settings.DesiredNumber(HdcpNotification);
    public int? DesiredOsdDuration => _settings.DesiredNumber(OsdDuration);
    public AutoSwitchMode? DesiredAutoSwitch => (AutoSwitchMode?)_settings.DesiredNumber(AutoSwitch);
    public HdcpOutputMode? DesiredHdcpMode => (HdcpOutputMode?)_settings.DesiredNumber(HdcpMode);

    public MuteState DesiredVideoMute => _settings.Desired(VideoMutes[0]) switch
    {
        VideoAndSyncMuted => MuteState.On,
        Unmuted => MuteState.Off,
        _ => MuteState.Unknown
    };

    [GeneratedRegex(@"^In00 ([01](?:\*[01])*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SignalPresenceRegex();

    [GeneratedRegex(@"^In(\d+) (?:All|RGB)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTieRegex();

    [GeneratedRegex(@"^HdcpI(\d+)\*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex InputHdcpRegex();

    [GeneratedRegex(@"^HdcpO(\d+)\*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex OutputHdcpRegex();

    [GeneratedRegex(@"^HplgO(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HotplugRegex();

    [GeneratedRegex(@"^Nmi(\d+),(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex InputNameRegex();

    [GeneratedRegex(@"^(?:\(c\)|©) Copyright \d{4}, Extron Electronics, ([^,]+), V", RegexOptions.IgnoreCase)]
    private static partial Regex CopyrightRegex();

    [GeneratedRegex(@"^Vmt([^*]*)$")]
    private static partial Regex AllOutputsVideoMuteRegex();

    [GeneratedRegex("^[0-9A-Fa-f]{6}$")]
    private static partial Regex ColourRegex();

    private static string? ColourText(string value) => ColourRegex().IsMatch(value) ? value.ToUpperInvariant() : null;

    private static string? OsdDurationText(string value)
    {
        if (!int.TryParse(value, out var seconds) || seconds is < OsdOff or > OsdNeverTimesOut)
            return null;
        return seconds switch
        {
            OsdOff => "Off",
            OsdNeverTimesOut => "Never times out",
            _ => $"{seconds} s"
        };
    }

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                ProcessResponse(line.Trim());
        }
    }

    private void ProcessResponse(string line)
    {
        using var reply = TakeReply(line);
        if (SignalPresenceRegex().Match(line) is { Success: true } presence)
            HandleSignalPresence(presence.Groups[1].Value);
        else if (VideoTieRegex().Match(line) is { Success: true } tie)
            HandleVideoTie(tie.Groups[1].Value);
        else if (InputHdcpRegex().Match(line) is { Success: true } inputHdcp)
            HandleInputHdcp(line, int.Parse(inputHdcp.Groups[1].Value), inputHdcp.Groups[2].Value);
        else if (OutputHdcpRegex().Match(line) is { Success: true } outputHdcp)
            HandleOutputHdcp(line, int.Parse(outputHdcp.Groups[1].Value), outputHdcp.Groups[2].Value);
        else if (HotplugRegex().Match(line) is { Success: true } hotplug)
            Send(new ExtronSisCommand($"{EscapeHeader}O{hotplug.Groups[1].Value}HDCP\r", OutputHdcpReplyStart));
        else if (InputNameRegex().Match(line) is { Success: true } inputName)
            HandleInputName(line, int.Parse(inputName.Groups[1].Value), inputName.Groups[2].Value);
        else if (line == Reconfigured)
            Send(SignalPresenceQuery);
        else if (CopyrightRegex().Match(line) is { Success: true } copyright)
            SetDetail(ModelLabel, copyright.Groups[1].Value);
        else if (IdentityQueries.FirstOrDefault(query => query.IsAnsweredBy(line)) is { } identity)
            ShowRead(identity, line);
        else if (TemperatureQuery.IsAnsweredBy(line))
            ShowRead(TemperatureQuery, line);
        else if (Settings.FirstOrDefault(candidate => candidate.Read.IsAnsweredBy(line)) is { } setting)
            _settings.Handle(setting, line, line[setting.Read.ReplyTag.Length..].Trim());
        else if (AllOutputsVideoMuteRegex().Match(line) is { Success: true } allOutputsMute)
            foreach (var videoMute in VideoMutes)
                _settings.Handle(videoMute, line, allOutputsMute.Groups[1].Value);
        else if (IsError(line))
            HandleError(line);
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
            var input = new ExtronMatrixInput($"Input {number}", number);
            input.SetStreamAddress(number.ToString());
            Inputs.Add(input);
        }
        EndpointsChangedHandlers?.Invoke();
        QueryInputNames();
    }

    private void HandleVideoTie(string input)
    {
        var address = int.Parse(input).ToString();
        Outputs.ForEach(output => output.SetStreamAddress(address));
    }

    private void HandleInputHdcp(string line, int number, string value)
    {
        if (number < 1 || number > Inputs.Count)
        {
            LogWarning("Ignoring the input HDCP status {Line} as it doesn't name one of the {InputCount} inputs", line, Inputs.Count);
            return;
        }
        Inputs[number - 1].SetInputHdcpStatus(value switch
        {
            DetectedWithHdcp => HdcpStatus.Active,
            DetectedWithoutHdcp => HdcpStatus.NotSupported,
            _ => HdcpStatus.Unknown
        });
    }

    private void HandleOutputHdcp(string line, int connector, string value)
    {
        if (connector < 1 || connector > Outputs.Count)
        {
            LogWarning("Ignoring the output HDCP status {Line} as it doesn't name one of the {ConnectorCount} output connectors", line, Outputs.Count);
            return;
        }
        var (connection, hdcp) = value switch
        {
            NotDetected => (ConnectionState.Disconnected, HdcpStatus.Unknown),
            DetectedWithHdcp => (ConnectionState.Connected, HdcpStatus.Available),
            DetectedWithoutHdcp => (ConnectionState.Connected, HdcpStatus.NotSupported),
            _ => (ConnectionState.Unknown, HdcpStatus.Unknown)
        };
        Outputs[connector - 1].SetOutputStatus(connection);
        Outputs[connector - 1].SetOutputHdcpStatus(hdcp);
    }

    private void HandleInputName(string line, int number, string name)
    {
        if (number < 1 || number > Inputs.Count)
        {
            LogWarning("Ignoring the input name {Line} as it doesn't name one of the {InputCount} inputs", line, Inputs.Count);
            return;
        }
        Inputs[number - 1].SetName(name);
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
            Send(GeneralInformationQuery);
            QueryStatus();
            QueryInputNames();
            QuerySettings();
            ReadUnlessNotSupported(TemperatureQuery);
            Read(IdentityQueries);
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
            output.SetStreamAddress(UntiedAddress);
        }
    }

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        ReportMissedReplies();
        Send(VerboseModeQuery);
        QueryStatus();
        QuerySettings();
        ReadUnlessNotSupported(TemperatureQuery);
        ReadAgainIfUnanswered(IdentityQueries);
        return Task.CompletedTask;
    }

    private void QueryStatus()
    {
        Send(SignalPresenceQuery);
        Send(TieQuery);
        for (var number = FirstHdmiInput; number <= _numberOfInputs; number++)
            Send(new ExtronSisCommand($"{EscapeHeader}I{number}HDCP\r", InputHdcpReplyStart));
        for (var connector = 1; connector <= Outputs.Count; connector++)
            Send(new ExtronSisCommand($"{EscapeHeader}O{connector}HDCP\r", OutputHdcpReplyStart));
    }

    private void QueryInputNames()
    {
        for (var number = 1; number <= Inputs.Count; number++)
            Send(new ExtronSisCommand($"{EscapeHeader}{number}NI\r", InputNameReplyStart));
    }

    private void QuerySettings()
    {
        foreach (var setting in Settings)
            ReadUnlessNotSupported(setting.Read);
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}!", new ExtronSisTie(input, ExtronSisTie.AllOutputs, ExtronSisTie.VideoAndAudio)));
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
            Send(new ExtronSisCommand($"{input}%", new ExtronSisTie(input, ExtronSisTie.AllOutputs, ExtronSisTie.Rgb)));
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
            Send(new ExtronSisCommand($"{input}$", new ExtronSisTie(input, ExtronSisTie.AllOutputs, ExtronSisTie.Audio)));
            AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching audio output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteAudio"))
                LogWarning("Not switching audio output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public void SetScreenSaver(ScreenSaverMode? mode) => _settings.SetDesired(ScreenSaver, mode is { } value ? $"{(int)value}" : null);

    public void SetScreenSaverColour(string? rgbHex)
    {
        if (rgbHex != null && !ColourRegex().IsMatch(rgbHex))
        {
            AddEvent(EventType.Error, $"The screen saver colour must be six hex digits (RRGGBB), it's {rgbHex}");
            using (PushProperties("SetScreenSaverColour"))
                LogWarning("The screen saver colour must be six hex digits (RRGGBB), it's {Colour}", rgbHex);
            return;
        }
        _settings.SetDesired(ScreenSaverColour, rgbHex?.ToUpperInvariant());
    }

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

    public void SetOsdDuration(int? seconds)
    {
        if (seconds is < OsdOff or > OsdNeverTimesOut)
        {
            AddEvent(EventType.Error, $"The OSD duration must be between {OsdOff} and {OsdNeverTimesOut} seconds, it's {seconds}");
            using (PushProperties("SetOsdDuration"))
                LogWarning("The OSD duration must be between {OsdOff} and {OsdNeverTimesOut} seconds, it's {Seconds}", OsdOff, OsdNeverTimesOut, seconds);
            return;
        }
        _settings.SetDesired(OsdDuration, seconds?.ToString());
    }

    public void SetAutoSwitch(AutoSwitchMode? mode) => _settings.SetDesired(AutoSwitch, mode is { } value ? $"{(int)value}" : null);

    public void SetHdcpMode(HdcpOutputMode? mode) => _settings.SetDesired(HdcpMode, mode is { } value ? $"{(int)value}" : null);

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
