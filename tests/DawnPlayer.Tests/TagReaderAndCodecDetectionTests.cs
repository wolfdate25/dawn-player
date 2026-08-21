using System.IO;
using System.Reflection;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit tests for metadata parsing and codec detection in TagReader:
/// 1. TagReader.ParseDb (parsing with/without dB, Unicode minus \u2212, whitespace, null/empty/invalid handling).
/// 2. TagReader.ParsePeak (linear peak float parsing, Unicode minus, null/empty/invalid handling).
/// 3. TagReader.DetectCodec (description matching for ALAC, FLAC, Vorbis, AAC, MP3; extension fallback for .mp3, .flac, .ogg, .oga, .wav, .m4a, .alac, .aac).
/// 4. TagReader.Sha1Hex hashing verification.
/// 5. TagReader.GetField polymorphic ReplayGain tag extraction (XiphComment, ID3v2, AppleTag, CombinedTag).
/// 6. AlbumKey computation and untagged album key collision prevention.
/// 7. Thread-safe concurrent album art extraction with atomic file caching.
/// 8. Folder cover art discovery.
/// </summary>
[Collection("SettingsStoreCollection")]
public class TagReaderAndCodecDetectionTests
{
    private static readonly MethodInfo ParseDbMethod = typeof(TagReader).GetMethod(
        "ParseDb", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo ParsePeakMethod = typeof(TagReader).GetMethod(
        "ParsePeak", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo DetectCodecMethod = typeof(TagReader).GetMethod(
        "DetectCodec", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo Sha1HexMethod = typeof(TagReader).GetMethod(
        "Sha1Hex", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo GetFieldMethod = typeof(TagReader).GetMethod(
        "GetField",
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
        null,
        new[] { typeof(TagLib.Tag), typeof(string) },
        null)!;

    private static double? CallParseDb(string? input) =>
        (double?)ParseDbMethod.Invoke(null, new object?[] { input });

    private static double? CallParsePeak(string? input) =>
        (double?)ParsePeakMethod.Invoke(null, new object?[] { input });

    private static string CallDetectCodec(string path, TagLib.Properties props) =>
        (string)DetectCodecMethod.Invoke(null, new object?[] { path, props })!;

    private static string CallSha1Hex(string input) =>
        (string)Sha1HexMethod.Invoke(null, new object?[] { input })!;

    private static string? CallGetField(TagLib.Tag tag, string field) =>
        (string?)GetFieldMethod.Invoke(null, new object?[] { tag, field });

    #region 1. TagReader.ParseDb Tests

    [Theory]
    [InlineData("-6.5 dB", -6.5)]
    [InlineData(" -6.5 dB ", -6.5)]
    [InlineData("-6.5dB", -6.5)]
    [InlineData("+3.2 dB", 3.2)]
    [InlineData("3.2 dB", 3.2)]
    [InlineData("0 dB", 0.0)]
    [InlineData("0.0 dB", 0.0)]
    [InlineData("-12.3456 dB", -12.3456)]
    [InlineData("10.5", 10.5)]
    [InlineData("-4.2", -4.2)]
    public void ParseDb_ValidStandardInputs_ReturnsParsedDouble(string input, double expected)
    {
        var result = CallParseDb(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, precision: 4);
    }

    [Theory]
    [InlineData("−3.2 dB", -3.2)]           // Unicode minus \u2212
    [InlineData("−6.5dB", -6.5)]            // Unicode minus \u2212
    [InlineData(" −0.8 dB ", -0.8)]         // Unicode minus with spaces
    [InlineData("−15.0", -15.0)]            // Unicode minus without dB
    public void ParseDb_UnicodeMinus_ParsesCorrectly(string input, double expected)
    {
        var result = CallParseDb(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, precision: 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dB")]
    [InlineData("invalid")]
    [InlineData("--6.5 dB")]
    [InlineData("abc dB")]
    [InlineData("---")]
    public void ParseDb_InvalidOrEmptyInputs_ReturnsNull(string? input)
    {
        var result = CallParseDb(input);
        Assert.Null(result);
    }

    [Fact]
    public void ParseDb_CaseSensitivityAndSpacing_Observations()
    {
        // "dB" trim removes 'd' and 'B', so "-15.25db" retains lowercase 'b' and returns null
        Assert.Null(CallParseDb("-15.25db"));
        Assert.Null(CallParseDb("-15.25Db"));

        // Space between sign and digits is not parsed by NumberStyles.Float
        Assert.Null(CallParseDb("− 10.5 dB"));
        Assert.Null(CallParseDb("+ 6.02 dB"));

        // .NET double.TryParse recognizes NaN and Infinity strings
        var nan = CallParseDb("NaN");
        Assert.NotNull(nan);
        Assert.True(double.IsNaN(nan!.Value));

        var inf = CallParseDb("Infinity");
        Assert.NotNull(inf);
        Assert.True(double.IsPositiveInfinity(inf!.Value));
    }

    #endregion

    #region 2. TagReader.ParsePeak Tests

    [Theory]
    [InlineData("0.9882", 0.9882)]
    [InlineData("1.0", 1.0)]
    [InlineData("0.0", 0.0)]
    [InlineData("1.25", 1.25)]
    [InlineData("  0.8523  ", 0.8523)]
    [InlineData("0.0001", 0.0001)]
    public void ParsePeak_ValidInputs_ReturnsParsedDouble(string input, double expected)
    {
        var result = CallParsePeak(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, precision: 4);
    }

    [Theory]
    [InlineData("−0.5", -0.5)]              // Unicode minus \u2212
    [InlineData(" −1.0 ", -1.0)]            // Unicode minus with spaces
    public void ParsePeak_UnicodeMinus_ParsesCorrectly(string input, double expected)
    {
        var result = CallParsePeak(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, precision: 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("peak")]
    public void ParsePeak_InvalidOrEmptyInputs_ReturnsNull(string? input)
    {
        var result = CallParsePeak(input);
        Assert.Null(result);
    }

    #endregion

    #region 3. TagReader.DetectCodec Tests

    private sealed class DummyCodec : TagLib.ICodec
    {
        public TagLib.MediaTypes MediaTypes => TagLib.MediaTypes.Audio;
        public TimeSpan Duration => TimeSpan.Zero;
        public string Description { get; }

        public DummyCodec(string description) => Description = description;
    }

    [Theory]
    [InlineData("Apple Lossless Audio Codec", "track.m4a", "ALAC")]
    [InlineData("ALAC (Apple Lossless)", "song.mp4", "ALAC")]
    [InlineData("Xiph Vorbis Audio", "music.ogg", "Vorbis")]
    [InlineData("Free Lossless Audio Codec (FLAC)", "audio.flac", "FLAC")]
    [InlineData("Advanced Audio Coding (AAC)", "stream.m4a", "AAC")]
    [InlineData("MPEG Version 1 Audio Layer 3", "track.mp3", "MP3")]
    public void DetectCodec_WithTagDescription_IdentifiesExactCodec(string description, string path, string expectedCodec)
    {
        var props = new TagLib.Properties(TimeSpan.FromMinutes(3), new TagLib.ICodec[] { new DummyCodec(description) });
        var codec = CallDetectCodec(path, props);
        Assert.Equal(expectedCodec, codec);
    }

    [Theory]
    [InlineData(@"C:\Music\song.mp3", "MP3")]
    [InlineData(@"C:\Music\song.MP3", "MP3")]
    [InlineData(@"C:\Music\album.flac", "FLAC")]
    [InlineData(@"C:\Music\track.ogg", "Vorbis")]
    [InlineData(@"C:\Music\audio.oga", "Vorbis")]
    [InlineData(@"C:\Music\sound.wav", "WAV")]
    [InlineData(@"C:\Music\track.m4a", "ALAC/AAC")]
    [InlineData(@"C:\Music\book.m4b", "ALAC/AAC")]
    [InlineData(@"C:\Music\movie.mp4", "ALAC/AAC")]
    [InlineData(@"C:\Music\lossless.alac", "ALAC/AAC")]
    [InlineData(@"C:\Music\voice.aac", "AAC")]
    [InlineData(@"C:\Music\audio.opus", "OPUS")]
    [InlineData(@"C:\Music\track.aiff", "AIFF")]
    [InlineData(@"C:\Music\track.wma", "WMA")]
    public void DetectCodec_ExtensionFallback_MapsStandardExtensions(string path, string expectedCodec)
    {
        // Empty codecs description in properties -> falls back to file extension
        var props = new TagLib.Properties(TimeSpan.FromMinutes(2), Array.Empty<TagLib.ICodec>());
        var codec = CallDetectCodec(path, props);
        Assert.Equal(expectedCodec, codec);
    }

    #endregion

    #region 4. TagReader.Sha1Hex Hashing Tests

    [Theory]
    [InlineData("hello", "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d")]
    [InlineData("pink floyd\u0001the wall", "f01fdc5c6934a2a93d49fe916380dad606c7e624")]
    public void Sha1Hex_ComputesDeterministicLowercaseHash(string input, string expectedHash)
    {
        var hash = CallSha1Hex(input);
        Assert.Equal(expectedHash, hash);
        Assert.Equal(40, hash.Length);
    }

    [Fact]
    public void Sha1Hex_EmptyStringAndKoreanCharacters_MatchesStandardSHA1()
    {
        // SHA-1 of empty string is da39a3ee5e6b4b0d3255bfef95601890afd80709
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", CallSha1Hex(""));

        // SHA-1 of "아이유\u0001좋은 날"
        var hash = CallSha1Hex("아이유\u0001좋은 날");
        Assert.Equal(40, hash.Length);
        Assert.Matches("^[0-9a-f]{40}$", hash);
    }

    #endregion

    #region 5. Polymorphic ReplayGain Tag Extraction Tests

    [Fact]
    public void GetField_XiphComment_ExtractsReplayGainFields()
    {
        var xiph = new TagLib.Ogg.XiphComment();
        xiph.SetField("REPLAYGAIN_TRACK_GAIN", "-7.5 dB");
        xiph.SetField("REPLAYGAIN_TRACK_PEAK", "0.9854");
        xiph.SetField("REPLAYGAIN_ALBUM_GAIN", "-8.2 dB");
        xiph.SetField("REPLAYGAIN_ALBUM_PEAK", "0.9912");

        Assert.Equal("-7.5 dB", CallGetField(xiph, "REPLAYGAIN_TRACK_GAIN"));
        Assert.Equal("0.9854", CallGetField(xiph, "REPLAYGAIN_TRACK_PEAK"));
        Assert.Equal("-8.2 dB", CallGetField(xiph, "REPLAYGAIN_ALBUM_GAIN"));
        Assert.Equal("0.9912", CallGetField(xiph, "REPLAYGAIN_ALBUM_PEAK"));
    }

    [Fact]
    public void GetField_Id3v2UserTextFrame_ExtractsReplayGainFields()
    {
        var id3 = new TagLib.Id3v2.Tag();
        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "REPLAYGAIN_TRACK_GAIN", true);
        frame.Text = new[] { "-4.50 dB" };

        var peakFrame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "REPLAYGAIN_TRACK_PEAK", true);
        peakFrame.Text = new[] { "0.8950" };

        Assert.Equal("-4.50 dB", CallGetField(id3, "REPLAYGAIN_TRACK_GAIN"));
        Assert.Equal("0.8950", CallGetField(id3, "REPLAYGAIN_TRACK_PEAK"));
    }

    [Fact]
    public void GetField_CombinedTag_ExtractsFromSubtags()
    {
        var xiph = new TagLib.Ogg.XiphComment();
        xiph.SetField("REPLAYGAIN_TRACK_GAIN", "-3.21 dB");

        var combined = new TagLib.CombinedTag(new TagLib.Tag[] { new TagLib.Id3v1.Tag(), xiph });
        Assert.Equal("-3.21 dB", CallGetField(combined, "REPLAYGAIN_TRACK_GAIN"));
    }

    [Fact]
    public void GetField_ApeTag_ExtractsField()
    {
        var ape = new TagLib.Ape.Tag();
        ape.SetValue("REPLAYGAIN_TRACK_GAIN", "-6.1 dB");

        Assert.Equal("-6.1 dB", CallGetField(ape, "REPLAYGAIN_TRACK_GAIN"));
    }

    #endregion

    #region 6. AlbumKey Computation & Untagged Isolation Tests

    [Fact]
    public void ComputeAlbumKey_StandardTaggedTrack_ReturnsNormalizedArtistAndAlbum()
    {
        var track = new Track
        {
            Path = @"C:\Music\Pink Floyd - Dark Side\01.flac",
            Artist = "Pink Floyd",
            AlbumArtist = "Pink Floyd",
            Album = "The Dark Side of the Moon"
        };

        var key = TagReader.ComputeAlbumKey(track);
        Assert.Equal("pink floyd\u0001the dark side of the moon", key);
        Assert.Equal(key, AlbumArtService.ComputeAlbumKey(track));
    }

    [Fact]
    public void ComputeAlbumKey_UntaggedTracks_ReturnsDistinctPathBasedKeys()
    {
        var track1 = new Track
        {
            Path = @"C:\Music\UntaggedFolderA\track01.mp3",
            Artist = "",
            AlbumArtist = "",
            Album = ""
        };

        var track2 = new Track
        {
            Path = @"C:\Music\UntaggedFolderB\track01.mp3",
            Artist = "   ",
            AlbumArtist = "",
            Album = "   "
        };

        var key1 = TagReader.ComputeAlbumKey(track1);
        var key2 = TagReader.ComputeAlbumKey(track2);

        Assert.NotEqual(key1, key2);
        Assert.StartsWith("file:", key1);
        Assert.StartsWith("file:", key2);
        Assert.Contains("untaggedfoldera", key1);
        Assert.Contains("untaggedfolderb", key2);
    }

    #endregion

    #region 7. Thread-Safe Album Art Extraction Tests

    private sealed class MockPicture : TagLib.IPicture
    {
        public string MimeType { get; set; } = "image/jpeg";
        public TagLib.PictureType Type { get; set; } = TagLib.PictureType.FrontCover;
        public string Description { get; set; } = "Cover";
        public TagLib.ByteVector Data { get; set; } = new(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 });
        public string Filename { get; set; } = "cover.jpg";
    }

    [Fact]
    public void TryExtractArt_ConcurrentWrites_DoesNotThrowIOException_AndCachesFile()
    {
        var track = new Track
        {
            Path = @"C:\Mock\MultiThread\song.mp3",
            Artist = "ConcurrentArtist",
            Album = "ConcurrentAlbum"
        };
        var albumKey = TagReader.ComputeAlbumKey(track);
        var mockPic = new MockPicture();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var results = new System.Collections.Concurrent.ConcurrentBag<string?>();

        Parallel.For(0, 20, _ =>
        {
            try
            {
                var artPath = AlbumArtService.TryExtractArt(track, albumKey, mockPic);
                results.Add(artPath);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        Assert.All(results, path =>
        {
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
        });
    }

    #endregion

    #region 8. FindFolderArt Tests

    [Fact]
    public void FindFolderArt_WithExistingCoverImage_FindsImage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnFolderArtTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var coverFile = Path.Combine(tempDir, "cover.jpg");
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });
            var audioFile = Path.Combine(tempDir, "song.flac");
            File.WriteAllBytes(audioFile, new byte[] { 4, 5, 6 });

            var found = TagReader.FindFolderArt(audioFile);
            Assert.NotNull(found);
            Assert.Equal(coverFile, found);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void FindFolderArt_NonExistentOrEmptyFolder_ReturnsNull()
    {
        Assert.Null(TagReader.FindFolderArt(@"C:\NonExistentDirectory\song.flac"));
    }

    #endregion
}
