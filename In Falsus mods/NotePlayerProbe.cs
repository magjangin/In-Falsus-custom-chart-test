using System;
using System.Reflection;
using MelonLoader;
using Il2Cppifapp.Game;
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// LogicalNotePlayer 의 double 필드 _xe / _Xe / _a / _A 가 무엇인지 판별하는 프로브.
    ///
    /// 곡 진입 직후에는 넷 다 133499.571 로 같은 값이라 구분이 안 됐다. 곡이 흐르는 동안
    /// 주기적으로 찍어 오디오 재생 시각(_Dg._jiA)과 함께 변화량을 보면 갈린다.
    ///
    ///   값이 안 움직이면  → 곡/차트 종료 시각 상수.
    ///                      커스텀 차트로 노트를 치환할 때 이 값도 같이 갱신해야 한다
    ///                      (재생바 길이·종료 판정이 여기에 물려 있을 가능성이 높다)
    ///   재생 시각을 따라가면 → 단순 재생 위치라 건드릴 필요 없다
    ///
    /// _jiA 는 초 단위, _xe 계열은 ms 단위로 보이므로 단위 환산에 주의할 것.
    /// </summary>
    internal static class NotePlayerProbe
    {
        /// <summary>
        /// 기본 OFF. _xe 계열이 차트 종료 시각 상수라는 결론이 이미 나왔으므로 상시 돌릴 이유가 없다.
        /// 게다가 이 프로브의 로그가 인게임 프레임 드랍의 주범이었다(2026-09-10 프로파일러 실측).
        /// 다시 측정할 일이 생기면 true 로 켤 것.
        /// </summary>
        private static readonly bool Enabled = false;

        private const int SampleIntervalFrames = 60;   // 약 1초 간격
        private const int MaxSamples = 20;             // 로그 폭주 방지

        private static int _frames;
        private static int _samples;
        private static string _chartId;
        private static double _baseXe, _baseXeUpper, _baseA, _baseAUpper;

        public static void Tick(MelonLogger.Instance log)
        {
            if (!Enabled) return;

            try
            {
                var np = LogicalNotePlayer._Qe;
                string chart = np == null ? null : np._Ve;

                if (string.IsNullOrEmpty(chart))
                {
                    _chartId = null;   // 플레이 씬을 나가면 다음 곡에서 다시 추적
                    return;
                }

                if (chart != _chartId)
                {
                    _chartId = chart;
                    _samples = 0;
                    _frames = 0;
                    _baseXe = np._xe;
                    _baseXeUpper = np._Xe;
                    _baseA = np._a;
                    _baseAUpper = np._A;

                    log.Msg($"[Probe] '{chart}' 추적 시작 — 기준 _xe={_baseXe:F3} _Xe={_baseXeUpper:F3} " +
                            $"_a={_baseA:F3} _A={_baseAUpper:F3}");
                }

                if (_samples >= MaxSamples) return;
                if (++_frames % SampleIntervalFrames != 0) return;

                _samples++;

                double xe = np._xe, xeUpper = np._Xe, a = np._a, aUpper = np._A;

                log.Msg($"[Probe] #{_samples,-2} {ReadAudioClock()}  " +
                        $"_xe {xe:F3} (Δ{xe - _baseXe:+0.000;-0.000;0})  " +
                        $"_Xe {xeUpper:F3} (Δ{xeUpper - _baseXeUpper:+0.000;-0.000;0})  " +
                        $"_a {a:F3} (Δ{a - _baseA:+0.000;-0.000;0})  " +
                        $"_A {aUpper:F3} (Δ{aUpper - _baseAUpper:+0.000;-0.000;0})");

                if (_samples == MaxSamples)
                {
                    log.Msg($"[Probe] 샘플 {MaxSamples}개 도달 — '{chart}' 추적 종료 (다음 곡에서 재개)");
                }
            }
            catch (Exception ex)
            {
                _samples = MaxSamples;   // 같은 곡에서 예외 반복 방지
                log.Warning($"[Probe] 샘플링 실패: {ex.Message}");
            }
        }

        /// <summary>기준 시계로 오디오 플레이어(_Dg)의 재생 시각을 읽는다.</summary>
        private static string ReadAudioClock()
        {
            try
            {
                var player = GameScene._nk();
                if (player == null) return "t=?";

                return $"t={player._jiA():F3}s (_miA={player._miA():F3}, 재생중={player._GiA()})";
            }
            catch
            {
                return "t=?";
            }
        }

        private static bool _fieldsDumped;

        /// <summary>
        /// LogicalNotePlayer 의 단순 타입 프로퍼티(전부 IL2CPP 필드)를 세션당 1회 전수 출력한다.
        /// 난독화된 이름이 실제로 무엇을 담는지 이름이 아니라 값으로 판별하기 위한 진단용이다.
        /// (문자열/숫자/불리언/enum 만 읽는다 — 노트 리스트·튜플은 비용과 위험 때문에 제외)
        /// </summary>
        public static void DumpNotePlayerFields(MelonLogger.Instance logger)
        {
            if (_fieldsDumped) return;
            _fieldsDumped = true;

            try
            {
                var np = LogicalNotePlayer._Qe;
                if (np == null) return;

                logger.Msg("  ├ [진단] LogicalNotePlayer 단순 필드 전수 (난독화 이름 ↔ 실제 값 대조용, 세션 1회)");

                var owner = typeof(LogicalNotePlayer);
                var props = owner.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

                foreach (var prop in props)
                {
                    var t = prop.PropertyType;
                    if (!prop.CanRead) continue;
                    if (t != typeof(string) && !t.IsPrimitive && !t.IsEnum) continue;

                    bool isStatic = prop.GetGetMethod(nonPublic: true)?.IsStatic ?? false;

                    string value;
                    try { value = Convert.ToString(prop.GetValue(isStatic ? null : np)) ?? "null"; }
                    catch (Exception ex) { value = $"<읽기 실패: {ex.GetType().Name}>"; }

                    if (value.Length == 0) value = "(빈 문자열)";
                    logger.Msg($"  │    {(isStatic ? "static " : "       ")}{prop.Name,-5} ({t.Name}) = {value}");
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"  ! LogicalNotePlayer 진단 실패: {ex.Message}");
            }
        }
    }
}
