# In Falsus 모딩 문서

대상: `H:\steam\steamapps\common\In Falsus` · lowiro(Arcaea 제작사) · 최종 실측 2026-09-10

## 문서 목록

| 문서 | 내용 |
|---|---|
| **[01. 차트 포맷과 에셋](01-차트-포맷과-에셋.md)** | 텍스트 저작 포맷, `.spc` 바이너리, StreamingAssets 매핑, 자켓 교체, 배경 영상(BGA), 새 곡 슬롯 조사 |
| **[02. 런타임 차트 주입 ★](02-런타임-차트-주입.md)** | **커스텀 차트의 핵심.** 노트 치환·레인 조작이 실제로 동작하는 경로 |
| **[03. 판정·오디오·세이브](03-판정-오디오-세이브.md)** | 판정 윈도우 조작, 재생 시계, 세이브 데이터, 스토리 |
| **[04. 후킹 함정과 성능](04-후킹-함정과-성능.md)** | 크래시를 유발하는 후킹 패턴, 덤프 스텁의 거짓말, 프레임 드랍, 인터롭 래퍼 함정 |
| **[05. 캐릭터·스킬 해금과 아키텍처](05-캐릭터-스킬-해금과-아키텍처.md)** | 캐릭터 선택 레이어, 해금 후킹, 스킬 조작, 모드 구조 |

## 0. 결론 — 난이도는 생각보다 낮습니다

Obfuz 난독화가 걸려 있어 처음엔 어려워 보이지만, **실제로 건드리고 싶은 부분은 거의 다 열려 있습니다.**
곡 오디오만 한 겹(lowiro 네이티브 레이어) 더 돌아가야 합니다.

| 영역 | 상태 | 난이도 |
|---|---|---|
| **곡 오디오** | `sam/` 은 파일별 암호화라 파일 교체는 **실패**. 게임 FMOD 에 `ignoresetfilesystem` 으로 평문 OGG 를 열고 `_IF._NGA` 후킹으로 끼워 넣어 **동작 확인** (재생바·결과 화면까지) | **보통** — [03번](03-판정-오디오-세이브.md) 2장 |
| 세이브 데이터 | 평문 MemoryPack | 쉬움 |
| 스토리 스크립트 | 평문 DSL, `Game.NovelEngine` 난독화 0% | 쉬움 |
| 데이터 구조체 (카드/곡/인카운터) | 필드명 100% 보존 | 쉬움 |
| **판정 파라미터** | 판정 윈도우가 쓰기 가능한 `public static double` | **쉬움** |
| **캐릭터/스킬 해금** | `_KI` 후킹 + UI 마스크 비활성화로 **동작 확인** | **쉬움** |
| **커스텀 차트** | 런타임 자료구조 직접 수정으로 **동작 확인** | **보통** |
| UI / 씬 클래스 | 직렬화 필드는 보존, 내부 상태만 난독화 | 보통 |
| `.spc` 바이너리 재작성 | 델타+비트패킹 인코더 필요 | 어려움 (그리고 불필요) |

핵심은 **Obfuz가 화이트리스트 방식**이라는 점입니다. Unity가 프리팹에 바인딩해야 하는 `[SerializeField]`
필드와 MemoryPack이 직렬화하는 멤버는 이름을 못 바꾸니 그대로 남았고, 순수 내부 로직만 `_Qe`, `_gb` 같은
이름으로 뭉개졌습니다. **게다가 난독화된 이름 뒤의 값과 구조는 런타임에 그대로 읽고 쓸 수 있습니다.**

### 초판에서 틀렸던 것 (기록용)

| 초판 주장 | 실제 |
|---|---|
| "차트가 평문이라 커스텀 차트가 매우 쉽다" | 평문 646개는 매핑에 0개 등록된 **빌드 잔재**. 게임은 `.spc` 바이너리만 읽음 |
| "커스텀 차트는 `_Gab` 파서 후킹이 정답" | 그 후킹은 **크래시**함. 런타임 자료구조 직접 수정이 정답 |
| "판정 엔진 조작은 어려움" | 판정 윈도우가 **쓰기 가능한 static 필드**. 후킹 불필요 |
| "`_Ue`(논리 노트 리스트)를 고치면 됨" | 게임은 로드 시점에 변환해 둔 **다른 구조**를 읽음. 아무 효과 없음 |
| "곡 오디오는 평문 `.ogg` 라 파일 스왑으로 교체 가능" | 게임이 여는 곡 음원은 암호화된 **`<슬러그>.wav`**. 1.0.4b 기준 `sam/` 에 평문 OGG 0개. 교체 테스트 실패 |
| "`_Cg._A` 는 네이티브 `FMOD::Sound*`" | `_A` 는 Channel 핸들(추정). 진짜 Sound* 는 **`_Cg._B`** = `_IF._ctA[AssetId]` |
| "`_VUA` 는 오디오 길이(샘플)" | 같은 곡도 실행마다 값이 다름 — 길이가 아님 |

## 1. 환경

```
개발사        lowiro
런타임        IL2CPP x64
Unity         6000.3.9f1
MelonLoader   0.7.3 Open-Beta (net6)
Il2CppInterop 1.5.1-ci.845
Cpp2IL        2022.1.0-pre-release.21
직렬화        MemoryPack
난독화        Obfuz (Il2CppObfuz.Runtime.dll)
에셋          Addressables + StreamingAssets 직접 매핑
```

MelonLoader RemoteAPI에 등록 안 된 게임이라 `ObfuscationRegex`/`MappingURL`이 전부 null입니다.
즉 MelonLoader 자체 디난독화 지원은 없고, Il2CppInterop이 만들어준 이름을 그대로 씁니다.

## 2. 어셈블리 구조

**`Assembly-CSharp.dll`은 8KB짜리 빈 껍데기(타입 2개)입니다.** 여기만 덤프하면 아무것도 안 나옵니다.

```
Il2CppGame.dll               991 타입   씬/UI/게임플레이
Il2CppGame.Data.dll          375 타입   데이터 구조체·세이브
Il2CppGame.NovelEngine.dll   152 타입   스토리 엔진
Il2CppGame.UI.Common.dll      93 타입
Il2CppGame.Character.dll      67 타입
Il2CppGame.Audio.dll          67 타입
그 외 Common / Common.Rendering / Input / NativeTime / Str / Gramma / __Generated
```

네임스페이스는 `ifapp.Game.*` → 덤프 폴더는 `Decompiled/Il2Cppifapp/Game/...`.

| 폴더 | 내용 | 타입 수 |
|---|---|---|
| `Decompiled/` | 게임 코드만 | 1,828 |
| `Decompiled_Full/` | 위 + Unity + BCL + 서드파티 | 19,898 |

### 덤프 툴

```bash
"H:/source/repos/In Falsus mods/SignatureDumper/bin/Release/net8.0/SignatureDumper.exe"
```

`--all`을 붙이면 Unity/BCL 포함 전체를 `Decompiled_Full/`로 덤프합니다.

**Windows 대소문자 미구분 때문에 `_fA`/`_FA` 같은 타입이 서로 덮어쓰던 버그를 고쳤습니다**(중복 시 `__2` 접미사).
이 게임에서만 437개 타입이 되살아났습니다(1,369 → 1,828). 그래서 `_Dg` 는 `_Dg__2.cs`, `_IA` 는 `_IA__2.cs`
처럼 **찾는 타입이 엉뚱한 파일에 있을 수 있으니** `grep -rn "class _Dg"` 로 확인할 것.

덤프 스텁은 시그니처만 있고 메서드 바디가 없습니다. **그리고 `static` 수식어를 잃어버립니다**
— [04번 문서](04-후킹-함정과-성능.md)를 먼저 볼 것.

메서드 바디가 필요하면 인터롭 어셈블리를 직접 디컴파일하는 편이 정확합니다:

```bash
ilspycmd "<게임>/MelonLoader/Il2CppAssemblies/Il2CppGame.dll" \
         -t Il2Cppifapp.Game.LogicalNotePlayer \
         -r "<게임>/MelonLoader/Il2CppAssemblies"
```

`[CallerCount(n)]` 속성으로 **게임 내 호출부 개수**까지 알 수 있습니다. 후킹 대상을 고를 때
`CallerCount(0)` 이면 게임이 안 쓰는 죽은 메서드라는 뜻이라 매우 유용합니다.

## 3. 난독화 실측

전체 1,369 타입 / 17,416 멤버 기준 (초판 덤프):

- 멤버 이름 멀쩡: **658 타입 (48.1%)**
- 일부만 난독화: 553 타입 (40.4%)
- 전부 난독화: 158 타입 (11.5%)
- 멤버 단위 난독화율: **43.7%**

| 난독화율 | 네임스페이스 | 비고 |
|---:|---|---|
| 0.0% | `Game.NovelEngine.*` (1,218 멤버) | 스토리 엔진 전체 무손상 |
| 0.0% | `Game.Character`, `Game.Animation.System`, `Str`, `Gramma` | |
| 5.9% | `Game.Input` | |
| **11.6%** | **`Game.Data`** (1,551 멤버) | 세이브/카드/곡 데이터 — 사실상 무손상 |
| 32.3% | `Game.Scenes.SongSelect` | |
| 45.2% | `Game.Scenes` (3,895 멤버) | |
| 49.5% | `Game.UI.Common` | |
| 57.9% | `Game.UI.Settings` | |
| 85~97% | `Il2Cpp_b` `_d` `_e` `_f` `_G` `_h` `_J` `_k` `_m` `_n` | 원래 네임스페이스까지 뭉개진 내부 헬퍼 |

**읽을 필요 없는 폴더:** `Decompiled/Il2Cpp_*` 중 한두 글자짜리 상당수는 Il2CppInterop이 만든
제네릭 `MethodInfoStore` 캐시입니다. 단 `Il2Cpp_b`(차트 자료구조)와 `Il2Cpp_n`(파서),
`Il2Cpp_J`(오디오), `Il2Cpp_K`(판정 이벤트)는 **실제 게임 로직**이니 예외입니다.

## 4. 게임 업데이트 이력

| 날짜 | buildid | 영향 |
|---|---|---|
| 2026-09-10 13:33 | 25207111 | **난독화 이름 변화 없음.** 덤프 1,828개 중 `Il2Cpp_J/_lF.cs` 1개만 변경 (FMOD ChannelGroup 헬퍼에서 static 메서드 `_DhA`/`_ehA` 2개 삭제) |
| 2026-09-19 ~ 09-20 | 25416896 (게임 1.0.4b) | **난독화 이름 변경 있음** — `GameScene._dK` 가 사라져 `BgmHook` 패치 실패. 같은 시그니처 `GameScene._IK(_Cg)` 로 바뀜 — 후킹 동작 확인 (`SongSelectScene._co` 는 생존). `Game.Audio` 쪽 `_IF._ZSA`·`_IF._ctA`·`_Cg`·`_zf._uFA` 는 이름 그대로 (ilspycmd 확인). `ifapp_fmod_native.dll` 교체, `sam/` 12,085 → 10,346 (매핑 밖 잔재 전부 삭제, [01번](01-차트-포맷과-에셋.md) 4장). **재덤프·diff 아직 안 함** |

### 게임은 반드시 Steam 으로 실행할 것

`infalsus.exe` 를 직접 실행하면 게임이 Steam 경유 재실행을 위해 스스로 종료를 부릅니다(Player.log `QuitOrThrow called`).
그런데 MelonLoader 환경에서는 이 종료가 **멈춰 버리고**(응답 없음, CPU 0), Steam 이 띄운 두 번째 인스턴스는
`Another instance is already running` 대화상자(창 제목 `Fatal error`)를 띄웁니다. 모드나 에셋 문제로 오진하기 쉽습니다.
`steam://rungameid/3971950` 이나 Steam 라이브러리에서 실행하면 정상입니다 (2026-09-23 대조 실험으로 확인).

### 업데이트 후 절차

1. MelonLoader를 한 번 실행 → `Il2CppAssemblies` 자동 재생성 (로그에 `Assembly is up to date` 가 뜨면 이미 됨)
2. **이전 `Decompiled/` 를 먼저 백업**
3. `SignatureDumper.exe` 재실행
4. `diff -rq <백업> Decompiled` 로 변경 지점만 확인

3번을 백업 없이 하면 무엇이 밀렸는지 알 방법이 사라집니다. 실제로 이 절차 덕분에
"업데이트 때문에 모드가 깨졌다"는 오진을 1분 만에 뒤집을 수 있었습니다 — 바뀐 건 파일 1개뿐이었고,
진짜 원인은 [후킹 크래시](04-후킹-함정과-성능.md)였습니다.
