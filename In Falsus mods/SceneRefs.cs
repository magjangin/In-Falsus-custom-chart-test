using System;
using MelonLoader;
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
        public static CoreScene CoreScene { get; private set; }
        public static Camera MainCamera { get; private set; }

        /// <summary>현재 활성화된 씬 이름</summary>
        public static string CurrentSceneName { get; private set; } = "Unknown";

        /// <summary>현재 활성화된 씬 빌드 인덱스</summary>
        public static int CurrentBuildIndex { get; private set; } = -1;

        /// <summary>플레이 씬 여부 (GameScene 활성 상태)</summary>
        public static bool IsGameScene => (UnityEngine.Object)GameScene != null;

        /// <summary>곡 선택 화면 여부 (SongSelectScene 활성 상태)</summary>
        public static bool IsSongSelectScene => (UnityEngine.Object)SongSelect != null;

        /// <summary>허브 화면 여부 (HubScene 활성 상태)</summary>
        public static bool IsHubScene => (UnityEngine.Object)Hub != null;

        /// <summary>스토리 화면 여부 (StoryScene 활성 상태)</summary>
        public static bool IsStoryScene => (UnityEngine.Object)Story != null;

        /// <summary>씬 변경 시 발생하는 이벤트 (buildIndex, sceneName)</summary>
        public static event Action<int, string> OnSceneChanged;

        private static bool _loggedCameraThisPlay;

        private static readonly System.Collections.Generic.Dictionary<string, string> KnownSceneNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "91ffe1a657cc84028b64801a0ec8efef", "CoreScene" },
                { "474880cd7737afe4e9758f1a75e48a7b", "HubScene" },
                { "51299cded558992458e26ef3c54713de", "CharacterSelectScene" },
                { "13572b4fe2a8a2346a4acee3c29ad5ea", "SongSelectScene" },
                { "2e7ae588d8f524047bc76ff9c214696a", "ResultsScene" },
                { "986f79fbb15418b48a813f0a31cbc6cc", "TimelineScene" },
                { "68f68491d98d243489a916de6ed67fb5", "StorySongTransitionScene" },
                { "6bd44d7524f1de344b750c1782dba00b", "ThanksForPlayingScene" },
                { "a202c27eb034d48749eae4255846b069", "RecipeScene" },
            };

        /// <summary>32자리 Addressables 해시명을 사람이 읽을 수 있는 씬 명칭으로 해독합니다.</summary>
        public static string ResolveSceneName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "Unknown";
            if (KnownSceneNames.TryGetValue(rawName, out var friendly)) return friendly;

            // 런타임 활성 컴포넌트 기반 역산
            if (IsGameScene) return "GameScene (플레이)";
            if (IsSongSelectScene) return "SongSelectScene (곡 선택)";
            if (IsHubScene) return "HubScene (메인 허브)";
            if (IsStoryScene) return "StoryScene (스토리)";

            return rawName;
        }

        /// <summary>
        /// MelonLoader의 씬 로드 콜백에서 호출되는 핵심 씬 감지 메서드입니다.
        /// </summary>
        public static void OnSceneLoaded(int buildIndex, string sceneName, MelonLogger.Instance log)
        {
            CurrentBuildIndex = buildIndex;
            CurrentSceneName = string.IsNullOrEmpty(sceneName) ? "Unknown" : sceneName;

            string friendlyName = ResolveSceneName(CurrentSceneName);
            string nameDisplay = friendlyName != CurrentSceneName
                ? $"'{CurrentSceneName}' (해독: {friendlyName})"
                : $"'{CurrentSceneName}'";

            log?.Msg("──────────────────────────────────────────────────────────────────────────");
            log?.Msg($"[SceneRefs][씬 감지] 씬 로드 완료: {nameDisplay} (BuildIndex: {CurrentBuildIndex})");
            log?.Msg("──────────────────────────────────────────────────────────────────────────");

            _loggedCameraThisPlay = false;
            Refresh(log);

            try
            {
                OnSceneChanged?.Invoke(buildIndex, CurrentSceneName);
            }
            catch { }
        }

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
        public static void Refresh(MelonLogger.Instance log = null)
        {
            bool retryMisses = (_ticks++ % MissRetryIntervalTicks) == 0;

            GameScene = Keep(GameScene, retryMisses);
            SongSelect = Keep(SongSelect, retryMisses);
            Transition = Keep(Transition, retryMisses);
            Story = Keep(Story, retryMisses);
            Hub = Keep(Hub, retryMisses);
            CoreScene = Keep(CoreScene, retryMisses);

            if (Hub != null && Hub.characterSelectLayer != null)
            {
                CharacterSelect = Hub.characterSelectLayer;
            }
            else
            {
                CharacterSelect = Keep(CharacterSelect, retryMisses);
            }

            // 플레이 씬(GameScene) 메인 카메라 감지
            if (GameScene != null)
            {
                if ((UnityEngine.Object)MainCamera == null)
                {
                    MainCamera = FindMainCamera();
                }

                if ((UnityEngine.Object)MainCamera != null && !_loggedCameraThisPlay)
                {
                    _loggedCameraThisPlay = true;
                    LogCameraDetails(MainCamera, log);
                }
            }
            else
            {
                MainCamera = null;
                _loggedCameraThisPlay = false;
            }
        }

        private static Camera FindMainCamera()
        {
            try
            {
                // 1순위: Unity 표준 Camera.main (MainCamera 태그)
                var cam = Camera.main;
                if ((UnityEngine.Object)cam != null) return cam;

                // 2순위: CoreScene 인스턴스에 등록된 MainCamera
                if (CoreScene != null && (UnityEngine.Object)CoreScene.MainCamera != null)
                {
                    return CoreScene.MainCamera;
                }

                // 3순위: GameScene 하위의 자식 카메라
                if (GameScene != null)
                {
                    var childCam = GameScene.GetComponentInChildren<Camera>();
                    if ((UnityEngine.Object)childCam != null) return childCam;
                }

                // 4순위: 씬 내 모든 활성 카메라 중 폴백
                var allCams = Camera.allCameras;
                if (allCams != null && allCams.Length > 0)
                {
                    for (int i = 0; i < allCams.Length; i++)
                    {
                        var c = allCams[i];
                        if ((UnityEngine.Object)c != null && c.isActiveAndEnabled) return c;
                    }
                }
            }
            catch { }

            return null;
        }

        private static void LogCameraDetails(Camera cam, MelonLogger.Instance log)
        {
            if (log == null || (UnityEngine.Object)cam == null) return;

            try
            {
                var tr = cam.transform;
                var pos = tr != null ? tr.position : Vector3.zero;
                var rot = tr != null ? tr.rotation.eulerAngles : Vector3.zero;

                log.Msg("══════════════════════════════════════════════════════════════════════════");
                log.Msg($"[SceneRefs][플레이 씬 카메라 감지] MainCamera 획득 성공!");
                log.Msg($"  ├ 오브젝트 이름: '{cam.name}' (태그: '{cam.tag}')");
                log.Msg($"  ├ 카메라 위치(Pos): ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})");
                log.Msg($"  ├ 카메라 회전(Rot): ({rot.x:F2}, {rot.y:F2}, {rot.z:F2})");
                log.Msg($"  ├ 직교 투영(Orthographic): {cam.orthographic} " +
                        $"{(cam.orthographic ? $"(Size: {cam.orthographicSize:F2})" : $"(FOV: {cam.fieldOfView:F2}°)")}");
                log.Msg($"  └ 클리핑 범위: Near {cam.nearClipPlane:F2} ~ Far {cam.farClipPlane:F2}");
                log.Msg("══════════════════════════════════════════════════════════════════════════");
            }
            catch { }
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
