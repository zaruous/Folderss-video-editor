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
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>[효과] 탭: 전환 효과를 클립 사이에 넣고(모두·고른 클립·끌어 놓기) 빼며, 타임라인 위치는 그대로다.</summary>
    public class TransitionTabTests
    {
        private static async Task Import(UiThread ui, Window window, string name)
        {
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, Path.Combine(MediaFolder, name), Wait);
            await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == name), Wait, name + " 가져오기");
            var before = ui.Run(() => Clips(window).Count);
            ui.Run(() =>
            {
                var list = Ui.ById<ListBox>(window, "MediaList");
                list.SelectedItem = list.Items.Cast<FrameworkElement>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == name);
                Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
            });
            await ui.WaitUntil(() => Clips(window).Count == before + 1, Wait, name + " 클립 추가");
        }

        private static object Red(Window window)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png");
        }

        private static string Kind(Window window) { return Get(Red(window), "Transition").ToString(); }

        private static long Length(Window window) { return (long)Get(Red(window), "TransitionIn"); }

        private static void Choose(Window window, string kind)
        {
            var list = Ui.ById<ListBox>(window, "TransitionList");
            list.SelectedItem = list.Items.Cast<ListBoxItem>().First(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "Transition_" + kind);
        }

        [EditorFact]
        public async Task EffectsTab_AddsTransitionsWithoutMovingClips()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                // 주 트랙: 시험 동영상(3초) 뒤에 빨간 사진(5초)이 붙는다
                await Import(ui, window, "testsrc.mp4");
                await Import(ui, window, "red.png");
                var start = ui.Run(() => (long)Get(Red(window), "Start"));
                Assert.Equal(90, start);

                // [효과] 탭: 닦아내기 1초를 모든 클립 사이에
                ui.Run(() =>
                {
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "EffectsTab");
                });
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "TransitionList"), Wait, "효과 목록");
                ui.Run(() =>
                {
                    Choose(window, "WipeLeft");
                    Ui.ById<TextBox>(window, "TransitionSeconds").Text = "1";
                    Ui.Click(Ui.ById<Button>(window, "TransitionAllButton"));
                });
                await ui.WaitUntil(() => Kind(window) == "WipeLeft" && Length(window) == 30, Wait, "모든 클립 사이에 닦아내기");
                ui.Run(() =>
                {
                    Assert.StartsWith("'닦아내기 ←' 전환을 '동영상 1'의 클립 사이 1곳에", Status(window));
                    // 넣은 클립이 골라지고, 타임라인 클립 이름에도 전환이 보인다
                    Assert.True((bool)Session(window).GetType().GetMethod("IsSelected").Invoke(Session(window), new[] { Red(window) }));
                    Assert.Contains(Clips(window), n => n.StartsWith("동영상 1 · red.png", StringComparison.Ordinal) && n.EndsWith("· 전환 닦아내기 ← 1초", StringComparison.Ordinal));
                });

                // 사진을 고르고 원 열기를 그 클립에
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("red.png"));
                    Choose(window, "CircleOpen");
                    Ui.Click(Ui.ById<Button>(window, "TransitionApplyButton"));
                });
                await ui.WaitUntil(() => Kind(window) == "CircleOpen", Wait, "고른 클립에 원 열기");

                // 타임라인을 맞춤(작게 보기)으로 해도 경계의 전환 배지를 누르면 전환이 든 클립(사진)이 골라진다
                ui.Run(() =>
                {
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Shift);
                    Session(window).GetType().GetMethod("ClearSelection").Invoke(Session(window), null);
                    var canvas = Canvas(window);
                    canvas.UpdateLayout();
                    var row = ((System.Collections.IEnumerable)canvas.GetType().GetMethod("Rows").Invoke(canvas, null)).Cast<object>().First(r => (string)Get(r, "Label") == "동영상 1");
                    var badge = (Rect)canvas.GetType().GetMethod("BadgeRect", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(canvas, new[] { row, Red(window) });
                    Assert.True(badge.Width >= 20, "배지 폭 " + badge.Width);
                    canvas.GetType().GetMethod("PointerDown", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(canvas, new object[] { new Point(badge.Left + badge.Width / 2, badge.Top + badge.Height / 2), ModifierKeys.None });
                    Assert.True((bool)Session(window).GetType().GetMethod("IsSelected").Invoke(Session(window), new[] { Red(window) }));
                    Assert.StartsWith("전환: 원 열기 · 1초", Status(window));
                    Ui.Snap(window, plugin.ShotFolder, "transition-badge-fit");
                });

                // 효과를 클립에 끌어 놓기(끌어 놓기 이벤트를 만들 수 없어 처리기를 직접 부른다)
                ui.Run(() =>
                {
                    var canvas = Canvas(window);
                    var handler = (Delegate)canvas.GetType().GetField("TransitionDropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
                    handler.DynamicInvoke("SlideUp|0.5", Get(Red(window), "Id"));
                });
                await ui.WaitUntil(() => Kind(window) == "SlideUp" && Length(window) == 15, Wait, "끌어 놓은 밀어내기");

                // 속성 패널 [화면] 탭에도 같은 효과가 보인다
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").SelectedIndex = 0);
                await ui.WaitFor(() => Ui.ById<ComboBox>(window, "PropTransitionKind") is ComboBox shown && shown.IsVisible ? shown : null, Wait, "전환 효과 칸");
                Assert.Equal("밀어내기 ↑", ui.Run(() => (string)Ui.ById<ComboBox>(window, "PropTransitionKind").SelectedItem));

                // 전환 한가운데를 미리 보기로 그린다
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "TimecodeBox");
                    Ui.Focus(box);
                    box.Text = "00:00:03:07";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "전환 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "transition-effects-tab"));

                // 빼기 → 실행 취소로 되돌리기, 그동안 위치는 그대로
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "TransitionRemoveButton")));
                await ui.WaitUntil(() => Length(window) == 0, Wait, "전환 빼기");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Length(window) == 15, Wait, "실행 취소");
                Assert.Equal(start, ui.Run(() => (long)Get(Red(window), "Start")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
