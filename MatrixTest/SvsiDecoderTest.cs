using AVCoders.Core;
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
    private readonly Mock<TcpClient> _mockClient;
    private readonly Mock<SyncInfoHandler> _mockOutputStatusChangedHandler;
    
    
    public SvsiDecoderTest()
    {
        _mockClient = new("foo", SvsiBase.DefaultPort, "bar");
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
        _mockOutputStatusChangedHandler.Verify(x => x.Invoke(ConnectionStatus.Connected, "1080p60"));
    }

    [Fact]
    public void ResponseHandler_IgnoresInvalidResponses()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("ND_V\r");
    }

    [Fact]
    public void SetInput_AcceptsAStreamId()
    {
        _svsiDecoder.SetInput(1);
        
        _mockClient.Verify(x => x.Send("set:1\r"));
    }

    [Fact]
    public void SetInput_AcceptsAnEncoder()
    {
        _svsiDecoder.SetInput(new TestSvsiEncoder(_mockClient.Object));
        
        _mockClient.Verify(x => x.Send("set:3\r"));
        _mockClient.Verify(x => x.Send("live\r"));
    }

    [Fact]
    public void SetPlaylist_AcceptsAStreamId()
    {
        _svsiDecoder.SetPlaylist(1);
        
        _mockClient.Verify(x => x.Send("local:1\r"));
    }

    [Theory]
    [InlineData(MuteState.On, "mute\r")]
    [InlineData(MuteState.Off, "unmute\r")]
    public void SetAudioMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _svsiDecoder.SetAudioMute(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(MuteState.On, "dviOff\r")]
    [InlineData(MuteState.Off, "dviOn\r")]
    public void SetVideoMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _svsiDecoder.SetVideoMute(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}