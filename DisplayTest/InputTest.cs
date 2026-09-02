namespace AVCoders.Display.Tests;

public class InputTest
{
    // SignalR serialises Input as an integer, so the original members must keep their ordinals.
    [Theory]
    [InlineData(Input.Unknown, 0)]
    [InlineData(Input.Hdmi1, 1)]
    [InlineData(Input.Hdmi2, 2)]
    [InlineData(Input.Hdmi3, 3)]
    [InlineData(Input.Hdmi4, 4)]
    [InlineData(Input.Sdi, 5)]
    [InlineData(Input.DvbtTuner, 6)]
    [InlineData(Input.Network6, 7)]
    [InlineData(Input.DisplayPort, 8)]
    public void OriginalMembers_KeepTheirOrdinals(Input input, int ordinal)
    {
        Assert.Equal(ordinal, (int)input);
    }

    [Fact]
    public void Network1To5_AreContiguous()
    {
        Assert.Equal((int)Input.Network1 + 1, (int)Input.Network2);
        Assert.Equal((int)Input.Network1 + 2, (int)Input.Network3);
        Assert.Equal((int)Input.Network1 + 3, (int)Input.Network4);
        Assert.Equal((int)Input.Network1 + 4, (int)Input.Network5);
    }

    [Fact]
    public void NewMembers_ComeAfterDisplayPort()
    {
        foreach (Input input in Enum.GetValues<Input>())
        {
            if (input is Input.Unknown or Input.Hdmi1 or Input.Hdmi2 or Input.Hdmi3 or Input.Hdmi4
                or Input.Sdi or Input.DvbtTuner or Input.Network6 or Input.DisplayPort)
                continue;
            Assert.True((int)input > (int)Input.DisplayPort, $"{input} must come after DisplayPort");
        }
    }
}
