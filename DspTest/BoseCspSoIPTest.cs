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
    
    private const string GainName = "Gain";
    private const string MuteName = "Gain";
    private const string StringName = "String";

    public BoseCspSoIPTest()
    {
        _dsp = new BoseCspSoIP(_mockClient.Object, "Test", 100);

        _mockClient.Setup(x => x.Send($"GA\"{GainName}\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName}\">1=0\r"));
        _mockClient.Setup(x => x.Send($"GA\"{MuteName}\">3\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{MuteName}\">3=F\r"));
        _mockClient.Setup(x => x.Send($"GA\"{StringName}\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName}\">1=2\r"));
        
        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Fact]
    public void HandleResponse_StoresThePercentage()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName}\">1=12\r");

        Assert.Equal(100, _dsp.GetLevel(GainName));
    }
    
    [Theory]
    [InlineData($"GA\"{MuteName}\">2=F\r", MuteState.Off)]
    [InlineData($"GA\"{MuteName}\">2=O\r", MuteState.On)]
    public void HandleResponse_StoresTheMuteState(string response, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(expectedMuteState, _dsp.GetAudioMute(MuteName));
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
}