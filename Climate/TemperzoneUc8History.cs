using System.Globalization;
using System.Text;
using AVCoders.Core;

namespace AVCoders.Climate;

public readonly record struct HistorySample(DateTimeOffset Timestamp, float Value);

public delegate void HistorySampleHandler(string pointName, HistorySample sample);

public class TemperzoneUc8History : LogBase, IDisposable
{
    public const string CsvHeader = "Timestamp (UTC),Point,Value";

    public const int PowerOffCode = 0;
    public const int PowerOnCode = 1;
    public const int PowerUnknownCode = -1;
    public const int HvacUnknownCode = 0;
    public const int HvacHeatCode = 1;
    public const int HvacCoolCode = 2;
    public const int HvacDryCode = 3;
    public const int HvacFanOnlyCode = 4;

    public const string OutdoorCoilTemperaturePoint = "Outdoor Coil Temperature";
    public const string IndoorCoilTemperaturePoint = "Indoor Coil Temperature";
    public const string OutdoorAmbientTemperaturePoint = "Outdoor Ambient Temperature";
    public const string SuctionLineTemperaturePoint = "Suction Line Temperature";
    public const string DischargeLineTemperaturePoint = "Discharge Line Temperature";
    public const string DeIceSensorTemperaturePoint = "De-Ice Sensor Temperature";
    public const string EvaporatingTemperaturePoint = "Evaporating Temperature";
    public const string CondensingTemperaturePoint = "Condensing Temperature";
    public const string ControllerTemperaturePoint = "Controller Temperature";
    public const string SuctionSideSuperheatPoint = "Suction Side Superheat";
    public const string DischargeSideSuperheatPoint = "Discharge Side Superheat";
    public const string SuctionLinePressurePoint = "Suction Line Pressure";
    public const string DischargeLinePressurePoint = "Discharge Line Pressure";
    public const string SupplyAirTemperaturePoint = "Supply Air Temperature";
    public const string ReturnAirTemperaturePoint = "Return Air Temperature";
    public const string SetpointTemperaturePoint = "Setpoint Temperature";
    public const string RoomTemperaturePoint = "Room Temperature";
    public const string IndoorFanSpeedPoint = "Indoor Fan Speed";
    public const string OutdoorFanSpeedPoint = "Outdoor Fan Speed";
    public const string CapacityPoint = "Capacity";
    public const string FanSpeedRequestPoint = "Fan Speed Request";
    public const string CapacityRequestPoint = "Capacity Request";
    public const string QuietModePoint = "Quiet Mode";
    public const string DryModePoint = "Dry Mode";
    public const string EconomyModePoint = "Economy Mode";
    public const string CoolingSupplyAirTargetPoint = "Cooling Supply Air Target";
    public const string HeatingSupplyAirTargetPoint = "Heating Supply Air Target";
    public const string UnitModePoint = "Unit Mode";
    public const string HvacModePoint = "Hvac Mode";
    public const string PowerStatePoint = "Power State";
    public const string DeIceRequestPoint = "De-Ice Request";
    public const string DeIceStatusPoint = "De-Ice Status";
    public const string FaultBank1Point = "Fault Bank 1";
    public const string FaultBank2Point = "Fault Bank 2";
    public const string FaultBank3Point = "Fault Bank 3";
    public const string FaultNumberPoint = "Fault Number";
    public const string MinimumRunTimerPoint = "Minimum Run Timer";
    public const string MinimumOffTimerPoint = "Minimum Off Timer";
    public const string CompressorStartTimerPoint = "Compressor Start Timer";
    public const string CoolingHoldOffTimerPoint = "Cooling Hold-Off Timer";
    public const string HeatingHoldOffTimerPoint = "Heating Hold-Off Timer";
    public const string CompressorRelayPoint = "Compressor Relay";
    public const string ReverseValvePoint = "Reverse Valve";
    public const string DredHoldOffPoint = "DRED Hold-Off";
    public const string OilRecoveryPoint = "Oil Recovery";
    public const string Exv1PositionPoint = "EXV 1 Position";
    public const string Exv2PositionPoint = "EXV 2 Position";

    public HistorySampleHandler? SampleHandlers;

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromHours(1);

    public string? FilePath { get; }

    private const string FlushIssueKey = "history-flush";
    private const int PendingLineCap = 100_000;
    private const double HundredthsPerUnit = 100;

    private static readonly TimeSpan CompactionInterval = TimeSpan.FromDays(1);

    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private readonly Dictionary<string, List<HistorySample>> _series = new();
    private readonly Dictionary<string, float> _lastValues = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRecorded = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new();
    private readonly Dictionary<string, double> _deadbandHundredths = new();
    private readonly List<string> _pendingLines = [];
    private readonly ThreadWorker? _flushWorker;
    private bool _pruned;
    private DateTimeOffset _lastCompaction = DateTimeOffset.UtcNow;

    public TemperzoneUc8History(string name, string? directory = null) : base(name)
    {
        if (directory == null)
            return;
        try
        {
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"{SanitizeFileName(name)}.history.csv");
            Load();
        }
        catch (Exception e)
        {
            using (PushProperties("Constructor"))
                LogWarning("Could not initialise history persistence in {Directory}: {Error}", directory, e.Message);
            FilePath = null;
            return;
        }
        _flushWorker = new ThreadWorker(FlushWorker, TimeSpan.FromMinutes(5), true);
        _flushWorker.Restart();
    }

    public void SetDeadband(string point, float deadband)
    {
        lock (_lock)
            _deadbandHundredths[point] = Hundredths(deadband);
    }

    public void Record(string point, float value) => Record(point, value, DateTimeOffset.UtcNow);

    public void Record(string point, float value, DateTimeOffset timestamp)
    {
        HistorySample sample;
        lock (_lock)
        {
            _lastSeen[point] = timestamp;
            var deadband = _deadbandHundredths.GetValueOrDefault(point);
            var hasLast = _lastValues.TryGetValue(point, out var last);
            var changed = !hasLast ||
                          (deadband > 0 ? Math.Abs(Hundredths(value) - Hundredths(last)) >= deadband : !last.Equals(value));
            var stale = _lastRecorded.TryGetValue(point, out var lastRecordedAt) &&
                        timestamp - lastRecordedAt > Heartbeat;
            if (!changed && !stale)
                return;
            _lastValues[point] = value;
            _lastRecorded[point] = timestamp;
            sample = new HistorySample(timestamp, value);
            if (!_series.TryGetValue(point, out var series))
            {
                series = [];
                _series[point] = series;
            }
            series.Add(sample);
            if (FilePath != null)
                _pendingLines.Add(Serialize(point, sample));
            PruneLocked(DateTimeOffset.UtcNow - Retention);
        }
        InvokeSampleHandlers(point, sample);
    }

    public IReadOnlyList<string> GetPointNames()
    {
        lock (_lock)
            return _series.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(k => k).ToList();
    }

    public IReadOnlyList<HistorySample> GetSamples(string point, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        lock (_lock)
        {
            if (!_series.TryGetValue(point, out var series))
                return [];
            IEnumerable<HistorySample> samples = series;
            if (from != null)
                samples = samples.Where(s => s.Timestamp >= from);
            if (to != null)
                samples = samples.Where(s => s.Timestamp <= to);
            return samples.ToList();
        }
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetLastUpdated()
    {
        lock (_lock)
            return new Dictionary<string, DateTimeOffset>(_lastSeen);
    }

    public string ExportCsv(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var rows = new List<(string Point, HistorySample Sample)>();
        lock (_lock)
        {
            foreach (var pair in _series)
                foreach (var sample in pair.Value)
                {
                    if (from != null && sample.Timestamp < from)
                        continue;
                    if (to != null && sample.Timestamp > to)
                        continue;
                    rows.Add((pair.Key, sample));
                }
        }
        var builder = new StringBuilder(CsvHeader);
        foreach (var row in rows.OrderBy(r => r.Sample.Timestamp))
        {
            builder.Append('\n');
            builder.Append(Serialize(row.Point, row.Sample));
        }
        return builder.ToString();
    }

    public void Flush()
    {
        if (FilePath == null)
            return;
        string? failure = null;
        lock (_fileLock)
        {
            List<string> pending;
            lock (_lock)
            {
                if (_pendingLines.Count == 0)
                    return;
                pending = [.._pendingLines];
                _pendingLines.Clear();
            }
            try
            {
                if (!File.Exists(FilePath))
                    File.WriteAllLines(FilePath, [CsvHeader]);
                AppendLines(FilePath, pending);
            }
            catch (Exception e)
            {
                RetainPending(pending);
                failure = e.Message;
            }
        }
        if (failure == null)
            ResolveIssue(FlushIssueKey);
        else
            RaiseMomentaryIssue($"Could not write history to {FilePath}: {failure}", key: FlushIssueKey,
                escalateAfter: 3);
    }

    private static void AppendLines(string path, List<string> lines)
    {
        var length = new FileInfo(path).Length;
        try
        {
            File.AppendAllLines(path, lines);
        }
        catch (Exception)
        {
            if (new FileInfo(path).Length != length)
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
                    file.SetLength(length);
            throw;
        }
    }

    private void RetainPending(List<string> pending)
    {
        lock (_lock)
        {
            _pendingLines.InsertRange(0, pending);
            if (_pendingLines.Count > PendingLineCap)
                _pendingLines.RemoveRange(0, _pendingLines.Count - PendingLineCap);
        }
    }

    public void Dispose()
    {
        _ = _flushWorker?.Stop();
        Flush();
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }

    private Task FlushWorker(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow - _lastCompaction >= CompactionInterval)
            Compact();
        else
            Flush();
        return Task.CompletedTask;
    }

    private void Compact()
    {
        if (FilePath == null)
            return;
        string? failure;
        lock (_fileLock)
        {
            List<string> lines;
            List<string> pending;
            lock (_lock)
            {
                PruneLocked(DateTimeOffset.UtcNow - Retention);
                if (!_pruned && _pendingLines.Count == 0)
                {
                    _lastCompaction = DateTimeOffset.UtcNow;
                    return;
                }
                lines = AllLinesLocked();
                pending = [.._pendingLines];
                _pendingLines.Clear();
                _pruned = false;
            }
            failure = WriteCompacted(FilePath, lines);
            if (failure != null)
                RetainPending(pending);
            _lastCompaction = DateTimeOffset.UtcNow;
        }
        if (failure == null)
            ResolveIssue(FlushIssueKey);
        else
        {
            using (PushProperties(nameof(Compact)))
                LogWarning("Could not compact history at {File}: {Error}", FilePath, failure);
            RaiseMomentaryIssue($"Could not compact history at {FilePath}", key: FlushIssueKey, escalateAfter: 3);
        }
    }

    private static string? WriteCompacted(string path, List<string> lines)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllLines(temporary, lines.Prepend(CsvHeader));
            File.Move(temporary, path, true);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private void Load()
    {
        if (FilePath == null || !File.Exists(FilePath))
            return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow - Retention;
            var entries = new List<(string Point, HistorySample Sample)>();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line == CsvHeader)
                    continue;
                var parts = line.Split(',');
                if (parts.Length != 3 ||
                    !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                        out var timestamp) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    using (PushProperties(nameof(Load)))
                        LogWarning("Skipped an unreadable line in {File}: {Line}", FilePath, line);
                    continue;
                }
                if (timestamp < cutoff)
                    continue;
                entries.Add((parts[1], new HistorySample(timestamp, value)));
            }
            entries.Sort((a, b) => a.Sample.Timestamp.CompareTo(b.Sample.Timestamp));
            List<string> lines;
            lock (_lock)
            {
                foreach (var (point, sample) in entries)
                {
                    if (!_series.TryGetValue(point, out var series))
                    {
                        series = [];
                        _series[point] = series;
                    }
                    series.Add(sample);
                    _lastValues[point] = sample.Value;
                    _lastRecorded[point] = sample.Timestamp;
                    _lastSeen[point] = sample.Timestamp;
                }
                lines = AllLinesLocked();
            }
            var failure = WriteCompacted(FilePath, lines);
            if (failure != null)
                using (PushProperties(nameof(Load)))
                    LogWarning("Could not compact history at {File}: {Error}", FilePath, failure);
            _lastCompaction = DateTimeOffset.UtcNow;
        }
        catch (Exception e)
        {
            using (PushProperties(nameof(Load)))
                LogWarning("Could not load history from {File}, starting fresh: {Error}", FilePath, e.Message);
            lock (_lock)
            {
                _series.Clear();
                _lastValues.Clear();
                _lastRecorded.Clear();
                _lastSeen.Clear();
                _pendingLines.Clear();
            }
        }
    }

    private List<string> AllLinesLocked() =>
        _series.SelectMany(kv => kv.Value.Select(s => (kv.Key, Sample: s)))
            .OrderBy(r => r.Sample.Timestamp)
            .Select(r => Serialize(r.Key, r.Sample))
            .ToList();

    private void PruneLocked(DateTimeOffset cutoff)
    {
        foreach (var series in _series.Values)
        {
            var removeCount = 0;
            while (removeCount < series.Count && series[removeCount].Timestamp < cutoff)
                removeCount++;
            if (removeCount == 0)
                continue;
            series.RemoveRange(0, removeCount);
            _pruned = true;
        }
    }

    private void InvokeSampleHandlers(string point, HistorySample sample)
    {
        try
        {
            SampleHandlers?.Invoke(point, sample);
        }
        catch (Exception e)
        {
            LogException(e, "A history sample handler threw an exception");
        }
    }

    private static double Hundredths(float value) => Math.Round(value * HundredthsPerUnit);

    private static string Serialize(string point, HistorySample sample) =>
        $"{sample.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)},{point},{sample.Value.ToString("R", CultureInfo.InvariantCulture)}";

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(invalid.Contains(c) || c == ' ' ? '-' : c);
        return builder.ToString();
    }
}
