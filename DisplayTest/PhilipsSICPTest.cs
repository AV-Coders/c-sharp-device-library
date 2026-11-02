using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public class PhilipsSICPTest
{
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly PhilipsSICP _display;
    private readonly byte _displayId = 0x01;
    private readonly byte _groupId = 0x01;

    public PhilipsSICPTest()
    {
        _display = new PhilipsSICP(_mockClient.Object, _displayId, _groupId, "Test", Input.Hdmi1, 23, _time);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = [0x06, 0x01, 0x01, 0x18, 0x02, 0x1C];
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        byte[] expectedPowerOnCommand = [0x06, 0x01, 0x01, 0x18, 0x01, 0x1F];
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerCommands_UseTheConfiguredDisplayId()
    {
        PhilipsSICP display5 = new PhilipsSICP(_mockClient.Object, 0x05, 0x01, "Test display", Input.Hdmi1, 23, _time);

        display5.PowerOn();
        display5.PowerOff();

        _mockClient.Verify(x => x.Send(new byte[] { 0x06, 0x05, 0x01, 0x18, 0x02, 0x18 }), Times.Once);
        _mockClient.Verify(x => x.Send(new byte[] { 0x06, 0x05, 0x01, 0x18, 0x01, 0x1B }), Times.Once);
    }

    [Theory]
    [InlineData(Input.Hdmi1, new byte[] { 0x09, 0x01, 0x01, 0xAC, 0x0D, 0x00, 0x01, 0x00, 0xA9 })]
    [InlineData(Input.Hdmi2, new byte[] { 0x09, 0x01, 0x01, 0xAC, 0x06, 0x00, 0x01, 0x00, 0xA2 })]
    [InlineData(Input.Hdmi3, new byte[] { 0x09, 0x01, 0x01, 0xAC, 0x0F, 0x00, 0x01, 0x00, 0xAB })]
    [InlineData(Input.Hdmi4, new byte[] { 0x09, 0x01, 0x01, 0xAC, 0x19, 0x00, 0x01, 0x00, 0xBD })]
    public void SetInput_SendsTheCommand(Input input, byte[] expectedCommand)
    {
        _display.SetInput(input);
        
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, new byte[] { 0x06, 0x01, 0x01, 0x47, 0x01, 0x40 })]
    [InlineData(MuteState.Off, new byte[] { 0x06, 0x01, 0x01, 0x47, 0x00, 0x41 })]
    public void SetAudioMute_SendsTheCommand(MuteState state, byte[] expectedState)
    {
        _display.SetAudioMute(state);
        _mockClient.Verify(x => x.Send(expectedState), Times.Once);
    }

    [Theory]
    [InlineData(0, new byte[] { 0x07, 0x01, 0x01, 0x44, 0x00, 0x00, 0x43 })]
    [InlineData(50, new byte[] { 0x07, 0x01, 0x01, 0x44, 0x32, 0x32, 0x43 })]
    [InlineData(100, new byte[] { 0x07, 0x01, 0x01, 0x44, 0x64, 0x64, 0x43 })]
    public void SetVolume_SendsTheCommand(int volume, byte[] expectedCommand)
    {
        _display.SetVolume(volume);
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x02, 0x1D }, PowerState.On)]
    [InlineData(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x01, 0x1E }, PowerState.Off)]
    public void HandleResponse_UpdatesThePowerState(byte[] response, PowerState expectedState)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        Assert.Equal(expectedState, _display.PowerState);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x02, 0x01 })]
    [InlineData(new byte[] { 0x06, 0x01, 0x01, 0x19 })]
    [InlineData(new byte[] { 0x07, 0x01, 0x01, 0x19, 0x02, 0x1D })]
    public void HandleResponse_IgnoresMalformedResponses(byte[] response)
    {
        var stateBefore = _display.PowerState;

        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke(response));

        Assert.Null(exception);
        Assert.Equal(stateBefore, _display.PowerState);
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x02, 0x1D });

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_GivenAMalformedResponse_ReportsAnError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x00 });

        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_HandlesAReplySplitAcrossTwoReceives()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x06, 0x01, 0x01 });
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x19, 0x02, 0x1D });

        Assert.Equal(PowerState.On, _display.PowerState);
    }

    [Fact]
    public void HandleResponse_HandlesTwoRepliesInOneReceive()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x02, 0x1D, 0x06, 0x01, 0x01, 0x45, 0x32, 0x71 });

        Assert.Equal(PowerState.On, _display.PowerState);
        Assert.Equal(50, _display.Volume);
    }

    [Fact]
    public void HandleResponse_GivenAWrongChecksum_IgnoresTheReply()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x06, 0x01, 0x01, 0x19, 0x02, 0x00 });

        Assert.Equal(PowerState.Unknown, _display.PowerState);
        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_IgnoresAReplyFromAnotherMonitor()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x06, 0x02, 0x01, 0x19, 0x02, 0x1E });

        Assert.Equal(PowerState.Unknown, _display.PowerState);
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
    }

    private static readonly TimeSpan StartStep = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BeforeTheSecondPoll = TimeSpan.FromSeconds(20);
    private static readonly byte[] PowerQuery = [0x05, 0x01, 0x00, 0x19, 0x1D];

    private static void SetConnectionState(Mock<TcpClient> mock, ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mock.Object, state);

    private static int PowerPollsSent(Mock<TcpClient> client) =>
        client.Invocations.Count(i => i.Method.Name == "Send" && i.Arguments[0] is byte[] frame && frame.SequenceEqual(PowerQuery));

    private static Task UntilPowerPolled(FakeTimeProvider time, Mock<TcpClient> client, int times) =>
        Wait.UntilAsync(time, StartStep, () => PowerPollsSent(client) >= times, $"The power poll was sent fewer than {times} times");

    private static Task AdvanceAsync(FakeTimeProvider time, TimeSpan duration)
    {
        var until = time.GetUtcNow() + duration;
        return Wait.UntilAsync(time, WindowStep, () => time.GetUtcNow() >= until, "The fake clock did not advance");
    }

    [Fact]
    public async Task Connected_SendsOnePowerPollBeforeTheNextPoll()
    {
        var time = new FakeTimeProvider();
        var client = TestFactory.CreateTcpClient();
        _ = new PhilipsSICP(client.Object, _displayId, _groupId, "Test", Input.Hdmi1, 23, time);

        SetConnectionState(client, ConnectionState.Connected);
        client.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await UntilPowerPolled(time, client, 1);
        await AdvanceAsync(time, BeforeTheSecondPoll);

        Assert.Equal(1, PowerPollsSent(client));
    }

    [Fact]
    public async Task AlreadyConnectedClient_SendsOnePowerPollBeforeTheNextPoll_ThenKeepsPolling()
    {
        var time = new FakeTimeProvider();
        var client = TestFactory.CreateTcpClient();
        SetConnectionState(client, ConnectionState.Connected);
        _ = new PhilipsSICP(client.Object, _displayId, _groupId, "Test", Input.Hdmi1, 23, time);

        await UntilPowerPolled(time, client, 1);
        await AdvanceAsync(time, BeforeTheSecondPoll);
        Assert.Equal(1, PowerPollsSent(client));

        await AdvanceAsync(time, BeforeTheSecondPoll);
        Assert.Equal(2, PowerPollsSent(client));
    }

    [Fact]
    public async Task ConnectedWhileBeingConstructed_SendsOnePowerPollBeforeTheNextPoll()
    {
        var time = new FakeTimeProvider();
        var client = TestFactory.CreateTcpClient();
        SetConnectionState(client, ConnectionState.Connected);
        _ = new PhilipsSICP(client.Object, _displayId, _groupId, "Test", Input.Hdmi1, 23, time);

        client.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await UntilPowerPolled(time, client, 1);
        await AdvanceAsync(time, BeforeTheSecondPoll);

        Assert.Equal(1, PowerPollsSent(client));
    }
}
