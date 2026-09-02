using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Display.Tests;

public class DisplayInputTest
{
    // A minimal non-PjLink driver, so the base Display input logic is exercised on its own.
    private class StubDisplay(CommunicationClient client, Input? defaultInput)
        : Display([Input.Hdmi1], "Stub", defaultInput, client, CommandStringFormat.Ascii)
    {
        public int InputCommandsSent;

        protected override void HandleConnectionState(ConnectionState connectionState) { }
        protected override Task DoPoll(CancellationToken token) => Task.CompletedTask;
        protected override void DoPowerOn() { }
        protected override void DoPowerOff() { }
        protected override void DoSetInput(Input input) => InputCommandsSent++;
        protected override void DoSetVolume(int percentage) { }
        protected override void DoSetAudioMute(MuteState state) { }

        public void ReportInput(Input input)
        {
            Input = input;
            ProcessInputResponse();
        }

        public void Support(params Input[] inputs) => SetSupportedInputs(inputs);
    }

    [Fact]
    public void UnsupportedDesiredInput_RaisesOneMinorIssueAndIsNotForced()
    {
        var display = new StubDisplay(TestFactory.CreateTcpClient().Object, Input.Hdmi2);
        display.PowerOn();
        display.ReportInput(Input.Hdmi1);
        int eventsAfterFirstRaise = display.Events.Count;

        for (int i = 0; i < 3; i++)
            display.ReportInput(Input.Hdmi1);

        var issue = Assert.Single(display.GetOngoingIssues());
        Assert.Equal("input", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains("Hdmi2", issue.Message);
        Assert.Equal(0, display.InputCommandsSent);
        Assert.DoesNotContain(display.Events.Skip(eventsAfterFirstRaise), e => e.Type == EventType.Error);
    }

    [Fact]
    public void UnsupportedDesiredInput_IssueResolvesOnceTheInputBecomesSupportedAndMatches()
    {
        var display = new StubDisplay(TestFactory.CreateTcpClient().Object, Input.Hdmi2);
        display.PowerOn();
        display.ReportInput(Input.Hdmi1);
        Assert.Single(display.GetOngoingIssues());

        display.Support(Input.Hdmi1, Input.Hdmi2);
        display.ReportInput(Input.Hdmi1);
        Assert.Equal(1, display.InputCommandsSent);

        display.ReportInput(Input.Hdmi2);
        Assert.Empty(display.GetOngoingIssues());
    }
}
