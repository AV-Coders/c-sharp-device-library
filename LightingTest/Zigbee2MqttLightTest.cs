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
    [InlineData("{\"state\":\"ON\",\"brightness\":255}", PowerState.On, 100u)]
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
    public void PowerOn_SendsTheCommand()
    {
        _light.PowerOn();

        _mockClient.Verify(x => x.Send("zigbee2mqtt/office/set", "{\"state\": \"On\"}"));
    }
}
