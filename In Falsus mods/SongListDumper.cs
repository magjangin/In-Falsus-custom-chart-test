using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Il2Cppifapp.Game;
using Il2Cppifapp.Game.Data;
using Il2Cppifapp.Game.Scenes;

namespace InFalsusMods
{
    /// <summary>
    /// 곡 선택 화면에 들어가면 곡 목록 전체를 로그로 출력한다.
    ///
    /// SongSelectScene.dataAccess -> SongData.allSongInfo 가 전부 난독화를 피해서
    /// 그대로 읽힌다. 차트 ID는 `&lt;곡슬러그&gt;&lt;난이도인덱스&gt;` 규칙이고, 이 값이
    /// StreamingAssetsMapping 의 `&lt;chartId&gt;.spc` 논리 경로와 그대로 연결된다.
    /// </summary>
    internal static class SongListDumper
    {
        private static bool _dumped;

        public static void Tick(MelonLogger.Instance log)
        {
            // 씬 탐색은 SceneRefs 가 디텍터 틱마다 한 번만 수행한다 (프레임 드랍 방지).
            var scene = SceneRefs.SongSelect;

            if (scene == null)
            {
                _dumped = false;   // 화면을 나가면 다음 진입 때 다시 출력
                return;
            }

            if (_dumped) return;

            try
            {
                if (Dump(scene, log)) _dumped = true;
            }
            catch (Exception ex)
            {
                _dumped = true;
                log.Error($"곡 목록 출력 실패: {ex}");
            }
        }

        private static bool Dump(SongSelectScene scene, MelonLogger.Instance log)
        {
            var dataAccess = scene.dataAccess;
            var allSongs = dataAccess?.SongData?.allSongInfo;
            if (allSongs == null || allSongs.Length == 0)
            {
                return false;
            }

            var rows = new List<string[]>();
            int totalDifficulties = 0;

            for (int i = 0; i < allSongs.Length; i++)
            {
                var song = allSongs[i];
                if (song == null) continue;

                string title = song.BaseName;
                if (string.IsNullOrWhiteSpace(title)) continue; // 더미 엔트리 건너뜀

                int diffCount = CountValidDifficulties(song);
                totalDifficulties += diffCount;

                rows.Add(new[] { title, diffCount.ToString() });
            }

            if (rows.Count == 0)
            {
                return false;
            }

            // 로그 출력
            log.Msg("══════════════════════════════════════════════════");
            log.Msg($"곡 목록 (총 {rows.Count}곡 / 난이도 합계: {totalDifficulties}개)");
            log.Msg("──────────────────────────────────────────────────");

            var headers = new[] { "곡 제목 (Title)", "난이도 수 (Difficulties)" };
            TablePrinter.Print(log, headers, rows);

            log.Msg("══════════════════════════════════════════════════");
            return true;
        }

        /// <summary>
        /// 더미 슬롯을 제외한 유효 난이도(차트) 개수를 계산합니다.
        /// </summary>
        private static int CountValidDifficulties(SongInfo song)
        {
            var chartInfos = song.ChartInfos;
            if (chartInfos == null || chartInfos.Length == 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < chartInfos.Length; i++)
            {
                var chart = chartInfos[i];
                if (chart == null) continue;

                // 유효 조건: 난이도 지정이 존재하고 차트 ID가 비어있지 않은 경우
                if (chart.Difficulty != ChartDifficultyFlag.None && !string.IsNullOrWhiteSpace(chart.Id))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
