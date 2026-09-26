
using AVCoders.Core;

namespace AVCoders.Dsp.Tests;

public class TestableFader : Fader
{
    public TestableFader(VolumeLevelHandler volumeLevelHandler, FaderCurve curve = FaderCurve.Linear) : base(volumeLevelHandler, curve)
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
        _linearFader = new TestableFader(_volumeLevelHandler.Object);
    }

    private TestableFader PerceptualFader(double min, double max)
    {
        var fader = new TestableFader(_volumeLevelHandler.Object, FaderCurve.Perceptual);
        fader.SetMaxGain(max);
        fader.SetMinGain(min);
        return fader;
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
    public void Constructor_DefaultsToLinear()
    {
        Assert.Equal(FaderCurve.Linear, _linearFader.Curve);
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

    [Fact]
    public void Linear_DoesNotRecalculate_WhenTheRangeArrivesAfterTheLevel()
    {
        _linearFader.SetVolumeFromDb(-18);

        _linearFader.SetMaxGain(12);
        _linearFader.SetMinGain(-100);

        Assert.Equal(82, _linearFader.Volume);
        _volumeLevelHandler.Verify(x => x.Invoke(It.IsAny<int>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(0, -100.0)]
    [InlineData(1, -47.4)]
    [InlineData(25, -33.0)]
    [InlineData(50, -18.0)]
    [InlineData(75, -3.0)]
    [InlineData(100, 12.0)]
    public void Perceptual_PercentageToDb_UsesTheTopSixtyDbOfAWideRange(int percentage, double expectedDb)
    {
        var fader = PerceptualFader(-100, 12);

        Assert.Equal(expectedDb, fader.PercentageToDb(percentage), 6);
    }

    [Theory]
    [InlineData(0, -5.0)]
    [InlineData(1, -4.9)]
    [InlineData(25, -2.5)]
    [InlineData(50, 0.0)]
    [InlineData(100, 5.0)]
    public void Perceptual_PercentageToDb_IsLinearAcrossANarrowRange(int percentage, double expectedDb)
    {
        var fader = PerceptualFader(-5, 5);

        Assert.Equal(expectedDb, fader.PercentageToDb(percentage), 6);
    }

    [Theory]
    [InlineData(-5, 5)]
    [InlineData(-40, 10)]
    [InlineData(-60, 0)]
    [InlineData(-20, -10)]
    public void Perceptual_PercentageToDbMatchesLinear_WhenTheRangeIsSixtyDbOrLess(double min, double max)
    {
        var perceptual = PerceptualFader(min, max);
        _linearFader.SetMaxGain(max);
        _linearFader.SetMinGain(min);

        for (var percentage = 0; percentage <= 100; percentage++)
            Assert.Equal(_linearFader.PercentageToDb(percentage), perceptual.PercentageToDb(percentage), 6);
    }

    [Theory]
    [InlineData(-100.0, 0)]
    [InlineData(-120.0, 0)]
    [InlineData(-70.0, 1)]
    [InlineData(-48.0, 1)]
    [InlineData(-47.4, 1)]
    [InlineData(-18.0, 50)]
    [InlineData(12.0, 100)]
    [InlineData(20.0, 100)]
    public void Perceptual_SetVolumeFromDb_CorrectlyConverts(double db, int expectedPercentage)
    {
        var fader = PerceptualFader(-100, 12);

        fader.SetVolumeFromDb(db);

        Assert.Equal(expectedPercentage, fader.Volume);
    }

    [Theory]
    [InlineData(-100, 12)]
    [InlineData(-100, 0)]
    [InlineData(-5, 5)]
    [InlineData(-60, 0)]
    [InlineData(-40, 10)]
    [InlineData(-20, -10)]
    [InlineData(-80, 20)]
    public void Perceptual_RoundTripsEveryPercentage_ThroughSinglePrecision(double min, double max)
    {
        var fader = PerceptualFader(min, max);

        for (var percentage = 0; percentage <= 100; percentage++)
        {
            var reportedDb = (double)(float)fader.PercentageToDb(percentage);
            fader.SetVolumeFromDb(reportedDb);
            Assert.Equal(percentage, fader.Volume);
        }
    }

    [Fact]
    public void Perceptual_RecalculatesTheVolume_WhenTheRangeArrivesAfterTheLevel()
    {
        var fader = new TestableFader(_volumeLevelHandler.Object, FaderCurve.Perceptual);
        fader.SetVolumeFromDb(-18);
        Assert.Equal(70, fader.Volume);

        fader.SetMaxGain(12);

        Assert.Equal(50, fader.Volume);
        _volumeLevelHandler.Verify(x => x.Invoke(50));
    }

    [Fact]
    public void Perceptual_RecalculatesANarrowRange_WhenMinArrivesAfterTheLevel()
    {
        var fader = new TestableFader(_volumeLevelHandler.Object, FaderCurve.Perceptual);
        fader.SetMaxGain(5);
        fader.SetVolumeFromDb(0);

        fader.SetMinGain(-5);

        Assert.Equal(50, fader.Volume);
    }

    [Fact]
    public void Perceptual_SettlesOnTheRange_WhenMinArrivesBeforeMax()
    {
        var fader = new TestableFader(_volumeLevelHandler.Object, FaderCurve.Perceptual);
        fader.SetVolumeFromDb(0);

        fader.SetMinGain(-5);
        fader.SetMaxGain(5);

        Assert.Equal(50, fader.Volume);
    }

    [Fact]
    public void Perceptual_DoesNotRevertAPercentage_WhenTheRangeIsRequeried()
    {
        var fader = PerceptualFader(-100, 12);
        fader.SetVolumeFromDb(-18);
        fader.SetVolumeFromPercentage(80);

        fader.SetMaxGain(12);
        fader.SetMinGain(-100);

        Assert.Equal(80, fader.Volume);
    }

    [Theory]
    [InlineData(-9.75, 3)]
    [InlineData(-4.75, 53)]
    public void Perceptual_SetVolumeFromDb_RoundsHalvesAwayFromZero(double db, int expectedPercentage)
    {
        var fader = PerceptualFader(-10, 0);

        fader.SetVolumeFromDb(db);

        Assert.Equal(expectedPercentage, fader.Volume);
    }
}