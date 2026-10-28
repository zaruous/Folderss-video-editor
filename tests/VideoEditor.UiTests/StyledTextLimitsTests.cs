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
                // 크게 잰 것은 범위를 넘을 때 해상도를 조금 더 낮출 뿐이다 — 휘기는 글자 상자로 어림해 몇 px(이 PC에서 0~10px) 크게 나온다
                Assert.True(capped.Item1 - png.Width <= Math.Max(10, png.Width * 0.04) && capped.Item2 - png.Height <= Math.Max(10, png.Height * 0.04), name + ": 잰 크기가 너무 크다");
            }
            // 범위보다 넓은 글은 잰 크기가 캔버스를 넘는다(렌더 그래프가 해상도를 낮출 근거)
            var wide = await Task.Run(() => ((double Width, double Height))measure.Invoke(rasterizer, new[] { cases[cases.Length - 1].Request }));
            Assert.True(wide.Width > 1920 * 4, "넓은 글 " + wide.Width);
        }

        // ---------- 상자 0/0 기준 그림 ----------

        /// <summary>기준 그림 요청: 이름, 글, 스타일(JSON 조각). 글자 80px, 캔버스 1280×720, 효과 배율 1.</summary>
        private static readonly (string Name, string Text, string Style)[] Baseline =
        {
            ("plain", "가나다 ABC\n두 번째 줄  ", null),
            ("effects", "가나다 ABC\n두 번째 줄  ", "\"Outline\":{\"Enabled\":true,\"Width\":6,\"Join\":\"Miter\"},\"Shadow\":{\"Enabled\":true,\"Distance\":10,\"Angle\":45,\"Blur\":6,\"Opacity\":0.6},\"Align\":\"Right\""),
            ("warp", "가나다 ABC\n두 번째 줄  ", "\"Warp\":{\"Kind\":\"ArchUp\",\"Amount\":60}"),
            ("spaced", "가나다 ABC\n두 번째 줄  ", "\"LetterSpacing\":300"),
            ("glow-gradient", "빛나는 Glow", "\"Bold\":true,\"Fill\":{\"Kind\":\"Linear\",\"Colors\":[\"#FF0000\",\"#0000FF\"],\"Angle\":30},\"Glow\":{\"Enabled\":true,\"Size\":20,\"Opacity\":0.8}"),
            ("reflection-italic", "비친 글 Ref", "\"Italic\":true,\"Reflection\":{\"Enabled\":true,\"Size\":60,\"Gap\":8,\"Opacity\":0.5}"),
            ("hollow-wave", "속 빈 물결", "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]},\"Outline\":{\"Enabled\":true,\"Width\":4},\"Warp\":{\"Kind\":\"Wave\",\"Amount\":-50}"),
            ("lines", "첫 줄\n\n셋째 줄 길게 Qg", "\"LineSpacing\":0.7,\"Align\":\"Left\""),
        };

        /// <summary>
        /// 기준 그림의 픽셀 요약(글꼴 판 → 이름 → "폭x높이 SHA-256"). 글자 상자가 생기기 전 그리개(Version 7, 커밋 9bc67f2)로 그린 것 —
        /// 지금 그리개(Version 10)와 바이트까지 같은 것을 확인한 뒤 적었다(Windows 11, 맑은 고딕 6.69, WPF 8.0.3226). 픽셀은 글꼴 파일에 따라 다르므로 판마다 적는다.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> BaselineDigests =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
            {
                ["6.69"] = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["plain"] = "436x194 7d8f8c80327c1bdeef43e2581bcad80d68f7fbe28b65c39147d100dc4f04331b",
                    ["effects"] = "486x246 c1a03c2c5273cc183f4f40ac42d14f991e796fbd975f334e75c7947bac20b3e3",
                    ["warp"] = "436x282 50340baedd968d9bfde36a4330542caffba39f305914dc7e0413079c7f0e0e0f",
                    ["spaced"] = "604x194 b467d5a041dcf773858634e962e8a29da921a4d375f141bf3a6387a8deda34df",
                    ["glow-gradient"] = "524x158 6506b5bb277ba07fb797b53baac4cb8f075a2ed59a3a183a9e55775026642394",
                    ["reflection-italic"] = "460x230 c931d4d4f4616c9c3b46b19abcac079bb4a0af41cb8b4aa93969276fdc28ab36",
                    ["hollow-wave"] = "388x116 851c63812f12ea07deacd793b021ddc6bf94d0973d1fc1eb91988f64bce3229d",
                    ["lines"] = "598x222 2eef2de9dbf8d1f36700feed33ad56787a3d40cb6f441cba7735bfad8624cc27"
                }
            };

        /// <summary>그린 PNG의 픽셀 요약: "폭x높이 " + SHA-256(폭·높이(int32 리틀 엔디언) + BGRA(알파를 곱하지 않은 값)).</summary>
        private static async Task<string> RenderDigest(object rasterizer, object request, string name)
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
                    var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    var data = new byte[8 + bgra.PixelWidth * bgra.PixelHeight * 4];
                    BitConverter.GetBytes(bgra.PixelWidth).CopyTo(data, 0);
                    BitConverter.GetBytes(bgra.PixelHeight).CopyTo(data, 4);
                    bgra.CopyPixels(new Int32Rect(0, 0, bgra.PixelWidth, bgra.PixelHeight), data, bgra.PixelWidth * 4, 8);
                    return bgra.PixelWidth + "x" + bgra.PixelHeight + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
                }
            });
        }

        /// <summary>
        /// 상자 0/0이면 예전과 같다(#34 시험 보강): 상자 0/0인 요청이 세로 정렬(가운데·아래)과 상관없이 같은 그림이고, 그 그림이 글자 상자가 생기기 전 그리개로 그린
        /// 기준 그림의 요약과 같다 — 새 코드끼리만 견주면 상자를 다루는 배치가 두 요청을 함께 바꿔도 알아채지 못했다.
        /// 기준이 없는 글꼴 판(다른 PC)이면 요약을 출력만 한다(그 판의 기준으로 더하려면 이 값을 확인한 뒤 BaselineDigests에).
        /// 그리는 방식을 일부러 바꿨으면(그리개 Version을 올림) 기준도 새 그림으로 바꾼다.
        /// </summary>
        [UiFact]
        public async Task Rasterizer_ZeroBox_MatchesThePreBoxBaselineDigest()
        {
            var rasterizer = NewRasterizer();
            var font = UiThread.Instance.Run(() => new System.Windows.Media.GlyphTypeface(new Uri(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf"))).Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            BaselineDigests.TryGetValue(font, out var expected);
            _out.WriteLine("맑은 고딕 " + font + (expected == null ? " — 기준 없음(요약만 출력)" : ""));
            foreach (var (name, text, style) in Baseline)
            {
                var plain = await RenderDigest(rasterizer, Request(rasterizer, text, style, canvasWidth: 1280, canvasHeight: 720), "baseline-" + name);
                var zero = await RenderDigest(rasterizer, Request(rasterizer, text, style, 0, 0, "Bottom", 1280, 720), "baseline-zero-" + name);
                _out.WriteLine(name + " " + plain);
                Assert.Equal(plain, zero);
                if (expected != null)
                    Assert.True(expected[name] == plain, name + ": 기준 그림과 다르다 — 기준 " + expected[name] + " · 지금 " + plain);
            }
        }
    }
}
