using System;
using MelonLoader;
using Il2Cpp_b;
using Il2Cppifapp.Game;

namespace InFalsusMods
{
    /// <summary>
    /// 커스텀 차트 주입 실험 — **맨 처음 노트 하나만 남기고 전부 지운다.**
    ///
    /// 1차 시도(2026-09-10)에서 `LogicalNotePlayer._Ue.Item2`(논리 노트 List)를 제자리에서
    /// 132개 → 1개로 줄이는 데 성공했지만 **인게임에는 아무 변화가 없었다.** 이유는 게임이
    /// 로드 시점(`_Ib`)에 논리 노트를 다른 자료구조로 변환해 두고 그쪽만 보기 때문이다:
    ///
    ///   LogicalNotePlayer._ve  (타입 _T, 클래스)
    ///     ├ _vC : Dictionary&lt;_CA, _hA&gt;   ← **side(Bottom/Sky)별 실제 노트 저장소**
    ///     │        _hA._ye : Il2CppStructArray&lt;_fA&gt;  (백킹 배열)
    ///     │        _hA._Ye : Memory&lt;_fA&gt;             (실제로 쓰는 구간)
    ///     │        _hA._ze : int                      (개수로 추정)
    ///     ├ _ZC : Memory&lt;_IA&gt;   (double 2개 = 시간 범위, 병렬 배열)
    ///     ├ _ad : Memory&lt;_jA&gt;   (Vector2 2개 = 좌표, 병렬 배열)
    ///     └ _zC : Memory&lt;_eA&gt;   (이벤트)
    ///
    /// 그래서 이번에는 `_vC` 쪽을 자른다. `_hA` 는 클래스라 참조로 직접 고쳐지고,
    /// `Memory&lt;T&gt;.Slice` 와 `_ze` setter 가 모두 살아 있어 후킹이 필요 없다.
    ///
    /// 후킹이 답이 아닌 이유: 변환 지점 `_Ib(string, long, ValueTuple&lt;...&gt;, string, bool)` 는
    /// 튜플을 `il2cpp_object_unbox` 한 **생 구조체 포인터**로 받는다. 인바운드 트램펄린이 이걸
    /// 객체로 오해하므로 8장의 `_qCA` 크래시가 그대로 재현된다. 파서 `_Gab` 도 `ReadOnlySpan&lt;byte&gt;`
    /// 라 같은 함정이다. **살아있는 자료구조를 직접 고치는 쪽이 유일하게 안전한 경로다.**
    /// </summary>
    internal static class ChartInjector
    {
        /// <summary>주입 실험 스위치. true면 1개만 남기고 자르는 실험 활성화.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>앞에서부터 남길 노트 개수 (기본 1개 실험용).</summary>
        public static int KeepCount { get; set; } = 1;

        /// <summary>
        /// 논리 노트 List(_Ue.Item2)도 같이 자를지 여부. 기본 false.
        /// 인게임 동작에는 영향이 없다는 것이 실측으로 확인됐고, 켜 두면 NoteListDumper 가
        /// 원본 차트를 못 보여줘서 비교가 안 된다.
        /// </summary>
        public static bool MutateLogicalList { get; set; } = false;

        /// <summary>
        /// 남긴 노트의 레인(가로 위치)을 강제로 바꿀지 여부.
        ///
        /// 위치는 `_fA._Ce`(startX) / `_de`(endX) 에 `_BA`(분자/분모 유리수)로 들어 있다.
        /// 실측상 하단 노트는 분모 4 에 분자 1~5 를 쓴다(1/4, 2/4, 3/4, 5/4 …).
        /// 분모는 원본을 그대로 두고 분자만 갈아끼워서 원본과 다른 자리에 뜨는지 본다.
        /// </summary>
        public static bool OverrideLane { get; set; } = true;

        /// <summary>바꿀 레인의 분자. 분모는 원본 노트의 것을 그대로 쓴다.</summary>
        public static int LaneNumerator { get; set; } = 3;

        private static string _injectedChart;

        public static void Tick(MelonLogger.Instance log)
        {
            if (!Enabled) return;

            try
            {
                var np = LogicalNotePlayer._Qe;
                string chartId = np == null ? null : np._Ve;

                if (string.IsNullOrEmpty(chartId))
                {
                    _injectedChart = null;   // 플레이 씬을 나가면 다음 곡에서 다시 주입
                    return;
                }

                if (chartId == _injectedChart) return;

                var notes = np._Ue.Item2;
                if (notes == null || notes.Count == 0) return;   // 아직 파싱 전

                var state = np._ve;
                if (state == null) return;                        // 변환 전

                _injectedChart = chartId;

                var firstNote = notes[0];

                log.Msg("══════════════════════════════════════════════════════════════════════════");
                log.Msg($"[ChartInjector] '{chartId}' 주입 시작 — 논리 노트 {notes.Count}개");
                log.Msg($"[ChartInjector]   └ 첫 노트: id={firstNote._ZD} group={firstNote._ae} " +
                        $"side={firstNote._Ae} type={firstNote._be} judge {firstNote._Be}~{firstNote._ce}ms" +
                        $"{(firstNote._ce > firstNote._Be ? " (홀드)" : " (단노트)")}");

                DumpParallelBuffers(state, log, "주입 전");
                int trimmed = TrimSideBuffers(state, firstNote._Ae, log);
                DumpParallelBuffers(state, log, "주입 후");

                if (MutateLogicalList)
                {
                    notes.Clear();
                    notes.Add(firstNote);
                    log.Msg($"[ChartInjector]   └ 논리 노트 List 도 {notes.Count}개로 축소");
                }

                log.Msg($"[ChartInjector] 완료 — side 컨테이너 {trimmed}개 조정");
                log.Msg("══════════════════════════════════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                _injectedChart = null;
                log.Error($"[ChartInjector] 주입 실패: {ex}");
            }
        }

        /// <summary>
        /// side 별 노트 컨테이너(_vC)를 잘라낸다.
        /// 첫 노트가 속한 side 만 KeepCount 개를 남기고 나머지 side 는 0 개로 만든다.
        /// </summary>
        private static int TrimSideBuffers(_T state, _CA keepSide, MelonLogger.Instance log)
        {
            var bySide = state._vC;
            if (bySide == null)
            {
                log.Warning("[ChartInjector]   ! _vC 가 null — side 컨테이너를 찾지 못했습니다");
                return 0;
            }

            int touched = 0;

            foreach (_CA side in Enum.GetValues(typeof(_CA)))
            {
                _hA container = null;
                try
                {
                    if (!bySide.ContainsKey(side)) continue;
                    container = bySide[side];
                }
                catch { continue; }

                if (container == null) continue;

                int before = container._Ye.Length;
                int keep = (side == keepSide) ? Math.Min(KeepCount, before) : 0;

                container._Ye = container._Ye.Slice(0, keep);
                container._ze = keep;
                touched++;

                log.Msg($"[ChartInjector]   └ side {side}: _Ye {before} → {container._Ye.Length}개, _ze={container._ze}" +
                        $"{(side == keepSide ? "  ← 첫 노트가 있는 side" : "")}");

                if (side == keepSide && keep > 0 && OverrideLane)
                {
                    ApplyLaneOverride(container, log);
                }
            }

            return touched;
        }

        /// <summary>
        /// 남긴 노트의 가로 위치를 바꾼다.
        ///
        /// `_hA._ye` 는 `Il2CppStructArray&lt;_fA&gt;`(백킹 배열)라 인덱서로 읽고 쓸 수 있다.
        /// `_fA` 는 struct 이므로 인덱서가 **복사본**을 준다 — 고친 뒤 반드시 다시 써넣어야 한다.
        ///
        /// 이게 화면에 반영되지 않으면, 렌더러가 `_fA` 대신 로드 시점에 미리 계산해 둔
        /// `_T._ad`(Memory&lt;_jA&gt; = Vector2 좌표 쌍)를 읽는다는 뜻이다. 그때는 그쪽을 고쳐야 한다.
        /// </summary>
        private static void ApplyLaneOverride(_hA container, MelonLogger.Instance log)
        {
            var arr = container._ye;
            if (arr == null || arr.Length == 0)
            {
                log.Warning("[ChartInjector]   ! _ye 백킹 배열이 비어 있어 레인을 바꾸지 못했습니다");
                return;
            }

            var note = arr[0];

            // 원본 레인 값을 먼저 기억한다. 아래에서 "이 값과 같은 계산 필드"를 찾아 판정용 사본을 특정한다.
            int originalNum = note._Ce._KD;
            int denom = note._Ce._lD;
            double originalValue = note._Ce._LD;
            double newValue = denom == 0 ? 0 : LaneNumerator / (double)denom;

            DumpNote(note, log, "변경 전");

            // 1) 렌더링이 읽는 좌표 (실측 확인됨)
            note._Ce = new _BA(LaneNumerator, denom);
            note._de = new _BA(LaneNumerator, denom);

            // 2) 판정이 읽는 사본 후보.
            //    _fA 는 필드가 18개인데 public 생성자는 10개만 채운다. 나머지 8개는 로드 시점에
            //    게임이 계산해 넣는 값이고, 렌더링과 판정이 서로 다른 걸 읽는다는 게 실측으로 드러났다
            //    (좌표를 바꾸니 노트는 옮겨졌는데 판정은 원래 자리에 남음 — 2026-09-10).
            //    그래서 "원본 레인 값과 일치하는 계산 필드"를 값으로 찾아내 같이 갈아끼운다.
            int patched = 0;

            if (note._He == originalNum) { note._He = LaneNumerator; patched++; log.Msg($"[ChartInjector]   └ _He 가 원본 분자({originalNum})와 일치 → {LaneNumerator} 로 변경"); }
            if (note._ie == originalNum) { note._ie = LaneNumerator; patched++; log.Msg($"[ChartInjector]   └ _ie 가 원본 분자({originalNum})와 일치 → {LaneNumerator} 로 변경"); }

            if (Near(note._Ee, originalValue)) { note._Ee = newValue; patched++; log.Msg($"[ChartInjector]   └ _Ee 가 원본 좌표({originalValue:0.###})와 일치 → {newValue:0.###} 로 변경"); }
            if (Near(note._fe, originalValue)) { note._fe = newValue; patched++; log.Msg($"[ChartInjector]   └ _fe 가 원본 좌표({originalValue:0.###})와 일치 → {newValue:0.###} 로 변경"); }
            if (Near(note._Fe, originalValue)) { note._Fe = newValue; patched++; log.Msg($"[ChartInjector]   └ _Fe 가 원본 좌표({originalValue:0.###})와 일치 → {newValue:0.###} 로 변경"); }

            if (patched == 0)
            {
                log.Warning("[ChartInjector]   ! 원본 레인 값과 일치하는 계산 필드를 못 찾았습니다 — " +
                            "판정 좌표는 _fA 밖(_T._ad 의 Vector2 좌표 등)에 있을 수 있습니다");
            }

            arr[0] = note;   // struct 복사본을 고친 것이므로 반드시 되써넣는다

            DumpNote(arr[0], log, "변경 후");
            log.Msg($"[ChartInjector]   └ (_ye 길이 {arr.Length}, _Ye 길이 {container._Ye.Length}, 계산 필드 {patched}개 조정)");
        }

        private static string Coord(_BA c)
        {
            return c._lD == 0 ? "0" : $"{c._KD}/{c._lD}({c._LD:0.###})";
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) < 0.0001;
        }

        /// <summary>
        /// _fA 의 18개 필드를 전부 찍는다.
        /// 생성자가 채우는 10개(id~endWidth)와, 로드 시점에 게임이 계산해 넣는 8개
        /// (_Ee _fe _Fe _ge _Ge _he _He _ie)를 구분해서 본다 — 판정용 좌표가 후자에 숨어 있다.
        /// </summary>
        private static void DumpNote(_fA n, MelonLogger.Instance log, string label)
        {
            log.Msg($"[ChartInjector]   [{label}] 생성자 필드: id={n._ZD} group={n._ae} side={n._Ae} type={n._be} " +
                    $"judge {n._Be}~{n._ce}ms");
            log.Msg($"[ChartInjector]   [{label}]   startX={Coord(n._Ce)} endX={Coord(n._de)} " +
                    $"startW={Coord(n._De)} endW={Coord(n._ee)}");
            log.Msg($"[ChartInjector]   [{label}] 계산 필드: _Ee={n._Ee:0.####} _fe={n._fe:0.####} _Fe={n._Fe:0.####} " +
                    $"_ge={n._ge} _Ge={n._Ge} _he={n._he} _He={n._He} _ie={n._ie}");
        }

        /// <summary>_T 가 들고 있는 병렬 버퍼 길이를 찍는다. 어느 버퍼가 노트 수와 같은지 대조용.</summary>
        private static void DumpParallelBuffers(_T state, MelonLogger.Instance log, string label)
        {
            try
            {
                log.Msg($"[ChartInjector]   [{label}] _ZC(_IA 시간범위)={state._ZC.Length}  _ad(_jA 좌표)={state._ad.Length}  " +
                        $"_zC(_eA 이벤트)={state._zC.Length}");
                log.Msg($"[ChartInjector]   [{label}] _Ad/_bd/_Bd(double)={state._Ad.Length}/{state._bd.Length}/{state._Bd.Length}  " +
                        $"_cd/_dd(_cH)={state._cd.Length}/{state._dd.Length}");
                log.Msg($"[ChartInjector]   [{label}] ints _YC={state._YC} _Dd={state._Dd} _ed={state._ed} _Ed={state._Ed}  _xC={state._xC}");
            }
            catch (Exception ex)
            {
                log.Warning($"[ChartInjector]   [{label}] 버퍼 덤프 실패: {ex.Message}");
            }
        }
    }
}
