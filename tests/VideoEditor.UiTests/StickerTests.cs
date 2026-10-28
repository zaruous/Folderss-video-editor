using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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
    /// 아트 요소 3단계(#40): 스티커 — 기본 모음(플러그인 zip의 stickers\), 내 스티커(SVG는 깨끗하게 다시 써서, PNG는 사진 클립으로), 그리개 픽셀(제 색·벡터로 다시 그림·실루엣 윤곽선).
    /// </summary>
    public class StickerTests
    {
        private readonly ITestOutputHelper _out;

        public StickerTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static string Folder()
        {
            var folder = Path.Combine(PluginUnderTest.Load(UiThread.Instance).Root, "sticker-tests");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void DoubleClick(Button button)
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        }

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

            public int Count(Func<(int R, int G, int B, int A), bool> match)
            {
                var count = 0;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        if (match(Pixel(x, y)))
                            count++;
                    }
                }
                return count;
            }
        }

        /// <summary>
        /// 플러그인 zip의 기본 스티커 하나로 스티커 클립을 만들어(Core의 CreateSticker — 편집기와 같은 함수) 960×540 출력의 그리개 요청을 렌더 그래프로 만들고 그린다.
        /// change로 클립(상자 크기·꾸미기)을 고친다.
        /// </summary>
        private static async Task<Png> RenderSticker(object rasterizer, string file, Action<object> change = null)
        {
            var plugin = PluginUnderTest.Load(UiThread.Instance);
            var assembly = rasterizer.GetType().Assembly;
            var svg = File.ReadAllText(Path.Combine(plugin.PluginDirectory, "stickers", file));
            var svgType = assembly.GetType("VideoEditor.Core.StickerSvg", true);
            var parse = svgType.GetMethod("Parse").Invoke(null, new object[] { svg });
            var sticker = parse.GetType().GetProperty("Sticker").GetValue(parse);
            Assert.NotNull(sticker);
            var edits = assembly.GetType("VideoEditor.Core.TimelineEdits", true);
            var clip = edits.GetMethod("CreateSticker").Invoke(null, new[] { null, sticker, 0L, 30L });
            change?.Invoke(clip);
            var project = assembly.GetType("VideoEditor.Core.Project", true).GetMethod("CreateDefault").Invoke(null, null);
            project.GetType().GetProperty("Magnetic").SetValue(project, false);
            edits.GetMethod("AddOverlay").Invoke(null, new[] { project, clip, null });
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
            var request = ((IDictionary)plan.GetType().GetProperty("TextImages").GetValue(plan)).Values.Cast<object>().Single();
            var path = Path.Combine(Folder(), Path.GetFileNameWithoutExtension(file) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            await Task.Run(() =>
            {
                try
                {
                    rasterizer.GetType().GetMethod("Render").Invoke(rasterizer, new[] { request, path, (object)CancellationToken.None });
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
                    var png = new Png { Width = bgra.PixelWidth, Height = bgra.PixelHeight, Bgra = new byte[bgra.PixelWidth * bgra.PixelHeight * 4] };
                    bgra.CopyPixels(png.Bgra, png.Width * 4, 0);
                    return png;
                }
            });
        }

        private static void Set(object target, string path, object value)
        {
            var parts = path.Split('.');
            for (var i = 0; i < parts.Length - 1; i++)
                target = target.GetType().GetProperty(parts[i]).GetValue(target);
            target.GetType().GetProperty(parts[^1]).SetValue(target, value);
        }

        /// <summary>
        /// 스티커 그리개(#40 완료 기준 "크게 키워도 가장자리가 깨지지 않는다"): 하트를 상자 300(출력 150px)과 1500(출력 750px)으로 그리면
        /// 칠한 넓이가 (5배)² 비율이고, 반쯤 칠한 가장자리 픽셀은 둘레 길이 수준(벡터를 그 크기로 다시 그림 — 늘인 비트맵이면 가장자리가 5배 두꺼워진다).
        /// 제 색(빨강)으로 칠하고, 흰 윤곽선을 켜면 실루엣 바로 바깥이 흰색이다.
        /// </summary>
        [UiFact]
        public async Task Rasterizer_Sticker_OwnColors_RedrawnSharpWhenLarge_OutlineAroundSilhouette()
        {
            var type = PluginUnderTest.Load(UiThread.Instance).Assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true);
            var rasterizer = Activator.CreateInstance(type);
            try
            {
                var small = await RenderSticker(rasterizer, "heart.svg");
                var large = await RenderSticker(rasterizer, "heart.svg", c =>
                {
                    Set(c, "Art.Width", 1500.0);
                    Set(c, "Art.Height", 1500.0);
                });
                bool Red((int R, int G, int B, int A) p) => p.A == 255 && p.R > 200 && p.G < 90 && p.B < 90;
                var center = small.Pixel(small.Width / 2, small.Height / 2);
                Assert.True(Red(center), "하트 가운데는 빨강 " + center);
                int Painted(Png png) => png.Count(p => p.A > 128);
                int Edge(Png png) => png.Count(p => p.A > 0 && p.A < 255);
                var ratio = (double)Painted(large) / Painted(small);
                _out.WriteLine(string.Format("하트 PNG {0}×{1} → {2}×{3} · 칠한 픽셀 비 {4:0.00}(25) · 가장자리 반투명 {5} → {6}", small.Width, small.Height, large.Width, large.Height, ratio, Edge(small), Edge(large)));
                Assert.InRange(ratio, 24, 26);
                // 750px 하트의 둘레는 약 2,400px — 가장자리 반투명 띠가 1~2px이면 그 안팎, 5배로 늘인 그림이면 그 다섯 배
                Assert.True(Edge(large) < 2400 * 2.5, "가장자리 반투명 픽셀 " + Edge(large));
                Assert.True(Edge(large) < Edge(small) * 10, "크게 그려도 가장자리 띠는 둘레만큼만 늘어남");

                var outlined = await RenderSticker(rasterizer, "heart.svg", c =>
                {
                    Set(c, "Style.Outline.Enabled", true);
                    Set(c, "Style.Outline.Color", "#FFFFFF");
                    Set(c, "Style.Outline.Width", 8.0);
                });
                // 가운데 줄에서 왼쪽 끝부터 처음 칠한 곳은 흰 윤곽선, 그 안쪽은 하트의 짙은 테두리·빨강
                var row = outlined.Height / 2;
                var first = Enumerable.Range(0, outlined.Width).First(x => outlined.Pixel(x, row).A > 200);
                var white = outlined.Pixel(first + 1, row);
                _out.WriteLine("윤곽선 PNG " + outlined.Width + "×" + outlined.Height + " · 가장자리 " + white);
                Assert.True(white.R > 230 && white.G > 230 && white.B > 230, "실루엣 바깥 흰 윤곽선 " + white);
                Assert.True(outlined.Width > small.Width + 4, "윤곽선만큼 넓어짐");
            }
            finally
            {
                ((IDisposable)rasterizer).Dispose();
            }
        }

        /// <summary>미리 보기 그림의 (x, y) 픽셀 — 위치는 프로젝트 픽셀(1920×1080). UI 스레드에서.</summary>
        private static (int R, int G, int B) PreviewAt(Window window, double projectX, double projectY)
        {
            var bitmap = (BitmapSource)Ui.ById<Image>(window, "PreviewImage").Source;
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(projectX * bitmap.PixelWidth / 1920.0), (int)(projectY * bitmap.PixelHeight / 1080.0), 1, 1), pixel, 4, 0);
            return (pixel[2], pixel[1], pixel[0]);
        }

        private static Button Tile(Window window, string panel, string name)
        {
            return Ui.ById<WrapPanel>(window, panel)?.Children.OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) == name);
        }

        private static async Task AddMine(UiThread ui, Window window, string path)
        {
            ui.Post(() => Ui.Click(Ui.ById<Button>(window, "StickerAddMine")));
            await NativeDialogs.Open(ui, path, Wait);
        }

        /// <summary>
        /// [요소] 탭 스티커(#40): 기본 모음이 보이고 두 번 누르면 스티커 클립(제 색으로 미리 보기), [꾸미기]는 채우기 묶음을 숨긴다.
        /// 내 스티커: 스크립트·외부 그림이 든 SVG는 그것을 버린 깨끗한 SVG로 데이터 폴더에 두고(알림은 상태 줄), PNG는 사진 클립으로 넣는다(프로젝트 미디어 폴더에 복사).
        /// 요소가 2,000개 넘는 SVG는 알리고 넣지 않는다.
        /// </summary>
        [EditorFact]
        public async Task StickerTab_Builtins_MineSvgCleaned_PngAsPicture_TooBigRejected()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var mineFolder = Path.Combine(plugin.Manager.DataDirectory, "stickers");
            if (Directory.Exists(mineFolder))
                Directory.Delete(mineFolder, true);
            var window = await Open(ui, plugin);
            try
            {
                var builtins = Directory.GetFiles(Path.Combine(plugin.PluginDirectory, "stickers"), "*.svg").Length;
                Assert.True(builtins >= 16, "zip의 기본 스티커 " + builtins);
                Assert.True(File.Exists(Path.Combine(plugin.PluginDirectory, "stickers", "LICENSES.md")), "zip에 출처·라이선스 목록");
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "ArtTab"));
                await ui.WaitUntil(() => Ui.ById<WrapPanel>(window, "ArtStickers")?.Children.Count == builtins, Wait, "기본 스티커 칸");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "sticker-tab"));

                // 하트를 두 번 누르면 스티커 클립이 재생 헤드에 들어가고 미리 보기 가운데가 빨강
                ui.Run(() => DoubleClick(Tile(window, "ArtStickers", "하트")));
                await ui.WaitUntil(() => ProjectClips(window).Any(c => Get(c, "Kind").ToString() == "Art" && Get(Get(c, "Art"), "Kind").ToString() == "Sticker"), Wait, "하트 스티커");
                ui.Run(() =>
                {
                    var clip = ProjectClips(window).Single(c => Get(c, "Kind").ToString() == "Art");
                    Assert.Equal("하트", Get(clip, "Name"));
                    Assert.StartsWith("<svg", (string)Get(Get(clip, "Art"), "StickerSvg"));
                    Assert.StartsWith("하트 스티커를 넣었습니다", Status(window));
                });
                await ui.WaitUntil(() => PreviewHasPicture(window) && PreviewAt(window, 960, 560) is var p && p.R > 180 && p.G < 100 && p.B < 100, Wait, "하트 미리 보기");
                await ui.WaitFor(() => Ui.ById<TextBlock>(window, "ArtStickerName"), Wait, "스티커 이름");
                ui.Run(() =>
                {
                    Assert.Equal("스티커: 하트", Ui.ById<TextBlock>(window, "ArtStickerName").Text);
                    var tabs = Ui.ById<TabControl>(window, "PropertyTabs");
                    var style = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "꾸미기");
                    tabs.SelectedItem = style;
                    window.UpdateLayout();
                    var sections = Ui.FindAll<TextBlock>((DependencyObject)style.Content, t => t.FontWeight == FontWeights.SemiBold && new[] { "채우기", "윤곽선", "그림자" }.Contains(t.Text))
                        .GroupBy(t => t.Text).ToDictionary(g => g.Key, g => g.First().Visibility);
                    Assert.Equal(Visibility.Collapsed, sections["채우기"]);
                    Assert.Equal(Visibility.Visible, sections["윤곽선"]);
                    Assert.Equal(Visibility.Visible, Ui.ById<TextBlock>(window, "StyleStickerNote").Visibility);
                    Ui.Snap(window, plugin.ShotFolder, "sticker-heart-preview");
                });

                // 내 스티커 SVG: 스크립트·외부 그림·이벤트 속성은 버리고 경로만 남긴 파일로
                var hostile = Path.Combine(Folder(), "나쁜 그림.svg");
                File.WriteAllText(hostile, "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 100 100\">"
                    + "<script>alert(1)</script><image xlink:href=\"https://example.invalid/a.png\" width=\"100\" height=\"100\"/>"
                    + "<circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"#00AA00\" onclick=\"alert(2)\"/></svg>", new UTF8Encoding(false));
                await AddMine(ui, window, hostile);
                await ui.WaitUntil(() => Tile(window, "ArtMyStickers", "나쁜 그림") != null, Wait, "내 스티커 SVG");
                ui.Run(() =>
                {
                    Assert.Contains("읽지 않고 버린 것", Status(window));
                    Assert.Contains("script", Status(window));
                    var stored = Assert.Single(Directory.GetFiles(mineFolder, "*.svg"));
                    var text = File.ReadAllText(stored);
                    _out.WriteLine(Path.GetFileName(stored) + ": " + text);
                    foreach (var word in new[] { "script", "href", "onclick", "example", "image" })
                        Assert.DoesNotContain(word, text);
                    DoubleClick(Tile(window, "ArtMyStickers", "나쁜 그림"));
                });
                await ui.WaitUntil(() => ProjectClips(window).Any(c => (string)Get(c, "Name") == "나쁜 그림"), Wait, "내 SVG 스티커 넣기");

                // 내 스티커 PNG: 사진 클립(맞춤)으로, 프로젝트 미디어 폴더에 복사
                await AddMine(ui, window, Path.Combine(MediaFolder, "red.png"));
                await ui.WaitUntil(() => Tile(window, "ArtMyStickers", "red") != null, Wait, "내 스티커 PNG");
                var before = ui.Run(() => ProjectClips(window).Count);
                ui.Run(() => DoubleClick(Tile(window, "ArtMyStickers", "red")));
                await ui.WaitUntil(() => ProjectClips(window).Count == before + 1, Wait, "PNG 스티커 넣기");
                ui.Run(() =>
                {
                    var picture = ProjectClips(window).Last(c => Get(c, "Kind").ToString() == "Media");
                    Assert.Equal("Image", Get(picture, "MediaKind").ToString());
                    Assert.Equal("Fit", Get(picture, "Fit").ToString());
                    // 400×300 그림 → 1920×1080 화면에서 원래 크기쯤(0.278)
                    Assert.InRange((double)Get(picture, "Scale"), 0.27, 0.29);
                    var path = (string)Get(picture, "Path");
                    Assert.True(File.Exists(path), path);
                    Assert.NotEqual(Path.GetDirectoryName(path), mineFolder);
                    Ui.Snap(window, plugin.ShotFolder, "sticker-mine");
                });

                // 상한을 넘는 SVG는 알리고 넣지 않는다
                var huge = Path.Combine(Folder(), "너무 큰 그림.svg");
                File.WriteAllText(huge, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">"
                    + string.Concat(Enumerable.Repeat("<rect width=\"1\" height=\"1\"/>", 2001)) + "</svg>");
                await AddMine(ui, window, huge);
                var notice = await ui.WaitFor(() => Ui.Window("내 스티커"), Wait, "거절 알림");
                ui.Run(() =>
                {
                    var message = string.Join(" ", Ui.Texts(notice));
                    _out.WriteLine("알림: " + message);
                    Assert.Contains("너무 큰 그림.svg", message);
                    Assert.Contains("요소가 너무 많습니다", message);
                    Ui.Click(Ui.ById<Button>(notice, "AskButton0"));
                });
                await ui.WaitUntil(() => Ui.Window("내 스티커") == null, Wait, "알림 닫힘");
                Assert.Equal(2, Directory.GetFiles(mineFolder).Length);

                // 내 스티커 [지우기]: 파일만 지우고 넣은 클립은 그대로(그림을 품고 있음)
                ui.Run(() =>
                {
                    var tile = Tile(window, "ArtMyStickers", "나쁜 그림");
                    var remove = tile.ContextMenu.Items.OfType<MenuItem>().Single();
                    remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                });
                await ui.WaitUntil(() => Tile(window, "ArtMyStickers", "나쁜 그림") == null, Wait, "내 스티커 지움");
                Assert.True(ui.Run(() => ProjectClips(window).Any(c => (string)Get(c, "Name") == "나쁜 그림")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                if (Directory.Exists(mineFolder))
                    Directory.Delete(mineFolder, true);
            }
        }
    }
}
