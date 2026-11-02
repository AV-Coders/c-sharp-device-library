using AVCoders.Core;

namespace AVCoders.Matrix;

internal sealed record ExtronSisCommand(string Text, string ReplyStart = "", ExtronSisQuery? Query = null, ExtronSisTie? Tie = null)
{
    public ExtronSisCommand(ExtronSisQuery query) : this(query.Command, Query: query)
    {
    }

    public ExtronSisCommand(string text, ExtronSisTie tie) : this(text, Tie: tie)
    {
    }

    public string Name => ExtronSis.Printable(Text);

    public bool IsAnsweredBy(string line, ExtronSisTie? tie)
    {
        if (Query != null)
            return Query.IsAnsweredBy(line);
        if (Tie != null)
            return tie != null && Tie.Answers(tie);
        return line.StartsWith(ReplyStart, StringComparison.Ordinal);
    }
}
