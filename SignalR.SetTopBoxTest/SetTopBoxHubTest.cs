using AVCoders.Core;
using AVCoders.MediaPlayer;

namespace AVCoders.SignalR.SetTopBox.Tests;

public class SetTopBoxHubTest
{
    private readonly TestSetTopBox _device;
    private readonly SetTopBoxManager _manager;
    private readonly string _groupName;
    private readonly SetTopBoxHubTestHarness _harness;

    public SetTopBoxHubTest()
    {
        _groupName = $"hub-stb-{Guid.NewGuid()}";
        _device = new TestSetTopBox();
        _manager = new SetTopBoxManager(_groupName, _device, "AppleTv");
        SetTopBoxHub.RegisterSetTopBoxManager(_groupName, _manager);
        _harness = SetTopBoxHubTestHarness.CreateHub();
    }

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsState()
    {
        _device.SetPowerStateForTest(PowerState.On);
        _device.SetCommunicationStateForTest(CommunicationState.Okay);

        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSetTopBox(It.Is<SetTopBoxState>(s =>
            s.Name == _groupName
            && s.SourceId == "AppleTv"
            && s.PowerState == PowerState.On
            && s.CommunicationState == CommunicationState.Okay)), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_StillJoinsButSendsNoState()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.JoinGroup(unknown);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), unknown, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.UpdateSetTopBox(It.IsAny<SetTopBoxState>()), Times.Never);
    }

    [Fact]
    public async Task GetState_ReturnsManagerState()
    {
        _device.SetPowerStateForTest(PowerState.Off);

        var state = await _harness.Hub.GetState(_groupName);

        Assert.NotNull(state);
        Assert.Equal(_groupName, state.Name);
        Assert.Equal("AppleTv", state.SourceId);
        Assert.Equal(PowerState.Off, state.PowerState);
        Assert.Equal(TestSetTopBox.DefaultSupportedButtons.Select(b => b.ToString()), state.SupportedButtons);
    }

    [Fact]
    public async Task GetState_UnknownGroup_ReturnsNull()
    {
        var state = await _harness.Hub.GetState($"missing-{Guid.NewGuid()}");

        Assert.Null(state);
    }

    [Theory]
    [InlineData("Up", RemoteButton.Up)]
    [InlineData("up", RemoteButton.Up)]
    [InlineData("ENTER", RemoteButton.Enter)]
    [InlineData("powerOn", RemoteButton.PowerOn)]
    public async Task SendRemoteButton_ParsesNameCaseInsensitivelyAndForwardsToDevice(string name, RemoteButton expected)
    {
        await _harness.Hub.SendRemoteButton(_groupName, name);

        WaitFor(() => _device.SentButtons.Count == 1);
        Assert.Equal([expected], _device.SentButtons);
    }

    [Fact]
    public async Task SendRemoteButton_UnknownButton_DoesNotThrowAndDoesNotSend()
    {
        await _harness.Hub.SendRemoteButton(_groupName, "NotAButton");

        AssertNothingSent();
    }

    [Fact]
    public async Task SendRemoteButton_EmptyButton_DoesNotThrowAndDoesNotSend()
    {
        await _harness.Hub.SendRemoteButton(_groupName, string.Empty);

        AssertNothingSent();
    }

    [Fact]
    public async Task SendRemoteButton_UnsupportedButton_DoesNotSend()
    {
        // Guide is a real RemoteButton but not in the test device's supported list.
        Assert.DoesNotContain(RemoteButton.Guide, _device.SupportedButtons);

        await _harness.Hub.SendRemoteButton(_groupName, "Guide");

        AssertNothingSent();
    }

    [Fact]
    public async Task SendRemoteButton_UnknownGroup_DoesNotSendToOtherManagers()
    {
        await _harness.Hub.SendRemoteButton($"missing-{Guid.NewGuid()}", "Up");

        AssertNothingSent();
    }

    [Fact]
    public async Task PowerOn_ForwardsToDevice()
    {
        await _harness.Hub.PowerOn(_groupName);

        WaitFor(() => _device.PowerOnCallCount == 1);
        Assert.Equal(0, _device.PowerOffCallCount);
    }

    [Fact]
    public async Task PowerOff_ForwardsToDevice()
    {
        await _harness.Hub.PowerOff(_groupName);

        WaitFor(() => _device.PowerOffCallCount == 1);
        Assert.Equal(0, _device.PowerOnCallCount);
    }

    [Fact]
    public async Task PowerOn_UnknownGroup_DoesNotTouchOtherManagers()
    {
        await _harness.Hub.PowerOn($"missing-{Guid.NewGuid()}");

        Assert.False(SpinWait.SpinUntil(() => _device.PowerOnCallCount > 0, 200));
    }

    [Fact]
    public async Task RegisterSetTopBoxManager_ReplacesExistingRegistration()
    {
        var replacementDevice = new TestSetTopBox();
        var replacement = new SetTopBoxManager(_groupName, replacementDevice, "AppleTv");
        SetTopBoxHub.RegisterSetTopBoxManager(_groupName, replacement);

        await _harness.Hub.SendRemoteButton(_groupName, "Down");

        WaitFor(() => replacementDevice.SentButtons.Count == 1);
        Assert.Empty(_device.SentButtons);
        Assert.Equal([RemoteButton.Down], replacementDevice.SentButtons);
    }

    private void AssertNothingSent()
    {
        // The hub dispatches on a worker, so give a rejected send a moment to prove it never arrives.
        Assert.False(SpinWait.SpinUntil(() => _device.SentButtons.Count > 0, 200));
        Assert.Empty(_device.SentButtons);
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }
}
