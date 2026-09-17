using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Matrix.Tests;

public class ExtronSwTransportTest
{
    [Fact]
    public void Sw2LiveResponse_ParsesQuotedSignalStatus()
    {
        var client = TestFactory.CreateSshClient();
        using var switcher = new ExtronSw(client.Object, "SW2 switch");
        client.Object.ResponseHandlers!("Inf01*SW2 HD 4K PLUS");
        client.Object.ResponseHandlers!("Sig\"0 1*0\"");
        client.Object.ResponseHandlers!("HdcpI0 2");
        client.Object.ResponseHandlers!("HdcpO0");
        client.Object.ResponseHandlers!("In1 All");
        Assert.Equal(2, switcher.NumberOfInputs);
        Assert.Equal(ConnectionState.Disconnected, switcher.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, switcher.Inputs[1].InputConnectionStatus);
        Assert.Equal(HdcpStatus.NotSupported, switcher.Inputs[1].InputHdcpStatus);
        Assert.Equal(ConnectionState.Disconnected, switcher.Outputs[0].OutputConnectionStatus);
        Assert.Equal("1", switcher.Outputs[0].StreamAddress);
    }

    [Fact]
    public void TcpResponses_CanBeSplitAtEveryCharacter()
    {
        var client = TestFactory.CreateTcpClient();
        using var switcher = new ExtronSw(client.Object, "TCP switch");
        const string response = "Inf01*SW4 HD 4K PLUS Series\r\nSig1 0 1 0*1\r\nHdcp I1 0 2 0\r\nHdcp O1\r\nIn3 All\r\n";
        foreach (var character in response)
            client.Object.ResponseHandlers!(character.ToString());

        Assert.Equal(4, switcher.NumberOfInputs);
        Assert.Equal(ConnectionState.Connected, switcher.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, switcher.Inputs[1].InputConnectionStatus);
        Assert.Equal(HdcpStatus.Active, switcher.Inputs[0].InputHdcpStatus);
        Assert.Equal(HdcpStatus.NotSupported, switcher.Inputs[2].InputHdcpStatus);
        Assert.Equal(ConnectionState.Connected, switcher.Outputs[0].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.Available, switcher.Outputs[0].OutputHdcpStatus);
        Assert.Equal("3", switcher.Outputs[0].StreamAddress);
    }

    [Fact]
    public void TcpResponses_ProcessCompleteLinesAndRetainTrailingFragment()
    {
        var client = TestFactory.CreateTcpClient();
        using var switcher = new ExtronSw(client.Object, "TCP switch");
        client.Object.ResponseHandlers!("Inf01*SW4 HD 4K PLUS Series\r\nSig1 0");
        Assert.Equal(4, switcher.NumberOfInputs);
        Assert.Equal(ConnectionState.Unknown, switcher.Inputs[0].InputConnectionStatus);
        client.Object.ResponseHandlers!(" 1 0*1\r\nIn2 All");
        Assert.Equal(ConnectionState.Connected, switcher.Inputs[0].InputConnectionStatus);
        Assert.Empty(switcher.Outputs[0].StreamAddress);
        client.Object.ResponseHandlers!("\r\n");
        Assert.Equal("2", switcher.Outputs[0].StreamAddress);
    }

    [Fact]
    public void ConnectionChange_DiscardsPartialResponseFromPreviousSession()
    {
        var client = TestFactory.CreateTcpClient();
        using var switcher = new ExtronSw(client.Object, "TCP switch");
        client.Object.ResponseHandlers!("Inf01*SW4 HD ");
        client.Object.ConnectionStateHandlers!(ConnectionState.Disconnected);
        client.Object.ConnectionStateHandlers!(ConnectionState.Connected);
        client.Object.ResponseHandlers!("4K PLUS Series\r\n");
        Assert.Empty(switcher.Inputs);
        client.Object.ResponseHandlers!("Inf01*SW8 HD 4K PLUS Series\r\n");
        Assert.Equal(8, switcher.NumberOfInputs);
    }

    [Fact]
    public void OversizedResponse_IsDiscardedAndNextLineIsProcessed()
    {
        var client = TestFactory.CreateTcpClient();
        using var switcher = new ExtronSw(client.Object, "TCP switch");
        client.Object.ResponseHandlers!(new string('x', 5000));
        client.Object.ResponseHandlers!("Inf01*SW4 HD 4K PLUS Series\r\n");
        Assert.Empty(switcher.Inputs);
        client.Object.ResponseHandlers!("Inf01*SW2 HD 4K PLUS Series\r\n");
        Assert.Equal(2, switcher.NumberOfInputs);
    }

    [Fact]
    public void SerialResponses_BufferPartialLines()
    {
        var client = TestFactory.CreateSerialClient();
        using var switcher = new ExtronSw(client.Object, "Serial switch");
        client.Object.ResponseHandlers!("Inf01*SW6 HD ");
        Assert.Empty(switcher.Inputs);
        client.Object.ResponseHandlers!("4K PLUS Series\r\n");
        Assert.Equal(6, switcher.NumberOfInputs);
    }

    [Fact]
    public void SshResponses_AreProcessedWithoutTerminators()
    {
        var client = TestFactory.CreateSshClient();
        using var switcher = new ExtronSw(client.Object, "SSH switch");
        client.Object.ResponseHandlers!("Inf01*SW2 HD 4K PLUS Series");
        client.Object.ResponseHandlers!("Sig1 0*1");
        client.Object.ResponseHandlers!("In1 All");
        Assert.Equal(2, switcher.NumberOfInputs);
        Assert.Equal(ConnectionState.Connected, switcher.Outputs[0].OutputConnectionStatus);
        Assert.Equal("1", switcher.Outputs[0].StreamAddress);
    }

    [Fact]
    public async Task Dispose_DoesNotCaptureCallerSynchronizationContext()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronSw(client.Object, "UI switch");
        var worker = (ThreadWorker)typeof(ExtronSw)
            .GetField("_pollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(switcher)!;
        await worker.Stop();

        // Force Stop to await unfinished work, avoiding a race with the real polling delay.
        var pendingWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cancellationHandle = cancellation.Token.WaitHandle;
        typeof(ThreadWorker).GetField("_task", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(worker, pendingWork.Task);
        typeof(ThreadWorker).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(worker, cancellation);
        var context = new RecordingContext();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                switcher.Dispose();
                completed.SetResult();
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            Assert.True(await Task.Run(() => cancellationHandle.WaitOne(TimeSpan.FromSeconds(5))));
        }
        finally
        {
            pendingWork.TrySetResult();
        }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, context.PostCount);
        Assert.Null(client.Object.ResponseHandlers);
        Assert.Null(client.Object.ConnectionStateHandlers);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int PostCount;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref PostCount);
            // Allow a regressed implementation to finish so the assertion fails without hanging.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
