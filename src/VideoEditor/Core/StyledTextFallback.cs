using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 긴 글(<see cref="RenderGraph.MaxStyledTextLength"/> 넘음)의 drawtext 대체 경로가 그리지 못하는 꾸밈 — 화면에 알린다(#34).
    /// 나눠 그리지 않고 알리기로 한 까닭: 휘기·그라데이션·반사·상자의 세로 정렬은 글 덩어리 전체를 기준으로 그리므로 조각마다 따로 그리면
    /// 같은 스타일의 짧은 글과 모양이 달라진다. 그리고 그만큼 긴 글은 대개 그리개 범위(화면의 몇 배)보다 커서 한 장에 담으려면 해상도를 크게 낮춰야 한다.
    /// 미리 보기와 내보내기는 같은 대체 경로로 그리므로 서로는 같다.
    /// </summary>
    public static class StyledTextFallback
    {
        /// <summary>
        /// 글자 클립을 drawtext 대체 경로로 그리는지(그리개가 있을 때): 꾸민 글자이거나 글자 상자를 정했는데 글이 <see cref="RenderGraph.MaxStyledTextLength"/>자를 넘을 때.
        /// 꾸미지도 상자를 정하지도 않은 글자는 원래 drawtext로 그리므로 빠지는 것이 없다.
        /// </summary>
        public static bool IsLong(Clip clip)
        {
            return clip != null && clip.Kind == ClipKind.Text && (clip.Style != null || ClipLayout.HasTextBox(clip))
                   && (clip.Text ?? "").Length > RenderGraph.MaxStyledTextLength;
        }

        /// <summary>
        /// 대체 경로에서 빠지거나 다르게 그려지는 것(이 클립이 쓰는 것만, 화면에 보일 차례). 긴 글이 아니면 빈 목록.
        /// drawtext는 기본 글꼴 하나(맑은 고딕)로 왼쪽 맞춤·고정 행간으로 그리고, 채우기는 첫 색, 윤곽선은 이음 없이 테두리 안쪽을 글자로 덮고(채우기가 비치면 속에 테두리 색),
        /// 그림자는 흐리지 못한다. 글자 상자·광선·반사·휘기는 없다.
        /// </summary>
        public static List<string> Lost(Clip clip)
        {
            var lost = new List<string>();
            if (!IsLong(clip))
                return lost;
            if (ClipLayout.HasTextBox(clip))
                lost.Add("글자 상자");
            var style = clip.Style?.Clone();
            if (style == null)
                return lost;
            style.Normalize();
            if (!string.Equals(style.FontFamily, TextStyle.DefaultFont, StringComparison.OrdinalIgnoreCase))
                lost.Add("글꼴");
            if (style.Bold || style.Italic)
                lost.Add("굵게·기울임");
            if (style.LetterSpacing != 0)
                lost.Add("자간");
            if (Math.Abs(style.LineSpacing - 1.2) > 1e-9)
                lost.Add("행간");
            if (style.Align != TextAlign.Left)
                lost.Add("정렬");
            if ((style.Fill.Kind == FillKind.Linear || style.Fill.Kind == FillKind.Radial) && style.Fill.Colors.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                lost.Add("그라데이션");
            var outline = style.Outline.Enabled && style.Outline.Width > 0;
            // 채우기가 비치면(없음·반투명) 꾸민 경로는 속이 빈 글자, drawtext는 속에 테두리 색이 보인다
            if (outline && (style.Fill.Kind == FillKind.None || Alpha(style.Fill.Colors[0]) < 255))
                lost.Add("속 빈 글자");
            if (outline && style.Outline.Join != LineJoinKind.Round)
                lost.Add("윤곽선 이음");
            if (style.Shadow.Enabled && style.Shadow.Opacity > 0 && style.Shadow.Blur > 0)
                lost.Add("그림자 흐림");
            if (style.Glow.Enabled && style.Glow.Size > 0 && style.Glow.Opacity > 0)
                lost.Add("광선");
            if (style.Reflection.Enabled && style.Reflection.Opacity > 0)
                lost.Add("반사");
            if (style.Warp.Kind != WarpKind.None && style.Warp.Amount != 0)
                lost.Add("휘기");
            return lost;
        }

        /// <summary>속성 패널에 보일 안내(긴 글이 아니면 null). 빠지는 꾸밈이 없어도 긴 글이면 간단히 그린다는 것은 알린다.</summary>
        public static string Notice(Clip clip)
        {
            if (!IsLong(clip))
                return null;
            var lost = Lost(clip);
            var head = "글이 " + RenderGraph.MaxStyledTextLength.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "자를 넘어 간단한 방식으로 그립니다(미리 보기·내보내기 같음).";
            var tail = " 글을 " + RenderGraph.MaxStyledTextLength.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "자 이하로 나눠 글자 클립 여럿에 담으면 모두 그립니다.";
            return lost.Count == 0 ? head : head + " 그리지 못하는 것: " + string.Join(" · ", lost) + "." + tail;
        }

        /// <summary>정규화한 색(#RRGGBB·#AARRGGBB)의 알파(0~255).</summary>
        private static int Alpha(string color)
        {
            var normalized = TextStyle.NormalizeColor(color, "#FFFFFF");
            return normalized.Length == 9 ? Convert.ToInt32(normalized.Substring(1, 2), 16) : 255;
        }
    }
}
