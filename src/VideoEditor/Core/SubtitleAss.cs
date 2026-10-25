using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 자막 트랙 → ASS(libass로 입힘, 순수 함수). PlayRes = 프로젝트 크기라 미리 보기처럼 작게 그려도 libass가 같은 비율로 줄인다.
    /// 큐 시각은 그리는 구간 시작 기준이고, 프레임 경계에서 한 프레임 어긋나지 않게 반 프레임 앞으로 적는다
    /// (큐 [S, E) 프레임 → (S−½)/fps ~ (E−½)/fps — ASS는 1/100초 단위라 프레임 시각 바로 위에 두면 반올림으로 흔들린다).
    /// </summary>
    public static class SubtitleAss
    {
        // ASS 줄바꿈 이스케이프(\N·\n·\h)를 끊는 보이지 않는 문자(U+2060 WORD JOINER)
        private static readonly string Joiner = ((char)0x2060).ToString();

        /// <summary>기본 모양: 짧은 변의 6% 크기, 흰 글자 + 검은 윤곽선 + 옅은 그림자, 아래 가운데(아래 여백 = 높이의 8%, 좌우 = 폭의 6%).</summary>
        public static (int Size, double Outline, double Shadow, int MarginSide, int MarginBottom) DefaultLook(Project project)
        {
            var size = Math.Max(8, (int)Math.Round(Math.Min(project.Width, project.Height) * 0.06));
            return (size, Math.Max(1, Math.Round(size * 0.06, 1)), Math.Max(1, Math.Round(size * 0.03, 1)),
                (int)Math.Round(project.Width * 0.06), (int)Math.Round(project.Height * 0.08));
        }

        /// <summary>
        /// 시간 배율: ASS는 1/100초 단위라 반 프레임이 5ms보다 짧으면(100fps 이상) 경계가 흔들린다.
        /// 50fps가 넘으면 그래프가 ass 앞뒤에서 시간을 이 배율만큼 늘였다 줄이고(setpts), ASS 시각도 같은 배율로 적는다.
        /// </summary>
        public static int TimeScale(FrameRate rate)
        {
            return Math.Max(1, (int)Math.Ceiling(rate.Fps / 50));
        }

        /// <summary>트랙의 큐 가운데 [from, to) 프레임에 걸치는 것을 ASS 글로(시각은 <see cref="TimeScale"/>배). 그릴 큐가 없으면 null.</summary>
        public static string Build(Project project, Track track, long from, long to)
        {
            var rate = project.Rate;
            var scale = TimeScale(rate);
            var half = rate.ToSeconds(1) / 2;
            var events = new StringBuilder();
            foreach (var clip in track.Clips.Where(c => c.Kind == ClipKind.Text && c.End > from && c.Start < to).OrderBy(c => c.Start))
            {
                var text = Escape(clip.Text);
                if (text.Length == 0)
                    continue;
                var start = Math.Max(0, rate.ToSeconds(clip.Start - from) - half) * scale;
                var end = (rate.ToSeconds(clip.End - from) - half) * scale;
                if (track.WordHighlight && CanHighlight(clip))
                    text = Highlight(rate, clip, from, start / scale, scale);
                events.Append("Dialogue: 0,").Append(Time(start)).Append(',').Append(Time(end)).Append(",Default,,0,0,0,,").Append(text).Append('\n');
            }
            if (events.Length == 0)
                return null;
            var look = DefaultLook(project);
            var ass = new StringBuilder();
            ass.Append("[Script Info]\n");
            ass.Append("ScriptType: v4.00+\n");
            ass.Append("PlayResX: ").Append(project.Width.ToString(CultureInfo.InvariantCulture)).Append('\n');
            ass.Append("PlayResY: ").Append(project.Height.ToString(CultureInfo.InvariantCulture)).Append('\n');
            // 0 = 넘치면 줄을 고르게 나눈다
            ass.Append("WrapStyle: 0\n");
            ass.Append("ScaledBorderAndShadow: yes\n");
            ass.Append("YCbCr Matrix: None\n\n");
            ass.Append("[V4+ Styles]\n");
            ass.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
            ass.Append(string.Format(CultureInfo.InvariantCulture,
                "Style: Default,{0},{1},&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,{2},{3},2,{4},{4},{5},1\n\n",
                TextStyle.DefaultFont, look.Size, look.Outline, look.Shadow, look.MarginSide, look.MarginBottom));
            ass.Append("[Events]\n");
            ass.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
            ass.Append(events);
            return ass.ToString();
        }

        /// <summary>강조할 단어 색(ASS &amp;HBBGGRR — 노랑 #FFD700)과 원래 글자 색(흰색).</summary>
        public const string HighlightColour = "&H00D7FF&";
        private const string PlainColour = "&HFFFFFF&";

        /// <summary>단어 시각이 큐 글과 맞는지(글자·숫자가 같아야 — 큐 글을 고쳤으면 강조하지 않는다).</summary>
        public static bool CanHighlight(Clip clip)
        {
            if (clip.Words == null || clip.Words.Count == 0)
                return false;
            var words = string.Concat(clip.Words.Select(w => w.Text)).Where(char.IsLetterOrDigit);
            return words.SequenceEqual((clip.Text ?? "").Where(char.IsLetterOrDigit));
        }

        /// <summary>
        /// 말하는 단어만 노랗게: 단어마다 {\r}(앞 단어의 변화를 끊음)와 그 단어가 말해질 때 노랑, 다음 단어가 시작할 때 흰색으로 바뀌는 \t를 붙인다.
        /// \t 시각은 대사 시작부터의 밀리초(× 시간 배율)이고, 같은 시각 두 개(\t(0,0,…))는 대사 내내 서서히 바뀌는 뜻이라 1ms 폭으로 쓴다.
        /// 단어 시각도 큐처럼 반 프레임 앞으로 적는다.
        /// </summary>
        private static string Highlight(FrameRate rate, Clip clip, long from, double lineStart, int scale)
        {
            var half = rate.ToSeconds(1) / 2;
            long At(long offset) => (long)Math.Round(Math.Max(0, Math.Max(0, rate.ToSeconds(clip.Start + offset - from) - half) - lineStart) * 1000 * scale);
            var result = new StringBuilder();
            for (var i = 0; i < clip.Words.Count; i++)
            {
                var word = clip.Words[i];
                var on = At(word.Start);
                var off = i + 1 < clip.Words.Count ? Math.Max(on, At(clip.Words[i + 1].Start)) : Math.Max(on, At(word.End));
                result.Append("{\\r");
                // 대사 시작에 이미 말하는 단어는 처음부터 노랑(\t(0,1,…)은 0ms에서 아직 바뀌기 전이라 한 장 미리 보기에 흰색으로 나온다)
                if (off > 0 && on == 0)
                    result.AppendFormat(CultureInfo.InvariantCulture, "\\1c{0}\\t({1},{2},\\1c{3})", HighlightColour, off, off + 1, PlainColour);
                else if (off > 0)
                {
                    result.AppendFormat(CultureInfo.InvariantCulture, "\\t({0},{1},\\1c{2})\\t({3},{4},\\1c{5})",
                        on, on + 1, HighlightColour, off, off + 1, PlainColour);
                }
                result.Append('}').Append(Escape(word.Text));
                if (i + 1 < clip.Words.Count)
                    result.Append(' ');
            }
            return result.ToString();
        }

        /// <summary>초 → ASS 시각 "시:분:초.백분의일초".</summary>
        public static string Time(double seconds)
        {
            var centiseconds = (long)Math.Round(Math.Max(0, seconds) * 100);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}",
                centiseconds / 360000, centiseconds / 6000 % 60, centiseconds / 100 % 60, centiseconds % 100);
        }

        /// <summary>
        /// 큐 글 → ASS 대사: { }는 꾸밈 태그로 읽히지 않게 전각 괄호로, \N·\n·\h 앞의 역슬래시는 보이지 않는 문자로 끊고, 줄바꿈은 \N.
        /// 빈 줄은 빼고 2,000자까지.
        /// </summary>
        public static string Escape(string text)
        {
            // 제어 문자를 먼저 빼야 그 앞뒤 빈칸도 떼어진다
            var visible = new string((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray());
            var lines = visible.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
            var joined = SubtitleFile.Cut(string.Join("\n", lines), SubtitleFile.MaxTextLength);
            var result = new StringBuilder(joined.Length + 8);
            for (var i = 0; i < joined.Length; i++)
            {
                var ch = joined[i];
                if (ch == '{')
                    result.Append('｛');
                else if (ch == '}')
                    result.Append('｝');
                else if (ch == '\n')
                    result.Append("\\N");
                else if (ch == '\\' && i + 1 < joined.Length && "Nnh".IndexOf(joined[i + 1]) >= 0)
                    result.Append('\\').Append(Joiner);
                else
                    result.Append(ch);
            }
            return result.ToString();
        }
    }
}
