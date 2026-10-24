using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public enum MagewellAudioState
{
    Unknown = -1,
    AlwaysOn = 0,
    AlwaysOff = 1,
    AudioFollowVideo = 2
}

public enum MagewellSwitchEffect
{
    Cut = 0,
    Fade = 1
}

public record MagewellScene(string Uuid, string Name);

public record MagewellStreamDestination(long Id, string Name, bool IsConfigured);

public class MagewellDirectorAudioChannel : VolumeControl
{
    private const int MinDb = -40;
    private const int MaxDb = 10;
    private const double PercentPerDb = 100.0 / (MaxDb - MinDb);

    public readonly int AudioType;
    public event EventHandler<MagewellAudioState>? AudioStateChanged;

    private readonly RestComms _restClient;
    private MagewellAudioState _audioState = MagewellAudioState.Unknown;
    private MagewellAudioState _unmutedState = MagewellAudioState.Unknown;

    internal MagewellDirectorAudioChannel(string name, VolumeType type, int audioType, RestComms restClient)
        : base(name, type)
    {
        AudioType = audioType;
        _restClient = restClient;
    }

    public bool SupportsFollowVideo { get; private set; }

    public MagewellAudioState AudioState
    {
        get => _audioState;
        private set
        {
            if (_audioState == value)
                return;
            _audioState = value;
            if (value != MagewellAudioState.AlwaysOff)
                _unmutedState = value;
            MuteState = value == MagewellAudioState.AlwaysOff ? MuteState.On : MuteState.Off;
            AddEvent(EventType.Volume, $"Audio state: {value}");
            AudioStateChanged?.Invoke(this, value);
        }
    }

    public override void SetLevel(int percentage) =>
        SendLevel(MinDb + (int)Math.Round(percentage / PercentPerDb, MidpointRounding.AwayFromZero));

    public override void LevelUp(int amount) =>
        SendLevel(MinDb + (int)Math.Ceiling((Volume + amount) / PercentPerDb));

    public override void LevelDown(int amount) =>
        SendLevel(MinDb + (int)Math.Floor((Volume - amount) / PercentPerDb));

    public override void SetAudioMute(MuteState state) =>
        SetAudioState(state == MuteState.On ? MagewellAudioState.AlwaysOff : UnmutedState());

    public override void ToggleAudioMute() =>
        SetAudioMute(MuteState == MuteState.On ? MuteState.Off : MuteState.On);

    public void SetAudioState(MagewellAudioState state)
    {
        _ = _restClient.Get(new Uri($"/V1.0/volume/setState?type={AudioType}&state={(int)state}", UriKind.Relative));
        AudioState = state;
    }

    internal void Update(int levelDb, MagewellAudioState state, bool supportsFollowVideo)
    {
        SupportsFollowVideo = supportsFollowVideo;
        Volume = PercentFromDb(levelDb);
        AudioState = state;
    }

    private MagewellAudioState UnmutedState() => _unmutedState switch
    {
        MagewellAudioState.Unknown when SupportsFollowVideo => MagewellAudioState.AudioFollowVideo,
        MagewellAudioState.Unknown => MagewellAudioState.AlwaysOn,
        _ => _unmutedState
    };

    private void SendLevel(int levelDb)
    {
        var clampedDb = Math.Clamp(levelDb, MinDb, MaxDb);
        _ = _restClient.Get(new Uri($"/V1.0/volume/setVolume?type={AudioType}&volume={clampedDb}", UriKind.Relative));
        Volume = PercentFromDb(clampedDb);
    }

    private static int PercentFromDb(int levelDb) => (int)Math.Round((levelDb - MinDb) * PercentPerDb);
}

public class MagewellDirectorPlus : Recorder
{
    public const ushort DefaultPort = 80;
    public static readonly TimeSpan RecordingSpaceThreshold = TimeSpan.FromHours(1);

    private const string StateMachinePath = "/mwapi/statemachine";
    private const string DeviceInfoPath = "/mwapi/device/get-device-info";
    private const string SummaryPath = "/mwapi/device/get-summary-info";
    private const string MixerPath = "/V1.0/volume/getConfig";
    private const string SceneListPath = "/V1.0/scene/getSceneList";
    private const string StreamListPath = "/V1.0/stream/getStreamServersList";
    private const string JsonContentType = "application/json";
    private const string EmptyRequest = "{}";
    private const int GetterSuccess = 0;
    private const int QuickSwitchOnMode = 0;
    private const int PollFailuresBeforeIssue = 3;
    private const int RecordingStatePollsBeforeIssue = 5;
    private const double MegabytesPerGigabyte = 1000;

    private const int ProgramAudioType = 0x0003;
    private const int MonitorAudioType = 0x0001;
    private const int Mic1AudioType = 0x0200;
    private const int Mic2AudioType = 0x0202;
    private const int Hdmi1AudioType = 0x0700;
    private const int Hdmi2AudioType = 0x0800;
    private const int Hdmi3AudioType = 0x0802;
    private const int Hdmi4AudioType = 0x0803;
    private const int BgmAudioType = 0x0603;

    private const string PollIssueKey = "unanswered-poll";
    private const string CommandIssueKey = "command-refused";
    private const string RecordingStateIssueKey = "recording-state";
    private const string RecordingErrorIssueKey = "recording-error";
    private const string RecordingSpaceIssueKey = "recording-space";
    private const string StreamingErrorIssueKey = "streaming-error";
    private const string TemperatureIssueKey = "temperature";
    private const string FanIssueKey = "fan";
    private const string CpuIssueKey = "cpu";
    private const string MemoryIssueKey = "memory";
    private const string FirmwareUpdateIssueKey = "firmware-update";

    private static readonly TimeSpan StatePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MixerPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(30);
    private static readonly Uri StateMachineUri = new(StateMachinePath, UriKind.Relative);
    private static readonly Uri DeviceInfoUri = new(DeviceInfoPath, UriKind.Relative);
    private static readonly Uri SummaryUri = new(SummaryPath, UriKind.Relative);
    private static readonly Uri MixerUri = new(MixerPath, UriKind.Relative);
    private static readonly Uri SceneListUri = new(SceneListPath, UriKind.Relative);
    private static readonly Uri StreamListUri = new(StreamListPath, UriKind.Relative);

    public readonly MagewellDirectorAudioChannel ProgramAudio;
    public readonly MagewellDirectorAudioChannel MonitorAudio;
    public readonly MagewellDirectorAudioChannel Mic1Audio;
    public readonly MagewellDirectorAudioChannel Mic2Audio;
    public readonly MagewellDirectorAudioChannel Hdmi1Audio;
    public readonly MagewellDirectorAudioChannel Hdmi2Audio;
    public readonly MagewellDirectorAudioChannel Hdmi3Audio;
    public readonly MagewellDirectorAudioChannel Hdmi4Audio;
    public readonly MagewellDirectorAudioChannel BgmAudio;
    public readonly IReadOnlyList<MagewellDirectorAudioChannel> AudioChannels;

    public event EventHandler<IReadOnlyList<MagewellScene>>? ScenesChanged;
    public event EventHandler<string>? ProgramSceneChanged;
    public event EventHandler<string>? PreviewSceneChanged;
    public event EventHandler<PowerState>? QuickSwitchStateChanged;
    public event EventHandler<PowerState>? FadeToBlackStateChanged;
    public event EventHandler<PowerState>? FreezeStateChanged;
    public event EventHandler<IReadOnlyList<MagewellStreamDestination>>? StreamDestinationsChanged;
    public event EventHandler<PowerState>? StreamingStateChanged;
    public event EventHandler<TimeSpan>? RecordingTimeChanged;
    public event EventHandler<TimeSpan>? RecordingTimeRemainingChanged;

    private readonly RestComms _restClient;
    private readonly ThreadWorker _stateWorker;
    private readonly ThreadWorker _mixerWorker;
    private readonly ThreadWorker _deviceWorker;
    private IReadOnlyList<MagewellScene> _scenes = [];
    private string _programSceneUuid = string.Empty;
    private string _previewSceneUuid = string.Empty;
    private PowerState _quickSwitchState = PowerState.Unknown;
    private PowerState _fadeToBlackState = PowerState.Unknown;
    private PowerState _freezeState = PowerState.Unknown;
    private IReadOnlyList<MagewellStreamDestination> _streamDestinations = [];
    private PowerState _streamingState = PowerState.Unknown;
    private TimeSpan _recordingTime = TimeSpan.Zero;
    private TimeSpan _recordingTimeRemaining = TimeSpan.Zero;

    public MagewellDirectorPlus(string name, RestComms restClient) : base(name, restClient)
    {
        _restClient = restClient;
        _restClient.HttpResponseHandlers += HandleResponse;
        _restClient.ConnectionStateHandlers += HandleConnectionState;

        ProgramAudio = new MagewellDirectorAudioChannel("Program", VolumeType.Speaker, ProgramAudioType, restClient);
        MonitorAudio = new MagewellDirectorAudioChannel("Monitor", VolumeType.Speaker, MonitorAudioType, restClient);
        Mic1Audio = new MagewellDirectorAudioChannel("Mic 1", VolumeType.Microphone, Mic1AudioType, restClient);
        Mic2Audio = new MagewellDirectorAudioChannel("Mic 2", VolumeType.Microphone, Mic2AudioType, restClient);
        Hdmi1Audio = new MagewellDirectorAudioChannel("HDMI 1", VolumeType.LineIn, Hdmi1AudioType, restClient);
        Hdmi2Audio = new MagewellDirectorAudioChannel("HDMI 2", VolumeType.LineIn, Hdmi2AudioType, restClient);
        Hdmi3Audio = new MagewellDirectorAudioChannel("HDMI 3", VolumeType.LineIn, Hdmi3AudioType, restClient);
        Hdmi4Audio = new MagewellDirectorAudioChannel("HDMI 4", VolumeType.LineIn, Hdmi4AudioType, restClient);
        BgmAudio = new MagewellDirectorAudioChannel("BGM", VolumeType.LineIn, BgmAudioType, restClient);
        AudioChannels = [ProgramAudio, MonitorAudio, Mic1Audio, Mic2Audio, Hdmi1Audio, Hdmi2Audio, Hdmi3Audio, Hdmi4Audio, BgmAudio];

        _stateWorker = new ThreadWorker(PollState, StatePollInterval);
        _mixerWorker = new ThreadWorker(PollMixer, MixerPollInterval);
        _deviceWorker = new ThreadWorker(PollDevice, DevicePollInterval);
        _ = _stateWorker.Restart();
        _ = _mixerWorker.Restart();
        _ = _deviceWorker.Restart();
    }

    public IReadOnlyList<MagewellScene> Scenes
    {
        get => _scenes;
        private set
        {
            if (_scenes.SequenceEqual(value))
                return;
            _scenes = value;
            ScenesChanged?.Invoke(this, value);
        }
    }

    public string ProgramSceneUuid
    {
        get => _programSceneUuid;
        private set
        {
            if (_programSceneUuid == value)
                return;
            _programSceneUuid = value;
            AddEvent(EventType.Input, $"Program: {value}");
            ProgramSceneChanged?.Invoke(this, value);
        }
    }

    public string PreviewSceneUuid
    {
        get => _previewSceneUuid;
        private set
        {
            if (_previewSceneUuid == value)
                return;
            _previewSceneUuid = value;
            AddEvent(EventType.Input, $"Preview: {value}");
            PreviewSceneChanged?.Invoke(this, value);
        }
    }

    public PowerState QuickSwitchState
    {
        get => _quickSwitchState;
        private set
        {
            if (_quickSwitchState == value)
                return;
            _quickSwitchState = value;
            AddEvent(EventType.Other, $"Quick switch: {value}");
            QuickSwitchStateChanged?.Invoke(this, value);
        }
    }

    public PowerState FadeToBlackState
    {
        get => _fadeToBlackState;
        private set
        {
            if (_fadeToBlackState == value)
                return;
            _fadeToBlackState = value;
            AddEvent(EventType.VideoMute, $"Fade to black: {value}");
            FadeToBlackStateChanged?.Invoke(this, value);
        }
    }

    public PowerState FreezeState
    {
        get => _freezeState;
        private set
        {
            if (_freezeState == value)
                return;
            _freezeState = value;
            AddEvent(EventType.VideoMute, $"Freeze: {value}");
            FreezeStateChanged?.Invoke(this, value);
        }
    }

    public IReadOnlyList<MagewellStreamDestination> StreamDestinations
    {
        get => _streamDestinations;
        private set
        {
            if (_streamDestinations.SequenceEqual(value))
                return;
            _streamDestinations = value;
            StreamDestinationsChanged?.Invoke(this, value);
        }
    }

    public PowerState StreamingState
    {
        get => _streamingState;
        private set
        {
            if (_streamingState == value)
                return;
            _streamingState = value;
            AddEvent(EventType.Transport, $"Streaming: {value}");
            StreamingStateChanged?.Invoke(this, value);
        }
    }

    public TimeSpan RecordingTime
    {
        get => _recordingTime;
        private set
        {
            if (_recordingTime == value)
                return;
            _recordingTime = value;
            RecordingTimeChanged?.Invoke(this, value);
        }
    }

    public TimeSpan RecordingTimeRemaining
    {
        get => _recordingTimeRemaining;
        private set
        {
            if (_recordingTimeRemaining == value)
                return;
            _recordingTimeRemaining = value;
            RecordingTimeRemainingChanged?.Invoke(this, value);
        }
    }

    public void SwitchScene(string sceneUuid, MagewellSwitchEffect effect) =>
        SendCommand($"/V1.0/scene/switchByUUID?uuid={Uri.EscapeDataString(sceneUuid)}&switchEffect={(int)effect}");

    public void TakePreview(MagewellSwitchEffect effect) =>
        SendCommand($"/V1.0/scene/previewToPGM?switchEffect={(int)effect}");

    public void SetQuickSwitch(bool on) =>
        SendCommand($"/V1.0/scene/setQuickSwitch?state={(on ? "On" : "Off")}");

    public void SetFadeToBlack(bool on) =>
        SendCommand(on ? "/V1.0/scene/toggleOnFTB" : "/V1.0/scene/toggleOffFTB");

    public void SetFreeze(bool on) =>
        SendCommand(on ? "/V1.0/scene/freeze" : "/V1.0/scene/unfreeze");

    public void StartStream(long destinationId) =>
        SendCommand($"/V1.0/stream/actionById?id={destinationId}&start=true");

    public void StopStream(long destinationId) =>
        SendCommand($"/V1.0/stream/actionById?id={destinationId}&start=false");

    public void StopAllStreams() => SendCommand("/V1.0/stream/stopAll");

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void AddMarker()
    {
        using (PushProperties("AddMarker"))
            LogWarning("The Director Plus can't add a marker");
    }

    protected override void DoRecord() => SendCommand("/V1.0/record/start");

    protected override void DoStop() => SendCommand("/V1.0/record/stop");

    protected override void DoPause()
    {
        using (PushProperties("DoPause"))
            LogWarning("The Director Plus can't pause a recording");
        if (DesiredTransportState == TransportState.RecordingPaused)
            DesiredTransportState = TransportState.Recording;
    }

    private void SendCommand(string pathAndQuery) => _ = _restClient.Get(new Uri(pathAndQuery, UriKind.Relative));

    private Task PollState(CancellationToken token) =>
        _restClient.Post(StateMachineUri, $"{{\"client\":\"{InstanceUid}\"}}", JsonContentType);

    private Task PollMixer(CancellationToken token) => _restClient.Get(MixerUri);

    private async Task PollDevice(CancellationToken token)
    {
        await _restClient.Post(DeviceInfoUri, EmptyRequest, JsonContentType);
        await _restClient.Post(SummaryUri, EmptyRequest, JsonContentType);
    }

    private void HandleConnectionState(ConnectionState state)
    {
        using (PushProperties("HandleConnectionState"))
        {
            switch (state)
            {
                case ConnectionState.Connected:
                    _ = _restClient.Get(SceneListUri);
                    _ = _restClient.Get(StreamListUri);
                    break;
                case ConnectionState.Error:
                    CommunicationState = CommunicationState.Error;
                    break;
            }
        }
    }

    private void HandleResponse(HttpResponseMessage response)
    {
        using (PushProperties("HandleResponse"))
        {
            var path = response.RequestMessage!.RequestUri!.AbsolutePath;
            try
            {
                switch (path)
                {
                    case StateMachinePath:
                        HandleStateMachine(response);
                        break;
                    case MixerPath:
                        HandleMixer(response);
                        break;
                    case SceneListPath:
                        HandleSceneList(response);
                        break;
                    case StreamListPath:
                        HandleStreamList(response);
                        break;
                    case DeviceInfoPath:
                        HandleDeviceInfo(response);
                        break;
                    case SummaryPath:
                        HandleSummary(response);
                        break;
                    default:
                        HandleCommandReply(response);
                        break;
                }
            }
            catch (Exception e)
            {
                LogException(e, $"The reply to {path} could not be handled");
            }
        }
    }

    private JsonNode? ReadReply(HttpResponseMessage response)
    {
        var reply = ParseJson(response.Content.ReadAsStringAsync().Result);
        if (response.IsSuccessStatusCode && reply?["status"]?.GetValue<int>() == GetterSuccess)
            return reply;
        var reason = reply?["message"]?.GetValue<string>() ?? "no JSON status";
        RaiseMomentaryIssue(
            $"The {response.RequestMessage!.RequestUri!.AbsolutePath} poll failed: HTTP {(int)response.StatusCode}, {reason}",
            PollIssueKey, escalateAfter: PollFailuresBeforeIssue);
        return null;
    }

    private static JsonNode? ParseJson(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void HandleCommandReply(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        RaiseMomentaryIssue(
            $"The Director refused {response.RequestMessage!.RequestUri!.PathAndQuery}: HTTP {(int)response.StatusCode}",
            CommandIssueKey);
    }

    private void HandleStateMachine(HttpResponseMessage response)
    {
        var reply = ReadReply(response);
        if (reply == null)
        {
            CommunicationState = CommunicationState.Error;
            return;
        }

        ResolveIssue(PollIssueKey);
        CommunicationState = CommunicationState.Okay;
        ProgramSceneUuid = reply["lastProgramSceneId"]!.GetValue<string>();
        PreviewSceneUuid = reply["lastPreviewSceneId"]!.GetValue<string>();
        QuickSwitchState = OnOff(reply["switchMode"]!.GetValue<int>() == QuickSwitchOnMode);
        FadeToBlackState = OnOff(reply["ftbEnable"]!.GetValue<bool>());
        FreezeState = OnOff(reply["isSceneFreezed"]!.GetValue<bool>());
        StreamingState = OnOff(reply["streamingStatus"]!.AsArray().Count > 0);
        HandleRecordStatus(reply["recordStatus"]!);
        ReportRecordingState();

        var recordError = reply["recordError"]!.AsArray();
        SetIssue(RecordingErrorIssueKey, recordError.Count > 0,
            $"The recorder reported {recordError.ToJsonString()}", IssueSeverity.Critical);
        var streamingError = reply["streamingError"]!.AsArray();
        SetIssue(StreamingErrorIssueKey, streamingError.Count > 0,
            $"Streaming reported {streamingError.ToJsonString()}", IssueSeverity.Major);
        SetIssue(FirmwareUpdateIssueKey, reply["firmwareUpdating"]!.GetValue<bool>(),
            "A firmware update is in progress", IssueSeverity.Minor);

        if (reply["sceneListChanged"]!.GetValue<bool>() || reply["currentShowChanged"]!.GetValue<bool>())
            _ = _restClient.Get(SceneListUri);
        if (reply["streamServersChanged"]!.GetValue<bool>())
            _ = _restClient.Get(StreamListUri);
    }

    private void HandleRecordStatus(JsonNode recordStatus)
    {
        var recording = recordStatus["recording"]!.GetValue<bool>();
        var wasRecording = TransportState == TransportState.Recording;
        TransportState = recording ? TransportState.Recording : TransportState.Stopped;
        RecordingTime = recording ? TimeSpan.FromSeconds(recordStatus["timeRecording"]!.GetValue<int>()) : TimeSpan.Zero;
        if (recording)
            TimestampHandlers?.Invoke(FormatTimestamp(RecordingTime));
        else if (wasRecording)
            TimestampHandlers?.Invoke(string.Empty);

        var enabledTimeRemaining = recordStatus["record"]!.AsArray()
            .Where(recorder => recorder!["enable"]!.GetValue<bool>())
            .Select(recorder => recorder!["timeRemaining"]!.GetValue<int>())
            .ToList();
        RecordingTimeRemaining = TimeSpan.FromSeconds(enabledTimeRemaining.DefaultIfEmpty(0).Min());
        SetIssue(RecordingSpaceIssueKey,
            enabledTimeRemaining.Count > 0 && RecordingTimeRemaining < RecordingSpaceThreshold,
            $"Less than {RecordingSpaceThreshold.TotalMinutes} minutes of recording time remain", IssueSeverity.Major);
    }

    private void ReportRecordingState()
    {
        if (DesiredTransportState == TransportState.Unknown || DesiredTransportState == TransportState)
        {
            ResolveIssue(RecordingStateIssueKey);
            return;
        }
        RaiseMomentaryIssue($"Recording is {TransportState}, should be {DesiredTransportState}",
            RecordingStateIssueKey, escalateAfter: RecordingStatePollsBeforeIssue);
    }

    private void HandleMixer(HttpResponseMessage response)
    {
        var reply = ReadReply(response);
        if (reply == null)
            return;

        foreach (var audio in reply["audioList"]!.AsArray())
        {
            var info = audio!["audioInfo"]!;
            var type = info["type"]!.GetValue<int>();
            AudioChannels.FirstOrDefault(channel => channel.AudioType == type)
                ?.Update(info["volume"]!.GetValue<int>(), (MagewellAudioState)info["muted"]!.GetValue<int>(),
                    info["supportAFV"]!.GetValue<bool>());
        }
    }

    private void HandleSceneList(HttpResponseMessage response)
    {
        var reply = ReadReply(response);
        if (reply == null)
            return;

        Scenes = reply["sceneList"]!.AsArray()
            .Select(scene => new MagewellScene(scene!["uuid"]!.GetValue<string>(), scene["name"]!.GetValue<string>()))
            .ToList();
    }

    private void HandleStreamList(HttpResponseMessage response)
    {
        var reply = ReadReply(response);
        if (reply == null)
            return;

        StreamDestinations = reply["serverList"]!.AsArray()
            .Select(server => new MagewellStreamDestination(server!["id"]!.GetValue<long>(),
                server["name"]!.GetValue<string>(), server["isConfigured"]!.GetValue<bool>()))
            .ToList();
    }

    private void HandleDeviceInfo(HttpResponseMessage response)
    {
        var reply = ReadReply(response);
        if (reply == null)
            return;

        SetDetail("Model", reply["productName"]!.GetValue<string>());
        SetDetail("Device name", reply["deviceName"]!.GetValue<string>());
        SetDetail("Serial", reply["serialNumber"]!.GetValue<string>());
        SetDetail("Software", reply["appVersion"]!.GetValue<string>());
        SetDetail("Firmware", reply["firmwareVersion"]!.GetValue<string>());
        SetDetail("Hardware", reply["hardwareVersion"]!.GetValue<string>());
    }

    private void HandleSummary(HttpResponseMessage response)
    {
        var reply = ReadReply(response);
        if (reply == null)
            return;

        var temperatureWarning = reply["coreTempWarning"]!.GetValue<bool>();
        var fanWarning = reply["fanSpeedWarning"]!.GetValue<bool>();
        var storageFree = reply["storageFreeSize"]!.GetValue<double>() / MegabytesPerGigabyte;
        var storageSize = reply["storageSize"]!.GetValue<double>() / MegabytesPerGigabyte;

        SetDetail("Temperature", $"{reply["coreTemp"]!.GetValue<string>()} °C", Tone(temperatureWarning));
        SetDetail("Fan", reply["fanSpeed"]!.GetValue<string>(), Tone(fanWarning));
        SetDetail("Storage free", string.Create(CultureInfo.InvariantCulture, $"{storageFree:0.0} GB of {storageSize:0} GB"));
        SetDetail("Power draw", reply["powerConsumption"]!.GetValue<string>());

        SetIssue(TemperatureIssueKey, temperatureWarning, "Core temperature warning", IssueSeverity.Major);
        SetIssue(FanIssueKey, fanWarning, "Fan speed warning", IssueSeverity.Major);
        SetIssue(CpuIssueKey, reply["cpuWarning"]!.GetValue<bool>(), "CPU load warning", IssueSeverity.Minor);
        SetIssue(MemoryIssueKey, reply["memInfo"]!["warning"]!.GetValue<bool>(), "Memory warning", IssueSeverity.Minor);
    }

    private void SetIssue(string key, bool active, string message, IssueSeverity severity)
    {
        if (active)
            RaiseOngoingIssue(key, message, severity);
        else
            ResolveIssue(key);
    }

    private static DetailTone Tone(bool warning) => warning ? DetailTone.Warning : DetailTone.Normal;

    private static PowerState OnOff(bool on) => on ? PowerState.On : PowerState.Off;

    private static string FormatTimestamp(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
}
