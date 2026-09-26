# N2 DLNA 클라이언트 — as-built 세부 구현서

> 작성일: 2026-09-26 · 기준 커밋: `19356a8` (feat: network sources — DLNA 전체 파일이 이 커밋에 신규 포함)
> 성격: N2 착수 전 설계였던 [implementation_plan.md](../implementation_plan.md) §1.2 N2 항목의 **as-built
> 정본**. 실측 코드 조사(2026-09-26)로 검증했으며, §7의 잔여 갭 구현 상세는 **승인 대기**(AGENTS.md §1)다.
> 근거 표기: `파일:행`은 이 커밋 기준. 코드 조사 후 수정 없음(§1 대조표의 검증 방법 참조).

---

## 0. 요약

N2(DLNA 클라이언트)는 **구현 완료·커밋됨**. 완료 기준 6건 중 4건 자동 검증 통과(단위 50종+통합 1종,
M3U8 왕복, 콜드 리빌드 0경고, README 3개 국어), **수동 상호운용 매트릭스는 실행 기록 없음**.
코드 조사에서 **구현 갭 3건**(복원 트랙 아트 상실, 동명 서버 구분 누락, DIDL 스킵 무로그)과
계획 대비 **문서화된 편차 4건**을 확인했다 — §7에 파일 수준 수정안을 명세했다.

## 1. 완료 기준 대조 (계획 §1.2 vs 실측)

| 완료 기준 (계획 337–339행) | 상태 | 근거 |
|---|---|---|
| fixture 단위테스트 전수 + 통합 1종 | ✅ | `tests/DawnPlayer.Tests/Network/Dlna/` 7개 클래스 — §5 테스트 지도. 실행 기록: 1,901/1,901 (2026-09-23, plan §0 N2 기록) |
| DLNA 트랙의 M3U8 저장·복원 왕복 (`#DPTRACK` kind=Dlna) | ✅ | `tests/DawnPlayer.Tests/Persistence/M3uDpTrackTests.cs:34` `RemoteTracks_RoundTripWithKindAndMetadata` — Dlna(SourceKind=2) 저장→로드→`RemoteTrackCodec.ToTrack`→SourceKind/메타 복원 직접 검증. ※ N2 실행 기록 표에 이 항목이 누락되어 있었으나 실제로는 충족됨 |
| 재생목록 편집 무결성 | ✅ | Dlna 트랙은 `Track{Path=http URL, SourceKind=Dlna}`로 일반 로컬 트랙과 동일 파이프라인 진입(`AddTracks`) — 전용 무결성 경로 없음(= 별도 결함 없음) |
| 콜드 리빌드 0경고 + 전체 스위트 1회 | ✅ | plan §0 N2 기록: obj/bin 삭제 콜드 리빌드 0경고 0오류, 1,901/1,901 (2026-09-23) |
| 상호운용 수동 매트릭스 (MinimServer·Windows 미디어 스트리밍) | ❌ 미실행 | 저장소 내 실행 기록 없음. §7 M1에 체크리스트 명세 |
| README 3개 국어 갱신 | ✅ | README.md:42·README.ko.md:38·README.zh-CN.md (커밋 `19356a8` stat 확인) |

## 2. 아키텍처 (as-built)

```
[SSDP M-SEARCH 4 ST, 3초 수집]            SsdpDiscovery (UdpClient, 239.255.255.250:1900)
        │ 데이터그램
        ▼
[SsdpResponseParser]  →  SsdpDeviceHit{Location, Usn, St, ServerHeader}   USN 중복 제거
        │ GET 장치 기술 XML (10초 타임아웃)
        ▼
[DlnaDeviceDescriptionParser] → DlnaServer{DescriptionUrl, FriendlyName, Udn, ControlUrl,
        │                              ServiceType}   ContentDirectory 없으면 null
        ▼
[ContentDirectoryClient.BrowseAsync]  ── SOAP text/xml POST, SOAPACTION="<광고된 ServiceType>#Browse"
        │ (HttpMessageHandler 주입 seam — 기본 페이지 500, 15초 타임아웃)
        ▼
[SoapBrowseMessage] (요청 골든 조립/응답 필드 추출, never throws)
        ▼
[DidlLiteParser] → DidlContainerEntry/DidlItemEntry(+DidlResource)   객체 단위 try/skip
        ▼
[DlnaTrackFactory.TryCreate] → Track{Path=res URL, SourceKind=Dlna, Codec, ...}
        │                              포맷 우선: FLAC(100)>WAV(90)>ALAC(80)>AAC(70)>MP3(60)>OGG(50)>LPCM(10)
        ▼
재생: AudioFileReaderFactory.Open(path, Dlna) ── 라우팅 불변식: Dlna/YouTube kind의 http URL은
        │      HttpFileTrackReaderProvider(Order 280) 고정, kind 없는 http는 라디오(Order 300)
        ▼
[HttpProgressiveTrackReader] 전체 다운로드 → spool 파일 → 기존 로컬 디코더 체인 (갭리스·DSP·WASAPI 유지)

아트: DlnaSection.PlayRowAsync가 AlbumArtUri를 DlnaArtCache.GetOrDownloadAsync로 즉석 다운로드
      → Track.ArtPath 심음(유일한 부착 지점). 캐시: URL SHA256 파일명, 64MB LRU, 이미지 시그니처 검증.
```

핵심 설계 결정(계획에서 승인된 것 포함): Rssdp 대신 자체 SSDP(~150줄, 검증성), MF-over-URL 배제와
풀-스풀(N1 스파이크 결정 재사용), Core 8종은 프로젝트 참조 의존성 없음(DlnaTrackFactory→Models.Track,
DlnaArtCache→AppPaths 제외), AppServices 등록 없음 — DlnaSection 코드비하인드가
`ContentDirectoryClient`/`DlnaArtCache`를 직접 소유(`DlnaSection.xaml.cs:76–77`).

## 3. 컴포넌트 명세

### 3.1 Core — `src/DawnPlayer.Core/Network/Dlna/`

| 컴포넌트 | 계약(핵심 시그니처) | 스레딩/오류 | 상수/비고 |
|---|---|---|---|
| `SsdpDiscovery.cs` | `static Task<IReadOnlyList<SsdpDeviceHit>> SearchAsync(TimeSpan timeout, IReadOnlyList<string>? searchTargets = null, CancellationToken = default)` | 전부 static·async. `CancellationTokenSource.CreateLinkedTokenSource`+`CancelAfter`로 외부 취소·타임아웃 결합. **절대 던지지 않음** — SocketException/전체 실패는 빈 목록(주석 "best-effort") | ST 4종(`MediaServerSearchTargets`), MX=3, 수신 버퍼 64KB, USN(없으면 LOCATION) 중복 제거. 소켓 seam 없음 → 파싱만 테스트(문서화된 결정) |
| `SsdpResponseParser.cs` | `static SsdpDeviceHit? TryParse(string datagram)` | 순수·null 반환. `HTTP/1.1 200` 응답만 수락(NOTIFY 폐기), LOCATION+USN 필수, CRLF/LF 혼용·헤더 대소문자 무관 | |
| `DlnaDeviceDescriptionParser.cs` | `static DlnaServer? TryParse(string xml, Uri descriptionUrl)` | 순수·null 반환(ContentDirectory 없으면 null = 렌더러 전용 장치 배제) | 로컬명 매칭, `Descendants()` 한 번 순회로 embedded device 커버, URLBase>상대 해석>절대. **계획 4필드에 `ServiceType` 추가**(SOAPACTION이 광고된 버전 사용 — 통합 테스트가 서버측 검증) |
| `ContentDirectoryClient.cs` | `Task<DlnaBrowsePage> BrowseAsync(DlnaServer, objectId="0", startingIndex=0, requestedCount=500, CancellationToken)` / `DlnaException` | **예외 던짐 전략**(전송·HTTP·SOAP fault·불량 본문 → `DlnaException`, 취소는 통과) | `HttpClient?` 주입 seam, 기본 타임아웃 15초. `DlnaBrowsePage(Entries, NumberReturned, TotalMatches)` |
| `SoapBrowseMessage.cs` | `static string BuildBrowseRequest(objectId, startingIndex, requestedCount)` / `static bool TryParseBrowseResponse(soapXml, out didl, out returned, out total)` | 순수. 요청은 **고전 문자열 조립**(XML 객체 모델 직렬화의 `xmlns=""` 리셋에 일부 서버가 질식 — 주석 기록), 응답 파서는 never throws | BrowseDirectChildren·Filter `*` 고정, ObjectID XML 이스케이프(주입 방지), Result는 1회만 언이스케이프 |
| `DidlLiteParser.cs` | `static IReadOnlyList<DidlEntry> Parse(string didlXml)` / `DidlDuration.TryParse` | 순수·never throws. 문서 실패→빈 리스트, **객체 단위 try/skip**(한 불량 row가 세션을 끌지 못함) | `DidlEntry`(abstract)→`DidlContainerEntry{ChildCount}`/`DidlItemEntry{Artist, Album, Genre, Duration, AlbumArtUri, Resources}`. 계획에 없던 `Genre` 추가. albumArtURI는 절대 URI만, item duration은 duration 보유 첫 res에서 채택. ※ 스킵 시 로그 없음(계획 대비 갭 G3) |
| `DlnaTrackFactory.cs` | `static Track? TryCreate(DidlItemEntry item, Uri baseUrl)` — 재생 불가 포맷만 null | 순수 | 포맷 테이블 Preference: FLAC 100>WAV 90>ALAC 80>AAC 70>MP3 60>OGG 50>LPCM 10(트랜스코드는 원본 부재 시만). 동점이면 SizeBytes 내림차순. 빈 제목→URL 폴백. `SourceKind=Dlna`. ※ `baseUrl`은 현재 미사용(상대 res URI 폐기 — 방어용 파라미터) |
| `DlnaArtCache.cs` | `Task<string?> GetOrDownloadAsync(Uri url, CancellationToken)` / `const long MaxTotalBytes = 64MB` | 인스턴스. 실패 전부 null(예외 없음, "art is always optional"). `_gate` 락 + 파일 재검사로 동일 URL 동시 호출 결과 공유 | `HttpClient`+`cacheDir` 주입 seam, 15초 타임아웃. 파일명 `dlna-`+SHA256(URL)+확장자 — `AppPaths.ArtCacheDir` 공유 시 로컬 아트와 예산 스캔 비간섭. JPEG/PNG/BMP 시그니처 검증, 예산 초과 시 LastWriteTimeUtc 오름차순 삭제 |

### 3.2 App — `src/DawnPlayer.App/Views/`

**NetworkPage** — RadioButton 2개(`EoleNavTabStyle`, GroupName 공유)로 라디오/DLNA 섹션 전환(상단 탭 수법).
셸 계산기(`NavigationStateCalculator`)는 손대지 않는 확장 구조. `ActivatePage()`는 라디오만 1회 활성화하고
**DLNA는 첫 탭 클릭 시 lazy 활성화**(`_dlnaActive` 가드 — 페이지 진입 즉시 SSDP를 돌리지 않기 위한 선택,
계획 308행 표기와 다름 §6-D2).

**DlnaSection** — 툴바(서버 ComboBox `DisplayMemberPath="FriendlyName"` + 새로고침 + BusyRing) /
브레드크럼(`ItemsControl`+`DlnaCrumb{Title, Index}`, x:Bind) / ListView(`DlnaRow` 스냅샷 배열 재할당) /
푸터("더 불러오기"). 상태 흐름:

- 검색: SSDP 3초 → 장치 XML GET(`DescriptionHttp`, 10초) → 파서 → **UDN 2차 중복 제거**(USN 제거와 별개) → ComboBox 교체·`SelectedIndex=0`, 0개면 빈 상태.
- 브라우징: `BrowseAsync(reset, startIndex)` — **세대 가드는 `_browseGeneration` 증가(요청 직전, UI 스레드)와 응답 직후 단일 검사 지점**으로 서버·폴더·크럼·페이지 경쟁을 모두 폐기. 페이지네이션: Core 기본 500, `_loaded < _totalMatches`일 때만 로드 MORE 노출. 비오디오 항목은 `DlnaTrackFactory.TryCreate==null`로 목록 제외(음악 브라우저 계약).
- 재생: `DlnaTrackFactory.TryCreate` → 아트 즉석 다운로드→`track.ArtPath` → `PlaylistManager.AddTracks` → `PlaybackUiHelper.PlayItemAsync`. 오류는 `AppServices.RaiseWarning` + 로그.
- resw 13종 × 3개 국어(자동화 이름 포함) — 목록은 조사 결과 기준 `Network_PageHeader`~`Network_Dlna_Unplayable`.

## 4. 통합 계약 (불변식 — 테스트로 고정된 것)

1. **라우팅 불변식**: `SourceKind==Dlna||YouTube`인 http(s) URL은 반드시 스풀링 리더(`HttpFileTrackReaderProvider`), **kind 없는 http URL은 종전대로 라디오** — `AudioFileReaderFactory.SelectProvider`(`AudioFileReaderFactory.cs:163`), `TrackReaderFactoryRoutingTests` 6종.
2. **M3U8 왕복**: `SourceKind != File` 트랙에 `#DPTRACK` 지시문, **ArtUrl 슬롯은 항상 null로 저장**(`M3u.cs:174`). 복원 시 로컬 경로 부착 지시문은 폐기, 미지 kind는 Radio 폴백, kind 값의 숫자 안정성 필수(`TrackSourceKind.cs` 주석) — `M3uDpTrackTests` 10종.
3. **세대 가드**: 이전 브라우즈 응답은 UI에 절대 반영 불가(단일 검사 지점 — 응답 직후).
4. **아트**: 다운로드 실패는 언제나 무음(null) — 아트는 재생을 막지 않는다.
5. **라이브 구분**: `RadioTrack.IsStreamUrl`이 아니면서 http인 Dlna 트랙만 스풀링 — 라디오 유입 경로와 충돌 없음.

## 5. 테스트 지도

| 클래스 | 종(Fact/Theory) | 고정하는 계약 |
|---|---|---|
| `SsdpResponseParserTests` | 4(7케이스) | 응답만 수락·필수 헤더·개행/대소문자 강건·불량 폐기 |
| `DlnaDeviceDescriptionParserTests` | 7 | controlURL 해석 규칙(URLBase/상대/절대), embedded device, 렌더러 전용→null, ns 강건 |
| `SoapBrowseMessageTests` | 4(3+케이스) | 요청 골든+ObjectID 이스케이프, 응답 필드 추출·1회 언이스케이프, Fault/결누→false |
| `DidlLiteParserTests` | 6(4+7케이스) | 실서버류 fixture 완전 파싱(한글 제목·다중 res), 접두 무관, 불량 객체 스킵-계속, duration 리터럴 경계 |
| `DlnaTrackFactoryTests` | 5 | FLAC>LPCM, 동 포맷 대용량 승, 비오디오→null, Codec/확장자 결정, 빈 제목 폴백 |
| `DlnaArtCacheTests` | 5 | URL 해시 재사용, 비이미지 미기록, 실패→null, 64MB 오래된 것부터 삭제 |
| `DlnaClientIntegrationTests` | 1 | 장치XML→서버→Browse(**서버측에서 SOAPACTION·요청 골든 검증**)→DIDL→Track 전 체인 |

통합 테스트가 **명시적으로 커버하지 않는 것**(파일 내 주석·조사 확인): SSDP 소켓 계층(파서만 단위),
오디오 스풀링/재생(N1 통합이 커버), UI 전체(수동 이월), 실서버 상호운용(수동 매트릭스),
`startingIndex>0` 다중 페이지 **반복 호출 흐름**(단위는 StartingIndex 40만), 아트 다운로드→ArtPath 부착(UI 내부).

## 6. 계획 대비 편차 (기록용 — 조치 불요, 단 D3는 plan 정정 대상)

- **D1** 통합 테스트가 계획의 `RawHttpServer` 소켓 확장 대신 **가짜 `HttpMessageHandler`** — 결정론적·저비용, 근거는 plan 119행에 기록됨.
- **D2** DLNA 섹션 활성화가 `ActivatePage`가 아닌 **첫 탭 클릭 lazy** — 페이지 진입 즉시 SSDP 회피. 동작상 개선으로 판단.
- **D3** 계획 문서 내부 불일치: §1.2 301행 "FLAC>WAV>ALAC>MP3>AAC>OGG" vs 116행 "…MP3>AAC…" — **코드는 ALAC(80)>AAC(70)>MP3(60) 단일 순서로 정합**. plan §1.2 표기 정정 권장.
- **D4** 모델 명칭: `DlnaBrowsePage.Objects`→`Entries`, `DidlItem`→`DidlItemEntry`(+Genre), `DlnaServer`+`ServiceType`.

## 7. 잔여 갭 구현 상세 (**→ 2026-09-26 L10으로 구현 완료** — 아래는 착수 시 명세 기록)

> 적대적 검토로 확정된 변경: (1) G1 주입 지점은 `PlaybackController.StartPending` 훅 +
> `RemoteArtResolved` 릴레이 + NowPlayingBar `UpdateArt` 재실행 — "아트 파이프라인"이란 중앙
> 서비스는 없음이 확인됐기 때문. (2) G2는 UI 래퍼 대신 `DlnaServer.DisplayName` Core 계산
> 프로퍼티(테스트 가능성·캐스팅 무변경). (3) 신규 방어: 복원 ArtUrl은 http(s) 절대 URI만 허용.
> 실행 기록·게이트 실측은 plan 문서 L10.

### G1 (P1) 복원된 DLNA 트랙의 아트 상실 + stale 주석

- **현상**: 재생 시점에만 아트를 부착하고(`DlnaSection.xaml.cs:284–288`), M3U8에는 ArtUrl을 null로 저장(`M3u.cs:174`)하며, `Track`에는 원격 아트 URL 필드가 없다(`Track.cs:33` — ArtPath만 존재). 결과: **M3U8 재시작 후 복원된 DLNA 트랙은 아트가 없다.** `RemoteTrackCodec.cs:38–39`의 "N2 adds the remote-art download" 주석이 N2 완료 후에도 방치됐다.
- **선택지 A (권장 — 영속 아트)**: (1) `Track`에 `ArtUrl`(원격, 직렬화 대상) 필드 추가. (2) `M3u.Write`에서 `null` 대신 `t.ArtUrl` 저장. (3) `DlnaTrackFactory.TryCreate`가 `item.AlbumArtUri`를 `track.ArtUrl`로 전달. (4) `RemoteTrackCodec.ToTrack`이 `meta.ArtUrl`을 `Track.ArtUrl`로 복원 + stale 주석 갱신. (5) 아트 파이프라인에서 `ArtPath==null && ArtUrl!=null`인 트랙을 만나면 `DlnaArtCache`로 fire-and-forget 다운로드 후 `ArtPath` 설정·갱신 통지 — **주입 지점이 열려 있는 의사결정**(AlbumArtService 계열을 건드리므로 백그라운드→UI 마샬링 불변식 적용 필요, AppServices.RunOnUi 패턴 재사용).
- **선택지 B (최소)**: 영속 아트는 비목표로 명시하고 stale 주석만 정정.
- **불변식(채택 시)**: 아트 다운로드 실패는 재생·복원을 막지 않는다(기존 계약 유지). 동일 URL 동시 다운로드는 결과 공유(DlnaArtCache 기존 보장). M3U8 왕복에서 ArtUrl은 손실 없이 보존(`M3uDpTrackTests`에 왕복 케이스 추가).
- **실패 시나리오 테스트(선설계)**: ArtUrl 보유 트랙의 M3U8 왕복, 손상 ArtUrl→null 디그레이드, 다운로드 실패 시 ArtPath 미설정·예외 무음.

### G2 (P2) 동명 서버 구분 누락

- **현상**: 계획 실패 시나리오 "동명 다수 서버(UDN 구분 표시)" 미구현 — ComboBox가 `FriendlyName`만 표시(`DlnaSection.xaml:24`).
- **수정안**: Core는 순수 유지(표시명은 UI 관심) — `DlnaSection`에서 `ItemsSource`를 `DlnaServer` 대신 표시용 래퍼(`DisplayName = $"{FriendlyName} ({DescriptionUrl.Host})"`, Server 보유)로 교체하고 `DisplayMemberPath="DisplayName"`. UDN까지는 과한 정보(호스트로 충분 — 동일 호스트 다중 서버는 극소).
- **테스트**: 표시명 조립은 코드비하인드 유틸 함수로 추출해 문자열 테스트 1종.

### G3 (P2) DIDL 스킵 무로그

- **현상**: 계획은 "스킵+로그하고 계속"이었으나 `DidlLiteParser.cs:50–55`의 catch는 빈 몸체. 실서버 비표준 편차 디버깅 시 무엇이 잘렸는지 관측 불가(M1 로깅 파사드 도입 취지와 상반).
- **수정안**: catch에서 `DawnPlayer.Core.Util.Log.Debug($"[dlna] skipped malformed DIDL object (class={element.Name.LocalName})")` 수준 1줄. 파서의 순수성 유지(로그 파사드는 Core 관례상 허용 — `DlnaArtCache`가 이미 `Log.Debug` 사용).
- **테스트**: 로그 자체는 게이트 대상 아님(기존 계약 "스킵 후 계속"은 `DidlLiteParserTests`가 이미 고정).

### G4 (P3) 소각 정리

- `DlnaSection.xaml.cs:170` — `requestedCount`를 UI 상수로 명시(현재 Core 기본값 500 암묵 의존; 값 불변).
- `DlnaTrackFactory.TryCreate`의 미사용 `baseUrl` — 유지(방어 파라미터, 주석 기재) 또는 제거. 권장: 주석 기재 유지(상대 res URL 대응 여지).
- `DidlLiteParser` 내부 `return null!` 2곳 — nullable 시그니처로 정리(동작 불변, 선택).

### M1 (수동, 자동 게이트 불가) 상호운용 매트릭스 — 실행 체크리스트

대상: MinimServer, Windows 미디어 스트리밍(사용자 환경). 각 서버에서:
1. 탐색: SSDP 검색 노출·ComboBox 2서버 동시 표시(동명이면 G2 후 구분 표기 확인)
2. 브라우징: 루트→하위 컨테이너 진입, 브레드크럼 클릭 점프, 한국어 제목 표기
3. 재생: FLAC/MP3 더블클릭 재생, 시크, 일시정지, 갭리스(연속 트랙)
4. 아트: 앨범 아트 표시, 아트 없는 항목 무음 처리
5. 페이지네이션: 500+ 항목 컨테이너에서 "더 불러오기" 반복·목록 이어짐
6. 경쟁: 브라우징 중 서버 전환·크럼 연타 → 이전 응답 잔상 없음(세대 가드)
7. 이탈: 서버 종료 후 새로고침 → 빈 상태 복귀, 재생 중 이탈 → 경고 후 정지(N1 규칙)
8. SSDP 차단 환경(가능하면): 타임아웃 → 빈 목록, 예외 없음
결과는 plan §0 N2 기록에 한 줄 실측으로 추가.

### M2 (수동) Narrator 스모크

서버 ComboBox·새로고침 버튼(자동화 이름), 목록 행(제목/부제), 컨텍스트 메뉴 항목 낭독 확인.

## 8. 검증 게이트 (§7 구현 시 적용 — AGENTS.md §4·§5)

클래스 단위 필터 테스트로 진행 피드백 → 완료 시점 콜드 리빌드(obj/bin 삭제) 0경고 0오류 + 전체 스위트 1회.
G1 채택 시 `M3uDpTrackTests` 왕복 케이스 추가가 게이트 선행 조건. 사용자 가시 기능(G1·G2)이면 마이너 버전 bump 대상.

## 9. 수동 확인 관찰 사항 (결함 아님, 설계 특성 — 실기기 청음 시 함께)

- `_rows` 스냅샷 재할당(`DlnaSection.xaml.cs:198`) — 페이지네이션 시 전체 리바인딩, **스크롤/선택 유지 동작은 육안 확인 대상**.
- `SetBusy`가 카운터 아닌 bool 토글(`:305`) — 새로고침·브라우즈 오버랩 시 링 조기 해제 가능(드묾).
- 타임아웃 값 산재: SSDP 3초 / 장치XML 10초 / CDS·아트 15초 / 스풀 100초 — 상수 미통합(운영 교훈 나올 시 통합 검토).
