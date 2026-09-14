using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.SignalR.SetTopBox.Tests;

/// <summary>
/// Pins the wire shape the panel UI is written against: camelCase names and integer enums, which is
/// what the SignalR JSON protocol produces (it uses <see cref="JsonSerializerDefaults.Web"/>).
/// </summary>
public class SetTopBoxStateTest
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SerialisesWithCamelCaseNamesAndIntegerEnums()
    {
        var state = new SetTopBoxState("Boardroom", "AppleTv", ["Up", "Enter"],
            PowerState.On, PowerState.Off, true, CommunicationState.Okay);

        var json = JsonSerializer.Serialize(state, WireOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("Boardroom", root.GetProperty("name").GetString());
        Assert.Equal("AppleTv", root.GetProperty("sourceId").GetString());
        Assert.Equal(["Up", "Enter"], root.GetProperty("supportedButtons").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal((int)PowerState.On, root.GetProperty("powerState").GetInt32());
        Assert.Equal((int)PowerState.Off, root.GetProperty("desiredPowerState").GetInt32());
        Assert.True(root.GetProperty("isActiveSource").GetBoolean());
        Assert.Equal((int)CommunicationState.Okay, root.GetProperty("communicationState").GetInt32());
    }

    [Fact]
    public void RoundTrips()
    {
        var state = new SetTopBoxState("Boardroom", "AppleTv", ["Up"],
            PowerState.On, PowerState.On, false, CommunicationState.Error);

        var roundTripped = JsonSerializer.Deserialize<SetTopBoxState>(JsonSerializer.Serialize(state, WireOptions), WireOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(state with { SupportedButtons = roundTripped.SupportedButtons }, roundTripped);
        Assert.Equal(state.SupportedButtons, roundTripped.SupportedButtons);
    }
}
