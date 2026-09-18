using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

// Response strings are taken verbatim from an Annotator 401 (firmware V1.01) capture.
public class ExtronAnnotator401VideoMatrixTest : IDisposable
{
    private readonly ExtronAnnotator401VideoMatrix _matrix;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private const string EscapeHeader = "\x1b";

    public ExtronAnnotator401VideoMatrixTest()
    {
        _matrix = new ExtronAnnotator401VideoMatrix(_mockClient.Object, "Test annotator");
    }

    public void Dispose() => _matrix.Dispose();

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private Task Poll() => (Task)typeof(ExtronAnnotator401VideoMatrix)
        .GetMethod("Poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(_matrix, [CancellationToken.None])!;

    [Fact]
    public void Constructor_BuildsTheFixedTopology()
    {
        Assert.Single(_matrix.Inputs);
        Assert.Equal(2, _matrix.Outputs.Count);
        Assert.Equal(1, _matrix.NumberOfInputs);
        Assert.Equal(2, _matrix.NumberOfOutputs);
        Assert.Single(_matrix.GetInputs());
        Assert.Equal(2, _matrix.GetOutputs().Count);
        Assert.Equal("Input 1", _matrix.Inputs[0].Name);
        Assert.Equal("Output 1", _matrix.Outputs[0].Name);
        Assert.Equal("Output 2", _matrix.Outputs[1].Name);
    }

    [Fact]
    public void Constructor_TiesBothOutputsToTheOnlyInput()
    {
        Assert.Equal("1", _matrix.Inputs[0].StreamAddress);
        Assert.All(_matrix.Outputs, output => Assert.Equal("1", output.StreamAddress));
    }

    [Fact]
    public void ConnectAndReconnect_EnablesVerboseModeAndQueriesStatus()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);

        foreach (var command in new[] { "3CV", "0LS", "I1HDCP", "O1HDCP", "O2HDCP" })
            _mockClient.Verify(client => client.Send($"{EscapeHeader}{command}\r"), Times.Exactly(2));
    }

    [Fact]
    public void Disconnecting_ResetsTheReportedStatus()
    {
        SetConnection(ConnectionState.Connected);
        Respond("In00 1\r\n");
        Respond("HdcpI1*2\r\n");
        Respond("HdcpO1*3\r\n");
        Respond("HdcpO2*1\r\n");
        Assert.All(_matrix.Outputs, output => Assert.Equal(ConnectionState.Connected, output.OutputConnectionStatus));

        SetConnection(ConnectionState.Disconnected);

        Assert.Equal(ConnectionState.Unknown, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, _matrix.Inputs[0].InputHdcpStatus);
        Assert.All(_matrix.Outputs, output =>
        {
            Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, output.OutputHdcpStatus);
        });
    }

    [Fact]
    public async Task Heartbeat_RefreshesStatusOnlyWhileConnected()
    {
        await Poll();
        _mockClient.Verify(client => client.Send(It.IsAny<string>()), Times.Never);

        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        foreach (var command in new[] { "0LS", "I1HDCP", "O1HDCP", "O2HDCP" })
            _mockClient.Verify(client => client.Send($"{EscapeHeader}{command}\r"), Times.Once);

        SetConnection(ConnectionState.Disconnected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("In00 1", ConnectionState.Connected)]
    [InlineData("In00 0", ConnectionState.Disconnected)]
    public void SignalPresence_SetsTheInputSyncStatus(string response, ConnectionState expected)
    {
        Respond($"{response}\r\n");

        Assert.Equal(expected, _matrix.Inputs[0].InputConnectionStatus);
    }

    [Theory]
    [InlineData("HdcpI1*0", ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI1*1", ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpI1*2", ConnectionState.Connected, HdcpStatus.Active)]
    public void InputHdcp_SetsTheInputStatus(string response, ConnectionState connection, HdcpStatus hdcp)
    {
        Respond($"{response}\r\n");

        Assert.Equal(connection, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(hdcp, _matrix.Inputs[0].InputHdcpStatus);
    }

    [Theory]
    [InlineData("HdcpO1*0", 0, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO1*1", 0, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO2*2", 1, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO2*3", 1, ConnectionState.Connected, HdcpStatus.Active)]
    public void OutputHdcp_SetsTheOutputStatus(string response, int index, ConnectionState connection, HdcpStatus hdcp)
    {
        Respond($"{response}\r\n");

        Assert.Equal(connection, _matrix.Outputs[index].OutputConnectionStatus);
        Assert.Equal(hdcp, _matrix.Outputs[index].OutputHdcpStatus);
    }

    [Fact]
    public void HandleResponse_ProcessesEveryResponseInAChunk()
    {
        Respond("In00 1\r\nHdcpI1*1\r\nHdcpO1*0\r\nHdcpO2*1\r\n");

        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(HdcpStatus.NotSupported, _matrix.Inputs[0].InputHdcpStatus);
        Assert.Equal(ConnectionState.Disconnected, _matrix.Outputs[0].OutputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _matrix.Outputs[1].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.NotSupported, _matrix.Outputs[1].OutputHdcpStatus);
    }

    // The annotation driver shares this client, so its traffic must leave the video status alone.
    [Theory]
    [InlineData("Draw02")]
    [InlineData("Ashw3")]
    [InlineData("CfmtP test")]
    [InlineData("Ims1*/graphics/test-2026-09-18-02-32-50.png")]
    [InlineData("Pti0*00030")]
    [InlineData("Vrb3")]
    [InlineData("Vid01 Typ1 Amt1*0 Amt2*0 Vmt1*0 Vmt2*0 Hrt67.5 Vrt60.0")]
    [InlineData("E10")]
    [InlineData("HdcpI2*1")]
    [InlineData("HdcpO3*1")]
    public void HandleResponse_IgnoresResponsesItDoesNotOwn(string response)
    {
        Respond($"{response}\r\n");

        Assert.Equal(ConnectionState.Unknown, _matrix.Inputs[0].InputConnectionStatus);
        Assert.All(_matrix.Outputs, output => Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus));
    }

    [Fact]
    public void Routing_DoesNotSendAnything()
    {
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();

        _matrix.RouteAV(1, 1);
        _matrix.RouteVideo(1, 1);
        _matrix.RouteAudio(1, 1);

        _mockClient.Verify(client => client.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void HandleResponse_KeepsProcessingAfterASubscriberThrows()
    {
        // The client wraps the whole multicast handler in one try/catch, so an exception escaping this driver
        // would stop the annotation driver on the same client from ever seeing the rest of the response.
        _matrix.Inputs[0].InputStatusChangedHandlers += (_, _, _) => throw new InvalidOperationException("UI gone");

        Respond("In00 1\r\nHdcpO2*1\r\n");

        Assert.Equal(ConnectionState.Connected, _matrix.Outputs[1].OutputConnectionStatus);
    }

    [Fact]
    public void Dispose_StopsRespondingToTheClient()
    {
        _matrix.Dispose();
        SetConnection(ConnectionState.Connected);

        _mockClient.Verify(client => client.Send(It.IsAny<string>()), Times.Never);
    }
}
