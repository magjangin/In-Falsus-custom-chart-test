using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InFalsusMods
{
    /// <summary>
    /// 차트 텍스트 포맷. 인터롭 타입을 쓰지 않아 게임 없이도 읽고 쓸 수 있다. 게임 구조체로 바꾸는 건 ChartInjector 가 한다.
    /// 한 파일에 두 층을 섞어 쓸 수 있다. '#' 뒤는 주석, 빈 줄 무시.
    ///
    /// ■ 쉬운 문법 (손으로 쓰는 용) — 위치는 '마디:박'(1:1 부터, 박은 1.5 · 2+1/3 · 3/2 처럼) 또는 '@ms'
    /// <code>
    /// bpm 125 [박자=4]              처음 BPM (차트 헤더)
    /// offset 226                    1:1 이 음원 몇 ms 인지
    /// bpm 33:1 150 [박자]           BPM(·박자) 변경 — 박자 변경은 마디 첫 박에서만
    /// tap   &lt;위치&gt; &lt;레인&gt;             레인 1~4 (A S D F), 0·5 = 바깥 레인, 넓은 노트는 1-2 · 2-4
    /// hold  &lt;위치&gt; &lt;레인&gt; &lt;길이(박)&gt;
    /// flick &lt;위치&gt; &lt;x&gt; &lt;폭&gt; &lt;L|R&gt;     스카이 플릭 — x 는 중심, 0~1 (6/12 · 0.5)
    /// sky   &lt;위치&gt; &lt;길이(박)&gt; &lt;x시작&gt; &lt;x끝&gt; &lt;폭&gt; [w2=끝폭] [ease=00] [group=이름]
    ///                               앞 스카이가 끝난 시각·x 에서 시작하면 자동으로 한 줄로 이어짐
    /// speed &lt;위치&gt; &lt;배율&gt;            스크롤 속도
    /// lane  &lt;위치&gt; &lt;1|4&gt; &lt;close|open&gt;  레인 닫기/열기 (4키 ↔ 3키)
    /// </code>
    ///
    /// ■ 원본 형식 — 게임 파서(_S._Gab)가 내놓는 구조를 필드 하나 빠짐없이 옮긴 것 (chart_dump 가 이 형식)
    /// <code>
    /// chart &lt;_uC&gt; &lt;_UC&gt;                         _t (float 2개 = BPM, 박자)
    /// event &lt;_TD&gt; &lt;_uD&gt; &lt;_UD&gt; &lt;hex32&gt;            _eA — 순번, ms, 종류, 0x10~0x1F 공용체 16바이트
    /// note  &lt;id&gt; &lt;group&gt; &lt;side&gt; &lt;type&gt; &lt;start&gt; &lt;end&gt; &lt;sx&gt; &lt;ex&gt; &lt;sw&gt; &lt;ew&gt; [키=값 …]
    /// </code>
    /// note 의 앞 10개는 게임 생성자 _fA(id, groupId, side, type, judgeStart, judgeEnd, startX, endX, startWidth, endWidth) 순서,
    /// 좌표는 '분자/분모'. 뒤의 키=값은 생성자 밖 8개(Ee fe Fe ge Ge he He ie).
    ///
    /// 쉬운 문법 줄이 하나라도 있으면 노트·이벤트를 시간순으로 정렬하고 번호(id·_TD)를 다시 매긴다.
    /// 원본 형식만 있으면 순서·번호를 그대로 둔다(게임 차트 왕복 보존).
    ///
    /// 필드 의미 (2026-09-24, 게임 차트 283개·노트 185,531개 덤프로 확인):
    /// side 1 = 하단 4레인(x = 레인/4, He = 시작 레인, ie = 끝 레인), 2 = 왼쪽 바깥(x 0/4, He 0), 3 = 오른쪽 바깥(x 5/4, He 5),
    /// 4 = 스카이(x = 중심, He = ie = -1). type 1 탭, 2 홀드, 4 플릭(he 1024/4096 = 방향), 5 스카이 구간(he = (4&lt;&lt;a)|(32&lt;&lt;b) 곡선).
    /// 이벤트 종류 0 스크롤 속도(double), 1 BPM(double)·박자(float), 2 BPM 계열(미확정), 3 레인(byte 레인, +4 bool 닫힘).
    /// Ee·ge 는 게임이 로드 때 덮어쓰고(_pb 가 Ee=0·ge=-1), fe·Fe 는 게임 차트 전부 -1800000, Ge 는 전부 0.
    /// </summary>
    internal sealed class ChartData
    {
        public float Bpm;          // _t._uC
        public float Beats;        // _t._UC
        public readonly List<NoteRow> Notes = new();
        public readonly List<EventRow> Events = new();

        /// <summary>마지막 노트 판정 끝(ms). 노트가 없으면 0.</summary>
        public int LastNoteEndMs
        {
            get
            {
                int max = 0;
                foreach (var n in Notes) max = Math.Max(max, Math.Max(n.Start, n.End));
                return max;
            }
        }
    }

    internal struct Coord
    {
        public int Num, Den;
        public Coord(int num, int den) { Num = num; Den = den; }
        public double Value => (double)Num / Den;
        public override string ToString() => $"{Num}/{Den}";
    }

    internal sealed class NoteRow
    {
        public long Id, Group;
        public int Side, Type;
        public int Start, End;
        public Coord StartX, EndX, StartW, EndW;

        // 생성자 밖 필드 — 원본 형식은 파서 출력값을 그대로 보존하려고 둔다. 기본값은 게임 차트에서 본 값.
        public double Ee;                    // _Ee
        public double Fe_ = Sentinel;        // _fe
        public double FE = Sentinel;         // _Fe
        public long Ge_;                     // _ge
        public bool GE;                      // _Ge
        public int He_;                      // _he (_FA 플래그)
        public int HE = -1, Ie = -1;         // _He _ie (판정 레인, 스카이는 -1)

        public const double Sentinel = -1800000;
    }

    internal sealed class EventRow
    {
        public long TD;
        public int UD_;              // _uD (ms)
        public int Kind;             // _UD (_EA)
        public readonly byte[] Payload = new byte[16];   // 0x10~0x1F

        public byte PayloadByte => Payload[0];                               // _vD
        public bool PayloadFlag => Payload[4] != 0;                          // _VD
        public double PayloadDouble => BitConverter.ToDouble(Payload, 0);    // _wD / _xD
        public float PayloadFloat => BitConverter.ToSingle(Payload, 8);      // _WD
    }

    internal static class ChartText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 게임 enum 값
        private const int SideBottom = 1, SideLeftOuter = 2, SideRightOuter = 3, SideSky = 4;
        private const int TypeTap = 1, TypeHold = 2, TypeFlick = 4, TypeSky = 5;
        private const int EventSpeed = 0, EventBpm = 1, EventLane = 3;

        // 플릭 방향 — 게임 차트에서 1024 는 화면 오른쪽, 4096 은 왼쪽에 더 많다(바깥쪽을 가리킨다고 추정). 인게임 확인 필요.
        private const int FlickRight = 1024, FlickLeft = 4096;

        public static ChartData Load(string path) => Parse(File.ReadAllLines(path), path);

        private sealed class Line
        {
            public int No;
            public string Raw;
            public string[] Tok;
            public string Cmd;
        }

        public static ChartData Parse(IEnumerable<string> lines, string source = "")
        {
            string file = Path.GetFileName(source);

            var all = new List<Line>();
            int lineNo = 0;
            foreach (var raw in lines)
            {
                lineNo++;
                string text = raw;
                int hash = text.IndexOf('#');
                if (hash >= 0) text = text.Substring(0, hash);
                text = text.Trim();
                if (text.Length == 0) continue;
                var tok = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                all.Add(new Line { No = lineNo, Raw = raw.Trim(), Tok = tok, Cmd = tok[0].ToLowerInvariant() });
            }

            var chart = new ChartData();
            var tempo = new TempoMap();
            bool haveHeader = false, easy = false;

            // 1차: 헤더와 템포 — 음표 위치를 ms 로 바꾸려면 먼저 다 알아야 한다
            foreach (var l in all)
            {
                Guard(file, l, () =>
                {
                    switch (l.Cmd)
                    {
                        case "chart":
                            Need(l.Tok, 3);
                            chart.Bpm = F(l.Tok[1]);
                            chart.Beats = F(l.Tok[2]);
                            if (!tempo.HasInitial) tempo.SetInitial(chart.Bpm, chart.Beats);
                            haveHeader = true;
                            break;

                        case "bpm":
                            easy = true;
                            Need(l.Tok, 2);
                            if (IsPosition(l.Tok[1]))
                            {
                                Need(l.Tok, 3);
                                tempo.AddChange(Pos.Parse(l.Tok[1]), D(l.Tok[2]), l.Tok.Length > 3 ? F(l.Tok[3]) : (float?)null);
                            }
                            else
                            {
                                float beats = l.Tok.Length > 2 ? F(l.Tok[2]) : 4f;
                                tempo.SetInitial(D(l.Tok[1]), beats);
                                chart.Bpm = (float)D(l.Tok[1]);
                                chart.Beats = beats;
                                haveHeader = true;
                            }
                            break;

                        case "offset":
                            easy = true;
                            Need(l.Tok, 2);
                            tempo.Offset = D(l.Tok[1]);
                            break;
                    }
                });
            }

            if (!haveHeader) throw new FormatException($"{file}: 'bpm <값>' (또는 'chart <bpm> <박자>') 줄이 없음");
            Guard(file, null, tempo.Resolve);

            // 2차: 노트·이벤트
            var skies = new List<NoteRow>();
            var linked = new Dictionary<NoteRow, NoteRow>();   // 스카이 → 이어지는 앞 스카이
            var named = new Dictionary<NoteRow, string>();     // 스카이 → group=이름
            var easyNotes = new List<NoteRow>();
            long nextGroup = 0;

            foreach (var l in all)
            {
                Guard(file, l, () =>
                {
                    switch (l.Cmd)
                    {
                        case "chart":
                        case "bpm":
                        case "offset":
                            break;

                        case "event":
                        {
                            Need(l.Tok, 5);
                            var e = new EventRow { TD = L(l.Tok[1]), UD_ = I(l.Tok[2]), Kind = I(l.Tok[3]) };
                            Hex(l.Tok[4], e.Payload);
                            chart.Events.Add(e);
                            break;
                        }

                        case "note":
                        {
                            Need(l.Tok, 11);
                            var n = ParseRawNote(l.Tok);
                            chart.Notes.Add(n);
                            nextGroup = Math.Max(nextGroup, n.Group + 1);
                            break;
                        }

                        case "tap":
                        case "hold":
                        {
                            easy = true;
                            bool hold = l.Cmd == "hold";
                            Need(l.Tok, hold ? 4 : 3);
                            var pos = Pos.Parse(l.Tok[1]);
                            double startBeat = tempo.AbsBeat(pos);
                            int start = Ms(tempo, startBeat);
                            int end = start;
                            if (hold)
                            {
                                double len = Num(l.Tok[3]);
                                if (len <= 0) throw new FormatException("홀드 길이는 0보다 커야 함");
                                end = Ms(tempo, startBeat + len);
                            }
                            var (lane, width) = Lane(l.Tok[2]);
                            int side = lane == 0 ? SideLeftOuter : lane == 5 ? SideRightOuter : SideBottom;
                            var n = new NoteRow
                            {
                                Side = side, Type = hold ? TypeHold : TypeTap, Start = start, End = end,
                                StartX = new Coord(lane, 4), EndX = new Coord(lane, 4),
                                StartW = new Coord(width, 4), EndW = new Coord(width, 4),
                                HE = lane, Ie = lane + width - 1,
                            };
                            easyNotes.Add(n);
                            chart.Notes.Add(n);
                            break;
                        }

                        case "flick":
                        {
                            easy = true;
                            Need(l.Tok, 5);
                            int start = Ms(tempo, tempo.AbsBeat(Pos.Parse(l.Tok[1])));
                            var x = Frac(l.Tok[2]);
                            var w = Frac(l.Tok[3]);
                            if (w.Value <= 0) throw new FormatException("플릭 폭은 0보다 커야 함");
                            string dir = l.Tok[4].ToUpperInvariant();
                            int he = dir == "L" ? FlickLeft : dir == "R" ? FlickRight
                                   : throw new FormatException($"플릭 방향은 L 또는 R: '{l.Tok[4]}'");
                            var n = new NoteRow
                            {
                                Side = SideSky, Type = TypeFlick, Start = start, End = start,
                                StartX = x, EndX = x, StartW = w, EndW = w, He_ = he,
                            };
                            easyNotes.Add(n);
                            chart.Notes.Add(n);
                            break;
                        }

                        case "sky":
                        {
                            easy = true;
                            Need(l.Tok, 6);
                            double startBeat = tempo.AbsBeat(Pos.Parse(l.Tok[1]));
                            double len = Num(l.Tok[2]);
                            if (len <= 0) throw new FormatException("스카이 길이는 0보다 커야 함");
                            var n = new NoteRow
                            {
                                Side = SideSky, Type = TypeSky,
                                Start = Ms(tempo, startBeat), End = Ms(tempo, startBeat + len),
                                StartX = Frac(l.Tok[3]), EndX = Frac(l.Tok[4]), StartW = Frac(l.Tok[5]),
                            };
                            n.EndW = n.StartW;
                            int easeA = 0, easeB = 0;
                            string groupName = null;
                            for (int i = 6; i < l.Tok.Length; i++)
                            {
                                var (key, v) = KeyValue(l.Tok[i]);
                                switch (key)
                                {
                                    case "w2": n.EndW = Frac(v); break;
                                    case "ease":
                                        if (v.Length != 2 || v[0] < '0' || v[0] > '2' || v[1] < '0' || v[1] > '2')
                                            throw new FormatException($"ease 는 00~22 두 자리: '{v}'");
                                        easeA = v[0] - '0';
                                        easeB = v[1] - '0';
                                        break;
                                    case "group": groupName = v; break;
                                    default: throw new FormatException($"sky 에 없는 키 '{key}' (w2 · ease · group)");
                                }
                            }
                            n.He_ = (4 << easeA) | (32 << easeB);

                            if (groupName != null)
                            {
                                named[n] = groupName;
                            }
                            else
                            {
                                // 앞 스카이가 끝난 시각·위치에서 시작하면 같은 줄
                                NoteRow prev = null;
                                for (int i = skies.Count - 1; i >= 0; i--)
                                {
                                    if (skies[i].End == n.Start && Math.Abs(skies[i].EndX.Value - n.StartX.Value) < 1e-6) { prev = skies[i]; break; }
                                }
                                if (prev != null) linked[n] = prev;
                            }
                            skies.Add(n);
                            easyNotes.Add(n);
                            chart.Notes.Add(n);
                            break;
                        }

                        case "speed":
                        {
                            easy = true;
                            Need(l.Tok, 3);
                            var e = new EventRow { UD_ = Ms(tempo, tempo.AbsBeat(Pos.Parse(l.Tok[1]))), Kind = EventSpeed };
                            BitConverter.GetBytes(D(l.Tok[2])).CopyTo(e.Payload, 0);
                            chart.Events.Add(e);
                            break;
                        }

                        case "lane":
                        {
                            easy = true;
                            Need(l.Tok, 4);
                            int laneNo = I(l.Tok[2]);
                            if (laneNo != 1 && laneNo != 4) throw new FormatException("lane 은 1번 또는 4번 레인만 (게임 차트 기준)");
                            string act = l.Tok[3].ToLowerInvariant();
                            bool close = act == "close" || act == "1" ? true
                                       : act == "open" || act == "0" ? false
                                       : throw new FormatException($"close 또는 open: '{l.Tok[3]}'");
                            var e = new EventRow { UD_ = Ms(tempo, tempo.AbsBeat(Pos.Parse(l.Tok[1]))), Kind = EventLane };
                            e.Payload[0] = (byte)laneNo;
                            e.Payload[4] = close ? (byte)1 : (byte)0;
                            chart.Events.Add(e);
                            break;
                        }

                        default:
                            throw new FormatException($"알 수 없는 명령 '{l.Tok[0]}'");
                    }
                });
            }

            if (easy)
            {
                // BPM 변경은 게임 차트처럼 이벤트(종류 1)로도 넣는다 — 박자선 표시용
                foreach (var c in tempo.Changes)
                {
                    var e = new EventRow { UD_ = Ms(tempo, c.AbsBeat), Kind = EventBpm };
                    BitConverter.GetBytes(c.Bpm).CopyTo(e.Payload, 0);
                    BitConverter.GetBytes(c.Beats ?? tempo.MeterAtBeat(c.AbsBeat)).CopyTo(e.Payload, 8);
                    chart.Events.Add(e);
                }

                AssignGroups(easyNotes, linked, named, nextGroup);
                StableSort(chart.Notes, (a, b) => a.Start.CompareTo(b.Start));
                for (int i = 0; i < chart.Notes.Count; i++) chart.Notes[i].Id = i;
                StableSort(chart.Events, (a, b) => a.UD_.CompareTo(b.UD_));
                for (int i = 0; i < chart.Events.Count; i++) chart.Events[i].TD = i;
            }

            return chart;
        }

        /// <summary>
        /// 쉬운 문법 노트의 group 번호(원본 형식 노트 번호 다음부터) — 스카이는 이어진 줄끼리·같은 이름끼리 같게, 나머지는 하나씩.
        /// 파일 순서대로 매기므로 이어지는 앞 스카이는 항상 먼저 번호를 받는다.
        /// </summary>
        private static void AssignGroups(List<NoteRow> notes, Dictionary<NoteRow, NoteRow> linked, Dictionary<NoteRow, string> named, long next)
        {
            var nameIds = new Dictionary<string, long>();
            foreach (var n in notes)
            {
                if (named.TryGetValue(n, out var name))
                {
                    if (!nameIds.TryGetValue(name, out long g)) nameIds[name] = g = next++;
                    n.Group = g;
                }
                else if (linked.TryGetValue(n, out var prev))
                {
                    n.Group = prev.Group;
                }
                else
                {
                    n.Group = next++;
                }
            }
        }

        private static NoteRow ParseRawNote(string[] tok)
        {
            var n = new NoteRow
            {
                Id = L(tok[1]),
                Group = L(tok[2]),
                Side = I(tok[3]),
                Type = I(tok[4]),
                Start = I(tok[5]),
                End = I(tok[6]),
                StartX = C(tok[7]),
                EndX = C(tok[8]),
                StartW = C(tok[9]),
                EndW = C(tok[10]),
            };

            for (int i = 11; i < tok.Length; i++)
            {
                var (key, v) = KeyValue(tok[i]);
                // 대소문자로 필드를 가른다 (게임 이름 그대로: Ee fe Fe ge Ge he He ie)
                switch (key)
                {
                    case "Ee": n.Ee = D(v); break;
                    case "fe": n.Fe_ = D(v); break;
                    case "Fe": n.FE = D(v); break;
                    case "ge": n.Ge_ = L(v); break;
                    case "Ge": n.GE = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "he": n.He_ = I(v); break;
                    case "He": n.HE = I(v); break;
                    case "ie": n.Ie = I(v); break;
                    default: throw new FormatException($"알 수 없는 키 '{key}'");
                }
            }
            return n;
        }

        // ─────────────────────────────────────────────────────────────── 위치·템포

        private struct Pos
        {
            public bool IsMs;
            public double Ms;
            public int Measure;
            public double Beat;

            public static Pos Parse(string s)
            {
                if (s.StartsWith("@", StringComparison.Ordinal)) return new Pos { IsMs = true, Ms = D(s.Substring(1)) };
                int colon = s.IndexOf(':');
                if (colon <= 0) throw new FormatException($"위치는 '마디:박' 또는 '@ms': '{s}'");
                var p = new Pos { Measure = I(s.Substring(0, colon)), Beat = Num(s.Substring(colon + 1)) };
                if (p.Measure < 1) throw new FormatException($"마디는 1부터: '{s}'");
                if (p.Beat < 1) throw new FormatException($"박은 1부터: '{s}'");
                return p;
            }
        }

        private static bool IsPosition(string s) => s.StartsWith("@", StringComparison.Ordinal) || s.IndexOf(':') > 0;

        private sealed class TempoChange
        {
            public Pos At;
            public double Bpm;
            public float? Beats;
            public double AbsBeat;
        }

        /// <summary>
        /// 마디:박 → 절대 박 → ms. 박자(한 마디 박 수)는 마디 첫 박에서만 바뀌고, BPM 은 아무 박에서나 바뀐다.
        /// ms(b) = offset + Σ 구간 길이(박) × 60000 / BPM.
        /// </summary>
        private sealed class TempoMap
        {
            public double Offset;
            public bool HasInitial;
            private double _bpm0;
            private float _beats0 = 4;
            private readonly List<TempoChange> _changes = new();
            private readonly List<(int measure, float beats)> _meters = new();
            private readonly List<(double beat, double bpm)> _points = new();

            public IReadOnlyList<TempoChange> Changes => _changes;

            public void SetInitial(double bpm, float beats)
            {
                if (bpm <= 0) throw new FormatException("BPM 은 0보다 커야 함");
                if (beats <= 0) throw new FormatException("박자는 0보다 커야 함");
                _bpm0 = bpm;
                _beats0 = beats;
                HasInitial = true;
            }

            public void AddChange(Pos at, double bpm, float? beats)
            {
                if (bpm <= 0) throw new FormatException("BPM 은 0보다 커야 함");
                if (at.IsMs) throw new FormatException("BPM 변경 위치는 '마디:박' 으로 (@ms 불가)");
                if (beats.HasValue && at.Beat != 1) throw new FormatException("박자 변경은 마디 첫 박(마디:1)에서만");
                _changes.Add(new TempoChange { At = at, Bpm = bpm, Beats = beats });
            }

            public void Resolve()
            {
                if (!HasInitial) throw new FormatException("처음 BPM ('bpm <값>') 이 없음");

                // 박자 변경은 마디 번호로 정해지므로 먼저
                foreach (var c in _changes)
                {
                    if (c.Beats.HasValue) _meters.Add((c.At.Measure, c.Beats.Value));
                }
                _meters.Sort((a, b) => a.measure.CompareTo(b.measure));

                // BPM 변경 위치를 절대 박으로
                _points.Add((0, _bpm0));
                foreach (var c in _changes)
                {
                    c.AbsBeat = AbsBeatOf(c.At.Measure, c.At.Beat);
                    _points.Add((c.AbsBeat, c.Bpm));
                }
                _points.Sort((a, b) => a.beat.CompareTo(b.beat));
            }

            public float MeterAt(int measure)
            {
                float m = _beats0;
                foreach (var (mm, beats) in _meters) if (mm <= measure) m = beats;
                return m;
            }

            public float MeterAtBeat(double absBeat)
            {
                int measure = 1;
                double b = 0;
                while (b + MeterAt(measure) <= absBeat + 1e-9) { b += MeterAt(measure); measure++; }
                return MeterAt(measure);
            }

            private double AbsBeatOf(int measure, double beat)
            {
                double b = 0;
                for (int m = 1; m < measure; m++) b += MeterAt(m);
                float meter = MeterAt(measure);
                if (beat > meter + 1 - 1e-9) throw new FormatException($"{measure}마디는 {meter}박인데 {beat}박을 가리킴");
                return b + (beat - 1);
            }

            public double AbsBeat(Pos p) => p.IsMs ? BeatAtMs(p.Ms) : AbsBeatOf(p.Measure, p.Beat);

            public double MsAt(double absBeat)
            {
                double ms = Offset, beat = 0, bpm = _bpm0;
                if (absBeat < 0) return Offset + absBeat * 60000.0 / _bpm0;
                foreach (var (pb, pbpm) in _points)
                {
                    if (pb <= beat) { bpm = pbpm; continue; }
                    if (pb >= absBeat) break;
                    ms += (pb - beat) * 60000.0 / bpm;
                    beat = pb;
                    bpm = pbpm;
                }
                return ms + (absBeat - beat) * 60000.0 / bpm;
            }

            public double BeatAtMs(double ms)
            {
                if (ms < Offset) return (ms - Offset) * _bpm0 / 60000.0;
                double t = Offset, beat = 0, bpm = _bpm0;
                foreach (var (pb, pbpm) in _points)
                {
                    if (pb <= beat) { bpm = pbpm; continue; }
                    double segEnd = t + (pb - beat) * 60000.0 / bpm;
                    if (segEnd >= ms) break;
                    t = segEnd;
                    beat = pb;
                    bpm = pbpm;
                }
                return beat + (ms - t) * bpm / 60000.0;
            }
        }

        private static int Ms(TempoMap tempo, double absBeat) => (int)Math.Round(tempo.MsAt(absBeat), MidpointRounding.AwayFromZero);

        // ─────────────────────────────────────────────────────────────── 쓰기 (원본 형식)

        public static string Format(ChartData chart, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine($"# In Falsus 차트 텍스트 (원본 형식) — 노트 {chart.Notes.Count}개, 이벤트 {chart.Events.Count}개");
            sb.AppendLine("# note <id> <group> <side> <type> <판정시작ms> <판정끝ms> <시작X> <끝X> <시작폭> <끝폭> [Ee fe Fe ge Ge he He ie]");
            sb.AppendLine("#   side: 1=하단 4레인 2=왼쪽 바깥 3=오른쪽 바깥 4=스카이   type: 1=탭 2=홀드 4=플릭 5=스카이");
            sb.AppendLine("# event <순번> <ms> <종류 0속도 1BPM 2BPM계열 3레인> <0x10~0x1F 16바이트 hex>   (# 뒤는 해석 — 읽을 때 무시)");
            sb.AppendLine();
            sb.AppendLine($"chart {R(chart.Bpm)} {R(chart.Beats)}");
            sb.AppendLine();

            foreach (var e in chart.Events)
            {
                sb.Append($"event {e.TD} {e.UD_} {e.Kind} {ToHex(e.Payload)}");
                sb.AppendLine($"   # b={e.PayloadByte} flag={(e.PayloadFlag ? 1 : 0)} | d={R(e.PayloadDouble)} f={R(e.PayloadFloat)}");
            }
            if (chart.Events.Count > 0) sb.AppendLine();

            foreach (var n in chart.Notes)
            {
                sb.Append($"note {n.Id} {n.Group} {n.Side} {n.Type} {n.Start} {n.End} {n.StartX} {n.EndX} {n.StartW} {n.EndW}");
                sb.AppendLine($" Ee={R(n.Ee)} fe={R(n.Fe_)} Fe={R(n.FE)} ge={n.Ge_} Ge={(n.GE ? 1 : 0)} he={n.He_} He={n.HE} ie={n.Ie}");
            }
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────── 도우미

        private static void Guard(string file, Line l, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentException)
            {
                string where = l != null ? $"{file}:{l.No}" : file;
                string what = l != null ? $" — '{l.Raw}'" : "";
                throw new FormatException($"{where}: {ex.Message}{what}", ex);
            }
        }

        private static void Need(string[] tok, int n)
        {
            if (tok.Length < n) throw new FormatException($"인자가 {n - 1}개 필요한데 {tok.Length - 1}개");
        }

        private static (string key, string value) KeyValue(string s)
        {
            int eq = s.IndexOf('=');
            if (eq <= 0) throw new FormatException($"'키=값' 이 아님: '{s}'");
            return (s.Substring(0, eq), s.Substring(eq + 1));
        }

        /// <summary>'2' → (2, 1), '1-3' → (1, 3). 0·5 는 바깥 레인이라 한 칸만.</summary>
        private static (int lane, int width) Lane(string s)
        {
            int dash = s.IndexOf('-');
            if (dash < 0)
            {
                int lane = I(s);
                if (lane < 0 || lane > 5) throw new FormatException($"레인은 0~5: '{s}'");
                return (lane, 1);
            }
            int a = I(s.Substring(0, dash)), b = I(s.Substring(dash + 1));
            if (a < 1 || b > 4 || a > b) throw new FormatException($"넓은 노트는 1~4 안에서 '시작-끝': '{s}'");
            return (a, b - a + 1);
        }

        /// <summary>'1.5' · '3/2' · '1+1/3' → 수. 박·길이용.</summary>
        private static double Num(string s)
        {
            double sum = 0;
            foreach (var part in s.Split('+'))
            {
                if (part.Length == 0) throw new FormatException($"수가 아님: '{s}'");
                int slash = part.IndexOf('/');
                if (slash < 0) sum += D(part);
                else
                {
                    double den = D(part.Substring(slash + 1));
                    if (den == 0) throw new FormatException($"분모 0: '{s}'");
                    sum += D(part.Substring(0, slash)) / den;
                }
            }
            return sum;
        }

        /// <summary>가로 좌표·폭: '6/12' 는 그대로, '0.5' 같은 소수는 가장 작은 분모(≤ 64)로 — 안 되면 /1000.</summary>
        private static Coord Frac(string s)
        {
            if (s.IndexOf('/') > 0) return C(s);
            double v = D(s);
            for (int den = 1; den <= 64; den++)
            {
                double num = v * den;
                if (Math.Abs(num - Math.Round(num)) < 1e-9) return new Coord((int)Math.Round(num), den);
            }
            return new Coord((int)Math.Round(v * 1000), 1000);
        }

        private static void StableSort<T>(List<T> list, Comparison<T> cmp)
        {
            var indexed = new List<(T item, int i)>(list.Count);
            for (int i = 0; i < list.Count; i++) indexed.Add((list[i], i));
            indexed.Sort((a, b) => { int c = cmp(a.item, b.item); return c != 0 ? c : a.i.CompareTo(b.i); });
            for (int i = 0; i < list.Count; i++) list[i] = indexed[i].item;
        }

        private static int I(string s) => int.Parse(s, NumberStyles.Integer, Inv);
        private static long L(string s) => long.Parse(s, NumberStyles.Integer, Inv);
        private static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);
        private static double D(string s) => double.Parse(s, NumberStyles.Float, Inv);
        private static string R(float v) => v.ToString("R", Inv);
        private static string R(double v) => v.ToString("R", Inv);

        private static Coord C(string s)
        {
            int slash = s.IndexOf('/');
            if (slash <= 0) throw new FormatException($"좌표는 '분자/분모': '{s}'");
            var c = new Coord(I(s.Substring(0, slash)), I(s.Substring(slash + 1)));
            if (c.Den == 0) throw new FormatException($"분모 0: '{s}'");
            return c;
        }

        private static void Hex(string s, byte[] dst)
        {
            if (s.Length != dst.Length * 2) throw new FormatException($"hex 는 {dst.Length * 2}자리여야 함: '{s}'");
            for (int i = 0; i < dst.Length; i++) dst[i] = byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, Inv);
        }

        private static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
