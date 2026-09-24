# In Falsus Custom Chart & Modding Framework

**In Falsus** (제작사: lowiro / Arcaea 제작사) 전용 MelonLoader 기반 커스텀 차트 런타임 주입 및 모딩 프로젝트입니다.

난독화(Obfuz)가 적용된 IL2CPP 런타임 환경에서 크래시 위험이 있는 파서 후킹 대신, **로드 완료된 런타임 자료구조를 직접 조작하는 무후킹 주입 경로를 개척**하여 커스텀 차트 및 전체 시스템 모딩을 실측 검증했습니다.

---

## 🌟 주요 기능 및 실측 성과

### 1. 커스텀 채보 주입 (`ChartInjector`, `BmsChart`) ★
- **차트 로더 네이티브 훅:** 차트 파일을 읽는 `_s._VA` 에 MelonLoader `NativeHook` 을 걸어, 파서 출력(노트·이벤트 리스트)을 통째로 바꿉니다. 로더(`_Ib`)가 판정 레인·판정 윈도우·곡 종료 시각까지 다시 계산합니다.
- **새 곡 hwa custom 전용:** `hwa/hwa0.bms` ~ `hwa3.bms` (난이도별). 채보는 자체 BMS 에디터로 쓰고 노트 종류는 WAV 파일 이름으로 가립니다 — 지금은 하단(단노트·홀드) 뼈대, 스카이는 설계 중 ([06번 문서](docs/06-커스텀-채보-작성.md)).
- **점수 기록 차단 (`ResultGuard`):** 새 곡 결과는 세이브에 남기지 않습니다.
- **원곡 차트 283개 텍스트 덤프:** 노트 필드 의미를 전수 조사해 정리 ([02번 문서](docs/02-런타임-차트-주입.md) 9장).
- 초기 실험(2026-09-10): 로드된 런타임 구조(`LogicalNotePlayer._ve`)를 직접 고쳐 노트 치환·레인 이동, 렌더링(`_Ce`/`_de`)과 판정 레인(`_He`/`_ie`) 분리 규명.

### 2. 동적 레인 변형 기믹 해독 (`lane(ms, mask, flag)`)
- 634개 차트 전수 조사를 통해 인게임에서 **레인이 실시간으로 닫히며 4키 ↔ 3키로 전환되는 시스템 완전 해독**:
  - `lane(ms, 1, 1)`: 맨 왼쪽 1번 레인(`A` 키) 닫힘 → **`S, D, F` 3키 모드 전환**
  - `lane(ms, 4, 1)`: 맨 오른쪽 4번 레인(`F` 키) 닫힘 → **`A, S, D` 3키 모드 전환**
  - `lane(ms, mask, 0)`: 해당 레인 재활성화 → `A, S, D, F` 4키 모드 복귀
  - 런타임: `LogicalNotePlayer._ve._zC : Memory<_eA>` 이벤트 버퍼와 `Track` 렌더러 연동.

### 3. 전체 캐릭터 및 스킬 완전 해금 (`CharacterUnlocker`)
- `CharacterSelectLayer._KI`(해금 여부) 및 `_LI`(선택 여부) Harmony Prefix 가로채기로 모든 캐릭터 상시 선택 가능.
- 하단 5개 슬롯의 `storyDisabledWidgets`("???") 마스크 강제 제거 및 초상화 활성화.
- 캐릭터 고유 스킬 및 공용 스킬 7종 최대 레벨(Lv.5) 주입.

### 4. 스토리 빨리감기 상시 활성화 (`StorySkipUnlocker`)
- `StoryScene._gS.IsStoryAllowFastForward = true` 강제 적용으로 모든 스토리 구간 즉시 빨리감기/스킵 가능.

### 5. 정밀 오디오 & 자켓 감지 시스템 (`BgmHook`, `JacketHook`)
- `StreamingAssetsMapping` GUID ↔ 논리 파일명(`.wav`/`.spc` 등 10,346개) 자동 해독 캐시.
- 곡 음원은 lowiro 네이티브 레이어(`ifapp_fmod_native`)가 복호화하는 암호화 파일이라 파일 교체로는 넣을 수 없음(2026-09-23 실측) — [03번 문서](docs/03-판정-오디오-세이브.md) 2장.
- 크래시 위험이 있는 `_qCA`(byref 구조체) 대신 안전한 `_QCA(float)`(재생바 진행도) 기반 무장 트리거.
- 정밀 오디오 재생 시계(`_Dg._miA` - 리드인 음수 클럭) 및 차트 종료 시각 상수(`_xe` 계열) 규명.

### 6. 독립 IL2CPP 스켈레톤 추출기 (`SignatureDumper`)
- MelonLoader가 생성한 `Il2CppAssemblies`를 오프라인에서 `System.Reflection`으로 정적 분석하여 C# 스켈레톤 소스 생성.
- Windows 파일시스템 대소문자 미구분 충돌 자동 해결 (중복 시 `__2` 접미사 처리로 437개 소실 타입 복원).

---

## 📚 상세 기술 문서 (`docs/`)

프로젝트 내 상세 연구 및 실측 결과는 `docs/` 디렉터리에 정리되어 있습니다:

| 문서 | 내용 |
|---|---|
| **[01. 차트 포맷과 에셋](docs/01-차트-포맷과-에셋.md)** | 텍스트 저작 포맷, `.spc` 바이너리, 4키/3키 `lane` 기믹, StreamingAssets 매핑 |
| **[02. 런타임 차트 주입 ★](docs/02-런타임-차트-주입.md)** | **커스텀 차트의 핵심.** 런타임 자료구조 직접 수정 및 렌더링/판정 분리 |
| **[06. 커스텀 채보 — BMS ★](docs/06-커스텀-채보-작성.md)** | hwa 채보를 BMS 로 쓰는 법과 다음 단계 |
| **[03. 판정 · 오디오 · 세이브](docs/03-판정-오디오-세이브.md)** | 판정 윈도우 조작, 정밀 재생 시계, MemoryPack 세이브, 스토리 |
| **[04. 후킹 함정과 성능](docs/04-후킹-함정과-성능.md)** | `byref` 값 타입 트램펄린 크래시 방지, 덤프 스텁 함정, 프레임 드랍 최적화 |
| **[05. 캐릭터 · 스킬 해금과 아키텍처](docs/05-캐릭터-스킬-해금과-아키텍처.md)** | 캐릭터 선택 레이어, 해금 후킹, 스킬 조작 구조 |

---

## 🛠️ 개발 환경

```yaml
게임: In Falsus (lowiro)
런타임: Unity 6000.3.9f1 (IL2CPP x64)
모드로더: MelonLoader 0.7.3 Open-Beta (net6.0)
인터롭: Il2CppInterop 1.5.1-ci.845
타깃 프레임워크: net6.0
난독화 툴: Obfuz (Il2CppObfuz.Runtime.dll)
```

---

## 🚀 빌드 및 적용 방법

### 1. 전제 조건
- .NET 6.0 SDK / .NET 8.0 SDK
- MelonLoader 0.7.3 Open-Beta가 설치된 In Falsus 게임 클라이언트

### 2. 빌드
```bash
dotnet build "In Falsus mods/In Falsus mods.csproj" -c Release
```
*(기본 설정 시 게임 설치 경로 `H:\steam\steamapps\common\In Falsus\Mods`로 빌드된 DLL이 자동 복사됩니다.)*

### 3. 디컴파일 시그니처 덤프 (선택 사항)
```bash
dotnet run --project SignatureDumper/SignatureDumper.csproj -c Release
```

---

## 👤 Author

* **화영왕 (Hwa-young-wang)** - Lead Modder / Admin
