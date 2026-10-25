using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

public class TestSvsiEncoder : SvsiEncoder
{
    public TestSvsiEncoder(TcpClient communicationClient) : base("dummy", communicationClient)
    {
        StreamId = 3;
    }
    
}

public class SvsiDecoderTest
{
    private readonly string _sampleStatusResponse =
        "SVSI_RXGEN2:N1222A30033627\rNAME:Receiver\rMAC:00:19:00:11:22:33\rIP:10.1.30.197\rNM:255.255.252.0\rGW:10.1.0.1\rIPTRIAL:0\rIPMODE:DHCP\rSWVER:4/3/2017\rWEBVER:1500561028\rUPDATE:0\rUPDTRY:0\rUPDFAILED:0\rMEDIAPORT0:on\rMEDIAPORT1:off\rDIVASEN:0\rDIVASIP:0.0.0.0\rMASSEREN:0\rMASSERIP:0.0.0.0\rsecurePortsOnly:off\rdiscoveryIP:239.254.12.16\renableDiscoveryPackets:on\rdiscoveryIntervalSec:10\rdiscoveryPort:50019\rBAUD:9600\rSNUMB:8\rSPAR:none\rSP2S:1\rMODE:1080p60.mode\rPORTSD1:no\rGARP:0\rGARPINT:50\rUNSOLST:1\rUNSOLSTINT:10\rID:0\rDVICEVTDLY:1\rDVIDEVTDLY:1\rUSERMCMODE:off\rUSERMCIP:0.0.0.0\rLPDISKSPACE:87336960\rSERSRCIP:0.0.0.0\rSEROPEN:0\rSLCK:0\rHTTPS:0\rLINEOUTVOL_L:100\rLINEOUTVOL_R:100\rMUTE:0\rSTREAM:2\rSTREAMAUDIO:0\rSCALERBYPASS:no\rPLAYMODE:live\rPLAYLIST:1\rHDMIAUDIO:auto\rLIVEAUDIOLP:off\rYUVOUT:off\rGEN1COMP:auto\rSIMDVIDET:off\rFRAMEHOLD:off\rVIDOFFNOSTRM:off\rNEGSYNC:off\rDVIOFF:off\rDVISTATUS:connected\rINPUTRES:1280x720\rFPGAVER:8/25/2016\rSTRMCAST:0\rNEEDVSTRM:1\rND_MRRQ:10.1.3.58\rND_MRRQ_CHG:1\rND_A_DROP:0\rND_A_DROP1S:0\rND_V_DROP:0\rND_V_DROP1S:0\rND_F_DROP:0\rND_F_DROP1S:0\rDM_D_EN:auto\rDM_D_IEN:off\rDM_A_EN:on\rDM_A_SRC:0\rDM_CGAIN:42\rDM_FGAIN:60\rDM_SLGAIN:-42\rDM_SRGAIN:42\rFCPC:on\rLPWR:0\rVMUTE:0\r";
    private readonly SvsiDecoder _svsiDecoder;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<SyncInfoHandler> _mockOutputStatusChangedHandler;
    
    
    public SvsiDecoderTest()
    {
        _svsiDecoder = new("Test", _mockClient.Object);
        _mockOutputStatusChangedHandler = new();
        _svsiDecoder.OutputStatusChangedHandlers += _mockOutputStatusChangedHandler.Object;
    }

    [Fact]
    public void ResponseHandler_ProcessesStatusData()
    {
        _mockClient.Object.ResponseHandlers!.Invoke(_sampleStatusResponse);
        
        Assert.Equal("Receiver", _svsiDecoder.StatusDictionary["NAME"]);
        Assert.Equal("10.1.30.197", _svsiDecoder.StatusDictionary["IP"]);
        Assert.Equal("00:19:00:11:22:33", _svsiDecoder.StatusDictionary["MAC"]);
        _mockOutputStatusChangedHandler.Verify(x => x.Invoke(ConnectionState.Connected, "1080p60", HdcpStatus.Unknown));
    }

    [Fact]
    public void ResponseHandler_IgnoresInvalidResponses()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("ND_V\r");
    }

    private List<string> SentMessages() => _mockClient.Invocations
        .Where(invocation => invocation.Method.Name == nameof(TcpClient.Send) && invocation.Arguments[0] is string)
        .Select(invocation => (string)invocation.Arguments[0])
        .ToList();

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    [Fact]
    public async Task SetInput_AcceptsAStreamId()
    {
        _svsiDecoder.SetInput(1);

        await Wait.UntilAsync(() => SentMessages().Contains("set:1\r"), "set:1 was never sent");
    }

    [Fact]
    public async Task SetInput_AcceptsAnEncoder()
    {
        _svsiDecoder.SetInput(new TestSvsiEncoder(_mockClient.Object));

        await Wait.UntilAsync(() => SentMessages().Contains("live\r"), "live was never sent");
        Assert.Contains("set:3\r", SentMessages());
    }

    [Fact]
    public async Task SetInput_ReturnsBeforeTheLiveCommandAndSendsItAfterTheStreamId()
    {
        _svsiDecoder.SetInput(1);

        Assert.DoesNotContain("live\r", SentMessages());
        await Wait.UntilAsync(() => SentMessages().Contains("live\r"), "live was never sent");
        Assert.True(SentMessages().IndexOf("set:1\r") < SentMessages().IndexOf("live\r"));
    }

    [Fact]
    public async Task SetInput_SendsEachRequestInTheOrderItWasMade()
    {
        _svsiDecoder.SetInput(1);
        _svsiDecoder.SetInput(2);

        await Wait.UntilAsync(() => SentMessages().Count(message => message == "live\r") == 2, "live was never sent twice");
        Assert.Equal(["set:1\r", "live\r", "set:2\r", "live\r"],
            SentMessages().Where(message => message != "\r"));
    }

    [Fact]
    public void SetAudioMute_Unknown_SendsNothing()
    {
        _svsiDecoder.SetAudioMute(MuteState.Unknown);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ResponseHandler_SkipsANonNumericStreamId()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("STREAM:abc\rMODE:1080p60.mode\rDVISTATUS:connected\r");

        Assert.Equal(string.Empty, _svsiDecoder.StreamAddress);
        _mockOutputStatusChangedHandler.Verify(x => x.Invoke(ConnectionState.Connected, "1080p60", HdcpStatus.Unknown));
    }

    [Fact]
    public void ResponseHandler_SetsTheStreamAddressOnTheFirstReport()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("STREAM:0\r");

        Assert.Equal("0", _svsiDecoder.StreamAddress);
    }

    [Fact]
    public void ResponseHandler_KeepsALineSplitAcrossReceives()
    {
        var split = _sampleStatusResponse.IndexOf("STREAM:2", StringComparison.Ordinal) + 4;

        _mockClient.Object.ResponseHandlers!.Invoke(_sampleStatusResponse[..split]);
        _mockClient.Object.ResponseHandlers!.Invoke(_sampleStatusResponse[split..]);

        Assert.Equal("2", _svsiDecoder.StatusDictionary["STREAM"]);
        Assert.Equal((uint)2, _svsiDecoder.StreamId);
        Assert.False(_svsiDecoder.StatusDictionary.ContainsKey("AM"));
    }

    [Fact]
    public void ResponseHandler_DropsAPartialLineWhenTheConnectionDrops()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("STREAM:9");
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Disconnected);

        _mockClient.Object.ResponseHandlers!.Invoke("\r");

        Assert.False(_svsiDecoder.StatusDictionary.ContainsKey("STREAM"));
    }

    [Fact]
    public void Constructor_TakesTheStateOfAnAlreadyConnectedClient()
    {
        SetConnection(ConnectionState.Connected);

        var decoder = new SvsiDecoder("Second Decoder", _mockClient.Object);

        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public async Task SetAudioInput_AcceptsAStreamId()
    {
        _svsiDecoder.SetAudioInput(1);

        await Wait.UntilAsync(() => SentMessages().Contains("seta:1\r"), "seta:1 was never sent");
    }

    [Fact]
    public async Task SetAudioInput_AcceptsAnEncoder()
    {
        _svsiDecoder.SetAudioInput(new TestSvsiEncoder(_mockClient.Object));

        await Wait.UntilAsync(() => SentMessages().Contains("seta:3\r"), "seta:3 was never sent");
    }

    [Fact]
    public async Task SetPlaylist_AcceptsAStreamId()
    {
        _svsiDecoder.SetPlaylist(1);

        await Wait.UntilAsync(() => SentMessages().Contains("local:1\r"), "local:1 was never sent");
    }

    [Theory]
    [InlineData(MuteState.On, "mute\r")]
    [InlineData(MuteState.Off, "unmute\r")]
    public async Task SetAudioMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _svsiDecoder.SetAudioMute(state);

        await Wait.UntilAsync(() => SentMessages().Contains(expectedCommand), $"{expectedCommand} was never sent");
    }

    [Theory]
    [InlineData(MuteState.On, "dviOff\r")]
    [InlineData(MuteState.Off, "dviOn\r")]
    public async Task SetVideoMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _svsiDecoder.SetVideoMute(state);

        await Wait.UntilAsync(() => SentMessages().Contains(expectedCommand), $"{expectedCommand} was never sent");
    }

    [Fact]
    public async Task SetInput_ThenSetVideoMute_KeepsTheOrder()
    {
        _svsiDecoder.SetInput(1);
        _svsiDecoder.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(() => SentMessages().Contains("dviOn\r"), "dviOn was never sent");
        Assert.Equal(["set:1\r", "live\r", "dviOn\r"], SentMessages().Where(message => message != "\r"));
    }

    [Fact]
    public async Task SetInput_AFailedSend_DoesNotStopLaterSends()
    {
        _mockClient.Setup(client => client.Send("set:1\r")).Throws(new IOException("Oh No!"));

        _svsiDecoder.SetInput(1);
        _svsiDecoder.SetInput(2);

        await Wait.UntilAsync(() => SentMessages().Contains("live\r"), "live was never sent");
        Assert.Equal(["set:1\r", "set:2\r", "live\r"], SentMessages().Where(message => message != "\r"));
        Assert.Single(_svsiDecoder.Errors);
    }
}