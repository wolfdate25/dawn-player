# Dawn Player 고도화 계획 (Advanced Roadmap) — 개정 5판

> 작성일: 2026-09-20 (개정) · 기준: `aed6bb9` (U0–U5·NAudio 3.1 완료 후)
> **개정 5판 (2026-09-20)**: 본문 주제를 네트워크 소스 로드맵(N0–N4 — Network 탭·DLNA 클라이언트·
> YouTube)으로 교체한다. 완료된 개정 4판(U0–U5)의 계획 본문(구 §1–§5)은 §0 실행 기록으로 접고,
> 설계 근거는 U0 영속화 산출물 `design-system/dawn-player/MASTER.md`가 잇는다 — 개정 4판이
> 개정 3판 본문을 폐기한 관례와 동일. ASIO 제외 원칙은 유지.
> 근거 리서치: YouTube/DLNA 기술 조사 2026-09-20(yt-dlp PO 토큰·JS 런타임 현황, .NET UPnP 생태계,
> foo_upnp·foo_youtube·mpv 선행사례). **N 계열은 제안 단계 — 착수 범위 승인 대기**(§1.4).
> 작업 규약: [AGENTS.md](AGENTS.md).

---

## 0. 진행 상황 (완료 기록)

### 마일스톤 완료 표 (M0–M6, 개정 1·2판)

| 마일스톤 | 커밋 | 내용 | 검증 |
|---|---|---|---|
| M0 | `be544ee` | Last.fm 설정 통합 + Window 루트 x:Uid 크래시 수정(+게이트 테스트) | 빌드 0경고, 필터 테스트 56 |
| M1 | `6ddf1d2` | Core 로깅 파사드(`Log`/`ILogSink`) + 롤링 파일 싱크(5MB×3), 조용한 catch 60+곳 관측화 | 로깅 계약 테스트 10 |
| M2 | `db2b1b9` | 엔진 seam 4종 — `ITrackReaderProvider`, `IOutputDriver`, `ITagProvider`, `IPlayOrderStrategy` + `PlaybackController`→`IPlaylistManager` | 계약 테스트 13 |
| M3 | `dbe334a` | 배타 모드 샘플레이트 불일치 정책(재구성/리샘플) + 시크·A-B 시 노멀라이저 이득 보존 | 정책 매트릭스 테스트 9 |
| M4 | `cfe4cd8` | DSD 고도화 — DoP(WASAPI 배타), DFF 파서, PCM 폴백, raw-passthrough, 재생 모드 UI | 정책 공존 12종. ⚠️ 실기기 청음 미수행 |
| M5 | `8a6dac1`+`3848e94`+`2f499c1` | DI 컨테이너, 큐 델타 갱신, `RechunkAlbumRows` 고속경로, `IUiDispatcher`, Library/Playlist VM, Last.fm VM, 스플리터 통합 | VM/서비스 테스트. LOC 기준은 트리거 신호 재정의로 폐지(아래) |
| M6 | `79ef494`+`83be8f7` | 보조 창 테마 연결, AutomationProperties i18n, 하이컨트라스트 가드, Core 메시지 키화, 자동화 키 게이트 | 테스트 8종+게이트 |

**M5 LOC 기준 재정의**: "code-behind LOC 50% 절감" 폐지 → 추출 트리거 신호 4종((1) UI 실행 없이
테스트 불가, (2) 상태가 페이지보다 오래 살아야 함, (3) 로직 사본 갈라짐, (4) 헤드리스 회귀 증명 필요)
발생 시에만 추출. 사후 수정: `8ebfe12` 재시작 위치 미스매치(불변식: 보고 위치 ≠ 디코더 위치 금지).

**개정 3판 폐기 기록 (2026-09-20)**: M7 파일 정리 / M8 undo·redo / M9 포맷 갭 / M10 원격 API /
R Core 내품질 트랙 — 사용자 판단으로 로드맵에서 제외. 개별 착수가 필요해지면 그때 개별 계획서로 회수.

**릴리스 상태**: 공개 태그 v1.1.0. v1.2.0 포터블 ZIP 로컬 빌드 완료(`dist/`, a795e67 기준) —
태그 푸시/GitHub Release는 사용자 명시 요청 대기(규약 §2).

누적: **테스트 1,740개 통과, 빌드 0경고 0오류 (2026-09-20 기준).**

### U0–U5 실행 기록 (2026-09-20, 개정 4판)

| 마일스톤 | 커밋 | 내용 | 검증 |
|---|---|---|---|
| U0 | `89b3999` | 디자인 토큰 레이어(`Styles/DesignTokens.xaml` + `DesignTokenValues.cs` 미러), 스킬 MASTER.md 영속화 + WinUI 번역 규칙, WCAG 대비 게이트(상태색 4.5:1·본문 7:1·액센트 3:1), hex 베이스라인 게이트 | `DesignTokenTests` 11종 |
| U1 | `89b3999` | `MotionService`(앱 토글 ∧ OS 애니메이션), `MotionHelper`(페이드·프레스 팝 — reduced-motion 시 최종 상태 즉시 고정), 탭/배경화면 페이드, `SpectrumSmoother`(즉시 공격·선형 감쇠), 설정 UI + 3개 국어 | `MotionAndSpectrumTests` 12종 |
| U2 | `89b3999` | `NotificationPresenter`(일회성 자동 닫힘 6초·경고/오류 수동, 단일 슬롯 교체 규칙)로 InfoBar 3곳 표준화, `LoadingGate` 300ms 임계값(스캔 진행 플래싱 제거), LiveSetting 접근성 | `StateUxTests` 8종 |
| U3 | `89b3999` | `DensityScale` 3단(Compact/Cozy=기존 룩/Comfortable) + 행 메트릭 리소스화, NowPlayingBar AdaptiveTrigger 컴팩트(<640px), LyricsPane 터치 타깃 26–28px→44px | 스위트 내 게이트 |
| U4 | `89b3999` | 풀스크린 Now Playing 창(커버 히어로 + `WaveformLayout` 기반 파형 시크 캔버스(드래그 프리뷰) + 28밴드 스펙트럼), Acrylic 옵트인(기본 꺼짐), 타이틀 메뉴 진입, Esc 종료 | `WaveformLayoutTests` 6종 |
| U5 | `89b3999` | 하드코딩 자동화 이름 31건 일괄 제거(x:Uid resw만이 이름의 원천 — 비한국어 UI에서 내레이터 한국어 읽힘 결함 수정) + 게이트 테스트, PlayGreen 액센트 프리셋(스킬 "Dark audio + play green"), 팔레트 대비 게이트 | `AutomationNameGateTests` 2종 |

**부수 정비 (NAudio 3.1 마이그레이션 대응, `d8696b7`)**: 별도 세션에서 NAudio 2.3→3.1 전환(Span 기반
Read, WasapiOut→WasapiPlayer)이 진행됨에 따라 (1) DirectSound 경로를 16-bit PCM 타깃으로 전환하고
새 COM 계층의 E_NOTIMPL 즉사를 감지하는 1회성 프로브 → WaveOut 폴백(현지화 경고 포함)을 추가,
(2) `RestartIfPlaying`의 reopen 실패가 unobserved task로 사라지지 않도록 방어 catch 추가(8ebfe12
계약 유지), (3) 가사·설정 테스트의 액센트 인덱스 갱신.

**감사 정정 (구 §1.2, 구현 중 확인)**: 최초 감사의 "0건" 판정 중 일부는 스캔 범위 누락이었음 —
(1) `DawnTheme.xaml` 컨트롤 템플릿 내 상태 스토리보드(TransportToggleButton/PlayPauseButton)는
기존 존재, (2) 스캔 ProgressBar는 `LibraryPage`에 기존 존재, (3) 가사 빈 상태는 기존 존재. "전무"
판정이 유지되는 것은 페이지/콘텐츠 계층 모션, AdaptiveTrigger, 밀도 시스템, reduced-motion,
풀스크린 파형이며, 이것이 U1–U4의 실제 범위였다.

**개정 4판 계획 본문 접기 (개정 5판)**: UI/UX 진단(구 §1)·고도화 전략(구 §2)·U0–U5 상세(구 §3)·
U 계열 리스크(구 §4)는 전 마일스톤 완료로 본문에서 제거 — 실행 결과는 위 표, 스킬 설계 근거는
`design-system/dawn-player/MASTER.md`, 검증 관례는 §2(상시 검증 원칙)로 인계.

누적: **테스트 1,779개 통과(신규 39종 포함), 콜드 리빌드 0경고 0오류 (2026-09-20 기준).**

### N0 실행 기록 (2026-09-20, 개정 5판 — 커밋 대기)

| 항목 | 내용 | 검증 |
|---|---|---|
| 탭 확장 | `NavigationViewState`+`NormalizeTab`/`ForTab`/`ForSettings` Network 분기, MainWindow 세 번째 탭·`NetworkPage`·활성화·Settings 진입 언체크 연결 | `LayoutAndNavigationTransitionTests` 19종(신규 5케이스) |
| 방송국 저장소 | `Core/Persistence/RadioStationStore`(AtomicFile+`.bak` 폴백, Normalize: URL 검증·중복 제거·상한 500) + `RadioStation` 모델 + `AppPaths.NetworkStationsFile` + AppServices 등록·Shutdown 저장 | `RadioStationStoreTests` 9종 |
| 라디오 섹션 UI | `Views/Network/RadioSection`(즐겨찾기 목록·추가/편집/삭제·컨텍스트 메뉴/더블클릭 재생·빈 상태), 타이틀 메뉴 "네트워크 스트림 열기"를 Network 탭 추가 대화상자로 재연결(중복 재생 로직 제거) | 수동 확인 항목(아래) |
| ICY 곡명 체인 | `ILiveMetadataSource`(StationName 분리) + `LiveMetadataRelay`(BuildPending 단일 퍼널 후킹, TrackLeave 시 자동 해지·면등급 detach) + 컨트롤러 `StreamTitleChanged` + `PlaylistItem.NowPlayingSubtitle` + AppServices UI 릴레이 → NowPlayingBar·Fullscreen·SMTC(`FormatLiveMetadata`, 현재 항목 일치 가드·세대 가드) | `LiveMetadataRelayTests` 4종·`RadioSubtitleFormatterTests` 9종·`RadioStationCommandsTests` 2종 |
| resw | Network 키 15종 × 3개 국어(ko/en/ja), 미참조화된 `OpenUrl_Title`/`OpenUrl_Header` 제거 | LocalizationTests 게이트(전체 스위트로 검증) |

**게이트**: obj/bin 삭제 콜드 리빌드 **0경고 0오류**, 전체 테스트 **1,808/1,808 통과**(신규 29종).
README 3개 국어 라디오 항목 갱신(Network 탭·즐겨찾기·실시간 곡명 표기).
**다음 실행 항목(대화형 환경 필요 — 자동 게이트는 전부 통과)**: 실제 라디오 스트림으로 ICY 곡명
표시·방송국 즐겨찾기 재생 육안 확인, Narrator 스모크.

### N1 실행 기록 (2026-09-20, 개정 5판 — 커밋 대기 · **N2/N3는 사용자 지시로 이월**)

| 항목 | 내용 | 검증 |
|---|---|---|
| 소스 종류 | `TrackSourceKind`(File/Radio/Dlna/YouTube) + `Track.SourceKind`(기본 File, AlbumKey 미참여) + `RadioTrack.Create` Radio 설정 | `TrackReaderFactoryRoutingTests` 6종 |
| M3U8 `#DPTRACK` | `M3uEntry` 확장·Read/Write 지시문 파싱, `DpTrackMeta`(JSON+URI 이스케이프, 손상·미래값은 null/디그레이드), `RemoteTrackCodec`(재구성: Radio 재사용·미지 종류 → 라디오 폴백) | `M3uDpTrackTests` 10종 |
| 라우팅 재설계 | `Open(path, sourceKind)` + `SelectProvider` 공개(관찰 가능 계약), `HttpFileTrackReaderProvider`(Order 280, 스풀 파일 1일 스윕), **호환 불변식: kind 없는 http URL은 종전대로 라디오** | 라우팅 매트릭스 이론 포함 |
| 풀-스풀 리더 | `HttpProgressiveTrackReader`(전체 다운로드 → `AppPaths.HttpSpoolDir` 임시 파일 → 기존 로컬 체인 오픈, Dispose 취소·삭제). 컨트롤러 콜사이트 4곳 kind 전달 | `HttpProgressiveTrackReaderTests` 3종(로컬 raw-HTTP 서버: 200+시크/404/다운로드 중 Dispose 취소) |

**결함 발견·수정(기존 코드)**: `M3u.Read`가 상대경로 병합에서 http URL을 `GetFullPath`로 깨뜨림
(`http://x` → `<dir>\http:\x`) — 라디오 URL의 M3U8 재시작 복원이 실제로는 동작하지 않았던 기존
결함. URL은 병합 제외로 수정(`M3uDpTrackTests.LegacyPlaylist_*`로 계약화).

**운영 교훈(환경·러너)**: .NET HttpClient는 "서버가 응답 기록 직후 연결을 즉시 닫는(Shutdown/Dispose)"
경우 FIN이 꼬리 데이터를 추월해 본문 읽기가 **영구 대기**함 — 로컬 테스트 더미 서버는 keep-alive
드레인(클라이언트 종료 대기)으로 응답해야 함. 처음에 "환경 전반 고착"으로 오인했으나 원시 TCP
루프백 정상·클린 서버 정상 대조 실험으로 서버 종료 시점이 원인임을 특정(규약 "메커니즘 주장 전 검증").
운영 리더와 실제 DLNA 서버(keep-alive)는 무관.

**게이트**: 콜드 리빌드 **0경고 0오류**(콜드 패스 전용 경고 CA1310·xUnit1031 2건 수정 후 재확인),
전체 테스트 **1,827/1,827 통과**(신규 19종). 스파이크 보고서·(b)안 채택 근거는 §1.2 N1 항목에 기록.
**다음 실행 항목**: 원격 FLAC/MP3 수동 재생 매트릭스(실제 네트워크 서버 대상 — N2 착수 시점에
함께), N2(DLNA)·N3(YouTube)는 사용자 지시 대기(2026-09-20 "일단 N1만 진행").

### N2 실행 기록 (2026-09-23, 개정 5판 — 커밋 대기)

| 항목 | 내용 | 검증 |
|---|---|---|
| SSDP | `SsdpDiscovery`(static M-SEARCH 4 ST·USN 중복 제거·3초 타임아웃, 네트워크 실패는 빈 목록) + `SsdpResponseParser`(순수) | `SsdpResponseParserTests` 9종 |
| 장치 기술 | `DlnaDeviceDescriptionParser`(로컬명 매칭·중첩 device 재귀·URLBase/상대 controlURL 해석·ContentDirectory 없으면 null) | `DlnaDeviceDescriptionParserTests` 9종 |
| SOAP/클라이언트 | `SoapBrowseMessage`(요청은 고전 문자열 형태 — LINQ 직렬화의 `xmlns=""` 리셋 회피, 인자 이스케이프 golden) + `ContentDirectoryClient`(HttpMessageHandler 주입형, SOAPACTION=광고된 서비스 버전, `DlnaException`) | `SoapBrowseMessageTests` 5종 |
| DIDL | `DidlLiteParser`(컨테이너/항목·res 다중·비정규 행 스킵-계속·로컬명 ns 강건) + `DidlDuration`("H:MM:SS.frac") | `DidlLiteParserTests` 12종 |
| 트랙 매핑 | `DlnaTrackFactory`(원본 포맷 우선 FLAC>WAV>ALAC>AAC>MP3>OGG, LPCM 폴백, 비디오/이미지 제외, 빈 제목→URL 폴백) | `DlnaTrackFactoryTests` 5종 |
| 아트 캐시 | `DlnaArtCache`(URL 해시·중복 재사용·64MB 상한 LRU·이미지 시그니처 검증·실패는 null) | `DlnaArtCacheTests` 5종 |
| UI | NetworkPage 섹션 전환(라디오/DLNA, 상단 탭 수법) + `DlnaSection`(서버 ComboBox·새로고침 ProgressRing·브레드크럼 클릭 점프·컨테이너 클릭 진입/트랙 더블클릭 재생·컨텍스트 재생/Now Playing 추가·"더 불러오기" 페이지네이션·세대 가드·서버 없음 빈 상태) + resw 12종×3개 국어 | 수동 체크리스트로 이월(아래) |
| 통합 | 장치XML→서버→Browse(SOAPACTION·요청 형태를 서버측에서 검증)→DIDL→Track — 가짜 HttpHandler로 결정론적으로 수행(계획의 RawHttpServer 소켓 확장 대신 — 오디오 스풀 재생 구간은 N1 통합 테스트가 이미 커버) | `DlnaClientIntegrationTests` 1종 |

**외부 파일 최소 수정(비-본 작업)**: 타 세션 산출 미추적 파일
`tests/Lyrics/Online/AlsongPluginTests.cs`의 CA2016 경고 1건이 콜드 리빌드 게이트를 깨서
`ReadAsStringAsync(CancellationToken.None)` 명시로 최소 수정(동작 불변).

**게이트**: obj/bin 삭제 콜드 리빌드 **0경고 0오류**, 전체 테스트 **1,901/1,901 통과**
(= N1 종료 1,827 + 본 작업 DLNA 50 + 타 세션 Alsong 24종 — Alsong은 병행 세션 작업분).
README 3개 국어 DLNA 항목 추가.
**다음 실행 항목(대화형 환경 필요 — 자동 게이트는 전부 통과)**: 상호운용 수동 매트릭스
(MinimServer·Windows 미디어 스트리밍에서 탐색→브라우징→재생·시크·아트 표시·서버 전환 경쟁 육안),
Narrator 스모크. N3(YouTube)는 별도 승인 대기.

---

## 1. 네트워크 소스 로드맵 (N0–N4, 개정 5판 — **제안·승인 대기**)

> 배경: 2026-09-20 사용자 방향 — "Network 탭에서 DLNA·YouTube 등 네트워크 소스 다루기".
> 리서치 요약(사실 근거): (1) **YouTube** — 오디오는 DASH 분리 스트림(Opus ~160k/AAC 128k,
> Premium AAC 256k, 업로드본의 유손실 트랜스코드)이고 스트림 URL은 IP 묶임·수 시간 만료·
> 스로틀링(n-챌린지)이 붙는다. yt-dlp는 2025.11.12부터 전체 지원에 JS 런타임(Deno 등) +
> yt-dlp-ejs를 요구(단일 exe 불완전). 결론: "yt-dlp(해석) + ffmpeg(PCM 디코드) 파이프"가
> foo_youtube·mpv ytdl_hook과 같은 표준 아키텍처. (2) **DLNA** — 관리되는 .NET UPnP AV 스택이
> 없음(Mono.Upnp 중단) → SSDP + ContentDirectory
> SOAP + DIDL-Lite 파싱을 직접 구현. Dawn을 **클라이언트**(서버 탐색 → HTTP 수신 → 자체 엔진
> 재생)로 만들면 갭리스·DSP·WASAPI 독점 출력이 그대로 유지된다(렌더러/푸시 수신은 별개 역할).
> (3) **엔진 확장점** — `ITrackReaderProvider` 체인 + `RadioStreamReader` 선버퍼·언더런 실음
> 패턴 재사용 가능. 단 현재 http/https 전량이 Order 300 라디오(MP3) 프로바이더에 인터셉트됨.

### 1.1 마일스톤 개요

| 마일스톤 | 주제 | 핵심 산출물 | 규모 |
|---|---|---|---|
| N0 | Network 탭 셸 + 소스 모듈 프레임워크 | 탭 추가(`NavigationStateCalculator` 확장), 섹션 구조, 라디오 모듈 승격(URL 열기 이전 + 방송국 즐겨찾기 저장소 + ICY 방송중 곡명 UI 연결) | S |
| N1 | 원격 트랙 공통 기반 | `Track` 소스 종류 필드 + M3U8 영속 확장(`#DPTRACK`), 리더 라우팅 재설계(컨텍스트 기반), HTTP 프로그레시브 리더(Range 시크) | M |
| N2 | DLNA 클라이언트 | SSDP 탐색, ContentDirectory 브라우징(SOAP+DIDL-Lite), 아트 캐시, 서버 트랙 재생·재생목록 추가 | M–L |
| N3 | YouTube | yt-dlp 해석 + ffmpeg PCM 파이프 리더, 의존성 감지·`-U` 갱신 안내, 설정 UI | M–L + 상시 운영 |
| N4(선택) | 확장 소스 | DLNA 렌더러(푸시 수신), 팟캐스트(RSS), Subsonic/Navidrome | — |

### 1.2 구현 상세 (N0–N2 파일 수준 — 2026-09-20 코드 조사 기반)

> 조사 근거: 탭=Visibility 전환 + `NavigationStateCalculator`(WinUI-free, 테스트 링크됨),
> 재생목록=M3U8(`AtomicFile` 스택), DI=`AppServices` 정적 컴포지션 루트, 리더 팩토리=`Open(string path)`
> 문자열 시그니처(콜사이트 4곳: PlaybackController 201/340/571/1050). 로드맵 초안 대비 **설계 정정**:
> (1) DB 마이그레이션 불필요 — 원격 트랙의 library.db 비저장 원칙(라디오 전례) 유지, 영속은
> M3U8 확장으로. (2) 메타데이터/아트 캐시는 N2로 이동(첫 실수요가 DLNA 아트). (3) 모듈 등록형
> 인터페이스(`INetworkSourceModule`)는 유보 — 섹션 3개는 UserControl+컨벤션으로 충분, 4번째 소스
> 시점 승격(YAGNI). 섹션 프레임워크=N0의 배치·활성화·resw·테스트 링크 컨벤션으로 정의.

**공통 원칙 (N0–N2)**

- 원격 트랙의 library.db 비저장·스캔/통계/스코블 제외 유지. 제외 판정은 URL 휴리스틱
  (`RadioTrack.IsStreamUrl` 산재)을 `Track.SourceKind != File` 일반화로 교체하되, 기존 http URL은
  라디오 취급을 유지(호환 불변식).
- 신규 App 코드 중 테스트 대상 로직은 WinUI-free 순수 클래스로 분리하고 테스트 csproj
  링크 목록에 추가(누락 시 CS0246 — 규약 §5).
- resw는 3개 국어 동시 추가(ko-KR 기준 카탈로그, 키 패리티·자동화 이름 게이트 대상).
- 백그라운드→UI는 `AppServices.RunOnUi` 퍼널과 세대 스탬프 가드(`SmtcService._currentUpdateVersion`
  패턴) 준수. 컬렉션 순회는 스냅샷.

**N0 — Network 탭 셸 + 라디오 모듈 (S)**

*설계 결정*

1. **탭 확장**: `MainWindow.xaml` TopNavPanel(~L98)에 `TabNetwork` 라디오버튼(x:Uid
   `MainWindow_Tab_Network`, AutomationId, `EoleNavTabStyle`), ContentHost에
   `<views:NetworkPage x:Name="NetworkPageView" Visibility="Collapsed"/>`. 
   `NavigationStateCalculator.cs`: `NavigationViewState`에 `TabNetworkChecked`/`NetworkVisible`
   추가, `NetworkTab` 상수 + `NormalizeTab` 양방향 매핑(미확장 시 "unknown→Library" 폴백이
   Network을 삼킴 — 확장 필수), `ForTab`/`ForSettings`/`ForLyricsToggle` 분기. 파급: record struct
   위치 생성자 변경 → `new(...)` 전 콜사이트(탭 상태 적용 309–336, Settings 진입 676)와 기존
   내비게이션 테스트 갱신. `Settings.Ui.LastNavTab`(문자열) 기존 필드 재사용.
2. **NetworkPage**: `Views/NetworkPage.xaml(.cs)` + `ActivatePage()` 패턴(PlaylistPage:69 준용).
   섹션 전환은 상단 탭과 동일 수법(라디오버튼+Visibility). N0 섹션: 라디오 1개(N2에서 DLNA 추가).
3. **방송국 즐겨찾기 저장소**: `src/DawnPlayer.Core/Persistence/RadioStationStore.cs` —
   `SettingsStore` 스택 복제(`AtomicFile.WriteAllText` keepBackup+flushToDisk, 로드 `.bak` 폴백,
   `Normalize`: null 이름 기본값·URL 중복 제거·개수 상한 500). 모델 `RadioStation`(record:
   Name, Url, Genre?, AddedUtcTicks, LastPlayedUtcTicks?). `AppPaths.NetworkStationsFile` =
   `network-stations.json` + `...In(baseDir)` 헬퍼. 내부 lock 즉시 저장(편집 빈도 낮음 — debounce
   불필요). `AppServices` 싱글톤 등록 + 정적 접근자 + `Shutdown`에서 `Playlists.SaveAll()` 직후 저장.
4. **재생 연결**: 스테이션 재생 = `RadioTrack.Create(url)`(Title=스테이션명) → Now Playing 추가 →
   `PlaybackUiHelper.PlayItemAsync` 재사용(기존 `OnMenuOpenUrl` 409–444 로직을 RadioSection으로
   이전). 타이틀 메뉴 "네트워크 스트림 열기(라디오)"는 Network 탭 라디오 섹션의 "URL 추가" 플로우를
   여는 진입점으로 재연결(이중 구현 제거).
5. **ICY 곡명 표시**: Core `Audio/ILiveMetadataSource.cs`(`StreamTitleChanged`, `StationName`) —
   `RadioStreamReader` 구현(icy-name 노출 추가). `PlaybackController`: 리더 생성 시 인터페이스
   확인→구독→`StreamTitleChanged(item, title)` 중계, TrackLeft/세션 종료 시 해지(구독 누수 방지
   불변식). `AppServices` 정적 이벤트 + `RunOnUi`. 표시: `PlaylistItem.NowPlayingSubtitle`
   (Volatile+INPC — `RemainingTimeText` 패턴) + 순수 포매터(스테이션명·곡명 조합, 빈값 규칙) →
   NowPlayingBar 아티스트 라인·FullscreenNowPlayingWindow·SMTC(`SmtcMapping.ForStreamTitle`,
   세대 가드) 갱신.

*작업 순서*: (1) 캘컬레이터+탭 확장+테스트 갱신 (2) RadioStationStore+AppPaths+AppServices
(3) RadioSection UI+resw 3개 국어 (4) 메뉴 재연결 (5) ICY 중계 체인+UI/SMTC (6) 콜드 리빌드+
전체 테스트 1회.

*선설계 테스트(코드 선행)*: `NavigationStateCalculator_NetworkTab_*`(정규화·상태·Settings 진입 시
언체크·가사 토글 조합), `RadioStationStore_*`(왕복/손상→bak 폴백/URL 중복 제거/Normalize 상한/
동시 저장), `PlaybackController_RelaysStreamTitle`·`UnsubscribesOnTrackLeft`(가짜
ILiveMetadataSource 리더를 고순위 테스트 provider로 Register), `NowPlayingSubtitleFormatter_*`.

*완료 기준*: 기존 라디오 URL 재생 무회귀 계약, 스테이션 영속(재시작 복원), ICY 곡명이 바·
풀스크린·SMTC에 표시, resw 패리티 게이트 통과, 콜드 리빌드 0경고+전체 스위트.

**N1 — 원격 트랙 공통 기반 (M)**

*설계 결정*

1. **소스 종류**: `Core/Models/TrackSourceKind.cs`(File=0, Radio=1, Dlna=2, YouTube=3). `Track`
   말단 위치 파라미터 + 기본값 File(기존 생성 호환). `AlbumKey` 불변 — 새 필드 미참여 확인.
   `RadioTrack.Create`가 Radio 설정. library DB 무변경(비저장 원칙).
2. **M3U8 영속 확장**: 항목 직전 `#DPTRACK:<uri-escaped json>` 1행에 SourceKind+표시 메타데이터
   (title/artist/album/durationMs/artUrl). `M3u.Read`는 미지행 무시 원칙(타 플레이어 호환),
   `M3u.Write`가 부가. 역호환: `#DPTRACK` 없는 http URL → 기존대로 라디오.
3. **라우팅 재설계**: `AudioFileReaderFactory.Open(TrackOpenContext)`(path+SourceKind) 추가,
   기존 `Open(path)`는 컨텍스트 래핑 유지. `ITrackReaderProvider`에 `CanOpen(in TrackOpenContext)`
   오버로드 추가(기본 구현은 기존 path 버전 위임 — 하위 호환). 콜사이트 4곳 전환.
   `RadioStreamTrackReaderProvider`(Order 300): kind==Radio 또는 Unknown+http(현행 유지).
   신규 `HttpFileTrackReaderProvider`(Order 280): kind==Dlna 담당.
4. **HttpProgressiveTrackReader**: 착수 전 스파이크 — `tools/FormatProbe` 확장으로 HTTP URL의
   MF 직접 열기 실측(FLAC/AAC·지연·시크 필요). 채택 시 (a) MF-over-URL 래핑,
   미채택 시 (b) 스풀 방식(HttpClient Range 순차 스풀러+선버퍼 → 기존 로컬 리더 재사용).
   유한 소스 언더런 규칙: 15초 무진행 시 정상 종료(예외 아님) — 라디오의 "실음 무한 지속"과
   대비되는 불변식. Range 미지원 서버(200 전체 응답) 폴백 포함.

   **스파이크 보고서(2026-09-20 실행, 채택 결정)**: `FormatProbe --http` 모드로 로컬 서버
   (Range 지원 HTTP/1.1·미지원 HTTP/1.0 두 종)에서 FLAC/MP3/M4A/WAV 4포맷 실측 — **전 조합
   실패**(MF 오류 0xC00D0029/0xC00D426A, 포맷·서버 무관 약 5.2초 타임아웃형 — 컨테이너가 아니라
   연결/바이트스트림 계층 실패). 프록시 없음(netsh 직접 연결 확인). 부수 확인: 현행 팩토리 체인은
   http URL 전량을 라디오 프로바이더가 인터셉트 후 실패(라우팅 재설계 동기 재확인).
   **결정: (a) MF-over-URL 기각, (b) 풀-스풀 방식 채택** — HttpClient 전체 다운로드(취소 가능) →
   임시 파일 → 기존 로컬 리더 체인(Mf/Vorbis) 재사용. 효과: 모든 로컬 지원 포맷이 그대로 원격
   재생 가능, 시크·길이 완전, Range 미지원 서버도 동작. 트레이드오프: 첫 재생 시작 지연 =
   다운로드 시간(LAN 수백 ms~수 초) — U2 로딩 패턴으로 표시. 재생 중 언더런 자체가 소멸(로컬
   파일 재생)하여 "15초 무진행 종료" 규칙은 다운로드 단계 취소/타임아웃으로 흡수.

*작업 순서*: (1) FormatProbe 스파이크+보고서(채택 결정 기록) (2) TrackSourceKind+Track 파급 점검
(3) M3U8 확장 (4) Open 컨텍스트+콜사이트 (5) 리더 구현(스파이크 경로) (6) 라디오 무회귀 계약 강화.

*선설계 테스트*: `Factory_CanOpen_Matrix`(kind×scheme×확장자), `M3u_*`(`#DPTRACK` 왕복/
레거시 http URL 라디오 유지/미지행 무시/이스케이프 경계), `HttpProgressiveTrackReader_*`
(로컬 HttpListener 게이트형: Range 206/200 전체/416/끊김→유한 종료/시크 정확도 — 환경 플래그
스킵 패턴), `Radio_NoRegression`.

*완료 기준*: 스파이크 보고서와 채택 근거 기록, 원격 FLAC/MP3 재생·시크 수동 매트릭스(로컬 정적
서버), 전체 게이트.

**N2 — DLNA 클라이언트 (M–L) — 구현 상세(파일 수준)**

> **설계 정정(1건)**: Rssdp NuGet 대신 **자체 SSDP M-SEARCH 클라이언트** 확정. 근거:
> Rssdp는 MIT이나 유지보수 부재(2017 era beta가 최종권)·상호운용 이슈 보고(장치 미검출),
> 필요 범위는 멀티캐스트 검색 요청 하나이고 DIDL/SOAP는 어차피 자체 구현이며, N1 교훈
> (외부 네트워킹 스택은 블랙박스 디버깅 비용 — MF-over-URL·테스트 서버 사례)까지 감안하면
> ~150줄 자체 구현이 검증성에서 우세. 스파이크 보고서 없이 착수(프로토콜은 표준 텍스트 기반,
> fixture 테스트로 계약화 가능 — 실서버 상호운용은 완료 기준의 수동 매트릭스로 검증).

*Core 구성 — `src/DawnPlayer.Core/Network/Dlna/` 아래 8종 (의존성 없음, 기존 HttpClient 관례 준용)*

1. **`SsdpDiscovery.cs`** — `Task<IReadOnlyList<SsdpDeviceHit>> SearchAsync(TimeSpan timeout,
   string[] searchTargets, CancellationToken)`. UdpClient로 239.255.255.250:1900 멀티캐스트
   M-SEARCH(MAN "ssdp:discover", MX=3, ST=`urn:schemas-upnp-org:device:MediaServer:1/2` +
   `…service:ContentDirectory:1/2`), 타임아웃까지 수신 수집, USN 중복 제거. 소켓 계층은 얇게
   두고 파싱은 순수 클래스로. `SsdpDeviceHit{Location(Uri), Usn, St, ServerHeader}`.
2. **`SsdpResponseParser.cs`** (순수) — 데이터그램 문자열 → hit(LOCATION·USN 필수, 결누 폐기,
   헤더명 대소문자 무시).
3. **`DlnaDeviceDescriptionParser.cs`** (순수) + `DlnaServer` 모델{DescriptionUrl, FriendlyName,
   Udn, ControlUrl}. 장치 기술 XML을 XDocument 로컬명 매칭으로: friendlyName/UDN/중첩
   device·deviceList 재귀/서비스 중 serviceType에 "ContentDirectory" 포함 → controlURL
   (URLBase 요소 또는 LOCATION 기준 상대 해석). ContentDirectory 없으면 null(미지원 장치).
4. **`ContentDirectoryClient.cs`** — `Task<DlnaBrowsePage> BrowseAsync(DlnaServer, objectId,
   startIndex, requestedCount, CancellationToken)`. SOAPACTION 헤더
   `urn:schemas-upnp-org:service:ContentDirectory:1#Browse`, text/xml POST.
   `DlnaBrowsePage{Objects, NumberReturned, TotalMatches}` — 대형 컨테이너 페이지네이션의 기반.
5. **`SoapBrowseMessage.cs`** (순수) — 요청 엔벨로프 빌더(ObjectID 등 XML 이스케이프) + 응답에서
   Result(이스케이프된 DIDL 문자열)·NumberReturned·TotalMatches 추출.
6. **`DidlLiteParser.cs`** (순수) — `DidlContainer{Id, ParentId, Title, ChildCount, Class}` /
   `DidlItem{Id, ParentId, Title, Artist, Album, Class, DurationMs?, AlbumArtUri?, Resources[]}` /
   `DidlResource{Uri, ProtocolInfo, SizeBytes?, Duration?, Bitrate?}`. 로컬명 매칭(dc:title,
   upnp:artist, upnp:album, res@protocolInfo/size/duration, upnp:albumArtURI), duration
   "H:MM:SS.frac" 파싱, 비정규 자식은 스킵+로그하고 계속(서버별 비표준 편차 대응).
7. **`DlnaTrackFactory.cs`** (순수) — item → `Track{SourceKind.Dlna}`. `res@protocolInfo`의
   원본 오디오 우선(FLAC>WAV>ALAC>MP3>AAC>OGG), 서버 트랜스코드(LPCM/L16)는 원본이 없을 때만,
   비디오/이미지 res 제외. mime→스풀 확장자 매핑, Path=절대 res URI, 메타데이터 매핑.
8. **`DlnaArtCache.cs`** — `GetOrDownloadAsync(url)` → 기존 `ArtCacheDir`에 URL 해시 파일명으로
   다운로드/재사용, 총 용량 상한(64MB) 초과 시 오래된 파일 제거. HttpClient 주입 가능(테스트).

*App 구성*

9. **NetworkPage 섹션 전환 추가** — 현재 라디오 단독 → 섹션 셀렉터(라디오버튼: 라디오/DLNA)+
   Visibility 전환(상단 탭과 동일 수법), `ActivatePage`에서 활성 섹션 Activate.
10. **`Views/Network/DlnaSection.xaml(.cs)`** — 서버 ComboBox+새로고침(ProgressRing, U2 패턴),
    브레드크럼(루트→현재 컨테이너, 클릭 이동), 항목 ListView(컨테이너 행: 폴더 아이콘·자식 수 /
    항목 행: 제목·아티스트·길이), 더블클릭 재생 + 컨텍스트 메뉴(지금 재생/Now Playing 추가),
    RequestedCount 500 + 목록 끝 "더 불러오기", `_browseGeneration` 세대 가드(서버·컨테이너 전환 시
    이전 응답 폐기 — SmtcService 패턴), 빈/오류 상태(서버 없음 안내). 재생 연결은 RadioSection과
    동일(`PlaylistManager.AddTracks` + `PlaybackUiHelper.PlayItemAsync`), 아트는 DlnaArtCache가
    `Track.ArtPath`에 로컬 파일을 심어 기존 파이프라인 재사용.
11. **AppServices 변경 없음** — Core 클래스는 무상태, 섹션 코드비하인드에서 직접 사용(RadioSection
    컨벤션). resw 3개 국어 `Network_Dlna_*` ~12종(섹션명/새로고침/서버 없음/더 불러오기/재생/추가/오류).

*작업 순서*: (1) SSDP 파서+검색 + 테스트 (2) 장치 기술 파서 + 테스트 (3) SOAP 클라이언트+메시지 +
테스트 (4) DIDL 파서 fixture 전수 (5) TrackFactory+ArtCache + 테스트 (6) DlnaSection UI+섹션 전환+
resw (7) 통합 테스트 + 콜드 리빌드/전체 게이트. 각 단계 필터 테스트, 전체는 종료 시 1회(규약 §4).

*선설계 테스트(코드 선행)*: `SsdpResponseParser_*`(정상/LOCATION·USN 결누 폐기/헤더 대소문자/
CRLF·LF 혼용), `DlnaDeviceDescriptionParser_*`(정상/상대 controlURL/URLBase/중첩 device/
ContentDirectory 없음/비정규 XML→null), `SoapBrowseMessage_*`(요청 golden·ObjectID 이스케이프/
응답 Result 이스케이프 해제·페이지 필드), `DidlLite_*`(정상/컨테이너+항목 혼합/확장 ns·접두 변형/
비정규 항목 스킵/빈 DIDL/다중 res/duration 이론 "0:03:45.500"·"1:02:03"·빈), `DlnaTrackFactory_*`
(FLAC>LPCM 우선/비디오 res 제외/mime→확장자/메타 매핑), `DlnaArtCache_*`(해시 재사용/용량 상한
LRU/다운로드 실패→null), 통합 1종 게이트형(N1 `RawHttpServer` 패턴 확장 — 장치 XML+SOAP 더미 →
DlnaServer→Browse→Track 생성까지; 오디오 재생 구간은 N1 통합이 이미 커버).

*실패 시나리오(규약 §3, 위 테스트로 계약화 + 수동)*: 서버 이탈 mid-browse(세대 가드), 비표준 DIDL
(fixture 다양성), 대형 컨테이너(페이지네이션), res URL 만료·404(풀-스풀 실패→경고, N1 규칙),
SSDP 멀티캐스트 차단(타임아웃→"서버 없음" 상태), 동명 다수 서버(UDN 구분 표시).

*완료 기준*: fixture 단위테스트 전수 + 통합 1종, DLNA 트랙의 M3U8 저장·복원 왕복(N1 `#DPTRACK`
kind=Dlna — 인프라 그대로), 재생목록 편집 무결성, 콜드 리빌드 0경고+전체 스위트 1회,
**상호운용 수동 매트릭스(MinimServer·Windows 미디어 스트리밍 — 사용자 환경, 완료 기준의
실행 항목)**, README 3개 국어 갱신(N2 완료 시점).

**N3 — YouTube (M–L, N0–N2 완료 후 별도 승인 — 착수 시 위 수준으로 상세화)**

1. 해석: `yt-dlp -J`(제목·길이·썸네일·bestaudio 포맷) → 재생: `yt-dlp -f bestaudio -o -`를
   ffmpeg(`-f s16le` 파이프)로 PCM화 → `RadioStreamReader` 패턴의 `YouTubeStreamReader`.
   재생목록에는 만료되는 미디어 URL이 아니라 **페이지 URL**을 보관.
2. 의존성: **감지형 권장** — PATH 탐색(yt-dlp/ffmpeg/Deno) + 설정 페이지 상태 표시·설치 안내,
   미설치 시 모듈 비활성 안내. 동봉 전략(인스톨러 +100MB 내외 + 갱신 추종 부담)은 별도 결정.
   해석 실패 시 `yt-dlp -U` 갱신 유도.
3. 제약 명시: 소스는 유손실(Opus ~160k / Premium AAC 256k), YouTube ToS 위반 회색지대 —
   UI·README(3개 국어)에 명시.
4. 실패 시나리오: PO 토큰 벽·스로틀(JS 런타임 부재), 연속 해석 레이트리밋(429), 연령 제한
   (쿠키 옵션), 실시간 스트림(길이 0 — 라디오 경로 재사용), 프로세스 파이프 파손·행(타임아웃+
   재시작), 시크 = 오프셋 재시작 비용.
5. 착수 전 스파이크: 시작 지연·언더런·시크 비용 실측, visionos 기본 클라이언트 무런타임 동작 확인.

### 1.3 리스크 및 완화 (N 계열)

| 리스크 | 완화 |
|---|---|
| YouTube 측 변경(PO 토큰·포맷)으로 상시 파손 | yt-dlp 갱신 추종, 해석 실패 원인 UI 표시, N3는 기능 플래그로 격리 |
| 외부 프로세스(yt-dlp/ffmpeg) 수명 관리 | 프로세스 래퍼 인터페이스화(테스트 목 대체), 좀비 프로세스 방지 불변식 |
| DLNA 서버별 비표준 준수 편차 | fixture 기반 파서 강건성 + 주요 서버 상호운용 매트릭스 |
| M3U8 확장(`#DPTRACK`) round-trip 회귀 | 레거시 형식 왕복 테스트 + 미지행 무시 원칙 + "원격 트랙 library.db 비저장" 불변식 게이트 |
| http 라우팅 재설계로 라디오 회귀 | 라디오 경로 계약 테스트 선보강 후 착수 |

### 1.4 승인 대기 결정 사항 (2026-09-20)

1. **착수 범위**: 권장 **N0–N2**(YouTube는 DLNA 완성 후 별도 승인). 대안: N0만 / N0–N3 전체.
2. **DLNA 렌더러(스마트폰→Dawn 푸시 수신)**: 권장 **제외**(N4 선택 유보 — 구현량 크고 갭리스·
   DSP 제어권 제한).
3. **YouTube 의존성 배포 전략**: 권장 **감지형**(동봉 시 인스톨러 증량 + yt-dlp 갱신 부담).

---

## 2. 상시 검증 원칙 (마일스톤 공통)

[AGENTS.md](AGENTS.md) §3–§5 적용 + 개정 4판 UI 계열에서 이관된 상시 게이트:

- 착수 전: 결함 트리 + 실패 시나리오 테스트 선설계 (각 마일스톤 절 참조).
- 진행 중: 타깃 필터 테스트만. 종료 시: 콜드 리빌드 0경고 + 전체 테스트 1회.
- UI 변경 시: Narrator 스모크(재생 제어·탐색), 대비 스팟 체크, 스크린샷 리뷰, reduced-motion·
  HC·Light 3테마 렌더 스모크(U1 이후 상시).
- 배포는 사용자 명시 요청 시에만(규약 §2).

## 3. 즉시 실행 가능한 다음 액션

1. **N0·N1·N2 완료(§0 실행 기록, 커밋 대기)** — 커밋 시점·분할은 사용자 판단. **N3(YouTube)는
   별도 승인 대기**(N2 세부 계획은 §1.2 참조).
2. **N0·N2 수동 체크리스트** — 실제 라디오 스트림 ICY 곡명·즐겨찾기, DLNA 상호운용 매트릭스
   (MinimServer·Windows 미디어 스트리밍), Narrator 스모크(다국어).
3. **U0–U5 수동 체크리스트** — Narrator 스모크(다국어), 밀도 전환·컴팩트 바·풀스크린 파형 육안
   확인, reduced-motion 환경 시나리오 (자동 게이트는 전부 통과).
4. v1.2.0 배포(태그 푸시 + GitHub Release) 여부 — 사용자 명시 요청 대기.
5. M4 실기기 청음 체크리스트 — DSD/DoP 지원 DAC 환경에서 기회 시 수행.

### L0. Alsong 가사 플러그인 (2026-09-25 완료 — N 계열 외 별도 항목)

> 원본: `wolfdate25/alsong-lyrics-searcher`(foobar2000 ESLyrics 스크립트).
> 실측(2026-09-25): `GetResembleLyric2` SOAP 엔드포인트 살아있음(밤편지/아이유 → 200·후보 100개·
> 싱크 가사). `GetLyric7`(파일 해시)은 외부 md5 서버 의존으로 사망 — 이식 제외.

| 항목 | 내용 | 검증 |
|---|---|---|
| 플러그인 | `samples/AlsongLyricsPlugin/`(netstandard2.0·무외부 의존) + slnx·테스트 `ProjectReference` 등록 | 콜드 리빌드 **0경고 0오류** |
| 계약 매핑 | `SearchAsync`=제목/아티스트 SOAP 검색(둘 다 비면 무호출 빈 목록), `GetAsync`=캐시 적중→즉시 / 미스→내장 쿼리 재검색 후 `strInfoID` 일치분. `ResultId`=Base64url(infoId·title·artist) | `AlsongPluginTests` 24종 |
| 가사 정제 | `HtmlDecode` → `<br\s*/?>` 분할(원본 `/br/g` 버그 수정) → 싱크는 `SyncedLrc`+타임스탬프 제거 `PlainText`, 비싱크는 `PlainText`만 | fixture 이론 포함 |
| 문서 | README 3개 국어에 동봉 플러그인 1줄 추가 | — |

**불변식**: 깨진 가사를 절대 넘기지 않기 — 파서 방어 + throw(HTTP 오류·파싱 실패, 호스트가 다음
플러그인으로 이동)/빈 목록(미발견)/null(무효 ResultId) 구분을 테스트로 계약화.
**미실행 항목**: 전체 스위트 1회 — 트리의 미커밋 N2 WIP(`DidlEntry` 미정의 CS0246)가 Core
컴파일을 막아 실행 불가. 해당 WIP 정리 후 전체 게이트 실행 필요. (검증은 격리 하네스
`E:\scratch-alsong-harness\`에서 24/24 통과로 대체 — repo 밖 스크래치, 삭제하지 말 것.)

**후속 결함·수정 (2026-09-25, 배포 후 보고)**: 플러그인 DLL을 올바른 포터블 경로에 둬도
설정 목록이 "설치된 플러그인이 없습니다"로 고정되는 결함. 원인: `SettingsPage`가 보는 정적
`AppServices.LyricsOnline`에 값을 대입하는 코드가 저장소 전체에 없음(DI 쪽 진짜 인스턴스는
따로 로드되어 로그에만 찍힘). `다시 스캔`도 null 가드로 무동작. 수정 1줄
(`AppServices.cs`: 컨테이너의 `LyricsOnlineService`를 정적 속성에 대입) + 회귀 테스트
`LyricsPluginListWiringTests` 3종(격리 하네스 `E:\scratch-vm-harness\`에서 3/3 통과).
**적용 조건**: `DawnPlayer.App` 재빌드·재배포 후 확인 필요(H:\ 배치는 09-20 빌드라 수정 미포함).
배포 바이너리 기준 E2E(밤편지/아이유 → 후보 100개·동기 가사 27줄)는 PASS.

**마감 (2026-09-25)**: N2 WIP 소유자가 `DidlEntry` 정의를 추가해 차단 해제 →
`build-installer.ps1 -Version 1.2.2 -SkipInstaller`로 포터블 ZIP 생성
(`dist/DawnPlayer-v1.2.2-portable-win-x64.zip`, 106.1MB, SHA256SUMS 기록) →
콜드 리빌드 **0경고 0오류** → 전체 스위트 **1904/1904 통과**(신규 27종 포함).
배포(태그·Release·H:\ 교체)는 사용자 판단 대기(규약 §2).

### L1. 보조창(가사 검색·편집) 크기 유지 (2026-09-25 승인·구현)

> 요청: 리사이즈한 창 크기가 다음 실행에도 유지될 것. 위치·최대화는 범위 외.

| 항목 | 내용 | 검증 |
|---|---|---|
| 수학 | `Core/Util/AuxWindowSize.cs` 신규(WinUI-free): 유효성·DIP 변환·clamp | `AuxWindowSizeTests` 28종(100사이클 드리프트 포함) |
| 설정 | `UiSettings`에 nullable 4종(`LyricsSearch/EditorWidth/Height`, null=미조정) | JSON 왕복 테스트 |
| 후킹 | 양 창 ctor 복원 + `Closed` 저장, 최대화 종료 시 저장 스킵, `WindowPlacementHelper` 크기 전용 분리(메인 배치 불간섭) | App 빌드 0경고(증분, 포터블 시 콜드 게이트) |

### L2. 가사 패널 헤더 2행화 (2026-09-25 지시·구현)

> 원인: 44px 버튼 4개(185px 고정)가 최소 폭(200px) 패널을 넘쳐 오른쪽 버튼부터 잘림.
> 지시: 텍스트 위·버튼 아래 2행, 버튼 우측 정렬·축소·좁은 마진.

| 항목 | 내용 | 검증 |
|---|---|---|
| 레이아웃 | `LyricsPane.xaml` 단일 수정(양 페이지 공유): 1행 제목 + 2행 `[출처 뱃지 | 버튼]` 공유, 버튼 30px·Spacing 2, 마진 12,6,12,4, 제목·뱃지 말줄임. 전 `x:Uid`·동작 유지 | App 빌드 0경고 + 자동화/현지화 게이트 통과 |

**마감 (2026-09-25)**: L1+L2 포함 포터블 `v1.3.0` 생성
(`dist/DawnPlayer-v1.3.0-portable-win-x64.zip`, 106.1MB, SHA256SUMS 기록) →
전량 재컴파일 **0경고 0오류** → 전체 스위트 **1932/1932 통과**.
배포(태그·Release·H:\ 교체)는 사용자 판단 대기(규약 §2).
