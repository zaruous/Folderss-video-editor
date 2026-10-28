using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>#40 아트 요소 3단계: 스티커 SVG 읽기(제한된 SVG — 믿을 수 없는 입력), 기본 스티커 모음과 출처·라이선스, 내 스티커 이름·키, 스티커 클립.</summary>
    public class StickerTests
    {
        private const string Head = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">";

        private static Sticker Read(string body, string head = Head)
        {
            var parse = StickerSvg.Parse(head + body + "</svg>");
            Assert.True(parse.Sticker != null, parse.Error);
            return parse.Sticker;
        }

        private static StickerParse Fail(string svg)
        {
            var parse = StickerSvg.Parse(svg);
            Assert.Null(parse.Sticker);
            Assert.False(string.IsNullOrEmpty(parse.Error));
            return parse;
        }

        private static (double X, double Y) End(StickerSegment s)
        {
            return (Math.Round(s.X, 6) + 0.0, Math.Round(s.Y, 6) + 0.0);
        }

        /// <summary>3차 베지어의 t = 0.5 점.</summary>
        private static (double X, double Y) Middle((double X, double Y) from, StickerSegment s)
        {
            return (0.125 * from.X + 0.375 * s.X1 + 0.375 * s.X2 + 0.125 * s.X, 0.125 * from.Y + 0.375 * s.Y1 + 0.375 * s.Y2 + 0.125 * s.Y);
        }

        private static string StickerFolder { get { return Path.Combine(AppContext.BaseDirectory, "stickers"); } }

        // ---------- 기본 스티커 모음 ----------

        [Fact]
        public void Builtin_EveryFileReads_Cleanly_AndIsListedAsSelfMadeMit()
        {
            var files = Directory.GetFiles(StickerFolder, "*.svg").Select(Path.GetFileName).OrderBy(f => f).ToList();
            Assert.True(files.Count >= 16, "기본 스티커 " + files.Count + "개");
            var licenses = File.ReadAllText(Path.Combine(StickerFolder, "LICENSES.md"));
            // 표의 줄: | 파일 | 이름 | 출처 | 라이선스 |
            var rows = licenses.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("| ") && l.Contains(".svg"))
                .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray()).ToDictionary(c => c[0], c => c);
            foreach (var file in files)
            {
                var parse = StickerSvg.Parse(File.ReadAllText(Path.Combine(StickerFolder, file)));
                Assert.True(parse.Sticker != null, file + ": " + parse.Error);
                Assert.Empty(parse.Ignored);
                Assert.Equal((0.0, 0.0, 128.0, 128.0), (parse.Sticker.MinX, parse.Sticker.MinY, parse.Sticker.Width, parse.Sticker.Height));
                Assert.False(string.IsNullOrWhiteSpace(parse.Sticker.Title), file + " 제목");
                Assert.True(rows.ContainsKey(file), file + "이 LICENSES.md 표에 없다");
                Assert.Equal(parse.Sticker.Title, rows[file][1]);
                Assert.Equal("직접 제작", rows[file][2]);
                Assert.Equal("MIT", rows[file][3]);
                // 다시 쓴 깨끗한 SVG도 같은 그림
                var again = StickerSvg.Parse(StickerSvg.Write(parse.Sticker));
                Assert.Equal(parse.Sticker.Paths.Count, again.Sticker.Paths.Count);
            }
            // 표에만 있고 파일이 없는 줄도 없다
            Assert.All(rows.Keys, name => Assert.Contains(name, files));
        }

        // ---------- 경로·도형·칠하기 ----------

        [Fact]
        public void PathData_AllCommands_RelativeImplicitAndCompactNumbers()
        {
            var sticker = Read("<path d=\"M10 10 L20 10 l0 10 H5 h5 V30 v5 Z m30,0 50-5 1.5.5 1e1-2E0\"/>");
            var path = Assert.Single(sticker.Paths);
            Assert.Equal(2, path.Figures.Count);
            var first = path.Figures[0];
            Assert.Equal((10.0, 10.0), (first.StartX, first.StartY));
            Assert.Equal(new[] { (20.0, 10.0), (20.0, 20.0), (5.0, 20.0), (10.0, 20.0), (10.0, 30.0), (10.0, 35.0) }, first.Segments.Select(End));
            Assert.True(first.Closed);
            // Z 뒤의 m은 그림 시작점(10, 10) 기준, 이어진 쌍은 l: (40,10) → (90,5) → (91.5,5.5) → (101.5,3.5)
            var second = path.Figures[1];
            Assert.Equal((40.0, 10.0), (second.StartX, second.StartY));
            Assert.Equal(new[] { (90.0, 5.0), (91.5, 5.5), (101.5, 3.5) }, second.Segments.Select(End));
            Assert.Equal("#FF000000", path.Fill);
        }

        [Fact]
        public void Curves_SmoothAndQuadratic_BecomeCubics()
        {
            var path = Assert.Single(Read("<path d=\"M0 0 C10 0 20 10 20 20 S30 40 40 40 Q50 40 50 50 T60 60\" fill=\"none\" stroke=\"red\"/>").Paths);
            var s = path.Figures[0].Segments;
            Assert.All(s, x => Assert.True(x.Cubic));
            // S: 첫 조절점 = 앞 조각 둘째 조절점(20,10)을 끝(20,20)에 비춘 (20,30)
            Assert.Equal((20.0, 30.0), (s[1].X1, s[1].Y1));
            // Q(50,40)→(50,50): 3차 조절점은 2/3 지점
            Assert.Equal(46.666667, s[2].X1, 5);
            Assert.Equal(40, s[2].Y1, 9);
            // T: 앞 2차 조절점(50,40)을 (50,50)에 비춘 (50,60) — 3차 첫 조절점 = (50,50) + 2/3 × ((50,60) − (50,50))
            Assert.Equal((50.0, 56.666667), (Math.Round(s[3].X1, 6), Math.Round(s[3].Y1, 6)));
            Assert.Equal((60.0, 60.0), End(s[3]));
            Assert.Null(path.Fill);
            Assert.Equal("#FFFF0000", path.Stroke);
        }

        [Fact]
        public void Arc_EndsExactly_PassesThroughTheCircle_CompactFlags()
        {
            // 반지름 10, (0,10) → (20,10) 위쪽 반원(sweep 1): 가운데 (10,10), 꼭대기 (10,0)
            var figure = Assert.Single(Read("<path d=\"M0 10 a10 10 0 01 20 0\"/>").Paths).Figures[0];
            Assert.Equal(2, figure.Segments.Count);
            Assert.Equal((10.0, 0.0), End(figure.Segments[0]));
            Assert.Equal((20.0, 10.0), End(figure.Segments[1]));
            var middle = Middle((0, 10), figure.Segments[0]);
            Assert.Equal(10, Math.Sqrt(Math.Pow(middle.X - 10, 2) + Math.Pow(middle.Y - 10, 2)), 2);
            // 반지름이 모자라면 키운다(지름 20인 두 점 사이에 반지름 1)
            var grown = Assert.Single(Read("<path d=\"M0 0 A1 1 0 0 0 20 0\"/>").Paths).Figures[0];
            Assert.Equal((20.0, 0.0), End(grown.Segments.Last()));
            // 반지름 0은 직선
            var flat = Assert.Single(Read("<path d=\"M0 0 A0 5 0 0 0 20 0\"/>").Paths).Figures[0];
            Assert.False(Assert.Single(flat.Segments).Cubic);
        }

        [Fact]
        public void Shapes_RectCircleEllipseLinePolylinePolygon()
        {
            var sticker = Read("<rect x=\"10\" y=\"20\" width=\"30\" height=\"40\"/><rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" rx=\"5\"/>"
                               + "<circle cx=\"50\" cy=\"50\" r=\"10\"/><ellipse cx=\"50\" cy=\"50\" rx=\"20\" ry=\"5\"/>"
                               + "<line x1=\"0\" y1=\"0\" x2=\"10\" y2=\"10\" stroke=\"black\"/><polyline points=\"0,0 10,0 10,10\" fill=\"none\" stroke=\"#000\"/>"
                               + "<polygon points=\"0 0, 10 0 5 8\"/><rect width=\"0\" height=\"5\"/><circle r=\"-1\"/>");
            Assert.Equal(7, sticker.Paths.Count);
            Assert.Equal(new[] { (40.0, 20.0), (40.0, 60.0), (10.0, 60.0), (10.0, 20.0) }, sticker.Paths[0].Figures[0].Segments.Select(End));
            Assert.Contains(sticker.Paths[1].Figures[0].Segments, s => s.Cubic);
            Assert.Equal(4, sticker.Paths[2].Figures[0].Segments.Count(s => s.Cubic));
            Assert.Equal((60.0, 50.0), (sticker.Paths[2].Figures[0].StartX, sticker.Paths[2].Figures[0].StartY));
            // 선은 채우지 않고, 열린 그림
            Assert.Null(sticker.Paths[4].Fill);
            Assert.False(sticker.Paths[4].Figures[0].Closed);
            Assert.False(sticker.Paths[5].Figures[0].Closed);
            Assert.True(sticker.Paths[6].Figures[0].Closed);
        }

        [Fact]
        public void Transforms_Compose_GroupsInheritPaint_StyleOverridesAttributes()
        {
            var sticker = Read("<g fill=\"#00ff00\" transform=\"translate(10 20) scale(2)\" opacity=\"0.5\">"
                               + "<rect width=\"5\" height=\"5\"/>"
                               + "<rect width=\"5\" height=\"5\" fill=\"blue\" style=\"fill: #f00; fill-opacity:50%\" transform=\"rotate(90)\"/>"
                               + "<g transform=\"matrix(1 0 0 1 3 4)\" stroke=\"rgb(0, 0, 255)\" stroke-width=\"2\"><path d=\"M0 0 L1 0\"/></g></g>");
            var plain = sticker.Paths[0];
            // translate(10,20) ∘ scale(2): (5,0) → (20,20), 불투명도 0.5는 알파로
            Assert.Equal((20.0, 20.0), End(plain.Figures[0].Segments[0]));
            Assert.Equal("#8000FF00", plain.Fill);
            // style이 fill 속성을 이기고, 불투명도는 곱한다(0.5 × 0.5)
            var turned = sticker.Paths[1];
            Assert.Equal("#40FF0000", turned.Fill);
            // rotate(90): (5,0) → (0,5) → ×2 + (10,20) = (10,30)
            Assert.Equal((10.0, 30.0), End(turned.Figures[0].Segments[0]));
            var nested = sticker.Paths[2];
            Assert.Equal((16.0, 28.0), (nested.Figures[0].StartX, nested.Figures[0].StartY));
            Assert.Equal("#800000FF", nested.Stroke);
            // 선 두께도 배율만큼(2 × 2)
            Assert.Equal(4, nested.StrokeWidth, 9);
            // rotate(각 cx cy)·skewX
            var pivot = Read("<path d=\"M10 0 L20 0\" transform=\"rotate(180 15 0)\"/><path d=\"M0 10 L0 20\" transform=\"skewX(45)\"/>");
            Assert.Equal((20.0, 0.0), (Math.Round(pivot.Paths[0].Figures[0].StartX, 6), Math.Round(pivot.Paths[0].Figures[0].StartY, 6) + 0.0));
            Assert.Equal((10.0, 10.0), (Math.Round(pivot.Paths[1].Figures[0].StartX, 6), pivot.Paths[1].Figures[0].StartY));
        }

        [Fact]
        public void Colors_Forms_CurrentColor_Hidden_UrlFallback()
        {
            var sticker = Read("<g color=\"#123456\"><rect width=\"1\" height=\"1\" fill=\"#abc\"/><rect width=\"1\" height=\"1\" fill=\"#11223380\"/>"
                               + "<rect width=\"1\" height=\"1\" fill=\"rgba(255,0,0,0.5)\"/><rect width=\"1\" height=\"1\" fill=\"gold\"/>"
                               + "<rect width=\"1\" height=\"1\" fill=\"currentColor\"/><rect width=\"1\" height=\"1\" fill=\"url(#g) #00FF00\"/>"
                               + "<rect width=\"1\" height=\"1\" fill=\"url(#g)\"/><rect width=\"1\" height=\"1\" visibility=\"hidden\"/>"
                               + "<rect width=\"1\" height=\"1\" display=\"none\"/><rect width=\"1\" height=\"1\" fill=\"none\"/>"
                               + "<rect width=\"1\" height=\"1\" fill-rule=\"evenodd\" fill=\"#ff0000\" fill-opacity=\"0\"/></g>");
            Assert.Equal(new[] { "#FFAABBCC", "#80112233", "#80FF0000", "#FFFFD700", "#FF123456", "#FF00FF00", "#FF808080" }, sticker.Paths.Select(p => p.Fill));
            // url() 칠하기는 대체 색을 쓰고 알린다
            Assert.Contains("url()", StickerSvg.Parse(Head + "<rect width=\"1\" height=\"1\" fill=\"url(#g)\"/></svg>").Ignored);
        }

        [Fact]
        public void Title_IsTheName_TrimmedAndTagFree()
        {
            Assert.Equal("웃는 얼굴", Read("<title> 웃는 <b>얼굴</b> </title><rect width=\"1\" height=\"1\"/>").Title);
            Assert.Equal(new string('가', StickerSvg.MaxTitle), Read("<title>" + new string('가', 60) + "</title><rect width=\"1\" height=\"1\"/>").Title);
            // 크기만 있고 viewBox가 없으면 0 0 width height(단위는 무시)
            var sized = Read("<rect width=\"1\" height=\"1\"/>", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64px\" height=\"32\">");
            Assert.Equal((64.0, 32.0), (sized.Width, sized.Height));
        }

        // ---------- 믿을 수 없는 입력 ----------

        [Fact]
        public void ScriptsReferencesAndForeignContent_AreIgnored_OnlyPathsRemain()
        {
            var parse = StickerSvg.Parse(Head
                + "<script>alert(1)</script><style>rect{fill:red}</style>"
                + "<image href=\"https://example.invalid/x.png\" width=\"10\" height=\"10\"/>"
                + "<use xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"#a\"/>"
                + "<foreignObject><div xmlns=\"http://www.w3.org/1999/xhtml\">x</div></foreignObject>"
                + "<defs><linearGradient id=\"g\"/><path id=\"a\" d=\"M0 0 L9 9\"/></defs>"
                + "<a href=\"https://example.invalid\"><rect width=\"5\" height=\"5\"/></a>"
                + "<text x=\"0\" y=\"10\">글</text>"
                + "<path d=\"M0 0 L10 0 L10 10 Z\" href=\"https://example.invalid\" onclick=\"alert(1)\"/>"
                + "<other:thing xmlns:other=\"urn:x\"/>"
                + "</svg>");
            Assert.True(parse.Sticker != null, parse.Error);
            // 그리는 것은 마지막 경로 하나뿐(defs·a 안의 도형도 쓰지 않음)
            var path = Assert.Single(parse.Sticker.Paths);
            Assert.Equal(2, path.Figures[0].Segments.Count);
            foreach (var name in new[] { "script", "style", "image", "use", "foreignObject", "defs", "a", "text", "href", "thing" })
                Assert.Contains(name, parse.Ignored);
            // 다시 쓴 SVG에는 path만 남는다
            var clean = StickerSvg.Write(parse.Sticker);
            foreach (var word in new[] { "script", "href", "onclick", "image", "use", "style", "example" })
                Assert.DoesNotContain(word, clean);
            Assert.StartsWith("<svg ", clean);
            Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", clean);
        }

        [Fact]
        public void Entities_ExternalAndExpanding_AreRejected_WithoutReadingFiles()
        {
            // 바깥 파일을 읽는 엔티티: DTD를 읽지 않으므로 정의되지 않은 엔티티 — 읽지 못한다(파일 내용이 들어갈 수 없다)
            var secret = Path.Combine(Path.GetTempPath(), "fv-secret-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(secret, "SECRET-CONTENT");
            try
            {
                var external = Fail("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///" + secret.Replace('\\', '/') + "\">]>"
                                    + Head + "<title>&xxe;</title><rect width=\"1\" height=\"1\"/></svg>");
                Assert.DoesNotContain("SECRET-CONTENT", external.Error);
            }
            finally
            {
                File.Delete(secret);
            }
            // 기하급수로 커지는 엔티티: 곧바로 거절(풀지 않음)
            var laughs = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\">");
            for (var i = 1; i < 10; i++)
                laughs.Append("<!ENTITY a" + i + " \"" + string.Concat(Enumerable.Repeat("&a" + (i == 1 ? "" : (i - 1).ToString()) + ";", 10)) + "\">");
            laughs.Append("]>" + Head + "<title>&a9;</title><rect width=\"1\" height=\"1\"/></svg>");
            var watch = Stopwatch.StartNew();
            Fail(laughs.ToString());
            Assert.True(watch.ElapsedMilliseconds < 2000, "엔티티를 풀지 않고 거절 " + watch.ElapsedMilliseconds + "ms");
            // 엔티티를 쓰지 않는 DOCTYPE(예전 편집기가 쓰던 것)은 무시하고 읽는다
            Assert.NotNull(StickerSvg.Parse("<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">"
                                            + Head + "<rect width=\"1\" height=\"1\"/></svg>").Sticker);
        }

        [Fact]
        public void Limits_TooBigTooManyTooDeepTooFar_AreRejectedWithAReason()
        {
            Assert.Contains("256KB", Fail(Head + "<!--" + new string('x', StickerSvg.MaxChars) + "--></svg>").Error);
            Assert.Contains("요소", Fail(Head + string.Concat(Enumerable.Repeat("<g/>", StickerSvg.MaxElements + 1)) + "</svg>").Error);
            Assert.Contains("숫자", Fail(Head + "<path d=\"M0 0" + string.Concat(Enumerable.Repeat(" L1 1", StickerSvg.MaxNumbers / 2 + 1)) + "\"/></svg>").Error);
            Assert.Contains("깊게", Fail(Head + string.Concat(Enumerable.Repeat("<g>", StickerSvg.MaxDepth + 2)) + "<rect width=\"1\" height=\"1\"/>"
                                          + string.Concat(Enumerable.Repeat("</g>", StickerSvg.MaxDepth + 2)) + "</svg>").Error);
            Assert.Contains("좌표", Fail(Head + "<path d=\"M0 0 L1e7 0 L0 1\"/></svg>").Error);
            Assert.Contains("크기", Fail("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>").Error);
            Assert.Contains("그릴 모양이 없습니다", Fail(Head + "<script>x</script></svg>").Error);
            Assert.Contains("SVG 파일이 아닙니다", Fail("<html><body/></html>").Error);
            Assert.Contains("읽지 못했습니다", Fail(Head + "<rect></svg>").Error);
            Fail("");
            // 상한 바로 안쪽은 읽는다
            Assert.NotNull(StickerSvg.Parse(Head + string.Concat(Enumerable.Repeat("<rect width=\"1\" height=\"1\"/>", StickerSvg.MaxElements)) + "</svg>").Sticker);
        }

        [Fact]
        public void BrokenPathData_KeepsWhatCameBefore()
        {
            var figure = Assert.Single(Read("<path d=\"M0 0 L10 0 L20 x L30 0\"/>").Paths).Figures[0];
            Assert.Equal((10.0, 0.0), End(Assert.Single(figure.Segments)));
            // 숫자 없는 명령만 있는 경로는 그리지 않는다
            Assert.Single(Read("<path d=\"Q\"/><rect width=\"1\" height=\"1\"/>").Paths);
        }

        [Fact]
        public void Write_IsCleanAndRoundTrips()
        {
            var sticker = Read("<title>둥근 &amp; 별</title><path d=\"M0 0 C1 2 3 4 5 6 L7 8 Z\" fill=\"#11223380\" fill-rule=\"evenodd\" stroke=\"#fff\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"bevel\"/>");
            var svg = StickerSvg.Write(sticker);
            Assert.Contains("<title>둥근 &amp; 별</title>", svg);
            var again = StickerSvg.Parse(svg).Sticker;
            Assert.Equal("둥근 & 별", again.Title);
            var (a, b) = (sticker.Paths[0], again.Paths[0]);
            Assert.Equal((a.Fill, a.EvenOdd, a.Stroke, a.StrokeWidth, a.LineCap, a.LineJoin), (b.Fill, b.EvenOdd, b.Stroke, b.StrokeWidth, b.LineCap, b.LineJoin));
            Assert.Equal(a.Figures[0].Segments.Select(End), b.Figures[0].Segments.Select(End));
            Assert.Equal(svg, StickerSvg.Write(again));
            Assert.Equal(StickerSvg.Hash(svg), StickerSvg.Hash(StickerSvg.Write(again)));
            Assert.Equal(64, StickerSvg.Hash("x").Length);
        }

        // ---------- 내 스티커 이름·키 ----------

        [Fact]
        public void Library_FileNamesAndKeys_CannotEscapeTheFolder()
        {
            Assert.Equal("내그림", StickerLibrary.FileStem(" 내/그:림?* "));
            Assert.Equal("고양이 사진", StickerLibrary.FileStem("고양이 사진"));
            Assert.Equal("스티커", StickerLibrary.FileStem("..."));
            var file = StickerLibrary.MineFileName("고양이", "ABCDEF0123456789", ".PNG");
            Assert.Equal("고양이-abcdef01.png", file);
            Assert.Equal("고양이", StickerLibrary.NameFromFile(file));
            Assert.Equal("이름-없는-꼬리", StickerLibrary.NameFromFile("이름-없는-꼬리.svg"));
            Assert.True(StickerLibrary.TryParseKey(StickerLibrary.Key(true, file), out var mine, out var name) && mine && name == file);
            Assert.True(StickerLibrary.TryParseKey("builtin:heart.svg", out mine, out name) && !mine && name == "heart.svg");
            foreach (var bad in new[] { "mine:../x.svg", "mine:..\\x.svg", "mine:C:\\x.svg", "builtin:sub/heart.svg", "mine:x.exe", "builtin:x.png", "mine:.svg", "heart.svg", null, "mine:" })
                Assert.False(StickerLibrary.TryParseKey(bad, out _, out _), bad);
            Assert.True(StickerLibrary.IsPicture("a.PNG"));
            Assert.Equal(0.25, TimelineEdits.PictureStickerScale(480, 270, 1920, 1080));
            Assert.Equal(0.5, TimelineEdits.PictureStickerScale(4000, 4000, 1920, 1080));
            Assert.Equal(0.05, TimelineEdits.PictureStickerScale(10, 10, 1920, 1080));
        }

        // ---------- 스티커 클립 ----------

        private static Sticker Heart()
        {
            return StickerSvg.Parse(File.ReadAllText(Path.Combine(StickerFolder, "heart.svg"))).Sticker;
        }

        [Fact]
        public void CreateSticker_EmbedsCleanSvg_BoxFromViewBox_NameShown()
        {
            var wide = Read("<rect width=\"200\" height=\"100\"/>", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 200 100\">");
            var clip = TimelineEdits.CreateSticker("넓은 것", wide, 15, 90);
            Assert.Equal((ClipKind.Art, ArtKind.Sticker, 300.0, 150.0), (clip.Kind, clip.Art.Kind, clip.Art.Width, clip.Art.Height));
            Assert.Equal("넓은 것", clip.Name);
            Assert.Equal(StickerSvg.Write(wide), clip.Art.StickerSvg);
            Assert.Empty(ArtHandles.For(ArtKind.Sticker));
            // 채우기 없음 — 스티커를 모르는 예전 판은 사각형으로 읽어도 칠하지 않는다
            Assert.Equal(FillKind.None, clip.Style.Fill.Kind);
            Assert.Equal("스티커", ArtShape.Group(ArtKind.Sticker));
            // 이름을 주지 않으면 SVG 제목
            Assert.Equal("하트", TimelineEdits.CreateSticker(null, Heart(), 0, 30).Name);
            var tall = Read("<rect width=\"10\" height=\"40\"/>", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 40\">");
            Assert.Equal((75.0, 300.0), (TimelineEdits.CreateSticker("길쭉", tall, 0, 30).Art.Width, TimelineEdits.CreateSticker("길쭉", tall, 0, 30).Art.Height));
        }

        [Fact]
        public void StickerValues_OnlyOnStickers_TooLongSvgDropped_SnapshotRoundTrips()
        {
            var shape = new ArtShape { Kind = ArtKind.Rectangle, StickerName = "x", StickerSvg = "<svg/>" };
            shape.Normalize();
            Assert.Equal((null, (string)null), (shape.StickerName, shape.StickerSvg));
            var sticker = new ArtShape { Kind = ArtKind.Sticker, StickerName = " \u0001이름\u0002 ", StickerSvg = new string('x', StickerSvg.MaxChars + 1) };
            sticker.Normalize();
            Assert.Equal(("이름", (string)null), (sticker.StickerName, sticker.StickerSvg));

            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.AddOverlay(project, TimelineEdits.CreateSticker("하트", Heart(), 0, 30));
            var json = ProjectFile.Snapshot(project);
            var node = JsonNode.Parse(json)["VideoTracks"][1]["Clips"][0]["Art"];
            Assert.Equal(("Sticker", "하트"), ((string)node["Kind"], (string)node["StickerName"]));
            var read = ProjectFile.FromSnapshot(json).VideoTracks[1].Clips[0];
            Assert.Equal(clip.Art.StickerSvg, read.Art.StickerSvg);
            // 1단계 모양의 JSON에는 스티커 값이 없다
            var rectangle = TimelineEdits.AddArt(project, ArtKind.Rectangle, 100, 30);
            var rectangleNode = JsonNode.Parse(ProjectFile.Snapshot(project))["VideoTracks"][1]["Clips"].AsArray().Single(c => (string)c["Art"]["Kind"] == "Rectangle")["Art"];
            Assert.Null(rectangleNode["StickerSvg"]);
            Assert.Null(rectangleNode["StickerName"]);
        }

        [Fact]
        public void Graph_StickerRequest_KeyFollowsTheSvg_NotTheName()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.AddOverlay(project, TimelineEdits.CreateSticker("하트", Heart(), 0, 30));
            var rasterizer = new FakeTextRasterizer();
            var options = new RenderOptions { From = 0, To = 30, Width = 640, Height = 360, Audio = false, TempFolder = @"C:\Temp", TextRasterizer = rasterizer };
            TextImageRequest Request() => Assert.Single(RenderGraph.Build(project, options).TextImages).Value;
            var request = Request();
            Assert.Equal(ArtKind.Sticker, request.Art.Kind);
            Assert.Equal(clip.Art.StickerSvg, request.Art.StickerSvg);
            Assert.Equal("", request.Text);
            var key = request.Key(rasterizer.Version);
            clip.Art.StickerName = "다른 이름";
            Assert.Equal(key, Request().Key(rasterizer.Version));
            clip.Art.StickerSvg = StickerSvg.Write(Read("<rect width=\"5\" height=\"5\"/>"));
            Assert.NotEqual(key, Request().Key(rasterizer.Version));
        }
    }
}
