using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using MelonLoader;
using Il2CppFMOD;
using Il2Cpp_J;
using Il2Cppifapp.Game;
using Il2Cppifapp.Game.Scenes;
using Il2Cppifapp.Game.Scenes.Game.UI;

namespace InFalsusMods
{
    /// <summary>
    /// 커스텀 음원 B 경로 3단계 — 게임이 alamode 음원 대신 hwa/music.ogg 를 "자기 Sound" 로 알고 틀게 만든다.
    ///
    /// 1단계(FmodProbe)에서 게임 FMOD 로 평문 OGG 를 여는 데는 성공했지만, 그건 게임 옆에서 따로 튼 것이라
    /// 게임 시계(_miA)·노트와 무관하다. 게임 파이프라인 안으로 넣어야 채보와 싱크가 맞는다.
    ///
    /// 끼워 넣는 곳은 _IF._NGA(int assetId, _BF) — 에셋 ID 로 Sound 를 만드는 함수다. 대상 ID 면 우리 Sound 를
    /// 대신 돌려준다. 인자가 int·enum 뿐이라 docs/04 기준 안전한 시그니처다. 게임은 곡 음원을 로드할 때마다
    /// _NGA 를 부르고, 돌려받은 Sound 를 그대로 _Cg._B 에 넣어 프리뷰·플레이 씬 모두 재생한다(2026-09-23 확인).
    /// 쓰였는지는 _Cg._B 가 우리가 만든 핸들인지로 판정한다 (IsOurs).
    ///
    /// 함께 시험한 _IF._ctA[assetId] 선점은 효과가 없었다 — 게임은 캐시를 먼저 보지 않고 매번 _NGA 를 부르고,
    /// _ctA 는 곡을 바꾸면 항목이 빠지는 "지금 로드된 Sound" 목록에 가깝다.
    ///
    /// 곡 종료 시각과 재생바는 음원이 아니라 차트 길이 기준이라 그것도 음원 길이로 맞춘다 (SyncEndTime, Prefix_QCA).
    /// </summary>
    internal static class AudioInjector
    {
        // 목적을 달성하면 false 로 끌 것
        public static bool Enabled { get; set; } = true;

        // alamode.wav (sam/89523571a0aaeb247ac449748a0fd876). 곡 선택 프리뷰와 플레이 씬 모두 이 ID 를 썼다.
        // 새 곡 슬롯(NewSongInjector)을 쓰면 새 곡 전용으로 등록한 AssetId 로 바뀐다 — 원곡 음원은 건드리지 않는다.
        internal static int TargetAssetId { get; set; } = 5610;

        private const string FileName = "music.ogg";

        // _NGA 가 효과음 로드에도 불릴 수 있어서 로그 줄 수를 제한한다 (플레이 중 로그 한 줄이 수십 ms — docs/04)
        private const int NgaLogBudget = 30;

        private static MelonLogger.Instance _logger;
        private static readonly HashSet<long> _ourSounds = new();
        private static int _ngaLogged;
        private static bool _broken;
        private static long _lastReportedPlaySound;
        private static bool _playSoundIsOurs;
        private static uint _hwaLengthMs;
        private static int _endTimeWrites;

        public static void Init(HarmonyLib.Harmony harmony, MelonLogger.Instance logger)
        {
            _logger = logger;
            if (!Enabled) return;

            // PatchAll 에 섞으면 이 패치가 실패할 때 뒤따르는 다른 패치까지 같이 빠진다 — 따로 건다.
            try
            {
                var target = AccessTools.Method(typeof(_IF), nameof(_IF._NGA));
                var prefix = AccessTools.Method(typeof(AudioInjector), nameof(Prefix_NGA));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                logger.Msg($"[AudioInjector] _IF._NGA 후킹 완료 (대상 AssetId: {TargetAssetId} → hwa/{FileName})");
            }
            catch (Exception ex)
            {
                logger.Error($"[AudioInjector] _IF._NGA 후킹 실패 — 커스텀 음원 주입 불가: {ex}");
            }

            try
            {
                var target = AccessTools.Method(typeof(SongInfoContainer), nameof(SongInfoContainer._QCA));
                var prefix = AccessTools.Method(typeof(AudioInjector), nameof(Prefix_QCA));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                logger.Msg("[AudioInjector] SongInfoContainer._QCA 후킹 완료 (재생바 = 재생 위치 ÷ 음원 길이)");
            }
            catch (Exception ex)
            {
                logger.Error($"[AudioInjector] SongInfoContainer._QCA 후킹 실패 — 재생바는 차트 길이 기준으로 남음: {ex}");
            }
        }

        /// <summary>
        /// hwa 폴더의 음원으로 게임 FMOD System 위에 Sound 를 만든다.
        /// ignoresetfilesystem = 1 로 lowiro 의 복호화 파일 콜백을 건너뛴다 (docs/03 2장).
        /// </summary>
        internal static bool TryCreateHwaSound(MelonLogger.Instance logger, string reason, out Sound sound)
        {
            sound = default;

            string path = Path.Combine(HwaPaths.HwaDirectory ?? string.Empty, FileName);
            if (!File.Exists(path))
            {
                logger?.Warning($"[AudioInjector] 파일 없음: {path}");
                return false;
            }

            var system = _IF._ZSA;
            if (!system.hasHandle())
            {
                logger?.Error("[AudioInjector] _IF._ZSA 가 비어 있음 — FMOD 초기화 전이거나 이름이 바뀌었다");
                return false;
            }

            var exinfo = new CREATESOUNDEXINFO
            {
                cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(),
                ignoresetfilesystem = 1,
            };
            var mode = MODE.LOOP_OFF | MODE._2D | MODE.CREATESTREAM | MODE.ACCURATETIME;

            var result = system.createSound(path, mode, ref exinfo, out sound);
            if (result != RESULT.OK)
            {
                logger?.Error($"[AudioInjector] {reason}: createSound 실패 → {result} ({path})");
                sound = default;
                return false;
            }

            lock (_ourSounds)
            {
                _ourSounds.Add(sound.handle.ToInt64());
            }

            string length = "길이 미상";
            if (sound.getLength(out uint ms, TIMEUNIT.MS) == RESULT.OK)
            {
                _hwaLengthMs = ms;
                length = $"{ms / 1000.0:F3}초";
            }
            logger?.Msg($"[AudioInjector] {reason}: hwa/{FileName} → Sound 0x{sound.handle.ToInt64():X} ({length})");
            return true;
        }

        /// <summary>이 모드가 만든 Sound 핸들인지. 게임이 이미 해제했을 수 있으니 비교에만 쓰고 FMOD 에 넘기지 말 것.</summary>
        internal static bool IsOurs(IntPtr handle)
        {
            lock (_ourSounds)
            {
                return _ourSounds.Contains(handle.ToInt64());
            }
        }

        public static void Tick(MelonLogger.Instance logger)
        {
            if (!Enabled || _broken) return;

            try
            {
                if (!SceneRefs.IsGameScene)
                {
                    _playSoundIsOurs = false;
                    _lastReportedPlaySound = 0;
                    _endTimeWrites = 0;
                    return;
                }

                ReportPlaySceneSound(logger);
                if (_playSoundIsOurs) SyncEndTime(logger);
            }
            catch (Exception ex)
            {
                _broken = true;
                logger.Error($"[AudioInjector] 예외 — 주입 중단: {ex}");
            }
        }

        /// <summary>플레이 씬 BGM 핸들(_Cg._B)이 우리 Sound 인지 1회씩 보고한다.</summary>
        private static void ReportPlaySceneSound(MelonLogger.Instance logger)
        {
            var handle = BgmHook.ActivePlayBgmHandle;
            if (handle == null) return;

            long sound = handle._B.ToInt64();
            if (sound == 0 || sound == _lastReportedPlaySound) return;
            _lastReportedPlaySound = sound;

            _playSoundIsOurs = IsOurs(handle._B);
            string verdict = _playSoundIsOurs ? "모드 Sound — 주입 성공" : "원본";
            logger.Msg($"[AudioInjector][플레이 씬] AssetId={handle._SUA}, _B=0x{sound:X} → {verdict}");
        }

        /// <summary>
        /// 곡 종료(결과 화면)는 음원 길이(_Dg._jiA)가 아니라 LogicalNotePlayer._xe 계열(원래는 마지막 노트 끝, ms)을 따른다.
        /// 원곡도 음원 135.7초 / 차트 133.5초라 원래부터 차트 기준이다. 2026-09-23 실측: _xe 계열을 음원 길이로 바꾸면
        /// 결과 화면이 음원이 끝난 뒤로 밀린다 (결과 = _xe + 약 13~15초). 재생바는 이걸로 안 바뀌어서 따로 고친다 (Prefix_QCA).
        /// _xe·_Xe 는 필드, _a·_A 는 같은 값을 돌려주는 프로퍼티(getter 인라인, CallerCount 0)라 넷 다 맞춘다.
        /// 게임이 다시 계산해 덮어쓸 수 있어서 플레이 중에는 틱마다 확인하고 되돌린다.
        /// </summary>
        private static void SyncEndTime(MelonLogger.Instance logger)
        {
            if (_hwaLengthMs == 0) return;

            var np = LogicalNotePlayer._Qe;
            if (np == null) return;

            // 커스텀 채보가 음원보다 길면 마지막 노트까지는 끝내지 않는다
            double target = Math.Max(_hwaLengthMs, ChartInjector.LastInjectedEndMs);
            double xe = np._xe;
            if (xe == target && np._Xe == target && np._a == target && np._A == target) return;

            string before = $"_xe={xe:F3} _Xe={np._Xe:F3} _a={np._a:F3} _A={np._A:F3}";
            np._xe = target;
            np._Xe = target;
            np._a = target;
            np._A = target;

            _endTimeWrites++;
            if (_endTimeWrites <= 3)
            {
                logger.Msg($"[AudioInjector][종료 시각] 음원 길이 {target:F0}ms 로 맞춤 (#{_endTimeWrites}) — 이전: {before}");
                logger.Msg($"  └ 이후: _xe={np._xe:F3} _Xe={np._Xe:F3} _a={np._a:F3} _A={np._A:F3}");
            }
        }

        /// <summary>
        /// 재생바는 _xe 를 바꿔도 원래 차트 길이(133.5초) 기준 그대로였다 — 25% 마다 33.37초 (2026-09-23).
        /// 곡 시작 때 GameScene·LogicalNotePlayer·_T·_Dg 에서 원래 길이와 같은 숫자 필드를 값으로 찾아봤지만 0개였다.
        /// 그래서 게임이 넘기는 진행률 대신 "재생 위치(_miA, 초) ÷ 음원 길이" 를 직접 넣는다.
        /// 원래 공식도 0초 기준 "시각 ÷ 길이" 였다 (25% 간격 33.37초 = 133.5초 ÷ 4).
        /// _QCA 는 void(float) 라 docs/04 기준 안전하게 후킹된다 (JacketHook 도 postfix 로 쓰고 있다).
        /// </summary>
        private static void Prefix_QCA(ref float __0)
        {
            if (!_playSoundIsOurs || _hwaLengthMs == 0) return;

            try
            {
                var scene = SceneRefs.GameScene;
                var player = scene != null ? scene._gN ?? GameScene._nk() : null;
                if (player == null) return;

                double progress = player._miA() * 1000.0 / _hwaLengthMs;
                __0 = (float)Math.Clamp(progress, 0.0, 1.0);
            }
            catch
            {
                // 재생바 표시가 틀릴 뿐이라 게임 진행을 막지 않는다
            }
        }

        /// <summary>A 경로: 에셋 ID 로 Sound 를 만드는 _IF._NGA 를 대상 ID 에 한해 가로챈다.</summary>
        private static bool Prefix_NGA(int __0, _BF __1, ref Sound __result)
        {
            try
            {
                if (_ngaLogged < NgaLogBudget)
                {
                    _ngaLogged++;
                    _logger?.Msg($"[AudioInjector][_NGA 호출] AssetId={__0}, _BF={__1}, 스레드={Environment.CurrentManagedThreadId}");
                }

                if (__0 != TargetAssetId) return true;
                if (!TryCreateHwaSound(_logger, "A 경로(_NGA 대체)", out var sound)) return true;

                __result = sound;
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Error($"[AudioInjector][_NGA] 예외 — 원본 실행: {ex}");
                return true;
            }
        }
    }
}
