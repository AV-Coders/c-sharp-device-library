using System.Collections.Concurrent;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Annotator.Tests;

public class ExtronAnnotator401Test
{
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronAnnotator401 _annotator;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    internal const string EscapeHeader = "\x1b";
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ConnectStepGap = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan OutputStepGap = TimeSpan.FromMilliseconds(200);

    public ExtronAnnotator401Test()
    {
        _annotator = new ExtronAnnotator401(_mockClient.Object, "Test annotator", "test", Annotator401Outputs.All,
            Annotator401Outputs.All, _time);
    }

    internal static void SetConnection(Mock<CommunicationClient> client, ConnectionState state) =>
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, state);

    internal static List<string> SentCommands(Mock<CommunicationClient> client) => client.Invocations
        .Where(invocation => invocation.Method.Name == nameof(CommunicationClient.Send) &&
                             invocation.Arguments[0] is string)
        .Select(invocation => (string)invocation.Arguments[0])
        .ToList();

    private ConcurrentQueue<string> RecordSends(string heldCommand, ManualResetEventSlim release)
    {
        var sent = new ConcurrentQueue<string>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(command =>
        {
            sent.Enqueue(command);
            if (command == heldCommand)
                release.Wait(HoldTimeout);
        });
        return sent;
    }

    [Fact]
    public async Task Constructor_WithAConnectedClient_Initialises()
    {
        var connectedClient = TestFactory.CreateCommunicationClient();
        SetConnection(connectedClient, ConnectionState.Connected);

        _ = new ExtronAnnotator401(connectedClient.Object, "Connected annotator", "test", Annotator401Outputs.All,
            Annotator401Outputs.All, _time);

        await Wait.UntilAsync(() => SentCommands(connectedClient).Contains($"{EscapeHeader}3CV\r"),
            "The annotator never initialised the connected client");
    }

    [Fact]
    public async Task HandleConnectionState_ReturnsAtOnceAndSendsTheInitialisationInOrder()
    {
        SetConnection(_mockClient, ConnectionState.Connected);

        Assert.DoesNotContain($"{EscapeHeader}DRAW\r", SentCommands(_mockClient));
        await Wait.UntilAsync(_time, ConnectStepGap, () => SentCommands(_mockClient).Contains($"{EscapeHeader}DRAW\r"),
            "The initialisation never finished");
        Assert.Equal([$"{EscapeHeader}3CV\r", $"{EscapeHeader}ASHW\r", $"{EscapeHeader}PtestCFMT\r", $"{EscapeHeader}DRAW\r"],
            SentCommands(_mockClient));
    }

    [Fact]
    public async Task HandleConnectionState_TwoConnectsInQuickSuccession_SendEachInitialisationInTurn()
    {
        SetConnection(_mockClient, ConnectionState.Connected);
        SetConnection(_mockClient, ConnectionState.Disconnected);
        SetConnection(_mockClient, ConnectionState.Connected);

        await Wait.UntilAsync(_time, ConnectStepGap,
            () => SentCommands(_mockClient).Count(command => command == $"{EscapeHeader}DRAW\r") == 2,
            "The second initialisation never finished");
        string[] initialisation =
            [$"{EscapeHeader}3CV\r", $"{EscapeHeader}ASHW\r", $"{EscapeHeader}PtestCFMT\r", $"{EscapeHeader}DRAW\r"];
        Assert.Equal([..initialisation, ..initialisation], SentCommands(_mockClient));
    }

    [Fact]
    public async Task PowerOn_ReturnsBeforeItsSends()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends($"{EscapeHeader}0ASHW\r", release);

        var powerOn = Task.Run(() => _annotator.PowerOn());

        await powerOn.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, OutputStepGap, () => sent.Count == 2, "The power on never reached the cursor output");
        Assert.Equal([$"{EscapeHeader}0ASHW\r", $"{EscapeHeader}0CSHW\r"], sent);
    }

    [Fact]
    public async Task PowerOff_ReturnsBeforeItsSends()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends($"{EscapeHeader}3ASHW\r", release);

        var powerOff = Task.Run(() => _annotator.PowerOff());

        await powerOff.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, OutputStepGap, () => sent.Count == 2, "The power off never reached the cursor output");
        Assert.Equal([$"{EscapeHeader}3ASHW\r", $"{EscapeHeader}3CSHW\r"], sent);
    }

    [Fact]
    public async Task PowerOnThenPowerOff_SendInOrderWithTheirSpacing()
    {
        var sends = new ConcurrentQueue<(string Command, DateTimeOffset At)>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>()))
            .Callback<string>(command => sends.Enqueue((command, _time.GetUtcNow())));

        _annotator.PowerOn();
        _annotator.PowerOff();

        await Wait.UntilAsync(_time, OutputStepGap, () => sends.Count == 4, "The power off never reached the cursor output");
        var sent = sends.ToArray();
        Assert.Equal([$"{EscapeHeader}0ASHW\r", $"{EscapeHeader}0CSHW\r", $"{EscapeHeader}3ASHW\r", $"{EscapeHeader}3CSHW\r"],
            sent.Select(send => send.Command));
        TimeSpan Gap(int i) => sent[i].At - sent[i - 1].At;
        Assert.True(Gap(1) >= OutputStepGap,
            $"The cursor output went out only {Gap(1).TotalMilliseconds:F0} ms after the annotation output");
        Assert.True(Gap(2) >= OutputStepGap,
            $"The power off went out only {Gap(2).TotalMilliseconds:F0} ms after the power on");
        Assert.True(Gap(3) >= OutputStepGap,
            $"The cursor output went out only {Gap(3).TotalMilliseconds:F0} ms after the annotation output");
    }

    [Fact]
    public async Task PowerOn_DuringAConnectInitialisation_GoesAfterIt()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends($"{EscapeHeader}3CV\r", release);
        SetConnection(_mockClient, ConnectionState.Connected);
        await Wait.UntilAsync(() => sent.Contains($"{EscapeHeader}3CV\r"), "The initialisation never started");

        _annotator.PowerOn();
        release.Set();

        await Wait.UntilAsync(_time, OutputStepGap, () => sent.Count == 6, "The power on never reached the cursor output");
        Assert.Equal([$"{EscapeHeader}3CV\r", $"{EscapeHeader}ASHW\r", $"{EscapeHeader}PtestCFMT\r", $"{EscapeHeader}DRAW\r",
            $"{EscapeHeader}0ASHW\r", $"{EscapeHeader}0CSHW\r"], sent);
    }

    [Fact]
    public async Task PowerOn_SendsUnderTheCallersTriggerId()
    {
        var ids = new ConcurrentQueue<string?>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback(() => ids.Enqueue(Trigger.Id));
        var id = Trigger.Start();

        _annotator.PowerOn();

        await Wait.UntilAsync(_time, OutputStepGap, () => ids.Count == 2, "The power on never reached the cursor output");
        Assert.All(ids, sentUnder => Assert.Equal(id, sentUnder));
    }

    [Fact]
    public async Task Commands_AreSentInTheOrderTheyWereAsked()
    {
        _annotator.PowerOff();
        _annotator.Clear();
        _annotator.SetAnnotationOutput(Annotator401Outputs.All);
        _annotator.SetCursorOutput(Annotator401Outputs.Output1);
        _annotator.Reboot();

        await Wait.UntilAsync(_time, OutputStepGap, () => SentCommands(_mockClient).Contains($"{EscapeHeader}1boot\r"),
            "The reboot was never sent");
        Assert.Equal([$"{EscapeHeader}3ASHW\r", $"{EscapeHeader}3CSHW\r", $"{EscapeHeader}0EDIT\r", $"{EscapeHeader}0ASHW\r",
            $"{EscapeHeader}1CSHW\r", $"{EscapeHeader}1boot\r"], SentCommands(_mockClient));
    }

    [Fact]
    public async Task Clear_SendsTheCommand()
    {
        _annotator.Clear();
        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains($"{EscapeHeader}0EDIT\r"), "The clear was never sent");
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
    public async Task SetAnnotationOutput_SendsTheCommand(Annotator401Outputs output, string expectedCommand)
    {
        _annotator.SetAnnotationOutput(output);
        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains(expectedCommand), "The annotation output was never sent");
    }

    [Theory]
    [InlineData(Annotator401Outputs.All, $"{EscapeHeader}0CSHW\r")]
    [InlineData(Annotator401Outputs.Output1, $"{EscapeHeader}1CSHW\r")]
    [InlineData(Annotator401Outputs.Output2, $"{EscapeHeader}2CSHW\r")]
    [InlineData(Annotator401Outputs.None, $"{EscapeHeader}3CSHW\r")]
    public async Task SetCursorOutput_SendsTheCommand(Annotator401Outputs output, string expectedCommand)
    {
        _annotator.SetCursorOutput(output);
        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains(expectedCommand), "The cursor output was never sent");
    }

    [Theory]
    [InlineData("Ims1*/Graphics/filename.png")]
    [InlineData("Ims0*/Graphics/filename.png")]
    [InlineData("Ims1*/Graphics/filename.png\r\n")]
    [InlineData("Ims0*/Graphics/filename.png\r\n")]
    public void ResponseHandler_ReportsTheTrimmedSavedFilePath(string response)
    {
        Mock<StringHandler> saveHandler = new Mock<StringHandler>();
        _annotator.OnFileSaved += saveHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke(response);
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
    public async Task Reboot_SendsTheCommand()
    {
        _annotator.Reboot();

        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains($"{EscapeHeader}1boot\r"), "The reboot was never sent");
    }

    [Fact]
    public void Save_SendsTheCommand()
    {
        _annotator.Save();

        _mockClient.Verify(x => x.Send("W9MF|"));
    }

    [Fact]
    public void SaveToInternalMemory_SendsTheCommand()
    {
        _annotator.SaveToInternalMemory();

        _mockClient.Verify(x => x.Send("W0*/graphics/MF|"));
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

    [Theory]
    [InlineData("In00 1")]
    [InlineData("In00 0")]
    [InlineData("HdcpI1*1")]
    [InlineData("HdcpO1*0")]
    [InlineData("HdcpO2*1")]
    public void ResponseHandler_IgnoresTheVideoStatusResponses(string response)
    {
        Mock<DrawingToolHandler> toolHandler = new Mock<DrawingToolHandler>();
        Mock<StringHandler> fileHandler = new Mock<StringHandler>();
        _annotator.OnDrawingToolChanged += toolHandler.Object;
        _annotator.OnFileSaved += fileHandler.Object;

        _mockClient.Object.ResponseHandlers!.Invoke(response);

        toolHandler.Verify(x => x.Invoke(It.IsAny<DrawingTool>()), Times.Never);
        fileHandler.Verify(x => x.Invoke(It.IsAny<string>()), Times.Never);
        Assert.Null(_annotator.CurrentTool);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronAnnotator401LogTest
{
    private const string EscapeHeader = ExtronAnnotator401Test.EscapeHeader;
    private readonly FakeTimeProvider _time = new();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly ExtronAnnotator401 _annotator;

    public ExtronAnnotator401LogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _annotator = new ExtronAnnotator401(_mockClient.Object, "Test annotator", "test", Annotator401Outputs.All,
                Annotator401Outputs.All, _time);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private List<string> SentCommands() => ExtronAnnotator401Test.SentCommands(_mockClient);

    [Fact]
    public async Task HandleConnectionState_AfterAnInitialisationThrows_TheNextConnectStillInitialises()
    {
        var throwing = true;
        _annotator.CommunicationStateHandlers += _ =>
        {
            if (throwing)
                throw new InvalidOperationException("disposed control");
        };
        ExtronAnnotator401Test.SetConnection(_mockClient, ConnectionState.Connected);
        await Wait.UntilAsync(() => _logs.Logs.Any(log => log.Message.Contains("Initialising the annotator failed")),
            "The first initialisation never failed");
        throwing = false;

        ExtronAnnotator401Test.SetConnection(_mockClient, ConnectionState.Disconnected);
        ExtronAnnotator401Test.SetConnection(_mockClient, ConnectionState.Connected);

        await Wait.UntilAsync(_time, ExtronAnnotator401Test.ConnectStepGap, () => SentCommands().Contains($"{EscapeHeader}DRAW\r"),
            "The second initialisation never finished");
        Assert.Equal([$"{EscapeHeader}3CV\r", $"{EscapeHeader}3CV\r", $"{EscapeHeader}ASHW\r", $"{EscapeHeader}PtestCFMT\r",
            $"{EscapeHeader}DRAW\r"], SentCommands());
    }

    [Fact]
    public async Task PowerOn_AfterItsSendsFail_LogsTheFailureAndTheNextCommandStillGoesOut()
    {
        var throwing = true;
        _annotator.CommunicationStateHandlers += _ =>
        {
            if (throwing)
                throw new InvalidOperationException("disposed control");
        };
        _annotator.PowerOn();
        await Wait.UntilAsync(() => _logs.Logs.Any(log => log.Message.Contains("Sending to the annotator failed")),
            "The power on never failed");
        throwing = false;

        _annotator.PowerOff();

        await Wait.UntilAsync(_time, ExtronAnnotator401Test.OutputStepGap, () => SentCommands().Contains($"{EscapeHeader}3CSHW\r"),
            "The power off never reached the cursor output");
        Assert.Equal([$"{EscapeHeader}0ASHW\r", $"{EscapeHeader}3ASHW\r", $"{EscapeHeader}3CSHW\r"], SentCommands());
    }

    [Fact]
    public void ResponseHandler_IgnoresAndLogsUnknownDrawValues()
    {
        Mock<DrawingToolHandler> toolHandler = new Mock<DrawingToolHandler>();
        _annotator.OnDrawingToolChanged += toolHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke("Draw99");
        toolHandler.Verify(x => x.Invoke(It.IsAny<DrawingTool>()), Times.Never);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message.Contains("Draw99"));
    }
}
