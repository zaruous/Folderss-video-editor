using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 꾸민 글자의 한계(#34): 긴 글(2,000자 초과) 안내.
    /// </summary>
    public class StyledTextLimitsTests
    {
        private readonly ITestOutputHelper _out;

        public StyledTextLimitsTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static async Task<object> AddText(UiThread ui, Window window, string text)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
            ui.Run(() =>
            {
                Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
            });
            await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
            return ui.Run(() => Get(ProjectClips(window).First(c => (string)Get(c, "Text") == text), "Id"));
        }

        private static object ClipById(Window window, object id)
        {
            return ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
        }

        /// <summary>[글자] 탭 글 칸에 적고 칸을 떠나 고친다.</summary>
        private static void SetText(Window window, string text)
        {
            var box = Ui.ById<TextBox>(window, "PropText");
            Ui.Focus(box);
            box.Text = text;
            Ui.Focus(Canvas(window));
        }

        [EditorFact]
        public async Task LongText_NoticeListsWhatIsNotDrawn_ShortTextHasNone()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddText(ui, window, "긴 글 안내");
                // 네온(채우기 없음 + 윤곽선 + 광선)
                await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "꾸미기 탭");
                ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<Button>(window, "StylePreset8"), Wait, "스타일 모음");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset8")));
                await ui.WaitUntil(() => Get(ClipById(window, id), "Style") is object style && (bool)Get(Get(style, "Glow"), "Enabled"), Wait, "네온");
                Assert.Equal(Visibility.Collapsed, ui.Run(() => Ui.ById<TextBlock>(window, "StyleLongTextNotice").Visibility));

                // 2,001자 → 두 탭 모두 안내, 이 클립이 쓰는 것만
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").Items.OfType<TabItem>().First(t => (string)t.Header == "글자").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropText") is TextBox shown && shown.IsVisible ? shown : null, Wait, "글 칸");
                ui.Run(() => SetText(window, new string('가', 2001)));
                await ui.WaitUntil(() => ((string)Get(ClipById(window, id), "Text")).Length == 2001, Wait, "2,001자");
                var notice = await ui.WaitFor(() => Ui.ById<TextBlock>(window, "PropTextLongNotice") is TextBlock shown && shown.Visibility == Visibility.Visible ? shown : null, Wait, "긴 글 안내");
                var text = ui.Run(() => notice.Text);
                _out.WriteLine(text);
                Assert.StartsWith("글이 2,000자를 넘어", text);
                Assert.Contains("광선", text);
                Assert.Contains("속 빈 글자", text);
                Assert.DoesNotContain("반사", text);
                Assert.Equal(text, ui.Run(() => System.Windows.Automation.AutomationProperties.GetName(notice)));
                Assert.Equal(Visibility.Visible, ui.Run(() => Ui.ById<TextBlock>(window, "StyleLongTextNotice").Visibility));
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "long-text-notice"));

                // 2,000자 → 안내가 사라진다
                ui.Run(() => SetText(window, new string('가', 2000)));
                await ui.WaitUntil(() => ((string)Get(ClipById(window, id), "Text")).Length == 2000, Wait, "2,000자");
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "PropTextLongNotice").Visibility == Visibility.Collapsed, Wait, "안내 사라짐");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        // ---------- 그리개 범위 ----------

        private static object NewRasterizer()
        {
            var type = PluginUnderTest.Load(UiThread.Instance).Assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true);
            return Activator.CreateInstance(type);
        }

        /// <summary>그리기 요청(JSON → 플러그인의 TextImageRequest). 캔버스는 넉넉히(4000×4000) — 잰 크기와 그린 크기를 견주게 자르지 않는다.</summary>
        private static object Request(object rasterizer, string text, string style = null, double wrap = 0, double height = 0, string vertical = "Middle",
            int canvasWidth = 4000, int canvasHeight = 4000)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Text = text,
                FontPixelSize = 80,
                EffectScale = 1.0,
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight,
                WrapWidth = wrap,
                BoxHeight = height
            });
            var styleJson = "{\"FontFamily\":\"Malgun Gothic\",\"Fill\":{\"Kind\":\"Solid\",\"Colors\":[\"#FF3366\"]}" + (style == null ? "" : "," + style) + "}";
            json = json.TrimEnd('}') + ",\"VerticalAlign\":\"" + vertical + "\",\"Style\":" + styleJson + "}";
            var options = new System.Text.Json.JsonSerializerOptions();
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return System.Text.Json.JsonSerializer.Deserialize(json, rasterizer.GetType().Assembly.GetType("VideoEditor.Core.TextImageRequest", true), options);
        }

        /// <summary>STA가 아닌 스레드에서 그려 PNG 크기를 읽는다.</summary>
        private static async Task<(int Width, int Height)> RenderSize(object rasterizer, object request, string name)
        {
            var folder = System.IO.Path.Combine(PluginUnderTest.Load(UiThread.Instance).Root, "styled-text-limits");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, name + ".png");
            await Task.Run(() =>
            {
                try
                {
                    rasterizer.GetType().GetMethod("Render").Invoke(rasterizer, new[] { request, path, (object)System.Threading.CancellationToken.None });
                }
                catch (System.Reflection.TargetInvocationException e)
                {
                    throw e.InnerException;
                }
            });
            return UiThread.Instance.Run(() =>
            {
                using (var stream = System.IO.File.OpenRead(path))
                {
                    var frame = new System.Windows.Media.Imaging.PngBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                    return (frame.PixelWidth, frame.PixelHeight);
                }
            });
        }

        /// <summary>
        /// 그리개 범위(#34): 렌더 그래프가 범위보다 큰 글을 알아보는 어림(Measure)은 그린 PNG보다 작지 않고(작으면 범위 밖이 잘린다) 거의 같다.
        /// 기울임·좁은 행간·휘기 다섯 가지·효과·반사·상자·넘침, 그리고 범위보다 넓은 글(그린 PNG는 캔버스로 잘림).
        /// </summary>
        [UiFact]
        public async Task Rasterizer_Measure_CoversTheDrawnPng_AndIsClose()
        {
            var rasterizer = NewRasterizer();
            var cases = new (string Name, object Request)[]
            {
                ("plain", Request(rasterizer, "가나다 ABC\n두 번째 줄  ")),
                ("italic", Request(rasterizer, "Wgjyp 기울임 fff", "\"Italic\":true")),
                ("spaced", Request(rasterizer, "가나다 ABC", "\"LetterSpacing\":300")),
                ("tight", Request(rasterizer, "가\n나\n다Qg", "\"LineSpacing\":0.5")),
                ("effects", Request(rasterizer, "효과 ABC", "\"Outline\":{\"Enabled\":true,\"Width\":12,\"Join\":\"Miter\"},\"Shadow\":{\"Enabled\":true,\"Distance\":40,\"Angle\":45,\"Blur\":20,\"Opacity\":1},\"Glow\":{\"Enabled\":true,\"Size\":30,\"Opacity\":1}")),
                ("reflection", Request(rasterizer, "반사 ABC", "\"Reflection\":{\"Enabled\":true,\"Size\":100,\"Gap\":50,\"Opacity\":1}")),
                ("arch", Request(rasterizer, "아치 위로 휘는 긴 글", "\"Warp\":{\"Kind\":\"ArchUp\",\"Amount\":80}")),
                ("circle", Request(rasterizer, "원으로 휘는 글자들", "\"Warp\":{\"Kind\":\"Circle\",\"Amount\":100}")),
                ("wave", Request(rasterizer, "물결 글자", "\"Warp\":{\"Kind\":\"Wave\",\"Amount\":-100}")),
                ("bulge", Request(rasterizer, "부풀리기", "\"Warp\":{\"Kind\":\"Bulge\",\"Amount\":100}")),
                ("stairs", Request(rasterizer, "계단 글자 계단", "\"Warp\":{\"Kind\":\"Stairs\",\"Amount\":-100}")),
                ("box", Request(rasterizer, "가나다라 마바사 아자차카 타파하 가나다라 마바사", wrap: 600)),
                ("overflow", Request(rasterizer, "가\n나\n다\n라\n마", height: 150, vertical: "Top")),
                ("wide", Request(rasterizer, string.Concat(Enumerable.Repeat("넓은 글 ", 60)), canvasWidth: 1920, canvasHeight: 1080))
            };
            var measure = rasterizer.GetType().GetMethod("Measure");
            foreach (var (name, request) in cases)
            {
                // 어느 스레드에서나 잰다(그래프는 UI 스레드에서도 만든다)
                var size = await Task.Run(() => ((double Width, double Height))measure.Invoke(rasterizer, new[] { request }));
                var png = await RenderSize(rasterizer, request, "measure-" + name);
                var canvas = ((int)Get(request, "CanvasWidth"), (int)Get(request, "CanvasHeight"));
                var capped = (Math.Min(canvas.Item1, size.Width), Math.Min(canvas.Item2, size.Height));
                _out.WriteLine(name + ": 잰 " + size.Width + "×" + size.Height + " · 그린 " + png.Width + "×" + png.Height);
                Assert.True(capped.Item1 >= png.Width && capped.Item2 >= png.Height, name + ": 잰 크기가 그린 PNG보다 작다");
                Assert.True(capped.Item1 - png.Width <= Math.Max(4, png.Width * 0.04) && capped.Item2 - png.Height <= Math.Max(4, png.Height * 0.04), name + ": 잰 크기가 너무 크다");
            }
            // 범위보다 넓은 글은 잰 크기가 캔버스를 넘는다(렌더 그래프가 해상도를 낮출 근거)
            var wide = await Task.Run(() => ((double Width, double Height))measure.Invoke(rasterizer, new[] { cases[cases.Length - 1].Request }));
            Assert.True(wide.Width > 1920 * 4, "넓은 글 " + wide.Width);
        }
    }
}
