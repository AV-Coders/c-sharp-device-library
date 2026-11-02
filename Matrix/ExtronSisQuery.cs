using AVCoders.Core;

namespace AVCoders.Matrix;

internal record ExtronSisQuery(string Command, string ReplyTag, string Label, string IssueKey)
{
    public string Name => ExtronSis.Printable(Command);

    public bool IsAnsweredBy(string line) => line.StartsWith(ReplyTag.TrimEnd(), StringComparison.Ordinal);
}
