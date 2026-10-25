using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>꾸민 글자 효과 계산: 그라데이션 끝점(각도)과 정지점(이슈 #14).</summary>
    public class TextEffectsTests
    {
        private static (double, double, double, double) Rounded((double StartX, double StartY, double EndX, double EndY) p)
        {
            return (System.Math.Round(p.StartX, 6), System.Math.Round(p.StartY, 6), System.Math.Round(p.EndX, 6), System.Math.Round(p.EndY, 6));
        }

        [Fact]
        public void GradientPoints_FollowTheAngle_AndCoverTheBox()
        {
            // 0° 왼쪽 → 오른쪽, 90° 위 → 아래(시계 방향), 180° 오른쪽 → 왼쪽
            Assert.Equal((0.0, 0.5, 1.0, 0.5), Rounded(TextEffects.GradientPoints(0)));
            Assert.Equal((0.5, 0.0, 0.5, 1.0), Rounded(TextEffects.GradientPoints(90)));
            Assert.Equal((1.0, 0.5, 0.0, 0.5), Rounded(TextEffects.GradientPoints(180)));
            Assert.Equal((0.0, 0.5, 1.0, 0.5), Rounded(TextEffects.GradientPoints(360)));
            // 45°는 왼쪽 위 모서리 → 오른쪽 아래 모서리(상자를 덮게 늘림)
            Assert.Equal((0.0, 0.0, 1.0, 1.0), Rounded(TextEffects.GradientPoints(45)));
            Assert.Equal((1.0, 0.0, 0.0, 1.0), Rounded(TextEffects.GradientPoints(135)));
            // 잘못된 각도는 0°
            Assert.Equal((0.0, 0.5, 1.0, 0.5), Rounded(TextEffects.GradientPoints(double.NaN)));
        }

        [Fact]
        public void NoneFill_WithoutRasterizer_DrawsTransparentText()
        {
            // 그리개가 없어 drawtext로 그릴 때도 채우기 없음이면 칠하지 않는다(투명), 단색이면 첫 색
            var project = TestProjects.Empty();
            var text = TimelineEdits.CreateText("가나다", 0, 30);
            text.Style = new TextStyle();
            text.Style.Fill.Kind = FillKind.None;
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var options = new RenderOptions { From = 0, To = 30, Width = 640, Height = 360, Audio = false };
            Assert.Contains("fontcolor='black@0.0'", RenderGraph.Build(project, options).Filter);
            text.Style.Fill.Kind = FillKind.Solid;
            text.Style.Fill.Colors = new System.Collections.Generic.List<string> { "#FF0000" };
            Assert.Contains("fontcolor='0xFF0000'", RenderGraph.Build(project, options).Filter);
        }

        [Fact]
        public void GradientStops_SpreadEvenly()
        {
            Assert.Equal(new[] { 0.0 }, TextEffects.GradientStops(1));
            Assert.Equal(new[] { 0.0 }, TextEffects.GradientStops(0));
            Assert.Equal(new[] { 0.0, 1.0 }, TextEffects.GradientStops(2));
            Assert.Equal(new[] { 0.0, 0.333333, 0.666667, 1.0 }, TextEffects.GradientStops(4).Select(v => System.Math.Round(v, 6)));
        }
    }
}
