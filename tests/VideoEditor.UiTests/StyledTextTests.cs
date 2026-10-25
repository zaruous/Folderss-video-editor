using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// 꾸민 글자(이슈 #3·#13): WPF 그리개의 픽셀, 꾸미기 탭(글꼴), 미리 보기 = 내보내기.
    /// 그리개·요청은 플러그인 형식이라 리플렉션으로 만들고, 요청은 JSON을 플러그인의 TextImageRequest로 역직렬화해 만든다.
    /// </summary>
    public class StyledTextTests
    {
        private const string Fill = "#FF3366";
        private readonly ITestOutputHelper _out;

        public StyledTextTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- 그리개 픽셀 ----------

        /// <summary>디코드한 PNG(BGRA, 알파는 곱하지 않은 값).</summary>
        private sealed class Png
        {
            public int Width;
            public int Height;
            public double Dpi;
            public byte[] Bgra;

            public int Alpha(int x, int y) { return Bgra[(y * Width + x) * 4 + 3]; }

            public bool Painted(int x, int y) { return Alpha(x, y) > 128; }

            public int PaintedCount()
            {
                var count = 0;
                for (var i = 3; i < Bgra.Length; i += 4)
                {
                    if (Bgra[i] > 128)
                        count++;
                }
                return count;
            }

            /// <summary>[top, bottom) 줄들에서 칠한 픽셀의 가장 왼쪽·오른쪽 x(없으면 -1).</summary>
            public (int Left, int Right) Extent(int top, int bottom)
            {
                int left = int.MaxValue, right = -1;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        if (!Painted(x, y))
                            continue;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
                return (right < 0 ? -1 : left, right);
            }
        }

        private static string Folder()
        {
            var folder = Path.Combine(PluginUnderTest.Load(UiThread.Instance).Root, "styled-text-tests");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static object NewRasterizer()
        {
            var type = PluginUnderTest.Load(UiThread.Instance).Assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true);
            return Activator.CreateInstance(type);
        }

        private static object Request(object rasterizer, string text, int canvasWidth = 1280, int canvasHeight = 720, double fontSize = 80, string style = null)
        {
            var json = JsonSerializer.Serialize(new
            {
                Text = text,
                FontPixelSize = fontSize,
                EffectScale = 1.0,
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight
            });
            var styleJson = "{\"FontFamily\":\"Malgun Gothic\",\"Fill\":{\"Kind\":\"Solid\",\"Colors\":[\"" + Fill + "\"]}" + (style == null ? "" : "," + style) + "}";
            json = json.TrimEnd('}') + ",\"Style\":" + styleJson + "}";
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            return JsonSerializer.Deserialize(json, rasterizer.GetType().Assembly.GetType("VideoEditor.Core.TextImageRequest", true), options);
        }

        /// <summary>STA가 아닌 스레드(Task.Run)에서 그리게 하고 PNG를 읽는다.</summary>
        private static async Task<Png> Render(object rasterizer, object request, string name)
        {
            var path = Path.Combine(Folder(), name + ".png");
            var render = rasterizer.GetType().GetMethod("Render");
            await Task.Run(() =>
            {
                Assert.NotEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                try
                {
                    render.Invoke(rasterizer, new[] { request, path, (object)CancellationToken.None });
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException;
                }
            });
            return UiThread.Instance.Run(() =>
            {
                using (var stream = File.OpenRead(path))
                {
                    var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    var png = new Png { Width = bgra.PixelWidth, Height = bgra.PixelHeight, Dpi = frame.DpiX, Bgra = new byte[bgra.PixelWidth * bgra.PixelHeight * 4] };
                    bgra.CopyPixels(png.Bgra, png.Width * 4, 0);
                    return png;
                }
            });
        }

        /// <summary>(x, y) 줄에서 x에 가장 가까운 칠한 픽셀의 색(R, G, B) — 글자 사이 빈틈을 피한다.</summary>
        private static (int R, int G, int B) ColorNear(Png png, int x, int y)
        {
            for (var d = 0; d < png.Width; d++)
            {
                foreach (var probe in new[] { x - d, x + d })
                {
                    if (probe >= 0 && probe < png.Width && png.Painted(probe, y))
                    {
                        var i = (y * png.Width + probe) * 4;
                        return (png.Bgra[i + 2], png.Bgra[i + 1], png.Bgra[i]);
                    }
                }
            }
            return (-1, -1, -1);
        }

        [UiFact]
        public async Task Rasterizer_LinearGradient_RedLeftBlueRight_FourColorsInOrder()
        {
            // 0° 선형(빨강 → 파랑): 글자 모양의 왼쪽 끝은 빨강 쪽, 오른쪽 끝은 파랑 쪽
            var rasterizer = NewRasterizer();
            var two = await Render(rasterizer, Request(rasterizer, "■■■■", fontSize: 120,
                style: "\"Bold\":true,\"Fill\":{\"Kind\":\"Linear\",\"Colors\":[\"#FF0000\",\"#0000FF\"],\"Angle\":0}"), "fill-linear");
            var middle = two.Height / 2;
            var (left, right) = two.Extent(middle, middle + 1);
            var start = ColorNear(two, left + 2, middle);
            var end = ColorNear(two, right - 2, middle);
            _out.WriteLine("왼쪽 " + start + " 오른쪽 " + end);
            Assert.True(start.R > 200 && start.B < 60, "왼쪽 " + start);
            Assert.True(end.B > 200 && end.R < 60, "오른쪽 " + end);

            // 4색(빨강·노랑·초록·파랑)이 순서대로 — 정지점 0·⅓·⅔·1 사이를 1/8·3/8·5/8·7/8에서 본다
            var four = await Render(rasterizer, Request(rasterizer, "■■■■", fontSize: 120,
                style: "\"Bold\":true,\"Fill\":{\"Kind\":\"Linear\",\"Colors\":[\"#FF0000\",\"#FFFF00\",\"#00FF00\",\"#0000FF\"],\"Angle\":0}"), "fill-linear-4");
            (left, right) = four.Extent(middle, middle + 1);
            var samples = new[] { 1, 3, 5, 7 }.Select(k => ColorNear(four, left + (right - left) * k / 8, middle)).ToArray();
            _out.WriteLine("4색: " + string.Join(" ", samples));
            Assert.True(samples[0].R > 200 && samples[0].R > samples[0].G && samples[0].B < 60, "1/8 빨강~주황 " + samples[0]);
            Assert.True(samples[1].R > 150 && samples[1].G > 150 && samples[1].B < 60, "3/8 노랑 " + samples[1]);
            Assert.True(samples[2].G > 200 && samples[2].R < 100 && samples[2].B < 60, "5/8 초록 " + samples[2]);
            Assert.True(samples[3].B > samples[3].G && samples[3].R < 60, "7/8 파랑 쪽 " + samples[3]);
        }

        [UiFact]
        public async Task Rasterizer_RadialGradient_CenterIsFirstColor_NoneFillPaintsNothing()
        {
            var rasterizer = NewRasterizer();
            var radial = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200,
                style: "\"Fill\":{\"Kind\":\"Radial\",\"Colors\":[\"#FFFF00\",\"#0000FF\"]}"), "fill-radial");
            // 원형의 가운데는 글자 모양(칠한 곳)의 가운데 — PNG 가운데와 다를 수 있다
            var (left, right) = radial.Extent(0, radial.Height);
            var column = (left + right) / 2;
            var rows = Enumerable.Range(0, radial.Height).Where(y => radial.Painted(column, y)).ToList();
            var middle = (rows.First() + rows.Last()) / 2;
            var center = ColorNear(radial, column, middle);
            var edge = ColorNear(radial, left + 1, middle);
            _out.WriteLine("가운데 " + center + " 가장자리 " + edge);
            Assert.True(center.R > 200 && center.G > 200 && center.B < 60, "가운데는 색1(노랑) " + center);
            Assert.True(edge.B > center.B + 100, "가장자리는 색2(파랑) 쪽 " + edge);

            // 채우기 없음(윤곽선도 없음): 칠한 픽셀이 없다
            var none = await Render(rasterizer, Request(rasterizer, "가나다 ABC", style: "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]}"), "fill-none");
            Assert.Equal(0, none.PaintedCount());
        }

        [UiFact]
        public async Task Rasterizer_FromWorkerThread_TransparentCorners_FillColorInMiddle()
        {
            var rasterizer = NewRasterizer();
            var png = await Render(rasterizer, Request(rasterizer, "가나다 ABC"), "basic");
            _out.WriteLine("크기 " + png.Width + "×" + png.Height + " · DPI " + png.Dpi + " · 칠한 픽셀 " + png.PaintedCount());
            Assert.Equal(96, png.Dpi, 1);
            Assert.Equal(0, png.Width % 2);
            Assert.Equal(0, png.Height % 2);
            Assert.Equal(0, png.Alpha(0, 0));
            Assert.Equal(0, png.Alpha(png.Width - 1, 0));
            Assert.Equal(0, png.Alpha(0, png.Height - 1));
            Assert.Equal(0, png.Alpha(png.Width - 1, png.Height - 1));
            // 불투명한 픽셀은 채움 색이고, 가운데 띠에 있다
            var solid = 0;
            for (var y = png.Height / 4; y < png.Height * 3 / 4; y++)
            {
                for (var x = 0; x < png.Width; x++)
                {
                    var i = (y * png.Width + x) * 4;
                    if (png.Bgra[i + 3] == 255 && png.Bgra[i + 2] == 0xFF && png.Bgra[i + 1] == 0x33 && png.Bgra[i] == 0x66)
                        solid++;
                }
            }
            Assert.True(solid > 200, "채움 색 픽셀 " + solid);
        }

        [UiFact]
        public async Task Rasterizer_BoldPaintsMore_LetterSpacingWidens()
        {
            var rasterizer = NewRasterizer();
            var normal = await Render(rasterizer, Request(rasterizer, "가나다 ABC"), "normal");
            var bold = await Render(rasterizer, Request(rasterizer, "가나다 ABC", style: "\"Bold\":true"), "bold");
            var spaced = await Render(rasterizer, Request(rasterizer, "가나다 ABC", style: "\"LetterSpacing\":500"), "spaced");
            _out.WriteLine("칠한 픽셀 보통 " + normal.PaintedCount() + " · 굵게 " + bold.PaintedCount() + " · 폭 보통 " + normal.Width + " · 자간 500 " + spaced.Width);
            Assert.True(bold.PaintedCount() > normal.PaintedCount() * 1.1);
            // 글자 7개(빈칸 포함) 사이 6칸 × 0.5em(40px)
            Assert.InRange(spaced.Width - normal.Width, 6 * 40 - 8, 6 * 40 + 8);
        }

        [UiFact]
        public async Task Rasterizer_TwoLines_LeftAndRightAlign()
        {
            var rasterizer = NewRasterizer();
            // 두 줄 모두 '가'로 시작하고 '바'로 끝나서 글자 옆 여백이 같다
            const string text = "가나다라바\n가바";
            var left = await Render(rasterizer, Request(rasterizer, text, style: "\"Align\":\"Left\""), "align-left");
            var right = await Render(rasterizer, Request(rasterizer, text, style: "\"Align\":\"Right\""), "align-right");
            var center = await Render(rasterizer, Request(rasterizer, text, style: "\"Align\":\"Center\""), "align-center");
            var l1 = left.Extent(0, left.Height / 2);
            var l2 = left.Extent(left.Height / 2, left.Height);
            var r1 = right.Extent(0, right.Height / 2);
            var r2 = right.Extent(right.Height / 2, right.Height);
            var c1 = center.Extent(0, center.Height / 2);
            var c2 = center.Extent(center.Height / 2, center.Height);
            _out.WriteLine("왼쪽 " + l1 + " / " + l2 + " · 오른쪽 " + r1 + " / " + r2 + " · 가운데 " + c1 + " / " + c2);
            Assert.InRange(l2.Left - l1.Left, -2, 2);
            Assert.True(l2.Right < l1.Right - 100, "왼쪽 정렬이면 짧은 줄의 오른쪽 끝이 안쪽");
            Assert.InRange(r2.Right - r1.Right, -2, 2);
            Assert.True(r2.Left > r1.Left + 100, "오른쪽 정렬이면 짧은 줄의 왼쪽 끝이 안쪽");
            Assert.InRange((c2.Left + c2.Right) - (c1.Left + c1.Right), -4, 4);
        }

        [UiFact]
        public async Task Rasterizer_MissingFont_StillDrawsHangul()
        {
            var rasterizer = NewRasterizer();
            var installed = (bool)rasterizer.GetType().GetMethod("IsInstalled").Invoke(null, new object[] { "없는글꼴XYZ" });
            Assert.False(installed);
            var png = await Render(rasterizer, Request(rasterizer, "가나다", style: "\"FontFamily\":\"없는글꼴XYZ\""), "missing-font");
            Assert.True(png.PaintedCount() > 500, "칠한 픽셀 " + png.PaintedCount());
        }

        [UiFact]
        public async Task Rasterizer_4KCanvas_FastAndNotLargerThanCanvas()
        {
            var rasterizer = NewRasterizer();
            // 그리기 스레드·글꼴 준비는 처음 한 번 — 재지 않는다
            await Render(rasterizer, Request(rasterizer, "준비"), "warm-up");
            var request = Request(rasterizer, "가나다 ABC\n두 번째 줄", 3840, 2160, 400, "\"Shadow\":{\"Enabled\":true,\"Distance\":40,\"Blur\":30}");
            var watch = Stopwatch.StartNew();
            var png = await Render(rasterizer, request, "uhd");
            _out.WriteLine("4K 요청 " + watch.ElapsedMilliseconds + "ms · " + png.Width + "×" + png.Height);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "걸린 시간 " + watch.Elapsed);
            Assert.True(png.Width <= 3840 && png.Height <= 2160);
            // 아주 큰 글자도 캔버스로 잘린다
            var huge = await Render(rasterizer, Request(rasterizer, "가나다라마바사", 640, 360, 300), "huge");
            Assert.Equal(640, huge.Width);
            Assert.Equal(360, huge.Height);
        }

        // ---------- 화면 ----------

        /// <summary>미리 보기 화면의 픽셀(UI 스레드).</summary>
        private static byte[] PreviewPixels(Window window)
        {
            var image = Ui.ById<Image>(window, "PreviewImage");
            var bitmap = image?.Source as BitmapSource;
            if (bitmap == null)
                return null;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        private static bool Differs(byte[] a, byte[] b)
        {
            return a == null || b == null || a.Length != b.Length || !a.AsSpan().SequenceEqual(b);
        }

        private static async Task<object> AddStyledText(UiThread ui, Window window, string text)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
            ui.Run(() =>
            {
                Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
            });
            await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
            await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "꾸미기 탭");
            ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
            await ui.WaitFor(() => Ui.ById<ComboBox>(window, "StyleFontBox"), Wait, "글꼴 칸");
            return ui.Run(() => Get(ProjectClips(window).First(c => (string)Get(c, "Text") == text), "Id"));
        }

        private static object StyleOf(Window window, object id)
        {
            var clip = ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
            return Get(clip, "Style");
        }

        [EditorFact]
        public async Task StyleTab_FontChangesPreview_AndSliderDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "가나다 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기(drawtext)");
                Assert.Null(ui.Run(() => StyleOf(window, id)));
                Assert.Equal("Collapsed", ui.Run(() => Ui.ById<TextBlock>(window, "StyleFontWarning").Visibility.ToString()));

                // 굵게 → 스타일이 생기고 그리개로 그린다
                var before = ui.Run(() => PreviewPixels(window));
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleBold")));
                await ui.WaitUntil(() => StyleOf(window, id) is object style && (bool)Get(style, "Bold"), Wait, "굵게");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "굵게 미리 보기");
                await Task.Delay(500);
                var bold = ui.Run(() => PreviewPixels(window));

                // 글꼴 바꾸기 → 미리 보기가 바뀐다
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "StyleFontBox"), "Arial"));
                await ui.WaitUntil(() => (string)Get(StyleOf(window, id), "FontFamily") == "Arial", Wait, "글꼴 Arial");
                await ui.WaitUntil(() => Differs(bold, PreviewPixels(window)), Wait, "글꼴 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-font"));

                // 자간 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleLetterSpacingSlider");
                    foreach (var value in new[] { 100.0, 200.0, 300.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(StyleOf(window, id), "LetterSpacing") - 300) < 1e-6, Wait, "자간 300");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(StyleOf(window, id), "LetterSpacing")) < 1e-6, Wait, "실행 취소 한 번에 자간 0");
                Assert.Equal("Arial", ui.Run(() => (string)Get(StyleOf(window, id), "FontFamily")));

                // 클립을 다시 고르고 자간 칸을 지나가기만 하면 편집이 아니다 — 다음 실행 취소는 글꼴 변경을 되돌린다
                ui.Run(() =>
                {
                    var session = Session(window);
                    var clipType = PluginUnderTest.Load(ui).Assembly.GetType("VideoEditor.Core.Clip", true);
                    session.GetType().GetMethod("Select").Invoke(session, new object[] { Array.CreateInstance(clipType, 0), false });
                });
                await ui.WaitUntil(() => Ui.ById<TabItem>(window, "StyleTab") == null, Wait, "선택 풀림");
                ui.Run(() => SelectClip(window, n => n.Contains("가나다 ABC")));
                await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "다시 고름");
                ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "StyleLetterSpacing") is TextBox shown && shown.IsVisible ? shown : null, Wait, "자간 칸");
                ui.Run(() =>
                {
                    Ui.Focus(Ui.ById<TextBox>(window, "StyleLetterSpacing"));
                    Ui.Focus(Canvas(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => (string)Get(StyleOf(window, id), "FontFamily") == "Malgun Gothic", Wait, "실행 취소가 글꼴 변경을 되돌림");
                Assert.True(ui.Run(() => (bool)Get(StyleOf(window, id), "Bold")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StyleTab_FillKind_ChangesPreview_ColorsAndAngle_UndoInOneStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "채우기 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<ComboBox>(window, "StyleFillKind"), Wait, "채우기 칸");
                var before = ui.Run(() =>
                {
                    // 스타일이 없을 때: 단색, 색 1만 보이고(글자 색) 그라데이션 칸은 숨김·각도는 꺼짐
                    Assert.Equal("단색", Ui.ById<ComboBox>(window, "StyleFillKind").SelectedItem);
                    Assert.True(Ui.ById<TextBox>(window, "StyleFillColor1").IsVisible);
                    Assert.Equal("#FFFFFF", Ui.ById<TextBox>(window, "StyleFillColor1").Text);
                    Assert.False(Ui.ById<TextBox>(window, "StyleFillColor2").IsVisible);
                    Assert.False(Ui.ById<Button>(window, "StyleFillAdd").IsVisible);
                    Assert.False(Ui.ById<TextBox>(window, "StyleFillAngle").IsEnabled);
                    return PreviewPixels(window);
                });

                // 선형 그라데이션 → 둘째 색(흰색과 대비되는 파랑)이 더해지고 미리 보기가 바뀐다
                ui.Run(() => Ui.ById<ComboBox>(window, "StyleFillKind").SelectedIndex = 1);
                await ui.WaitUntil(() => StyleOf(window, id) is object style && Get(Get(style, "Fill"), "Kind").ToString() == "Linear", Wait, "선형");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "그라데이션 미리 보기");
                ui.Run(() =>
                {
                    var colors = ((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors")).Cast<string>().ToList();
                    Assert.Equal(new[] { "#FFFFFF", "#3B82F6" }, colors);
                    Assert.True(Ui.ById<TextBox>(window, "StyleFillColor2").IsVisible);
                    Assert.True(Ui.ById<TextBox>(window, "StyleFillAngle").IsEnabled);
                    Assert.False(Ui.ById<Button>(window, "StyleFillRemove").IsEnabled);
                    Ui.Snap(window, plugin.ShotFolder, "style-tab-fill-linear");
                });

                // 색 추가 → 3색, 16진 칸에 색을 적으면 그 색, 잘못 적으면 되돌린다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StyleFillAdd")));
                await ui.WaitUntil(() => ((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors")).Count == 3, Wait, "색 추가");
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "StyleFillColor3");
                    Ui.Focus(hex);
                    hex.Text = "22c55e";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (string)((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors"))[2] == "#22C55E", Wait, "색3 초록");
                ui.Run(() =>
                {
                    // 적용하면 포커스가 남아 있어도 정리한 값으로 보이고, 팔레트는 그 색(이름으로 읽힘)
                    var hex = Ui.ById<TextBox>(window, "StyleFillColor3");
                    Assert.Equal("#22C55E", hex.Text);
                    Assert.Equal("초록", Ui.ById<ComboBox>(window, "StyleFillColor3Palette").SelectedItem.ToString());
                    hex.Text = "초록";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                    Assert.Equal("#22C55E", hex.Text);
                });

                // 칸에 포커스를 둔 채 메뉴로 실행 취소 → 칸에 되돌린 값이 보이고, 칸을 떠나도 옛 값(초록)을 다시 적용하지 않는다
                ui.Run(() => Ui.Menu(window, "편집", "실행 취소"));
                await ui.WaitUntil(() => (string)((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors"))[2] == "#3B82F6", Wait, "색3 실행 취소");
                ui.Run(() =>
                {
                    Assert.Equal("#3B82F6", Ui.ById<TextBox>(window, "StyleFillColor3").Text);
                    Ui.Focus(Canvas(window));
                });
                await Task.Delay(300);
                Assert.Equal("#3B82F6", ui.Run(() => (string)((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors"))[2]));
                Assert.True(ui.Run(() => (bool)Get(Session(window), "CanRedo")), "다시 실행이 남아 있어야 한다");

                // 실행 취소: 색 추가 → 선형(스타일 없던 처음으로) — 접히는 색3 칸이 포커스를 잃어도 색을 다시 만들지 않는다
                foreach (var step in new[] { "색 추가", "선형" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StyledText_PreviewFrame_EqualsExportedMp4()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "가나다 ABC");
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleBold")));
                await ui.WaitUntil(() => StyleOf(window, id) != null, Wait, "스타일");
                // 글자 크기는 [글자] 탭(보이는 칸에 키를 보낸다)
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").SelectedIndex = 0);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropFontSize") is TextBox shown && shown.IsVisible ? shown : null, Wait, "글자 크기 칸");
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "PropFontSize");
                    Ui.Focus(box);
                    box.Text = "300";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(ProjectClips(window).First(c => Equals(Get(c, "Id"), id)), "FontSize") - 300) < 1e-6, Wait, "글자 크기 300");

                var assembly = plugin.Assembly;
                var tool = assembly.GetType("VideoEditor.Media.FfmpegTool", true).GetMethod("Locate").Invoke(null, new object[] { FfmpegFolder, null });
                var projectFile = assembly.GetType("VideoEditor.Core.ProjectFile", true);
                var copy = ui.Run(() => projectFile.GetMethod("FromSnapshot").Invoke(null, new[] { projectFile.GetMethod("Snapshot").Invoke(null, new[] { Project(window) }) }));
                var rasterizer = NewRasterizer();

                var frame = assembly.GetType("VideoEditor.Media.PreviewRenderer", true).GetMethod("Frame");
                var preview = await (Task<byte[]>)frame.Invoke(null, new[] { tool, copy, (object)15L, 320, 180, CancellationToken.None, rasterizer });
                Assert.NotNull(preview);
                var bright = 0;
                for (var i = 0; i < preview.Length; i += 4)
                {
                    if (preview[i] > 200 && preview[i + 1] > 200 && preview[i + 2] > 200)
                        bright++;
                }
                Assert.True(bright > 300, "흰 글자 픽셀 " + bright);

                var output = Path.Combine(Folder(), "styled-" + Guid.NewGuid().ToString("N") + ".mp4");
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
                _out.WriteLine("흰 글자 픽셀 " + bright + " · 미리 보기와 내보내기의 평균 차이 " + mean);
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
