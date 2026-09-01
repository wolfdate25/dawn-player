using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Boundary and stress coverage for the pure-logic calculators behind the player controls, driven
/// with the inputs a real library produces and a UI cannot: <see cref="LyricsScrollSynchronizer"/>
/// (10k-line LRC lookup, duplicate and out-of-order timestamps, offsets at the range ends),
/// <see cref="SeekbarScrubbingCalculator"/> (drags past both ends, zero and negative durations),
/// <see cref="QueuePopupController"/> (10k-item sync, missing titles, indices outside the list),
/// and <see cref="AudioFormatBadgeFormatter"/> (unusual sample rates, unknown codecs, nulls).
/// </summary>
public class ControlsCalculatorTests
{
    // =========================================================================
    // 1. LyricsScrollSynchronizer Adversarial & Stress Tests
    // =========================================================================

    [Fact]
    public void LyricsScrollSynchronizer_10kLines_StressAndRandomAccessPerformance()
    {
        const int lineCount = 10000;
        var lines = new List<LrcLineVm>(lineCount);
        for (int i = 0; i < lineCount; i++)
        {
            lines.Add(new LrcLineVm
            {
                Time = TimeSpan.FromMilliseconds(i * 250), // Every 250ms -> 2500 seconds total
                Text = $"Lyric line #{i} [Stress Test]"
            });
        }

        // Exact hits on every 100th element
        for (int i = 0; i < lineCount; i += 100)
        {
            var targetTime = TimeSpan.FromMilliseconds(i * 250);
            int idx = LyricsScrollSynchronizer.FindActiveLineIndex(lines, targetTime, 0);
            Assert.Equal(i, idx);
        }

        // Fractional time between line 500 (125,000ms) and line 501 (125,250ms)
        int midIdx = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromMilliseconds(125120), 0);
        Assert.Equal(500, midIdx);

        // Before first line (if line 0 starts at 0ms, negative time is before first line)
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromMilliseconds(-500), 0));
        // At 100ms (between line 0 at 0ms and line 1 at 250ms), line 0 is active
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromMilliseconds(100), 0));

        // Far beyond last line
        Assert.Equal(lineCount - 1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromHours(10), 0));
    }

    [Fact]
    public void LyricsScrollSynchronizer_DuplicateTimestamps_ResolvesToLastMatchingIndex()
    {
        // LRC files sometimes have multiple lines at the exact same timestamp (e.g. vocal harmonies / duets)
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(1), Text = "Line 1" },
            new() { Time = TimeSpan.FromSeconds(5), Text = "Duet Part A" },
            new() { Time = TimeSpan.FromSeconds(5), Text = "Duet Part B" },
            new() { Time = TimeSpan.FromSeconds(5), Text = "Duet Part C" },
            new() { Time = TimeSpan.FromSeconds(10), Text = "Line 5" }
        };

        // At exactly 5s or slightly after (5.5s), binary search with '<= effectivePos' should pick index 3 (Duet Part C)
        int idxExact = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5), 0);
        Assert.Equal(3, idxExact);

        int idxBetween = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5.5), 0);
        Assert.Equal(3, idxBetween);

        // Just before 5s
        int idxBefore = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(4.999), 0);
        Assert.Equal(0, idxBefore);
    }

    [Fact]
    public void LyricsScrollSynchronizer_OutOfOrderTimestamps_DoesNotCrashAndReturnsValidIndex()
    {
        var unsortedLines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(10), Text = "Line at 10s" },
            new() { Time = TimeSpan.FromSeconds(2), Text = "Line at 2s" },
            new() { Time = TimeSpan.FromSeconds(8), Text = "Line at 8s" },
            new() { Time = TimeSpan.FromSeconds(4), Text = "Line at 4s" }
        };

        // Binary search on unsorted data must not throw IndexOutOfRangeException or loop infinitely
        var ex = Record.Exception(() =>
        {
            for (int s = 0; s <= 15; s++)
            {
                int res = LyricsScrollSynchronizer.FindActiveLineIndex(unsortedLines, TimeSpan.FromSeconds(s), 0);
                Assert.InRange(res, -1, unsortedLines.Count - 1);
            }
        });
        Assert.Null(ex);
    }

    [Fact]
    public void LyricsScrollSynchronizer_ExtremeOffsetsAndSpecialDoubles()
    {
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(5), Text = "Line 1" },
            new() { Time = TimeSpan.FromSeconds(10), Text = "Line 2" }
        };

        // Massive positive offset (+1,000,000ms = +1000s) -> effective position is playback - 1000s -> negative -> -1
        int idx1 = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(10), 1_000_000);
        Assert.Equal(-1, idx1);

        // Massive negative offset (-1,000,000ms = -1000s) -> effective position is playback + 1000s -> beyond line 2 -> 1
        int idx2 = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(1), -1_000_000);
        Assert.Equal(1, idx2);

        // NaN and Infinity offsets fallback to 0ms offset
        int idxNaN = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(7), double.NaN);
        Assert.Equal(0, idxNaN);

        int idxPosInf = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(7), double.PositiveInfinity);
        Assert.Equal(0, idxPosInf);

        int idxNegInf = LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(7), double.NegativeInfinity);
        Assert.Equal(0, idxNegInf);
    }

    [Fact]
    public void LyricsScrollSynchronizer_BoundaryTimeSearches()
    {
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.Zero, Text = "Intro at 0s" },
            new() { Time = TimeSpan.FromSeconds(10), Text = "Main 10s" }
        };

        // Position = TimeSpan.Zero matches line 0
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.Zero, 0));

        // Position = negative TimeSpan matches -1
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(-1), 0));

        // Position = TimeSpan.MaxValue matches last line
        Assert.Equal(1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.MaxValue, 0));

        // Position = TimeSpan.MinValue matches -1
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.MinValue, 0));
    }

    [Fact]
    public void LyricsScrollSynchronizer_UpdateActiveLineState_AdversarialIndices()
    {
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(1), Text = "1" },
            new() { Time = TimeSpan.FromSeconds(2), Text = "2" }
        };

        int currentIndex = -1;

        // Target out of range high: 999
        bool r1 = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 999);
        Assert.True(r1);
        Assert.Equal(999, currentIndex);
        Assert.False(lines[0].IsCurrent);
        Assert.False(lines[1].IsCurrent);

        // Transition from invalid high (999) to valid (0)
        bool r2 = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 0);
        Assert.True(r2);
        Assert.Equal(0, currentIndex);
        Assert.True(lines[0].IsCurrent);

        // Transition from valid (0) to negative out-of-range (-100)
        bool r3 = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, -100);
        Assert.True(r3);
        Assert.Equal(-100, currentIndex);
        Assert.False(lines[0].IsCurrent);

        // Transition from negative out-of-range to same negative out-of-range (no-op)
        bool r4 = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, -100);
        Assert.False(r4);
        Assert.Equal(-100, currentIndex);
    }

    [Fact]
    public void LyricsScrollSynchronizer_StepOffset_AdversarialClampingAndInvertedBounds()
    {
        // Inverted min/max: min=5000, max=-5000 should handle gracefully via Math.Min / Math.Max
        double stepped = LyricsScrollSynchronizer.StepOffset(0, 2000, minOffsetMs: 5000, maxOffsetMs: -5000);
        Assert.Equal(2000, stepped);

        double steppedOverflow = LyricsScrollSynchronizer.StepOffset(0, 10000, minOffsetMs: 5000, maxOffsetMs: -5000);
        Assert.Equal(5000, steppedOverflow);

        // Extreme delta (+1e12, -1e12)
        Assert.Equal(10000, LyricsScrollSynchronizer.StepOffset(0, 1e12));
        Assert.Equal(-10000, LyricsScrollSynchronizer.StepOffset(0, -1e12));

        // NaN & Infinity inputs
        Assert.Equal(0, LyricsScrollSynchronizer.StepOffset(double.NaN, double.NaN));
        Assert.Equal(0, LyricsScrollSynchronizer.StepOffset(double.PositiveInfinity, double.NegativeInfinity));
    }

    [Theory]
    [InlineData(0.00001, "오프셋 0.0s")]
    [InlineData(-0.00001, "오프셋 0.0s")]
    [InlineData(double.NaN, "오프셋 0.0s")]
    [InlineData(double.PositiveInfinity, "오프셋 0.0s")]
    [InlineData(double.NegativeInfinity, "오프셋 0.0s")]
    [InlineData(100, "오프셋 +0.1s")]
    [InlineData(-100, "오프셋 -0.1s")]
    [InlineData(100000, "오프셋 +100s")]
    public void LyricsScrollSynchronizer_FormatOffsetLabel_AdversarialInputs(double offsetMs, string expected)
    {
        Assert.Equal(expected, LyricsScrollSynchronizer.FormatOffsetLabel(offsetMs));
    }

    [Fact]
    public void LyricsScrollSynchronizer_CalculateSeekTarget_NegativeAndExtremeValues()
    {
        // Negative offset larger than timestamp -> clamps to TimeSpan.Zero
        var res1 = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(5), -10000);
        Assert.Equal(TimeSpan.Zero, res1);

        // Infinite offset
        var resInf = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(5), double.PositiveInfinity);
        Assert.Equal(TimeSpan.FromSeconds(5), resInf);

        // Negative infinity
        var resNegInf = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(5), double.NegativeInfinity);
        Assert.Equal(TimeSpan.FromSeconds(5), resNegInf);
    }

    // =========================================================================
    // 2. SeekbarScrubbingCalculator Adversarial & Stress Tests
    // =========================================================================

    [Fact]
    public void SeekbarScrubbingCalculator_ExtremeDragValuesAndNegativeDurations()
    {
        var calc = new SeekbarScrubbingCalculator();

        // 1. Drag with negative duration
        calc.BeginDrag();
        var resNegDur = calc.CompleteDrag(50.0, TimeSpan.FromSeconds(-100));
        Assert.Equal(TimeSpan.FromSeconds(50.0), resNegDur);

        // 2. Drag with double.MaxValue
        calc.BeginDrag();
        var resMaxVal = calc.CompleteDrag(double.MaxValue, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(100), resMaxVal);

        // 3. Drag with double.MinValue
        calc.BeginDrag();
        var resMinVal = calc.CompleteDrag(double.MinValue, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.Zero, resMinVal);

        // 4. Repeated CompleteDrag calls without BeginDrag
        Assert.Null(calc.CompleteDrag(10.0, TimeSpan.FromSeconds(100)));
        Assert.Null(calc.CompleteDrag(20.0, TimeSpan.FromSeconds(100)));
        Assert.False(calc.IsDragging);

        // 5. Repeated BeginDrag calls followed by single CompleteDrag
        calc.BeginDrag();
        calc.BeginDrag();
        calc.BeginDrag();
        Assert.True(calc.IsDragging);
        var target = calc.CompleteDrag(30.0, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(30.0), target);
        Assert.False(calc.IsDragging);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_CalculateSliderProgress_ExtremeInputs()
    {
        // Negative position clamped to 0
        var (updateMax1, max1, val1) = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(-50), TimeSpan.FromSeconds(100), 100, isDragging: false);
        Assert.False(updateMax1);
        Assert.Equal(100, max1);
        Assert.Equal(0, val1);

        // Position exceeding duration clamped to duration
        var (updateMax2, max2, val2) = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(250), TimeSpan.FromSeconds(100), 100, isDragging: false);
        Assert.False(updateMax2);
        Assert.Equal(100, max2);
        Assert.Equal(100, val2);

        // Negative duration treated as zero duration (defaults to max 100, value 0)
        var (updateMax3, max3, val3) = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(-10), 100, isDragging: false);
        Assert.False(updateMax3); // already 100
        Assert.Equal(100, max3);
        Assert.Equal(0, val3);

        // Massive position while dragging
        var (updateMaxDrag, maxDrag, valDrag) = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromHours(50), TimeSpan.FromSeconds(100), 100, isDragging: true);
        Assert.False(updateMaxDrag);
        Assert.Equal(100, maxDrag);
        Assert.Equal(180000, valDrag);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_CalculateRestoreState_ExtremeAndBoundaryScenarios()
    {
        // 1. Exceeded values (seconds > maxSeconds)
        var res1 = SeekbarScrubbingCalculator.CalculateRestoreState(500, 300);
        Assert.Equal(300, res1.ClampedMax);
        Assert.Equal(300, res1.ClampedValue);
        Assert.Equal("5:00", res1.Elapsed);
        Assert.Equal("0:00", res1.Remaining);

        // 2. Both negative
        var res2 = SeekbarScrubbingCalculator.CalculateRestoreState(-50, -100);
        Assert.Equal(1.0, res2.ClampedMax); // Math.Max(1.0, -100) -> 1.0
        Assert.Equal(0.0, res2.ClampedValue);
        Assert.Equal("0:00", res2.Elapsed);
        Assert.Equal("0:00", res2.Remaining);

        // 3. Positive infinity / Negative infinity
        var resInf = SeekbarScrubbingCalculator.CalculateRestoreState(double.PositiveInfinity, double.NegativeInfinity);
        Assert.Equal(1.0, resInf.ClampedMax);
        Assert.Equal(0.0, resInf.ClampedValue);
        Assert.Equal("0:00", resInf.Elapsed);
        Assert.Equal("0:00", resInf.Remaining);
    }

    [Theory]
    [InlineData(-1000, "0:00")]
    [InlineData(0, "0:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(86400, "24:00:00")]
    [InlineData(360000, "100:00:00")]
    public void SeekbarScrubbingCalculator_FormatTime_AdversarialRanges(int seconds, string expected)
    {
        Assert.Equal(expected, SeekbarScrubbingCalculator.FormatTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void SeekbarScrubbingCalculator_FormatRemaining_AdversarialRanges()
    {
        // Position > Duration -> 0:00
        Assert.Equal("0:00", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.FromHours(2), TimeSpan.FromHours(1)));

        // Both negative -> 0:00
        Assert.Equal("0:00", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.FromSeconds(-10), TimeSpan.FromSeconds(-5)));

        // Large remaining time (over 24 hours)
        Assert.Equal("-25:00:00", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.FromHours(5), TimeSpan.FromHours(30)));
    }

    // =========================================================================
    // 3. QueuePopupController Adversarial & Stress Tests
    // =========================================================================

    [Fact]
    public void QueuePopupController_10kItems_MassiveSyncStress()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Massive Playlist");
        var items = Enumerable.Range(1, 10000)
            .Select(i => new PlaylistItem(new Track
            {
                Title = $"Track {i:D5}",
                Artist = $"Artist {i:D5}"
            }))
            .ToList();

        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);

        controller.SyncFromQueue(queue.Entries);

        Assert.Equal(10000, controller.Entries.Count);
        Assert.Equal(1, controller.Entries[0].Index);
        Assert.Equal("Track 00001", controller.Entries[0].Title);
        Assert.Equal(10000, controller.Entries[9999].Index);
        Assert.Equal("Track 10000", controller.Entries[9999].Title);

        Assert.Equal("99+", QueuePopupController.FormatBadgeText(controller.Entries.Count));
        Assert.True(QueuePopupController.ShouldShowBadge(controller.Entries.Count));

        // Clear queue
        QueuePopupController.RequestClear(queue);
        Assert.Equal(0, queue.Count);

        controller.SyncFromQueue(queue.Entries);
        Assert.Empty(controller.Entries);
        Assert.Equal("", QueuePopupController.FormatBadgeText(controller.Entries.Count));
        Assert.False(QueuePopupController.ShouldShowBadge(controller.Entries.Count));
    }

    [Fact]
    public void QueuePopupController_NullOrEmptyTitlesAndSubtitles_SafeMapping()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Null Titles");
        var items = new List<PlaylistItem>
        {
            new(new Track { Title = null!, Artist = null! }),
            new(new Track { Title = "", Artist = "" }),
            new(new Track { Title = "   ", Artist = "   " })
        };

        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);

        controller.SyncFromQueue(queue.Entries);

        Assert.Equal(3, controller.Entries.Count);
        Assert.Equal("", controller.Entries[0].Title);
        Assert.Equal("", controller.Entries[0].Subtitle);
        Assert.Equal("", controller.Entries[1].Title);
        Assert.Equal("", controller.Entries[1].Subtitle);
        Assert.Equal("   ", controller.Entries[2].Title);
        Assert.Equal("   ", controller.Entries[2].Subtitle);
    }

    [Fact]
    public void QueuePopupController_RequestRemoveAt_AdversarialIndices()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Test");
        var items = new List<PlaylistItem>
        {
            new(new Track { Title = "A" }),
            new(new Track { Title = "B" }),
            new(new Track { Title = "C" })
        };
        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);

        // Boundary/invalid 1-based indices
        QueuePopupController.RequestRemoveAt(queue, int.MinValue);
        QueuePopupController.RequestRemoveAt(queue, -100);
        QueuePopupController.RequestRemoveAt(queue, 0);
        QueuePopupController.RequestRemoveAt(queue, 4); // count is 3
        QueuePopupController.RequestRemoveAt(queue, 100);
        QueuePopupController.RequestRemoveAt(queue, int.MaxValue);

        Assert.Equal(3, queue.Count);

        // Valid removals at boundaries: last element (3), then first element (1)
        QueuePopupController.RequestRemoveAt(queue, 3); // removes "C"
        Assert.Equal(2, queue.Count);
        Assert.Equal("B", queue.Entries[1].Title);

        QueuePopupController.RequestRemoveAt(queue, 1); // removes "A"
        Assert.Equal(1, queue.Count);
        Assert.Equal("B", queue.Entries[0].Title);

        QueuePopupController.RequestRemoveAt(queue, 1); // removes "B"
        Assert.Equal(0, queue.Count);

        // Removing from empty queue
        QueuePopupController.RequestRemoveAt(queue, 1);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(int.MinValue, "")]
    [InlineData(-999, "")]
    [InlineData(-1, "")]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(98, "98")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(999999, "99+")]
    [InlineData(int.MaxValue, "99+")]
    public void QueuePopupController_FormatBadgeText_ExtremeBoundaries(int count, string expected)
    {
        Assert.Equal(expected, QueuePopupController.FormatBadgeText(count));
    }

    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public void QueuePopupController_ShouldShowBadge_ExtremeBoundaries(int count, bool expected)
    {
        Assert.Equal(expected, QueuePopupController.ShouldShowBadge(count));
    }

    // =========================================================================
    // 4. AudioFormatBadgeFormatter Adversarial & Stress Tests
    // =========================================================================

    [Theory]
    [InlineData("flac 24-bit 96khz", null, "FLAC")]
    [InlineData("MPEG-2 Layer III", null, "MP3")]
    [InlineData("PCM 32-bit float", null, "WAV")]
    [InlineData("vorbis (ogg)", null, "OGG")]
    [InlineData("Apple Lossless ALAC", null, "ALAC")]
    [InlineData("Monkey's Audio APE v3.99", null, "APE")]
    [InlineData("DSD DSF 5.6MHz", null, "DSD")]
    [InlineData("DSDIFF DFF", null, "DSD")]
    [InlineData("Windows Media Audio 9.2 Lossless (WMA)", null, "WMA")]
    [InlineData("OPUS Codec", null, "OPUS")]
    [InlineData("UnknownCustomCodec", null, "UNKNOWNCUSTOMCODEC")]
    [InlineData(null, "C:\\Music\\audio.tak", "TAK")]
    [InlineData(null, "C:\\Music\\audio.wv", "WV")]
    [InlineData(null, "C:\\Music\\audio.aiff", "AIFF")]
    [InlineData(null, "C:\\Music\\audio.caf", "CAF")]
    [InlineData(null, "C:\\Music\\audio.dsf", "DSF")]
    [InlineData(null, "C:\\Music\\audio.dff", "DFF")]
    [InlineData(null, "C:\\Music\\audio.m4b", "M4B")]
    [InlineData(null, "C:\\Music\\audio.M4A", "AAC")]
    [InlineData(null, "C:\\Music\\no_extension", "")]
    [InlineData(null, "C:\\Music\\.hidden", "HIDDEN")]
    [InlineData(null, "", "")]
    [InlineData(null, "   ", "")]
    [InlineData(null, null, "")]
    public void AudioFormatBadgeFormatter_GetCodec_RareAndAdversarialFormats(string? codec, string? path, string expected)
    {
        Assert.Equal(expected, AudioFormatBadgeFormatter.GetCodec(codec, path));
    }

    [Fact]
    public void AudioFormatBadgeFormatter_FormatTrackBadgeText_HighResAndOddSampleRates()
    {
        // 1. DXD / Extreme Hi-Res: 32-bit 384kHz
        var dxdTrack = new Track
        {
            Codec = "FLAC",
            BitsPerSample = 32,
            SampleRate = 384000
        };
        Assert.Equal("FLAC · 32bit/384kHz", AudioFormatBadgeFormatter.FormatTrackBadgeText(dxdTrack));

        // 2. Fractional kHz: 24-bit 88.2kHz
        var hiRes88 = new Track
        {
            Codec = "FLAC",
            BitsPerSample = 24,
            SampleRate = 88200
        };
        Assert.Equal("FLAC · 24bit/88.2kHz", AudioFormatBadgeFormatter.FormatTrackBadgeText(hiRes88));

        // 3. Fractional kHz: 16-bit 44.1kHz
        var cd44 = new Track
        {
            Codec = "FLAC",
            BitsPerSample = 16,
            SampleRate = 44100
        };
        Assert.Equal("FLAC · 16bit/44.1kHz", AudioFormatBadgeFormatter.FormatTrackBadgeText(cd44));

        // 4. Low sample rate: 16-bit 11.025kHz
        var lowRate = new Track
        {
            Codec = "WAV",
            BitsPerSample = 16,
            SampleRate = 11025
        };
        Assert.Equal("WAV · 16bit/11.0kHz", AudioFormatBadgeFormatter.FormatTrackBadgeText(lowRate));

        // 5. Zero bits per sample with valid sample rate (e.g. lossy AAC stream)
        var aacStream = new Track
        {
            Codec = "AAC",
            BitsPerSample = 0,
            SampleRate = 48000
        };
        Assert.Equal("AAC · 48kHz", AudioFormatBadgeFormatter.FormatTrackBadgeText(aacStream));

        // 6. Zero sample rate with bitrate only
        var mp3BitrateOnly = new Track
        {
            Codec = "MP3",
            BitsPerSample = 0,
            SampleRate = 0,
            BitrateKbps = 320
        };
        Assert.Equal("MP3 · 320kbps", AudioFormatBadgeFormatter.FormatTrackBadgeText(mp3BitrateOnly));
    }

    [Fact]
    public void AudioFormatBadgeFormatter_FormatTrackBadgeText_NullsAndZeroValues()
    {
        // Empty track (all zeros / nulls)
        var emptyTrack = new Track();
        Assert.Equal("", AudioFormatBadgeFormatter.FormatTrackBadgeText(emptyTrack));

        // Track with negative values
        var negTrack = new Track
        {
            BitsPerSample = -16,
            SampleRate = -44100,
            BitrateKbps = -320
        };
        Assert.Equal("", AudioFormatBadgeFormatter.FormatTrackBadgeText(negTrack));

        // Track with path fallback only
        var pathTrack = new Track { Path = "D:\\Songs\\test.flac" };
        Assert.Equal("FLAC", AudioFormatBadgeFormatter.FormatTrackBadgeText(pathTrack));

        // Null track
        Assert.Equal("", AudioFormatBadgeFormatter.FormatTrackBadgeText(null));
    }

    [Fact]
    public void AudioFormatBadgeFormatter_FormatOutputBadgeText_DriverAndDeviceCombinations()
    {
        var session = new SessionInfo("DAC", true, "32-bit 384000Hz", 100, AudioDriverType.Wasapi);
        Assert.Equal("WASAPI 배타 · DAC", AudioFormatBadgeFormatter.FormatOutputBadgeText(session));

        var sharedSession = new SessionInfo("Default", false, "16-bit 44100Hz", 50, AudioDriverType.Wasapi);
        Assert.Equal("WASAPI 공유 · Default", AudioFormatBadgeFormatter.FormatOutputBadgeText(sharedSession));

        var dsSession = new SessionInfo("스피커", false, "32-bit float", 50, AudioDriverType.DirectSound);
        Assert.Equal("DirectSound · 스피커", AudioFormatBadgeFormatter.FormatOutputBadgeText(dsSession));

        var waveSession = new SessionInfo("헤드폰", false, "16-bit", 50, AudioDriverType.WaveOut);
        Assert.Equal("WaveOut · 헤드폰", AudioFormatBadgeFormatter.FormatOutputBadgeText(waveSession));

        var nullSession = (SessionInfo?)null;
        Assert.Equal("", AudioFormatBadgeFormatter.FormatOutputBadgeText(nullSession));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t\n", false)]
    [InlineData("FLAC", true)]
    [InlineData("FLAC · 24bit/96kHz · WASAPI 배타", true)]
    public void AudioFormatBadgeFormatter_IsBadgeVisible_AdversarialStrings(string? text, bool expected)
    {
        Assert.Equal(expected, AudioFormatBadgeFormatter.IsBadgeVisible(text));
    }
}
