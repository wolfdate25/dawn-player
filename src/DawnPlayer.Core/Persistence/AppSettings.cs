using System.Text.Json.Serialization;

namespace DawnPlayer.Core.Persistence;

public enum RepeatMode { Off, All, One }
public enum ShuffleMode { Off = 0, Tracks = 1, Albums = 2 }
public enum ReplayGainMode { Off, Track, Album }
public enum ExclusiveBitDepth { Source, Bits16, Bits24, Bits32 }
public enum AudioDriverType { Wasapi = 0, DirectSound = 1, WaveOut = 2 }
public enum ThemeMode { System, Light, Dark, OledBlack }
public enum AccentColorPreset { EoleAmber, ElectricGold, ForestEmerald, CyanSapphire, CrimsonRed, ModernSlate, NordFrost, TokyoNight, CatppuccinMocha, RosePine, SunsetViolet, Custom }
public enum BackdropMode { Mica, MicaAlt, Acrylic, Solid, AlbumArtBlur }
public enum EqFilterType { PeakEq, LowShelf, HighShelf, LowPass, HighPass }
public enum NormalizerMode { Hybrid, AlwaysDynamic, ReplayGainOnly }
public enum NormalizerSpeed { Fast, Balanced, Smooth }

/// <summary>User settings, persisted as JSON under %AppData%\DawnPlayer.</summary>
public sealed class AppSettings
{
    public OutputSettings Output { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public NormalizerSettings Normalizer { get; set; } = new();
    public EqualizerSettings Equalizer { get; set; } = new();
    public LibrarySettings Library { get; set; } = new();
    public LyricsSettings Lyrics { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();

    public static AppSettings CreateDefault() => new();
}

public sealed class OutputSettings
{
    public AudioDriverType DriverType { get; set; } = AudioDriverType.Wasapi;
    /// <summary>Endpoint ID / GUID / Index, or null for default device.</summary>
    public string? DeviceId { get; set; }
    public bool UseExclusiveMode { get; set; } = true;
    public ExclusiveBitDepth ExclusiveBitDepth { get; set; } = ExclusiveBitDepth.Source;
    /// <summary>Requested buffer length in ms (higher = more resilient, higher latency).</summary>
    public int LatencyMs { get; set; } = 120;
    /// <summary>Allow digital volume/ReplayGain while in exclusive mode (breaks bit-perfect).</summary>
    public bool AllowVolumeInExclusive { get; set; } = false;
}

public sealed class QueueSavedEntry
{
    public string PlaylistName { get; set; } = "";
    public string TrackPath { get; set; } = "";
}

public sealed class PlaybackSettings
{
    public double Volume { get; set; } = 0.8;          // 0..1
    public ShuffleMode ShuffleMode { get; set; } = ShuffleMode.Off;

    [JsonIgnore]
    public bool Shuffle
    {
        get => ShuffleMode != ShuffleMode.Off;
        set => ShuffleMode = value ? (ShuffleMode == ShuffleMode.Off ? ShuffleMode.Tracks : ShuffleMode) : ShuffleMode.Off;
    }
    public bool StopAfterCurrent { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.All;
    public ReplayGainMode ReplayGain { get; set; } = ReplayGainMode.Off;
    public double ReplayGainPreampDb { get; set; } = 0; // -12..+12
    public bool ReplayGainPreventClipping { get; set; } = true;
    public string? ActivePlaylistName { get; set; }
    public string? LastPlayedTrackPath { get; set; }
    public string? LastPlayedPlaylistName { get; set; }
    public double LastPlayedPositionSeconds { get; set; }
    public List<QueueSavedEntry> QueueItems { get; set; } = new();
}

public sealed class LibrarySettings
{
    public List<string> Folders { get; set; } = new();
    public bool ScanOnStartup { get; set; } = true;
}

public sealed class LyricsSettings
{
    public List<string> FilePatterns { get; set; } = new()
    {
        "%filename%.lrc",
        "%artist% - %title%.lrc",
        "%title%.lrc"
    };
    /// <summary>Extra folders to search for lrc files. Files next to the track are always checked.</summary>
    public List<string> SearchFolders { get; set; } = new();

    // Typography Settings
    public string FontFamily { get; set; } = "Segoe UI Variable, Malgun Gothic";
    public double FontSize { get; set; } = 13.5;          // 10.0 .. 24.0 px
    public double ActiveFontSize { get; set; } = 16.5;    // 12.0 .. 32.0 px
    public int CharacterSpacing { get; set; } = 0;        // -50 .. 200 (1/1000 em)
    public double LineHeight { get; set; } = 24.0;        // 16.0 .. 48.0 px
    public double LineSpacing { get; set; } = 4.0;        // 0.0 .. 20.0 px
    public string Alignment { get; set; } = "Center";     // Center, Left, Right
    public bool BoldActiveLine { get; set; } = true;
    public bool EnableFocusEffect { get; set; } = true;
    public bool ReadEmbeddedLyrics { get; set; } = true;

    // Editor step preference
    public double DefaultOffsetStepMs { get; set; } = 0.5; // 0.5 ms default
}

/// <summary>
/// Persisted keyboard shortcut overrides. Only bindings that differ from
/// <c>ShortcutCommandCatalog</c> defaults are stored, so shipping a new default does not require a
/// settings migration. A key mapped to the empty string means "deliberately unassigned"; a command
/// absent from the dictionary falls back to its catalog default.
/// </summary>
public sealed class ShortcutSettings
{
    /// <summary>Command id (<c>ShortcutCommand</c> enum name) to chord token, e.g. "Ctrl+Shift+S".</summary>
    public Dictionary<string, string> Bindings { get; set; } = new();
}

public sealed class UiSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public AccentColorPreset AccentColor { get; set; } = AccentColorPreset.EoleAmber;
    public string CustomAccentHex { get; set; } = "#FFE8A33D";
    public bool AutoAlbumArtAccent { get; set; } = true;
    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    public bool ShowLyricsPane { get; set; }
    public string LastNavTab { get; set; } = "Library";
    public bool PlaylistGroupedView { get; set; } = true;

    // Eole Layout Customization
    public double LeftSidebarWidth { get; set; } = 220;
    public double RightSidebarWidth { get; set; } = 300;
    public double LyricsSidebarWidth { get; set; } = 300;
    public double AlbumCoverSize { get; set; } = 144;

    // Library View & Tree State Persistence
    public int LibraryTreeGroupMode { get; set; } = 0;
    public int LibraryViewMode { get; set; } = 0; // 0 = Grid, 1 = List
    public int LibrarySortColumn { get; set; } = 0;
    public bool LibrarySortAscending { get; set; } = true;
    public string? LibrarySelectedFilterType { get; set; }
    public string? LibrarySelectedFilterValue { get; set; }
    public string? LibrarySelectedFilterExtra { get; set; }
}

public sealed class EqualizerSettings
{
    /// <summary>
    /// Global master enable toggle for the parametric equalizer.
    /// When disabled, all device equalizer DSP stages are bypassed (bit-perfect unity gain).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Collection of user-defined named EQ profiles keyed by profile ID.
    /// </summary>
    public Dictionary<string, EqProfile> Profiles { get; set; } = new();

    /// <summary>
    /// ID of the global default profile used when a device does not have an explicit binding.
    /// </summary>
    public string DefaultProfileId { get; set; } = "default";

    /// <summary>
    /// Device-to-profile bindings keyed by canonical device identifier
    /// ("wasapi:{endpointId}", "dsound:{guid}", "waveout:{index}").
    /// Maps to a Profile ID in <see cref="Profiles"/>.
    /// </summary>
    public Dictionary<string, string> DeviceBindings { get; set; } = new();

    public void EnsureDefaultProfile()
    {
        if (Profiles.Count == 0)
        {
            DefaultProfileId = "default";
            Profiles["default"] = new EqProfile
            {
                Id = "default",
                Name = "기본 프로필 (Default)",
                Enabled = true,
                PreampDb = 0.0,
                Bands = new()
            };
        }
        else if (string.IsNullOrEmpty(DefaultProfileId) || !Profiles.ContainsKey(DefaultProfileId))
        {
            DefaultProfileId = Profiles.Keys.First();
        }
    }

    public EqualizerSettings Clone()
    {
        var clone = new EqualizerSettings
        {
            Enabled = Enabled,
            DefaultProfileId = DefaultProfileId,
            DeviceBindings = new Dictionary<string, string>(DeviceBindings)
        };
        foreach (var kvp in Profiles)
        {
            clone.Profiles[kvp.Key] = kvp.Value.Clone();
        }
        clone.EnsureDefaultProfile();
        return clone;
    }
}

public sealed class EqProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 프로필";
    public bool Enabled { get; set; } = true;
    public double PreampDb { get; set; } = 0;          // -12..+12
    public List<EqBandSettings> Bands { get; set; } = new();

    public EqProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        PreampDb = PreampDb,
        Bands = Bands.Select(b => b.Clone()).ToList()
    };
}

public sealed class EqBandSettings
{
    public EqFilterType Type { get; set; } = EqFilterType.PeakEq;
    public double FrequencyHz { get; set; } = 1000;    // 20..20000
    public double GainDb { get; set; } = 0;            // -15..+15
    public double Q { get; set; } = 1.0;               // 0.1..8

    public EqBandSettings Clone() => new()
    {
        Type = Type,
        FrequencyHz = FrequencyHz,
        GainDb = GainDb,
        Q = Q
    };
}

public sealed class NormalizerSettings
{
    public bool Enabled { get; set; } = false;
    public NormalizerMode Mode { get; set; } = NormalizerMode.Hybrid;
    public double TargetLevelDb { get; set; } = -12.0; // -24.0 .. -6.0 dBFS
    public double MaxBoostDb { get; set; } = 12.0;     // 0.0 .. 18.0 dB
    public NormalizerSpeed Speed { get; set; } = NormalizerSpeed.Balanced;

    public NormalizerSettings Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        TargetLevelDb = TargetLevelDb,
        MaxBoostDb = MaxBoostDb,
        Speed = Speed
    };
}


