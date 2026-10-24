using System.Net;
using AVCoders.Core;
using static AVCoders.MediaPlayer.Tests.MagewellDirectorPlusReplies;

namespace AVCoders.MediaPlayer.Tests;

public class MagewellDirectorPlusTest
{
    private const string Host = "http://10.1.3.49";
    private const string StateMachinePath = "/mwapi/statemachine";
    private const string MixerPath = "/V1.0/volume/getConfig";
    private const string SceneListPath = "/V1.0/scene/getSceneList";
    private const string StreamListPath = "/V1.0/stream/getStreamServersList";
    private const string DeviceInfoPath = "/mwapi/device/get-device-info";
    private const string SummaryPath = "/mwapi/device/get-summary-info";
    private const string PictureUuid = "da38f7ef-a133-4813-80da-9134ed0e2c31";
    private const string Hdmi1Uuid = "5e9a9fb3-3d5a-4889-b7ef-18b7ac2c829b";
    private const string Hdmi1Mixer = "\"muted\":2,\"name\":\"HDMI 1\",\"preview\":false,\"scope\":0,\"solo\":0,\"sourceType\":6,\"supportAFV\":true,\"type\":1792,\"uuid\":\"eco0\",\"volume\":0";
    private const string Stopped = "\"recordStatus\": { \"recording\": false, \"timeRecording\": 0";
    private const string EnabledRecorder = "\"enable\": true, \"recording\": false, \"storageType\": 2, \"freeSize\": 101767.553024, \"timeRemaining\": 129404";

    private readonly Mock<RestComms> _restComms = new("host", (ushort)80, "test");
    private readonly MagewellDirectorPlus _director;

    public MagewellDirectorPlusTest()
    {
        _director = new MagewellDirectorPlus("Director", _restComms.Object);
    }

    private void Reply(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _restComms.Object.HttpResponseHandlers!.Invoke(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(Host + pathAndQuery))
        });

    private static string Recording(int seconds) => StateMachine.Replace(Stopped,
        $"\"recordStatus\": {{ \"recording\": true, \"timeRecording\": {seconds}");

    private static string MixerWithHdmi1(int muted, int volume) => Mixer.Replace(Hdmi1Mixer,
        Hdmi1Mixer.Replace("\"muted\":2", $"\"muted\":{muted}").Replace("\"volume\":0", $"\"volume\":{volume}"));

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_restComms.Object, state);

    private void VerifyGet(string pathAndQuery, Times times) =>
        _restComms.Verify(client => client.Get(It.Is<Uri>(uri => uri.OriginalString == pathAndQuery)), times);

    private int CountPosts(string path, string payload) => _restComms.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(RestComms.Post) && invocation.Arguments.Count == 3 &&
        (invocation.Arguments[0] as Uri)?.OriginalString == path && (string)invocation.Arguments[1] == payload);

    private int CountGets(string path) => _restComms.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(RestComms.Get) && invocation.Arguments.Count == 1 &&
        (invocation.Arguments[0] as Uri)?.OriginalString == path);

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(25);
        }
    }

    private Issue? Ongoing(string key) => _director.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    private string? Detail(string label) => _director.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    [Fact]
    public void Record_SendsRecordStart()
    {
        _director.Record();

        VerifyGet("/V1.0/record/start", Times.Once());
    }

    [Fact]
    public void Stop_SendsRecordStop()
    {
        _director.Stop();

        VerifyGet("/V1.0/record/stop", Times.Once());
    }

    [Theory]
    [InlineData(MagewellSwitchEffect.Cut, "/V1.0/scene/switchByUUID?uuid=5e9a9fb3-3d5a-4889-b7ef-18b7ac2c829b&switchEffect=0")]
    [InlineData(MagewellSwitchEffect.Fade, "/V1.0/scene/switchByUUID?uuid=5e9a9fb3-3d5a-4889-b7ef-18b7ac2c829b&switchEffect=1")]
    public void SwitchScene_SendsSwitchByUuid(MagewellSwitchEffect effect, string expected)
    {
        _director.SwitchScene(Hdmi1Uuid, effect);

        VerifyGet(expected, Times.Once());
    }

    [Theory]
    [InlineData(MagewellSwitchEffect.Cut, "/V1.0/scene/previewToPGM?switchEffect=0")]
    [InlineData(MagewellSwitchEffect.Fade, "/V1.0/scene/previewToPGM?switchEffect=1")]
    public void TakePreview_SendsPreviewToProgram(MagewellSwitchEffect effect, string expected)
    {
        _director.TakePreview(effect);

        VerifyGet(expected, Times.Once());
    }

    [Theory]
    [InlineData(true, "/V1.0/scene/setQuickSwitch?state=On")]
    [InlineData(false, "/V1.0/scene/setQuickSwitch?state=Off")]
    public void SetQuickSwitch_SendsTheState(bool on, string expected)
    {
        _director.SetQuickSwitch(on);

        VerifyGet(expected, Times.Once());
    }

    [Theory]
    [InlineData(true, "/V1.0/scene/toggleOnFTB")]
    [InlineData(false, "/V1.0/scene/toggleOffFTB")]
    public void SetFadeToBlack_SendsTheCommand(bool on, string expected)
    {
        _director.SetFadeToBlack(on);

        VerifyGet(expected, Times.Once());
    }

    [Theory]
    [InlineData(true, "/V1.0/scene/freeze")]
    [InlineData(false, "/V1.0/scene/unfreeze")]
    public void SetFreeze_SendsTheCommand(bool on, string expected)
    {
        _director.SetFreeze(on);

        VerifyGet(expected, Times.Once());
    }

    [Fact]
    public void StartStream_SendsActionByIdWithStartTrue()
    {
        _director.StartStream(1789241795392);

        VerifyGet("/V1.0/stream/actionById?id=1789241795392&start=true", Times.Once());
    }

    [Fact]
    public void StopStream_SendsActionByIdWithStartFalse()
    {
        _director.StopStream(1789241795392);

        VerifyGet("/V1.0/stream/actionById?id=1789241795392&start=false", Times.Once());
    }

    [Fact]
    public void StopAllStreams_SendsStopAll()
    {
        _director.StopAllStreams();

        VerifyGet("/V1.0/stream/stopAll", Times.Once());
    }

    [Fact]
    public void PowerAndMarkerCommands_SendNothing()
    {
        _director.PowerOn();
        _director.PowerOff();
        _director.AddMarker();
        _director.Pause();

        _restComms.Verify(client => client.Get(It.Is<Uri>(uri => uri.OriginalString != MixerPath)), Times.Never);
    }

    [Fact]
    public async Task Poll_ReadsTheStateMachineAsThisInstance()
    {
        var request = $"{{\"client\":\"{_director.InstanceUid}\"}}";

        await WaitUntilAsync(() => CountPosts(StateMachinePath, request) > 0, "The statemachine was never polled");
    }

    [Fact]
    public async Task Poll_ReadsTheMixerIdentityAndHealth()
    {
        await WaitUntilAsync(() => CountGets(MixerPath) > 0, "The mixer was never polled");
        await WaitUntilAsync(() => CountPosts(DeviceInfoPath, "{}") > 0, "The device info was never read");
        await WaitUntilAsync(() => CountPosts(SummaryPath, "{}") > 0, "The health summary was never read");
    }

    [Fact]
    public void Connecting_ReadsTheSceneAndStreamLists()
    {
        SetConnection(ConnectionState.Connected);

        VerifyGet(SceneListPath, Times.Once());
        VerifyGet(StreamListPath, Times.Once());
    }

    [Fact]
    public void StateMachine_SetsCommunicationOkay()
    {
        Reply(StateMachinePath, StateMachine);

        Assert.Equal(CommunicationState.Okay, _director.CommunicationState);
        Assert.Empty(_director.GetOngoingIssues());
    }

    [Fact]
    public void StateMachine_ReadsTheSwitcherState()
    {
        Reply(StateMachinePath, StateMachine);

        Assert.Equal(PictureUuid, _director.ProgramSceneUuid);
        Assert.Equal(PictureUuid, _director.PreviewSceneUuid);
        Assert.Equal(PowerState.On, _director.QuickSwitchState);
        Assert.Equal(PowerState.Off, _director.FadeToBlackState);
        Assert.Equal(PowerState.Off, _director.FreezeState);
        Assert.Equal(PowerState.Off, _director.StreamingState);
        Assert.Equal(TransportState.Stopped, _director.TransportState);
        Assert.Equal(TimeSpan.Zero, _director.RecordingTime);
        Assert.Equal(TimeSpan.FromSeconds(129404), _director.RecordingTimeRemaining);
    }

    [Fact]
    public void BeforeTheFirstPoll_TheVideoAndStreamingStatesAreUnknown()
    {
        Assert.Equal(PowerState.Unknown, _director.QuickSwitchState);
        Assert.Equal(PowerState.Unknown, _director.FadeToBlackState);
        Assert.Equal(PowerState.Unknown, _director.FreezeState);
        Assert.Equal(PowerState.Unknown, _director.StreamingState);
    }

    [Fact]
    public void FirstPoll_FiresEveryStateChange()
    {
        var changes = new List<string>();
        _director.QuickSwitchStateChanged += (_, state) => changes.Add($"Quick switch {state}");
        _director.FadeToBlackStateChanged += (_, state) => changes.Add($"Fade to black {state}");
        _director.FreezeStateChanged += (_, state) => changes.Add($"Freeze {state}");
        _director.StreamingStateChanged += (_, state) => changes.Add($"Streaming {state}");

        Reply(StateMachinePath, StateMachine);

        Assert.Equal(["Quick switch On", "Fade to black Off", "Freeze Off", "Streaming Off"], changes);
    }

    [Fact]
    public void StateMachine_ProgramChange_FiresProgramSceneChanged()
    {
        Reply(StateMachinePath, StateMachine);
        var programs = new List<string>();
        var previews = new List<string>();
        _director.ProgramSceneChanged += (_, uuid) => programs.Add(uuid);
        _director.PreviewSceneChanged += (_, uuid) => previews.Add(uuid);

        Reply(StateMachinePath, StateMachine.Replace($"\"lastProgramSceneId\": \"{PictureUuid}\"", $"\"lastProgramSceneId\": \"{Hdmi1Uuid}\""));
        Reply(StateMachinePath, StateMachine.Replace($"\"lastProgramSceneId\": \"{PictureUuid}\"", $"\"lastProgramSceneId\": \"{Hdmi1Uuid}\""));

        Assert.Equal([Hdmi1Uuid], programs);
        Assert.Empty(previews);
        Assert.Equal(Hdmi1Uuid, _director.ProgramSceneUuid);
    }

    [Fact]
    public void StateMachine_PreviewChange_FiresPreviewSceneChanged()
    {
        Reply(StateMachinePath, StateMachine);
        var previews = new List<string>();
        _director.PreviewSceneChanged += (_, uuid) => previews.Add(uuid);

        Reply(StateMachinePath, StateMachine.Replace($"\"lastPreviewSceneId\": \"{PictureUuid}\"", $"\"lastPreviewSceneId\": \"{Hdmi1Uuid}\""));

        Assert.Equal([Hdmi1Uuid], previews);
        Assert.Equal(PictureUuid, _director.ProgramSceneUuid);
    }

    [Fact]
    public void StateMachine_QuickSwitchOff_FiresQuickSwitchStateChanged()
    {
        Reply(StateMachinePath, StateMachine);
        var changes = new List<PowerState>();
        _director.QuickSwitchStateChanged += (_, state) => changes.Add(state);

        Reply(StateMachinePath, StateMachine.Replace("\"switchMode\": 0", "\"switchMode\": 1"));

        Assert.Equal([PowerState.Off], changes);
        Assert.Equal(PowerState.Off, _director.QuickSwitchState);
    }

    [Fact]
    public void StateMachine_FadeToBlack_FiresFadeToBlackStateChanged()
    {
        Reply(StateMachinePath, StateMachine);
        var changes = new List<PowerState>();
        _director.FadeToBlackStateChanged += (_, state) => changes.Add(state);

        Reply(StateMachinePath, StateMachine.Replace("\"ftbEnable\": false", "\"ftbEnable\": true"));

        Assert.Equal([PowerState.On], changes);
        Assert.Equal(PowerState.On, _director.FadeToBlackState);
    }

    [Fact]
    public void StateMachine_Frozen_FiresFreezeStateChanged()
    {
        Reply(StateMachinePath, StateMachine);
        var changes = new List<PowerState>();
        _director.FreezeStateChanged += (_, state) => changes.Add(state);

        Reply(StateMachinePath, StateMachine.Replace("\"isSceneFreezed\": false", "\"isSceneFreezed\": true"));

        Assert.Equal([PowerState.On], changes);
        Assert.Equal(PowerState.On, _director.FreezeState);
    }

    [Fact]
    public void StateMachine_StreamingStatusEntries_FireStreamingStateChanged()
    {
        Reply(StateMachinePath, StateMachine);
        var changes = new List<PowerState>();
        _director.StreamingStateChanged += (_, state) => changes.Add(state);

        Reply(StateMachinePath, StateMachine.Replace("\"streamingStatus\": [ ]", "\"streamingStatus\": [ { \"id\": 1789241795389 } ]"));
        Reply(StateMachinePath, StateMachine);

        Assert.Equal([PowerState.On, PowerState.Off], changes);
    }

    [Fact]
    public void StateMachine_Recording_SetsTheTransportStateAndTimestamp()
    {
        var states = new List<TransportState>();
        var timestamps = new List<string>();
        _director.TransportStateHandlers += state => states.Add(state);
        _director.TimestampHandlers += timestamp => timestamps.Add(timestamp);

        Reply(StateMachinePath, StateMachine);
        Reply(StateMachinePath, Recording(1));
        Reply(StateMachinePath, Recording(3723));
        Reply(StateMachinePath, StateMachine);
        Reply(StateMachinePath, StateMachine);

        Assert.Equal([TransportState.Stopped, TransportState.Recording, TransportState.Stopped], states);
        Assert.Equal(["00:00:01", "01:02:03", ""], timestamps);
    }

    [Fact]
    public void StateMachine_Recording_SetsTheRecordingTime()
    {
        var changes = new List<TimeSpan>();
        _director.RecordingTimeChanged += (_, time) => changes.Add(time);

        Reply(StateMachinePath, StateMachine);
        Reply(StateMachinePath, Recording(1));
        Reply(StateMachinePath, Recording(3723));
        Reply(StateMachinePath, StateMachine);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3723), TimeSpan.Zero], changes);
        Assert.Equal(TimeSpan.Zero, _director.RecordingTime);
    }

    [Fact]
    public void DesiredTransportState_IsUnknownUntilCommanded()
    {
        Assert.Equal(TransportState.Unknown, _director.DesiredTransportState);

        _director.Record();
        Assert.Equal(TransportState.Recording, _director.DesiredTransportState);

        _director.Stop();
        Assert.Equal(TransportState.Stopped, _director.DesiredTransportState);
    }

    [Fact]
    public void StateMachine_RecordingTimeRemaining_ComesFromTheEnabledRecorder()
    {
        var changes = new List<TimeSpan>();
        _director.RecordingTimeRemainingChanged += (_, remaining) => changes.Add(remaining);

        Reply(StateMachinePath, StateMachine);
        Reply(StateMachinePath, StateMachine.Replace(EnabledRecorder, EnabledRecorder.Replace("129404", "7200")));

        Assert.Equal([TimeSpan.FromSeconds(129404), TimeSpan.FromHours(2)], changes);
    }

    [Theory]
    [InlineData("\"sceneListChanged\": false", "\"sceneListChanged\": true")]
    [InlineData("\"currentShowChanged\": false", "\"currentShowChanged\": true")]
    public void StateMachine_SceneOrShowFlag_RereadsTheSceneList(string flag, string set)
    {
        Reply(StateMachinePath, StateMachine.Replace(flag, set));

        VerifyGet(SceneListPath, Times.Once());
        VerifyGet(StreamListPath, Times.Never());
    }

    [Fact]
    public void StateMachine_StreamServersFlag_RereadsTheStreamList()
    {
        Reply(StateMachinePath, StateMachine.Replace("\"streamServersChanged\": false", "\"streamServersChanged\": true"));

        VerifyGet(StreamListPath, Times.Once());
        VerifyGet(SceneListPath, Times.Never());
    }

    [Fact]
    public void StateMachine_WithoutFlags_ReadsNoLists()
    {
        Reply(StateMachinePath, StateMachine);

        VerifyGet(SceneListPath, Times.Never());
        VerifyGet(StreamListPath, Times.Never());
    }

    [Fact]
    public void SceneList_SetsTheScenesOnce()
    {
        var changes = new List<IReadOnlyList<MagewellScene>>();
        _director.ScenesChanged += (_, scenes) => changes.Add(scenes);

        Reply(SceneListPath, SceneList);
        Reply(SceneListPath, SceneList);

        Assert.Single(changes);
        Assert.Equal(7, _director.Scenes.Count);
        Assert.Equal(new MagewellScene(PictureUuid, "PICTURE"), _director.Scenes[0]);
        Assert.Equal(new MagewellScene(Hdmi1Uuid, "HDMI 1"), _director.Scenes[1]);
        Assert.Equal(new MagewellScene("5d6c0c2a-10aa-4f99-8b5a-f5721035dcb8", "Phone Camera #1"), _director.Scenes[6]);
    }

    [Fact]
    public void SceneList_Rename_FiresScenesChanged()
    {
        Reply(SceneListPath, SceneList);
        var changes = new List<IReadOnlyList<MagewellScene>>();
        _director.ScenesChanged += (_, scenes) => changes.Add(scenes);

        Reply(SceneListPath, SceneList.Replace("\"name\":\"HDMI 1\"", "\"name\":\"Lectern\""));

        Assert.Single(changes);
        Assert.Equal(new MagewellScene(Hdmi1Uuid, "Lectern"), _director.Scenes[1]);
    }

    [Fact]
    public void StreamList_SetsTheDestinations()
    {
        var changes = new List<IReadOnlyList<MagewellStreamDestination>>();
        _director.StreamDestinationsChanged += (_, destinations) => changes.Add(destinations);

        Reply(StreamListPath, StreamList);
        Reply(StreamListPath, StreamList);

        Assert.Single(changes);
        Assert.Equal(9, _director.StreamDestinations.Count);
        Assert.Equal(new MagewellStreamDestination(1789241795389, "YouTube", false), _director.StreamDestinations[0]);
        Assert.Equal(new MagewellStreamDestination(1789241795392, "NDI", false), _director.StreamDestinations[3]);
        Assert.Equal(new MagewellStreamDestination(1789241795397, "Restream", false), _director.StreamDestinations[8]);
    }

    [Fact]
    public void Mixer_SetsEveryFixedChannel()
    {
        Reply(MixerPath, Mixer);

        Assert.Equal(80, _director.ProgramAudio.Volume);
        Assert.Equal(MagewellAudioState.AlwaysOn, _director.ProgramAudio.AudioState);
        Assert.Equal(MuteState.Off, _director.ProgramAudio.MuteState);
        Assert.Equal(50, _director.MonitorAudio.Volume);
        Assert.Equal(MagewellAudioState.AudioFollowVideo, _director.Hdmi1Audio.AudioState);
        Assert.Equal(MuteState.Off, _director.Hdmi1Audio.MuteState);
        Assert.All(_director.AudioChannels, channel => Assert.NotEqual(MagewellAudioState.Unknown, channel.AudioState));
        Assert.Equal(9, _director.AudioChannels.Count);
    }

    [Fact]
    public void Mixer_ChannelOff_IsMuted()
    {
        Reply(MixerPath, Mixer);
        var states = new List<MagewellAudioState>();
        var mutes = new List<MuteState>();
        _director.Hdmi1Audio.AudioStateChanged += (_, state) => states.Add(state);
        _director.Hdmi1Audio.MuteStateHandlers += mute => mutes.Add(mute);

        Reply(MixerPath, MixerWithHdmi1(1, 0));

        Assert.Equal([MagewellAudioState.AlwaysOff], states);
        Assert.Equal([MuteState.On], mutes);
    }

    [Theory]
    [InlineData(-40, 0)]
    [InlineData(-15, 50)]
    [InlineData(-10, 60)]
    [InlineData(-1, 78)]
    [InlineData(0, 80)]
    [InlineData(10, 100)]
    public void Mixer_MapsDecibelsToPercent(int db, int expectedPercent)
    {
        Reply(MixerPath, MixerWithHdmi1(2, db));

        Assert.Equal(expectedPercent, _director.Hdmi1Audio.Volume);
    }

    [Theory]
    [InlineData(0, -40, 0)]
    [InlineData(50, -15, 50)]
    [InlineData(49, -15, 50)]
    [InlineData(51, -14, 52)]
    [InlineData(80, 0, 80)]
    [InlineData(100, 10, 100)]
    [InlineData(150, 10, 100)]
    [InlineData(-5, -40, 0)]
    public void SetLevel_SendsTheDecibelLevel(int percent, int expectedDb, int expectedPercent)
    {
        _director.Hdmi1Audio.SetLevel(percent);

        VerifyGet($"/V1.0/volume/setVolume?type=1792&volume={expectedDb}", Times.Once());
        Assert.Equal(expectedPercent, _director.Hdmi1Audio.Volume);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(10, 5)]
    public void LevelUp_RaisesAtLeastOneDecibel(int amount, int expectedDb)
    {
        Reply(MixerPath, Mixer);

        _director.ProgramAudio.LevelUp(amount);

        VerifyGet($"/V1.0/volume/setVolume?type=3&volume={expectedDb}", Times.Once());
    }

    [Theory]
    [InlineData(1, -1)]
    [InlineData(2, -1)]
    [InlineData(10, -5)]
    public void LevelDown_LowersAtLeastOneDecibel(int amount, int expectedDb)
    {
        Reply(MixerPath, Mixer);

        _director.ProgramAudio.LevelDown(amount);

        VerifyGet($"/V1.0/volume/setVolume?type=3&volume={expectedDb}", Times.Once());
    }

    [Fact]
    public void LevelUp_AtTheTop_StaysAtTheTop()
    {
        _director.ProgramAudio.SetLevel(100);

        _director.ProgramAudio.LevelUp(5);

        VerifyGet("/V1.0/volume/setVolume?type=3&volume=10", Times.Exactly(2));
    }

    [Fact]
    public void SetAudioMute_On_TurnsTheChannelOff()
    {
        Reply(MixerPath, Mixer);

        _director.Hdmi1Audio.SetAudioMute(MuteState.On);

        VerifyGet("/V1.0/volume/setState?type=1792&state=1", Times.Once());
        Assert.Equal(MuteState.On, _director.Hdmi1Audio.MuteState);
        Assert.Equal(MagewellAudioState.AlwaysOff, _director.Hdmi1Audio.AudioState);
    }

    [Fact]
    public void SetAudioMute_Off_RestoresAudioFollowVideo()
    {
        Reply(MixerPath, Mixer);
        _director.Hdmi1Audio.SetAudioMute(MuteState.On);

        _director.Hdmi1Audio.SetAudioMute(MuteState.Off);

        VerifyGet("/V1.0/volume/setState?type=1792&state=2", Times.Once());
        Assert.Equal(MuteState.Off, _director.Hdmi1Audio.MuteState);
    }

    [Fact]
    public void SetAudioMute_Off_RestoresAlwaysOnWhereThePollSawIt()
    {
        Reply(MixerPath, MixerWithHdmi1(0, 0));
        Reply(MixerPath, MixerWithHdmi1(1, 0));

        _director.Hdmi1Audio.SetAudioMute(MuteState.Off);

        VerifyGet("/V1.0/volume/setState?type=1792&state=0", Times.Once());
    }

    [Fact]
    public void SetAudioMute_Off_BeforeAnyPoll_TurnsTheChannelOn()
    {
        _director.Hdmi3Audio.SetAudioMute(MuteState.Off);
        _director.Mic1Audio.SetAudioMute(MuteState.Off);

        VerifyGet("/V1.0/volume/setState?type=2050&state=0", Times.Once());
        VerifyGet("/V1.0/volume/setState?type=512&state=0", Times.Once());
    }

    [Fact]
    public void SetAudioMute_Off_WhenOnlySeenOff_RestoresFollowVideoWhereSupported()
    {
        Reply(MixerPath, MixerWithHdmi1(1, 0).Replace("\"muted\":0,\"name\":\"PROGRAM\"", "\"muted\":1,\"name\":\"PROGRAM\""));

        _director.Hdmi1Audio.SetAudioMute(MuteState.Off);
        _director.ProgramAudio.SetAudioMute(MuteState.Off);

        VerifyGet("/V1.0/volume/setState?type=1792&state=2", Times.Once());
        VerifyGet("/V1.0/volume/setState?type=3&state=0", Times.Once());
    }

    [Fact]
    public void Mixer_ReadsFollowVideoSupport()
    {
        Assert.All(_director.AudioChannels, channel => Assert.False(channel.SupportsFollowVideo));

        Reply(MixerPath, Mixer);

        Assert.Equal([_director.Hdmi1Audio, _director.Hdmi2Audio, _director.Hdmi3Audio, _director.Hdmi4Audio],
            _director.AudioChannels.Where(channel => channel.SupportsFollowVideo));
    }

    [Fact]
    public void ToggleAudioMute_FlipsTheMute()
    {
        Reply(MixerPath, Mixer);

        _director.ProgramAudio.ToggleAudioMute();
        _director.ProgramAudio.ToggleAudioMute();

        VerifyGet("/V1.0/volume/setState?type=3&state=1", Times.Once());
        VerifyGet("/V1.0/volume/setState?type=3&state=0", Times.Once());
    }

    [Fact]
    public void SetAudioState_SendsTheStateAndFiresAudioStateChanged()
    {
        Reply(MixerPath, Mixer);
        var states = new List<MagewellAudioState>();
        _director.BgmAudio.AudioStateChanged += (_, state) => states.Add(state);

        _director.BgmAudio.SetAudioState(MagewellAudioState.AlwaysOff);

        VerifyGet("/V1.0/volume/setState?type=1539&state=1", Times.Once());
        Assert.Equal([MagewellAudioState.AlwaysOff], states);
    }

    [Fact]
    public void StateMachine_HttpError_ReportsCommunicationError()
    {
        Reply(StateMachinePath, StateMachine);

        Reply(StateMachinePath, "<html>502 Bad Gateway</html>", HttpStatusCode.BadGateway);

        Assert.Equal(CommunicationState.Error, _director.CommunicationState);
        var issue = Assert.Single(_director.GetIssues(), i => i.Key == "unanswered-poll");
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Contains("HTTP 502", issue.Message);
    }

    [Fact]
    public void StateMachine_NonZeroStatus_ReportsTheMessage()
    {
        Reply(StateMachinePath, ItemNotExist);

        Assert.Equal(CommunicationState.Error, _director.CommunicationState);
        Assert.Contains(_director.GetIssues(), issue => issue.Key == "unanswered-poll" && issue.Message.Contains("item not exist"));
    }

    [Fact]
    public void StateMachine_NotJson_ReportsCommunicationError()
    {
        Reply(StateMachinePath, "{ \"status\": 0, \"message\": ");

        Assert.Equal(CommunicationState.Error, _director.CommunicationState);
        Assert.Contains(_director.GetIssues(), issue => issue.Key == "unanswered-poll" && issue.Message.Contains("no JSON status"));
    }

    [Fact]
    public void StateMachine_ThreeFailures_EscalateAndAGoodReplyResolves()
    {
        Reply(StateMachinePath, ItemNotExist);
        Reply(StateMachinePath, ItemNotExist);
        Assert.Null(Ongoing("unanswered-poll"));

        Reply(StateMachinePath, ItemNotExist);
        Assert.Equal(IssueSeverity.Major, Ongoing("unanswered-poll")!.Severity);

        Reply(StateMachinePath, StateMachine);
        Assert.Empty(_director.GetOngoingIssues());
        Assert.Equal(CommunicationState.Okay, _director.CommunicationState);
    }

    [Fact]
    public void ConnectionError_ReportsCommunicationError()
    {
        Reply(StateMachinePath, StateMachine);

        SetConnection(ConnectionState.Error);

        Assert.Equal(CommunicationState.Error, _director.CommunicationState);
        Assert.Equal(IssueSeverity.Critical, Ongoing("communication")!.Severity);
    }

    [Fact]
    public void ConnectionRestored_ResolvesOnTheNextStateMachine()
    {
        SetConnection(ConnectionState.Error);
        SetConnection(ConnectionState.Connected);

        Reply(StateMachinePath, StateMachine);

        Assert.Equal(CommunicationState.Okay, _director.CommunicationState);
        Assert.Null(Ongoing("communication"));
    }

    [Fact]
    public void Command_Refused_RaisesAMomentaryIssue()
    {
        Reply("/V1.0/stream/actionById?id=1789241795392&start=true", CommandRefused, HttpStatusCode.Forbidden);

        var issue = Assert.Single(_director.GetIssues());
        Assert.Equal("command-refused", issue.Key);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Contains("/V1.0/stream/actionById?id=1789241795392&start=true", issue.Message);
        Assert.Contains("HTTP 403", issue.Message);
    }

    [Fact]
    public void Command_Accepted_RaisesNothing()
    {
        Reply("/V1.0/record/start", CommandAccepted);

        Assert.Empty(_director.GetIssues());
    }

    [Fact]
    public void RecordError_RaisesACriticalIssueUntilCleared()
    {
        Reply(StateMachinePath, StateMachine.Replace("\"recordError\": [ ]", "\"recordError\": [ { \"code\": 1 } ]"));

        var issue = Ongoing("recording-error");
        Assert.Equal(IssueSeverity.Critical, issue!.Severity);
        Assert.Contains("\"code\":1", issue.Message);

        Reply(StateMachinePath, StateMachine);
        Assert.Null(Ongoing("recording-error"));
    }

    [Fact]
    public void StreamingError_RaisesAMajorIssueUntilCleared()
    {
        Reply(StateMachinePath, StateMachine.Replace("\"streamingError\": [ ]", "\"streamingError\": [ { \"code\": 1 } ]"));

        Assert.Equal(IssueSeverity.Major, Ongoing("streaming-error")!.Severity);

        Reply(StateMachinePath, StateMachine);
        Assert.Null(Ongoing("streaming-error"));
    }

    [Fact]
    public void FirmwareUpdating_RaisesAMinorIssueUntilDone()
    {
        Reply(StateMachinePath, StateMachine.Replace("\"firmwareUpdating\": false", "\"firmwareUpdating\": true"));

        Assert.Equal(IssueSeverity.Minor, Ongoing("firmware-update")!.Severity);

        Reply(StateMachinePath, StateMachine);
        Assert.Null(Ongoing("firmware-update"));
    }

    [Fact]
    public void LowRecordingTime_RaisesAMajorIssueUntilSpaceReturns()
    {
        Reply(StateMachinePath, StateMachine.Replace(EnabledRecorder, EnabledRecorder.Replace("129404", "3599")));

        Assert.Equal(IssueSeverity.Major, Ongoing("recording-space")!.Severity);

        Reply(StateMachinePath, StateMachine.Replace(EnabledRecorder, EnabledRecorder.Replace("129404", "3600")));
        Assert.Null(Ongoing("recording-space"));
    }

    [Fact]
    public void LowRecordingTime_OnADisabledRecorder_IsIgnored()
    {
        var reply = StateMachine.Replace(
            "{ \"enable\": false, \"recording\": false, \"storageType\": 2, \"freeSize\": 101767.553024, \"timeRemaining\": 129404 } ] }",
            "{ \"enable\": false, \"recording\": false, \"storageType\": 2, \"freeSize\": 0, \"timeRemaining\": 0 } ] }");
        Assert.NotEqual(StateMachine, reply);

        Reply(StateMachinePath, reply);

        Assert.Null(Ongoing("recording-space"));
        Assert.Equal(TimeSpan.FromSeconds(129404), _director.RecordingTimeRemaining);
    }

    [Fact]
    public void NoRecorderEnabled_RaisesNoRecordingSpaceIssue()
    {
        var reply = StateMachine.Replace("\"enable\": true", "\"enable\": false");
        Assert.NotEqual(StateMachine, reply);
        Reply(StateMachinePath, StateMachine.Replace(EnabledRecorder, EnabledRecorder.Replace("129404", "60")));
        Assert.NotNull(Ongoing("recording-space"));

        Reply(StateMachinePath, reply);

        Assert.Null(Ongoing("recording-space"));
        Assert.Equal(TimeSpan.Zero, _director.RecordingTimeRemaining);
    }

    [Fact]
    public void Record_NotStartedAfterFivePolls_RaisesAMajorIssue()
    {
        _director.Record();

        for (var poll = 0; poll < 4; poll++)
            Reply(StateMachinePath, StateMachine);
        Assert.Null(Ongoing("recording-state"));

        Reply(StateMachinePath, StateMachine);
        Assert.Equal(IssueSeverity.Major, Ongoing("recording-state")!.Severity);

        Reply(StateMachinePath, Recording(1));
        Assert.Null(Ongoing("recording-state"));
    }

    [Fact]
    public void Record_StartedOnTheNextPoll_RaisesNoOngoingIssue()
    {
        _director.Record();

        Reply(StateMachinePath, StateMachine);
        Reply(StateMachinePath, Recording(1));

        Assert.Empty(_director.GetOngoingIssues());
    }

    [Fact]
    public void Stopped_WithoutACommand_RaisesNoRecordingStateIssue()
    {
        for (var poll = 0; poll < 6; poll++)
            Reply(StateMachinePath, Recording(poll));

        Assert.DoesNotContain(_director.GetIssues(), issue => issue.Key == "recording-state");
    }

    [Fact]
    public void Pause_WhileRecording_KeepsRecordingDesired()
    {
        Reply(StateMachinePath, Recording(1));
        _director.Record();

        _director.Pause();
        for (var poll = 0; poll < 6; poll++)
            Reply(StateMachinePath, Recording(poll + 2));

        Assert.DoesNotContain(_director.GetIssues(), issue => issue.Key == "recording-state");
        Assert.Equal(TransportState.Recording, _director.DesiredTransportState);
    }

    [Fact]
    public void DeviceInfo_SetsTheIdentityDetails()
    {
        Reply(DeviceInfoPath, DeviceInfo);

        Assert.Equal("Director Plus", Detail("Model"));
        Assert.Equal("Director-SC6-LG-COMMS-RACK", Detail("Device name"));
        Assert.Equal("A529260506027", Detail("Serial"));
        Assert.Equal("3.3.1106", Detail("Software"));
        Assert.Equal("1.1", Detail("Firmware"));
        Assert.Equal("A", Detail("Hardware"));
    }

    [Fact]
    public void Summary_SetsTheHealthDetails()
    {
        Reply(SummaryPath, Summary);

        Assert.Equal("43.7 °C", Detail("Temperature"));
        Assert.Equal("3840 rpm", Detail("Fan"));
        Assert.Equal("102.3 GB of 128 GB", Detail("Storage free"));
        Assert.Equal("9.34 w", Detail("Power draw"));
        Assert.Empty(_director.GetIssues());
    }

    [Theory]
    [InlineData("\"coreTempWarning\": false", "\"coreTempWarning\": true", "temperature", IssueSeverity.Major)]
    [InlineData("\"fanSpeedWarning\": false", "\"fanSpeedWarning\": true", "fan", IssueSeverity.Major)]
    [InlineData("\"cpuWarning\": false", "\"cpuWarning\": true", "cpu", IssueSeverity.Minor)]
    [InlineData("\"total\": 8109528, \"warning\": false", "\"total\": 8109528, \"warning\": true", "memory", IssueSeverity.Minor)]
    public void Summary_Warning_RaisesAnIssueUntilCleared(string flag, string set, string key, IssueSeverity severity)
    {
        Reply(SummaryPath, Summary.Replace(flag, set));

        Assert.Equal(severity, Ongoing(key)!.Severity);

        Reply(SummaryPath, Summary);
        Assert.Null(Ongoing(key));
    }

    [Fact]
    public void Summary_TemperatureWarning_MarksTheDetail()
    {
        Reply(SummaryPath, Summary.Replace("\"coreTempWarning\": false", "\"coreTempWarning\": true"));

        Assert.Equal(DetailTone.Warning, _director.Details.Single(detail => detail.Label == "Temperature").Tone);
        Assert.Equal(DetailTone.Normal, _director.Details.Single(detail => detail.Label == "Fan").Tone);
    }

    [Fact]
    public void StateMachine_WithAThrowingHandler_StaysHealthy()
    {
        Reply(StateMachinePath, StateMachine);
        _director.FadeToBlackStateChanged += (_, _) => throw new InvalidOperationException("disposed panel");

        Reply(StateMachinePath, StateMachine.Replace("\"ftbEnable\": false", "\"ftbEnable\": true"));

        Assert.Equal(CommunicationState.Okay, _director.CommunicationState);
        Assert.Contains(_director.Errors, error => error.Message.Contains(StateMachinePath));
    }
}
