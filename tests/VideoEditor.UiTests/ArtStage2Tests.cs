using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 아트 요소 2단계(#39): 말풍선(꼬리 손잡이·안의 글)·강조 표시·프레임·배경 — WPF 그리개 픽셀, [요소] 탭 묶음, [모양]·[꾸미기] 탭, 미리 보기 = 내보내기.
    /// 그리개 요청은 플러그인의 렌더 그래프(RenderGraph.Build)가 만든 것을 그대로 그린다(편집기와 같은 길).
    /// </summary>
    public class ArtStage2Tests
    {
        private readonly ITestOutputHelper _out;

        public ArtStage2Tests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static string Folder()
        {
            var folder = Path.Combine(PluginUnderTest.Load(UiThread.Instance).Root, "art-stage2-tests");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Invoke(target, args);
        }

        private static List<object> ArtClips(Window window)
        {
            return ProjectClips(window).Where(c => Get(c, "Kind").ToString() == "Art").ToList();
        }

        private static string KindOf(object clip)
        {
            return Get(Get(clip, "Art"), "Kind").ToString();
        }

        private static double ArtValue(object clip, string name)
        {
            return Convert.ToDouble(Get(Get(clip, "Art"), name));
        }

        /// <summary>
        /// 말풍선 꼬리 안의 한 점(상자 좌표, 크기 배율 1): 플러그인의 윤곽(ArtGeometry.Outline)에서 꼬리 끝의 앞뒤로 꼬리 길이의 약 1/4 떨어진 두 가장자리 점의 가운데 —
        /// 꼬리는 휘고 끝이 가늘어 끝 바로 옆은 칠해졌는지 알 수 없다.
        /// </summary>
        private static Point TailInside(object art)
        {
            var geometry = art.GetType().Assembly.GetType("VideoEditor.Core.ArtGeometry", true);
            var outline = (IList)geometry.GetMethod("Outline").Invoke(null, new[] { art, (object)1.0 });
            var tail = ((IEnumerable)outline[1]).Cast<ValueTuple<double, double>>().ToList();
            var tip = (ValueTuple<double, double>)geometry.GetMethod("TailTip").Invoke(null, new[] { art, (object)1.0 });
            var at = tail.IndexOf(tip);
            var step = Math.Max(2, tail.Count / 8);
            var (a, b) = (tail[at - step], tail[at + step]);
            return new Point((a.Item1 + b.Item1) / 2, (a.Item2 + b.Item2) / 2);
        }

        private static void DoubleClick(Button button)
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        }

        // ---------- 그리개 픽셀 ----------

        /// <summary>디코드한 PNG(BGRA, 알파는 곱하지 않은 값).</summary>
        private sealed class Png
        {
            public int Width;
            public int Height;
            public byte[] Bgra;

            public (int R, int G, int B, int A) Pixel(int x, int y)
            {
                var i = (y * Width + x) * 4;
                return (Bgra[i + 2], Bgra[i + 1], Bgra[i], Bgra[i + 3]);
            }
        }

        /// <summary>
        /// 1920×1080 프로젝트에 kind 요소를 넣고(편집기의 AddArt와 같은 Core 함수) 960×540으로 그릴 때의 그리개 요청을 플러그인 렌더 그래프로 만들어 그린다.
        /// 출력 배율 0.5 — PNG 가운데 = 모양 상자 가운데. 돌려주는 상자는 PNG 안 모양 상자(픽셀).
        /// </summary>
        private static async Task<(Png Png, Rect Box, object Clip)> RenderArt(object rasterizer, string kind, Action<object> change = null)
        {
            var assembly = rasterizer.GetType().Assembly;
            var project = assembly.GetType("VideoEditor.Core.Project", true).GetMethod("CreateDefault").Invoke(null, null);
            project.GetType().GetProperty("Magnetic").SetValue(project, false);
            var kindValue = Enum.Parse(assembly.GetType("VideoEditor.Core.ArtKind", true), kind);
            var edits = assembly.GetType("VideoEditor.Core.TimelineEdits", true);
            var add = edits.GetMethods().Single(m => m.Name == "AddArt" && m.GetParameters()[1].ParameterType.Name == "ArtKind");
            var clip = add.Invoke(null, new[] { project, kindValue, 0L, 30L, null });
            change?.Invoke(clip);
            var optionsType = assembly.GetType("VideoEditor.Core.RenderOptions", true);
            var options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("From").SetValue(options, 0L);
            optionsType.GetProperty("To").SetValue(options, 30L);
            optionsType.GetProperty("Width").SetValue(options, 960);
            optionsType.GetProperty("Height").SetValue(options, 540);
            optionsType.GetProperty("Audio").SetValue(options, false);
            optionsType.GetProperty("TempFolder").SetValue(options, Folder());
            optionsType.GetProperty("TextRasterizer").SetValue(options, rasterizer);
            var plan = assembly.GetType("VideoEditor.Core.RenderGraph", true).GetMethod("Build").Invoke(null, new[] { project, options });
            var images = (IDictionary)plan.GetType().GetProperty("TextImages").GetValue(plan);
            var request = images.Values.Cast<object>().Single();
            var path = Path.Combine(Folder(), "stage2-" + kind + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            var render = rasterizer.GetType().GetMethod("Render");
            await Task.Run(() =>
            {
                try
                {
                    render.Invoke(rasterizer, new[] { request, path, (object)CancellationToken.None });
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException;
                }
            });
            var png = UiThread.Instance.Run(() =>
            {
                using (var stream = File.OpenRead(path))
                {
                    var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    var result = new Png { Width = bgra.PixelWidth, Height = bgra.PixelHeight, Bgra = new byte[bgra.PixelWidth * bgra.PixelHeight * 4] };
                    bgra.CopyPixels(result.Bgra, result.Width * 4, 0);
                    return result;
                }
            });
            var art = clip.GetType().GetProperty("Art").GetValue(clip);
            double w = Convert.ToDouble(Get(art, "Width")) * 0.5, h = Convert.ToDouble(Get(art, "Height")) * 0.5;
            return (png, new Rect(png.Width / 2.0 - w / 2, png.Height / 2.0 - h / 2, w, h), clip);
        }

        private static bool Dark((int R, int G, int B, int A) p)
        {
            return p.A > 200 && p.R < 90 && p.G < 90 && p.B < 110;
        }

        /// <summary>둥근 말풍선 몸통(타원) 안쪽 80%의 짙은(글) 픽셀 경계와 수 — 몸통 가장자리의 짙은 윤곽선은 빼고 잰다.</summary>
        private static (int Left, int Top, int Right, int Bottom, int Count) TextInk(Png png, Rect body)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
            for (var y = (int)body.Top; y < body.Bottom; y++)
            {
                for (var x = (int)body.Left; x < body.Right; x++)
                {
                    var u = (x + 0.5 - (body.Left + body.Width / 2)) / (body.Width / 2);
                    var v = (y + 0.5 - (body.Top + body.Height / 2)) / (body.Height / 2);
                    if (u * u + v * v > 0.8 || !Dark(png.Pixel(x, y)))
                        continue;
                    count++;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            return (left, top, right, bottom, count);
        }

        /// <summary>
        /// 2단계 모양의 그리개 픽셀: 테두리 상자·화면 테두리는 가운데가 비고(구멍) 테두리는 칠해진다, 형광펜은 반투명(알파 약 0x8C),
        /// 집중선은 가운데가 비고 가장자리가 칠해진다, 말풍선 안의 글(짙은 색)은 글 상자 안에만 있고 꼬리 끝까지 흰 바탕으로 칠해진다.
        /// </summary>
        [UiFact]
        public async Task Rasterizer_Stage2_RingHoles_TranslucentMarker_BubbleTextInside()
        {
            var type = PluginUnderTest.Load(UiThread.Instance).Assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true);
            var rasterizer = Activator.CreateInstance(type);
            try
            {
                var (box, boxRect, _) = await RenderArt(rasterizer, "BoxMark");
                var middle = (int)(boxRect.Top + boxRect.Height / 2);
                _out.WriteLine("테두리 상자 PNG " + box.Width + "×" + box.Height + " · 상자 " + boxRect);
                Assert.Equal(0, box.Pixel(box.Width / 2, box.Height / 2).A);
                var edge = box.Pixel((int)boxRect.Left + 2, middle);
                Assert.True(edge.A == 255 && edge.R > 200 && edge.G < 100, "테두리는 빨강 " + edge);

                // 화면 크기 테두리(PNG는 둘레 1px 여백): 가운데는 비고, 바깥 가장자리는 모서리까지 칠한다
                var (border, borderRect, _) = await RenderArt(rasterizer, "ScreenBorder");
                Assert.InRange(border.Width, 960, 962);
                Assert.Equal(0, border.Pixel(border.Width / 2, border.Height / 2).A);
                Assert.Equal(255, border.Pixel((int)borderRect.Left, (int)borderRect.Top).A);
                Assert.Equal(255, border.Pixel((int)borderRect.Right - 1, border.Height / 2).A);

                var (marker, _, _) = await RenderArt(rasterizer, "Highlighter");
                var center = marker.Pixel(marker.Width / 2, marker.Height / 2);
                _out.WriteLine("형광펜 가운데 " + center);
                Assert.InRange(center.A, 0x8C - 8, 0x8C + 8);
                Assert.True(center.R > 230 && center.G > 180 && center.B < 60, "노랑 " + center);

                var (focus, _, _) = await RenderArt(rasterizer, "FocusLines");
                Assert.Equal(0, focus.Pixel(480, 270).A);
                Assert.True(Enumerable.Range(0, 960).Count(x => focus.Pixel(x, 2).A > 128) > 100, "위 가장자리에 줄들");

                // 말풍선: 글(짙은 색)이 글 상자 안(몸통 타원 안쪽 68%)에, 꼬리 끝 근처까지 흰 바탕
                var (bubble, bubbleRect, clip) = await RenderArt(rasterizer, "Bubble");
                var (left, top, right, bottom, ink) = TextInk(bubble, bubbleRect);
                var area = new Rect(bubbleRect.Left + bubbleRect.Width * 0.16, bubbleRect.Top + bubbleRect.Height * 0.16, bubbleRect.Width * 0.68, bubbleRect.Height * 0.68);
                _out.WriteLine(string.Format("말풍선 PNG {0}×{1} · 상자 {2} · 글 {3}픽셀 ({4},{5})-({6},{7}) · 글 상자 {8}", bubble.Width, bubble.Height, bubbleRect, ink, left, top, right, bottom, area));
                Assert.True(ink > 80, "글 픽셀 " + ink);
                Assert.True(left >= area.Left - 1 && right <= area.Right + 1 && top >= area.Top - 1 && bottom <= area.Bottom + 1, "글이 글 상자 안");
                // 꼬리 끝(상자 가운데에서 −0.22w, +0.85h) 쪽 꼬리 안은 흰 바탕으로 칠해져 있다
                var inside = TailInside(Get(clip, "Art"));
                var tail = bubble.Pixel((int)Math.Round(bubbleRect.Left + inside.X * 0.5), (int)Math.Round(bubbleRect.Top + inside.Y * 0.5));
                Assert.True(tail.A > 200 && tail.R > 200 && tail.G > 200 && tail.B > 200, "꼬리 안 " + tail);
                Assert.True(bubble.Height > bubbleRect.Height * 1.5, "그림이 꼬리까지 넓어짐");

                // 글이 길면 글 상자 너비에서 줄을 나눈다(높이가 늘고 폭은 글 상자 안)
                var (wrapped, wrappedRect, _) = await RenderArt(rasterizer, "Bubble", c => c.GetType().GetProperty("Text").SetValue(c, "아주 긴 말풍선 글은 몸통 안에서 여러 줄로 나뉩니다"));
                var (wrappedLeft, wrappedTop, wrappedRight, wrappedBottom, _) = TextInk(wrapped, wrappedRect);
                _out.WriteLine("긴 글 (" + wrappedLeft + "," + wrappedTop + ")-(" + wrappedRight + "," + wrappedBottom + ")");
                Assert.True(wrappedRight - wrappedLeft <= wrappedRect.Width * 0.68 + 2, "줄을 나눠 글 상자 폭 안");
                Assert.True(wrappedBottom - wrappedTop > (bottom - top) * 2, "여러 줄");
            }
            finally
            {
                ((IDisposable)rasterizer).Dispose();
            }
        }

        // ---------- 편집기 ----------

        /// <summary>
        /// [요소] 탭 묶음(말풍선 3·강조 6·프레임·배경 3)의 칸을 두 번 누르면 그 종류가 들어가고, [모양] 탭은 종류에 맞는 줄을 보인다.
        /// 말풍선의 [꾸미기]는 글꼴 묶음이 보이고(안의 글에 씀) 휘기는 숨는다.
        /// </summary>
        [EditorFact]
        public async Task ArtTab_Stage2Groups_TilesAddTheirKinds_ShapeAndStyleRows()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var groups = new[] { ("ArtTilesBubble", 3), ("ArtTilesMark", 6), ("ArtTilesFrame", 3) };
                var kinds = new List<string>();
                ui.Run(() =>
                {
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "ArtTab");
                    window.UpdateLayout();
                    Assert.Equal(8, Ui.ById<WrapPanel>(window, "ArtTiles").Children.Count);
                    foreach (var (id, count) in groups)
                    {
                        var tiles = Ui.ById<WrapPanel>(window, id).Children.Cast<Button>().ToList();
                        Assert.Equal(count, tiles.Count);
                        kinds.AddRange(tiles.Select(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t).Substring("ArtTile".Length)));
                    }
                    Ui.Snap(window, plugin.ShotFolder, "art-stage2-tab");
                });
                foreach (var kind in kinds)
                {
                    var before = ui.Run(() => ArtClips(window).Count);
                    ui.Run(() => DoubleClick(Ui.ById<Button>(window, "ArtTile" + kind)));
                    await ui.WaitUntil(() => ArtClips(window).Count == before + 1, Wait, kind + " 넣기");
                    await ui.WaitUntil(() => Get(Session(window), "PrimaryClip") is object picked && Get(picked, "Kind").ToString() == "Art" && KindOf(picked) == kind, Wait, kind + " 고름");
                }
                _out.WriteLine("넣은 요소: " + string.Join(", ", ui.Run(() => ArtClips(window).Select(KindOf))));

                // 고른 것은 마지막(배경 판). 말풍선을 골라 [모양] 탭의 글·꼬리 줄과 [꾸미기]의 글꼴 묶음을 본다
                ui.Run(() => SelectClip(window, n => n.Contains("둥근 말풍선")));
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "ArtText"), Wait, "말풍선 글 칸");
                ui.Run(() =>
                {
                    Assert.Equal("안녕하세요!", Ui.ById<TextBox>(window, "ArtText").Text);
                    Assert.NotNull(Ui.ById<TextBox>(window, "ArtTailX"));
                    Assert.NotNull(Ui.ById<TextBox>(window, "ArtFontSize"));
                    Assert.Null(Ui.ById<TextBox>(window, "ArtThickness"));
                    var tabs = Ui.ById<TabControl>(window, "PropertyTabs");
                    var style = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "꾸미기");
                    tabs.SelectedItem = style;
                    window.UpdateLayout();
                    var sections = Ui.FindAll<TextBlock>((DependencyObject)style.Content, t => t.FontWeight == FontWeights.SemiBold && new[] { "글꼴", "휘기", "채우기" }.Contains(t.Text))
                        .GroupBy(t => t.Text).ToDictionary(g => g.Key, g => g.First().Visibility);
                    Assert.Equal(Visibility.Visible, sections["글꼴"]);
                    Assert.Equal(Visibility.Collapsed, sections["휘기"]);
                    Assert.Equal(Visibility.Visible, sections["채우기"]);
                    Ui.Snap(window, plugin.ShotFolder, "art-bubble-style-tab");
                });
                // 동그라미 치기는 두께 줄, 집중선은 빈 가운데 줄
                ui.Run(() => SelectClip(window, n => n.Contains("동그라미 치기")));
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "ArtThickness"), Wait, "두께 칸");
                ui.Run(() => SelectClip(window, n => n.Contains("집중선")));
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "ArtInnerRatio"), Wait, "빈 가운데 칸");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>미리 보기 그림의 (x, y) 픽셀(R, G, B) — 위치는 프로젝트 픽셀(1920×1080). UI 스레드에서.</summary>
        private static (int R, int G, int B) PreviewAt(Window window, double projectX, double projectY)
        {
            var bitmap = (BitmapSource)Ui.ById<Image>(window, "PreviewImage").Source;
            var x = (int)(projectX * bitmap.PixelWidth / 1920.0);
            var y = (int)(projectY * bitmap.PixelHeight / 1080.0);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[2], pixel[1], pixel[0]);
        }

        /// <summary>미리 보기에서 프로젝트 사각형 안의 짙은(글) 픽셀 수 — 흰 말풍선 바탕 위의 글.</summary>
        private static int DarkIn(Window window, Rect project)
        {
            var bitmap = (BitmapSource)Ui.ById<Image>(window, "PreviewImage").Source;
            var k = bitmap.PixelWidth / 1920.0;
            var rect = new Int32Rect((int)(project.X * k), (int)(project.Y * k), Math.Max(1, (int)(project.Width * k)), Math.Max(1, (int)(project.Height * k)));
            var pixels = new byte[rect.Width * rect.Height * 4];
            bitmap.CopyPixels(rect, pixels, rect.Width * 4, 0);
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] < 110 && pixels[i + 1] < 90 && pixels[i + 2] < 90)
                    count++;
            }
            return count;
        }

        private static bool White((int R, int G, int B) p)
        {
            return p.R > 220 && p.G > 220 && p.B > 220;
        }

        /// <summary>
        /// 말풍선(#39 완료 기준): 꼬리 끝 손잡이를 미리 보기에서 끌면 꼬리가 옮겨지고(실행 취소 한 단계), 안의 글은 꼬리를 옮겨도 말풍선 몸통의 글 상자 안에 있다.
        /// [모양] 탭에서 글을 바꾸면 같은 그림의 글이 바뀐다.
        /// </summary>
        [EditorFact]
        public async Task Bubble_TailDragged_TextStaysInside_OneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                ui.Run(() =>
                {
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "ArtTab");
                    window.UpdateLayout();
                    DoubleClick(Ui.ById<Button>(window, "ArtTileBubble"));
                });
                await ui.WaitUntil(() => ArtClips(window).Count == 1, Wait, "말풍선 넣기");
                object Bubble() => ArtClips(window)[0];
                // 1920×1080, 말풍선 480×300이 화면 가운데: 몸통 (720, 390)-(1200, 690), 글 상자는 몸통의 가운데 68%
                var text = new Rect(720 + 480 * 0.16, 390 + 300 * 0.16, 480 * 0.68, 300 * 0.68);
                await ui.WaitUntil(() => PreviewHasPicture(window) && DarkIn(window, text) > 30, Wait, "말풍선 글 미리 보기");
                var tip = (X: 960 - 0.22 * 480, Y: 540 + 0.85 * 300);
                // 꼬리 안 한 점(상자 좌표 → 프로젝트: 상자 왼쪽 위 (720, 390))
                Point TailAt() => ui.Run(() =>
                {
                    var inside = TailInside(Get(Bubble(), "Art"));
                    return new Point(720 + inside.X, 390 + inside.Y);
                });
                var oldTail = TailAt();
                ui.Run(() =>
                {
                    Assert.True(White(PreviewAt(window, 960 + 150, 540)), "몸통은 흰색");
                    Assert.True(White(PreviewAt(window, oldTail.X, oldTail.Y)), "꼬리 안도 흰색 " + PreviewAt(window, oldTail.X, oldTail.Y));
                    Ui.Snap(window, plugin.ShotFolder, "art-bubble-preview");
                });
                var inkBefore = ui.Run(() => DarkIn(window, text));

                // 꼬리 끝 손잡이를 오른쪽 위로(화면 220px, −60px) 끈다 — 놓을 때 한 번 편집
                var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "말풍선 틀");
                var zoom = ui.Run(() => Math.Min(overlay.ActualWidth / 1920, overlay.ActualHeight / 1080));
                ui.Run(() =>
                {
                    var shape = Invoke(overlay, "ShapePoints");
                    var points = (Point[])shape.GetType().GetField("Item1").GetValue(shape);
                    var kinds = (string[])shape.GetType().GetField("Item2").GetValue(shape);
                    Assert.Equal(new[] { "Tail" }, kinds);
                    var expected = (Point)Invoke(overlay, "ToScreen", tip.X, tip.Y);
                    Assert.True((points[0] - expected).Length < 1, "꼬리 손잡이 = 꼬리 끝 " + points[0] + " · " + expected);
                    Assert.True((bool)Invoke(overlay, "PointerDown", points[0]));
                    Invoke(overlay, "PointerMove", new Point(points[0].X + 220, points[0].Y - 60));
                    Assert.StartsWith("꼬리 끝", Status(window));
                    Assert.Equal(-0.22, ArtValue(Bubble(), "TailX"), 6);
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => ArtValue(Bubble(), "TailX") > -0.22, Wait, "꼬리 옮김");
                var (tailX, tailY) = ui.Run(() => (ArtValue(Bubble(), "TailX"), ArtValue(Bubble(), "TailY")));
                Assert.InRange(tailX, -0.22 + 220 / zoom / 480 - 0.01, -0.22 + 220 / zoom / 480 + 0.01);
                Assert.InRange(tailY, 0.85 - 60 / zoom / 300 - 0.01, 0.85 - 60 / zoom / 300 + 0.01);
                var newTail = TailAt();
                _out.WriteLine("꼬리 안 " + oldTail + " → " + newTail);
                await ui.WaitUntil(() => White(PreviewAt(window, newTail.X, newTail.Y)) && !White(PreviewAt(window, oldTail.X, oldTail.Y)), Wait, "꼬리 미리 보기");
                ui.Run(() =>
                {
                    // 글은 그대로 몸통 글 상자 안(같은 픽셀 수 근처), 글 상자 밖 몸통 위아래 띠에는 글이 없다
                    var ink = DarkIn(window, text);
                    _out.WriteLine("글 픽셀 " + inkBefore + " → " + ink);
                    Assert.InRange(ink, inkBefore * 0.8, inkBefore * 1.2);
                    Ui.Snap(window, plugin.ShotFolder, "art-bubble-tail-dragged");
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => Math.Abs(ArtValue(Bubble(), "TailX") + 0.22) < 1e-9 && Math.Abs(ArtValue(Bubble(), "TailY") - 0.85) < 1e-9, Wait, "꼬리 실행 취소 한 번");

                // [모양] 탭의 글을 바꾸면 그림의 글이 바뀐다(두 줄 — 글 픽셀이 늘어남)
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "ArtText");
                    Ui.Focus(box);
                    box.Text = "두 줄로 나뉘는\n말풍선 글입니다";
                    Ui.Focus(Ui.ById<TextBox>(window, "ArtWidth"));
                });
                await ui.WaitUntil(() => (string)Get(Bubble(), "Text") == "두 줄로 나뉘는\n말풍선 글입니다", Wait, "글 바꿈");
                await ui.WaitUntil(() => DarkIn(window, text) > inkBefore * 1.5, Wait, "두 줄 글 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "art-bubble-two-lines"));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>
        /// 강조 표시·프레임(#39 완료 기준): 화면 테두리·집중선·테두리 상자·형광펜·아래 자막 띠(글)를 넣은 프로젝트의 미리 보기 한 장과 내보낸 MP4의 같은 프레임이 같다
        /// (StyledText_PreviewFrame_EqualsExportedMp4와 같은 방법 — 같은 렌더 그래프·그리개 PNG).
        /// </summary>
        [EditorFact]
        public async Task EmphasisAndFrame_PreviewFrame_EqualsExportedMp4()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var kinds = new[] { "ScreenBorder", "FocusLines", "BoxMark", "Highlighter", "LowerThird", "Sparkle", "CircleMark" };
                ui.Run(() =>
                {
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "ArtTab");
                    window.UpdateLayout();
                });
                foreach (var kind in kinds)
                {
                    var before = ui.Run(() => ArtClips(window).Count);
                    ui.Run(() => DoubleClick(Ui.ById<Button>(window, "ArtTile" + kind)));
                    await ui.WaitUntil(() => ArtClips(window).Count == before + 1, Wait, kind + " 넣기");
                }
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "art-emphasis-frame-preview"));

                var assembly = plugin.Assembly;
                var tool = assembly.GetType("VideoEditor.Media.FfmpegTool", true).GetMethod("Locate").Invoke(null, new object[] { FfmpegFolder, null });
                var projectFile = assembly.GetType("VideoEditor.Core.ProjectFile", true);
                var copy = ui.Run(() => projectFile.GetMethod("FromSnapshot").Invoke(null, new[] { projectFile.GetMethod("Snapshot").Invoke(null, new[] { Project(window) }) }));
                var rasterizer = Activator.CreateInstance(assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true));
                var frame = assembly.GetType("VideoEditor.Media.PreviewRenderer", true).GetMethod("Frame");
                var arguments = new[] { tool, copy, (object)15L, 320, 180, CancellationToken.None, rasterizer };
                arguments = arguments.Concat(Enumerable.Repeat(Type.Missing, frame.GetParameters().Length - arguments.Length)).ToArray();
                var preview = await (Task<byte[]>)frame.Invoke(null, arguments);
                Assert.NotNull(preview);
                var painted = 0;
                for (var i = 0; i < preview.Length; i += 4)
                {
                    if (preview[i] > 40 || preview[i + 1] > 40 || preview[i + 2] > 40)
                        painted++;
                }
                Assert.True(painted > 2000, "칠한 픽셀 " + painted);

                var output = Path.Combine(Folder(), "emphasis-" + Guid.NewGuid().ToString("N") + ".mp4");
                var settingsType = assembly.GetType("VideoEditor.Media.ExportSettings", true);
                var settings = Activator.CreateInstance(settingsType);
                settingsType.GetProperty("OutputPath").SetValue(settings, output);
                settingsType.GetProperty("Width").SetValue(settings, 320);
                settingsType.GetProperty("Height").SetValue(settings, 180);
                settingsType.GetProperty("Encoder").SetValue(settings, "mpeg4");
                settingsType.GetProperty("Quality").SetValue(settings, 0);
                settingsType.GetProperty("From").SetValue(settings, 0L);
                settingsType.GetProperty("To").SetValue(settings, (long)Get(copy, "Duration"));
                var export = assembly.GetType("VideoEditor.Media.Exporter", true).GetMethod("Export");
                await (Task)export.Invoke(null, new[] { tool, copy, settings, null, CancellationToken.None, rasterizer });

                var raw = output + ".bgra";
                RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", "0.5", "-i", output, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", raw);
                var exported = File.ReadAllBytes(raw);
                Assert.Equal(preview.Length, exported.Length);
                long diff = 0;
                for (var i = 0; i < preview.Length; i++)
                {
                    if (i % 4 != 3)
                        diff += Math.Abs(preview[i] - exported[i]);
                }
                var mean = diff / (preview.Length * 0.75);
                _out.WriteLine("칠한 픽셀 " + painted + " · 미리 보기와 내보내기의 평균 차이 " + mean);
                Assert.True(mean < 4, "평균 차이 " + mean);
                ((IDisposable)rasterizer).Dispose();
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
