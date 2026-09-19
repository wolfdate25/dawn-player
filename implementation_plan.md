# Dawn Player 고도화 계획 (Advanced Roadmap)

> 작성일: 2026-09-19 · 대상: `main` @ `242235e` + 미커밋 Last.fm 설정 통합 작업
> 성격: **계획서 — 승인 후 착수**. 본 문서는 분석 결과와 단계별 실행 계획을 담는다.

---

## 1. 프로젝트 현황 진단

### 1.1 규모 및 성숙도

| 영역 | 규모 | 비고 |
|---|---|---|
| `DawnPlayer.Core` | 68파일 / 14,211 LOC | 오디오 엔진·재생목록·라이브러리·가사·설정 |
| `DawnPlayer.App` | 81파일 / 16,143 LOC | WinUI 3 UI (Views·Services·Shortcuts·i18n) |
| `DawnPlayer.Plugin.Abstractions` | 6파일 / 209 LOC | DSP·가사 플러그인 SDK |
| `DawnPlayer.Tests` | 91파일 / 28,376 LOC | 동시성 전용 스위트 포함, 1,600+ 테스트 |

기능 완성도는 높음: WASAPI 배타(+DS/WaveOut 폴백), 갭리스 시퀀서, CUE/Opus/DSF, 8밴드 파라메트릭 EQ,
컨볼루션, ReplayGain 1.0+2.0/AGC, 평점·쿼리 스마트 재생목록, 청취 리포트, 인터넷 라디오, Last.fm,
WAV 변환기, DSP 플러그인 SDK, 3개 국어 i18n, 인스톨러/포터블, CI/CD+CodeQL.
기존 제안서(`docs/feature-enhancement-proposals.md`)의 1~4단계는 **거의 전부 구현 완료** 상태.

### 1.2 강점 (유지·확장할 자산)

- **동시성 설계가 예외적 수준**: 불변 스냅샷 게시(`SessionSnapshot` + `Volatile.Write`), 명령 세대 카운터,
  오디오 스레드→ThreadPool 핸드오프 + 세션 동일성 재검증, 문서화된 락 순서(`_gate`→`_prefetchLock`).
- 코드 마커(TODO/HACK) **0개** — 부채가 주석이 아니라 구조로 존재.
- DSP 체인 copy-on-write·무할당 렌더, 플러그인 계약이 렌더 스레드 제약을 명시.
- i18n 키 드리프트를 xUnit으로 게이트. 인큐베이션된 3개 언어 resw 동기화.

### 1.3 약점 (고도화 대상)

**Core**
1. **관측성 부재** — 로깅 추상화 없음, 조용한 `catch {}` 54곳(최다: WasapiDeviceService 11,
   PlaybackController·MusicLibrary·TagReader 각 7). 장애 재현이 사용자 보고에 의존.
2. **추상화 솔(seam) 부재** — 디코더(`AudioFileReaderFactory.Open` 확장자 switch), 출력 드라이버
   (`OutputSessionFactory.Start` enum switch), 태그(정적 `TagReader`), 재생 순서(`PlayOrderResolver` 구체형)가
   모두 하드코딩. `IPlaylistManager`가 존재하는데 `PlaybackController`가 구체형 `PlaylistManager`에 의존.
3. **오디오 충실도 갭** — 사용자 설정 리샘플러(ASRC) 없음: 배타 모드에서 레이트 불일치 시 트랙 경계에서
   **세션 재구성(가청 갭)**. ASIO 미지원. DSD는 박스카 PCM뿐(DoP/DFF 없음). 시크 시 DSP 전체 리셋.
   변환기는 WAV 전용.
4. **갓 클래스** — `PlaylistManager`(1,187 LOC, 6개 책임: CRUD·스마트목록·M3U8 영속화·태그 병렬 해석·
   정렬/중복제거·UI 마샬링), `PlaybackController`(1,154), `MusicLibrary`(856: 스키마+마이그레이션+스캔+업서트).
5. **Core가 헤드리스가 아님** — `ObservableCollection`/INPC/`UiInvoke` 마샬링이 Core에 유입.
6. **Core i18n 누수** — 한국어 문자열이 Core에 하드코딩(PlaybackController 4곳, OutputSessionFactory 5곳 등).
7. **중복** — 통계 carry-forward 2벌(MusicLibrary 329-343 vs 443-456), sync/async 쌍 4벌, Enqueue/EnqueueNext.

**App**
1. **MVVM 이원화** — Settings만 진짜 MVVM. Library(1,164 LOC)·Playlist(741)은 code-behind-as-viewmodel.
2. **정적 조합 루트** — `AppServices`(643 LOC)가 서비스 보유 + 이벤트 버스 + 비즈니스 로직(재생 카운트
   휴리스틱, DB 복구, 배치 스캔)까지 겸함. DI 컨테이너 부재.
3. **전체 새로고침 UI 패턴** — `RechunkAlbumRows`가 필터/리사이즈/줌마다 전체 행 재생성, 큐 변경마다 전체
   재그룹, NowPlayingBar 200ms 타이머 상시 구동. 대형 라이브러리에서 비용 선형 증가.
4. **접근성** — `AutomationProperties.Name`이 전부 하드코딩 한국어(resw 파이프라인 미연결), 하이컨트라스트 미지원.
5. **테마 사각지대** — `LyricsEditorWindow`/`LyricsSearchWindow`가 ThemeService/Mica 적용 제외.
6. **중복/파편화** — 스플리터 plumbing 2벌(LibraryPage vs PlaylistPage), 다이얼로그 작성 방식 3가지 혼재.

**잔여 미구현 제안**(구 제안서 기준): APE/WavPack/DFF, 손실 인코더 변환, 파일 정리, 재생목록 undo/redo,
AcoustID/MusicBrainz, HTTP/WS 원격 제어, 풀스크린 비주얼라이저(웨이브폼 스캐너는 보존됨).

---

## 2. 고도화 전략

지향: **"foobar2000의 기능적 깊이 × 상업 앱 수준의 내품질"**. 세 축으로 진행한다.

- **축 A — 오디오 충실도/기능**: 사용자 가치가 가장 큰 엔진 항목.
- **축 B — 아키텍처/내품질**: 확장성·관측성·접근성. 대형 기능(ASIO, 컴포넌트 생태계)의 전제가 되는 작업을 먼저.
- **축 C — 파워유저 기능**: foobar DNA 계열 신기능.

원칙(프로젝트 규약 준수): 착수 전 결함 트리 분석 + 실패 시나리오 테스트先行, 빌드 0 경고 유지,
테스트는 타깃 필터 실행 + 마일스톤 종료 시 전체 1회, 임의 배포 금지.

### 마일스톤 개요

| 마일스톤 | 주제 | 핵심 산출물 | 규모 |
|---|---|---|---|
| M0 | 미커밋 작업 마무리 | Last.fm 설정 통합 커밋 | S |
| M1 | 관측성 기반 공사 | Core 로깅 추상화 + 조용한 catch 정리 | M |
| M2 | 엔진 seam 도입 | 디코더/출력드라이버/태그/재생순서 인터페이스화, `IPlaylistManager` 연결 | M |
| M3 | 오디오 충실도 | ASRC 리샘플러 옵션, 시크 DSP 상태 보존 | M |
| M4 | ASIO + DSD 고도화 | `IOutputDriver` 기반 ASIO, DoP, DFF | L |
| M5 | App 구조 개선 | DI 컨테이너, Library/Playlist VM 추출, 증분 UI 갱신 | L |
| M6 | 접근성·테마·마무리 | AutomationProperties i18n, 하이컨트라스트, 보조 창 테마 연결 | S–M |
| M7+ | 파워유저 기능 (선택) | 파일 정리, undo/redo, AcoustID, 원격 API | 개별 L |

---

## 3. 마일스톤 상세

### M0 — 미커밋 작업 마무리 (S, 반나절)

작업 트리에 Last.fm 대화상자→설정 페이지 통합이 완료 상태로 존재 (구 `LastfmDialog.cs` 삭제,
`SettingsPage`에 `InitializeLastfmSection` 등 +303/-220, 3개 언어 resw 21키 동기, 테스트 갱신 완료).
잔여 검증 후 커밋만 수행:

1. `dotnet build DawnPlayer.slnx` 0경고 확인 + `--filter` i18n/Settings VM 테스트.
2. 사소한 후속: `LyricsEditorWindow.xaml`/`LyricsSearchWindow.xaml`의 `x:Uid` 제거 잔분 정리 확인
   (신규 LocalizationTests가 Window 루트 x:Uid 금지를 검사 — 통과 여부만 확인).
3. 커밋 분할 제안: `feat(app): integrate Last.fm into settings page` + UI 여백/슬라이더 정리분.

### M1 — 관측성 기반 공사 (M, 2–4일)

**목표**: 장애를 재현 가능하게 만들어 이후 모든 마일스톤의 리스크를 낮춘다.

1. **로깅 추상화 도입(Core)**: `Microsoft.Extensions.Logging.ILogger` 추상 또는 경량 자체 인터페이스
   (`ILogSink` — App이 파일 싱크 주입, 테스트는 메모리 싱크). 기존 `App.Log`(File.AppendAllText)을 싱크로 교체.
   - 결함 분석: 오디오 렌더 스레드에서는 무할당/논블로킹이므로 **로그는 ThreadPool 핸드오프 지점에서만**.
2. **조용한 catch 54곳 정리**: 각 블록에 사유 주석이 이미 있으므로 `Log.Debug/Trace` 1줄씩 부착.
   사용자 의사결정이 필요한 곳(장치 열기 실패 등)만 `Warning` 승격.
3. **플러그인 로드 오류·오디오 스레드 예외의 영속 기록**: `DspPluginLoader.LoadErrors`, `ReadError` 경로 연결.
4. 로그 롤링(현재 단일 파일 무한 증가 가능성) — 크기 기반 5MB×3 롤.
5. 실패 시나리오 테스트: 메모리 싱크로 "catch 도달 시 로그 1회, 렌더 스레드 무차단" 검증.

**완료 기준**: Core의 모든 catch가 관측 가능(로그 또는 명시적 무시 사유 주석 태그), dawnplayer.log 롤링,
관련 단위 테스트 통과.

### M2 — 엔진 seam(인터페이스화) (M, 3–5일)

**목표**: 신규 디코더/출력/메타데이터가 "팩토리 편집"이 아니라 "등록"으로 추가되게 한다.

1. **`ITrackReaderProvider` 레지스트리**: `AudioFileReaderFactory`의 확장자 switch를
   provider 등록 모델로(기존 리더들은 provider로 래핑, 동작 100% 보존).
2. **`IOutputDriver`**: `OutputSessionFactory`의 enum switch 분해 — WasapiShared/WasapiExclusive/
   DirectSound/WaveOut 각 driver 클래스화. M4(ASIO)의 직접 전제.
3. **`ITagProvider`**: 정적 `TagReader`/`TagWriter` 뒤 인터페이스 — AcoustID(M7)와 태그 포맷 확장의 전제.
4. **`IPlayOrderStrategy`**: `PlayOrderResolver` 인터페이스화 + `PlaybackController` 생성자 주입.
5. **`PlaybackController` → `IPlaylistManager` 의존 전환**(인터페이스 이미 존재, 미사용).
6. 각 seam에 대한 계약 테스트(기존 동작과 동일함을 스냅샷 비교).

**완료 기준**: 팩토리/스위치 제거, 전체 오디오 경로 회귀 테스트 통과, 빌드 0 경고.

### M3 — 오디오 충실도: ASRC + 시크 개선 (M, 3–5일)

1. **고품질 리샘플러 옵션**: 배타 모드에서 레이트 불일치 시 (a) 세션 재구성(현행, 갭 있음) 또는
   (b) 고품질 리샘플(`WdlResamplingSampleProvider` 이미 사용 중 — HQ 모드 파라미터 공개) 중 선택.
   - 설정: 환경설정 → 재생 → "샘플레이트 불일치 처리" (재구성/리샘플 자동/리샜플 고정 레이트).
   - 결함 분석: 리샘플 도입 시 DSP 체인 순서(리샘플은 게인/EQ **앞**, 비트퍼펙트 경로에서는 자동 무장 해제
     로직과 상호작용 — `SequencerStream.cs:311-328` 불변식 재검증 필수).
2. **시크 시 DSP 상태 보존**: 현재 전체 리셋 → 컨볼루션/AGC 등 상태 유지 시크(리밋터만 리셋) 옵션화.
3. 웨이브폼/스펙트럼이 리샘플된 신호를 따라가는지 확인(SpectrumTap 위치 검증).
4. 실패 시나리오: 샘플레이트 급변 연속 트랙(44.1k→96k→44.1k), A-B 반복 구간 내 리샘플, 시크 중 일시정지.

**완료 기준**: 배타+혼합 레이트 재생에서 사용자 선택 동작, 갭리스 회귀(동일 포맷 체인) 무손상 유지.

### M4 — ASIO + DSD 고도화 (L, 1–2주)

1. **ASIO 출력 드라이버**: M2의 `IOutputDriver` 위에 `AsioOutDriver`(NAudio AsioOut).
   - 32비트 float/INT32 형식 협상, 배타적 접근 충돌(WASAPI 배타와 상호배제) 처리, 장치 열거 UI.
   - 이벤트 타이밍 모드 지원 여부 확인(커버리지: 버퍼 크기 조절 설정 노출).
2. **DoP(DSD over PCM)**: DSF 판독 경로에서 박스카 대신 DoP 패킹(0x05/0xFA 마커) — ASIO/WASAPI 배타
   24비트 경로에서만 활성화. DSD 네이티브(ASIO DSD)는 후속 분리 과제로 표기.
3. **DFF 지원**: `DsfTrackReader`와 병렬 `DffTrackReader`(DFF/DSDIFF 헤더 파싱, 로직 대부분 공유).
4. 실패 시나리오: DoP 미지원 장치 조합 폴백(박스카 PCM으로 자동 강등 + 알림), ASIO 드라이버 크래시
   시 세션 복구, DSD256/512 초고속 스트림 메모리 예산.

**완료 기준**: ASIO 장치에서 재생·볼륨 정책(ASIO는 하드웨어 볼륨 또는 무음 주의 — UI 사전 경고),
DoP 재생 확인, 기존 PCM 경로 무회귀.

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
5. **Last.fm 섹션 VM화**: `LastfmSettingsViewModel` — `_pendingLastfmToken` 페이지 상태 문제
   (네비게이션 이탈 시 인증 흐름 소실) 함께 해결.
6. Core 헤드리스화 1차: `FastObservableCollection`, `Playlist`/`PlaylistItem` INPC를 App 쪽 어댑터로 이동
   (Core는 순수 모델+통지 인터페이스만). **범위 크므로 M5에서는 인터페이스 경계만 확립, 이전은 별도 커밋.**

**완료 기준**: Library/Playlist 페이지 code-behind LOC 절감(목표 50%+), 페이지 단위 회귀(수동 시나리오 +
기존 `PlaybackUiHelper` 등 계산 로직 테스트), 10만 트랙 스케일 스모크 테스트(목킹)에서 UI 응답성 유지.

### M6 — 접근성·테마·i18n 사각지대 (S–M, 2–4일)

1. **AutomationProperties i18n**: 모든 하드코딩 한국어 값을 `…AutomationProperties.Name` resw 키로
   (파이프라인 이미 지원, 신규 Lastfm 키가 선례). 리스트 행·앨범 카드·드로어에 automation name 부여.
2. **하이컨트라스트 대응**: `ThemeService`가 HC 테마 감지 시 커스텀 팔레트 오버라이드 축소
   (`SystemParameters.HighContrast` / `AccessibilitySettings`), 필수 브러시만 시스템 위임.
3. **보조 창 테마 연결**: `LyricsEditorWindow`/`LyricsSearchWindow`에 ThemeService 백드롭·액센트 적용,
   액센트 변경 전파.
4. **Core 하드코딩 한국어 → 리소스 키**: `PlaybackController`/`OutputSessionFactory`/
   `AudioFileReaderFactory` 사용자 메시지를 코드 반환 → App 레이어 변환(enum/키) 구조로
   (M1 로깅과 함께 진행하면 자연스러움 — 순서 조정 가능).
5. i18n 테스트 강화: AutomationProperties 키 누락 검사 추가.

**완료 기준**: 내레이터 스모크(재생 제어·탐색), HC 켰 때 대비 텍스트 대비 유지, 3개 언어 resw 동기 게이트 통과.

### M7+ — 파워유저 기능 (선택, 착수 시 개별 계획서)

우선순위 제안(가치/준비도 기준):

1. **파일 정리(File Operations)** — `%artist%/%album%/%track% - %title%` 이동/이름변경.
   태그 인프라 공유. M2의 `ITagProvider` 후 활용. (L)
2. **재생목록 실행 취소/다시 실행 + 잠금** — `PlaylistManager` 스냅샷 인프라(`CollectionSnapshot` 존재) 재사용. (M)
3. **원격 제어 HTTP/WS API** — 스마트폰 리모컨. `AppServices` 이벤트 버스가 M5에서 정리된 후 착수 권장. (L)
4. **AcoustID/MusicBrainz 자동 태깅** — `ITagProvider` + 원자적 쓰기 이미 완비. (L)
5. **PLS/XSPF 가져오기** — 재생목록 포맷 추상화(M2 부산물). (S)
6. **풀스크린 Now Playing + 웨이브폼 캔버스** — `WaveformPeaks` 스캐너 보존 상태, 큰 캔버스에서 부활. (M)
7. **APE/WavPack 디코딩, FLAC 등 손실무손실 인코더 변환기 확장** — 관리형 라이브러리 생태계 조사 후
   착수(M4 이후 `ITrackReaderProvider`에 등록형). (조사 필요)

---

## 4. 리스크 및 완화

| 리스크 | 영향 | 완화 |
|---|---|---|
| ASIO 드라이버 다양성(벤더별 버그) | M4 지연 | 드라이버 블랙리스트 + 세이프모드(ASIO 꺼짐) 폴백, 베타 옵션 출시 |
| 배타 모드 리샘플이 비트퍼펙트 불변식 훼손 | 음질 회귀 | 리미터 무장 자동판정 로직에 "리샘플 활성" 조건 추가, A/B 테스트 시나리오 |
| AppServices 이벤트 버스 제거 중 구독 누수 재발 | 메모리 누수 | 기존 문서화된 누수 사례(SettingsPage:258-266) 회귀 테스트로 고정 후 진행 |
| UI 가상화 전환 시 Eole 드로어 상호작용 파괴 | UX 회귀 | 드로어 시나리오 수동 체크리스트 + feature flag로 신규 패널 전환 |
| 대규모 리팩터링 중 오디오 회귀 | 핵심 가치 훼손 | 각 마일스톤마다 "동일 동작 스냅샷" 계약 테스트, 오디오 경로는 최소한의 mechanical 이동만 |

## 5. 검증 전략 (마일스톤 공통)

- 착수 전: 해당 범위 결함 트리 + 실패 시나리오 테스트 먼저 작성(규약 3).
- 진행 중: 수정 클래스 단위 `dotnet test --filter` 만.
- 종료 시: 전체 `dotnet test` 1회 + `dotnet build` 0경고/0오류.
- 오디오 변경: 실기기 청음 체크리스트(갭리스 경계, 배타 협상, 폴백 알림) 1회.
- 배포는 사용자 명시 요청 시에만(규약 2).

## 6. 즉시 실행 가능한 다음 액션

1. M0 커밋 (미커밋 Last.fm 통합 — 검증만 남음).
2. M1 착수 승인 요청 — 로깅 추상화 설계안(인터페이스 초안 + 싱크 주입 지점) 제시 후 승인받아 구현.
