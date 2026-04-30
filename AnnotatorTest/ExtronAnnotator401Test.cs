using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Annotator.Tests;

public class ExtronAnnotator401Test
{
    private readonly ExtronAnnotator401 _annotator;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
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
    
    [Theory]
    [InlineData(Annotator401Outputs.All, MuteState.On, "0*2B")]
    [InlineData(Annotator401Outputs.All, MuteState.Off, "0*0B")]
    [InlineData(Annotator401Outputs.Output1, MuteState.On, "1*2B")]
    [InlineData(Annotator401Outputs.Output1, MuteState.Off, "1*0B")]
    [InlineData(Annotator401Outputs.Output2, MuteState.On, "2*2B")]
    [InlineData(Annotator401Outputs.Output2, MuteState.Off, "2*0B")]
    public void SetVideoMute_ForASpecificOutput_SendsTheCommand(Annotator401Outputs output, MuteState state, string expectedCommand)
    {
        _annotator.SetVideoMute(output, state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData("Server/images", "W0*/shares/Server/images/test-")]
    [InlineData("Server/images2", "W0*/shares/Server/images2/test-")]
    [InlineData("Server/images2/", "W0*/shares/Server/images2/test-")]
    [InlineData("/Server/images2/", "W0*/shares/Server/images2/test-")]
    public void SaveToNetworkShare_SendTheCommand(string folderPath, string expectedPrefix)
    {
        _annotator.SaveToNetworkShare(folderPath);
        
        _mockClient.Verify(x => x.Send(It.Is<string>( s => 
            s.StartsWith(expectedPrefix) &&
            s.EndsWith(".pngMF|")
            )));
    }
    
    [Fact]
    public void Reboot_SendsTheCommand()
    {
        _annotator.Reboot();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}1boot\r"));
    }

    [Fact]
    public void Save_SendsTheCommand()
    {
        _annotator.Save();

        _mockClient.Verify(x => x.Send("W9MF|"));
    }

    [Theory]
    [InlineData(DrawingTool.Eraser, $"{EscapeHeader}0DRAW\r")]
    [InlineData(DrawingTool.Pointer, $"{EscapeHeader}1DRAW\r")]
    [InlineData(DrawingTool.Freehand, $"{EscapeHeader}2DRAW\r")]
    [InlineData(DrawingTool.Highlighter, $"{EscapeHeader}3DRAW\r")]
    [InlineData(DrawingTool.VectorLine, $"{EscapeHeader}4DRAW\r")]
    [InlineData(DrawingTool.ArrowLine, $"{EscapeHeader}5DRAW\r")]
    [InlineData(DrawingTool.Ellipse, $"{EscapeHeader}6DRAW\r")]
    [InlineData(DrawingTool.Rectangle, $"{EscapeHeader}7DRAW\r")]
    [InlineData(DrawingTool.Text, $"{EscapeHeader}8DRAW\r")]
    [InlineData(DrawingTool.Spotlight, $"{EscapeHeader}9DRAW\r")]
    [InlineData(DrawingTool.Zoom, $"{EscapeHeader}10DRAW\r")]
    [InlineData(DrawingTool.Pan, $"{EscapeHeader}11DRAW\r")]
    public void SetTool_SendsTheCommand(DrawingTool tool, string expectedCommand)
    {
        _annotator.SetTool(tool);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void QueryTool_SendsTheCommand()
    {
        _annotator.QueryTool();
        _mockClient.Verify(x => x.Send($"{EscapeHeader}DRAW\r"));
    }

    [Theory]
    [InlineData("Draw00", DrawingTool.Eraser)]
    [InlineData("Draw01", DrawingTool.Pointer)]
    [InlineData("Draw02", DrawingTool.Freehand)]
    [InlineData("Draw03", DrawingTool.Highlighter)]
    [InlineData("Draw04", DrawingTool.VectorLine)]
    [InlineData("Draw05", DrawingTool.ArrowLine)]
    [InlineData("Draw06", DrawingTool.Ellipse)]
    [InlineData("Draw07", DrawingTool.Rectangle)]
    [InlineData("Draw08", DrawingTool.Text)]
    [InlineData("Draw09", DrawingTool.Spotlight)]
    [InlineData("Draw10", DrawingTool.Zoom)]
    [InlineData("Draw11", DrawingTool.Pan)]
    public void ResponseHandler_RaisesOnDrawingToolChanged(string response, DrawingTool expectedTool)
    {
        Mock<DrawingToolHandler> toolHandler = new Mock<DrawingToolHandler>();
        _annotator.OnDrawingToolChanged += toolHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        toolHandler.Verify(x => x.Invoke(expectedTool));
    }

    [Fact]
    public void ResponseHandler_ExposesCurrentTool()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Draw04");
        Assert.Equal(DrawingTool.VectorLine, _annotator.CurrentTool);
    }

    [Fact]
    public void ResponseHandler_IgnoresUnknownDrawValues()
    {
        Mock<DrawingToolHandler> toolHandler = new Mock<DrawingToolHandler>();
        _annotator.OnDrawingToolChanged += toolHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke("Draw99");
        toolHandler.Verify(x => x.Invoke(It.IsAny<DrawingTool>()), Times.Never);
    }
}