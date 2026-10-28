using System;
using System.Collections;
using System.Collections.Generic;
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
    /// 사용자 키프레임(#4): 속성 [화면] 탭의 ◆ 단추로 넣고 빼기, 키프레임이 있는 값은 재생 헤드 시각의 값을 보이고 고치면 그 시각의 키프레임,
    /// 미리 보기에서 끌면 그 시각의 키프레임, 타임라인 ◆ 표시·끌어 옮기기, 이징, 실행 취소 한 단계씩, 좁은 패널(1280×800)에서 줄 모양.
    /// </summary>
    public class KeyframeTests
    {
        private readonly ITestOutputHelper _out;

        public KeyframeTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return Call(target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public), target, args);
        }

        private static List<object> Keys(object clip)
        {
            return ((IEnumerable)Get(clip, "Keyframes"))?.Cast<object>().ToList() ?? new List<object>();
        }

        /// <summary>위치 키프레임: (시각, X, Y, 이징).</summary>
        private static List<(long Frame, double X, double Y, string Ease)> Positions(object clip)
        {
            return Keys(clip).Where(k => Get(k, "Property").ToString() == "Position")
                .Select(k => ((long)Get(k, "Frame"), (double)Get(k, "Value"), (double)Get(k, "Y"), Get(k, "Ease").ToString())).OrderBy(k => k.Item1).ToList();
        }

        private static void SetPlayhead(Window window, long frame)
        {
            var session = Session(window);
            session.GetType().GetProperty("Playhead").SetValue(session, frame);
        }

        private static string Mark(Window window, string id)
        {
            var button = Ui.ById<Button>(window, id);
            return ((TextBlock)button.Content).Text;
        }

        private static void Type(Window window, string id, string text)
        {
            var box = Ui.ById<TextBox>(window, id);
            Ui.Focus(box);
            box.Text = text;
            Ui.Press(box, System.Windows.Input.Key.Enter);
        }

        [EditorFact]
        public async Task KeyButton_PanelValues_PreviewDrag_TimelineMarks_Undo()
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
                ui.Run(() => SelectClip(window, n => n.Contains("red.png")));
                await ui.WaitFor(() => Ui.ById<Button>(window, "PropXKey") is Button b && b.IsVisible ? b : null, Wait, "◆ 단추");
                object Photo() => ProjectClips(window)[0];

                // 좁은 패널(1280×800 창 — 속성 약 250px): 이름·숫자 칸·단위·◆가 한 줄, 슬라이더는 아랫줄에 쓸 만한 폭
                ui.Run(() =>
                {
                    window.UpdateLayout();
                    var box = Ui.ById<TextBox>(window, "PropX");
                    var key = Ui.ById<Button>(window, "PropXKey");
                    var slider = Ui.ById<Slider>(window, "PropXSlider");
                    var panel = Ui.ById<TabControl>(window, "PropertyTabs");
                    var keyRight = key.TranslatePoint(new Point(key.ActualWidth, 0), panel).X;
                    _out.WriteLine("속성 탭 폭 " + panel.ActualWidth.ToString("0") + " · 숫자 칸 " + box.ActualWidth.ToString("0") + " · 슬라이더 " + slider.ActualWidth.ToString("0")
                                   + " · ◆ 오른쪽 " + keyRight.ToString("0"));
                    Assert.True(keyRight <= panel.ActualWidth, "◆가 패널 밖");
                    Assert.True(box.ActualWidth >= 50, "숫자 칸이 좁음");
                    Assert.True(slider.ActualWidth >= 100, "슬라이더가 좁음");
                    Assert.InRange(Math.Abs(box.TranslatePoint(new Point(0, 0), panel).Y - key.TranslatePoint(new Point(0, 0), panel).Y), 0, 3);
                    Assert.Equal("◇", Mark(window, "PropXKey"));
                    Ui.Snap(window, plugin.ShotFolder, "keyframe-row");
                    // 재생 헤드 0에 위치 키프레임(지금 값 0, 0)
                    SetPlayhead(window, 0);
                    Ui.Click(Ui.ById<Button>(window, "PropXKey"));
                });
                await ui.WaitUntil(() => Positions(Photo()).Count == 1, Wait, "키프레임 하나");
                ui.Run(() =>
                {
                    Assert.Equal((0L, 0.0, 0.0, "Linear"), Positions(Photo())[0]);
                    Assert.Equal("◆", Mark(window, "PropXKey"));
                    Assert.Equal("◆", Mark(window, "PropYKey"));
                    Assert.Equal("◇", Mark(window, "PropScaleKey"));
                    Assert.StartsWith("위치 키프레임을 넣었습니다", Status(window));
                });

                // 1초(30장)에서 X를 300으로 적으면 그 시각에 새 키프레임(고정 값은 그대로)
                ui.Run(() => SetPlayhead(window, 30));
                await ui.WaitUntil(() => Mark(window, "PropXKey") == "◇", Wait, "30장에는 키프레임 없음");
                ui.Run(() => Type(window, "PropX", "300"));
                await ui.WaitUntil(() => Positions(Photo()).Count == 2, Wait, "키프레임 둘");
                ui.Run(() =>
                {
                    Assert.Equal((30L, 300.0, 0.0, "Linear"), Positions(Photo())[1]);
                    Assert.Equal(0.0, (double)Get(Photo(), "X"));
                    Assert.Contains(Clips(window), n => n.Contains("키프레임 2"));
                });

                // 재생 헤드를 15장으로 옮기면 칸이 사이 값(150)을 보인다
                ui.Run(() => SetPlayhead(window, 15));
                await ui.WaitUntil(() => Ui.ById<TextBox>(window, "PropX").Text == "150", Wait, "사이 값 150");

                // 미리 보기에서 끌면 그 시각(15장)의 키프레임을 만든다(붙기는 끔)
                var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "틀");
                var zoom = ui.Run(() =>
                {
                    var project = Project(window);
                    return Math.Min(overlay.ActualWidth / (int)Get(project, "Width"), overlay.ActualHeight / (int)Get(project, "Height"));
                });
                ui.Run(() =>
                {
                    var corners = (Point[])Invoke(overlay, "HandlePoints");
                    var center = new Point((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
                    overlay.GetType().GetProperty("TestModifiers", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(overlay, ModifierKeys.Alt);
                    Assert.True((bool)Invoke(overlay, "PointerDown", center));
                    Invoke(overlay, "PointerMove", new Point(center.X + 50 * zoom, center.Y + 20 * zoom));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                    overlay.GetType().GetProperty("TestModifiers", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(overlay, null);
                });
                await ui.WaitUntil(() => Positions(Photo()).Count == 3, Wait, "끌어 만든 키프레임");
                ui.Run(() =>
                {
                    Assert.Equal((15L, 200.0, 20.0), (Positions(Photo())[1].Frame, Positions(Photo())[1].X, Positions(Photo())[1].Y));
                    Assert.Equal(0.0, (double)Get(Photo(), "X"));
                });

                // 이징: 재생 헤드(15장)의 키프레임을 "서서히 끝"으로
                await ui.WaitUntil(() => Ui.ById<ComboBox>(window, "PropKeyEase") is ComboBox c && c.IsEnabled && c.IsVisible, Wait, "이징 칸");
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "PropKeyEase"), "서서히 끝"));
                await ui.WaitUntil(() => Positions(Photo())[1].Ease == "Out", Wait, "이징");

                // 타임라인: 고른 클립 위에 ◆ 셋, 가운데 것을 오른쪽으로 끌면 그 키프레임이 옮겨진다(놓을 때 한 단계)
                var canvas = ui.Run(() => Canvas(window));
                var id = ui.Run(() => (string)Get(Photo(), "Id"));
                var marks = ui.Run(() => (Point[])Invoke(canvas, "KeyframePoints", id));
                Assert.Equal(3, marks.Length);
                var perFrame = (marks[2].X - marks[0].X) / 30;
                ui.Run(() =>
                {
                    Ui.Snap(window, plugin.ShotFolder, "keyframe-marks");
                    Drag(window, marks[1], new Point(marks[1].X + 5.4 * perFrame, marks[1].Y), ModifierKeys.None);
                });
                await ui.WaitUntil(() => Positions(Photo()).Select(k => k.Frame).SequenceEqual(new long[] { 0, 20, 30 }), Wait, "◆ 옮김");
                Assert.Equal("Out", ui.Run(() => Positions(Photo())[1].Ease));
                Assert.Equal(20L, ui.Run(() => (long)Get(Session(window), "Playhead")));

                // 실행 취소 한 번에 ◆가 15장으로
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Positions(Photo()).Select(k => k.Frame).SequenceEqual(new long[] { 0, 15, 30 }), Wait, "실행 취소");

                // 30장에서 ◆를 누르면 뺀다
                ui.Run(() => SetPlayhead(window, 30));
                await ui.WaitUntil(() => Mark(window, "PropXKey") == "◆", Wait, "30장 ◆");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "PropXKey")));
                await ui.WaitUntil(() => Positions(Photo()).Count == 2, Wait, "◆ 뺌");
                ui.Run(() =>
                {
                    Assert.Equal("◇", Mark(window, "PropXKey"));
                    Assert.StartsWith("위치 키프레임을 뺐습니다", Status(window));
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
