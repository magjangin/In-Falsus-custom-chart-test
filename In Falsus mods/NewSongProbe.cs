using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using Il2Cppifapp.Game;
using Il2Cppifapp.Game.Data;
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// 새 곡 슬롯 추가 조사 — 곡을 하나 더 넣으려면 어디를 같이 늘려야 하는지 로그로 남긴다. 게임 데이터는 읽기만 한다.
    ///
    /// 곡 선택 화면에 처음 들어갔을 때 세션당 한 번 찍는다(플레이 중 로그는 비싸다 — docs/04).
    /// 보는 것:
    /// 1. SongData — allSongInfo 와 파생 캐시(_ffb 배열·_Ffb BitArray·_Efb 슬러그→ID·_Dfb/_efb 자켓)가 SongId 로
    ///    인덱싱되는지, ID 에 빈 번호가 있는지. 새 곡 ID 를 어디에 끼울 수 있는지 판단용.
    /// 2. PackData·DLC 구성 — 곡이 어느 팩에 속하고 팩 소유/해금이 어떻게 표시되는지.
    /// 3. DynamicStringMapping — 제목·아티스트 문자열이 SongId 로 따로 붙어 있는지.
    /// 4. StreamingAssetsMapping + 에셋 등록부(_BG) — 논리 경로 → AssetId 표, 파일별 암호화 여부(_UxA 추정).
    /// 5. 배경·영상 에셋 — SongInfo.GameplayBackground 와 VideoFiles.
    /// 6. 세이브(_NH._CEb) — 점수 키가 게임이 모르는 SongId 를 이미 담고 있는지(= 모르는 곡 기록을 견디는지).
    ///
    /// [2차] 1차(2026-09-24 12:11, docs/01 8장)에서 숫자만 나온 것들의 정체 — 더미·팩 밖 곡·_Ffb 켜진 곡·제목만 있는 ID,
    /// 파일 하나에 붙은 경로 키 전부, bool false 인 파일.
    ///
    /// 이름은 1.0.4b 인터롭 DLL 기준으로 확인함(2026-09-24, ilspycmd).
    /// </summary>
    internal static class NewSongProbe
    {
        // 조사가 끝나면 false 로 끌 것
        public static bool Enabled { get; set; } = true;

        // 기준으로 자세히 볼 곡
        private const string SampleSlug = "alamode";

        // 목록을 늘어놓을 때 최대 줄 수
        private const int MaxListed = 12;

        private static bool _done;

        public static void Tick(MelonLogger.Instance log)
        {
            if (!Enabled || _done) return;

            var scene = SceneRefs.SongSelect;
            if (scene == null) return;

            var da = scene.dataAccess;
            var sd = da != null ? da.SongData : null;
            if (sd == null || sd.allSongInfo == null || sd.allSongInfo.Length == 0) return;

            _done = true;

            log.Msg("══════════════════════════════════════════════════════════════════════════");
            log.Msg($"[NewSongProbe] 새 곡 슬롯 추가 조사 (읽기 전용, 기준 곡 '{SampleSlug}')");

            var known = new Dictionary<int, string>();   // SongId → BaseName
            SongInfo sample = null;

            Section(log, "1. 곡 목록·SongId 캐시", () => sample = ProbeSongData(sd, known, log));
            int sampleId = sample != null ? sample.Id.Value : -1;

            Section(log, "1-2. [2차] SongId 정체 — 더미·팩 밖 곡·_Ffb·제목만 있는 ID", () => ProbeIds(sd, da, log));

            Section(log, "2. 팩·DLC", () => ProbePacks(da, sampleId, log));
            Section(log, "3. 제목·아티스트 문자열", () => ProbeStrings(da, known, sampleId, log));
            Section(log, "4. 에셋 매핑·등록부", () => ProbeAssets(da, log));
            Section(log, "5. 배경·영상 에셋", () => ProbeMedia(da, sample, log));
            Section(log, "6. 세이브 점수 기록", () => ProbeSave(known, sampleId, log));

            log.Msg("══════════════════════════════════════════════════════════════════════════");
        }

        /// <summary>섹션 하나가 터져도 나머지는 계속 찍는다.</summary>
        private static void Section(MelonLogger.Instance log, string name, Action body)
        {
            log.Msg($"── {name}");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                log.Warning($"  ! {name} 실패: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────── 1. SongData

        private static SongInfo ProbeSongData(SongData sd, Dictionary<int, string> known, MelonLogger.Instance log)
        {
            var all = sd.allSongInfo;
            SongInfo sample = null;
            int dummies = 0, duplicates = 0;

            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                if (s == null) continue;

                int id = s.Id.Value;
                if (string.IsNullOrWhiteSpace(s.BaseName)) dummies++;
                if (!known.TryAdd(id, s.BaseName ?? string.Empty)) duplicates++;
                if (s.BaseName == SampleSlug) sample = s;
            }

            int min = known.Count > 0 ? known.Keys.Min() : -1;
            int max = known.Count > 0 ? known.Keys.Max() : -1;
            var gaps = new List<int>();
            for (int id = Math.Max(0, min); id <= max; id++)
            {
                if (!known.ContainsKey(id)) gaps.Add(id);
            }

            log.Msg($"  ├ allSongInfo: {all.Length}개 (빈 BaseName {dummies}, 중복 ID {duplicates}), SongId {min} ~ {max}");

            // 2026-09-24 새 곡 주입 1회차에서 드러남 — allSongInfo 는 칸 번호 == SongId 이고 빈 번호 칸은 더미(ID 0)다
            int named = 0, indexedAll = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                if (s == null || string.IsNullOrWhiteSpace(s.BaseName)) continue;
                named++;
                if (s.Id.Value == i) indexedAll++;
            }
            log.Msg($"  ├ allSongInfo 칸 번호 == SongId: 이름 있는 {named}칸 중 {indexedAll}칸 " +
                    $"({(named > 0 && indexedAll == named ? "SongId 인덱싱 — 빈 번호 칸은 더미" : "인덱싱 아님")})");
            log.Msg($"  ├ 빈 SongId {gaps.Count}개: {Preview(gaps)} → 새 곡 ID 후보는 {max + 1} 이상 또는 빈 번호");

            // _ffb: SongId 로 바로 인덱싱하는 배열인지 — 칸 번호 == 그 칸 곡의 ID 인지 본다
            var ffb = sd._ffb;
            if (ffb != null)
            {
                int indexed = 0, filled = 0;
                for (int i = 0; i < ffb.Length; i++)
                {
                    var s = ffb[i];
                    if (s == null || string.IsNullOrEmpty(s.BaseName)) continue;
                    filled++;
                    if (s.Id.Value == i) indexed++;
                }
                log.Msg($"  ├ _ffb: 길이 {ffb.Length}, 채워진 칸 {filled}, 칸 번호 == SongId 인 칸 {indexed} " +
                        $"({(filled > 0 && indexed == filled ? "SongId 로 인덱싱됨 → 새 ID 는 배열을 늘려야 함" : "인덱싱 아님")})");
            }
            else
            {
                log.Msg("  ├ _ffb: null");
            }

            // _Ffb: SongId 별 존재 플래그인지
            var ffbBits = sd._Ffb;
            if (ffbBits != null)
            {
                int len = ffbBits.Length, set = 0, setKnown = 0;
                for (int i = 0; i < len; i++)
                {
                    if (!ffbBits.Get(i)) continue;
                    set++;
                    if (known.ContainsKey(i)) setKnown++;
                }
                log.Msg($"  ├ _Ffb(BitArray): 길이 {len}, 켜진 비트 {set} (그중 실제 곡 ID {setKnown}), " +
                        $"'{SampleSlug}' 비트 {(sample != null && sample.Id.Value < len ? ffbBits.Get(sample.Id.Value).ToString() : "범위 밖")}");
            }

            var efb = sd._Efb;
            string slugLookup = efb != null && efb.ContainsKey(SampleSlug) ? efb[SampleSlug].Value.ToString() : "없음";
            log.Msg($"  ├ _Efb(슬러그→ID): {(efb != null ? efb.Count : -1)}개, '{SampleSlug}' → {slugLookup}");
            log.Msg($"  ├ 자켓: songIdJacketMaterials {Len(sd.songIdJacketMaterials)}개, chartIdJacketMaterials {Len(sd.chartIdJacketMaterials)}개, " +
                    $"_Dfb {(sd._Dfb != null ? sd._Dfb.Count : -1)}개, _efb {(sd._efb != null ? sd._efb.Count : -1)}개");

            if (sample == null)
            {
                log.Msg($"  └ '{SampleSlug}' 없음");
                return null;
            }

            LogSampleSong(sample, log);
            LogSampleJacket(sd, sample, log);
            return sample;
        }

        private static void LogSampleSong(SongInfo s, MelonLogger.Instance log)
        {
            log.Msg($"  ├ [{SampleSlug}] Id {s.Id.Value}, 캐릭터 {s.CharacterIdentifier}, 배경 {s.GameplayBackground}, 보상 {s.RewardStyle}, " +
                    $"프리뷰 {s.PreviewStartSeconds:F2}~{s.PreviewEndSeconds:F2}초");

            var reading = s.LocalizationToTitleReadingOverride;
            string readings = reading == null ? "null" : $"{reading.Length}개 [{string.Join(", ", Enumerable.Range(0, reading.Length).Select(i => $"'{reading[i]}'"))}]";
            log.Msg($"  │   제목 읽기 override {readings}, 아티스트 읽기 override '{s.ArtistReadingOverride}'");

            var charts = s.ChartInfos;
            if (charts == null) return;
            for (int i = 0; i < charts.Length; i++)
            {
                var c = charts[i];
                if (c == null) continue;
                log.Msg($"  │   차트[{i}] '{c.Id}' {c.Difficulty} Rating {c.Rating} ('{c.LevelSectionIndicator}'), 사용 가능 {c.Available}, " +
                        $"채보 '{c.DisplayChartDesigner}', 자켓 '{c.DisplayJacketDesigner}'");
            }
        }

        private static void LogSampleJacket(SongData sd, SongInfo sample, MelonLogger.Instance log)
        {
            var entries = sd.songIdJacketMaterials;
            if (entries == null) return;

            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e == null || e.SongId.Value != sample.Id.Value) continue;

                try
                {
                    log.Msg($"  │   자켓 엔트리[{i}]: Large GUID {e.JacketLargeMaterial?.AssetGUID}, Small GUID {e.JacketSmallMaterial?.AssetGUID}");
                }
                catch (Exception ex)
                {
                    log.Msg($"  │   자켓 엔트리[{i}] 있음 (GUID 읽기 실패: {ex.Message})");
                }
                return;
            }
            log.Msg("  │   자켓 엔트리: songIdJacketMaterials 에 없음 (차트별 자켓만 있거나 Fallback)");
        }

        // ─────────────────────────────────────────────────────────────── 1-2. SongId 정체

        /// <summary>
        /// [2차] 1차에서 숫자만 나온 것들의 정체 — 새 곡에 쓸 SongId 를 고르려면 어떤 번호가 이미 뜻을 갖는지 알아야 한다.
        /// 1차 결과: 더미 10칸, 팩 합계 70 < 실제 곡 73, _Ffb 켜진 비트 3, 제목 매핑 87 > 곡 73.
        /// </summary>
        private static void ProbeIds(SongData sd, DataAccess da, MelonLogger.Instance log)
        {
            // 이름 있는 칸만 실제 곡으로 본다 — 1차의 known 은 더미 ID 가 먼저 들어가면 실제 곡 이름을 가릴 수 있다
            var real = new Dictionary<int, string>();
            var dummyIds = new List<int>();
            var all = sd.allSongInfo;
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                if (s == null) continue;
                if (string.IsNullOrWhiteSpace(s.BaseName)) dummyIds.Add(s.Id.Value);
                else real[s.Id.Value] = s.BaseName;
            }
            log.Msg($"  ├ 실제 곡 {real.Count}개, 더미 칸 {dummyIds.Count}개 — 더미 SongId [{string.Join(", ", dummyIds)}]");

            var inPack = new HashSet<int>();
            var packs = da.PackData != null ? da.PackData.PackInfo : null;
            for (int i = 0; packs != null && i < packs.Length; i++)
            {
                var ids = packs[i] != null ? packs[i].SongIds : null;
                for (int j = 0; ids != null && j < ids.Length; j++) inPack.Add(ids[j].Value);
            }

            var bits = sd._Ffb;
            bool Bit(int id) => bits != null && id >= 0 && id < bits.Length && bits.Get(id);

            var outside = real.Keys.Where(id => !inPack.Contains(id)).OrderBy(id => id).ToList();
            log.Msg($"  ├ 팩 밖 실제 곡 {outside.Count}개: " +
                    string.Join(", ", outside.Select(id => $"{id} '{real[id]}' (_Ffb {Bit(id)})")));

            var setIds = new List<int>();
            for (int i = 0; bits != null && i < bits.Length; i++)
            {
                if (bits.Get(i)) setIds.Add(i);
            }
            log.Msg($"  ├ _Ffb 켜진 비트 {setIds.Count}개: " +
                    string.Join(", ", setIds.Select(id => $"{id} '{(real.TryGetValue(id, out var n) ? n : "(곡 없음)")}' (팩 {(inPack.Contains(id) ? "안" : "밖")})")));

            // 제목만 있고 곡이 없는 ID — 삭제된 곡 또는 예정 곡으로 보인다. 새 곡 ID 는 이것들과 겹치면 안 된다.
            var dsm = da.DynamicStringMapping;
            var titles = dsm != null ? dsm.songIdTitleTypeMapping : null;
            var artists = dsm != null ? dsm.songIdArtistTypeMapping : null;
            var titleIds = titles != null ? titles.Ids : null;
            var titleStrs = titles != null ? titles.IdStr : null;
            if (titleIds == null)
            {
                log.Msg("  └ 제목 매핑: null");
                return;
            }

            int maxTitleId = -1;
            var orphanTitles = new List<string>();
            var withTitle = new HashSet<int>();
            for (int i = 0; i < titleIds.Count; i++)
            {
                int id = titleIds[i].Value;
                withTitle.Add(id);
                maxTitleId = Math.Max(maxTitleId, id);
                if (real.ContainsKey(id)) continue;

                string title = titleStrs != null && i < titleStrs.Count ? titleStrs[i] : "?";
                string artist = "?";
                var key = new SongId { Value = (ushort)id };
                if (artists != null && artists.Mapping != null && artists.Mapping.ContainsKey(key))
                {
                    artist = artists.Mapping[key].English;
                }
                orphanTitles.Add($"{id} '{title}' / '{artist}'{(dummyIds.Contains(id) ? " [더미 칸 ID]" : "")}");
            }

            var noTitle = real.Keys.Where(id => !withTitle.Contains(id)).OrderBy(id => id).ToList();
            log.Msg($"  ├ 제목 매핑 최대 SongId {maxTitleId}, 제목 없는 실제 곡 {noTitle.Count}개 [{string.Join(", ", noTitle.Select(id => $"{id} '{real[id]}'"))}]");
            log.Msg($"  └ 곡 없이 제목만 있는 SongId {orphanTitles.Count}개:");
            foreach (var row in orphanTitles) log.Msg($"      {row}");
        }

        // ─────────────────────────────────────────────────────────────── 2. PackData

        private static void ProbePacks(DataAccess da, int sampleId, MelonLogger.Instance log)
        {
            var pd = da.PackData;
            if (pd == null)
            {
                log.Msg("  └ PackData: null");
                return;
            }

            var packs = pd.PackInfo;
            int total = 0;
            for (int i = 0; packs != null && i < packs.Length; i++)
            {
                var p = packs[i];
                if (p == null) continue;

                var ids = p.SongIds;
                int count = ids != null ? ids.Length : 0;
                total += count;

                bool hasSample = false;
                for (int j = 0; j < count; j++)
                {
                    if (ids[j].Value == sampleId) hasSample = true;
                }
                log.Msg($"  ├ 팩 {p.Id.Value} '{p.Slug}': 곡 {count}개{(hasSample ? $"  ← '{SampleSlug}' 포함" : "")}");
            }
            log.Msg($"  ├ 팩 합계 곡 {total}개 (allSongInfo 와 다르면 팩에 안 든 곡이 있음)");

            var songToPack = pd._fYA;
            log.Msg($"  ├ _fYA(SongId→PackId): {(songToPack != null ? songToPack.Count : -1)}개");

            var set = pd._FYA;
            if (set != null)
            {
                var members = new List<int>();
                foreach (var id in set) members.Add(id.Value);
                log.Msg($"  ├ _FYA(HashSet<PackId>): [{string.Join(", ", members)}]");
            }

            var flags = pd._gYA;
            if (flags != null)
            {
                var pairs = new List<string>();
                foreach (var kv in flags) pairs.Add($"{kv.Key.Value}={kv.Value}");
                log.Msg($"  ├ _gYA(PackId→bool): [{string.Join(", ", pairs)}]");
            }

            var dlc = da.DlcRuntimeStartupConfiguration;
            var dlcEntries = dlc != null ? dlc.Entries : null;
            if (dlcEntries == null)
            {
                log.Msg("  └ DLC 구성: 없음");
                return;
            }
            log.Msg($"  └ DLC 구성 {dlcEntries.Count}개");
            foreach (var e in dlcEntries)
            {
                if (e == null) continue;
                log.Msg($"      DLC '{e.DlcId}' → 팩 {e.PackId.Value}, 에셋 엔트리 {(e.StreamingAssets != null ? e.StreamingAssets.Count : -1)}개");
            }
        }

        // ─────────────────────────────────────────────────────────────── 3. 문자열

        private static void ProbeStrings(DataAccess da, Dictionary<int, string> known, int sampleId, MelonLogger.Instance log)
        {
            var dsm = da.DynamicStringMapping;
            if (dsm == null)
            {
                log.Msg("  └ DynamicStringMapping: null");
                return;
            }

            LogStringMapping("제목", dsm.songIdTitleTypeMapping, known, sampleId, log);
            LogStringMapping("아티스트", dsm.songIdArtistTypeMapping, known, sampleId, log);
        }

        private static void LogStringMapping(string label, DynamicStringMapping.StringTypeMapping<SongId> map, Dictionary<int, string> known,
                                             int sampleId, MelonLogger.Instance log)
        {
            if (map == null)
            {
                log.Msg($"  ├ {label}: null");
                return;
            }

            var dict = map.Mapping;
            int missing = 0;
            foreach (var id in known.Keys)
            {
                if (dict == null || !dict.ContainsKey(new SongId { Value = (ushort)id })) missing++;
            }

            // IdStr 는 Ids 와 같은 순서의 문자열 키로 보인다 — 기준 곡 칸을 찾아 같이 찍는다
            string idStr = "?";
            var ids = map.Ids;
            var strs = map.IdStr;
            for (int i = 0; ids != null && strs != null && i < ids.Count && i < strs.Count; i++)
            {
                if (ids[i].Value == sampleId) idStr = strs[i];
            }

            string value = "없음";
            var key = new SongId { Value = (ushort)sampleId };
            if (dict != null && sampleId >= 0 && dict.ContainsKey(key))
            {
                var v = dict[key];
                value = $"EN '{v.English}' / JP '{v.Japanese}' / KO '{v.Korean}'";
            }

            log.Msg($"  ├ {label}: Mapping {(dict != null ? dict.Count : -1)}개, Ids {(ids != null ? ids.Count : -1)}개, " +
                    $"문자열 없는 곡 {missing}개 | '{SampleSlug}' IdStr '{idStr}' → {value}");
        }

        // ─────────────────────────────────────────────────────────────── 4. 에셋

        private static void ProbeAssets(DataAccess da, MelonLogger.Instance log)
        {
            var sam = da.StreamingAssetsMapping;
            var entries = sam != null ? sam.Entries : null;
            if (entries != null)
            {
                log.Msg($"  ├ StreamingAssetsMapping: {entries.Count}개");
                foreach (var e in entries)
                {
                    if (e == null || e.FullLookupPath == null || !e.FullLookupPath.StartsWith(SampleSlug, StringComparison.Ordinal)) continue;
                    log.Msg($"  │   '{e.FullLookupPath}' → {e.Guid} ({e.FileLength:N0} 바이트)");
                }
            }

            // 에셋 등록부 — 모든 소스(기본 + DLC)를 합친 논리 경로 → AssetId 표
            var reg = Il2Cpp_k._BG._RxA;
            if (reg == null)
            {
                log.Msg("  └ 에셋 등록부(_BG._RxA): null");
                return;
            }

            var table = reg._txA;
            var files = reg._TxA;
            var longs = reg._uxA;
            var flags = reg._UxA;
            int flagTrue = 0;
            for (int i = 0; flags != null && i < flags.Length; i++)
            {
                if (flags[i]) flagTrue++;
            }

            log.Msg($"  ├ 에셋 등록부: 경로 {(table != null ? table.Count : -1)}개, FileInfo {Len(files)}, long[] {Len(longs)}, " +
                    $"bool[] {Len(flags)} (true {flagTrue}), 소스(_vxA) {Len(reg._vxA)}, _VxA {(reg._VxA != null ? reg._VxA.Count : -1)}, " +
                    $"_SxA {reg._SxA}, _sxA {reg._sxA}");
            log.Msg($"  ├ 정적 문자열: _QxA '{Il2Cpp_k._BG._QxA}', _rxA '{Il2Cpp_k._BG._rxA}'");

            foreach (string path in new[] { SampleSlug + ".wav", SampleSlug + "0.spc" })
            {
                if (table == null || !table.ContainsKey(path))
                {
                    log.Msg($"  │   '{path}': 등록부에 없음");
                    continue;
                }

                int id = table[path];
                string file = files != null && id < files.Length && files[id] != null ? $"{files[id].FullName} ({files[id].Length:N0} 바이트)" : "?";
                string lng = longs != null && id < longs.Length ? longs[id].ToString("N0") : "?";
                string flag = flags != null && id < flags.Length ? flags[id].ToString() : "?";
                log.Msg($"  │   '{path}' → AssetId {id}, 파일 {file}, long {lng}, bool {flag}");
            }

            if (table != null) ProbeAssetKeys(table, files, longs, flags, log);
        }

        /// <summary>
        /// [2차] 경로 키 39,394개가 파일 10,344개에 어떻게 붙는지 — 새 파일을 등록할 때 어떤 키들을 같이 넣어야 하는지 본다.
        /// 기준 곡 음원·차트에 붙은 키 전부와, bool 이 false 인 파일(1차에서 1개, 키 없음)의 정체를 찍는다.
        /// </summary>
        private static void ProbeAssetKeys(Il2CppSystem.Collections.Generic.Dictionary<string, int> table,
                                           Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.IO.FileInfo> files,
                                           Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<long> longs,
                                           Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<bool> flags,
                                           MelonLogger.Instance log)
        {
            int wavId = table.ContainsKey(SampleSlug + ".wav") ? table[SampleSlug + ".wav"] : -1;
            int spcId = table.ContainsKey(SampleSlug + "0.spc") ? table[SampleSlug + "0.spc"] : -1;

            var falseIdx = new HashSet<int>();
            for (int i = 0; flags != null && i < flags.Length; i++)
            {
                if (!flags[i]) falseIdx.Add(i);
            }

            var perAsset = new Dictionary<int, int>();
            var wavKeys = new List<string>();
            var spcKeys = new List<string>();
            var falseKeys = new List<string>();
            int outOfRange = 0;
            foreach (var kv in table)
            {
                int id = kv.Value;
                perAsset[id] = perAsset.TryGetValue(id, out int c) ? c + 1 : 1;
                if (files != null && (id < 0 || id >= files.Length)) outOfRange++;
                if (id == wavId) wavKeys.Add(kv.Key);
                if (id == spcId) spcKeys.Add(kv.Key);
                if (falseIdx.Contains(id)) falseKeys.Add(kv.Key);
            }

            // 파일 하나에 키가 몇 개씩 붙는지 분포
            var dist = perAsset.Values.GroupBy(n => n).OrderBy(g => g.Key).Select(g => $"{g.Key}개:{g.Count()}");
            int unkeyed = files != null ? files.Length - perAsset.Count : -1;
            log.Msg($"  ├ [2차] 파일당 키 수 분포 [{string.Join(", ", dist)}], 키 없는 파일 {unkeyed}개, 범위 밖 AssetId {outOfRange}개");
            log.Msg($"  ├ [2차] AssetId {wavId}('{SampleSlug}.wav') 키 {wavKeys.Count}개: {string.Join(" | ", wavKeys.Select(k => $"'{k}'"))}");
            log.Msg($"  ├ [2차] AssetId {spcId}('{SampleSlug}0.spc') 키 {spcKeys.Count}개: {string.Join(" | ", spcKeys.Select(k => $"'{k}'"))}");

            foreach (int i in falseIdx)
            {
                string file = files != null && i < files.Length && files[i] != null ? $"'{files[i].FullName}'" : "null";
                string len = longs != null && i < longs.Length ? longs[i].ToString("N0") : "?";
                log.Msg($"  └ [2차] bool false 인 파일: AssetId {i}, FileInfo {file}, long {len}, 키 {falseKeys.Count}개");
            }
            if (falseIdx.Count == 0) log.Msg("  └ [2차] bool false 인 파일 없음");
        }

        // ─────────────────────────────────────────────────────────────── 5. 배경·영상

        private static void ProbeMedia(DataAccess da, SongInfo sample, MelonLogger.Instance log)
        {
            var bgs = da.GameplayBackgrounds;
            var list = bgs != null ? bgs.gameplayBackgrounds : null;
            log.Msg($"  ├ GameplayBackgrounds: 머티리얼 참조 {Len(list)}개, 기준 곡 배경 {(sample != null ? sample.GameplayBackground.ToString() : "?")}");

            var video = da.VideoFiles;
            var entries = video != null ? video.entries : null;
            if (entries == null)
            {
                log.Msg("  └ VideoFiles: null");
                return;
            }

            var names = new List<string>();
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e != null) names.Add($"'{e.AssetName}'{(string.IsNullOrEmpty(e.AudioAsset) ? "" : $"+'{e.AudioAsset}'")}");
            }
            log.Msg($"  └ VideoFiles: {entries.Length}개 — {string.Join(", ", names.Take(MaxListed))}{(names.Count > MaxListed ? " …" : "")}");
        }

        // ─────────────────────────────────────────────────────────────── 6. 세이브

        private static void ProbeSave(Dictionary<int, string> known, int sampleId, MelonLogger.Instance log)
        {
            var nh = Il2Cpp_K._NH._CEb;
            var save = nh != null && nh._DEb != null ? nh._DEb._iEb : null;
            var results = save != null ? save.GameResults : null;
            if (results == null)
            {
                log.Msg($"  └ 세이브: 없음 (_NH._CEb {(nh == null ? "null" : "있음")})");
                return;
            }

            // highscores — 키가 (BaseName, SongId, 난이도). 게임이 모르는 SongId·이름이 어긋난 키가 이미 있는지 본다
            var hs = results.highscores;
            int total = 0, orphan = 0, nameMismatch = 0;
            var orphans = new List<string>();
            var sampleRows = new List<string>();
            if (hs != null)
            {
                foreach (var kv in hs)
                {
                    total++;
                    var k = kv.Key;
                    int id = k.SongId.Value;

                    if (!known.TryGetValue(id, out string baseName))
                    {
                        orphan++;
                        if (orphans.Count < 5) orphans.Add($"'{k.BaseName}'({id}) {k.Difficulty}");
                    }
                    else if (baseName != k.BaseName)
                    {
                        nameMismatch++;
                    }

                    if (id == sampleId)
                    {
                        var r = kv.Value;
                        sampleRows.Add($"{k.Difficulty} 키 '{k.BaseName}' → 점수 {r.PlayerScore:N0}, 램프 {r.Lamp}");
                    }
                }
            }
            log.Msg($"  ├ highscores: {total}개, 모르는 SongId {orphan}개 {(orphans.Count > 0 ? $"[{string.Join(", ", orphans)}]" : "")}, " +
                    $"BaseName 불일치 {nameMismatch}개");
            foreach (var row in sampleRows) log.Msg($"  │   [{SampleSlug}] {row}");

            // history — 플레이 기록 전체
            var history = results.history;
            if (history != null)
            {
                int len = history.Length, sampleCount = 0, historyOrphan = 0;
                var buf = history.buffer;
                for (int i = 0; buf != null && i < len && i < buf.Length; i++)
                {
                    int id = buf[i].SongId.Value;
                    if (id == sampleId) sampleCount++;
                    if (!known.ContainsKey(id)) historyOrphan++;
                }
                log.Msg($"  ├ history: {len}개 ('{SampleSlug}' {sampleCount}개, 모르는 SongId {historyOrphan}개)");
            }

            // songInfoMapping — history 구간별 BaseName↔SongId 스냅샷으로 보인다(버전 간 ID 가 바뀌어도 이름으로 되찾기용?)
            var mappings = results.songInfoMapping;
            if (mappings != null)
            {
                int len = mappings.Length;
                var buf = mappings.buffer;
                log.Msg($"  └ songInfoMapping: {len}개");
                for (int i = 0; buf != null && i < len && i < buf.Length; i++)
                {
                    if (i >= 3 && i < len - 2) continue;   // 앞 3개·뒤 2개만

                    var m = buf[i];
                    if (m == null) continue;
                    var arr = m.Mappings;
                    string sample = "없음";
                    for (int j = 0; arr != null && j < arr.Length; j++)
                    {
                        var e = arr[j];
                        if (e != null && e.BaseName == SampleSlug) sample = $"'{e.BaseName}'→{e.SongId.Value}";
                    }
                    log.Msg($"      [{i}] history {m.HistoryIndexStart}+{m.HistoryIndexLength}, 매핑 {Len(arr)}개, 기준 곡 {sample}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────── 도우미

        private static int Len<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<T> arr) => arr != null ? arr.Length : -1;

        private static string Preview(List<int> ids)
        {
            if (ids.Count == 0) return "없음";
            string head = string.Join(", ", ids.Take(MaxListed));
            return ids.Count > MaxListed ? head + " …" : head;
        }
    }
}
