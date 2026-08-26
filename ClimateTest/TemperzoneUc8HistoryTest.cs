using Moq;

namespace AVCoders.Climate.Tests;

public class TemperzoneUc8HistoryTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"uc8-history-test-{Guid.NewGuid():N}");
    private readonly List<TemperzoneUc8History> _recorders = [];
    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);

    private TemperzoneUc8History Create(string? directory = null)
    {
        var history = new TemperzoneUc8History("Test HVAC", directory);
        _recorders.Add(history);
        return history;
    }

    private TemperzoneUc8History CreateLongRetention()
    {
        var history = Create();
        history.Retention = TimeSpan.FromDays(3650);
        return history;
    }

    public void Dispose()
    {
        foreach (var recorder in _recorders)
            recorder.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [Fact]
    public void Record_DeduplicatesUnchangedValues()
    {
        var history = Create();

        history.Record("X", 1.5f);
        history.Record("X", 1.5f);

        var sample = Assert.Single(history.GetSamples("X"));
        Assert.Equal(1.5f, sample.Value);
    }

    [Fact]
    public void Record_RecordsChangedValues()
    {
        var history = CreateLongRetention();

        history.Record("X", 1.5f, T1);
        history.Record("X", 2.5f, T2);
        history.Record("X", 1.5f, T3);

        Assert.Equal(new HistorySample[] { new(T1, 1.5f), new(T2, 2.5f), new(T3, 1.5f) }, history.GetSamples("X"));
    }

    [Fact]
    public void Record_InvokesSampleHandlersOnNewSamplesOnly()
    {
        var history = CreateLongRetention();
        var handler = new Mock<HistorySampleHandler>();
        history.SampleHandlers += handler.Object;

        history.Record("X", 1.5f, T1);
        history.Record("X", 1.5f, T2);

        handler.Verify(x => x.Invoke("X", new HistorySample(T1, 1.5f)), Times.Once);
        handler.Verify(x => x.Invoke(It.IsAny<string>(), It.IsAny<HistorySample>()), Times.Once);
    }

    [Fact]
    public void Record_PrunesSamplesOlderThanTheRetention()
    {
        var history = Create();
        history.Retention = TimeSpan.FromDays(1);

        history.Record("X", 1f, DateTimeOffset.UtcNow - TimeSpan.FromDays(2));
        history.Record("X", 2f, DateTimeOffset.UtcNow);

        var sample = Assert.Single(history.GetSamples("X"));
        Assert.Equal(2f, sample.Value);
    }

    [Fact]
    public void GetSamples_FiltersByRange()
    {
        var history = CreateLongRetention();
        history.Record("X", 1f, T1);
        history.Record("X", 2f, T2);
        history.Record("X", 3f, T3);

        Assert.Equal(2, history.GetSamples("X", from: T2).Count);
        Assert.Equal(2, history.GetSamples("X", to: T2).Count);
        var sample = Assert.Single(history.GetSamples("X", from: T2, to: T2));
        Assert.Equal(2f, sample.Value);
    }

    [Fact]
    public void GetPointNames_ReturnsSortedNames()
    {
        var history = Create();
        history.Record("Zeta", 1f);
        history.Record("Alpha", 1f);

        Assert.Equal(new[] { "Alpha", "Zeta" }, history.GetPointNames());
    }

    [Fact]
    public void ExportCsv_ProducesOrderedIsoRows()
    {
        var history = CreateLongRetention();
        history.Record("Y", 2.5f, T2);
        history.Record("X", 1.5f, T1);
        history.Record("X", 3f, T3);

        var lines = history.ExportCsv().Split('\n');

        Assert.Equal("Timestamp (UTC),Point,Value", lines[0]);
        Assert.Equal("2026-01-01T00:00:00.0000000Z,X,1.5", lines[1]);
        Assert.Equal("2026-01-01T01:00:00.0000000Z,Y,2.5", lines[2]);
        Assert.Equal("2026-01-01T02:00:00.0000000Z,X,3", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void ExportCsv_FiltersByRange()
    {
        var history = CreateLongRetention();
        history.Record("X", 1.5f, T1);
        history.Record("X", 3f, T3);

        var lines = history.ExportCsv(from: T2).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-01-01T02:00:00.0000000Z,X,3", lines[1]);
    }

    [Fact]
    public void Flush_PersistsSamplesForANewRecorder()
    {
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var first = Create(_directory);
        first.Record("X", 1.5f, recent);
        first.Record("Y", -2.5f, recent);
        first.Flush();

        var second = Create(_directory);

        Assert.Equal(new HistorySample[] { new(recent, 1.5f) }, second.GetSamples("X"));
        Assert.Equal(new HistorySample[] { new(recent, -2.5f) }, second.GetSamples("Y"));
    }

    [Fact]
    public void Dispose_FlushesPendingSamples()
    {
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var first = Create(_directory);
        first.Record("X", 1.5f, recent);
        first.Dispose();

        var second = Create(_directory);

        Assert.Single(second.GetSamples("X"));
    }

    [Fact]
    public void Load_PrunesSamplesOlderThanTheRetention()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "Test-HVAC.history.csv");
        var old = (DateTimeOffset.UtcNow - TimeSpan.FromDays(40)).UtcDateTime.ToString("O");
        var recent = (DateTimeOffset.UtcNow - TimeSpan.FromHours(1)).UtcDateTime.ToString("O");
        File.WriteAllLines(path, [$"{old},X,1", $"{recent},X,2"]);

        var history = Create(_directory);

        var sample = Assert.Single(history.GetSamples("X"));
        Assert.Equal(2f, sample.Value);
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(TemperzoneUc8History.CsvHeader, lines[0]);
    }

    [Fact]
    public void Load_HandlesACorruptFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "Test-HVAC.history.csv");
        File.WriteAllLines(path, ["complete", "garbage,here", "not,a,timestamp"]);

        var history = Create(_directory);
        history.Record("X", 1f);

        Assert.Equal(new[] { "X" }, history.GetPointNames());
        Assert.Single(history.GetSamples("X"));
    }

    [Fact]
    public void Load_HandlesAMissingFile()
    {
        var history = Create(_directory);
        history.Record("X", 1f);

        Assert.Single(history.GetSamples("X"));
    }

    [Fact]
    public void Record_AppliesTheDeadband()
    {
        var history = CreateLongRetention();
        history.SetDeadband("X", 0.2f);

        history.Record("X", 20.0f, T1);
        history.Record("X", 20.1f, T2);
        history.Record("X", 20.3f, T3);

        Assert.Equal(new HistorySample[] { new(T1, 20.0f), new(T3, 20.3f) }, history.GetSamples("X"));
    }

    [Fact]
    public void Record_HeartbeatRecordsAnUnchangedValueAfterAnHour()
    {
        var history = CreateLongRetention();

        history.Record("X", 1f, T1);
        history.Record("X", 1f, T1 + TimeSpan.FromMinutes(30));
        history.Record("X", 1f, T1 + TimeSpan.FromHours(2));

        Assert.Equal(2, history.GetSamples("X").Count);
    }

    [Fact]
    public void Record_HeartbeatAppliesToDeadbandPoints()
    {
        var history = CreateLongRetention();
        history.SetDeadband("X", 0.2f);

        history.Record("X", 20.0f, T1);
        history.Record("X", 20.1f, T1 + TimeSpan.FromHours(2));

        Assert.Equal(2, history.GetSamples("X").Count);
    }

    [Fact]
    public void GetLastUpdated_TracksEveryRecordCallEvenWhenDeduplicated()
    {
        var history = CreateLongRetention();

        history.Record("X", 1f, T1);
        history.Record("X", 1f, T2);

        Assert.Single(history.GetSamples("X"));
        Assert.Equal(T2, history.GetLastUpdated()["X"]);
    }

    [Fact]
    public void Flush_IsAppendOnly()
    {
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        var history = Create(_directory);
        history.Record("X", 1f, recent);
        history.Flush();
        history.Record("X", 2f, recent + TimeSpan.FromMinutes(5));
        history.Flush();

        var lines = File.ReadAllLines(history.FilePath!);

        Assert.Equal(3, lines.Length);
        Assert.Equal(TemperzoneUc8History.CsvHeader, lines[0]);
        Assert.EndsWith(",X,1", lines[1]);
        Assert.EndsWith(",X,2", lines[2]);
    }

    [Fact]
    public void Flush_RetainsLinesWhenTheFileIsLocked()
    {
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        var history = Create(_directory);
        history.Record("X", 1f, recent);

        using (new FileStream(history.FilePath!, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            history.Flush();

        Assert.Contains(history.GetIssues(), i => i.Key == "history-flush");
        Assert.Empty(File.ReadAllLines(history.FilePath!));

        history.Flush();

        var line = Assert.Single(File.ReadAllLines(history.FilePath!));
        Assert.EndsWith(",X,1", line);
    }

    [Fact]
    public void Load_SeedsDeduplicationAcrossRestarts()
    {
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
        var first = Create(_directory);
        first.Record("X", 5f, recent);
        first.Flush();

        var second = Create(_directory);
        second.Record("X", 5f);

        Assert.Single(second.GetSamples("X"));
    }
}
