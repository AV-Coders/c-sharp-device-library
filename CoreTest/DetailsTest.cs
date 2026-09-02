namespace AVCoders.Core.Tests;

[Collection("LogBaseIssues")]
public class DetailsTest : IDisposable
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public void Set(string label, string value, DetailTone tone = DetailTone.Normal) => SetDetail(label, value, tone);
        public void Remove(string label) => RemoveDetail(label);
    }

    private readonly TestLogBase _logBase = new("DetailsTest");

    public void Dispose() => LogBaseRegistry.Deregister(_logBase);

    [Fact]
    public void SetDetail_KeepsFirstInsertionOrder()
    {
        _logBase.Set("Serial", "123");
        _logBase.Set("Firmware", "1.0");
        _logBase.Set("Serial", "456");

        Assert.Equal(["Serial", "Firmware"], _logBase.Details.Select(d => d.Label));
        Assert.Equal("456", _logBase.Details[0].Value);
    }

    [Fact]
    public void SetDetail_RaisesOnlyWhenChanged()
    {
        int raised = 0;
        _logBase.DetailsUpdated += () => raised++;

        _logBase.Set("Serial", "123");
        _logBase.Set("Serial", "123");
        _logBase.Set("Serial", "123", DetailTone.Warning);
        _logBase.Set("Serial", "124", DetailTone.Warning);

        Assert.Equal(3, raised);
        Assert.Equal(new DeviceDetail("Serial", "124", DetailTone.Warning), Assert.Single(_logBase.Details));
    }

    [Fact]
    public void Details_SnapshotIsCachedUntilMutation()
    {
        _logBase.Set("Serial", "123");
        var first = _logBase.Details;
        Assert.Same(first, _logBase.Details);

        _logBase.Set("Serial", "124");
        Assert.NotSame(first, _logBase.Details);
    }

    [Fact]
    public void DetailsUpdated_ThrowingSubscriberDoesNotStopOthersOrPropagate()
    {
        bool secondCalled = false;
        _logBase.DetailsUpdated += () => throw new InvalidOperationException("boom");
        _logBase.DetailsUpdated += () => secondCalled = true;

        var exception = Record.Exception(() => _logBase.Set("Serial", "123"));

        Assert.Null(exception);
        Assert.True(secondCalled);
        Assert.Single(_logBase.Details);
    }

    [Fact]
    public void RemoveDetail_RemovesAndRaisesOnce()
    {
        int raised = 0;
        _logBase.Set("Serial", "123");
        _logBase.Set("Firmware", "1.0");
        _logBase.DetailsUpdated += () => raised++;

        _logBase.Remove("Serial");
        _logBase.Remove("Serial");

        Assert.Equal(1, raised);
        Assert.Equal("Firmware", Assert.Single(_logBase.Details).Label);
    }
}
