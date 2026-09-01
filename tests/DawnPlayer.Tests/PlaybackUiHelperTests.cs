using System.Collections.ObjectModel;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

[Collection("SettingsStoreCollection")]
public class PlaybackUiHelperTests
{
    // =========================================================================
    // 1. QueuePopupController Tests
    // =========================================================================

    [Fact]
    public void QueuePopupController_SyncFromQueue_PopulatesEntriesWithOneBasedIndex()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Test Playlist");
        var items = new List<PlaylistItem>
        {
            new(new Track { Title = "Song 1", Artist = "Artist 1" }),
            new(new Track { Title = "Song 2", Artist = "Artist 2" }),
            new(new Track { Title = "Song 3", Artist = "Artist 3" })
        };

        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);

        controller.SyncFromQueue(queue.Entries);

        Assert.Equal(3, controller.Entries.Count);
        Assert.Equal(1, controller.Entries[0].Index);
        Assert.Equal("Song 1", controller.Entries[0].Title);
        Assert.Equal("Artist 1", controller.Entries[0].Subtitle);

        Assert.Equal(2, controller.Entries[1].Index);
        Assert.Equal("Song 2", controller.Entries[1].Title);
        Assert.Equal("Artist 2", controller.Entries[1].Subtitle);

        Assert.Equal(3, controller.Entries[2].Index);
        Assert.Equal("Song 3", controller.Entries[2].Title);
        Assert.Equal("Artist 3", controller.Entries[2].Subtitle);
    }

    [Fact]
    public void QueuePopupController_SyncFromQueue_NullOrEmptyClearsEntries()
    {
        var controller = new QueuePopupController();
        controller.Entries.Add(new QueueUiEntry { Index = 1, Title = "Temp", Subtitle = "Temp" });
        Assert.Single(controller.Entries);

        controller.SyncFromQueue(null);
        Assert.Empty(controller.Entries);

        controller.Entries.Add(new QueueUiEntry { Index = 1, Title = "Temp", Subtitle = "Temp" });
        controller.SyncFromQueue(new List<QueueEntry>());
        Assert.Empty(controller.Entries);
    }

    [Theory]
    [InlineData(-10, "")]
    [InlineData(-1, "")]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(5, "5")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(1000, "99+")]
    public void QueuePopupController_FormatBadgeText_Boundaries(int count, string expected)
    {
        Assert.Equal(expected, QueuePopupController.FormatBadgeText(count));
    }

    [Theory]
    [InlineData(-5, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    public void QueuePopupController_ShouldShowBadge_Boundaries(int count, bool expected)
    {
        Assert.Equal(expected, QueuePopupController.ShouldShowBadge(count));
    }

    [Fact]
    public void QueuePopupController_RequestClear_ClearsPlaybackQueue()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Test Playlist");
        var items = new List<PlaylistItem>
        {
            new(new Track { Title = "Song 1" }),
            new(new Track { Title = "Song 2" })
        };
        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);
        Assert.Equal(2, queue.Count);

        QueuePopupController.RequestClear(queue);
        Assert.Equal(0, queue.Count);

        // Safe with null
        QueuePopupController.RequestClear(null);
    }

    [Fact]
    public void QueuePopupController_RequestRemoveAt_RemovesCorrectOneBasedItem()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Test Playlist");
        var items = new List<PlaylistItem>
        {
            new(new Track { Title = "Song 1" }),
            new(new Track { Title = "Song 2" }),
            new(new Track { Title = "Song 3" })
        };
        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);
        Assert.Equal(3, queue.Count);

        // Remove 2nd item ("Song 2") via 1-based index 2
        QueuePopupController.RequestRemoveAt(queue, 2);

        Assert.Equal(2, queue.Count);
        Assert.Equal("Song 1", queue.Entries[0].Title);
        Assert.Equal("Song 3", queue.Entries[1].Title);

        // Invalid indices (0, -1, 10) should safely no-op
        QueuePopupController.RequestRemoveAt(queue, 0);
        QueuePopupController.RequestRemoveAt(queue, -1);
        QueuePopupController.RequestRemoveAt(queue, 10);
        QueuePopupController.RequestRemoveAt(null, 1);
        Assert.Equal(2, queue.Count);
    }

    // =========================================================================
    // 2. SeekbarScrubbingCalculator Tests
    // =========================================================================

    [Fact]
    public void SeekbarScrubbingCalculator_DragLifecycle()
    {
        var calc = new SeekbarScrubbingCalculator();
        Assert.False(calc.IsDragging);

        // CompleteDrag without BeginDrag returns null
        var resultNoDrag = calc.CompleteDrag(50.0, TimeSpan.FromSeconds(100));
        Assert.Null(resultNoDrag);
        Assert.False(calc.IsDragging);

        // BeginDrag
        calc.BeginDrag();
        Assert.True(calc.IsDragging);

        // CompleteDrag
        var seekTarget = calc.CompleteDrag(35.5, TimeSpan.FromSeconds(100));
        Assert.NotNull(seekTarget);
        Assert.Equal(TimeSpan.FromSeconds(35.5), seekTarget.Value);
        Assert.False(calc.IsDragging);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_CompleteDrag_ClampingAndSpecialValues()
    {
        var calc = new SeekbarScrubbingCalculator();

        // Exceeding duration
        calc.BeginDrag();
        var target1 = calc.CompleteDrag(150.0, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(100), target1);

        // Negative slider value
        calc.BeginDrag();
        var target2 = calc.CompleteDrag(-10.0, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.Zero, target2);

        // NaN / Infinity handling
        calc.BeginDrag();
        var targetNaN = calc.CompleteDrag(double.NaN, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.Zero, targetNaN);

        calc.BeginDrag();
        var targetInf = calc.CompleteDrag(double.PositiveInfinity, TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.Zero, targetInf);

        // Zero duration
        calc.BeginDrag();
        var targetZeroDur = calc.CompleteDrag(25.0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(25.0), targetZeroDur);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_CalculateSliderProgress_NormalPlaybackAndDragging()
    {
        // 1. While dragging: should not update max or value
        var dragRes = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(100), 100, isDragging: true);
        Assert.False(dragRes.UpdateMax);
        Assert.Equal(100, dragRes.NewMax);
        Assert.Equal(20, dragRes.NewValue);

        // 2. Normal playback (no duration change)
        var normRes = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(100), 100, isDragging: false);
        Assert.False(normRes.UpdateMax);
        Assert.Equal(100, normRes.NewMax);
        Assert.Equal(45, normRes.NewValue);

        // 3. Duration change detected (> 0.5s difference)
        var durChangeRes = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(240), 100, isDragging: false);
        Assert.True(durChangeRes.UpdateMax);
        Assert.Equal(240, durChangeRes.NewMax);
        Assert.Equal(10, durChangeRes.NewValue);

        // 4. Zero/Stopped duration
        var zeroDurRes = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.Zero, TimeSpan.Zero, 240, isDragging: false);
        Assert.True(zeroDurRes.UpdateMax);
        Assert.Equal(100, zeroDurRes.NewMax);
        Assert.Equal(0, zeroDurRes.NewValue);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_CalculateRestoreState_ValidAndEdgeCases()
    {
        // Valid case: 45s out of 180s
        var res1 = SeekbarScrubbingCalculator.CalculateRestoreState(45, 180);
        Assert.Equal(180, res1.ClampedMax);
        Assert.Equal(45, res1.ClampedValue);
        Assert.Equal("0:45", res1.Elapsed);
        Assert.Equal("-2:15", res1.Remaining);

        // Exceeded case: 200s out of 100s
        var res2 = SeekbarScrubbingCalculator.CalculateRestoreState(200, 100);
        Assert.Equal(100, res2.ClampedMax);
        Assert.Equal(100, res2.ClampedValue);
        Assert.Equal("1:40", res2.Elapsed);
        Assert.Equal("0:00", res2.Remaining);

        // Negative values and zero max
        var res3 = SeekbarScrubbingCalculator.CalculateRestoreState(-10, 0);
        Assert.Equal(1.0, res3.ClampedMax);
        Assert.Equal(0.0, res3.ClampedValue);
        Assert.Equal("0:00", res3.Elapsed);
        Assert.Equal("0:00", res3.Remaining);

        // NaN / Infinity safety
        var resNaN = SeekbarScrubbingCalculator.CalculateRestoreState(double.NaN, double.NaN);
        Assert.Equal(1.0, resNaN.ClampedMax);
        Assert.Equal(0.0, resNaN.ClampedValue);
        Assert.Equal("0:00", resNaN.Elapsed);
        Assert.Equal("0:00", resNaN.Remaining);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(-5, "0:00")]
    [InlineData(45, "0:45")]
    [InlineData(65, "1:05")]
    [InlineData(599, "9:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(90125, "25:02:05")]
    public void SeekbarScrubbingCalculator_FormatTime_FormatsCorrectly(int totalSeconds, string expected)
    {
        Assert.Equal(expected, SeekbarScrubbingCalculator.FormatTime(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void SeekbarScrubbingCalculator_FormatRemaining_FormatsCorrectly()
    {
        Assert.Equal("-0:20", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
        Assert.Equal("0:00", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
        Assert.Equal("0:00", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(30)));
        Assert.Equal("0:00", SeekbarScrubbingCalculator.FormatRemaining(TimeSpan.Zero, TimeSpan.Zero));
    }

    // =========================================================================
    // 3. AudioFormatBadgeFormatter Tests
    // =========================================================================

    [Theory]
    [InlineData("FLAC", null, "FLAC")]
    [InlineData("flac", null, "FLAC")]
    [InlineData("MPEG-1 Layer 3", null, "MP3")]
    [InlineData("mp3", null, "MP3")]
    [InlineData("AAC", null, "AAC")]
    [InlineData("ALAC", null, "ALAC")]
    [InlineData("PCM / WAV", null, "WAV")]
    [InlineData("Vorbis", null, "OGG")]
    [InlineData("Opus", null, "OPUS")]
    [InlineData("WMA", null, "WMA")]
    [InlineData("Monkey's Audio", null, "APE")]
    [InlineData("DSDIFF", null, "DSD")]
    [InlineData(null, "music/song.flac", "FLAC")]
    [InlineData(null, "music/song.mp3", "MP3")]
    [InlineData(null, "music/song.m4a", "AAC")]
    [InlineData(null, "music/song.wav", "WAV")]
    [InlineData(null, "music/song.ogg", "OGG")]
    [InlineData(null, null, "")]
    [InlineData("", "", "")]
    public void AudioFormatBadgeFormatter_GetCodec_ResolvesCodec(string? codec, string? path, string expected)
    {
        Assert.Equal(expected, AudioFormatBadgeFormatter.GetCodec(codec, path));
    }

    [Theory]
    [InlineData(AudioDriverType.Wasapi, true, "WASAPI 배타")]
    [InlineData(AudioDriverType.Wasapi, false, "WASAPI 공유")]
    [InlineData(AudioDriverType.DirectSound, true, "DirectSound")]
    [InlineData(AudioDriverType.DirectSound, false, "DirectSound")]
    [InlineData(AudioDriverType.WaveOut, true, "WaveOut")]
    [InlineData(AudioDriverType.WaveOut, false, "WaveOut")]
    public void AudioFormatBadgeFormatter_GetDriverLabel_ReturnsCorrectLabel(AudioDriverType driver, bool exclusive, string expected)
    {
        Assert.Equal(expected, AudioFormatBadgeFormatter.GetDriverLabel(driver, exclusive));
    }

    [Fact]
    public void AudioFormatBadgeFormatter_FormatTrackBadgeText_FormatsTrackDetails()
    {
        var track = new Track
        {
            Codec = "FLAC",
            BitsPerSample = 24,
            SampleRate = 96000
        };
        Assert.Equal("FLAC · 24bit/96kHz", AudioFormatBadgeFormatter.FormatTrackBadgeText(track));

        var mp3Track = new Track
        {
            Codec = "MP3",
            BitrateKbps = 320
        };
        Assert.Equal("MP3 · 320kbps", AudioFormatBadgeFormatter.FormatTrackBadgeText(mp3Track));

        // Null handling
        Assert.Equal("", AudioFormatBadgeFormatter.FormatTrackBadgeText(null));
    }

    [Fact]
    public void AudioFormatBadgeFormatter_FormatOutputBadgeText_FormatsDriverAndDevice()
    {
        var wasapiEx = new SessionInfo("스피커 (Realtek Audio)", true, "24-bit 96000Hz", 50, AudioDriverType.Wasapi);
        Assert.Equal("WASAPI 배타 · 스피커 (Realtek Audio)", AudioFormatBadgeFormatter.FormatOutputBadgeText(wasapiEx));

        var wasapiShared = new SessionInfo("스피커", false, "16-bit 44100Hz", 50, AudioDriverType.Wasapi);
        Assert.Equal("WASAPI 공유 · 스피커", AudioFormatBadgeFormatter.FormatOutputBadgeText(wasapiShared));

        var directSound = new SessionInfo("스피커", false, "32-bit float", 50, AudioDriverType.DirectSound);
        Assert.Equal("DirectSound · 스피커", AudioFormatBadgeFormatter.FormatOutputBadgeText(directSound));

        var waveOut = new SessionInfo("스피커", false, "16-bit", 50, AudioDriverType.WaveOut);
        Assert.Equal("WaveOut · 스피커", AudioFormatBadgeFormatter.FormatOutputBadgeText(waveOut));

        var noDeviceName = new SessionInfo("", false, "16-bit", 50, AudioDriverType.DirectSound);
        Assert.Equal("DirectSound", AudioFormatBadgeFormatter.FormatOutputBadgeText(noDeviceName));

        var nullDeviceName = new SessionInfo(null!, false, "16-bit", 50, AudioDriverType.WaveOut);
        Assert.Equal("WaveOut", AudioFormatBadgeFormatter.FormatOutputBadgeText(nullDeviceName));

        // Null handling
        Assert.Equal("", AudioFormatBadgeFormatter.FormatOutputBadgeText(null));
    }

    [Theory]
    [InlineData("FLAC", true)]
    [InlineData("FLAC · WASAPI", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void AudioFormatBadgeFormatter_IsBadgeVisible_ChecksVisibility(string? text, bool expected)
    {
        Assert.Equal(expected, AudioFormatBadgeFormatter.IsBadgeVisible(text));
    }

    // =========================================================================
    // 4. LyricsScrollSynchronizer Tests
    // =========================================================================

    [Fact]
    public void LyricsScrollSynchronizer_FindActiveLineIndex_ProgressionAndOffsets()
    {
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(2), Text = "Line 1" },
            new() { Time = TimeSpan.FromSeconds(5), Text = "Line 2" },
            new() { Time = TimeSpan.FromSeconds(10), Text = "Line 3" },
            new() { Time = TimeSpan.FromSeconds(15), Text = "Line 4" }
        };

        // 1. Before first line (0s) -> -1
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(0), 0));
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(1.9), 0));

        // 2. Exact match on Line 1 (2s)
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(2), 0));
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(3.5), 0));

        // 3. Line 2 (5s)
        Assert.Equal(1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5), 0));
        Assert.Equal(1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(8), 0));

        // 4. Line 3 (10s)
        Assert.Equal(2, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(10), 0));

        // 5. Line 4 (15s) and beyond
        Assert.Equal(3, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(15), 0));
        Assert.Equal(3, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(30), 0));

        // 6. Positive offset (+1000ms): playback at 2s effective = 1s < 2s -> -1. Playback at 3s -> 0
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(2), 1000));
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(3), 1000));

        // 7. Negative offset (-1000ms): playback at 1s effective = 2s >= 2s -> 0
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(1), -1000));
    }

    [Fact]
    public void LyricsScrollSynchronizer_FindActiveLineIndex_EmptyAndEdgeCases()
    {
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(null, TimeSpan.FromSeconds(10), 0));
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(new List<LrcLineVm>(), TimeSpan.FromSeconds(10), 0));

        var lines = new List<LrcLineVm> { new() { Time = TimeSpan.FromSeconds(5), Text = "Line 1" } };
        // Position 2s (< 5s) with NaN offset -> defaults to 0 offset -> -1
        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(2), double.NaN));
        // Position 5s (>= 5s) with NaN offset -> defaults to 0 offset -> 0
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5), double.NaN));
    }

    [Fact]
    public void LyricsScrollSynchronizer_StepOffset_SteppingAndClamping()
    {
        Assert.Equal(500, LyricsScrollSynchronizer.StepOffset(0, 500));
        Assert.Equal(0, LyricsScrollSynchronizer.StepOffset(500, -500));

        // Clamping at default [-10000, 10000]
        Assert.Equal(10000, LyricsScrollSynchronizer.StepOffset(9800, 500));
        Assert.Equal(-10000, LyricsScrollSynchronizer.StepOffset(-9800, -500));

        // Custom clamp range [-5000, 5000]
        Assert.Equal(5000, LyricsScrollSynchronizer.StepOffset(4800, 500, -5000, 5000));
        Assert.Equal(-5000, LyricsScrollSynchronizer.StepOffset(-4800, -500, -5000, 5000));

        // NaN safety
        Assert.Equal(500, LyricsScrollSynchronizer.StepOffset(double.NaN, 500));
    }

    [Theory]
    [InlineData(0, "오프셋 0.0s")]
    [InlineData(500, "오프셋 +0.5s")]
    [InlineData(-500, "오프셋 -0.5s")]
    [InlineData(1500, "오프셋 +1.5s")]
    [InlineData(-2000, "오프셋 -2s")]
    public void LyricsScrollSynchronizer_FormatOffsetLabel_FormatsCorrectly(double offsetMs, string expected)
    {
        Assert.Equal(expected, LyricsScrollSynchronizer.FormatOffsetLabel(offsetMs));
    }

    [Fact]
    public void LyricsScrollSynchronizer_CalculateSeekTarget_ComputesTarget()
    {
        // Normal offset 0
        var target1 = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(10), 0);
        Assert.Equal(TimeSpan.FromSeconds(10), target1);

        // Positive offset +500ms
        var target2 = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(10), 500);
        Assert.Equal(TimeSpan.FromSeconds(10.5), target2);

        // Negative offset -500ms
        var target3 = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(10), -500);
        Assert.Equal(TimeSpan.FromSeconds(9.5), target3);

        // Negative offset exceeding timestamp -> clamped to TimeSpan.Zero
        var target4 = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(1), -2000);
        Assert.Equal(TimeSpan.Zero, target4);

        // NaN handling
        var targetNaN = LyricsScrollSynchronizer.CalculateSeekTarget(TimeSpan.FromSeconds(5), double.NaN);
        Assert.Equal(TimeSpan.FromSeconds(5), targetNaN);
    }

    [Fact]
    public void LyricsScrollSynchronizer_UpdateActiveLineState_StateTransitions()
    {
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(1), Text = "Line 1" },
            new() { Time = TimeSpan.FromSeconds(5), Text = "Line 2" },
            new() { Time = TimeSpan.FromSeconds(10), Text = "Line 3" }
        };

        int currentIndex = -1;

        // Transition: -1 -> 0
        bool changed1 = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 0);
        Assert.True(changed1);
        Assert.Equal(0, currentIndex);
        Assert.True(lines[0].IsCurrent);
        Assert.True(lines[0].IsActive);
        Assert.False(lines[1].IsCurrent);

        // Transition: 0 -> 0 (no change)
        bool changedSame = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 0);
        Assert.False(changedSame);
        Assert.Equal(0, currentIndex);
        Assert.True(lines[0].IsCurrent);

        // Transition: 0 -> 1
        bool changed2 = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 1);
        Assert.True(changed2);
        Assert.Equal(1, currentIndex);
        Assert.False(lines[0].IsCurrent);
        Assert.True(lines[1].IsCurrent);

        // Transition: 1 -> -1 (e.g. rewind before first line)
        bool changedToMinusOne = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, -1);
        Assert.True(changedToMinusOne);
        Assert.Equal(-1, currentIndex);
        Assert.False(lines[1].IsCurrent);

        // Null collection safety
        bool nullRes = LyricsScrollSynchronizer.UpdateActiveLineState(null, ref currentIndex, 0);
        Assert.False(nullRes);
    }

    [Fact]
    public void LrcLineVm_PropertyChanged_FiresProperly()
    {
        var vm = new LrcLineVm { Time = TimeSpan.FromSeconds(5), Text = "Lyric Text" };
        var changedProps = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.IsCurrent = true;
        Assert.Contains(nameof(LrcLineVm.IsCurrent), changedProps);
        Assert.Contains(nameof(LrcLineVm.IsActive), changedProps);

        changedProps.Clear();
        vm.IsCurrent = true; // no change
        Assert.Empty(changedProps);

        vm.IsCurrent = false;
        Assert.Contains(nameof(LrcLineVm.IsCurrent), changedProps);
    }

    // =========================================================================
    // 5. Adversarial & Stress Tests
    // =========================================================================

    [Fact]
    public void QueuePopupController_LargeQueue_MaintainsCorrectIndexingAndBadge()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Large Playlist");
        var items = Enumerable.Range(1, 500)
            .Select(i => new PlaylistItem(new Track { Title = $"Song {i}", Artist = $"Artist {i}" }))
            .ToList();

        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);

        controller.SyncFromQueue(queue.Entries);
        Assert.Equal(500, controller.Entries.Count);
        Assert.Equal("99+", QueuePopupController.FormatBadgeText(controller.Entries.Count));
        Assert.Equal(1, controller.Entries[0].Index);
        Assert.Equal("Song 1", controller.Entries[0].Title);
        Assert.Equal(500, controller.Entries[499].Index);
        Assert.Equal("Song 500", controller.Entries[499].Title);

        // Remove from the middle repeatedly
        for (int i = 0; i < 100; i++)
        {
            QueuePopupController.RequestRemoveAt(queue, 50);
        }
        Assert.Equal(400, queue.Count);

        controller.SyncFromQueue(queue.Entries);
        Assert.Equal(400, controller.Entries.Count);
        Assert.Equal(1, controller.Entries[0].Index);
        Assert.Equal(400, controller.Entries[399].Index);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_RapidScrubbingSimulation_MaintainsConsistentState()
    {
        var calc = new SeekbarScrubbingCalculator();
        var duration = TimeSpan.FromMinutes(4); // 240 seconds

        for (int i = 0; i < 100; i++)
        {
            calc.BeginDrag();
            Assert.True(calc.IsDragging);

            // Calculate progress while dragging should return currentSliderMax
            var progress = SeekbarScrubbingCalculator.CalculateSliderProgress(
                TimeSpan.FromSeconds(i), duration, 240, calc.IsDragging);
            Assert.False(progress.UpdateMax);
            Assert.Equal(i, progress.NewValue);

            // Complete drag at random value
            double seekSec = (i * 3.7) % 300; // may exceed duration
            var target = calc.CompleteDrag(seekSec, duration);
            Assert.NotNull(target);
            Assert.InRange(target.Value.TotalSeconds, 0, 240);
            Assert.False(calc.IsDragging);
        }
    }

    [Fact]
    public void AudioFormatBadgeFormatter_VariousAudioSpecs_FormatsCleanBadges()
    {
        // 1. 44.1kHz 16-bit FLAC
        var track44 = new Track
        {
            Codec = "FLAC",
            BitsPerSample = 16,
            SampleRate = 44100
        };
        var res44 = AudioFormatBadgeFormatter.FormatTrackBadgeText(track44);
        Assert.Equal("FLAC · 16bit/44.1kHz", res44);

        // 2. 192kHz 24-bit WAV
        var track192 = new Track
        {
            Codec = "WAV",
            BitsPerSample = 24,
            SampleRate = 192000
        };
        var res192 = AudioFormatBadgeFormatter.FormatTrackBadgeText(track192);
        Assert.Equal("WAV · 24bit/192kHz", res192);

        // 3. Track with only SampleRate
        var trackOnlySr = new Track
        {
            Codec = "AAC",
            SampleRate = 48000
        };
        var resSr = AudioFormatBadgeFormatter.FormatTrackBadgeText(trackOnlySr);
        Assert.Equal("AAC · 48kHz", resSr);

        // 4. Track with only Bitrate
        var trackOnlyBr = new Track
        {
            Codec = "MP3",
            BitrateKbps = 256
        };
        var resBr = AudioFormatBadgeFormatter.FormatTrackBadgeText(trackOnlyBr);
        Assert.Equal("MP3 · 256kbps", resBr);
    }

    [Fact]
    public void LyricsScrollSynchronizer_LargeLyricsDocument_BinarySearchIsFastAndAccurate()
    {
        // 10,000 lyrics lines spaced at 100ms intervals (total 1000 seconds)
        var lines = Enumerable.Range(0, 10000)
            .Select(i => new LrcLineVm
            {
                Time = TimeSpan.FromMilliseconds(i * 100),
                Text = $"Line {i}"
            })
            .ToList();

        // Check various points
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.Zero, 0));
        Assert.Equal(50, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromMilliseconds(5000), 0));
        Assert.Equal(50, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromMilliseconds(5050), 0));
        Assert.Equal(51, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromMilliseconds(5100), 0));
        Assert.Equal(9999, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(2000), 0));
    }

    // =========================================================================
    // 6. PlaybackUiHelper Consolidated Logic Tests
    // =========================================================================

    [Fact]
    public void PlaybackUiHelper_UpdatePlayingState_CorrectlyUpdatesFlagsAndIsIdempotent()
    {
        var t1 = new Track { Path = @"C:\Music\Song1.flac", Title = "Song 1" };
        var t2 = new Track { Path = @"C:\Music\Song2.flac", Title = "Song 2" };
        var t3 = new Track { Path = @"C:\Music\Song3.flac", Title = "Song 3" };

        var item1 = new PlaylistItem(t1);
        var item2 = new PlaylistItem(t2);
        var item3 = new PlaylistItem(t3);

        var items = new List<PlaylistItem> { item1, item2, item3 };

        // 1. Play track 2
        PlaybackUiHelper.UpdatePlayingState(items, item2);
        Assert.False(item1.IsPlaying);
        Assert.True(item2.IsPlaying);
        Assert.False(item3.IsPlaying);

        // 2. Track changes property change count
        int item2PropChanges = 0;
        item2.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistItem.IsPlaying)) item2PropChanges++;
        };

        // Calling again with the same playing item should NOT fire duplicate PropertyChanged
        PlaybackUiHelper.UpdatePlayingState(items, item2);
        Assert.Equal(0, item2PropChanges);

        // 3. Switch to track 3
        PlaybackUiHelper.UpdatePlayingState(items, item3);
        Assert.False(item1.IsPlaying);
        Assert.False(item2.IsPlaying);
        Assert.True(item3.IsPlaying);
        Assert.Equal(1, item2PropChanges); // item2 transitioned True -> False

        // 4. Stopped (null current item)
        PlaybackUiHelper.UpdatePlayingState(items, (PlaylistItem?)null);
        Assert.False(item1.IsPlaying);
        Assert.False(item2.IsPlaying);
        Assert.False(item3.IsPlaying);
    }

    [Fact]
    public void PlaybackUiHelper_UpdatePlayingState_CaseInsensitivePathMatching()
    {
        var item = new PlaylistItem(new Track { Path = @"C:\music\song.mp3" });
        var list = new[] { item };

        PlaybackUiHelper.UpdatePlayingState(list, @"C:\MUSIC\SONG.MP3");
        Assert.True(item.IsPlaying);

        PlaybackUiHelper.UpdatePlayingState(list, @"C:\other\song.mp3");
        Assert.False(item.IsPlaying);
    }

    [Fact]
    public void PlaybackUiHelper_UpdatePlayingState_NullOrEmptySafelyHandled()
    {
        // Should safely no-op without throwing
        PlaybackUiHelper.UpdatePlayingState((List<PlaylistItem>?)null, "some/path.mp3");
        PlaybackUiHelper.UpdatePlayingState(new List<PlaylistItem>(), "some/path.mp3");
        PlaybackUiHelper.UpdatePlayingState(new List<PlaylistItem> { new(new Track()) }, "");
    }

    [Theory]
    [InlineData(0, 0, "0 items")]
    [InlineData(-5, 0, "0 items")]
    [InlineData(1, 45, "45s, 1 items")]
    [InlineData(3, 150, "2 min 30s, 3 items")]
    [InlineData(50, 7354, "2 h 2 min 34s, 50 items")]
    public void PlaybackUiHelper_FormatEolePlaylistStats_CountAndDuration(int count, int totalSec, string expected)
    {
        var result = PlaybackUiHelper.FormatEolePlaylistStats(count, TimeSpan.FromSeconds(totalSec));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PlaybackUiHelper_FormatEolePlaylistStats_PlaylistOverload()
    {
        // Null playlist
        Assert.Equal("0 items", PlaybackUiHelper.FormatEolePlaylistStats((Playlist?)null));

        // Empty playlist
        var pl = new Playlist("Test");
        Assert.Equal("0 items", PlaybackUiHelper.FormatEolePlaylistStats(pl));

        // Populated playlist
        pl.Items.Add(new PlaylistItem(new Track { DurationMs = 60000 })); // 1 min
        pl.Items.Add(new PlaylistItem(new Track { DurationMs = 90000 })); // 1.5 min
        Assert.Equal("2 min 30s, 2 items", PlaybackUiHelper.FormatEolePlaylistStats(pl));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(60, "1 min 0s")]
    [InlineData(125, "2 min 5s")]
    [InlineData(3600, "1 h 0 min 0s")]
    [InlineData(3665, "1 h 1 min 5s")]
    public void PlaybackUiHelper_FormatEoleDuration_FormatsCorrectly(int totalSec, string expected)
    {
        Assert.Equal(expected, PlaybackUiHelper.FormatEoleDuration(TimeSpan.FromSeconds(totalSec)));
    }

    [Fact]
    public async Task PlaybackUiHelper_PlayTracksAsync_And_ReplaceAndPlayTracksAsync_WorkCleanly()
    {
        using var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl = pm.CreatePlaylist("TestPl");
        var settings = new AppSettings { Output = new OutputSettings { DriverType = AudioDriverType.DirectSound } };
        using var controller = new PlaybackController(settings, pm);

        var tracks = new[]
        {
            new Track { Title = "T1", Path = @"C:\Music\t1.wav" },
            new Track { Title = "T2", Path = @"C:\Music\t2.wav" }
        };

        // 1. PlayTracksAsync without play (add only)
        var added = await PlaybackUiHelper.PlayTracksAsync(pm, controller, pl, tracks, play: false);
        Assert.Equal(2, added.Count);
        Assert.Equal(2, pl.Items.Count);

        // 2. ReplaceAndPlayTracksAsync
        var replacedTracks = new[]
        {
            new Track { Title = "Replaced 1", Path = @"C:\Music\r1.wav" }
        };
        var replacedItems = await PlaybackUiHelper.ReplaceAndPlayTracksAsync(pm, controller, pl, replacedTracks);
        Assert.Single(replacedItems);
        Assert.Single(pl.Items);
        Assert.Equal("Replaced 1", pl.Items[0].Track.Title);

        // 3. Null checks
        var nullRes1 = await PlaybackUiHelper.PlayTracksAsync(null, null, null, null);
        Assert.Empty(nullRes1);

        var nullRes2 = await PlaybackUiHelper.ReplaceAndPlayTracksAsync(null, null, null, null);
        Assert.Empty(nullRes2);
    }

    [Fact]
    public void PlaybackUiHelper_EnqueueAndRemoveItems_ManagesQueueAndPlaylist()
    {
        using var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl = pm.CreatePlaylist("QueueTest");
        var settings = new AppSettings();
        using var controller = new PlaybackController(settings, pm);

        var tracks = new[]
        {
            new Track { Title = "Track 1", Path = @"C:\Music\1.mp3" },
            new Track { Title = "Track 2", Path = @"C:\Music\2.mp3" },
            new Track { Title = "Track 3", Path = @"C:\Music\3.mp3" }
        };

        var items = pm.AddTracks(pl, tracks);

        // Enqueue single item
        PlaybackUiHelper.EnqueueItems(controller, pl, new[] { items[0] }, playNext: false);
        Assert.Equal(1, controller.Queue.Count);
        Assert.Equal(1, items[0].QueueIndex);

        // Enqueue Next
        PlaybackUiHelper.EnqueueItems(controller, pl, new[] { items[1] }, playNext: true);
        Assert.Equal(2, controller.Queue.Count);
        Assert.Equal(1, items[1].QueueIndex);
        Assert.Equal(2, items[0].QueueIndex);

        // EnqueueTracks
        PlaybackUiHelper.EnqueueTracks(pm, controller, pl, new[] { new Track { Title = "Track 4", Path = @"C:\Music\4.mp3" } });
        Assert.Equal(3, controller.Queue.Count);

        // Remove item
        PlaybackUiHelper.RemoveItems(pm, pl, new[] { items[0] });
        Assert.Equal(3, pl.Items.Count); // 1, 2, 4 -> 0 was removed so 3 items remain
    }

    [Fact]
    public async Task PlaybackUiHelper_TriggerPlayOrResumeAsync_ResumesOrStartsPlayback()
    {
        using var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl = pm.CreatePlaylist("ResumeTest");
        var settings = new AppSettings { Output = new OutputSettings { DriverType = AudioDriverType.DirectSound } };
        using var controller = new PlaybackController(settings, pm);

        // Null safety
        await PlaybackUiHelper.TriggerPlayOrResumeAsync(null, null, null);
    }

    [Fact]
    public async Task PlaybackUiHelper_TriggerPlayOrResumeAsync_PlaysNonEmptyPlaylistWhenCurrentEmpty()
    {
        using var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl1 = pm.CreatePlaylist("EmptyPl");
        var pl2 = pm.CreatePlaylist("PopulatedPl");

        var track = new Track { Title = "PopulatedTrack", Path = @"C:\Music\pop.wav", DurationMs = 50000 };
        pm.AddTracks(pl2, new[] { track });
        pm.SelectPlaylist(pl1);

        var settings = new AppSettings { Output = new OutputSettings { DriverType = AudioDriverType.DirectSound } };
        using var controller = new PlaybackController(settings, pm);

        // When current playlist is empty, PlaybackUiHelper switches to the non-empty playlist and starts playback
        await PlaybackUiHelper.TriggerPlayOrResumeAsync(controller, pm, lib);

        Assert.Equal(pm.Current, pl2);
    }

    [Fact]
    public void PlaybackUiHelper_EnqueueAndRemove_NullCollectionSafelyHandled()
    {
        // EnqueueTracks null safety
        PlaybackUiHelper.EnqueueTracks(null, null, null, null);

        // EnqueueItems null safety
        PlaybackUiHelper.EnqueueItems(null, null, null);

        // RemoveItems null safety
        PlaybackUiHelper.RemoveItems(null, null, null);
    }

    [Fact]
    public async Task PlaybackUiHelper_UpdatePlayingState_ConcurrentMutations_DoesNotThrow()
    {
        var list = new ObservableCollection<PlaylistItem>();
        for (int i = 0; i < 50; i++)
        {
            list.Add(new PlaylistItem(new Track { Title = $"Track {i}", Path = $@"C:\Music\track_{i}.mp3" }));
        }

        bool running = true;
        var updaterTask = Task.Run(() =>
        {
            int tick = 0;
            while (running)
            {
                var targetPath = $@"C:\Music\track_{tick % 50}.mp3";
                PlaybackUiHelper.UpdatePlayingState(list, targetPath);
                tick++;
            }
        });

        for (int i = 0; i < 50; i++)
        {
            list.Add(new PlaylistItem(new Track { Title = $"New Track {i}", Path = $@"C:\Music\new_{i}.mp3" }));
            if (list.Count > 20) list.RemoveAt(0);
            await Task.Yield();
        }

        running = false;
        await updaterTask;
    }

    [Fact]
    public void PlaybackUiHelper_FindItemToScroll_ReturnsMatchingItemOrNull()
    {
        var item1 = new PlaylistItem(new Track { Title = "Song 1", Path = @"C:\Music\song1.mp3" });
        var item2 = new PlaylistItem(new Track { Title = "Song 2", Path = @"C:\Music\song2.mp3" });
        var item3 = new PlaylistItem(new Track { Title = "Song 3", Path = @"C:\Music\song3.mp3" });
        var items = new List<PlaylistItem> { item1, item2, item3 };

        // Exact reference match
        var found1 = PlaybackUiHelper.FindItemToScroll(items, item2);
        Assert.Same(item2, found1);

        // Path match with distinct instance
        var cloneItem3 = new PlaylistItem(new Track { Title = "Song 3 Clone", Path = @"c:\music\SONG3.mp3" });
        var found3 = PlaybackUiHelper.FindItemToScroll(items, cloneItem3);
        Assert.Same(item3, found3);

        // Non-existent item
        var notFound = PlaybackUiHelper.FindItemToScroll(items, new PlaylistItem(new Track { Path = @"C:\Other\ghost.mp3" }));
        Assert.Null(notFound);

        // Null inputs
        Assert.Null(PlaybackUiHelper.FindItemToScroll(null, item1));
        Assert.Null(PlaybackUiHelper.FindItemToScroll(items, null));
    }

    [Fact]
    public void QueuePopupController_SyncFromQueue_HandlesNullEntriesGracefully()
    {
        var controller = new QueuePopupController();
        var mixedEntries = new List<QueueEntry?>
        {
            new QueueEntry(new Playlist("Pl"), new PlaylistItem(new Track { Title = "Song 1" }), "Song 1", "Artist 1"),
            null,
            new QueueEntry(new Playlist("Pl"), new PlaylistItem(new Track { Title = "Song 2" }), "Song 2", "Artist 2")
        };

        // Should safely skip null entries without throwing
        controller.SyncFromQueue(mixedEntries!);

        Assert.Equal(2, controller.Entries.Count);
        Assert.Equal("Song 1", controller.Entries[0].Title);
        Assert.Equal("Song 2", controller.Entries[1].Title);
    }

    [Fact]
    public void PlaybackQueue_EnqueueAndEnqueueNext_NullCollectionsAndNullEntries_DoesNotThrow()
    {
        var queue = new PlaybackQueue();
        var pl = new Playlist("Test Playlist");
        var item1 = new PlaylistItem(new Track { Title = "Song 1", Artist = "Artist 1" });
        var item2 = new PlaylistItem(new Track { Title = "Song 2", Artist = "Artist 2" });

        // Null collection safety
        queue.Enqueue(null!, null!);
        queue.EnqueueNext(null!, null!);
        queue.RemoveItems(null!);
        Assert.Equal(0, queue.Count);

        // Mixed null items
        var mixedItems = new List<PlaylistItem?> { null, item1, null, item2 };
        queue.Enqueue(pl, mixedItems!);
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, item1.QueueIndex);
        Assert.Equal(2, item2.QueueIndex);

        var item3 = new PlaylistItem(new Track { Title = "Song 3", Artist = "Artist 3" });
        var nextMixed = new List<PlaylistItem?> { null, item3, null };
        queue.EnqueueNext(pl, nextMixed!);
        Assert.Equal(3, queue.Count);
        Assert.Equal(1, item3.QueueIndex);
        Assert.Equal(2, item1.QueueIndex);
        Assert.Equal(3, item2.QueueIndex);
    }

    [Fact]
    public async Task PlaybackController_ConcurrentPlaylistReorderingAndClearing_DoesNotThrow()
    {
        using var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl = pm.CreatePlaylist("Concurrent Transition Playlist");
        var settings = new AppSettings { Output = new OutputSettings { DriverType = AudioDriverType.DirectSound } };
        using var controller = new PlaybackController(settings, pm);

        for (int i = 0; i < 40; i++)
        {
            pl.Items.Add(new PlaylistItem(new Track
            {
                Title = $"Track {i}",
                Album = $"Album {i / 10}",
                Artist = $"Artist {i / 10}",
                Path = $@"C:\Music\mock_{i}.wav"
            }));
        }

        bool running = true;
        var nextResolverTask = Task.Run(async () =>
        {
            while (running)
            {
                // Advance / inspect context while items are being sorted, moved, and cleared
                await controller.NextAsync();
                await Task.Yield();
            }
        });

        for (int i = 0; i < 40; i++)
        {
            pm.Sort(pl, (PlaylistSort)(i % 7));
            pl.Items.Add(new PlaylistItem(new Track { Title = $"Extra {i}", Path = $@"C:\Music\extra_{i}.wav" }));
            if (pl.Items.Count > 10) pl.Items.RemoveAt(0);
            await Task.Yield();
        }

        running = false;
        await nextResolverTask;
    }

    [Fact]
    public async Task PlaylistGroupBuilder_BuildGroupsFromItems_ConcurrentListMutation_IsSafe()
    {
        var list = new List<PlaylistItem>();
        for (int i = 0; i < 60; i++)
        {
            list.Add(new PlaylistItem(new Track
            {
                Title = $"Track {i}",
                Album = $"Album {i / 10}",
                Artist = $"Artist {i / 10}",
                Path = $@"C:\Music\t_{i}.mp3"
            }));
        }

        bool running = true;
        var builderTask = Task.Run(() =>
        {
            while (running)
            {
                var groups = PlaylistGroupBuilder.BuildGroupsFromItems(list);
                Assert.NotNull(groups);
            }
        });

        for (int i = 0; i < 60; i++)
        {
            list.Add(new PlaylistItem(new Track { Title = $"Extra {i}", Album = "Extra Album", Artist = "Artist" }));
            if (list.Count > 30) list.RemoveAt(0);
            await Task.Yield();
        }

        running = false;
    }
}

[Collection("SettingsStoreCollection")]
public class AppPathsPortableModeTests : IDisposable
{
    private readonly string _testTempDir;
    private readonly string _originalBaseDir;

    public AppPathsPortableModeTests()
    {
        _originalBaseDir = AppPaths.BaseDir;
        _testTempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_AppPaths_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        lock (AppPaths.BaseDirGate)
        {
            AppPaths.SetCustomBaseDir(_originalBaseDir);
        }
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void DataDirectoryLayout_IsComposedFromTheBaseDirectory()
    {
        // Asserted through the pure helpers: redirecting the process-wide base directory just to
        // check path composition made every other test running in parallel observe the redirect.
        var customDataDir = Path.Combine(_testTempDir, "custom_data");

        Assert.Equal(Path.Combine(customDataDir, "library.db"), AppPaths.LibraryDbPathIn(customDataDir));
        Assert.Equal(Path.Combine(customDataDir, "settings.json"), AppPaths.SettingsFileIn(customDataDir));
        Assert.Equal(Path.Combine(customDataDir, "playlists"), AppPaths.PlaylistsDirIn(customDataDir));
        Assert.Equal(Path.Combine(customDataDir, "artcache"), AppPaths.ArtCacheDirIn(customDataDir));
        Assert.Equal(Path.Combine(customDataDir, "dawnplayer.log"), AppPaths.LogFileIn(customDataDir));
    }

    [Fact]
    public void CustomBaseDir_OverridesBaseDir_AndCreatesDirectories()
    {
        var customDataDir = Path.Combine(_testTempDir, "custom_data2");

        // The override is process-wide, so hold the gate and put it back before releasing it.
        lock (AppPaths.BaseDirGate)
        {
            var previous = AppPaths.BaseDir;
            try
            {
                AppPaths.SetCustomBaseDir(customDataDir);

                Assert.Equal(customDataDir, AppPaths.BaseDir);
                Assert.True(Directory.Exists(customDataDir));
                Assert.True(Directory.Exists(AppPaths.PlaylistsDir));
                Assert.True(Directory.Exists(AppPaths.ArtCacheDir));
            }
            finally
            {
                AppPaths.SetCustomBaseDir(previous);
            }
        }
    }

    [Fact]
    public void ResetBaseDir_ClearsTheOverride()
    {
        var customDataDir = Path.Combine(_testTempDir, "temp_data");

        lock (AppPaths.BaseDirGate)
        {
            var previous = AppPaths.BaseDir;
            try
            {
                AppPaths.SetCustomBaseDir(customDataDir);
                Assert.Equal(customDataDir, AppPaths.BaseDir);

                AppPaths.ResetBaseDir();
                Assert.NotEqual(customDataDir, AppPaths.BaseDir);
            }
            finally
            {
                AppPaths.SetCustomBaseDir(previous);
            }
        }
    }

    [Theory]
    [InlineData(".mp3", true)]
    [InlineData(".flac", true)]
    [InlineData(".wav", true)]
    [InlineData(".m4a", true)]
    [InlineData(".aac", true)]
    [InlineData(".ogg", true)]
    [InlineData(".alac", true)]
    [InlineData(".txt", false)]
    [InlineData(".exe", false)]
    [InlineData("", false)]
    public void IsSupportedAudioFile_CorrectlyIdentifiesExtensions(string extension, bool expected)
    {
        var dummyPath = "C:\\Music\\track" + extension;
        Assert.Equal(expected, AppPaths.IsSupportedAudioFile(dummyPath));
    }
}



