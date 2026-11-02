using System.Net;
using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.Motor;

public record BondActionResult(bool Succeeded, string Failure);

public class BondGroup : Motor
{
    private readonly string _deviceId;
    private readonly BondBridge _bridge;

    public BondGroup(string name, string groupId, RelayAction powerOnAction, int moveSeconds, BondBridge bridge) 
        : base(name, powerOnAction, moveSeconds, bridge.Comms)
    {
        _deviceId = groupId;
        _bridge = bridge;
    }

    public override void Raise()
    {
        _ = TrackAction(nameof(Raise), _bridge.OpenGroup(_deviceId));
    }

    public override void Lower()
    {
        _ = TrackAction(nameof(Lower), _bridge.CloseGroup(_deviceId));
    }

    public override void Stop()
    {
        _ = TrackAction(nameof(Stop), _bridge.StopGroup(_deviceId));
    }

    private async Task TrackAction(string command, Task<BondActionResult> action)
    {
        BondActionResult result;
        try
        {
            result = await action;
        }
        catch (Exception e)
        {
            using (PushProperties(command))
                LogException(e, $"{command} of Bond group {_deviceId} failed");
            CommunicationState = CommunicationState.Error;
            return;
        }

        if (result.Succeeded)
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }
        using (PushProperties(command))
            LogWarning("{Command} of Bond group {GroupId} failed: {Failure}", command, _deviceId, result.Failure);
        CommunicationState = CommunicationState.Error;
    }
}

public class BondDevice : Motor
{
    private readonly string _deviceId;
    private readonly BondBridge _bridge;

    public BondDevice(string name, string deviceId, RelayAction powerOnAction, int moveSeconds, BondBridge bridge) 
        : base(name, powerOnAction, moveSeconds, bridge.Comms)
    {
        _deviceId = deviceId;
        _bridge = bridge;
    }

    public override void Raise()
    {
        _ = TrackAction(nameof(Raise), _bridge.OpenDevice(_deviceId));
    }

    public override void Lower()
    {
        _ = TrackAction(nameof(Lower), _bridge.CloseDevice(_deviceId));
    }

    public override void Stop()
    {
        _ = TrackAction(nameof(Stop), _bridge.StopDevice(_deviceId));
    }

    private async Task TrackAction(string command, Task<BondActionResult> action)
    {
        BondActionResult result;
        try
        {
            result = await action;
        }
        catch (Exception e)
        {
            using (PushProperties(command))
                LogException(e, $"{command} of Bond device {_deviceId} failed");
            CommunicationState = CommunicationState.Error;
            return;
        }

        if (result.Succeeded)
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }
        using (PushProperties(command))
            LogWarning("{Command} of Bond device {DeviceId} failed: {Failure}", command, _deviceId, result.Failure);
        CommunicationState = CommunicationState.Error;
    }
}

public class BondBridge : LogBase
{
    private const string DefaultName = "Bond Bridge";
    private const string VersionPath = "/v2/sys/version";
    private const string VersionIssueKey = "version-read";
    private const string FirmwareProperty = "fw_ver";
    private const string ModelProperty = "model";
    private const string BondIdProperty = "bondid";
    private const string UptimeProperty = "uptime_s";
    private const string FirmwareLabel = "Firmware";
    private const string ModelLabel = "Model";
    private const string BondIdLabel = "Bond ID";
    private const string UptimeLabel = "Uptime";
    private const string UptimeFormat = @"d\d\ h\h\ m\m";
    private const string NotSupportedPrefix = "Not supported: ";
    private const int MaxMissedReplies = 3;
    private static readonly string[] VersionLabels = [FirmwareLabel, ModelLabel, BondIdLabel, UptimeLabel];
    private static readonly HttpStatusCode[] UnsupportedStatuses =
        [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotImplemented];
    private static readonly BondActionResult Succeeded = new(true, string.Empty);
    private static readonly BondActionResult NoResponse = new(false, "no response");
    private static readonly Uri VersionUri = new(VersionPath, UriKind.Relative);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(85);
    private readonly RestComms _comms;
    private readonly ThreadWorker _pollWorker;
    private readonly AsyncLocal<TaskCompletionSource<BondActionResult>?> _sendingAction = new();
    private int _missedVersionReplies;

    public BondBridge(RestComms comms, string token) : this(comms, token, DefaultName)
    {
    }

    public BondBridge(RestComms comms, string token, string name = DefaultName) : base(name)
    {
        _comms = comms;
        _comms.AddDefaultHeader("BOND-Token", token);
        _comms.HttpResponseHandlers += HandleResponse;

        _pollWorker = new ThreadWorker(Poll, PollInterval, true);
        _pollWorker.Restart();
    }

    internal RestComms Comms => _comms;

    private void HandleResponse(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri?.AbsolutePath == VersionPath)
        {
            HandleVersion(response);
            return;
        }
        _sendingAction.Value?.TrySetResult(response.IsSuccessStatusCode
            ? Succeeded
            : new BondActionResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
    }

    private void HandleVersion(HttpResponseMessage response)
    {
        using (PushProperties())
        {
            string body = response.Content.ReadAsStringAsync().Result;
            string reply = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}, body: {body}";
            if (UnsupportedStatuses.Contains(response.StatusCode))
            {
                foreach (var label in VersionLabels)
                    ShowNotSupported(label, reply, reply);
                VersionAnswered();
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                CountMissedReply(reply);
                return;
            }

            if (ParseJsonObject(body) is not { } version)
            {
                CountMissedReply($"a reply that is not a JSON object: {body}");
                return;
            }

            ShowFirmware(version);
            ShowOptionalValue(version, ModelProperty, ModelLabel, body);
            ShowOptionalValue(version, BondIdProperty, BondIdLabel, body);
            ShowUptime(version, body);

            if (version.TryGetProperty(FirmwareProperty, out _))
                VersionAnswered();
            else
                CountMissedReply($"a reply with no {FirmwareProperty}: {body}");
        }
    }

    private void CountMissedReply(string result)
    {
        var missed = Interlocked.Increment(ref _missedVersionReplies);
        if (missed < MaxMissedReplies)
        {
            LogInformation("GET {Path} got {Result}, asking again at the next poll ({Missed} of {MaxMissedReplies})",
                VersionPath, result, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(VersionIssueKey,
            $"GET {VersionPath} did not get a real reply {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private void VersionAnswered()
    {
        Interlocked.Exchange(ref _missedVersionReplies, 0);
        ResolveIssue(VersionIssueKey);
    }

    private void ShowFirmware(JsonElement version)
    {
        if (version.TryGetProperty(FirmwareProperty, out var firmware))
            SetDetail(FirmwareLabel, RawText(firmware));
        else
            RemoveDetail(FirmwareLabel);
    }

    private void ShowOptionalValue(JsonElement version, string property, string label, string body)
    {
        if (version.TryGetProperty(property, out var value))
            SetDetail(label, RawText(value));
        else
            ShowNotSupported(label, MissingFromTheReply(property), body);
    }

    private void ShowNotSupported(string label, string reason, string reply)
    {
        if (!Details.Any(detail => detail.Label == label && detail.Value.StartsWith(NotSupportedPrefix)))
            LogInformation("The bridge does not support {Label}: {Reply}", label, reply);
        SetDetail(label, $"{NotSupportedPrefix}{reason}", DetailTone.Normal);
    }

    private static string MissingFromTheReply(string property) => $"no \"{property}\" in the version reply";

    private void ShowUptime(JsonElement version, string body)
    {
        if (!version.TryGetProperty(UptimeProperty, out var uptime))
        {
            ShowNotSupported(UptimeLabel, MissingFromTheReply(UptimeProperty), body);
            return;
        }
        if (uptime.ValueKind == JsonValueKind.Number && uptime.TryGetInt64(out long seconds)
            && seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds)
            SetDetail(UptimeLabel, TimeSpan.FromSeconds(seconds).ToString(UptimeFormat));
        else
            SetDetail(UptimeLabel, RawText(uptime), DetailTone.Warning);
    }

    private static string RawText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static JsonElement? ParseJsonObject(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task Poll(CancellationToken arg)
    {
        await _comms.Get(VersionUri);
    }

    private async Task<BondActionResult> ExecuteAction(string type, string id, string action)
    {
        var reply = new TaskCompletionSource<BondActionResult>();
        _sendingAction.Value = reply;
        await _comms.Put(new Uri($"/v2/{type}/{id}/actions/{action}", UriKind.Relative), "{}", "application/json");
        return reply.Task.IsCompleted ? reply.Task.Result : NoResponse;
    }

    public Task<BondActionResult> OpenDevice(string deviceId)
    {
        return ExecuteAction("devices", deviceId, "Open");
    }

    public Task<BondActionResult> CloseDevice(string deviceId)
    {
        return ExecuteAction("devices", deviceId, "Close");
    }

    public Task<BondActionResult> StopDevice(string deviceId)
    {
        return ExecuteAction("devices", deviceId, "Hold");
    }

    public Task<BondActionResult> OpenGroup(string groupId)
    {
        return ExecuteAction("groups", groupId, "Open");
    }

    public Task<BondActionResult> CloseGroup(string groupId)
    {
        return ExecuteAction("groups", groupId, "Close");
    }

    public Task<BondActionResult> StopGroup(string groupId)
    {
        return ExecuteAction("groups", groupId, "Hold");
    }
}