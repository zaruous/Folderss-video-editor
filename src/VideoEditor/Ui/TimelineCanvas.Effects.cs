using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 타임라인 클립의 동영상 효과 배지(#34): 효과가 든 동영상·사진 클립의 오른쪽 위(어긋남 배지가 있으면 그 왼쪽)에 "✦"(둘 이상이면 "✦3")를 보인다.
    /// 어두운 반투명 바탕에 흰 글이라 클립 색과 상관없이 읽힌다. 클립이 좁아 이름 자리를 너무 가리면 그리지 않는다.
    /// 화면 낭독 이름에는 "· 효과 스캔 라인, RGB 분할"을 붙인다(<see cref="VideoEffectLabels"/>).
    /// </summary>
    internal sealed partial class TimelineCanvas
    {
        private static readonly Brush EffectBadgeBrush = Frozen(Color.FromArgb(0xB4, 0x11, 0x18, 0x27));
        // 이번 그리기에서 배지를 그린 자리(시험용 — 클립 Id → 자리)
        private readonly Dictionary<string, Rect> _effectBadges = new Dictionary<string, Rect>();

        /// <summary>효과 배지를 그린다(클립 모양으로 자른 채 — DrawClip 안에서). right는 배지 오른쪽 끝이 올 곳.</summary>
        private void DrawEffectBadge(DrawingContext dc, Clip clip, Rect rect, double right)
        {
            var text = VideoEffectLabels.Badge(clip);
            if (text == null)
            {
                _effectBadges.Remove(clip.Id);
                return;
            }
            var label = Text(text, 10, Brushes.White, true);
            var box = new Rect(right - label.Width - 6, rect.Top + 2, label.Width + 6, label.Height + 2);
            // 이름 글자 자리(왼쪽 30px)까지 덮어야 하면 그리지 않는다
            if (box.Left < rect.Left + 30)
            {
                _effectBadges.Remove(clip.Id);
                return;
            }
            dc.DrawRoundedRectangle(EffectBadgeBrush, null, box, 3, 3);
            dc.DrawText(label, new Point(box.Left + 3, box.Top + 1));
            _effectBadges[clip.Id] = box;
        }

        /// <summary>클립의 효과 배지 자리(화면 픽셀 — 마지막으로 그렸을 때, 없으면 Rect.Empty) — 시험용.</summary>
        internal Rect EffectBadgeBounds(string clipId)
        {
            return _effectBadges.TryGetValue(clipId, out var box) ? box : Rect.Empty;
        }
    }
}
