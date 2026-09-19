# Dawn Player 고도화 계획 (Advanced Roadmap) — 개정 2판

> 작성일: 2026-09-19 (개정) · 기준: `dbe334a` (M0–M3 완료 직후)
> 개정 사유: **ASIO 지원을 로드맵에서 제외** (사용자 결정). M4를 DSD 고도화(DoP + DFF)로 재편하고,
> ASIO 관련 항목·리스크·의존성 표현을 모두 제거했다.
> 성격: **계획서 — 착수 전 승인 필요**. 이후 마일스톤 착수 시 본 문서의 해당 절을 계획 근거로 삼는다.

---

## 0. 진행 상황 (완료 기록)

| 마일스톤 | 커밋 | 내용 | 검증 |
|---|---|---|---|
| M0 | `be544ee` | Last.fm 설정 통합 + Window 루트 x:Uid 크래시 수정(+게이트 테스트) | 빌드 0경고, 필터 테스트 56 |
| M1 | `6ddf1d2` | Core 로깅 파사드(`Log`/`ILogSink`) + 롤링 파일 싱크(5MB×3), 조용한 catch 60+곳 관측화, DSP 플러그인 오류 로그 미러링 | 로깅 계약 테스트 10 |
| M2 | `db5b1b9` | 엔진 seam 4종 — `ITrackReaderProvider`, `IOutputDriver`, `ITagProvider`, `IPlayOrderStrategy` + `PlaybackController`→`IPlaylistManager` | 계약 테스트 13 |
| M3 | `dbe334a` | 배타 모드 샘플레이트 불일치 정책(재구성/리샘플, 3개 국어 UI) + 시크·A-B 시 노멀라이저 수렴 이득 보존(`ResetForSeek`) | 정책 매트릭스+DSP 상태 테스트 9 |

누적: **테스트 1,700개 전부 통과, 빌드 0경고 0오류 유지.**

---

## 1. 프로젝트 현황 진단 (개정판 기준)

### 1.1 규모 및 성숙도

| 영역 | 규모 | 비고 |
|---|---|---|
| `DawnPlayer.Core` | 70파일 / ~14.6k LOC | 오디오 엔진·재생목록·라이브러리·가사·설정 + M1~M3 신규(로깅, seam, 정책) |
| `DawnPlayer.App` | 81파일 / ~16.2k LOC | WinUI 3 UI (Views·Services·Shortcuts·i18n) |
| `DawnPlayer.Plugin.Abstractions` | 6파일 / 209 LOC | DSP·가사 플러그인 SDK |
| `DawnPlayer.Tests` | 93파일 / ~28.8k LOC | 동시성 전용 스위트 포함 1,700+ 테스트 |

### 1.2 강점 (유지·확장할 자산)

- **동시성 설계**: 불변 스냅샷 게시(`SessionSnapshot` + `Volatile.Write`), 명령 세대 카운터,
  오디오 스레드→ThreadPool 핸드오프 + 세션 동일성 재검증, 문서화된 락 순서.
- **M1 이후 관측 가능**: 모든 조용한 폴백이 로그에 흔적을 남긴다. 롤링 싱크로 무한 증가도 차단.
- **M2 이후 확장 가능**: 디코더·출력 드라이버·태그·재생 순서가 모두 "등록"으로 추가된다.
  (`IOutputDriver`는 ASIO가 빠져도 가상 장치·원격 출력 등 미래 백엔드의 솔로 남는다.)
- **M3 이후 충실도 선택권**: 배타 모드에서 레이트 불일치를 비트 퍼펙트(재구성)와
  끊김 없음(리샘플) 중 사용자가 고른다. 기본값은 기존 동작(비트 퍼펙트).

### 1.3 남은 약점 (고도화 대상)

**오디오 충실도**
1. **DSD 재생이 박스카 PCM뿐** — `DsfTrackReader`가 디시메이션 변환만 한다. DoP가 없어
   DSD-capable DAC의 DSD 경로를 전혀 활용하지 못하고, DFF(DSDIFF)는 미지원.
2. **포맷 불일치 갭의 잔여 영역** — M3 정책이 레이트 불일치를 다루지만, 채널 수 불일치는
   여전히 세션 재구성을 강제한다 (리샘플 정책으로 자연스럽게 확장 가능).
3. 시크가 샘플 단위 정밀 시크 추상화가 없어 디코더별 편차를 그대로 받는다 (소규모 개선 과제).

**Core 구조**
4. 갓 클래스 — `PlaylistManager`(1,187 LOC, 6개 책임), `MusicLibrary`(856 LOC, 스키마+마이그레이션+스캔+업서트).
5. Core가 헤드리스가 아님 — `ObservableCollection`/INPC/`UiInvoke` 마샬링이 Core에 유입.
6. Core i18n 누수 — 한국어 문자열이 Core에 하드코딩(PlaybackController, OutputSessionFactory 등).
7. 관측화가 *존재* 수준 — 구조적 컨텍스트(세션 ID, 명령 세대)가 로그 라인에 없어 상관 분석은 수동.

**App 구조**
8. MVVM 이원화 — Settings만 진짜 MVVM. Library(1,164 LOC)·Playlist(741)은 code-behind-as-viewmodel.
9. 정적 조합 루트 — `AppServices`(643 LOC)가 서비스 보유 + 이벤트 버스 + 비즈니스 로직까지 겸함. DI 부재.
10. 전체 새로고침 UI 패턴 — 큐 변경마다 전체 재그룹, `RechunkAlbumRows` 전체 재생성, 200ms 타이머 상시 구동.
11. 접근성 — `AutomationProperties.Name` 하드코딩 한국어, 하이컨트라스트 미지원.
12. 테마 사각지대 — `LyricsEditorWindow`/`LyricsSearchWindow`가 ThemeService/Mica 적용 제외.
13. 중복 — 스플리터 plumbing 2벌, 다이얼로그 작성 방식 3가지 혼재.

**잔여 미구현 제안**: 손실 인코더 변환, 파일 정리, 재생목록 undo/redo, AcoustID/MusicBrainz,
HTTP/WS 원격 제어, PLS/XSPF, 풀스크린 Now Playing(`WaveformPeaks` 보존됨), APE/WavPack/DFF.
(**ASIO는 범위에서 제외** — 사용자 결정. 본 문서 어디에도 ASIO 작업을 두지 않는다.)

---

## 2. 고도화 전략

지향: **"foobar2000의 기능적 깊이 × 상업 앱 수준의 내품질"**. 세 축으로 진행한다.

- **축 A — 오디오 충실도**: DSD 경로 완성(DoP·DFF)과 포맷 전환 경험 마무리. ASIO 없이
  WASAPI 배타를 유일한 비트퍼펙트 경로로 전제하며, 그 경로의 품질에 집중한다.
- **축 B — 아키텍처/내품질**: App 쪽 구조(구조 개선은 M2가 Core 쪽 절반을 이미 해소).
- **축 C — 파워유저 기능**: foobar DNA 계열 신기능.

원칙(프로젝트 규약 준수): 착수 전 결함 트리 분석 + 실패 시나리오 테스트 선행, 빌드 0 경고 유지,
테스트는 타깃 필터 실행 + 마일스톤 종료 시 전체 1회, 임의 배포 금지.

### 마일스톤 개요

| 마일스톤 | 주제 | 핵심 산출물 | 규모 |
|---|---|---|---|
| ~~M0–M3~~ | ~~완료~~ | ~~위 표 참조~~ | — |
| M4 | DSD 고도화 (DoP + DFF) | ✅ 완료 `cfe4cd8` — `DopTrackReader`(WASAPI 배타 DoP), `DffRawReader`, PCM 폴백 체계, 정책 공존 | M–L |
| M5 | App 구조 개선 | ✅ 1차 완료 `8a6dac1` — Lastfm VM+토큰 서비스화, SplitterChrome 통합, 타이머 게이팅. 잔여: DI 전면 전환, Library/Playlist 전체 VM화, 큐 델타 갱신 (아래 진행 기록 참조) | L |
| M6 | 접근성·테마·i18n 사각지대 | ✅ 완료 — 가사 창 테마 연결+액센트 전파, AutomationProperties i18n(3개 국어), 하이컨트라스트 가드 | S–M |
| M7+ | 파워유저 기능 (선택) | 파일 정리, undo/redo, AcoustID, 원격 API 등 — 미착수 | 개별 L |

순서 논리: M4는 M2의 `ITrackReaderProvider`/`IOutputDriver` 위에 얹는 순수 기능 마일스톤이고,
M5는 원격 API 등 축 C 대형 항목의 발판이므로 그 앞에 온다. M6는 어느 시점에도 끼워 넣을 수 있는
독립 규모다.

---

## 3. 마일스톤 상세

### M4 — DSD 고도화: DoP + DFF (M–L, 약 1주)

**목표**: DSD 소스를 PCM 강등 없이(DoP) 재생하고, DFF 파일도 색인·재생한다.
출력은 **WASAPI 배타만** 전제한다(ASIO 없음이 본 개정판의 전제).

1. **DoP(DSD over PCM) 송신 경로**
   - `DsfTrackReader`의 박스카 디시메이션과 별도로, DSD 비트스트림을 DoP 프레임
     (24-bit PCM, 0x05/0xFA 마커, DSD64→176.4kHz)으로 패킹하는 `DopTrackReader` 추가.
     `ITrackReaderProvider`로 등록(DSF/DFF + DoP 활성 설정 → 우선순위 공급).
   - **활성 조건은 포맷 프로브 기반**: `WasapiDeviceService.TryNegotiateExclusive`가
     DoP 레이트(176.4/352.8kHz·24비트)를 수락할 때만 세션을 DoP 포맷으로 연다.
     수락하지 않으면(일반 엔드포인트가 그렇다) 기존 박스카 PCM으로 자동 강등 + InfoBar 알림.
     "설정했지만 장치가 못 받는" 상태를 조용히 두지 않는다.
   - M3의 `ExclusiveRateMismatchPolicy`와 상호작용: DoP 세션 중 일반 PCM 트랙으로 넘어가면
     재구성(기본) 또는 리샘플(선택)이 그대로 적용되게 한다 — 새 정책이 기존 정책을 우회하지 않음.
   - 볼륨: DoP 프레임에 디지털 볼륨을 적용하면 DSD 스트림이 깨진다(마커 바이트 훼손).
     DoP 세션은 `AllowVolumeInExclusive`와 무관하게 볼륨/DSP를 강제 바이패스하고 UI에 표기.
2. **DFF(DSDIFF) 지원**
   - `DffTrackReader`: DFF 헤더 파싱(FRM8/FSND chunk, DSD/DST), DSF와 로직 공유.
     DST 압축 트랙은 1차에서 미지원(명확한 오류 메시지)으로 하고, 필요 시 후속.
   - 라이브러리 색인·확장자 연결(`AppPaths.SupportedExtensions`, 파일 피커, 인스톨러)·태그 읽기.
3. **설정 UI**: 환경설정 → 재생에 "DSD 재생 방식" (DoP 우선 / 항상 PCM 변환) 추가. 3개 국어 resw.
4. **실패 시나리오 테스트 (착수 전 설계)**
   - DoP 레이트 미수락 엔드포인트 → 박스카 폴백 + 알림 1회(트랙마다 반복 금지).
   - DoP 세션 중 볼륨/이퀄라이저/컨볼루션 활성 → 바이패스 불변식 (마커 바이트 무결성 검사).
   - DSD64/128/256 스트림의 메모리 예산과 prefetch 상호작용.
   - M3 정책 공존: DoP↔PCM 전환 시 재구성/리샘플 정책 일관성.
5. **완료 기준**: DoP 수락 장치에서 마커 무결성 유지 재생, 미수락 장치에서 조용한 PCM 폴백,
   DFF 색인·재생, 기존 PCM 경로 무회귀, 전체 테스트 통과.

### M5 — App 구조 개선: DI + VM 추출 + 증분 UI (L, 1–2주)

1. **DI 컨테이너 도입**: `Microsoft.Extensions.DependencyInjection` — `AppServices`의 서비스 보유/조합만
   이관(정적 이벤트 버스는 단계적 축소, 즉시 제거하지 않음 — 페이지 구독 호환 유지).
   비즈니스 로직(재생 카운트 휴리스틱 `OnPlaybackTrackLeft`, DB 복구, 배치 스캔)은 서비스 클래스로 분리.
2. **Library/Playlist ViewModel 추출**: 페이지 상태(`_search`, `_visible`, `_selectedNode`)와
   필터/정렬/레이아웃 지속을 `LibraryViewModel`/`PlaylistViewModel`로. ~20개 유사 클릭 핸들러를
   커맨드 파라미터화("어떤 트랙 집합"만 다른 부분 제거).
3. **증분 UI 갱신**: (a) 큐 패널 전체 재그룹 → 델타 갱신, (b) `RechunkAlbumRows` 전체 재생성 →
   가상화 wrap panel(`ItemsRepeater` + WrapLayout) 전환 검증, (c) NowPlayingBar 200ms 타이머 →
   위치 이벤트 구동(스크럽 중만 타이머).
4. **중복 제거**: 스플리터 attached behavior 통합, 다이얼로그 팩토리 단일화(PlaylistDialogs 스타일로),
   `Converters.cs` 위치 정리, `LibraryTreeBuilder`/`LibraryTreeModelBuilder` 명명 정리.
5. **Last.fm 섹션 VM화**: `LastfmSettingsViewModel` — 인증 토큰이 페이지 상태로 남아
   네비게이션 이탈 시 흐름이 소실되는 문제를 함께 해결.
6. Core 헤드리스화 1차: `FastObservableCollection`, `Playlist`/`PlaylistItem` INPC의
   인터페이스 경계만 확립(이전은 별도 커밋 — 범위 폭발 방지).

**완료 기준**: Library/Playlist code-behind LOC 50%+ 절감, 수동 시나리오 + 기존 계산 로직 테스트 통과,
10만 트랙 스케일 목킹 스모크에서 UI 응답성 유지.

### M6 — 접근성·테마·i18n 사각지대 (S–M, 2–4일)

1. **AutomationProperties i18n**: 하드코딩 한국어 값을 `…AutomationProperties.Name` resw 키로.
   리스트 행·앨범 카드·드로어에 automation name 부여.
2. **하이컨트라스트 대응**: HC 감지 시 `ThemeService` 커스텀 팔레트 오버라이드 축소, 필수 브러시 시스템 위임.
3. **보조 창 테마 연결**: `LyricsEditorWindow`/`LyricsSearchWindow`에 ThemeService 백드롭·액센트 적용.
4. **Core 하드코딩 한국어 → 리소스 키**: `PlaybackController`/`OutputSessionFactory`/
   `AudioFileReaderFactory` 사용자 메시지를 enum/키 반환 → App 레이어 변환 구조로.
5. i18n 테스트 강화: AutomationProperties 키 누락 검사 추가.

**완료 기준**: 내레이터 스모크(재생 제어·탐색), HC 켬 시 텍스트 대비 유지, 3개 언어 resw 동기 게이트 통과.

### M7+ — 파워유저 기능 (선택, 착수 시 개별 계획서)

우선순위 제안(가치/준비도 기준):

1. **파일 정리(File Operations)** — `%artist%/%album%/%track% - %title%` 이동/이름변경.
   M2의 `ITagProvider`와 태그 편집기 인프라 공유. (L)
2. **재생목록 실행 취소/다시 실행 + 잠금** — `CollectionSnapshot` 인프라 재사용. (M)
3. **원격 제어 HTTP/WS API** — 스마트폰 리모컨. M5의 정리된 서비스 경계 위에서 착수. (L)
4. **AcoustID/MusicBrainz 자동 태깅** — `ITagProvider` 체인에 등록하는 형태. 원자적 쓰기 완비. (L)
5. **PLS/XSPF 가져오기** — 재생목록 포맷 추상화. (S)
6. **풀스크린 Now Playing + 웨이브폼 캔버스** — `WaveformPeaks` 부활. (M)
7. **M3 정책 확장 — 채널 수 불일치도 리샘플/컨버터로 흡수하는 옵션** (S–M)
8. **APE/WavPack 디코딩, 손실무손실 인코더 변환기 확장** — `ITrackReaderProvider` 등록형. (조사 필요)

---

## 4. 리스크 및 완화

| 리스크 | 영향 | 완화 |
|---|---|---|
| DoP 레이트를 배타 프로브가 수락하는 엔드포인트가 드뜸 | M4의 DoP가 일부 장치에서만 동작 | 프로브 기반 활성화 + PCM 폴백을 기본 동작으로 설계(이미 M3 폴백 체계와 동일 패턴), "DoP 미지원"을 결함이 아닌 상태로 알림 |
| DoP 세션에 볼륨/DSP가 흘러들면 스트림 깨짐 | 음원 파손 사고 | DoP 경로 강제 바이패스 + 마커 바이트 무결성 단위 테스트, UI에 "볼륨 비활성" 명시 |
| DSD 고비트레이트(DSD128+)에서 prefetch/메모리 압박 | 재생 끊김 | 스트림 속도별 예산 테스트, 초과 시 PCM 강등 폴백 |
| 배타 모드 리샘플 정책이 비트퍼펙트 기대와 충돌 | 음질 회귀 (M3에서 이미 옵션화) | 기본값 RestartSession 고정 테스트 존재, UI 설명 유지 |
| AppServices 이벤트 버스 정리 중 구독 누수 재발 | 메모리 누수 | 기존 문서화된 누수 사례(SettingsPage:258-266) 회귀 테스트 고정 후 진행 |
| UI 가상화 전환 시 Eole 드로어 상호작용 파괴 | UX 회귀 | 드로어 수동 체크리스트 + feature flag 전환 |
| 대규모 리팩터링 중 오디오 회귀 | 핵심 가치 훼손 | seam 계약 테스트(M2)가 이미 존재 — mechanical 이동만 허용 |

---

## 5. 검증 전략 (마일스톤 공통, 변경 없음)

- 착수 전: 해당 범위 결함 트리 + 실패 시나리오 테스트 먼저 작성(규약 3).
- 진행 중: 수정 클래스 단위 `dotnet test --filter` 만.
- 종료 시: 전체 `dotnet test` 1회 + `dotnet build` 0경고/0오류.
- 오디오 변경: 실기기 청음 체크리스트(갭리스 경계, 배타 협상, 폴백 알림) 1회.
- 배포는 사용자 명시 요청 시에만(규약 2).

## 6. 즉시 실행 가능한 다음 액션

1. **M4(DSD 고도화) 착수 승인 요청** — DoP 프레임 포맷·프로브 활성화 설계안 제시 후 승인받아 구현.
2. M5/M6는 순서 대기 (M4 완료 후 M5 권장 — 원격 API 등 축 C의 전제).
