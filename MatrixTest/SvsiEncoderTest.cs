using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class SvsiEncoderTest
{
    private readonly string _sampleNetworkStatusResponse = "SVSI_NETSTATS:N1122A30037981\rNAME:Transmitter\rMAC:00:19:00:11:22:33\rIP:10.56.78.99\rNM:255.255.255.0\rGW:10.56.78.1\rSWVER:4/10/2018\rCHASSISID:mac e4:38:00:00:00:00\rSYSNAME:SwitchPro24PoE\rSYSDESCR:USW-Pro-24-PoE, 7.0.50.15613, Linux 3.6.5\rPORTID:local Port 19\rPORTDESCR:Port 19\rFPGAVER:7/18/2017\r";
    private readonly string _sampleStatusResponse = "SVSI_TXGEN2:N1122A30037981\rNAME:Transmitter\rMAC:00:19:00:11:22:33\rIP:10.56.78.99\rNM:255.255.252.0\rGW:10.1.0.1\rIPTRIAL:0\rIPMODE:DHCP\rSWVER:4/10/2018\rWEBVER:1523376226\rUPDATE:0\rUPDTRY:0\rUPDFAILED:0\rMEDIAPORT0:on\rMEDIAPORT1:off\rDIVASEN:0\rDIVASIP:0.0.0.0\rMASSEREN:0\rMASSERIP:0.0.0.0\rsecurePortsOnly:off\rdiscoveryIP:239.254.12.16\renableDiscoveryPackets:on\rdiscoveryIntervalSec:10\rdiscoveryPort:50019\rBAUD:9600\rSNUMB:8\rSPAR:none\rSP2S:1\rMODE:720p60.mode\rPORTSD1:no\rGARP:0\rGARPINT:50\rUNSOLST:1\rUNSOLSTINT:10\rID:0\rDVICEVTDLY:1\rDVIDEVTDLY:1\rUSERMCMODE:off\rUSERMCIP:0.0.0.0\rLPDISKSPACE:78997504\rSERSRCIP:10.1.3.199\rSEROPEN:1\rSLCK:0\rHTTPS:0\rLINEIN:unbal\rMUTE:0\rSTREAM:2\rSAMPLE:48000\rAUDIODELAY:0\rCLRSPCCOR:auto\rHPNONSUP:off\rHDMIAUDIO:auto\rUNCOMP:off\rLIVEAUDIOHP:off\rEXTREMEQUAL:off\rQUALITY:100\rMOTQUAL:100\rSCALERBYPASS:yes\rPLAYMODE:live\rPLAYLIST:1\rOUTBW:48689280\rOUTBWMBS:371.4\rDVIINPUT:disconnected\rDVIPASSTHR:connected\rPTHDMIAUDIO:auto\rPTYUVOUT:off\rPTSIMDVIDET:off\rPTNEGSYNC:off\rAGAINL:0\rAGAINR:0\rCPC:allowed\rCISPROT:not-protected\rINPUTRES:0x0\rFPGAVER:7/18/2017\rGEN1OUTPUTMODE:off\rSOGWindow:16\rCPCENC:1\rAUDENC:0\rLPWR:0\rVMUTE:0\rVMUTEPT:0\rVSRC:0\rVSTS:0\rVDET:0\rHDET:0\rIMC:0";
    private readonly SvsiEncoder _svsiEncoder;
    private readonly Mock<TcpClient> _mockClient;
    private readonly Mock<InputStatusChangedHandler> _mockInputStatusChangedHandler;

    public SvsiEncoderTest()
    {
        _mockClient = new("foo", SvsiBase.DefaultPort, "bar");
        _svsiEncoder = new(_mockClient.Object);
        _mockInputStatusChangedHandler = new();
        _svsiEncoder.InputStatusChangedHandlers += _mockInputStatusChangedHandler.Object;
    }

    [Fact]
    public void ResponseHandler_ProcessesNetworkData()
    {
        _mockClient.Object.ResponseHandlers!.Invoke(_sampleNetworkStatusResponse);
        
        Assert.Equal("Transmitter", _svsiEncoder.StatusDictionary["NAME"]);
        Assert.Equal("10.56.78.99", _svsiEncoder.StatusDictionary["IP"]);
        Assert.Equal("00:19:00:11:22:33", _svsiEncoder.StatusDictionary["MAC"]);
    }

    [Fact]
    public void ResponseHandler_ProcessesStatusData()
    {
        _mockClient.Object.ResponseHandlers!.Invoke(_sampleStatusResponse);
        
        Assert.Equal("Transmitter", _svsiEncoder.StatusDictionary["NAME"]);
        Assert.Equal("10.56.78.99", _svsiEncoder.StatusDictionary["IP"]);
        Assert.Equal("00:19:00:11:22:33", _svsiEncoder.StatusDictionary["MAC"]);
        Assert.Equal((uint)2, _svsiEncoder.StreamNumber);
        _mockInputStatusChangedHandler.Verify(x => x.Invoke(1, ConnectionStatus.Disconnected, "0x0"));
    }

    [Fact]
    public void ResponseHandler_HandlesNoInput()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("INPUTRES:0x0\r");
        _mockClient.Object.ResponseHandlers!.Invoke("DVIINPUT:disconnected\r");
        
        _mockInputStatusChangedHandler.Verify(x => x.Invoke(1, ConnectionStatus.Disconnected, "0x0"));
    }

    [Fact]
    public void ResponseHandler_HandlesInput()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("INPUTRES:1920x1080\r");
        _mockClient.Object.ResponseHandlers!.Invoke("DVIINPUT:connected\r");
        
        _mockInputStatusChangedHandler.Verify(x => x.Invoke(1, ConnectionStatus.Connected, "1920x1080"));
    }
    
}