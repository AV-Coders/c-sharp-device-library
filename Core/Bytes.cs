using System.Text;

namespace AVCoders.Core;

public static class Bytes
{
    public static byte[] FromString(string input)
    {
        return Encoding.ASCII.GetBytes(input);
    }
    
    public static byte[] AsciiRepresentationOfHexEquivalentOf(int value, int padding = 0)
    {
        return Encoding.ASCII.GetBytes(value.ToString($"X{padding}"));
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

    public static byte XorAList(List<Byte> bytes)
    {
        byte result = 0x00;

        foreach (byte theByte in bytes)
        {
            result ^= theByte;
        }

        return result;
    }
}