# Dawn Player 가사 플러그인 개발 가이드

Dawn Player는 .NET DLL 기반의 가사 플러그인을 지원합니다. 특정 사이트/API에 접근하는
플러그인을 만들어 `plugins` 폴더에 넣으면 앱이 자동으로 불러와 자동 검색·수동 검색에
사용합니다. 참고 구현으로 [samples/LrclibLyricsPlugin](../samples/LrclibLyricsPlugin/LrclibPlugin.cs)
(LRCLIB API)가 저장소에 포함되어 있습니다.

## SDK 획득

SDK는 `DawnPlayer.Plugin.Abstractions` 어셈블리 하나입니다(netstandard2.0, 의존성 없음).

- **릴리스 이용자**: 릴리스 아티팩트에 동봉된 `DawnPlayer.Plugin.Abstractions.dll`을 참조하세요.
- **저장소 이용자**: `src/DawnPlayer.Plugin.Abstractions/DawnPlayer.Plugin.Abstractions.csproj`를
  프로젝트 참조하세요.

```xml
<ItemGroup>
  <Reference Include="DawnPlayer.Plugin.Abstractions">
    <HintPath>path\to\DawnPlayer.Plugin.Abstractions.dll</HintPath>
  </Reference>
</ItemGroup>
```

SDK가 netstandard2.0이므로 플러그인은 .NET Framework 4.x, .NET 5+ 어느 대상으로도 빌드해도
Dawn Player(net10.0-windows)에서 로드됩니다. 플러그인이 NuGet 의존성을 가져도 됩니다 —
`dotnet publish` 결과물 전체를 폴더에 넣으면 함께 로드됩니다(아래 배포 참고).

## 설치 폴더 구조

플러그인은 **플러그인별 하위 폴더**에 담아야 합니다. 폴더 안의 모든 DLL이 같은 격리
컨텍스트(AssemblyLoadContext)로 로드되므로 플러그인끼리, 호스트와 의존성 버전이 충돌하지
않습니다.

```
%AppData%\DawnPlayer\plugins\        (포터블 모드면 <exe>\data\plugins\)
└─ MySiteLyrics\
   ├─ MySiteLyrics.dll
   ├─ MySiteLyrics.deps.json         ← NuGet 의존성이 있으면 publish 결과물 전체
   └─ Newtonsoft.Json.dll            ← 플러그인이 가져온 비공개 의존성
```

설치 후 앱에서 **설정 → 온라인 가사 → 다시 스캔**을 누르면 재시작 없이 등록됩니다.
이미 로드된 플러그인의 DLL을 교체할 때는 앱을 재시작해야 합니다.

## API 레퍼런스

SDK의 전체 공개 API입니다(netstandard2.0 호환 문법). 레코드의 속성은 모두
`init` 전용입니다.

### 어트리뷰트

```csharp
[LyricsPlugin("id", "표시명", "1.0.0", "작성자")]
```

`ILyricsPlugin` 구현 클래스 하나에 부착합니다.

| 인자 | 설명 |
|---|---|
| `id` | 고유 식별자(예: `"lrclib"`). 설정의 우선순위·활성화에 키로 쓰입니다. **출시 후 변경 금지** |
| `name` | UI에 표시되는 이름 |
| `version` | 플러그인 버전 |
| `author` | 작성자 표시 |

### 플러그인 인터페이스

```csharp
public interface ILyricsPlugin
{
    Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken);
    Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken);
}
```

- `SearchAsync`: 검색 후보를 반환합니다. 빈 리스트 = "결과 없음". 오류는 **예외를 던지세요**
  (호스트가 로그를 남기고 다음 플러그인으로 넘어갑니다).
- `GetAsync`: `SearchAsync`가 돌려준 결과 하나의 가사를 내려받습니다. 결과가 이미 사라졌으면
  `null`을 반환하세요. `result.ResultId`는 SearchAsync가 만든 불투명 핸들 그대로 돌아옵니다.

### 모델

```csharp
public sealed record LyricsSearchQuery
{
    public string? Title { get; init; }      // 트랙 제목, 모르면 null
    public string? Artist { get; init; }     // 아티스트, 모르면 null
    public string? Album { get; init; }      // 앨범명(사용자가 앨범으로만 검색한 경우 이것만 있음)
    public int DurationMs { get; init; }     // 트랙 길이(ms), 모르면 0
}

public sealed record LyricsSearchResult
{
    public string ResultId { get; init; } = "";  // 필수. GetAsync로 돌아오는 불투명 핸들
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public int DurationMs { get; init; }         // 사이트가 길이를 알려주면 채움, 모르면 0
    public bool IsSynced { get; init; }          // LRC 동기 가사가 있는 결과면 true
    public string? SourceUrl { get; init; }      // 선택: 검색 창에 표시할 원본 링크
}

public sealed record LyricsContent
{
    public string? SyncedLrc { get; init; }      // LRC 텍스트 전체
    public string? PlainText { get; init; }      // 타임스탬프 없는 일반 가사
    public bool HasContent { get; }              // 둘 중 하나라도 비지 않았는지
}
```

`SyncedLrc`와 `PlainText`를 모두 제공하면 사용자 설정(동기 가사 우선)에 따라 하나가
선택됩니다. 일반 가사만 있어도 표시됩니다(비동기 표시).

### 호스트 컨텍스트

생성자가 `ILyricsPluginContext` 하나를 받으면 호스트가 주입합니다(없는 매개변수 없는
생성자도 허용).

```csharp
public interface ILyricsPluginContext
{
    string DataFolder { get; }       // plugins-data\<id>\ — 캐시 등 자유롭게 사용
    string? GetSetting(string key);  // 플러그인 옵션(아래 참고), 없으면 null
    void Log(string message);        // dawnplayer.log에 한 줄 추가, 절대 던지지 않음
}
```

## 첫 플러그인

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Plugins;

[LyricsPlugin("mysite", "My 가사사이트", "1.0.0", "you@example.com")]
public sealed class MySitePlugin : ILyricsPlugin
{
    // 프로세스에 1개: 자동 검색과 검색 창이 동시에 호출해도 안전해야 합니다.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ILyricsPluginContext? _context;

    public MySitePlugin() { }
    public MySitePlugin(ILyricsPluginContext context) => _context = context;

    public async Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(
        LyricsSearchQuery query, CancellationToken ct)
    {
        var url = $"https://api.example.com/search?q={Uri.EscapeDataString(query.Title ?? "")}";
        var json = await GetJsonAsync(url, ct);
        return ParseResults(json);   // 사이트 응답 → LyricsSearchResult 목록
    }

    public async Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken ct)
    {
        var url = $"https://api.example.com/lyrics/{Uri.EscapeDataString(result.ResultId)}";
        var lrc = await GetJsonAsync(url, ct);
        return string.IsNullOrWhiteSpace(lrc) ? null : new LyricsContent { SyncedLrc = lrc };
    }

    private static async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
```

계약 요점:

- **동시성**: 두 메서드는 동시에 호출될 수 있습니다. 상태를 두지 말고, `HttpClient`는
  정적 1개를 재사용하세요.
- **취소**: `ct`를 모든 비동기 호출에 전달하세요. 트랙이 바뀌면 진행 중인 요청이 취소됩니다.
- **오류**: 네트워크 실패 등은 예외로. "결과 없음"은 빈 리스트로 표현합니다.
- **검색 모드**: 제목·아티스트 검색과 앨범명만으로 하는 검색이 같은 메서드로 들어옵니다.
  `Title`/`Artist`가 null이고 `Album`만 있으면 앨범 검색입니다. 지원하지 않는 모드면 빈
  리스트를 반환하세요.

## 플러그인 옵션(API 키 등)

v1에서는 사용자가 `settings.json`의 `LyricsOnline.PluginOptions`에 플러그인 id별로 키·값을
직접 기입하고 플러그인이 `context.GetSetting(key)`로 읽습니다.

```json
{
  "LyricsOnline": {
    "PluginOptions": {
      "mysite": { "ApiKey": "..." }
    }
  }
}
```

필수 키가 비어 있으면 `SearchAsync`에서 명확한 예외 메시지를 던지세요 — 호스트가 로그와
검색 창에 표시합니다.

## 자동 검색에서 어떻게 선택되나

설치된 플러그인은 사용자가 지정한 **우선순위**(설정 → 온라인 가사) 순서로 시도되며,
각 플러그인의 검색 결과는 트랙 메타데이터와 비교해 점수가 매겨집니다:

| 신호 | 점수 |
|---|---|
| 제목 완전 일치 | +4 |
| 제목 부분 포함(양방향) | +2 |
| 제목 불일치 | −3 |
| 아티스트 완전 / 부분 / 불일치 | +2 / +1 / −2 |
| 앨범 완전 / 부분 | +1 / +1 |
| 길이 차이 ±3초 / ±10초 | +2 / +1 |
| 길이 차이 15초 초과 | −6 (다른 버전의 곡으로 판단) |
| 동기 가사 + 사용자가 동기 우선 | +1 |

기준점 2점을 넘는 최고 점수 후보만 다운로드되고, 못 넘으면 해당 플러그인은 결과 없음으로
취급되어 다음 플러그인이 시도됩니다. 결과 목록의 순서는 무관하지만 best-first를 권장합니다.

## 빌드와 배포

```powershell
# 의존성이 있는 플러그인은 publish로 폴더를 통째로 만듭니다
dotnet publish MySiteLyrics -c Release -o %AppData%\DawnPlayer\plugins\MySiteLyrics
```

## 디버깅

- 호스트의 플러그인 스캔 오류(로드 실패, 중복 id, 인스턴스화 실패)는 설정 → 온라인 가사
  화면 하단과 `dawnplayer.log`(`%AppData%\DawnPlayer`)에 표시됩니다.
- 플러그인의 `SearchAsync`/`GetAsync` 예외와 `context.Log` 출력도 같은 로그로 들어갑니다
  (`[lyrics-online]`, `[plugin:<id>]` 접두사).
