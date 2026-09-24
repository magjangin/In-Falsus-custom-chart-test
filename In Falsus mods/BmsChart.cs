using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace InFalsusMods
{
    /// <summary>
    /// BMS → ChartData 변환 뼈대 (docs/06). 인터롭 타입을 쓰지 않아 게임 없이도 읽을 수 있다.
    ///
    /// 시간: 위치를 마디 단위 tick 으로 잡고 time(초) = Σ Δtick × 240 / bpm. 마디 길이(02)는 그 마디의 tick 폭,
    /// BPM 변경은 03(16진수 값)·08(#BPMxx 참조). 게임 속 노트 시각 = (노트 time − 01 채널 BGM time) × 1000 ms.
    ///
    /// 키음 자릿수와 인코딩은 사용자의 BMS 에디터(`H:\source\repos\bms editer`, Services/BmsParser*.cs)와 같은 규칙:
    /// - 3자리 #WAV 가 하나라도 있으면 줄 길이가 3의 배수(2의 배수 아님)일 때 3자리, 6의 배수면 #WAV 표에 더 많이 맞는 쪽.
    /// - 인코딩은 BOM → 엄격 UTF-8 → CP949.
    ///
    /// 정해진 것만 넣는다 (2026-09-24 사용자 결정):
    /// - 하단: 16 = 왼쪽 바깥, 11~14 = A S D F. 노트 종류는 WAV 파일 이름(홀드·홀드 끝 키워드, 나머지는 단노트).
    /// - 스카이 채널 15·18 은 좌우 위치 규칙이 아직 없어 개수만 세고 넣지 않는다. 오른쪽 바깥 레인 채널도 미정.
    /// </summary>
    internal sealed class BmsResult
    {
        public ChartData Chart;
        public readonly List<string> Warnings = new();
        public int Singles, Holds, SkySkipped, KeyWidth;
        public double BgmOffsetMs;
        public string Title, Artist, PlayLevel, EncodingName;

        public string Summary =>
            $"단노트 {Singles}, 홀드 {Holds}, 스카이(미반영) {SkySkipped}, BPM {Chart.Bpm}, BPM 변경 {Chart.Events.Count}, " +
            $"BGM 오프셋 {BgmOffsetMs:0.###}ms, 키 {KeyWidth}자리, 인코딩 {EncodingName ?? "-"}";
    }

    internal static class BmsChart
    {
        // 채널 → 하단 레인 (0 = 왼쪽 바깥, 1~4 = A S D F). 오른쪽 바깥(레인 5)은 채널 미정.
        private static readonly Dictionary<string, int> BottomLanes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["16"] = 0, ["11"] = 1, ["12"] = 2, ["13"] = 3, ["14"] = 4,
        };

        // 스카이 — 좌우 위치를 어디에 담을지 미정 (docs/06 결정 목록)
        private static readonly HashSet<string> SkyChannels = new(StringComparer.OrdinalIgnoreCase) { "15", "18" };

        // 노트 종류 — WAV 파일 이름 키워드 (대소문자 무시, 부분 일치). 끝을 먼저 본다('홀드 끝' 에는 '홀드' 도 들어 있다).
        private static readonly string[] HoldEndKeywords = { "홀드 끝", "홀드끝", "hold end", "holdend", "hold_end" };
        private static readonly string[] HoldStartKeywords = { "홀드", "hold" };

        private static readonly string[] MusicExtensions = { ".ogg", ".mp3", ".flac", ".wav" };
        private static readonly Regex DataLine = new(@"^#(\d{3})([0-9A-Za-z]{2}):(.*)$", RegexOptions.Compiled);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private enum Kind { Single, HoldStart, HoldEnd }

        private sealed class Obj
        {
            public int Measure;
            public double Frac;      // 마디 안 위치 0~1
            public string Channel;
            public string Key;
            public double Tick;      // 마디 단위 절대 위치
        }

        public static BmsResult Load(string path)
        {
            var lines = ReadLines(path, out var encoding);
            var r = Parse(lines, path);
            r.EncodingName = encoding;
            return r;
        }

        public static BmsResult Parse(IEnumerable<string> lines, string source = "")
        {
            var r = new BmsResult();
            string file = Path.GetFileName(source);

            double baseBpm = 130;   // #BPM 이 없을 때 BMS 관례값
            bool haveBpm = false;
            var bpmTable = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var wavTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var measureLen = new Dictionary<int, double>();
            var rawData = new List<(int measure, string channel, string data, int lineNo)>();

            int lineNo = 0;
            foreach (var raw in lines)
            {
                lineNo++;
                string line = raw.Trim();
                if (!line.StartsWith("#", StringComparison.Ordinal)) continue;

                var m = DataLine.Match(line);
                if (m.Success)
                {
                    rawData.Add((int.Parse(m.Groups[1].Value, Inv), m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value.Trim(), lineNo));
                    continue;
                }

                int sp = IndexOfSpace(line);
                string head = (sp < 0 ? line : line.Substring(0, sp)).ToUpperInvariant();
                string value = sp < 0 ? "" : line.Substring(sp + 1).Trim();

                if (head == "#BPM")
                {
                    if (TryDouble(value, out var v) && v > 0) { baseBpm = v; haveBpm = true; }
                    else r.Warnings.Add($"{file}:{lineNo}: #BPM 값을 못 읽음 '{value}'");
                }
                else if (head.StartsWith("#BPM", StringComparison.Ordinal) && head.Length is 6 or 7)
                {
                    if (TryDouble(value, out var v) && v > 0) bpmTable[head.Substring(4)] = v;
                    else r.Warnings.Add($"{file}:{lineNo}: {head} 값을 못 읽음 '{value}'");
                }
                else if (head.StartsWith("#WAV", StringComparison.Ordinal) && head.Length is 6 or 7)
                {
                    wavTable[head.Substring(4)] = value;
                }
                else if (head == "#TITLE") r.Title = value;
                else if (head == "#ARTIST") r.Artist = value;
                else if (head == "#PLAYLEVEL") r.PlayLevel = value;
                else if (head == "#LNOBJ" || head == "#LNTYPE")
                {
                    r.Warnings.Add($"{file}:{lineNo}: {head} 는 안 씀 — 홀드는 WAV 이름(홀드 / 홀드 끝)으로 찍을 것");
                }
                else if (head is "#RANDOM" or "#IF" or "#SWITCH" or "#SETRANDOM")
                {
                    r.Warnings.Add($"{file}:{lineNo}: {head} 조건 블록은 지원 안 함 — 모든 갈래를 다 읽음");
                }
            }

            if (!haveBpm) r.Warnings.Add($"{file}: #BPM 없음 — {baseBpm} 으로 계산");

            bool has3Digit = false, bpm3Digit = false;
            foreach (var k in wavTable.Keys) if (k.Length == 3) has3Digit = true;
            foreach (var k in bpmTable.Keys) if (k.Length == 3) bpm3Digit = true;
            r.KeyWidth = has3Digit ? 3 : 2;

            // 마디 길이(02) 먼저 — tick 계산에 필요
            foreach (var (measure, channel, data, ln) in rawData)
            {
                if (channel != "02") continue;
                if (TryDouble(data, out var len) && len > 0) measureLen[measure] = len;
                else r.Warnings.Add($"{file}:{ln}: 마디 길이(02) 값을 못 읽음 '{data}'");
            }

            int maxMeasure = 0;
            foreach (var d in rawData) maxMeasure = Math.Max(maxMeasure, d.measure);
            var measureStart = new double[maxMeasure + 2];
            for (int i = 1; i < measureStart.Length; i++) measureStart[i] = measureStart[i - 1] + Len(measureLen, i - 1);

            // 오브젝트 펼치기
            var objs = new List<Obj>();
            var bpmChanges = new List<(double tick, double bpm)>();
            foreach (var (measure, channel, data, ln) in rawData)
            {
                if (channel == "02") continue;

                int width = channel == "03" ? 2                                    // 16진수 BPM 값은 항상 2자리
                          : channel == "08" ? ChunkSize(data, bpm3Digit, bpmTable.ContainsKey)       // #BPMxx 참조
                          : ChunkSize(data, has3Digit, wavTable.ContainsKey);
                int count = data.Length / width;
                if (count == 0) continue;
                if (data.Length % width != 0) r.Warnings.Add($"{file}:{ln}: 데이터 길이 {data.Length} 가 {width}자리로 안 나눠짐 — 끝 조각 버림");

                for (int i = 0; i < count; i++)
                {
                    string key = data.Substring(i * width, width).ToUpperInvariant();
                    if (IsZero(key)) continue;
                    double tick = measureStart[measure] + Len(measureLen, measure) * i / count;

                    if (channel == "03")
                    {
                        if (int.TryParse(key, NumberStyles.HexNumber, Inv, out int hex) && hex > 0) bpmChanges.Add((tick, hex));
                        continue;
                    }
                    if (channel == "08")
                    {
                        if (bpmTable.TryGetValue(key, out var bpm)) bpmChanges.Add((tick, bpm));
                        else r.Warnings.Add($"{file}:{ln}: 08 채널이 없는 #BPM{key} 를 가리킴");
                        continue;
                    }
                    objs.Add(new Obj { Measure = measure, Frac = (double)i / count, Channel = channel, Key = key, Tick = tick });
                }
            }

            var timeline = new Timeline(baseBpm, bpmChanges);

            // BGM(01): 음원 파일을 가리키는 가장 이른 오브젝트가 음원 0ms
            double bgmSec = double.NaN;
            foreach (var o in objs)
            {
                if (o.Channel != "01" || !wavTable.TryGetValue(o.Key, out var name) || !IsMusic(name)) continue;
                double t = timeline.Seconds(o.Tick);
                if (double.IsNaN(bgmSec) || t < bgmSec) bgmSec = t;
            }
            if (double.IsNaN(bgmSec))
            {
                bgmSec = 0;
                r.Warnings.Add($"{file}: 01 채널에 음원(.ogg 등) BGM 이 없음 — 000마디 시작을 음원 0ms 로 봄");
            }
            r.BgmOffsetMs = bgmSec * 1000;

            // 하단 노트 — 레인별로 시간순, 같은 자리면 끝이 먼저(이어지는 홀드)
            var byLane = new SortedDictionary<int, List<(Obj o, Kind kind)>>();
            var unknownKeys = new HashSet<string>();
            var otherChannels = new SortedDictionary<string, int>();
            foreach (var o in objs)
            {
                if (BottomLanes.TryGetValue(o.Channel, out int lane))
                {
                    if (!wavTable.TryGetValue(o.Key, out var name)) { unknownKeys.Add(o.Key); name = ""; }
                    if (!byLane.TryGetValue(lane, out var list)) byLane[lane] = list = new List<(Obj, Kind)>();
                    list.Add((o, Classify(name)));
                }
                else if (SkyChannels.Contains(o.Channel)) r.SkySkipped++;
                else if (o.Channel != "01") otherChannels[o.Channel] = otherChannels.TryGetValue(o.Channel, out int c) ? c + 1 : 1;
            }

            var chart = new ChartData { Bpm = (float)baseBpm, Beats = (float)(4 * Len(measureLen, 0)) };
            int ToMs(Obj o) => (int)Math.Round((timeline.Seconds(o.Tick) - bgmSec) * 1000, MidpointRounding.AwayFromZero);

            foreach (var (lane, list) in byLane)
            {
                list.Sort((a, b) =>
                {
                    int c = a.o.Tick.CompareTo(b.o.Tick);
                    return c != 0 ? c : (b.kind == Kind.HoldEnd).CompareTo(a.kind == Kind.HoldEnd);
                });

                Obj pending = null;
                foreach (var (o, kind) in list)
                {
                    switch (kind)
                    {
                        case Kind.Single:
                            chart.Notes.Add(Bottom(lane, ToMs(o), ToMs(o), hold: false));
                            r.Singles++;
                            break;
                        case Kind.HoldStart:
                            if (pending != null) r.Warnings.Add($"{file}: {Where(pending)} 홀드 시작이 끝 없이 다시 시작됨 — 앞 시작 버림");
                            pending = o;
                            break;
                        case Kind.HoldEnd:
                            if (pending == null) { r.Warnings.Add($"{file}: {Where(o)} 짝 없는 홀드 끝 — 버림"); break; }
                            chart.Notes.Add(Bottom(lane, ToMs(pending), ToMs(o), hold: true));
                            r.Holds++;
                            pending = null;
                            break;
                    }
                }
                if (pending != null) r.Warnings.Add($"{file}: {Where(pending)} 끝 없는 홀드 시작 — 버림");
            }

            if (unknownKeys.Count > 0) r.Warnings.Add($"{file}: #WAV 에 없는 키음 {string.Join(", ", unknownKeys)} — 단노트로 처리");
            if (r.SkySkipped > 0) r.Warnings.Add($"{file}: 스카이 채널(15·18) 노트 {r.SkySkipped}개 — 좌우 위치 규칙이 정해지기 전이라 넣지 않음");
            foreach (var kv in otherChannels) r.Warnings.Add($"{file}: 안 쓰는 채널 {kv.Key} 노트 {kv.Value}개 무시");

            foreach (var n in chart.Notes)
            {
                if (n.Start < 0) { r.Warnings.Add($"{file}: 음원 시작(BGM)보다 앞선 노트가 있음 ({n.Start}ms)"); break; }
            }

            // BPM 변경은 게임 차트처럼 이벤트(종류 1: double BPM, float 박자)로도 넣는다 — 박자선 표시용
            foreach (var (tick, bpm) in timeline.Changes)
            {
                var e = new EventRow { UD_ = (int)Math.Round((timeline.Seconds(tick) - bgmSec) * 1000, MidpointRounding.AwayFromZero), Kind = 1 };
                BitConverter.GetBytes(bpm).CopyTo(e.Payload, 0);
                BitConverter.GetBytes((float)(4 * Len(measureLen, MeasureAt(measureStart, tick)))).CopyTo(e.Payload, 8);
                chart.Events.Add(e);
            }

            chart.Notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i < chart.Notes.Count; i++) { chart.Notes[i].Id = i; chart.Notes[i].Group = i; }
            chart.Events.Sort((a, b) => a.UD_.CompareTo(b.UD_));
            for (int i = 0; i < chart.Events.Count; i++) chart.Events[i].TD = i;

            r.Chart = chart;
            return r;
        }

        // ─────────────────────────────────────────────────────────────── 시간

        /// <summary>tick(마디 단위) → 초. 구간마다 Δtick × 240 / bpm 을 더한다. 같은 자리에 변경이 여럿이면 뒤의 것.</summary>
        private sealed class Timeline
        {
            private readonly double _bpm0;
            public readonly List<(double tick, double bpm)> Changes = new();

            public Timeline(double bpm0, List<(double tick, double bpm)> changes)
            {
                _bpm0 = bpm0;
                // 안정 정렬 — 같은 자리 변경은 파일에 나온 순서 그대로 두고 뒤의 것이 이긴다
                foreach (var c in changes.OrderBy(c => c.tick))
                {
                    if (Changes.Count > 0 && Changes[^1].tick == c.tick) Changes[^1] = c;
                    else Changes.Add(c);
                }
            }

            public double Seconds(double tick)
            {
                double sec = 0, at = 0, bpm = _bpm0;
                foreach (var (t, b) in Changes)
                {
                    if (t >= tick) break;
                    sec += (t - at) * 240.0 / bpm;
                    at = t;
                    bpm = b;
                }
                return sec + (tick - at) * 240.0 / bpm;
            }
        }

        private static double Len(Dictionary<int, double> lens, int measure) => lens.TryGetValue(measure, out var l) ? l : 1.0;

        private static int MeasureAt(double[] starts, double tick)
        {
            int m = 0;
            while (m + 1 < starts.Length && starts[m + 1] <= tick + 1e-9) m++;
            return m;
        }

        // ─────────────────────────────────────────────────────────────── 노트

        private static Kind Classify(string wavName)
        {
            string n = Path.GetFileNameWithoutExtension(wavName ?? "").ToLowerInvariant();
            foreach (var k in HoldEndKeywords) if (n.Contains(k)) return Kind.HoldEnd;
            foreach (var k in HoldStartKeywords) if (n.Contains(k)) return Kind.HoldStart;
            return Kind.Single;
        }

        private static NoteRow Bottom(int lane, int start, int end, bool hold)
        {
            return new NoteRow
            {
                Side = lane == 0 ? 2 : lane == 5 ? 3 : 1,   // 2 = 왼쪽 바깥, 3 = 오른쪽 바깥, 1 = 하단 4레인
                Type = hold ? 2 : 1,                        // 1 = 단노트, 2 = 홀드
                Start = start,
                End = end,
                StartX = new Coord(lane, 4), EndX = new Coord(lane, 4),
                StartW = new Coord(1, 4), EndW = new Coord(1, 4),
                HE = lane, Ie = lane,
            };
        }

        private static string Where(Obj o) => $"#{o.Measure:000}{o.Channel} ({o.Frac:0.###})";

        // ─────────────────────────────────────────────────────────────── 파일·자릿수

        /// <summary>에디터(BmsParser.DetermineChunkSize)와 같은 규칙. defined = 그 키가 표(#WAV 또는 #BPM)에 있는지.</summary>
        private static int ChunkSize(string data, bool has3Digit, Func<string, bool> defined)
        {
            if (!has3Digit) return 2;
            if (data.Length % 3 == 0 && data.Length % 2 != 0) return 3;
            if (data.Length % 6 == 0)
            {
                int hit2 = 0, hit3 = 0;
                for (int i = 0; i + 2 <= data.Length; i += 2)
                {
                    string k = data.Substring(i, 2);
                    if (!IsZero(k) && defined(k)) hit2++;
                }
                for (int i = 0; i + 3 <= data.Length; i += 3)
                {
                    string k = data.Substring(i, 3);
                    if (!IsZero(k) && defined(k)) hit3++;
                }
                if (hit3 > hit2) return 3;
            }
            return 2;
        }

        private static bool IsZero(string key)
        {
            foreach (char c in key) if (c != '0') return false;
            return true;
        }

        private static bool IsMusic(string wavName)
        {
            string ext = Path.GetExtension(wavName ?? "").ToLowerInvariant();
            foreach (var e in MusicExtensions) if (ext == e) return ext != ".wav" || wavName.ToLowerInvariant().Contains("music");
            return false;
        }

        private static int IndexOfSpace(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] == ' ' || s[i] == '\t') return i;
            return -1;
        }

        private static bool TryDouble(string s, out double v) => double.TryParse(s, NumberStyles.Float, Inv, out v);

        /// <summary>에디터(BmsParser.Encoding)처럼 BOM → 엄격 UTF-8 → CP949 순으로 읽는다.</summary>
        public static string[] ReadLines(string path, out string encodingName)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                encodingName = "UTF-8 (BOM)";
            }
            else
            {
                try
                {
                    text = new UTF8Encoding(false, true).GetString(bytes);
                    encodingName = "UTF-8";
                }
                catch (DecoderFallbackException)
                {
                    try
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        text = Encoding.GetEncoding(949).GetString(bytes);
                        encodingName = "CP949";
                    }
                    catch (Exception)
                    {
                        text = Encoding.Latin1.GetString(bytes);
                        encodingName = "Latin1 (CP949 못 씀 — 한글 WAV 이름이 깨질 수 있음)";
                    }
                }
            }
            return text.Replace("\r\n", "\n").Split('\n');
        }
    }
}
