using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2Cpp_J;
using Il2Cppifapp.Game.Common;
using Il2Cppifapp.Game.Data;
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// 곡 목록(SongSelectScene)의 프리뷰 BGM 및 플레이 씬(GameScene)의 인게임 BGM을 감지하고 제어/후킹하는 모듈
    /// StreamingAssetsMapping을 통해 32자리 해시(GUID)를 원래 논리 파일명(예: alamode.ogg)으로 자동 해독합니다.
    /// </summary>
    public static class BgmHook
    {
        private static MelonLogger.Instance _logger;

        // StreamingAssetsMapping GUID -> 논리 경로 매핑 캐시
        private static readonly Dictionary<string, string> _guidToPath = new(StringComparer.OrdinalIgnoreCase);

        // 곡 목록(SongSelectScene) 상태 추적
        private static string _lastPreviewClipName = null;
        private static int _lastPreviewSongId = -1;

        public static void Init(HarmonyLib.Harmony harmony, MelonLogger.Instance logger)
        {
            _logger = logger;
            _logger.Msg("[BgmHook] BGM 후킹 및 감지 모듈 초기화 완료");
        }

        /// <summary>
        /// StreamingAssetsMapping 테이블을 1회 로드하여 GUID <-> 원래 파일명 매핑 캐시를 생성합니다.
        /// </summary>
        public static void EnsureMappingLoaded(DataAccess dataAccess)
        {
            if (_guidToPath.Count > 0 || dataAccess == null) return;

            try
            {
                var mapping = dataAccess.StreamingAssetsMapping;
                if (mapping == null) return;

                var entries = mapping.Entries;
                if (entries == null) return;

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry != null && !string.IsNullOrEmpty(entry.Guid) && !string.IsNullOrEmpty(entry.FullLookupPath))
                    {
                        _guidToPath[entry.Guid] = entry.FullLookupPath;
                    }
                }

                _logger?.Msg($"[BgmHook] StreamingAssetsMapping 로드 완료 (총 {_guidToPath.Count}개 에셋 매핑)");
            }
            catch (Exception ex)
            {
                _logger?.Error($"[BgmHook] 매핑 로드 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 32자리 GUID를 원래의 논리 경로 파일명(예: alamode.ogg)으로 변환합니다.
        /// </summary>
        public static string ResolveClipName(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "None";
            if (_guidToPath.TryGetValue(guid, out var realPath))
            {
                return $"{guid} (논리경로: \"{realPath}\")";
            }
            return $"{guid} (미매핑)";
        }

        private static string GetSongTitle(DataAccess dataAccess, int songId)
        {
            try
            {
                var allSongs = dataAccess?.SongData?.allSongInfo;
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

        /// <summary>
        /// 매 프레임 실행되는 상태 감지 (UI 변경 및 재생 위치 폴링)
        /// </summary>
        public static void Tick(MelonLogger.Instance logger)
        {
            try
            {
                // 1. 곡 목록 화면 프리뷰 BGM 상태 폴링
                PollSongSelectBgm(logger);

                // 2. 플레이 씬 BGM 재생 상태 폴링
                PollGameSceneBgm(logger);
            }
            catch
            {
                // 무시하여 게임 플레이 안정성 유지
            }
        }

        private static void PollSongSelectBgm(MelonLogger.Instance logger)
        {
            var selectScene = SceneRefs.SongSelect;
            if (selectScene == null)
            {
                _lastPreviewClipName = null;
                _lastPreviewSongId = -1;
                return;
            }

            EnsureMappingLoaded(selectScene.dataAccess);

            var previewHandle = selectScene._vr;
            int currentSongId = selectScene._sr.Value;

            if (previewHandle != null)
            {
                string clipName = previewHandle._d;
                if (clipName != _lastPreviewClipName || currentSongId != _lastPreviewSongId)
                {
                    _lastPreviewClipName = clipName;
                    _lastPreviewSongId = currentSongId;

                    string songTitle = GetSongTitle(selectScene.dataAccess, currentSongId);
                    float startSec = selectScene._wr != null ? selectScene._wr.PreviewStartSeconds : 0f;
                    float endSec = selectScene._wr != null ? selectScene._wr.PreviewEndSeconds : 0f;

                    logger.Msg("──────────────────────────────────────────────────────────────────────────");
                    logger.Msg($"[BgmHook][곡 목록 프리뷰 감지] 곡: '{songTitle}' (Id: {currentSongId})");
                    logger.Msg($"  └ BGM 파일/클립: {ResolveClipName(clipName)}");
                    logger.Msg($"  └ 프리뷰 구간: {startSec:F2}초 ~ {endSec:F2}초 (오프셋: {selectScene._Vr:F2}초)");
                    logger.Msg($"  └ 핸들 Duration: {previewHandle._VUA:F2}초");
                    logger.Msg("──────────────────────────────────────────────────────────────────────────");
                }
            }
        }

        // 플레이 씬(GameScene) 상태 추적
        private static string _lastPlayBgmName = null;
        private static _Cg _activePlayBgmHandle = null;
        private static bool _isPlayingSceneBgm = false;

        private static void PollGameSceneBgm(MelonLogger.Instance logger)
        {
            var gameScene = SceneRefs.GameScene;
            if (gameScene == null)
            {
                if (_isPlayingSceneBgm)
                {
                    _isPlayingSceneBgm = false;
                    _lastPlayBgmName = null;
                    _activePlayBgmHandle = null;
                }
                return;
            }

            EnsureMappingLoaded(gameScene.dataAccess);

            // _activePlayBgmHandle이 GameScene._dK 후킹을 통해 주입되었거나 감지되었을 때
            if (_activePlayBgmHandle != null && !_isPlayingSceneBgm)
            {
                string bgmName = _activePlayBgmHandle._d;
                if (!string.IsNullOrEmpty(bgmName) && bgmName != _lastPlayBgmName)
                {
                    _isPlayingSceneBgm = true;
                    _lastPlayBgmName = bgmName;

                    // _VUA 의 단위는 미확정이다. 48kHz PCM 샘플로 가정하면 26.7초가 나오는데
                    // 같은 곡의 실제 차트 길이는 133.5초라 앞뒤가 안 맞는다(2026-09-10 실측).
                    // 틀린 환산값을 찍어 오해를 부르느니 원시값만 남긴다.

                    logger.Msg("══════════════════════════════════════════════════════════════════════════");
                    logger.Msg($"[BgmHook][플레이 씬 BGM 감지] BGM 트랙: {ResolveClipName(bgmName)}");
                    logger.Msg($"  └ 오디오 길이 _VUA: {_activePlayBgmHandle._VUA:N0} (단위 미확정, AssetId: {_activePlayBgmHandle._SUA})");

                    var player = gameScene._gN ?? GameScene._nk();
                    if (player != null)
                    {
                        // _jiA 는 '현재 시간'이 아니다 — 곡이 흐르는 내내 135.669 로 고정돼 있었다(2026-09-10 실측).
                        // 실제 재생 위치는 _miA 이고 리드인 구간에서는 음수로 나온다.
                        logger.Msg($"  └ 오디오 플레이어(_Dg) 연결됨 (재생 여부: {player._GiA()}, " +
                                   $"재생 위치 _miA: {player._miA():F3}초, 전체 길이로 보이는 _jiA: {player._jiA():F3}초)");
                    }
                    logger.Msg("══════════════════════════════════════════════════════════════════════════");
                }
            }
        }

        #region Harmony Patches

        /// <summary>
        /// [후킹 1] 곡 목록에서 프리뷰 BGM 오디오 객체가 전달되는 시점 (_co)
        /// </summary>
        [HarmonyPatch(typeof(SongSelectScene), nameof(SongSelectScene._co))]
        private static class Patch_SongSelectScene_PreviewBgm
        {
            [HarmonyPostfix]
            public static void Postfix(SongSelectScene __instance, _Cg __0)
            {
                try
                {
                    if (__0 == null) return;

                    EnsureMappingLoaded(__instance.dataAccess);

                    string clipName = __0._d;
                    int songId = __instance._sr.Value;
                    string songTitle = GetSongTitle(__instance.dataAccess, songId);

                    _logger?.Msg($"[BgmHook][Hook:SongSelect._co] 프리뷰 오디오 로드 완료: '{songTitle}' (클립: {ResolveClipName(clipName)}, Id: {songId})");
                }
                catch (Exception ex)
                {
                    _logger?.Error($"[BgmHook][Hook:SongSelect._co] 예외 발생: {ex}");
                }
            }
        }

        /// <summary>
        /// [후킹 2] 플레이 씬(GameScene)에서 메인 BGM 오디오 객체가 로드되는 시점 (_dK)
        /// 게임이 씬 시작 시 실제 곡 BGM(_Cg)을 넘겨받는 핵심 메서드입니다.
        /// </summary>
        [HarmonyPatch(typeof(GameScene), nameof(GameScene._dK))]
        private static class Patch_GameScene_LoadBgm
        {
            [HarmonyPostfix]
            public static void Postfix(GameScene __instance, _Cg __0)
            {
                try
                {
                    if (__0 == null) return;

                    EnsureMappingLoaded(__instance.dataAccess);

                    _activePlayBgmHandle = __0;
                    string clipName = __0._d;
                    _logger?.Msg($"[BgmHook][Hook:GameScene._dK] 플레이 씬 메인 BGM 로드 완료: {ResolveClipName(clipName)} " +
                                 $"(AssetId: {__0._SUA}, 길이 _VUA: {__0._VUA:N0} — 단위 미확정)");
                }
                catch (Exception ex)
                {
                    _logger?.Error($"[BgmHook][Hook:GameScene._dK] 예외 발생: {ex}");
                }
            }
        }

        /// <summary>
        /// [후킹 3] 고정밀 오디오 플레이어(_Dg)에서 실제로 BGM 재생이 시작되는 시점 (_KiA)
        /// </summary>
        [HarmonyPatch(typeof(_Dg), nameof(_Dg._KiA))]
        private static class Patch_Dg_PlayBgm
        {
            [HarmonyPostfix]
            public static void Postfix(_Dg __instance, float __0, double __1)
            {
                try
                {
                    _logger?.Msg($"[BgmHook][Hook:_Dg._KiA] 인게임 BGM 재생 트리거! 볼륨: {__0:F2}, 시작 오프셋: {__1:F3}초");
                }
                catch (Exception ex)
                {
                    _logger?.Error($"[BgmHook][Hook:_Dg._KiA] 예외 발생: {ex}");
                }
            }
        }

        #endregion
    }
}
