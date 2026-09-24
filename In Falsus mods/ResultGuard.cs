using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using MelonLoader.NativeUtils;
using Il2Cppifapp.Game.Data;

namespace InFalsusMods
{
    /// <summary>
    /// 새 곡(커스텀 채보) 점수가 세이브에 기록되지 않게 막는다.
    ///
    /// 점수 기록은 `GameResultsV4.TryUpdate(in SongInfo, in GameResultV4, out long existingScore) : UpdateResult` 하나이고,
    /// 게임에서 부르는 곳은 ResultsScene.Start 한 곳이다(CallerCount 1, 네이티브 호출부 검색으로 확인 2026-09-24).
    /// 인자가 byref 구조체라 Harmony 로는 후킹 못 한다(docs/04 1장) — 네이티브 훅으로 포인터만 받는다.
    ///
    /// 네이티브 규약: UpdateResult(int) TryUpdate(GameResultsV4* this, SongInfo* songInfo, GameResultV4* newResult, long* existingScore, MethodInfo*)
    /// SongInfo 의 첫 필드가 SongId(ushort) 라 *(ushort*)songInfo 가 곡 번호다.
    /// 막을 때는 원본을 부르지 않고 NoChange 를 돌려준다 — 최고 기록·플레이 기록(history) 모두 안 바뀐다.
    /// </summary>
    internal static unsafe class ResultGuard
    {
        public static bool Enabled { get; set; } = true;

        private const int UpdateResultNoChange = 2;   // Updated=0, New=1, NoChange=2

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TryUpdateFn(IntPtr self, IntPtr songInfo, IntPtr newResult, IntPtr existingScore, IntPtr methodInfo);

        private static readonly TryUpdateFn _detour = Detour;   // 함수 포인터로 넘기는 동안 살려 둔다
        private static NativeHook<TryUpdateFn> _hook;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int> _original;
        private static MelonLogger.Instance _logger;
        private static readonly HashSet<ushort> _blocked = new();

        public static void BlockSong(ushort songId)
        {
            lock (_blocked) _blocked.Add(songId);
        }

        public static void Init(MelonLogger.Instance logger)
        {
            _logger = logger;
            if (!Enabled) return;

            try
            {
                FieldInfo field = null;
                foreach (var f in typeof(GameResultsV4).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (f.Name.StartsWith("NativeMethodInfoPtr_TryUpdate_", StringComparison.Ordinal)) { field = f; break; }
                }
                if (field == null) throw new MissingFieldException(nameof(GameResultsV4), "NativeMethodInfoPtr_TryUpdate_*");

                var methodInfo = (IntPtr)field.GetValue(null);
                var target = *(IntPtr*)methodInfo;

                _hook = new NativeHook<TryUpdateFn>(target, Marshal.GetFunctionPointerForDelegate(_detour));
                _hook.Attach();
                _original = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)_hook.TrampolineHandle;

                logger.Msg("[ResultGuard] GameResultsV4.TryUpdate 네이티브 훅 완료 — 등록된 새 곡은 점수를 기록하지 않음");
            }
            catch (Exception ex)
            {
                Enabled = false;
                logger.Error($"[ResultGuard] 훅 실패 — ⚠ 새 곡 점수가 세이브에 기록될 수 있음: {ex}");
            }
        }

        private static int Detour(IntPtr self, IntPtr songInfo, IntPtr newResult, IntPtr existingScore, IntPtr methodInfo)
        {
            try
            {
                if (songInfo != IntPtr.Zero)
                {
                    ushort id = *(ushort*)songInfo;
                    bool block;
                    lock (_blocked) block = _blocked.Contains(id);
                    if (block)
                    {
                        if (existingScore != IntPtr.Zero) *(long*)existingScore = 0;
                        _logger?.Msg($"[ResultGuard] SongId {id} 결과 기록 차단 — 세이브의 최고 기록·플레이 기록을 건드리지 않음");
                        return UpdateResultNoChange;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"[ResultGuard] 판별 실패 — 원본 실행: {ex.Message}");
            }
            return _original(self, songInfo, newResult, existingScore, methodInfo);
        }
    }
}
