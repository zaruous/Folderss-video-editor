using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>꾸민 글자 효과 계산: 그라데이션 끝점(각도)과 정지점(이슈 #14), 윤곽선 여백과 drawtext 테두리(이슈 #15), 그림자 거리·흐림과 drawtext 그림자(이슈 #16).</summary>
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
        public void ShadowOffset_ZeroIsRight_NinetyIsDown_Clockwise()
        {
            var right = TextEffects.ShadowOffset(10, 0);
            Assert.Equal(10, right.X, 6);
            Assert.Equal(0, right.Y, 6);
            var down = TextEffects.ShadowOffset(10, 90);
            Assert.Equal(0, down.X, 6);
            Assert.Equal(10, down.Y, 6);
            // 225° = 왼쪽 위
            var upLeft = TextEffects.ShadowOffset(10, 225);
            Assert.Equal(-7.07, upLeft.X, 2);
            Assert.Equal(-7.07, upLeft.Y, 2);
            // 이상한 거리는 0, 이상한 각도는 0°
            Assert.Equal((0.0, 0.0), TextEffects.ShadowOffset(double.NaN, 45));
            Assert.Equal((0.0, 0.0), TextEffects.ShadowOffset(-5, 45));
            Assert.Equal((0.0, 0.0), TextEffects.ShadowOffset(double.PositiveInfinity, 45));
            Assert.Equal(10, TextEffects.ShadowOffset(10, double.NaN).X, 6);
            // 스타일의 거리 × 효과 배율도 같은 계산
            var scaled = StyledTextLayout.ShadowOffset(new TextShadow { Distance = 10, Angle = 225 }, 2);
            Assert.Equal(-14.14, scaled.X, 2);
            Assert.Equal(-14.14, scaled.Y, 2);
        }

        [Fact]
        public void BlurRadius_ScalesAndStopsAtMax()
        {
            Assert.Equal(6, TextEffects.BlurRadius(6, 1), 6);
            Assert.Equal(3, TextEffects.BlurRadius(6, 0.5), 6);
            // 효과 배율을 곱한 뒤 상한 100(4K로 내보낼 때 흐림이 오래 걸리지 않게)
            Assert.Equal(TextEffects.MaxBlurRadius, TextEffects.BlurRadius(100, 2), 6);
            Assert.Equal(100, TextEffects.BlurRadius(80, 1.5), 6);
            Assert.Equal(0, TextEffects.BlurRadius(double.NaN, 1));
            Assert.Equal(0, TextEffects.BlurRadius(-4, 1));
            Assert.Equal(0, TextEffects.BlurRadius(6, double.PositiveInfinity));
            Assert.Equal(TextEffects.MaxBlurRadius, TextEffects.BlurRadius(double.MaxValue, double.MaxValue), 6);
        }

        [Fact]
        public void MaxShadow_LeavesRoomForOutlineDistanceAndBlur()
        {
            // 가장 굵은 윤곽선(각지게 50 → 100px) + 가장 먼 그림자(200, 45°) + 가장 큰 흐림(100): 한쪽으로 100 + 141.42 + 100
            var style = new TextStyle();
            style.Outline.Enabled = true;
            style.Outline.Width = 50;
            style.Outline.Join = LineJoinKind.Miter;
            style.Shadow.Enabled = true;
            style.Shadow.Distance = 200;
            style.Shadow.Angle = 45;
            style.Shadow.Blur = 100;
            var spread = StyledTextLayout.Spread(style, 1, 80);
            Assert.Equal(100 + 200 / System.Math.Sqrt(2) + 100, spread.Horizontal, 6);
            Assert.Equal(spread.Horizontal, spread.Vertical, 6);
            // 효과 배율 2: 윤곽선·거리는 두 배, 흐림은 상한 100(그리개와 같은 반지름)
            Assert.Equal(200 + 400 / System.Math.Sqrt(2) + 100, StyledTextLayout.Spread(style, 2, 160).Horizontal, 6);
            var layout = StyledTextLayout.Fit(300, 80, style, 1, 4000, 4000);
            Assert.True(layout.OriginX >= spread.Horizontal && layout.OriginY >= spread.Vertical, "여백 " + layout.OriginX + "×" + layout.OriginY);
        }

        [Fact]
        public void Shadow_WithoutRasterizer_DrawsDrawtextShadow()
        {
            // 그리개가 없어 drawtext로 그릴 때도 그림자(거리 × 출력 배율을 각도대로, 색 알파 × 불투명도 — 흐림은 할 수 없음), 끄면 없음
            var project = TestProjects.Empty();
            var text = TimelineEdits.CreateText("가나다", 0, 30);
            text.Style = new TextStyle();
            text.Style.Shadow.Enabled = true;
            text.Style.Shadow.Distance = 20;
            text.Style.Shadow.Angle = 0;
            text.Style.Shadow.Opacity = 0.5;
            text.Style.Shadow.Color = "#FF0000";
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var options = new RenderOptions { From = 0, To = 30, Width = project.Width / 2, Height = project.Height / 2, Audio = false };
            Assert.Contains(":shadowx=10:shadowy=0:shadowcolor='0xFF0000@0.5'", RenderGraph.Build(project, options).Filter);
            // 윤곽선과 함께면 테두리 뒤에
            text.Style.Outline.Enabled = true;
            Assert.Contains(":borderw=2:bordercolor='0x000000':shadowx=10", RenderGraph.Build(project, options).Filter);
            text.Style.Shadow.Enabled = false;
            Assert.DoesNotContain("shadowx", RenderGraph.Build(project, options).Filter);
            // 90° = 아래, 225° = 왼쪽 위, 색의 알파도 곱한다, 보이지 않거나 이상한 값은 그리지 않음
            Assert.Equal(":shadowx=0:shadowy=6:shadowcolor='0x000000@0.6'", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true, Angle = 90 }, 1));
            Assert.Equal(":shadowx=-7:shadowy=-7:shadowcolor='0x000000@0.6'", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true, Distance = 10, Angle = 225 }, 1));
            Assert.Equal(":shadowx=8:shadowy=0:shadowcolor='0x123456@0.25098'", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true, Distance = 4, Angle = 0, Color = "#80123456", Opacity = 0.5 }, 2));
            Assert.Equal("", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true, Opacity = 0 }, 1));
            Assert.Equal("", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true, Opacity = double.NaN }, 1));
            Assert.Equal("", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true }, double.NaN));
            Assert.Equal(":shadowx=0:shadowy=0:shadowcolor='0x000000@0.6'", RenderGraph.DrawtextShadow(new TextShadow { Enabled = true, Distance = double.NaN }, 1));
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
