using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2Cpp;
using Il2CppFastText;
using Il2Cppifapp.Game.Data;
using Il2Cppifapp.Game.Scenes;
using Il2Cppifapp.Game.Scenes.Game.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppifapp.Game.UI.Common;

namespace InFalsusMods
{
    /// <summary>
    /// 곡 목록(SongSelectScene), 로딩씬(SongTransitionLayer), 플레이 씬(GameScene)의
    /// 커버/자켓, 곡 제목, 재생바(Progress Bar) 오브젝트 상태를 감지하고 로깅합니다.
    ///
    /// 플레이 씬 덤프는 프레임 카운트로 추측하지 않고 재생바 갱신(_QCA) 후킹으로 무장(arm)한 뒤,
    /// 비동기로 도착하는 자켓 텍스처와 차트 메타데이터가 준비될 때까지 기다렸다가 출력합니다.
    /// (곡 정보 주입 메서드 _qCA 는 byref 값 타입 인자 때문에 후킹하면 프로세스가 죽는다 — Init 주석 참고)
    /// </summary>
    internal static class JacketHook
    {
        /// <summary>
        /// 자켓/차트 로드를 기다리는 최대 폴링 횟수. 초과하면 미완료 상태임을 명시하고 덤프한다.
        /// Tick 은 NoteListDumper.DetectorIntervalFrames(10프레임)마다 불리므로 60폴링 ≈ 600프레임이다.
        /// </summary>
        private const int AssetWaitTimeoutPolls = 60;

        private static MelonLogger.Instance _logger;
        private static int _lastSelectedSongId = -1;
        private static ChartDifficultyFlag _lastDifficulty = ChartDifficultyFlag.None;
        private static bool _transitionActive = false;

        // 플레이 씬 덤프 상태
        private static bool _dumpArmed;
        private static bool _dumpDone;
        private static bool _armedByHook;
        private static int _armedPolls;
        private static SongInfoContainer _pendingContainer;

        public static void Init(HarmonyLib.Harmony harmony, MelonLogger.Instance logger)
        {
            _logger = logger;

            // ★ SongInfoContainer._qCA(ref SongInfo, ChartDifficultyFlag, _Jh) 는 절대 후킹하지 말 것.
            //
            // SongInfo 는 ValueType(구조체)이라 첫 인자가 byref 값 타입이다. Il2CppInterop 1.5.1 이
            // 만드는 il2cpp->managed 트램펄린이 이 인자 슬롯을 제대로 잡지 못해서, postfix 에 도달하기도
            // 전에 Il2CppObjectPool.Get -> il2cpp_object_get_class 에서 AccessViolationException 이
            // 터지고 프로세스가 즉사한다 (2026-09-10 실측 — 후킹 등록은 성공하고 곡 진입 순간 죽음).
            //
            // 대신 시그니처가 void(float) 라 안전한 _QCA(재생바) 후킹을 무장 트리거로 쓴다.
            // 그마저 안 오면 IsSongInfoPopulated UI 폴링 폴백으로 넘어간다.
            // 난이도는 _qCA 인자 대신 LogicalNotePlayer 가 물고 있는 차트 ID 로 역산한다.
            logger.Msg("[JacketHook] 커버/자켓 및 플레이 씬 UI 감지 모듈 초기화 완료 (_QCA 재생바 트리거)");
        }

        /// <summary>
        /// 매 프레임 호출되어 곡 선택 상태, 로딩씬 트랜지션, 플레이 씬 UI를 실시간으로 감지합니다.
        /// </summary>
        public static void Tick(MelonLogger.Instance logger)
        {
            try
            {
                // 1. 곡 목록 화면 (SongSelectScene) 감지
                DetectSongSelectScene(logger);

                // 2. 로딩 씬 / 트랜지션 레이어 (SongTransitionLayer) 감지
                DetectLoadingScene(logger);

                // 3. 플레이 씬 (GameScene) 및 SongInfoContainer 감지
                DetectGameScene(logger);
            }
            catch
            {
                // 감지 중 예외는 무시하여 게임 플레이 영향 방지
            }
        }

        private static void DetectSongSelectScene(MelonLogger.Instance logger)
        {
            var scene = SceneRefs.SongSelect;
            if (scene != null)
            {
                int songId = scene._sr.Value;
                var diff = scene._Sr;

                if (songId > 0 && (songId != _lastSelectedSongId || diff != _lastDifficulty))
                {
                    _lastSelectedSongId = songId;
                    _lastDifficulty = diff;

                    string title = GetSongTitle(scene, songId);
                    logger.Msg($"[JacketHook][곡 목록] 선택 곡 변경: '{title}' (Id: {songId}, 난이도: {diff})");

                    if (scene.backingJacket != null)
                    {
                        logger.Msg($"[JacketHook][곡 목록] └ backingJacket 활성 상태 (Obj: {scene.backingJacket.name})");
                    }
                }
            }
            else
            {
                _lastSelectedSongId = -1;
                _lastDifficulty = ChartDifficultyFlag.None;
            }
        }

        private static void DetectLoadingScene(MelonLogger.Instance logger)
        {
            var trans = SceneRefs.Transition;
            if (trans != null)
            {
                if (trans.gameObject.activeInHierarchy)
                {
                    if (!_transitionActive)
                    {
                        _transitionActive = true;
                        logger.Msg("[JacketHook][로딩씬] SongTransitionLayer 진입 (곡 시작 전환)");

                        if (trans.jacket != null)
                        {
                            logger.Msg($"[JacketHook][로딩씬] └ jacket 컴포넌트: {trans.jacket.name}");
                        }
                        if (trans.jacketTargetMaterial != null)
                        {
                            logger.Msg($"[JacketHook][로딩씬] └ jacketTargetMaterial: {trans.jacketTargetMaterial.name}");
                        }
                        if (trans.backgroundJacket != null)
                        {
                            logger.Msg($"[JacketHook][로딩씬] └ backgroundJacket: {trans.backgroundJacket.name}");
                        }
                    }
                }
                else
                {
                    _transitionActive = false;
                }
            }
            else
            {
                _transitionActive = false;
            }
        }

        /// <summary>
        /// 재생바 갱신(_QCA) 후킹에서 호출되는 무장(arm) 트리거.
        /// 플레이 씬이 실제로 돌기 시작한 시점이라 곡 정보 주입과 거의 동시이고,
        /// 시그니처가 void(float) 뿐이라 트램펄린이 안전하다.
        /// 자켓 텍스처(Addressable)와 차트는 아직 비동기 로드 중이므로 덤프는 예약만 해 둔다.
        /// </summary>
        public static void ArmFromProgress(SongInfoContainer container)
        {
            if (_dumpArmed || _dumpDone) return;

            try
            {
                _pendingContainer = container;
                _dumpArmed = true;
                _armedByHook = true;
                _armedPolls = 0;

                _logger?.Msg("[JacketHook][플레이 씬] 재생바 갱신 감지 (_QCA) — 곡 정보/자켓/차트 로드 대기 중…");
            }
            catch { }
        }

        private static void DetectGameScene(MelonLogger.Instance logger)
        {
            var gameScene = SceneRefs.GameScene;
            if (gameScene == null)
            {
                // 이번 곡 덤프가 끝난 뒤에만 정리한다.
                // (_QCA 가 GameScene 검색보다 먼저 불릴 수 있으므로 무장 상태를 함부로 지우지 않는다)
                if (_dumpDone) ResetPendingDump();
                return;
            }

            // Unity 오브젝트는 파괴돼도 C# 참조가 null 이 아니라 ?? 가 통하지 않는다.
            // 반드시 == null (UnityEngine.Object 오버로드)로 판정해 파괴된 컨테이너를 걸러낸다.
            var infoContainer = _pendingContainer != null ? _pendingContainer : gameScene.songInfoContainer;
            if (infoContainer == null || !infoContainer.gameObject.activeInHierarchy) return;

            if (_dumpDone) return;

            // 폴백: 후킹이 실패했거나 어떤 이유로 postfix 가 오지 않은 경우.
            // 그림자/본체 텍스트가 모두 같은 값이 되어야 실제 주입 완료로 간주한다.
            if (!_dumpArmed && IsSongInfoPopulated(infoContainer))
            {
                _dumpArmed = true;
                _armedByHook = false;
                _armedPolls = 0;

                logger.Msg("[JacketHook][플레이 씬] 곡 정보 주입 감지 (UI 폴링 폴백) — 자켓/차트 로드 대기 중…");
            }

            if (!_dumpArmed) return;

            _armedPolls++;

            bool jacketReady = IsJacketReady(infoContainer);
            bool chartReady = IsChartReady();
            bool timedOut = _armedPolls > AssetWaitTimeoutPolls;

            if ((jacketReady && chartReady) || timedOut)
            {
                _dumpDone = true;
                LogSongInfoContainerDetails(infoContainer, logger, jacketReady, chartReady, timedOut);
            }
        }

        private static void ResetPendingDump()
        {
            _dumpArmed = false;
            _dumpDone = false;
            _armedByHook = false;
            _armedPolls = 0;
            _pendingContainer = null;
            Patch_SongInfoContainer_Progress.Reset();
        }

        /// <summary>
        /// 곡 제목 슬롯(본체 + 그림자)이 전부 동일한 실제 문자열로 채워졌는지 확인한다.
        /// 프리팹 기본값은 슬롯마다 다른 더미('... long name here' / 'Song Name')라 이 조건에서 걸러진다.
        /// </summary>
        private static bool IsSongInfoPopulated(SongInfoContainer info)
        {
            try
            {
                var titles = info.songTitle;
                if (titles == null || titles.Length == 0) return false;

                string first = SafeGetText(titles[0]);
                if (string.IsNullOrEmpty(first)) return false;
                if (first.Equals("Song Name", StringComparison.OrdinalIgnoreCase)) return false;

                for (int i = 1; i < titles.Length; i++)
                {
                    if (!string.Equals(SafeGetText(titles[i]), first, StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>자켓 Addressable 이 도착해 머티리얼 텍스처가 기본값(white)에서 교체됐는지 확인한다.</summary>
        private static bool IsJacketReady(SongInfoContainer info)
        {
            try
            {
                var jackets = info.jacket;
                if (jackets == null || jackets.Length == 0) return true; // 자켓 슬롯이 없으면 대기 불필요

                for (int i = 0; i < jackets.Length; i++)
                {
                    var j = jackets[i];
                    if (j == null) continue;

                    var mat = j._H;
                    if (mat == null) return false;

                    var tex = mat.mainTexture;
                    if (tex == null) return false;
                    if (string.IsNullOrEmpty(tex.name) || tex.name.Equals("white", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>LogicalNotePlayer 에 차트가 실제로 적재됐는지 확인한다.</summary>
        private static bool IsChartReady()
        {
            try
            {
                var np = Il2Cppifapp.Game.LogicalNotePlayer._Qe;
                return np != null && !string.IsNullOrEmpty(np._Ve);
            }
            catch { return false; }
        }

        private static void LogSongInfoContainerDetails(
            SongInfoContainer infoContainer,
            MelonLogger.Instance logger,
            bool jacketReady,
            bool chartReady,
            bool timedOut)
        {
            logger.Msg("══════════════════════════════════════════════════════════════════════════");
            logger.Msg($"[JacketHook][플레이 씬] SongInfoContainer UI 및 커버/재생바 상태 " +
                       $"(트리거: {(_armedByHook ? "_QCA 재생바 후킹" : "UI 폴링")}, 대기 {_armedPolls}폴링)");

            if (timedOut)
            {
                logger.Warning($"  ! 로드 대기 타임아웃({AssetWaitTimeoutPolls}폴링): " +
                               $"자켓 {(jacketReady ? "OK" : "미완료")}, 차트 {(chartReady ? "OK" : "미완료")} — 아래 값은 미완성 상태일 수 있습니다.");
            }

            // 차트/엔진 실제 메타데이터 확인
            long chartSongId = -1;
            string chartId = null;
            try
            {
                var notePlayer = Il2Cppifapp.Game.LogicalNotePlayer._Qe;
                if (notePlayer != null)
                {
                    chartSongId = notePlayer._We;
                    chartId = StripChartExtension(notePlayer._Ve);
                    logger.Msg($"  ├ [현재 차트] 차트 파일: '{notePlayer._Ve}', 차트 BGM: '{notePlayer._we}', ID: {chartSongId}");
                }
            }
            catch { }

            // 곡 데이터(SongInfo / SongChartInfo) 조회
            LogSongDataEntry(infoContainer, chartSongId, chartId, logger);

            // 난독화 필드가 실제로 뭘 담는지 값으로 확인 (_we/_We 가 비어 나온 건 이걸로 판별)
            NotePlayerProbe.DumpNotePlayerFields(logger);

            // 1) 곡 제목 (FastText)
            if (infoContainer.songTitle != null)
            {
                for (int i = 0; i < infoContainer.songTitle.Length; i++)
                {
                    var st = infoContainer.songTitle[i];
                    if (st != null)
                    {
                        string text = SafeGetText(st);
                        logger.Msg($"  ├ [곡 이름 UI] songTitle[{i}]: '{text}' (Obj: {st.name})");
                    }
                }
            }

            // 2) 아티스트 (FastText)
            if (infoContainer.songArtist != null)
            {
                for (int i = 0; i < infoContainer.songArtist.Length; i++)
                {
                    var sa = infoContainer.songArtist[i];
                    if (sa != null)
                    {
                        string text = SafeGetText(sa);
                        logger.Msg($"  ├ [아티스트 UI] songArtist[{i}]: '{text}' (Obj: {sa.name})");
                    }
                }
            }

            // 3) 난이도 텍스트 — [0] 만 보면 안 된다.
            //    songTitle 처럼 그림자/본체로 나뉘거나 레벨이 자리수별로 쪼개져 있을 수 있어서
            //    (Rating 11 인데 [0] 이 '1' 로 나온 사례) 슬롯을 전부 찍는다.
            LogTextSlots("난이도 이름", infoContainer.difficultyNameText, logger);
            LogTextSlots("난이도 레벨", infoContainer.difficultyLevelText, logger);

            // 4) 커버/자켓 (Constrained2D)
            if (infoContainer.jacket != null)
            {
                for (int i = 0; i < infoContainer.jacket.Length; i++)
                {
                    var j = infoContainer.jacket[i];
                    if (j != null)
                    {
                        var mat = j._H;
                        string matName = mat != null ? mat.name : "null";
                        string texName = mat != null && mat.mainTexture != null ? mat.mainTexture.name : "null";
                        logger.Msg($"  ├ [커버/자켓] jacket[{i}]: {j.name} (머티리얼: {matName}, 텍스처: {texName})");
                    }
                }
            }

            // 5) 재생바 (progressBar & progressIndicator)
            if (infoContainer.progressBar != null)
            {
                var pos = infoContainer.progressBar.transform.position;
                logger.Msg($"  ├ [재생바] progressBar: {infoContainer.progressBar.name} (활성: {infoContainer.progressBar.gameObject.activeInHierarchy}, Pos: {pos})");
            }
            if (infoContainer.progressIndicator != null)
            {
                logger.Msg($"  ├ [인디케이터] progressIndicator: {infoContainer.progressIndicator.name} (활성: {infoContainer.progressIndicator.gameObject.activeInHierarchy})");
            }
            if (infoContainer.progressBarStartPoint != null && infoContainer.progressBarEndPoint != null)
            {
                var startPos = infoContainer.progressBarStartPoint.transform.position;
                var endPos = infoContainer.progressBarEndPoint.transform.position;
                logger.Msg($"  └ [진행바 범위] {infoContainer.progressBarStartPoint.name}({startPos}) ➔ {infoContainer.progressBarEndPoint.name}({endPos})");
            }

            logger.Msg("══════════════════════════════════════════════════════════════════════════");
        }

        /// <summary>SongInfoContainer.dataAccess 로 현재 곡의 SongInfo/SongChartInfo 를 조회해 출력한다.</summary>
        private static void LogSongDataEntry(SongInfoContainer infoContainer, long songId, string chartId, MelonLogger.Instance logger)
        {
            // 조회 키는 차트 ID('alamode0')를 우선한다.
            // LogicalNotePlayer._We(곡 ID)는 이 시점에 -1 로 비어 있는 경우가 있어서(2026-09-10 실측)
            // 그걸 단독 조건으로 걸면 곡/차트 데이터 조회가 통째로 스킵된다.
            bool haveChartId = !string.IsNullOrEmpty(chartId);
            if (!haveChartId && songId <= 0) return;

            try
            {
                var allSongs = infoContainer.dataAccess?.SongData?.allSongInfo;
                if (allSongs == null) return;

                for (int i = 0; i < allSongs.Length; i++)
                {
                    var s = allSongs[i];
                    if (s == null) continue;

                    var charts = s.ChartInfos;

                    bool isMatch = false;
                    if (haveChartId)
                    {
                        for (int c = 0; charts != null && c < charts.Length && !isMatch; c++)
                        {
                            var ci = charts[c];
                            if (ci != null && string.Equals(ci.Id, chartId, StringComparison.OrdinalIgnoreCase)) isMatch = true;
                        }
                    }
                    else
                    {
                        isMatch = s.Id.Value == songId;
                    }

                    if (!isMatch) continue;

                    logger.Msg($"  ├ [곡 데이터] BaseName: '{s.BaseName}' (곡 Id: {s.Id.Value}), 캐릭터: {s.CharacterIdentifier}, " +
                               $"프리뷰: {s.PreviewStartSeconds:0.##}s ~ {s.PreviewEndSeconds:0.##}s");

                    if (charts == null) return;

                    for (int c = 0; c < charts.Length; c++)
                    {
                        var ci = charts[c];
                        if (ci == null) continue;

                        // 차트 ID 를 얻었으면 그 난이도만, 못 얻었으면 전 난이도를 출력한다.
                        if (haveChartId && !string.Equals(ci.Id, chartId, StringComparison.OrdinalIgnoreCase)) continue;

                        // Rating(int)과 LevelSectionIndicator(string)를 이어붙이면 'Lv.11' 이 Rating=11 인지
                        // Rating=1 + 표기 '1' 인지 구분이 안 된다. 반드시 분리해서 남길 것.
                        logger.Msg($"  ├ [차트 데이터] {ci.Difficulty} / Rating: {ci.Rating}, 표기접미: '{ci.LevelSectionIndicator}' " +
                                   $"(Id: '{ci.Id}', 채보: {ci.DisplayChartDesigner}, 자켓: {ci.DisplayJacketDesigner}, 해금: {ci.Available})");
                    }
                    return;
                }

                logger.Warning($"  ! 곡 데이터 조회 실패 — 차트 ID '{chartId}' / 곡 ID {songId} 에 맞는 SongInfo 가 없습니다");
            }
            catch (Exception ex)
            {
                logger.Warning($"  ! 곡 데이터 조회 예외: {ex.Message}");
            }
        }

        /// <summary>FastText 배열의 모든 슬롯을 인덱스와 함께 출력한다.</summary>
        private static void LogTextSlots(string label, Il2CppReferenceArray<FastText> slots, MelonLogger.Instance logger)
        {
            if (slots == null || slots.Length == 0) return;

            for (int i = 0; i < slots.Length; i++)
            {
                var ft = slots[i];
                if (ft == null) continue;

                logger.Msg($"  ├ [{label} UI] [{i}]: '{SafeGetText(ft)}' (Obj: {ft.name})");
            }
        }

        /// <summary>'alamode0.spc' 같은 차트 파일명에서 SongChartInfo.Id('alamode0')를 뽑아낸다.</summary>
        private static string StripChartExtension(string chartFileName)
        {
            if (string.IsNullOrEmpty(chartFileName)) return null;

            int dot = chartFileName.LastIndexOf('.');
            return dot > 0 ? chartFileName.Substring(0, dot) : chartFileName;
        }

        private static string SafeGetText(FastText ft)
        {
            try
            {
                if (ft == null) return string.Empty;
                return ft.GetText() ?? string.Empty;
            }
            catch
            {
                return ft?.name ?? string.Empty;
            }
        }

        private static string GetSongTitle(SongSelectScene scene, int songId)
        {
            try
            {
                var allSongs = scene.dataAccess?.SongData?.allSongInfo;
                if (allSongs != null)
                {
                    for (int i = 0; i < allSongs.Length; i++)
                    {
                        var s = allSongs[i];
                        if (s != null && s.Id.Value == songId)
                        {
                            return s.BaseName ?? $"Song_{songId}";
                        }
                    }
                }
            }
            catch { }

            return $"Song_{songId}";
        }

        #region Harmony Patches

        /// <summary>
        /// [후킹] 플레이 씬에서 재생바 진행도가 갱신되는 시점 (0.0f ~ 1.0f)
        /// 25% 구간마다 로깅하여 실시간 진행 상태를 보여줍니다.
        /// </summary>
        [HarmonyPatch(typeof(SongInfoContainer), nameof(SongInfoContainer._QCA))]
        private static class Patch_SongInfoContainer_Progress
        {
            private static int _lastLoggedQuarter = -1;

            public static void Reset()
            {
                _lastLoggedQuarter = -1;
            }

            [HarmonyPostfix]
            public static void Postfix(SongInfoContainer __instance, float __0)
            {
                try
                {
                    // _qCA 를 못 쓰므로 이 후킹이 플레이 씬 덤프의 무장 트리거를 겸한다.
                    ArmFromProgress(__instance);

                    int percent = Math.Clamp((int)(__0 * 100f), 0, 100);
                    int quarter = percent / 25; // 0, 1, 2, 3, 4 (0%, 25%, 50%, 75%, 100%)

                    if (quarter > _lastLoggedQuarter)
                    {
                        _lastLoggedQuarter = quarter;
                        _logger?.Msg($"[SongInfoContainer][재생바] 곡 진행도: {quarter * 25}% (현재 진행률: {__0 * 100f:F1}%)");
                    }
                }
                catch { }
            }
        }

        #endregion
    }
}
