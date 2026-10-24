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
