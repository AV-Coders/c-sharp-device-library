using System.Net;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Motor.Tests;

public class BondBridgeTest
{
    private const string ActionContent = "{}";
    private const string ActionContentType = "application/json";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly Mock<RestComms> _mockClient = new("host", (ushort)80, "RestClient");
    private readonly BondBridge _bridge;
    private readonly BondDevice _blind;
    private readonly BondDevice _curtain;
    private readonly BondGroup _group;

    public BondBridgeTest()
    {
        _bridge = new BondBridge(_mockClient.Object, "token");
        _blind = new BondDevice("Blind", "abc", RelayAction.Lower, 30, _bridge);
        _curtain = new BondDevice("Curtain", "def", RelayAction.Lower, 30, _bridge);
        _group = new BondGroup("Blinds", "xyz", RelayAction.Lower, 30, _bridge);
    }

    internal static void RespondTo(Mock<RestComms> client, string endpoint, HttpStatusCode status) =>
        client.Setup(x => x.Put(It.Is<Uri>(uri => uri.ToString() == endpoint), ActionContent, ActionContentType))
            .Callback(() => client.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(status)))
            .Returns(Task.CompletedTask);

    private void RespondTo(string endpoint, HttpStatusCode status) => RespondTo(_mockClient, endpoint, status);

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

    internal static void ReplyToVersion(Mock<RestComms> client, HttpStatusCode status, string body) =>
        client.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://host/v2/sys/version"),
            Content = new StringContent(body)
        });

    private void ReplyToVersion(HttpStatusCode status, string body) => ReplyToVersion(_mockClient, status, body);

    private string? Detail(string label) => _bridge.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? OngoingIssue(string key) => _bridge.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    [Fact]
    public void Constructor_NamesTheBridgeBondBridgeUnlessGivenAName()
    {
        var named = new BondBridge(_mockClient.Object, "token", "Office Bond");

        Assert.Equal("Bond Bridge", _bridge.Name);
        Assert.Equal("Office Bond", named.Name);
    }

    [Fact]
    public void Constructor_KeepsTheTwoArgumentSignatureForCompiledCallers()
    {
        var twoArgument = typeof(BondBridge).GetConstructor([typeof(RestComms), typeof(string)]);

        Assert.NotNull(twoArgument);
        Assert.True(twoArgument.IsPublic);
        Assert.Equal("Bond Bridge", ((BondBridge)twoArgument.Invoke([_mockClient.Object, "token"])).Name);
    }

    [Fact]
    public void VersionReply_ShowsTheFirmwareModelBondIdAndUptime()
    {
        ReplyToVersion(HttpStatusCode.OK,
            "{\"target\":\"zermatt\",\"fw_ver\":\"v3.10.6\",\"fw_date\":\"Wed Nov 22 15:45:19 UTC 2023\",\"uptime_s\":274325,\"make\":\"Olibra LLC\",\"model\":\"BD-1000\",\"bondid\":\"KSMJWCE12345\",\"api\":2}");

        Assert.Equal("v3.10.6", Detail("Firmware"));
        Assert.Equal("BD-1000", Detail("Model"));
        Assert.Equal("KSMJWCE12345", Detail("Bond ID"));
        Assert.Equal("3d 4h 12m", Detail("Uptime"));
        Assert.Empty(_bridge.GetOngoingIssues());
    }

    [Fact]
    public void VersionReply_FromTheDocsExample_ShowsTheBondIdAsNotSupportedAndRaisesNoIssue()
    {
        const string body = "{\"target\":\"snowbird\",\"fw_ver\":\"v2.5.2\",\"fw_date\":\"Fri Feb 22 14:13:25 -03 2019\",\"make\":\"Olibra LLC\",\"model\":\"model\",\"branding_profile\":\"O_SNOWBIRD\",\"uptime_s\":380,\"_\":\"c342ae74\"}";

        ReplyToVersion(HttpStatusCode.OK, body);

        Assert.Equal("v2.5.2", Detail("Firmware"));
        Assert.Equal("0d 0h 6m", Detail("Uptime"));
        Assert.Contains(new DeviceDetail("Bond ID", "Not supported: no \"bondid\" in the version reply", DetailTone.Normal), _bridge.Details);
        Assert.Empty(_bridge.GetIssues());
    }

    [Fact]
    public void VersionReply_WithOnlyTheFirmware_ShowsTheRestAsNotSupportedAndRaisesNoIssue()
    {
        ReplyToVersion(HttpStatusCode.OK, "{\"fw_ver\":\"v3.10.6\"}");

        Assert.Equal(
        [
            new DeviceDetail("Firmware", "v3.10.6"),
            new DeviceDetail("Model", "Not supported: no \"model\" in the version reply", DetailTone.Normal),
            new DeviceDetail("Bond ID", "Not supported: no \"bondid\" in the version reply", DetailTone.Normal),
            new DeviceDetail("Uptime", "Not supported: no \"uptime_s\" in the version reply", DetailTone.Normal)
        ], _bridge.Details);
        Assert.Empty(_bridge.GetIssues());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404 Not Found")]
    [InlineData(HttpStatusCode.NotImplemented, "HTTP 501 Not Implemented")]
    public void VersionReply_GivenNotFoundOrNotImplemented_ShowsEveryValueAsNotSupportedAndRaisesNoIssue(HttpStatusCode status, string reply)
    {
        ReplyToVersion(status, "{\"_error_msg\":\"no such path\"}");

        var notSupported = $"Not supported: {reply}, body: {{\"_error_msg\":\"no such path\"}}";
        Assert.Equal(
        [
            new DeviceDetail("Firmware", notSupported, DetailTone.Normal),
            new DeviceDetail("Model", notSupported, DetailTone.Normal),
            new DeviceDetail("Bond ID", notSupported, DetailTone.Normal),
            new DeviceDetail("Uptime", notSupported, DetailTone.Normal)
        ], _bridge.Details);
        Assert.Empty(_bridge.GetIssues());
    }

    [Fact]
    public void VersionReply_WithoutTheFirmwareThreeTimesInARow_RaisesAMinorIssue()
    {
        for (var reply = 0; reply < 3; reply++)
            ReplyToVersion(HttpStatusCode.OK, "{\"model\":\"BD-1000\",\"bondid\":\"KSMJWCE12345\",\"uptime_s\":60}");

        Assert.Null(Detail("Firmware"));
        Assert.Equal("BD-1000", Detail("Model"));
        var issue = OngoingIssue("version-read");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains("no fw_ver", issue.Message);
    }

    [Fact]
    public void VersionReply_GivenAnHttpErrorThreeTimesInARow_RaisesAnIssueWithTheStatusAndBody()
    {
        ReplyToVersion(HttpStatusCode.InternalServerError, "{\"_error_msg\":\"oops\"}");
        ReplyToVersion(HttpStatusCode.InternalServerError, "{\"_error_msg\":\"oops\"}");

        Assert.Empty(_bridge.GetIssues());

        ReplyToVersion(HttpStatusCode.InternalServerError, "{\"_error_msg\":\"oops\"}");

        var issue = OngoingIssue("version-read");
        Assert.NotNull(issue);
        Assert.Contains("HTTP 500 Internal Server Error", issue.Message);
        Assert.Contains("{\"_error_msg\":\"oops\"}", issue.Message);
    }

    [Fact]
    public void VersionReply_ThatIsNotJsonThreeTimesInARow_RaisesAnIssueWithTheBody()
    {
        for (var reply = 0; reply < 3; reply++)
            ReplyToVersion(HttpStatusCode.OK, "<html>captive portal</html>");

        Assert.Contains("<html>captive portal</html>", OngoingIssue("version-read")?.Message);
        Assert.Empty(_bridge.Details);
    }

    [Fact]
    public void VersionReply_WithAnUnreadableUptimeAndAFirmwareThatIsNotAString_ShowsThemRaw()
    {
        ReplyToVersion(HttpStatusCode.OK,
            "{\"fw_ver\":{\"major\":3},\"model\":\"BD-1000\",\"bondid\":\"KSMJWCE12345\",\"uptime_s\":\"soon\"}");

        Assert.Equal("soon", Detail("Uptime"));
        Assert.Equal(DetailTone.Warning, _bridge.Details.Single(detail => detail.Label == "Uptime").Tone);
        Assert.Equal("{\"major\":3}", Detail("Firmware"));
    }

    [Fact]
    public void VersionReply_WithAnUptimeTooLargeForATimeSpan_ShowsItRaw()
    {
        ReplyToVersion(HttpStatusCode.OK,
            "{\"fw_ver\":\"v3.10.6\",\"model\":\"BD-1000\",\"bondid\":\"KSMJWCE12345\",\"uptime_s\":9223372036854775807}");

        Assert.Equal(new DeviceDetail("Uptime", "9223372036854775807", DetailTone.Warning),
            _bridge.Details.Single(detail => detail.Label == "Uptime"));
        Assert.Equal("v3.10.6", Detail("Firmware"));
    }

    [Fact]
    public void VersionReply_AfterThreeErrors_ResolvesTheIssueAndStartsTheCountAgain()
    {
        for (var reply = 0; reply < 3; reply++)
            ReplyToVersion(HttpStatusCode.Unauthorized, "");

        ReplyToVersion(HttpStatusCode.OK, "{\"fw_ver\":\"v3.10.6\",\"model\":\"BD-1000\",\"bondid\":\"KSMJWCE12345\",\"uptime_s\":60}");

        Assert.Empty(_bridge.GetOngoingIssues());
        Assert.Contains(_bridge.GetIssues(), issue => issue.Key == "version-read" && issue.Status == IssueStatus.Resolved);

        ReplyToVersion(HttpStatusCode.Unauthorized, "");
        ReplyToVersion(HttpStatusCode.Unauthorized, "");

        Assert.Empty(_bridge.GetOngoingIssues());
    }

    [Fact]
    public async Task VersionReply_WhileAnActionIsInFlight_DoesNotCompleteTheAction()
    {
        _mockClient.Setup(x => x.Put(It.Is<Uri>(uri => uri.ToString() == "/v2/devices/abc/actions/Open"), ActionContent, ActionContentType))
            .Callback(() =>
            {
                ReplyToVersion(HttpStatusCode.InternalServerError, "");
                _mockClient.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(HttpStatusCode.OK));
            })
            .Returns(Task.CompletedTask);

        Assert.Equal(new BondActionResult(true, ""), await _bridge.OpenDevice("abc"));
    }

    [Fact]
    public void BondDevicesAndGroups_CommunicationClient_IsTheBridgesRestClient()
    {
        Assert.Same(_mockClient.Object, _blind.CommunicationClient);
        Assert.Same(_mockClient.Object, _group.CommunicationClient);
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

[Collection(LoggerFactoryCollection.Name)]
public class BondBridgeLogTest
{
    private readonly Mock<RestComms> _mockClient = new("host", (ushort)80, "RestClient");
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly BondBridge _bridge;
    private readonly BondDevice _blind;
    private readonly BondGroup _group;

    public BondBridgeLogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _bridge = new BondBridge(_mockClient.Object, "token");
            _blind = new BondDevice("Blind", "abc", RelayAction.Lower, 30, _bridge);
            _group = new BondGroup("Blinds", "xyz", RelayAction.Lower, 30, _bridge);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private bool Logged(params string[] parts) =>
        _logs.Logs.Any(log => parts.All(part => log.Message.Contains(part)));

    [Fact]
    public void VersionReply_MissingFieldsOnEveryPoll_KeepTheDetailAndLogTheBodyOnce()
    {
        BondBridgeTest.ReplyToVersion(_mockClient, HttpStatusCode.OK, "{\"fw_ver\":\"v3.10.6\",\"uptime_s\":60}");

        BondBridgeTest.ReplyToVersion(_mockClient, HttpStatusCode.OK, "{\"fw_ver\":\"v3.10.6\",\"uptime_s\":145}");

        Assert.Equal("Not supported: no \"model\" in the version reply",
            _bridge.Details.SingleOrDefault(detail => detail.Label == "Model")?.Value);
        var log = Assert.Single(_logs.Logs, entry => entry.Message.Contains("does not support Model"));
        Assert.Contains("{\"fw_ver\":\"v3.10.6\",\"uptime_s\":60}", log.Message);
    }

    [Fact]
    public void BondDevice_Raise_GivenAnHttpError_SetsErrorAndLogsTheStatus()
    {
        BondBridgeTest.RespondTo(_mockClient, "/v2/devices/abc/actions/Open", HttpStatusCode.NotFound);

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
    public void BondGroup_Raise_GivenAnHttpError_SetsErrorAndLogsTheStatus()
    {
        BondBridgeTest.RespondTo(_mockClient, "/v2/groups/xyz/actions/Open", HttpStatusCode.Unauthorized);

        _group.Raise();

        Assert.Equal(CommunicationState.Error, _group.CommunicationState);
        Assert.True(Logged("Raise", "xyz", "HTTP 401 Unauthorized"));
    }
}
