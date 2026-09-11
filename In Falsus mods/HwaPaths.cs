using System;
using System.IO;
using MelonLoader;

namespace InFalsusMods
{
    /// <summary>
    /// 모드 전용 디렉터리(hwa) 및 파일 경로를 전역적으로 관리하는 유틸리티 클래스입니다.
    /// </summary>
    public static class HwaPaths
    {
        /// <summary>
        /// H:\steam\steamapps\common\In Falsus\hwa 디렉터리 경로
        /// </summary>
        public static string HwaDirectory { get; private set; }

        /// <summary>
        /// 모드 초기화 시 호출되어 hwa 폴더 존재 여부를 확인하고 없으면 자동 생성합니다.
        /// </summary>
        public static void Init(MelonLogger.Instance logger)
        {
            try
            {
                string gameDir = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
                if (string.IsNullOrEmpty(gameDir))
                {
                    gameDir = AppDomain.CurrentDomain.BaseDirectory;
                }

                HwaDirectory = Path.Combine(gameDir, "hwa");
                if (!Directory.Exists(HwaDirectory))
                {
                    Directory.CreateDirectory(HwaDirectory);
                    logger.Msg($"[HwaPaths] hwa 폴더 생성 완료: {HwaDirectory}");
                }
                else
                {
                    logger.Msg($"[HwaPaths] hwa 폴더 확인: {HwaDirectory}");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[HwaPaths] hwa 폴더 생성/확인 실패: {ex}");
            }
        }
    }
}
