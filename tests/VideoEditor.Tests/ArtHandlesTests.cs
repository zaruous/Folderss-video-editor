using System;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>#38 아트 요소 1단계 마무리: 모양 손잡이(모서리 반경·선 끝·별 안쪽) 계산, 도형 견본 스타일, 주 트랙에 놓은 요소의 자리.</summary>
    public class ArtHandlesTests
    {
        private static (Project Project, Clip Clip) Art(ArtKind kind, double x = 0, double y = 0, double scale = 1, double rotation = 0)
        {
            // 1920×1080 화면(가운데 960, 540)
            var project = TestProjects.Empty(magnetic: false);
            project.Width = 1920;
            project.Height = 1080;
            var clip = TimelineEdits.CreateArt(kind, 0, 30);
            clip.X = x;
            clip.Y = y;
            clip.Scale = scale;
            clip.Rotation = rotation;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static void Near((double X, double Y) expected, (double X, double Y) actual, double tolerance = 1e-6)
        {
            Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance, "기대 " + expected + " · 실제 " + actual);
        }

        /// <summary>끈 결과를 클립에 넣은 사본(같은 프로젝트 좌표로 손잡이 자리를 다시 재려고).</summary>
        private static Clip Apply(Clip clip, (double X, double Y, double Rotation, ArtShape Art) dragged)
        {
            var copy = clip.CloneWithNewId();
            copy.X = dragged.X;
            copy.Y = dragged.Y;
            copy.Rotation = dragged.Rotation;
            copy.Art = dragged.Art;
            return copy;
        }

        [Fact]
        public void Handles_ForEachKind()
        {
            Assert.Equal(new[] { ArtHandle.CornerRadius }, ArtHandles.For(ArtKind.Rectangle));
            Assert.Equal(new[] { ArtHandle.LineStart, ArtHandle.LineEnd }, ArtHandles.For(ArtKind.Line));
            Assert.Equal(new[] { ArtHandle.LineStart, ArtHandle.LineEnd }, ArtHandles.For(ArtKind.Arrow));
            Assert.Equal(new[] { ArtHandle.InnerRatio }, ArtHandles.For(ArtKind.Star));
            foreach (var kind in new[] { ArtKind.Ellipse, ArtKind.Triangle, ArtKind.Polygon, ArtKind.Heart })
                Assert.Empty(ArtHandles.For(kind));
        }

        [Fact]
        public void LocalAndProject_RoundTrip_FollowRotationAndScale()
        {
            // 1920×1080, 사각형 400×300을 X+100·Y−50, 크기 2, 90° 돌림: 상자 가운데 (1060, 490)
            var (project, clip) = Art(ArtKind.Rectangle, 100, -50, 2, 90);
            Near((1060, 490), ArtHandles.ToProject(project, clip, 200, 150));
            // 상자 오른쪽 가운데(400, 150)는 가운데에서 오른쪽 200 × 2 → 90° 돌려 아래로 400
            Near((1060, 890), ArtHandles.ToProject(project, clip, 400, 150));
            var local = ArtHandles.ToLocal(project, clip, 1234.5, 321.25);
            Near((1234.5, 321.25), ArtHandles.ToProject(project, clip, local.X, local.Y), 1e-9);
        }

        [Fact]
        public void LineEnd_Drag_KeepsTheOtherEnd_ChangesLengthAndRotation()
        {
            // 화살표 400×60, X+100·Y−50: 시작 (860, 490), 끝 (1260, 490)
            var (project, arrow) = Art(ArtKind.Arrow, 100, -50);
            var start = ArtHandles.Position(project, arrow, ArtHandle.LineStart);
            var end = ArtHandles.Position(project, arrow, ArtHandle.LineEnd);
            Near((860, 490), start);
            Near((1260, 490), end);
            // 끝을 (1160, 790)으로: 시작에서 (300, 300) — 길이 424.26, 45°
            var dragged = ArtHandles.Drag(project, arrow, ArtHandle.LineEnd, end, (1160, 790));
            Assert.Equal(Math.Sqrt(2) * 300, dragged.Art.Width, 6);
            Assert.Equal(45, dragged.Rotation, 6);
            var moved = Apply(arrow, dragged);
            Near(start, ArtHandles.Position(project, moved, ArtHandle.LineStart));
            Near((1160, 790), ArtHandles.Position(project, moved, ArtHandle.LineEnd));
            // 높이·두께·머리는 그대로(크기 배율도)
            Assert.Equal((60.0, 12.0, 3.0), (dragged.Art.Height, dragged.Art.Thickness, dragged.Art.HeadSize));
            // 원래 클립은 고치지 않는다
            Assert.Equal((400.0, 0.0), (arrow.Art.Width, arrow.Rotation));

            // 시작 끝을 끌면 끝이 그대로 — 누른 자리가 손잡이에서 조금 벗어나도 끈 만큼만
            var pressed = (start.X + 3, start.Y - 2);
            dragged = ArtHandles.Drag(project, arrow, ArtHandle.LineStart, pressed, (pressed.Item1 - 100, pressed.Item2 + 100));
            moved = Apply(arrow, dragged);
            Near(end, ArtHandles.Position(project, moved, ArtHandle.LineEnd));
            Near((760, 590), ArtHandles.Position(project, moved, ArtHandle.LineStart));
            Assert.Equal(Math.Sqrt(500 * 500 + 100 * 100), dragged.Art.Width, 6);
        }

        [Fact]
        public void LineEnd_Drag_WithScaleAndRotation_AndContinuousAngle()
        {
            // 크기 2, 350° 돌린 선: 끝을 조금 아래로 끌어도 회전은 350 근처(−10으로 튀지 않음)
            var (project, line) = Art(ArtKind.Line, -30, 40, 2, 350);
            var start = ArtHandles.Position(project, line, ArtHandle.LineStart);
            var end = ArtHandles.Position(project, line, ArtHandle.LineEnd);
            var dragged = ArtHandles.Drag(project, line, ArtHandle.LineEnd, end, (end.X, end.Y + 200));
            Assert.InRange(dragged.Rotation, 350, 370);
            var moved = Apply(line, dragged);
            Near(start, ArtHandles.Position(project, moved, ArtHandle.LineStart), 1e-6);
            Near((end.X, end.Y + 200), ArtHandles.Position(project, moved, ArtHandle.LineEnd), 1e-6);
            // 크기 배율은 그대로라 너비 = 화면 길이 / 2
            var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y + 200 - start.Y, 2));
            Assert.Equal(length / 2, dragged.Art.Width, 6);
            // 끝을 시작 끝 위로 겹치면 너비는 1(하한)이고 시작 끝은 그대로
            dragged = ArtHandles.Drag(project, line, ArtHandle.LineEnd, end, start);
            Assert.Equal(1, dragged.Art.Width);
            Near(start, ArtHandles.Position(project, Apply(line, dragged), ArtHandle.LineStart), 1e-6);
        }

        [Fact]
        public void CornerRadius_Drag_AlongTheDiagonal_ClampedToHalfTheShortSide()
        {
            // 사각형 400×300, 반경 0: 손잡이는 왼쪽 위 모서리, 대각선 안쪽으로 (30, 30) 끌면 반경 30
            var (project, rectangle) = Art(ArtKind.Rectangle);
            var handle = ArtHandles.Position(project, rectangle, ArtHandle.CornerRadius);
            Near((760, 390), handle);
            var dragged = ArtHandles.Drag(project, rectangle, ArtHandle.CornerRadius, handle, (handle.X + 30, handle.Y + 30));
            Assert.Equal(30, dragged.Art.CornerRadius, 9);
            Assert.Equal((0.0, 0.0, 0.0), (dragged.X, dragged.Y, dragged.Rotation));
            // 한쪽으로만 끌면 반만(대각선에 투영), 바깥으로 끌면 0 아래로 가지 않는다
            Assert.Equal(20, ArtHandles.Drag(project, rectangle, ArtHandle.CornerRadius, handle, (handle.X + 40, handle.Y)).Art.CornerRadius, 9);
            Assert.Equal(0, ArtHandles.Drag(project, rectangle, ArtHandle.CornerRadius, handle, (handle.X - 50, handle.Y - 50)).Art.CornerRadius);
            // 짧은 변(300)의 절반까지
            Assert.Equal(150, ArtHandles.Drag(project, rectangle, ArtHandle.CornerRadius, handle, (handle.X + 900, handle.Y + 900)).Art.CornerRadius);

            // 크기 2·90° 돌린 사각형: 화면 거리 / 크기 배율만큼, 상자 대각선 방향(돌린 방향)으로
            var (turnedProject, turned) = Art(ArtKind.Rectangle, 0, 0, 2, 90);
            turned.Art.CornerRadius = 10;
            var at = ArtHandles.Position(turnedProject, turned, ArtHandle.CornerRadius);
            var inward = ArtHandles.ToProject(turnedProject, turned, 11, 11);
            var step = (X: (inward.X - at.X) * 20, Y: (inward.Y - at.Y) * 20);
            Assert.Equal(30, ArtHandles.Drag(turnedProject, turned, ArtHandle.CornerRadius, at, (at.X + step.X, at.Y + step.Y)).Art.CornerRadius, 6);
        }

        [Fact]
        public void InnerRatio_Drag_ProjectsOnTheInnerVertexDirection()
        {
            // 별 300×300(뿔 5, 안쪽 0.5): 손잡이는 첫 안쪽 꼭짓점(위 가운데에서 시계 방향으로 36°)
            var (project, star) = Art(ArtKind.Star);
            var handle = ArtHandles.Position(project, star, ArtHandle.InnerRatio);
            var angle = (-90 + 36) * Math.PI / 180;
            Near((960 + 75 * Math.Cos(angle), 540 + 75 * Math.Sin(angle)), handle, 1e-9);
            // 꼭짓점 방향으로 바깥 반지름(150)의 0.2만큼 더 → 0.7
            var dragged = ArtHandles.Drag(project, star, ArtHandle.InnerRatio, handle, (handle.X + 30 * Math.Cos(angle), handle.Y + 30 * Math.Sin(angle)));
            Assert.Equal(0.7, dragged.Art.InnerRatio, 9);
            // 범위 밖은 0.1~0.95
            Assert.Equal(0.95, ArtHandles.Drag(project, star, ArtHandle.InnerRatio, handle, (handle.X + 900 * Math.Cos(angle), handle.Y + 900 * Math.Sin(angle))).Art.InnerRatio);
            Assert.Equal(0.1, ArtHandles.Drag(project, star, ArtHandle.InnerRatio, handle, (960, 540)).Art.InnerRatio);
        }

        [Fact]
        public void Drag_HostileValues_LeaveTheShapeAsItWas()
        {
            var (project, arrow) = Art(ArtKind.Arrow, 10, 20);
            foreach (var handle in new[] { ArtHandle.CornerRadius, ArtHandle.LineStart, ArtHandle.LineEnd, ArtHandle.InnerRatio })
            {
                var dragged = ArtHandles.Drag(project, arrow, handle, (double.NaN, 0), (5, double.PositiveInfinity));
                Assert.Equal((10.0, 20.0, 0.0, 400.0), (dragged.X, dragged.Y, dragged.Rotation, dragged.Art.Width));
            }
            // 끝을 화면 밖 아주 멀리 끌어도 너비는 상한(16,000)이고 값은 유한하다
            var end = ArtHandles.Position(project, arrow, ArtHandle.LineEnd);
            var far = ArtHandles.Drag(project, arrow, ArtHandle.LineEnd, end, (1e9, 1e9));
            Assert.Equal(ClipLayout.MaxTextBox, far.Art.Width);
            Assert.True(double.IsFinite(far.X) && double.IsFinite(far.Y) && double.IsFinite(far.Rotation));
        }

        [Fact]
        public void UsedStyle_KeepsEffects_DropsFontAndWarp_Sample()
        {
            var style = new TextStyle { FontFamily = "Arial", Bold = true, LetterSpacing = 300 };
            style.Warp.Kind = WarpKind.Wave;
            style.Warp.Amount = 60;
            style.Outline.Enabled = true;
            style.Glow.Enabled = true;
            style.Fill.Colors = new System.Collections.Generic.List<string> { "#FF0000" };
            var used = ArtShape.UsedStyle(style);
            Assert.Equal((TextStyle.DefaultFont, false, 0.0, WarpKind.None), (used.FontFamily, used.Bold, used.LetterSpacing, used.Warp.Kind));
            Assert.True(used.Outline.Enabled && used.Glow.Enabled);
            Assert.Equal("#FF0000", used.Fill.Colors.Single());
            // 원래 스타일은 그대로, 견본은 둥근 사각형
            Assert.Equal(WarpKind.Wave, style.Warp.Kind);
            var sample = ArtShape.StyleSample();
            Assert.Equal(ArtKind.Rectangle, sample.Kind);
            Assert.True(sample.CornerRadius > 0 && sample.Width > sample.Height);
        }

        [Fact]
        public void AddArt_DroppedOnMagneticMainTrack_KeepsItsTime_OnAnOverlayTrack()
        {
            // 빈 마그네틱 주 트랙에 3초(90)로 놓아도 0초에 붙지 않고 덮어 쓰기 트랙의 90
            var project = TestProjects.Empty(magnetic: true);
            var star = TimelineEdits.AddArt(project, ArtKind.Star, 90, 30, project.MainTrack);
            Assert.NotSame(project.MainTrack, project.TrackOf(star));
            Assert.Equal(90, star.Start);
            Assert.Empty(project.MainTrack.Clips);
            // 동영상이 있는 주 트랙 끝 뒤에 놓아도 클립 끝(100)에 붙지 않는다
            TestProjects.Add(project, project.MainTrack, 0, 100);
            var line = TimelineEdits.AddArt(project, ArtKind.Line, 300, 30, project.MainTrack);
            Assert.Equal(300, line.Start);
            Assert.Equal(TrackKind.Video, project.TrackOf(line).Kind);
            Assert.False(project.IsMain(project.TrackOf(line)));
            Assert.Equal(100, project.MainTrack.Clips.Single().Duration);

            // 마그네틱을 끈 주 트랙은 다른 줄과 같다 — 비었으면 거기, 놓은 시각 그대로
            var plain = TestProjects.Empty(magnetic: false);
            var heart = TimelineEdits.AddArt(plain, ArtKind.Heart, 45, 30, plain.MainTrack);
            Assert.Same(plain.MainTrack, plain.TrackOf(heart));
            Assert.Equal(45, heart.Start);
        }
    }
}
