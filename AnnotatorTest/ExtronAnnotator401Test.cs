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

    [Theory]
    [InlineData(Annotator401Outputs.All, $"{EscapeHeader}0ASHW\r")]
    [InlineData(Annotator401Outputs.Output1, $"{EscapeHeader}1ASHW\r")]
    [InlineData(Annotator401Outputs.Output2, $"{EscapeHeader}2ASHW\r")]
    [InlineData(Annotator401Outputs.None, $"{EscapeHeader}3ASHW\r")]
    public void SetAnnotationOutput_SendsTheCommand(Annotator401Outputs output, string expectedCommand)
    {
        _annotator.SetAnnotationOutput(output);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(Annotator401Outputs.All, $"{EscapeHeader}0CSHW\r")]
    [InlineData(Annotator401Outputs.Output1, $"{EscapeHeader}1CSHW\r")]
    [InlineData(Annotator401Outputs.Output2, $"{EscapeHeader}2CSHW\r")]
    [InlineData(Annotator401Outputs.None, $"{EscapeHeader}3CSHW\r")]
    public void SetCursorOutput_SendsTheCommand(Annotator401Outputs output, string expectedCommand)
    {
        _annotator.SetCursorOutput(output);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void ResponseHandler_HandlesUSBSaveFeedback()
    {
        Mock<ActionHandler> saveHandler = new Mock<ActionHandler>();
        _annotator.UsbSavedHandlers += saveHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke("Ims1*/Graphics/filename.png");
        saveHandler.Verify(x => x.Invoke());
    }

    [Fact]
    public void ResponseHandler_HandlesUSBSaveFeedbackWithFilename()
    {
        Mock<StringHandler> saveHandler = new Mock<StringHandler>();
        _annotator.UsbFileSavedHandlers += saveHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke("Ims1*/Graphics/filename.png");
        saveHandler.Verify(x => x.Invoke("/Graphics/filename.png"));
    }
    
    [Theory]
    [InlineData(MuteState.On, "0*2B")]
    [InlineData(MuteState.Off, "0*0B")]
    public void SetVideoMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _annotator.SetVideoMute(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
}