using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>꾸민 글자 효과 계산: 그라데이션 끝점(각도)과 정지점(이슈 #14), 윤곽선 여백과 drawtext 테두리(이슈 #15).</summary>
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
        public void OutlineExtent_IsWidth_OrTwiceForMiter()
        {
            Assert.Equal(8, TextEffects.OutlineExtent(8, LineJoinKind.Round));
            Assert.Equal(8, TextEffects.OutlineExtent(8, LineJoinKind.Bevel));
            Assert.Equal(16, TextEffects.OutlineExtent(8, LineJoinKind.Miter));
            Assert.Equal(0, TextEffects.OutlineExtent(0, LineJoinKind.Miter));
            Assert.Equal(0, TextEffects.OutlineExtent(-3, LineJoinKind.Round));
            Assert.Equal(0, TextEffects.OutlineExtent(double.NaN, LineJoinKind.Miter));
        }

        [Fact]
        public void ThickMiterOutline_LeavesRoomInTheImage()
        {
            // 두께 50 + 각지게: 모서리 끝이 100px까지 나가므로 배치 상자 둘레에 100px 넘게 여백
            var style = new TextStyle();
            style.Outline.Enabled = true;
            style.Outline.Width = 50;
            style.Outline.Join = LineJoinKind.Miter;
            var layout = StyledTextLayout.Fit(300, 80, style, 1, 4000, 4000);
            Assert.True(layout.OriginX >= 100, "가로 여백 " + layout.OriginX);
            Assert.True(layout.OriginY >= 100, "세로 여백 " + layout.OriginY);
            Assert.True(layout.Width - layout.OriginX - 300 >= 100);
            Assert.True(layout.Height - layout.OriginY - 80 >= 100);
            // 효과 배율도 곱한다
            Assert.True(StyledTextLayout.Fit(300, 80, style, 2, 4000, 4000).OriginX >= 200);
        }

        [Fact]
        public void Outline_WithoutRasterizer_DrawsDrawtextBorder()
        {
            // 그리개가 없어 drawtext로 그릴 때도 윤곽선을 테두리로(두께 × 출력 배율, 이음은 고를 수 없음), 끄면 없음
            var project = TestProjects.Empty();
            var text = TimelineEdits.CreateText("가나다", 0, 30);
            text.Style = new TextStyle();
            text.Style.Outline.Enabled = true;
            text.Style.Outline.Width = 6;
            text.Style.Outline.Color = "#FF0000";
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var options = new RenderOptions { From = 0, To = 30, Width = project.Width / 2, Height = project.Height / 2, Audio = false };
            Assert.Contains(":borderw=3:bordercolor='0xFF0000'", RenderGraph.Build(project, options).Filter);
            text.Style.Outline.Enabled = false;
            Assert.DoesNotContain("borderw", RenderGraph.Build(project, options).Filter);
            // 아주 얇아도 1px, 이상한 값은 그리지 않음
            Assert.Equal(":borderw=1:bordercolor='0x000000'", RenderGraph.DrawtextBorder(new TextOutline { Enabled = true, Width = 0.2 }, 1));
            Assert.Equal("", RenderGraph.DrawtextBorder(new TextOutline { Enabled = true, Width = double.NaN }, 1));
            Assert.Equal("", RenderGraph.DrawtextBorder(new TextOutline { Enabled = true, Width = 0 }, 1));
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
