namespace AVCoders.Core.Tests;

public class BytesTest
{
    [Fact]
    public void FromString_ReturnsBytes()
    {
        var actual = Bytes.FromString("01");

        byte[] expected = { 0x30, 0x31 };
        
        Assert.Equal(expected, actual);
    }
    
    [Fact]
    public void FromString_AcceptsNonAsciiCharacters()
    {
        var actual = Bytes.FromString("01\u0003\r");

        byte[] expected = { 0x30, 0x31, 0x03, 0x0D };
        
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AsciiRepresentationOfHexEquivalentOf_ReturnsTheCorrectValue()
    {
        //Decimal 12 = Hex C => '0' 'C'
        var actual = Bytes.AsciiRepresentationOfHexEquivalentOf(12, 2);

        byte[] expected = { (byte)'0', (byte)'C' };
        Assert.Equal(expected, actual);
        Assert.Equal(new byte[]{0x30, 0x43}, actual);
    }

    [Fact]
    public void AsciiRepresentationOfHexEquivalentOf_HandlesTwoDigitNumbers()
    {
        var actual = Bytes.AsciiRepresentationOfHexEquivalentOf(50, 2);
        
        //Decimal 50 = Hex 32 => '3' '2'
        byte[] expected = { (byte)'3', (byte)'2' };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AsciiRepresentationOfHexEquivalentOf_DoesNotTruncate()
    {
        var actual = Bytes.AsciiRepresentationOfHexEquivalentOf(50, 1);

        byte[] expected = { (byte)'3', (byte)'2' };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AsciiRepresentationOfHexEquivalentOf_PadsWithZeros()
    {
        var actual = Bytes.AsciiRepresentationOfHexEquivalentOf(50, 5);
        
        //Decimal 50 = Hex 32 => '0' '0' '0' '3' '2'
        byte[] expected = {(byte)'0', (byte)'0', (byte)'0',  (byte)'3', (byte)'2' };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void XorAnArray_ReturnsTheValue()
    {
        var actual = Bytes.XorAnArray(new byte[] { 0x01, 0x01 });

        byte expected = 0;
        
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void XorAnArray_CalculatesANecChecksum()
    {
        var actual = Bytes.XorAnArray(new byte[] { 0x30, 0x41, 0x30, 0x41, 0x30, 0x43, 0x02, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, 0x03 });

        byte expected = 0x73;
        
        Assert.Equal(expected, actual);
    }
}