using System.Reflection;
using System.Text.Json;
using AVCoders.Core;
using Moq;

namespace AVCoders.Lighting.Tests;

public class Zigbee2MqttLightTest
{
    private const string StateRequest = "{\"state\": \"\", \"brightness\": \"\"}";
    private readonly Mock<MqttClient> _mockClient = new("host", (ushort)1883, "Mqtt");
    private readonly Zigbee2MqttLight _light;
    private Action<string>? _valueChangeHandler;

    public Zigbee2MqttLightTest()
    {
        _mockClient.Setup(x => x.SubscribeToTopic("zigbee2mqtt/office", It.IsAny<Action<string>>()))
            .Callback<string, Action<string>>((_, handler) => _valueChangeHandler = handler);
        _light = new Zigbee2MqttLight("Office", "office", _mockClient.Object);
    }

    private static void SetConnectionState(Mock<MqttClient> mock, ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mock.Object, state);

    [Fact]
    public void Connected_RequestsTheStateAndBrightness()
    {
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        _mockClient.Verify(x => x.Send("zigbee2mqtt/office/get", StateRequest), Times.Once);
    }

    [Fact]
    public void EachReconnect_RequestsTheStateAgain()
    {
        foreach (var state in new[] { ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connecting, ConnectionState.Connected })
            _mockClient.Object.ConnectionStateHandlers!.Invoke(state);

        _mockClient.Verify(x => x.Send("zigbee2mqtt/office/get", StateRequest), Times.Exactly(2));
    }

    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Error)]
    public void CreatedBeforeTheConnection_AndOtherConnectionStates_RequestNothing(ConnectionState state)
    {
        _mockClient.Object.ConnectionStateHandlers!.Invoke(state);

        _mockClient.Verify(x => x.Send(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CreatedWhileTheClientIsConnected_SubscribesThenRequestsTheState()
    {
        var client = new Mock<MqttClient>("host", (ushort)1883, "Mqtt");
        SetConnectionState(client, ConnectionState.Connected);

        _ = new Zigbee2MqttLight("Office", "office", client.Object);

        Assert.Equal(["SubscribeToTopic", "Send"], client.Invocations.Select(invocation => invocation.Method.Name));
        client.Verify(x => x.SubscribeToTopic("zigbee2mqtt/office", It.IsAny<Action<string>>()));
        client.Verify(x => x.Send("zigbee2mqtt/office/get", StateRequest));
    }

    [Theory]
    [InlineData("{\"state\":\"ON\",\"brightness\":254}", PowerState.On, 100u)]
    [InlineData("{\"state\":\"OFF\",\"brightness\":0}", PowerState.Off, 0u)]
    public void HandleValueChange_UpdatesTheState(string payload, PowerState expectedPowerState, uint expectedBrightness)
    {
        _valueChangeHandler!.Invoke(payload);

        Assert.Equal(expectedPowerState, _light.PowerState);
        Assert.Equal(expectedBrightness, _light.Brightness);
    }

    [Fact]
    public void HandleValueChange_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.Unknown, _light.CommunicationState);

        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":128}");

        Assert.Equal(CommunicationState.Okay, _light.CommunicationState);
    }

    [Fact]
    public void HandleValueChange_WithInvalidJson_LogsIt()
    {
        _valueChangeHandler!.Invoke("{\"state\":");

        Assert.Equal(CommunicationState.Unknown, _light.CommunicationState);
        Assert.Contains("{\"state\":", Assert.Single(_light.Errors).Message);
    }

    [Fact]
    public void HandleValueChange_WithoutABrightness_KeepsTheBrightnessQuietly()
    {
        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":254}");

        _valueChangeHandler!.Invoke("{\"state\":\"OFF\"}");

        Assert.Equal(100u, _light.Brightness);
        Assert.Equal(PowerState.Off, _light.PowerState);
        Assert.Empty(_light.Errors);
    }

    [Theory]
    [InlineData("127.5")]
    [InlineData("\"high\"")]
    [InlineData("true")]
    [InlineData("1e400")]
    public void HandleValueChange_WithAnUnreadableBrightness_LogsItAndKeepsTheBrightness(string brightness)
    {
        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":254}");

        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"brightness\":{brightness}}}");

        Assert.Equal(100u, _light.Brightness);
        Assert.Contains("Unable to read the brightness", Assert.Single(_light.Errors).Message);
    }

    [Theory]
    [InlineData(14, 6u)]
    [InlineData(127, 50u)]
    [InlineData(251, 99u)]
    [InlineData(254, 100u)]
    public void HandleValueChange_RoundsTheBrightnessToTheNearestPercent(int brightness, uint expectedBrightness)
    {
        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"brightness\":{brightness}}}");

        Assert.Equal(expectedBrightness, _light.Brightness);
    }

    [Theory]
    [InlineData("-1", 0u)]
    [InlineData("255", 100u)]
    public void HandleValueChange_WithABrightnessOutsideTheRange_ClampsItAndLogsIt(string brightness, uint expectedBrightness)
    {
        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":128}");

        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"brightness\":{brightness}}}");

        Assert.Equal(expectedBrightness, _light.Brightness);
        Assert.Contains("Value must be between 0 and 254", Assert.Single(_light.Errors).Message);
    }

    private string? Detail(string label) => _light.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? OngoingIssue(string key) => _light.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    [Fact]
    public void HandleValueChange_ShowsTheLinkQualityAndUpdateState()
    {
        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":254,\"linkquality\":87,\"update\":{\"state\":\"available\",\"installed_version\":1,\"latest_version\":2}}");

        Assert.Equal("87", Detail("Link Quality"));
        Assert.Equal("available", Detail("Firmware Update"));
        Assert.Empty(_light.GetOngoingIssues());
    }

    [Fact]
    public void HandleValueChange_WhileUpdating_ShowsTheStateFromTheOtaExample()
    {
        _valueChangeHandler!.Invoke("{\"update\":{\"state\":\"updating\",\"progress\":13.37,\"remaining\":219}}");

        Assert.Equal("updating", Detail("Firmware Update"));
    }

    [Fact]
    public void HandleValueChange_WithoutLinkQualityOrUpdate_AddsNoDetails()
    {
        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":254}");

        Assert.Empty(_light.Details);
        Assert.Empty(_light.GetOngoingIssues());
    }

    [Theory]
    [InlineData("\"strong\"")]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("12.5")]
    public void HandleValueChange_WithAnUnreadableLinkQuality_ShowsItRawAndRaisesAnIssueOnTheThirdInARowUntilAReadableOne(string linkQuality)
    {
        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"linkquality\":{linkQuality}}}");
        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"linkquality\":{linkQuality}}}");

        Assert.Equal(linkQuality, Detail("Link Quality"));
        Assert.Empty(_light.GetIssues());

        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"linkquality\":{linkQuality}}}");

        var issue = OngoingIssue("linkquality-read");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains(linkQuality, issue.Message);

        _valueChangeHandler!.Invoke("{\"linkquality\":255}");

        Assert.Equal("255", Detail("Link Quality"));
        Assert.Null(OngoingIssue("linkquality-read"));
    }

    [Theory]
    [InlineData("\"available\"")]
    [InlineData("{\"state\":3}")]
    public void HandleValueChange_WithAnUnreadableUpdate_ShowsItRawAndRaisesAnIssueOnTheThirdInARowUntilAState(string update)
    {
        _valueChangeHandler!.Invoke($"{{\"update\":{update}}}");
        _valueChangeHandler!.Invoke($"{{\"update\":{update}}}");

        Assert.Equal(update, Detail("Firmware Update"));
        Assert.Empty(_light.GetIssues());

        _valueChangeHandler!.Invoke($"{{\"update\":{update}}}");

        Assert.Contains(update, OngoingIssue("update-read")?.Message);

        _valueChangeHandler!.Invoke("{\"update\":{\"state\":\"idle\"}}");

        Assert.Equal("idle", Detail("Firmware Update"));
        Assert.Null(OngoingIssue("update-read"));
    }

    [Theory]
    [InlineData("{\"linkquality\":null}", "Link Quality", "null")]
    [InlineData("{\"update\":{\"progress\":13.37}}", "Firmware Update", "{\"progress\":13.37}")]
    [InlineData("{\"update\":{\"state\":\"\"}}", "Firmware Update", "{\"state\":\"\"}")]
    [InlineData("{\"update\":null}", "Firmware Update", "null")]
    public void HandleValueChange_WithAnEmptyOrAbsentValue_ShowsItRawWithNoIssue(string message, string label, string raw)
    {
        for (var repeat = 0; repeat < 3; repeat++)
            _valueChangeHandler!.Invoke(message);

        Assert.Equal(raw, Detail(label));
        Assert.Empty(_light.GetIssues());
    }

    [Fact]
    public void SetLevel_ThenItsEcho_ReportsTheSameLevel()
    {
        string sent = "";
        _mockClient.Setup(x => x.Send("zigbee2mqtt/office/set", It.IsAny<string>()))
            .Callback<string, string>((_, payload) => sent = payload);

        for (int level = 0; level <= 100; level++)
        {
            _light.SetLevel(level);
            using var document = JsonDocument.Parse(sent);
            int brightness = document.RootElement.GetProperty("brightness").GetInt32();
            _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"brightness\":{brightness}}}");

            Assert.Equal((uint)level, _light.Brightness);
        }
        Assert.Empty(_light.Errors);
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        _light.PowerOn();

        _mockClient.Verify(x => x.Send("zigbee2mqtt/office/set", "{\"state\": \"ON\"}"));
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        _light.PowerOff();

        _mockClient.Verify(x => x.Send("zigbee2mqtt/office/set", "{\"state\": \"OFF\"}"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 127)]
    [InlineData(100, 254)]
    public void SetLevel_SendsTheStateAndTheBrightness(int level, int expectedBrightness)
    {
        _light.SetLevel(level);

        _mockClient.Verify(x => x.Send("zigbee2mqtt/office/set", $"{{\"state\": \"ON\", \"brightness\": {expectedBrightness}}}"));
    }
}
