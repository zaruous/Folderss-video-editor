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
    }
}
