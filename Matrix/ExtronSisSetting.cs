using System.Collections.Concurrent;
using AVCoders.Core;

namespace AVCoders.Matrix;

internal sealed record ExtronSisSetting(ExtronSisQuery Read, Func<string, string> Set, Func<string, string?> Describe)
{
    private const int NeverTimesOut = 501;
    private const int Unmuted = 0;
    private const int VideoMuted = 1;
    private const int VideoAndSyncMuted = 2;

    public static string? Named<T>(string value) where T : struct, Enum =>
        int.TryParse(value, out var code) ? Enum.GetName(typeof(T), code) : null;

    public static string? SyncTimeoutText(string value)
    {
        if (!int.TryParse(value, out var seconds) || seconds is < 0 or > NeverTimesOut)
            return null;
        return seconds == NeverTimesOut ? "Never" : $"{seconds} s";
    }

    public static string? VideoMuteText(string value)
    {
        if (!int.TryParse(value, out var code))
            return null;
        return code switch
        {
            Unmuted => "Unmuted",
            VideoMuted => "Video muted",
            VideoAndSyncMuted => "Video and sync muted",
            _ => null
        };
    }
}

public abstract partial class ExtronSisMatrix
{
    private protected sealed class SettingsTracker(ExtronSisMatrix matrix)
    {
        private const string NotReadYet = "Not read yet";
        private static readonly TimeSpan ReconcileGap = TimeSpan.FromMilliseconds(300);
        private readonly ConcurrentDictionary<ExtronSisSetting, string> _desired = new();
        private readonly ConcurrentDictionary<ExtronSisSetting, (string Reply, string? Known)> _reported = new();

        public string? Desired(ExtronSisSetting setting) => _desired.GetValueOrDefault(setting);

        public int? DesiredNumber(ExtronSisSetting setting) => _desired.TryGetValue(setting, out var code) ? int.Parse(code) : null;

        public void SetDesired(ExtronSisSetting setting, string? code)
        {
            StoreDesired(setting, code);
            if (code != null)
                Send(setting, code);
        }

        public void StoreDesired(ExtronSisSetting setting, string? code)
        {
            if (code == null)
                _desired.TryRemove(setting, out _);
            else
                _desired[setting] = code;
            Show(setting);
        }

        public void Send(ExtronSisSetting setting, string code) =>
            matrix.Send(new ExtronSisCommand(setting.Set(code), setting.Read.ReplyTag));

        public void Handle(ExtronSisSetting setting, string reply, string value)
        {
            matrix.MarkRead(setting.Read);
            var known = setting.Describe(value);
            var seenBefore = _reported.TryGetValue(setting, out var previous) && previous.Reply == reply;
            _reported[setting] = (reply, known);
            if (known == null && !seenBefore)
                matrix.LogWarning("The switcher reported {Setting} as {Reply}, which the driver doesn't recognise", setting.Read.Label, reply);
            Show(setting);
            if (!_desired.TryGetValue(setting, out var desired) || known == setting.Describe(desired))
                return;
            matrix.LogWarning("Reconciling {Setting}: the switcher reported {Reply} but the desired value is {Desired}",
                setting.Read.Label, reply, setting.Describe(desired));
            matrix.AddEvent(EventType.DriverState, $"Reconciling {setting.Read.Label} to {setting.Describe(desired)}, the switcher reported {reply}");
            matrix.SendSpaced(async () =>
            {
                await Task.Delay(ReconcileGap, matrix.TimeProvider);
                Send(setting, desired);
            }, "The reconcile send failed");
        }

        private void Show(ExtronSisSetting setting)
        {
            var reported = _reported.TryGetValue(setting, out var read) ? read.Known ?? read.Reply : NotReadYet;
            if (!_desired.TryGetValue(setting, out var code))
            {
                matrix.SetDetail(setting.Read.Label, reported, read.Known == null ? DetailTone.Warning : DetailTone.Normal);
                return;
            }
            var desired = setting.Describe(code);
            matrix.SetDetail(setting.Read.Label, $"{reported}, desired {desired}", read.Known == desired ? DetailTone.Normal : DetailTone.Warning);
        }
    }
}
