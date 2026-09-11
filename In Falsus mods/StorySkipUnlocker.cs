using System;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// 스토리 빨리감기/스킵 잠금 해제.
    ///
    /// 원래 스킵은 스크립트가 StoryFastForwardAllowCommand(문법상 AllowFastForward)를 실행한
    /// 구간에서만 켜진다. 그 결과가 StoryState.IsStoryAllowFastForward이고, false면 메뉴의
    /// 빨리감기 버튼이 비활성으로 보인다. 매 프레임 true로 눌러줘서 항상 켜지게 한다.
    ///
    /// 주의: StoryScene이 StoryState를 들고 있는 프로퍼티 이름 `_gS`는 Obfuz 난독화된 이름이라
    /// 게임 업데이트 시 바뀔 수 있다. 그때는 Decompiled/Il2Cppifapp/Game/Scenes/StoryScene.cs
    /// 에서 `public StoryState _xx { get; set; }` 를 다시 찾아 고칠 것.
    /// </summary>
    internal static class StorySkipUnlocker
    {
        private static bool _logged;
        private static bool _broken;

        public static void Tick(MelonLogger.Instance logger)
        {
            if (_broken) return;

            try
            {
                var scene = SceneRefs.Story;
                if (scene == null) return;

                var state = scene._gS;
                if (state == null || state.IsStoryAllowFastForward) return;

                state.IsStoryAllowFastForward = true;

                if (!_logged)
                {
                    _logged = true;
                    logger.Msg("스토리 빨리감기 잠금 해제 (IsStoryAllowFastForward = true)");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                logger.Error($"StoryState 접근 실패 - 스킵 해제 비활성화: {ex.Message}");
            }
        }
    }
}
