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

> **as-built 세부 구현서**: [docs/n2-dlna-implementation.md](docs/n2-dlna-implementation.md)
> (2026-09-26 작성 — 완료 기준 6건 대조, 계획 대비 편차 4건, 잔여 갭 G1–G4 파일 수준 명세+
> 수동 매트릭스 체크리스트 M1·M2. 아래 기록 표에 누락됐던 "M3U8 저장·복원 왕복"은
> `M3uDpTrackTests.RemoteTracks_RoundTripWithKindAndMetadata`로 실제 충족 확인.)

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

### 0.18 UI/UX 전면 감사 시정 (2026-09-30, L12)

**배경**: ui-ux-pro-max 스킬 기준으로 서브 에이전트 5개 파트(셸/토큰, 라이브러리, 재생 컨트롤,
네트워크·설정·대화상자, 횡단 접근성) 독립 감사 → 총 91건(P0 2·P1 17·P2 40·P3 32).
보고서: `docs/ui-ux-audit-2026-09-30.md`. 사용자 승인 후 배치 1–6 시정 실행.

**시정 완료 (36건)** — P0 2건 전부 포함:
- **P0 대비(PT1-01/02, PT5-04/05)**: `DawnAccentTextBrush` 신설(라이트 #8F5408, 다크=액센트) —
  액센트 전경 28곳 XAML+7곳 코드비하인드 전환, `ContrastMath.SolveTextVariant` 순수 솔버로
  프리셋/커스텀 액센트 동적 재계산, TextTertiary 조정(다크 #8F8FA0·라이트 #62626F),
  게이트 2종 추가(`TextTertiary_MeetsTextContrast_*`, `AccentText_MeetsTextContrast_*`).
  이모지 아이콘 💾 → FontIcon(E78C) 전환(PT4-15/PT5-16, resw 3개 국어 동기).
- **P1 신뢰성**: 라디오 컨텍스트 메뉴 캐스트 불일치 수정(PT4-01), DLNA 재시도 버튼 팬텀 컬럼
  수정(PT4-02), YouTube SetBusy 상태 분리(PT4-03), DLNA 빈 상태 액션 버튼(PT4-13),
  라이브러리 4개 면 컨텍스트 메뉴 flyout.Target 기반 타깃 확정(PT2-01~04), 트랙 표 우클릭-선택
  갱신 + 무음 첫 트랙 폴백 제거(PT2-01), 시크바 드래그/휴지 라벨이 썸 추종 + 호버 프리뷰(PT3-05,
  `CalculateDraggingLabels` 순수 함수 + 실패 시나리오 테스트).
- **P1 접근성**: 버튼류 스타일 12종 `UseSystemFocusVisuals` 복원(PT3-01), 트랙 리스트 4곳 +
  드로어 행 Enter/Space 재생(PT5-01/02), 톱니·검색·평점 셀 자동명 + EQ 밴드 동적 접근명
  (PT5-03/06/14/15, resw 3개 국어), 고대비 실시간 재평가(PT1-04).
- **P1 데이터**: 가사 편집기 스냅샷 dirty 확인 + Esc(PT4-04/PT5-20), 재생목록·방송국·최근 항목
  삭제 확인(PT2-06/PT4-05), RG 전체 재분석 확인 + 스캔 버튼 잠금/완료 이벤트(PT4-10),
  가사 덮어쓰기 기본 버튼 취소로(PT4-11).
- **P2 완결성/폴리시**: 트리 확장 상태 재빌드 보존(PT2-05), 창 복원 가상 데스크톱 클램프(PT1-06,
  순수 클램프 함수), 가사 자동 스크롤 호버 억제(PT3-04), 전체화면 Space/←→ + 종료 힌트(PT3-02/03,
  PT5-17), 목록 선택 하이라이트 액센트 동기(PT1-05), AccentButton #141414 통일(PT1-13),
  "Mixed selection" 지역화(PT2-18), 배지 11px(PT1-10), 재생 버튼 120ms 페이드(PT3-06).

**검증**: 클린 리빌드 0경고 0오류, 전체 테스트 **2,120/2,120 통과**(감사 전 2,069 → 신규 게이트·
솔버·시크바 테스트 51종 추가).

**Wave 2 시정 완료 (2026-10-01, 사용자 지적 직후 — 25건 추가, 총 61건)**:
- **PT5-03 완결**: Settings 무명 컨트롤 46개 라벨링(행 Title 텍스트 기반 `_A11yName` 키 신설,
  3개 국어; EQ 밴드 7종은 1차 완료) — VM 델리게이트 회귀(AppServices 미링크 CS0234)는
  `warningNotifier` 생성자 주입으로 해결.
- **PT1-07/02-16/03-09 토큰 수렴**: 오프스케일 FontSize 120건을 `{StaticResource FontXxx}` 토큰으로
  전환(9~16 → Caption/BodySmall/Body/Subtitle/Title), 코드비하인드 4곳 DesignTokenValues 사용.
- **히트 타깃(PT2-17/03-14/05-12)**: 24~30px 버튼을 32px로(플러그인 이동 2, 대기열 제거+열,
  가사 헤더 4, 라이브러리 툴바 6), RatingCellButton Min 20, 슬라이더 MinHeight 24.
- **PT1-09/05-07 스플리터 키보드 대체**: `SplitterResizer.EnableKeyboardResizing` — Tab 포커스 +
  ←/→ 8px 조절 + 자동명(5개 스플리터, resw 3개 국어).
- **PT1-03 설정 위치 표시**: 설정 진입 시 톱니 아이콘 액센트 전경(탭 해제 상태에서 유일한
  위치 마커), 이탈 시 복원.
- **PT4-06 Last.fm**: Header 라벨 + 시크릿 PasswordBox 마스킹(PasswordChanged 동기화, 로드 시 시딩).
- **PT4-07/08/09 무음 무시 제거**: 커스텀 HEX blur 커밋+필드 옆 오류, LRC 패턴 필터 요약 통보,
  감시 폴더 중복 경고+제거 확인.
- **PT4-12(라디오) 재생 실패 인라인 상태 유지**, **PT2-14 M3U8 내보내기 성공 토스트**,
  **PT5-08 타이틀바 트랙 LiveSetting**, **PT3-06/02-09 120ms 암시적 전환**(CommonStates 4그룹).

**검증**: 클린 리빌드 0경고 0오류, 전체 테스트 **2,120/2,120 통과**. resw 게이트가 신규 키 누락을
2회 실제로 잡아냄(EQ 포맷 키 접미사, 스플리터 3키).

**Wave 3 시정 완료 (2026-10-01, PT2-08 + PT4-14)**:
- **PT2-08 필터 비동기화**: `LibraryFilterService.FilterAndSort`(순수)를 Task.Run으로 이전,
  UI 스레드 스냅샷 복사로 스캔 동시 변경 방어, 신규 순수 클래스 `Views/Library/FilterRequestGate.cs`
  (세대 게이트 — 역순 완료 시 낡은 결과 폐기, DLNA 브라우즈 가드와 동일 규율)로 경쟁 상태 차단,
  카드 생성(UI 객체)은 UI 스레드 유지, 계산 예외 시 기존 목록 유지+로그.
  실패 시나리오 테스트 5종(역순 완료·stale 영구 거부·버스트·멱허·토큰 단조) 선설계.
  트리 활성(더블클릭/Enter)은 방금 선택한 노드를 동기 계산해 재생 — 낡은 `_visible` 재생 제거,
  `_visible`=렌더링된 리스트 불변식 주석 고정. 큰 라이브러리의 검색/정렬/노드 전환 프리즈 제거.
- **PT4-14 설정 검색(사용자 승인)**: 사이드바 상단 AutoSuggestBox — 전체 섹션(Visibility 토글이므로
  전부 실체화)의 카드 제목+형제 설명+카테고리명을 지연 인덱싱, 제안 12개, 선택 시 카테고리 전환 →
  1틱 뒤(SelectionChanged 스크롤 리셋 이후) 해당 카드로 스크롤+1.2초 액센트 플래시.
  resw 3개 국어(Placeholder+자동명).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 **2,125/2,125**(게이트 테스트 5종 추가).
  XAML 이벤트 핸들러는 생성 코드가 인스턴스 참조로 연결하므로 CA1822 static 전환 불가 —
  기존 BooleanToVisibility 선례와 같이 pragma 처리.

**Wave 4 시정 완료 (2026-10-01, 사용자 전면 승인 — 잔여 P2/P3 일괄)**:
- **PT3-02 전체화면 트랜스포트 행**: 이전/재생(액센트)/다음 3버튼(자동명·단축키 툴팁 포함,
  재생 아이콘은 PlaybackStateChanged 추적) + 종료 힌트는 Wave2 기존.
- **PT5-09 툴팁 단축키 합성**: `ShortcutTooltipBinder`(앱 전용, 테스트 미링크)가 라이브 맵의
  chord를 정적 툴팁에 합성 — Previous/Play/Next/Stop/Repeat/Mute, 셔플·A-B는 동적 갱신 지점에서
  suffix 합성, A-B Off 툴팁의 하드코딩 "(Ctrl+L)"은 resw에서 제거하고 동적 chord로 대체(재바인딩
  시 거짓말 제거).
- **PT3-10**: 전체화면에 셔플/반복/A-B 상태 행 + 파형 위 A-B 밴드·마커(선형 매핑 — 썸 inset
  매핑은 파형에서 거짓).
- **PT3-11** 소스 배지·버퍼링은 PlaybackState 확장이 필요해 제외(장기 과제로 유지).
- **PT3-12** 전체화면 커버 로드 Task.Run + 세대 가드. **PT3-13** 파형 호버 시간 툴팁+가이드라인
  (Window ProtectedCursor 부재 → 가이드라인 대체). **PT3-17** 커버 없는 트랙 플레이스홀더 글리프.
- **PT1-15** 미니 모드 최대화 복원. **PT1-12** 타이틀바 행/바 40px 일치. **PT3-15** 컴팩트 기준
  단일화(640→730, MiniPlayerLayoutTests 계약 갱신). **PT1-14** 실패 인라인 텍스트에
  StatusDangerBrush 소비(InfoBar 시각은 Fluent 관례 유지로 명시).
- **PT1-08** 팔레트 3중 일치 게이트(`PaletteConsistencyTests` 3종 — DawnTheme 테마사전 ↔
  ThemeService 어플라이어 ↔ 루트 폴백). **PT1-11** 슬라이더 템플릿 ThemeResource 전환.
  **PT1-13/03-18** OnAccentBrush 토큰(#141414)으로 잉크 3곳 통일, VisualizerSurfaceBrush 토큰
  (PT4-16). **PT5-13** 슬라이더 트랙 SliderTrackFill 전환 + 라이트 값 진하게 완화(다크 1.0→1.54,
  라이트 1.36→1.61 — WCAG 3:1 미충족은 Fluent 관례 범위로 명시적 수용).
- **PT2-07** 라이브러리 중앙 빈 상태(라이브러리 비어있음 vs 검색 무히트 분리 + 설정 열기/검색
  지우기 액션). **PT2-11** 라이브러리 표 Extended 선택 + 우클릭 다중 유지 규칙. **PT2-12** 선택
  수 표시(라이브러리 상태줄 + 재생목록 헤더). **PT2-15** 태그 편집기 숫자 필드 저장 시 인라인
  검증(오류 필드 붉은 테두리+메시지, 대화상자 유지). **PT3-07/08** A-B 거부 사유가 툴팁·자동명에
  영구 남고 WaitingForB 라벨에 ✕ 취소 어포던스. **PT3-16** 대기열 비우기 확인 대화상자(곡수 표시).
- **PT4-17** 라디오 대화상자: 오류를 URL 필드 아래로, blur 사전 검증. **PT4-18** 청취 리포트 기간
  전환 로딩 표시 + 실패 시 오류 표시(낡은 리포트 잔존 제거). **PT4-19** 미확인 Warning/Error 뒤
  하위 심각도 알림 억제(단일 슬롯 정책 보강). **PT4-20** 가사 검색 상태 행 별도 배치(가려짐 제거)
  + Enter 제출. **PT5-10** 상호작용 규약 문서화(단일 클릭=선택/탐색, 더블클릭·Enter=재생, YouTube
  최근 카드는 의도적 예외로 명시). **PT5-11** 글리프 규칙 정착(E710=재생목록, E8FD=대기열,
  E76C=다음 — PlaylistPage 2곳 정렬 + 주석). **PT5-19** 자동명 부재 게이트 신설
  (`IconOnlyButtonNameTests`, 14파일 — 현재 0위반, 회귀 차단).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 **2,142/2,142**(신규 8종 + 계약 갱신 1종).

**코드 리뷰 패스 (2026-10-01, 시정 전체 54파일 +2.3k/-0.5k 대상)**:
- **수정 6건**: CR1 가사 편집기 닫기 연타 시 ContentDialog 중복 ShowAsync 예외(재진입 가드) —
  실버그, CR2 RG 스캔 완료 이벤트를 페이지 이탈로 놓치면 버튼 영구 비활성(OnPageLoaded에서
  `IsReplayGainScanRunning` 동기화), CR4 전체화면 재생 버튼 자동명이 커스텀 템플릿(ContentPresenter
  래핑) 때문에 갱신 안 됨(FindAncestor), CR5 설정 검색 인덱스가 방문 간 캐시되어 지역화 변경 시
  낡음(방문마다 재구축), CR3 NotificationPresenter 조건 중복 정리, CR7 테스트 using 중복.
- **보강**: PT1-06 클램프의 실패 시나리오 테스트가 누락되어 있었음(§3 위반) — 순수 클래스
  `WindowPlacementMath`로 추출 + tests 링크 + 경계 테스트 5종(2차 모니터 분리·음수·멱허성),
  PT4-19 억제 정책 게이트 4종(경고 위 transient 뭉갬·상위 심각도 대체는 허용).
- **수용 판단(수정 안 함)**: 스플리터 키보드 ←/→ 방향 의미(포인터 방향 의미론과 일치),
  시크바 호버·드래그 툴팁 공존(무해), UpdateTransportState 10Hz 호출(DP 동값 단락),
  빈 선택 경로(우클릭 선택 갱신이 보장해 도달 불가 + 헬퍼 안전).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 **2,151/2,151**(신규 9종).

**잔여 3건 해소 + 리뷰 수렴 루프 (2026-10-02, 사용자 /goal 지시 — "코드 리뷰를 반복하여 잔여 항목이
남지 않을 때까지 수정")**:
- **PT3-11 소스 배지·버퍼링 피드백**: `PlaybackState`에 `Buffering` 신설(열기 창 — 아무 소리도
  나지 않을 때만 진입, 모든 실패·대체 경로는 `RestoreFromBuffering`으로 정직 복구: 트랙 보유
  세션 존속 시 Paused, 배수 시퀀서는 정리 후 Stopped, `_sessionLock` 하 재확인). 재생 중 스트림
  스톨은 별도 신호 — `IStreamStallSource`/`StreamStallTracker`(휘발성 히스테리시스: 읽기 측 침묵
  서브 시 set, fill 측 0.25초 이상 적립 시 clear)를 `SessionSnapshot.StallSource`에 담아
  `IPlaybackController.IsBuffering`로 노출, 핫스왑(`StartOrSwitchLocked`)·갭리스 체인·자연
  어드밴드(`OnTrackStarted`)마다 재게시. 소비처: SMTC(Buffering→Playing 매핑, Pause 버튼은
  `CancelPendingOpen`), 슬립 타이머(취소 전용 — 재개 없음), 재생 바·전체화면 배지(Paused에서의
  낡은 스톨 플래그 미표시 게이트), 배지 앞 소스 라벨(RADIO/DLNA/YouTube —
  `AudioFormatBadgeFormatter.GetSourceLabel`). 죽은 라디오 스트림은 `StreamDied` 1회 경고
  (Dispose 유발 중단은 `_disposed`로 억제, 서버 깔끔 종료(EOF)도 동일 발표, 부착은 모든 시작
  경로가 통과하는 `OnTrackStarted` 단일 퍼널). resw 3개 국어(버퍼링 배지·전체화면 라벨·CoreMsg).
- **PT2-10 x:Phase**: LibraryPage 트랙 목록·PlaylistPage 대기열(모두 ListView)의 비-제목 셀에
  `x:Phase="1"` — 제목이 0단계로 먼저 실체화. ItemsRepeater 앨범 그리드는 ContainerContentChanging
  콜백이 없어 x:Phase 불가(위반 시 WMC0911/빈 카드) — 구조적 제외 근거를 XAML 주석으로 고정.
- **PT5-13 3:1 수렴**: SliderTrackFill 다크 #3D3D49→**#707079**, 라이트 #8A8780→**#85827C**
  (호버 #7B7B85/#6E6B65 — 기존 라이트 호버는 밝아져 대비가 오히려 하락하던 것을 수정).
  Panel·LayerBg·Card 전 표면 기준 3:1 돌파(최저 3.01:1 — Wave 4 완화치는 카드 표면에서
  2.82:1로 미달이었음). `PaletteConsistencyTests`에 표면×테마×rest/hover 게이트 4종 추가.
- **리뷰 수렴 루프**: 서브에이전트 리뷰 5회(1차 6건 → 2차 6건 → 3차 6건 → 4차 2건 → 5차 **0건**,
  총 20건 수선 — Buffering 잔류·슬립 타이머 의도 반전·StallSource 미게시·배수 시퀀서 좀비
  Playing·RestartIfPlaying 상태 간섭·가짜 스트림 사망 경고·부착 누락 경로 등). 5차 패스에서
  잔여 0 확인. 수용: CancelPendingOpen의 완주-경합 TOCTOU(이미 시작된 재생은 존중 — 검토자
  판정 P3 수용), TrackStarted 가드의 이론적 발행-순서 창(기존 동작, 재구성 없음).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 **2,170/2,170**(신규 19종: StreamStallTracker 4,
  컨트롤러 Buffering 계약 5, SMTC 미러+소스 스캔 게이트 2, 소스 배지 4, 팔레트 3:1 게이트 4).

**타이틀바 좁은 창 겹침 수정 (2026-10-02, 사용자 스크린샷 보고 → 승인 후 수정)**:
- 원인: AppTitleBar의 브랜드·상태 열과 내비 탭 열이 모두 Auto — 창이 ~870 유효 px 이하로 줄면
  그리드가 오버픈하고 ExtendsContentIntoTitleBar의 시스템 캡션 버튼이 그 위에 덮여
  Playlists·Network 탭과 최소화/닫기가 겹침.
- 수정: 상태 텍스트를 유연(*) 열로 이동해 좁아지면 말줄임으로 먼저 흡수, 840/740 유효 px
  임계값의 단계 숨김(상태 텍스트→브랜드, OnTitleBarSizeChanged), 창 최소 폭 620
  (`OverlappedPresenter.PreferredMinimumWidth` — 미니 모드 진입 시 해제·복원).
- 교훈 ①: WinUI `Window`에는 XAML MinWidth가 없고 최소 폭은 OverlappedPresenter 소속이다.
  교훈 ②: AdaptiveTrigger가 이 창의 라이브 리사이즈에서 재평가되지 않는 확인 — 창 수준
  단계 전환은 SizeChanged 직접 처리가 결정적. 교훈 ③: 테스트 하니스의 SetWindowPos 좌표는
  프로세스 DPI 인식에 따라 해석이 달라진다.
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 2,173/2,173(+3 계약 게이트
  `MainWindowTitleBarLayoutTests` — 1건 병렬 부하 플레이크, 격리 재실행 통과), 실행 육안
  검증 670/760/988 유효 px 3구간.

**라이브러리 트리 빈 행(사이드바 글자 소실) 수정 (2026-10-03, 사용자 스크린샷 보고)**:
- 원인: 460616e(L12)의 PT2-05 확장상태 유지 리팩터링에서 `LibraryTreeBuilder.ToTreeViewNode`의
  `Content = model` 한 줄이 유실 — 모든 TreeViewNode의 Content가 null이 되어 템플릿 바인딩
  (Content.Title/Glyph/CountText)이 빈 값을 그리고, `Content is LibraryTreeNode` 패턴매치에
  의존하는 트리 클릭 필터링·지연 확장·선택 복원까지 전부 무기화. "글자가 안 보이는 사이드바"의
  정체는 색 문제가 아니라 **빈 데이터**였다.
- 수정: `Content = model` 복원 + 소스 스캔 회귀 게이트(`LibraryTreeContentGateTests` —
  템플릿이 Content.* 바인딩인데 팩토리가 Content를 안 할당하면 실패; 빌더는 WinUI 의존으로
  테스트 프로젝트 링크 불가라 소스 스캔 방식).
- 실행 검증: 트리 텍스트·카운트 복원, "All/한국" 노드 클릭 → 헤더·그리드 필터 전환 확인,
  시작 시 선택 복원(일본 470)도 살아남 확인. 관찰(별도 과제): TreeViewItem 선택 인디케이터가
  시스템 액센트(파랑) — 앱 액센트(앰버)와 불일치, DawnTheme의 TreeView 선택 리소스 미정의.
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 2,174/2,174(+1).

**트리 행 기하학 사용자 지정 (2026-10-03, 목업 슬라이더로 값 확정 → 적용)**:
- 1차 적용(셰브런 8/간격 1/패딩 1 = 28px) 후 사용자가 "이전 레이아웃과 달라 재판단 필요" —
  ui-ux-pro-max 근거의 4변형 목업(A 이전 복원 · B 현재 컴팩트 · C 목록 리듬 통일 · D 카드형)을
  제시하고 사용자가 **D 카드형 여유**를 선택.
- 최종 기하학: 행 32px 카드 + 1px 공극(피치 34 = 트랙 목록 리듬), 카드 배경 Panel 40%,
  호버 카드(CardHover), 선택 앰버 틴트(ListViewItemBackgroundSelected* 26/3D/4D 재사용),
  셰브런 칸 10px, 셰브런→제목 5px, 카운트 여백 8px, 코너 5px.
- 구현: MUX_TreeViewItemStyle 복제 템플릿의 기하학 교체 + 카드 브러시는 **DawnTheme 토큰으로
  승격**(`TreeRowCardBrush` 다크 #661F1F25 / 라이트 #66F0EFEB — 뷰 XAML 하드코딩 hex 게이트
  준수), 선택 틴트는 기존 ListViewItemBackgroundSelected* 토큰 재사용.
- TreeRowDensityGateTests 계약 갱신(변형 D 값 + 토큰화 검증 2종).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 2,175/2,175(+1), 실행 육안 검증(카드 룩·클릭
  필터·선택 표시). UIA 실측 행 40 물리 px(32 논리)·피치 43(34.4 논리) 확인.

**하단바 평점 아이콘 이질감 수정 + 표시 토글 (2026-10-03, 사용자 보고 → 4안 목업 → A안 + 토글)**:
- 원인: 하단바 평점이 텍스트 별(★☆, 본문 폰트)을 앰버로 렌더링 — 하단바의 다른 아이콘은 전부
  Segoe Fluent FontIcon 무채색이라 ①폰트 계열 ②색 ③제목 행 끝 홀로 배치의 삼중 이질감.
- 수정(A안): `TrackRatingButton` 내용을 Segoe Fluent FontIcon으로 교체 — 미평점 E735
  (TextSecondary, 다른 아이콘과 동일 무채색), 평점 있음 E734(DawnAccentTextBrush). 자동명은
  기존 컨버터 그대로.
- 토글: `UiSettings.ShowNowPlayingRating`(기본 true) + `AppearanceSettingsService
  .SetNowPlayingRatingVisible` + AppearanceSettingsViewModel 속성 + 설정 "레이아웃 & 디스플레이"
  섹션 토글 행(resw 3개 국어) + NowPlayingBar가 AppearanceChanged를 구독해 즉시 반영(테마/액센트
  변경 시 아이콘 브러시 새로 고침 겸용). 라이브러리·재생목록 표의 별점 셀은 영향 없음.
- 교훈: NowPlayingBar 생성자는 MainWindow InitializeComponent 도중에 돈다 — AppServices 초기화
  후에만 접근 가능한 서비스 구독은 InitializeState로 (생성자 구독이 XAML instance-creation
  크래시를 냈었음).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 2,175/2,175(+서비스 토글 테스트), 실행 검증:
  새 아이콘(회색/앰버) 확인, 토글 Hide → 하단바 별 즉시 소실 + settings.json false 저장, Show
  복원 → 별 돌아옴.
**상단바 OLD 레이아웃 복원 + 커스텀 캡션 버튼 (2026-10-03, 사용자 보고 → 4안 목업 → OLD + ①커스텀 캡션)**:
- 배경: 타이틀바 겹침 수정 때 탭이 우측(캡션 옆)으로 이동해 "이전과 달라졌다" 보고 — 4안 목업
  (OLD 복원·현재 유지·중앙 정렬·이전+인라인)에서 사용자가 **OLD** 선택, 이어서 "─ □ ✕가 따로
  노는데" 지적으로 캡션 통일 3안(색 튜닝·커스텀·유지) 중 **커스텀 캡션** 선택.
- OLD 레이아웃: 상태 텍스트를 탭 왼쪽 Auto 열로(열 순서 Auto·Auto·*·Auto), 탭이 좌측 그룹 복귀.
  겹침 방지(상태→브랜드 단계 숨김, 최소 폭 620)는 유연 구조와 무관하게 유지 — 재발 없음.
- 커스텀 캡션: 시스템 캡션 버튼을 AppWindow.TitleBar 전 색 투명화로 숨기고 앱이 ─ □ ✕를 그림
  (CaptionButtonStyle 46×40, 호버 CardHover 토큰, 닫기만 Windows 관례 빨강 #C42B1C+흰 글리프).
  기능은 OverlappedPresenter Minimize/Maximize/Restore + Close() 기존 파이프라인(tray 숨김 존중),
  최대화 글리프는 SizeChanged로 E922/E923 전환, 드래그·스냅·더블클릭·단축키는 시스템이 계속 처리.
- 실행 검증: OLD 배치 스크린샷 확인, 최소화·최대화(전환)·닫기(정상 종료) UIA 프레스, 닫기 호버 빨강
  확인. 교훈: WinUI에는 SolidColorBrush(byte,byte,byte,byte) 생성자가 없고 OverlappedPresenter에는
  상태 변경 이벤트가 없다(최대화 추적은 SizeChanged).
- 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 2,177/2,177(+커스텀 캡션 게이트 — E2E 1건 병렬
  부하 플레이크, 격리 재실행 통과).

**상단바 캡션 겹침 3차 수정 — TitleBar 컨트롤 전환 (2026-10-03, 사용자 재보고 + 공식 문서 학습)**:
- 1~2차(시스템 캡션 투명화 + 앱 버튼 오버레이, InputNonClientPointerSource 영역 제거 시도) 모두
  겹침 지속. 공식 문서(/windows/apps/develop/title-bar) 학습으로 원인 확정: **캡션 글리프 전경색은
  알파 채널이 무시되어(투명화 불가) 시스템 글리프가 항상 그려지고**, 캡션 영역의 입력은 시스템이
  독점하므로 "앱이 캡션 버튼을 직접 그리는" 패턴 자체가 공식 불가 — 앱 글리프+시스템 글리프가
  겹쳐 보였던 것이 사용자가 본 이중 ✕의 정체.
- 3차 수정(정석): WinUI **TitleBar 컨트롤**(WASDK 1.7+, 우리 2.4.0에 포함 — 슬롯은
  LeftHeader/Content/RightHeader)로 타이틀바 재구성(변형 OLD 배치 유지) + 시스템 캡션은 문서가
  허용하는 최대치로 통일: 배경 4종 투명(=타이틀바 Mica와 재질 통일), 글리프·호버 색은 테마 팔레트
  (다크/라이트), 닫기 호버는 시스템 빨강 유지(문서 고정). StyleSystemCaptionButtons는 ApplyTheme에
  연동해 테마 전환마다 재적용. 앱 캡션 버튼 요소는 전면 제거.
- 검증: 우측 상단 밴드 픽셀 분석 — 밝은 글리프 클러스터가 버튼당 1벌씩(─ 1070-74, □ 1103-09,
  ✕ 1137-43), 배경 밝기 34 균일(재질 통일 확인). 겹침은 구조적으로 불가능해짐(XAML에 앱 캡션 없음).
- 게이트: MainWindowTitleBarLayoutTests 계약 전면 갱신(TitleBar 슬롯 구조·상태→탭 순서·앱 캡션
  부재 회귀 가드·StyleSystemCaptionButtons 색 계약). 클린 리빌드 0경고 0오류, 전체 2,177/2,177.
- 교훈: WinUI 캡션 커스터마이징은 문서의 허용 집합(배경 투명 4종+불투명 글리프 색) 밖이면
  불가능하다 — "되는 것처럼 보이는" 비공식 경로는 시스템 글리프와 겹친다. 1.7+ TitleBar 컨트롤이
  정석 진입점.

**타이틀바 문서 정합 리팩터링 (2026-10-03, 공식 문서 3건 학습 → 승인 후 수정)**:
- 배경: 3차 수정 뒤 미커밋 상태로 LeftHeader 재배치(상태·탭을 Content→LeftHeader 이동)와 창 폭
  기준 숨김 전환이 진행됐으나 ①게이트 2건이 구 계약(`<TitleBar.Content>` 존재, XAML
  `SizeChanged` 연결)과 충돌해 실패 중이었고 ②**Window.SizeChanged에 컨트롤용 시그니처
  (`SizeChangedEventArgs`)를 연결한 CS0123으로 App 프로젝트 빌드 자체가 깨진 채**였다
  (`WindowSizeChangedEventArgs.Size`가 정답 — 테스트는 MainWindow를 링크 컴파일하지 않고 소스
  텍스트만 스캔하므로 게이트가 잡지 못했다).
- 문서 정합 수정 4건:
  ① 캡션 색 단일 진실 원천 — MainWindow `StyleSystemCaptionButtons`(사용자 튜닝값)를
  `ThemeService.UpdateTitleBar`로 통합. 글리프 색은 하드코딩 hex → 팔레트 리소스(TextPrimary/
  TextTertiaryColor) 구동으로 변경, 보조 창과 메인 창의 값 갈라짐(불일치 결함) 해소.
  ② 고대비 문서 준수(titlebar-design "colors should adjust for high contrast") — HC 활성 시 커스텀
  캡션 색을 전부 null 리셋(문서 계약: null = 시스템 기본 복귀)으로 걷어내고, ApplyTheme의 HC
  분기도 캡션 리셋을 경유. 기존에는 HC에서도 커스텀 팔레트를 덧칠했다.
  ③ 비활성 디밍(문서 "Do": 창 비활성 시 타이틀바 모든 요소 반투명) — Window.Activated에서
  `AppTitleBar.Opacity = 0.5` 토글(시스템 캡션은 자체 디밍, 커스텀 Left/RightHeader 콘텐츠 대상).
  ④ 생성자의 무조건 `SystemBackdrop = Mica` 제거 — ApplyTheme가 설정값(Mica/Acrylic/Solid/
  AlbumArtBlur)으로 즉시 덮어쓰는 죽은 코드(경우에 따라 Solid 모드에서 Mica 섬광 위험).
- XAML은 사용자 선택 OLD 배치를 픽셀 단위 보존하고, 반복 수정으로 겹치던 주석을 단일 기록으로
  정리(잔여 공백 아티팩트 제거).
- 게이트: MainWindowTitleBarLayoutTests 계약 갱신 4종 + 신규 2종(캡션 단일 구현·HC null 리셋,
  비활성 디밍) = 6종. 클린 리빌드 0경고 0오류, 전체 테스트 **2,179/2,179**.
- 교훈: ① 소스 텍스트 스캔 게이트는 컴파일 오류를 못 잡는다 — App 프로젝트 빌드가 최종 진실.
  ② `UIElement.SizeChanged`(SizeChangedEventArgs)와 `Window.SizeChanged`(
  WindowSizeChangedEventArgs)는 다른 대리자·다른 Size 프로퍼티다.

**상단바 통일 — 변형 A(트리 카드 칩) + Tall 48 적용 (2026-10-03, 4안 목업 → 사용자 승인)**:
- 진단: 상단바 안에 선택 언어 3종 공존(탭 밑줄+SemiBold `EoleNavTabStyle` / 트리 D 카드형 / 라이브러리
  Grid·List 토글), 앱 어디에도 없는 텍스트 구분자 `|`(브랜드–상태), 4의 배수가 아닌 간격(2·6·8·10·14),
  **높이 불일치(바 40px vs 시스템 캡션 32px — 사용자 스크린샷 빨간 선 지적, 8px 괴리로 캡션 호버
  배경이 바 하단에 못 미침)**, 1~2차 커스텀 캡션 잔재 스타일 2종이 통일감을 깼다.
- 적용(목업 titlebar-unity-variants.html에서 사용자가 **A + Tall 48** 선택):
  ① 탭 = `TitleBarTabStyle` 신설(트리 D 카드형과 동일 언어: 28px 칩·코너 5·호버 CardHover·체크 =
  선택 틴트 `ListViewItemBackgroundSelected`(액센트 연동) + `DawnAccentTextBrush`, 밑줄·SemiBold 은퇴).
  **`EoleNavTabStyle`은 네트워크 페이지 섹션 탭이 쓰므로 승인 범위(상단바) 밖 — 별도 남김.**
  ② 높이 = 생성자에서 `ExtendsContentIntoTitleBar=true` 이후 `PreferredHeightOption = Tall`(문서
  경고 순서 준수) + XAML 행·MinHeight 40→48 + 미니 모드 복원 하드코딩 40 → `TitleBarRowHeight`
  상수 + **미니 진입 시 Standard로 낮추고 복귀 시 Tall 복원**(104px 창에서 48px 캡션 잠식 방지).
  ③ 리듬 = `|` 제거(브랜드–상태 12px), 상태–탭 10→12px, 탭 간 2→4px.
  ④ `CaptionButtonStyle`·`CaptionCloseButtonStyle` 삭제(사용처 0 확인).
- 게이트: 신규 2종(높이 일치 계약 — XAML 48·Tall 설정 순서·미니 Standard/Tall, 탭 카드 언어 —
  토큰·ActiveIndicator/FontWeight 부재·EoleNavTabStyle 역참조 금지). 클린 리빌드 0경고 0오류,
  전체 테스트 **2,181/2,181**.
- 결함 수선(적용 직후 사용자 실시 보고 2건 → 즉시 수선):
  ① **클릭 후 탭 하이라이트 소실** — TitleBarTabStyle 초판이 호버·체크·누름 배경을 전부
  `Root.Background` 한 프로퍼티에 지정해, 상태 종료 시의 스냅샷 복원이 체크 틴트를 덮어썼다.
  수정: 배경을 전용 오버레이 3종(Hover/Checked/Pressed Border의 Opacity)으로 분리 — 한 프로퍼티는
  한 상태 그룹만 쓰는 계약. 피드백은 배경만(트리 카드 행과 동일, 호버 글자색 변경도 제거).
  게이트에 `Target="Root.Background"` 금지 단언 추가.
  ② **설정 톱니 위치 마커 잔존** — 주황 톱니는 PT1-03의 "설정 중 위치 표시자"(의도)지만, 색 갱신이
  `ContentFrame.Navigated` 한 곳에만 있어 탭 클릭으로 설정을 나가면(탐색 미발생) 주황이 세션 내내
  남았다. 수정: `UpdateSettingsGearMarker()` 추출 — `ApplyNavigationState`(탭 전환 경로)에서도
  호출. 게이트 +1(두 경로 호출 계약).
- 최종 게이트: 클린 리빌드 0경고 0오류, 전체 테스트 **2,182/2,182**.
- 캡션 커스텀 토큰 통일(동일 날 사용자 "─□✕가 윈도우 기본 같다" 요청): 플랫폼 제약 재확인 —
  글리프 **모양**은 교체 API가 없음(전경 알파 무시·입력 독점, 1~2차 실증·문서 명시). 문서 허용
  집합 내 최대치로 캡션 호버·누름 배경을 중립 알파 틴트 → 앱 `CardHoverColor`/`CardPressedColor`
  토큰으로 교체 — 캡션에 올리면 탭 칩·트리 카드와 동일한 호버 표면이 뜬다. 글리프 색(팔레트)·
  배경 투명(재질)·닫기 호버 시스템 빨강(문서 고정) 유지. 모양 교체를 원하면 Win32
  WM_NCHITTEST 서브클래싱만 가능하나 비공식·스냅 레이아웃 플라이아웃 상실로 비추천 제시.
  `Tint` 헬퍼 삭제, 게이트에 CardHover/CardPressed 토큰 계약 추가. 2,182/2,182 유지.
- 잔여(별도 제안): 네트워크 페이지 섹션 탭(EoleNavTabStyle, 밑줄 언어)도 카드 언어로 통일할지 —
  승인 범위 밖이라 미적용. 실행 육안 확인: 48px 바 + 캡션 꽉 참, 탭 카드 칩, 미니 모드 진입·복귀.

**수용·문서화된 항목(수정 안 함 — 잔여 아님)**: PT5-10의 YouTube 최근 카드 단일 클릭(상호작용
규약상 의도적 예외), PT5-13의 액센트 value-fill 대비(Fluent 관례 범위, 감사 요구는 트랙), 스플리터
키보드 방향 의미, 시크바 호버·드래그 툴팁 공존, UpdateTransportState 10Hz 호출, 빈 선택 경로.
"진짜 잔여" 3건은 위 2026-10-02 기록으로 전건 해소되어 더 이상 남지 않는다.

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
   원본 오디오 우선(FLAC>WAV>ALAC>AAC>MP3>OGG — 2026-09-26 코드 대조로 정정, 실제 구현 순서),
   서버 트랜스코드(LPCM/L16)는 원본이 없을 때만,
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

> **2026-09-26 착수·구현 완료 + 적대적 검토·수정** — 사용자 지시("N3 작업을 진행해라")로 상세화 후,
> §8 결정 사항 승인(D1 자연 경계 갭 수용·D3 라우팅 테스트 기대값 갱신 등)에 따라 구현:
> **[docs/n3-youtube-implementation.md](docs/n3-youtube-implementation.md)** (파일 수준 명세 + 1차
> 관문 스파이크 실측). 스파이크: ffmpeg 9.0.1 + yt-dlp 2026.08.19(pip 신설), Deno 없이도
> visionos 클라이언트 폴백으로 `-J` 해석 2.5초·파이프(→s16le 48k 스테레오) 19초 트랙 종단 2.7초
> 성공 — **A안(파이프 리더) 실측 확정**.
> **적대적 검토(서브 에이전트 3개 — 스레딩·프로세스·UI/불변식) 결과 P0 2건·P1 4건·P2 다수 발견,
> 전부 수정**: (1) 유한 YouTube의 A-B 바운스가 렌더 스레드에서 프로세스 킬+pump 대기(최악 ~2.5초
> 렌더 정지) → 체인 폐기의 느린 절차를 오프스레드로, A-B 자체는 YouTube 소스 거부
> (`UnsupportedSource` 신설+안내), 시크 재기동의 스폰만 호출 스레드에 잔존. (2) 버퍼 무한 성장
> (긴 영상 RAM 폭증) → 30초 상한+백프레셔 페이싱. (3) 세대 게이트 비원자 RMW·Dispose∩Seek 체인
> 누수·MarkEnded 체크-액트 창 → `Interlocked.Increment`+CAS 슬롯+BufferedPcm 세대 인자(락 내
> 원자 판정). (4) 시크 재기동 갭의 무음이 시컨서 위치 클록을 영구 드리프트(8ebfe12 교훈 재발) →
> `IResyncRequestSource` 원샷 재앵커("보고=청취" 복원). (5) `-J` 실패 stderr 유실(I2) → 예외
> 메시지 부착. (6) PATHEXT 미지원(.cmd 설치 오판) → PATH+PATHEXT 해상. (7) 해석 메타 폐기 →
> 시작 시 제목·아티스트·길이·썸네일(ArtUrl→아트 훅) 반영. (8) 조기 종료 무통보 스킵 →
> `PrematureEnd` 경고. (9) 설정 토글 사망 코드 제거, Unknown 오 표시 제거, Play 연타 가드,
> Open 입구 URL 재검증(cmd 인젝션 표면 봉쇄), 프로브 재진입 병합.
> 게이트: obj/bin 삭제 콜드 리빌드 **0경고 0오류**, 전체 테스트 **2,055/2,055 통과(2회 연속)**(신규
> 47종 + 도구 경로 선택 4종 — 순수 파서·fake seam 리더 불변식·BufferedPcm 세대 계약·.cmd shim
> 실프로세스 통합 5종·M3U8 왕복·라우팅 갱신·리싱크 계약). **UI 정리(사용자 지시)**: Re-check·
> Tool paths 2버튼 → **단일 'Configure…' 버튼** 통합(재검사는 플라이아웃 Open 시 수행), 플라이아웃
> 폭 440→340으로 잘림 수정. **실기기 경로 테스트**: `H:\음악\tools`의 yt-dlp 2026.08.19·
> ffmpeg 2026-09-24-git로 --version 검증 + 19초 트랙 파이프 종단 재측정(정확히 19.0초분 PCM) +
> 사용자 settings.json에 두 경로 구성(재시작 시 '준비됨 (사용자 지정)' 배지 확인 대상). 테스트 결함
> 1건 수정: ReadAll의 프레임 상한이 스레드풀 기아 상황에서 실오디오 도착 전 무음을 소진해 위치
> 단언을 깨뜨림 → 시간 경계 방식으로 변경. 수동 이월: 실서버 장기 청음(긴 트랙·시크 체감·라이브·
> 지역/연령 제한·Premium), Narrator 스모크.
> **후속 정리(2026-09-27)**: Re-check·Tool paths 2버튼 → **단일 'Configure…' 버튼 통합**(재검사는
> 플라이아웃 Open 시 자동 수행), 플라이아웃 폭 440→340 잘림 수정. `H:\음악\tools` 실기기 경로
> 검증(yt-dlp 2026.08.19·ffmpeg 2026-09-24-git — 파이프 종단 재측정 성공) + 사용자 settings.json
> 구성 완료. **결함 2건 추가 수정**: (1) 의존성 프로브가 구성 경로를 무시하고 맨이름만 PATH 조회 →
> 오버라이드가 유효해도 "미설치" 판정(측정: ffmpeg `--version` exit 8, `-version`만 성공하는 플래그
> 방언 → 폴백 추가) — 구성 경로 프로브+회귀 테스트. (2) **`xunit.runner.json`이
> `parallelizeTestCollections: true`로 어셈블리 직렬화 어트리뷰트를 덮어써 컬렉션 병렬 실행 중 —
> EngineSeam의 FakeProvider 등록 창이 라우팅 테스트를 오염(간헐 RadioKind 실패)하고 스레드풀 기아로
> 실시간 테스트 부하 실패** → 직렬화로 원복(주석의 AppPaths 오염 사고와 일치하는 원 의도).
> 게이트 재확인: 클린 리빌드 0경고 0오류, 전체 **2,058/2,058 × 3회 연속 통과**(신규 누계 50종).
> **UX 고도화 1+2단계(2026-09-27 사용자 승인·구현, ui-ux-pro-max 근거)**: (1단계) 제출 피드백 —
> 버튼 비활성+ProgressRing+"해석 중…" 상태, Enter 제출(WinUI 3에 Button.IsDefault가 없어 KeyDown
> 우회), URL 라벨 추가, blur 인라인 검증, 실패 시 [다시 시도]; (2단계) **최근 항목 카드 그리드** —
> `YouTubeRecentStore`(정규화 URL dedup·상한 20·AtomicFile+.bak, `youtube-recent.json`),
> `YouTubeResolveCache`(페이지 URL 키·TTL 15분·상한 32)로 섹션의 -J 선해석을 Open이 재사용(이중
> 해석 2.5s 제거), 카드 = 썸네일(공유 아트 캐시 비동기 로드)+제목·채널·길이 배지, 클릭 재생/
> 컨텍스트 재생·추가·제거, 빈 상태 안내. 함정: WinUI 3 Button.IsDefault 부재(KeyDown 우회),
> out var 같은 호출 후속 인자 CS0165, CA1822/CA1310. 게이트: 클린 리빌드 0경고 0오류, 전체
> **2,069/2,069 통과**(신규 11종). 3단계(검색 통합·플레이리스트 가져오기·클립보드 감지·드래그앤드롭)
> 는 별도 계획 승인 대기.
> **Radio·DLNA 동일 패턴 적용(2026-09-27 사용자 지시, ui-ux-pro-max 근거)**: Radio — 현재 스트리밍
> 중인 방송국 하이라이트(액센트 바+"재생 중" 배지, PlaybackStateChanged/CurrentTrackChanged 구독
> 갱신), 연결 피드백(헤더 ring+"연결 중: <이름>" — connect+1.5s 프리버퍼의 무반응 제거), 추가/편집
> 대화상자 인라인 URL 검증(Closing 취소 방식 — 잘못된 URL이 닫힌 뒤 경고로만 안내되던 문제). DLNA —
> 트랙 행 썸네일(AlbumArtUri→공유 아트 캐시 비동기, 페이지당 40장 상한, 폴더 행은 아이콘 유지),
> 재생 준비 피드백(스풀 전체 다운로드의 무음 수 초 간격을 ring+"재생 준비 중…"으로 설명), 브라우즈
> 실패 [다시 시도] 버튼(실패 요청 인자 재사용). 함정: 파일 범위 namespace 재선언 CS8954, 러너
> 잠금(실행 중 앱) 빌드 실패. 게이트: 클린 리빌드 0경고 0오류, 전체 **2,069/2,069 통과**.
> **선존재 네이티브 크래시 기록(2026-09-27 사용자 보고·분석)**: Network 탭에서 재생 중 탭 전환 시
> 앱 종료 1회 — 관리 예외 로그 0건(3종 핸들러 전부 무음) → 이벤트 로그 Event 1000 확인:
> **0xC0000374(힙 손상), ntdll**. WER Archive에 **동일 버킷(StackHash_9898)이 08-19에 11회**(모든
> N-시리즈 이전, 구 빌드) → N 계열/UX 변경과 무관한 선존재 네이티브 결함(배타 WASAPI/MF 계열
> 추정). 조치: 디버그 빌드에 크래시 덤프 수집 상시 장착(`DawnPlayer.App.csproj`의
> `System.DbgEnableMiniDump`/`DbgMiniDumpName`/`DbgMiniDumpType` → `crash-dumps/DawnPlayer.dmp`,
> 디렉터 .gitignore 처리) — 재발 시 덤프로 근본 원인 분석.

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
**릴리즈 완료 (2026-09-25, 사용자 명시 요청)**: 트리 전체 커밋(`19356a8`) + `main`·`v1.3.0`
푸시 + GitHub Release 발행(포터블 ZIP + SHA256SUMS). H:\ 교체는 사용자 판단.

### L3. 라이브러리 툴바 아이콘 정정 (2026-09-26 지시·구현)

> 원인: 커버 크기 버튼 `E71E`(Zoom=돋보기)이 검색창 `QueryIcon="Find"`(돋보기) 바로 옆에 있어
> 구분 불가. 목록 뷰 토글은 `E8D2`(=Font, 글꼴 아이콘)로 리스트 의미 없음. 둘 다 공식 MDL2
> 문서 코드표 대조 확정. 그리드 토글 `E80A`(TiltDown)도 의미 어긋나나 범위 외로 유지(사용자 결정).

| 항목 | 내용 | 검증 |
|---|---|---|
| 수정 | `LibraryPage.xaml` 2줄: 크기 `E71E`→`E741`(ResizeTouchLarger)→확정형 `PathIcon` 중첩사각(폰트 룰렛 종료), 목록 `E8D2`→`EA37`(List). 툴팁·자동화 이름 resw 원천 유지 | App 빌드 **0경고 0오류** + E2E 포함 전체 **1961/1961**(PathIcon 파싱은 페이지 로드로 검증) |
| 게이트 | `LibraryToolbarIconTests` 9종(돋보기계열 금지·이웃글리프 중복금지·목록=EA37 고정·추출기 이론) — 수정 전 RED(3종 실패) → 후 GREEN | 포함 전체 1941종 중 1940 통과, 1 실패는 `PlaybackUiHelperTests` 동시성 건 → 격리 재실행 통과로 병렬부하 플레이크 판정(규약 §4) |

### L4. 미니 플레이어 레이아웃 (2026-09-26 지시·구현)

> 원인 2건: (1) `NowPlayingBar`의 `Wide` 상태에 트리거가 없어 `Compact`(<640px 의도)가 전
> 너비에서 불발. (2) 미니(500px)에 바 고정폭 ≈584px(아트56+트랜스포트288+우측묶음220) vs
> 가용 464px → 우측 도구 잘림. 뱃지 축소만으로는 고정폭이 그대로라 해소 불가.

| 항목 | 내용 | 검증 |
|---|---|---|
| 수정 | `NowPlayingBar.xaml`만: `Wide`에 `MinWindowWidth="640"` 트리거, `Compact`에 고정폭 다이어트(`VolumeSlider` 숨김·아트 56→40·패딩 18→12·양 묶음 Spacing 6→4, 절약 ≈148px). C# 변경 없음, 음소거 유지 | 콜드 리빌드 **0경고 0오류** |
| 게이트 | `MiniPlayerLayoutTests` 7종(Wide=640 고정·Compact 세터 존재·추출기 이론) — 수정 전 RED(3종 실패) → 후 GREEN | 포함 전체 **1948/1948 통과** |
| 미확인 | 실기 렌더 육안(환경에 GUI 없음 — 수치 근거+게이트로 보증, 사용자 육안 확인 요망) | — |

### L5. 라이브러리 폴더 트리 고도화 P0 (2026-09-26 지시·구현)

> 사전 검토 판정: GO(음악 플레이어 용도에 적합 — 기본 `아티스트/앨범` 유지, 폴더 모드는
> opt-in 파워도구). 원격 트랙은 재생목록에만 들어가 라이브러리 혼입 없음 실측.

| 항목 | 내용 | 검증 |
|---|---|---|
| A1 지연 확장 | 폴더 모드만 UI 노드 지연 실체화(`DeferChildren`+더미→`Expanding` 실체화, 자식은 재지연). 선택 복원은 실체화하며 탐색(`FindNodeRecursiveMaterialized`) | `TreeLazyExpansionGateTests` 2종 RED→GREEN |
| A2 체인 압축 | 중첩 단일-자식(무트랙) 전 구간 `A / B` 병합. 트랙 보유 폴더에서 정지, `FilterValue`=리프 전체경로라 필터 불변 | `LibraryFolderTreeAdvancedTests` 5종 RED→GREEN |
| A0 경로 가드 | 비루트 경로 스킵(N1 M3U 결함 등급 — CWD 결합 방지) | 상기 테스트 포함 |
| 빌드 | 콜드 1회전서 CA1822 1건 포착 → 저장소 관례 pragma(인스턴스 요구 주석) → App 콜드 재빌드 **0경고 0오류** | 전체 **1955/1955 통과** |
| A 컨테이너 다이어트 | 셰브런 열은 템플릿 고정이라 `ItemContainerStyle`로 행 높이 26·수평 거터만 제거(`TreeRowDensityGateTests` 2종 RED→GREEN). B(컴팩트 템플릿)는 미승인·보류 | 포함 전체 **1957/1957 통과**(E2E 2종 포함) |
| E2E 오판 방지 기록 | 앱 실행 중 전체 스위트를 돌리자 E2E 하네스(프로세스 정리 설계)가 실행 중 앱을 종료 + 부분 삭제된 bin에서 구동 실패 → 2종 실패. 제품 회귀 아님. 교훈: 앱 실행 중에는 전체 스위트 금지, 필터 실행만 | 클린 필드 재실행 **1957/1957** |
| 미확인 | TreeView 실체화 타이밍·키보드·Narrator·셰브런 육안 실기 스모크(WinUI 바인딩은 단위테스트 불가 — 수동 체크리스트로 이월) | — |
| V1 폴더 아이콘 숨김 | 폴더 모드에서 행 폴더 아이콘 숨김(`IsFolder`+역변환, 간격 마진 이관). `TreeFolderIconGateTests` 5종 RED→GREEN | 콜드 **0경고 0오류**, 전체 1961/1962 → `PlaybackRestartTests` 재시작 타이밍 1건 실패는 격리 재실행 통과로 플레이크 판정 |
| bin 파손 사고 기록 | 앱 실행 중 `bin` 삭제로 runtimeconfig·PRI 등 소실 → exe 구동 불가. 원인 제공 후 프로세스 종료 확인 → 전체 클린·콜드 리빌드로 복원. 교훈: 실행 중 프로세스 확인 없이 `bin` 삭제 금지 | 복원 후 exe·runtimeconfig·PRI 존재 확인 |

### L6. 앨범 셔플 결함 수정 (2026-09-26 지시·구현 — Linux 검증 완료, Windows 잔여 게이트 있음)

> 증상: 앨범 셔플이 한 곡만 재생하고 다른 앨범으로 뛰거나, 같은 앨범만 반복.
> 원인: `PlayOrderResolver` 3a 블록의 인접-인덱스 의존 + 연속-구간 그룹핑 + 무상태 균등 추첨
> (기존 테스트가 `A,A,B,B` 연속 배치만 다뤄 결함을 가림). 활성화 경로(UI 순환·단축키·설정 저장)는 정상.

| 항목 | 내용 | 검증 |
|---|---|---|
| R1 인접-인덱스 의존 | 앨범 내 다음 곡을 `curIdx+1` 한 칸만 검사 → 비앨범순 목록에서 1곡 만에 앨범 탈출. 이후 항목 전방 스캔(`FirstInAlbum`)으로 수정 | `AlbumShuffle_FragmentedPlaylist_StaysInsideTheAlbum` |
| R2 연속-구간 그룹핑 | 추첨에 `PlaylistGroupBuilder`(연속 기준) 사용 → 조각난 앨범 편향·중간 시작. distinct `AlbumKey` 단위로 변경 | `AlbumShuffle_HopStartsAtTheChosenAlbumsFirstTrack` |
| R3 무상태 균등 추첨 | 매 경계 독립 추첨 → 중복·미방문. Fisher-Yates 앨범 덱(주기당 전수 1회 방문) 도입, 목록·앨범 집합 변경 시 재생성 | `AlbumShuffle_VisitsEveryAlbumOncePerCycle` |
| R4 Repeat=Off 무시 | 덱 소진 + `Repeat=Off` → 정지(`null`)로 명세화(단일 앨범 1회 재생 후 정지 포함) | `AlbumShuffle_RepeatOff_StopsAfterFullCoverage` 등 2종 |
| R5 수동 Next (승인안) | 앨범 즉시 탈출 → 앨범 내 이동으로 변경. 기존 테스트 1건 기대값 갱신(승인된 스펙 변경이므로 삭제·완화가 아님) | `AlbumShuffle_ManualAdvance_StaysInsideTheCurrentAlbum` |
| 미확인 | ~~빌드·테스트 미실행~~ → Linux에서 .NET 10.0.401 설치 후 검증: Core+Tests 콜드 리빌드 **0경고 0오류**, 커밋 테스트 본문 그대로 실행해 원본 16/21(실패 5종=R1~R5) → 수정본 **21/21** (RED→GREEN). 단 `dotnet test` 실러너는 Linux에 없는 `Microsoft.WindowsDesktop.App`를 요구해 스크래치 콘솔 러너(`/tmp/run-fixed`, `/tmp/run-orig`)로 실행 | RED→GREEN 실측 |

### L7. 가사 검색 Apply·Save 불량 수정 (2026-09-26 승인·구현 — Linux 검증 완료, App 컴파일은 Windows 게이트)

> 증상: 미리보기로 불러온 가사가 Apply·Save 후에도 화면에 반영되지 않음.

| 항목 | 내용 | 검증 |
|---|---|---|
| CLICK-01 Apply 미적용 | Apply가 새로고침 신호만 보내고 선택을 어디에도 저장하지 않아, 오프라인 가사가 있으면 기존 문서가 다시 표시(상태줄은 거짓 성공). 세션 한정 사용자 오버라이드 도입 + 팬 조회 순서(선택→오프라인→세션). `StoreSessionLyrics` 무호출(호출자 0건)이 미완성 증거 | 오버라이드 3종: 원본 대비 컴파일-RED(CS1061) → 수정본 통과 |
| CLICK-02 Save 왕복 단절 | 커스텀 폴더·하위폴더 템플릿 저장 후 파인더가 못 찾음. 설정된 저장 경로를 탐색 후보에 항상 포함(맨 뒤 추가, 기존 우선순위 유지) | 왕복 3종 RED(저장됨↔null)→GREEN 실측 |
| CLICK-03 stale·덮어쓰기 | 재생 이동 시 무음 무동작 → 상태줄 경고 문구(3개 국어 키 패리티 유지). 자동조회가 적용 선택을 덮지 않도록 스킵, 에디터·파일 저장 시 오버라이드 해제 | 코드 리뷰 + 빌드 |
| 파급 | 인터페이스 멤버 추가에 맞춘 테스트 페이크 2건(`FakeOnlineService`, `FakeLyricsOnlineService`) 갱신 — 콜드 게이트가 포착(CS0535) | Core+Tests 콜드 리빌드 **0경고 0오류** |
| 미확인 | App 본체(WinUI: pane·window·service impl) 컴파일, `dotnet test` 실러너, 전체 스위트, LocalizationTests는 Windows 전용 → Windows에서 확인 필요. Linux 경로 가정 기존 테스트 3종 실패는 수정 전후 동일(환경 요인, CI Windows에서 통과 대상) | — |
| 후속(릴리스 실패) | v1.3.2 첫 태그 CI에서 `LyricsCandidateBuilderTests` 2종 실패(무조건 후보 추가가 기존 정확-목록 단언과 충돌 — F2 설계 미스, 테스트는 계약대로 정상). 커스텀 저장 설정일 때만 후보 추가로 수정 + `DefaultSaveSettings_AddsNoExtraCandidate` 가드 추가. 동시성 1건 실패는 2차 시도 통과로 플레이크 판정. 태그를 수정 커밋으로 이동 후 CI 재실행 | — |

### L8. 수동 Save 덮어쓰기 확인 (2026-09-26 승인·구현 — Linux 검증 완료, App 대화상자는 Windows 확인)

> 요청: 스킵 대신 "파일 있음" 경고 + 확인 버튼으로 덮어쓸 수 있게. 자동저장은 비대화형이라 스킵 정책 유지가 맞고, 수동 Save만 그 자리에서 확인한다는 설계에 합의.

| 항목 | 내용 | 검증 |
|---|---|---|
| 1회성 덮어쓰기 | 리졸버·서비스·인터페이스에 `overwriteOnce` 오버로드 추가. 전역 `OverwriteExisting` 설정은 untouched(자동저장 보호 유지). 확인 시에만 1회 교체 후 기존 `Saved` 경로(오버라이드 해제·새로고침) | `Save_OverwriteOnce_ReplacesExistingWithoutChangingSetting` (원본 대비 컴파일-RED CS1739 → 통과) |
| 확인창 | `SkippedExisting`일 때만 ContentDialog(제목·경로 포함 메시지·덮어쓰기/취소, 3개 국어 키 패리티). 취소 시 기존 스킵 안내 유지 | 코드 리뷰 + 빌드 |
| 파급 | 인터페이스 멤버 추가에 맞춘 테스트 페이크 2건 갱신 | Core+Tests 콜드 리빌드 **0경고 0오류** |
| 미확인 | 검색창 대화상자 실동작은 WinUI라 Windows에서 확인 필요 | — |

### L9. A-B 반복 UX 고도화 (2026-09-26 제안·적대적 검토 통과·구현)

> 제안 범위: P0(시크바 구간 오버레이·버튼 상태 라벨·툴팁 구간 시간) + P1(취소 경로·B<A 침묵 제거·
> 기본 단축키). 적대적 검토 결과: (1) Escape는 `ShortcutKeyNames` 허용 목록에서 의도적 제외
> (포커스 이동/다이얼로그 닫기 전용) → 취소를 **버튼 우클릭**으로 대체. (2) Ctrl+L 미사용 확인,
> 단축키는 델타 영속화라 기본값 추가가 기존 사용자 설정 무손상. (3) **기존 결함 동시 수정** —
> 라디오 등 라이브 소스는 `RadioStreamReader.CurrentTime` 세터가 no-op + `TotalTime=0`인데
> `CycleAbRepeat`가 이를 가드하지 않아, UI가 "반복 중"을 보고해도 점프가 일어나지 않는 가짜
> 루프 상태(보고 ≠ 실제 불일치)가 됨.

| 항목 | 내용 | 검증 |
|---|---|---|
| 코어 | `AbRepeatSupport.cs` 신설(`AbRepeatRejectionReason`·`AbRepeatGate.CanMark`·`AbRepeatWindow` 스냅샷), `CycleAbRepeat` 라이브 가드 + `AbRepeatRejected` 이벤트, B≤A 거부 통보, `CancelAbRepeat`, 바이트→시간 윈도우 스냅샷 | `AbRepeatTests` 컨트롤러 4종 + 게이트 1종 |
| UI | 시크바 A-B 오버레이(구간 밴드·A/B 마커·WaitingForB 플레이헤드 프리뷰, `AbRepeatOverlayCalculator`로 thumb 이동거리 미러), 버튼 라벨 상태화(A–B/A…/A→B), 툴팁에 구간 시간·길이, 우클릭 해제, 거부 시 라벨 플래시(B<A·LIVE)+안내 툴팁(1.4초 복원), Ctrl+L 기본 바인딩, resw 3개 국어 갱신·추가 | 오버레이 기하 10종 + `ShortcutBindingTests` 110 회귀 통과 |
| 불변식 | 오버레이 밴드 가장자리 = 해당 시각의 thumb 중심 x(그려진 윈도우 = 강제 윈도우). 퇴화 입력(길이 0·B≤A·thumb 이하 폭·재생 위치 초과)에서 NaN·음수 폭·슬라이더 이탈 금지 | `AbRepeatTests` 기하 12종 |
| 게이트 | 클린 리빌드 **0경고 0오류**, 전체 스위트 **1,997 통과** | 2026-09-26 실측 |
| 미확인 | 오버레이 실렌더링(thumb 정렬 육안 확인)은 Windows 실기기 청음과 함께 확인 필요 | — |

### L10. N2 잔여 갭 구현 (2026-09-26 구현서 적대적 검토 후 착수·구현 — 커밋 대기)

> 구현서 [docs/n2-dlna-implementation.md](docs/n2-dlna-implementation.md) §7 G1–G4를 사용자 승인
> ("기존 작업 커밋 후 구현서 적대적 검토, 이상 없으면 구현")으로 착수. 검토 결과: 구현서의 하중
> 사실 전부 실증, 확정 3건 — (1) **G1 주입 지점**: 중앙 아트 파이프라인이 없음(각 UI가
> `Track.ArtPath` 직접 구독)이 확인돼, 세션 시작 단일 진입점 `PlaybackController.StartPending`에서
> fire-and-forget 다운로드 → `RemoteArtResolved` 이벤트 → AppServices 릴레이 →
> NowPlayingBar `UpdateArt` 재실행(세대 가드 재사용)으로 확정. 스풀 다운로드가 해드 스타트를 주므로
> 첫 페인트 전 도착이 일반적. (2) **G2 표시명**: 구현서의 "UI 래퍼" 대신 `DlnaServer.DisplayName`
> 계산 프로퍼티(테스트 가능성·단순성 — ComboBox 캐스팅 무변경). (3) **신규 방어**: 복원 ArtUrl은
> http(s) 절대 URI만 허용 — 사용자 편집 M3U8에서 `file://`·ftp 등 로컬 자원 지시 유입 차단
> (구현서에 없던 항목).

| 항목 | 내용 | 검증 |
|---|---|---|
| G1 아트 복원 | `Track.ArtUrl` 신설(AlbumKey·library 스키마 비참여), `DlnaTrackFactory`가 albumArtURI 전달, `M3u.Write`가 ArtUrl 영속(기존 null 고정 해소), `RemoteTrackCodec` 복원+스킴 검증(stale 주석 해소), 컨트롤러 훅(`ResolveRemoteArt` — ArtUrl 절대 URI 재검증·ArtPath 이미 있으면 스킵·캐시가 동일 URL 중복 흡수·실패 무음) | `M3uDpTrackTests` +2(왕복·스킴 Theory 4케이스), `DlnaTrackFactoryTests` +1 |
| G2 동명 서버 | `DlnaServer.DisplayName`("FriendlyName (host)") + ComboBox `DisplayMemberPath` 교체(선택 캐스팅 무변경) | `DlnaDeviceDescriptionParserTests` +1 |
| G3 DIDL 스킵 로그 | per-object catch에 `Log.Debug`(스킵-계속 계약 불변, 시스템적 서버 편차 관측화) | 기존 `DidlLiteParserTests` 회귀 |
| G4 소각 | `DlnaSection.BrowsePageSize = 500` 상수 소유 + 요청에 명시 전달(페이지네이션 산술의 값 소유), `requestedCount` 암묵 의존 해소 | 코드 리뷰 |
| 게이트 | 클린 리빌드 **0경고 0오류**, 전체 스위트 **2,004/2,004 통과**(신규 7종) | 2026-09-26 실측 |
| 미확인 | 복원 트랙 재생 시 아트가 SMTC 첫 페인트 이후 도착하면 SMTC 아트는 다음 갱신까지 비음(드묾 — 스풀 지연이 해드 스타트); 실기기 수동 매트릭스(M1)·Narrator 스모크(M2)는 구현서 그대로 대기 | — |

### L11. 평점 UX 고도화 (2026-09-27 지시·구현 — 커밋 대기)

> 사용자 지시("rating 기능의 부족함" 분석 → "전부 진행해")로 착수. 원인 분석 결과: 백엔드
> (DB+태그 이중 영속화, 스마트 재생목록 연동, 레이스 방지)는 완성도가 높은 반면 UX는
> **발견(미평점이 빈 문자열로 렌더링돼 기능 존재 자체가 보이지 않음) → 입력(재생목록 우클릭
> 2단 메뉴가 유일 경로, 라이브러리·재생 중 화면에 노출 없음, 키보드 불가) → 소비(스마트
> 재생목록 쿼리 문법이 유일 활용처)** 3단계 모두 막혀 있음. 접근성 결함(별점 TextBlock에
> 접근 이름 없음), 큐 트랙 태그 쓰기가 `AppPaths.PhysicalPath` 미경유로 실패하는 잠재 결함,
> 태그 쓰기 실패가 로그만 남고 사용자 통보가 없는 결함을 함께 확인.

| 항목 | 내용 | 검증 |
|---|---|---|
| 순수 명령 계층 | `App/Services/RatingCommands.cs` 신설 — `SelectTargets`(null·빈 경로·스트림 URL 제외, 경로 대소문자 무시 중복 제거, **무변경 스킵**: 이미 동일 별점인 트랙은 대상 제외 → DB·태그·프록시·스마트 재생목록 갱신 전부 노옵), `FormatTagWriteFailure`(실패 0건 → null, 유실 → 지역화된 경고 메시지), `TagWritePath`(큐 프래그먼트 제거 — `AppPaths.PhysicalPath` 경유) | `RatingCommandsTests` 신설(클램프·스트림 제외·중복 병합·무변경 스킵·빈 대상, 실패 메시지 0/N건) |
| RateTracks 개선 | `AppServices.RateTracks`가 RatingCommands 경유로 재작성. 태그 쓰기 실패 건수 집계 후 `RaiseWarning`(InfoBar 단일 슬롯 — Warning은 수동 닫힘), **`RatingsApplied` 이벤트 신설**(UI 스레드, 대상 Track 목록) — NowPlayingBar·LibraryPage 화면 간 동기화. 무변경 시 조기 반환(이벤트·스마트 갱신 없음) | 기존 회귀 + 신규 순수 계층 테스트 |
| 표시 변환기 | `RatingToStarsConverter` 확장 — 무평점 0 → 외곽 별 `☆` 1개(발견 가능성 어포던스), 1~5 → 채움 별, 범위 외 클램프. `RatingAccessibilityConverter` 신설 — "평점 N/5"/"평점 없음"(resw 3개 국어) | 변환기 단위 테스트(0·3·7·음수, 접근성 텍스트 0·3) |
| 재생목록 셀 | 행 별점 TextBlock(양쪽 템플릿: 일반+재생 강조)을 투명 Button 셀로 교체 — 클릭 시 페이지 공유 `Flyout` + WinUI `RatingControl`(키보드·스크린리더 내장). 클릭 행이 다중 선택에 속하면 선택 전체에 적용(컨텍스트 메뉴 `CanRate`와 동일 계약). 플라이아웃 초기값 설정 시 `ValueChanged` 재진입 억제 플래그 — **프로그램적 Value 설정이 즉시 적용·닫힘으로 이어지는 재진입 결함 방어** | `ValueChanged` 가드는 코드 경로 리뷰 + 플레이스토어 규칙상 RatingControl 내장 접근성 |
| 재생목록 메뉴 | 컨텍스트 메뉴 Opening에서 "평점" 하위 메뉴 텍스트를 다중 선택 시 "평점 (N곡)"으로 갱신(`AppStrings.Format`, 단독 선택 시 기본 라벨) | 기존 `CanRate` 회귀 유지 |
| 라이브러리 노출 | 트랙 목록에 평점 컬럼 추가(헤더 정렬 버튼 + 행 셀 버튼+플라이아웃), `SortColumn.Rating`=6 신설 — 오름차순 미평점 먼저→5★, 내림차순 5★→1★→미평점 마지막(0이 최소값이므로 자연 숫자 순), 동률은 LINQ 안정 정렬로 기존 순서 보존. 컨텍스트 메뉴에 평점 하위 메뉴(`Playlist_TrackMenu_*` x:Uid 재사용). 평점 반영: 정렬 기준이 평점이면 `ApplyFilters()` 재정렬, 아니면 실현된 컨테이너만 `FindDescendant<Button>`으로 제자리 갱신(가상화 미실현 행은 x:Bind OneTime이 스크롤 시 신규 값으로 바인딩 — Track.Rating 제자리 변이 계약) | `LibraryFilterServiceTests`에 Rating 정렬 Theory 추가(양방향·미평점 위치·안정성) |
| NowPlayingBar | 제목 우측 소형 평점 버튼(현재 곡 별점 표시) + 자체 플라이아웃 RatingControl. `OnTrackChanged`에서 갱신, **스트림(라디오/원격 URL)은 숨김**(RateTracks가 이미 스트림을 대상 제외 — UI도 계약 일치), `RatingsApplied` 구독으로 재생목록에서 매긴 평점이 즉시 반영 | 코드 경로 리뷰 |
| 접근성 | 셀 버튼 `AutomationProperties.Name`은 **동적 바인딩**(x:Bind 변환기)·플라이아웃 RatingControl은 x:Uid resw 이름 — `AutomationNameScan` 리터럴 금지 게이트 준수(리터럴 어트리뷰트 사용 금지, 데이터 바인딩·x:Uid만) | `AutomationNameGateTests` 회귀 |
| 지역화 | resw 3개 국어 신규 키: `Library_Header_Rating`, `Rating_Selector`(접근 이름+툴팁), `Rating_ClearHint`, `Playlist_RatingCell`(툴팁), `Rating_Accessibility_Format`, `Rating_Unrated`, `Playlist_TrackMenu_RatingMulti`, `Msg_RatingTagWriteFailed` | 빌드 게이트(키 누락 시 XamlCompiler/런타임 폴백 폴백문자열 확인) |
| 불변식 | (1) 보고 별점 = `Track.Rating` = DB rating(무변경 스킵으로 프록시·DB·파일 삼자 일치). (2) 스트림 URL은 어떤 경로로도 평점 대상이 되지 않음(UI 숨김 + SelectTargets 이중 방어). (3) 큐 트랙 태그 쓰기는 항상 `AppPaths.PhysicalPath` 경유. (4) 프로그램적 RatingControl.Value 설정은 사용자 ValueChanged를 유발하지 않음(억제 플래그). (5) 태그 쓰기 실패 ≥1건이면 반드시 사용자 통보(로그만 남기기 금지) | `RatingCommandsTests` + 코드 경로 리뷰 |
| 게이트 | 클린 리빌드 **0경고 0오류**(obj/bin 삭제 후 재빌드 — 1차 패스 WMC1509 연쇄는 2차 빌드로 해소, §5 절차), 전체 스위트 **2,110/2,110 통과**(신규 37종 포함 — 동일 파일에 병행된 우클릭 선택 갱신 수정(+4종)과의 합본 트리 기준). E2E 앱 실행 1회가 병렬 부하에서 간헐 실패 → 격리 재실행 통과로 플레이크 판별(§4) | 2026-09-27 실측 |
| 미확인 | 행 셀 호버 시각 상태·플라이아웃 위치·NowPlayingBar 별점 셀 정렬은 실기기 육안 확인 필요 (Linux 빌드 환경에서 App XAML 렌더링 불가) | — |

### 우클릭 컨텍스트 메뉴 선택 갱신 결함 수정 (2026-09-27 구현·검증 — 커밋 대기)

> 플레이리스트 트랙 우클릭 → "Convert to WAV..."가 무음 no-op하는 사용자 보고. 실측 재현(앱 기동 →
> 우클릭 → 메뉴 항목 클릭 → 피커/토스트/로그 관찰)으로 2중 원인 확정: (1) **ListViewBase가 우클릭의
> ContextRequested를 ListView 수준 XAML 핸들러에 전달하지 않음** — 임시 `[ctxclick]` 진단 로그가
> 우클릭 시 전혀 기록되지 않아 실증. 기존 `OnListContextRequested`는 키보드 호출 외엔 아예 불리지
> 않았고, 사이드바 목록만 RightTapped를 써서 정상 동작하고 있었음. (2) 이벤트가 도달해도 x:Bind
> DataTemplate 안의 요소는 DataContext가 설정되지 않아 `FindAncestorDataContext` 해석이 실패.
> 결과적으로 컨텍스트 메뉴 전체(재생·대기열·평점·변환·이동·제거)가 우클릭 직후에는 선택 0으로 무음
> no-op이었고, 좌클릭 선택이 남아 있으면 엉뚱한(이전 선택) 행에 적용됐음.

| 항목 | 내용 | 검증 |
|---|---|---|
| 이벤트 배선 | `PlaylistList`에 `RightTapped="OnListRightTapped"` 추가, `ContextRequested`와 공통 `RefreshSelectionForContextClick` 경유(키보드 Shift+F10 경로 겸용) | GUI 실측: 선택 없는 우클릭 → Play 재생 시작·Convert 피커 즉시 오픈(수정 전 둘 다 no-op) |
| 행 해석 | `ResolveRowItem` — OriginalSource→`ListViewItem` 컨테이너→`PlaylistList.ItemFromContainer`. x:Bind 안전 | 우클릭 행 = 메뉴 대상(전체 경로 실측, 변환 1건 성공) |
| 순수 결정 | `PlaylistViewModel.SelectionAfterContextClick` — 선택 외 행 클릭→그 행 1개로 축소, 선택 내 행 클릭→다중 선택 보존, 행 밖/해석 실패→선택 불변 | `PlaylistAndLibraryViewModelTests` +4(빈 선택 축소·다른 선택 교체·멀티 보존·행 밖 불변) |
| 게이트 | 순수 계층 필터 테스트 통과, 트리 일관 시점 전체 스위트 2,073/2,073 3회 연속(v1 포함)·클린 리빌드 0경고 0오류(14:24 기준). **RightTapped 재배선(v2) 이후 클린 게이트·전체 스위트는 미실행** — 같은 파일(PlaylistPage)에 동시 진행 중인 평점 기능 작업이 있어 트리 정착 후 재실행 필요 | 2026-09-27 실측 |
| 미확인 | 다중 선택 보존은 순수 함수 테스트로 고정(GUI 육안 미확인), 키보드 Shift+F10 경로 미실측 | — |

### L13. Library·Playlist·Network 아웃라인 통일 (2026-10-03 제안 → 2026-10-04 변형 A 승인·구현 — **커밋 대기**)

> 사용자 지시("세 페이지 디자인이 통일되지 않은 느낌 — outline을 통일감 있게, UI/UX 우선 설계,
> WinUI 3 문서 참고"). 서브 에이전트 전수 조사(LibraryPage 1,077행·PlaylistPage 482행·NetworkPage
> 52행+섹션 UserControl 3종·DawnTheme/DesignTokens)로 불일치를 확정하고, WinUI 3 공식 문서 2종으로
> 근거를 확보한 뒤 변형 4종 HTML 목업
> ([outline-unity-variants.html](design-system/dawn-player/mockups/outline-unity-variants.html),
> 브라우저 배율 100%)를 제작. 이어 사용자가 **"네트워크 탭의 데이터 소스 사이드바로 마이그레이션"을
> 지시(2026-10-04)**해 네트워크 구조는 탭→사이드바로 확정, **변형 A(전면 플랫)+Playlist Italic 제거를
> 승인**해 같은 날 구현·검증까지 마쳤다. 검증 근거는 아래 실행 기록 표.

**불일치 확정 목록 (조사 결과)**

| # | 항목 | Library | Playlist | Network |
|---|---|---|---|---|
| 1 | 리스트 프레임 | 플랫(테두리 없음) | 플랫 | 박스형: PanelSubtle+1px+**R6**(토큰 없는 값) |
| 2 | 행 기하 | 28/R4, 24/R3, 트리 32/R5 | 28/R3, 사이드바 32/R4 | ItemContainerStyle **미설정**(기본 ~46+/기본 R) |
| 3 | 구분선 토큰 | SeparatorSubtle(341·756) vs BorderSubtle(934) **혼용** | BorderSubtle(237·387) | 없음 |
| 4 | 섹션 헤더 | 11 캡션+컬럼헤더 11.5 | 14 SemiBold **Italic**(유일) | 없음(DLNA)/14(Radio)/17(YouTube) 공존 |
| 5 | 페이지 타이틀 | 없음 | 없음 | 있음 — 17px 하드코딩(FontTitle 토큰 미사용) |
| 6 | 섹션 탭 | SegmentedTabStyle(박스 칩) | 없음 | EoleNavTabStyle(밑줄 40px — 셸 칩 탭과 이질, DawnTheme 623–626행이 "승인 범위 밖" 잔존 명시) |
| 7 | 재생 하이라이트 | 좌측 큐(Muted+Glow 박스) | 동일 패턴 | 별도 패턴 + 배지 글자 TextPrimary(→OnAccent 규약 위반) |
| 8 | 리스트 외부 패딩 | 4종 서로 다름 |  | 없음 |
| 9 | 반경 값 분포 | 3/4/5 혼재 (토큰 없음) | 3/4 | 6/기본값 |
| 10 | 숫자 리터럴 | FontSize 등 56곳(3페이지 합계) |  | 페이지 패딩·그리드 셀 크기 하드코딩 |

**WinUI 3 공식 근거 (2026-10-03 학습 문서)**

- **Geometry** (learn.microsoft.com → windows/apps/design/signature-experiences/geometry): 모서리 반경 3단 —
  **8px** 최상위 컨테이너(창·플라이아웃·대화상자), **4px** 인페이지 요소(버튼·리스트 백플레이트·바),
  **0px** 직선 모서리가 다른 직선 모서리와 맞닿는 경우. 전역 리소스 `ControlCornerRadius`(기본 4)·
  `OverlayCornerRadius`(기본 8). → 앱의 산포한 3/5/6은 문서 체계와 무관한 자의 값.
- **SelectorBar** (windows-app-sdk API 문서, SDK 1.5+ 도입·앱은 2.4.0이라 사용 가능): "소수의 옵션 중
  하나를 선택해 **표시되는 콘텐츠를 변경**하는" 공식 컨트롤. 공식 예시(Recent/Shared/Favorites 뷰 전환)가
  네트워크 섹션 전환과 동일 용도 — 구 B-2(SelectorBar 탭)안의 근거였으나 **사이드바 구조 확정으로
  미채택**(탭 자체가 소멸해 필요성도 소멸).
- **NavigationView 디자인 가이드 + WinUI generic.xaml**(design/controls/navigationview ·
  `microsoft.windowsappsdk.winui` 2.3.6 로컬 템플릿 대조): 왼쪽 모드에서 **선택 항목이 왼쪽 가장자리를
  따라 선택 인디케이터**를 그린다(공식 선택 언어). 콘텐츠 격리 기본값 — `NavigationViewContentGridCornerRadius`
  =**8,0,0,0**, `NavigationViewContentGridBorderThickness`=**1,1,0,0**, `NavigationViewContentGridBorderBrush`
  =**CardStrokeColorDefaultBrush**, 배경은 LayerFill(`LayerFillColorDefault` 다크 **#4C3A3A3A**), 기본
  `ListViewItemMinHeight`=**40**. 템플릿 수정보다 **light-weight styling(리소스 재정의) 권장**. — **D안의 근거**.

**구조 결정 (2026-10-04 사용자 지시 — 네트워크 탭 → 데이터 소스 사이드바)**

| 항목 | 설계 |
|---|---|
| 골격 | NetworkPage 3행(타이틀/탭/콘텐츠) → 2열(사이드바 200px + 콘텐츠 `*`) — **3페이지가 모두 "좌측 사이드바 + 콘텐츠" 동일 구조** |
| 사이드바 표면 | `PanelSubtleBrush` + 우측 `SeparatorSubtleBrush` 1px(플러시 — 코너 0·테두리 없음, Library/Playlist 사이드바와 동일 표면). 헤더 "데이터 소스"(Playlist 사이드바 헤더와 동일 타이포 — Italic 여부는 공통 결정에 연동) |
| 사이드바 행 | 소스 3종(인터넷 라디오/DLNA/YouTube) — 아이콘+이름(+보유 카운트), 높이 32·R4·선택 틴트 `ListViewItemBackgroundSelected*`(Playlist 사이드바 행과 동일 토큰), 키보드·자동화 이름은 Playlist 사이드바 관례 준용 |
| 전환 메커니즘 | 기존 섹션 UserControl 3종 겹침+Visibility 전환 **유지** — 선택 소스만 RadioButton 탭에서 사이드바 행으로 교체(바인딩·상태 최소 변경) |
| 제거 | 섹션 탭 행, 페이지 타이틀 행(Library와 동일 — 사이드바가 구조 역할 흡수), `EoleNavTabStyle`(Network 전용 스타일 → 미사용화 후 삭제, 죽은 스타일 잔존 금지 — CaptionButtonStyle 삭제 전례) |
| UX 근거 | (1) Library/Playlist와 동일 골격이라 구조 통일 — 탭 문법 논쟁 자체 소멸. (2) 소스 추가(팟캐스트·WebDAV 등)가 행 1개 — 탭 개수 한계 해소. (3) 콘텐츠 폭 확보(DLNA 탐색·YouTube 그리드) |

**변형 (사이드바 공통 구조 위에서 콘텐츠 프레임 처리만 선택 — 목업 갱신 완료)**

| 변형 | 정의 | 판정 |
|---|---|---|
| A 전면 플랫 | 콘텐츠 리스트 박스 제거 → 3페이지 전부 플랫 | 콘텐츠 영역에 떠 있는 리스트가 경계 없이 뭉개짐. 0px 규칙은 "맞닿는 경우" 한정이라 문서 취지와 어긋남 |
| **B-1 역할 기반(박스 콘텐츠)** | 플러시 밴드=0px 유지 / 콘텐츠 묶음=PanelSubtle+BorderSubtle 1px+**R4** 토큰화 | **추천** — Geometry 규칙 부합 + 최소 침습 + 짧은 묶음 리스트(라디오 24개국 등)의 덩어리 가독성 |
| C 전면 카드 | Library·Playlist 중앙까지 전부 박스화 | 통일감은 최대이나 박스 중첩·밀집 리스트 소음·대규모 재작업, 0px 규칙 충돌 지점 발생 |
| **D WinUI 공식 패턴(모범 사례)** | 콘텐츠=**NavigationView 콘텐츠 레이어 기본값**(LayerFillColorDefaultBrush 시트(다크 #4C3A3A3A) + CardStrokeColorDefaultBrush 스트로크 1,1,0,0 + 코너 8,0,0,0), 시트 안 리스트=플랫폼 기본 ListViewItem(호버·선택·키보드·내레이터 상태 기본) / 반경·표면은 공식 리소스(ControlCornerRadius 4·OverlayCornerRadius 8·LayerFill) 우선 — 커스텀 토큰·스타일 최소화(light-weight styling 권장). **사이드바는 공통안(Playlist 언어) 그대로 — 변형 간 차이는 콘텐츠 처리만**(초판 목업에서 D의 사이드바 행까지 왼쪽 필로 바꿨던 것은 비교 축 혼합 — 사용자 지적으로 공통화 정리) | **모범 사례 준수 최강** — 접근성·키보드 상태 기본 제공, SDK 업데이트 따라감, 설정 앱 질감. 단, Eole 커스텀 팔레트와 반투명 LayerFill 질감 갈림 + 앱 토큰 밖 공식 리소스 1종(표면) 추가 |

**공통 정비 (변형 무관 6건)** — ⓪ 구조 마이그레이션: 위 "구조 결정" 표 그대로(탭·타이틀·EoleNavTabStyle 제거, 데이터 소스 사이드바 신설). ① 구분선 역할 분리: 내부 헤어라인=SeparatorSubtleBrush 단일 / 박스 외곽=BorderSubtleBrush 1px 단일. ② 반경 스케일 토큰화: **0(플러시)/4(행·박스·세그먼트)/5(칩)/8(오버레이 — 시스템 관리)** 만 허용, R3·R6 제거. 칩 R5는 방금 승인된 셸 탭 룩 보존을 위한 명시적 예외 토큰(셸 변경은 범위 밖). ③ `SectionHeaderText`(FontSubtitle 14 SemiBold) 신설 — 4종 헤더 스케일 수렴, DLNA 헤더 부재 해소(사이드바 전환 후 콘텐츠 헤더로 존재), 페이지 타이틀은 구조 결정으로 제거(토큰화 불요). 부속 결정: Playlist 헤더 Italic(유일) 유지/제거는 사용자 선택 대기. ④ Network 행 정비: ItemContainerStyle 신설 + `MediaRowMinHeight 44` 토큰(28 밀도행과 병존) + 선택 틴트 ListViewItemBackgroundSelected* 통일 + Radio 재생 배지 전경 OnAccentBrush. ⑤ 리터럴 제거: FontSize 56곳→Font* 토큰, 간격→Space* 토큰.

**검증 전략 (§3 규약 — 실패 시나리오 테스트 선설계)**

- 소스 스캔 게이트 테스트 신설(AutomationNameScan 관례 준용, bin/obj 제외): (1) 3페이지+Network 섹션 XAML에 토큰 없는 `CornerRadius` 리터럴 금지(허용: 토큰 참조 또는 {0,4,5}), (2) 섹션 헤어라인의 `BorderBrush`는 SeparatorSubtleBrush·박스 외곽은 BorderSubtleBrush 이외 금지, (3) `FontSize` 리터럴 금지(Font* 토큰만), (4) `EoleNavTabStyle` 정의·참조 금지(삭제 확인) + NetworkPage에 섹션 탭·페이지 타이틀 행 잔존 금지. 위반 시 파일:행 목록 반환.
- 불변식: 플러시 밴드는 코너·테두리 0 / 플로팅 박스는 PanelSubtle+BorderSubtle+R4 / 재생 배지 전경=OnAccentBrush / 섹션 헤더=SectionHeaderText. — "보고 토큰 ≠ 실제 적용값 금지"(8ebfe12 교훈의 XAML 판).
- 신설 토큰은 `DesignTokenValues.cs` 미러 + `DesignTokenTests` 확장. 클린 리빌드 0경고 0오류 + 전체 스위트 1회(§4·§5 절차).

**실행 기록 (2026-10-04, 변형 A + Italic 제거 — 커밋 대기)**

| 항목 | 내용 | 검증 |
|---|---|---|
| 게이트 선설계 | `OutlineUnityGateTests` 5종 신설(① 반경 스케일 0/1/2/4/5 — 3·6 재유입 금지, ② Network 사이드바 골격+탭·타이틀·EoleNavTabStyle 퇴출, ③ Radio/DLNA 플랫 리스트+미디어 행 44/R4+재생 배지 OnAccent, ④ 텍스트 FontSize 토큰 전용(FontIcon·고정 글리프 상자 예외)+SectionHeaderText(Italic 금지) 수렴, ⑤ 1px 헤어라인=SeparatorSubtle 단일) — **구현 전 5/5 실패(레드) 확인 후 착수** | 레드→그린 |
| 토큰 | DesignTokens `RowCornerRadius 4`·`ChipCornerRadius 5`·`MediaRowMinHeight 44` 신설 + `DesignTokenValues.Radius/Rows` 미러 + DesignTokenTests 확장. 게이트가 리터럴 값을 미러 상수로 단언 | `DesignTokenTests` |
| Network 구조 | NetworkPage 2열 재작성 — 데이터 소스 사이드바(200px 고정, Playlist 사이드바 동일 언어: PanelSubtle+헤어라인, 행 32/R4·선택 틴트 ListView) + `SelectionChanged` 전환(**파싱 중 IsSelected 기동 시 섹션 컨트롤 null 가드** — 첫 행 IsSelected가 파스 도중 이벤트를 일으킨다). 섹션 탭·페이지 타이틀 제거, `EoleNavTabStyle` 삭제(정의+주석 정리) | 게이트 ② + 실행 육안 |
| Network 섹션 | Radio/DLNA 리스트 플랫화(PanelSubtle+1px+R6 박스 제거)+ItemContainerStyle 신설(MinHeight 44·R4), Radio 재생 배지 전경 OnAccent·R4, **DLNA 헤더 신설**(`Network_Dlna_Title`), Radio/YouTube 헤더 SectionHeaderText 수렴(YouTube 17→14), YouTube 썸네일 R6·duration 배지 R3→4 | 게이트 ③④ + 스크린샷 |
| Playlist/Library | Playlist Italic 3곳(사이드바·제목·앨범 그룹) 제거→SectionHeaderText, 헤어라인 BorderSubtle→SeparatorSubtle ×2(Library 큐 패널·Playlist 중앙), R3→4 ×5(재생 하이라이트·이미지 클립·행 Setter), **FontSize 리터럴 53건 토큰화**(스크립트 일괄 — FontIcon·고정 W/H 글리프 상자 제외, 값 등가 치환이므로 무시각 변화) | 게이트 ①④ + diff 전수 확인 |
| resw | `Network_SidebarHeader`·`Network_Dlna_Title` 3개 국어(en-US/ko-KR/ja-JP) | 실행 렌더(en 확인) |
| 게이트 | 클린 리빌드 **0경고 0오류**(obj 삭제 후 — 실행 중 앱 인스턴스가 bin 잠금해 종료 후 진행), 전체 스위트 **2,187/2,187**(신규 5종) | 2026-10-04 실측 |
| 실행 검증 | 앱 구동 → Network 탭: 사이드바 렌더·Radio/DLNA 전환(**실제 입력 이벤트+UIA Select 이중 확인** — CUA 기본 AXPress는 ListViewItem에서 no-op인 자동화 계층 현상, 앱 결함 아님)·DLNA 신설 헤더·플랫 빈 상태·en resw 렌더 확인 | 컴퓨터 사용 캡처 |
| 수반 수정 | E2E `App_Launches`가 **저장 창 배치에 의존**(당시 608px<730 → U3 컴팩트 하단바가 VolumeSlider Collapsed → Require 실패) — 플레이크가 아닌 환경-의존 결함으로, 하니스가 Require 전 표준 크기(1200×800)로 리사이즈하도록 견고화(앱 코드 무변경). 격리 재실행 2회 실패로 근거 확인 후 수선 | 격리 재실행 → settings.json 근거 → 수선 후 E2E 2/2 |
| 미확인 | Light 테마 렌더·고DPI 실기기 육안, DLNA 실서버 탐색은 N2 검증 절차 그대로 | — |

### L14. 페이지 콘텐츠 인셋·간격 리듬 통일 (2026-10-04 감사 → **전부 승인·구현 — 커밋 대기**)

> 사용자 보고("Network 탭에 메인 window padding이 없다")로 ui-ux-pro-max 감사 실시 — 세 메인
> 페이지+Network 섹션 6파일의 Margin/Padding/Spacing 리터럴 **164건 전수 조사**. 근원: L13 사이드바
> 전환에서 구 탭 구조의 루트 `Padding="24,16,24,12"`가 소실(F1). 비-제로 간격 성분 246개 중
> **61%가 4px 토큰 그리드 이탈**(6·10·14·5·22류), `Space*` 토큰의 페이지 소비는 0회.

| ID | 내용 | 위치 | 심각도 |
|---|---|---|---|
| F1 | **Network 콘텐츠 4방향 인셋 0** — 사이드바 헤어라인·창 위/우측·하단바 헤어라인에 0px 밀착(구 패딩 소실) | NetworkPage:71 | **P0** |
| F2 | 헤더 액션 버튼(추가/새로고침/구성) 우측 플러시 — 하단바 버튼은 18px 인셋과 수준 차 | Radio:35 / Dlna:39 / YT:28 | P1 |
| F3 | Radio/DLNA 리스트 right 0 — 선택 하이라이트 카드가 창 끝까지, 스크롤바가 행 위 겹침(Library right 8/10, Playlist 20) | Radio:48 / Dlna:86 | P1 |
| F4 | 페이지 헤더 좌우 인셋 3패밀리 분열 — Library 14 / Playlist 22 / Network 0 | L:341·756, P:165 | P1 |
| F5 | Network 섹션 제목(x=8) vs 첫 행 텍스트(x=16) 8px 어긋남 — 3페이지가 2/4/-8px로 제각각 | Radio:24-26·60 | P1 |
| F6 | 620px 최소폭 창에서 Network 콘텐츠 420px+right 0 — compact 하단바(12px 인셋)와 우측 리듬 어긋 | MW:70·560 | P1 |
| F7 | Library 뷰 전환 시 인셋 14→8 이동 + 컬럼헤더/행 2px 어긋 | L:427·791·756 | P2 |
| F8 | Playlist 헤더 22 vs 헤어라인·리스트 20 | P:165·236 | P2 |
| F9 | Library 트리 헤더만 `12,10` — Playlist/Network 사이드바 헤더 `16,14`와 이격 | L:249 | P2 |
| F10 | 빈 상태 5종 제각각(아이콘 22/28/36, Spacing 4/8/10, MaxWidth 무/380/420/440) | 5곳 | P2 |
| F11 | 스플리터 히트존 Library 10px vs Playlist 8px | L:324, P:149 | P2 |
| F12 | YouTube 섹션 헤더만 선행 FontIcon 누락(Radio/DLNA는 있음) | YT:23-25 | P2 |
| F13 | 버튼 패딩 `10,5`×9 vs `10,6`×4, 아이콘 Spacing 7 vs 8 혼용 | 6파일 | P2 |
| F14 | 리스트 bottom 16/20/24 혼재, YouTube ToS 문구 하단 0px 밀착 | YT:166 | P2 |

**수리안(제안)** — Network 콘텐츠 열에 `Padding="20,14,20,16"`(**Playlist 패밀리**: 사이드바를 값까지 빌린 원본 패밀리, 하단바 18과 Δ2 — Library 14는 커버그리드·밀집 테이블 전용 패밀리) + Playlist 헤더 22→20 정규화 + F5 좌열 정렬 + F12 아이콘 보강 + 버튼 `10,6`/Spacing 8 통일 + 빈 상태 표준(아이콘 28/Spacing 8/MaxWidth 380) + F11 히트존 8 통일. 범위 선택지: **(1) P0+P1만 최소 수리 / (2) P0–P2 전부**. 옵션: `PageContentPadding`·`PageHeaderMargin` Thickness 토큰 신설(DesignTokenValues 미러+게이트 갱신 — Space* 미소비 문제의 의미 토큰 대응).

**실행 기록 (2026-10-04, "전부 승인" — P0–P2 + 토큰 전체 구현, 커밋 대기)**

| 항목 | 내용 | 검증 |
|---|---|---|
| 게이트 선설계 | `LayoutRhythmGateTests` 4종 신설(토큰 정의+소비 동시 단언, 오프그리드 `22,14,22,6`·`10,5`·`14,5`·`Spacing 7` 퇴출, Library 사이드바 헤더 원점 16,14·스플리터 10/-5 패턴 퇴출) — **구현 전 4/4 실패(레드) 확인** | 레드→그린 |
| 토큰 | DesignTokens `PageContentPadding 20,14,20,16`·`PageHeaderMargin 20,14,20,6` (Thickness — 공식 NavigationViewHeaderMargin 패턴과 동일 방식, 스칼라 미러 대상 외) | 게이트 |
| F1·F2·F6 | NetworkPage 콘텐츠 열 `Padding="{ThemeResource PageContentPadding}"` — 헤더 버튼·리스트가 20/14 인셋 안으로, 620px 창에서도 right 리듬 확보 | 실행 스크린샷 |
| F3 | Radio/DLNA 리스트 패딩 유지(0,2,0,16) — 패널 인셋이 창 끝 20px을 확보해 하이라이트 카드·스크롤바 문제 해소 | 스크린샷 |
| F5 | Radio/DLNA 행 컨테이너 4,1→**6,1**·행 Grid 12,9/12,8→**16,8** — 제목(x=42)과 행 텍스트(x=42) 정렬 | 스크린샷 |
| F4·F8 | Playlist 헤더 `Margin="{ThemeResource PageHeaderMargin}"`(22→20) — 헤어라인 20·리스트 20과 동일 열 | 게이트 |
| F7 | Library 트랙 리스트 8,4,8,20→**14,4,14,20** + 컨테이너 8,3→**4,3** — 컬럼헤더 텍스트(x=32)와 행 텍스트(x=32) 0px 정렬 + 뷰 전환 인셋 이동 6px→0 | 계산+스크린샷 |
| F9 | Library 트리 헤더 12,10→**16,14,14,8** + 콤보박스 10,0,10,8→**16,0,14,8** + 트리 리스트 0,0,4,16→**6,2,6,16** — 사이드바 3종 동일 원점 | 게이트 + 스크린샷 |
| F10 | 빈 상태 표준(아이콘 28·Spacing 8·MaxWidth 380) — Playlist 36/10/440, Radio 22, DLNA 22/420, YouTube Spacing 4·MaxWidth 무 → 전부 수렴 | 스크린샷 |
| F11 | Library 좌측 스플리터 10px/-5 → **8px/-4**(Playlist 패턴 통일 — Width="10" 자체는 트리 셰브런 템플릿 정당 용례라 게이트는 결합 패턴만 민다) | 게이트 |
| F12 | YouTube 섹션 헤더에 선행 FontIcon(E8F2 — 사이드바 행과 동일 글리프)+Spacing 8 — Radio/DLNA와 동일 패턴 | 스크린샷 |
| F13 | 버튼 `10,5`×9→`10,6`·`14,5`×1→`14,6`·아이콘-라벨 `Spacing 7`×2→8 (스크립트 일괄) | 게이트 |
| F14 | YouTube ToS·리스트 하단 — 패널 bottom 16이 확보(리스트 bottom 패딩 유지) | 스크린샷 |
| 게이트 | 클린 리빌드 **0경고 0오류**, 전체 스위트 **2,191/2,191**(신규 4종) | 2026-10-04 실측 |
| 실행 검증 | Network(Radio/DLNA)·Playlist·Library 캡처 — 콘텐츠 인셋 4방향 확보, DLNA 헤더-행 정렬, Playlist 제목 무이탤릭·20 정렬, Library 트리 헤더 원점 확인 | 컴퓨터 사용 캡처 |
| 미확인 | Light 테마·고DPI 육안, 컴팩트(<730px) 하단바와의 우측 리듬은 620px 최소폭 수동 확인 잔여 | — |

### 상단바 높이 44 완화 시도 → 48 롤백 (2026-10-04 — 커밋 대기)

> 사용자 결정("살짝만 줄이자")으로 Tall 48 → 바 44 완화(XAML 행·TitleBar MinHeight·`TitleBarRowHeight`
> 3곳, 시스템 캡션은 Tall 48 유지)를 구현했으나, 사용자가 "어떤 방식이 가장 자연스러운가 — 48 롤백?"
> 으로 재판단 → **48 롤백 확정**. 근거: 캡션 높이는 플랫폼이 32/48만 제공하므로 44은 어느 쪽과도
> 정합 불가(Tall 유지 시 호버 4px 블리드가 매 캡션 조작마다 반복, Standard 32는 글리프 6px 상승 —
> 구 빨간 선 문제의 확대), 반면 44의 이득(콘텐츠 4px)은 사실상 지각 불가. **48 = 정합이 유일한
> 자연스러운 높이**. 게이트: `MainWindowTitleBarLayoutTests` 계약 48 복원 + `TitleBarRowHeight = 44`
> 금지 단언 추가(재시도 방지, 경위는 MainWindow.xaml/.cs 주석에 기록), 클린 리빌드 **0경고 0오류**,
> 전체 스위트 **2,191/2,191**, 실행 캡처로 48 복원·캡션 정렬 확인.

### L15. 하단바 별점 위치 이동 (2026-10-04 보고 → 변형 A 승인·구현 — **커밋 대기**)

> 사용자 보고("하단바 별점 위치가 여전히 별로"). 원인: 405f07f의 별점 버튼이 제목 행 끝 Auto 열에
> 고정돼 있어 **정보 블록과 트랜스포트의 경계에 떠 있음**(아이콘 언어는 L11에서 Segoe로 통일됐지만
> 위치는 그대로). 목업
> ([rating-position-variants.html](design-system/dawn-player/mockups/rating-position-variants.html),
> 브라우저 배율 100%)에 4안 제시 — 미평점 회색 ☆ 기준, 플라이아웃(Top)·스트림 숨김(IsRateable)·
> `ShowNowPlayingRating` 토글 계약은 전 안 불변.

| 변형 | 정의 | 비고 |
|---|---|---|
| **A 제목 인라인** | 별을 제목 텍스트 바로 뒤로 — 제목 열 Auto+별+여백 `*` | **추천** — "이 곡의 속성" 의미 연결 최강(스포티파이/YTM 패턴). 긴 제목이 별을 밀어내지 않게 제목 MaxWidth 코드비하인드 갱신 필요(~10줄) |
| B 정보 블록 세로 중앙 | 현재 x 유지, 제목 행 → 정보 블록 3줄 세로 중앙 | 최소 변경(요소 이동만) — 경계 떠있는 느낌 절반 해소 |
| C 우측 Tools 편입 | 볼륨·대기열·가사 줄에 TransportButton 크기로 | 그리드 정합 최고·구현 단순 — 메타데이터 편집의 의미 연결 약화 |
| D 트랜스포트 행 끝 | A-B 뒤 구분선+별 | 손 위치 최고 — 재생 제어 혼합·중앙 열 폭 압박 |

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | 변형 선택(A/B/C/D) → 사용자 | **A 승인·구현 완료** |
| 2 | 구현(+A인 경우 제목 MaxWidth 코드비하인드) + `UpdateTrackRatingCell` 상태 전환 회귀 확인 | 완료 |
| 3 | 필터 테스트 → 전체 스위트 + 실행 육안 | 완료 |

**실행 기록 (2026-10-04 변형 A 구현 — 커밋 대기)**

| 항목 | 내용 | 검증 |
|---|---|---|
| 게이트 선설계 | `NowPlayingRatingInlineGateTests` 3종 신설(① 제목 행 3열 [제목 Auto][별 Auto][여백 *] — 구 [제목 *][별 Auto] 재유입 금지, ② 코드비하인드 `TrackTitle.MaxWidth` 갱신+`TitleRow.SizeChanged` 배선+별 가시성 반영 — Collapsed 요소 ActualWidth 잔존 함정, ③ 스트림 숨김·플라이아웃 계약 생존) — **구현 전 2/3 실패(레드, 기존 계약 1종은 통과) 확인** | 레드→그린 |
| 구현 | NowPlayingBar.xaml 제목 행 `[Auto][Auto][*]` 3열 + `TitleRow` 명명 / 코드비하인드 `UpdateTitleMaxWidth()`(행 폭-별 가시 폭-6px 마진, 하한 24px) + `TitleRow.SizeChanged`·`UpdateTrackRatingCell` 양 경로 호출 | 게이트 3종 |
| 게이트 | 클린 리빌드 **0경고 0오류**, 전체 스위트 **2,194/2,194**(신규 3종) | 2026-10-04 실측 |
| 실행 검증 | 앱 구동 → 별이 제목("Elf ☆") 바로 뒤 배치 확인 + 별 클릭 → RatingControl 플라이아웃(Top) 정상 개방 | 컴퓨터 사용 캡처 |
| 후속 표시 변경(같은 날 사용자 제안 승인) | 평점 부여 곡 = 단일 아이콘 대신 **평점 수만큼 채운 별**(E734×N, 3점 → 3개 — 재생목록·라이브러리 표의 DisplayText 계약과 동일 표현). 미평점은 외곽 별(E735) 1개 유지. 구현: 단일 `TrackRatingIcon` FontIcon → `TrackRatingStars` StackPanel(코드비하인드가 N개 채움) | 게이트 1종 추가(레드→그린): TrackRatingIcon 재유입 금지+컨테이너 채움+E734/E735 단언 |
| 후속 렌더 수정(사용자 지적 "평점이 부여됐는데 별이 비어있어 보인다") | **원인 확정**: Segoe Fluent Icons의 E734(FavoriteStarFill)는 Fluent 스타일에서 **가운데 파인 도넛 모양**으로 렌더링돼 12px에서 빈 별로 읽힘(평점 2·3 부여 후 근접 캡처로 확인 — 개수는 정확, 모양이 문제). 수리: 별 아이콘 `FontFamily`를 **Segoe MDL2 Assets**로 고정(MDL2의 E734는 속이 찬 별, Win10/11 공용 시스템 글꼴) | 재실행 캡처: 평점 4 → 속이 찬 별 4개 렌더 확인 |
| 후속 수정(사용자 보고 ①"미평점 곡인데 플라이아웃에 첫 별이 채워져 보인다" ②"플레이리스트 행의 별 위치가 밀린다") | ① 원인 확정: **WinUI 3 RatingControl은 미평점(0) 렌더링이 불가** — Opening의 Value=0이 최소값 1로 강제됨(라이브 AXSetValue("0") → "1 of 5" 재확인) + 세션 복원 후 재생 전엔 CurrentItem이 null이라 Opening이 early-return해 값 설정 자체가 생략됨. 수리: 플라이아웃의 RatingControl을 **앱 소유 별 5개 버튼 행**으로 교체 — 미평점 = 모두 외곽 별, 별 N 클릭 = N점 적용+하단바 즉시 갱신+플라이아웃 닫힘, 같은 별 재클릭 = 지우기(0), 접근 이름은 코드 설정(별 N점 — resw 3개 국어 추가). ② 원인 확정: 재생 강조 행의 후행 열 구조가 일반 행과 달라(Auto 재생시간+Padding 8,0 vs 54 고정+Padding 2,0) 별 x가 5px 밀림. 수리: 재생 행 후행 열을 일반 행과 동일([Auto 별][54]·Padding 2,0)로 정렬 | 스크린샷: 미평점 플라이아웃 전부 외곽 ✓, 재생 행 별 x 정렬 ✓, 별 3 클릭 → 하단바 ★★★ 즉시 갱신 ✓, 재클릭 → 지우기 ✓ |
| 게이트(최종) | 클린 리빌드 **0경고 0오류**, 전체 스위트 **2,196/2,196**(신규 게이트 4종 반영 — E2E 1건은 이전 실행의 병렬 부하 플레이크, 중간 실행에서 격리 2/2 통과 확인) | 2026-10-04 실측 |
| 실행 검증 | 미평점 플라이아웃 전부 외곽 ✓, 별 3 클릭 → 하단바 ★★★ 즉시 갱신 ✓, 재클릭 → 지우기 ✓ | 스크린샷 |
| 미확인 | 긴 제목 말줄임(긴 이름 트랙 수동 확인), Light 테마 렌더 | — |

### L16. Network 섹션 헤더 통일 (2026-10-04 보고 → 즉시 구현 — **커밋 대기**)

> 사용자 보고("network 탭의 레이아웃이 통일감이 없다. radio, youtube, dlna 탭의 타이틀 크기와
> 위치는 제각각이며 UI/UX도 그지같다"). 원인 3건: ① **액션 앵커 불일치** — Radio는 `*` 필러로
> 추가 버튼을 우측 끝에 두는데 YouTube 헤더는 가로 StackPanel이라 구성 버튼이 제목 바로 옆에
> 붙고, DLNA는 헤더에 액션이 아예 없다(새로고침이 아래 툴바 행). ② **메타 위치 불일치** —
> Radio 방송국 수는 우측 클러스터, YouTube 의존성 배지는 제목 옆. ③ **위계 붕괴** — YouTube
> "최근 항목"이 섹션 제목과 동일한 SectionHeaderText(14 SemiBold)라 한 화면에 동급 헤더 2개.
> 이미 통일된 축(아이콘 14px accent, RowSpacing 10, 버튼 10,6·R4, 콘텐츠 원점
> PageContentPadding)은 변경 없음. ui-ux-pro-max 자문: ux 도메인 직접 매치 없음(일반 탐색
> 항목만 반환) — 스킬 우선순위 #4(일관성 must-have)·#6(위계는 스케일 단계 차이)과
> design-system MASTER.md의 토큰 타이포 계약(Segoe UI Variable 11/12/13/14/17)을 근거로 사용.

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | 공용 헤더 계약(3존 골격) 정의 + 세 섹션 수렴 | 완료 |
| 2 | 위계 사다리 신설(GroupHeaderText) + 게이트 선설계·계약 갱신 | 완료 |
| 3 | 필터 테스트 → 클린 리빌드 → 전체 스위트 + 실행 육안 | 완료 |

**실행 기록 (2026-10-04 — 커밋 대기)**

| 항목 | 내용 | 검증 |
|---|---|---|
| 공용 헤더 계약 | 3존 골격: 아이콘(14px accent)+SectionHeaderText(Margin 8,0,0,0) 좌측 \| `*` 필러 \| 메타(우측 클러스터, 액션 직전 12px 간격) \| 액션(Padding 10,6·R4, 마지막 컬럼 우측 끝 앵커). Radio 기준 유지 / YouTube StackPanel→Grid(배지 우측 메타, 구성 우측 끝) / DLNA 서버 새로고침을 헤더 우측 앵커로 승격(툴바는 ComboBox+BusyRing+재시도, 4열→3열 정리) | 실행 캡처 3종 |
| 위계 사다리 | DawnTheme에 `GroupHeaderText`(12 SemiBold, TextSecondaryBrush) 신설 — 14 SemiBold Primary(섹션) > 12 SemiBold Secondary(그룹 라벨) > 11.5 SemiBold Tertiary(테이블 컬럼). "최근 항목"을 GroupHeaderText로 강등 | 실행 캡처: 제목 대비 작고 흐린 라벨 확인 |
| 게이트 | `NetworkHeaderUnityGateTests` 3종 신설(① 3존 골격 공유 — Grid 뿌리+필러+아이콘→제목→액션 순서·액션 마지막 컬럼·10,6/R4, ② 메타 우측 클러스터 고정 — 구 인라인 배치 재유입 금지, ③ 위계 사다리 정의+최근 항목 소비) + `OutlineUnityGateTests` YouTube 계약 갱신(SectionHeaderText 2곳→**1곳**, 최근 항목 GroupHeaderText) | 필터 12/12 그린(초기 1건은 테스트 자체 결함 — 파일 정의 순서 단언을 스케일 단언으로 수리) |
| 빌드·스위트 | 클린 리빌드 **0경고 0오류**, 전체 스위트 **2,199/2,199**(신규 3종 반영 — 1차 실행의 1건 실패는 병렬 부하 플레이크: E2E 격리 2/2 + 재실행 전수 그린으로 판별) | 2026-10-04 실측 |
| 실행 검증 | Network 페이지 Radio/DLNA/YouTube 전환 캡처 — 세 헤더 모두 제목 동일 x/y, 메타+액션 우측 끝 동일 앵커, DLNA 첫 방문 지연 활성화·자동 스캔 정상 | 컴퓨터 사용 캡처 |
| 미확인 | Light 테마 렌더, 좁은 폭에서 긴 지역화 문자열(독일어류) 헤더 줄바꿈 동작 | — |

### ARM64(win-arm64) 지원 (2026-10-04 질의 → 조사 보고 → 승인·구현 — **커밋 대기**)

> 사용자 질의("arm64 지원 작업 가능해?") → 조사 결과 보고 → 승인 후 구현. 조사 결론: 소스 P/Invoke는
> user32/gdi32/kernel32/shell32/comctl32뿐(ARM64 Windows 네이티브 탑재 시스템 DLL, x64와 포인터
> 크기 동일), 벤더링 NAudio 패치 패키지는 순수 관리형(lib/net9.0만 존재), Microsoft.Data.Sqlite는
> win-arm64 e_sqlite3 네이티브를 포함한 SQLitePCLRaw 번들 사용, WASDK 2.5.1·.NET 10
> self-contained 모두 win-arm64 지원, 네이티브 종속이 있는 NAudio.Asio는 미사용 — **C# 코드 변경
> 없이 빌드 구성·스크립트·CI만으로 지원 가능**. Inno Setup은 문서 원본(isetup.xml Architecture
> Identifiers)에서 사양 확정: `arm64`는 "Arm64 Windows 실행 시스템", `x64compatible`는 "x64 바이너리
> 실행 가능 시스템(x64 Windows + ARM64 W11 에뮬레이션)". ISPP는 #if/#elif/#error·문자열 `==` 지원.

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | 사전 조사(네이티브 의존성 전수 스캔) + Inno Setup 아키텍처 사양 확정 | 완료 |
| 2 | App.csproj ARM64 플랫폼·RID 추가 + 빌드 스크립트 Platform 파생·ISCC 아키텍처 전달 + iss 매개변수화 | 완료 |
| 3 | release.yml 3-잡 재구성(validate → build 매트릭스 → release 병합) + ci.yml windows-11-arm 잡·드라이런 매트릭스 | 완료 |
| 4 | 로컬 검증: 클린 win-arm64 publish PE 머신 검사 + win-x64 회귀 + 전체 스위트 | 완료 |

**실행 기록 (2026-10-04 — 커밋 대기)**

| 항목 | 내용 | 검증 |
|---|---|---|
| App.csproj | `Platforms` x64→x64;ARM64, `RuntimeIdentifiers` win-x64→win-x64;win-arm64, `RuntimeIdentifier`는 조건부 기본값(win-x64) — `-r win-arm64 -p:Platform=ARM64` 오버라이드 경로 확보 | 클린 퍼블리시 2회 성공 |
| build-installer.ps1 | `-Runtime` 검증(win-x64/win-arm64 외 Write-Error), Platform(x64/ARM64)·Arch(x64/arm64) 파생, publish 인자에 `-p:Platform` 추가, ISCC에 `/DMyAppArch` 전달, 인스톨러 검증 경로 `-{arch}.exe`, SHA256SUMS를 LF+UTF-8 no BOM으로 정규화(기존 WriteAllLines의 CRLF는 Linux `sha256sum -c`에서 파일명 오류 유발) | 파서 0오류, 양쪽 아키텍처 종단 실행 |
| DawnPlayer.iss | `MyAppArch` 매개변수화(ISPP) — arm64: `ArchitecturesAllowed`/`ArchitecturesInstallIn64BitMode`=`arm64`, x64: `x64compatible`(현행 유지), `OutputBaseFilename=…-{#MyAppArch}`. Setup 스텁 자체는 기본(32비트 x86, ARM64에서 x86 에뮬레이션) 유지 — 64비트 설치 모드가 ARM64 네이티브 Program Files/HKLM 해석 | ISCC 6.7.3에서 양쪽 아키텍처 컴파일 성공(성공 메시지+파일명 확인) |
| release.yml | 단일 잡 → 3-잡: validate(Release 빌드+전체 테스트·재시도, x64) → build(매트릭스 win-x64/win-arm64, 각자 인스톨러+ZIP+체크섬 업로드) → release(ubuntu, 아티팩트 병합·SHA256SUMS 통합·릴리스 게시 5파일[x64/arm64 인스톨러·ZIP+체크섬]) | YAML 재구성(푸시 후 CI 1회 검증 대상) |
| ci.yml | `test-arm64` 잡 신설(windows-11-arm 무료 호스티드 러너 — public 저장소 대상, Release 빌드+전체 테스트, 릴리스와 동일 2회 재시도 계약) + `package-dry-run` 매트릭스화(win-x64/win-arm64 — ARM64 크로스 퍼블리시를 매 푸시에 검증, 아티팩트 명명 `dawnplayer-ci-build-{runtime}`) | 동일 |
| 로컬 검증 | 클린 win-arm64 publish — DawnPlayer.App.exe·coreclr.dll·hostfxr.dll·e_sqlite3.dll·Microsoft.WindowsAppRuntime.Bootstrap.dll·Microsoft.ui.xaml.dll 전부 PE Machine=ARM64 확인, **0경고 0오류**. 클린 win-x64 전체 패키징 회귀 — **0경고 0오류**, `-x64.exe` 명명 유지. SHA256SUMS LF·BOM 없음 확인 | 2026-10-04 실측 |
| 산출물(로컬 시험) | win-arm64: 포터블 ZIP 104.4MB + 인스톨러 67.9MB / win-x64: ZIP 108.0MB + 인스톨러 71.3MB | dist/ |
| 전체 스위트 | Release 전체 실행 **2,199/2,199 통과**(0경고 0오류) — C# 소스 무변경이므로 신규 테스트 없음 | 2026-10-04 |
| 미확인 | **ARM64 실기기 실행·청음**(로컬 x64 호스트로 불가) — windows-11-arm CI 러너 테스트가 빌드·로직을 대체 검증하고, WASAPI 출력·DSD는 ARM64 실기기에서 최종 확인 필요 | — |

### 하단바 평점 설정 다국어 누락 → x:Uid 베어 키 + MRT 점 조회 결함 전수 수리 (2026-10-04, 사용자 보고 — 커밋 대기)

- 보고: 설정 "레이아웃 & 디스플레이"의 "하단바 평점 버튼" 행(토글·설명)이 영어·일본어 UI에서도
  한국어로 표시.
- 원인 1(x:Uid 베어 키): `Settings_Layout_Rating_Title/_Desc`가 resw에 속성 접미사 없는 베어 키로
  등록됨 — x:Uid 파이프라인은 `<uid>.<속성>` 항목만 요소에 적용하므로 XAML 하드코딩 한국어가 세
  언어 모두에서 그대로 노출. 전수 스캔으로 동일 결함 총 51건 확인: 수면 타이머 메뉴 6·DLNA 재생
  준비 문구 1·평점 설정 행 2·`*_A11yName` 42(후자는 스크린리더 자동명이 아예 미적용 상태).
- 원인 2(MRT 점 조회): `AppStrings.Get("....Text")` 형태의 기존 호출 11곳(라이브러리 헤더 5·
  플레이리스트 평점 메뉴·빈 트랙 문구·L15 평점 플라이아웃 별 자동명·타이틀바 텍스트·설정 톱니
  툴팁·창 제목)이 `ResourceLoader.GetString`에 점 키를 그대로 넘기는데, MRT는 네스티드 키를
  슬래시로만 조회한다. **DawnPlayer.App.pri 프로브 실험으로 확정**: 점 형태는 NamedResourceNotFound
  예외 → 서비스가 null 반환 → 하드코딩 폴백 (windows-app-sdk localize-strings 문서 "replace dots
  with forward slash"). 즉 11곳도 전부 조용히 폴백으로 동작하고 있었음.
- 수리: ① resw 3개 국어 51건 속성 접미사화(`.Text`/`.AutomationProperties.Name`). 코드 조회와 공유
  돼 베어 키를 유지하던 3건은 베어+`.Text` 병기 시도가 PRI175("entity defined as both resource and
  scope") 빌드 실패를 유발 — 전면 `.Text`화로 해소(빌드 실패가 원인 2의 실증이 되기도 함).
  ② `MrtLocalizationService.GetExact`에 점→슬래시 번역 1곳 추가 — 호출부는 resw 표기 그대로 인용,
  기존 11곳은 호출부 무수정으로 자동 수리. ③ 수면 타이머 조회 4곳을 `.Text` 표기로 전환(플라이아웃
  카운트다운 헤더까지 단일 출처화). ④ `Xaml_XUid_Values_HaveApplicablePropertyEntries` 게이트 신설 —
  접미사 없는 uid 재유입을 빌드 때 적색화(기존 베이스명 게이트는 베어 키를 히트로 보고 못 잡았음).
- 게이트: 클린 리빌드 0경고 0오류, LocalizationTests 15/15, 전체 스위트 **2,200/2,200**(신규 게이트
  1건 반영), 프로브 재검증 — 수면 타이머 6건 + 기존 11건이 슬래시 형태로 en-US 값 전부 조회 성공.
- 실행 검증: settings.json Language=EnUS로 앱 기동(CUA) — 설정 "Layout & Sizing"에 **"Now Playing
  bar rating button"/"Show the star-rating button…"** 영문 노출 확인, 메뉴 Sleep Timer 하위
  "Off/15 min/30 min/1 hour/After current track" 전부 영문 확인, 타이틀바 "No sound — Nothing
  played"·슬라이더 자동명 "Default Album Cover Size" 적용 확인. 검증 후 settings.json 원복.

### 하단바 seek·볼륨 드래그 빈 툴팁 수리 (2026-10-05, 사용자 보고·스크린샷 → 승인·구현 — 커밋 대기)

- 보고: 하단바 seekbar 드래그 시 타임스탬프가 떠야 하는데 **비어서 보인다**(스크린샷 — 엄지 위 빈
  둥근 박스).
- 원인 확정: 그 박스는 **WinUI 3 Slider 내장 드래그 툴팁**(`IsThumbToolTipEnabled`, 기본 true —
  ColorPicker·MediaTransportControls가 전부 명시적으로 끄는 그것). 콘텐츠는
  `ThumbToolTipValueConverter`가 만드는데 앱이 변환기를 지정하지 않아 빈 문자열 → 빈 박스. 앱의
  커스텀 템플릿(EoleSlimSliderStyle)은 무관 — 내장 툴팁은 템플릿 파트가 아니라 플랫폼 코드가
  생성(generic.xaml에 ThumbToolTip 파트 부재 확인). 앱의 호버 미리보기 툴팁(ToolTipService,
  포인터 위치 시간)은 별개 파이프라인이라 드래그 중엔 뜨지 않음. 볼륨 슬라이더도 동일 결함.
- 수리(사용자 승인: 시크+볼륨 모두): ① 순수 계약 `SliderThumbToolTipText`(App/Controls 신설 —
  `Time(double 초)` → SeekbarScrubbingCalculator.FormatTime 위임 m:ss/h:mm:ss, `Percent(double)`
  → "42%", NaN·∞·범위 백은 0:00/0%로 클램프). ② 얇은 셸 변환기 2종(Converters.cs 기존 관례 —
  `SeekSecondsThumbToolTipConverter`·`VolumePercentThumbToolTipConverter`, ConvertBack은 툴팁이
  읽기 전용이라 0d). ③ NowPlayingBar.xaml에 `xmlns:services` + UserControl.Resources 2종 등록 +
  양 슬라이더에 `ThumbToolTipValueConverter` 지정. ④ 게이트 `SliderThumbToolTipGateTests` 11종 —
  포맷 단위 테스트(0:00/1:15/1:01:15/NaN 클램프/퍼센트 반올림) + 소스 스캔(양 슬라이더가 변환기
  지정 없이 재유입 금지, 셸이 순수 계약 위임 유지).
- 엣지 기록: 트랙 미로드 시 Maximum=100이라 드래그 툴팁이 0:00~1:40 표시(재생 중엔 Maximum=곡
  길이로 정확). 세션 복원 경로는 저장 위치로 Max를 되돌리므로 통상 무해 — 필요 시 트랙 존재 시에만
  툴팁 활성화로 강화 가능.
- 게이트: 클린 리빌드 0경고 0오류, **전체 스위트 2,211/2,211**(신규 11종 반영), 런타임 스모크(앱
  기동 — Resources 파스·양 슬라이더 렌더 확인; 스위트 실행 중 기동한 1차 시도 소실은 E2E 하니스의
  프로세스 정리와 충돌한 것으로 재기동에서 무해 판명). **드래그 중 툴팁 내용은 CUA가 마우스 홀드를
  못 해 자동 캡처 불가(L15 한계와 동일) — 사용자 육안 확인 요청**.

### v1.6.0 발행 (2026-10-05, 사용자 지시 "1.5.0 내리고 다시 릴리즈" — CI 경로)

- 미커밋 전체를 2커밋으로 분할(215f560 ARM64 지원 / aef3b76 다국어 수리) → main 푸시 → v1.5.0
  릴리스·태그 삭제(`gh release delete --cleanup-tag`) → v1.6.0 태그(마이너 bump — ARM64는 사용자
  가시 기능, AGENTS.md §2 관례) → CI 발행. 최초 실행 37184200611 **전 잡 success였는데 3자산만
  발행**(인스톨러 2개 누락 — 조용한 부분 실패).
- 원인: ARM64 3-잡 재구성에서 빌드 잡이 `dist/installer/*.exe`를 하위 폴더째 아티팩트로 올리는데
  release 잡 스테이징이 `Get-ChildItem -File`(재귀 없음)이라 `installer/`를 누락, softprops 액션은
  매칭 없는 글롭을 조용히 건너뛰어 "성공" — **존재하지 않는 자산에 대한 릴리스 게이트가 없었다**.
- 수리(3d2a383): 스테이징 재귀 복사 + **불변식 게이트**(5자산 전부 dist에 존재해야 발행, 누락 시
  잡 실패+파일 목록 출력) + `fail_on_unmatched_files: true`. 태그 삭제→재생성→재푸시 재발행(2차
  37280584235 성공) → **5자산 완발행**(Setup x64 74.8MB·arm64 71.2MB, 포터블 x64 113.3MB·arm64
  109.6MB, SHA256SUMS 4라인 — 인스톨러 해시 포함 확인).
- 교훈: CI "녹색"은 발행된 자산 수를 보증하지 않는다 — 부분 발행은 자산 개수 불변식으로만 잡는다.
  upload-artifact의 상대 경로 보존을 전제로 한 다운로드 측 복사는 반드시 -Recurse로.
