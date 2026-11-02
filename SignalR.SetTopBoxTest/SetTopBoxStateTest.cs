using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.SignalR.SetTopBox.Tests;

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
}
