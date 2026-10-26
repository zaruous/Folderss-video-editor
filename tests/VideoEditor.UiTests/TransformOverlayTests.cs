using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 미리 보기 틀: 고른 사진을 미리 보기에서 끌어 옮기고 모서리를 끌어 크기를 바꾼다(놓을 때 한 번 편집).
    /// 글자는 미리 보기에서 눌러 고르고, 옆 손잡이로 글자 상자 너비·높이를 바꾼다(반대쪽 옆은 그대로).
    /// </summary>
    public class TransformOverlayTests
    {
        private readonly ITestOutputHelper _out;

        public TransformOverlayTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Invoke(target, args);
        }

        /// <summary>
        /// 글자 상자 범위(#27, Windows 실측): 미리 보기에서 상자 너비를 4,000px 넘게 끌어도 [글자] 탭 슬라이더가 그 값을 따라간다(최대 = 모델 상한 16,000).
        /// </summary>
        [EditorFact]
        public async Task Preview_TextBoxDraggedPast4000_SliderFollows()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                const string text = "넓은 상자";
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "글자 틀");
                object Clip() => ProjectClips(window).First(c => (string)Get(c, "Text") == text);
                var project = ui.Run(() => Project(window));
                var width = ui.Run(() => (int)Get(project, "Width"));
                var zoom = ui.Run(() => Math.Min(overlay.ActualWidth / width, overlay.ActualHeight / (int)Get(project, "Height")));
                // 오른쪽 옆을 프로젝트 6,000px만큼 오른쪽으로(화면 밖까지 끈다)
                ui.Run(() =>
                {
                    var edges = (Point[])Invoke(overlay, "EdgePoints");
                    Assert.True((bool)Invoke(overlay, "PointerDown", edges[1]));
                    Invoke(overlay, "PointerMove", new Point(edges[1].X + 6000 * zoom, edges[1].Y));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (double)Get(Clip(), "TextBoxWidth") > 4000, Wait, "상자 너비 4,000 넘음");
                var boxWidth = ui.Run(() => (double)Get(Clip(), "TextBoxWidth"));
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").Items.OfType<TabItem>().First(t => (string)t.Header == "글자").IsSelected = true);
                var slider = await ui.WaitFor(() => Ui.ById<Slider>(window, "PropTextBoxWidthSlider") is Slider shown && shown.IsVisible ? shown : null, Wait, "상자 너비 슬라이더");
                ui.Run(() =>
                {
                    Assert.Equal(16000, slider.Maximum);
                    Assert.Equal(boxWidth, slider.Value, 1);
                    Assert.Equal(boxWidth, double.Parse(Ui.ById<TextBox>(window, "PropTextBoxWidth").Text, System.Globalization.CultureInfo.InvariantCulture));
                    Ui.Snap(window, plugin.ShotFolder, "text-box-past-4000");
                    // 작은 값은 숫자 칸으로 정확히 맞춘다
                    var box = Ui.ById<TextBox>(window, "PropTextBoxWidth");
                    Ui.Focus(box);
                    box.Text = "37";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (double)Get(Clip(), "TextBoxWidth") == 37, Wait, "숫자 칸 37");
                _out.WriteLine("끈 상자 너비 " + boxWidth + "px, 슬라이더 1px = 최대 16,000 / 폭 " + ui.Run(() => slider.ActualWidth).ToString("0") + "px");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task Preview_DragMovesAndCornerResizes_SelectedPhoto()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "red.png"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == 1, Wait, "가져오기");
                ui.Run(() =>
                {
                    Ui.ById<ListBox>(window, "MediaList").SelectedIndex = 0;
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Count == 1, Wait, "클립 추가");
                var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));

                // 고르지 않으면 틀이 없고, 고르면(재생 헤드가 클립 안) 틀과 손잡이 넷
                Assert.False(ui.Run(() => (bool)Get(overlay, "HasFrame")));
                ui.Run(() => SelectClip(window, n => n.Contains("red.png")));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "틀");
                var project = ui.Run(() => Project(window));
                var (width, height) = ui.Run(() => ((int)Get(project, "Width"), (int)Get(project, "Height")));
                var zoom = ui.Run(() => Math.Min(overlay.ActualWidth / width, overlay.ActualHeight / height));
                var corners = ui.Run(() => (Point[])Invoke(overlay, "HandlePoints"));
                Assert.Equal(4, corners.Length);
                // 사진은 기본으로 화면 채우기라 틀이 화면 전체
                Assert.Equal(width * zoom, corners[1].X - corners[0].X, 1);
                var center = new Point((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "transform-overlay"));

                // 안쪽을 끌면 옮긴다(화면 60픽셀 → 프로젝트 60/zoom 픽셀), 놓기 전에는 클립 값이 그대로
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "PointerDown", center));
                    Invoke(overlay, "PointerMove", new Point(center.X + 60, center.Y + 3));
                    Assert.Equal(0.0, (double)Get(ProjectClips(window)[0], "X"));
                    Assert.StartsWith("위치 X", Status(window));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (double)Get(ProjectClips(window)[0], "X") != 0, Wait, "옮김");
                ui.Run(() =>
                {
                    Assert.Equal(Math.Round(60 / zoom), (double)Get(ProjectClips(window)[0], "X"));
                    // 세로는 가운데 가까이(3픽셀)라 붙는다
                    Assert.Equal(0.0, (double)Get(ProjectClips(window)[0], "Y"));
                });

                // 오른쪽 아래 모서리를 가운데 쪽으로 반만큼 끌면 크기 약 50%
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "HandlePoints");
                    var middle = new Point((now[0].X + now[2].X) / 2, (now[0].Y + now[2].Y) / 2);
                    Assert.True((bool)Invoke(overlay, "PointerDown", now[2]));
                    Invoke(overlay, "PointerMove", new Point((now[2].X + middle.X) / 2, (now[2].Y + middle.Y) / 2));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (double)Get(ProjectClips(window)[0], "Scale") < 0.9, Wait, "크기");
                Assert.InRange(ui.Run(() => (double)Get(ProjectClips(window)[0], "Scale")), 0.45, 0.55);

                // 틀 밖을 누르면 아무 일도 없고, 실행 취소 한 번에 크기만 되돌아간다
                ui.Run(() =>
                {
                    Assert.False((bool)Invoke(overlay, "PointerDown", new Point(1, 1)));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => (double)Get(ProjectClips(window)[0], "Scale") == 1, Wait, "실행 취소");
                Assert.Equal(Math.Round(60 / zoom), ui.Run(() => (double)Get(ProjectClips(window)[0], "X")));

                // 화면 전체 사진을 고른 채로, 그 위에 있는 글자를 누르면 사진을 끌지 않고 글자를 고른다
                const string title = "위 글자";
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = title;
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(title)), Wait, "글자 클립");
                ui.Run(() => SelectClip(window, n => n.Contains("red.png")));
                await ui.WaitUntil(() => (string)Get(Get(Session(window), "PrimaryClip"), "Path") is string path && path.EndsWith("red.png"), Wait, "사진 고름");
                var photoX = ui.Run(() => (double)Get(ProjectClips(window).First(c => (string)Get(c, "Path") is string path && path.EndsWith("red.png")), "X"));
                ui.Run(() =>
                {
                    var titleCenter = (Point)Invoke(overlay, "ToScreen", width / 2.0, height / 2.0);
                    Assert.True((bool)Invoke(overlay, "PointerDown", titleCenter));
                    Invoke(overlay, "PointerMove", new Point(titleCenter.X + 20, titleCenter.Y));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (string)Get(Get(Session(window), "PrimaryClip"), "Text") == title, Wait, "위 글자를 고름");
                // 끈 것은 글자다(사진은 제자리)
                await ui.WaitUntil(() => (double)Get(ProjectClips(window).First(c => (string)Get(c, "Text") == title), "X") != 0, Wait, "글자를 옮김");
                Assert.Equal(photoX, ui.Run(() => (double)Get(ProjectClips(window).First(c => (string)Get(c, "Path") is string path && path.EndsWith("red.png")), "X")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        // ---------- 글자 상자 ----------

        /// <summary>미리 보기 화면의 픽셀(BGRA)과 크기(UI 스레드).</summary>
        private static (byte[] Pixels, int Width, int Height) Preview(Window window)
        {
            var bitmap = Ui.ById<Image>(window, "PreviewImage")?.Source as BitmapSource;
            if (bitmap == null)
                return (null, 0, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
        }

        private static bool Differs(byte[] a, byte[] b)
        {
            return a == null || b == null || a.Length != b.Length || !a.AsSpan().SequenceEqual(b);
        }

        /// <summary>미리 보기에서 밝은(흰 글자) 픽셀의 경계(프로젝트 픽셀) — 바탕이 검정이라 글자만 밝다. 없으면 Rect.Empty.</summary>
        private static Rect InkBounds(Window window, int projectWidth)
        {
            var (pixels, width, height) = Preview(window);
            if (pixels == null)
                return Rect.Empty;
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    if (pixels[i] < 128 || pixels[i + 1] < 128 || pixels[i + 2] < 128)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right < 0)
                return Rect.Empty;
            var k = (double)projectWidth / width;
            return new Rect(left * k, top * k, (right - left + 1) * k, (bottom - top + 1) * k);
        }

        /// <summary>틀(손잡이 네 모서리, 화면 픽셀)의 경계를 프로젝트 픽셀로 — 돌리지 않은 틀.</summary>
        private static Rect FrameBounds(Point[] corners, double zoom, double left, double top)
        {
            var xs = corners.Select(p => (p.X - left) / zoom).ToList();
            var ys = corners.Select(p => (p.Y - top) / zoom).ToList();
            return new Rect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
        }

        [EditorFact]
        public async Task Preview_TextClip_ClickSelects_MoveScaleAndBoxEdges()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                // 글자 클립을 넣는다(넣으면 골라진다) — 미리 보기 바탕이 검정이라 흰 글자가 보인다
                const string text = "글자 상자 시험 ABC";
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "글자 틀");
                object Clip() => ProjectClips(window).First(c => (string)Get(c, "Text") == text);
                double Value(string name) => (double)Get(Clip(), name);
                var id = ui.Run(() => Get(Clip(), "Id"));
                var project = ui.Run(() => Project(window));
                var (width, height) = ui.Run(() => ((int)Get(project, "Width"), (int)Get(project, "Height")));
                var (zoom, left, top) = ui.Run(() =>
                {
                    var z = Math.Min(overlay.ActualWidth / width, overlay.ActualHeight / height);
                    return (z, (overlay.ActualWidth - width * z) / 2, (overlay.ActualHeight - height * z) / 2);
                });
                // 글자는 모서리 넷 + 옆 넷
                Assert.Equal(4, ui.Run(() => ((Point[])Invoke(overlay, "EdgePoints")).Length));

                // 고르기를 풀면 틀이 없다 → 미리 보기에서 글자를 누르면 골라지고 틀이 글자를 감싼다(놓기만 하면 편집 아님)
                ui.Run(() =>
                {
                    var session = Session(window);
                    var clipType = plugin.Assembly.GetType("VideoEditor.Core.Clip", true);
                    session.GetType().GetMethod("Select").Invoke(session, new object[] { Array.CreateInstance(clipType, 0), false });
                });
                await ui.WaitUntil(() => !(bool)Get(overlay, "HasFrame"), Wait, "고르기 풀림");
                var center = ui.Run(() => (Point)Invoke(overlay, "ToScreen", width / 2.0, height / 2.0));
                // 누르기만 해서는 편집이 없다 — CanUndo는 글자를 넣을 때부터 참이라 프로젝트 스냅숏으로 비교한다
                var projectFile = plugin.Assembly.GetType("VideoEditor.Core.ProjectFile", true);
                string Snapshot() => (string)projectFile.GetMethod("Snapshot").Invoke(null, new[] { Project(window) });
                var untouched = ui.Run(Snapshot);
                ui.Run(() =>
                {
                    // 빈 곳(화면 모서리)을 누르면 아무것도 고르지 않는다
                    Assert.False((bool)Invoke(overlay, "PointerDown", (Point)Invoke(overlay, "ToScreen", 5.0, 5.0)));
                    Assert.True((bool)Invoke(overlay, "PointerDown", center));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "눌러 고름");
                ui.Run(() =>
                {
                    Assert.Equal(id, Get(Get(Session(window), "PrimaryClip"), "Id"));
                    Assert.Equal(untouched, Snapshot());
                    Assert.Equal(0.0, Value("X"));
                });
                await ui.WaitUntil(() => !InkBounds(window, width).IsEmpty, Wait, "글자 픽셀");
                var frame = ui.Run(() => FrameBounds((Point[])Invoke(overlay, "HandlePoints"), zoom, left, top));
                var ink = ui.Run(() => InkBounds(window, width));
                _out.WriteLine("틀 " + frame + " · 글자 " + ink);
                // 틀은 화면 가운데의 글자 상자(꾸미지 않은 글자는 어림값이라 여유를 둔다): 글자를 다 감싸고 너무 크지 않다
                var loose = frame;
                loose.Inflate(12, 12);
                Assert.True(loose.Contains(ink), "틀 " + frame + "이 글자 " + ink + "를 감싸야 한다");
                Assert.True(frame.Height < ink.Height * 2.2 && frame.Width < ink.Width + 60, "틀 " + frame + " · 글자 " + ink);
                Assert.Equal(width / 2.0, frame.X + frame.Width / 2, 1);
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "transform-text"));

                // 안쪽을 끌면 X·Y가 바뀌고(놓을 때 한 번), 실행 취소 한 번에 둘 다 되돌아간다
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "PointerDown", center));
                    Invoke(overlay, "PointerMove", new Point(center.X + 60, center.Y + 40));
                    Assert.Equal(0.0, Value("X"));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => Value("X") != 0, Wait, "옮김");
                Assert.Equal((Math.Round(60 / zoom), Math.Round(40 / zoom)), ui.Run(() => (Value("X"), Value("Y"))));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Value("X") == 0 && Value("Y") == 0, Wait, "옮김 실행 취소");

                // 모서리를 바깥으로 끌면 크기 %가 커진다(가운데 기준)
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                ui.Run(() =>
                {
                    var corners = (Point[])Invoke(overlay, "HandlePoints");
                    var middle = new Point((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
                    Assert.True((bool)Invoke(overlay, "PointerDown", corners[2]));
                    Invoke(overlay, "PointerMove", new Point(middle.X + (corners[2].X - middle.X) * 1.5, middle.Y + (corners[2].Y - middle.Y) * 1.5));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => Value("Scale") > 1.2, Wait, "크기");
                Assert.InRange(ui.Run(() => Value("Scale")), 1.4, 1.6);
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Value("Scale") == 1, Wait, "크기 실행 취소");

                // 오른쪽 옆을 안쪽으로 끌면 상자 너비가 생겨 줄이 나뉜다: 틀이 높아지고, 왼쪽 옆은 그대로(X는 줄어든 너비의 절반만큼 왼쪽)
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                await Task.Delay(300);
                var beforeWidth = ui.Run(() => Preview(window).Pixels);
                var (edges, frameWidth, frameHeight) = ui.Run(() =>
                {
                    var e = (Point[])Invoke(overlay, "EdgePoints");
                    return (e, e[1].X - e[3].X, e[2].Y - e[0].Y);
                });
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "PointerDown", edges[1]));
                    Invoke(overlay, "PointerMove", new Point(edges[1].X - frameWidth * 0.35, edges[1].Y + 7));
                    Assert.StartsWith("상자 너비", Status(window));
                    Assert.Equal(0.0, Value("TextBoxWidth"));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => Value("TextBoxWidth") > 0, Wait, "상자 너비");
                var boxWidth = ui.Run(() => Value("TextBoxWidth"));
                Assert.InRange(boxWidth, frameWidth * 0.65 / zoom - 2, frameWidth * 0.65 / zoom + 2);
                var shifted = -(frameWidth / zoom - boxWidth) / 2;
                Assert.InRange(ui.Run(() => Value("X")), shifted - 1, shifted + 1);
                await ui.WaitUntil(() => Differs(beforeWidth, Preview(window).Pixels), Wait, "줄 나눈 미리 보기");
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "EdgePoints");
                    // 줄이 나뉘어 틀이 높다(두 줄), 왼쪽 옆은 그대로(X 반올림 1px 안)
                    Assert.True(now[2].Y - now[0].Y > frameHeight * 1.7, "틀 높이 " + (now[2].Y - now[0].Y) + " · 처음 " + frameHeight);
                    Assert.InRange(now[3].X - edges[3].X, -zoom - 0.5, zoom + 0.5);
                    Assert.Equal(boxWidth, double.Parse(Ui.ById<TextBox>(window, "PropTextBoxWidth").Text, System.Globalization.CultureInfo.InvariantCulture));
                    Ui.Snap(window, plugin.ShotFolder, "transform-text-box-width");
                });
                // 실행 취소 한 번에 너비·위치가 함께 돌아가고, 다시 실행으로 되살린다
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Value("TextBoxWidth") == 0 && Value("X") == 0, Wait, "상자 너비 실행 취소");
                ui.Run(() => Key(window, System.Windows.Input.Key.Y, ModifierKeys.Control));
                await ui.WaitUntil(() => Value("TextBoxWidth") == boxWidth, Wait, "다시 실행");

                // 아래 옆을 아래로 끌면 상자 높이가 생긴다 — 위 옆은 그대로(Y는 늘어난 만큼의 절반 아래로)
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                await Task.Delay(500);
                var beforeHeight = ui.Run(() => Preview(window).Pixels);
                var (tallEdges, tallHeight) = ui.Run(() =>
                {
                    var e = (Point[])Invoke(overlay, "EdgePoints");
                    return (e, e[2].Y - e[0].Y);
                });
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "PointerDown", tallEdges[2]));
                    Invoke(overlay, "PointerMove", new Point(tallEdges[2].X - 5, tallEdges[2].Y + 80));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => Value("TextBoxHeight") > 0, Wait, "상자 높이");
                var boxHeight = ui.Run(() => Value("TextBoxHeight"));
                Assert.InRange(boxHeight, (tallHeight + 80) / zoom - 2, (tallHeight + 80) / zoom + 2);
                Assert.InRange(ui.Run(() => Value("Y")), 80 / zoom / 2 - 1, 80 / zoom / 2 + 1);
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "EdgePoints");
                    Assert.InRange(now[0].Y - tallEdges[0].Y, -zoom - 0.5, zoom + 0.5);
                    Assert.InRange(now[2].Y - tallEdges[2].Y, 80 - zoom - 0.5, 80 + zoom + 0.5);
                    // [글자] 탭: 너비·높이 칸에 값, 높이가 있으니 세로 정렬을 고를 수 있다, 글이 상자보다 짧아 넘침 안내는 없다
                    Assert.Equal(boxWidth, double.Parse(Ui.ById<TextBox>(window, "PropTextBoxWidth").Text, System.Globalization.CultureInfo.InvariantCulture));
                    Assert.Equal(boxHeight, double.Parse(Ui.ById<TextBox>(window, "PropTextBoxHeight").Text, System.Globalization.CultureInfo.InvariantCulture));
                    Assert.True(Ui.ById<ComboBox>(window, "PropTextVerticalAlign").IsEnabled);
                    Assert.Equal("가운데", Ui.ById<ComboBox>(window, "PropTextVerticalAlign").SelectedItem);
                    Assert.False(Ui.ById<TextBlock>(window, "PropTextOverflow").IsVisible);
                });

                // 세로 정렬 아래 → 글이 상자 아래로 가 미리 보기가 바뀐다(실행 취소 한 단계)
                await ui.WaitUntil(() => Differs(beforeHeight, Preview(window).Pixels), Wait, "상자 높이 미리 보기");
                await Task.Delay(500);
                var beforeAlign = ui.Run(() => Preview(window).Pixels);
                var inkBefore = ui.Run(() => InkBounds(window, width));
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "PropTextVerticalAlign"), "아래"));
                await ui.WaitUntil(() => Get(Clip(), "TextVerticalAlign").ToString() == "Bottom", Wait, "세로 정렬 아래");
                await ui.WaitUntil(() => Differs(beforeAlign, Preview(window).Pixels), Wait, "아래 정렬 미리 보기");
                await Task.Delay(500);
                var inkAfter = ui.Run(() => InkBounds(window, width));
                _out.WriteLine("가운데 정렬 글자 " + inkBefore + " · 아래 정렬 " + inkAfter);
                Assert.True(inkAfter.Top > inkBefore.Top + 10, "아래 정렬이면 글이 내려간다");

                // 아래 옆 손잡이를 두 번 누르면 높이가 자동(0)으로 — 세로 정렬은 고를 수 없게
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "EdgePoints");
                    Assert.True((bool)Invoke(overlay, "PointerDoubleClick", now[2]));
                });
                await ui.WaitUntil(() => Value("TextBoxHeight") == 0, Wait, "높이 자동");
                ui.Run(() =>
                {
                    Assert.Equal(boxWidth, Value("TextBoxWidth"));
                    Assert.False(Ui.ById<ComboBox>(window, "PropTextVerticalAlign").IsEnabled);
                    Assert.Equal("0", Ui.ById<TextBox>(window, "PropTextBoxHeight").Text);
                });

                // 높이 칸에 글보다 낮은 값을 적으면 넘침 안내가 보인다
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "PropTextBoxHeight");
                    Ui.Focus(box);
                    box.Text = "40";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => Value("TextBoxHeight") == 40, Wait, "높이 40");
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "PropTextOverflow").IsVisible, Wait, "넘침 안내");

                // 미리 보기에 포커스가 있으면 화살표가 고른 클립을 옮긴다(1px, Shift 10px) — 새로 누를 때마다 실행 취소 한 단계
                var y = ui.Run(() => Value("Y"));
                var x = ui.Run(() => Value("X"));
                ui.Run(() =>
                {
                    Ui.Focus(overlay);
                    Assert.True(Ui.Press(overlay, System.Windows.Input.Key.Right));
                    Assert.True(Ui.Press(overlay, System.Windows.Input.Key.Down, ModifierKeys.Shift));
                });
                await ui.WaitUntil(() => Value("X") == x + 1 && Value("Y") == y + 10, Wait, "화살표로 옮김");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Value("Y") == y && Value("X") == x + 1, Wait, "화살표 실행 취소 한 단계");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

    }
}
