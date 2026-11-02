using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class NavigatorTest
{
    private static readonly TimeSpan SettleStep = TimeSpan.FromSeconds(1);
    private readonly FakeTimeProvider _time = new();
    private readonly Navigator _navigator;
    private readonly Mock<SshClient> _mockSshClient = TestFactory.CreateSshClient();
    public const string EscapeHeader = "\x1b";

    public NavigatorTest()
    {
        _navigator = new Navigator("Foo", _mockSshClient.Object, _time);
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
        Mock<Action<string>> otherOutput = new Mock<Action<string>>();
        _navigator.RegisterDevice("0671o", otherOutput.Object);

        var exception = Record.Exception(() =>
        {
            _mockSshClient.Object.ResponseHandlers!.Invoke("Out999 In1 All");
            _mockSshClient.Object.ResponseHandlers!.Invoke("In0i Usb");
            _mockSshClient.Object.ResponseHandlers!.Invoke("E10");
        });

        Assert.Null(exception);
        otherOutput.Verify(x => x.Invoke(It.IsAny<string>()), Times.Never);
        Assert.Empty(_navigator.Errors);
        Assert.Empty(_navigator.GetIssues());
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
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum3");

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*112");
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*111");
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);

        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec");
        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public void ResponseHandler_TheFirstInventoryAfterConnecting_ConnectsOnlineEndpoints()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum3");
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*112");

        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*111");

        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public async Task AHungEndpointTheInventoryListsOnline_ReachesItsCriticalIssue()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        await Wait.UntilAsync(() => CountSent("{10.1.3.207:I}\r") == 1, "The first poll never ran");
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum1");

        for (var step = 0; step < 40 && !decoder.CommunicationClient.GetOngoingIssues().Any(); step++)
        {
            await Task.Run(() => _time.Advance(TimeSpan.FromSeconds(30)));
            _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*1");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.Equal(IssueSeverity.Critical, Assert.Single(decoder.CommunicationClient.GetOngoingIssues()).Severity);
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);
    }

    private DeviceDetail? Detail(string label) => _navigator.Details.SingleOrDefault(detail => detail.Label == label);

    [Fact]
    public void ResponseHandler_ShowsTheInventoryCounts()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*I*1101");
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*1220");

        Assert.Equal(new DeviceDetail("Encoders Online", "3"), Detail("Encoders Online"));
        Assert.Equal(new DeviceDetail("Encoders Offline", "0"), Detail("Encoders Offline"));
        Assert.Equal(new DeviceDetail("Decoders Online", "1"), Detail("Decoders Online"));
        Assert.Equal(new DeviceDetail("Decoders Offline", "2", DetailTone.Warning), Detail("Decoders Offline"));
        Assert.DoesNotContain(_navigator.GetOngoingIssues(), issue => issue.Key.StartsWith("inventory-read:"));

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*1110");

        Assert.Equal("3", Detail("Decoders Online")?.Value);
        Assert.Equal(new DeviceDetail("Decoders Offline", "0"), Detail("Decoders Offline"));
    }

    [Fact]
    public void ResponseHandler_RaisesAnIssueForAnUnrecognisedInventoryState()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*1x2");

        var issue = Assert.Single(_navigator.GetOngoingIssues(), issue => issue.Key.StartsWith("inventory-read:"));
        Assert.Equal("inventory-read:O", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains("Rprt*Inventory*O*1x2", issue.Message);
        Assert.Equal("1", Detail("Decoders Online")?.Value);
        Assert.Equal("1", Detail("Decoders Offline")?.Value);

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*112");

        Assert.DoesNotContain(_navigator.GetOngoingIssues(), issue => issue.Key.StartsWith("inventory-read:"));
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
        _ = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        var verboseMode = $"{{10.1.3.207:{EscapeHeader}3CV\r}}\r";
        var deviceNumberQuery = $"{{10.1.3.207:{EscapeHeader}DNUM\r}}\r";
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 1, "The first poll never ran");

        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        Assert.Equal(0, CountSent(verboseMode));
        await Wait.UntilAsync(_time, SettleStep, () => CountSent(verboseMode) == 1, "The endpoint was never put in verbose mode");
        await Wait.UntilAsync(_time, SettleStep, () => CountSent(deviceNumberQuery) == 2, "The poll never restarted");
    }

    [Fact]
    public async Task ConnectionState_AFailedInitialisation_DoesNotStopTheNextOne()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        var verboseMode = $"{{10.1.3.207:{EscapeHeader}3CV\r}}\r";
        var deviceNumberQuery = $"{{10.1.3.207:{EscapeHeader}DNUM\r}}\r";
        _mockSshClient.SetupSequence(client => client.Send(verboseMode)).Throws(new IOException("Oh No!")).Pass();
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 1, "The first poll never ran");

        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        await Wait.UntilAsync(_time, SettleStep, () => CountSent(deviceNumberQuery) == 2, "The poll never restarted");
        Assert.Equal(2, CountSent(verboseMode));
        Assert.Single(decoder.Errors);
    }

    [Fact]
    public async Task ConnectionState_TheEndpointsVerboseModeReply_StartsItsReads()
    {
        _ = new NavEncoder("Codec 1", "10.1.3.212", _navigator, _time);
        var verboseMode = $"{{10.1.3.212:{EscapeHeader}3CV\r}}\r";
        var deviceNumberQuery = $"{{10.1.3.212:{EscapeHeader}DNUM\r}}\r";
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 1, "The first poll never ran");
        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await Wait.UntilAsync(_time, SettleStep, () => CountSent(verboseMode) == 1, "The endpoint was never put in verbose mode");

        _mockSshClient.Object.ResponseHandlers!.Invoke(NavigatorCaptures.Court82CodecOneVerboseMode);

        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 2, "The reads didn't follow the verbose mode reply");
    }

    [Fact]
    public async Task ConnectionState_WithNoVerboseModeReply_StartsTheReadsAfterAGap()
    {
        _ = new NavEncoder("Codec 1", "10.1.3.212", _navigator, _time);
        var verboseMode = $"{{10.1.3.212:{EscapeHeader}3CV\r}}\r";
        var deviceNumberQuery = $"{{10.1.3.212:{EscapeHeader}DNUM\r}}\r";
        await Wait.UntilAsync(() => CountSent(deviceNumberQuery) == 1, "The first poll never ran");
        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await Wait.UntilAsync(_time, SettleStep, () => CountSent(verboseMode) == 1, "The endpoint was never put in verbose mode");

        _time.Advance(SettleStep);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, CountSent(deviceNumberQuery));

        await Wait.UntilAsync(_time, SettleStep, () => CountSent(deviceNumberQuery) == 2, "The reads never started");
    }

    [Fact]
    public void ResponseHandler_CountsPresentButNotConnectedEndpoints()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*1320");

        Assert.Equal(new DeviceDetail("Decoders Present, Not Connected", "1", DetailTone.Warning), Detail("Decoders Present, Not Connected"));
        Assert.Equal("1", Detail("Decoders Offline")?.Value);
        Assert.DoesNotContain(_navigator.GetOngoingIssues(), issue => issue.Key.StartsWith("inventory-read:"));

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*1120");

        Assert.Equal(new DeviceDetail("Decoders Present, Not Connected", "0"), Detail("Decoders Present, Not Connected"));
    }

    [Fact]
    public void ResponseHandler_PresentButNotConnected_DisconnectsTheEndpoint()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum2");

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*13");

        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);
    }

    [Fact]
    public void ResponseHandler_PresentButNotConnected_CountsWithTheOfflineDevices()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*32");

        Assert.Contains("(1, 2)", _navigator.GetOngoingIssues().Single(issue => issue.Key == "unclaimed-offline:o").Message);
    }

    [Fact]
    public void ResponseHandler_RaisesAnIssueForAStateOutsideZeroToThree()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*I*14");

        Assert.Contains("Rprt*Inventory*I*14", _navigator.GetOngoingIssues().Single(issue => issue.Key == "inventory-read:I").Message);
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
    public void DerouteUsb_SendsTheCommand()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.DerouteUsb(decoder);

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}0i*671o^\r"));
    }

    private static readonly TimeSpan PollStep = TimeSpan.FromSeconds(5);
    private const string EncoderInventoryQuery = $"{EscapeHeader}Inventory*I*RPRT\r";
    private const string DecoderInventoryQuery = $"{EscapeHeader}Inventory*O*RPRT\r";
    private const string RelayIssueKey = "endpoint-relay";
    private static readonly string[] Court82OfflineEncoders = ["10.1.3.205", "10.1.3.206", "10.1.3.207", "10.1.3.208"];
    private static readonly string[] Court82OfflineDecoders =
        ["10.1.3.218", "10.1.3.219", "10.1.3.220", "10.1.3.221", "10.1.3.222", "10.1.3.223", "10.1.3.224", "10.1.3.225", "10.1.3.228"];

    private void Receive(string response) => _mockSshClient.Object.ResponseHandlers!.Invoke(response);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockSshClient.Object, state);

    private Issue? OngoingIssue(string key) => _navigator.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    private void RegisterCourt82Encoders(IEnumerable<string> offline, bool includeCodecTwo = true)
    {
        foreach (var address in offline)
            _ = new NavEncoder($"Encoder {address}", address, _navigator, _time);
        _ = new NavEncoder("QuadView", "10.1.3.210", _navigator, _time);
        _ = new NavEncoder("Annotated", "10.1.3.211", _navigator, _time);
        _ = new NavEncoder("Codec 1", "10.1.3.212", _navigator, _time);
        Receive(NavigatorCaptures.Court82QuadViewDeviceNumber);
        Receive(NavigatorCaptures.Court82AnnotatedDeviceNumber);
        Receive(NavigatorCaptures.Court82CodecOneDeviceNumber);
        if (!includeCodecTwo)
            return;
        _ = new NavEncoder("Codec 2", "10.1.3.213", _navigator, _time);
        Receive(NavigatorCaptures.Court82CodecTwoDeviceNumber);
    }

    private void RegisterCourt82Decoders(IEnumerable<string> offline)
    {
        foreach (var address in offline)
            _ = new NavDecoder($"Decoder {address}", address, _navigator, _time);
        _ = new NavDecoder("Codec Input", "10.1.3.226", _navigator, _time);
        _ = new NavDecoder("Lumens In", "10.1.3.227", _navigator, _time);
        Receive(NavigatorCaptures.Court82CodecInputDeviceNumber);
        Receive(NavigatorCaptures.Court82LumensInDeviceNumber);
    }

    [Fact]
    public async Task InventoryPoll_RepeatsTheInventoryQueriesEvery30Seconds()
    {
        SetConnection(ConnectionState.Connected);
        Assert.Equal(1, CountSent(EncoderInventoryQuery));

        await Wait.UntilAsync(_time, PollStep, () => CountSent(DecoderInventoryQuery) == 3, "The inventory was never polled");

        Assert.Equal(3, CountSent(EncoderInventoryQuery));
        Assert.Equal(1, CountSent($"{EscapeHeader}Ties*A*RPRT\r"));
        Assert.True(_time.GetUtcNow() - _time.Start >= TimeSpan.FromSeconds(60));
    }

    private async Task AnsweringCodecOne()
    {
        _ = new NavEncoder("Codec 1", "10.1.3.212", _navigator, _time);
        await Wait.UntilAsync(() => CountSent("{10.1.3.212:I}\r") == 1, "The first poll never ran");
        Receive(NavigatorCaptures.Court82CodecOneDeviceNumber);
        for (var reply = 0; reply < 3; reply++)
            Receive(NavigatorCaptures.Court82CodecOneStatus);
    }

    private async Task RaiseTheRelayIssue()
    {
        SetConnection(ConnectionState.Connected);
        await AnsweringCodecOne();
        for (var forward = 0; forward < 6; forward++)
            _navigator.SendCommandToDevice("10.1.3.212", "I");
        await Wait.UntilAsync(_time, PollStep, () => OngoingIssue(RelayIssueKey) != null, "The relay issue was never raised");
    }

    [Fact]
    public async Task UnansweredForwards_RaiseTheRelayIssue()
    {
        await RaiseTheRelayIssue();

        var issue = OngoingIssue(RelayIssueKey)!;
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Contains("the last 6 commands", issue.Message);
        Assert.Contains("{10.1.3.212:I}", issue.Message);
    }

    [Fact]
    public async Task UnansweredForwards_FiveAreNotEnoughForTheRelayIssue()
    {
        SetConnection(ConnectionState.Connected);
        await AnsweringCodecOne();
        for (var forward = 0; forward < 5; forward++)
            _navigator.SendCommandToDevice("10.1.3.212", "I");

        await Wait.UntilAsync(_time, PollStep, () => CountSent(EncoderInventoryQuery) == 3, "The inventory was never polled");

        Assert.Null(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public async Task UnansweredForwards_ToEndpointsThatArentAnswering_DoNotCount()
    {
        SetConnection(ConnectionState.Connected);
        _ = new NavDecoder("Unknown 1", "10.1.3.230", _navigator, _time);
        _ = new NavDecoder("Unknown 2", "10.1.3.231", _navigator, _time);
        await Wait.UntilAsync(() => CountSent("{10.1.3.231:I}\r") == 1 && CountSent("{10.1.3.230:I}\r") == 1, "The first polls never ran");
        for (var error = 0; error < 8; error++)
            Receive(NavigatorCaptures.BareInvalidParameter);

        await Wait.UntilAsync(_time, PollStep, () => CountSent(EncoderInventoryQuery) == 3, "The inventory was never polled");

        Assert.Null(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public async Task ANavigatorThatStopsAnsweringTwoEndpoints_RaisesTheRelayIssue()
    {
        SetConnection(ConnectionState.Connected);
        _ = new NavEncoder("QuadView", "10.1.3.210", _navigator, _time);
        _ = new NavEncoder("Codec 1", "10.1.3.212", _navigator, _time);
        await Wait.UntilAsync(() => CountSent("{10.1.3.210:I}\r") == 1 && CountSent("{10.1.3.212:I}\r") == 1, "The first polls never ran");
        Receive(NavigatorCaptures.Court82QuadViewDeviceNumber);
        Receive(NavigatorCaptures.Court82QuadViewHostname);
        Receive(NavigatorCaptures.Court82QuadViewSerial);
        Receive(NavigatorCaptures.Court82QuadViewStatus);
        Receive(NavigatorCaptures.Court82CodecOneDeviceNumber);
        Receive(NavigatorCaptures.Court82CodecOneHostname);
        Receive(NavigatorCaptures.Court82CodecOneSerial);
        Receive(NavigatorCaptures.Court82CodecOneStatus);

        await Wait.UntilAsync(_time, TimeSpan.FromSeconds(10), () => OngoingIssue(RelayIssueKey) != null, "The relay issue was never raised");

        Assert.Contains("the last 6 commands", OngoingIssue(RelayIssueKey)!.Message);
    }

    [Fact]
    public async Task UnansweredForwards_AreForgottenOnReconnect()
    {
        SetConnection(ConnectionState.Connected);
        await AnsweringCodecOne();
        for (var forward = 0; forward < 4; forward++)
            _navigator.SendCommandToDevice("10.1.3.212", "I");
        await Wait.UntilAsync(_time, PollStep, () => CountSent(EncoderInventoryQuery) == 2, "The inventory was never polled");

        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, PollStep, () => CountSent(EncoderInventoryQuery) == 4, "The inventory was never polled again");

        Assert.Equal(1, CountSent($"{{10.1.3.212:{EscapeHeader}3CV\r}}\r"));

        Assert.Null(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public async Task ARelayedReply_ResolvesTheRelayIssue()
    {
        await RaiseTheRelayIssue();

        _navigator.SendCommandToDevice("10.1.3.212", "I");
        Receive(NavigatorCaptures.Court82CodecOneStatus);

        Assert.Null(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public async Task ARelayedReplyForAnotherController_DoesNotResolveTheRelayIssue()
    {
        await RaiseTheRelayIssue();

        Receive(NavigatorCaptures.Court73RelayedVideoMute);

        Assert.NotNull(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public async Task UnansweredForwards_FollowedByARelayedReply_DoNotCount()
    {
        SetConnection(ConnectionState.Connected);
        await AnsweringCodecOne();
        for (var forward = 0; forward < 6; forward++)
            _navigator.SendCommandToDevice("10.1.3.212", "I");
        _navigator.SendCommandToDevice("10.1.3.226", $"{EscapeHeader}DNUM\r");
        Receive(NavigatorCaptures.Court82CodecInputDeviceNumber);

        await Wait.UntilAsync(_time, PollStep, () => CountSent(EncoderInventoryQuery) == 3, "The inventory was never polled");

        Assert.Null(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public async Task BareE13sForAnOfflineEndpoint_AreNotUnansweredForwards()
    {
        SetConnection(ConnectionState.Connected);
        _ = new NavDecoder("Left Display", "10.1.3.218", _navigator, _time);
        await Wait.UntilAsync(() => CountSent("{10.1.3.218:I}\r") == 1, "The first poll never ran");
        for (var forward = 0; forward < 3; forward++)
            _navigator.SendCommandToDevice("10.1.3.218", "I");
        Receive(NavigatorCaptures.Court82DecoderInventory);

        for (var error = 0; error < 7; error++)
            Receive(NavigatorCaptures.BareInvalidParameter);
        await Wait.UntilAsync(_time, PollStep, () => CountSent(EncoderInventoryQuery) == 3, "The inventory was never polled");

        Assert.Null(OngoingIssue(RelayIssueKey));
    }

    [Fact]
    public void Court82Inventories_WithAnUnansweredEndpointForEveryOfflineDevice_RaiseNothing()
    {
        RegisterCourt82Encoders(Court82OfflineEncoders);
        RegisterCourt82Decoders(Court82OfflineDecoders);

        Receive(NavigatorCaptures.Court82EncoderInventory);
        Receive(NavigatorCaptures.Court82DecoderInventory);

        Assert.DoesNotContain(_navigator.GetOngoingIssues(),
            issue => issue.Key.StartsWith("unclaimed-offline:") || issue.Key.StartsWith("unregistered:"));
        Assert.DoesNotContain(_mockSshClient.Invocations, invocation =>
            invocation.Arguments.FirstOrDefault() is string text && text.EndsWith($":{EscapeHeader}CI\r}}\r"));
    }

    [Fact]
    public void Court82DecoderInventory_WithMoreOfflineDecodersThanUnansweredEndpoints_RaisesTheCountIssue()
    {
        RegisterCourt82Decoders(Court82OfflineDecoders[1..]);

        Receive(NavigatorCaptures.Court82DecoderInventory);

        var issue = OngoingIssue("unclaimed-offline:o");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The Navigator lists 9 offline or not-connected decoders that no registered decoder has claimed " +
                     "(1531, 1532, 1533, 1534, 1535, 1536, 1537, 1538, 1541), more than the 8 registered decoders still without a device number " +
                     "(10.1.3.219, 10.1.3.220, 10.1.3.221, 10.1.3.222, 10.1.3.223, 10.1.3.224, 10.1.3.225, 10.1.3.228)", issue.Message);

        _ = new NavDecoder("Left Display", "10.1.3.218", _navigator, _time);
        Receive(NavigatorCaptures.Court82DecoderInventory);

        Assert.Null(OngoingIssue("unclaimed-offline:o"));
    }

    [Fact]
    public void Court82EncoderInventory_AsksAnUnclaimedOnlineEncoderForItsAddressOnce()
    {
        RegisterCourt82Encoders(Court82OfflineEncoders, includeCodecTwo: false);

        Receive(NavigatorCaptures.Court82EncoderInventory);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Assert.Equal(1, CountSent($"{{1539i:{EscapeHeader}CI\r}}\r"));
        Assert.Null(OngoingIssue("unregistered:1539i"));
    }

    [Fact]
    public void UnclaimedOnlineEncoder_ReportingAnUnregisteredAddress_RaisesAMinorIssueUntilItIsRegistered()
    {
        RegisterCourt82Encoders(Court82OfflineEncoders, includeCodecTwo: false);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Receive(NavigatorCaptures.Court82CodecTwoAddressReply);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        var issue = OngoingIssue("unregistered:1539i");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The Navigator has encoder 1539 (inventory state 1), which isn't registered to this module: " +
                     "it reports IP 10.1.3.213 ({1539i}:CI replied Ipi 10.1.3.213)", issue.Message);
        Assert.Null(OngoingIssue("unclaimed-offline:i"));

        _ = new NavEncoder("Codec 2", "10.1.3.213", _navigator, _time);
        Receive(NavigatorCaptures.Court82CodecTwoDeviceNumber);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Assert.Null(OngoingIssue("unregistered:1539i"));
    }

    [Fact]
    public void UnclaimedOnlineEncoder_ReportingARegisteredAddress_RaisesNothing()
    {
        RegisterCourt82Encoders(Court82OfflineEncoders, includeCodecTwo: false);
        _ = new NavEncoder("Codec 2", "10.1.3.213", _navigator, _time);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Receive(NavigatorCaptures.Court82CodecTwoAddressReply);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Assert.DoesNotContain(_navigator.GetOngoingIssues(),
            issue => issue.Key.StartsWith("unclaimed-offline:") || issue.Key.StartsWith("unregistered:"));
        Assert.Equal(1, CountSent($"{{1539i:{EscapeHeader}CI\r}}\r"));
    }

    [Fact]
    public void AddressReply_UnderAnotherKey_StillAnswersTheQuery()
    {
        RegisterCourt82Encoders(Court82OfflineEncoders, includeCodecTwo: false);
        _ = new NavEncoder("Codec 2", "10.1.3.213", _navigator, _time);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Receive(NavigatorCaptures.InventedUppercaseKeyAddressReply);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Assert.Null(OngoingIssue("unregistered:1539i"));
        Assert.Equal(1, CountSent($"{{1539i:{EscapeHeader}CI\r}}\r"));
    }

    [Fact]
    public void UnclaimedOnlineEncoder_NotAnsweringItsAddressQuery_IsAskedAgainAndRaisesIpUnknownAfterAFullEndpointPoll()
    {
        var addressQuery = $"{{1539i:{EscapeHeader}CI\r}}\r";
        RegisterCourt82Encoders(Court82OfflineEncoders, includeCodecTwo: false);
        Receive(NavigatorCaptures.Court82EncoderInventory);

        _time.Advance(TimeSpan.FromSeconds(30));
        Receive(NavigatorCaptures.Court82EncoderInventory);
        Assert.Equal(2, CountSent(addressQuery));
        Assert.Null(OngoingIssue("unregistered:1539i"));

        _time.Advance(TimeSpan.FromSeconds(60));
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Assert.Equal(3, CountSent(addressQuery));
        Assert.Equal("The Navigator has encoder 1539 (inventory state 1), which isn't registered to this module: " +
                     "IP unknown: no reply to {1539i}:CI", OngoingIssue("unregistered:1539i")?.Message);
    }

    [Fact]
    public void RegisteredEncoder_ReportingItsDeviceNumberWithinAFullEndpointPoll_RaisesNothing()
    {
        RegisterCourt82Encoders(Court82OfflineEncoders, includeCodecTwo: false);
        _ = new NavEncoder("Codec 2", "10.1.3.213", _navigator, _time);
        Receive(NavigatorCaptures.Court82EncoderInventory);
        _time.Advance(TimeSpan.FromSeconds(30));
        Receive(NavigatorCaptures.Court82EncoderInventory);
        _time.Advance(TimeSpan.FromSeconds(30));
        Receive(NavigatorCaptures.Court82EncoderInventory);

        _time.Advance(TimeSpan.FromSeconds(25));
        Receive(NavigatorCaptures.Court82CodecTwoDeviceNumber);
        _time.Advance(TimeSpan.FromSeconds(5));
        Receive(NavigatorCaptures.Court82EncoderInventory);

        Assert.DoesNotContain(_navigator.GetIssues(), issue => issue.Key.StartsWith("unregistered:"));
    }

    [Fact]
    public void AnEndpoint_ShowsTheNavigatorsConnectionReasonWhileTheNavigatorIsDisconnected()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator, _time);
        SetConnection(ConnectionState.Disconnected);

        typeof(CommunicationClient)
            .GetMethod("ReportConnectionFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                [typeof(ConnectionReasonCategory), typeof(string), typeof(Exception)])!
            .Invoke(_mockSshClient.Object, [ConnectionReasonCategory.CredentialsRejected, "host:22 rejected the credentials", null]);

        Assert.Equal(_mockSshClient.Object.ConnectionReason, decoder.CommunicationClient.ConnectionReason);
        Assert.Equal(ConnectionReasonCategory.CredentialsRejected, decoder.CommunicationClient.ConnectionReason?.Category);
    }

    [Fact]
    public void UnregisteredDevice_StaysRaisedWhileOfflineAndResolvesWhenTheInventoryDropsIt()
    {
        Receive("Rprt*Inventory*I*1");
        Receive("{0001i}Ipi 10.1.3.99");
        Receive("Rprt*Inventory*I*1");
        Assert.NotNull(OngoingIssue("unregistered:0001i"));

        Receive("Rprt*Inventory*I*2");
        Assert.Equal("The Navigator has encoder 1 (inventory state 2), which isn't registered to this module: " +
                     "it reports IP 10.1.3.99 ({0001i}:CI replied Ipi 10.1.3.99)", OngoingIssue("unregistered:0001i")?.Message);
        Assert.Null(OngoingIssue("unclaimed-offline:i"));

        Receive("Rprt*Inventory*I*3");
        Assert.Contains("(inventory state 3)", OngoingIssue("unregistered:0001i")?.Message);

        Receive("Rprt*Inventory*I*0");
        Assert.Null(OngoingIssue("unregistered:0001i"));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class NavigatorLogTest
{
    private readonly Mock<SshClient> _mockSshClient = TestFactory.CreateSshClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Navigator _navigator;

    public NavigatorLogTest()
    {
        _navigator = Logged(() => new Navigator("Logged navigator", _mockSshClient.Object, _time));
    }

    private T Logged<T>(Func<T> create)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            return create();
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Receive(string response) => _mockSshClient.Object.ResponseHandlers!.Invoke(response);

    private int CountSent(string message) => _mockSshClient.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(SshClient.Send) && invocation.Arguments[0] as string == message);

    private const string UnexplainedError = "The Navigator rejected a command: E13 - Invalid parameter";

    [Fact]
    public async Task BareE13_WhileTheInventoryListsOfflineDecoders_IsADebugMissForTheUnansweredDecoder()
    {
        Logged(() => new NavDecoder("Left Display", "10.1.3.218", _navigator, _time));
        await Wait.UntilAsync(() => CountSent("{10.1.3.218:I}\r") == 1, "The first poll never ran");
        Receive(NavigatorCaptures.Court82DecoderInventory);

        Receive(NavigatorCaptures.BareInvalidParameter);

        var miss = Assert.Single(_logs.Logs, log => log.Message.StartsWith("No reply to"));
        Assert.Equal(LogLevel.Debug, miss.Level);
        Assert.Equal("No reply to Esc DNUM: the Navigator answered E13 while its inventory lists offline endpoints", miss.Message);
        Assert.Equal("Left Display", miss.Scope["InstanceName"]);
        Assert.DoesNotContain(_logs.Logs, log => log.Message == UnexplainedError);
    }

    [Fact]
    public void BareE13_WithNoOfflineEndpointsInTheInventory_IsAWarning()
    {
        _navigator.SendCommandToDevice("10.1.3.212", "I");

        Receive(NavigatorCaptures.BareInvalidParameter);

        Assert.Equal(LogLevel.Warning, Assert.Single(_logs.Logs, log => log.Message == UnexplainedError).Level);
    }

    [Fact]
    public async Task BareE13_WhenOnlyAnsweringDecodersAwaitAReply_IsAWarning()
    {
        Logged(() => new NavDecoder("Codec Input", "10.1.3.226", _navigator, _time));
        await Wait.UntilAsync(() => CountSent("{10.1.3.226:I}\r") == 1, "The first poll never ran");
        Receive(NavigatorCaptures.Court82CodecInputDeviceNumber);
        Receive(NavigatorCaptures.Court82DecoderInventory);

        Receive(NavigatorCaptures.BareInvalidParameter);

        Assert.Equal(LogLevel.Warning, Assert.Single(_logs.Logs, log => log.Message == UnexplainedError).Level);
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith("No reply to"));
    }

    [Fact]
    public void SendCommandToDevice_PastTheTrackerLimit_LogsTheDroppedCommand()
    {
        for (var forward = 0; forward <= SentCommandTracker<object>.MaxCommands; forward++)
            _navigator.SendCommandToDevice("10.1.3.212", "I");

        var dropped = Assert.Single(_logs.Logs, log => log.Message.StartsWith("Dropped"));
        Assert.Equal(LogLevel.Warning, dropped.Level);
        Assert.Equal($"Dropped {{10.1.3.212:I}}: more than {SentCommandTracker<object>.MaxCommands} commands awaiting a reply", dropped.Message);
    }

    private void VerifyNoUsbCommandSent() =>
        _mockSshClient.Verify(x => x.Send(It.Is<string>(message => message.EndsWith("^\r"))), Times.Never);

    [Fact]
    public void RouteUsb_WithAnUnknownHostNumber_SendsNothingAndLogs()
    {
        var encoder = new NavEncoder("Encoder", "10.1.3.201", _navigator);
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteUsb(encoder, decoder);

        VerifyNoUsbCommandSent();
        Assert.Contains(_logs.Logs, log => log.Message == "Not requesting a USB route from Encoder as its device number is 0");
    }

    [Fact]
    public void RouteUsb_WithAnUnknownDeviceNumber_SendsNothingAndLogs()
    {
        var encoder = new NavEncoder("Encoder", "10.1.3.201", _navigator);
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.201}Dnum5");

        _navigator.RouteUsb(encoder, decoder);

        VerifyNoUsbCommandSent();
        Assert.Contains(_logs.Logs, log => log.Message == "Not requesting a USB route to Decoder as its device number is 0");
    }

    [Fact]
    public void DerouteUsb_WithAnUnknownDeviceNumber_SendsNothingAndLogs()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);

        _navigator.DerouteUsb(decoder);

        VerifyNoUsbCommandSent();
        Assert.Contains(_logs.Logs, log => log.Message == "Not requesting a USB deroute of Decoder as its device number is 0");
    }
}