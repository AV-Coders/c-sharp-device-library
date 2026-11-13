using System.Text;

namespace AVCoders.Core;

public static class Bytes
{
    private const byte FirstPrintable = 0x20;
    private const byte LastPrintable = 0x7E;
    private const string CutMark = "...";

    public static byte[] FromString(string input)
    {
        return Encoding.Latin1.GetBytes(input);
    }

    public static string Escaped(IReadOnlyCollection<byte> bytes, int maxShown) =>
        string.Concat(bytes.Take(maxShown).Select(b => b is >= FirstPrintable and <= LastPrintable ? ((char)b).ToString() : $"\\x{b:X2}"))
        + (bytes.Count > maxShown ? CutMark : "");

    public static byte[] AsciiRepresentationOfHexEquivalentOf(int value, int padding = 0, bool capitalise = true)
    {
        return Encoding.ASCII.GetBytes(capitalise ? value.ToString($"X{padding}") : value.ToString($"x{padding}"));
    }

    public static byte XorAnArray(byte[] bytes)
    {
        byte result = 0x00;

        foreach (byte theByte in bytes)
        {
            result ^= theByte;
        }

        return result;
    }

    public static byte XorAList(List<byte> bytes)
    {
        byte result = 0x00;

        foreach (byte theByte in bytes)
        {
            result ^= theByte;
        }

        return result;
    }
}