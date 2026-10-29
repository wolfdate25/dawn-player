# Dawn Player 기능별 적대적 검토 보고서 (2026-10-24)

> **수리 상태 (2026-10-24)**: 사용자 승인 후 P0 2건, 실질 P1(쿼리 파서) 포함 P1 18건, P2 14건,
> P3 선별 6건이 수리되었다. 수리 내역·검증 게이트는 `implementation_plan.md` §8, 방어 게이트
> 테스트는 `tests/DawnPlayer.Tests/Review20261024RepairGateTests.cs`(16건). 클린 리빌드 0경고 +
> 전체 스위트 2,326/2,326 통과. **잔여는 4개 그룹(A 소형 26 · B 구조 15 · C 폐기권장 8 ·
> D 실행확인 3)으로 분류해 결정 대기** — `docs/adversarial-review-2026-10-24-remaining.md` 참조.

- **대상**: `main` @ `d6469fc` (v1.8.0 발행 직후 상태)
- **방법**: 9개 기능 영역별 서브 에이전트 적대적 리뷰(병렬 한도 2, 배치 실행). 모든 항목은 해당 메서드 전체·호출자·테스트를 실제로 읽어 교차 검증했으며, 사실과 추정을 구분해 기록했다. **소스 수정·빌드·테스트 실행은 하지 않았다(읽기 전용).**
- **총계**: **104건** (P0 2 · P1 18 · P2 33 · P3 51) — 두 에이전트가 동일하게 발견한 1건(DoP×DirectSound/WaveOut)은 병합. 영역 간 중복 제외.

| 영역 | P0 | P1 | P2 | P3 | 계 |
|---|---|---|---|---|---|
| 1. 재생 엔진 | 1 | 2 | 2 | 4 | 9 |
| 2. 포맷/DSD/CUE | 0 | 5 | 1 | 6 | 12 |
| 3. DSP 체인 | 0 | 3 | 2 | 6 | 11 |
| 4. 플레이리스트·큐 | 1 | 3 | 5 | 4 | 13 |
| 5. 라이브러리·평점·태그 | 0 | 1 | 8 | 5 | 14 |
| 6. 트랜스코더·오거나이저 | 0 | 0 | 6 | 6 | 12 |
| 7. 네트워크(라디오/DLNA/YouTube) | 0 | 1 | 3 | 5 | 9 |
| 8. 가사 | 0 | 1 | 4 | 6 | 11 |
| 9. UI 통합 | 0 | 2 | 2 | 9 | 13 |
| **합계** | **2** | **18** | **33** | **51** | **104** |

> 심각도 정의 — P0: 크래시/데이터 손실, P1: 일반 기능 결함, P2: 경계·드문 조건 결함, P3: 저위험.
> 특기: 4-5(쿼리 파서 무한 재귀)는 보고 등급 P2이나 **잡을 수 없는 StackOverflow 크래시**를 유발하므로 실질 P1로 취급할 것을 권장.

---

## 1. 재생 엔진 (WASAPI·갭리스 시퀀서·크로스페이드·장치 처리)

### [P0-1] 크로스페이드 진행 중 프리페치 리더를 `_gate` 없이 Dispose — 오디오 스레드 사용 중 해제 경쟁
- **위치**: `src/DawnPlayer.Core/Audio/SequencerStream.cs:547-563`(렌더 스레드 크로스페이드 읽기), `:301-314`(SetPrefetched), `:352-363`(DiscardPrefetched), `:286-287`(SwitchTo가 DiscardPrefetched를 `_gate` 이전에 호출), `:366-369`(Cancel)
- **근거**: 렌더 스레드는 `_prefetchLock`으로 `nextTrack = _prefetched`를 복사한 뒤 잠금을 풀고 `nextTrack.Source.Read(...)`를 호출한다. 반면 `SetPrefetched`/`DiscardPrefetched`는 `_gate` 없이 `previous?.Track.Reader.Dispose()`를 즉시 실행한다. 두 스레드가 동일 리더에 동시 접근하는 창이 존재한다.
- **도달 경로(실사용자 동작)**: (a) 트랙 마지막 ≤10초에 큐 편집·대기열 추가·순서변경 → `Queue.Changed` → `InvalidatePrefetch` → `SetPrefetched(null)`; (b) Stop/Next/Previous/더블클릭; (c) "트랙 후 정지" 토글; (d) 앱 종료.
- **실패 시나리오**: 크로스페이드 6s 설정, 트랙 끝 3s 전 "대기열에 추가" 클릭 → 페이드인 중인 다음 트랙의 MediaFoundationReader/VorbisWaveReader가 렌더 중 Dispose → **AccessViolation(프로세스 크래시)**. FileStream 계열이면 ObjectDisposedException → 재생 급정지+오류 토스트.
- **테스트 갭**: `SequencerStreamResponsivenessTests`의 동시성 테스트는 `CrossfadeDurationSeconds=0`으로 실행되어 이 창을 커버하지 못함.
- **수정 방향**: 폐기 예정 프리페치 리더의 실제 Dispose를 렌더 스레드로 이관(그레이브야드 패턴 — `_prefetchLock` 내 표시만 하고 실제 해제는 다음 렌더 패스 또는 `_gate` 취득 후).

### [P1-1] 프리페치 폴링 임계(1.2s 고정)가 크로스페이드를 잘라냄 — 1.2s 초과 설정 시 페이드 잘림 + 핸드오프 음량 점프
- **위치**: `PlaybackController.cs:1532`(`RemainingTime > 1.2s`면 스킵), `SequencerStream.cs:521-541`(아밍은 프리페치 설치 후에만), `:596`(조기 핸드오프)
- **근거**: 크로스페이드 아밍은 "남은 시간 ≤ fadeDur + 프리페치 존재"가 동시 만족이어야 하는데, 프리페치는 남은 시간 ≤ 1.2s일 때만 열린다. fadeDur > 1.2s면 페이드가 설치 시점부터만 진행되다 트랙 A 소진 시 `elapsed < total`인 채 핸드오프 → 다음 곡 게인이 0.15~0.19에서 1.0으로 점프.
- **시나리오**: 크로스페이드 10s 설정 → 실제 페이드 약 1.2s → 전환 순간 "쿵" 소리.
- **테스트 갭**: `CrossfadeMixerTests`는 전부 `SetPrefetched` 수동 설치로 폴링을 우회.
- **수정 방향**: 폴링 임계를 `max(1.2s, CrossfadeDurationSeconds)`로.

### [P1-2] 자연 진행(다음 트랙 resolve) 창에 일시정지가 끼면 진행이 취소되고, 재개 시 영구 무음 "Playing" 좀비 상태
- **위치**: `PlaybackController.cs:346-354`(Paused arm이 드레인 시퀀서 미검사), `:1280-1327`(OnSequenceEnded의 generation 재검사에서 폐기), `SequencerStream.cs:509-515`(드레인 Read)
- **시나리오**: 로컬→YouTube 갭리스 전환 직전 수 초 창에 스페이스바 일시정지 → 재개 → "재생 중" 표시, 시크바 0:00, 영원한 무음. Next로만 탈출. 슬립 타이머 만료가 같은 창에 걸려도 동일.
- **수정 방향**: Paused arm에 `seq.CurrentItem != null` 검사를 추가해 드레인 시퀀서면 Stop/재해결로 전환(Buffering arm과 동일 계약).

### [P2-1] 페이드 창 길이가 현재/다음 트랙 길이로 클램프되지 않음
- **위치**: `SequencerStream.cs:528-537`, `:596`
- **시나리오**: (a) 5s 트랙 + 10s 크로스페이드 → 중간 핸드오프 +3dB 점프. (b) 다음 트랙이 fadeDur보다 짧으면 트랙 대부분 스킵.
- **수정 방향**: 아밍 시 `fadeDur = min(fadeDur, 현재 남은 시간, 다음 트랙 길이)`.

### [P2-2] 무관한 큐 편집이 진행 중 크로스페이드를 즉시 중단 — 페이드아웃 도중 원음량 복귀 점프
- **위치**: `PlaybackController.cs:260-268`(Queue.Changed → InvalidatePrefetch), `SequencerStream.cs:306-312`(SetPrefetched가 무조건 `_inCrossfade=false`)
- **시나리오**: 페이드 진행 중 재생 예정과 무관한 플레이리스트 편집 → 디핑 후 점프. `Queue.Consume`(매 트랙 시작)도 발생원.
- **수정 방향**: 진행 중 페이드 유지 또는 중단 시 현재 게인에서 역방향 램프.

### [P2-3] (→ 2영역 P1과 동일 결함으로 병합) DoP 우선 + DirectSound/WaveOut → 노이즈 출력. 두 에이전트가 독립 발견.

### [P3-1] 공유 모드(및 DS/WaveOut) Init/Play 실패 시 반쯤 만들어진 output/sequencer/reader 해제 누락 + DoP→PCM 스왑 시 원본 DoP 리더 미폐기
- **위치**: `OutputDrivers.cs:102-116`(`when (exclusive)` catch), `:268-269`, `:311-312`, `:81`(`ReopenPendingAsPcm`에서 원본 Dispose 없음)
- **수정 방향**: catch 확장 + 스왑 시 원본 Dispose.

### [P3-2] 급속 연속 수동 명령 시 TrackLeft 이중 보고·히스토리 이중 적립
- **위치**: `PlaybackController.cs:640-648`(FireManualLeave), `:1183-1218`(`_currentItem` 갱신이 비동기)
- **시나리오**: Next 직후 수십 ms 내 재Next → 같은 곡 이탈 2회 집계, 히스토리 [A, A].
- **수정 방향**: `_manualLeaveMarker`와 동일하게 히스토리 중복 방지.

### [P3-3] 배타 재구축 창(수백 ms)의 사용자 탐색 조용히 무시; A-B 루프 종점이 페이드 구간 내부면 루프 조용히 종료
- **위치**: `PlaybackController.cs:503-506`, `:1106-1118`; `SequencerStream.cs:703-722`, `:812-814`
- **수정 방향**: (a) 재구축 창 시크를 새 세션 StartPosition으로 이월, (b) AB 루프 활성 시 크로스페이드 아밍 억제.

### [P3-4] 라디오가 갭리스 체인될 때 보고 위치가 open+prebuffer만큼 선행
- **위치**: `RadioStreamReader.cs:23,61`(벽시계 CurrentTime), `SequencerStream.cs:806-807`
- **수정 방향**: 라디오 체인 시 앵커 0.

### 무결 판정(재생 엔진)
v1.8 크로스페이드 수리 자체(위치 앵커·시크 되감기) 정상, 등전력 코사인 게인 수학, DoP/RequireRestart 트랙의 크로스페이드 완전 우회, WASAPI 배타 폴백 체인(HRESULT 매핑), 전용 포맷 정책 행렬, 장치 실종 처리(단, 테스트 미커버), 세대/잠금 재검사, A-B 반복(오디오 스레드 샘플 단위·퇴화 방어), DSP 체인 COW 스냅샷 무할당, PCM 변환 경계값, BufferedPcm 세대 게이트, StreamStallTracker 히스테리시스, WaveformPeaks, PlayOrderResolver(큐 우선·반복-원).

---

## 2. 오디오 포맷 디코딩 / DSD·DoP / CUE

### [P1-1] 실제 DFF(DSDIFF) 파일을 전혀 열 수 없음 — 스펙 불일치
- **위치**: `DsdRawReaders.cs:208-219`(FS를 AIFF식 80비트 확장실수로, 채널을 'CHAN' 4바이트 int32로 읽음)
- **근거**: DSDIFF 1.5 스펙(Philips)은 FS 청크가 **4바이트 u32 BE**, 채널 청크가 **'CHNL' + 2바이트 u16 BE + 채널별 4바이트 ID**. FFmpeg/MPD/taglib/xbmc 모두 u32/u16+CHNL로 파싱. 현재 코드는 어떤 정상 .dff에서도 `haveRate/haveChannels`가 참이 되지 못해 전부 "헤더 비정상"으로 거부. 게다가 TagLibSharp 2.3.0은 .dff 미지원이라 라이브러리 스캔에서도 누락 — **DFF 지원 경로 전체가 죽어 있음**. 테스트 픽스처(`DoPIntegrationTests.cs:287-288`)가 오독된 형식을 오히려 고정.
- **수정 방향**: FS u32 BE(기존 80비트는 폴백 유지), CHNL u16+ID skip, 실 포맷 픽스처로 테스트 재작성.

### [P1-2] DoP 리더가 DirectSound/WaveOut 출력에서 마커 파괴(풀스케일 노이즈, 볼륨 무시) — ※엔진 영역과 중복 병합건
- **위치**: `OutputDrivers.cs:235-277`(DS), `:285-320`(WaveOut) — DoP 게이트는 `WasapiOutputDriver`(69-83행)에만 존재
- **근거**: DoPPriority면 무조건 DoP 리더가 열리고 `rawPassthrough=true`로 DSP·볼륨 전부 우회된 24비트 DoP 워드가 `PcmConvert.ToBytes`의 16비트 케이스(`SampleProviders.cs:90-99`, `*32767f`)를 통과하며 상위 비트 잘림 → 마커 소거. **비트퍼펙트 불변식의 유일한 위반 지점.**
- **시나리오**: 설정에서 드라이버 DirectSound + DSD "DoP 우선" → DSF 재생 → 볼륨 슬라이더도 무시한 풀스케일 노이즈.
- **수정 방향**: DoP 게이트를 드라이버 진입점 공통으로 이동, DS/WO에서는 PCM 리더로 스왑.

### [P1-3] WASAPI 독점 실패 → 공유 폴백이 같은 DoP 리더를 재생성해 노이즈 재생
- **위치**: `OutputDrivers.cs:117-131`(폴백이 `ReopenPendingAsPcm`이 아님), `OutputSessionFactory.cs:171-178`
- **시나리오**: DoP 재생 중 타 앱이 엔드포인트 점유한 순간 트랙 시작 → "공유 모드 전환" 경고와 함께 노이즈.
- **수정 방향**: 폴백 경로에서 raw DSD 리더면 `BlockDoP()` 후 `ReopenPendingAsPcm`.

### [P1-4] CUE `FILE "이름 공백"` 경로가 첫 공백에서 절단됨
- **위치**: `CueSheet.cs:85` + `:172-176`(TakeFirstToken이 따옴표 내부 공백에서 분리)
- **근거**: `FILE "My Album.flac" WAVE` → `"My`만 취함 → `File.Exists` 실패 → 그룹 전체 스킵. EAC/XLD 표준 인용 형식이며 공백 포함 앨범 이미지명은 극히 흔함. 테스트는 공백 없는 파일명만 커버.
- **시나리오**: 공백 포함 이미지명 CUE 앨범 스캔 → 가상 트랙 0개, 원본 이미지가 단일 롱트랙으로만 남음.
- **수정 방향**: quote-aware 파싱.

### [P1-5] BOM 없는 ANSI(CP949) CUE를 UTF-8로 오인 — 한글 FILE명 미해결·타이틀 깨짐
- **위치**: `CueSheet.cs:211`(`Encoding.Default` = .NET에서 항상 UTF-8)
- **근거**: `M3u.cs`는 `CodePagesEncodingProvider`+`Encoding.GetEncoding(0)`으로 정석 처리하고 있어 의도와 배치됨.
- **시나리오**: 구형 EAC가 CP949로 쓴 한글 CUE → 가수/제목 깨짐 또는 트랙 미출현.
- **수정 방향**: M3u와 동일한 ANSI 폴백.

### [P2-1] DSF data 청크 과대 선언 + 실제 파일 절단 시 스테일 버퍼 오디오 반환·종료하지 않는 재생
- **위치**: `DsfTrackReader.cs:111`(선언값 클램프 없음), `:195-197`, `:219-224`
- **시나리오**: 절단된 DSF 재생 → 오디오 끊긴 뒤에도 시크바가 끝까지 진행, 무음/잔재 재생, 트랙 미종료. (DFF 원시 경로는 안전 — 불일치)
- **수정 방향**: `_dataBytes`를 파일 길이로 클램프, EOF 시 `written` 기준 반환.

### [P3-1] `DsdPcmTrackReader` 비-8배수 decimation에서 비트 정렬 붕괴(폴백 허용 조건이 44100의 8 이상 배수 전부) — `DopTrackReader.cs:249`, `:290-292`. 폴백 조건에 `decimation % 8 == 0` 추가.
### [P3-2] DSF `bitsPerSample=8` 변형 무시(스펙상 레이아웃 상이) — `DsfTrackReader.cs:79`. 거부 또는 분기.
### [P3-3] 절단 DFF/DSF 마지막 블록 부분 읽기에서 후행 채널 스테일 바이트(≤1블록, 끝단 클릭) — `DsdRawReaders.cs:115-123`. short-read 시 유효 프레임만 반환.
### [P3-4] DoP/DsdPcm 리더 생성자 throw 시 원시 리더(FileStream) 누수 — `DopTrackReader.cs:88-91`, `:200-203`. throw 경로에서 Dispose.
### [P3-5] APE 이미지 CUE가 무경고 스킵(README는 "APE 등" 명시) — `AppPaths.cs:114-117` + `MusicLibrary.cs:296`. 지원 추가 또는 안내.
### [P3-6] 마지막 트랙 INDEX가 음원 길이 초과 시 해당 트랙 조용히 소실 — `MusicLibrary.cs:306-309`. DurationMs로 클램프해 유지.

### 무결 판정(포맷)
DoP 패킹 수학(24비트 배치·마커 위상·float 왕복 비트 정확, DSD128 더블 DoP 무손실), 박스카 데시메이션(불완전 그룹·채널 인터리브), CueTrackReader 범위 재생(드레인 슬랙·범위 밖 클램프·0길이 방어·DoP 투과), CUE 시간 파싱(INDEX 00 무시·75fps), CUE 통합(이미지 행 숨김·fragment 키 이월·스테일 프루닝), Media Foundation 예외 래핑, DoP/PCM 세션 경계 매트릭스, DST 압축 거부·악성 헤더 방어.

---

## 3. DSP 체인 (EQ·ReplayGain·컨볼루션·AGC·크로스피드·리미터)

### [P1-1] EQ 라이브 파라미터 변경·갭리스 트랙 경계에서 바이쿼드 지연선(z-상태)이 통째로 폐기 — 클릭/크래클
- **위치**: `EqualizerDspEffect.cs:33-41`(스냅샷마다 State 영초기화), `:86-93`(SetProfile), `:157-179`; `SequencerStream.cs:828-834`(갭리스 경계 `Reset()`)
- **근거**: TDF2 출력은 `y = B0·x + z1`이라 상태 0화는 z1만큼 순간 점프. 저주파 쉘프에서 정상상태 z1 ≈ (H(0)−B0)·x로 진폭 수 배. 재생 중 슬라이더 드래그 시 0.1dB 틱마다 상태 초기화 = 반복 크래클. Preamp도 동일.
- **테스트 갭**: 스냅샷 교체 전후 출력 연속성(스텝 상한) 단언 없음.
- **수정 방향**: 밴드 배열 크기 동일 시 기존 State 재사용, 갭리스 경계(연속 신호)에서는 EQ Reset 제거.

### [P1-2] 앨범 ReplayGain 스캔이 앨범 피크를 항상 0.0으로 기록 — 안티클리핑 우회 + 태그 데이터 오염
- **위치**: `AppServices.cs:673-674, 683-690, 695-700`; `LoudnessScanner.cs:101-104, 162-166`; `ReplayGainMath.cs:32-35`
- **근거**: 앨범 스캐너는 `AppendBlocks`만 받아 `_peak`가 0 유지 → `REPLAYGAIN_ALBUM_PEAK = "0.000000"`을 파일에 기록 → 재생 시 `peak is > 0` 조건 미통과로 안티클리핑 클램프 완전 우회 → 양수 게인 앨범이 최대 +18dB까지 무제한 적용 → 클리핑/리미터 왜곡 + 사용자 파일 태그 영구 오염.
- **테스트 갭**: 앨범 Peak 무단언, `RunReplayGainScan` 오케스트레이션 무테스트.
- **수정 방향**: 앨범 피크를 멤버 트랙 피크 max로 집계.

### [P1-3] 노멀라이저 끄기 토글 시 ReplayGain 이중 적용 — 순간 RG² 부스트
- **위치**: `AudioSettingsService.cs:135-146` → `PlaybackController.cs:685-693` → `DynamicNormalizerDspEffect.cs:163-178`(비활성 페이드) + `SequencerStream.cs:396-409`(노드 즉시 적용)
- **근거**: 끄기 토글 시 (1) 노멀라이저 게인 RG→1.0으로 ~50ms 페이드, (2) 전단 노드에 RG 즉시 적용 — 사이 출력 = RG×RG. RG=+6dB면 순간 +12dB, +12dB면 +24dB.
- **테스트 갭**: `MasterFaderSplitTests`는 OFF→ON 방향만 단언.
- **수정 방향**: 소유권 노드 이전 시 노멀라이저 게인 즉시 1.0 스냅.

### [P2-1] `SetNormalizer`가 게이트 없이 트랙 RG를 기입 — 갭리스 경계와 교차 시 이전 트랙 RG가 신곡 전체에 적용 (d4d3a77이 노드에 대해서만 수리한 잔여)
- **위치**: `SequencerStream.cs:473-486`(게이트 없음) vs `:396-409`(노드는 `_gate` 하)
- **수정 방향**: `staticReplayGainLinear` 기입을 `_gate` 하에서 실행.

### [P2-2] 컨볼루션 IR 샘플레이트 불일치 미처리 — 리샘플·경고 없이 그대로 적용
- **위치**: `ImpulseResponse.cs:10-39`(레이트 정보 폐기), `ConvolutionDspEffect.cs:64-81`, `AppSettings.cs:428-439`
- **시나리오**: 44.1kHz IR + 48kHz 재생 → 필터 특성 전체가 약 8.8% 낮게 이동, 무경고.
- **수정 방향**: IR 레이트 반환·비교·리샘플 또는 경고.

### [P3-1] IR 라이브 스왑 순간 512프레임 드라이 통과 + 잔향 꼬리 절단 — `ConvolutionDspEffect.cs:274-282`
### [P3-2] IR 2초 초과 시 무경고 절단 — `ConvolutionDspEffect.cs:18-19, 72-73`
### [P3-3] `ClampFrequency`가 sampleRate ≤ 40Hz에서 ArgumentException 가능(이론상) — `BiquadDesign.cs:42-43`
### [P3-4] 세션 시작 스레드의 프로필 결정이 UI의 Profiles 사전 재구조와 경합(빈도 낮음) — `Equalizer.cs:26-63` + `OutputDrivers.cs:98`
### [P3-5] 크로스피드 강도 변경 시 LP 상태 폐기 미세 스텝(P1-1 동일 클래스, 영향 미약) — `CrossfeedDspEffect.cs:64-72`
### [P3-6] `SetImpulse`(스레드풀)와 `Initialize` 경합 시 구레이트 IR 채택 가능(빈도 매우 낮음) — `ConvolutionDspEffect.cs:72` vs `:131`

### 무결 판정(DSP)
라이브 파라미터 원자성(불변 스냅샷+Volatile, 부분 읽기 없음), 바이쿼드 계수 경계(클램프+Identity 폴백), FFT 분할 컨볼루션 수학(prevAcc 폴딩·파티션 불변식·모노 IR 전채널), DSP 체인 순서 불변식(RG 전단+페이더 후단이 전 경로 유지, DoP 일관 우회), AGC 역학(프레임률 수리 확인·하이브리드 전환 연속·시크/트랙 전환 구분), 소프트 리미터(±1 상계·비트 정확 통과), AutoEq 파서(malformed 무크래시·JSON 무손실), RG 태그 파싱/R128 폴백/무음 처리.

---

## 4. 플레이리스트 / 재생 큐 / 언두 / 검색

### [P0-1] 이름 변경 시 중복 이름 검사 없음 → 기존 플레이리스트 파일 덮어쓰기(내용 영구 유실)
- **위치**: `PlaylistManager.cs:589-629`(RenamePlaylist), `PlaylistDialogs.cs:38-41`
- **근거**: 생성 경로 `CreatePlaylistLocked`(422-429행)는 `UniqueNameLocked`으로 동일 실패를 명시적으로 막고 있으나(주석에 같은 실패 모드 기술) 이름 변경은 무방비. 새 이름 .m3u8을 즉시 쓴다. 종료 시 `SaveAll`이 두 플레이리스트를 같은 파일에 순차 기록 → 한쪽 영구 유실.
- **시나리오**: "A"를 "B"로 이름 변경(B 존재) → B.m3u8이 A 내용으로 덮어써짐 → 다음 실행 시 한 개만 로드. 살균화 충돌("A/B"→"A_B")과 "Now Playing"으로의 변경도 동일 경로.
- **수정 방향**: RenamePlaylist에 생성과 동일한 충돌 검사 + 다이얼로그 사전 검증.

### [P1-1] 스마트 플레이리스트 자동 갱신이 큐 항목을 무조건 소거 (보고 큐 ≠ 실제 큐)
- **위치**: `PlaylistManager.cs:830-843`(ReplaceWithTracks — 내용 동일해도 전체 인스턴스 교체), `AppServices.cs:182`(큐 퍼지), `:341`/`:414`/`:276`(갱신 트리거)
- **시나리오**: "많이 재생"에서 곡 2개 "큐에 추가"(Q 배지) → 현재 곡 종료 순간 갱신 → 배지 소멸, 큐 비어 있음.
- **수정 방향**: 동일 Track 항목 인스턴스 재사용(또는 실제로 사라진 항목만 퍼지).

### [P1-2] 스마트 플레이리스트 순차 재생 시 1·2번 트랙 무한 진동
- **위치**: `PlayOrderResolver.cs:108`(IndexOf → -1), `:143-147`; 원인은 P1-1의 인스턴스 교체
- **시나리오**: 스마트 목록 5번 트랙 더블클릭 → 5→6 재생 후 목록 머리 두 곡만 `0↔1` 반복, 나머지 미재생, 반복 off여도 종료 안 됨.
- **수정 방향**: P1-1 수정 또는 CurrentItem 미발견 시 Track.Path 폴백.

### [P1-3] M3U8 가져오기가 라디오/원격(URL) 항목을 전부 버림 (내보내기→가져오기 왕복 실패)
- **위치**: `PlaylistManager.cs:767, 784`(`File.Exists` 필터), 대조적으로 자동 로드 `:1185-1191`은 `#DPTRACK`/`IsStreamUrl` 복원
- **시나리오**: 라디오 포함 재생목록 내보내기 → 다시 가져오기 → 라디오만 빠진 채 로드. 백업 복원 시 원격 항목 유실.
- **수정 방향**: 필터에 `IsStreamUrl` 추가 + `#DPTRACK` 디코딩 공유.

### [P2-1] 언두/리두가 재생 큐 퍼지를 우회 (직접 삭제와 불일치)
- **위치**: `PlaylistUndoManager.cs:122-125, 157-160`(ReplaceAll만), 대조 `PlaylistManager.cs:806-815`
- **시나리오**: "큐에 추가" 후 해당 추가작업 Ctrl+Z → 목록에서 사라졌지만 큐 팝업에 잔존, 실제 재생됨.
- **수정 방향**: 언두/리두 diff 계산 후 ItemsRemoved 동일 퍼지 경로.

### [P2-2] 플랫 모드 드래그 재정렬이 언두 이력에 기록되지 않음 (MoveItem은 죽은 코드)
- **위치**: `PlaylistPage.xaml.cs:640`, `PlaylistManager.cs:944-960`(호출자 없음)
- **시나리오**: 드래그 후 Ctrl+Z → 드래그는 안 되돌려지고 이전 스냅숏 시점으로 전체 되감김.
- **수정 방향**: `DragItemsCompleted`에서 스냅숏 기록 경로 연결.

### [P2-3] 플레이리스트 내 검색 매치 인덱스가 변형 후 무효화되지 않음 (v1.8 "search origin" 수리의 잔여)
- **위치**: `PlaylistPage.xaml.cs:903`(`_searchMatches`), `:947-967`(텍스트 변경 시에만 갱신), `:213-230`(탭 전환 리셋 없음)
- **시나리오**: 검색 중 매치 위 행 삭제 → Enter가 엉뚱한 곡으로; 탭 전환 후 F3이 이전 플레이리스트 인덱스로 새 목록 탐색.
- **수정 방향**: OnItemsChanged/사이드바 전환 시 재검색 또는 매치를 항목 참조로 보관.

### [P2-4] 스마트 플레이리스트를 보고 있을 때 트랙이 넘어갈 때마다 목록 Reset (스크롤·선택 유실)
- **위치**: `PlaylistManager.cs:839`(불변시에도 ReplaceAll), `PlaylistPage.xaml.cs:613-649`
- **시나리오**: "많이 재생" 스크롤 탐색 중 곡 하나 넘어가면 목록 머리로 튕기고 선택 풀림. 1만 트랙에서 매 경계 O(N) 재구성.
- **수정 방향**: no-change 판정으로 교체 건너뛰기(P1-1과 동일 지점).

### [P2-5] 스마트 쿼리 파서의 무제한 재귀 — 깊은 괄호에서 StackOverflow로 프로세스 크래시 ※실질 P1 권장
- **위치**: `SmartPlaylistQuery.cs:268-277`, 진입 `PlaylistDialogs.cs:132-142`(UI 스레드 즉시 파싱)
- **시나리오**: `"((((…))))"` 수천 개 붙여넣기 → 잡을 수 없는 StackOverflowException → 앱 즉시 종료.
- **수정 방향**: 파서 깊이 상한(예: 64) + 일반 오류로 보고.

### [P3-1] 미해결 경로가 저장 시 항상 파일 끝으로 이동(원래 순서 유실) — `M3u.cs:186-193`
### [P3-2] `DURING LAST n MONTHS` 곱셈 오버플로 → 조용히 무매치 — `SmartPlaylistQuery.cs:333`. long 연산+상한.
### [P3-3] 쿼리 문자열 값에 따옴표 이스케이프 없음(문서화 안 됨) — `SmartPlaylistQuery.cs:122-134`
### [P3-4] `SaveAll`이 잠금 없이 타이머 Dispose — 주석과 달리 새 타이머 미생성(수백 ms 유실 창) — `PlaylistManager.cs:1118-1121` vs `:1040-1044`

### 무결 판정(플레이리스트)
언두×스마트 갱신 가드(IsSmart — 테스트 단언), 큐 자체 인덱스 불변식(단일 잠금·버전 단조·1000항목 스트레스), M3U8 파일 계층(인코딴 폴백 체계·상대경로·원자적 쓰기), 쿼리 파서 나머지(빈 쿼리/LIMIT/NULL 비교), 셔플·반복·이력 우선순위, A-B 리핏(샘플 단위·라이브 거부), 자동저장(`_loading` 가드·잠금 순서), STA/MTA 마샬링(CollectionSnapshot·RunOnUiAsync), 그룹 빌더 O(N) 1회 패스.

---

## 5. 라이브러리 / 통계 / 평점 / 태그 편집

### [P1-1] 태그 편집기 재반입 목록에 Rating이 없어 DB 평점이 소실됨
- **위치**: `TagEditorDialog.cs:361-367`(Apply — `fresh with { PlayCount, SkipCount, LastPlayedUtcTicks, FirstSeenUtcTicks }`만 운반), 대조 `MusicLibrary.cs:445-455`(스캔은 Rating carry-forward)
- **시나리오**: ① 읽기 전용 파일에 5★ 부여(DB=5, 태그=없음 — 앱이 "라이브러리에만 저장" 안내) ② 쓰기 가능하게 한 뒤 제목만 태그 편집 → 재읽은 Rating=0이 DB·메모리에 기록 → **앱이 보관한다고 약속한 평점이 자동 소실.**
- **수정 방향**: `Rating = original.Rating` 운반.

### [P2-1] 스캔 중 태그 편집하면 스캔 종료 시 편집 내용이 메모리에서 되돌려짐 (메모리≠DB)
- **위치**: `MusicLibrary.cs:271`(스냅숏), `:384-405`(캐시 경로), `:508-510`(SwapTracks), `:836-848`(ReplaceTracks)
- **시나리오**: 대형 재스캔 중 태그 저장 → 화면 갱신됐다가 스캔 종료 순간 옛 태그로 복귀, 다음 스캔까지 DB와 불일치.
- **수정 방향**: SwapTracks 병합(최신 인스턴스 우선) 또는 세대 카운터.

### [P2-2] 스캔 중 재생 통계/평점 쓰기가 재읽기 파일에서 경합해 DB 값이 되돌려짐
- **위치**: `MusicLibrary.cs:429-467`(worker의 previous 필드 읽기), `AppServices.cs:323`/`:372`(인스턴스 직접 변경)
- **시나리오**: 재스캔 중 방금 들은 트랙의 mtime이 변해 재읽기 대상이면 직전 UpdateStats/UpdateRating 되돌림(창은 좁으나 대형 스캔 수 분 내내 개방).
- **수정 방향**: upsert의 stats/rating 열 부분 갱신 전환(`ON CONFLICT DO UPDATE`에서 해당 열 유지) 또는 세대 스탬프.

### [P2-3] 저장된 "평점 정렬"이 재시작 시 "길이 정렬"로 바뀜 — 클램프 상한 5 미갱신
- **위치**: `LibraryPage.xaml.cs:196`(`Clamp(...,0,5)`), `LibraryFilterService.cs:20`(`Rating = 6`)
- **수정 방향**: 상한 `(int)SortColumn.Rating`.

### [P2-4] 태그 편집기 저장 후 해당 트랙 ArtPath가 NULL이 되어 앨범 커버가 사라짐
- **위치**: `TagEditorDialog.cs:354-367`, `TagReader.cs:118-145`(ArtPath는 스캔에서만 설정), `LibraryFilterService.cs:102-109`
- **시나리오**: 앨범 한 곡 제목 수정 → 그 트랙이 first면 커버 그리드에서 커버 소실(재스캔까지). 새 아트 임베드도 반영 안 됨.
- **수정 방향**: Apply에서 아트 재추출/폴더 아트로 ArtPath 재결정.

### [P2-5] 스캔 실패(비취소 예외) 시 진행바와 "다시 스캔" 버튼이 영구 고정
- **위치**: `LibraryPage.xaml.cs:336-358`(`Finished` 분기에서만 종료 처리), `AppServices.cs:553-563`
- **시나리오**: 디스크 가득 상태 재스캔 → 경고는 뜨지만 진행바·버튼이 재시작 전까지 잠김.
- **수정 방향**: catch/finally에서 무조건 종료 이벤트 발화.

### [P2-6] CUE 가상 트랙의 태그 편집은 항상 실패 (물리 경로 미변환)
- **위치**: `TagEditorDialog.cs:345`(fragment 경로 그대로 `TagWriter.TryApplyAtomic`), 대조 `RatingCommands.TagWritePath`는 물리 경로 사용
- **수정 방향**: 진입 시 `AppPaths.PhysicalPath` 변환(+공유 물리 파일 안내).

### [P2-7] 단일 트랙 편집 "빈 칸은 그대로 둡니다" 안내가 실제 동작과 반대 (텍스트 필드는 빈 칸=삭제)
- **위치**: `TagEditorDialog.cs:143`(문구), `:287-299`(`?? ""`), `TagWriter.cs:236-247`
- **시나리오**: 안내를 믿고 제목 칸 비워 저장 → 태그 삭제 + 파일명이 제목으로 표시.
- **수정 방향**: 문구 수정 또는 공란=유지 + 별도 "지우기" UX.

### [P2-8] 이미 인덱싱된 파일은 외부 태그 평점(POPM/RATING) 변경이 영구 무시됨 (양방향 동기 깨짐)
- **위치**: `MusicLibrary.cs:445-455`(기존 행 존재 시 무조건 `Rating = previous.Rating`)
- **시나리오**: 타 앱에서 별점 1→5 변경 → 재스캔 → 여전히 1★.
- **수정 방향**: 태그 평점 변화 감지형 병합 규칙.

### [P3-1] "올해" 기간 경계가 UTC — KST 새해 00:00–08:59 누락/편입; "최근 30일"도 롤링 30×24h — `AppServices.cs:426-432`
### [P3-2] 아트·블러 캐시 무한 증가 + 스테일 재사용(아트 교체 반영 안 됨) / 팔레트 캐시 테마 미구분 — `AlbumArtService.cs:57-60`, `AlbumArtBlurHelper.cs:41-44`, `AlbumArtColorExtractor.cs:47-51`
### [P3-3] 같은 파일 동시 태그 쓰기가 결정적 임시파일 이름(`.dawn-tmp`)으로 충돌 — `TagWriter.cs:44-48`(원본은 File.Replace로 보존). GUID 임시명 또는 파일별 락.
### [P3-4] ASF(WMA) 컨테이너 평점 태그 쓰기가 조용히 스킵되는데 성공 보고(보고≠실제) — `TagWriter.cs:197-220`(Asf 분기 부재)
### [P3-5] 태그 편집기 저장+재독기가 UI 스레드 동기 실행 — 앨범 배치 수 초 프리즈 — `TagEditorDialog.cs:338-368`

### 무결 판정(라이브러리)
SQLite 구조(단일 연결+전면 직렬화+WAL, 부분 커밋 없음), 증분 스캔 prune 안전장치(오프라인 루트·케이스 키·CUE 전이), POPM 스케일 왕복(1/64/128/196/255), 원자적 태그 저장(동일 볼륨 temp+File.Replace), 재생 통계 이중 카운트 방어(수동/자연 상호배타·크로스페이드 경계 정확·라디오 무오염), 청취 리포트 빌더(결정적·미해결 제외 문서화), FilterRequestGate(세대 토큰·디바운스), 평점 배치(중복 병합·불변 스킵·실패 수량 알림), 트리 빌더·커버그리드 크기 지속화, 아트 캐시 동시성(AtomicFile+재시도).

---

## 6. 트랜스코더 / 파일 오거나이저 / Convert to WAV

### [P2-1] ReplayGain "굽기" 변환 후 원본 RG 태그를 그대로 다시 기록 → 이중 적용
- **위치**: `AudioTranscoder.cs:410-415`(ApplyMetadataAndArt — 굽기 여부와 무관하게 원본 RG 태그 재기록), 재생 측 `PlaybackController.cs:1509-1513`
- **시나리오**: "ReplayGain 음량 굽기" ON + FLAC→MP3 → 결과 파일 재생 시 게인 2회 적용(-8dB 트랙이 -16dB).
- **수정 방향**: 굽기 시 RG 태그 미기록 또는 0dB 리셋.

### [P2-2] ffmpeg 변환 취소 시 반쪽 출력 파일이 최종 이름으로 남음 (비동기 Kill 직후 delete)
- **위치**: `AudioTranscoder.cs:312-316`, `:370-374`, `:469-473`(TryDelete 실패 Trace만), 출력은 처음부터 최종 파일명
- **시나리오**: 배치 취소 → 잘린 "…2.flac" 잔여 → 재변환 시 `SuggestUnique`가 이를 정상 파일로 간주해 " 3" 부여 → 잘린 파일이 영구히 남아 오인·임포트.
- **수정 방향**: kill 후 `WaitForExit(timeout)` 후 삭제, 또는 `.tmp` 기록 후 성공 시 개명.

### [P2-3] 오거나이저 실행 단계가 충돌 정책을 재검사하지 않고 무조건 `overwrite:true` (TOCTOU + 재진입 무가드)
- **위치**: `FileOperationService.cs:218`, `FileOperationsDialog.cs:220`(fire-and-forget, 재진입 잠금 없음)
- **시나리오**: AutoRename 정책으로 정리 실행 → 완료 토스트 확인 전 같은 선택으로 재실행 → 두 배치가 같은 대상에 수렴 시 약속된 자동 번호 대신 덮어쓰기.
- **수정 방향**: 실행 직전 대상 재확인+정책 재적용, 실행 중 재진입 잠금.

### [P2-4] Overwrite 정책에서 "선행 계획의 대상 == 후행 계획의 원본" 체인 미검사 → 원본 데이터 소멸
- **위치**: `FileOperationService.cs:66-145`(원본 경로 미추적), 실행 루프 `:187-236`
- **시나리오**: 제자리 재편집 + Overwrite에서 A의 대상이 B의 원본과 일치 → `File.Move(A.src, B.src, overwrite:true)`로 B 원본 파괴 → B의 DB 행은 B 메타데이터 + A 오디오로 오염(전제 조건 좁음).
- **수정 방향**: 계획 단계에서 배치 내 원본 경로 집합과 대상 충돌 검사.

### [P2-5] 편집형 패턴 콤보박스에 직접 타이핑한 패턴이 미리보기·실행에 반영 안 됨
- **위치**: `FileOperationsDialog.cs:190`(SelectionChanged만 연결 — WinUI 3 편의 콤보박스는 타이핑이 TextSubmitted로만 전달), `:213-216`
- **시나리오**: `%year% - %title%` 직접 입력 후 실행 → 이전 프리셋으로 실제 이동·개명.
- **수정 방향**: TextSubmitted 연결 또는 실행 직전 RefreshPreview.

### [P2-6] 배치 실행 결과의 상세 오류(DB·플레이리스트 동기화 실패 포함)가 UI에 전혀 표시되지 않음
- **위치**: `FileOperationsDialog.cs:224-233`(result.Errors 폐기), 오류 생성 `FileOperationService.cs:241-279`
- **시나리오**: 이동은 성공, `RelocateTracks` 실패 → "N개 성공, 0개 실패" 토스트 → 재시작 시 DB가 구 경로.
- **수정 방향**: Errors 카운트·요약 토스트 노출.

### [P3-1] ffmpeg stderr 종료 후 드레인 + `WaitForExit()` 무한 대기 → 취소 무응답 교착 가능(정상 조건에선 완화됨) — `AudioTranscoder.cs:288-296`, `:350-353`. 비동기 stderr 리더+타임아웃 루프.
### [P3-2] WAV "32-bit Float" 프리셋이 실제로는 32비트 정수 PCM 기록(라벨 계약 위반) — `TranscodeDialog.cs:73`, `AudioTranscoder.cs:206-212`, `:492`
### [P3-3] 태그/아트워크 복사 실패가 결과에 반영 안 됨(경고 로그만) — `AudioTranscoder.cs:396-416`
### [P3-4] `RelocateTracks`가 play_events 경로 미갱신 → 청취 통계 귀실 — `MusicLibrary.cs:853-882`, 소비측 `ListeningReport.cs:44-60`. 같은 트랜잭션에서 `UPDATE play_events SET path=…`.
### [P3-5] 4GiB 초과 WAV에서 RIFF 크기 필드 int 오버플로(RF64 미지원) — `AudioTranscoder.cs:488`, `:499`
### [P3-6] case-only 개명 2단계 이동 중간 실패 시 `.dawn_case_tmp_<guid>` 방치 — `FileOperationService.cs:210-212`. catch에서 롤백 시도.

### 무결 판정(트랜스코더)
s24 FLAC 파이프(3바이트 LE ↔ float32 가수 정확 대응, 테스트 단언), CUE 이미지 분할(범위 컷·진행률), ffmpeg 좀비 프로세스 없음(finally 전 경로 Kill+Dispose), WAV 경로 취소 정리, **우클릭→변환/정리 진입점 3곳 모두 수리 확인**(과거 "조용한 no-op" 결함 재발 없음), v1.8 수리 2건(Overwrite 배치 수렴 봉쇄·플레이리스트 동기화) 정상, DB·플레이리스트 원자성, 패턴 포매터 방어(트래버설·예약 장치명·무효 문자), ffmpeg 인자 quoting·유니코드, 경로 260 초과 개별 실패 처리.

---

## 7. 네트워크 스트리밍 (인터넷 라디오 / DLNA / YouTube)

### [P1-1] 무한(라디오) 트랙 재생 중 프리페치가 다음 트랙을 실제로 열어, 라디오가 다음이면 메모리가 무한 증가
- **위치**: `PlaybackController.cs:1525-1532`(PollPrefetch — 라디오 `RemainingTime ≤ 0`이라 항상 통과), `RadioStreamReader.cs:121-138`(백프레셔 없음), `BufferedPcm.cs:38-63`, `PlayOrderResolver.cs:96-98`
- **시나리오**: [라디오A, 라디오B] 또는 라디오+반복1 → A 재생 수 초 내 B(또는 A 자신 — Icecast 다중 접속 차단 시 재생 스트림 자체 끊김 가능)를 연결·디코드 → 아무도 안 읽는 버퍼에 약 10MB/분 → 몇 시간 청음 시 수 GB. 다음이 YouTube면 yt-dlp+ffmpeg가 상시 구동, DLNA면 파일 전체 스풀.
- **수정 방향**: 현재 트랙 `TotalTime == 0`이면 프리페치 스킵 + 라디오 리더 백프레셔 상한. (한 번의 수정으로 세 경로 동시 해결)

### [P2-1] 라디오 무활동 스톨(반개방 TCP) 감지 타임아웃 부재 — "버퍼링"만 영구 표시, 끊김 경고도 없음
- **위치**: `RadioStreamReader.cs:46, 77-155`, `StreamStallTracker.cs`, `NowPlayingBar.xaml.cs:581-592`
- **근거**: `HttpClient.Timeout=15s`는 헤더까지만 적용. 서버가 FIN 없이 침묵하면 판독이 영구 블록, `StreamDied` 미발화 — "설명 없는 무음 방지" 기능 서술의 정확히 반대 방향. 사용자가 빠져나가도 블록된 스레드·연결 잔존.
- **수정 방향**: 무활동 감시(N초 0바이트면 종결 처리) 또는 읽기 취소 타임아웃.

### [P2-2] HttpProgressiveTrackReader(DLNA): 본문 수신 정지 시 열기가 영구 행, 취소 불가 구멍 + 스풀 크기 무제한
- **위치**: `HttpProgressiveTrackReader.cs:18, 53-61, 83-93`, `PlaybackController.cs:299-304`(Open 반환 전엔 Dispose 불가)
- **수정 방향**: 본문 무활동 타임아웃 CTS + 최대 스풀 상한.

### [P2-3] YouTubeSection 경로 플라이아웃의 TryProbe 동기 호출이 UI 스레드를 최대 ~10초 블록
- **위치**: `YouTubeSection.xaml.cs:296-307`, `:348`, `YouTubeProcessRunner.cs:213-240`(WaitForExit(5000) × 2 바이너리)
- **시나리오**: 오동작 exe 오버라이드 상태에서 플라이아웃 열기 → UI "응답 없음" 5–10초.
- **수정 방향**: Task.Run+완료 콜백(배지 패치와 동일).

### [P3-1] ICY 종료 `';` 없는 메타데이터에서 NUL 패딩이 제목에 포함 — `RadioTrack.cs` ParseStreamTitle. `'\0'` 절단.
### [P3-2] RadioStreamReader가 AcmMp3FrameDecompressor를 Dispose하지 않음(방송국 전환 반복 시 네이티브 핸들 지연 해제) — `RadioStreamReader.cs:112, 165-171`
### [P3-3] DLNA 서버 전환 후 첫 브라우즈 실패 시 이전 서버 항목이 잔존(표시≠탐색 대상) — `DlnaSection.xaml.cs:172-178, 205-209, 242-248`
### [P3-4] 루트 없는 XML(주석만)에서 DeviceDescriptionParser NRE(호출부가 삼켜 사용자 영향 없음, 계약 위반) — `DlnaDeviceDescriptionParser.cs:45`
### [P3-5] 자연 종료 직후 ffmpeg 퇴장이 2초 초과 시 허위 "조기 종료" 경고 — `YouTubeProcessRunner.cs:340-346`

### 무결 판정(네트워크)
ICY 메타데이터 파싱(청크 경계·UTF-8 조립 후 단일 디코드·StreamUrl 무시), 무한 스트림 위치 불변식(시퀀서 클램프·시크 no-op·A-B 거부·SMTC·UI 일관), yt-dlp/ffmpeg 수명·인자(ArgumentList+URL 정규화로 주입 불가·세대 CAS 게이트·stderr 상시 배수·Kill 트리), SSDP(CTS 타임아웃·UDN 중복 제거·예외 미누출), DIDL(XML 기본 DTD 금지·로컬네임 매칭·포맷 우선순위), 아트 캐시(매직 검증·64MB LRU), 저장소(원자적 쓰기·Normalize), UI 스레딩(열기 전부 Task.Run·라이브 이벤트 RunOnUi 릴레이), HttpClient FIN 추월 패턴은 아웃바운드 스트리밍에 해당 없음.

---

## 8. 가사 (LRC 파서·탐색·온라인 플러그인·에디터)

### [P1-1] ANSI(EUC-KR/CP949) 한국어 .lrc가 전부 깨져 보임 — "ANSI 자동 감지"가 실제로는 구현되지 않음
- **위치**: `LrcParser.cs:243-251`(DecodeBytes — 실패 시 `Encoding.Default`=UTF-8 폴백뿐, CP949 시도 없음. `CodePagesEncodingProvider`는 M3u에만 등록)
- **시나리오**: 오래된 알송/메모장 ANSI 한국어 .lrc → "�"와 엉뚱한 한자 혼재 표시. README "UTF-8/ANSI 자동 감지" 명시 위반.
- **수정 방향**: CP949 후보 디코드 + 한글 음절 비율/U+FFFD 개수 비교 채택. 테스트 갭: ANSI 케이스 전무.
- **※ 참고**: CUE(2영역 P1-5)도 동일한 `Encoding.Default` 오류 패턴 — 함께 수리 권장.

### [P2-1] 검색 창 트랙 전환 경쟁 — 이전 트랙 검색 결과가 새 트랙 목록에 섞여 Apply/저장 대상이 어긋남
- **위치**: `LyricsSearchWindow.xaml.cs:175, 181-198`(generation 변수를 만들고 미사용 — 미리보기 쪽만 사용)
- **시나리오**: 트랙 A 검색 중 트랙 B로 재타깃 → A 결과가 B 창에 늦게 추가 → A 가사를 B에 Apply/내장.
- **수정 방향**: 결과 적용 continuation에 세대/트랙 정합성 검사.

### [P2-2] 플러그인 호출에 타임아웃/행 방어 없음 — 떠 있는 플러그인이 검색 창을 영구 잠금
- **위치**: `OnlineLyricsService.cs:75-126`, `LyricsSearchWindow.xaml.cs:172-174`
- **근거**: 예외는 완전 격리되나(검증됨) 무한 블로킹 플러그인(프로세스 내 ALC 로드)은 `Task.WhenAll`이 영원히 미완료 — "검색 중…"+버튼 비활성 고정, 취소도 무시. 번들 2종은 자체 HttpClient.Timeout으로 방어.
- **수정 방향**: `Task.WhenAny(work, Task.Delay(timeout, token))` 래핑.

### [P2-3] 편집기 오프셋 극값 입력 → `TimeSpan.FromMilliseconds` OverflowException으로 핸들러 중단 + 표시 총 오프셋 ≠ 줄 실제 시각
- **위치**: `LyricsEditorWindow.xaml.cs:319-329`, `:269-276`, `:339-349`(클램프 헬퍼 `StepOffset`가 프로덕션 미사용)
- **수정 방향**: StepOffset 클램프 적용.

### [P2-4] "태그에 내장"이 UI 스레드에서 전체 오디오 파일 복사+재작성 동기 수행 — 대용량 파일 UI 멈춤
- **위치**: `LyricsSearchWindow.xaml.cs:304` → `TagWriter.TryApplyAtomic`(원자성 자체는 확인)
- **수정 방향**: Task.Run 오프로드.

### [P3-1] 내용 없는 .lrc(0바이트/메타불만)가 내장 가사 폴백을 차단(자동 온라인 검색 쪽과 비대칭) — `LyricsFinder.cs:26-35`
### [P3-2] 동일 타임스탬프 줄 상대 순서 비결정적(불안정 Sort) — `LrcParser.cs:146`. 인덱스 타이브레이커.
### [P3-3] 편집기 시각 셀 음수 분 입력 → `[00:-30.000]` 저장으로 재파싱 실패 — `LyricsEditorWindow.xaml.cs:81-97`
### [P3-4] 기본 패턴 `%title%.lrc`가 같은 폴더 동명 곡(다른 아티스트)에 오매칭 — `AppSettings.cs:162-167`
### [P3-5] .lrc 저장이 비원자적(File.WriteAllText) — 크래시 시 기존 큐레이션 소실 위험(태그 경로는 원자적 — 비대칭) — `LrcParser.cs:185-194`
### [P3-6] 트랙 변경 시 가사 로드(파일 탐색+전체 읽기+TagLib open)가 UI 스레드 동기 — 네트워크 경로에서 수백 ms 프리즈 — `LyricsPane.xaml.cs:137`

### 무결 판정(가사)
자동 검색 경쟁(세대 카운터+CTS+경로 재검사+RunOnUi — 앱 서비스 쪽은 방어됨), 플러그인 예외 격리(로드·인스턴스화·호출 전부), 오프셋 부호 일관성(오프셋을 타임스탬프에 굽는 구조라 부호 어긋남 불가능), LRC 파서 경계(음수·60초 초과·역순·offset 누적·단어 태그·CRLF/BOM — 전부 테스트 단언), 저장 경로(트래버설 차단·읽기 전용 Fail 보고·태그 내장 원자 교체), 스크롤 동기(가상화 폴백·수동 스크롤 억제 문서화·라이브 소스 빈 상태 안전).

---

## 9. UI 통합 (단축키·SMTC·트레이·미니·풀스크린·슬립 타이머·Last.fm·설정·세션)

### [P1-1] 트레이 "종료"/미니 메뉴 "종료" 경로에서 종료 절차(세션·창 배치 저장 전체)가 실행되지 않는다
- **위치**: `MainWindow.xaml.cs:137-159, 221-234`(CloseFromTray가 `_closing=true` 후 Close — Closing/Closed 핸들러 모두 `_closing`이면 조기 반환), `TrayIconService.cs:321`
- **근거**: `ShutdownForReal()`(그 안의 `SessionManager.SaveSession`, `WindowPlacementHelper.SavePlacement`, `AppServices.Shutdown`(SaveAll·FlushNow·Playback.Dispose), `Log.Shutdown`)을 호출하는 살아있는 경로는 X 버튼(CloseToTray 꺼짐)과 타이틀 메뉴뿐. v1.0.0부터 존재.
- **시나리오**: "트레이로 닫기" ON 사용자(트레이 사용자의 유일한 종료 경로)가 트레이 → 종료 → 창만 닫힘 → 재시작 시 대기열·재생 위치·창 배치가 **이전 정상 종료 시점의 낡은 값**으로 복원, 종료 직전 변경 유실.
- **수정 방향**: 숨김 분기와 실제 종료를 분리하는 플래그 재구성.

### [P1-2] 언어 변경 재시작(RestartApp)이 "트레이로 닫기" 켜짐 상태에서 이중 인스턴스를 만든다
- **위치**: `AppServices.cs:521-538` + `MainWindow.xaml.cs:150-155`(새 프로세스 먼저 → 기존 Close가 트레이 숨김으로 취소됨; 단일 인스턴스 뮤텍스 없음)
- **시나리오**: 트레이 사용자가 언어 변경 + "지금 다시 시작" → 트레이 아이콘 2개, WASAPI 엔진 2개, 동시 출력 가능, 이전 인스턴스는 재부팅 전까지 잔존.
- **수정 방향**: 종료 의도 명시(래치) 또는 단일 인스턴스 뮤텍스.

### [P2-1] 슬립 타이머: "현재 곡 끝나고" → "15/30/60분" 전환 시 StopAfterCurrent 플래그 잔류
- **위치**: `SleepTimerService.cs:59-98`(default 분기가 StopAfterCurrent 환원 안 함)
- **시나리오**: "현재 곡 끝나고" 후 "30분"으로 변경 → 메뉴는 30분 카운트다운 표시 but 실제로는 현재 곡 끝나면 정지(보고≠동작).
- **수정 방향**: Set 진입부에서 이전 옵션 AfterCurrentTrack이면 플래그 환원.

### [P2-2] 풀스크린 Now Playing 창이 사용자 테마 설정을 따르지 않는다
- **위치**: `FullscreenNowPlayingWindow.xaml.cs:40-91`(ApplyTheme 미호출), `ThemeService.cs:30-51`(보조창 등록이 가사 창 2종뿐)
- **시나리오**: OS 라이트 + 앱 다크 → 풀스크린 창만 라이트 팔레트. 실행 중 테마 변경도 미갱신.
- **수정 방향**: ApplyTheme + RegisterAuxiliaryWindow.

### [P3-1] 시스템 예약 키(Win+·Alt+F4) 캡처·저장 가능하지만 절대 발동 안 함 + 풀스크린 Space/←/→ 하드코딩이 재바인딩 무시 — `ShortcutCaptureDialog.cs:69-84`, `FullscreenNowPlayingWindow.xaml.cs:136-159`
### [P3-2] 세션 저장: 위치가 정확히 0이면 저장 스킵 → 직전 세션의 위치가 다른 트랙에 적용 — `SessionManager.cs:88-95`
### [P3-3] 세션 복원에서 라디오(URL) 대기열 항목 조용히 드롭(저장은 함 — 비대칭) — `SessionManager.cs:35-37`
### [P3-4] SettingsWriter.Flush 동시 실행 순서 미보장 — 오래된 스냅샷이 최신 쓰기 덮어쓰기 가능(마지막 한 세대 롤백) — `SettingsWriter.cs:76-94`
### [P3-5] 트레이 아이콘 생성 실패 후 재시도 경로 — 이벤트 구독 중복 + HICON 핸들 누수(정상 경로는 무결) — `TrayIconService.cs:53-92, 229-260`
### [P3-6] ScrobbleService.StateChanged가 스레드풀에서 발화(주석은 UI 스레드 주장; 현재 구독자 없음) — `ScrobbleService.cs:168`
### [P3-7] SMTC UpdateTimeline이 no-op 빈 몸통 — OS 오버레이/잠금화면 진행 표시 없음(죽은 호출 다수) — `SmtcService.cs:290-294`
### [P3-8] 드래그&드롭: 접근 불가 서브폴더 하나로 폴더 전체가 조용히 실패("0개 추가") — `PlaylistManager.cs:631-656` + `MainWindow.xaml.cs:1107-1141`. 서브디렉터리 단위 열거로.
### [P3-9] (의심·검증 필요) "트레이로 닫기" ON에서 OS 종료/로그오프가 cancel로 차단될 가능성 — `MainWindow.xaml.cs:143-155`. WM_QUERYENDSESSION 서브클래싱으로 확인 필요.

### 무결 판정(UI)
단축키 충돌 감지 완전성(다중 모디파이어 정규화·병합 결정론적 — 53테스트), 과거 수리 4건 재발 없음(Follow System 크래시·미니 DPI/캡션·풀스크린 히트 테스트·NowPlayingBar 초기화 순서), SMTC 미디어 키 스레딩+메타데이터 버전 가드(53+ 스트레스), 스크로블 이중 카운트 없음·큐 50개/13일 상한, settings.json 원자성/복구(AtomicFile+.bak), 창 배치 가상 화면 클램프·DPI 왕복, 트레이 TaskbarCreated 복구·Focus Assist 토스트, QueuePopup·Seekbar 계산기·NotificationPresenter 순수 로직.

---

## 우선순위 수정 제안 (승인 후 순차 진행)

1. **P0 2건** — 크로스페이드 Dispose 경쟁(크래시), 플레이리스트 이름 변경 덮어쓰기(데이터 유실). + 쿼리 파서 StackOverflow(실질 P1).
2. **데이터 오염류** — 앨범 RG 피크 0 기록, 태그 편집 평점 소실, RG 굽기 이중 적용, DFF 스펙(기능 전면 불능), ANSI 인코딩(.lrc + CUE 공통 패턴).
3. **사용자 가시 결함** — 스마트 플레이리스트 진동/큐 소거, 트레이 종료 저장 생략, 재시작 이중 인스턴스, 라디오 프리페치 무한 메모리, EQ z-상태 팝, 노멀라이저 토글 RG², DoP×DS/WaveOut 노이즈.
4. **P2 잔여 → P3** 순서로 진행.

각 건의 실패 시나리오 재현 + 수리 후 클린 리빌드(AGENTS.md §5)와 영역별 필터 테스트, 마일스톤 시점 전체 스위트 1회로 검증한다.
