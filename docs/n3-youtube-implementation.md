# N3 YouTube 소스 — 착수 상세 구현서 (스파이크 실측 포함)

> 작성일: 2026-09-26 · 기준 커밋: `37ac13f` (L10 직후)
> 성격: [implementation_plan.md](../implementation_plan.md) §1.2 N3 스케치를 **착수 수준(N0–N2와 동일한
> 파일 수준)으로 상세화**한 문서 + 1차 관문(스파이크) 실측 기록. **→ 2026-09-26 §8 승인 후 구현 완료**
> (게이트 실측·편차는 plan 문서 N3 기록 참조: 설정 카테고리는 §4.10 대신 YouTube 섹션에 통합).
> 코드 조사는 2026-09-26 워킹트리 실측(`파일:행` 근거), 스파이크는 §1 실측.

---

## 0. 요약

스파이크(§1)가 로드맵의 1차 관문을 통과했다: **yt-dlp → ffmpeg s16le 파이프가 JS 런타임 없이 동작**하고,
19초 트랙을 총 2.7초(해석 2.5초 + 다운로드·변환, 실시간 대비 수 배)에 종단 처리했다. → **A안(파이프
리더) 채택을 실측으로 확정**하고, B안(m4a 스풀)은 기록상 폴백으로 유지한다. 본 문서는 리더·의존성 감지·
설정 UI·테스트 설계를 파일 수준으로 명세한다. 최대 리스크는 유한 트랙 경계에서의 수 초 Open(해석
2.5초 > 프리페치 마진 1.2초)이며 v1에서는 수용·문서화한다(§8).

## 1. 스파이크 실측 (2026-09-26, 이 개발 환경)

| 관문 | 결과 | 데이터 |
|---|---|---|
| 도구 | ffmpeg 9.0.1(winget) 존재, yt-dlp 2026.08.19를 pip로 설치, **Deno 없음** | |
| ① `-J` 해석 | **성공, 2.5초**, 93KB JSON | 대상: 19초 영상(jNQXAC9IVRw) |
| ② 파이프 `yt-dlp -f bestaudio -o - \| ffmpeg -i pipe:0 -f s16le -ac 2 -ar 48000 pipe:1` | **성공, 총 2.7초**, s16le 48k 스테레오 **정확히 19.0초분**(3,649,064B) | 포맷 251(Opus, ~106–130k abr) — ffmpeg가 Opus 디코딩 |
| ③ JS 런타임 부재 동작 | **visionos player API로 폴백해 정상 스트리밍**(로그: "Downloading visionos player API JSON") | 계획서 메모의 예측과 일치 — Deno 없이도 (이 영상은) 전 과정 동작 |
| (참고) B안 데이터 | 19초 = 246KB(bestaudio) → ~130KB/s | |

**결론**: A안 채택. 해석 ~2.5초가 시작 지연의 주도 항목(Open은 스레드풀에서 실행되므로 UI 블로킹 없음
— `PlaybackController.cs:227` Task.Run). 이후 진행은 실시간보다 빠르다. **제한**: 단일 단편 영상 실측 —
긴 트랙·라이브·지역 제한·PO 토큰 요구 영상은 수동 검증 항목으로 이월(§7).

## 2. 아키텍처

```
재생: Track{Path=페이지 URL, SourceKind=YouTube}                    (재생목록·M3U8엔 페이지 URL만 — I5)
   → AudioFileReaderFactory.Open(path, YouTube)                     (스레드풀 — Open 수 초 허용 계약)
   → SelectProvider: kind==YouTube → YouTubeTrackReaderProvider     (라우팅 갱신 — §5 R1)
   → YouTubeStreamReader.Connect():
        [yt-dlp -f bestaudio -o - <pageUrl>]  ──stdout──▶  [ffmpeg -i pipe:0 -f s16le -ac 2 -ar 48000 pipe:1]
        (네트워크 다운로드 + 스로틀 회피, IP 묶임 해석)         (Opus/m4a → s16le PCM, 포맷 고정)
                                                    │ stdout (s16le 192,000 B/s @48k)
                                                    ▼
        BufferedPcm(라디오에서 재사용) → PcmSampleProvider(EOF 지원 파생) → 시퀀서(리샘플·DSP 자동)
   - TotalTime = yt-dlp -J의 duration (Connect 시 1회 -J 해석을 스트리밍과 겸하려면 2회 해석 대신
     "먼저 -J, 그다음 스트리밍" 2단계 — 해석 2.5초×2 비용 대신 스트리밍 시작 전 1회 해석을 유지하고
     duration은 스트리밍 체인과 별개로 -J 1회에서 얻는다: v1은 해석 2회 수용, 최적화는 후속)
   - 시크: 오프셋 재시작(프로세스 체인 재기동 + ffmpeg -ss <offset>) — getter는 목표 오프셋을 즉시
     반환해 SeekLocked read-back 계약(SequencerStream.cs:225-229) 충족, 오디오는 재기동 후 재개
   - 종료: ffmpeg stdout EOF → 유한 트랙 종료(0 반환) — 라디오의 "끝 없음"과 대비되는 정책(I1)
   - 언더런: 부족분 무음(라디오 재사용) — 단 프로세스 사망은 EOF로 처리(I1)

의존성: PATH에서 yt-dlp·ffmpeg 탐지(--version 실행으로 검증) → Settings 상태 표시·설치 안내.
미설치 상태에서 재생 시도 → AudioOpenException + 지역화된 안내 경고(조용한 실패 금지 — I2).
```

## 3. Core 구성 — `src/DawnPlayer.Core/Audio/` + `Network/YouTube/`

1. **`Network/YouTube/YouTubeDependency.cs`** — 감지 전담. `YouTubeDependencyStatus { YtDlpPath, FfmpegPath, YtDlpVersion, FfmpegVersion, HasJsRuntime(Deno), ProbedAt }` 레코드 + `Probe()`: PATH 탐색(Windows PATHEXT 포함) → 각 `--version` 실행(5초 타임아웃, 실패는 미설치 취급). 결과 캐시 + `Invalidate()`. 어떤 경우에도 던지지 않음(감지 실패 = 미설치 상태).
2. **`Network/YouTube/YouTubePageUrl.cs`** (순수) — 페이지 URL 판정/정규화: `youtube.com/watch?v=`, `youtu.be/`, `music.youtube.com` 수용, playlist 파라미터 제거, 그 외 스킴/형식 거부. 재생 가능 URL과 거부 사유(테스트 계약).
3. **`Network/YouTube/YouTubeJsonParser.cs`** (순수) — `-J` JSON → `YouTubeTrackMeta{Title, Uploader, DurationMs, BestAudioFormatId?, ThumbnailUrl?}`. never throws(불량 JSON → null), duration 결누 = 라이브 취급(TotalTime 0).
4. **`Audio/YouTubeProcessRunner.cs`** — **프로세스 seam**. `IYouTubeProcessRunner { ProcessSpec ResolveJson(url, ct); ProcessSpec StreamAudio(url, formatId, ct); bool TryProbe(path, out version) }` + 실구현(`Process.Start` 래퍼) + 테스트 fake. 프로세스 수명 책임: Dispose 시 자식 체인 전부 종료 — 좀비 방지 불변식(I4). 리스크 표(`implementation_plan.md:361`)의 "프로세스 래퍼 인터페이스화" 이행.
5. **`Audio/YouTubeStreamReader.cs`** — `ITrackReader`. RadioStreamReader에서 `BufferedPcm` 재사용(내부 조합 방식 — `RadioStreamReader.cs:281` PcmSampleProvider 선례), 새 **EOF 지원** 샘플 프로바이더 파생: PCM 고갈 시 0 반환(유한 종료), 프로세스 사망도 EOF, 언더런은 무음. `SourceFormat = s16le 48000×2`(ffmpeg 인자 고정 — 시퀀서가 리샘플 담당, `SequencerStream.cs:558`). `TotalTime` = 해석 duration. `CurrentTime` 세터 = 오프셋 재시작(동기적으로 목표치 기록 + getter 반환, 체인 비동기 재기동 — §8 제약 기록). `Dispose`: 체인 전부 종료 + BufferedPcm 정리.
6. **`Audio/YouTubeTrackReaderProvider.cs`** — `ITrackReaderProvider`, `Order => 350`. `CanOpen`: http(s) 스킴 + 의존성 감지 성공 여부와 무관하게 수용(실패는 Open에서 AudioOpenException+안내로 — I2). `Open`: ① 의존성 미탐지 → AudioOpenException(설치 안내 메시지, `CoreMessages` 키 신설) ② `-J` 해석(10초 타임아웃) ③ 해석 실패(비공개·지역·삭제) → AudioOpenException(사유 포함) ④ 스트리밍 체인 기동 + 선버퍼 대기(라디오의 1.5s/8s 패턴 재사용 — `RadioStreamReader.cs:106`).
7. **라우팅 변경** — `AudioFileReaderFactory.SelectProvider`(`AudioFileReaderFactory.cs:168-179`): `sourceKind == YouTube` → 신규 공급자 고정 분기 추가. **`fileOverHttp`에서 YouTube를 제외**(Dlna만 유지). 파이프 리더는 스풀을 쓰지 않으므로 spool 디렉터리 불요(AppPaths 변경 없음).
8. **설정 노드** — `AppSettings.YouTube { bool Enabled = true }`(v1 최소). 저장·마이그레이션은 기존 설정 파이프라인.

## 4. App 구성

9. **`Views/Network/YouTubeSection.xaml(.cs)`** — Network 탭 세 번째 섹션(NetworkPage 라디오버튼 1세트 추가 — `NetworkPage.xaml:22-42` 패턴). v1 최소 기능: URL 입력 + "재생/Now Playing에 추가" + **의존성 상태 배지**(미설치 시 설치 안내 문구·링크, 섹션 비활성 안내). 재생 연결은 RadioSection과 동일(`AddTracks` + `PlaybackUiHelper.PlayItemAsync`). 로직은 WinUI-free `YouTubeSectionCommands`(테스트 링크 컴파일 — `RadioStationCommands` 선례).
10. **SettingsPage YouTube 카테고리** — ListViewItem+Section 패널 1세트(`SettingsPage.xaml:82-173` 패턴): 의존성 상태(yt-dlp/ffmpeg 버전·JS 런타임 유무), "다시 검사", 설치 안내(https://github.com/yt-dlp/yt-dlp), 활성화 토글, **유손실·ToS 고지 문구**. 상태 조회는 `IAudioSettingsService.GetExclusiveModeStatus` 선례(`IAudioSettingsService.cs:9-34`)를 따라 조회형 레코드.
11. **AppServices** — 의존성 프로브 등록(정적 노출), NetworkPage/Settings 진입 시 1회 probe(수 초 방지 위해 5초 타임아웃·비동기).
12. **resw ×3** — `Network_YouTube_*`·`Settings_YouTube_*` ~14종(ko/en/ja, 설치 안내·ToS 고지 포함).
13. **README ×3** — YouTube 항목(의존성 감지형, 유손실 상한, ToS 고지 명시 — 규약 §6).

## 5. 정책 불변식 (§3 결함 트리 → 테스트로 계약화)

- **I1 종료 정책**: ffmpeg stdout EOF·프로세스 사망 = 유한 트랙 종료 → 시퀀서가 다음 트랙으로 진행. 무음 정지(라디오의 dead=silence)를 유한 소스에 복사하지 않는다. (라이브 스트림=duration 0은 라디오 정책 적용)
- **I2 의존성 실패 가시화**: 미설치·해석 실패는 반드시 AudioOpenException+지역화 안내로 표면화. 조용한 실패·무음 재생 금지.
- **I3 위치 보고**: 시크 세터 후 getter가 목표 오프셋을 반환(SeekLocked read-back 계약 수신). 재기동 지연 동안 무음이 흐르고 오디오가 도착하면 위치가 실제 재생과 재동기화 — "보고 위치 ≠ 실제 위치" 영구 불일치 금지(8ebfe12 교훈).
- **I4 좀비 방지**: 리더 Dispose는 yt-dlp·ffmpeg 체인 전부를 종료한다(하나만 죽어도 나머지가 파이프 대기로 남지 않음). 테스트로 계약화.
- **I5 영속 최소화**: 재생목록·M3U8에는 페이지 URL만 저장(만료되는 미디어 URL·format id 저장 금지). `#DPTRACK kind=YouTube` 왕복은 기존 인프라 재사용(`RemoteTrackCodec.cs:29-32` 이미 수용).
- **I6 라우팅 회귀 금지**: Dlna→스풀, kind 없는 http→라디오 경로 불변. **YouTube만** 스풀 리더에서 파이프 리더로 재라우팅(기존 `TrackReaderFactoryRoutingTests`의 YouTube 기대값 갱신은 승인된 스펙 변경 — 아래 §8).
- **I7 게이트 상속**: 페이지 URL은 `RadioTrack.IsStreamUrl` 스킴 게이트가 자동 적용(라이브러리 비저장·RG 스캔 제외 등 — 조사 §4.3). 스크로블·통계 허용 여부를 바꾸려면 별도 결정.

## 6. 테스트 설계 (코드 선행 — AGENTS.md §3)

- **순수**: `YouTubePageUrl_*`(수용/거부/정규화), `YouTubeJsonParser_*`(정상·불량·duration 결누=라이브·bestaudio 선택), 라우팅 갱신(`YouTubeKind_RoutesToPipeReader` + Dlna/라디오 회귀 유지), `M3uDpTrackTests` kind=YouTube 왕복 + ArtUrl.
- **프로세스 seam**: fake `IYouTubeProcessRunner` — 정상 PCM 스트림, **즉사**(→EOF), 느린 stdout(→언더런 무음), 비정상 종료 코드(→AudioOpenException), -J 실패 JSON. 종료·Dispose 불변식(I1·I4)은 fake로 단언.
- **실프로세스 통합 1종**: Windows 러너 전제 — 임시 `.cmd` shim이 캔 PCM을 stdout으로 출력하는 체인을 실제 `Process`로 기동해 Connect→Read→EOF 전 흐름 검증(raw 서버 패턴 `HttpProgressiveTrackReaderTests.cs:97` 선례, 테스트 직렬화·샌드박스 AppPaths 재사용).
- **의존성 감지**: fake PATH/실행 파일로 탐지·미탐지·version 파싱.

## 7. 작업 순서·완료 기준·수동 항목

순서: (1) 순수 파서·URL + 라우팅 갱신 + 테스트 (2) 프로세스 seam + 리더 + fake 테스트 (3) 의존성 감지 + 설정 노드 (4) YouTubeSection + Settings 카테고리 + resw (5) 통합 shim 테스트 + 콜드 리빌드/전체 게이트 (6) README ×3.

*완료 기준*: 위 테스트 전수 + 통합 1종, 콜드 리빌드 0경고 + 전체 스위트 1회, M3U8 왕복, README ×3.
*수동(이월)*: 실서버 장기 청음(긴 트랙·재생 중 시크 체감 지연·라이브 스트림·지역/연령 제한·Premium 256k),
의존성 미설치 UX 육안, Narrator 스모크, YouTube ToS 고지 문구 검토.

## 8. 리스크·승인 대기 결정 사항

| # | 항목 | 내용 | 권장 |
|---|---|---|---|
| D1 | 자연 경계 갭 | 해석 2.5초 > 프리페치 트리거 마진 1.2초 → 자연 진행 시 수 초 무음 갭 가능(조사 §1.4). 프리페치가 미리 Open하므로 재생 큐가 이미 구성된 경우 완화되나 보장 없음 | v1 수용·기록, 후속으로 YouTube 후속 트랙 프리페치 임계 연장(예: 남은 15초) |
| D2 | 시크 체감 | 오프셋 재기동 = 재해석 포함 수 초. UI 블로킹 없음(비동기 재기동)이나 시크 응답성은 MfTrackReader 대비 열위 | v1 수용·문서화. 후속: 해석 캐시(같은 페이지 URL 재해석 생략) |
| D3 | 라우팅 테스트 갱신 | `TrackReaderFactoryRoutingTests`의 "YouTube→스풀 리더" 기대값이 파이프 리더로 바뀜 — 기존 테스트 1건 기대값 갱신(스펙 변경) | 승인 대기 항목 |
| D4 | 동봉 vs 감지 | 인스톨러 +100MB 갱신 추종 부담 vs 사용자 설치 부담 | **감지형 유지**(plan 기존 결정) — 설치 안내 UI만 제공 |
| D5 | ToS·유손실 고지 | UI·README에 명시(plan §1.2 3번) | v1 필수 포함 |
| D6 | 라이브 스트림 | YouTube 라이브(duration 0)는 라디오 정책(끝 없음·시크 no-op·A-B 거부) — 별도 검증 없음 | 수동 항목으로 이월 |
