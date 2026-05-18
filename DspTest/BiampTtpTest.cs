using AVCoders.Core;
using AVCoders.Core.Tests;
using Xunit.Abstractions;

namespace AVCoders.Dsp.Tests;

public class BiampTtpTest
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly BiampTtp _dsp;
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private const string GainName = "Gain";
    private const string MuteName = "Mute";
    private const string StringName = "String";

    public BiampTtpTest(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _dsp = new BiampTtp(_mockClient.Object, "Test", 100);
        
        _mockClient.Setup(x => x.Send($"{GainName} get maxLevel 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":12.000000\n"));
        _mockClient.Setup(x => x.Send($"{GainName} get minLevel 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":0.000000\n"));
        _mockClient.Setup(x => x.Send($"{GainName} get level 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":3.000000\n"));
        
        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Fact]
    public void AddControl_SubscribesToTheLevel()
    {
        _mockClient.Verify(x => x.Send($"{GainName} subscribe level 1 AvCodersLevel-{GainName}-1\n"));
    }

    [Fact]
    public void AddControl_SubscribesToTheMute()
    {
        _mockClient.Verify(x => x.Send($"{MuteName} subscribe mute 1 AvCodersMute-{MuteName}-1\n"));
    }

    [Fact]
    public void HandleResponse_StoresThePercentage()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersLevel-Gain-1\" \"value\":-2.000000\n");

        Assert.Equal(98, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData("! \"publishToken\":\"AvCodersMute-Mute-1\" \"value\":false\n", MuteState.Off)]
    [InlineData("! \"publishToken\":\"AvCodersMute-Mute-1\" \"value\":true\n", MuteState.On)]
    public void HandleResponse_StoresTheMuteState(string response, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        Assert.Equal(expectedMuteState, _dsp.GetAudioMute(MuteName));
    }

    [Theory]
    [InlineData(0, "Gain set level 1 -100\n")]
    [InlineData(100, "Gain set level 1 0\n")]
    [InlineData(50, "Gain set level 1 -50\n")]
    [InlineData(13, "Gain set level 1 -87\n")]
    public void SetLevel_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        _dsp.SetLevel(GainName, percentage);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(1001, "DEVICE recallPreset 1001\n")]
    [InlineData(1500, "DEVICE recallPreset 1500\n")]
    public void RecallPreset_RecallsThePreset(int presetNumber, string expectedCommand)
    {
        _dsp.RecallPreset(presetNumber);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData("EWIS_ON", "DEVICE recallPresetByName \"EWIS_ON\"\n")]
    [InlineData("EWIS_OFF","DEVICE recallPresetByName \"EWIS_OFF\"\n")]
    [InlineData("EWIS OFF","DEVICE recallPresetByName \"EWIS OFF\"\n")]
    public void RecallPresetByName_RecallsThePreset(string presetName, string expectedCommand)
    {
        _dsp.RecallPreset(presetName);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
    
    
    [Theory]
    [InlineData(true, 5, "mic_press set state 5 true\n")]
    [InlineData(false,3, "mic_press set state 3 false\n")]
    public void SetState_SendsTheCommand(bool state, int index, string expectedCommand)
    {
        _dsp.SetState("mic_press", index, state);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(MuteState.On, "true")]
    [InlineData(MuteState.Off, "false")]
    public void SetAudioMute_DefaultsToIndexOne(MuteState state, string expectedValue)
    {
        _dsp.SetAudioMute(MuteName, state);
        _mockClient.Verify(x => x.Send($"{MuteName} set mute 1 {expectedValue}\n"));
    }

    [Theory]
    [InlineData(MuteState.On, 5, "room_mic_level set mute 5 true\n")]
    [InlineData(MuteState.Off, 5, "room_mic_level set mute 5 false\n")]
    [InlineData(MuteState.On, 3, "room_mic_level set mute 3 true\n")]
    public void SetAudioMute_AtIndex_SendsTheCommand(MuteState state, int index, string expectedCommand)
    {
        _dsp.SetAudioMute("room_mic_level", index, state);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void ToggleAudioMute_AtIndex_TogglesFromOnToOff()
    {
        _dsp.AddControl(Mock.Of<MuteStateHandler>(), "room_mic_level", 5);
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersMute-room_mic_level-5\" \"value\":true\n");

        _dsp.ToggleAudioMute("room_mic_level", 5);

        _mockClient.Verify(x => x.Send("room_mic_level set mute 5 false\n"));
    }

    [Theory]
    [InlineData(MuteState.On, "true")]
    [InlineData(MuteState.Off, "false")]
    public void BiampVolumeControl_SetAudioMute_UsesTheConfiguredIndex(MuteState state, string expectedValue)
    {
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);

        vc.SetAudioMute(state);

        _mockClient.Verify(x => x.Send($"room_mic_level set mute 5 {expectedValue}\n"));
    }

    [Fact]
    public void BiampVolumeControl_ToggleAudioMute_UsesTheConfiguredIndex()
    {
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersMute-room_mic_level-5\" \"value\":false\n");

        vc.ToggleAudioMute();

        _mockClient.Verify(x => x.Send("room_mic_level set mute 5 true\n"));
    }

    [Fact]
    public void BiampVolumeControl_SetLevel_UsesTheConfiguredIndex()
    {
        _mockClient.Setup(x => x.Send("room_mic_level get maxLevel 5\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":0.000000\n"));
        _mockClient.Setup(x => x.Send("room_mic_level get minLevel 5\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":-100.000000\n"));
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);

        vc.SetLevel(50);

        _mockClient.Verify(x => x.Send("room_mic_level set level 5 -50\n"));
    }

    [Fact]
    public void BiampVolumeControl_Construction_SubscribesAtTheConfiguredIndex()
    {
        _ = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);

        _mockClient.Verify(x => x.Send("room_mic_level subscribe level 5 AvCodersLevel-room_mic_level-5\n"));
        _mockClient.Verify(x => x.Send("room_mic_level subscribe mute 5 AvCodersMute-room_mic_level-5\n"));
    }
}