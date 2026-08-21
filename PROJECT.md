# Project: Dawn Player Residual Modularization & Clean Code Refactoring

> **[역사적 문서]** 완료된 리팩토링 마일스톤(M1–M5)의 원본 계획서입니다.
> 현재 아키텍처와 기능 목록은 [README.md](README.md), 작업 규약은 [CLAUDE.md](CLAUDE.md)를 참조하세요.

## Architecture
Dawn Player is a high-performance Windows audio player built with WinUI 3 (Windows App SDK) and .NET 8.
- `src/DawnPlayer.Core`: Pure .NET class library containing audio decoding, TagLib metadata extraction, LRC lyrics parsing, DSP pipelines, playback queue, and playlist management.
- `src/DawnPlayer.App`: WinUI 3 desktop application containing UI controls (`NowPlayingBar`, `LyricsPane`, `SettingsPage`), application services (`AudioSettingsService`, `AppearanceSettingsService`, `SmtcService`), and MVVM wiring.
- `tests/DawnPlayer.Tests`: xUnit test project covering core algorithms, settings persistence, converters, and UI helpers without WinUI UI thread dependencies.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Polymorphic Tag & ReplayGain Extraction | Extract VorbisComment, ID3v2, Apple MP4, and APE ReplayGain tags in `TagReader.cs` | M1 | ORIGINAL_REQUEST §R3 |
| 2 | Thread-Safe Album Art Caching | Prevent concurrent `File.WriteAllBytes` collisions and fix untagged album key hashing | M1 | ORIGINAL_REQUEST §R3 |
| 3 | Robust LRC Parsing & BOM Detection | 2-pass `[offset:]` tag processing, `[hh:mm:ss.xx]` format support, UTF-8/UTF-16 LE/BE BOM detection | M1 | ORIGINAL_REQUEST §R3 |
| 4 | Clean DSP Pipeline & Soft Limiter | Extract `ReplayGainMath`, add `SoftLimiterSampleProvider` anti-clipping node | M1 | ORIGINAL_REQUEST §R3 |
| 5 | SMTC Service & Async Thumbnail Stream | Implement `ISmtcService`, connect playback events, async thumbnail loader via `StorageFile`, thread-safe button dispatch | M2 | ORIGINAL_REQUEST §R4 |
| 6 | Audio Settings Service Extraction | Extract `IAudioSettingsService` / `AudioSettingsService` (device enumeration, WASAPI exclusive checks, latency, ReplayGain) | M3 | ORIGINAL_REQUEST §R1 |
| 7 | Appearance Settings Service Extraction | Extract `IAppearanceSettingsService` / `AppearanceSettingsService` (theme, backdrop, accent preset, font scale, layout reset) | M3 | ORIGINAL_REQUEST §R1 |
| 8 | SettingsPage View Slimming | Slim `SettingsPage.xaml.cs` to pure view & binding layer | M3 | ORIGINAL_REQUEST §R1 |
| 9 | Queue Popup Controller | Extract `QueuePopupController` (data mapping, 1-based indexing, "99+" badge formatting, list operations) | M4 | ORIGINAL_REQUEST §R2 |
| 10 | Seekbar Scrubbing Calculator | Extract `SeekbarScrubbingCalculator` (dragging state, progress clamping, elapsed/remaining formatting) | M4 | ORIGINAL_REQUEST §R2 |
| 11 | Audio Format Badge Formatter | Extract `AudioFormatBadgeFormatter` (codec detection, WASAPI exclusive vs shared session mode formatting) | M4 | ORIGINAL_REQUEST §R2 |
| 12 | Lyrics Scroll Synchronizer | Extract `LyricsScrollSynchronizer` (binary search active line lookup, offset stepping/clamping, seek target calculation) | M4 | ORIGINAL_REQUEST §R2 |
| 13 | Solution Build & Test Suite Expansion | Build with 0 errors/warnings, expand test suite with new unit tests, achieve 100% pass (>368 tests) | M5 | Acceptance Criteria |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | M1: Core DSP, TagReader & LrcParser | Refactor `TagReader`, `LrcParser`, `SampleProviders`, `ReplayGainMath`, `SoftLimiterSampleProvider` + tests | None | DONE |
| 2 | M2: SMTC & Background Services | Refactor `SmtcService`, implement `ISmtcService`, wire playback events, async thumbnail stream + tests | M1 | DONE |
| 3 | M3: SettingsPage Refactor & Services | Extract `AudioSettingsService`, `AppearanceSettingsService`, slim `SettingsPage.xaml.cs` + tests | M1, M2 | DONE |
| 4 | M4: NowPlayingBar & LyricsPane Modularization | Extract `QueuePopupController`, `SeekbarScrubbingCalculator`, `AudioFormatBadgeFormatter`, `LyricsScrollSynchronizer` + tests | M1, M3 | DONE |
| 5 | M5: E2E Integration & Acceptance Hardening | Solution build verification (0 err, 0 warn), full test execution (100% pass), adversarial review, victory audit | M1, M2, M3, M4 | DONE |

## Interface Contracts

### M1 ↔ Core / Audio
- `ReplayGainMath.ComputeGain(Track? track, double volume, ReplayGainMode mode, double preampDb, bool preventClipping) -> float`
- `TagReader.TryRead(string path, out TagLib.IPicture? embeddedArt) -> Track?`
- `TagReader.ComputeAlbumKey(Track track) -> string`
- `LrcParser.Parse(string text) -> LyricsDocument`
- `LrcParser.ParseFile(string path) -> LyricsDocument?`
- `SoftLimiterSampleProvider : ISampleProvider`

### M2 ↔ App / SMTC
- `ISmtcService : IDisposable`
  - `bool IsInitialized { get; }`
  - `bool TryInitialize(IntPtr hwnd)`
  - `void UpdateTrack(PlaylistItem? item)`
  - `Task UpdateTrackAsync(PlaylistItem? item, CancellationToken ct = default)`
  - `void UpdateState(PlaybackState state)`
  - `void UpdateTimeline(TimeSpan position, TimeSpan duration)`

### M3 ↔ App / Settings
- `IAudioSettingsService`
  - `IReadOnlyList<OutputDeviceInfo> GetDevices(AudioDriverType driverType)`
  - `OutputDeviceInfo? GetSelectedDevice(AudioDriverType driverType, string? deviceId)`
  - `ExclusiveModeStatus GetExclusiveModeStatus(string? deviceId)`
  - `void SetDriverType(AudioDriverType driverType)`
  - `void SetDevice(string? deviceId)`
  - `void SetUseExclusiveMode(bool useExclusive)`
  - `void SetExclusiveBitDepth(ExclusiveBitDepth bitDepth)`
  - `void SetLatency(int latencyMs)`
  - `void SetAllowVolumeInExclusive(bool allow)`
  - `void SetReplayGain(ReplayGainMode mode, double preampDb, bool preventClipping)`
  - `void OpenSoundControlPanel()`
- `IAppearanceSettingsService`
  - `event Action? AppearanceChanged`
  - `void SetTheme(ThemeMode theme)`
  - `void SetAccentColor(AccentColorPreset preset)`
  - `void SetBackdrop(BackdropMode backdrop)`
  - `void SetFontScale(double scale)`
  - `void SetAlbumCoverSize(double size)`
  - `void ResetLayoutToDefaults()`

### M4 ↔ App / Controls
- `QueuePopupController`:
  - `ObservableCollection<QueueUiEntry> Entries { get; }`
  - `void SyncFromQueue(IReadOnlyList<QueueEntry> queueEntries)`
  - `static string FormatBadgeText(int count)`
  - `static bool ShouldShowBadge(int count)`
  - `void RequestClear(PlaybackQueue? queue)`
  - `void RequestRemoveAt(PlaybackQueue? queue, int oneBasedIndex)`
- `SeekbarScrubbingCalculator`:
  - `bool IsDragging { get; }`
  - `void BeginDrag()`
  - `TimeSpan? CompleteDrag(double currentSliderValue, TimeSpan duration)`
  - `static (bool UpdateMax, double NewMax, double NewValue) CalculateSliderProgress(TimeSpan position, TimeSpan duration, double currentSliderMax, bool isDragging)`
  - `static (double ClampedMax, double ClampedValue, string Elapsed, string Remaining) CalculateRestoreState(double seconds, double maxSeconds)`
- `AudioFormatBadgeFormatter`:
  - `static string GetCodec(string? codec, string? filePath)`
  - `static string GetSessionMode(bool exclusive)`
  - `static string FormatBadgeText(Track? track, SessionInfo? sessionInfo)`
  - `static bool IsBadgeVisible(string? badgeText)`
- `LyricsScrollSynchronizer`:
  - `static int FindActiveLineIndex(IReadOnlyList<LrcLineVm> lines, TimeSpan playbackPosition, double offsetMs)`
  - `static double StepOffset(double currentOffsetMs, double deltaMs)`
  - `static string FormatOffsetLabel(double offsetMs)`
  - `static TimeSpan CalculateSeekTarget(TimeSpan lineTime, double offsetMs)`
  - `static bool UpdateActiveLineState(IReadOnlyList<LrcLineVm> lines, ref int currentIndex, int targetIndex)`

## Code Layout
- `src/DawnPlayer.Core/Library/TagReader.cs`
- `src/DawnPlayer.Core/Lyrics/LrcParser.cs`
- `src/DawnPlayer.Core/Audio/SampleProviders.cs`
- `src/DawnPlayer.Core/Audio/ReplayGainMath.cs`
- `src/DawnPlayer.Core/Audio/SequencerStream.cs`
- `src/DawnPlayer.Core/Audio/PlaybackController.cs`
- `src/DawnPlayer.App/Services/ISmtcService.cs`
- `src/DawnPlayer.App/Services/SmtcService.cs`
- `src/DawnPlayer.App/Services/IAudioSettingsService.cs`
- `src/DawnPlayer.App/Services/AudioSettingsService.cs`
- `src/DawnPlayer.App/Services/IAppearanceSettingsService.cs`
- `src/DawnPlayer.App/Services/AppearanceSettingsService.cs`
- `src/DawnPlayer.App/Services/AppServices.cs`
- `src/DawnPlayer.App/Views/SettingsPage.xaml`
- `src/DawnPlayer.App/Views/SettingsPage.xaml.cs`
- `src/DawnPlayer.App/Controls/QueuePopupController.cs`
- `src/DawnPlayer.App/Controls/SeekbarScrubbingCalculator.cs`
- `src/DawnPlayer.App/Controls/AudioFormatBadgeFormatter.cs`
- `src/DawnPlayer.App/Controls/LyricsScrollSynchronizer.cs`
- `src/DawnPlayer.App/Controls/NowPlayingBar.xaml`
- `src/DawnPlayer.App/Controls/NowPlayingBar.xaml.cs`
- `src/DawnPlayer.App/Controls/LyricsPane.xaml`
- `src/DawnPlayer.App/Controls/LyricsPane.xaml.cs`
- `tests/DawnPlayer.Tests/TagReaderAndCodecDetectionTests.cs`
- `tests/DawnPlayer.Tests/LrcParserTests.cs`
- `tests/DawnPlayer.Tests/PcmConvertAndSampleProviderTests.cs`
- `tests/DawnPlayer.Tests/ReplayGainAndVolumeMathTests.cs`
- `tests/DawnPlayer.Tests/SmtcServiceLifecycleAndMappingTests.cs`
- `tests/DawnPlayer.Tests/AudioSettingsServiceTests.cs`
- `tests/DawnPlayer.Tests/AppearanceSettingsServiceTests.cs`
- `tests/DawnPlayer.Tests/PlaybackUiHelperTests.cs`
