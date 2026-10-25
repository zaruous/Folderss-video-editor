using System;
using System.Linq;
using System.Text.Json.Nodes;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>글자 상자(너비·높이·세로 정렬): 프로젝트 파일, 값 검증, 나누기·복사, 렌더 그래프 경로, 미리 보기 틀 계산(옆 끌기·고르기).</summary>
    public class TextBoxTests
    {
        private static (Project Project, Clip Text) TextProject(string text = "가나다 ABC")
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateText(text, 0, 30);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static RenderOptions Options(ITextRasterizer rasterizer, int width = 640, int height = 360)
        {
            return new RenderOptions { From = 0, To = 30, Width = width, Height = height, Audio = false, FontFile = @"C:\Windows\Fonts\malgun.ttf", TempFolder = @"C:\Temp", TextRasterizer = rasterizer };
        }

        private static string Filter(Project project, RenderOptions options)
        {
            return System.Text.RegularExpressions.Regex.Replace(RenderGraph.Build(project, options).Filter, "fv-text-[0-9a-f]+", "fv-text-*");
        }

        // ---------- 프로젝트 파일 ----------

        [Fact]
        public void ProjectFile_OmitsDefaults_AndRoundTripsValues()
        {
            var (project, clip) = TextProject();
            // 기본값(자동·가운데)은 저장하지 않는다 — 예전 프로젝트 파일과 형식 버전이 그대로
            var json = ProjectFile.Snapshot(project);
            Assert.DoesNotContain("TextBoxWidth", json);
            Assert.DoesNotContain("TextBoxHeight", json);
            Assert.DoesNotContain("TextVerticalAlign", json);
            Assert.Equal(2, Project.CurrentVersion);

            clip.TextBoxWidth = 420;
            clip.TextBoxHeight = 180.5;
            clip.TextVerticalAlign = TextVerticalAlign.Bottom;
            json = ProjectFile.Snapshot(project);
            var node = JsonNode.Parse(json)["VideoTracks"][0]["Clips"][0];
            Assert.Equal(420, (double)node["TextBoxWidth"]);
            Assert.Equal(180.5, (double)node["TextBoxHeight"]);
            Assert.Equal("Bottom", (string)node["TextVerticalAlign"]);
            var read = ProjectFile.FromSnapshot(json).MainTrack.Clips[0];
            Assert.Equal((420.0, 180.5, TextVerticalAlign.Bottom), (read.TextBoxWidth, read.TextBoxHeight, read.TextVerticalAlign));

            // 높이만 정해도 따로 저장된다
            clip.TextBoxWidth = 0;
            clip.TextVerticalAlign = TextVerticalAlign.Top;
            json = ProjectFile.Snapshot(project);
            Assert.DoesNotContain("TextBoxWidth", json);
            read = ProjectFile.FromSnapshot(json).MainTrack.Clips[0];
            Assert.Equal((0.0, 180.5, TextVerticalAlign.Top), (read.TextBoxWidth, read.TextBoxHeight, read.TextVerticalAlign));
        }

        [Fact]
        public void ProjectFile_RepairsHostileValues()
        {
            var (project, clip) = TextProject();
            var photo = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 40, 30);
            TimelineEdits.Place(project, project.MainTrack, photo, 40);
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var text = json["VideoTracks"][0]["Clips"][0];
            var media = json["VideoTracks"][0]["Clips"][1];
            Clip Read() => ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0];

            // 음수는 자동, 너무 큰 값은 상한까지
            text["TextBoxWidth"] = -5;
            text["TextBoxHeight"] = 1e308;
            Assert.Equal((0.0, ClipLayout.MaxTextBox), (Read().TextBoxWidth, Read().TextBoxHeight));
            // 모르는 이름·정의되지 않은 숫자·숫자 글·다른 형식은 가운데, 맞는 값은 그 값
            foreach (var (value, expected) in new (JsonNode, TextVerticalAlign)[]
            {
                ("Banana", TextVerticalAlign.Middle), (99, TextVerticalAlign.Middle), ("1", TextVerticalAlign.Middle), (JsonNode.Parse("{}"), TextVerticalAlign.Middle),
                ("Top", TextVerticalAlign.Top), (2, TextVerticalAlign.Bottom)
            })
            {
                text["TextVerticalAlign"] = value;
                Assert.Equal(expected, Read().TextVerticalAlign);
            }
            // 글자가 아닌 클립에는 상자가 없다
            media["TextBoxWidth"] = 300;
            media["TextBoxHeight"] = 200;
            media["TextVerticalAlign"] = "Top";
            var repaired = ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[1];
            Assert.Equal((0.0, 0.0, TextVerticalAlign.Middle), (repaired.TextBoxWidth, repaired.TextBoxHeight, repaired.TextVerticalAlign));

            // 메모리 안에서 잘못 된 열거 값도 실행 취소 스냅샷을 거치면 가운데
            clip.TextVerticalAlign = (TextVerticalAlign)7;
            Assert.Equal(TextVerticalAlign.Middle, ProjectFile.FromSnapshot(ProjectFile.Snapshot(project)).MainTrack.Clips[0].TextVerticalAlign);
        }

        [Fact]
        public void TextBoxLength_NotFiniteOrNotPositiveIsAuto_AndCapped()
        {
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0 })
                Assert.Equal(0, ClipLayout.TextBoxLength(value));
            Assert.Equal(300, ClipLayout.TextBoxLength(300));
            Assert.Equal(ClipLayout.MaxTextBox, ClipLayout.TextBoxLength(20000));
        }

        [Fact]
        public void SplitCopyAndStyleChange_KeepTheBox()
        {
            var (project, clip) = TextProject();
            clip.TextBoxWidth = 500;
            clip.TextBoxHeight = 240;
            clip.TextVerticalAlign = TextVerticalAlign.Top;
            var right = Assert.Single(TimelineEdits.Split(project, new[] { clip }, 10));
            Assert.Equal((500.0, 240.0, TextVerticalAlign.Top), (right.TextBoxWidth, right.TextBoxHeight, right.TextVerticalAlign));
            Assert.Equal((500.0, 240.0, TextVerticalAlign.Top), (clip.TextBoxWidth, clip.TextBoxHeight, clip.TextVerticalAlign));
            var copy = clip.CloneWithNewId();
            Assert.Equal((500.0, 240.0, TextVerticalAlign.Top), (copy.TextBoxWidth, copy.TextBoxHeight, copy.TextVerticalAlign));
            // 스타일을 통째로 바꿔도(스타일 모음 적용과 같은 대입) 상자는 클립의 값이라 남는다
            clip.Style = TextStylePresets.All[1].Style.Clone();
            Assert.Equal((500.0, 240.0, TextVerticalAlign.Top), (clip.TextBoxWidth, clip.TextBoxHeight, clip.TextVerticalAlign));
        }

        // ---------- 렌더 그래프 ----------

        [Fact]
        public void StylelessTextWithBox_UsesRasterizer_WithLegacyStyle()
        {
            var (project, clip) = TextProject();
            clip.TextColor = "#FACC15";
            clip.TextBoxWidth = 300;
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer()));
            var request = Assert.Single(plan.TextImages).Value;
            Assert.DoesNotContain("drawtext", plan.Filter);
            // 처음 꾸밀 때와 같은 스타일(왼쪽 맞춤, 글자 색 채우기) — 클립에는 스타일을 만들지 않는다
            Assert.Equal(TextAlign.Left, request.Style.Align);
            Assert.Equal(new[] { "#FACC15" }, request.Style.Fill.Colors);
            Assert.Null(clip.Style);
            Assert.Equal(300, request.WrapWidth, 6);
            Assert.Equal(0, request.BoxHeight);

            // 높이만 정해도 그리개로
            clip.TextBoxWidth = 0;
            clip.TextBoxHeight = 200;
            clip.TextVerticalAlign = TextVerticalAlign.Bottom;
            request = Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer())).TextImages).Value;
            Assert.Equal((0.0, 200.0, TextVerticalAlign.Bottom), (request.WrapWidth, request.BoxHeight, request.VerticalAlign));
        }

        [Fact]
        public void BoxRouting_ZeroBoxNoRasterizerOrLongText_KeepsDrawtext()
        {
            var (project, clip) = TextProject();
            var expected = Filter(project, Options(null));
            Assert.Contains("drawtext", expected);
            // 상자 0/0·스타일 없음: 그리개가 있어도 예전 그래프 그대로
            Assert.Empty(RenderGraph.Build(project, Options(new FakeTextRasterizer())).TextImages);
            Assert.Equal(expected, Filter(project, Options(new FakeTextRasterizer())));
            // 그리개가 없으면 상자가 있어도 drawtext(상자는 무시)
            clip.TextBoxWidth = 300;
            clip.TextBoxHeight = 200;
            Assert.Equal(expected, Filter(project, Options(null)));
            // 긴 글(drawtext 대체 경로)도 상자를 무시한다
            clip.Text = new string('가', RenderGraph.MaxStyledTextLength);
            Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer())).TextImages);
            clip.Text += "나";
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer()));
            Assert.Empty(plan.TextImages);
            Assert.Contains("drawtext", plan.Filter);
        }

        [Fact]
        public void Request_BoxScalesWithOutput_AndAlignOnlyCountsWithHeight()
        {
            var (project, clip) = TextProject();
            clip.Style = TextStyle.FromLegacy(clip);
            clip.FontSize = 80;
            clip.TextBoxWidth = 400;
            clip.TextBoxHeight = 0;
            clip.TextVerticalAlign = TextVerticalAlign.Top;
            // 출력 320×180 = 프로젝트(640×360)의 절반 — 글자 크기처럼 상자도 절반
            var request = Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer(), 320, 180)).TextImages).Value;
            Assert.Equal(40, request.FontPixelSize, 6);
            Assert.Equal(200, request.WrapWidth, 6);
            // 높이가 자동이면 세로 자리는 그림과 무관 — 기본값으로 넘겨 같은 그림이 같은 캐시 파일이 되게
            Assert.Equal(TextVerticalAlign.Middle, request.VerticalAlign);
            clip.TextBoxHeight = 100;
            request = Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer(), 320, 180)).TextImages).Value;
            Assert.Equal(50, request.BoxHeight, 6);
            Assert.Equal(TextVerticalAlign.Top, request.VerticalAlign);

            // 화소 상한을 넘는 캔버스(8K)는 줄여 그린다 — 상자도 같은 비율로
            project.Width = 8192;
            project.Height = 8192;
            request = Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer(), 8192, 8192)).TextImages).Value;
            var shrink = (double)request.CanvasWidth / 8192;
            Assert.True(shrink < 1);
            Assert.Equal(400 * shrink, request.WrapWidth, 3);
            Assert.Equal(100 * shrink, request.BoxHeight, 3);
            Assert.Equal(80 * shrink, request.FontPixelSize, 3);
        }

        [Fact]
        public void Key_DiffersByWrapWidthBoxHeightAndAlign()
        {
            TextImageRequest Request()
            {
                var style = new TextStyle();
                style.Normalize();
                return new TextImageRequest { Text = "가나 AB", Style = style, FontPixelSize = 72, EffectScale = 1, CanvasWidth = 1920, CanvasHeight = 1080, BoxHeight = 300 };
            }
            var keys = new System.Collections.Generic.HashSet<string> { Request().Key(1) };
            foreach (var edit in new Action<TextImageRequest>[] { r => r.WrapWidth = 500, r => r.WrapWidth = 501, r => r.BoxHeight = 301, r => r.VerticalAlign = TextVerticalAlign.Top, r => r.VerticalAlign = TextVerticalAlign.Bottom })
            {
                var request = Request();
                edit(request);
                Assert.True(keys.Add(request.Key(1)), "키가 겹침: " + keys.Count);
            }
            Assert.Equal(Request().Key(1), Request().Key(1));
        }

        // ---------- 틀 계산 ----------

        /// <summary>옆 하나의 가운데(프로젝트 픽셀): 상자 가운데에서 클립 축으로 반 너비·반 높이.</summary>
        private static (double X, double Y) EdgeMiddle(BoxEdge edge, double centerX, double centerY, double width, double height, double scale, double rotation)
        {
            var radians = rotation * Math.PI / 180;
            var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
            var (lx, ly) = edge switch
            {
                BoxEdge.Right => (width * scale / 2, 0.0),
                BoxEdge.Left => (-width * scale / 2, 0.0),
                BoxEdge.Bottom => (0.0, height * scale / 2),
                _ => (0.0, -height * scale / 2)
            };
            return (centerX + lx * cos - ly * sin, centerY + lx * sin + ly * cos);
        }

        private static BoxEdge Opposite(BoxEdge edge)
        {
            return (BoxEdge)(((int)edge + 2) % 4);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(90, 1)]
        [InlineData(37, 1)]
        [InlineData(0, 0.5)]
        [InlineData(37, 1.75)]
        [InlineData(-120, 2)]
        public void DragEdge_KeepsTheOppositeEdge(double rotation, double scale)
        {
            const double x = 30, y = -20, width = 400, height = 150;
            foreach (var edge in new[] { BoxEdge.Top, BoxEdge.Right, BoxEdge.Bottom, BoxEdge.Left })
            {
                foreach (var (dx, dy) in new[] { (40.0, 15.0), (-25.0, 60.0), (-70.0, -30.0) })
                {
                    var moved = ClipLayout.DragEdge(edge, x, y, width, height, scale, rotation, dx, dy, 10, 10);
                    var fixedBefore = EdgeMiddle(Opposite(edge), x, y, width, height, scale, rotation);
                    var fixedAfter = EdgeMiddle(Opposite(edge), moved.X, moved.Y, moved.Width, moved.Height, scale, rotation);
                    Assert.Equal(fixedBefore.X, fixedAfter.X, 6);
                    Assert.Equal(fixedBefore.Y, fixedAfter.Y, 6);
                    // 끈 옆은 끈 거리를 클립 축에 투영한 만큼 움직이고, 다른 방향 크기는 그대로
                    var radians = rotation * Math.PI / 180;
                    var horizontal = edge == BoxEdge.Left || edge == BoxEdge.Right;
                    var (ax, ay) = horizontal ? (Math.Cos(radians), Math.Sin(radians)) : (-Math.Sin(radians), Math.Cos(radians));
                    var along = dx * ax + dy * ay;
                    var draggedBefore = EdgeMiddle(edge, x, y, width, height, scale, rotation);
                    var draggedAfter = EdgeMiddle(edge, moved.X, moved.Y, moved.Width, moved.Height, scale, rotation);
                    Assert.Equal(along, (draggedAfter.X - draggedBefore.X) * ax + (draggedAfter.Y - draggedBefore.Y) * ay, 6);
                    var sign = edge == BoxEdge.Right || edge == BoxEdge.Bottom ? 1 : -1;
                    if (horizontal)
                    {
                        Assert.Equal(width + sign * along / scale, moved.Width, 6);
                        Assert.Equal(height, moved.Height);
                    }
                    else
                    {
                        Assert.Equal(height + sign * along / scale, moved.Height, 6);
                        Assert.Equal(width, moved.Width);
                    }
                }
            }
        }

        [Fact]
        public void DragEdge_RightEdgeAtZeroDegrees_MovesCenterByHalf()
        {
            // 오른쪽 옆을 안쪽으로 100 끌면 너비 -100, 가운데는 왼쪽으로 50(왼쪽 옆 그대로). 크기 배율 2면 상자는 50만 줄고 가운데는 50
            Assert.Equal((-50.0, 0.0, 300.0, 100.0), ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 400, 100, 1, 0, -100, 37, 10, 10));
            Assert.Equal((-50.0, 0.0, 350.0, 100.0), ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 400, 100, 2, 0, -100, 0, 10, 10));
            // 90°: 클립의 가로축이 화면 아래 — 아래로 끌면 너비가 늘고 가운데가 아래로
            var turned = ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 400, 100, 1, 90, 0, 60, 10, 10);
            Assert.Equal(460, turned.Width, 6);
            Assert.Equal(0, turned.X, 6);
            Assert.Equal(30, turned.Y, 6);
            // 위 옆을 위로 끌면 높이가 늘고 가운데가 위로(아래 옆 그대로)
            Assert.Equal((0.0, -20.0, 400.0, 140.0), ClipLayout.DragEdge(BoxEdge.Top, 0, 0, 400, 100, 1, 0, 5, -40, 10, 10));
        }

        [Fact]
        public void DragEdge_StopsAtMinimumAndCap_NeverNegative()
        {
            // 한 글자(72)보다 좁아지지 않는다 — 그만큼만 움직이고 왼쪽 옆은 그대로
            var narrow = ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 400, 100, 1, 0, -1000, 0, 72, 86.4);
            Assert.Equal(72, narrow.Width, 6);
            Assert.Equal(-164, narrow.X, 6);
            // 한 줄보다 낮아지지 않는다
            Assert.Equal(86.4, ClipLayout.DragEdge(BoxEdge.Bottom, 0, 0, 400, 300, 1, 0, 0, -1000, 72, 86.4).Height, 6);
            // 시작 크기가 이미 최소보다 작으면(좁은 글 하나) 그 크기에서 멈추고 갑자기 커지지 않는다, 그래도 1 아래로는 안 간다
            Assert.Equal(30, ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 30, 100, 1, 0, -500, 0, 72, 86.4).Width, 6);
            Assert.Equal(31, ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 30, 100, 1, 0, 1, 0, 72, 86.4).Width, 6);
            Assert.Equal(1, ClipLayout.DragEdge(BoxEdge.Right, 0, 0, 0, 100, 1, 0, -500, 0, 72, 86.4).Width, 6);
            // 상한
            Assert.Equal(ClipLayout.MaxTextBox, ClipLayout.DragEdge(BoxEdge.Left, 0, 0, 400, 100, 0.01, 0, -1e6, 0, 72, 86.4).Width);
            // 잘못된 값(NaN)은 움직이지 않은 것으로
            var nan = ClipLayout.DragEdge(BoxEdge.Left, 5, 6, 400, 100, double.NaN, double.NaN, double.NaN, double.NaN, 72, 86.4);
            Assert.Equal((5.0, 6.0, 400.0, 100.0), nan);
        }

        [Fact]
        public void MinTextBox_IsOneCharacterAndOneLine()
        {
            var (_, clip) = TextProject();
            clip.FontSize = 72;
            var min = ClipLayout.MinTextBox(clip);
            Assert.Equal(72, min.Width, 6);
            Assert.Equal(86.4, min.Height, 6);
            clip.Style = new TextStyle { LineSpacing = 2 };
            Assert.Equal(144, ClipLayout.MinTextBox(clip).Height, 6);
            clip.Style.LineSpacing = double.NaN;
            clip.FontSize = double.NaN;
            Assert.Equal((72.0, 72 * 1.2), ClipLayout.MinTextBox(clip));
        }

        [Fact]
        public void Contains_RotatedBox()
        {
            // 200×50 상자를 90° 돌리면 세로로 길다
            var box = (100.0, 100.0, 200.0, 50.0, 90.0);
            Assert.True(ClipLayout.Contains(box, 100, 190));
            Assert.False(ClipLayout.Contains(box, 190, 100));
            Assert.True(ClipLayout.Contains(box, 120, 100));
            Assert.False(ClipLayout.Contains(box, 130, 100));
            // 37°: 가로축(cos, sin) 방향으로 95는 안, 105는 밖. 세로축(-sin, cos)으로 24는 안, 26은 밖
            var tilted = (0.0, 0.0, 200.0, 50.0, 37.0);
            var r = 37 * Math.PI / 180;
            Assert.True(ClipLayout.Contains(tilted, 95 * Math.Cos(r), 95 * Math.Sin(r)));
            Assert.False(ClipLayout.Contains(tilted, 105 * Math.Cos(r), 105 * Math.Sin(r)));
            Assert.True(ClipLayout.Contains(tilted, -24 * Math.Sin(r), 24 * Math.Cos(r)));
            Assert.False(ClipLayout.Contains(tilted, -26 * Math.Sin(r), 26 * Math.Cos(r)));
            // 돌리지 않은 같은 자리(축에 맞춘 상자라면 안)는 돌린 상자 밖
            Assert.False(ClipLayout.Contains(tilted, 95, 20));
        }

        [Fact]
        public void TextBox_IsCenterPlusOffset_TimesScale()
        {
            var (project, clip) = TextProject();
            clip.X = 50;
            clip.Y = -30;
            clip.Scale = 0.5;
            clip.Rotation = 12;
            Assert.Equal((370.0, 150.0, 150.0, 40.0, 12.0), ClipLayout.TextBox(project, clip, 300, 80));
        }

        [Fact]
        public void HitTest_TopmostVisibleUnlocked_TextByBox()
        {
            var project = TestProjects.Empty(magnetic: false);
            var media = TestProjects.Image();
            project.Media.Add(media);
            // 맨 아래: 화면을 다 덮는 사진(채우기), 그 위 트랙: 가운데 글자(상자 200×100), 그 위: 자막 트랙 큐
            var photo = TimelineEdits.CreateClip(media, project.Rate, 0, 60);
            photo.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, photo, 0);
            var textTrack = TimelineEdits.AddTrack(project, TrackKind.Video);
            var text = TimelineEdits.CreateText("제목", 0, 60);
            TimelineEdits.Place(project, textTrack, text, 0);
            var subtitles = TimelineEdits.AddTrack(project, TrackKind.Video);
            subtitles.Subtitles = true;
            var cue = TimelineEdits.CreateText("자막", 0, 60);
            subtitles.Clips.Add(cue);
            var measured = 0;
            (double, double) Box(Clip c)
            {
                measured++;
                return (200, 100);
            }

            // 화면 가운데: 글자(자막 트랙은 건너뜀), 글자 상자 밖: 사진
            Assert.Same(text, ClipLayout.HitTest(project, 10, 320, 180, Box));
            Assert.Same(text, ClipLayout.HitTest(project, 10, 419, 229, Box));
            Assert.Same(photo, ClipLayout.HitTest(project, 10, 421, 180, Box));
            Assert.Same(photo, ClipLayout.HitTest(project, 10, 5, 5, Box));
            Assert.True(measured > 0);
            // 화면 밖·재생 헤드에 없는 클립은 없다
            Assert.Null(ClipLayout.HitTest(project, 10, -5, 5, Box));
            Assert.Null(ClipLayout.HitTest(project, 100, 320, 180, Box));
            // 글자를 옮기고 돌리고 줄이면 그 상자로
            text.X = 200;
            text.Rotation = 90;
            text.Scale = 0.5;
            Assert.Same(photo, ClipLayout.HitTest(project, 10, 320, 180, Box));
            Assert.Same(text, ClipLayout.HitTest(project, 10, 520, 180 + 45, Box));
            Assert.Same(photo, ClipLayout.HitTest(project, 10, 520 + 45, 180, Box));
            // 잠긴·숨긴 트랙은 건너뛰어 아래 클립을 고른다
            text.X = 0;
            text.Rotation = 0;
            text.Scale = 1;
            textTrack.Locked = true;
            Assert.Same(photo, ClipLayout.HitTest(project, 10, 320, 180, Box));
            textTrack.Locked = false;
            textTrack.Hidden = true;
            Assert.Same(photo, ClipLayout.HitTest(project, 10, 320, 180, Box));
            project.MainTrack.Hidden = true;
            Assert.Null(ClipLayout.HitTest(project, 10, 320, 180, Box));
        }
    }
}
