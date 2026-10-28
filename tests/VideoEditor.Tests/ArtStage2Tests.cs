using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>#39 아트 요소 2단계: 말풍선(꼬리·안의 글)·강조 표시·프레임·배경의 모양, 처음 값, 렌더 요청, 저장(형식 3 그대로).</summary>
    public class ArtStage2Tests
    {
        private static readonly ArtKind[] Stage2 =
        {
            ArtKind.Bubble, ArtKind.BubbleSquare, ArtKind.BubbleThought, ArtKind.CircleMark, ArtKind.Underline, ArtKind.Highlighter,
            ArtKind.BoxMark, ArtKind.Sparkle, ArtKind.FocusLines, ArtKind.LowerThird, ArtKind.ScreenBorder, ArtKind.Plate
        };

        private static void AllInside(IEnumerable<List<(double X, double Y)>> polygons, double w, double h)
        {
            foreach (var (x, y) in polygons.SelectMany(p => p))
            {
                Assert.True(double.IsFinite(x) && double.IsFinite(y));
                Assert.InRange(x, -1e-6, w + 1e-6);
                Assert.InRange(y, -1e-6, h + 1e-6);
            }
        }

        /// <summary>점이 다각형 안인지(0이 아닌 감김 수 — 여러 조각을 함께, 그리개와 같은 규칙).</summary>
        private static bool Filled(List<List<(double X, double Y)>> polygons, double x, double y)
        {
            var winding = 0;
            foreach (var polygon in polygons)
            {
                for (var i = 0; i < polygon.Count; i++)
                {
                    var (x1, y1) = polygon[i];
                    var (x2, y2) = polygon[(i + 1) % polygon.Count];
                    var cross = (x2 - x1) * (y - y1) - (x - x1) * (y2 - y1);
                    if (y1 <= y && y2 > y && cross > 0)
                        winding++;
                    else if (y1 > y && y2 <= y && cross < 0)
                        winding--;
                }
            }
            return winding != 0;
        }

        private static ArtShape Default(ArtKind kind)
        {
            return ArtShape.Default(kind);
        }

        // ---------- 모양 ----------

        [Fact]
        public void NewKinds_StayInTheirBox_ExceptTheBubbleTail()
        {
            foreach (var kind in Stage2)
            {
                var shape = Default(kind);
                // 꼬리를 가운데에 두면 말풍선도 상자 안
                shape.TailX = shape.TailY = 0;
                foreach (var scale in new[] { 0.25, 1.0, 3.0 })
                {
                    var outline = ArtGeometry.Outline(shape, scale);
                    Assert.NotEmpty(outline);
                    Assert.All(outline, p => Assert.True(p.Count >= 3, kind + " 조각 점 수"));
                    AllInside(outline, shape.Width * scale, shape.Height * scale);
                    Assert.All(outline, p => Assert.True(p.Count <= 2000, kind + " 점 수 " + p.Count));
                }
            }
        }

        [Fact]
        public void Bubble_TailReachesTheTip_SameWindingAsTheBody()
        {
            var shape = Default(ArtKind.Bubble);
            Assert.Equal((-0.22, 0.85), (shape.TailX, shape.TailY));
            var outline = ArtGeometry.Outline(shape, 1);
            Assert.Equal(2, outline.Count);
            var tip = ArtGeometry.TailTip(shape, 1);
            Assert.Equal((240 - 0.22 * 480, 150 + 0.85 * 300), tip);
            Assert.Contains(tip, outline[1]);
            // 몸통과 꼬리가 같은 방향이라 겹친 곳이 합쳐진다(0이 아닌 감김 수)
            Assert.All(outline, p => Assert.True(ArtGeometry.SignedArea(p) > 0));
            Assert.True(Filled(outline, 240, 150));
            // 꼬리 끝 바로 안쪽(끝과 그 앞뒤 점의 무게중심)
            var at = outline[1].IndexOf(tip);
            var (before, after) = (outline[1][at - 2], outline[1][at + 2]);
            Assert.True(Filled(outline, (before.X + after.X + tip.X) / 3, (before.Y + after.Y + tip.Y) / 3));
            Assert.False(Filled(outline, 470, 290));
            // 꼬리를 옮기면 끝이 따라간다(상자 배수), 가운데면 꼬리가 없다
            shape.TailX = 0.8;
            shape.TailY = -0.9;
            Assert.Contains((240 + 0.8 * 480, 150 - 0.9 * 300), ArtGeometry.Outline(shape, 1)[1]);
            shape.TailX = shape.TailY = 0;
            Assert.Single(ArtGeometry.Outline(shape, 1));
            // 각진 말풍선은 둥근 사각형 몸통 + 꼬리
            var square = Default(ArtKind.BubbleSquare);
            Assert.Equal(24, square.CornerRadius);
            Assert.Equal(2, ArtGeometry.Outline(square, 1).Count);
        }

        [Fact]
        public void ThoughtBubble_CirclesShrinkTowardTheTip_NoneWhenTheTipIsInside()
        {
            var shape = Default(ArtKind.BubbleThought);
            var outline = ArtGeometry.Outline(shape, 1);
            Assert.Equal(4, outline.Count);
            var radii = outline.Skip(1).Select(p => (p.Max(q => q.X) - p.Min(q => q.X)) / 2).ToList();
            Assert.True(radii[0] > radii[1] && radii[1] > radii[2], string.Join(", ", radii));
            // 마지막 동그라미 가운데 = 꼬리 끝
            var last = outline[3];
            var tip = ArtGeometry.TailTip(shape, 1);
            Assert.Equal(tip.X, (last.Max(q => q.X) + last.Min(q => q.X)) / 2, 6);
            shape.TailX = 0.1;
            shape.TailY = 0.1;
            Assert.Single(ArtGeometry.Outline(shape, 1));
        }

        [Fact]
        public void Rings_HaveAnOppositeWindingHole_ThicknessAndRadius()
        {
            var box = Default(ArtKind.BoxMark);
            var ring = ArtGeometry.Outline(box, 1);
            Assert.Equal(2, ring.Count);
            Assert.True(ArtGeometry.SignedArea(ring[0]) > 0);
            Assert.True(ArtGeometry.SignedArea(ring[1]) < 0);
            // 가운데는 비고, 두께(8) 안은 칠해진다
            Assert.False(Filled(ring, 240, 140));
            Assert.True(Filled(ring, 4, 140));
            Assert.False(Filled(ring, 12, 140));
            Assert.Equal((8.0, 472.0), (ring[1].Min(p => p.X), ring[1].Max(p => p.X)));
            // 두께가 짧은 변의 절반 이상이면 구멍이 없다
            box.Thickness = 200;
            Assert.Single(ArtGeometry.Outline(box, 1));

            // 화면 테두리: 바깥은 각지고(화면 모서리까지 칠함) 안쪽만 둥글다
            var border = Default(ArtKind.ScreenBorder);
            var frame = ArtGeometry.Outline(border, 0.5);
            Assert.True(Filled(frame, 0.5, 0.5));
            Assert.False(Filled(frame, 480, 270));
            Assert.True(Filled(frame, 12 + 1, 12 + 1), "안쪽 모서리가 둥글어 구석이 칠해진다");
            Assert.False(Filled(frame, 13, 270));
        }

        [Fact]
        public void Emphasis_Shapes()
        {
            // 동그라미 치기: 가운데는 비고 둘레는 칠해진다
            var circle = ArtGeometry.Outline(Default(ArtKind.CircleMark), 1);
            Assert.Single(circle);
            Assert.False(Filled(circle, 210, 120));
            Assert.True(Filled(circle, 210, 3) || Filled(circle, 210, 6), "위 둘레");
            // 형광펜: 평행사변형, 반투명 노랑
            var marker = Assert.Single(ArtGeometry.Outline(Default(ArtKind.Highlighter), 1));
            Assert.Equal(4, marker.Count);
            Assert.Equal("#8CFACC15", TimelineEdits.ArtStyle(ArtKind.Highlighter).Fill.Colors.Single());
            // 반짝임: 큰 것 하나 + 작은 것 둘
            Assert.Equal(3, ArtGeometry.Outline(Default(ArtKind.Sparkle), 1).Count);
            // 집중선: 가운데(타원 × 0.55)는 비고 가장자리에서 시작한다, 늘 같은 모양
            var focus = Default(ArtKind.FocusLines);
            var lines = ArtGeometry.Outline(focus, 0.5);
            Assert.Equal(ArtGeometry.FocusLineCount, lines.Count);
            Assert.False(Filled(lines, 480, 270));
            Assert.All(lines, l => Assert.True(l.Take(2).All(p => p.X < 1e-6 || p.X > 960 - 1e-6 || p.Y < 1e-6 || p.Y > 540 - 1e-6)));
            Assert.Equal(lines.SelectMany(l => l), ArtGeometry.Outline(focus, 0.5).SelectMany(l => l));
            // 아래 자막 띠: 강조 막대 + 띠
            Assert.Equal(2, ArtGeometry.Outline(Default(ArtKind.LowerThird), 1).Count);
        }

        [Fact]
        public void TextArea_InsideTheBody_OnlyForTextKinds()
        {
            foreach (ArtKind kind in Enum.GetValues(typeof(ArtKind)))
            {
                var shape = Default(kind);
                var area = ArtGeometry.TextArea(shape, 2);
                Assert.Equal(ArtShape.HoldsText(kind), area != null);
                if (area == null)
                    continue;
                var (x, y, w, h) = area.Value;
                Assert.True(w > 0 && h > 0);
                var outline = ArtGeometry.Outline(shape, 2);
                // 글 상자 네 모서리가 모두 칠한 곳 안(말풍선 몸통·띠·판)
                foreach (var (px, py) in new[] { (x, y), (x + w, y), (x, y + h), (x + w, y + h) })
                    Assert.True(Filled(outline, px, py), kind + " 글 상자 모서리 (" + px + ", " + py + ")");
            }
            // 아래 자막 띠의 글은 강조 막대 오른쪽
            var lower = ArtGeometry.TextArea(Default(ArtKind.LowerThird), 1).Value;
            Assert.True(lower.X > 140 * 0.12 + 140 * 0.08);
        }

        [Fact]
        public void HostileTail_IsClampedOrZero()
        {
            var shape = new ArtShape { Kind = ArtKind.Bubble, TailX = double.NaN, TailY = 1e9 };
            shape.Normalize();
            Assert.Equal((0.0, ArtShape.MaxTail), (shape.TailX, shape.TailY));
            var outline = ArtGeometry.Outline(new ArtShape { Kind = ArtKind.BubbleThought, TailX = -1e300, TailY = double.PositiveInfinity }, 1);
            Assert.All(outline.SelectMany(p => p), p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        }

        // ---------- 클립·처음 값 ----------

        [Fact]
        public void CreateArt_TextKinds_HaveTextFontAndColor_Styles()
        {
            var bubble = TimelineEdits.CreateArt(ArtKind.Bubble, 0, 30);
            Assert.Equal(("안녕하세요!", 44.0, "#111827"), (bubble.Text, bubble.FontSize, bubble.TextColor));
            Assert.Equal("#FFFFFF", bubble.Style.Fill.Colors.Single());
            Assert.True(bubble.Style.Outline.Enabled);
            var lower = TimelineEdits.CreateArt(ArtKind.LowerThird, 0, 30);
            Assert.Equal(("#FFFFFF", TextAlign.Left), (lower.TextColor, lower.Style.Align));
            Assert.Equal("", TimelineEdits.CreateArt(ArtKind.Plate, 0, 30).Text);
            // 글을 넣지 않는 모양은 글이 없다
            Assert.Null(TimelineEdits.CreateArt(ArtKind.CircleMark, 0, 30).Text);
            Assert.Equal("둥근 말풍선", bubble.Name);
            foreach (var kind in Stage2)
                Assert.NotEqual("도형", ArtShape.Group(kind));
            Assert.Equal("도형", ArtShape.Group(ArtKind.Arrow));
        }

        [Fact]
        public void AddArt_ScreenKinds_FitTheScreen_LowerThirdAtBottomLeft()
        {
            var project = TestProjects.Empty(magnetic: false);
            var focus = TimelineEdits.AddArt(project, ArtKind.FocusLines, 0, 30);
            Assert.Equal((640.0, 360.0), (focus.Art.Width, focus.Art.Height));
            var border = TimelineEdits.AddArt(project, ArtKind.ScreenBorder, 0, 30);
            Assert.Equal((640.0, 360.0), (border.Art.Width, border.Art.Height));
            var lower = TimelineEdits.AddArt(project, ArtKind.LowerThird, 0, 30);
            var box = ClipLayout.TextBox(project, lower, lower.Art.Width, lower.Art.Height);
            // 화면 안 왼쪽 아래(가장자리에서 폭의 5%·높이의 8%)
            Assert.InRange(box.CenterX - box.Width / 2, 640 * 0.05 - 1, 640 * 0.05 + 1);
            Assert.InRange(box.CenterY + box.Height / 2, 360 - 360 * 0.08 - 1, 360 - 360 * 0.08 + 1);
        }

        [Fact]
        public void Snapshot_RoundTripsTailAndText_Version3()
        {
            var project = TestProjects.Empty(magnetic: false);
            var bubble = TimelineEdits.AddArt(project, ArtKind.BubbleSquare, 0, 30);
            bubble.Art.TailX = 1.5;
            bubble.Text = "두 줄\n말풍선";
            var json = ProjectFile.Snapshot(project);
            var node = JsonNode.Parse(json)["VideoTracks"][1]["Clips"][0];
            Assert.Equal("BubbleSquare", (string)node["Art"]["Kind"]);
            Assert.Equal(1.5, (double)node["Art"]["TailX"]);
            var read = ProjectFile.FromSnapshot(json).VideoTracks[1].Clips[0];
            Assert.Equal((ArtKind.BubbleSquare, 1.5, 0.85, "두 줄\n말풍선"), (read.Art.Kind, read.Art.TailX, read.Art.TailY, read.Text));
            // 꼬리가 없는 모양은 TailX·TailY를 적지 않는다(1단계 파일과 같은 모양)
            var plain = TimelineEdits.AddArt(project, ArtKind.Rectangle, 100, 30);
            var plainNode = JsonNode.Parse(ProjectFile.Snapshot(project))["VideoTracks"][1]["Clips"].AsArray().Single(c => (string)c["Art"]["Kind"] == "Rectangle");
            Assert.Null(plainNode["Art"]["TailX"]);

            // 저장 형식은 그대로 3(새 종류도 아트 클립) — 형식 번호를 올리지 않는다
            var folder = Path.Combine(Path.GetTempPath(), "fv-art2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "p.fvproj");
                ProjectFile.Save(project, path);
                Assert.Equal(3, (int)JsonNode.Parse(File.ReadAllText(path))["Version"]);
                Assert.Equal(ArtKind.BubbleSquare, ProjectFile.Load(path).VideoTracks[1].Clips.First(c => c.Art.Kind != ArtKind.Rectangle).Art.Kind);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void UnknownKind_FromANewerVersion_ReadsAsRectangle_KeepingBoxStyleAndText()
        {
            // 예전 판이 이 판의 새 종류를 읽을 때와 같은 길: 모르는 이름은 사각형, 모르는 값(꼬리 등)은 버리고 상자·스타일·글은 그대로
            var project = TestProjects.Empty(magnetic: false);
            var bubble = TimelineEdits.AddArt(project, ArtKind.Bubble, 0, 30);
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var art = json["VideoTracks"][1]["Clips"][0]["Art"];
            art["Kind"] = "Balloon2099";
            art["Spikes"] = 7;
            var read = ProjectFile.FromSnapshot(json.ToJsonString()).VideoTracks[1].Clips[0];
            Assert.Equal((ArtKind.Rectangle, 480.0, 300.0, "안녕하세요!"), (read.Art.Kind, read.Art.Width, read.Art.Height, read.Text));
            Assert.Equal("#FFFFFF", read.Style.Fill.Colors.Single());
        }

        // ---------- 렌더 요청 ----------

        private static RenderOptions Options(ITextRasterizer rasterizer)
        {
            return new RenderOptions { From = 0, To = 30, Width = 1280, Height = 720, Audio = false, TempFolder = @"C:\Temp", TextRasterizer = rasterizer };
        }

        [Fact]
        public void Graph_BubbleRequest_CarriesTextFontColor_KeyFollowsTailAndText()
        {
            var project = TestProjects.Empty(magnetic: false);
            var bubble = TimelineEdits.AddArt(project, ArtKind.Bubble, 0, 30);
            bubble.Style.FontFamily = "Arial";
            bubble.Style.Warp.Kind = WarpKind.Wave;
            var rasterizer = new FakeTextRasterizer();
            TextImageRequest Request() => Assert.Single(RenderGraph.Build(project, Options(rasterizer)).TextImages).Value;
            var request = Request();
            Assert.Equal(("안녕하세요!", "#111827", "Arial"), (request.Text, request.ArtTextColor, request.Style.FontFamily));
            // 글자 크기 × 출력 배율(1280 / 640 = 2), 휘기는 쓰지 않는다
            Assert.Equal(44 * 2, request.FontPixelSize, 9);
            Assert.Equal(WarpKind.None, request.Style.Warp.Kind);
            var key = request.Key(rasterizer.Version);
            bubble.Art.TailX = 0.5;
            Assert.NotEqual(key, Request().Key(rasterizer.Version));
            bubble.Art.TailX = -0.22;
            Assert.Equal(key, Request().Key(rasterizer.Version));
            bubble.Text = "다른 글";
            Assert.NotEqual(key, Request().Key(rasterizer.Version));
            bubble.TextColor = "#FF0000";
            Assert.Equal("#FF0000", Request().ArtTextColor);
            // 글이 비면 글 값을 넘기지 않는다
            bubble.Text = "";
            Assert.Equal(("", 0.0, null), (Request().Text, Request().FontPixelSize, Request().ArtTextColor));
        }

        [Fact]
        public void Graph_ShapesWithoutText_IgnoreClipTextAndTail()
        {
            var project = TestProjects.Empty(magnetic: false);
            var rectangle = TimelineEdits.AddArt(project, ArtKind.Rectangle, 0, 30);
            var rasterizer = new FakeTextRasterizer();
            string Key() => Assert.Single(RenderGraph.Build(project, Options(rasterizer)).TextImages).Value.Key(rasterizer.Version);
            var key = Key();
            // 사각형에 글·꼬리가 들어 있어도(남이 만든 파일) 그림·키는 같다
            rectangle.Text = "숨은 글";
            rectangle.Art.TailX = 2;
            var request = Assert.Single(RenderGraph.Build(project, Options(rasterizer)).TextImages).Value;
            Assert.Equal(("", (string)null), (request.Text, request.ArtTextColor));
            Assert.Equal(key, Key());
        }

        // ---------- 손잡이 ----------

        [Fact]
        public void Handles_TailAndCornerRadius_ForStage2Kinds()
        {
            Assert.Equal(new[] { ArtHandle.Tail }, ArtHandles.For(ArtKind.Bubble));
            Assert.Equal(new[] { ArtHandle.CornerRadius, ArtHandle.Tail }, ArtHandles.For(ArtKind.BubbleSquare));
            Assert.Equal(new[] { ArtHandle.Tail }, ArtHandles.For(ArtKind.BubbleThought));
            foreach (var kind in new[] { ArtKind.BoxMark, ArtKind.ScreenBorder, ArtKind.Plate })
                Assert.Equal(new[] { ArtHandle.CornerRadius }, ArtHandles.For(kind));
            foreach (var kind in new[] { ArtKind.CircleMark, ArtKind.Underline, ArtKind.Highlighter, ArtKind.Sparkle, ArtKind.FocusLines, ArtKind.LowerThird })
                Assert.Empty(ArtHandles.For(kind));

            var project = TestProjects.Empty(magnetic: false);
            project.Width = 1920;
            project.Height = 1080;
            var bubble = TimelineEdits.CreateArt(ArtKind.Bubble, 0, 30);
            bubble.Scale = 2;
            TimelineEdits.Place(project, project.MainTrack, bubble, 0);
            // 꼬리 손잡이 = 꼬리 끝, 끈 만큼(화면 거리 / 크기 배율) 옮긴다
            var tip = ArtHandles.Position(project, bubble, ArtHandle.Tail);
            var local = ArtGeometry.TailTip(bubble.Art, 1);
            var expected = ArtHandles.ToProject(project, bubble, local.X, local.Y);
            Assert.Equal(expected, tip);
            var dragged = ArtHandles.Drag(project, bubble, ArtHandle.Tail, tip, (tip.X + 96, tip.Y - 60));
            Assert.Equal(-0.22 + 48.0 / 480, dragged.Art.TailX, 9);
            Assert.Equal(0.85 - 30.0 / 300, dragged.Art.TailY, 9);
            Assert.Equal((0.0, 0.0), (dragged.X, dragged.Y));
            // 아주 멀리 끌어도 상자의 ±4배까지
            dragged = ArtHandles.Drag(project, bubble, ArtHandle.Tail, tip, (tip.X - 1e6, tip.Y + 1e6));
            Assert.Equal((-ArtShape.MaxTail, ArtShape.MaxTail), (dragged.Art.TailX, dragged.Art.TailY));

            // 화면 테두리: 반경 손잡이는 안쪽 모서리(두께만큼 안쪽)에서, 상한은 안쪽 구멍 짧은 변의 절반
            var border = TimelineEdits.AddArt(project, ArtKind.ScreenBorder, 0, 30);
            Assert.Equal((24.0 + 40, 24.0 + 40), ArtHandles.Local(border.Art, ArtHandle.CornerRadius));
            var at = ArtHandles.Position(project, border, ArtHandle.CornerRadius);
            var limit = ArtHandles.Drag(project, border, ArtHandle.CornerRadius, at, (at.X + 5000, at.Y + 5000)).Art.CornerRadius;
            Assert.Equal((1080 - 48) / 2.0, limit);
        }
    }
}
