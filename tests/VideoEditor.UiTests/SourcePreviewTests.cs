using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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
    /// 원본 보기: 미디어 패널에서 사진을 한 번 누르면 미리 보기 화면에 그 사진 그대로(Esc로 돌아감),
    /// 타임라인에서 재생 헤드 밖의 클립을 누르면 그 클립 하나를 그려 보이고(재생 헤드는 그대로), 재생 헤드 안의 클립·재생 헤드 이동이면 재생 헤드 화면으로.
    /// </summary>
    public class SourcePreviewTests
    {
        private readonly ITestOutputHelper _out;

        public SourcePreviewTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static FrameworkElement MediaRow(Window window, string name)
        {
            return Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().FirstOrDefault(i => AutomationProperties.GetName(i) == name);
        }

        private static async Task Import(UiThread ui, Window window, string name)
        {
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, Path.Combine(MediaFolder, name), Wait);
            await ui.WaitUntil(() => MediaRow(window, name) != null, Wait, name + " 가져오기");
        }

        private static async Task AddToTimeline(UiThread ui, Window window, string name)
        {
            var before = ui.Run(() => Clips(window).Count);
            ui.Run(() =>
            {
                var list = Ui.ById<ListBox>(window, "MediaList");
                list.SelectedItem = MediaRow(window, name);
                Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
            });
            await ui.WaitUntil(() => Clips(window).Count == before + 1, Wait, name + " 클립 추가");
        }

        /// <summary>미디어 목록 칸을 한 번 누른다(끌지 않고 놓기 — UI 스레드). 실제 마우스처럼 터널 이벤트(PreviewMouseDown/Up)로 올린다 — 왼쪽 단추 이벤트는 그 길의 요소마다 다시 올려진다.</summary>
        private static void ClickMedia(Window window, string name)
        {
            Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 0;
            var list = Ui.ById<ListBox>(window, "MediaList");
            var row = MediaRow(window, name);
            list.SelectedItem = row;
            window.UpdateLayout();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
            foreach (var routed in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
                item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed, Source = item });
        }

        /// <summary>타임라인 클립을 누른다(PointerDown/Up — UI 스레드).</summary>
        private static void ClickClip(Window window, string path)
        {
            var clip = ProjectClips(window).First(c => ((string)Get(c, "Path") ?? "").EndsWith(path));
            var rect = ClipBounds(window, clip);
            var point = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var canvas = Canvas(window);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            canvas.GetType().GetMethod("PointerDown", flags).Invoke(canvas, new object[] { point, ModifierKeys.None });
            canvas.GetType().GetMethod("PointerUp", flags).Invoke(canvas, new object[] { point, ModifierKeys.None });
        }

        private static string SourceLabel(Window window)
        {
            var label = Ui.ById<FrameworkElement>(window, "SourcePreviewLabel");
            return label.Visibility == Visibility.Visible ? AutomationProperties.GetName(label) : null;
        }

        /// <summary>미리 보기 화면 가운데 픽셀(R, G, B).</summary>
        private static (int R, int G, int B) Center(Window window)
        {
            var bitmap = Ui.ById<Image>(window, "PreviewImage").Source as BitmapSource;
            if (bitmap == null)
                return (-1, -1, -1);
            var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
            return (pixel[2], pixel[1], pixel[0]);
        }

        private static bool Red((int R, int G, int B) p)
        {
            return p.R > 200 && p.G < 60 && p.B < 60;
        }

        private static long Playhead(Window window)
        {
            return (long)Get(Session(window), "Playhead");
        }

        [EditorFact]
        public async Task MediaClick_ShowsThePicture_ClipOutsidePlayheadShowsTheClip_EscAndPlayheadReturn()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, "red.png");
                await Import(ui, window, "testsrc.mp4");

                // 미디어 한 번 누르기 → 타임라인이 비어 있어도 사진 그대로
                ui.Run(() => ClickMedia(window, "red.png"));
                await ui.WaitUntil(() => SourceLabel(window)?.StartsWith("원본 · red.png · 400×300") == true && Red(Center(window)), Wait, "사진 원본 보기");
                ui.Run(() =>
                {
                    Assert.Equal(Visibility.Collapsed, Ui.ById<FrameworkElement>(window, "PreviewPlaceholder").Visibility);
                    Assert.Equal(Visibility.Visible, Ui.ById<FrameworkElement>(window, "SourcePreviewClose").Visibility);
                    Ui.Snap(window, plugin.ShotFolder, "source-media");
                    Key(window, System.Windows.Input.Key.Escape);
                });
                await ui.WaitUntil(() => SourceLabel(window) == null, Wait, "Esc로 닫기");
                ui.Run(() => Assert.Equal(Visibility.Visible, Ui.ById<FrameworkElement>(window, "PreviewPlaceholder").Visibility));

                // 동영상 미디어는 대표 프레임
                ui.Run(() => ClickMedia(window, "testsrc.mp4"));
                await ui.WaitUntil(() => SourceLabel(window)?.StartsWith("원본 · testsrc.mp4") == true && !SourceLabel(window).Contains("불러오는 중"), Wait, "동영상 대표 프레임");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "SourcePreviewClose")));
                await ui.WaitUntil(() => SourceLabel(window) == null, Wait, "× 단추로 닫기");

                // 타임라인: 동영상(0~3초) 뒤에 사진. 재생 헤드는 0 — 사진 클립은 재생 헤드 밖
                await AddToTimeline(ui, window, "testsrc.mp4");
                await AddToTimeline(ui, window, "red.png");
                ui.Run(() => Assert.Equal(0, Playhead(window)));
                ui.Run(() => ClickClip(window, "red.png"));
                await ui.WaitUntil(() => SourceLabel(window)?.StartsWith("원본 · red.png") == true && Red(Center(window)), Wait, "클립 원본 보기");
                ui.Run(() =>
                {
                    Assert.Equal(0, Playhead(window));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<FrameworkElement>(window, "TransformOverlay").Visibility);
                    Ui.Snap(window, plugin.ShotFolder, "source-clip");
                });

                // 재생 헤드 안의 클립을 누르면 재생 헤드 화면(이미 보이므로 원본 보기를 닫는다)
                ui.Run(() => ClickClip(window, "testsrc.mp4"));
                await ui.WaitUntil(() => SourceLabel(window) == null && !Red(Center(window)), Wait, "재생 헤드 화면으로");
                ui.Run(() => Assert.Equal(Visibility.Visible, Ui.ById<FrameworkElement>(window, "TransformOverlay").Visibility));

                // 다시 열고 재생 헤드를 옮기면 닫힌다
                ui.Run(() => ClickClip(window, "red.png"));
                await ui.WaitUntil(() => SourceLabel(window) != null, Wait, "다시 원본 보기");
                ui.Run(() => Key(window, System.Windows.Input.Key.Right));
                await ui.WaitUntil(() => SourceLabel(window) == null && Playhead(window) == 1, Wait, "재생 헤드 이동으로 닫기");
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>미리 보기 화면 그림 전체의 해시(바뀌었는지 보기).</summary>
        private static long PictureHash(Window window)
        {
            var bitmap = Ui.ById<Image>(window, "PreviewImage").Source as BitmapSource;
            if (bitmap == null)
                return 0;
            var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            long hash = 17;
            foreach (var b in pixels)
                hash = hash * 31 + b;
            return hash;
        }

        private static string SourceTime(Window window)
        {
            return AutomationProperties.GetName(Ui.ById<FrameworkElement>(window, "SourceTimecode")) ?? "";
        }

        private static bool SourcePlaying(Window window)
        {
            var glyph = (TextBlock)Ui.ById<Button>(window, "SourcePlayButton").Content;
            return glyph.Text == "";
        }

        private static void SeekSource(Window window, double frame)
        {
            Ui.ById<Slider>(window, "SourceSlider").Value = frame;
        }

        private static object LastClip(Window window, string path)
        {
            return ProjectClips(window).LastOrDefault(c => ((string)Get(c, "Path") ?? "").EndsWith(path));
        }

        [EditorFact]
        public async Task VideoSource_ScrubPlayInOut_InsertsOnlyTheRange_IOOutsideSetTimelineMarks()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, "testsrc.mp4");
                ui.Run(() => ClickMedia(window, "testsrc.mp4"));
                await ui.WaitUntil(() => SourceLabel(window)?.StartsWith("원본 · testsrc.mp4") == true && !SourceLabel(window).Contains("불러오는 중"), Wait, "동영상 원본 보기");
                ui.Run(() => Assert.Equal(Visibility.Visible, Ui.ById<FrameworkElement>(window, "SourceTransport").Visibility));
                var max = ui.Run(() => Ui.ById<Slider>(window, "SourceSlider").Maximum);
                Assert.True(max >= 60, "원본 길이(프레임): " + max);

                // 원본 위치를 옮기면 그 프레임 — 재생 헤드는 그대로, 원본 보기도 그대로
                await ui.WaitUntil(() => PictureHash(window) != 0, Wait, "첫 그림");
                var before = ui.Run(() => PictureHash(window));
                var time = ui.Run(() => SourceTime(window));
                ui.Run(() => SeekSource(window, max - 5));
                await ui.WaitUntil(() => PictureHash(window) != before && SourceTime(window) != time, Wait, "원본 위치 옮기기");
                ui.Run(() =>
                {
                    Assert.Equal(0, Playhead(window));
                    Assert.NotNull(SourceLabel(window));
                    Ui.Snap(window, plugin.ShotFolder, "source-scrub");
                });

                // Space = 원본 재생/멈춤(타임라인 재생이 아님 — 원본 보기가 닫히지 않고 재생 헤드도 그대로)
                ui.Run(() =>
                {
                    SeekSource(window, 0);
                    Key(window, System.Windows.Input.Key.Space);
                });
                await ui.WaitUntil(() => SourcePlaying(window), Wait, "Space로 원본 재생");
                await ui.WaitUntil(() => !SourceTime(window).StartsWith("00:00:00:00"), Wait, "원본 재생이 나아감");
                // 3초짜리라 그 사이 끝까지 가 스스로 멈췄으면 누르지 않는다(누르면 처음부터 다시 재생)
                ui.Run(() =>
                {
                    if (SourcePlaying(window))
                        Key(window, System.Windows.Input.Key.Space);
                });
                await ui.WaitUntil(() => !SourcePlaying(window), Wait, "Space로 원본 멈춤");
                ui.Run(() =>
                {
                    Assert.Equal(0, Playhead(window));
                    Assert.NotNull(SourceLabel(window));
                    Assert.Null(Get(Project(window), "MarkIn"));
                });

                // I/O = 원본 In/Out(타임라인 In/Out은 그대로) → [타임라인에 넣기] = 그 구간만, 실행 취소 한 번에 사라짐
                ui.Run(() =>
                {
                    SeekSource(window, 15);
                    Key(window, System.Windows.Input.Key.I);
                    SeekSource(window, 44);
                    Key(window, System.Windows.Input.Key.O);
                    Assert.Null(Get(Project(window), "MarkIn"));
                    Assert.Null(Get(Project(window), "MarkOut"));
                    Assert.Contains("In 00:00:00:15", SourceTime(window));
                    Ui.Click(Ui.ById<Button>(window, "SourceInsertButton"));
                });
                await ui.WaitUntil(() => LastClip(window, "testsrc.mp4") != null, Wait, "구간 넣기");
                ui.Run(() =>
                {
                    var clip = LastClip(window, "testsrc.mp4");
                    Assert.Equal(15L, (long)Get(clip, "SourceIn"));
                    Assert.Equal(45L, (long)Get(clip, "SourceOut"));
                    Assert.Equal(30L, (long)Get(clip, "Duration"));
                    Assert.NotNull(SourceLabel(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => ProjectClips(window).Count == 0, Wait, "실행 취소 한 번");

                // 미디어 [타임라인에 추가](두 번 누르기와 같은 길)도 원본 In/Out 구간만
                ui.Run(() =>
                {
                    Ui.ById<ListBox>(window, "MediaList").SelectedItem = MediaRow(window, "testsrc.mp4");
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => LastClip(window, "testsrc.mp4") != null, Wait, "[+]로 구간 넣기");
                ui.Run(() =>
                {
                    Assert.Equal(15L, (long)Get(LastClip(window, "testsrc.mp4"), "SourceIn"));
                    Assert.Equal(45L, (long)Get(LastClip(window, "testsrc.mp4"), "SourceOut"));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => ProjectClips(window).Count == 0, Wait, "다시 실행 취소");

                // 원본 보기를 닫으면 I/O는 다시 타임라인 In/Out
                ui.Run(() => Key(window, System.Windows.Input.Key.Escape));
                await ui.WaitUntil(() => SourceLabel(window) == null, Wait, "Esc로 닫기");
                ui.Run(() =>
                {
                    Key(window, System.Windows.Input.Key.Right);
                    Key(window, System.Windows.Input.Key.I);
                    Key(window, System.Windows.Input.Key.Right);
                    Key(window, System.Windows.Input.Key.Right);
                    Key(window, System.Windows.Input.Key.O);
                    Assert.Equal(1L, (long?)Get(Project(window), "MarkIn"));
                    Assert.Equal(3L, (long?)Get(Project(window), "MarkOut"));
                });
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
