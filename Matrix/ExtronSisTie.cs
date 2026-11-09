using System.Text.RegularExpressions;

namespace AVCoders.Matrix;

internal sealed partial record ExtronSisTie(int? Input, int Output, string Plane)
{
    public const string VideoAndAudio = "All";
    public const string Video = "Vid";
    public const string Audio = "Aud";
    public const string Rgb = "RGB";
    public const int AllOutputs = 0;

    [GeneratedRegex(@"^(?:Out\s*(?<output>\d+)\s+In\s*(?<input>\d+)\s+(?<plane>All|Vid|Aud)|In\s*(?<input>\d+)(?:\*(?<output>\d+))?\s+(?:(?<plane>All|Vid|Aud|RGB)|Ausw.*))$", RegexOptions.IgnoreCase)]
    private static partial Regex TieReplyRegex();

    public static ExtronSisTie? Parse(string line)
    {
        if (TieReplyRegex().Match(line) is not { Success: true } match || !int.TryParse(match.Groups["input"].Value, out var input))
            return null;
        if (!match.Groups["output"].Success)
            return new ExtronSisTie(input, AllOutputs, PlaneOf(match));
        return int.TryParse(match.Groups["output"].Value, out var output) ? new ExtronSisTie(input, output, PlaneOf(match)) : null;
    }

    private static string PlaneOf(Match match) => match.Groups["plane"].Success ? match.Groups["plane"].Value : VideoAndAudio;

    public bool Answers(ExtronSisTie reply) =>
        (Input == null || Input == reply.Input) && Output == reply.Output && string.Equals(Plane, reply.Plane, StringComparison.OrdinalIgnoreCase);
}
