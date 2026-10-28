using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>미리 보기에서 옮길 때 붙기(#4): 화면 가장자리·가운데, 다른 클립 상자의 가장자리·가운데에 가장 가까운 선 하나로, 회전하면 감싼 상자로.</summary>
    public class PreviewSnapTests
    {
        private static (double, double) Box(Clip clip)
        {
            return (200, 80);
        }

        /// <summary>640×360 화면에 사진(800×600 맞춤 — 480×360, 가로 80~560)과 글자 하나.</summary>
        private static (Project Project, Clip Photo, Clip Title) Layered()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Media.Add(TestProjects.Image());
            var photo = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 90);
            TimelineEdits.Place(project, project.MainTrack, photo, 0);
            var title = TimelineEdits.CreateText("제목", 0, 90);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), title, 0);
            return (project, photo, title);
        }

        [Fact]
        public void HalfExtents_RotatedRectangle_UsesTheBoxAroundIt()
        {
            Assert.Equal((50.0, 25.0), PreviewSnap.HalfExtents(100, 50, 0));
            var (w, h) = PreviewSnap.HalfExtents(100, 50, 90);
            Assert.Equal(25, w, 9);
            Assert.Equal(50, h, 9);
            var (dw, dh) = PreviewSnap.HalfExtents(100, 100, 45);
            Assert.Equal(50 * System.Math.Sqrt(2), dw, 9);
            Assert.Equal(dw, dh, 9);
            Assert.Equal((0.0, 0.0), PreviewSnap.HalfExtents(-5, 0, double.NaN));
        }

        [Fact]
        public void Targets_ScreenEdgesAndCenter_PlusOtherVisibleClips_NotTheMovingOnes()
        {
            var (project, photo, title) = Layered();
            title.X = 100;
            var lines = PreviewSnap.Targets(project, 10, new[] { title.Id }, Box);
            // 화면 0·320·640, 사진 80·320·560(세로는 화면을 꽉 채워 0·180·360)
            Assert.Equal(new[] { 0.0, 320, 640, 80, 320, 560 }, lines.X);
            Assert.Equal(new[] { 0.0, 180, 360, 0, 180, 360 }, lines.Y);
            // 글자(200×80 상자, X 100)를 넣으면 그 상자도
            lines = PreviewSnap.Targets(project, 10, new[] { photo.Id }, Box);
            Assert.Equal(new[] { 0.0, 320, 640, 320, 420, 520 }, lines.X);
            Assert.Equal(new[] { 0.0, 180, 360, 140, 180, 220 }, lines.Y);
            // 그 시각에 없는 클립·숨긴 트랙은 빠진다
            Assert.Equal(3, PreviewSnap.Targets(project, 100, new string[0], Box).X.Count);
            project.TrackOf(title).Hidden = true;
            Assert.Equal(new[] { 0.0, 320, 640, 80, 320, 560 }, PreviewSnap.Targets(project, 10, null, Box).X);
        }

        [Fact]
        public void Move_NearestLineWithinThreshold_EachAxis_ReturnsGuides()
        {
            var (project, photo, title) = Layered();
            var lines = PreviewSnap.Targets(project, 10, new[] { title.Id }, Box);
            // 200×80 상자의 왼쪽 가장자리(320 + x − 100)가 사진 왼쪽 80에서 3 → 붙음, 세로 가운데는 화면 가운데에서 5 → 붙음
            var moved = PreviewSnap.Move(project, -137, 5, 200, 80, 0, lines, 8);
            Assert.Equal((-140.0, 0.0, (double?)80, (double?)180), moved);
            // 오른쪽 가장자리가 화면 오른쪽 640에 붙는다(오른쪽 = 320 + 217 + 100 = 637)
            moved = PreviewSnap.Move(project, 217, 60, 200, 80, 0, lines, 8);
            Assert.Equal(220, moved.X);
            Assert.Equal(640, moved.GuideX);
            // 세로: 아래 가장자리 180 + 60 + 40 = 280 — 가까운 선이 없어 그대로
            Assert.Equal(60, moved.Y);
            Assert.Null(moved.GuideY);
            // 둘이 문턱 안이면 더 가까운 선: 236×80 상자의 왼쪽 83(80에서 3)보다 오른쪽 319(320에서 1)
            moved = PreviewSnap.Move(project, -119, 0, 236, 80, 0, lines, 8);
            Assert.Equal(320, moved.GuideX);
            Assert.Equal(-118, moved.X);
            // 멀면 붙지 않는다
            Assert.Equal((-120.0, 30.0, (double?)null, (double?)null), PreviewSnap.Move(project, -120, 30, 200, 80, 0, lines, 8));
        }

        [Fact]
        public void Move_RotatedClip_SnapsByTheBoxAroundIt()
        {
            var (project, _, title) = Layered();
            var lines = PreviewSnap.Targets(project, 10, new[] { title.Id }, Box);
            // 200×80을 90° 돌리면 가로 반폭 40 — 가운데 70이면 가장자리 30·110이라 어느 선에서도 멀다(돌리지 않으면 왼쪽 −30)
            Assert.Null(PreviewSnap.Move(project, -250, 0, 200, 80, 90, lines, 8).GuideX);
            // 가운데 43이면 왼쪽 3(화면 0에서 3)·오른쪽 83(사진 80에서 3) — 어느 쪽이든 3만큼 왼쪽으로
            var moved = PreviewSnap.Move(project, -277, 0, 200, 80, 90, lines, 8);
            Assert.Contains(moved.GuideX.Value, new[] { 0.0, 80 });
            Assert.Equal(-280, moved.X, 9);
        }
    }
}
