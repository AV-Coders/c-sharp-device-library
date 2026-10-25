using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

[Collection("LoggerFactory")]
public class NavigatorTest
{
    private readonly Navigator _navigator;
    private readonly Mock<SshClient> _mockSshClient = TestFactory.CreateSshClient();
    public const string EscapeHeader = "\x1b";

    public NavigatorTest()
    {
        _navigator = new Navigator("Foo", _mockSshClient.Object);
    }

    [Fact]
    public void ResponseHandler_ForwardsToTheDevice()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("10.1.3.207", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec");
        
        mockResponseHandler.Verify(x => x.Invoke("VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec"));
    }

    [Fact]
    public void ResponseHandler_ForwardsTieResponsesToTheOutput()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("0671o", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("Out671 In663 All");

        mockResponseHandler.Verify(x => x.Invoke("In663 All"));
    }

    [Fact]
    public void ResponseHandler_ForwardsUntieResponsesToTheOutput()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("0661o", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("Out661 In00 All");

        mockResponseHandler.Verify(x => x.Invoke("In00 All"));
    }

    [Fact]
    public void ResponseHandler_IgnoresUnregisteredTieResponses()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Out999 In1 All");
        _mockSshClient.Object.ResponseHandlers!.Invoke("In0i Usb");
        _mockSshClient.Object.ResponseHandlers!.Invoke("E10");
    }

    [Fact]
    public void ResponseHandler_AppliesDevicePresenceUpdates()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _mockSshClient.Object.ResponseHandlers!.Invoke("DevpP*671o*0");
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);

        _mockSshClient.Object.ResponseHandlers!.Invoke("DevpP*671o*1");
        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public void ConnectionState_SeedsSystemState()
    {
        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}3CV\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}Inventory*I*RPRT\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}Inventory*O*RPRT\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}Ties*A*RPRT\r"));
    }

    [Fact]
    public void ResponseHandler_AppliesInventoryReports()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum3");

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*112");
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*111");
        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public void ResponseHandler_ForwardsTieReportRowsToTheOutput()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("0671o", mockResponseHandler.Object);

        _mockSshClient.Object.ResponseHandlers!.Invoke("671\t663\t661");
        mockResponseHandler.Verify(x => x.Invoke("In663 Vid"));
        mockResponseHandler.Verify(x => x.Invoke("In661 Aud"));

        _mockSshClient.Object.ResponseHandlers!.Invoke("671\t---\t----");
        mockResponseHandler.Verify(x => x.Invoke("In0 Vid"));
        mockResponseHandler.Verify(x => x.Invoke("In0 Aud"));
    }

    [Fact]
    public void DeviceNumberDiscovery_QueriesEndpointState()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}P*671oDEVP\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}671%\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}671$\r"));
    }

    [Fact]
    public void DeviceNumberDiscovery_ARepeatedReplyKeepsTheDeviceRegistered()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _mockSshClient.Object.ResponseHandlers!.Invoke("Out671 In5 All");

        Assert.Equal("5", decoder.StreamAddress);
    }

    [Fact]
    public void DeviceNumberDiscovery_AChangedReplyMovesTheRegistration()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum672");

        _mockSshClient.Object.ResponseHandlers!.Invoke("Out671 In5 All");
        Assert.Equal(string.Empty, decoder.StreamAddress);

        _mockSshClient.Object.ResponseHandlers!.Invoke("Out672 In6 All");
        Assert.Equal("6", decoder.StreamAddress);
    }

    [Fact]
    public async Task RegisterDevice_ConcurrentWithResponses_KeepsEveryRegistration()
    {
        const int registeringThreads = 4;
        const int devicesPerThread = 5000;
        const int deviceCount = registeringThreads * devicesPerThread;
        const int outputCount = 9999;
        var forwarded = new bool[deviceCount];
        using var receiverStarted = new ManualResetEventSlim();
        var registering = Task.WhenAll(Enumerable.Range(0, registeringThreads).Select(thread => Task.Run(() =>
        {
            receiverStarted.Wait();
            for (var device = thread * devicesPerThread; device < (thread + 1) * devicesPerThread; device++)
            {
                var index = device;
                _navigator.RegisterDevice($"10.0.{device}", _ => forwarded[index] = true);
            }
        })));
        var receiving = Task.Run(() =>
        {
            receiverStarted.Set();
            for (var response = 0; !registering.IsCompleted; response++)
            {
                var output = $"{response % outputCount + 1:D4}o";
                _navigator.RegisterDevice(output, _ => { });
                _mockSshClient.Object.ResponseHandlers!.Invoke($"{{10.0.{response % deviceCount}}}Ping");
                _navigator.DeregisterDevice(output);
            }
        });
        await Task.WhenAll(registering, receiving).WaitAsync(TimeSpan.FromSeconds(30));

        Array.Clear(forwarded);
        for (var device = 0; device < deviceCount; device++)
            _mockSshClient.Object.ResponseHandlers!.Invoke($"{{10.0.{device}}}Ping");

        Assert.All(forwarded, Assert.True);
    }

    [Fact]
    public void ResponseHandler_ACallbackCanRegisterADeviceFromAnotherThread()
    {
        var registered = false;
        _navigator.RegisterDevice("10.1.3.207", _ =>
            registered = Task.Run(() => _navigator.RegisterDevice("10.1.3.208", _ => { })).Wait(TimeSpan.FromSeconds(5)));

        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Ping");

        Assert.True(registered);
    }

    private int CountSent(string message) => _mockSshClient.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(SshClient.Send) && invocation.Arguments[0] as string == message);

    [Fact]
    public async Task ConnectionState_InitialisesTheEndpointsWithoutBlocking()
    {
        _ = new NavDecoder("Decoder", "10.1.3.207", _navigator)
        {
            NavConnectedSettle = TimeSpan.FromSeconds(1)
        };
        var verboseMode = $"{{10.1.3.207:{EscapeHeader}3CV\r}}\r";
        var deviceNumberQuery = $"{{10.1.3.207:{EscapeHeader}DNUM\r}}\r";
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 1, "The first poll never ran");

        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        Assert.Equal(0, CountSent(verboseMode));
        await Wait.UntilAsync(() => CountSent(verboseMode) == 1, "The endpoint was never put in verbose mode");
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 2, "The poll never restarted");
    }

    [Fact]
    public async Task ConnectionState_AFailedInitialisation_DoesNotStopTheNextOne()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator)
        {
            NavConnectedSettle = TimeSpan.Zero
        };
        var verboseMode = $"{{10.1.3.207:{EscapeHeader}3CV\r}}\r";
        var deviceNumberQuery = $"{{10.1.3.207:{EscapeHeader}DNUM\r}}\r";
        _mockSshClient.SetupSequence(client => client.Send(verboseMode)).Throws(new IOException("Oh No!")).Pass();
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 1, "The first poll never ran");

        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 2, "The poll never restarted");
        Assert.Equal(2, CountSent(verboseMode));
        Assert.Single(decoder.Errors);
    }

    [Fact]
    public void RouteAv_SendsTheCommand()
    {
        _navigator.RouteAV(1, 101);
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}1*101!\r"));
    }

    [Fact]
    public void RouteAv_TracksTheDesiredRouteOnTheDecoder()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteAV(5, 671);
        Assert.Equal(5, decoder.DesiredVideoInput);
        Assert.Equal(5, decoder.DesiredAudioInput);
    }

    [Fact]
    public void RouteVideo_TracksTheDesiredRouteOnTheDecoder()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteVideo(5, 671);
        Assert.Equal(5, decoder.DesiredVideoInput);
        Assert.Null(decoder.DesiredAudioInput);
    }

    [Fact]
    public void RouteAudio_TracksTheDesiredRouteOnTheDecoder()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteAudio(5, 671);
        Assert.Null(decoder.DesiredVideoInput);
        Assert.Equal(5, decoder.DesiredAudioInput);
    }

    [Fact]
    public void RouteAudio_SendsTheCommand()
    {
        _navigator.RouteAudio(111, 101);
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}111*101$\r"));
    }

    [Fact]
    public void RouteVideo_SendsTheCommand()
    {
        _navigator.RouteVideo(123, 121);
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}123*121%\r"));
    }

    private (Navigator Navigator, ScopeCapturingLoggerFactory Logs) CreateWithCapturedLogs()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (new Navigator("Logged navigator", _mockSshClient.Object), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void VerifyNoUsbCommandSent() =>
        _mockSshClient.Verify(x => x.Send(It.Is<string>(message => message.EndsWith("^\r"))), Times.Never);

    [Fact]
    public void RouteUsb_WithAnUnknownHostNumber_SendsNothingAndLogs()
    {
        var (navigator, logs) = CreateWithCapturedLogs();
        var encoder = new NavEncoder("Encoder", "10.1.3.201", navigator);
        var decoder = new NavDecoder("Decoder", "10.1.3.207", navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        navigator.RouteUsb(encoder, decoder);

        VerifyNoUsbCommandSent();
        Assert.Contains(logs.Logs, log => log.Message == "Not requesting a USB route from Encoder as its device number is 0");
    }

    [Fact]
    public void RouteUsb_WithAnUnknownDeviceNumber_SendsNothingAndLogs()
    {
        var (navigator, logs) = CreateWithCapturedLogs();
        var encoder = new NavEncoder("Encoder", "10.1.3.201", navigator);
        var decoder = new NavDecoder("Decoder", "10.1.3.207", navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.201}Dnum5");

        navigator.RouteUsb(encoder, decoder);

        VerifyNoUsbCommandSent();
        Assert.Contains(logs.Logs, log => log.Message == "Not requesting a USB route to Decoder as its device number is 0");
    }

    [Fact]
    public void RouteUsb_SendsTheCommand()
    {
        var encoder = new NavEncoder("Encoder", "10.1.3.201", _navigator);
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.201}Dnum5");
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteUsb(encoder, decoder);

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}5i*671o^\r"));
    }

    [Fact]
    public void DerouteUsb_WithAnUnknownDeviceNumber_SendsNothingAndLogs()
    {
        var (navigator, logs) = CreateWithCapturedLogs();
        var decoder = new NavDecoder("Decoder", "10.1.3.207", navigator);

        navigator.DerouteUsb(decoder);

        VerifyNoUsbCommandSent();
        Assert.Contains(logs.Logs, log => log.Message == "Not requesting a USB deroute of Decoder as its device number is 0");
    }

    [Fact]
    public void DerouteUsb_SendsTheCommand()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.DerouteUsb(decoder);

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}0i*671o^\r"));
    }
}