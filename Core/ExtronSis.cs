namespace AVCoders.Core;

public static class ExtronSis
{
    public const string InvalidCommand = "E10";
    public const string InvalidParameter = "E13";
    public const string NotValidForThisConfiguration = "E14";

    public static bool MeansNotSupported(string error) => error is InvalidCommand or InvalidParameter or NotValidForThisConfiguration;

    public static string Printable(string command) => command.Replace("\x1b", "Esc ").TrimEnd('\r');
}
