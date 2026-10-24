using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEditor.Core
{
    /// <summary>자막 큐 하나: [Start, End) 초와 글(줄바꿈은 \n).</summary>
    public sealed class SubtitleCue
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// 자막 파일(SRT·WebVTT) 읽기(순수 함수). 자막 파일은 남이 만든 것일 수 있으므로 크기·개수·길이를 묶고,
    /// 꾸밈 태그(&lt;i&gt;·{\an8} 등)는 빼고 글만 남긴다.
    /// </summary>
    public static class SubtitleFile
    {
        public const int MaxBytes = 10 * 1024 * 1024;
        public const int MaxCues = 20000;
        public const int MaxTextLength = 2000;
        public const double MaxSeconds = 24 * 3600;

        private static readonly string[] Extensions = { ".srt", ".vtt" };
        // "00:01:02,345 --> 00:01:04,000" (SRT) 또는 "01:02.345 --> 01:04.000 align:start" (VTT — 시는 없어도 되고 뒤에 설정이 붙는다).
        // 숫자는 [0-9]만 — \d는 전각·아랍 숫자에도 맞는데 int.Parse는 그것을 읽지 못한다
        private static readonly Regex TimeLine = new Regex(@"^\s*(?<s>(?:[0-9]{1,2}:)?[0-9]{1,2}:[0-9]{1,2}[,.][0-9]{1,3})\s*-->\s*(?<e>(?:[0-9]{1,2}:)?[0-9]{1,2}:[0-9]{1,2}[,.][0-9]{1,3})(?:\s.*)?$",
            RegexOptions.CultureInvariant);
        private static readonly Regex MarkupTag = new Regex(@"<[^<>]{0,200}>", RegexOptions.CultureInvariant);
        private static readonly Regex OverrideTag = new Regex(@"\{\\[^{}]{0,200}\}", RegexOptions.CultureInvariant);

        public static bool IsSubtitleFile(string path)
        {
            var extension = System.IO.Path.GetExtension(path ?? "");
            return Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>파일 바이트 → 글: BOM(UTF-8·UTF-16) → 엄격한 UTF-8 → 아니면 CP949(한국어 자막에 흔함).</summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            // BOM 없는 UTF-16: 자막은 숫자(ASCII)로 시작하므로 짝 자리마다 0이 끼어 있다
            if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0)
                return Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[2] == 0 && bytes[1] != 0)
                return Encoding.BigEndianUnicode.GetString(bytes);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(949).GetString(bytes);
            }
        }

        /// <summary>
        /// SRT·WebVTT 글 → 큐(시작 시각 순서는 파일 그대로). 시각 줄 다음 빈 줄까지가 한 큐의 글이다.
        /// 큐 번호·WEBVTT 머리·NOTE·STYLE 블록은 시각 줄이 없어 저절로 건너뛴다. 시각이 잘못되거나 글이 비면 버린다.
        /// </summary>
        public static List<SubtitleCue> Parse(string content)
        {
            var cues = new List<SubtitleCue>();
            var lines = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length && cues.Count < MaxCues; i++)
            {
                var times = TimeLine.Match(lines[i]);
                if (!times.Success)
                    continue;
                var text = new List<string>();
                var j = i + 1;
                for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
                {
                    if (TimeLine.IsMatch(lines[j]))
                    {
                        // 빈 줄 없이 다음 큐가 붙었다 — 바로 앞 줄이 큐 번호면 글이 아니다
                        if (text.Count > 0 && text[text.Count - 1].Trim().All(char.IsDigit))
                            text.RemoveAt(text.Count - 1);
                        break;
                    }
                    text.Add(lines[j]);
                }
                i = j - 1;
                double start, end;
                if (!TrySeconds(times.Groups["s"].Value, out start) || !TrySeconds(times.Groups["e"].Value, out end) || end <= start || start >= MaxSeconds)
                    continue;
                var clean = Clean(string.Join("\n", text));
                if (clean.Length > 0)
                    cues.Add(new SubtitleCue { Start = start, End = Math.Min(end, MaxSeconds), Text = clean });
            }
            return cues;
        }

        /// <summary>꾸밈 태그·엔티티·제어 문자를 정리하고 줄마다 앞뒤 빈칸을 뗀다(빈 줄은 뺌). 2,000자까지.</summary>
        public static string Clean(string text)
        {
            // ASS에서 바꾼 SRT에 흔한 \N·\n(줄바꿈 표시)을 진짜 줄바꿈으로
            text = (text ?? "").Replace("\\N", "\n").Replace("\\n", "\n");
            text = OverrideTag.Replace(text, "");
            text = MarkupTag.Replace(text, "");
            text = WebUtility.HtmlDecode(text);
            var builder = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (ch == '\n' || !char.IsControl(ch))
                    builder.Append(ch);
            }
            var lines = builder.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            return Cut(string.Join("\n", lines), MaxTextLength);
        }

        /// <summary>length자까지 자른다(대리 쌍을 반으로 가르지 않게).</summary>
        public static string Cut(string text, int length)
        {
            if (text.Length <= length)
                return text;
            return text.Substring(0, char.IsHighSurrogate(text[length - 1]) ? length - 1 : length);
        }

        /// <summary>"[시:]분:초,밀리초"(밀리초 자리는 1~3, 쉼표·점) → 초. 분·초는 60 미만.</summary>
        private static bool TrySeconds(string value, out double seconds)
        {
            seconds = 0;
            var parts = value.Replace(',', '.').Split(':');
            if (parts.Length < 2 || parts.Length > 3)
                return false;
            var hours = 0;
            if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                return false;
            if (!int.TryParse(parts[parts.Length - 2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                || !double.TryParse(parts[parts.Length - 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rest)
                || minutes >= 60 || rest >= 60)
                return false;
            seconds = hours * 3600 + minutes * 60 + rest;
            return true;
        }
    }
}
