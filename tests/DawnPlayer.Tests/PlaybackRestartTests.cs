using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using NAudio.Wave;

namespace DawnPlayer.Tests;

/// <summary>
/// Device/mode switch restart behavior: <see cref="PendingTrack.StartPosition"/>
/// must reposition the track atomically with the switch so that no audio is
/// served from position zero and the reported position stays continuous.
/// </summary>
public class PlaybackRestartTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "DawnPlayer_Restart_" + Guid.NewGuid().ToString("N"));

    public PlaybackRestartTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateWav(string name, int seconds)
    {
        var path = Path.Combine(_tempDir, name);
        using var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 2));
        var buf = new short[44100 * 2];
        for (int sec = 0; sec < seconds; sec++) w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    private static PendingTrack MakePending(Playlist pl, PlaylistItem item, ITrackReader reader, TimeSpan? start = null) => new()
    {
        Playlist = pl,
        Item = item,
        Reader = reader,
        StartPosition = start
    };

    [Fact]
    public void SwitchTo_WithStartPosition_ReportsPositionBeforeAnyRead()
    {
        var path = CreateWav("start.wav", 30);
        var outFmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var seq = new SequencerStream(outFmt, applyVolume: false, _ => 1f, latencyMs: 50);

        var pl = new Playlist("RestartTest");
        var item = new PlaylistItem(new Track { Path = path, Title = "start" });
        using var reader = AudioFileReaderFactory.Open(path);

        seq.SwitchTo(MakePending(pl, item, reader, TimeSpan.FromSeconds(12.5)));

        // Position must already reflect the start offset without a single Read(),
        // so the device/mode restart never reports or emits position zero.
        var pos = seq.GetPosition();
        Assert.True(pos >= TimeSpan.FromSeconds(12.5 - 0.1), $"position {pos} should continue from ~12.5s");
        Assert.True(pos < TimeSpan.FromSeconds(13), $"position {pos} should not run ahead");
    }

    [Fact]
    public void SwitchTo_WithStartPosition_SeeksUnderlyingReader()
    {
        var path = CreateWav("seek.wav", 30);
        var outFmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var seq = new SequencerStream(outFmt, applyVolume: false, _ => 1f, latencyMs: 50);

        var pl = new Playlist("RestartTest");
        var item = new PlaylistItem(new Track { Path = path, Title = "seek" });
        using var reader = AudioFileReaderFactory.Open(path);

        seq.SwitchTo(MakePending(pl, item, reader, TimeSpan.FromSeconds(10)));

        // First served audio must come from ~10s, not from the track start.
        var buf = new byte[outFmt.AverageBytesPerSecond / 10];
        int read = seq.Read(buf, 0, buf.Length);
        Assert.True(read > 0);
        var pos = seq.GetPosition();
        Assert.True(pos >= TimeSpan.FromSeconds(9.9), $"audio served from {pos}, expected ~10s");
    }

    [Fact]
    public void SwitchTo_WithoutStartPosition_ResetsToZero()
    {
        var path = CreateWav("reset.wav", 30);
        var outFmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var seq = new SequencerStream(outFmt, applyVolume: false, _ => 1f, latencyMs: 50);

        var pl = new Playlist("RestartTest");
        var item = new PlaylistItem(new Track { Path = path, Title = "reset" });
        using var reader = AudioFileReaderFactory.Open(path);

        seq.SwitchTo(MakePending(pl, item, reader, TimeSpan.FromSeconds(20)));
        seq.SwitchTo(MakePending(pl, item, AudioFileReaderFactory.Open(path)));

        Assert.True(seq.GetPosition() < TimeSpan.FromSeconds(0.5),
            "a switch without StartPosition is a fresh track start and must reset the position");
    }

    [Fact]
    public void SwitchTo_WithStartPosition_ClampsBeyondTrackEnd()
    {
        var path = CreateWav("clamp.wav", 10);
        var outFmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var seq = new SequencerStream(outFmt, applyVolume: false, _ => 1f, latencyMs: 50);

        var pl = new Playlist("RestartTest");
        var item = new PlaylistItem(new Track { Path = path, Title = "clamp" });
        using var reader = AudioFileReaderFactory.Open(path);

        seq.SwitchTo(MakePending(pl, item, reader, TimeSpan.FromMinutes(5)));

        Assert.True(seq.GetPosition() <= reader.TotalTime + TimeSpan.FromMilliseconds(50),
            "start position beyond the track end must clamp to the track length");
    }

    [Fact]
    public void PendingTrack_StartPosition_DefaultsToNull()
    {
        var path = CreateWav("default.wav", 5);
        var pl = new Playlist("RestartTest");
        var item = new PlaylistItem(new Track { Path = path, Title = "default" });
        using var reader = AudioFileReaderFactory.Open(path);

        var pending = MakePending(pl, item, reader);

        Assert.Null(pending.StartPosition);
    }
}
