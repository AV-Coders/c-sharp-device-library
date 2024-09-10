using AVCoders.Core;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsTest
{
    
    public abstract class StubbedClient : IpComms
    {
        protected StubbedClient(string host, ushort port) : base(host, port){}

        public override void Send(string message){}

        public override void Send(byte[] bytes){}

        public override void SetPort(ushort port){}

        public override void SetHost(string host){}
        public new ConnectionState GetConnectionState() => ConnectionState.Connected;
    }
    
    private readonly Mock<StubbedClient> _mockClient = new("foo", (ushort)1);
    private readonly Mock<LogHandler> _logHandlers = new ();
    private readonly Mock<CommunicationStateHandler> _communicationStateHandlers = new ();
    private readonly Mock<VolumeLevelHandler> _outputVolumeLevelHandler = new ();
    private readonly Mock<MuteStateHandler> _outputMuteStateHandler = new ();
    private readonly Mock<MuteStateHandler> _microphoneMuteStateHandler = new ();
    
    private readonly CiscoRoomOs _codec;

    public CiscoRoomOsTest()
    {
        _codec = new CiscoRoomOs(_mockClient.Object, new CiscoRoomOsDeviceInfo("Test", "Xunit", "An Awesome Laptop", "012934"));
        _codec.LogHandlers += _logHandlers.Object;
        _codec.CommunicationStateHandlers += _communicationStateHandlers.Object;
        _codec.OutputVolume.VolumeLevelHandlers += _outputVolumeLevelHandler.Object;
        // _codec.OutputMute.MuteStateHandlers += _outputMuteStateHandler.Object;
        _codec.MicrophoneMute.MuteStateHandlers += _microphoneMuteStateHandler.Object;
    }

    [Fact]
    public void Module_RegistersAndSubscribes()
    {
        _mockClient.Object.ConnectionStateHandlers.Invoke(ConnectionState.Connected);
        new List<string> {
        "xFeedback register /Status/Standby",
        "xFeedback register /Event/UserInterface/Extensions/Event",
        "xFeedback register /Status/Call",
        "xFeedback register /Status/Audio/Volume",
        "xFeedback register /Status/Audio/Microphones",
        "xFeedback register /Status/Conference/Presentation",
        "xFeedback register /Event/UserInterface/Presentation/ExternalSource",
        "xFeedback register /Status/Conference/DoNotDisturb",
        "xFeedback register /Status/Conference/Call/AuthenticationRequest",
        "xFeedback register /Status/Conference/Call",
        "xFeedback register /Status/Cameras/SpeakerTrack",
        "xFeedback register /Status/Video/Selfview",
        "xFeedback register /Status/Video/Layout/CurrentLayouts/ActiveLayout",
        "xFeedback register /Event/Bookings",
        "xStatus Standby",
        "xStatus Call",
        "xStatus Audio Volume",
        "xStatus Audio Microphones",
        "xStatus Conference DoNotDisturb",
        "xStatus Conference Presentation",
        "xStatus Cameras SpeakerTrack status",
        "xStatus Video Selfview",
        "xStatus Video Layout CurrentLayouts ActiveLayout",
        "xStatus SIP Registration URI",
        }.ForEach(s => 
            _mockClient.Verify(x => x.Send($"{s}\r\n")));

        Assert.StartsWith("xCommand Peripherals Connect ID: AV-Coders-RoomOS-Module-", (string) _mockClient.Invocations[0].Arguments[0]);
    }

    [Fact]
    public void HeartbeatOkayUpdatesCommunicationState()
    {
        _mockClient.Object.ResponseHandlers.Invoke("*r PeripheralsHeartBeatResult (status=OK): \n");
        
        _communicationStateHandlers.Verify(x => x.Invoke(CommunicationState.Okay), Times.Once);
    }

    [Theory]
    [InlineData(50)]
    public void VolumeStatusResponse_UpdatesVolumeLevel(int volume)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"*s Audio Volume: {volume}\n");
        
        _outputVolumeLevelHandler.Verify(x => x.Invoke(volume));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(100)]
    public void SetOutputVolume_SendsTheCommand(int volume)
    {
        _codec.SetOutputVolume(volume);
        
        _mockClient.Verify(x=> x.Send($"xCommand Audio Volume Set Level: {volume}\r\n"));
    }

    [Theory]
    [InlineData("Off", MuteState.Off)]
    [InlineData("On", MuteState.On)]
    public void MuteStatusResponse_UpdatesMuteState(string response, MuteState expectedState)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"*s Audio Microphones Mute: {response}\n");
        
        _microphoneMuteStateHandler.Verify(x => x.Invoke(expectedState));
    }
    
}