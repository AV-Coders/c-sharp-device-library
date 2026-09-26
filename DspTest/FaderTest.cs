
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
    private readonly Fader _linearFader;
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
    [InlineData(-6.5, 52)]
    [InlineData(6, 100)]
    public void SetVolumeFromDB_CorrectlyConvertsInOtherRanges(double input, int expectedPercentage)
    {
        _linearFader.SetMinGain(-20);
        _linearFader.SetMaxGain(6);
        
        _linearFader.SetVolumeFromDb(input);
        
        Assert.Equal(expectedPercentage, _linearFader.Volume);
    }

    [Fact]
    public void SetVolumeFromDB_Reports()
    {
        _linearFader.SetVolumeFromDb(0);
        
        _volumeLevelHandler.Verify(x => x.Invoke(100));
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

    [Fact]
    public void SetVolumeFromPercentage_Reports()
    {
        _linearFader.SetVolumeFromPercentage(100);
        
        _volumeLevelHandler.Verify(x => x.Invoke(100));
    }

    [Fact]
    public void Linear_SetVolumeFromDb_Rounds()
    {
        _linearFader.SetMaxGain(12);

        _linearFader.SetVolumeFromDb(-55.200001);

        Assert.Equal(40, _linearFader.Volume);
    }

    [Theory]
    [InlineData(-100, 12)]
    [InlineData(-100, 0)]
    [InlineData(-5, 5)]
    [InlineData(-20, 6)]
    [InlineData(-80, 20)]
    public void Linear_RoundTripsEveryPercentage_ThroughSinglePrecision(double min, double max)
    {
        _linearFader.SetMaxGain(max);
        _linearFader.SetMinGain(min);

        for (var percentage = 0; percentage <= 100; percentage++)
        {
            var reportedDb = (double)(float)_linearFader.PercentageToDb(percentage);
            _linearFader.SetVolumeFromDb(reportedDb);
            Assert.Equal(percentage, _linearFader.Volume);
        }
    }
}