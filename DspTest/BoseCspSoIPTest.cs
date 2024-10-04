using AVCoders.Core;

namespace AVCoders.Dsp.Tests;

public class BoseCspSoIPTest
{
    private readonly BoseCspSoIP _dsp;
    private readonly Mock<TcpClient> _mockClient = new("foo", BoseCspSoIP.DefaultPort);
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();
    
    private const string GainName = "Gain";
    private const string MuteName = "Gain";
    private const string StringName = "String";

    public BoseCspSoIPTest()
    {
        _dsp = new BoseCspSoIP(_mockClient.Object, 100);

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
    [InlineData(0, $"SA\"{GainName}\">1=-60.5\r")]
    [InlineData(100, $"SA\"{GainName}\">1=12\r")]
    [InlineData(50, $"SA\"{GainName}\">1=-24\r")]
    [InlineData(13, $"SA\"{GainName}\">1=-51\r")]
    public void SetLevel_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        _dsp.SetLevel(GainName, percentage);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}