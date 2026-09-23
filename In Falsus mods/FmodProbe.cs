using System;
using System.IO;
using System.Runtime.InteropServices;
using MelonLoader;
using Il2CppFMOD;
using Il2Cpp_J;

namespace InFalsusMods
{
    /// <summary>
    /// 커스텀 음원 B 경로 1단계 프로브 — 게임의 FMOD System(_IF._ZSA)으로 hwa/music.ogg 를 직접 재생한다.
    ///
    /// 게임은 FMOD 파일 I/O 를 lowiro 네이티브 콜백(ifapp_fmod_native)으로 갈아끼워 두고 sam/ 파일을
    /// 읽을 때마다 복호화한다. 그래서 평문 OGG 를 sam/ 에 넣으면 무음이 된다(2026-09-23 A 테스트).
    /// CREATESOUNDEXINFO.ignoresetfilesystem = 1 을 주면 이 Sound 하나만 그 콜백을 건너뛰고 OS 파일 I/O 로 읽는다.
    ///
    /// 곡 선택 화면에 들어가면 1회 재생하고, 나가면 Sound.release() 로 멈춘다.
    /// 1.0.4b 래퍼에는 Channel.stop 이 스트리핑돼 없지만, Sound 를 해제하면 그 소리를 틀던 채널도 멈춘다.
    /// 게임 프리뷰와 겹쳐서 들리는 게 정상이다.
    ///
    /// 덤으로 프리뷰 핸들(_Cg)과 _IF._ctA(에셋 ID → Sound 캐시로 추정)를 대조해 남긴다 (2단계 준비, 읽기 전용).
    /// </summary>
    internal static class FmodProbe
    {
        // 목적을 달성하면 false 로 끌 것 (docs/04 — 진단 로깅은 플래그로 남겨두기)
        private const bool Enabled = true;

        private const string FileName = "music.ogg";

        private static bool _broken;
        private static bool _visited;
        private static Sound _sound;
        private static IntPtr _lastPreviewPtr = IntPtr.Zero;

        public static void Tick(MelonLogger.Instance logger)
        {
            if (!Enabled || _broken) return;

            try
            {
                if (SceneRefs.IsSongSelectScene)
                {
                    if (!_visited)
                    {
                        _visited = true;   // 실패해도 이번 방문에서는 재시도하지 않는다
                        Play(logger);
                    }
                    ProbePreviewHandle(logger);
                }
                else if (_visited)
                {
                    _visited = false;
                    _lastPreviewPtr = IntPtr.Zero;
                    Stop(logger);
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                logger.Error($"[FmodProbe] 예외 — 프로브 중단: {ex}");
            }
        }

        private static void Play(MelonLogger.Instance logger)
        {
            string path = Path.Combine(HwaPaths.HwaDirectory ?? string.Empty, FileName);
            if (!File.Exists(path))
            {
                logger.Warning($"[FmodProbe] 파일 없음: {path}");
                return;
            }

            var system = _IF._ZSA;
            logger.Msg("──────────────────────────────────────────────────────────────────────────");
            logger.Msg($"[FmodProbe] 게임 FMOD System 핸들: 0x{system.handle.ToInt64():X}");
            if (!system.hasHandle())
            {
                logger.Error("[FmodProbe] _IF._ZSA 가 비어 있음 — FMOD 초기화 전이거나 이름이 바뀌었다");
                return;
            }

            var exinfo = new CREATESOUNDEXINFO
            {
                cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(),
                ignoresetfilesystem = 1,
            };
            var mode = MODE.LOOP_OFF | MODE._2D | MODE.CREATESTREAM | MODE.ACCURATETIME;

            var result = system.createSound(path, mode, ref exinfo, out _sound);
            logger.Msg($"[FmodProbe] createSound(ignoresetfilesystem=1, cbsize={exinfo.cbsize}) → {result}");
            logger.Msg($"  ├ 파일: {path}");
            logger.Msg($"  ├ Sound 핸들: 0x{_sound.handle.ToInt64():X}");
            if (result != RESULT.OK) return;

            if (_sound.getLength(out uint lengthMs, TIMEUNIT.MS) == RESULT.OK)
            {
                logger.Msg($"  ├ 길이: {lengthMs / 1000.0:F3}초");
            }

            result = system.getMasterChannelGroup(out ChannelGroup master);
            if (result != RESULT.OK)
            {
                logger.Error($"  └ getMasterChannelGroup 실패: {result}");
                return;
            }

            result = system.playSound(_sound, master, false, out Channel channel);
            logger.Msg($"  └ playSound(마스터 그룹) → {result}, Channel 핸들: 0x{channel.handle.ToInt64():X}");
            logger.Msg("──────────────────────────────────────────────────────────────────────────");
        }

        private static void Stop(MelonLogger.Instance logger)
        {
            if (_sound.handle == IntPtr.Zero) return;

            var result = _sound.release();
            logger.Msg($"[FmodProbe] 곡 선택 화면 이탈 — Sound.release() → {result}");
            _sound = default;
        }

        /// <summary>
        /// 프리뷰 로드가 끝난 핸들(_A 가 채워진 _Cg)마다 1회, _IF._ctA 와 대조해 로그로 남긴다.
        /// FMOD 함수에는 _ctA 에서 꺼낸 Sound 만 넘긴다 — _Cg._A 는 포인터가 아니라서 넘기면 위험하다.
        /// </summary>
        private static void ProbePreviewHandle(MelonLogger.Instance logger)
        {
            var handle = SceneRefs.SongSelect._vr;
            if (handle == null || handle.Pointer == _lastPreviewPtr) return;

            IntPtr a = handle._A;
            if (a == IntPtr.Zero) return;   // 아직 로드 전 (로드에 실패한 핸들은 계속 여기서 걸린다)
            _lastPreviewPtr = handle.Pointer;

            int assetId = handle._SUA;
            logger.Msg("──────────────────────────────────────────────────────────────────────────");
            logger.Msg($"[FmodProbe][핸들 대조] 프리뷰: {BgmHook.ResolveClipName(handle._d)}");
            logger.Msg($"  ├ _Cg: AssetId(_SUA)={assetId}, _A=0x{a.ToInt64():X}, _b=0x{handle._b.ToInt64():X}, " +
                       $"_B=0x{handle._B.ToInt64():X}, _c={handle._c}, _D={handle._D}");

            var cache = _IF._ctA;
            if (cache == null)
            {
                logger.Msg("  └ _IF._ctA: null");
                return;
            }

            try
            {
                logger.Msg($"  ├ _IF._ctA 개수: {cache.Count}");
            }
            catch (Exception ex)
            {
                logger.Warning($"  ├ _IF._ctA.Count 실패: {ex.Message}");
            }

            LogCacheEntry(logger, cache, "AssetId", assetId);
            LogCacheEntry(logger, cache, "_A 하위 32비트", unchecked((int)a.ToInt64()));
            logger.Msg("──────────────────────────────────────────────────────────────────────────");
        }

        private static void LogCacheEntry(MelonLogger.Instance logger,
                                          Il2CppSystem.Collections.Concurrent.ConcurrentDictionary<int, Sound> cache,
                                          string label, int key)
        {
            try
            {
                if (!cache.TryGetValue(key, out Sound sound))
                {
                    logger.Msg($"  ├ _ctA[{key}] ({label}) → 없음");
                    return;
                }

                string length = sound.getLength(out uint ms, TIMEUNIT.MS) == RESULT.OK
                    ? $"{ms / 1000.0:F3}초"
                    : "getLength 실패";
                logger.Msg($"  ├ _ctA[{key}] ({label}) → Sound 0x{sound.handle.ToInt64():X}, 길이 {length}");
            }
            catch (Exception ex)
            {
                logger.Warning($"  ├ _ctA[{key}] ({label}) 조회 실패: {ex.Message}");
            }
        }
    }
}
