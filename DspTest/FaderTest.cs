
using AVCoders.Core;

namespace AVCoders.Dsp.Tests;

public class TestableFader : Fader
{
    public TestableFader(VolumeLevelHandler volumeLevelHandler, bool convertLogarithmicToLinear) : base(volumeLevelHandler, convertLogarithmicToLinear)
    {
    }
}

public class FaderTest
{
    private Fader _linearFader;
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new Mock<VolumeLevelHandler>();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new Mock<MuteStateHandler>();
    private readonly Mock<StringValueHandler> _stringValueHandler = new Mock<StringValueHandler>();


    public FaderTest()
    {
        _linearFader = new TestableFader(_volumeLevelHandler.Object, false);
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(-99, 1)]
    [InlineData(0, 100)]
    public void SetVolumeFromDB_CorrectlyConverts(double input, int expectedPercentage)
    {
        _linearFader.SetVolumeFromDb(input);
        
        Assert.Equal(expectedPercentage, _linearFader.Volume);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(-10, 38)]
    [InlineData(6, 100)]
    public void SetVolumeFromDB_CorrectlyConvertsInOtherRanges(double input, int expectedPercentage)
    {
        _linearFader.SetMinGain(-20);
        _linearFader.SetMaxGain(6);
        
        _linearFader.SetVolumeFromDb(input);
        
        Assert.Equal(expectedPercentage, _linearFader.Volume);
    }

    [Theory]
    [InlineData(0, -100.0)]
    [InlineData(100, 0)]
    [InlineData(50, -50)]
    public void PercentageToDb_CorrectlyConverts(int percentage, double expectedDb)
    {
        double actual = _linearFader.PercentageToDb(percentage);
        
        Assert.Equal(expectedDb, actual);
    }

    [Theory]
    [InlineData(0, -20.0)]
    [InlineData(100, 6)]
    [InlineData(50, -7)]
    public void PercentageToDb_CorrectlyConvertsInOtherRanges(int percentage, double expectedDb)
    {
        _linearFader.SetMinGain(-20);
        _linearFader.SetMaxGain(6);
        double actual = _linearFader.PercentageToDb(percentage);
        
        Assert.Equal(expectedDb, actual);
    }
}