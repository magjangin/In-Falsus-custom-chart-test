using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2Cppifapp.Game.Data;
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// 캐릭터 선택 화면(CharacterSelectLayer)의 모든 캐릭터 및 스킬을 강제 해금하는 모듈입니다.
    ///
    /// 1) Harmony 후킹:
    ///    - CharacterSelectLayer._KI(CharacterIdentifier): 캐릭터 해금 여부 판정 -> 항상 true 반환.
    ///    - CharacterSelectLayer._LI(): 캐릭터 선택(SELECT) 버튼 활성화 판정 -> 항상 true 반환.
    /// 2) 런타임 UI 활성화:
    ///    - storyDisabledWidgets("???" 잠금 마스크) 숨김 처리.
    ///    - storyActiveWidgets(캐릭터 초상화 및 정보) 강제 활성화.
    ///    - selectButton, leftButton, rightButton 상시 상호작용 가능하도록 보장.
    ///    - (옵션) 캐릭터 스킬 레벨(_Dl, _el, _El) 및 공용 스킬(_fl, _Fl) 최대치 주입.
    /// </summary>
    internal static class CharacterUnlocker
    {
        public static bool Enabled { get; set; } = true;

        /// <summary>캐릭터 스킬 및 공용 스킬 레벨을 최대치로 해금할지 여부</summary>
        public static bool UnlockMaxSkills { get; set; } = true;

        private static bool _loggedThisOpen;

        public static void Tick(MelonLogger.Instance log)
        {
            if (!Enabled) return;

            try
            {
                var selectLayer = SceneRefs.CharacterSelect;
                if (selectLayer == null || !selectLayer.gameObject.activeInHierarchy)
                {
                    _loggedThisOpen = false;
                    return;
                }

                // 1. 하단 잠금 오버레이("???") 숨김 및 활성 위젯 표시
                UnlockBottomWidgets(selectLayer);

                // 2. 선택 버튼 활성화
                if (selectLayer.selectButton != null)
                {
                    selectLayer.selectButton.gameObject.SetActive(true);
                    selectLayer.selectButton.button?._BSA(true);
                }

                // 3. 좌우 네비게이션 버튼 활성화
                if (selectLayer.leftButton != null)
                {
                    selectLayer.leftButton.gameObject.SetActive(true);
                    selectLayer.leftButton.button?._BSA(true);
                }
                if (selectLayer.rightButton != null)
                {
                    selectLayer.rightButton.gameObject.SetActive(true);
                    selectLayer.rightButton.button?._BSA(true);
                }

                // 4. 스킬 레벨 최대치 적용 (옵션)
                if (UnlockMaxSkills)
                {
                    MaximizeSkillLevels(selectLayer);
                }

                if (!_loggedThisOpen)
                {
                    _loggedThisOpen = true;
                    log.Msg("══════════════════════════════════════════════════════════════════════════");
                    log.Msg("[CharacterUnlocker] 캐릭터 선택 화면 감지 — 전체 캐릭터/스킬 해금 활성화!");
                    log.Msg("  ├ 하단 슬롯 '???' 잠금 해제 및 초상화 활성화 완료");
                    log.Msg("  ├ 좌우 이동 버튼 및 SELECT 버튼 활성화 완료");
                    if (UnlockMaxSkills)
                    {
                        log.Msg("  └ 캐릭터 스킬 및 공유 스킬 레벨 최대치 적용 완료");
                    }
                    log.Msg("══════════════════════════════════════════════════════════════════════════");
                }
            }
            catch (Exception ex)
            {
                // UI 갱신 중 예외는 안전하게 무시
                log.Warning($"[CharacterUnlocker] UI 갱신 중 예외: {ex.Message}");
            }
        }

        /// <summary>
        /// 하단 5개 슬롯 중 잠긴("???") 위젯들을 비활성화하고, 활성 슬롯을 켭니다.
        /// </summary>
        private static void UnlockBottomWidgets(CharacterSelectLayer layer)
        {
            var disabled = layer.storyDisabledWidgets;
            if (disabled != null)
            {
                for (int i = 0; i < disabled.Length; i++)
                {
                    var d = disabled[i];
                    if (d != null && d.gameObject.activeSelf)
                    {
                        d.gameObject.SetActive(false);
                    }
                }
            }

            var active = layer.storyActiveWidgets;
            if (active != null)
            {
                for (int i = 0; i < active.Length; i++)
                {
                    var a = active[i];
                    if (a != null && !a.gameObject.activeSelf)
                    {
                        a.gameObject.SetActive(true);
                    }
                }
            }
        }

        /// <summary>
        /// 캐릭터 스킬 레벨(_Dl, _el, _El) 및 공용 스킬(_fl, _Fl)을 최대치로 설정합니다.
        /// </summary>
        private static void MaximizeSkillLevels(CharacterSelectLayer layer)
        {
            try
            {
                // _Dl: 캐릭터별 스킬 레벨 배열
                MaximizeSkillArray(layer._Dl);
                MaximizeSkillArray(layer._el);
                MaximizeSkillArray(layer._El);

                // _fl, _Fl: 글로벌 공용 스킬
                var fl = layer._fl;
                fl.DiscoveryBoost = 5;
                fl.IntuitionBoost = 5;
                fl.CardInventoryExpansion = 5;
                fl.IotaRetrievalBoost = 5;
                fl.IotaStackBoost = 5;
                fl.IotaPotencyBoost = 5;
                fl.IotaPotencyMaximization = 5;
                layer._fl = fl;

                var Fl = layer._Fl;
                Fl.DiscoveryBoost = 5;
                Fl.IntuitionBoost = 5;
                Fl.CardInventoryExpansion = 5;
                Fl.IotaRetrievalBoost = 5;
                Fl.IotaStackBoost = 5;
                Fl.IotaPotencyBoost = 5;
                Fl.IotaPotencyMaximization = 5;
                layer._Fl = Fl;
            }
            catch { }
        }

        private static void MaximizeSkillArray(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<CharacterSkillLevels> arr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var skill = arr[i];
                skill.Infusion = 5;
                skill.Synesthesia = 5;
                skill.ExpandedIotaLimit = 5;
                skill.OutOfBoundsTolerance = 5;
                skill.OverlapTolerance = 5;
                skill.DisconnectionTolerance = 5;
                skill.AugmentResolve = 5;
                arr[i] = skill;
            }
        }

        #region Harmony Patches

        /// <summary>
        /// [후킹 1] 캐릭터 해금 여부 판정 (_KI) -> 모든 캐릭터에 대해 항상 true 반환
        /// </summary>
        [HarmonyPatch(typeof(CharacterSelectLayer), nameof(CharacterSelectLayer._KI))]
        private static class Patch_CharacterSelectLayer_IsUnlocked
        {
            [HarmonyPrefix]
            public static bool Prefix(CharacterIdentifier __0, ref bool __result)
            {
                if (!Enabled) return true;

                __result = true;
                return false; // 원본 메서드 건너뛰고 상시 해금
            }
        }

        /// <summary>
        /// [후킹 2] 캐릭터 선택(SELECT) 버튼 활성화 여부 판정 (_LI) -> 상시 선택 가능
        /// </summary>
        [HarmonyPatch(typeof(CharacterSelectLayer), nameof(CharacterSelectLayer._LI))]
        private static class Patch_CharacterSelectLayer_CanSelect
        {
            [HarmonyPrefix]
            public static bool Prefix(ref bool __result)
            {
                if (!Enabled) return true;

                __result = true;
                return false;
            }
        }

        #endregion
    }
}
