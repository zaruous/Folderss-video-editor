using System;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>글자 상자의 옆(미리 보기 틀의 옆 손잡이). 순서는 위부터 시계 방향.</summary>
    public enum BoxEdge
    {
        Top,
        Right,
        Bottom,
        Left
    }

    /// <summary>
    /// 화면 클립이 화면(프로젝트 픽셀)의 어디에 얼마나 크게 보이는지 — 렌더 그래프와 같은 규칙(틀 = 화면 × 크기 배율, 맞춤 방식, 가운데 + X/Y, 시계 방향 회전).
    /// 미리 보기에서 클립을 직접 고르고 옮기고 크기를 바꾸는 틀에 쓴다.
    /// </summary>
    public static class ClipLayout
    {
        /// <summary>글자 상자 너비·높이의 상한(프로젝트 픽셀).</summary>
        public const double MaxTextBox = 16000;

        /// <summary>보이는 영역(회전 전): 가운데와 크기(프로젝트 픽셀), 회전(도). 미디어 크기를 모르면 틀 크기.</summary>
        public static (double CenterX, double CenterY, double Width, double Height, double Rotation) Content(Project project, Clip clip)
        {
            var scale = Math.Max(0.01, clip.Scale);
            var boxWidth = project.Width * scale;
            var boxHeight = project.Height * scale;
            var width = boxWidth;
            var height = boxHeight;
            var media = project.Media.FirstOrDefault(m => string.Equals(m.Path, clip.Path, StringComparison.OrdinalIgnoreCase));
            if (clip.Kind == ClipKind.Media && clip.Fit == ClipFit.Fit && media != null && media.Width > 0 && media.Height > 0)
            {
                var aspect = (double)media.Width / media.Height;
                if (aspect > boxWidth / boxHeight)
                    height = boxWidth / aspect;
                else
                    width = boxHeight * aspect;
            }
            return (project.Width / 2.0 + clip.X, project.Height / 2.0 + clip.Y, width, height, clip.Rotation);
        }

        /// <summary>가운데(0)에 가까우면(threshold 픽셀 안) 0으로 — 옮길 때 화면 가운데에 붙게.</summary>
        public static double Snap(double offset, double threshold)
        {
            return Math.Abs(offset) <= threshold ? 0 : offset;
        }

        // ---------- 글자 상자 ----------

        /// <summary>상자 너비·높이로 쓸 값: NaN·무한대·0 이하는 0(자동), 상한(<see cref="MaxTextBox"/>)까지.</summary>
        public static double TextBoxLength(double value)
        {
            return double.IsFinite(value) && value > 0 ? Math.Min(MaxTextBox, value) : 0;
        }

        /// <summary>글자 클립이 상자(너비나 높이)를 정했는지 — 줄 나눔·상자는 그리개로 그릴 때만 반영된다(<see cref="RenderGraph"/>).</summary>
        public static bool HasTextBox(Clip clip)
        {
            return clip != null && clip.Kind == ClipKind.Text && (clip.TextBoxWidth > 0 || clip.TextBoxHeight > 0);
        }

        /// <summary>
        /// 글자 상자의 자리(회전 전): 가운데 = 화면 가운데 + X/Y, 크기 = 상자(boxWidth×boxHeight, 프로젝트 픽셀 — 잰 값) × 크기 배율, 회전(도).
        /// 렌더 그래프는 상자를 가운데에 둔 캔버스 크기 그림을 크기 배율 → 회전 → 가운데 + X/Y로 놓으므로 그려진 상자와 같다.
        /// </summary>
        public static (double CenterX, double CenterY, double Width, double Height, double Rotation) TextBox(Project project, Clip clip, double boxWidth, double boxHeight)
        {
            var scale = Math.Max(0.01, clip.Scale);
            return (project.Width / 2.0 + clip.X, project.Height / 2.0 + clip.Y, Math.Max(0, boxWidth) * scale, Math.Max(0, boxHeight) * scale, clip.Rotation);
        }

        /// <summary>점(x, y)이 회전한 사각형(가운데·크기·시계 방향 회전) 안인지(가장자리 포함).</summary>
        public static bool Contains((double CenterX, double CenterY, double Width, double Height, double Rotation) rect, double x, double y)
        {
            // 점을 사각형 가운데 둘레로 거꾸로 돌려 축에 맞춘 사각형과 견준다
            var radians = -rect.Rotation * Math.PI / 180;
            var dx = x - rect.CenterX;
            var dy = y - rect.CenterY;
            var localX = dx * Math.Cos(radians) - dy * Math.Sin(radians);
            var localY = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            return Math.Abs(localX) <= rect.Width / 2 && Math.Abs(localY) <= rect.Height / 2;
        }

        /// <summary>상자의 가장 작은 크기(프로젝트 픽셀, 크기 배율 전): 너비 = 글자 크기(한 글자쯤), 높이 = 글자 크기 × 줄 간격(한 줄).</summary>
        public static (double Width, double Height) MinTextBox(Clip clip)
        {
            var size = double.IsFinite(clip.FontSize) && clip.FontSize > 0 ? clip.FontSize : 72;
            var spacing = clip.Style?.LineSpacing ?? 1.2;
            spacing = double.IsFinite(spacing) ? Math.Max(0.5, Math.Min(3, spacing)) : 1.2;
            return (size, size * spacing);
        }

        /// <summary>
        /// 상자의 옆 하나를 (dx, dy)(화면 방향, 프로젝트 픽셀)만큼 끈다 — 반대쪽 옆은 그대로 두고 그 방향 크기만 바꾼다.
        /// 끈 거리를 클립 자신의 축(회전한 가로·세로)에 투영해 크기 배율로 나눈 만큼 상자가 커지고, 가운데는 화면에서 커진 만큼의 절반을 같은 축으로 옮긴다.
        /// width·height는 끌기 시작 때의 상자 크기(자동이면 잰 크기), 가장 작은 크기는 min(시작 크기가 더 작으면 시작 크기, 적어도 1)이고 상한은 <see cref="MaxTextBox"/>.
        /// 돌려주는 너비·높이 가운데 끌지 않은 쪽은 받은 값 그대로.
        /// </summary>
        public static (double X, double Y, double Width, double Height) DragEdge(BoxEdge edge, double x, double y, double width, double height,
            double scale, double rotation, double dx, double dy, double minWidth, double minHeight)
        {
            var radians = (double.IsFinite(rotation) ? rotation : 0) * Math.PI / 180;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var s = double.IsFinite(scale) ? Math.Max(0.01, scale) : 1;
            var horizontal = edge == BoxEdge.Left || edge == BoxEdge.Right;
            // 클립의 가로축(화면에서 (cos, sin))과 세로축((-sin, cos)) — 시계 방향 회전, y는 아래로
            var (axisX, axisY) = horizontal ? (cos, sin) : (-sin, cos);
            var along = (double.IsFinite(dx) ? dx : 0) * axisX + (double.IsFinite(dy) ? dy : 0) * axisY;
            // 오른쪽·아래 옆은 축 방향으로, 왼쪽·위 옆은 반대 방향으로 끌 때 커진다
            var sign = edge == BoxEdge.Right || edge == BoxEdge.Bottom ? 1 : -1;
            var start = Math.Max(0, horizontal ? width : height);
            var min = Math.Max(1, Math.Min(horizontal ? minWidth : minHeight, start));
            var size = Math.Max(min, Math.Min(MaxTextBox, start + sign * along / s));
            var shift = (size - start) * s / 2 * sign;
            return (x + shift * axisX, y + shift * axisY, horizontal ? size : width, horizontal ? height : size);
        }

        /// <summary>
        /// 재생 헤드 frame에서 점(x, y)(프로젝트 픽셀)에 보이는 맨 위 클립(없으면 null) — 미리 보기에서 눌러 고르기.
        /// 쌓는 순서는 렌더 그래프와 같고(동영상 트랙 위 번호가 위), 숨김·잠금·자막 트랙(libass가 그림)은 건너뛴다.
        /// 동영상·사진은 보이는 영역(<see cref="Content"/>), 글자는 상자(<see cref="TextBox"/> — textBox는 크기 배율 전 상자 크기를 잰다), 아트는 모양 상자로, 회전·크기 배율을 따른다.
        /// </summary>
        public static Clip HitTest(Project project, long frame, double x, double y, Func<Clip, (double Width, double Height)> textBox)
        {
            for (var i = project.VideoTracks.Count - 1; i >= 0; i--)
            {
                var track = project.VideoTracks[i];
                if (track.Hidden || track.Locked || track.Subtitles)
                    continue;
                // 같은 트랙은 늦게 시작한 것이 위
                foreach (var clip in track.Clips.Where(c => c.IsVisual && c.Contains(frame)).OrderByDescending(c => c.Start))
                {
                    // 키프레임이 있으면 그 시각의 자리·크기·회전으로
                    if (Contains(Area(project, ClipKeyframes.Pose(clip, frame - clip.Start), textBox), x, y))
                        return clip;
                }
            }
            return null;
        }

        /// <summary>
        /// 클립이 보이는 영역(회전 전 가운데·크기, 회전): 동영상·사진은 <see cref="Content"/>, 글자는 상자(<see cref="TextBox"/> — textBox가 크기 배율 전 상자 크기를 잰다),
        /// 아트는 모양 상자. 고르기(<see cref="HitTest"/>)와 붙기(<see cref="PreviewSnap"/>)가 같은 영역을 쓴다.
        /// </summary>
        public static (double CenterX, double CenterY, double Width, double Height, double Rotation) Area(Project project, Clip clip, Func<Clip, (double Width, double Height)> textBox)
        {
            if (clip.Kind == ClipKind.Art && clip.Art != null)
                return TextBox(project, clip, clip.Art.Width, clip.Art.Height);
            if (clip.Kind == ClipKind.Text)
            {
                var box = textBox(clip);
                return TextBox(project, clip, box.Width, box.Height);
            }
            return Content(project, clip);
        }
    }
}
