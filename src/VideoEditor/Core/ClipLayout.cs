using System;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 화면 클립이 화면(프로젝트 픽셀)의 어디에 얼마나 크게 보이는지 — 렌더 그래프와 같은 규칙(틀 = 화면 × 크기 배율, 맞춤 방식, 가운데 + X/Y, 시계 방향 회전).
    /// 미리 보기에서 클립을 직접 옮기고 크기를 바꾸는 틀에 쓴다.
    /// </summary>
    public static class ClipLayout
    {
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
    }
}
