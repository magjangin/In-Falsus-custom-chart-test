using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Il2Cpp;                       // SongTransitionLayer 는 전역 네임스페이스에 있다
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// 씬 컴포넌트 조회 캐시.
    ///
    /// FindObjectOfType 은 씬 전체를 훑는 데다 Il2CppInterop 마샬링까지 얹혀서 비싸다.
    /// 모듈들이 각자 매 프레임 호출하면 프레임당 6회가 나가서 눈에 띄는 프레임 드랍이 생긴다
    /// (2026-09-10 실측 — 인게임에서 체감됨). 여기서 틱당 타입별 1회씩만 모아 처리한다.
    ///
    /// 파괴된 오브젝트는 틱마다 다시 찾으므로 자연히 걸러진다. 캐시된 참조를 쓰는 쪽에서는
    /// Unity 의 == null 오버로드로 판정할 것 (?? 는 파괴된 오브젝트를 못 걸러낸다).
    /// </summary>
    internal static class SceneRefs
    {
        public static GameScene GameScene { get; private set; }
        public static SongSelectScene SongSelect { get; private set; }
        public static SongTransitionLayer Transition { get; private set; }
        public static StoryScene Story { get; private set; }
        public static HubScene Hub { get; private set; }
        public static CharacterSelectLayer CharacterSelect { get; private set; }

        /// <summary>
        /// 못 찾은(=현재 씬에 없는) 타입을 다시 뒤지는 주기. 디텍터 틱 6회 = 60프레임 ≈ 0.5초.
        ///
        /// 프로파일러 실측(2026-09-10)에서 매 틱 4종을 전부 뒤지면 **호출당 3.75ms**(600프레임당 225ms)로
        /// 모드 비용의 대부분을 차지했다. FindObjectOfType 은 그만큼 비싸다.
        /// 이미 찾아 둔 참조는 파괴되기 전까지 재사용하고, 없는 것만 가끔 다시 뒤진다.
        /// </summary>
        private const int MissRetryIntervalTicks = 6;

        private static int _ticks;

        /// <summary>디텍터 틱마다 1회 호출. 이 안에서만 실제 씬 탐색이 일어난다.</summary>
        public static void Refresh()
        {
            bool retryMisses = (_ticks++ % MissRetryIntervalTicks) == 0;

            GameScene = Keep(GameScene, retryMisses);
            SongSelect = Keep(SongSelect, retryMisses);
            Transition = Keep(Transition, retryMisses);
            Story = Keep(Story, retryMisses);
            Hub = Keep(Hub, retryMisses);

            if (Hub != null && Hub.characterSelectLayer != null)
            {
                CharacterSelect = Hub.characterSelectLayer;
            }
            else
            {
                CharacterSelect = Keep(CharacterSelect, retryMisses);
            }
        }

        /// <summary>살아있는 참조는 그대로 쓰고, 없을 때만(그마저도 가끔만) 다시 탐색한다.</summary>
        private static T Keep<T>(T current, bool retryMisses) where T : UnityEngine.Object
        {
            // 제네릭 T 로는 Unity 의 == null 연산자 오버로드가 걸리지 않는다(정적 해석).
            // 파괴된 오브젝트를 걸러내려면 반드시 UnityEngine.Object 로 캐스팅해서 비교할 것.
            if ((UnityEngine.Object)current != null) return current;

            return retryMisses ? Find<T>() : null;
        }

        private static T Find<T>() where T : UnityEngine.Object
        {
            try
            {
                var obj = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<T>());
                return obj == null ? null : obj.TryCast<T>();
            }
            catch
            {
                return null;
            }
        }
    }
}
