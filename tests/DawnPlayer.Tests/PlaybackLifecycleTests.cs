using System;
using System.Threading;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace DawnPlayer.Tests;

public class PlaybackLifecycleTests
{
    private readonly ITestOutputHelper _output;

    public PlaybackLifecycleTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// xUnit 2.5.3 cannot turn a running test into a skip, so an environment bail-out reports PASS.
    /// This marker is the only way a log reader can tell one from a run that actually asserted.
    /// </summary>
    private void LogEnvironmentSkip(string reason) => _output.WriteLine($"[SKIPPED-ENV] {reason}");

    [Fact]
    [Trait("Category", "RequiresAudio")]
    public void TestWasapiExclusiveFormatNegotiationVariants()
    {
        MMDevice? def = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            LogEnvironmentSkip("no audio render endpoint (MMDeviceEnumerator threw COMException)");
            return;
        }

        if (def == null)
        {
            LogEnvironmentSkip("no default audio render endpoint");
            return;
        }

        var source16 = new WaveFormat(44100, 16, 2);
        var source24 = new WaveFormat(44100, 24, 2);

        var negotiated16 = WasapiDeviceService.TryNegotiateExclusive(def, source16, ExclusiveBitDepth.Source);
        var negotiated24 = WasapiDeviceService.TryNegotiateExclusive(def, source24, ExclusiveBitDepth.Source);

        Assert.NotNull(negotiated16);
        Assert.NotNull(negotiated24);
        Assert.Equal(44100, negotiated16.SampleRate);
        Assert.Equal(44100, negotiated24.SampleRate);
    }

    [Fact]
    [Trait("Category", "RequiresAudio")]
    public void TestPauseAndResumeWithSilenceStreaming()
    {
        MMDevice? def = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            LogEnvironmentSkip("no audio render endpoint (MMDeviceEnumerator threw COMException)");
            return;
        }

        if (def == null)
        {
            LogEnvironmentSkip("no default audio render endpoint");
            return;
        }

        var exclusiveFmt = WasapiDeviceService.TryNegotiateExclusive(def, new WaveFormat(44100, 16, 2), ExclusiveBitDepth.Source);
        bool useExclusive = exclusiveFmt != null;
        var fmt = exclusiveFmt ?? WasapiDeviceService.GetSharedTarget(def);

        var provider = new TestCountingSampleProvider(fmt);
        WasapiOut? output = null;
        try
        {
            output = new WasapiOut(def, useExclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, 100);
            output.Init(provider);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            output?.Dispose();
            // Hardware in use or exclusive blocked -> fallback to shared
            fmt = WasapiDeviceService.GetSharedTarget(def);
            provider = new TestCountingSampleProvider(fmt);
            try
            {
                output = new WasapiOut(def, AudioClientShareMode.Shared, true, 100);
                output.Init(provider);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                LogEnvironmentSkip("no audio render endpoint accepts a shared-mode WasapiOut");
                return;
            }
        }

        using (output)
        {
            // 1. Initial Play
            output.Play();
            SpinWait.SpinUntil(() => provider.RealReadCount > 0, 500);
            int real1 = provider.RealReadCount;
            Assert.True(real1 > 0, "Initial playback did not produce reads.");

            // 2. Pause (feeds silence)
            provider.IsPaused = true;
            Thread.Sleep(10);
            int realAtPauseStart = provider.RealReadCount;
            int silenceAtPauseStart = provider.SilenceReadCount;
            SpinWait.SpinUntil(() => provider.SilenceReadCount > silenceAtPauseStart, 500);
            int realDuringPause = provider.RealReadCount;
            int silenceDuringPause = provider.SilenceReadCount;

            Assert.Equal(realAtPauseStart, realDuringPause); // Real audio did not advance
            Assert.True(silenceDuringPause > silenceAtPauseStart);  // Hardware kept receiving zeros

            // 3. Resume (feeds real samples again)
            provider.IsPaused = false;
            SpinWait.SpinUntil(() => provider.RealReadCount > realDuringPause, 500);
            int real2 = provider.RealReadCount;
            Assert.True(real2 > realDuringPause, "Playback did not resume after pause.");

            // 4. Second Pause & Resume Cycle
            provider.IsPaused = true;
            Thread.Sleep(10);
            int realAtPause2Start = provider.RealReadCount;
            int silenceAtPause2Start = provider.SilenceReadCount;
            SpinWait.SpinUntil(() => provider.SilenceReadCount > silenceAtPause2Start, 500);
            int realDuringPause2 = provider.RealReadCount;
            Assert.Equal(realAtPause2Start, realDuringPause2);

            provider.IsPaused = false;
            SpinWait.SpinUntil(() => provider.RealReadCount > realDuringPause2, 500);
            int real3 = provider.RealReadCount;
            Assert.True(real3 > realDuringPause2, "Playback did not resume after second pause.");

            output.Stop();
        }
    }

    [Fact]
    public void TestEnumerateDevicesForEachDriverType()
    {
        // 1. WASAPI
        var wasapiDevs = WasapiDeviceService.EnumerateDevices(AudioDriverType.Wasapi);
        Assert.NotNull(wasapiDevs);

        // 2. DirectSound
        var dsDevs = WasapiDeviceService.EnumerateDevices(AudioDriverType.DirectSound);
        Assert.NotNull(dsDevs);
        Assert.NotEmpty(dsDevs);
        Assert.Contains(dsDevs, d => d.IsDefault);

        // 3. WaveOut
        var waveOutDevs = WasapiDeviceService.EnumerateDevices(AudioDriverType.WaveOut);
        Assert.NotNull(waveOutDevs);
        Assert.NotEmpty(waveOutDevs);
        Assert.Contains(waveOutDevs, d => d.Id == "-1" && d.IsDefault);
    }

    [Fact]
    public void TestUiSettingsLayoutAndCoverSizePersistence()
    {
        var settings = new AppSettings();
        settings.Ui.AlbumCoverSize = 220;
        settings.Ui.LeftSidebarWidth = 310;
        settings.Ui.RightSidebarWidth = 360;
        settings.Ui.LyricsSidebarWidth = 280;
        settings.Ui.LibraryTreeGroupMode = 4;
        settings.Ui.LibraryViewMode = 1;
        settings.Ui.LibrarySortColumn = 2;
        settings.Ui.LibrarySortAscending = false;
        settings.Ui.LibrarySelectedFilterType = "Genre";
        settings.Ui.LibrarySelectedFilterValue = "Classical";

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(220, loaded.Ui.AlbumCoverSize);
        Assert.Equal(310, loaded.Ui.LeftSidebarWidth);
        Assert.Equal(360, loaded.Ui.RightSidebarWidth);
        Assert.Equal(280, loaded.Ui.LyricsSidebarWidth);
        Assert.Equal(4, loaded.Ui.LibraryTreeGroupMode);
        Assert.Equal(1, loaded.Ui.LibraryViewMode);
        Assert.Equal(2, loaded.Ui.LibrarySortColumn);
        Assert.False(loaded.Ui.LibrarySortAscending);
        Assert.Equal("Genre", loaded.Ui.LibrarySelectedFilterType);
        Assert.Equal("Classical", loaded.Ui.LibrarySelectedFilterValue);
    }

    private sealed class TestCountingSampleProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; }
        public volatile bool IsPaused;
        public int RealReadCount = 0;
        public int SilenceReadCount = 0;

        public TestCountingSampleProvider(WaveFormat fmt) => WaveFormat = fmt;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (IsPaused)
            {
                Array.Clear(buffer, offset, count);
                Interlocked.Increment(ref SilenceReadCount);
                return count;
            }

            for (int i = 0; i < count; i++) buffer[offset + i] = 0x55;
            Interlocked.Increment(ref RealReadCount);
            return count;
        }
    }
}
