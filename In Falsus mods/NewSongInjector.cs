using System;
using System.Collections.Generic;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppifapp.Game;
using Il2Cppifapp.Game.Data;

namespace InFalsusMods
{
    /// <summary>
    /// 새 곡 슬롯 추가 시험 — 원본 곡(alamode)을 복사해 새 SongId·슬러그로 곡 목록에 한 곡을 더 넣는다.
    /// 이번 단계의 목표는 "곡 선택 화면에 뜨는가"까지다. 플레이는 아직 하지 않는다(세이브 보호가 불완전).
    ///
    /// 손대는 곳 (docs/01 8장 조사 결과):
    /// 1. SongData.allSongInfo — **SongId 로 인덱싱된다**(칸 번호 == SongId, 빈 번호 칸은 ID 0·차트 0개 더미).
    ///    새 곡은 끝에 붙이지 않고 71번 더미 칸을 교체한다. 2026-09-24 1회차에 끝(83번 칸)에 붙였더니 게임이 71번으로 찾은
    ///    더미로 곡 항목(_K._SH)을 만들다 IndexOutOfRangeException 이 나서 팩 선택 화면 초기화가 멈췄다.
    /// 2. SongData._ffb(튜토리얼 뺀 곡 목록, 인덱싱 아님) 끝에 추가, _Efb(슬러그→ID).
    ///    _Ffb(튜토리얼 표시, 길이 83)는 새 ID 71 이 범위 안이고 false 여야 하므로 그대로 둔다.
    /// 3. 자켓 — 새 곡은 자켓 항목을 넣지 않는다. 게임이 자켓 없는 곡에 쓰는 기본 자켓(nojacket)이 나오고, JacketInjector 가
    ///    그 텍스처를 hwa/Thumbnail.png 로 바꾼다 — 원곡 자켓은 그대로. (3회차에는 원곡 자켓 항목을 복사해서 원곡과 자켓이 같았다.)
    ///    한계: 이 방식으로는 커스텀 곡 한 곡만 자기 자켓을 가질 수 있다.
    ///    참고: 게임은 곡을 고를 때마다 _Dfb·_efb 를 원본 배열 songIdJacketMaterials·chartIdJacketMaterials 로 다시 만든다(2회차).
    /// 4. PackData — 원본 곡이 든 팩의 SongIds 와 _fYA(SongId→PackId). (팩에 안 넣어도 곡 목록엔 뜬다 — 2회차)
    /// 5. DynamicStringMapping — 제목·아티스트.
    /// 6. 에셋 등록부(_BG) — 음원은 새 AssetId 를 하나 늘려 원곡 음원 항목(파일·길이·복호화 정보)을 그대로 복제하고
    ///    '&lt;새 슬러그&gt;.wav' 키를 거기에 건다. AudioInjector 는 그 새 ID 만 hwa/music.ogg 로 바꾸므로 원곡 음원은 그대로다.
    ///    가로채기가 실패해도 새 곡에서 원곡 음원이 나올 뿐이다. 차트 'N.spc' 키는 원곡 차트 AssetId 로 별칭만 건다.
    ///
    /// SongId 71: 곡·제목 어디에도 안 쓰이는 번호. 0(더미)·4·66·77~89(제목만 예약된 번호)는 피했다 (2차 조사).
    ///
    /// 구조체(SongInfo·SongChartInfo·PackInfo·자켓 엔트리)는 배열에서 꺼내면 복사본이라 고친 뒤 반드시 되써넣는다 (docs/04 4장).
    /// 인터롭 배열은 값 형식 원소를 제자리 memcpy 로 저장한다(Il2CppReferenceArray.StoreValue).
    ///
    /// 세이브 보호: 곡 선택에서 새 곡을 고르면 GeneralSaveStateV5.PackIdToSelectedSongId 에 새 ID 가 남을 수 있다.
    /// 모드를 빼면 게임이 모르는 곡을 선택된 곡으로 읽게 되므로, 곡 선택을 나갈 때와 게임 종료 때 원본 곡 ID 로 되돌린다.
    /// 점수 기록(GameResultsV4.TryUpdate)은 ref SongInfo 인자라 후킹할 수 없다(docs/04 1장) — 기록이 생기면 로그로만 알린다.
    /// </summary>
    internal static class NewSongInjector
    {
        // 목적을 달성하면 false 로 끌 것
        public static bool Enabled { get; set; } = true;

        private const string SourceSlug = "alamode";
        private const string NewSlug = "hwa";
        private const ushort NewId = 71;
        private const string Title = "hwa custom";
        private const string Artist = "hwa";

        // 게임 기본 자켓(SongData.FallbackJacket*Material) 머티리얼 이름. 큰 카드에서 'nojacket'(256x256) 확인(2회차).
        // 작은 자켓 이름은 아직 못 봐서 후보를 같이 둔다.
        private static readonly string[] FallbackJacketNames = { "nojacket", "nojacket_small" };

        private static MelonLogger.Instance _logger;
        private static bool _broken;
        private static bool _wasSongSelect;
        private static ushort _sourceId;
        private static IntPtr _injectedData;   // 주입한 SongData 객체 — 같은 객체엔 한 번만 (단계 하나가 실패해도 곡이 두 번 붙지 않게)
        private static int _audioAssetId = -1;   // 새 곡 음원에 새로 붙인 AssetId

        // 새로 만든 IL2CPP 객체 — 래퍼가 살아 있어야 GC 핸들이 유지된다. 배열에 memcpy 로 들어간 구조체 안의
        // 문자열·배열이 이 박싱 객체에서도 참조되도록 세션 내내 붙잡아 둔다.
        private static readonly List<object> _keepAlive = new();

        public static void Init(MelonLogger.Instance logger)
        {
            _logger = logger;
            if (!Enabled) return;

            // 새 곡은 기본 자켓을 쓰므로 자켓 교체도 원곡 대신 기본 자켓으로 — 머티리얼이 쓰이기 전에 바꿔야 한다
            JacketInjector.TargetMaterialNames = FallbackJacketNames;

            // 새 곡 차트('hwaN.spc')는 원곡 파일 별칭이라 원곡 이름으로 파싱하고, hwa/hwaN.txt 가 있으면 그 채보로 바꾼다
            ChartInjector.RegisterSong(NewSlug, SourceSlug);

            logger.Msg($"[NewSongInjector] 준비 완료 ('{SourceSlug}' 복사 → '{NewSlug}' SongId {NewId}, 제목 '{Title}') — " +
                       $"자켓 교체 대상을 기본 자켓({string.Join(", ", FallbackJacketNames)})으로 바꿈");
        }

        /// <summary>디텍터 틱마다 — 데이터가 올라오면 한 번 주입, 곡 선택을 나가면 세이브 정리.</summary>
        public static void Tick(MelonLogger.Instance log)
        {
            if (!Enabled || _broken) return;

            try
            {
                var da = GetDataAccess();
                var sd = da != null ? da.SongData : null;
                if (sd == null || sd._Efb == null || sd.allSongInfo == null) return;

                // SongData 가 새 객체로 다시 로드되면 주입이 사라진다 — 객체가 바뀌었을 때만 (다시) 넣는다
                if (sd.Pointer != _injectedData)
                {
                    _injectedData = sd.Pointer;
                    Inject(da, log);
                }

                bool inSelect = SceneRefs.IsSongSelectScene;
                if (_wasSongSelect && !inSelect) ScrubSave("곡 선택 화면 나감", log);
                _wasSongSelect = inSelect;
            }
            catch (Exception ex)
            {
                _broken = true;
                log.Error($"[NewSongInjector] 예외 — 중단: {ex}");
            }
        }

        /// <summary>게임 종료 직전 — 세이브에 새 곡 흔적이 남지 않게 정리한다.</summary>
        public static void Shutdown()
        {
            if (!Enabled || _sourceId == 0) return;

            try
            {
                ScrubSave("게임 종료", _logger);
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[NewSongInjector] 종료 정리 실패: {ex.Message}");
            }
        }

        private static DataAccess GetDataAccess()
        {
            var nh = Il2Cpp_K._NH._CEb;
            if (nh != null && nh._EEb != null) return nh._EEb;

            var select = SceneRefs.SongSelect;
            return select != null ? select.dataAccess : null;
        }

        // ─────────────────────────────────────────────────────────────── 주입

        private static void Inject(DataAccess da, MelonLogger.Instance log)
        {
            var sd = da.SongData;
            if (!sd._Efb.ContainsKey(SourceSlug))
            {
                _broken = true;
                log.Warning($"[NewSongInjector] 원본 곡 '{SourceSlug}' 없음 — 중단");
                return;
            }

            SongInfo src = FindSong(sd.allSongInfo, SourceSlug);
            if (src == null)
            {
                _broken = true;
                log.Warning($"[NewSongInjector] allSongInfo 에 '{SourceSlug}' 없음 — 중단");
                return;
            }
            _sourceId = src.Id.Value;

            // 이미 쓰이는 번호면 멈춘다 — 업데이트로 71 번 곡이 생겼을 수 있다
            if (FindSong(sd.allSongInfo, id: NewId) != null)
            {
                _broken = true;
                log.Warning($"[NewSongInjector] SongId {NewId} 이 이미 곡으로 쓰임 — 중단 (번호를 다시 고를 것)");
                return;
            }
            var titleMap = da.DynamicStringMapping != null ? da.DynamicStringMapping.songIdTitleTypeMapping : null;
            if (titleMap != null && titleMap.Mapping != null && titleMap.Mapping.ContainsKey(Id(NewId)))
            {
                _broken = true;
                log.Warning($"[NewSongInjector] SongId {NewId} 에 제목이 이미 예약돼 있음 — 중단 (번호를 다시 고를 것)");
                return;
            }

            // allSongInfo 가 SongId 인덱싱이 아니면(업데이트로 구조가 바뀜) 어디에 넣을지 모른다 — 멈춘다
            string layout = CheckIndexedBySongId(sd.allSongInfo);
            if (layout != null)
            {
                _broken = true;
                log.Warning($"[NewSongInjector] allSongInfo 가 SongId 인덱싱이 아님 ({layout}) — 중단");
                return;
            }

            log.Msg("══════════════════════════════════════════════════════════════════════════");
            log.Msg($"[NewSongInjector] 새 곡 주입 — '{SourceSlug}'(SongId {_sourceId}) 복사 → '{NewSlug}'(SongId {NewId})");

            var song = CloneSong(src);
            Step(log, $"1. allSongInfo {NewId}번 더미 칸 교체", () => PlaceInSlot(sd, song));
            Step(log, "2. _ffb 끝에 추가", () =>
            {
                if (sd._ffb == null) return "null — 건너뜀";
                sd._ffb = Append(sd._ffb, song);
                return $"{sd._ffb.Length}칸";
            });
            Step(log, "3. _Efb 슬러그→ID", () =>
            {
                sd._Efb[NewSlug] = Id(NewId);
                return $"{sd._Efb.Count}개";
            });
            Step(log, "4. 자켓", () => "항목 넣지 않음 — 게임 기본 자켓을 쓰고 JacketInjector 가 그걸 바꾼다");
            Step(log, "5. 팩 SongIds·_fYA", () => AddToPack(da.PackData));
            Step(log, "6. 제목·아티스트", () => AddStrings(da.DynamicStringMapping));
            Step(log, "7. 에셋 등록부 (음원 새 AssetId·차트 별칭)", () => AddAssets(src));

            log.Msg("══════════════════════════════════════════════════════════════════════════");
        }

        /// <summary>단계 하나가 실패해도 다음 단계로 간다 — 어디까지 들어갔는지 로그로 남기는 게 목적.</summary>
        private static void Step(MelonLogger.Instance log, string name, Func<string> body)
        {
            try
            {
                log.Msg($"  ├ {name}: {body()}");
            }
            catch (Exception ex)
            {
                log.Warning($"  ! {name} 실패: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>이름 있는 칸이 전부 칸 번호 == SongId 인지. 맞으면 null, 아니면 이유.</summary>
        private static string CheckIndexedBySongId(Il2CppReferenceArray<SongInfo> all)
        {
            int named = 0, mismatch = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                if (s == null || string.IsNullOrEmpty(s.BaseName)) continue;
                named++;
                if (s.Id.Value != i) mismatch++;
            }
            if (mismatch > 0) return $"이름 있는 {named}칸 중 {mismatch}칸이 칸 번호 ≠ SongId";
            if (NewId >= all.Length) return $"새 ID {NewId} 가 배열 길이 {all.Length} 밖";
            return null;
        }

        private static string PlaceInSlot(SongData sd, SongInfo song)
        {
            var all = sd.allSongInfo;
            var slot = all[NewId];
            if (slot != null && !string.IsNullOrEmpty(slot.BaseName))
            {
                throw new InvalidOperationException($"{NewId}번 칸에 이미 '{slot.BaseName}' 가 있음");
            }

            all[NewId] = song;   // 게임 배열 제자리 복사 — 길이는 그대로
            var check = all[NewId];
            return $"{all.Length}칸 그대로, [{NewId}] = '{check.BaseName}'(SongId {check.Id.Value}, 차트 {check.ChartInfos?.Length ?? 0}개)";
        }

        private static SongInfo CloneSong(SongInfo src)
        {
            var srcCharts = src.ChartInfos;
            var charts = new Il2CppReferenceArray<SongChartInfo>(srcCharts != null ? srcCharts.Length : 0);
            for (int i = 0; i < charts.Length; i++)
            {
                var c = srcCharts[i];
                var n = new SongChartInfo
                {
                    Id = NewChartId(c.Id),
                    Available = c.Available,
                    Difficulty = c.Difficulty,
                    DisplayChartDesigner = c.DisplayChartDesigner,
                    DisplayJacketDesigner = c.DisplayJacketDesigner,
                    Rating = c.Rating,
                    LevelSectionIndicator = c.LevelSectionIndicator,
                };
                charts[i] = n;   // 제자리 복사
            }

            var song = new SongInfo
            {
                Id = Id(NewId),
                BaseName = NewSlug,
                CharacterIdentifier = src.CharacterIdentifier,
                ChartInfos = charts,
                PreviewStartSeconds = src.PreviewStartSeconds,
                PreviewEndSeconds = src.PreviewEndSeconds,
                LocalizationToTitleReadingOverride = src.LocalizationToTitleReadingOverride,
                ArtistReadingOverride = src.ArtistReadingOverride,
                GameplayBackground = src.GameplayBackground,
                RewardStyle = src.RewardStyle,
            };

            _keepAlive.Add(charts);
            _keepAlive.Add(song);
            return song;
        }

        /// <summary>'alamode2' → 'hwa2'. 원본 슬러그로 시작하지 않으면 뒤에 붙인다.</summary>
        private static string NewChartId(string sourceChartId)
        {
            if (sourceChartId != null && sourceChartId.StartsWith(SourceSlug, StringComparison.Ordinal))
            {
                return NewSlug + sourceChartId.Substring(SourceSlug.Length);
            }
            return NewSlug + sourceChartId;
        }

        private static string AddToPack(PackData pd)
        {
            if (pd == null || pd.PackInfo == null) return "PackData 없음 — 건너뜀";

            var packs = pd.PackInfo;
            for (int i = 0; i < packs.Length; i++)
            {
                var p = packs[i];   // 복사본
                var ids = p != null ? p.SongIds : null;
                if (ids == null || !Contains(ids, _sourceId)) continue;

                if (!Contains(ids, NewId))
                {
                    var grown = new Il2CppStructArray<SongId>(ids.Length + 1);
                    for (int j = 0; j < ids.Length; j++) grown[j] = ids[j];
                    grown[ids.Length] = Id(NewId);
                    p.SongIds = grown;
                    packs[i] = p;   // 되써넣기
                    _keepAlive.Add(grown);
                }

                if (pd._fYA != null) pd._fYA[Id(NewId)] = p.Id;
                return $"팩 {p.Id.Value} '{p.Slug}' → {packs[i].SongIds.Length}곡, _fYA {(pd._fYA != null ? pd._fYA.Count : -1)}개";
            }
            return $"'{SourceSlug}' 가 든 팩 없음 — 건너뜀";
        }

        private static string AddStrings(DynamicStringMapping dsm)
        {
            if (dsm == null) return "DynamicStringMapping 없음 — 건너뜀";

            AddString(dsm.songIdTitleTypeMapping, Title);
            AddString(dsm.songIdArtistTypeMapping, Artist);
            return $"제목 '{Title}' / 아티스트 '{Artist}' (제목 {dsm.songIdTitleTypeMapping?.Mapping?.Count ?? -1}개)";
        }

        private static void AddString(DynamicStringMapping.StringTypeMapping<SongId> map, string text)
        {
            if (map == null) return;

            // 언어가 비어 있으면 영어로 떨어지는 것으로 보이지만(alamode 는 JP·KO 빈 문자열) 확실치 않아 전부 채운다
            var values = new DynamicStringMapping.TextMappingValues
            {
                English = text,
                Japanese = text,
                Korean = text,
                TraditionalChinese = text,
                SimplifiedChinese = text,
            };
            _keepAlive.Add(values);

            var key = Id(NewId);
            if (map.Mapping != null) map.Mapping[key] = values;

            // 직렬화 원본 목록도 맞춰 둔다 — Mapping 을 이 목록으로 다시 만드는 경우 대비
            if (map.Ids != null && !map.Ids.Contains(key))
            {
                map.Ids.Add(key);
                map.IdStr?.Add(text);
                map.IdValues?.Add(values);
            }
        }

        /// <summary>
        /// 새 곡 음원은 새 AssetId(원곡 항목 복제)로, 차트는 원곡 AssetId 별칭으로 등록한다.
        /// 등록부는 로더 스레드와 같이 쓰므로 쓰기 잠금을 건다.
        /// </summary>
        private static string AddAssets(SongInfo src)
        {
            var reg = Il2Cpp_k._BG._RxA;
            var table = reg != null ? reg._txA : null;
            if (table == null) return "에셋 등록부 없음 — 건너뜀";

            var pairs = new List<(string from, string to)>();
            var srcCharts = src.ChartInfos;
            for (int i = 0; srcCharts != null && i < srcCharts.Length; i++)
            {
                pairs.Add((srcCharts[i].Id + ".spc", NewChartId(srcCharts[i].Id) + ".spc"));
            }

            var lk = reg._yxA;
            bool locked = false;
            try
            {
                try
                {
                    lk?.EnterWriteLock();
                    locked = lk != null;
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"  ! 등록부 쓰기 잠금 실패 — 잠금 없이 진행: {ex.Message}");
                }

                string audio = AddAudioAsset(reg, table);

                var added = new List<string>();
                foreach (var (from, to) in pairs)
                {
                    if (!table.ContainsKey(from) || table.ContainsKey(to)) continue;
                    int id = table[from];
                    table[to] = id;
                    added.Add($"'{to}'→{id}");
                }
                return $"{audio} / 차트 별칭 {added.Count}개 [{string.Join(", ", added)}]";
            }
            finally
            {
                if (locked) lk.ExitWriteLock();
            }
        }

        /// <summary>
        /// 등록부 배열(_TxA FileInfo·_uxA 길이·_UxA 플래그·_vxA 복호화 정보)을 한 칸 늘려 원곡 음원 항목을 그대로 복제하고,
        /// '&lt;새 슬러그&gt;.wav' 를 그 새 ID 에 건다. 원곡과 똑같은 파일·복호화 정보라 게임이 파일을 미리 읽어도 원곡과 같이 동작한다.
        /// 호출 전에 쓰기 잠금을 잡고 있어야 한다.
        /// </summary>
        private static string AddAudioAsset(Il2Cpp_k._BG reg, Il2CppSystem.Collections.Generic.Dictionary<string, int> table)
        {
            string from = SourceSlug + ".wav";
            string to = NewSlug + ".wav";
            if (table.ContainsKey(to))
            {
                _audioAssetId = table[to];
                AudioInjector.TargetAssetId = _audioAssetId;
                return $"음원 '{to}' 이미 등록됨 → AssetId {_audioAssetId}";
            }
            if (!table.ContainsKey(from)) return $"음원 '{from}' 없음 — 건너뜀";

            int srcId = table[from];
            var files = reg._TxA;
            var lens = reg._uxA;
            var flags = reg._UxA;
            var states = reg._vxA;
            int n = files.Length;
            if (lens.Length != n || flags.Length != n || states.Length != n || srcId <= 0 || srcId >= n)
            {
                throw new InvalidOperationException($"등록부 배열 길이 불일치 FileInfo {n}·long {lens.Length}·bool {flags.Length}·_DG {states.Length}, 원곡 ID {srcId}");
            }

            var files2 = new Il2CppReferenceArray<Il2CppSystem.IO.FileInfo>(n + 1);
            var lens2 = new Il2CppStructArray<long>(n + 1);
            var flags2 = new Il2CppStructArray<bool>(n + 1);
            var states2 = new Il2CppReferenceArray<Il2Cpp_k._BG._DG>(n + 1);
            CopyArray(files, files2, n);
            CopyArray(lens, lens2, n);
            CopyArray(flags, flags2, n);
            CopyArray(states, states2, n);

            files2[n] = files[srcId];
            lens2[n] = lens[srcId];
            flags2[n] = flags[srcId];
            states2[n] = states[srcId];   // 제자리 복사

            reg._TxA = files2;
            reg._uxA = lens2;
            reg._UxA = flags2;
            reg._vxA = states2;
            table[to] = n;
            _keepAlive.Add(files2);
            _keepAlive.Add(lens2);
            _keepAlive.Add(flags2);
            _keepAlive.Add(states2);

            _audioAssetId = n;
            AudioInjector.TargetAssetId = n;
            return $"음원 '{to}' → 새 AssetId {n} ('{from}' {srcId} 항목 복제, 등록부 {n}→{n + 1}칸) — AudioInjector 대상 {srcId}→{n}";
        }

        /// <summary>IL2CPP 배열을 네이티브 Array.Copy 로 통째로 복사한다(원소 1만 개를 래퍼로 하나씩 옮기지 않게). 실패하면 하나씩.</summary>
        private static void CopyArray<T>(Il2CppArrayBase<T> src, Il2CppArrayBase<T> dst, int count)
        {
            try
            {
                Il2CppSystem.Array.Copy(new Il2CppSystem.Array(src.Pointer), new Il2CppSystem.Array(dst.Pointer), count);
            }
            catch (Exception ex)
            {
                _logger?.Warning($"  ! Array.Copy 실패 — 하나씩 복사: {ex.Message}");
                for (int i = 0; i < count; i++) dst[i] = src[i];
            }
        }

        // ─────────────────────────────────────────────────────────────── 세이브 정리

        /// <summary>
        /// 팩별 마지막 선택 곡이 새 곡이면 원본 곡으로 되돌린다. 점수 기록에 새 곡이 있으면 알리기만 한다
        /// (history 는 songInfoMapping 이 인덱스 구간으로 가리켜서 함부로 지우면 어긋난다).
        /// </summary>
        private static void ScrubSave(string reason, MelonLogger.Instance log)
        {
            if (_sourceId == 0) return;

            var nh = Il2Cpp_K._NH._CEb;
            var save = nh != null && nh._DEb != null ? nh._DEb._iEb : null;
            if (save == null) return;

            // GeneralSaveState 는 구조체 복사본이지만 Dictionary 는 참조라 원본이 바뀐다
            var selected = save.GeneralSaveState != null ? save.GeneralSaveState.PackIdToSelectedSongId : null;
            var fixedPacks = new List<PackId>();
            if (selected != null)
            {
                foreach (var kv in selected)
                {
                    if (kv.Value.Value == NewId) fixedPacks.Add(kv.Key);
                }
                foreach (var p in fixedPacks) selected[p] = Id(_sourceId);
            }

            int scores = 0;
            var hs = save.GameResults != null ? save.GameResults.highscores : null;
            if (hs != null)
            {
                foreach (var kv in hs)
                {
                    if (kv.Key.SongId.Value == NewId) scores++;
                }
            }

            if (fixedPacks.Count > 0 || scores > 0)
            {
                log?.Msg($"[NewSongInjector] 세이브 정리({reason}): 마지막 선택 곡 {fixedPacks.Count}개 팩 → SongId {_sourceId} 로 되돌림" +
                         (scores > 0 ? $", ⚠ 새 곡 점수 기록 {scores}개 있음 — 세이브 백업으로 되돌릴 것" : ""));
            }
        }

        // ─────────────────────────────────────────────────────────────── 도우미

        private static SongId Id(ushort value) => new SongId { Value = value };

        private static SongInfo FindSong(Il2CppReferenceArray<SongInfo> all, string slug = null, ushort id = 0)
        {
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var s = all[i];
                if (s == null || string.IsNullOrEmpty(s.BaseName)) continue;
                if (slug != null ? s.BaseName == slug : s.Id.Value == id) return s;
            }
            return null;
        }

        private static bool Contains(Il2CppStructArray<SongId> ids, ushort value)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i].Value == value) return true;
            }
            return false;
        }

        private static Il2CppReferenceArray<T> Append<T>(Il2CppReferenceArray<T> arr, params T[] items) where T : Il2CppObjectBase
        {
            var grown = new Il2CppReferenceArray<T>(arr.Length + items.Length);
            for (int i = 0; i < arr.Length; i++) grown[i] = arr[i];
            for (int i = 0; i < items.Length; i++) grown[arr.Length + i] = items[i];
            _keepAlive.Add(grown);
            return grown;
        }

        private static int Len<T>(Il2CppArrayBase<T> arr) => arr != null ? arr.Length : -1;
    }
}
