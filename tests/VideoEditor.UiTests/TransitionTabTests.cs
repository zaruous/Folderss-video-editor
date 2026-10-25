using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>[전환] 탭: 전환 효과를 클립 사이에 넣고(모두·고른 클립·끌어 놓기) 빼며, 타임라인 위치는 그대로다.</summary>
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
            Ui.ById<ListBox>(window, "TransitionList").SelectedItem = Tile(window, kind);
        }

        private static ListBoxItem Tile(Window window, string id)
        {
            return Ui.ById<ListBox>(window, "TransitionList").Items.Cast<ListBoxItem>()
                .FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "Transition_" + id);
        }

        private static List<string> Tiles(Window window)
        {
            return Ui.ById<ListBox>(window, "TransitionList").Items.Cast<ListBoxItem>().Select(System.Windows.Automation.AutomationProperties.GetAutomationId).ToList();
        }

        /// <summary>효과 썸네일 그림(아직 없으면 null).</summary>
        private static ImageSource Thumbnail(Window window, string id)
        {
            var tile = Tile(window, id);
            return tile == null ? null : ((Image)((Border)((StackPanel)tile.Content).Children[0]).Child).Source;
        }

        [EditorFact]
        public async Task TransitionsTab_AddsTransitionsWithoutMovingClips()
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
                // 사진은 기본으로 화면을 꽉 채운다(설정 [사진을 타임라인에 놓을 때])
                Assert.Equal("Fill", ui.Run(() => Get(Red(window), "Fit").ToString()));

                // [전환] 탭(동영상 효과는 [효과] 탭에 따로 있다): 닦아내기 1초를 모든 클립 사이에
                ui.Run(() =>
                {
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "TransitionsTab");
                });
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "TransitionList"), Wait, "효과 목록");
                ui.Run(() =>
                {
                    // 전환 목록은 [전환] 탭 안에만 있고(탭 전체가 스크롤), 동영상 효과 목록은 없다
                    var tab = Ui.ById<TabItem>(window, "TransitionsTab");
                    Assert.True(tab.IsSelected);
                    Assert.Same(tab.Content, Ui.ById<ScrollViewer>(window, "TransitionsTabScroll"));
                    Assert.NotNull(Ui.ById<ListBox>((DependencyObject)tab.Content, "TransitionList"));
                    Assert.Null(Ui.ById<ListBox>((DependencyObject)tab.Content, "VideoEffectList"));
                });

                // 효과마다 실제로 그린 썸네일(미리 보기 띠의 가운데 칸)이 붙고, 마우스를 올리면 칸이 바뀌며 움직인다
                await ui.WaitUntil(() => Thumbnail(window, "WipeLeft") != null && Thumbnail(window, "Heart") != null, Wait, "효과 썸네일");
                var still = ui.Run(() => Thumbnail(window, "WipeLeft"));
                var first = ui.Run(() =>
                {
                    Tile(window, "WipeLeft").RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                    return Thumbnail(window, "WipeLeft");
                });
                Assert.NotSame(still, first);
                await ui.WaitUntil(() => !ReferenceEquals(Thumbnail(window, "WipeLeft"), first), Wait, "썸네일 움직임");
                ui.Run(() => Tile(window, "WipeLeft").RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent }));
                Assert.Same(still, ui.Run(() => Thumbnail(window, "WipeLeft")));

                // 묶음으로 거르기
                ui.Run(() => Ui.ById<ComboBox>(window, "TransitionGroup").SelectedItem = "모양");
                await ui.WaitUntil(() => Tiles(window).Contains("Transition_Heart") && !Tiles(window).Contains("Transition_WipeLeft"), Wait, "모양 묶음");
                await ui.WaitUntil(() => Thumbnail(window, "Diamond") != null, Wait, "모양 썸네일");
                ui.Run(() =>
                {
                    // 전환 목록이 보이게(길이·묶음·단추 줄 아래) 스크롤해 찍는다
                    Ui.ById<ListBox>(window, "TransitionList").BringIntoView();
                    Ui.Snap(window, plugin.ShotFolder, "transition-thumbnails-shapes");
                });
                ui.Run(() => Ui.ById<ComboBox>(window, "TransitionGroup").SelectedItem = "모든 효과");
                await ui.WaitUntil(() => Tiles(window).Contains("Transition_WipeLeft"), Wait, "모든 효과");
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

                // 사용자 효과: 효과 폴더에 JSON을 넣고 [다시 읽기] → 효과가 늘어난다(잘못된 정의는 건너뛰고 알림)
                var folder = Path.Combine(plugin.Manager.DataDirectory, "transitions");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "ui-test.json");
                File.WriteAllText(file, "{ \"transitions\": [ { \"id\": \"UiRing\", \"name\": \"시험 고리\", \"expr\": \"if(lt(abs(hypot(X-W/2,Y-H/2)/H-0.3),(1-P)*0.8),B,A)\" },"
                    + " { \"id\": \"Bad\", \"name\": \"나쁨\", \"expr\": \"while(1,A)\" } ] }");
                try
                {
                    ui.Run(() => Ui.Click(Ui.ById<Button>(window, "TransitionReloadButton")));
                    await ui.WaitUntil(() => Tiles(window).Contains("Transition_UiRing"), Wait, "사용자 효과");
                    ui.Run(() =>
                    {
                        Assert.StartsWith("전환 효과를 다시 읽었습니다", Status(window));
                        Assert.Contains("건너뜀 1개", Status(window));
                        Assert.Equal(Visibility.Visible, Ui.ById<TextBlock>(window, "TransitionProblems").Visibility);
                        SelectClip(window, n => n.Contains("red.png"));
                        Choose(window, "UiRing");
                        Ui.Click(Ui.ById<Button>(window, "TransitionApplyButton"));
                    });
                    await ui.WaitUntil(() => Kind(window) == "UiRing", Wait, "사용자 효과 넣기");
                    await ui.WaitUntil(() => Thumbnail(window, "UiRing") != null, Wait, "사용자 효과 썸네일");
                }
                finally
                {
                    File.Delete(file);
                }
                // 효과 파일이 없어져도 클립에 정의가 함께 있어 이름과 그리기가 그대로다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "TransitionReloadButton")));
                await ui.WaitUntil(() => !Tiles(window).Contains("Transition_UiRing"), Wait, "사용자 효과 빠짐");
                ui.Run(() =>
                {
                    Assert.NotNull(Get(Red(window), "TransitionDef"));
                    Assert.Contains(Clips(window), n => n.StartsWith("동영상 1 · red.png", StringComparison.Ordinal) && n.EndsWith("· 전환 시험 고리 1초", StringComparison.Ordinal));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<TextBlock>(window, "TransitionProblems").Visibility);
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
