using System;
using System.Reflection;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Unit tests for the pre-chain ReplayGain node gain and the ReplayGainMath static API:
/// 1. Node passes unity when ReplayGain is off, the track is null, or the normalizer owns the tag.
/// 2. ReplayGain Track mode (10^((TrackGain + Preamp) / 20)).
/// 3. ReplayGain Album mode (10^((AlbumGain + Preamp) / 20)).
/// 4. Anti-clipping prevention (limiting g <= 1.0 / peak).
/// 5. Overall gain clamping ([0.0f, 8.0f]).
/// The master fader is a separate post-chain multiply and deliberately appears in none of these
/// numbers — volume must never leak into the node gain (that fusion was the AGC-compensates-the-
/// slider defect).
/// </summary>
public class ReplayGainAndVolumeMathTests : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _playlists;
    private readonly PlaybackController _controller;
    private readonly MethodInfo _nodeGainMethod;

    public ReplayGainAndVolumeMathTests()
    {
        _settings = AppSettings.CreateDefault();
        _library = new MusicLibrary();
        _playlists = new PlaylistManager(_library);
        _controller = new PlaybackController(_settings, _playlists);

        _nodeGainMethod = typeof(PlaybackController).GetMethod(
            "ComputeReplayGainNodeGain",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(_nodeGainMethod);
    }

    public void Dispose()
    {
        _controller.Dispose();
        _library.Dispose();
    }

    private float NodeGain(Track? track)
    {
        return (float)_nodeGainMethod.Invoke(_controller, new object?[] { track })!;
    }

    #region 1. Null Track, ReplayGain Off, Normalizer Ownership

    [Fact]
    public void NodeGain_NullTrack_ReturnsOne()
    {
        _settings.Playback.Volume = 0.5;
        _settings.Playback.ReplayGain = ReplayGainMode.Track;

        float gain = NodeGain(null);
        Assert.Equal(1.0f, gain);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    public void NodeGain_ReplayGainOff_IsUnity_RegardlessOfVolume(double volume)
    {
        // The node is source correction only: the listening volume rides the post-chain fader and
        // must not leak in here (that fusion let the AGC compensate the slider away).
        _settings.Playback.Volume = volume;
        _settings.Playback.ReplayGain = ReplayGainMode.Off;
        _settings.Playback.ReplayGainPreampDb = 6.0;

        var track = new Track
        {
            Path = "test.mp3",
            RgTrackGainDb = -6.0,
            RgTrackPeak = 0.95,
            RgAlbumGainDb = -4.0,
            RgAlbumPeak = 0.90
        };

        float gain = NodeGain(track);
        Assert.Equal(1.0f, gain, precision: 5);
    }

    [Fact]
    public void NodeGain_NormalizerEnabled_OwnsTheTag_NodePassesUnity()
    {
        // With the normalizer on, ReplayGain rides the normalizer's static paths; the node must
        // stay at unity or the tag would apply twice.
        _settings.Normalizer.Enabled = true;
        _settings.Playback.Volume = 0.7;
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 3.0;

        var track = new Track { Path = "tagged.mp3", RgTrackGainDb = -6.0 };

        Assert.Equal(1.0f, NodeGain(track));
    }

    #endregion

    #region 2. ReplayGain Track Mode Tests

    [Fact]
    public void NodeGain_TrackMode_AppliesTrackGainAndPreamp()
    {
        _settings.Playback.Volume = 0.3; // must not influence the node
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = false;

        // -6.0205999 dB -> 10^(-6.0205999/20) ≈ 0.5
        var track = new Track
        {
            Path = "test.mp3",
            RgTrackGainDb = -6.020599913279624,
            RgAlbumGainDb = -12.0
        };

        float gain = NodeGain(track);
        Assert.Equal(0.5f, gain, precision: 4);
    }

    [Fact]
    public void NodeGain_TrackMode_WithPreamp_CombinesDecibels()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 6.020599913279624;
        _settings.Playback.ReplayGainPreventClipping = false;

        // TrackGain = -6.02 dB, Preamp = +6.02 dB -> Net 0 dB -> multiplier 1.0
        var track = new Track
        {
            Path = "test.mp3",
            RgTrackGainDb = -6.020599913279624
        };

        float gain = NodeGain(track);
        Assert.Equal(1.0f, gain, precision: 4);
    }

    [Fact]
    public void NodeGain_TrackMode_Untagged_FallsBackToUnity()
    {
        _settings.Playback.Volume = 0.7;
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 3.0;

        var track = new Track
        {
            Path = "untagged.mp3",
            RgTrackGainDb = null
        };

        float gain = NodeGain(track);
        Assert.Equal(1.0f, gain, precision: 5);
    }

    #endregion

    #region 3. ReplayGain Album Mode Tests

    [Fact]
    public void NodeGain_AlbumMode_UsesAlbumGainAndAlbumPeak()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Album;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = false;

        // AlbumGain = +6.0206 dB (x2.0), TrackGain = -20 dB (ignored)
        var track = new Track
        {
            Path = "test.flac",
            RgTrackGainDb = -20.0,
            RgAlbumGainDb = 6.020599913279624
        };

        float gain = NodeGain(track);
        Assert.Equal(2.0f, gain, precision: 4);
    }

    [Fact]
    public void NodeGain_AlbumMode_Untagged_FallsBackToUnity()
    {
        _settings.Playback.Volume = 0.8;
        _settings.Playback.ReplayGain = ReplayGainMode.Album;
        _settings.Playback.ReplayGainPreampDb = 4.0;

        var track = new Track
        {
            Path = "untagged.flac",
            RgTrackGainDb = -3.0,
            RgAlbumGainDb = null
        };

        float gain = NodeGain(track);
        Assert.Equal(1.0f, gain, precision: 5);
    }

    #endregion

    #region 4. Anti-Clipping Prevention Tests

    [Fact]
    public void NodeGain_AntiClipping_ClampsGainToInversePeak()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = true;

        // TrackGain = +6.0206 dB (x2.0 multiplier), Peak = 0.8 -> max 1.25
        var track = new Track
        {
            Path = "loud.mp3",
            RgTrackGainDb = 6.020599913279624,
            RgTrackPeak = 0.8
        };

        float gain = NodeGain(track);
        Assert.Equal(1.25f, gain, precision: 4);
    }

    [Fact]
    public void NodeGain_AntiClipping_Disabled_AllowsGainExceedingInversePeak()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = false; // Disabled

        var track = new Track
        {
            Path = "loud.mp3",
            RgTrackGainDb = 6.020599913279624, // x2.0
            RgTrackPeak = 0.8                  // max 1.25
        };

        float gain = NodeGain(track);
        Assert.Equal(2.0f, gain, precision: 4);
    }

    [Fact]
    public void NodeGain_AntiClipping_DoesNotAlterGainWhenBelowLimit()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = true;

        // TrackGain = -6.02 dB -> 0.5. Peak = 0.8 -> max = 1.25. Since 0.5 <= 1.25, remains 0.5.
        var track = new Track
        {
            Path = "moderate.mp3",
            RgTrackGainDb = -6.020599913279624,
            RgTrackPeak = 0.8
        };

        float gain = NodeGain(track);
        Assert.Equal(0.5f, gain, precision: 4);
    }

    [Fact]
    public void NodeGain_AntiClipping_IgnoresNullOrZeroOrNegativePeak()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = true;

        // Peak is null
        var track1 = new Track
        {
            Path = "no_peak.mp3",
            RgTrackGainDb = 6.020599913279624,
            RgTrackPeak = null
        };
        Assert.Equal(2.0f, NodeGain(track1), precision: 4);

        // Peak is 0
        var track2 = new Track
        {
            Path = "zero_peak.mp3",
            RgTrackGainDb = 6.020599913279624,
            RgTrackPeak = 0.0
        };
        Assert.Equal(2.0f, NodeGain(track2), precision: 4);

        // Peak is negative
        var track3 = new Track
        {
            Path = "neg_peak.mp3",
            RgTrackGainDb = 6.020599913279624,
            RgTrackPeak = -0.5
        };
        Assert.Equal(2.0f, NodeGain(track3), precision: 4);
    }

    [Theory]
    [InlineData(0.5, 1.0, 1.0)]    // Peak 0.5 -> max gain 2.0. Desired gain 1.0 -> 1.0
    [InlineData(0.5, 3.0, 2.0)]    // Peak 0.5 -> max gain 2.0. Desired gain 3.0 -> clamped to 2.0
    [InlineData(1.25, 1.0, 0.8)]   // Peak 1.25 -> max gain 0.8. Desired gain 1.0 -> clamped to 0.8
    [InlineData(2.0, 1.0, 0.5)]    // Peak 2.0 -> max gain 0.5. Desired gain 1.0 -> clamped to 0.5
    public void NodeGain_AntiClipping_StrictMathematicalBoundaries(
        double peak, double desiredMultiplier, double expectedGain)
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = true;

        var track = new Track
        {
            Path = "test.mp3",
            RgTrackGainDb = 20.0 * Math.Log10(desiredMultiplier),
            RgTrackPeak = peak
        };

        float gain = NodeGain(track);
        Assert.Equal((float)expectedGain, gain, precision: 4);
    }

    #endregion

    #region 5. Global Clamping [0.0f, 8.0f] Tests

    [Fact]
    public void NodeGain_ClampsToMaxEight()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 20.0;
        _settings.Playback.ReplayGainPreventClipping = false;

        // TrackGain = +20 dB -> Net +40 dB = multiplier 100.0 -> Clamped to 8.0
        var track = new Track
        {
            Path = "extreme.mp3",
            RgTrackGainDb = 20.0
        };

        float gain = NodeGain(track);
        Assert.Equal(8.0f, gain);
    }

    [Fact]
    public void NodeGain_ExtremeDecibelValues_ClampGracefully()
    {
        _settings.Playback.ReplayGain = ReplayGainMode.Track;
        _settings.Playback.ReplayGainPreampDb = 0.0;
        _settings.Playback.ReplayGainPreventClipping = false;

        // +100 dB (10^5 multiplier) -> clamped to 8.0f
        var trackHigh = new Track { Path = "high.mp3", RgTrackGainDb = 100.0 };
        Assert.Equal(8.0f, NodeGain(trackHigh));

        // -100 dB (10^-5 multiplier) -> near 0.0f, never negative
        var trackLow = new Track { Path = "low.mp3", RgTrackGainDb = -100.0 };
        float low = NodeGain(trackLow);
        Assert.True(low < 0.0001f);
        Assert.True(low >= 0.0f);
    }

    #endregion

    #region 6. ReplayGainMath Pure Static API Tests

    [Theory]
    [InlineData(0.0f, 1.0f)]
    [InlineData(6.0205999f, 2.0f)]
    [InlineData(-6.0205999f, 0.5f)]
    [InlineData(20.0f, 10.0f)]
    [InlineData(-20.0f, 0.1f)]
    public void ReplayGainMath_DecibelsToLinear_ComputesExactMultipliers(float db, float expectedLinear)
    {
        float linear = ReplayGainMath.DecibelsToLinear(db);
        Assert.Equal(expectedLinear, linear, precision: 4);
    }

    [Theory]
    [InlineData(1.0f, 0.0f)]
    [InlineData(2.0f, 6.0205999f)]
    [InlineData(0.5f, -6.0205999f)]
    [InlineData(10.0f, 20.0f)]
    [InlineData(0.1f, -20.0f)]
    public void ReplayGainMath_LinearToDecibels_ComputesExactDecibels(float linear, float expectedDb)
    {
        float db = ReplayGainMath.LinearToDecibels(linear);
        Assert.Equal(expectedDb, db, precision: 4);
    }

    [Fact]
    public void ReplayGainMath_LinearToDecibels_ZeroOrNegative_ReturnsFloor()
    {
        Assert.Equal(-144.0f, ReplayGainMath.LinearToDecibels(0.0f));
        Assert.Equal(-144.0f, ReplayGainMath.LinearToDecibels(-1.0f));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void ComputeReplayGainOnly_InvalidPeaks_SafelyIgnoredByAntiClipping(double? peak)
    {
        var track = new Track
        {
            RgTrackGainDb = 6.0205999, // x2.0
            RgTrackPeak = peak
        };

        // Gain should stay 2.0 without division by zero or NaN corruption
        float gain = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Track, preampDb: 0.0, preventClipping: true)!.Value;
        Assert.Equal(2.0f, gain, precision: 4);
    }

    [Fact]
    public void ComputeReplayGainOnly_PreventClipping_WithIntersampleOvers()
    {
        // Track with peak exceeding full scale (1.25 -> +1.94 dBFS peak)
        var track = new Track
        {
            RgTrackGainDb = 0.0, // multiplier 1.0
            RgTrackPeak = 1.25   // max safe gain = 1.0 / 1.25 = 0.80
        };

        // Gain = 1.0 -> would produce clipping if unconstrained; clamped to 0.80
        float gain = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Track, preampDb: 0.0, preventClipping: true)!.Value;
        Assert.Equal(0.80f, gain, precision: 4);
    }

    [Fact]
    public void ComputeReplayGainOnly_GlobalClampingBoundsEnforced()
    {
        var trackExtremeLoud = new Track
        {
            RgTrackGainDb = 20.0 // +20 dB tag, +20 dB preamp -> multiplier 100
        };

        // Extreme positive gain clamped to MaxGain (8.0)
        float gMax = ReplayGainMath.ComputeReplayGainOnly(trackExtremeLoud, ReplayGainMode.Track, preampDb: 20.0, preventClipping: false)!.Value;
        Assert.Equal(8.0f, gMax);

        // Deep negative gain lands near zero and never negative
        var trackQuiet = new Track { RgTrackGainDb = -100.0 };
        float gMin = ReplayGainMath.ComputeReplayGainOnly(trackQuiet, ReplayGainMode.Track, preampDb: 0.0, preventClipping: false)!.Value;
        Assert.True(gMin < 0.0001f);
        Assert.True(gMin >= 0.0f);
    }

    [Theory]
    [InlineData(0.0f, 1.0f)]
    [InlineData(6.0205999f, 2.0f)]
    [InlineData(-6.0205999f, 0.5f)]
    [InlineData(20.0f, 10.0f)]
    [InlineData(-20.0f, 0.1f)]
    [InlineData(40.0f, 100.0f)]
    [InlineData(-40.0f, 0.01f)]
    [InlineData(60.0f, 1000.0f)]
    [InlineData(-60.0f, 0.001f)]
    public void ReplayGainMath_DecibelsToLinear_KnownValues(float db, float expectedLinear)
    {
        float actual = ReplayGainMath.DecibelsToLinear(db);
        Assert.Equal(expectedLinear, actual, precision: 3);
    }

    [Fact]
    public void ReplayGainMath_RoundTripDecibelsAndLinear_DenseSweep()
    {
        for (float db = -80.0f; db <= 30.0f; db += 0.5f)
        {
            float linear = ReplayGainMath.DecibelsToLinear(db);
            float reconstructedDb = ReplayGainMath.LinearToDecibels(linear);
            Assert.Equal(db, reconstructedDb, precision: 3);
        }
    }

    [Theory]
    [InlineData(0.0f, -144.0f)]
    [InlineData(-0.0001f, -144.0f)]
    [InlineData(-100.0f, -144.0f)]
    [InlineData(float.NegativeInfinity, -144.0f)]
    [InlineData(float.NaN, -144.0f)]
    public void ReplayGainMath_LinearToDecibels_NonPositiveOrSpecial_ReturnsNoiseFloor(float linear, float expectedDb)
    {
        float actual = ReplayGainMath.LinearToDecibels(linear);
        Assert.Equal(expectedDb, actual);
    }

    #endregion

    #region 7. ReplayGainMath.ComputeReplayGainOnly Tests

    [Fact]
    public void ComputeReplayGainOnly_ReturnsNull_WhenOffOrUntagged()
    {
        var track = new Track { Path = "test.mp3", RgTrackGainDb = -4.5 };
        var resOff = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Off, 0, true);
        Assert.Null(resOff);

        var untaggedTrack = new Track { Path = "untagged.mp3", RgTrackGainDb = null };
        var resUntagged = ReplayGainMath.ComputeReplayGainOnly(untaggedTrack, ReplayGainMode.Track, 0, true);
        Assert.Null(resUntagged);
    }

    [Fact]
    public void ComputeReplayGainOnly_ComputesCorrectLinearMultiplierWithPreamp()
    {
        var track = new Track
        {
            Path = "test.flac",
            RgTrackGainDb = -6.0,
            RgAlbumGainDb = -2.0
        };

        // Track Mode with 0dB preamp: 10^(-6/20) ~ 0.501187
        var trackGain = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Track, 0.0, true);
        Assert.NotNull(trackGain);
        Assert.Equal((float)Math.Pow(10.0, -6.0 / 20.0), trackGain!.Value, 3);

        // Album Mode with +2dB preamp: 10^((-2+2)/20) = 1.0
        var albumGain = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Album, 2.0, true);
        Assert.NotNull(albumGain);
        Assert.Equal(1.0f, albumGain!.Value, 3);
    }

    [Fact]
    public void ComputeReplayGainOnly_ClampsToPeakAntiClipping()
    {
        var track = new Track
        {
            Path = "loud.flac",
            RgTrackGainDb = 6.0, // Wants 2.0x boost
            RgTrackPeak = 0.8   // Peak is 0.8 -> max allowed is 1.0 / 0.8 = 1.25x
        };

        var gainWithAntiClip = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Track, 0.0, preventClipping: true);
        Assert.NotNull(gainWithAntiClip);
        Assert.Equal(1.25f, gainWithAntiClip!.Value, 3);

        var gainWithoutAntiClip = ReplayGainMath.ComputeReplayGainOnly(track, ReplayGainMode.Track, 0.0, preventClipping: false);
        Assert.NotNull(gainWithoutAntiClip);
        Assert.Equal((float)Math.Pow(10.0, 6.0 / 20.0), gainWithoutAntiClip!.Value, 3);
    }

    #endregion
}
