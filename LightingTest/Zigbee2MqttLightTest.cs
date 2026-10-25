using System.Text.Json;
using AVCoders.Core;
using Moq;

namespace AVCoders.Lighting.Tests;

public class Zigbee2MqttLightTest
{
    private readonly Mock<MqttClient> _mockClient = new("host", (ushort)1883, "Mqtt");
    private readonly Zigbee2MqttLight _light;
    private Action<string>? _valueChangeHandler;

    public Zigbee2MqttLightTest()
    {
        _mockClient.Setup(x => x.SubscribeToTopic("zigbee2mqtt/office", It.IsAny<Action<string>>()))
            .Callback<string, Action<string>>((_, handler) => _valueChangeHandler = handler);
        _light = new Zigbee2MqttLight("Office", "office", _mockClient.Object);
    }

    [Fact]
    public void Constructor_SubscribesToTheTopic()
    {
        Assert.NotNull(_valueChangeHandler);
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
    [InlineData("128.0")]
    [InlineData("1.28e2")]
    public void HandleValueChange_WithAnIntegralBrightnessWrittenAsADecimal_SetsTheBrightness(string brightness)
    {
        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"brightness\":{brightness}}}");

        Assert.Equal(50u, _light.Brightness);
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
    [InlineData("2147483648", 100u)]
    [InlineData("-2147483649", 0u)]
    public void HandleValueChange_WithABrightnessOutsideTheRange_ClampsItAndLogsIt(string brightness, uint expectedBrightness)
    {
        _valueChangeHandler!.Invoke("{\"state\":\"ON\",\"brightness\":128}");

        _valueChangeHandler!.Invoke($"{{\"state\":\"ON\",\"brightness\":{brightness}}}");

        Assert.Equal(expectedBrightness, _light.Brightness);
        Assert.Contains("Value must be between 0 and 254", Assert.Single(_light.Errors).Message);
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
