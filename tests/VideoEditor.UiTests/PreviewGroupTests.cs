using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 미리 보기 틀(#34): Ctrl+누르기로 여러 클립을 고르고 함께 옮기기(실행 취소 한 단계).
    /// 포인터는 TransformOverlayTests처럼 리플렉션으로 PointerDown/Move/Up(그리고 Ctrl+누르기는 PointerToggle)을 부른다.
    /// </summary>
    public class PreviewGroupTests
    {
        private readonly ITestOutputHelper _out;

        public PreviewGroupTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().GetMethod(method, Members).Invoke(target, args);
        }

        private static object Member(object target, string property)
        {
            return target.GetType().GetProperty(property, Members).GetValue(target);
        }

        private static async Task AddText(UiThread ui, Window window, string text)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
            ui.Run(() =>
            {
                Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
            });
            await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
        }

        private static object Photo(Window window)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Path") is string path && path.EndsWith("red.png"));
        }

        private static object Text(Window window, string text)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Text") == text);
        }

        [EditorFact]
        public async Task Preview_CtrlClickSelectsSeveral_DragAndArrowsMoveAllInOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                // 동영상 1에 화면을 채우는 사진, 그 위 트랙에 가운데 글자
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "red.png"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == 1, Wait, "가져오기");
                ui.Run(() =>
                {
                    Ui.ById<ListBox>(window, "MediaList").SelectedIndex = 0;
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Count == 1, Wait, "사진 클립");
                const string title = "함께 옮길 글";
                await AddText(ui, window, title);
                var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));
                ui.Run(() => SelectClip(window, n => n.Contains("red.png")));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "사진 틀");
                var project = ui.Run(() => Project(window));
                var (width, height) = ui.Run(() => ((int)Get(project, "Width"), (int)Get(project, "Height")));
                var zoom = ui.Run(() => Math.Min(overlay.ActualWidth / width, overlay.ActualHeight / height));
                var titleCenter = ui.Run(() => (Point)Invoke(overlay, "ToScreen", width / 2.0, height / 2.0));
                var photoOnly = ui.Run(() => (Point)Invoke(overlay, "ToScreen", width * 0.1, height * 0.1));

                // Ctrl+누르기로 글자를 더한다 → 고른 클립마다 점선 틀(손잡이 없음)
                ui.Run(() => Assert.True((bool)Invoke(overlay, "PointerToggle", titleCenter)));
                await ui.WaitUntil(() => (int)Member(overlay, "GroupCount") == 2, Wait, "둘을 고름");
                ui.Run(() =>
                {
                    Assert.False((bool)Get(overlay, "HasFrame"));
                    Assert.Equal(2, ((System.Collections.IEnumerable)Get(Session(window), "SelectedClips")).Cast<object>().Count());
                    Assert.StartsWith("고르기에 더했습니다: " + title, Status(window));
                    Ui.Snap(window, plugin.ShotFolder, "preview-group-selected");
                });

                // 사진만 보이는 자리를 눌러 끌면 둘 다 같은 거리만큼(놓기 전에는 그대로)
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "PointerDown", photoOnly));
                    Invoke(overlay, "PointerMove", new Point(photoOnly.X + 60, photoOnly.Y + 3));
                    Assert.Equal(0.0, (double)Get(Photo(window), "X"));
                    Assert.Equal(0.0, (double)Get(Text(window, title), "X"));
                    Assert.StartsWith("2개 클립 함께 옮기기", Status(window));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                var expected = Math.Round(60 / zoom);
                await ui.WaitUntil(() => (double)Get(Photo(window), "X") == expected && (double)Get(Text(window, title), "X") == expected, Wait, "함께 옮김");
                ui.Run(() =>
                {
                    // 세로는 누른 사진이 가운데에 붙어 0
                    Assert.Equal(0.0, (double)Get(Photo(window), "Y"));
                    Assert.Equal(0.0, (double)Get(Text(window, title), "Y"));
                    Assert.StartsWith("2개 클립을 함께 옮겼습니다", Status(window));
                    Assert.Equal(2, (int)Member(overlay, "GroupCount"));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                // 실행 취소 한 번에 둘 다 돌아간다
                await ui.WaitUntil(() => (double)Get(Photo(window), "X") == 0 && (double)Get(Text(window, title), "X") == 0, Wait, "실행 취소 한 번");

                // 화살표도 함께(누르고 있는 동안은 한 단계)
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "Nudge", 1, 0, false, false));
                    Assert.True((bool)Invoke(overlay, "Nudge", 1, 0, false, true));
                });
                await ui.WaitUntil(() => (double)Get(Photo(window), "X") == 2 && (double)Get(Text(window, title), "X") == 2, Wait, "화살표로 함께");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => (double)Get(Photo(window), "X") == 0 && (double)Get(Text(window, title), "X") == 0, Wait, "화살표 실행 취소 한 번");

                // Ctrl+누르기를 한 번 더 하면 글자가 빠지고 사진 틀 하나로 돌아온다
                ui.Run(() => Assert.True((bool)Invoke(overlay, "PointerToggle", titleCenter)));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && (int)Member(overlay, "GroupCount") == 0, Wait, "하나로");
                Assert.StartsWith("고르기에서 뺐습니다: " + title, ui.Run(() => Status(window)));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
