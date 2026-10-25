using System.Net;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Motor.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class BondBridgeTest
{
    private const string ActionContent = "{}";
    private const string ActionContentType = "application/json";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly Mock<RestComms> _mockClient = new("host", (ushort)80, "RestClient");
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly BondBridge _bridge;
    private readonly BondDevice _blind;
    private readonly BondDevice _curtain;
    private readonly BondGroup _group;

    public BondBridgeTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _bridge = new BondBridge(_mockClient.Object, "token");
            _blind = new BondDevice("Blind", "abc", RelayAction.Lower, 30, _bridge);
            _curtain = new BondDevice("Curtain", "def", RelayAction.Lower, 30, _bridge);
            _group = new BondGroup("Blinds", "xyz", RelayAction.Lower, 30, _bridge);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void RespondTo(string endpoint, HttpStatusCode status) =>
        _mockClient.Setup(x => x.Put(It.Is<Uri>(uri => uri.ToString() == endpoint), ActionContent, ActionContentType))
            .Callback(() => _mockClient.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(status)))
            .Returns(Task.CompletedTask);

    private TaskCompletionSource HoldThenRespond(string endpoint, HttpStatusCode status)
    {
        var release = new TaskCompletionSource();
        _mockClient.Setup(x => x.Put(It.Is<Uri>(uri => uri.ToString() == endpoint), ActionContent, ActionContentType))
            .Returns(async () =>
            {
                await release.Task;
                _mockClient.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(status));
            });
        return release;
    }

    private void FailWith(Exception exception) =>
        _mockClient.Setup(x => x.Put(It.IsAny<Uri>(), ActionContent, ActionContentType)).ThrowsAsync(exception);

    private bool Logged(params string[] parts) =>
        _logs.Logs.Any(log => parts.All(part => log.Message.Contains(part)));

    [Fact]
    public void BondDevice_CommunicationClient_IsTheBridgesRestClient()
    {
        var device = new BondDevice("Blind", "abc", RelayAction.Lower, 30, _bridge);

        Assert.Same(_mockClient.Object, device.CommunicationClient);
    }

    [Fact]
    public void BondGroup_CommunicationClient_IsTheBridgesRestClient()
    {
        var group = new BondGroup("Blinds", "xyz", RelayAction.Lower, 30, _bridge);

        Assert.Same(_mockClient.Object, group.CommunicationClient);
    }

    [Fact]
    public async Task OpenDevice_GivenASuccessfulResponse_ReturnsSucceeded()
    {
        RespondTo("/v2/devices/abc/actions/Open", HttpStatusCode.OK);

        Assert.Equal(new BondActionResult(true, ""), await _bridge.OpenDevice("abc"));
    }

    [Fact]
    public async Task CloseGroup_GivenAnHttpError_ReturnsTheStatus()
    {
        RespondTo("/v2/groups/xyz/actions/Close", HttpStatusCode.Unauthorized);

        Assert.Equal(new BondActionResult(false, "HTTP 401 Unauthorized"), await _bridge.CloseGroup("xyz"));
    }

    [Fact]
    public async Task StopDevice_GivenNoResponse_ReturnsNoResponse()
    {
        Assert.Equal(new BondActionResult(false, "no response"), await _bridge.StopDevice("abc"));
    }

    [Fact]
    public async Task OpenDevice_GivenTwoActionsInFlight_ReturnsEachItsOwnResult()
    {
        var release = HoldThenRespond("/v2/devices/abc/actions/Open", HttpStatusCode.OK);
        RespondTo("/v2/devices/def/actions/Open", HttpStatusCode.InternalServerError);

        var first = _bridge.OpenDevice("abc");
        var second = _bridge.OpenDevice("def");
        release.SetResult();

        Assert.Equal(new BondActionResult(true, ""), await first.WaitAsync(WaitTimeout));
        Assert.Equal(new BondActionResult(false, "HTTP 500 Internal Server Error"), await second);
    }

    [Fact]
    public async Task OpenDevice_GivenAResponseToAnotherRequest_ReturnsItsOwnResult()
    {
        var release = HoldThenRespond("/v2/devices/abc/actions/Open", HttpStatusCode.OK);

        var action = _bridge.OpenDevice("abc");
        _mockClient.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        release.SetResult();

        Assert.Equal(new BondActionResult(true, ""), await action.WaitAsync(WaitTimeout));
    }

    [Fact]
    public void BondDevice_Raise_GivenASuccessfulResponse_SetsOkayOnThatDeviceOnly()
    {
        RespondTo("/v2/devices/abc/actions/Open", HttpStatusCode.OK);

        _blind.Raise();

        Assert.Equal(CommunicationState.Okay, _blind.CommunicationState);
        Assert.Equal(CommunicationState.Unknown, _curtain.CommunicationState);
        Assert.Equal(CommunicationState.Unknown, _group.CommunicationState);
    }

    [Fact]
    public void BondDevice_Raise_GivenAnHttpError_SetsErrorAndLogsTheStatus()
    {
        RespondTo("/v2/devices/abc/actions/Open", HttpStatusCode.NotFound);

        _blind.Raise();

        Assert.Equal(CommunicationState.Error, _blind.CommunicationState);
        Assert.True(Logged("Raise", "abc", "HTTP 404 Not Found"));
    }

    [Fact]
    public void BondDevice_Lower_GivenNoResponse_SetsErrorAndLogs()
    {
        _blind.Lower();

        Assert.Equal(CommunicationState.Error, _blind.CommunicationState);
        Assert.True(Logged("Lower", "abc", "no response"));
    }

    [Fact]
    public void BondDevice_Stop_GivenAClientException_SetsErrorAndLogsTheException()
    {
        var exception = new HttpRequestException("Connection refused");
        FailWith(exception);

        _blind.Stop();

        Assert.Equal(CommunicationState.Error, _blind.CommunicationState);
        Assert.Contains(_blind.Errors,
            error => error.Exception == exception && error.Message.Contains("Stop") && error.Message.Contains("abc"));
    }

    [Fact]
    public void BondDevice_Raise_GivenAFailure_LeavesTheOtherDevicesState()
    {
        RespondTo("/v2/devices/abc/actions/Open", HttpStatusCode.OK);
        RespondTo("/v2/devices/def/actions/Open", HttpStatusCode.InternalServerError);

        _blind.Raise();
        _curtain.Raise();

        Assert.Equal(CommunicationState.Okay, _blind.CommunicationState);
        Assert.Equal(CommunicationState.Error, _curtain.CommunicationState);
    }

    [Fact]
    public void BondDevice_Raise_GivenAnotherDeviceFailsWhileItsCommandIsInFlight_SetsEachDevicesOwnState()
    {
        var release = HoldThenRespond("/v2/devices/abc/actions/Open", HttpStatusCode.OK);
        RespondTo("/v2/devices/def/actions/Open", HttpStatusCode.InternalServerError);

        _blind.Raise();
        _curtain.Raise();
        release.SetResult();

        Assert.True(SpinWait.SpinUntil(() => _blind.CommunicationState == CommunicationState.Okay, WaitTimeout));
        Assert.Equal(CommunicationState.Error, _curtain.CommunicationState);
    }

    [Fact]
    public void BondDevice_Raise_ReturnsBeforeTheCommandCompletes()
    {
        var release = HoldThenRespond("/v2/devices/abc/actions/Open", HttpStatusCode.OK);

        _blind.Raise();

        Assert.Equal(CommunicationState.Unknown, _blind.CommunicationState);
        release.SetResult();
        Assert.True(SpinWait.SpinUntil(() => _blind.CommunicationState == CommunicationState.Okay, WaitTimeout));
    }

    [Fact]
    public void BondGroup_Lower_GivenASuccessfulResponse_SetsOkayOnThatGroupOnly()
    {
        RespondTo("/v2/groups/xyz/actions/Close", HttpStatusCode.OK);

        _group.Lower();

        Assert.Equal(CommunicationState.Okay, _group.CommunicationState);
        Assert.Equal(CommunicationState.Unknown, _blind.CommunicationState);
    }

    [Fact]
    public void BondGroup_Raise_GivenAnHttpError_SetsErrorAndLogsTheStatus()
    {
        RespondTo("/v2/groups/xyz/actions/Open", HttpStatusCode.Unauthorized);

        _group.Raise();

        Assert.Equal(CommunicationState.Error, _group.CommunicationState);
        Assert.True(Logged("Raise", "xyz", "HTTP 401 Unauthorized"));
    }

    [Fact]
    public void BondGroup_Stop_GivenAClientException_SetsErrorAndLogsTheException()
    {
        var exception = new HttpRequestException("Connection refused");
        FailWith(exception);

        _group.Stop();

        Assert.Equal(CommunicationState.Error, _group.CommunicationState);
        Assert.Contains(_group.Errors,
            error => error.Exception == exception && error.Message.Contains("Stop") && error.Message.Contains("xyz"));
    }
}
