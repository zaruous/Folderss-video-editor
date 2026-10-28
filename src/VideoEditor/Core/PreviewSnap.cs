using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 미리 보기에서 옮길 때 붙기(#4): 옮기는 클립의 보이는 영역을 감싼 상자(회전하면 돌린 사각형을 감싼 축 상자)의 왼쪽·가운데·오른쪽(위·가운데·아래)이
    /// 화면 가장자리·가운데, 그 재생 헤드에 보이는 다른 클립 상자의 가장자리·가운데 가까이(threshold 프로젝트 픽셀 안) 가면 그 선에 맞춘다 — 가로·세로 따로.
    /// 가장 가까운 선 하나에 붙고, 그 선(프로젝트 픽셀)을 안내선으로 돌려준다. Alt로 끄는 것은 부르는 쪽(미리 보기 틀)이 정한다(타임라인과 같은 규칙).
    /// </summary>
    public static class PreviewSnap
    {
        /// <summary>붙을 선: 세로선(x)·가로선(y), 프로젝트 픽셀.</summary>
        public sealed class Lines
        {
            public List<double> X { get; } = new List<double>();
            public List<double> Y { get; } = new List<double>();
        }

        /// <summary>회전한 사각형(가운데·크기·시계 방향 회전)을 감싼 축 상자의 반너비·반높이.</summary>
        public static (double HalfWidth, double HalfHeight) HalfExtents(double width, double height, double rotation)
        {
            var radians = (double.IsFinite(rotation) ? rotation : 0) * Math.PI / 180;
            var (cos, sin) = (Math.Abs(Math.Cos(radians)), Math.Abs(Math.Sin(radians)));
            var (w, h) = (Math.Max(0, width) / 2, Math.Max(0, height) / 2);
            return (w * cos + h * sin, w * sin + h * cos);
        }

        /// <summary>
        /// 붙을 선: 화면 가장자리·가운데, 그리고 frame에 보이는 다른 화면 클립(동영상 트랙, 숨김·자막 트랙 제외 — 잠긴 트랙은 보이므로 넣음)의 상자 가장자리·가운데.
        /// moving(옮기는 클립 — 여럿이면 모두)은 빼고, 클립 값은 그 시각의 값(키프레임 — <see cref="ClipKeyframes.Pose"/>)으로.
        /// </summary>
        public static Lines Targets(Project project, long frame, IEnumerable<string> moving, Func<Clip, (double Width, double Height)> textBox)
        {
            var lines = new Lines();
            lines.X.AddRange(new[] { 0, project.Width / 2.0, project.Width });
            lines.Y.AddRange(new[] { 0, project.Height / 2.0, project.Height });
            var skip = new HashSet<string>(moving ?? Enumerable.Empty<string>());
            foreach (var track in project.VideoTracks.Where(t => !t.Hidden && !t.Subtitles))
            {
                foreach (var clip in track.Clips.Where(c => c.IsVisual && c.Contains(frame) && !skip.Contains(c.Id)))
                {
                    var area = ClipLayout.Area(project, ClipKeyframes.Pose(clip, frame - clip.Start), textBox);
                    var (halfWidth, halfHeight) = HalfExtents(area.Width, area.Height, area.Rotation);
                    if (!double.IsFinite(area.CenterX) || !double.IsFinite(area.CenterY) || !double.IsFinite(halfWidth) || !double.IsFinite(halfHeight))
                        continue;
                    lines.X.AddRange(new[] { area.CenterX - halfWidth, area.CenterX, area.CenterX + halfWidth });
                    lines.Y.AddRange(new[] { area.CenterY - halfHeight, area.CenterY, area.CenterY + halfHeight });
                }
            }
            return lines;
        }

        /// <summary>
        /// 한 방향 붙기: 상자 가운데 center와 반폭 half의 세 자리(앞 가장자리·가운데·뒤 가장자리) 가운데 선에 가장 가까운 것이 threshold 안이면
        /// 그 선에 맞추도록 옮길 거리와 그 선. 없으면 (0, null).
        /// </summary>
        public static (double Shift, double? Line) Snap1(double center, double half, IReadOnlyList<double> lines, double threshold)
        {
            var best = double.MaxValue;
            double? line = null;
            var shift = 0.0;
            foreach (var target in lines)
            {
                foreach (var edge in new[] { center - half, center, center + half })
                {
                    var distance = Math.Abs(target - edge);
                    if (distance <= threshold && distance < best)
                    {
                        best = distance;
                        line = target;
                        shift = target - edge;
                    }
                }
            }
            return (shift, line);
        }

        /// <summary>
        /// 옮길 자리(X·Y — 화면 가운데에서, 프로젝트 픽셀)를 붙인다. width·height·rotation은 옮기는 클립의 보이는 영역(회전 전 크기·회전).
        /// 돌려주는 안내선은 붙은 선(없으면 null).
        /// </summary>
        public static (double X, double Y, double? GuideX, double? GuideY) Move(Project project, double x, double y, double width, double height, double rotation,
            Lines lines, double threshold)
        {
            var (halfWidth, halfHeight) = HalfExtents(width, height, rotation);
            var (dx, guideX) = Snap1(project.Width / 2.0 + x, halfWidth, lines.X, threshold);
            var (dy, guideY) = Snap1(project.Height / 2.0 + y, halfHeight, lines.Y, threshold);
            return (x + dx, y + dy, guideX, guideY);
        }
    }
}
