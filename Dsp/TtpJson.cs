using System.Text;
using System.Text.RegularExpressions;

namespace AVCoders.Dsp;

public static class TtpJson
{
    private const string Delimiters = "{}[]:\"";
    private static readonly Regex JsonNumber = new(@"^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$", RegexOptions.None, TimeSpan.FromMilliseconds(200));

    public static string ToJson(string ttp)
    {
        var json = new StringBuilder(ttp.Length);
        bool afterValue = false;
        int i = 0;
        while (i < ttp.Length)
        {
            char c = ttp[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '{':
                case '[':
                    if (afterValue)
                        json.Append(',');
                    json.Append(c);
                    afterValue = false;
                    i++;
                    break;
                case '}':
                case ']':
                    json.Append(c);
                    afterValue = true;
                    i++;
                    break;
                case ':':
                    json.Append(c);
                    afterValue = false;
                    i++;
                    break;
                case '"':
                    if (afterValue)
                        json.Append(',');
                    int stringEnd = StringEnd(ttp, i);
                    json.Append(ttp, i, stringEnd - i);
                    afterValue = true;
                    i = stringEnd;
                    break;
                default:
                    if (afterValue)
                        json.Append(',');
                    int tokenEnd = BareTokenEnd(ttp, i);
                    string token = ttp[i..tokenEnd];
                    json.Append(IsJsonLiteral(token) ? token : $"\"{token}\"");
                    afterValue = true;
                    i = tokenEnd;
                    break;
            }
        }
        return json.ToString();
    }

    private static int StringEnd(string ttp, int start)
    {
        int i = start + 1;
        while (i < ttp.Length && ttp[i] != '"')
            i += ttp[i] == '\\' ? 2 : 1;
        return Math.Min(i + 1, ttp.Length);
    }

    private static int BareTokenEnd(string ttp, int start)
    {
        int i = start;
        while (i < ttp.Length && !char.IsWhiteSpace(ttp[i]) && !Delimiters.Contains(ttp[i]))
            i++;
        return i;
    }

    private static bool IsJsonLiteral(string token) =>
        token is "true" or "false" or "null" || JsonNumber.IsMatch(token);
}
