using System.Collections.Concurrent;
using System.Net;
using AVCoders.Core;
using Moq;

namespace AVCoders.Power.Tests;

// Response bodies are taken verbatim from an RF11iQ (firmware iq1.0) capture.
public class ThorRf11IqPduTest
{
    private const string Host = "http://host";
    private const string StatusBody =
        "<response>\n<s1>1</s1>\n<b1>1</b1>\n<s2>1</s2>\n<b2>1</b2>\n<bz>0</bz>\n<v>~</v>\n" +
        "<p1>1</p1>\n<p2>1</p2>\n<p3>1</p3>\n<p4>1</p4>\n<p5>1</p5>\n<p6>0</p6>\n<p7>1</p7>\n<p8>1</p8>\n" +
        "<c1>0.0</c1>\n<c2>0.3</c2>\n<c3>0.3</c3>\n<c4>0.0</c4>\n<c5>0.3</c5>\n<c6>2.6</c6>\n<c7>0.3</c7>\n<c8>0.3</c8>\n" +
        "<ver>iq1.0</ver>\n</response>\n";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Mock<RestComms> _restComms = new("host", (ushort)80, "test");
    // The poll worker and the test thread touch these concurrently.
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _responses = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private volatile int _statusDelayMs;

    public ThorRf11IqPduTest()
    {
        _restComms.Setup(client => client.Get(It.IsAny<Uri>()))
            .Returns<Uri?>(async uri =>
            {
                var path = uri!.OriginalString;
                _requests.Enqueue(path);
                if (path == "/status.xml" && _statusDelayMs > 0)
                    await Task.Delay(_statusDelayMs);
                if (_responses.TryGetValue(path, out var factory))
                    _restComms.Object.HttpResponseHandlers?.Invoke(factory());
            });
        Respond("/status.xml", StatusBody);
        RespondToCommands("Success! 1");
    }

    private static HttpResponseMessage Response(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(Host + path))
        };

    private void Respond(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _responses[path] = () => Response(path, body, status);

    private void RespondToCommands(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        foreach (var parameter in new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "b1", "b2", "bz" })
        foreach (var value in new[] { "0", "1" })
            Respond($"/cpan.cgi?param={parameter}&value={value}", body, status);
    }

    private void DoNotAnswerCommands()
    {
        foreach (var path in _responses.Keys.Where(path => path.StartsWith("/cpan.cgi")).ToList())
            _responses.TryRemove(path, out _);
    }

    private static string CommandPath(int channel, bool on) => $"/cpan.cgi?param=p{channel}&value={(on ? "1" : "0")}";

    private int CountRequests(string path) => _requests.Count(request => request == path);

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(25);
        }
    }

    private ThorRf11IqPdu CreatePdu(IReadOnlyDictionary<int, string>? outletNames = null) =>
        new(_restComms.Object, "Test PDU", "vgr5", outletNames, PollInterval);

    private async Task<ThorRf11IqPdu> CreatePolledPdu()
    {
        var pdu = CreatePdu();
        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Okay, "The PDU never polled");
        return pdu;
    }

    private static ThorRf11IqOutlet Outlet(ThorRf11IqPdu pdu, int channel) => (ThorRf11IqOutlet)pdu.Outlets[channel - 1];

    [Fact]
    public void Constructor_BuildsTheFixedOutletsAndAuthenticatesAsAdmin()
    {
        var pdu = CreatePdu();

        Assert.Equal(8, pdu.Outlets.Count);
        Assert.Equal("Output 1", pdu.Outlets[0].Name);
        Assert.Equal("Output 7", pdu.Outlets[6].Name);
        Assert.Equal("IEC Outputs", pdu.Outlets[7].Name);
        Assert.Equal(8, Outlet(pdu, 8).Channel);
        var expectedHeader = $"Basic {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:vgr5"))}";
        _restComms.Verify(client => client.AddDefaultHeader("Authorization", expectedHeader), Times.Once);
    }

    [Fact]
    public void Constructor_EncodesANonAsciiPasswordAsUtf8()
    {
        _ = new ThorRf11IqPdu(_restComms.Object, "Test PDU", "pässw0rd", null, PollInterval);

        var expectedHeader = $"Basic {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:pässw0rd"))}";
        _restComms.Verify(client => client.AddDefaultHeader("Authorization", expectedHeader), Times.Once);
    }

    [Fact]
    public void Constructor_UsesTheSuppliedOutletNames()
    {
        var pdu = CreatePdu(new Dictionary<int, string>
        {
            [1] = "Projector", [6] = "Codec", [8] = "Rack Amps", [3] = " ", [9] = "Nope"
        });

        Assert.Equal("Projector", pdu.Outlets[0].Name);
        Assert.Equal("Output 2", pdu.Outlets[1].Name);
        Assert.Equal("Output 3", pdu.Outlets[2].Name);
        Assert.Equal("Codec", pdu.Outlets[5].Name);
        Assert.Equal("Rack Amps", pdu.Outlets[7].Name);
        Assert.Equal(8, pdu.Outlets.Count);
    }

    [Fact]
    public async Task Poll_ReadsTheOutletStatesAndCurrents()
    {
        var pdu = await CreatePolledPdu();

        Assert.Equal(PowerState.On, pdu.Outlets[0].PowerState);
        Assert.Equal(PowerState.Off, pdu.Outlets[5].PowerState);
        Assert.Equal(PowerState.On, pdu.Outlets[7].PowerState);
        Assert.Equal(0.3f, Outlet(pdu, 2).CurrentAmps);
        Assert.Equal(2.6f, Outlet(pdu, 6).CurrentAmps);
        Assert.Equal("iq1.0", pdu.FirmwareVersion);
        Assert.False(pdu.BuzzerActive);
        Assert.Equal(2, pdu.Circuits.Count);
        Assert.All(pdu.Circuits, circuit => Assert.True(circuit.Protected));
        Assert.All(pdu.Circuits, circuit => Assert.True(circuit.BuzzerEnabled));
        Assert.Empty(pdu.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_RaisesCurrentHandlersOnChange()
    {
        var pdu = await CreatePolledPdu();
        var handler = new Mock<FloatHandler>();
        Outlet(pdu, 6).CurrentHandlers += handler.Object;
        Respond("/status.xml", StatusBody.Replace("<c6>2.6</c6>", "<c6>1.1</c6>"));

        await WaitUntilAsync(() => handler.Invocations.Count > 0, "The current handler never fired");

        handler.Verify(x => x.Invoke(1.1f), Times.Once);
        Assert.Equal(1.1f, Outlet(pdu, 6).CurrentAmps);
    }

    [Fact]
    public async Task Poll_WithADamagedCircuit_RaisesAndResolvesTheIssue()
    {
        var pdu = await CreatePolledPdu();
        var handler = new Mock<ThorRf11IqCircuitHandler>();
        pdu.CircuitHandlers += handler.Object;

        Respond("/status.xml", StatusBody.Replace("<s2>1</s2>", "<s2>0</s2>"));
        await WaitUntilAsync(() => handler.Invocations.Count > 0, "The circuit handler never fired");
        var issue = Assert.Single(pdu.GetOngoingIssues());
        Assert.Equal("circuit-2-damaged", issue.Key);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.False(pdu.Circuits[1].Protected);
        handler.Verify(x => x.Invoke(It.Is<IReadOnlyList<ThorRf11IqCircuitStatus>>(c => !c[1].Protected)), Times.Once);

        Respond("/status.xml", StatusBody);
        await WaitUntilAsync(() => pdu.GetOngoingIssues().Count == 0, "The circuit issue was never resolved");
    }

    [Fact]
    public async Task Poll_WithTheBuzzerSounding_RaisesAMinorIssue()
    {
        var pdu = await CreatePolledPdu();
        var handler = new Mock<BoolHandler>();
        pdu.BuzzerActiveHandlers += handler.Object;

        Respond("/status.xml", StatusBody.Replace("<bz>0</bz>", "<bz>1</bz>"));
        await WaitUntilAsync(() => handler.Invocations.Count > 0, "The buzzer handler never fired");

        Assert.True(pdu.BuzzerActive);
        var issue = Assert.Single(pdu.GetOngoingIssues());
        Assert.Equal("buzzer", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        handler.Verify(x => x.Invoke(true), Times.Once);
    }

    [Fact]
    public async Task Poll_Unanswered_ReportsAnErrorAndEscalates()
    {
        _responses.TryRemove("/status.xml", out _);
        var pdu = CreatePdu();

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error, "The PDU never reported an error");
        await WaitUntilAsync(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "unanswered-poll"),
            "The unanswered poll never escalated");
        Assert.Contains(pdu.GetIssues(), issue => issue.Key == "unanswered-poll" && issue.Message.Contains("no response"));

        Respond("/status.xml", StatusBody);
        await WaitUntilAsync(() => pdu.GetOngoingIssues().Count == 0, "The issues were never resolved");
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
    }

    [Fact]
    public async Task Poll_WithAnHttpError_ReportsASingleIssueCarryingTheStatusCode()
    {
        Respond("/status.xml", "401 Unauthorized: Password required", HttpStatusCode.Unauthorized);
        var pdu = CreatePdu();

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error, "The failed poll was never recorded");

        // One driver issue for the failed poll; the Critical "communication" entry comes from DeviceBase.
        var issue = Assert.Single(pdu.GetIssues().Where(i => i.Key != "communication"));
        Assert.Equal("unanswered-poll", issue.Key);
        Assert.Contains("HTTP 401", issue.Message);
    }

    [Fact]
    public async Task Poll_WithInvalidXml_ReportsAnErrorWithoutThrowing()
    {
        Respond("/status.xml", "<html>nope");
        var pdu = CreatePdu();

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error, "The failed poll was never recorded");

        Assert.Contains(pdu.GetIssues(), issue => issue.Message.Contains("not valid XML"));
        Assert.Contains(pdu.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task Poll_WithAThrowingHandler_StaysHealthy()
    {
        var pdu = await CreatePolledPdu();
        Outlet(pdu, 1).CurrentHandlers += _ => throw new InvalidOperationException("disposed control");
        Respond("/status.xml", StatusBody.Replace("<c1>0.0</c1>", "<c1>5.0</c1>"));

        await WaitUntilAsync(() => pdu.Events.Any(e => e.Type == EventType.Error), "The handler failure was never recorded");
        await Task.Delay(PollInterval * 3);

        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
        Assert.Empty(pdu.GetOngoingIssues());
    }

    [Fact]
    public async Task PowerOn_SendsTheCommandAndSetsTheStateOnAcknowledgement()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 6);
        Assert.Equal(PowerState.Off, outlet.PowerState);
        Respond("/status.xml", StatusBody.Replace("<p6>0</p6>", "<p6>1</p6>"));

        outlet.PowerOn();

        await WaitUntilAsync(() => outlet.PowerState == PowerState.On, "The outlet never turned on");
        Assert.Equal(1, CountRequests(CommandPath(6, true)));
        Assert.Contains(pdu.Events, e => e.Type == EventType.Power && e.Info.Contains("power on"));
    }

    [Fact]
    public async Task PowerOff_SendsTheCommand()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 1);
        Respond("/status.xml", StatusBody.Replace("<p1>1</p1>", "<p1>0</p1>"));

        outlet.PowerOff();

        await WaitUntilAsync(() => outlet.PowerState == PowerState.Off, "The outlet never turned off");
        Assert.Equal(1, CountRequests(CommandPath(1, false)));
    }

    [Fact]
    public async Task PowerOn_WithAnUnacknowledgedCommand_DoesNotChangeTheState()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 6);
        DoNotAnswerCommands();

        outlet.PowerOn();

        await WaitUntilAsync(() => pdu.GetIssues().Any(issue => issue.Key == "unacknowledged-command"),
            "The unacknowledged command was never recorded");
        Assert.Equal(1, CountRequests(CommandPath(6, true)));
        Assert.Equal(PowerState.Off, outlet.PowerState);
        Assert.Contains(pdu.GetIssues(), issue => issue.Message.Contains("no response"));
    }

    [Fact]
    public async Task PowerOn_WithARejectedCommand_DoesNotChangeTheState()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 6);
        RespondToCommands("Error");

        outlet.PowerOn();

        await WaitUntilAsync(() => pdu.GetIssues().Any(issue => issue.Key == "unacknowledged-command"),
            "The rejected command was never recorded");
        Assert.Equal(PowerState.Off, outlet.PowerState);
        Assert.Contains(pdu.GetIssues(), issue => issue.Message.Contains("'Error'"));
    }

    [Fact]
    public async Task PowerOn_WithAnHttpError_DoesNotDisturbTheCommunicationState()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 6);
        RespondToCommands("Forbidden", HttpStatusCode.Forbidden);

        outlet.PowerOn();

        await WaitUntilAsync(() => pdu.GetIssues().Any(issue => issue.Key == "unacknowledged-command"),
            "The failed command was never recorded");
        Assert.Equal(PowerState.Off, outlet.PowerState);
        Assert.Contains(pdu.GetIssues(), issue => issue.Message.Contains("HTTP 403"));
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
        Assert.Empty(pdu.GetOngoingIssues());
    }

    [Fact]
    public async Task PowerOn_TwiceConcurrently_AcknowledgesBothCommands()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 6);
        Respond("/status.xml", StatusBody.Replace("<p6>0</p6>", "<p6>1</p6>"));

        outlet.PowerOn();
        outlet.PowerOn();

        await WaitUntilAsync(() => CountRequests(CommandPath(6, true)) == 2, "Both commands were never sent");
        await WaitUntilAsync(() => pdu.Events.Count(e => e.Info.Contains("power on command for outlet Output 6")) == 2,
            "Both commands were never acknowledged");
        Assert.DoesNotContain(pdu.GetIssues(), issue => issue.Key == "unacknowledged-command");
    }

    [Fact]
    public async Task Poll_DispatchedBeforeACommand_DoesNotOverwriteTheCommandedState()
    {
        var pdu = await CreatePolledPdu();
        var outlet = Outlet(pdu, 6);
        _statusDelayMs = 300;
        await Task.Delay(PollInterval * 2); // let a slow poll (carrying p6=0) get in flight

        outlet.PowerOn();
        await WaitUntilAsync(() => outlet.PowerState == PowerState.On, "The outlet never turned on");
        Respond("/status.xml", StatusBody.Replace("<p6>0</p6>", "<p6>1</p6>"));
        await Task.Delay(400); // the stale poll completes in this window

        Assert.Equal(PowerState.On, outlet.PowerState);
    }

    [Fact]
    public async Task Reboot_TurnsTheOutletOffThenOnWithoutPublishingOff()
    {
        var pdu = await CreatePolledPdu();
        pdu.RebootDelay = TimeSpan.FromMilliseconds(200);
        var outlet = Outlet(pdu, 1);
        var states = new ConcurrentQueue<PowerState>();
        outlet.PowerStateHandlers += state => states.Enqueue(state);

        outlet.Reboot();

        await WaitUntilAsync(() => outlet.PowerState == PowerState.Rebooting, "The outlet never entered rebooting");
        Assert.Equal(1, CountRequests(CommandPath(1, false)));
        Assert.Equal(0, CountRequests(CommandPath(1, true)));
        await WaitUntilAsync(() => outlet.PowerState == PowerState.On, "The outlet never came back on");
        Assert.Equal(1, CountRequests(CommandPath(1, true)));
        Assert.Equal([PowerState.Rebooting, PowerState.On], states);
    }

    [Fact]
    public async Task Reboot_KeepsTheRebootingStateWhilePollsReportOff()
    {
        var pdu = await CreatePolledPdu();
        pdu.RebootDelay = TimeSpan.FromMilliseconds(400);
        var outlet = Outlet(pdu, 1);
        Respond("/status.xml", StatusBody.Replace("<p1>1</p1>", "<p1>0</p1>"));

        outlet.Reboot();
        await WaitUntilAsync(() => outlet.PowerState == PowerState.Rebooting, "The outlet never entered rebooting");
        await Task.Delay(150);

        Assert.Equal(PowerState.Rebooting, outlet.PowerState);
    }

    [Fact]
    public async Task Reboot_IsCancelledByAPowerOffDuringTheDelay()
    {
        var pdu = await CreatePolledPdu();
        pdu.RebootDelay = TimeSpan.FromMilliseconds(300);
        var outlet = Outlet(pdu, 1);
        Respond("/status.xml", StatusBody.Replace("<p1>1</p1>", "<p1>0</p1>"));

        outlet.Reboot();
        await WaitUntilAsync(() => outlet.PowerState == PowerState.Rebooting, "The outlet never entered rebooting");
        outlet.PowerOff();
        await WaitUntilAsync(() => outlet.PowerState == PowerState.Off, "The outlet never turned off");
        await Task.Delay(500);

        Assert.Equal(PowerState.Off, outlet.PowerState);
        Assert.Equal(0, CountRequests(CommandPath(1, true)));
        Assert.Contains(pdu.Events, e => e.Info.Contains("cancelled"));
    }

    [Fact]
    public async Task Reboot_WhileAlreadyRebooting_IsIgnored()
    {
        var pdu = await CreatePolledPdu();
        pdu.RebootDelay = TimeSpan.FromMilliseconds(300);
        var outlet = Outlet(pdu, 1);

        outlet.Reboot();
        await WaitUntilAsync(() => outlet.PowerState == PowerState.Rebooting, "The outlet never entered rebooting");
        outlet.Reboot();
        await WaitUntilAsync(() => outlet.PowerState == PowerState.On, "The outlet never came back on");
        await Task.Delay(100);

        Assert.Equal(1, CountRequests(CommandPath(1, false)));
        Assert.Equal(1, CountRequests(CommandPath(1, true)));
    }

    [Fact]
    public async Task PowerOff_OnThePdu_SwitchesEveryChannel()
    {
        var pdu = await CreatePolledPdu();
        var allOff = StatusBody;
        for (var channel = 1; channel <= 8; channel++)
            allOff = allOff.Replace($"<p{channel}>1</p{channel}>", $"<p{channel}>0</p{channel}>");
        Respond("/status.xml", allOff);

        pdu.PowerOff();

        await WaitUntilAsync(() => pdu.Outlets.All(outlet => outlet.PowerState == PowerState.Off), "Not every outlet turned off");
        for (var channel = 1; channel <= 8; channel++)
            Assert.Equal(1, CountRequests(CommandPath(channel, false)));
    }

    [Fact]
    public async Task PowerOn_OnThePdu_SwitchesEveryChannel()
    {
        var pdu = await CreatePolledPdu();
        Respond("/status.xml", StatusBody.Replace("<p6>0</p6>", "<p6>1</p6>"));

        pdu.PowerOn();

        await WaitUntilAsync(() => pdu.Outlets.All(outlet => outlet.PowerState == PowerState.On), "Not every outlet turned on");
        for (var channel = 1; channel <= 8; channel++)
            Assert.Equal(1, CountRequests(CommandPath(channel, true)));
    }

    [Theory]
    [InlineData(1, true, "/cpan.cgi?param=b1&value=1")]
    [InlineData(2, false, "/cpan.cgi?param=b2&value=0")]
    public async Task SetBuzzerEnabled_SendsTheCommand(int circuit, bool enabled, string expectedPath)
    {
        var pdu = await CreatePolledPdu();

        pdu.SetBuzzerEnabled(circuit, enabled);

        await WaitUntilAsync(() => _requests.Contains(expectedPath), "The buzzer command was never sent");
    }

    [Fact]
    public async Task SetBuzzerEnabled_IgnoresUnknownCircuits()
    {
        var pdu = await CreatePolledPdu();

        pdu.SetBuzzerEnabled(3, true);
        await Task.Delay(100);

        Assert.DoesNotContain(_requests, path => path.Contains("param=b3"));
    }

    [Fact]
    public async Task SilenceBuzzer_SendsTheCommand()
    {
        var pdu = await CreatePolledPdu();

        pdu.SilenceBuzzer();

        await WaitUntilAsync(() => _requests.Contains("/cpan.cgi?param=bz&value=0"), "The silence command was never sent");
    }
}
