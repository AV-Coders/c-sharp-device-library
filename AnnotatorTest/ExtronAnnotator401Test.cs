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
        _annotator = new ExtronAnnotator401(_mockClient.Object, "Test annotator", "test");
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
}