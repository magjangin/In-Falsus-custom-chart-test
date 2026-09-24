using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using MelonLoader.NativeUtils;
using Il2CppInterop.Runtime;
using Il2Cpp_b;
using Il2Cppifapp.Game;

namespace InFalsusMods
{
    /// <summary>
    /// 커스텀 채보 주입 — 차트 로더 `_b._s._VA(string 차트파일명) : _R` 에 네이티브 훅을 건다.
    ///
    /// 게임 흐름 (1.0.4b 네이티브 코드로 확인, 2026-09-24):
    ///   _s._VA("hwa0.spc")                      등록부에서 AssetId → 파일 읽기 → _S._Gab(바이트, 이름)
    ///     └ _R { (_t, List&lt;_fA&gt;, List&lt;_eA&gt;), sourceText }
    ///   GameScene._qk → _Qk(이름, _R) → LogicalNotePlayer._Ib(이름, long, 튜플, sourceText, 미러)
    ///     └ _T(_ve) 를 새로 만들고 노트마다 _pb 로 side 컨테이너를 채운 뒤 _qb·_nb·_Pb·_Kb·_Lb·_mb·_Mb 로 파생 버퍼 계산,
    ///       _Ue = 튜플, _xe = _Xe = 마지막 노트/이벤트 시각. → 파서 출력만 바꾸면 판정 레인·판정 윈도우·곡 종료가 전부 따라온다.
    ///
    /// 왜 _VA 인가:
    /// - _Gab 은 디코더 시드를 **파일 이름**으로 만든다(_Z._Ab(name, noteCount, 1)). 새 곡 차트 'hwa0.spc' 는 원곡 파일 별칭이라
    ///   그대로 두면 원곡 바이트를 'hwa0.spc' 시드로 풀어 쓰레기가 나온다. 그래서 _VA 에서 이름을 원곡('alamode0.spc')으로 바꿔 부른다.
    /// - _Ib·_Qk 는 큰 구조체를 값으로 받아 Harmony(인터롭 트램펄린)로 후킹하면 죽는다(docs/04 1장). 네이티브 훅은 인자를 IntPtr 로만
    ///   받으므로 이 문제가 없다. _VA 는 (반환 버퍼, 문자열, MethodInfo*) 뿐이다.
    ///
    /// 주의: 원본(_vaOriginal) 안에서 IL2CPP 예외가 나면 이 관리 코드 프레임을 C++ 예외가 지나가게 된다 — 존재하는 차트 이름만 넘길 것.
    /// </summary>
    internal static unsafe class ChartInjector
    {
        public static bool Enabled { get; set; } = true;

        /// <summary>게임이 불러오는 원곡 차트를 UserData/InFalsusMods/chart_dump/ 에 텍스트로 떠 둔다 (이미 있으면 건너뜀).</summary>
        public static bool DumpLoadedCharts { get; set; } = true;

        /// <summary>곡 선택 화면에서 모든 차트를 한 틱에 하나씩 직접 불러 덤프한다 — 채보 포맷 분석용 전체 자료.</summary>
        public static bool DumpAllCharts { get; set; } = true;

        /// <summary>마지막으로 주입한 커스텀 채보의 마지막 노트 끝(ms). AudioInjector 가 곡 종료 시각을 정할 때 쓴다.</summary>
        public static int LastInjectedEndMs { get; private set; }

        private const string VaFieldPrefix = "NativeMethodInfoPtr__VA_";
        private const long ExpectedVaRva = 0x537680;   // 1.0.4b (cpp2il_out/Game.dll 의 Address 속성)

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr VaFn(IntPtr ret, IntPtr name, IntPtr methodInfo);

        // Unity CoreModule 에도 같은 이름의 UnmanagedCallersOnlyAttribute 가 있어 못 쓴다 — 델리게이트를 살려 두고 함수 포인터로 넘긴다
        private static readonly VaFn _vaDetour = VaDetour;
        private static NativeHook<VaFn> _vaHook;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr> _vaOriginal;
        private static MelonLogger.Instance _logger;
        private static string _dumpDir;

        private static readonly Dictionary<string, string> _songAliases = new();   // 새 곡 슬러그 → 원곡 슬러그
        private static readonly Dictionary<string, object[]> _keepAlive = new();    // 차트 파일명 → 주입한 리스트(GC 핸들 유지)
        private static readonly HashSet<string> _injected = new();                  // 커스텀 채보를 넣은 차트 파일명
        private static readonly Dictionary<long, _BA> _coordCache = new();
        private static readonly object _gate = new();

        /// <summary>새 곡 슬러그의 차트('hwa2.spc')를 원곡('alamode2.spc')으로 파싱하게 등록한다.</summary>
        public static void RegisterSong(string newSlug, string sourceSlug)
        {
            lock (_gate) _songAliases[newSlug] = sourceSlug;
        }

        public static void Init(MelonLogger.Instance logger)
        {
            _logger = logger;
            if (!Enabled) return;

            try
            {
                _dumpDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "InFalsusMods", "chart_dump");
                Directory.CreateDirectory(_dumpDir);

                var field = FindStaticField(typeof(_s), VaFieldPrefix);
                var methodInfo = (IntPtr)field.GetValue(null);
                if (methodInfo == IntPtr.Zero) throw new InvalidOperationException($"{field.Name} 가 0");
                var target = *(IntPtr*)methodInfo;   // Il2CppMethodInfo 첫 필드 = methodPointer

                long rva = target.ToInt64() - GetModuleHandleW("GameAssembly.dll").ToInt64();
                string rvaNote = rva == ExpectedVaRva ? "1.0.4b 와 같음" : $"⚠ 1.0.4b({ExpectedVaRva:X}) 와 다름 — 업데이트됐을 수 있음";

                _vaHook = new NativeHook<VaFn>(target, Marshal.GetFunctionPointerForDelegate(_vaDetour));
                _vaHook.Attach();
                _vaOriginal = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)_vaHook.TrampolineHandle;

                logger.Msg($"[ChartInjector] _s._VA 네이티브 훅 완료 (RVA 0x{rva:X}, {rvaNote}) — 커스텀 채보 폴더 {HwaPaths.HwaDirectory}, 덤프 {_dumpDir}");
            }
            catch (Exception ex)
            {
                Enabled = false;
                logger.Error($"[ChartInjector] 훅 실패 — 커스텀 채보 꺼짐: {ex}");
            }
        }

        // ─────────────────────────────────────────────────────────────── 네이티브 훅

        private static IntPtr VaDetour(IntPtr ret, IntPtr name, IntPtr methodInfo)
        {
            string chartFile = null, sourceFile = null;
            IntPtr callName = name;
            try
            {
                chartFile = IL2CPP.Il2CppStringToManaged(name);
                sourceFile = SourceFileFor(chartFile);
                if (sourceFile != null) callName = IL2CPP.ManagedStringToIl2Cpp(sourceFile);
            }
            catch (Exception ex)
            {
                sourceFile = null;
                callName = name;
                _logger?.Error($"[ChartInjector] _VA 이름 처리 실패 — 원본 그대로: {ex.Message}");
            }

            IntPtr result = _vaOriginal(ret, callName, methodInfo);

            try
            {
                if (sourceFile != null) InjectCustom(chartFile, sourceFile, ret);
                else if (DumpLoadedCharts && chartFile != null) DumpParsed(chartFile, ret);
            }
            catch (Exception ex)
            {
                _logger?.Error($"[ChartInjector] '{chartFile}' 파싱 후 처리 실패 (게임은 파싱 결과 그대로 진행): {ex}");
            }
            return result;
        }

        /// <summary>'hwa2.spc' → 'alamode2.spc'. 등록된 새 곡 차트가 아니면 null.</summary>
        private static string SourceFileFor(string chartFile)
        {
            if (chartFile == null || !chartFile.EndsWith(".spc", StringComparison.Ordinal)) return null;
            string id = chartFile.Substring(0, chartFile.Length - 4);

            lock (_gate)
            {
                foreach (var kv in _songAliases)
                {
                    if (!id.StartsWith(kv.Key, StringComparison.Ordinal)) continue;
                    string rest = id.Substring(kv.Key.Length);
                    if (rest.Length == 0 || !IsDigits(rest)) continue;
                    return kv.Value + rest + ".spc";
                }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────── 주입

        /// <summary>_R 레이아웃: +0x00 _t(float _uC, +0x08 float _UC) · +0x10 List&lt;_fA&gt; · +0x18 List&lt;_eA&gt; · +0x20 string sourceText.</summary>
        private static void InjectCustom(string chartFile, string sourceFile, IntPtr r)
        {
            string chartId = chartFile.Substring(0, chartFile.Length - 4);
            string path = Path.Combine(HwaPaths.HwaDirectory ?? "", chartId + ".txt");

            if (!File.Exists(path))
            {
                LastInjectedEndMs = 0;
                _logger.Msg($"[ChartInjector] '{chartFile}': 커스텀 채보 없음({Path.GetFileName(path)}) — 원곡 '{sourceFile}' 차트로 진행 " +
                            $"(노트 {ListCount(*(IntPtr*)(r + 0x10))}개)");
                return;
            }

            ChartData chart;
            try
            {
                chart = ChartText.Load(path);
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException)
            {
                LastInjectedEndMs = 0;
                _logger.Warning($"[ChartInjector] ⚠ 채보 파일 오류 — 원곡 '{sourceFile}' 차트로 진행: {ex.Message}");
                return;
            }

            var notes = new Il2CppSystem.Collections.Generic.List<_fA>(chart.Notes.Count);
            foreach (var row in chart.Notes) notes.Add(ToNote(row));
            var events = new Il2CppSystem.Collections.Generic.List<_eA>(chart.Events.Count);
            foreach (var row in chart.Events) events.Add(ToEvent(row));

            int before = ListCount(*(IntPtr*)(r + 0x10));
            *(float*)(r + 0x00) = chart.Bpm;
            *(float*)(r + 0x08) = chart.Beats;
            *(IntPtr*)(r + 0x10) = notes.Pointer;
            *(IntPtr*)(r + 0x18) = events.Pointer;

            lock (_gate)
            {
                _keepAlive[chartFile] = new object[] { notes, events };   // 게임이 _Ue 에 담기 전까지 살려 둔다
                _injected.Add(chartFile);
            }
            LastInjectedEndMs = chart.LastNoteEndMs;
            ForgetLoadedChart(chartFile);

            _logger.Msg($"[ChartInjector] '{chartFile}' 커스텀 채보 주입 — {Path.GetFileName(path)}: 노트 {chart.Notes.Count}개" +
                        $"(원곡 {before}개 대체), 이벤트 {chart.Events.Count}개, chart {chart.Bpm} {chart.Beats}, 마지막 노트 끝 {chart.LastNoteEndMs}ms");
        }

        /// <summary>
        /// _Ib 는 이름·ID·sourceText 가 지금 들고 있는 것과 같으면 새 튜플을 버리고 리셋만 한다(네이티브 코드 확인).
        /// LogicalNotePlayer 가 이 차트를 들고 남아 있으면 고친 채보가 안 들어가므로 이름을 비워 다시 계산하게 한다.
        /// </summary>
        private static void ForgetLoadedChart(string chartFile)
        {
            var np = LogicalNotePlayer._Qe;
            if (np == null || np._Ve != chartFile) return;
            np._Ve = null;
            _logger.Msg($"[ChartInjector]   └ 이전 '{chartFile}' 를 들고 있던 LogicalNotePlayer 발견 — 다시 계산하도록 이름을 비움");
        }

        private static _fA ToNote(NoteRow n)
        {
            return new _fA
            {
                _ZD = n.Id,
                _ae = n.Group,
                _Ae = (_CA)n.Side,
                _be = (_HA)(uint)n.Type,
                _Be = n.Start,
                _ce = n.End,
                _Ce = Ba(n.StartX),
                _de = Ba(n.EndX),
                _De = Ba(n.StartW),
                _ee = Ba(n.EndW),
                _Ee = n.Ee,
                _fe = n.Fe_,
                _Fe = n.FE,
                _ge = n.Ge_,
                _Ge = n.GE,
                _he = (_FA)n.He_,
                _He = n.HE,
                _ie = n.Ie,
            };
        }

        private static _eA ToEvent(EventRow e)
        {
            var ev = new _eA { _TD = e.TD, _uD = e.UD_, _UD = (_EA)e.Kind };
            byte* p = (byte*)&ev + 0x10;
            for (int i = 0; i < 16; i++) p[i] = e.Payload[i];
            return ev;
        }

        /// <summary>_BA 는 필드가 readonly 이고 실수값(_LD)을 게임 생성자가 계산하므로 생성자로 만든다 — 같은 좌표는 재사용.</summary>
        private static _BA Ba(Coord c)
        {
            long key = ((long)c.Num << 32) | (uint)c.Den;
            lock (_gate)
            {
                if (!_coordCache.TryGetValue(key, out var ba))
                {
                    ba = new _BA(c.Num, c.Den);
                    _coordCache[key] = ba;
                }
                return ba;
            }
        }

        // ─────────────────────────────────────────────────────────────── 덤프

        private static void DumpParsed(string chartFile, IntPtr r)
        {
            if (_dumpDir == null || !chartFile.EndsWith(".spc", StringComparison.Ordinal)) return;
            string path = Path.Combine(_dumpDir, chartFile.Substring(0, chartFile.Length - 4) + ".txt");
            if (File.Exists(path)) return;

            var chart = new ChartData { Bpm = *(float*)(r + 0x00), Beats = *(float*)(r + 0x08) };

            IntPtr notesPtr = *(IntPtr*)(r + 0x10);
            if (notesPtr != IntPtr.Zero)
            {
                var notes = new Il2CppSystem.Collections.Generic.List<_fA>(notesPtr);
                for (int i = 0; i < notes.Count; i++) chart.Notes.Add(FromNote(notes[i]));
            }

            IntPtr eventsPtr = *(IntPtr*)(r + 0x18);
            if (eventsPtr != IntPtr.Zero)
            {
                var events = new Il2CppSystem.Collections.Generic.List<_eA>(eventsPtr);
                for (int i = 0; i < events.Count; i++) chart.Events.Add(FromEvent(events[i]));
            }

            File.WriteAllText(path, ChartText.Format(chart, $"{chartFile} — 게임 파서(_S._Gab) 출력 그대로"));
            _dumpCount++;
            if (_dumpCount <= 5 || _dumpCount % 50 == 0)
            {
                _logger.Msg($"[ChartInjector] 덤프 #{_dumpCount} {chartFile}: 노트 {chart.Notes.Count}, 이벤트 {chart.Events.Count}, chart {chart.Bpm} {chart.Beats}");
            }
        }

        private static int _dumpCount;

        private static NoteRow FromNote(_fA n)
        {
            return new NoteRow
            {
                Id = n._ZD,
                Group = n._ae,
                Side = (int)n._Ae,
                Type = (int)n._be,
                Start = n._Be,
                End = n._ce,
                StartX = new Coord(n._Ce._KD, n._Ce._lD),
                EndX = new Coord(n._de._KD, n._de._lD),
                StartW = new Coord(n._De._KD, n._De._lD),
                EndW = new Coord(n._ee._KD, n._ee._lD),
                Ee = n._Ee,
                Fe_ = n._fe,
                FE = n._Fe,
                Ge_ = n._ge,
                GE = n._Ge,
                He_ = (int)n._he,
                HE = n._He,
                Ie = n._ie,
            };
        }

        private static EventRow FromEvent(_eA e)
        {
            var row = new EventRow { TD = e._TD, UD_ = e._uD, Kind = (int)e._UD };
            byte* p = (byte*)&e + 0x10;
            for (int i = 0; i < 16; i++) row.Payload[i] = p[i];
            return row;
        }

        private static int ListCount(IntPtr list)
        {
            return list == IntPtr.Zero ? -1 : new Il2CppSystem.Collections.Generic.List<_fA>(list).Count;
        }

        // ─────────────────────────────────────────────────────────────── 틱 (검증 로그·전체 덤프)

        private static string _reportedChart;
        private static Queue<string> _dumpQueue;
        private static int _dumpQueueTotal;

        public static void Tick(MelonLogger.Instance log)
        {
            if (!Enabled) return;

            try
            {
                ReportLoadedChart(log);
                if (DumpAllCharts) DumpNextChart(log);
            }
            catch (Exception ex)
            {
                DumpAllCharts = false;
                log.Error($"[ChartInjector] 틱 예외 — 전체 덤프 중단: {ex}");
            }
        }

        /// <summary>커스텀 채보가 _Ib 를 거쳐 실제 런타임 구조(_ve)에 들어갔는지 한 번 찍는다.</summary>
        private static void ReportLoadedChart(MelonLogger.Instance log)
        {
            var np = LogicalNotePlayer._Qe;
            string chart = np != null ? np._Ve : null;
            if (string.IsNullOrEmpty(chart) || np._ve == null)
            {
                if (!SceneRefs.IsGameScene) _reportedChart = null;
                return;
            }
            if (chart == _reportedChart) return;
            _reportedChart = chart;

            bool injected;
            lock (_gate) injected = _injected.Contains(chart);

            var logical = np._Ue.Item2;
            var sides = new List<string>();
            var bySide = np._ve._vC;
            foreach (_CA side in Enum.GetValues(typeof(_CA)))
            {
                if (bySide != null && bySide.ContainsKey(side)) sides.Add($"{side}={bySide[side]._ze}");
            }
            log.Msg($"[ChartInjector][플레이] '{chart}' {(injected ? "커스텀 채보" : "원본")} — 논리 노트 {logical?.Count ?? -1}개, " +
                    $"side별 [{string.Join(" ", sides)}], _xe={np._xe:F0}ms");
        }

        /// <summary>곡 선택 화면에서 차트를 하나씩 _VA 로 불러 덤프한다(훅의 DumpParsed 가 파일을 쓴다). 등록부에 있는 이름만 부른다.</summary>
        private static void DumpNextChart(MelonLogger.Instance log)
        {
            if (!SceneRefs.IsSongSelectScene) return;

            if (_dumpQueue == null)
            {
                var select = SceneRefs.SongSelect;
                var all = select != null && select.dataAccess != null && select.dataAccess.SongData != null
                    ? select.dataAccess.SongData.allSongInfo : null;
                var table = Il2Cpp_k._BG._RxA != null ? Il2Cpp_k._BG._RxA._txA : null;
                if (all == null || table == null) return;

                _dumpQueue = new Queue<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    var charts = all[i]?.ChartInfos;
                    for (int j = 0; charts != null && j < charts.Length; j++)
                    {
                        string file = charts[j].Id + ".spc";
                        if (SourceFileFor(file) != null) continue;                       // 새 곡은 원곡과 같다
                        if (!table.ContainsKey(file)) continue;                          // 없는 파일은 원본이 예외를 던진다
                        if (File.Exists(Path.Combine(_dumpDir, charts[j].Id + ".txt"))) continue;
                        _dumpQueue.Enqueue(file);
                    }
                }
                _dumpQueueTotal = _dumpQueue.Count;
                if (_dumpQueueTotal == 0)
                {
                    DumpAllCharts = false;   // 이미 다 떠 둠
                    return;
                }
                log.Msg($"[ChartInjector] 전체 차트 덤프 시작 — {_dumpQueueTotal}개 (곡 선택 화면에 있는 동안 한 틱에 하나씩)");
            }

            if (_dumpQueue.Count == 0)
            {
                log.Msg($"[ChartInjector] 전체 차트 덤프 완료 — {_dumpQueueTotal}개 → {_dumpDir}");
                DumpAllCharts = false;
                return;
            }

            string next = _dumpQueue.Dequeue();
            try
            {
                _s._VA(next);   // 훅을 거치며 DumpParsed 가 파일을 쓴다
            }
            catch (Exception ex)
            {
                log.Warning($"[ChartInjector] 덤프용 로드 실패 '{next}': {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────── 도우미

        private static FieldInfo FindStaticField(Type type, string prefix)
        {
            foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (f.Name.StartsWith(prefix, StringComparison.Ordinal)) return f;
            }
            throw new MissingFieldException(type.FullName, prefix + "*");
        }

        private static bool IsDigits(string s)
        {
            foreach (char ch in s) if (ch < '0' || ch > '9') return false;
            return true;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
    }
}
