using System;
using System.IO;
using System.Threading;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Adversarial and format extension tests for AudioTranscoder (T3-2).
/// Validates extension mapping, ffmpeg CLI argument generation, rejection of stream tracks,
/// ffmpeg-missing handling, and backward compatibility with the existing ConvertToWav contract.
/// </summary>
public sealed class AudioTranscoderFormatTests
{
    [Theory]
    [InlineData(OutputAudioFormat.Wav, ".wav")]
    [InlineData(OutputAudioFormat.Flac, ".flac")]
    [InlineData(OutputAudioFormat.Mp3, ".mp3")]
    [InlineData(OutputAudioFormat.Opus, ".opus")]
    [InlineData(OutputAudioFormat.Aac, ".m4a")]
    public void SuggestFileName_AppliesCorrectExtensionForFormat(OutputAudioFormat format, string expectedExt)
    {
        var track = new Track
        {
            Path = @"C:\music\sample.flac",
            Title = "Song Title",
            Artist = "Artist Name"
        };

        string suggested = AudioTranscoder.SuggestFileName(track, @"C:\out", format);

        Assert.StartsWith(@"C:\out", suggested);
        Assert.EndsWith(expectedExt, suggested);
        Assert.Contains("Artist Name - Song Title", suggested);
    }

    [Fact]
    public void BuildFfmpegArgs_GeneratesExpectedArgs_ForFlac()
    {
        string args = FfmpegTranscodeArgs.Build(
            OutputAudioFormat.Flac,
            quality: "8",
            sampleRate: 44100,
            channels: 2,
            pipeBitsPerSample: 24,
            outputPath: @"C:\out\test.flac");

        // 무손실 표방: s16 파이프는 24-bit 소스를 무음으로 강등했었다 — FLAC은 s24le로 밀어 넣는다.
        Assert.Contains("-f s24le", args);
        Assert.Contains("-ar 44100", args);
        Assert.Contains("-ac 2", args);
        Assert.Contains("-c:a flac", args);
        Assert.Contains("-compression_level 8", args);
        // stderr 비드레인 대비: 통계 출력은 버전별로 달라질 수 있으므로 항상 꺼둔다.
        Assert.Contains("-nostats", args);
        Assert.EndsWith("\"C:\\out\\test.flac\"", args);
    }

    [Fact]
    public void BuildFfmpegArgs_GeneratesExpectedArgs_ForMp3()
    {
        string args = FfmpegTranscodeArgs.Build(
            OutputAudioFormat.Mp3,
            quality: "320k",
            sampleRate: 48000,
            channels: 2,
            pipeBitsPerSample: 16,
            outputPath: @"C:\out\test.mp3");

        Assert.Contains("-f s16le", args);
        Assert.Contains("-nostats", args);
        Assert.Contains("-c:a libmp3lame", args);
        Assert.Contains("-b:a 320k", args);
    }

    [Fact]
    public void BuildFfmpegArgs_PipeDepthFollowsContainerLosslessness()
    {
        // FLAC(무손실 컨테이너)은 24-bit 파이프, 손실 포맷은 16-bit 파이프가 기본이다.
        Assert.Equal("s24le", FfmpegTranscodeArgs.PipeFormatFor(OutputAudioFormat.Flac));
        Assert.Equal("s16le", FfmpegTranscodeArgs.PipeFormatFor(OutputAudioFormat.Mp3));
        Assert.Equal("s16le", FfmpegTranscodeArgs.PipeFormatFor(OutputAudioFormat.Opus));
        Assert.Equal("s16le", FfmpegTranscodeArgs.PipeFormatFor(OutputAudioFormat.Aac));
    }

    [Fact]
    public void BuildFfmpegArgs_GeneratesExpectedArgs_ForOpus()
    {
        string args = FfmpegTranscodeArgs.Build(
            OutputAudioFormat.Opus,
            quality: "160k",
            sampleRate: 48000,
            channels: 2,
            pipeBitsPerSample: 16,
            outputPath: @"C:\out\test.opus");

        Assert.Contains("-c:a libopus", args);
        Assert.Contains("-b:a 160k", args);
    }

    [Fact]
    public void BuildFfmpegArgs_GeneratesExpectedArgs_ForAac()
    {
        string args = FfmpegTranscodeArgs.Build(
            OutputAudioFormat.Aac,
            quality: "256k",
            sampleRate: 44100,
            channels: 2,
            pipeBitsPerSample: 16,
            outputPath: @"C:\out\test.m4a");

        Assert.Contains("-c:a aac", args);
        Assert.Contains("-b:a 256k", args);
    }

    [Fact]
    public void Convert_RejectsRadioAndStreamTracks()
    {
        var radioTrack = new Track
        {
            Path = "http://stream.example.com/radio.mp3",
            Title = "Live Radio",
            SourceKind = TrackSourceKind.Radio
        };

        var options = new TranscodeOptions
        {
            OutputDirectory = Path.GetTempPath(),
            OutputFormat = OutputAudioFormat.Wav
        };

        var output = AudioTranscoder.Convert(radioTrack, options, out var result);

        Assert.Null(output);
        Assert.Equal(TranscodeResult.UnsupportedTrack, result);
    }

    [Fact]
    public void Convert_MissingFfmpeg_ReturnsFfmpegMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DawnTranscodeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srcFile = Path.Combine(dir, "src.wav");
        File.WriteAllBytes(srcFile, MinimalWav(22050, 1, 440.0, 0.2));

        try
        {
            var track = new Track
            {
                Path = srcFile,
                Title = "Test Song",
                SourceKind = TrackSourceKind.File
            };

            var options = new TranscodeOptions
            {
                OutputDirectory = dir,
                OutputFormat = OutputAudioFormat.Flac,
                FfmpegPath = @"C:\non_existent_folder_xyz\ffmpeg_never_there.exe"
            };

            var output = AudioTranscoder.Convert(track, options, out var result);

            Assert.Null(output);
            Assert.Equal(TranscodeResult.FfmpegMissing, result);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static byte[] MinimalWav(int sampleRate, int channels, double freqHz, double durationSec)
    {
        int totalSamples = (int)(sampleRate * durationSec);
        int bytesPerSample = 2;
        int dataSize = totalSamples * channels * bytesPerSample;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bytesPerSample);
        w.Write((short)(channels * bytesPerSample));
        w.Write((short)(bytesPerSample * 8));
        w.Write("data"u8.ToArray());
        w.Write(dataSize);

        for (int i = 0; i < totalSamples; i++)
        {
            short sample = (short)(Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate) * 16384);
            for (int c = 0; c < channels; c++) w.Write(sample);
        }
        return ms.ToArray();
    }
}
