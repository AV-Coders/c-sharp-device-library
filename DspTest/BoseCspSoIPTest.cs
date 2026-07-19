using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Dsp.Tests;

public class BoseCspSoIPTest
{
    private readonly BoseCspSoIP _dsp;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();

    private const string GainName = "Fitness";
    private const string MuteName = "Fitness";
    private const string StringName = "Source";

    public BoseCspSoIPTest()
    {
        // A long poll interval so the background poll worker never runs during a test.
        _dsp = new BoseCspSoIP(_mockClient.Object, "Test", 500000);

        _mockClient.Setup(x => x.Send($"GA\"{GainName} Gain\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=0\r"));
        _mockClient.Setup(x => x.Send($"GA\"{MuteName} Gain\">2\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{MuteName} Gain\">2=F\r"));
        _mockClient.Setup(x => x.Send($"GA\"{StringName} Selector\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName} Selector\">1=2\r"));

        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Fact]
    public void HandleResponse_StoresThePercentage()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        Assert.Equal(100, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData($"GA\"{MuteName} Gain\">2=F\r", MuteState.Off)]
    [InlineData($"GA\"{MuteName} Gain\">2=O\r", MuteState.On)]
    public void HandleResponse_StoresTheMuteState(string response, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(expectedMuteState, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void HandleResponse_StoresTheStringValue()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName} Selector\">1=3\r");

        Assert.Equal("3", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_IgnoresUnknownControls()
    {
        var levelBefore = _dsp.GetLevel(GainName);

        _mockClient.Object.ResponseHandlers!.Invoke("GA\"Somewhere else Gain\">1=12\r");

        Assert.Equal(levelBefore, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData(0, $"SA\"{GainName} Gain\">1=-60.5\r")]
    [InlineData(100, $"SA\"{GainName} Gain\">1=12\r")]
    [InlineData(50, $"SA\"{GainName} Gain\">1=-24\r")]
    [InlineData(13, $"SA\"{GainName} Gain\">1=-51\r")]
    public void SetLevel_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        _dsp.SetLevel(GainName, percentage);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData("1", $"SA\"{GainName} Selector\">1=1\r")]
    [InlineData("2", $"SA\"{GainName} Selector\">1=2\r")]
    [InlineData("3", $"SA\"{GainName} Selector\">1=3\r")]
    [InlineData("4", $"SA\"{GainName} Selector\">1=4\r")]
    public void SetValue_SendsTheCommand(string source, string expectedCommand)
    {
        _dsp.SetValue(GainName, source);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public async Task Poll_QueriesEachControlWithItsFullDeviceName()
    {
        var connectionStateField = typeof(CommunicationClient)
            .GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic);
        connectionStateField!.SetValue(_mockClient.Object, ConnectionState.Connected);

        var poll = typeof(BoseCspSoIP).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic);
        await (Task)poll!.Invoke(_dsp, [CancellationToken.None])!;

        // Once from AddControl in the constructor, once from Poll.
        _mockClient.Verify(x => x.Send($"GA\"{GainName} Gain\">1\r"), Times.Exactly(2));
        _mockClient.Verify(x => x.Send($"GA\"{MuteName} Gain\">2\r"), Times.Exactly(2));
        _mockClient.Verify(x => x.Send($"GA\"{StringName} Selector\">1\r"), Times.Exactly(2));
    }
}
