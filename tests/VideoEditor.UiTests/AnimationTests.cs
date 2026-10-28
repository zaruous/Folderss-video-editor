using System;
using System.Collections;
using System.Collections.Generic;
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
    /// 애니메이션 효과(#4): 속성 패널 [애니메이션] 탭(썸네일로 넣기·단위·실행 취소·[미리 보기]), 타임라인 띠 끌기, 미리 보기 화면,
    /// 꾸민 글자 그리개의 글자별 장(같은 크기·한글 음절 단위·다 나타난 장 = 정지 그림).
    /// </summary>
    public class AnimationTests
    {
        private readonly ITestOutputHelper _out;

        public AnimationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object TextClip(Window window, string text)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Text") == text);
        }

        private static List<object> Animations(object clip)
        {
            return ((IEnumerable)Get(clip, "Animations"))?.Cast<object>().ToList() ?? new List<object>();
        }

        private static string Describe(object clip)
        {
            return string.Join(", ", Animations(clip).Select(a => Get(a, "Kind") + "/" + Get(a, "Effect") + "/" + Get(a, "Unit")));
        }

        private static async Task GoTo(UiThread ui, Window window, string timecode)
        {
            ui.Run(() =>
            {
                var box = Ui.ById<TextBox>(window, "TimecodeBox");
                Ui.Focus(box);
                box.Text = timecode;
                Ui.Press(box, System.Windows.Input.Key.Enter);
            });
            await Task.Delay(100);
        }

        [EditorFact]
        public async Task AnimationTab_TilesAddEffects_UnitUndo_BandDrag_PreviewFollows()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                const string text = "안녕 ABC";
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");

                // [애니메이션] 탭: 다섯 탭이 한 줄, 분류별 썸네일(글자 클립은 타자기·흩어지기까지)
                ui.Run(() =>
                {
                    var tabs = Ui.ById<TabControl>(window, "PropertyTabs");
                    tabs.SelectedItem = Ui.ById<TabItem>(window, "AnimationTab");
                    window.UpdateLayout();
                    var tops = tabs.Items.Cast<TabItem>().Select(t => t.TranslatePoint(new Point(0, 0), tabs).Y).ToList();
                    _out.WriteLine("탭: " + string.Join(", ", tabs.Items.Cast<TabItem>().Select(t => t.Header)) + " · 윗변 " + string.Join(", ", tops.Select(y => y.ToString("0"))));
                    Assert.True(tops.Max() - tops.Min() < 1, "속성 탭 머리가 두 줄");
                    Assert.Equal(8, Ui.ById<WrapPanel>(window, "AnimationTilesIn").Children.Count);
                    Assert.Equal(5, Ui.ById<WrapPanel>(window, "AnimationTilesEmphasis").Children.Count);
                    Assert.Equal(8, Ui.ById<WrapPanel>(window, "AnimationTilesOut").Children.Count);
                    Ui.Snap(window, plugin.ShotFolder, "animation-tab-empty");
                    Ui.Click(Ui.ById<Button>(window, "AnimIn_Typewriter"));
                    Ui.Click(Ui.ById<Button>(window, "AnimEmphasis_Pulse"));
                    Ui.Click(Ui.ById<Button>(window, "AnimOut_Fly"));
                });
                await ui.WaitUntil(() => Animations(TextClip(window, text)).Count == 3, Wait, "효과 셋");
                ui.Run(() =>
                {
                    var clip = TextClip(window, text);
                    Assert.Equal("In/Typewriter/Character, Emphasis/Pulse/All, Out/Fly/All", Describe(clip));
                    Assert.Contains(Clips(window), n => n.Contains("나타나기 타자기") && n.Contains("강조 맥박") && n.Contains("사라지기 날아가기"));
                    Ui.Snap(window, plugin.ShotFolder, "animation-tab-added");
                });

                // 단위를 단어별로 바꾸고 실행 취소하면 글자별로(한 단계)
                await ui.WaitFor(() => Ui.ById<ComboBox>(window, "AnimIn_Unit"), Wait, "단위 칸");
                ui.Run(() => Ui.ById<ComboBox>(window, "AnimIn_Unit").SelectedIndex = 0);
                await ui.WaitUntil(() => Describe(TextClip(window, text)).StartsWith("In/Typewriter/Word", StringComparison.Ordinal), Wait, "단어별");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Describe(TextClip(window, text)).StartsWith("In/Typewriter/Character", StringComparison.Ordinal), Wait, "실행 취소");

                // 타임라인 아래쪽 초록 띠의 끝 손잡이를 오른쪽으로 끌면 나타나기가 길어진다(놓을 때 한 단계)
                var before = ui.Run(() => (long)Get(Animations(TextClip(window, text))[0], "Duration"));
                ui.Run(() =>
                {
                    var clip = TextClip(window, text);
                    var bounds = ClipBounds(window, clip);
                    var canvas = Canvas(window);
                    var x = (double)canvas.GetType().GetMethod("FrameToX").Invoke(canvas, new object[] { (long)Get(clip, "Start") + before });
                    Drag(window, new Point(x, bounds.Bottom - 3), new Point(x + 40, bounds.Bottom - 3), ModifierKeys.None);
                });
                await ui.WaitUntil(() => (long)Get(Animations(TextClip(window, text))[0], "Duration") > before, Wait, "띠 끌기");
                ui.Run(() =>
                {
                    _out.WriteLine("나타나기 " + before + " → " + Get(Animations(TextClip(window, text))[0], "Duration") + "장 · " + Status(window));
                    Ui.Snap(window, plugin.ShotFolder, "animation-band-dragged");
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => (long)Get(Animations(TextClip(window, text))[0], "Duration") == before, Wait, "띠 끌기 실행 취소");

                // 미리 보기: 클립 시작(타자기 첫 장)은 글자가 없고, 다 찍힌 뒤에는 보인다
                var start = ui.Run(() => (long)Get(TextClip(window, text), "Start"));
                await GoTo(ui, window, "00:00:00:00");
                await ui.WaitUntil(() => !PreviewHasPicture(window), Wait, "타자기 첫 장은 빈 화면");
                await GoTo(ui, window, "00:00:02:15");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "다 찍힌 글자");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "animation-preview-rest"));

                // [미리 보기]는 이 클립 구간만 재생하고 끝에서 멈춘다(장을 그리는 동안 상태 줄에 진행)
                var end = ui.Run(() => (long)Get(TextClip(window, text), "Start") + (long)Get(TextClip(window, text), "Duration"));
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "AnimationPreview")));
                await ui.WaitUntil(() => (long)Get(Session(window), "Playhead") >= end - 2, TimeSpan.FromSeconds(90), "클립 끝까지 재생");
                _out.WriteLine("재생 끝 " + ui.Run(() => Get(Session(window), "Playhead")) + " (클립 " + start + "~" + end + ") · " + ui.Run(() => Status(window)));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task VideoClip_HasAnimationTab_WithoutTextOnlyEffects()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                // 사진 클립: 가져와서(Ctrl+I) [타임라인에 추가]
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "red.png"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == "red.png"), Wait, "가져오기");
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = list.Items.Cast<FrameworkElement>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == "red.png");
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => ProjectClips(window).Count > 0, Wait, "사진 클립");
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("red.png"));
                    var tabs = Ui.ById<TabControl>(window, "PropertyTabs");
                    tabs.SelectedItem = Ui.ById<TabItem>(window, "AnimationTab");
                    window.UpdateLayout();
                    Assert.Null(Ui.ById<Button>(window, "AnimIn_Typewriter"));
                    Assert.Equal(6, Ui.ById<WrapPanel>(window, "AnimationTilesIn").Children.Count);
                    Ui.Click(Ui.ById<Button>(window, "AnimIn_Zoom"));
                });
                await ui.WaitUntil(() => Animations(ProjectClips(window)[0]).Count == 1, Wait, "확대 넣기");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        // ---------- 그리개 ----------

        private sealed class Png
        {
            public int Width;
            public int Height;
            public byte[] Bgra;

            public int Ink()
            {
                var count = 0;
                for (var i = 3; i < Bgra.Length; i += 4)
                {
                    if (Bgra[i] > 128)
                        count++;
                }
                return count;
            }

            /// <summary>칠한 열을 덩어리로 센다(글자 사이 빈 열로 나뉜 수).</summary>
            public int Blobs()
            {
                var blobs = 0;
                var inside = false;
                for (var x = 0; x < Width; x++)
                {
                    var painted = false;
                    for (var y = 0; y < Height && !painted; y++)
                        painted = Bgra[(y * Width + x) * 4 + 3] > 128;
                    if (painted && !inside)
                        blobs++;
                    inside = painted;
                }
                return blobs;
            }
        }

        private static object NewRasterizer()
        {
            var type = PluginUnderTest.Load(UiThread.Instance).Assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true);
            return Activator.CreateInstance(type);
        }

        private static object Request(object rasterizer, string text, string animation)
        {
            var json = "{\"Text\":" + JsonSerializer.Serialize(text) + ",\"FontPixelSize\":80,\"EffectScale\":1,\"CanvasWidth\":1280,\"CanvasHeight\":720,"
                + "\"Style\":{\"FontFamily\":\"Malgun Gothic\",\"Fill\":{\"Kind\":\"Solid\",\"Colors\":[\"#FF3366\"]}}"
                + (animation == null ? "" : ",\"Animation\":" + animation) + "}";
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            return JsonSerializer.Deserialize(json, rasterizer.GetType().Assembly.GetType("VideoEditor.Core.TextImageRequest", true), options);
        }

        private static async Task<Png> Render(object rasterizer, object request, string name)
        {
            var folder = Path.Combine(HeadlessEnvironment.WorkFolder, "animation-png");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name + ".png");
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

        [UiFact]
        public async Task Rasterizer_TypewriterBySyllable_FramesSameSize_LastFrameIsTheStill()
        {
            // "안녕 ABC": 음절·글자 5개가 10장 동안 하나씩(지연 100%) — 2장마다 하나씩 더 찍힌다
            var rasterizer = NewRasterizer();
            string Typewriter(int frame) => "{\"In\":{\"Kind\":\"In\",\"Effect\":\"Typewriter\",\"Unit\":\"Character\",\"Stagger\":100,\"Duration\":10},\"InStart\":0,\"InLength\":10,\"Frame\":" + frame + "}";
            var still = await Render(rasterizer, Request(rasterizer, "안녕 ABC", null), "still");
            var frames = new List<Png>();
            foreach (var f in new[] { 0, 1, 3, 5, 7, 9, 10 })
                frames.Add(await Render(rasterizer, Request(rasterizer, "안녕 ABC", Typewriter(f)), "typewriter-" + f));
            _out.WriteLine("정지 " + still.Width + "×" + still.Height + " 칠함 " + still.Ink() + " · 장: " + string.Join(", ", frames.Select(p => p.Width + "×" + p.Height + " " + p.Ink() + "/" + p.Blobs())));
            Assert.All(frames, p => Assert.Equal((still.Width, still.Height), (p.Width, p.Height)));
            Assert.Equal(0, frames[0].Ink());
            // 한 장씩 더 찍힐 때마다 칠한 픽셀이 는다 — 첫 장은 음절 "안" 하나, 둘째는 "안녕"만큼(자모로 쪼개지지 않는다)
            var inks = frames.Select(p => p.Ink()).ToList();
            for (var i = 1; i < 6; i++)
                Assert.True(inks[i] > inks[i - 1], "칠한 픽셀이 늘지 않음: " + string.Join(", ", inks));
            var one = (await Render(rasterizer, Request(rasterizer, "안", null), "one")).Ink();
            var two = (await Render(rasterizer, Request(rasterizer, "안녕", null), "two")).Ink();
            _out.WriteLine("\"안\" " + one + " · \"안녕\" " + two);
            Assert.InRange(inks[1], one * 0.95, one * 1.05);
            Assert.InRange(inks[2], two * 0.95, two * 1.05);
            Assert.Equal(still.Ink(), frames[^1].Ink());

            // 흩어지기: 모든 장이 같은 크기이고 정지 그림보다 넓다(글자가 날아가는 범위까지)
            string Scatter(int frame) => "{\"Out\":{\"Kind\":\"Out\",\"Effect\":\"Scatter\",\"Unit\":\"Character\",\"Stagger\":30,\"Duration\":10,\"Ease\":\"Out\",\"Amount\":100},\"OutStart\":10,\"OutLength\":10,\"Frame\":" + frame + "}";
            var scattered = new List<Png>();
            foreach (var f in new[] { 5, 14, 17, 20 })
                scattered.Add(await Render(rasterizer, Request(rasterizer, "안녕 ABC", Scatter(f)), "scatter-" + f));
            Assert.All(scattered, p => Assert.Equal((scattered[0].Width, scattered[0].Height), (p.Width, p.Height)));
            Assert.True(scattered[0].Width > still.Width && scattered[0].Height > still.Height);
            Assert.Equal(still.Ink(), scattered[0].Ink());
            Assert.Equal(0, scattered[^1].Ink());
        }
    }
}
