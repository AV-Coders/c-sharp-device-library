using AVCoders.Core;
using AVCoders.Core.Tests;

#pragma warning disable CS8602 // Dereference of a possibly null reference.

namespace AVCoders.Dsp.Tests;

public class QsysEcpTest
{
    private readonly QsysEcp _dsp;
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<VolumeLevelHandler> _monitorHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();
    private readonly Mock<CommunicationStateHandler> _communicationStateHandler = new();
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private const string GainName = "Gain";
    private const string MuteName = "Mute";
    private const string StringName = "String";
    private const string MeterName = "Meter";
    private const string LevelOnlyName = "Level Only";
    private const string MuteOnlyName = "Mute Only";
    private const string LastConnectSend = "cgsna 4 1000\n";
    private const string StatusRequest = "sg\n";
    private const string DesignName = "Mecca_SC6_v1";
    private const string DesignId = "sNiFioZwAKI6";
    private const string RedeployedDesignId = "yq81K7SOhcDT";
    private const string CommunicationIssueKey = "communication";
    private const string DesignLabel = "Design";
    private const string DesignIdLabel = "Design ID";
    private const string CoreLabel = "Core";
    private const string MissingControlsLabel = "Missing Controls";
    private const int NeverPollMs = 3_600_000;
    private const int WaitTimeoutMs = 5000;
    private const int WaitStepMs = 50;
    private static readonly int[] ChangeGroupIds = [1, 2, 3, 4];

    private static readonly string[] RegistrationSends =
    [
        $"cga 1 \"{GainName}\"\n", $"cg \"{GainName}\"\n",
        $"cga 2 \"{MuteName}\"\n", $"cg \"{MuteName}\"\n",
        $"cga 3 \"{StringName}\"\n", $"cg \"{StringName}\"\n",
        $"cga 4 \"{MeterName}\"\n", $"cg \"{MeterName}\"\n"
    ];

    public QsysEcpTest()
    {
        _dsp = new QsysEcp(_mockClient.Object, "Test", 100);

        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
        _dsp.CommunicationStateHandlers += _communicationStateHandler.Object;
    }

    [Fact]
    public void ControlNames_CanHaveSpaces()
    {
        var gainName = "Gain With Space";
        var muteName = "Mute with Space";
        var stringName = "String with Space";
        _dsp.AddControl(_volumeLevelHandler.Object, gainName);
        _dsp.AddControl(_muteStateHandler.Object, muteName);
        _dsp.AddControl(_stringValueHandler.Object, stringName);
        
        _dsp.SetLevel(gainName, 50);
        _mockClient.Verify(x => x.Send($"csp \"{gainName}\" 0.5\n"));
        
        _dsp.SetAudioMute(muteName, MuteState.On);
        _mockClient.Verify(x => x.Send($"css \"{muteName}\" muted\n"));
        
        _dsp.SetValue(stringName, "hello");
        _mockClient.Verify(x => x.Send($"css \"{stringName}\" hello\n"));
        
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{gainName}\" \"-6.40dB\" -6.4 0.989744");
        _volumeLevelHandler.Verify(x => x.Invoke(98));
        Assert.Equal(98, _dsp.GetLevel(gainName));
        
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{muteName}\" \"unmuted\" 1 1");
        _muteStateHandler.Verify(x => x.Invoke(MuteState.Off));
        Assert.Equal(MuteState.Off, _dsp.GetAudioMute(muteName));
        
        
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"This is a string\" 5 0.571429");

        Assert.Equal("This is a string", _dsp.GetValue(StringName));
    }

    [Fact]
    public void SetLevel_SendsTheCommand()
    {
        _dsp.SetLevel(GainName, 34);

        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.34\n"));
    }

    [Fact]
    public void LevelUp_IncrementsTheLevel()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelUp(GainName);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.92\n"));
    }

    [Fact]
    public void LevelUp_ObservesTheSpecifiedAmount()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelUp(GainName, 2);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.93\n"));
    }

    [Fact]
    public void LevelDown_DecrementsTheLevel()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelDown(GainName);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.9\n"));
    }

    [Fact]
    public void LevelDown_ObservesTheSpecifiedAmount()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelDown(GainName, 3);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.88\n"));
    }

    [Fact]
    public void GetLevel_IgnoresUnknownControlNames()
    {
        Assert.Equal(0, _dsp.GetLevel("foo"));
    }

    [Fact]
    public void GetLevel_ReturnsTheVolumePercentage()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        Assert.Equal(91, _dsp.GetLevel(GainName));
    }

    [Fact]
    public void SetAudioMute_SendsTheCommand()
    {
        _dsp.SetAudioMute(MuteName, MuteState.On);
        _mockClient.Verify(x => x.Send($"css \"{MuteName}\" muted\n"));
    }

    [Fact]
    public void GetAudioMute_IgnoresUnknownControlNames()
    {
        Assert.Equal(MuteState.Unknown, _dsp.GetAudioMute("foo"));
    }

    [Fact]
    public void GetAudioMute_ReturnsTheMuteState()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"muted\" 1 1");

        Assert.Equal(MuteState.On, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void ToggleAudioMute_SendsTheCommand()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"unmuted\" 1 1");

        _dsp.ToggleAudioMute(MuteName);

        _mockClient.Verify(x => x.Send($"css \"{MuteName}\" muted\n"));
    }

    [Fact]
    public void SetValue_SendsTheCommand()
    {
        _dsp.SetValue(StringName, "hello");
        _mockClient.Verify(x => x.Send($"css \"{StringName}\" hello\n"));
    }

    [Fact]
    public void GetValue_IgnoresUnknownControlNames()
    {
        Assert.Equal("", _dsp.GetValue("foo"));
    }

    [Fact]
    public void GetValue_ReturnsTheValue()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"5\" 5 0.571429");

        Assert.Equal("5", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_GivenAFaderValue_UpdatesTheFaderLevel()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.989744");

        Assert.Equal(98, _dsp.GetLevel(GainName));
    }

    [Fact]
    public void HandleResponse_GivenAFaderValue_CallsTheDelegate()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.989744");

        _volumeLevelHandler.Verify(x => x.Invoke(98));
    }

    [Theory]
    [InlineData("unmuted")]
    [InlineData("false")]
    public void HandleResponse_GivenAMuteValue_UpdatesTheMuteState(string muteState)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"{muteState}\" 1 1");

        Assert.Equal(MuteState.Off, _dsp.GetAudioMute(MuteName));
    }
    
    [Theory]
    [InlineData("muted")]
    [InlineData("true")]
    public void HandleResponse_GivenAMuteValue_UpdatesTheMuteStateForMuted(string muteState)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"{muteState}\" 1 1");

        Assert.Equal(MuteState.On, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void HandleResponse_GivenAMuteValue_CallsTheDelegate()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"unmuted\" 1 1");

        _muteStateHandler.Verify(x => x.Invoke(MuteState.Off));
    }

    [Fact]
    public void HandleResponse_GivenAStringValue_UpdatesTheValue()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"This is a string\" 5 0.571429");

        Assert.Equal("This is a string", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_GivenAStringValue_CallsTheDelegate()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"5\" 5 0.571429");

        _stringValueHandler.Verify(x => x.Invoke("5"));
    }

    [Theory]
    [InlineData("cvv \"rec_meter\" 2 \"-45.3dB\" \"-11.8dB\" 2 -45.2769 -11.759 2 0.0736156 0.764821", 7)]
    [InlineData("cvv \"rec_meter\" 2 \"-52.0dB\" \"-38.0dB\" 2 -51.9812 -38.039 2 0.0400942 0.11961", 4)]
    [InlineData("cvv \"rec_meter\" 2 \"-2.17dB\" \"2.81dB\" 2 -2.17039 2.80927 2 0.878296 0.914046", 87)]
    [InlineData("cvv \"rec_meter\" 2 \"-31.1dB\" \"1.21dB\" 2 -31.1223 1.20927 2 0.188777 0.906046", 18)]
    public void HandleResponse_GivenAMeterResponse_UpdatesTheLevel(string response, int expectedLevel)
    {
        _dsp.AddMonitor(_monitorHandler.Object, "rec_meter");
        _mockClient.Object.ResponseHandlers.Invoke(response);
        
        _monitorHandler.Verify(x => x.Invoke(expectedLevel));
    }

    [Fact]
    public void HandleResponse_GivenAnInvalidResponse_DoesNothing()
    {
        _mockClient.Object.ResponseHandlers.Invoke("cv \"gai ");
    }

    [Fact]
    public void HandleResponse_GivenABadId_DoesNotReportACommunicationError()
    {
        _mockClient.Object.ResponseHandlers.Invoke("cv \"Zone33BGMMute\"");
        _mockClient.Object.ResponseHandlers.Invoke("bad_id \"Zone33BGMMute\"");

        Assert.NotEqual(CommunicationState.Error, _dsp.CommunicationState);
        _communicationStateHandler.Verify(x => x.Invoke(CommunicationState.Error), Times.Never);
        Assert.DoesNotContain(_dsp.GetOngoingIssues(), i => i.Key == CommunicationIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenBadIds_ListsTheMissingControlsUntilEachResponds()
    {
        _mockClient.Object.ResponseHandlers.Invoke("bad_id \"Zone2\"\r\nbad_id \"Zone1\"\r\n");

        Assert.Equal(new DeviceDetail(MissingControlsLabel, "Zone1, Zone2", DetailTone.Warning), Detail(_dsp, MissingControlsLabel));

        _mockClient.Object.ResponseHandlers.Invoke("cv \"Zone1\" \"unmuted\" 1 1\r\n");
        Assert.Equal(new DeviceDetail(MissingControlsLabel, "Zone2", DetailTone.Warning), Detail(_dsp, MissingControlsLabel));

        _mockClient.Object.ResponseHandlers.Invoke("cv \"Zone2\" \"unmuted\" 1 1\r\n");
        Assert.Null(Detail(_dsp, MissingControlsLabel));
    }

    [Fact]
    public void HandleResponse_GivenABadId_RaisesAnIssueCarryingTheRawLine()
    {
        const string reply = "bad_id \"Zone33BGMMute\"";
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), i => i.Key == $"{QsysEcp.BadControlIssuePrefix}Zone33BGMMute");
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Contains(reply, issue.Message);
    }

    [Fact]
    public void HandleResponse_GivenAnEmptyString_PassesItOn()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"abc\" 0 0\r\n");
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"\" 0 0\r\n");

        _stringValueHandler.Verify(x => x.Invoke(""));
        Assert.Equal("", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_GivenAStatusReply_ShowsTheDesignAndCore()
    {
        _mockClient.Object.ResponseHandlers.Invoke(Status(DesignId));

        Assert.Equal(new DeviceDetail(DesignLabel, DesignName), Detail(_dsp, DesignLabel));
        Assert.Equal(new DeviceDetail(DesignIdLabel, DesignId), Detail(_dsp, DesignIdLabel));
        Assert.Equal(new DeviceDetail(CoreLabel, "Primary, active"), Detail(_dsp, CoreLabel));
        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public void HandleResponse_GivenAStandbyStatus_RaisesAStandbyIssueUntilTheCoreIsActive()
    {
        string standby = Status(DesignId, isPrimary: 0, isActive: 0);
        _mockClient.Object.ResponseHandlers.Invoke(standby);

        Assert.Equal(new DeviceDetail(CoreLabel, "Backup, standby", DetailTone.Error), Detail(_dsp, CoreLabel));
        var issue = Assert.Single(_dsp.GetOngoingIssues(), i => i.Key == QsysEcp.CoreStandbyIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Contains(standby.TrimEnd(), issue.Message);

        _mockClient.Object.ResponseHandlers.Invoke(Status(DesignId));

        Assert.DoesNotContain(_dsp.GetOngoingIssues(), i => i.Key == QsysEcp.CoreStandbyIssueKey);
        Assert.Equal(new DeviceDetail(CoreLabel, "Primary, active"), Detail(_dsp, CoreLabel));
    }

    [Fact]
    public void HandleResponse_GivenAnUnrecognisedStatus_RaisesAMomentaryIssueWithTheRawLine()
    {
        const string reply = "sr Mecca 1 1";
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n");

        var issue = Assert.Single(_dsp.GetIssues(), i => i.Key == QsysEcp.StatusParseIssueKey);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Contains(reply, issue.Message);
        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Theory]
    [InlineData("core_not_active", QsysEcp.CoreStandbyIssueKey, IssueSeverity.Critical)]
    [InlineData("too_many_change_groups", QsysEcp.ChangeGroupsIssueKey, IssueSeverity.Major)]
    [InlineData("login_required", QsysEcp.LoginIssueKey, IssueSeverity.Critical)]
    [InlineData("login_failed", QsysEcp.LoginIssueKey, IssueSeverity.Critical)]
    public void HandleResponse_GivenAnErrorReply_RaisesAnOngoingIssueWithTheRawLine(string reply, string key, IssueSeverity severity)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), i => i.Key == key);
        Assert.Equal(severity, issue.Severity);
        Assert.Contains(reply, issue.Message);
    }

    [Theory]
    [InlineData("bad_command \"CG\"", QsysEcp.BadCommandIssueKey, IssueSeverity.Minor)]
    [InlineData("control_read_only \"Gain\"", QsysEcp.ReadOnlyIssueKey, IssueSeverity.Minor)]
    [InlineData("core_read_only \"Gain\"", QsysEcp.ReadOnlyIssueKey, IssueSeverity.Minor)]
    [InlineData("bad_change_group_handle 2", QsysEcp.BadChangeGroupIssueKey, IssueSeverity.Minor)]
    [InlineData("rc", QsysEcp.ConnectionClosedIssueKey, IssueSeverity.Major)]
    public void HandleResponse_GivenAnIncidentReply_RaisesAMomentaryIssueWithTheRawLine(string reply, string key, IssueSeverity severity)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n");

        var issue = Assert.Single(_dsp.GetIssues(), i => i.Key == key);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(severity, issue.Severity);
        Assert.Contains(reply, issue.Message);
        Assert.Empty(_dsp.GetOngoingIssues());
    }

    [Fact]
    public void HandleResponse_GivenRc_AddsAConnectionEvent()
    {
        _mockClient.Object.ResponseHandlers.Invoke("rc\r\n");

        Assert.Contains(_dsp.Events, e => e.Type == EventType.Connection && e.Info.Contains("rc"));
    }

    [Theory]
    [InlineData("login_required")]
    [InlineData("login_failed")]
    public void HandleResponse_GivenALoginReply_ReportsAnErrorUntilTheCoreAnswersAStatus(string reply)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n");

        Assert.Equal(CommunicationState.Error, _dsp.CommunicationState);

        _mockClient.Object.ResponseHandlers.Invoke(Status(DesignId));

        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
        Assert.DoesNotContain(_dsp.GetOngoingIssues(), i => i.Key == QsysEcp.LoginIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenLoginSuccess_ReportsOkay()
    {
        _mockClient.Object.ResponseHandlers.Invoke("login_success\r\n");

        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public void HandleResponse_GivenAnUnknownReply_RaisesNothing()
    {
        _mockClient.Object.ResponseHandlers.Invoke("cgpa\r\nsomething else\r\n");

        Assert.Empty(_dsp.GetIssues());
        Assert.Equal(CommunicationState.Unknown, _dsp.CommunicationState);
    }

    [Theory]
    [InlineData("bad_command \"CG\"", QsysEcp.BadCommandIssueKey)]
    [InlineData("control_read_only \"Gain\"", QsysEcp.ReadOnlyIssueKey)]
    public void HandleResponse_GivenARejectionThreeTimesInARow_EscalatesUntilAControlValueArrives(string reply, string key)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n{reply}\r\n{reply}\r\n");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), i => i.Key == key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);

        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.989744\r\n");

        Assert.DoesNotContain(_dsp.GetOngoingIssues(), i => i.Key == key);
    }

    [Theory]
    [InlineData("bad_command \"CG\"", QsysEcp.BadCommandIssueKey)]
    [InlineData("control_read_only \"Gain\"", QsysEcp.ReadOnlyIssueKey)]
    public void HandleResponse_GivenAControlValueBetweenRejections_DoesNotEscalate(string reply, string key)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n{reply}\r\n");
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.989744\r\n");
        _mockClient.Object.ResponseHandlers.Invoke($"{reply}\r\n");

        Assert.DoesNotContain(_dsp.GetOngoingIssues(), i => i.Key == key);
    }

    [Fact]
    public void Constructor_BeforeTheFirstConnect_ReportsUnknownNotError()
    {
        var client = new TestTcpClient();
        var dsp = new QsysEcp(client, "Test");

        client.SetConnectionState(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Unknown, dsp.CommunicationState);
        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == CommunicationIssueKey);
    }

    [Fact]
    public void HandleConnectionState_GivenADisconnect_ReportsUnknown()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new QsysEcp(client, "Test");
        client.ResponseHandlers.Invoke(Status(DesignId));
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);

        client.SetConnectionState(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Unknown, dsp.CommunicationState);
    }

    [Fact]
    public async Task Poll_GivenAnUnansweredStatusRequest_RaisesAMomentaryIssueAtTheNextPoll()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysEcp(client);

        await dsp.PollNow();
        Assert.Equal(1, Count(client, StatusRequest));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == QsysEcp.UnansweredStatusIssueKey);

        await dsp.PollNow();

        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == QsysEcp.UnansweredStatusIssueKey);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal(0, client.Reconnects);
    }

    [Fact]
    public async Task Poll_GivenAnsweredStatusRequests_RaisesNothing()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysEcp(client);

        for (int i = 0; i < QsysEcp.MaxUnansweredStatusRequests + 1; i++)
        {
            await dsp.PollNow();
            client.ResponseHandlers.Invoke(Status(DesignId));
        }

        Assert.Equal(QsysEcp.MaxUnansweredStatusRequests + 1, Count(client, StatusRequest));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == QsysEcp.UnansweredStatusIssueKey);
        Assert.Equal(0, client.Reconnects);
    }

    [Fact]
    public async Task Poll_GivenThreeMissedStatusReplies_ReportsAnErrorAndReconnectsUntilTheCoreAnswers()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysEcp(client);

        for (int i = 0; i < QsysEcp.MaxUnansweredStatusRequests; i++)
            await dsp.PollNow();
        Assert.Equal(0, client.Reconnects);
        Assert.NotEqual(CommunicationState.Error, dsp.CommunicationState);

        await dsp.PollNow();

        Assert.Equal(1, client.Reconnects);
        Assert.Equal(CommunicationState.Error, dsp.CommunicationState);
        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == QsysEcp.UnansweredStatusIssueKey);
        Assert.Equal(IssueSeverity.Major, issue.Severity);

        await dsp.PollNow();
        client.ResponseHandlers.Invoke(Status(DesignId));

        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);
        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == QsysEcp.UnansweredStatusIssueKey);
        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == CommunicationIssueKey);
    }

    [Fact]
    public async Task Poll_GivenLoginRequiredReplies_DoesNotReconnect()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysEcp(client);

        for (int i = 0; i < QsysEcp.MaxUnansweredStatusRequests + 1; i++)
        {
            await dsp.PollNow();
            client.ResponseHandlers.Invoke("login_required\r\n");
        }

        Assert.Equal(0, client.Reconnects);
        Assert.Single(dsp.GetOngoingIssues(), i => i.Key == QsysEcp.LoginIssueKey);
    }

    [Fact]
    public async Task HandleResponse_GivenANewDesignId_RebuildsTheChangeGroups()
    {
        var (client, dsp) = await CreateConnectedDsp();
        await dsp.PollNow();
        client.ResponseHandlers.Invoke(Status(DesignId));
        await dsp.PollNow();
        client.ResponseHandlers.Invoke(Status(DesignId));
        Assert.Equal(1, Count(client, "cgc 1\n"));

        await dsp.PollNow();
        client.ResponseHandlers.Invoke(Status(RedeployedDesignId));
        await WaitUntil(() => Count(client, LastConnectSend) == 2);

        Assert.Contains(dsp.Events, e => e.Info == $"Design redeployed: {DesignName} ({RedeployedDesignId})");
        Assert.All(ChangeGroupIds, id => Assert.Equal(2, Count(client, $"cgc {id}\n")));
        Assert.All(ChangeGroupIds, id => Assert.Equal(2, Count(client, $"cgclr {id}\n")));
        Assert.All(RegistrationSends.Where(s => s.StartsWith("cga ")), s => Assert.Equal(2, Count(client, s)));
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("cgd "));
    }

    [Fact]
    public async Task HandleConnectionState_GivenAReconnect_DoesNotTreatTheNextDesignIdAsARedeploy()
    {
        var (client, dsp) = await CreateConnectedDsp();
        client.ResponseHandlers.Invoke(Status(DesignId));

        client.SetConnectionState(ConnectionState.Disconnected);
        client.SetConnectionState(ConnectionState.Connected);
        await WaitUntil(() => Count(client, LastConnectSend) == 2);
        await dsp.PollNow();
        client.ResponseHandlers.Invoke(Status(RedeployedDesignId));

        Assert.DoesNotContain(dsp.Events, e => e.Info.StartsWith("Design redeployed"));
        Assert.Equal(new DeviceDetail(DesignIdLabel, RedeployedDesignId), Detail(dsp, DesignIdLabel));
    }

    [Fact]
    public async Task HandleResponse_GivenBadChangeGroupHandles_RebuildsAtMostOncePerPollCycle()
    {
        var (client, dsp) = await CreateConnectedDsp();

        client.ResponseHandlers.Invoke("bad_change_group_handle 2\r\n");
        Assert.Equal(1, Count(client, "cgc 1\n"));

        await dsp.PollNow();
        client.ResponseHandlers.Invoke("bad_change_group_handle 2\r\nbad_change_group_handle 3\r\n");
        await WaitUntil(() => Count(client, LastConnectSend) == 2);

        Assert.Equal(2, Count(client, "cgc 1\n"));
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("cgd "));
    }

    [Fact]
    public async Task HandleResponse_GivenTooManyChangeGroups_KeepsTheIssueUntilAStatusFollowsASuccessfulBuild()
    {
        var (client, dsp) = await CreateConnectedDsp();
        client.ResponseHandlers.Invoke("too_many_change_groups\r\n");
        client.ResponseHandlers.Invoke(Status(DesignId));

        Assert.Single(dsp.GetOngoingIssues(), i => i.Key == QsysEcp.ChangeGroupsIssueKey);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.SetConnectionState(ConnectionState.Connected);
        await WaitUntil(() => Count(client, LastConnectSend) == 2);
        client.ResponseHandlers.Invoke(Status(DesignId));

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == QsysEcp.ChangeGroupsIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenABadId_RaisesAnOngoingIssue_ResolvedWhenTheControlResponds()
    {
        _mockClient.Object.ResponseHandlers.Invoke("bad_id \"Zone33BGMMute\"");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), i => i.Key == "bad-control:Zone33BGMMute");
        Assert.Equal(IssueStatus.Ongoing, issue.Status);

        _mockClient.Object.ResponseHandlers.Invoke("cv \"Zone33BGMMute\" \"unmuted\" 1 1");

        Assert.DoesNotContain(_dsp.GetOngoingIssues(), i => i.Key == "bad-control:Zone33BGMMute");
        Assert.Contains(_dsp.GetIssues(),
            i => i.Key == "bad-control:Zone33BGMMute" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void HandleResponse_GivenABatchResponse_ParsesEverythingCorrectly()
    {
        _dsp.AddControl(_stringValueHandler.Object, "Zone30BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone10BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone17BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone18BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone1BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone2BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone3BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone4BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone5BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone6BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone7BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone9BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone11BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone12BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone13BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone14BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone15BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone19BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone20BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone21BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone22BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone23BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone24BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone25BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone26BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone27BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone28BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone29BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone31BGMSelect");
        var response =
            "cv \"Zone30BGMSelect\" \"7\" 7 0.857143\ncv \"Zone10BGMSelect\" \"3\" 3 0.285714\ncv \"Zone17BGMSelect\" \"4\" 4 0.428571\ncv \"Zone18BGMSelect\" \"1\" 1 0\ncv \"Zone1BGMSelect\" \"1\" 1 0\ncv \"Zone2BGMSelect\" \"2\" 2 0.142857\ncv \"Zone3BGMSelect\" \"3\" 3 0.285714\ncv \"Zone4BGMSelect\" \"4\" 4 0.428571\ncv \"Zone5BGMSelect\" \"6\" 6 0.714286\ncv \"Zone6BGMSelect\" \"4\" 4 0.428571\ncv \"Zone7BGMSelect\" \"4\" 4 0.428571\ncv \"Zone9BGMSelect\" \"2\" 2 0.142857\ncv \"Zone11BGMSelect\" \"1\" 1 0\ncv \"Zone12BGMSelect\" \"2\" 2 0.142857\ncv \"Zone13BGMSelect\" \"1\" 1 0\ncv \"Zone14BGMSelect\" \"2\" 2 0.142857\ncv \"Zone15BGMSelect\" \"3\" 3 0.285714\ncv \"Zone19BGMSelect\" \"6\" 6 0.714286\ncv \"Zone20BGMSelect\" \"4\" 4 0.428571\ncv \"Zone21BGMSelect\" \"3\" 3 0.285714\ncv \"Zone22BGMSelect\" \"2\" 2 0.142857\ncv \"Zone23BGMSelect\" \"1\" 1 0\ncv \"Zone24BGMSelect\" \"6\" 6 0.714286\ncv \"Zone25BGMSelect\" \"4\" 4 0.428571\ncv \"Zone26BGMSelect\" \"4\" 4 0.428571\ncv \"Zone27BGMSelect\" \"4\" 4 0.428571\ncv \"Zone28BGMSelect\" \"4\" 4 0.428571\ncv \"Zone29BGMSelect\" \"4\" 4 0.428571\ncv \"Zone31BGMSelect\" \"6\" 6 0.71428\n";
        _mockClient.Object.ResponseHandlers.Invoke(response);
        
        _stringValueHandler.Verify(x => x.Invoke("7"), Times.Once);
        _stringValueHandler.Verify(x => x.Invoke("3"), Times.Exactly(4));
        _stringValueHandler.Verify(x => x.Invoke("1"), Times.Exactly(5));
    }

    [Fact]
    public void AddControl_WhileDisconnected_SendsNoChangeGroupAddOrGet()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new QsysEcp(client, "Test");

        RegisterOneOfEachControl(dsp);

        Assert.DoesNotContain(client.Sent, IsChangeGroupAddOrGet);
    }

    [Fact]
    public async Task Connecting_AddsEachRegisteredControlToItsChangeGroupAndGetsItOnce()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new QsysEcp(client, "Test");
        RegisterOneOfEachControl(dsp);

        client.SetConnectionState(ConnectionState.Connected);
        await WaitUntil(() => client.Sent.Contains(LastConnectSend) && RegistrationSends.All(client.Sent.Contains));

        Assert.Equal(RegistrationSends.Order(), client.Sent.Where(IsChangeGroupAddOrGet).Order());
    }

    [Fact]
    public void AddControl_WhileConnected_SendsTheChangeGroupAddAndGetStraightAway()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new QsysEcp(client, "Test");

        RegisterOneOfEachControl(dsp);

        Assert.All(RegistrationSends, message => Assert.Contains(message, client.Sent));
    }

    [Fact]
    public void QsysVolumeOnlyControl_AddsOnlyTheLevelToAChangeGroup()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new QsysEcp(client, "Test");

        _ = new QsysVolumeOnlyControl("Program", LevelOnlyName, VolumeType.Speaker, dsp);

        Assert.Contains($"cga 1 \"{LevelOnlyName}\"\n", client.Sent);
        Assert.DoesNotContain(client.Sent, message => message.StartsWith("cga 2 "));
    }

    [Fact]
    public void QsysVolumeOnlyControl_UpdatesTheVolumeFromTheLevel()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var control = new QsysVolumeOnlyControl("Program", LevelOnlyName, VolumeType.Speaker, new QsysEcp(client, "Test"));

        client.ResponseHandlers.Invoke($"cv \"{LevelOnlyName}\" \"-6.40dB\" -6.4 0.989744");

        Assert.Equal(98, control.Volume);
    }

    [Fact]
    public void QsysVolumeOnlyControl_SetLevel_SendsTheCommand()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var control = new QsysVolumeOnlyControl("Program", LevelOnlyName, VolumeType.Speaker, new QsysEcp(client, "Test"));

        control.SetLevel(34);

        Assert.Equal([$"csp \"{LevelOnlyName}\" 0.34\n"], client.Sent);
    }

    [Fact]
    public void QsysVolumeOnlyControl_MuteMethods_SendNothing()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var control = new QsysVolumeOnlyControl("Program", LevelOnlyName, VolumeType.Speaker, new QsysEcp(client, "Test"));

        control.SetAudioMute(MuteState.On);
        control.ToggleAudioMute();

        Assert.Empty(client.Sent);
    }

    [Fact]
    public void QsysVolumeOnlyControl_SupportsLevelButNotMute()
    {
        var control = new QsysVolumeOnlyControl("Program", LevelOnlyName, VolumeType.Speaker, _dsp);

        Assert.True(control.SupportsLevel);
        Assert.False(control.SupportsMute);
    }

    [Fact]
    public void QsysMuteOnlyControl_AddsOnlyTheMuteToAChangeGroup()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new QsysEcp(client, "Test");

        _ = new QsysMuteOnlyControl("Privacy", MuteOnlyName, VolumeType.Microphone, dsp);

        Assert.Contains($"cga 2 \"{MuteOnlyName}\"\n", client.Sent);
        Assert.DoesNotContain(client.Sent, message => message.StartsWith("cga 1 "));
    }

    [Fact]
    public void QsysMuteOnlyControl_UpdatesTheMuteStateFromTheMute()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var control = new QsysMuteOnlyControl("Privacy", MuteOnlyName, VolumeType.Microphone, new QsysEcp(client, "Test"));

        client.ResponseHandlers.Invoke($"cv \"{MuteOnlyName}\" \"muted\" 1 1");

        Assert.Equal(MuteState.On, control.MuteState);
    }

    [Fact]
    public void QsysMuteOnlyControl_SetAudioMute_SendsTheCommand()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var control = new QsysMuteOnlyControl("Privacy", MuteOnlyName, VolumeType.Microphone, new QsysEcp(client, "Test"));

        control.SetAudioMute(MuteState.On);

        Assert.Equal([$"css \"{MuteOnlyName}\" muted\n"], client.Sent);
    }

    [Fact]
    public void QsysMuteOnlyControl_LevelMethods_SendNothing()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var control = new QsysMuteOnlyControl("Privacy", MuteOnlyName, VolumeType.Microphone, new QsysEcp(client, "Test"));

        control.LevelUp(1);
        control.LevelDown(1);
        control.SetLevel(50);

        Assert.Empty(client.Sent);
    }

    [Fact]
    public void QsysMuteOnlyControl_SupportsMuteButNotLevel()
    {
        var control = new QsysMuteOnlyControl("Privacy", MuteOnlyName, VolumeType.Microphone, _dsp);

        Assert.True(control.SupportsMute);
        Assert.False(control.SupportsLevel);
    }

    private void RegisterOneOfEachControl(QsysEcp dsp)
    {
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        dsp.AddControl(_stringValueHandler.Object, StringName);
        dsp.AddMonitor(_monitorHandler.Object, MeterName);
    }

    private async Task<(TestTcpClient Client, TestableQsysEcp Dsp)> CreateConnectedDsp()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new TestableQsysEcp(client);
        RegisterOneOfEachControl(dsp);
        client.SetConnectionState(ConnectionState.Connected);
        await WaitUntil(() => client.Sent.Contains(LastConnectSend));
        return (client, dsp);
    }

    private class TestableQsysEcp(TcpClient client) : QsysEcp(client, "Test", NeverPollMs)
    {
        public Task PollNow() => Poll(CancellationToken.None);
    }

    private static string Status(string designId, int isPrimary = 1, int isActive = 1) =>
        $"sr \"{DesignName}\" \"{designId}\" {isPrimary} {isActive}\r\n";

    private static DeviceDetail? Detail(QsysEcp dsp, string label) => dsp.Details.SingleOrDefault(d => d.Label == label);

    private static int Count(TestTcpClient client, string message) => client.Sent.Count(s => s == message);

    private static bool IsChangeGroupAddOrGet(string message) => message.StartsWith("cga ") || message.StartsWith("cg ");

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(WaitTimeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(WaitStepMs);
        Assert.True(condition());
    }
}