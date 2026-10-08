using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Contract tests for the M2 engine seams (decoder registry, output-driver registry, tag
/// provider chain, play-order injection). The invariant under test is one sentence: adding a
/// capability must be a registration, and removing it must restore the built-in behavior
/// exactly.
/// </summary>
public sealed class TrackReaderProviderRegistryTests
{
    private sealed class FakeReader : ITrackReader
    {
        public FakeReader(string path) => Path = path;
        public ISampleProvider Samples => throw new NotSupportedException();
        public WaveFormat SourceFormat => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public TimeSpan TotalTime => TimeSpan.Zero;
        public TimeSpan CurrentTime { get => TimeSpan.Zero; set { } }
        public string Path { get; }
        public void Dispose() { }
    }

    private sealed class FakeProvider : ITrackReaderProvider
    {
        private readonly string _claimedExtension;
        public int Opens;
        public string? LastPath;

        public FakeProvider(string claimedExtension, int order = 500)
        {
            _claimedExtension = claimedExtension;
            Order = order;
        }

        public int Order { get; }
        public bool CanOpen(string path, string extension) => extension == _claimedExtension;
        public ITrackReader Open(string path)
        {
            Opens++;
            LastPath = path;
            return new FakeReader(path);
        }
    }

    [Fact]
    public void RegisteredProvider_ClaimsItsExtension()
    {
        var provider = new FakeProvider(".zzz");
        AudioFileReaderFactory.Register(provider);
        try
        {
            var reader = AudioFileReaderFactory.Open(@"C:\music\song.zzz");
            Assert.IsType<FakeReader>(reader);
            Assert.Equal(1, provider.Opens);
        }
        finally
        {
            Assert.True(AudioFileReaderFactory.Unregister(provider));
        }
    }

    [Fact]
    public void AfterUnregistration_UnknownExtensionFailsWithAudioOpenException()
    {
        var provider = new FakeProvider(".zzz");
        AudioFileReaderFactory.Register(provider);
        AudioFileReaderFactory.Unregister(provider);

        Assert.False(AudioFileReaderFactory.Unregister(provider), "double unregister");
        // The Media Foundation provider is the unconditional catch-all, so the extension is no
        // longer *claimed* by anyone but still lands there and fails on the missing file.
        var ex = Assert.Throws<AudioOpenException>(() => AudioFileReaderFactory.Open("song.zzz"));
        Assert.Contains("song.zzz", ex.Message);
    }

    [Fact]
    public void HigherOrder_BeatsTheBuiltInForAContestedExtension()
    {
        var provider = new FakeProvider(".ogg", order: 150);
        AudioFileReaderFactory.Register(provider);
        try
        {
            // A real .ogg file does not exist here; the fake claims the extension first, so the
            // open succeeds without ever touching NVorbis.
            var reader = AudioFileReaderFactory.Open("nonexistent.ogg");
            Assert.IsType<FakeReader>(reader);
        }
        finally
        {
            AudioFileReaderFactory.Unregister(provider);
        }
    }

    [Fact]
    public void CueFragmentRouting_WrapsThePhysicalProviderResult()
    {
        var provider = new FakeProvider(".flac");
        AudioFileReaderFactory.Register(provider);
        try
        {
            var cuePath = @"C:\music\album.flac#cue=0-5000";
            var reader = AudioFileReaderFactory.Open(cuePath);

            // The range reader wraps the provider's reader; the provider saw the physical path.
            var cue = Assert.IsType<CueTrackReader>(reader);
            Assert.Equal(@"C:\music\album.flac", provider.LastPath);
        }
        finally
        {
            AudioFileReaderFactory.Unregister(provider);
        }
    }
}

public sealed class OutputDriverRegistryTests : IDisposable
{
    private sealed class CapturingDriver : IOutputDriver
    {
        public OutputSessionRequest? Request;
        public AudioDriverType DriverType { get; set; } = AudioDriverType.DirectSound;
        public OutputSession Start(OutputSessionRequest request)
        {
            Request = request;
            return null!;
        }
    }

    private readonly CapturingDriver _driver = new();

    public OutputDriverRegistryTests()
    {
        OutputSessionFactory.RegisterDriver(_driver);
    }

    public void Dispose()
    {
        OutputSessionFactory.UnregisterDriver(AudioDriverType.DirectSound);
    }

    private static OutputSessionFactory NewFactory(AppSettings settings) => new(
        settings,
        replayGainNodeGainProvider: _ => 1f,
        replayGainProvider: _ => null,
        subscribeSequencer: _ => { },
        subscribeOutput: _ => { },
        warn: _ => { });

    private static PendingTrack NewPending() => new()
    {
        Playlist = new Playlist("pl"),
        Item = new PlaylistItem(new Track { Path = "fake", Title = "t" }),
        Reader = new StubReader(),
    };

    private sealed class StubReader : ITrackReader
    {
        public ISampleProvider Samples => throw new NotSupportedException();
        public WaveFormat SourceFormat => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public TimeSpan TotalTime => TimeSpan.Zero;
        public TimeSpan CurrentTime { get => TimeSpan.Zero; set { } }
        public string Path => "fake";
        public void Dispose() { }
    }

    [Fact]
    public void RegisteredDriver_ReceivesTheStartCall()
    {
        var settings = new AppSettings();
        settings.Output.DriverType = AudioDriverType.DirectSound;

        NewFactory(settings).Start(NewPending());

        Assert.NotNull(_driver.Request);
        Assert.Equal(AudioDriverType.DirectSound, _driver.Request.Settings.Output.DriverType);
    }

    [Theory]
    [InlineData(5, 20)]
    [InlineData(2000, 1000)]
    [InlineData(100, 100)]
    public void LatencyIsClampedBeforeReachingTheDriver(int configured, int expected)
    {
        var settings = new AppSettings();
        settings.Output.DriverType = AudioDriverType.DirectSound;
        settings.Output.LatencyMs = configured;

        NewFactory(settings).Start(NewPending());

        Assert.Equal(expected, _driver.Request!.Latency);
    }

    [Fact]
    public void ResolutionFallsBackToBuiltIns_AfterUnregistration()
    {
        Assert.True(OutputSessionFactory.UnregisterDriver(AudioDriverType.DirectSound));
        Assert.False(OutputSessionFactory.UnregisterDriver(AudioDriverType.DirectSound), "double unregister");

        Assert.IsType<DirectSoundOutputDriver>(OutputSessionFactory.ResolveDriver(AudioDriverType.DirectSound));
        Assert.IsType<WaveOutOutputDriver>(OutputSessionFactory.ResolveDriver(AudioDriverType.WaveOut));
        Assert.IsType<WasapiOutputDriver>(OutputSessionFactory.ResolveDriver(AudioDriverType.Wasapi));
    }

    [Fact]
    public void TheWasapiDriver_CannotBeRemoved()
    {
        Assert.False(OutputSessionFactory.UnregisterDriver(AudioDriverType.Wasapi));
        Assert.IsType<WasapiOutputDriver>(OutputSessionFactory.ResolveDriver(AudioDriverType.Wasapi));
    }
}

public sealed class PlayOrderInjectionTests
{
    private sealed class CountingStrategy : IPlayOrderStrategy
    {
        public int Calls;
        public List<PlayOrderContext> Contexts = new();

        public (Playlist Playlist, PlaylistItem Item)? PeekNext(PlayOrderContext ctx, ISet<PlaylistItem> skip)
        {
            Calls++;
            Contexts.Add(ctx);
            return null; // "nothing is next"
        }
    }

    private sealed class EmptyLibrary : IMusicLibrary
    {
        public IReadOnlyList<Track> Tracks { get; } = Array.Empty<Track>();
        public int Count => 0;
        public event Action? TracksChanged;
        public event Action<ScanProgress>? ScanProgress;
        public Track? GetTrack(string path) => null;
        public void UpdateStats(Track track) { }
        public void UpdateRating(Track track) { }
        public void UpdateReplayGain(Track track) { }
        public void ReplaceTracks(IReadOnlyCollection<Track> tracks) { }
        public void LoadFromDb() { }
        public Task ScanAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        internal void Unused() { TracksChanged?.Invoke(); ScanProgress?.Invoke(default!); }
    }

    [Fact]
    public async Task NextAsync_ConsultsTheInjectedStrategy_AsAManualAdvance()
    {
        var strategy = new CountingStrategy();
        using (var controller = new PlaybackController(new AppSettings(), new PlaylistManager(new EmptyLibrary()), strategy))
        {
            string? warned = null;
            controller.Warning += m => warned = m;

            await controller.NextAsync();

            Assert.Equal(1, strategy.Calls);
            Assert.True(strategy.Contexts[0].ManualAdvance);
            Assert.NotNull(warned); // "다음 트랙이 없습니다" — null from the strategy ends the advance
        }
    }
}

public sealed class TagProviderChainTests
{
    private sealed class FakeTagProvider : ITagProvider
    {
        public int Order => 500;
        public bool CanRead(string path, string extension) => extension == ".zzz";
        public Track? TryRead(string path, out TagLib.IPicture? embeddedArt)
        {
            embeddedArt = null;
            return new Track { Path = path, Title = "from-fake" };
        }
    }

    [Fact]
    public void RegisteredProvider_ServesTheRead()
    {
        var provider = new FakeTagProvider();
        TagReader.RegisterProvider(provider);
        try
        {
            var track = TagReader.TryRead(@"C:\music\mystery.zzz");
            Assert.NotNull(track);
            Assert.Equal("from-fake", track!.Title);
        }
        finally
        {
            Assert.True(TagReader.UnregisterProvider(provider));
        }
    }

    [Fact]
    public void AfterUnregistration_FallsBackToTagLib_ForMissingFiles()
    {
        var provider = new FakeTagProvider();
        TagReader.RegisterProvider(provider);
        TagReader.UnregisterProvider(provider);

        // No provider claims .zzz anymore and TagLib cannot open a nonexistent file: null.
        Assert.Null(TagReader.TryRead(@"C:\music\mystery.zzz"));
    }
}
