# Dawn Player

<p align="right"><b>English</b> · <a href="README.ko.md">한국어</a> · <a href="README.zh-CN.md">简体中文</a></p>

A native Windows music player (WinUI 3 / .NET 10) inspired by the functionality of foobar2000
and the design of the [Eole theme](https://github.com/Ottodix/Eole-foobar-theme).

![release](https://img.shields.io/github/v/tag/wolfdate25/dawn-player?label=release&color=blue)

![Dawn Player Screenshot](docs/screenshot.png)

## Features

### Audio engine
- **WASAPI exclusive output** — bit-perfect direct output. Falls back to shared mode
  automatically (with a notification) when the device does not support the format or another
  app is holding the device
- **Gapless playback** — a sequencer where track boundaries continue at sample precision. The
  next track's decoder is opened in advance (prefetch) and chained without a gap
- Exclusive-mode sample-rate mismatch policy — rebuild the output session at the track boundary
  (bit-perfect) or keep the session running and resample (seamless), as you prefer
- **Internet radio** — Icecast/Shoutcast MP3 streaming with station/song info from ICY metadata.
  Title menu → Open network stream; URLs inside M3U8 playlists work as-is
- **Supported formats**: MP3, AAC/ALAC (m4a), FLAC, Ogg Vorbis, Opus, WAV, DSF/DSD, DFF (DSDIFF)
  — Media Foundation + NVorbis decoding. DSD plays as boxcar-decimated 44.1k/48k-family PCM, or
  as DoP (DSD over PCM) packing to a DSD-capable DAC over WASAPI exclusive
- **CUE sheet support** — scanning an album image (FLAC/WAV/APE etc.) plus its `.cue` indexes
  track-level virtual tracks so per-track playback, statistics and ratings work, while the
  whole-image row is hidden (the foobar2000 way). Range playback continues on the gapless
  sequencer at sample precision
- Exclusive-mode bit-depth policy (source/16/24/32-bit), latency buffer adjustment (30–500 ms)
- **Parametric equalizer (per-device profiles)** — up to 8 bands of dynamic filters (peak EQ,
  low/high shelf, low/high pass), preamp (±12 dB), per-device profiles with a common default
  fallback, live apply during playback and bit-perfect bypass
- **ReplayGain** (track/album gain, preamp, clipping prevention) — FLAC/OGG (Xiph) and MP3
  (ID3v2 TXXX) tags
- **Convolution (impulse response)** — room/speaker/headphone correction with impulse files
  (WAV/FLAC etc.). FFT-partition convolution (512-sample blocks, IR up to 2 s) applies and swaps
  live during playback (Settings → Playback → Spatial listening)
- **Dynamic volume normalizer (AGC)** — automatic loudness leveling between tracks. A hybrid
  mode uses ReplayGain tags when present and falls back to dynamic AGC otherwise, with target
  level/max boost/reaction speed controls and silence gating
- **Headphone crossfeed** (Chu Moy phase, weak/medium/strong) and **mono downmix** — applied
  live during playback

### Playlists / queue (foobar2000 style)
- Multiple playlists (tabs), renaming, M3U8 auto-save / import / export
- **Smart playlists** — play statistics (plays, last played, skips) recorded in SQLite generate
  and refresh "Most played / Recently added / Not played lately" lists automatically (updated on
  every play, no settings to touch)
- **Query-based smart playlists** — build and edit smart playlists directly with foobar2000-style
  queries (`%rating% GREATER 3 AND %last_played% DURING LAST 30 DAYS LIMIT 50`). Strings
  (IS/HAS), numbers (GREATER, >=), dates (DURING LAST), MISSING/PRESENT, AND/OR/NOT, parentheses
- **Track ratings** — 0–5 stars, shown in playlist rows and set from the context menu (batch for
  multi-selection), synchronized both ways with ID3v2 POPM / Vorbis·MP4 RATING tags, usable in
  queries as `%rating%`
- **Playback queue** — add/add-next per track, queue-centered playback then return to the
  original order. Q1, Q2… badges on list rows
- Shuffle, repeat (off/all/one), previous-track history, **A-B repeat** (sample-tight loop on the
  audio thread, keybindable)
- Sorting (title/artist/album·track/path/random/reverse), duplicate removal, group-by toggle +
  drag reordering in flat mode

### Library
- Music folder indexing (SQLite, incremental scan), artist/album/genre filter browser + search
- **WAV converter** — export playlist tracks to WAV (CUE image splitting, ReplayGain baking,
  tags and artwork included)
- **Tag editor** — edit metadata and album art from the track/album context menu, atomic
  (file-replace) saving
- **ReplayGain batch analysis** — an EBU R128 loudness scanner (−18 LUFS) computes per-track and
  per-album gains, writes them to tags (REPLAYGAIN_*) and the database, and feeds volume
  normalization immediately. **ReplayGain 2.0 tags** (R128_TRACK_GAIN / R128_ALBUM_GAIN) can be
  written optionally, with read fallback
- Album art cache (embedded image extraction plus cover.jpg folder art)
- **Cover grid / list table view toggle** — grid tile resizing (Ctrl+wheel) with size
  persistence
- **Listening report** — play history (play_events) powers a dashboard of totals, listening
  time and top artists/albums/genres/tracks per period (all/last 30 days/this year)
  (title menu → Listening report)

### Lyrics
- **.lrc synced lyrics** — automatic lookup (`<filename>.lrc`, `artist - title.lrc`, `title.lrc`
  and custom patterns), current-line highlight + auto-scroll, click-to-seek, ±0.5 s offset
  adjustment
- **Online lyrics plugins** — per-site lyric search via .NET DLL plugins (dev guide:
  `docs/plugin-development.md`). Plugin priorities, auto-search during playback (offline lyrics
  first), title/artist/album search window, preview before apply, saving online lyrics as
  offline .lrc — with save location (source folder/custom folder) and file-name templates
  (`%title%` etc.)
- Standard/multi-timestamp/`[offset:]`/extended (word-level) LRC support, UTF-8/ANSI auto
  detection
- **LRC lyrics editor** — per-line timestamp editing/syncing, line reordering, clipboard import

### UI (Eole style × WinUI 3)
- Dark-first palette + Mica backdrop, amber accent, light theme toggle
- Multilingual UI — Korean / English / 日本語. Pick the language in Settings → Appearance & theme
  (system default supported); the whole UI shows it after a restart
  (`Strings/<culture>/Resources.resw` + MRT Core)
- Playlists with album group headers (art + metadata) — Eole's signature layout
- Bottom player bar: album art, seek, transport, volume, queue badge, lyrics toggle
- SMTC integration — media keys / OS media overlay / volume popup control
- 17 shortcuts — playback (`Space` play/pause, `Ctrl+S` stop, `Ctrl+←/→` previous/next,
  `Ctrl+H` shuffle, `Ctrl+R` repeat, `Ctrl+Shift+S` stop after track), navigation (`←/→` ±5 s,
  `Ctrl+B` start of track), volume (`Ctrl+↑/↓`, `Ctrl+M` mute), windows (`L` lyrics, `Ctrl+F`
  search, `Ctrl+P` settings)
- Shortcut rebinding: remap every command in Settings → Shortcuts (conflict detection,
  per-command/global reset); only overrides are stored in `settings.json`
- **Sleep timer** — stop playback after 15/30/60 minutes or at the end of the current track
  (title menu)
- **Mini player** — always-on-top compact mode from the title menu (player bar only, drag the
  background to move, Esc to exit)
- **Tray toast** — a balloon notification when the track changes while hidden to the tray
  (respects Focus Assist)
- **Last.fm scrobbling** — web auth with your own API keys; failed scrobbles queue and retry
  (title menu → Last.fm)
- **System tray integration** — "close to tray" hides the window and keeps playing; tray menu
  for playback/window/exit; taskbar thumbnail toolbar (previous/play/next)
- Drag & drop files/folders onto the playlist

## Build and run

Requirements: .NET SDK 10 (pinned by `global.json` — the `.slnx` solution format needs SDK
9.0.2xx or later), Windows 10 19041+

```powershell
dotnet build DawnPlayer.slnx -c Debug
# run
src/DawnPlayer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/DawnPlayer.App.exe
```

Builds unpackaged (non-MSIX) with the Windows App SDK self-contained, so the exe runs without
installing a separate runtime.

### Deployment packages and installer

The Inno Setup 6 based installer (`Setup.exe`) and a portable ZIP archive are built
automatically.

```powershell
# install Inno Setup 6 (once)
winget install --id JRSoftware.InnoSetup

# build the installer and portable ZIP (with a version)
pwsh -File tools/build-installer.ps1 -Version "1.0.0"
```

- **Installer (`.exe`)**: `dist/installer/DawnPlayer-Setup-v1.0.0-x64.exe`
  - Per-user (default) and per-machine installation
  - Shortcuts, startup entry, audio file associations (.mp3, .flac, .wav, .m4a etc.), Explorer
    "play with" context menu
  - Detects running processes and supports safe upgrades/uninstalls
- **Portable archive (`.zip`)**: `dist/DawnPlayer-v1.0.0-portable-win-x64.zip`
  - Unzip and run — no installation needed

### CI/CD

Verification and release deployment are fully automated with GitHub Actions.

- **CI (`.github/workflows/ci.yml`)**: Debug/Release builds, full tests with coverage, and an
  installer-packaging dry run on pushes/PRs to `main`.
- **CodeQL (`.github/workflows/codeql.yml`)**: C# static analysis (push/PR and weekly).
- **CD release (`.github/workflows/release.yml`)**:
  - Triggered by a git tag push (`git tag v1.0.0 && git push origin v1.0.0`) or manually from
    the Actions UI.
  - After tests pass, builds the Inno Setup installer, portable ZIP and `SHA256SUMS.txt`, and
    publishes them to GitHub Releases.

Data location: `%AppData%\DawnPlayer` (settings.json, library.db, playlists/, artcache/,
dawnplayer.log). **Portable mode**: with a `portable.dat` next to the exe, the app uses a
`data\` folder beside the executable instead of `%AppData%` (the portable ZIP includes this
marker; installer builds do not).

## Architecture

```
src/
├── DawnPlayer.Core/            # audio/data engine (UI-independent)
│   ├── Audio/                  # WASAPI output, gapless sequencer, decoders, format negotiation
│   ├── Playlists/              # playlists, playback queue, m3u8 persistence
│   ├── Library/                # SQLite library, tags/ReplayGain/art scanners
│   ├── Lyrics/                 # LRC parser/finder
│   └── Persistence/            # settings (JSON)
└── DawnPlayer.App/             # WinUI 3 UI
    ├── Controls/               # NowPlayingBar, LyricsPane
    ├── Views/                  # Library / Playlist / Settings pages
    └── Services/               # composition root, SMTC, thread marshaling
tests/DawnPlayer.Tests/         # core/service/view-model unit tests, concurrency & adversarial
                                # tests, FlaUI-based E2E UI automation
```

Playback pipeline: `file → decoder (MF/Vorbis) → float → [volume·ReplayGain] → [resample] →
[channel conversion] → [Equalizer (per-device/common)] → [DynamicNormalizer] → [SoftLimiter] →
PCM conversion → SequencerStream (gapless chain) → WasapiOut (Exclusive/Shared)`

## Roadmap

- DSP chain extensions — crossfeed, mono downmix, spectrum: done
- Converter (format conversion/ripping)
- Drag & drop between playlists (smart playlists: done)
- Tag editor — done (including ReplayGain batch analysis)
- Biography/online lyrics lookup
- Multilingual resources — done (MRT Core resw, three languages)

## Credits

- [Eole foobar theme](https://github.com/Ottodix/Eole-foobar-theme) — UI design reference
- [NAudio](https://github.com/naudio/NAudio) / [NVorbis](https://github.com/njdrummond/NVorbis) — audio
- [TagLib#](https://github.com/mono/taglib-sharp) — tags
- foobar2000 — the source of functional inspiration
