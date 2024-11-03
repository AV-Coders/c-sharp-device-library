using AVCoders.Core;
using Xunit.Abstractions;

namespace AVCoders.Dsp.Tests;

public class BiampTtpTest
{
    private readonly ITestOutputHelper _testOutputHelper;
    private BiampTtp _dsp;
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();
    private readonly Mock<TcpClient> _mockClient = new("foo", BiampTtp.DefaultPort, "bar");
    private const string GainName = "Gain";
    private const string MuteName = "Mute";
    private const string StringName = "String";

    public BiampTtpTest(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _dsp = new BiampTtp(_mockClient.Object, 100);
        
        _mockClient.Setup(x => x.Send($"{GainName} get maxLevel 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":12.000000\n"));
        _mockClient.Setup(x => x.Send($"{GainName} get minLevel 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":0.000000\n"));
        _mockClient.Setup(x => x.Send($"{GainName} get level 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":3.000000\n"));
        
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);
        
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
}