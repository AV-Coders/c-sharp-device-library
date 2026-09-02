namespace AVCoders.Core;

public enum DetailTone
{
    Normal,
    Warning,
    Error
}

public record DeviceDetail(string Label, string Value, DetailTone Tone = DetailTone.Normal);
