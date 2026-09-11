using System;
using System.Collections.Generic;
using MelonLoader;
using Il2Cppifapp.Game;
using Il2Cpp_b;

namespace InFalsusMods
{
    /// <summary>
    /// 플레이 씬에 들어가면 LogicalNotePlayer가 들고 있는 노트의 시간과 타입을 깔끔하게 로그로 출력하는 덤퍼 클래스입니다.
    /// </summary>
    internal static class NoteListDumper
    {
        private static bool _dumpedThisSong;

        public static void Tick(MelonLogger.Instance log)
        {
            var player = FindPlayer();
            if (player == null)
            {
                // 플레이 씬을 나갔으면 다음 곡에서 다시 출력하도록 초기화
                _dumpedThisSong = false;
                return;
            }

            if (_dumpedThisSong) return;

            try
            {
                if (TryDump(player, log))
                {
                    _dumpedThisSong = true;
                }
            }
            catch (Exception ex)
            {
                _dumpedThisSong = true; // 같은 곡에서 예외 반복 로그 방지
                log.Error($"노트 출력 실패: {ex}");
            }
        }

        /// <summary>
        /// LogicalNotePlayer는 static 싱글턴(_Qe)이다. FindObjectOfType을 쓸 이유가 없다.
        /// 차트가 아직 안 실린 인스턴스는 _Ve가 비어 있으므로 그것으로 걸러낸다.
        /// </summary>
        private static LogicalNotePlayer FindPlayer()
        {
            try
            {
                var player = LogicalNotePlayer._Qe;
                return (player != null && !string.IsNullOrEmpty(player._Ve)) ? player : null;
            }
            catch { return null; }
        }

        private static bool TryDump(LogicalNotePlayer player, MelonLogger.Instance log)
        {
            var chart = player._Ue;
            var notes = chart.Item2;

            if (notes == null || notes.Count == 0) return false; // 아직 로드 중

            var rows = new List<string[]>(notes.Count);
            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                rows.Add(new[]
                {
                    i.ToString(),
                    note._ZD.ToString(),
                    note._ae.ToString(),
                    GetFriendlySide(note._Ae),
                    GetFriendlyNoteType(note._be),
                    note._Be.ToString(),
                    note._ce.ToString(),
                    FormatCoord(note._Ce),
                    FormatCoord(note._de),
                    FormatCoord(note._De),
                    FormatCoord(note._ee),
                });
            }

            log.Msg("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
            log.Msg($"차트 노트 목록 (차트: '{SafeStr(player._Ve)}' / 총 {notes.Count}개 노트)");
            log.Msg("──────────────────────────────────────────────────────────────────────────────────────────────────────────");

            var headers = new[]
            {
                "#", "id", "groupId", "side", "type",
                "judgeStart", "judgeEnd",
                "startX", "endX", "startWidth", "endWidth"
            };
            TablePrinter.Print(log, headers, rows);

            log.Msg("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
            return true;
        }

        internal static string GetFriendlySide(_CA side)
        {
            return side switch
            {
                _CA._rD => "Bottom",
                _CA._sD => "Bottom",
                _CA._SD => "Sky",
                _CA._QD => "None",
                _ => side.ToString()
            };
        }

        internal static string GetFriendlyNoteType(_HA type)
        {
            return type switch
            {
                _HA._aE => "Tap",
                _HA._AE => "Hold",
                _HA._bE => "Drag",
                _HA._BE => "Flick",
                _HA._cE => "Sky",
                _ => type.ToString()
            };
        }

        private static string FormatCoord(_BA ba)
        {
            if (ba._lD == 0) return "0";
            return $"{ba._KD}/{ba._lD} ({ba._LD:0.###})";
        }

        private static string SafeStr(string s)
        {
            try { return s ?? ""; } catch { return ""; }
        }
    }
}
