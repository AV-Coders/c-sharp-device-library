using AVCoders.Core;
using Moq;

namespace AVCoders.Annotator.Tests;

public class ExtronAnnotator401Test
{
    private readonly ExtronAnnotator401 _annotator;
    private readonly Mock<CommunicationClient> _mockClient = new("foo", "bar", (ushort)1);
    private const string EscapeHeader = "\x1b";

    public ExtronAnnotator401Test()
    {
        _annotator = new ExtronAnnotator401(_mockClient.Object, "Test annotator");
    }
    
    [Fact]
    public void Clear_SendsTheCommand()
    {
        _annotator.Clear();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}0EDIT\r"));
    }

    [Fact]
    public void StartCalibration_SendsTheCommand()
    {
        _annotator.StartCalibration();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}1PCAL\r"));
    }

    [Fact]
    public void StopCalibration_SendsTheCommand()
    {
        _annotator.StopCalibration();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}0PCAL\r"));
    }

    [Fact]
    public void SaveToNetworkDrive_SendsTheCommand()
    {
        _annotator.SaveToNetworkDrive();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}3MCAP\r"));
    }

    [Fact]
    public void SaveToInternalMemory_SendsTheCommand()
    {
        _annotator.SaveToInternalMemory();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}0MCAP\r"));
    }

    [Fact]
    public void SaveToIQC_SendsTheCommand()
    {
        _annotator.SaveToIQC();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}1MCAP\r"));
    }

    [Fact]
    public void SaveToUSB_SendsTheCommand()
    {
        _annotator.SaveToUSB();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}2MCAP\r"));
    }
}