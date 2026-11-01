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
    /// 타임라인 Shift+누르기 범위 고르기와 속성 패널의 같은 종류 함께 고치기: 글자 셋을 범위로 고르고 글자 크기를 한 번 바꾸면 셋 모두(실행 취소 한 단계).
    /// </summary>
    public class MultiEditTests
    {
        private readonly ITestOutputHelper _out;

        public MultiEditTests(ITestOutputHelper output)
        {
            _out = output;
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

        private static object Text(Window window, string text)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Text") == text);
        }

        private static Point Center(Window window, string text)
        {
            var rect = ClipBounds(window, Text(window, text));
            return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        }

        private static void Press(Window window, Point point, ModifierKeys modifiers)
        {
            var canvas = Canvas(window);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            canvas.GetType().GetMethod("PointerDown", flags).Invoke(canvas, new object[] { point, modifiers });
            canvas.GetType().GetMethod("PointerUp", flags).Invoke(canvas, new object[] { point, modifiers });
        }

        [EditorFact]
        public async Task ShiftClick_SelectsRange_AndFontSizeChangesAllInOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var names = new[] { "첫째 글", "둘째 글", "셋째 글" };
                foreach (var name in names)
                    await AddText(ui, window, name);
                var before = ui.Run(() => names.Select(n => (double)Get(Text(window, n), "FontSize")).ToList());

                ui.Run(() =>
                {
                    Press(window, Center(window, names[0]), ModifierKeys.None);
                    Press(window, Center(window, names[2]), ModifierKeys.Shift);
                    Assert.Equal(3, ((System.Collections.IEnumerable)Get(Session(window), "SelectedClips")).Cast<object>().Count());
                    Assert.StartsWith("3개 클립을 골랐습니다", Status(window));
                });
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropFontSize"), Wait, "글자 크기 칸");
                ui.Run(() =>
                {
                    // 글 내용 칸은 숨긴다(한 글로 모두 덮어쓰지 않게)
                    Assert.False(Ui.ById<TextBox>(window, "PropText").IsVisible);
                    Ui.Snap(window, plugin.ShotFolder, "multi-edit-text");
                    var box = Ui.ById<TextBox>(window, "PropFontSize");
                    Ui.Focus(box);
                    box.Text = "123";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => names.All(n => (double)Get(Text(window, n), "FontSize") == 123), Wait, "셋 모두 글자 크기");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => names.Select(n => (double)Get(Text(window, n), "FontSize")).SequenceEqual(before), Wait, "실행 취소 한 번에 셋 모두");
                // 글 내용은 그대로
                ui.Run(() => Assert.All(names, n => Assert.NotNull(Text(window, n))));
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
