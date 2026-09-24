using System;
using MelonLoader;

[assembly: MelonInfo(typeof(InFalsusMods.InFalsusMod), "InFalsusMods", "1.0.0", "화영왕")]
[assembly: MelonGame("lowiro", "infalsus")]

namespace InFalsusMods
{
    /// <summary>
    /// In Falsus 모드 전체의 라이프사이클을 총괄하는 메인 MelonMod 클래스입니다.
    /// </summary>
    public class InFalsusMod : MelonMod
    {
        private const int PollIntervalFrames = 30;
        private const int DetectorIntervalFrames = 10;

        private int _frames;

        public override void OnInitializeMelon()
        {
            // 1. hwa 전용 디렉터리 초기화
            HwaPaths.Init(LoggerInstance);

            // 2. Harmony 패치 일괄 등록 (어셈블리 전체)
            try
            {
                HarmonyInstance.PatchAll(typeof(InFalsusMod).Assembly);
                LoggerInstance.Msg("[InFalsusMod] Harmony 어셈블리 전체 패치 완료");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[InFalsusMod] Harmony 패치 실패: {ex}");
            }

            // 3. 서브시스템 초기화
            JacketHook.Init(HarmonyInstance, LoggerInstance);
            BgmHook.Init(HarmonyInstance, LoggerInstance);
            AudioInjector.Init(HarmonyInstance, LoggerInstance);
            JacketInjector.Init(HarmonyInstance, LoggerInstance);
            VideoInjector.Init(LoggerInstance);
        }

        public override void OnUpdate()
        {
            _frames++;

            // 매 프레임: 영상 시계를 게임 재생 위치에 맞춘다 (영상이 없으면 즉시 반환)
            VideoInjector.Update();

            // 10프레임 주기(약 6Hz): 상태 감지 및 인게임 차트/스토리/오디오/UI 틱
            if (_frames % DetectorIntervalFrames == 0)
            {
                SceneRefs.Refresh(LoggerInstance);
                ChartInjector.Tick(LoggerInstance);
                NoteListDumper.Tick(LoggerInstance);
                StorySkipUnlocker.Tick(LoggerInstance);
                CharacterUnlocker.Tick(LoggerInstance);
                JacketHook.Tick(LoggerInstance);
                BgmHook.Tick(LoggerInstance);
                AudioInjector.Tick(LoggerInstance);
                JacketInjector.Tick(LoggerInstance);
                VideoInjector.Tick(LoggerInstance);
                FmodProbe.Tick(LoggerInstance);
            }

            // 30프레임 주기(약 2Hz): 무거운 폴링 (곡 목록 덤프)
            if (_frames % PollIntervalFrames == 0)
            {
                SongListDumper.Tick(LoggerInstance);
            }
        }

        public override void OnApplicationQuit()
        {
            // 플레이 중 종료 시 영상 디코더가 Unity 종료를 붙잡지 않게 먼저 끊는다
            VideoInjector.Shutdown();
        }

        /// <summary>
        /// MelonLoader의 씬 로드 이벤트 진입점
        /// </summary>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            SceneRefs.OnSceneLoaded(buildIndex, sceneName, LoggerInstance);
        }

        /// <summary>
        /// MelonLoader의 씬 초기화 완료 이벤트 진입점
        /// </summary>
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            SceneRefs.Refresh(LoggerInstance);
        }
    }
}
