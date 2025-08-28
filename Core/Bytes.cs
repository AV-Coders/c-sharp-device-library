using System.Text;

namespace AVCoders.Core;

public static class Bytes
{
    public static byte[] FromString(string input)
    {
        return Encoding.ASCII.GetBytes(input);
    }
    
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