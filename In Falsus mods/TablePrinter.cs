using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;

namespace InFalsusMods
{
    /// <summary>
    /// 콘솔 출력 시 한글/전각 문자의 너비를 보정하여 열 정렬이 어긋나지 않도록 표 형태로 출력하는 유틸리티입니다.
    /// </summary>
    internal static class TablePrinter
    {
        /// <summary>
        /// 2차원 문자열 리스트를 콘솔에 표 형태로 정렬하여 한 줄씩 출력합니다.
        /// </summary>
        public static void Print(MelonLogger.Instance log, string[] headers, List<string[]> rows)
        {
            if (headers == null || headers.Length == 0) return;

            var columnWidths = new int[headers.Length];

            for (int c = 0; c < headers.Length; c++)
            {
                columnWidths[c] = GetDisplayWidth(headers[c]);
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        if (c < row.Length)
                        {
                            int len = GetDisplayWidth(row[c]);
                            if (len > columnWidths[c])
                            {
                                columnWidths[c] = len;
                            }
                        }
                    }
                }
            }

            // 헤더 출력
            log.Msg(FormatRow(headers, columnWidths));

            // 데이터 행 출력
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    log.Msg(FormatRow(row, columnWidths));
                }
            }
        }

        /// <summary>
        /// 한글/영문 폭을 고려하여 여백을 맞춘 행 문자열을 생성합니다.
        /// </summary>
        public static string FormatRow(string[] cells, int[] widths)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Length && i < widths.Length; i++)
            {
                if (i > 0) sb.Append("    ");
                string cell = cells[i] ?? "";
                sb.Append(cell);

                int pad = widths[i] - GetDisplayWidth(cell);
                if (pad > 0)
                {
                    sb.Append(' ', pad);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 콘솔 출력 시 멀티바이트(한글/전각 문자) 너비를 계산하여 열 정렬이 어긋나지 않도록 합니다.
        /// </summary>
        public static int GetDisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int width = 0;
            foreach (char c in text)
            {
                width += (c >= 0x1100 && (c <= 0x11FF || c >= 0xAC00 && c <= 0xD7A3 || c >= 0x2E80 && c <= 0x9FFF)) ? 2 : 1;
            }
            return width;
        }
    }
}
