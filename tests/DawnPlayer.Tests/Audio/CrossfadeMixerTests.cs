using System;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

public class CrossfadeMixerTests
{
    private sealed class DummyTrackReader : ITrackReader, ISampleProvider
    {
        private readonly float _constantSample;
        private readonly int _totalSamples;
        private int _samplesRead;

        public DummyTrackReader(float constantSample, TimeSpan totalTime, WaveFormat format)
        {
            _constantSample = constantSample;
            WaveFormat = format;
            TotalTime = totalTime;
            _totalSamples = (int)(totalTime.TotalSeconds * format.SampleRate * format.Channels);
        }

        public WaveFormat WaveFormat { get; }
        public WaveFormat SourceFormat => WaveFormat;
        public ISampleProvider Samples => this;
        public string Path => "dummy.wav";
        public TimeSpan TotalTime { get; }
        public TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)_samplesRead / (WaveFormat.SampleRate * WaveFormat.Channels));
            set => _samplesRead = Math.Clamp((int)(value.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels), 0, _totalSamples);
        }

        public int Read(Span<float> buffer)
        {
            int available = _totalSamples - _samplesRead;
            if (available <= 0) return 0;

            int toRead = Math.Min(buffer.Length, available);
            for (int i = 0; i < toRead; i++)
            {
                buffer[i] = _constantSample;
            }
            _samplesRead += toRead;
            return toRead;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public void Dispose() { }
    }

    /// <summary>Each frame's sample encodes the source position in seconds — the handoff tests
    /// read it back to prove which frame of the next track actually got served.</summary>
    private sealed class PositionEncodedTrackReader : ITrackReader, ISampleProvider
    {
        private readonly int _totalSamples;
        private long _samplesRead;

        public PositionEncodedTrackReader(TimeSpan totalTime, WaveFormat format)
        {
            WaveFormat = format;
            TotalTime = totalTime;
            _totalSamples = (int)(totalTime.TotalSeconds * format.SampleRate * format.Channels);
        }

        public WaveFormat WaveFormat { get; }
        public WaveFormat SourceFormat => WaveFormat;
        public ISampleProvider Samples => this;
        public string Path => "encoded.wav";
        public TimeSpan TotalTime { get; }
        public TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)_samplesRead / (WaveFormat.SampleRate * WaveFormat.Channels));
            set => _samplesRead = Math.Clamp((long)(value.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels), 0, _totalSamples);
        }

        public int Read(Span<float> buffer)
        {
            int available = _totalSamples - (int)_samplesRead;
            if (available <= 0) return 0;

            int toRead = Math.Min(buffer.Length, available);
            double secondsPerSample = 1.0 / (WaveFormat.SampleRate * WaveFormat.Channels);
            for (int i = 0; i < toRead; i++)
            {
                buffer[i] = (float)((_samplesRead + i) * secondsPerSample);
            }
            _samplesRead += toRead;
            return toRead;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public void Dispose() { }
    }

    private static PendingTrack MakePending(Playlist pl, string title, string path, ITrackReader reader)
    {
        var track = new Track { Title = title, Path = path, DurationMs = (long)reader.TotalTime.TotalMilliseconds };
        return new PendingTrack { Playlist = pl, Item = new PlaylistItem(track), Reader = reader };
    }

    private static (SequencerStream stream, PendingTrack track1, PendingTrack track2) CreateFixture(
        float sample1, float sample2, TimeSpan duration, bool rawPassthrough = false)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var pl = new Playlist("Test");

        var t1 = new Track { Title = "T1", Path = "C:\\t1.wav", DurationMs = (long)duration.TotalMilliseconds };
        var t2 = new Track { Title = "T2", Path = "C:\\t2.wav", DurationMs = (long)duration.TotalMilliseconds };

        var item1 = new PlaylistItem(t1);
        var item2 = new PlaylistItem(t2);

        var reader1 = new DummyTrackReader(sample1, duration, format);
        var reader2 = new DummyTrackReader(sample2, duration, format);

        var pt1 = new PendingTrack { Playlist = pl, Item = item1, Reader = reader1 };
        var pt2 = new PendingTrack { Playlist = pl, Item = item2, Reader = reader2 };

        var seq = new SequencerStream(
            format,
            applyVolume: false,
            replayGainNodeGainProvider: _ => 1f,
            latencyMs: 0,
            rawPassthrough: rawPassthrough);

        seq.SwitchTo(pt1);
        seq.SetPrefetched(pt2);

        return (seq, pt1, pt2);
    }

    [Fact]
    public void Crossfade_ZeroDuration_PureGapless()
    {
        // 1초 길이의 트랙 2개, 0초 크로스페이드
        var (seq, _, _) = CreateFixture(1.0f, 0.5f, TimeSpan.FromSeconds(1));
        seq.CrossfadeDurationSeconds = 0.0;

        byte[] buf = new byte[44100 * 2 * 4]; // 1초 분량 버퍼
        int read1 = seq.Read(buf);
        Assert.Equal(buf.Length, read1);

        // 첫 번째 1초는 순수하게 1.0f 여야 함
        var floats1 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf);
        Assert.All(floats1.ToArray(), s => Assert.Equal(1.0f, s, precision: 3));

        // 두 번째 1초는 순수하게 0.5f 여야 함 (갭리스 전환)
        int read2 = seq.Read(buf);
        Assert.Equal(buf.Length, read2);
        var floats2 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf);
        Assert.All(floats2.ToArray(), s => Assert.Equal(0.5f, s, precision: 3));
    }

    [Fact]
    public void Crossfade_OverlapEqualPowerMixing_FadesSmoothly()
    {
        // 2초 길이의 트랙 2개, 1초 크로스페이드
        // Track 1: 1.0f, Track 2: 0.0f (다음 곡이 무음일 때 페이드아웃 검증)
        var (seq, _, _) = CreateFixture(1.0f, 0.0f, TimeSpan.FromSeconds(2));
        seq.CrossfadeDurationSeconds = 1.0;

        byte[] buf = new byte[44100 * 2 * 4]; // 1초 분량

        // 첫 번째 1초 읽기: 아직 크로스페이드 진입 전이므로 1.0f 유지
        seq.Read(buf);
        var floats1 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf);
        Assert.Equal(1.0f, floats1[0], precision: 2);
        Assert.Equal(1.0f, floats1[^1], precision: 2);

        // 두 번째 1초 읽기: 크로스페이드 구간 (1.0f -> 0.0f로 점진적 감쇠)
        seq.Read(buf);
        var floats2 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf);

        // 시작점은 1.0f에 가깝고, 끝점은 0.0f에 가까워야 함
        Assert.True(floats2[0] > 0.9f);
        Assert.True(floats2[^1] < 0.1f);

        // 단조 감소하는 경향성 확인
        float mid = floats2[floats2.Length / 2];
        Assert.True(mid > 0.4f && mid < 0.9f);
    }

    [Fact]
    public void Crossfade_RawPassthrough_BypassesCrossfade()
    {
        // DSD DoP 모드(rawPassthrough=true)에서는 크로스페이드가 설정되어 있어도 무조건 0초(갭리스)로 동작
        var (seq, _, _) = CreateFixture(1.0f, 0.5f, TimeSpan.FromSeconds(1), rawPassthrough: true);
        seq.CrossfadeDurationSeconds = 1.0;

        byte[] buf = new byte[44100 * 2 * 4]; // 1초 분량
        seq.Read(buf);

        var floats1 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf);
        // DoP 모드이므로 1초 내내 1.0f 유지 (페이드아웃 없음)
        Assert.Equal(1.0f, floats1[^1], precision: 3);
    }

    [Fact]
    public void Crossfade_SeekCancelsCrossfade()
    {
        // 5초 트랙에서 1초 크로스페이드 설정
        var (seq, _, _) = CreateFixture(1.0f, 0.0f, TimeSpan.FromSeconds(5));
        seq.CrossfadeDurationSeconds = 1.0;

        byte[] buf = new byte[44100 * 2 * 4]; // 1초 분량

        // 3초까지 읽기
        seq.Read(buf);
        seq.Read(buf);
        seq.Read(buf);

        // 4초 시점에 진입 (크로스페이드 시작)
        seq.Read(buf);

        // 사용자가 다시 1초 위치로 수동 시크
        seq.Seek(TimeSpan.FromSeconds(1));

        // 시크 직후 1초 분량 읽기 -> 크로스페이드가 취소되어 순수한 1.0f 가 나와야 함
        seq.Read(buf);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf);
        Assert.All(floats.ToArray(), s => Assert.Equal(1.0f, s, precision: 2));
    }

    [Fact]
    public void Crossfade_HandoffAnchorsReportedPositionToHeardAudio()
    {
        // 불변식: 보고 재생 위치 == 실제 들린 오디오 위치 (8ebfe12 교훈).
        // 페이드 창이 끝난 시점에 다음 트랙 소스는 이미 T초 진행된 상태다 — 보고 위치가 0으로
        // 리셋되면 트랙 끝까지 시크바가 T초 뒤처리고, 다음 아밍 판정도 틀린 남은 시간으로 계산된다.
        // 실제 디바이스처럼 작은 블록(100ms)으로 읽어야 창이 여러 패스에 걸쳐 진행된다.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var pl = new Playlist("Test");
        var seq = new SequencerStream(format, applyVolume: false,
            replayGainNodeGainProvider: _ => 1f, latencyMs: 0, rawPassthrough: false);

        seq.SwitchTo(MakePending(pl, "A", "C:\\a.wav", new DummyTrackReader(1.0f, TimeSpan.FromSeconds(2), format)));
        seq.SetPrefetched(MakePending(pl, "B", "C:\\b.wav", new DummyTrackReader(0.5f, TimeSpan.FromSeconds(2), format)));
        seq.CrossfadeDurationSeconds = 1.0;

        byte[] block = new byte[4410 * 2 * 4]; // 100ms

        for (int i = 0; i < 10; i++) seq.Read(block); // A 순수 구간 0→1초
        Assert.Equal(1.0, seq.GetPosition().TotalSeconds, 1);

        for (int i = 0; i < 10; i++) seq.Read(block); // 페이드 창: 완료 후 B로 전환 — B 소스는 이미 1초 소비됨
        // 보고 위치는 마지막 패스 100ms가 아니라 B의 실제 위치(1초)에 앵커되어야 한다.
        Assert.InRange(seq.GetPosition().TotalSeconds, 0.97, 1.03);

        for (int i = 0; i < 5; i++) seq.Read(block); // 0.5초 추가
        Assert.InRange(seq.GetPosition().TotalSeconds, 1.45, 1.55);
    }

    [Fact]
    public void Crossfade_ConsecutiveBoundaries_KeepFading()
    {
        // 회귀: 첫 전환 후 위치가 0 근처로 가라앉으면 rem 계산이 틀어져 두 번째 전환부터
        // 크로스페이드가 즉시-스위치로 퇴화했다. 연속 전환에서도 등교전 믹싱이 유지되어야 한다.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var pl = new Playlist("Test");
        var seq = new SequencerStream(format, applyVolume: false,
            replayGainNodeGainProvider: _ => 1f, latencyMs: 0, rawPassthrough: false);

        seq.SwitchTo(MakePending(pl, "A", "C:\\a.wav", new DummyTrackReader(1.0f, TimeSpan.FromSeconds(2), format)));
        seq.SetPrefetched(MakePending(pl, "B", "C:\\b.wav", new DummyTrackReader(0.5f, TimeSpan.FromSeconds(2), format)));
        seq.CrossfadeDurationSeconds = 1.0;

        byte[] block = new byte[4410 * 2 * 4]; // 100ms
        for (int i = 0; i < 10; i++) seq.Read(block); // A 순수 구간
        for (int i = 0; i < 10; i++) seq.Read(block); // A→B 페이드

        seq.SetPrefetched(MakePending(pl, "C", "C:\\c.wav", new DummyTrackReader(0.25f, TimeSpan.FromSeconds(2), format)));

        var secondFade = new byte[44100 * 2 * 4]; // B→C 창 1초 분량 수집
        int total = 0;
        while (total < secondFade.Length)
        {
            int read = seq.Read(secondFade.AsSpan(total));
            if (read <= 0) break;
            total += read;
        }

        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(secondFade);
        // 창 3/4 지점: 등교전이면 이미 B(0.5)→C(0.25)로 상당히 진행돼야 한다.
        // (퇴화 시 이 지점은 아직 순수 B인 0.5에 머문다.)
        int threeQuarters = (int)(floats.Length * 0.75);
        Assert.True(floats[threeQuarters] < 0.45f, $"창 3/4에서 C 쪽으로 페이드 진행 중이어야 함: {floats[threeQuarters]}");
        Assert.True(floats[0] > 0.45f, $"창 시작은 B(0.5) 근처여야 함: {floats[0]}");

        // 전환 직후 보고 위치도 C의 실제 위치(1초)에 앵커된다.
        Assert.InRange(seq.GetPosition().TotalSeconds, 0.97, 1.03);
    }

    [Fact]
    public void Crossfade_SeekAbortedFade_NextSourceStillStartsAtBeginning()
    {
        // 불변식: 시크로 페이드를 취소하면 일부 소비된 다음 트랙 소스는 0으로 되감긴다 —
        // 그렇지 않으면 (크로스페이드를 끄고) 갭리스 전환 시 다음 트랙 앞부분이 통째로 건너뛰어진다.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var pl = new Playlist("Test");
        var seq = new SequencerStream(format, applyVolume: false,
            replayGainNodeGainProvider: _ => 1f, latencyMs: 0, rawPassthrough: false);

        seq.SwitchTo(MakePending(pl, "A", "C:\\a.wav", new DummyTrackReader(1.0f, TimeSpan.FromSeconds(5), format)));
        seq.SetPrefetched(MakePending(pl, "B", "C:\\b.wav", new PositionEncodedTrackReader(TimeSpan.FromSeconds(5), format)));
        seq.CrossfadeDurationSeconds = 2.0;

        Exception? readError = null;
        seq.ReadError += ex => readError = ex;

        byte[] oneSecond = new byte[44100 * 2 * 4];
        for (int i = 0; i < 3; i++) seq.Read(oneSecond); // A 0→3초

        seq.Read(oneSecond); // 페이드 진입(부분): A 3→4초 × B 0→1초, 창(2초) 미완료 — A에 1초 잔여
        seq.Seek(TimeSpan.FromSeconds(1)); // 취소 + B 되감김
        seq.CrossfadeDurationSeconds = 0.0; // 재아밍 차단 — 갭리스 전환만 남김

        for (int i = 0; i < 4; i++) seq.Read(oneSecond); // A 1→5초

        byte[] tail = new byte[44100 * 2 * 4];
        int read = seq.Read(tail); // A 소진 → B로 갭리스 전환, B의 첫 프레임부터
        Assert.True(read > 0, $"read={read}, readError={readError?.GetType().Name}: {readError?.Message}, pos={seq.GetPosition().TotalSeconds}, hasPrefetch={seq.HasPrefetched}");

        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(tail);
        Assert.True(floats[0] < 0.02f, $"B가 트랙 처음(0초)부터 시작해야 함: {floats[0]}");
        Assert.True(floats[^1] > 0.9f && floats[^1] < 1.1f, $"전달 1초 후 B 위치는 ~1초: {floats[^1]}");
        // 보고 위치도 들린 내용과 일치해야 한다.
        Assert.InRange(seq.GetPosition().TotalSeconds, 0.95, 1.05);
    }
}
