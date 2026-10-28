using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>#7 아트 요소 1단계: 도형·선·화살표 모델, 윤곽 계산, 저장, 렌더 그래프.</summary>
    public class ArtTests
    {
        private static ArtShape Shape(ArtKind kind, double width = 400, double height = 300)
        {
            return new ArtShape { Kind = kind, Width = width, Height = height };
        }

        private static (Project Project, Clip Art) ArtProject(ArtKind kind = ArtKind.Rectangle)
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateArt(kind, 0, 30);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static RenderOptions Options(ITextRasterizer rasterizer, int width = 640, int height = 360)
        {
            return new RenderOptions { From = 0, To = 30, Width = width, Height = height, Audio = false, TempFolder = @"C:\Temp", TextRasterizer = rasterizer };
        }

        private static void AllInside(IEnumerable<List<(double X, double Y)>> polygons, double w, double h)
        {
            foreach (var (x, y) in polygons.SelectMany(p => p))
            {
                Assert.True(double.IsFinite(x) && double.IsFinite(y));
                Assert.InRange(x, -1e-9, w + 1e-9);
                Assert.InRange(y, -1e-9, h + 1e-9);
            }
        }

        // ---------- 윤곽 ----------

        [Fact]
        public void Rectangle_IsTheBox_TimesScale()
        {
            var outline = Assert.Single(ArtGeometry.Outline(Shape(ArtKind.Rectangle), 2));
            Assert.Equal(new List<(double, double)> { (0, 0), (800, 0), (800, 600), (0, 600) }, outline);
        }

        [Fact]
        public void RoundedRectangle_CutsCorners_ByRadius_AndKeepsEdges()
        {
            var shape = Shape(ArtKind.Rectangle);
            shape.CornerRadius = 50;
            var outline = Assert.Single(ArtGeometry.Outline(shape, 1));
            AllInside(new[] { outline }, 400, 300);
            Assert.Equal((0.0, 0.0, 400.0, 300.0), Round(TextWarp.Bounds(outline)));
            // 모서리 점은 모서리 원 위에 있고, 상자 모서리(0, 0)에서 반경 × (√2 − 1)만큼 떨어져 있다
            var nearest = outline.Min(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
            Assert.Equal(50 * Math.Sqrt(2) - 50, nearest, 3);
            // 반경이 짧은 변의 절반보다 크면 절반까지(캡슐 모양) — 가로 가운데 변이 남는다
            shape.CornerRadius = 10000;
            outline = Assert.Single(ArtGeometry.Outline(shape, 1));
            Assert.Equal((0.0, 0.0, 400.0, 300.0), Round(TextWarp.Bounds(outline)));
            Assert.Contains(outline, p => Math.Abs(p.Y) < 1e-9 && Math.Abs(p.X - 150) < 1e-9);
        }

        [Fact]
        public void Ellipse_PointsLieOnTheEllipse_AndAreDenseEnough()
        {
            foreach (var scale in new[] { 0.25, 1.0, 4.0 })
            {
                var outline = Assert.Single(ArtGeometry.Outline(Shape(ArtKind.Ellipse), scale));
                double a = 200 * scale, b = 150 * scale;
                foreach (var (x, y) in outline)
                    Assert.Equal(1, (x - a) * (x - a) / (a * a) + (y - b) * (y - b) / (b * b), 9);
                Assert.InRange(outline.Count, 24, 720);
                // 이웃 점 사이(현)가 출력 픽셀로 3 이하 — 크게 그려도 각이 보이지 않는다(상한 720점에 걸리지 않는 크기)
                var longest = outline.Select((p, i) => Distance(p, outline[(i + 1) % outline.Count])).Max();
                Assert.True(longest <= 3 || outline.Count == 720, scale + ": " + longest);
            }
        }

        [Fact]
        public void Polygon_And_Star_StartAtTopCenter_WithTheRightCount()
        {
            var polygon = Shape(ArtKind.Polygon, 200, 200);
            polygon.Sides = 6;
            var hexagon = Assert.Single(ArtGeometry.Outline(polygon, 1));
            Assert.Equal(6, hexagon.Count);
            Assert.Equal((100.0, 0.0), Round(hexagon[0]));
            Assert.All(hexagon, p => Assert.Equal(100, Distance(p, (100, 100)), 9));

            var star = Shape(ArtKind.Star, 200, 200);
            star.Sides = 5;
            star.InnerRatio = 0.4;
            var points = Assert.Single(ArtGeometry.Outline(star, 1));
            Assert.Equal(10, points.Count);
            Assert.Equal((100.0, 0.0), Round(points[0]));
            for (var i = 0; i < points.Count; i++)
                Assert.Equal(i % 2 == 0 ? 100 : 40, Distance(points[i], (100, 100)), 9);
        }

        [Fact]
        public void Triangle_And_Heart_FillTheBox()
        {
            Assert.Equal(new List<(double, double)> { (200, 0), (400, 300), (0, 300) }, Assert.Single(ArtGeometry.Outline(Shape(ArtKind.Triangle), 1)));
            var heart = Assert.Single(ArtGeometry.Outline(Shape(ArtKind.Heart), 1));
            Assert.Equal((0.0, 0.0, 400.0, 300.0), Round(TextWarp.Bounds(heart)));
            // 좌우 대칭이고 아래 끝(뾰족한 곳)은 가운데
            Assert.All(heart, p => Assert.Contains(heart, q => Math.Abs(q.X - (400 - p.X)) < 1e-6 && Math.Abs(q.Y - p.Y) < 1e-6));
            var bottom = heart.OrderByDescending(p => p.Y).First();
            Assert.Equal(200, bottom.X, 6);
        }

        [Fact]
        public void Line_IsABarOfThickness_AtMiddleHeight()
        {
            var shape = Shape(ArtKind.Line, 400, 60);
            shape.Thickness = 10;
            var bar = Assert.Single(ArtGeometry.Outline(shape, 2));
            Assert.Equal(new List<(double, double)> { (0, 50), (800, 50), (800, 70), (0, 70) }, bar);
        }

        [Fact]
        public void DashedLine_Repeats_ThreeOnTwoOff_InsideTheBox()
        {
            var shape = Shape(ArtKind.Line, 100, 20);
            shape.Thickness = 4;
            shape.Dashed = true;
            var dashes = ArtGeometry.Outline(shape, 1);
            // 대시 12 + 사이 8 = 20 주기 → 0, 20, 40, 60, 80에서 시작하는 다섯 개
            Assert.Equal(5, dashes.Count);
            Assert.Equal(new[] { 0.0, 20, 40, 60, 80 }, dashes.Select(d => d[0].X).ToArray());
            Assert.All(dashes, d => Assert.Equal(12, d[1].X - d[0].X, 9));
            AllInside(dashes, 100, 20);
            // 아주 긴 가는 점선도 대시 수에 상한이 있다
            shape.Width = ClipLayout.MaxTextBox;
            shape.Thickness = 1;
            Assert.InRange(ArtGeometry.Outline(shape, 4).Count, 1, 1001);
        }

        [Fact]
        public void Arrow_TipAtRightMiddle_HeadFromHeadSize()
        {
            var shape = Shape(ArtKind.Arrow, 400, 100);
            shape.Thickness = 10;
            shape.HeadSize = 3;
            var arrow = Assert.Single(ArtGeometry.Outline(shape, 1));
            Assert.Equal(7, arrow.Count);
            Assert.Contains((400.0, 50.0), arrow);
            // 머리 길이 = 두께 × 3, 머리 반폭 = 두께 × 3 × 0.6
            Assert.Contains((370.0, 32.0), arrow.Select(Round));
            Assert.Contains((370.0, 68.0), arrow.Select(Round));
            AllInside(new[] { arrow }, 400, 100);

            // 점선 화살표: 몸통 대시들 + 머리 삼각형 하나
            shape.Dashed = true;
            var parts = ArtGeometry.Outline(shape, 1);
            Assert.Equal(3, parts[parts.Count - 1].Count);
            Assert.Contains((400.0, 50.0), parts[parts.Count - 1]);
            Assert.All(parts.Take(parts.Count - 1), d => Assert.True(d.Max(p => p.X) <= 370 + 1e-9));
        }

        [Fact]
        public void HostileValues_AreClampedOrDefaulted_GeometryStaysFinite()
        {
            foreach (ArtKind kind in Enum.GetValues(typeof(ArtKind)))
            {
                var shape = new ArtShape
                {
                    Kind = kind, Width = double.NaN, Height = -5, CornerRadius = double.PositiveInfinity, Sides = int.MaxValue,
                    InnerRatio = double.NaN, Thickness = 1e300, HeadSize = -1, Dashed = true
                };
                var outline = ArtGeometry.Outline(shape, double.NaN);
                Assert.NotEmpty(outline);
                Assert.All(outline, p => Assert.True(p.Count >= 3));
                // 너비는 기본(400), 높이는 최소(1)
                AllInside(outline, 400, 1);
            }
            var unknown = new ArtShape { Kind = (ArtKind)99 };
            unknown.Normalize();
            Assert.Equal(ArtKind.Rectangle, unknown.Kind);
            var big = new ArtShape { Width = 1e9, Height = 1e9, Sides = -3 };
            big.Normalize();
            Assert.Equal((ClipLayout.MaxTextBox, ClipLayout.MaxTextBox, 3), (big.Width, big.Height, big.Sides));
            // 아주 큰 도형도 점 수는 상한 안
            Assert.InRange(Assert.Single(ArtGeometry.Outline(new ArtShape { Kind = ArtKind.Ellipse, Width = 16000, Height = 16000 }, 4)).Count, 24, 720);
        }

        // ---------- 클립·프로젝트 ----------

        [Fact]
        public void CreateArt_IsAVisualClip_WithShapeAndStyle()
        {
            var clip = TimelineEdits.CreateArt(ArtKind.Star, 15, 90);
            Assert.Equal(ClipKind.Art, clip.Kind);
            Assert.True(clip.IsVisual);
            Assert.False(clip.IsAudible);
            Assert.Equal(ArtKind.Star, clip.Art.Kind);
            Assert.NotNull(clip.Style);
            Assert.Equal((15L, 90L), (clip.Start, clip.Duration));
            Assert.Equal("별", clip.Name);
            // 사본은 모양을 따로 쥔다
            var copy = clip.CloneWithNewId();
            copy.Art.Width = 1;
            Assert.Equal(300, clip.Art.Width);
            // 선은 납작한 상자
            var line = TimelineEdits.CreateArt(ArtKind.Line, 0, 30);
            Assert.True(line.Art.Height < line.Art.Width);
        }

        [Fact]
        public void ArtClip_NotOnSubtitleOrAudioTracks()
        {
            var (project, art) = ArtProject();
            Assert.False(TimelineEdits.Accepts(new Track { Kind = TrackKind.Video, Subtitles = true }, art));
            Assert.False(TimelineEdits.Accepts(new Track { Kind = TrackKind.Audio }, art));
            Assert.True(TimelineEdits.Accepts(project.MainTrack, art));
        }

        [Fact]
        public void AddArt_UsesDropTrackIfFree_ElseAFreeOverlayTrack_ElseANewOne()
        {
            var project = TestProjects.Empty(magnetic: true);
            TestProjects.Add(project, project.MainTrack, 0, 100);
            // 끌어 놓은 줄이 주 트랙이어도 비어 있으면 거기 — 여기서는 주 트랙이 차 있으므로 새 덮어 쓰기 트랙
            var first = TimelineEdits.AddArt(project, ArtKind.Star, 10, 30, project.MainTrack);
            Assert.Equal(2, project.VideoTracks.Count);
            Assert.Same(project.VideoTracks[1], project.TrackOf(first));
            Assert.Equal(10, first.Start);
            // 같은 구간: 두 번째 덮어 쓰기 트랙, 겹치지 않는 구간: 첫 덮어 쓰기 트랙
            var second = TimelineEdits.AddArt(project, ArtKind.Line, 20, 30);
            Assert.Same(project.VideoTracks[2], project.TrackOf(second));
            var third = TimelineEdits.AddArt(project, ArtKind.Heart, 60, 30);
            Assert.Same(project.VideoTracks[1], project.TrackOf(third));
            // 놓은 줄이 비었으면 그 줄 — 마그네틱 주 트랙은 빼고(#38: 놓은 시각을 지키게 덮어 쓰기 트랙으로)
            var fourth = TimelineEdits.AddArt(project, ArtKind.Arrow, 200, 30, project.MainTrack);
            Assert.Same(project.VideoTracks[1], project.TrackOf(fourth));
            Assert.Equal(200, fourth.Start);
            var fourthB = TimelineEdits.AddArt(project, ArtKind.Arrow, 200, 30, project.VideoTracks[2]);
            Assert.Same(project.VideoTracks[2], project.TrackOf(fourthB));
            // 잠긴 줄·자막 줄·오디오 줄에는 놓지 않는다
            project.VideoTracks[1].Locked = true;
            var audio = TimelineEdits.AddTrack(project, TrackKind.Audio);
            var fifth = TimelineEdits.AddArt(project, ArtKind.Ellipse, 300, 30, audio);
            Assert.Equal(TrackKind.Video, project.TrackOf(fifth).Kind);
            Assert.NotSame(project.VideoTracks[1], project.TrackOf(fifth));
        }

        [Fact]
        public void Snapshot_RoundTripsShape_AndStyle()
        {
            var (project, art) = ArtProject(ArtKind.Arrow);
            art.Art.Width = 512;
            art.Art.Dashed = true;
            art.Style.Outline.Enabled = true;
            var json = ProjectFile.Snapshot(project);
            var node = JsonNode.Parse(json)["VideoTracks"][0]["Clips"][0];
            Assert.Equal("Art", (string)node["Kind"]);
            Assert.Equal("Arrow", (string)node["Art"]["Kind"]);
            var read = ProjectFile.FromSnapshot(json).MainTrack.Clips[0];
            Assert.Equal((ClipKind.Art, ArtKind.Arrow, 512.0, true, true),
                (read.Kind, read.Art.Kind, read.Art.Width, read.Art.Dashed, read.Style.Outline.Enabled));
        }

        [Fact]
        public void Validate_ShapeOnlyOnArtClips_AndRepairsMissingOrHostileShape()
        {
            var (project, art) = ArtProject();
            var text = TimelineEdits.CreateText("가", 40, 30);
            TimelineEdits.Place(project, project.MainTrack, text, 40);
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var clips = json["VideoTracks"][0]["Clips"];
            // 글자 클립에 붙은 모양은 버린다, 아트 클립의 빠진 모양·스타일은 기본으로, 모르는 모양 종류는 사각형
            clips[1]["Art"] = JsonNode.Parse("{\"Kind\":\"Star\"}");
            clips[0]["Art"] = null;
            clips[0]["Style"] = null;
            var read = ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips;
            Assert.Null(read[1].Art);
            Assert.Equal(ArtKind.Rectangle, read[0].Art.Kind);
            Assert.NotNull(read[0].Style);
            clips[0]["Art"] = JsonNode.Parse("{\"Kind\":\"Blob\",\"Width\":-4,\"Sides\":40}");
            read = ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips;
            Assert.Equal((ArtKind.Rectangle, 1.0, 12), (read[0].Art.Kind, read[0].Art.Width, read[0].Art.Sides));
            // 글자 상자·단어 시각은 아트 클립에 남지 않는다
            clips[0]["TextBoxWidth"] = 300;
            Assert.Equal(0, ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0].TextBoxWidth);
        }

        [Fact]
        public void Save_WritesVersion3_OnlyWhenThereIsArt_AndLoadReadsIt()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-art-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "p.fvproj");
                var plain = TestProjects.Empty(magnetic: false);
                TimelineEdits.Place(plain, plain.MainTrack, TimelineEdits.CreateText("가", 0, 30), 0);
                ProjectFile.Save(plain, path);
                Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(path))["Version"]);
                Assert.Equal(2, Project.CurrentVersion);

                var (project, _) = ArtProject(ArtKind.Heart);
                ProjectFile.Save(project, path);
                Assert.Equal(Project.ArtVersion, (int)JsonNode.Parse(File.ReadAllText(path))["Version"]);
                Assert.Equal(3, Project.ArtVersion);
                Assert.Equal(ArtKind.Heart, ProjectFile.Load(path).MainTrack.Clips[0].Art.Kind);

                // 아트를 지우고 저장하면 다시 2 — 예전 플러그인에서 열 수 있다
                var loaded = ProjectFile.Load(path);
                loaded.MainTrack.Clips.Clear();
                ProjectFile.Save(loaded, path);
                Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(path))["Version"]);

                // 더 새 형식은 거부(4는 애니메이션 형식 — #4)
                var json = JsonNode.Parse(File.ReadAllText(path));
                json["Version"] = Project.AnimationVersion + 1;
                File.WriteAllText(path, json.ToJsonString());
                Assert.Contains("더 새 버전", Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path)).Message);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // ---------- 렌더 그래프·키 ----------

        [Fact]
        public void Graph_DrawsArtWithTheRasterizer_LikeStyledText()
        {
            var (project, art) = ArtProject(ArtKind.Ellipse);
            art.Art.Width = 200;
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer(), 1280, 720));
            var request = Assert.Single(plan.TextImages).Value;
            Assert.Equal(ArtKind.Ellipse, request.Art.Kind);
            Assert.Equal(200, request.Art.Width);
            Assert.Equal("", request.Text);
            // 길이 값의 배율 = 출력 / 프로젝트(× 화소 배율) — 글자와 같다
            Assert.Equal(2, request.EffectScale, 9);
            Assert.Contains("overlay", plan.Filter);
            Assert.DoesNotContain("drawtext", plan.Filter);
            // 요청은 클립의 모양을 쥐지 않는다
            Assert.NotSame(art.Art, request.Art);
        }

        [Fact]
        public void Graph_WithoutRasterizer_ArtIsTransparent_NotText()
        {
            var (project, _) = ArtProject();
            var plan = RenderGraph.Build(project, Options(null));
            Assert.Empty(plan.TextImages);
            Assert.DoesNotContain("drawtext", plan.Filter);
            Assert.Contains("color=c=black@0.0", plan.Filter);
        }

        [Fact]
        public void ArtKey_IgnoresHiddenFontAndWarp_ButFollowsShape()
        {
            var (project, art) = ArtProject();
            var rasterizer = new FakeTextRasterizer();
            string KeyWith() => Path.GetFileName(Assert.Single(RenderGraph.Build(project, Options(rasterizer)).TextImages).Key);
            var baseline = KeyWith();
            art.Style.FontFamily = "Arial";
            art.Style.Bold = true;
            art.Style.Warp.Kind = WarpKind.Wave;
            art.Style.Warp.Amount = 50;
            art.FontSize = 200;
            Assert.Equal(baseline, KeyWith());
            art.Art.CornerRadius = 20;
            Assert.NotEqual(baseline, KeyWith());
            art.Art.CornerRadius = 0;
            art.Style.Fill.Colors = new List<string> { "#FF0000" };
            Assert.NotEqual(baseline, KeyWith());
        }

        [Fact]
        public void TextKey_IsUnchanged_SoOldCachesStayValid()
        {
            var style = new TextStyle();
            style.Normalize();
            var request = new TextImageRequest { Text = "가나 AB", Style = style, FontPixelSize = 72, EffectScale = 1, CanvasWidth = 1920, CanvasHeight = 1080 };
            // 아트 필드를 더하기 전(래스터라이저 버전 10)의 키
            Assert.Equal("adae510d1ff631136296f7ffe7006d310218dfe3392c21412bb3240b190d8b37", request.Key(10));
            request.Art = new ArtShape();
            Assert.NotEqual("adae510d1ff631136296f7ffe7006d310218dfe3392c21412bb3240b190d8b37", request.Key(10));
        }

        [Fact]
        public void NeedsTextImage_StyledOrBoxedText_AndArt()
        {
            var (_, art) = ArtProject();
            Assert.True(RenderGraph.NeedsTextImage(art));
            var text = TimelineEdits.CreateText("가", 0, 30);
            Assert.False(RenderGraph.NeedsTextImage(text));
            text.TextBoxWidth = 100;
            Assert.True(RenderGraph.NeedsTextImage(text));
            text.TextBoxWidth = 0;
            text.Style = new TextStyle();
            Assert.True(RenderGraph.NeedsTextImage(text));
            Assert.False(RenderGraph.NeedsTextImage(TestProjects.VideoClip(0, 30)));
        }

        [Fact]
        public void HitTest_PicksArtByItsShapeBox()
        {
            var (project, art) = ArtProject();
            art.Art.Width = 100;
            art.Art.Height = 50;
            art.X = 100;
            (double, double) Box(Clip c) => throw new InvalidOperationException("아트는 글자처럼 재지 않는다");
            // 가운데 (420, 180), 크기 100×50
            Assert.Same(art, ClipLayout.HitTest(project, 10, 420, 180, Box));
            Assert.Same(art, ClipLayout.HitTest(project, 10, 469, 204, Box));
            Assert.Null(ClipLayout.HitTest(project, 10, 320, 180, Box));
            art.Scale = 2;
            Assert.Same(art, ClipLayout.HitTest(project, 10, 515, 180, Box));
        }

        private static double Distance((double X, double Y) a, (double X, double Y) b)
        {
            return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        private static (double, double) Round((double X, double Y) p)
        {
            return (Math.Round(p.X, 6) + 0.0, Math.Round(p.Y, 6) + 0.0);
        }

        private static (double, double, double, double) Round((double A, double B, double C, double D) b)
        {
            return (Math.Round(b.A, 6) + 0.0, Math.Round(b.B, 6) + 0.0, Math.Round(b.C, 6) + 0.0, Math.Round(b.D, 6) + 0.0);
        }
    }
}
