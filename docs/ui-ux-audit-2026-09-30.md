# Dawn Player UI/UX 감사 보고서 (2026-09-30)

> **✅ 시정 상태 (2026-10-02 최종 — 91/91 전건 시정)**: 사용자 승인 배치 1–6(36건) + Wave 2(25건) +
> Wave 3(2건) + Wave 4(잔여 P2/P3 일괄)로 88건을 시정하고(2026-10-01), 의사결정 보류로 남았던
> 마지막 3건 — **PT3-11**(PlaybackState 확장 + 소스 배지·버퍼링 피드백), **PT2-10**(x:Phase 지연
> 바인딩 — ListView 템플릿 2곳; ItemsRepeater 앨범 그리드는 콜백 부재로 구조적으로 제외, 근거 주석),
> **PT5-13**(슬라이더 트랙 3:1 수렴 — 모든 호스팅 표면 기준)까지 2026-10-02 구현해 **총 91건 전건
> 시정 완료**. 시정 전체에 5차례 코드 리뷰 패스를 돌려 발견 20건을 추가 수선했다.
> 검증: 클린 리빌드 0경고 0오류, 전체 테스트 2,170/2,170. 내역은 `implementation_plan.md` §0.18.

> **방법론**: ui-ux-pro-max 스킬(2.13.0)의 규칙 체계(우선순위 1–10, quick-reference 119개 규칙, pro-rules 사전 인도 체크리스트)와
> `design-system/dawn-player/MASTER.md`의 "WinUI Translation Rules"(2026-09-20)를 판정 기준으로 사용.
> 서브 에이전트 5개가 파트별 독립 감사(읽기 전용, 코드 변경 없음)를 수행하고 본 문서에 종합했다.
>
> **심각도 정의**: P0=치명적(사용 불가·접근성 위반 확정) / P1=주요 사용성 저해 / P2=경미한 마찰 / P3=폴리시
>
> **요약**: 총 91건 — P0 2건, P1 17건, P2 40건, P3 32건. 강점: 테마 아키텍처·토큰 게이트·reduced-motion 구조화·
> x:Uid 접근명 인프라. 최대 약점: 대비(라이트 액센트·TextTertiary), 우클릭-선택 미갱신의 라이브러리 확산,
> 키보드 재생 경로 부재, 네트워크 섹션 3곳의 회귀(c8a13eb), 무음 데이터 손실.

---

## 파트 1: 앱 셸 · 내비게이션 · 테마/디자인 토큰 (15건, P0 2)

| ID | 심각도 | 위치 | 문제 | 수정 제안 |
|---|---|---|---|---|
| PT1-01 | **P0** | DawnTheme.xaml:609, :203, :516-517 + ThemeService.cs:280 | 라이트 테마 앰버 액센트(#C77F1B)가 텍스트 전경(SegmentedTab 체크, ToggleButton, AccentButton)으로 쓰여 대비 ~2.6:1 — 4.5:1/3:1 미달. PlayGreen 프리셋은 통과(프리셋 간 기준 불일치). DesignTokenTests.cs:212-214가 이 상태를 문서화만 함 | 라이트 액센트 상향 또는 액센트-텍스트 전용 변신 토큰, 프리셋별 최소 대비 게이트 |
| PT1-02 | **P0** | MainWindow.xaml:73-75 + DawnTheme.xaml:237, :252-256 | TextTertiaryBrush(다크 #787888/라이트 #868694)가 기능성 소형 텍스트(타이틀바 곡명 11.5px, 트랙 번호, 열 헤더)에 사용 — 다크 ~3.8:1, 라이트 ~3.1:1. 게이트는 TextPrimary/Secondary만 커버 | TextSecondary로 승격 또는 TextTertiary 조정 + 게이트 추가 |
| PT1-03 | P1 | MainWindow.xaml.cs:668-676, :394-400 | 설정 진입 시 탭·톱니 모두 비활성 표시 — 현재 위치 표시 부재(nav-state-active 위반), 복귀 경로는 탭 클릭뿐 | 톱니 checked 시각 상태 또는 설정 세그먼트 표시 |
| PT1-04 | P1 | ThemeService.cs:133-145, :56-65 | 고대비 판정이 세션 시작 1회(Lazy) — HighContrastChanged 미구독. HC 토글 시 커스텀 팔레트 덧칠/생략 고착. HC 위임 주석과 달리 Eole 커스텀 브러시는 시스템 HC 치환 대상 아님 | HighContrastChanged 재평가 + HC 테마 사전 매핑 |
| PT1-05 | P2 | DawnTheme.xaml:67-71, :167-171 + ThemeService.cs:306-343 | ListViewItemBackgroundSelected* 3종이 앰버 고정 — 액센트 프리셋 변경 시 목록 선택 하이라이트만 앰버 잔존 | SetAccentBrushes에 동시 갱신 추가 |
| PT1-06 | P2 | WindowPlacementHelper.cs:42-45 | 저장 창 좌표를 모니터 가시성 검증 없이 복원 — 모니터 분리 시 화면 밖 복원("앱이 안 뜸") | 복원 전 모니터 교차 검사·클램프 |
| PT1-07 | P2 | DawnTheme.xaml 다수 + MainWindow.xaml | 타이포 토큰 계약(11/12/13/14/17/22)이 셸·테마에서 무소비 — 11.5/12.5 등 원시 값 하드코딩. 토큰 소비처는 FullscreenNowPlayingWindow뿐 | 토큰 참조로 전환 |
| PT1-08 | P2 | DawnTheme.xaml vs ThemeService.cs:420-440 | 다크 팔레트 3중 중복 선언(사전/폴백/ApplyStandardDarkPalette) — 게이트 미커버, 드리프트 리스크 | 단일 소스화 + 3자 일치 게이트 |
| PT1-09 | P2 | SplitterResizer.cs:74-97 | 스플리터가 포인터 드래그 전용 — 키보드 대체 부재(WCAG 2.5.7), 히트 영역 8-10px | Tab+방향키 리사이즈, 더블탭 기본폭 |
| PT1-10 | P3 | DawnTheme.xaml:276-281 | 배지 텍스트 10px — 토큰 최소(FontCaption 11) 미달 | 11px 상향 |
| PT1-11 | P3 | DawnTheme.xaml:450-481 | 슬라이더 템플릿이 테마 가변 브러시를 StaticResource 참조 — 인스턴스 동일성 의존 취약 패턴 | ThemeResource 전환 |
| PT1-12 | P3 | MainWindow.xaml:13 vs :26 | 타이틀바 행 42 vs 바 40 — 2px 어긋남 | 일치 |
| PT1-13 | P3 | DawnTheme.xaml:517 vs DesignTokenTests.cs:227 | AccentButton 전경 #000000 vs 게이트 계약색 #141414 이중 기준 | #141414 통일·토큰화 |
| PT1-14 | P3 | DesignTokens.xaml:24-38 vs MainWindow.xaml:139-142 | Status 시맨틱 브러시 정의만 되고 소비처 전무 — InfoBar는 WinUI 기본 시각 | InfoBar에 Status* 매핑 |
| PT1-15 | P3 | MainWindow.xaml.cs:461-483 | 미니 모드 해제 시 최대화 상태 미복원 | Maximized 저장·복원 |

**강점**: in-place 브러시 갱신으로 WinUI 리소스 교체 함정 해결 / DesignTokenTests 게이트 문화(키 집합·스케일·WCAG·hex 베이스라인) / NavigationStateCalculator 순수 함수화·탭 영속화 / Segoe Fluent 단일 아이콘 패밀리·이모지 전무 / 레이아웃 시프트 없는 press 상태.
**총평**: 테마·토큰 인프라는 상급이나 "토큰이 게이트를 통과"와 "실제 조합이 통과" 사이 간격이 존재. 라이트 액센트-텍스트(PT1-01)와 TextTertiary(PT1-02)가 P0.
**Top 3**: PT1-01+02 대비 수렴·게이트화 → PT1-04 HC 동적 재평가 → PT1-05+06.

---

## 파트 2: 라이브러리 · 재생목록 뷰 (18건, P1 4)

| ID | 심각도 | 위치 | 문제 | 수정 제안 |
|---|---|---|---|---|
| PT2-01 | **P1** | LibraryPage.xaml:604, :645-691 / .cs:1100-1104 | 트랙 표에 우클릭-선택 갱신 핸들러 없음(RightTapped/ContextRequested 0건) — 행 A 선택 중 F 우클릭 시 메뉴가 A에 작동, 선택 없으면 첫 트랙에 오작동 | PlaylistPage의 RefreshSelectionForContextClick 패턴 이식 |
| PT2-02 | **P1** | LibraryPage.xaml.cs:972-984, :1026-1033 | 앨범 카드 메뉴가 MenuFlyoutItem.DataContext 의존 — 팝업 트리에서 DataContext 미전달(프로젝트 자체 실측 기록과 충돌)로 드로어 열려 있으면 다른 앨범, 없으면 무음 no-op | flyout.Target + Opening 시 타깃 확정 |
| PT2-03 | **P1** | LibraryPage.xaml.cs:908-935 | 드로어 트랙 행 메뉴도 동일 — 폴백이 "재생 중 트랙, 없으면 첫 트랙"이라 행 Y 우클릭 재생이 X/첫 트랙에 작동. 미연결 OnDrawerTrackRightTapped 잔존 | flyout.Target + ResolveItem 확정 |
| PT2-04 | P2 | LibraryPage.xaml.cs:652-660 | 트리 컨텍스트 메뉴가 우클릭 노드 미해석 — _visible(선택 노드 결과)에 작동 | Opening에서 flyout.Target→TreeViewNode.Content 확정 |
| PT2-05 | P2 | LibraryTreeBuilder.cs:20, :44-46 | 트리 확장 상태가 재빌드(스캔·태그 편집)마다 소실 — 선택 노드 조상만 재확장 | 확장 노드 키 스냅샷→재적용 |
| PT2-06 | P2 | PlaylistPage.xaml.cs:475-493, :360-367 | 재생목록 삭제/비우기 확인 없음(사이드바 Del 포함 즉시 삭제) | 삭제 확인 대화상자 |
| PT2-07 | P2 | LibraryPage.xaml.cs:426-430 | 라이브러리 빈 상태 없음 — "라이브러리 비어 있음"과 "검색 무히트"가 동일 메시지 혼재 | 중앙 EmptyState + 무히트 별도 메시지 |
| PT2-08 | P2 | LibraryPage.xaml.cs:369-431 | 필터/정렬이 UI 스레드 전체 동기 재계산, 로딩 어포던스 없음(주석이 비용 자인) | LoadingGate 적용·청크 비동기 |
| PT2-09 | P3 | DawnTheme.xaml:576-616, :303-340 | SegmentedTab/TransportToggle hover/checked 즉각 상태 변화(0ms) | 120ms 페이드 |
| PT2-10 | P3 | LibraryPage.xaml:616-644 등 | x:Phase 미사용 — 이미지·별점 1단계 실현 | x:Phase 1+ 지연 바인딩 |
| PT2-11 | P3 | LibraryPage.xaml:604 vs PlaylistPage.xaml:239 | SelectionMode Single vs Extended 불일치 — 평점 서브메뉴가 다중 선택 착시 유발 | 통일 또는 라벨 정리 |
| PT2-12 | P3 | PlaylistPage.xaml.cs:255-261 | 다중 선택 수가 UI 어디에도 표시 안 됨 | "n곡 선택됨" 표시 |
| PT2-13 | P3 | PlaylistPage.xaml.cs:721-745 | 표에서 Enter 재생 경로 없음(트리는 있음 — 일관성 결손) | Enter→재생 |
| PT2-14 | P3 | PlaylistDialogs.cs:160-171 | M3U8 내보내기 성공 시 무음 | 성공 토스트 |
| PT2-15 | P3 | TagEditorDialog.cs:240-245 | 숫자 필드 파싱 실패가 무음 무시·저장 성공으로 표시 | 인라인 검증 |
| PT2-16 | P3 | LibraryPage.xaml:333 등 | 하드코딩 #FFF0F0F0 + 반쪽 포인트 폰트 다수 | 토큰 치환 |
| PT2-17 | P3 | LibraryPage.xaml:221, :252, :404-419 | 26-28px 아이콘 버튼 히트 타깃 | 실효 타깃 확장 |
| PT2-18 | P3 | LibraryPage.xaml:195 | 상태줄 "Mixed selection • 0 tracks" 하드코딩 영어 | AppStrings화 |

**강점**: ItemsRepeater+StackLayout·x:Load·DecodePixelWidth 가상화 설계 / PlaylistPage의 우클릭-선택 갱신 모범 구현 / LoadingGate 300ms 임계 게이트 / 재정렬 다중 대안(Alt+↑↓·메뉴·선택 복원) / 트리 접근명·선택 복원·커버 줌 UX.
**총평**: PlaylistPage는 거의 모범적이나, 동일 플랫폼 결함(우클릭 미갱신) 방지가 PlaylistPage에만 적용되고 라이브러리 4개 면에 누락 — "우클릭 대상≠작동 대상" 신뢰성 결함이 라이브러리 전반에 잔존.
**Top 3**: PT2-01~04 일괄 이식 → PT2-06 삭제 확인 → PT2-05 확장 상태 보존.

---

## 파트 3: 재생 컨트롤 · Now Playing (18건, P1 4)

| ID | 심각도 | 위치 | 문제 | 수정 제안 |
|---|---|---|---|---|
| PT3-01 | **P1** | DawnTheme.xaml:323-356, :389-427, :446-490 | 커스텀 템플릿 3종(TransportToggle/PlayPause/SlimSlider)에 FocusStates·포커스 비주얼 부재 — 셔플·반복·A-B·가사 토글·재생·시크/볼륨이 Tab 순회해도 포커스 안 보임 | FocusStates 3상태 + 포커스 링 추가 |
| PT3-02 | **P1** | FullscreenNowPlayingWindow.xaml:11-55 / .cs:94-101 | 전체화면에 트랜스포트 전무, 유일 키 입력은 Esc — 닫기 안내도 없어 "갇힌" 경험 | 트랜스포트·오버레이 닫기·Space/←→ 지원 |
| PT3-03 | **P1** | FullscreenNowPlayingWindow.xaml.cs:232-255 | 파형 시크가 포인터 드래그 전용 — 키보드 대체 없음(WCAG 2.5.7) | 키보드 포커스 가능 슬라이더/방향키 |
| PT3-04 | **P1** | LyricsPane.xaml.cs:256-269 | 가사 자동 스크롤이 사용자 스크롤과 충돌 — 읽던 중 ChangeView가 즉시 끌어당김, 억제·복귀 수단 없음 | 사용자 스크롤 시 일시 억제 + "현재 줄로" 버튼 |
| PT3-05 | P2 | NowPlayingBar.xaml.cs:383-400 | 드래그 중 ElapsedText가 실제 재생 위치 표시 — 썸 위치와 라벨 어긋남("보고 값≠표시 값"). 호버 프리뷰 없음 | 드래그 중 슬라이더 값 기반 표시 + 호버 툴팁 |
| PT3-06 | P2 | DawnTheme.xaml:397-410, :328-341 | 호버/프레스 Duration="0" 즉시 스냅 | MotionDurationFast 120ms 적용 |
| PT3-07 | P2 | NowPlayingBar.xaml.cs:470-492 | A-B 거부 피드백이 1.4초 라벨 3글자 변경뿐 — 사유는 툴팁(호버 전용), SR 알림 없음 | TeachingTip/InfoBar + LiveSetting |
| PT3-08 | P2 | NowPlayingBar.xaml.cs:461-468 | A-B 해제가 우클릭 전용 제스처 — 어포던스 없음 | X 아이콘/라벨 표기 |
| PT3-09 | P2 | NowPlayingBar.xaml / LyricsPane.xaml | 토큰 밖 폰트 다수(9px 배지, 10.5, 13.5 등) | FontCaption(11) 등 정규화 |
| PT3-10 | P2 | FullscreenNowPlayingWindow.xaml:22-53 | 전체화면에 셔플/반복/A-B 상태 표시 전무, A-B 밴드 오버레이 없음 | Calculator 좌표 재사용 렌더링 |
| PT3-11 | P2 | NowPlayingBar.xaml.cs:134-147 | 소스 유지 표시 암묵적(라디오 부제·"YT" 3글자뿐), Buffering 상태 없어 버퍼링 피드백 불가 | 소스 배지 + PlaybackState 확장 검토 |
| PT3-12 | P2 | FullscreenNowPlayingWindow.xaml.cs:159-179 | 전체화면 커버 로드가 UI 스레드 디스크 I/O — 트랙 전환 스태터 가능 | Task.Run + TryEnqueue(바의 패턴 복제) |
| PT3-13 | P2 | FullscreenNowPlayingWindow.xaml.cs:239-242 | 파형 호버 무반응·커서 변경 없음 — 드래그 어포던스 부재 | 가이드라인+시간 툴팁+커서 |
| PT3-14 | P3 | LyricsPane.xaml:36-47, NowPlayingBar.xaml:269-273 | 26-30px 히트 타깃(대기열 제거 26px는 오터치 위험) | 32px+ 확장 |
| PT3-15 | P3 | NowPlayingBar.xaml:17 vs .cs:301 | 컴팩트 전환 기준 이중화(640 vs 730) | 단일 기준 통일 |
| PT3-16 | P3 | NowPlayingBar.xaml.cs:663-664 | 대기열 "비우기" 확인·되돌리기 없음 | 확인 또는 스냅샷 복원 |
| PT3-17 | P3 | FullscreenNowPlayingWindow.xaml.cs:124 | 커버 없는 트랙에서 완전 검은 화면 | 플레이스홀더 글리프 |
| PT3-18 | P3 | NowPlayingBar.xaml:188, :233 | 하드코딩 #141414/Black | 시맨틱 토큰 승격 |

**강점**: AbRepeatOverlayCalculator 불변식 분리 / 배터리 배려형 타이머(일시정지 정지·조건부 폴링·비활성 펌프 정지) / reduced-motion 구조적 강제(MotionHelper 전 진입점·스펙트럼 truth·AND 게이트) / 접근명 상태 추적(재생/음소거 이름 갱신) / 델타 패치 대기열·정직한 빈 상태.
**총평**: 로직 계층과 생애주기 관리는 모범적이나 상호작용 완결성 격차 큼 — 포커스 비주얼 상실, 전체화면의 갇힘, 가사 스크롤 충돌, 시크바 라벨 불일치(§3 불변식 위반 유형).
**Top 3**: PT3-01 포커스 상태 → PT3-05 시크바 라벨 → PT3-02/03 전체화면 완결성.

---

## 파트 4: 네트워크 · 설정 · 대화상자/폼 UX (20건, P1 4)

| ID | 심각도 | 위치 | 문제 | 수정 제안 |
|---|---|---|---|---|
| PT4-01 | **P1** | RadioSection.xaml.cs:141-142 등 | 라디오 컨텍스트 메뉴 3개 항목 전부 무반응 — StationFromSender가 `DataContext as RadioStation`인데 항목은 RadioRow라 캐스트 항상 실패. 편집/삭제는 이 메뉴가 유일 진입점(사실상 접근 불가). YouTube는 동일 패턴 올바르게 구현 — c8a13eb 회귀 | `as RadioRow → ?.Station` 수정 |
| PT4-02 | **P1** | DlnaSection.xaml:19-23, :34 | DLNA 탐색 실패 재시도 버튼이 Grid.Column="3"인데 ColumnDefinition 3개뿐 — 팬텀 컬럼에 배치되어 렌더링 안 됨(코드는 Visible로 시도). 회복 경로 부재 | 4번째 열 추가 |
| PT4-03 | **P1** | YouTubeSection.xaml.cs:196-234 | 실패 재시도 버튼이 finally SetBusy(false)가 무조건 Collapsed — 실패 안내만 남고 재시도 불가. 성공 후에도 "해석 중…" 행 잔존(보고 상태≠실제 상태) | SetBusy에 실패 상태 인자 분리 |
| PT4-04 | **P1** | LyricsEditorWindow.xaml.cs:562-565, :192-210 | 가사 편집기에 미저장 변경 확인 없음(dirty 추적·Closing 핸들러 부재) — 싱크 작업 실수로 닫으면 전체 소실 | dirty 플래그 + Closing 확인 대화상자 |
| PT4-05 | P2 | RadioSection.xaml.cs:110-116 | 방송국 삭제 확인·실행취소 없음(YouTube 최근 항목도 동일) | 확인 대화상자 |
| PT4-06 | P2 | SettingsPage.xaml:1477-1482 | Last.fm 키/시크릿 placeholder-only 라벨 + 평문 노출 | Header + PasswordBox 전환 |
| PT4-07 | P2 | AppearanceSettingsViewModel.cs:292-314 | 커스텀 액센트 무효 HEX 무음 무시 — 오류 표시 없음 | helper 텍스트 + 무효 시각 상태 |
| PT4-08 | P2 | LyricsSettingsViewModel.cs:254-269 | 무효 LRC 패턴 무음 필터링·저장 실패 안내 없음 | 필터 결과 요약 안내 |
| PT4-09 | P2 | LibrarySettingsViewModel.cs:76-114 | 감시 폴더 중복 추가 무음, 제거 확인 없음 | InfoBar 안내 + 확인 |
| PT4-10 | P2 | SettingsPage.xaml.cs:440-471 | 스캔/분석 버튼 실행 중 미비활성·즉각 피드백 없음, 전체 재분석(태그 덮쓰기) 확인 없음 | 버튼 잠금+ProgressRing+확인 |
| PT4-11 | P2 | LyricsSearchWindow.xaml.cs:316-327 | 덮어쓰기 확인 기본 버튼이 "덮어쓰기"(Primary) — 다른 파괴 작업 3종은 Close 기본(불일치) | DefaultButton=Close |
| PT4-12 | P2 | 3개 섹션 비교 | 실패 피드백 패턴 불일치(YouTube 섹션 내 상태행 / DLNA 전역 InfoBar / 라디오 혼용) | 섹션 내 상태행+재시도 표준화 |
| PT4-13 | P2 | DlnaSection.xaml:108-112 | DLNA 빈 상태에 행동 연결·원인 힌트 없음(라디오는 안내 존재) | 새로고침 액션+진단 힌트 |
| PT4-14 | P2 | SettingsPage.xaml 전체 | 설정 검색 부재(11개 카테고리·수백 항목) | 사이드바 검색+점프 |
| PT4-15 | P3 | LyricsEditorWindow.xaml:213 | `Content="💾 LRC 파일로 저장"` 이모지 아이콘(금지 항목) | FontIcon 교체 |
| PT4-16 | P3 | SettingsPage.xaml:459 등 | EQ 캔버스 #141414 등 하드코딩 — 라이트에서도 무조건 검은 박스 | 시각화 전용 토큰 |
| PT4-17 | P3 | RadioSection.xaml.cs:189-220 | 방송국 대화상자: 이름 필수 표시 없음, 오류가 필드 아래 아님, blur 사전 검증 없음(인라인 검증 자체는 모범) | required 표시+오류 위치+blur |
| PT4-18 | P3 | ListeningReportDialog.cs:28-38 | 기간 변경 로딩 없음, 실패 시 이전 기간 리포트와 선택 불일치 | ProgressRing+복원 |
| PT4-19 | P3 | NotificationPresenter.cs:41-48 | 뒤 알림이 미확인 Warning/Error 대체 가능 | 하위 심각도 억제·큐잉 |
| PT4-20 | P3 | LyricsSearchWindow.xaml:49-51 | StatusText가 ListView와 같은 셀 공유(가려짐), Enter 검색 불가 | 행 분리+Enter 바인딩 |

**강점**: YouTube 의존성 게이트 상태 커뮤니케이션(오판 플래시 방지·사전 프로브) / DLNA 세대 가드·Interlocked·CTS 비동기 경합 방어 / 라디오 URL 인라인 검증·파괴 작업 안전 기본 버튼 / 설정 IA 사이드바+즉시 적용 클램프 / 알림 정책 명시성.
**총평**: 설계 의도는 높으나 c8a13eb "Network UX overhaul"에서 회귀 3건 — 라디오 메뉴 캐스트 불일치, DLNA 재시도 팬텀 컬럼, YouTube SetBusy finally 순서. "실패에서 회복하기" 상호작용 3곳이 동시 침묵.
**Top 3**: PT4-01 한 줄 수정 → PT4-02+03 재시도 경로 복구 → PT4-04 편집기 dirty 확인.

---

## 파트 5: 횡단 — 접근성·키보드·상호작용 일관성 (20건, P1 7)

| ID | 심각도 | 위치 | 문제 | 수정 제안 |
|---|---|---|---|---|
| PT5-01 | **P1** | LibraryPage.xaml:441-442, :502-503 | 드로어 트랙 행 Button에 Click 미연결(DoubleTapped만) — 키보드 Space/Enter 무반응 | Click 연결 |
| PT5-02 | **P1** | LibraryPage.xaml:605, :748 / PlaylistPage.xaml:240 / RadioSection.xaml:46 / DlnaSection.xaml:70 | 트랙 리스트 5곳 모두 더블클릭으로만 재생 — Enter 재생은 TreeView뿐 | KeyDown Enter→재생 |
| PT5-03 | **P1** | SettingsPage.xaml 13곳 이상 | 라벨 없는 인터랙션 컨트롤 약 57개(x:Uid·Header·자동명 전무) — EQ 밴드 템플릿은 밴드당 7컨트롤 전부 무명 | Header/자동명 부여 |
| PT5-04 | **P1** | DawnTheme.xaml:24, :124 + SettingsPage.xaml:17-21 | TextTertiary 대비 다크 3.41-4.08:1, 라이트 2.82-3.32:1 — 설정 설명문 등 소형 텍스트 광범위 사용(파트1 PT1-02와 교차 확인) | 팔레트 조정+게이트 |
| PT5-05 | **P1** | DawnTheme.xaml:127 + :609 + PlaylistPage/SettingsPage | 라이트 액센트-텍스트 2.69-3.00:1(파트1 PT1-01과 교차 확인) — 토글 체크·큐 배지·가사 활성 라인 | 액센트-텍스트 변신 토큰 |
| PT5-06 | **P1** | MainWindow.xaml:118 + ko resw:57 | 설정 톱니 아이콘 전용 — resw는 ToolTip만 있고 UIA Name 공백(ToolTip은 UIA Name으로 승격 안 됨) | 자동명 키 3개 국어 추가 |
| PT5-07 | **P1** | SplitterResizer.cs + 스플리터 5곳 | 패널 폭 조절이 드래그 전용(파트1 PT1-09와 교차 확인) | 포커스 가능+방향키 |
| PT5-08 | P2 | MainWindow.xaml:73-75 | 곡 변경·재생 상태 변경의 SR 알림 없음 — LiveSetting은 3곳뿐(핵심 재생 상태 제외) | TitleBarTrack에 LiveSetting |
| PT5-09 | P2 | NowPlayingBar.xaml:177-207 + ShortcutCommandCatalog.cs | 툴팁에 단축키 조합 미표기(17개 명령), 가속기는 코드 부착이라 자동 표기도 없음 — Settings 페이지에서만 확인 가능 | 툴팁에 "(Ctrl+H)" 합성 |
| PT5-10 | P2 | YouTubeSection.xaml:118-120 vs LibraryPage vs DlnaSection | "재생" 제스처 불일치(단일 클릭/더블클릭/Enter 혼재) | 전역 규칙 문서화·통일 |
| PT5-11 | P2 | LibraryPage.xaml:307 vs PlaylistPage.xaml:416-420 | 동일 글리프(E8FD/E710)가 페이지마다 다른 명령 — 아이콘 의미 충돌 | 글리프-명령 매핑표 정착 |
| PT5-12 | P2 | DawnTheme.xaml:287-289 등 다수 | 히트 타깃 군집 미달 — 최소 24², 슬라이더 20px, RatingCell MinHeight=0 | 실효 32²+ 확보 |
| PT5-13 | P2 | DawnTheme.xaml:461-465 | 슬라이더 미재생 구간 대비 1.15:1 — 트랙 자체가 거의 안 보임 | SliderTrackFill 토큰 적용 |
| PT5-14 | P2 | LibraryPage.xaml:200-201 | 검색 상자 placeholder-only 라벨+UIA명 부재 | 자동명 추가 |
| PT5-15 | P2 | NowPlayingBar.xaml:135-151 | TrackRatingButton 무평점 시 UIA명 공백 — 같은 컨트롤이 Library/Playlist에선 바인딩 이름 사용(3곳 중 1곳 빈약) | RatingAccess 바인딩 적용 |
| PT5-16 | P2 | LyricsEditorWindow.xaml:213 | 이모지 아이콘 💾(3개 국어 resw 동일) — 파트4 PT4-15와 교차 확인 | FontIcon 교체 |
| PT5-17 | P2 | FullscreenNowPlayingWindow.xaml.cs:94-102 | 풀스크린 Esc뿐 — Space/화살표 미처리, 메인 17개 가속기 미적용(파트3 PT3-02/03과 교차 확인) | 키 지원 추가 |
| PT5-18 | P2 | SettingsPage.xaml:459 등 5곳 | 하드코딩 hex 5곳 — EQ 캔버스 #141414는 라이트에서도 무조건 검은 박스 | 토큰화·문서화 |
| PT5-19 | P2 | AutomationNameScan.cs:16-18 | 자체 스캐너 빈틈 — "이름 자체 부재" 미검출, resw 교차검증·줄바꿈·코드비하인드 미커버 | 아이콘 전용 버튼 resw 키 교차검증 추가 |
| PT5-20 | P3 | LyricsEditorWindow.xaml / LyricsSearchWindow.xaml | 편집기·검색 창에 Esc 닫기 미연결(미니/풀스크린/드로어는 구현) | KeyDown Esc→Close |

**집계**: UIA명 없는 아이콘 컨트롤 3종 · Settings 무명 컨트롤 ~57개 · 대비 실패 조합 11개 · 키보드 재생 불가 리스트 5곳+무반응 Button 3곳 · XAML KeyboardAccelerator 0건(코드 17명령) · 이모지 아이콘 1건.
**강점**: x:Uid→resw 자동명 65키 3개 국어 완전 동기 / AutomationNameScan 게이트(덮어쓰기 결함 차단) / ContrastMath+DesignTokenTests가 실측 유효(상태색 5.6-9.8:1 통과) / KeyboardHelper 포커스 거부권 모델+ShortcutMap bijection 불변식 / Esc·컨텍스트 키 부분 와비.
**총평**: 접근성은 "인프라 상급, 마지막 10% 적용 편차" — 시스템이 커버 못 하는 영역(더블클릭 전용 재생, 무명 군집, 저대비, 툴팁 단축키 미표기)에서 CRITICAL 규칙이 반복 위반. 다음 스윕은 "이름의 부재·제스처 대안의 부재" 겨냥이 필요.
**Top 3**: PT5-01/02 Enter 재생 → PT5-04/05 대비·게이트 → PT5-03/06 라벨링(resw 키 추가만으로 가능한 저비용 고효익).

---

## 종합 (Synthesis)

### 교차 확인 결함 — 신뢰도 최상 (파트 간 독립 발견)

| 결함 | 발견 파트 |
|---|---|
| 라이트 액센트를 텍스트 전경으로 사용 → 2.6-3.0:1 | PT1-01 + PT5-05 |
| TextTertiary 소형 텍스트 저대비 | PT1-02 + PT5-04 |
| 스플리터 드래그 전용(키보드 대체 없음) | PT1-09 + PT5-07 |
| 전체화면 키보드·트랜스포트 부재 | PT3-02/03 + PT5-17 |
| 💾 이모지 아이콘 | PT4-15 + PT5-16 |

### 군집(theme)별 정리

1. **색 대비 (P0 2 + P1 2)** — "토큰이 게이트를 통과"≠"실제 조합이 통과". 라이트 액센트-텍스트, TextTertiary, 슬라이더 미재생 트랙. 한 번의 팔레트 조정 + DesignTokenTests 게이트 확장으로 일괄 해소 가능.
2. **우클릭/컨텍스트 메뉴 (P1 4 + P1 1)** — PlaylistPage는 방지 완료, LibraryPage 4개 면+라디오 섹션은 미방지. PlaylistPage 패턴(flyout.Target+Opening 확정)의 일괄 이식이 해법. PT4-01은 한 줄 캐스트 수정.
3. **키보드 접근성 (P1 6)** — Enter 재생 부재(리스트 5곳), 커스텀 템플릿 포커스 비주얼 부재, 스플리터·파형 드래그 전용. 핵심 워크플로(재생)가 키보드만으로 불가.
4. **네트워크 회귀 (P1 3)** — c8a13eb에서 발생. 라디오 메뉴 데드 버튼, DLNA 재시도 팬텀 컬럼, YouTube SetBusy finally. 모두 소규모 수정.
5. **무음 데이터 손실/무음 무시 (P1 1 + P2 다수)** — 가사 편집기 미저장 소실, 재생목록 삭제 확인 없음, 무효 입력 무음 무시(액센트 HEX, LRC 패턴, 태그 숫자).
6. **토큰 계약 이탈 (P3 다수)** — 폰트 반쪽 포인트(9~13.5px), 하드코딩 hex 5-10곳, 토큰 무소비(타이포·Status 브러시). 게이트 확장 대상.
7. **상태 보존/복원** — 트리 확장 소실, 창 화면 밖 복원, 미니 모드 최대화 미복원, 성공 후 잔존 상태 텍스트.

### 권장 수정 우선순위 (실행 배치)

| 배치 | 항목 | 특징 |
|---|---|---|
| **1 (P0·한 줄급)** | PT4-01 라디오 메뉴 캐스트 · PT4-02 DLNA 열 추가 · PT1-01+PT5-05 라이트 액센트 · PT1-02+PT5-04 TextTertiary | 최소 비용 최대 효과, 게이트 확장 동반 |
| **2 (P1 신뢰성)** | PT2-01~04 라이브러리 컨텍스트 메뉴 일괄 · PT4-03 SetBusy 분리 · PT3-05 시크바 라벨 | 우클릭·드래그의 "보고 값=실제 값" 회복 |
| **3 (P1 접근성)** | PT3-01 포커스 상태 · PT5-01/02 Enter 재생 · PT5-03/06/14/15 라벨링(resw 키) · PT1-04 HC 재평가 | 접근성 인프라가 이미 있어 적용만 남음 |
| **4 (P1 데이터)** | PT4-04 편집기 dirty 확인 · PT2-06 삭제 확인 · PT4-05/10/11 파괴 작업 확인 통일 | 무음 소실 차단 |
| **5 (P2 완결성)** | PT3-02/03/04 전체화면·가사 스크롤 · PT2-05 확장 보존 · PT1-06 창 복원 · PT4-12~14 피드백 표준화 | 사용성 마찰 해소 |
| **6 (P3 폴리시)** | 토큰 이탈 정규화 · PT4-15/PT5-16 이모지 · 상태 전환 120ms · 히트 타깃 | 게이트로 상시 관리 |

---

*본 감사는 읽기 전용으로 수행되었으며 어떤 소스 파일도 수정하지 않았다. 모든 수정은 사용자 승인 후 진행(AGENTS.md §1).*
