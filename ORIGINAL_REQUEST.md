# Original User Request

> **[역사적 문서]** 프로젝트 착수 시점의 원본 요청 기록입니다. 현재 상태를 반영하지 않습니다.

## Initial Request — 2026-08-17T11:09:15Z

This is a single self-contained fix; keep it small and focused.
This is a focused code audit and review of recent Dawn Player implementations: the dedicated 'Now Playing' system playlist architecture, playback queue isolation, and the Eole-style in-line album tracklist drawer.

Working directory: e:\coding\dawn-player
Integrity mode: development

## Requirements

### R1. User Playlist Protection & Now Playing System Isolation Audit
Review and verify that user-curated playlists (e.g., saved playlists, M3U8 persistence) are completely protected against accidental modification or overwrite when playing tracks, double-clicking albums, or enqueuing from the Library. Verify that all ad-hoc library playback operations strictly target the dedicated `NowPlaying` system playlist.

### R2. Playback Queue Invariant & Thread-Safety Audit
Inspect `PlaylistManager`, `PlaybackController`, `PlaybackQueue`, and `PlaybackUiHelper` for:
- Concurrency and lock invariants (preventing deadlocks between `_saveLock`, `_lock`, `_stateLock`, and UI thread dispatchers).
- Correct queue clearing vs queue accumulation (`PlayAlbumNowPlayingAsync` vs `EnqueueAlbumNowPlaying`).
- Proper event bubbling and UI synchronization for the right-hand panel ("Playing tracks...").

### R3. In-line Accordion Album Drawer (Showlist) UI & UX Validation
Inspect `AlbumRowVm`, `AlbumCard`, and `LibraryPage.xaml/.cs` for:
- Selection state tracking (`IsSelected`) and caret anchor indicator visibility.
- Dynamic row chunking and column recalculation during resize/zoom.
- Correct behavior of quick action buttons (`[▶ 앨범 재생]`, `[+ 대기열 추가]`, `[+ 재생목록]`, `[✕ 닫기]`).
- 2-column tracklist rendering, live playing speaker icon (`\uE767`), and context flyout commands.

## Acceptance Criteria

### Verification & Test Suite
- [ ] All 1,253 existing unit tests pass without failures (`dotnet test --nologo`).
- [ ] Solution builds cleanly with 0 warnings and 0 errors (`dotnet build DawnPlayer.slnx --nologo`).

### Audit Report
- [ ] Deliver a structured adversarial audit report detailing:
  1. Invariant analysis results (Playlist protection, Queue integrity, UI thread safety).
  2. Any identified edge cases, potential race conditions, or unhandled UI states.
  3. Actionable recommendations or confirmation of code robustness.
