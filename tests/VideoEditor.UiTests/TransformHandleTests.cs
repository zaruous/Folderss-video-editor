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
    /// 미리 보기 틀의 확장 손잡이(#4): 위쪽 회전 손잡이(15° 단위, Shift는 자유롭게, Esc 취소, 실행 취소 한 단계)와
    /// 옮길 때 붙기(화면 가장자리·다른 클립의 가장자리·가운데, 안내선, Alt는 붙지 않음). 끌기는 <see cref="TransformOverlayTests"/>처럼 리플렉션 Pointer로.
    /// </summary>
    public class TransformHandleTests
    {
        private readonly ITestOutputHelper _out;

        public TransformHandleTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return Call(target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public), target, args);
        }

        private static void SetModifiers(object overlay, ModifierKeys? modifiers)
        {
            overlay.GetType().GetProperty("TestModifiers", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(overlay, modifiers);
        }

        /// <summary>사진(red.png)을 넣고 크기 50%로 해 고른다 — 틀 위쪽 회전 손잡이가 미리 보기 안에 보이게.</summary>
        private static async Task<(Window Window, Canvas Overlay)> PhotoAtHalf(UiThread ui, PluginUnderTest plugin)
        {
            var window = await Open(ui, plugin);
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
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropScale"), Wait, "크기 칸");
            ui.Run(() =>
            {
                var box = Ui.ById<TextBox>(window, "PropScale");
                Ui.Focus(box);
                box.Text = "50";
                Ui.Press(box, System.Windows.Input.Key.Enter);
            });
            await ui.WaitUntil(() => (double)Get(ProjectClips(window)[0], "Scale") == 0.5, Wait, "크기 50%");
            var overlay = ui.Run(() => Ui.ById<Canvas>(window, "TransformOverlay"));
            await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "틀");
            return (window, overlay);
        }

        private static Point Center(Point[] corners)
        {
            return new Point((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
        }

        /// <summary>가운데(center) 둘레로 점을 시계 방향 degrees만큼 돌린 자리.</summary>
        private static Point Turn(Point center, Point point, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            var (dx, dy) = (point.X - center.X, point.Y - center.Y);
            return new Point(center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians), center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
        }

        [EditorFact]
        public async Task RotationHandle_SnapsTo15_ShiftIsFree_EscCancels_UndoOneStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (window, overlay) = await PhotoAtHalf(ui, plugin);
            try
            {
                double Rotation() => (double)Get(ProjectClips(window)[0], "Rotation");
                // 손잡이는 틀 위쪽 가운데에서 바깥(위)으로
                var (knob, center) = ui.Run(() =>
                {
                    var corners = (Point[])Invoke(overlay, "HandlePoints");
                    var point = (Point?)Invoke(overlay, "RotationHandlePoint");
                    Assert.NotNull(point);
                    Assert.True(point.Value.Y < corners[0].Y, "손잡이가 틀 위쪽 밖");
                    Assert.Equal((corners[0].X + corners[1].X) / 2, point.Value.X, 1);
                    Ui.Snap(window, plugin.ShotFolder, "rotation-handle");
                    return (point.Value, Center(corners));
                });

                // 40°만큼 끌면 15° 단위로 붙어 45°, 놓기 전에는 클립 값이 그대로
                ui.Run(() =>
                {
                    Assert.True((bool)Invoke(overlay, "PointerDown", knob));
                    Invoke(overlay, "PointerMove", Turn(center, knob, 20));
                    Invoke(overlay, "PointerMove", Turn(center, knob, 40));
                    Assert.Equal(0.0, Rotation());
                    Assert.StartsWith("회전 45°", Status(window));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => Rotation() == 45, Wait, "45°");

                // Shift를 누른 채 7° 더 끌면 붙지 않고 52°
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                ui.Run(() =>
                {
                    var from = ((Point?)Invoke(overlay, "RotationHandlePoint")).Value;
                    SetModifiers(overlay, ModifierKeys.Shift);
                    Assert.True((bool)Invoke(overlay, "PointerDown", from));
                    Invoke(overlay, "PointerMove", Turn(center, from, 7));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                    SetModifiers(overlay, null);
                });
                await ui.WaitUntil(() => Math.Abs(Rotation() - 52) < 0.2, Wait, "52°(Shift)");

                // Esc: 끄는 중에 누르면 시작 각도로 돌아가고 편집하지 않는다
                ui.Run(() =>
                {
                    var from = ((Point?)Invoke(overlay, "RotationHandlePoint")).Value;
                    Assert.True((bool)Invoke(overlay, "PointerDown", from));
                    Invoke(overlay, "PointerMove", Turn(center, from, 90));
                    Assert.StartsWith("회전 ", Status(window));
                    Ui.Press(overlay, System.Windows.Input.Key.Escape);
                    Assert.Equal("회전을 취소했습니다.", Status(window));
                    Assert.False((bool)Invoke(overlay, "PointerUp"));
                });
                Assert.True(Math.Abs(ui.Run(Rotation) - 52) < 0.2);

                // 실행 취소 한 번에 45°, 또 한 번에 0°(끌기 한 번 = 한 단계)
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Rotation() == 45, Wait, "실행 취소 45°");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Rotation() == 0, Wait, "실행 취소 0°");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task Move_SnapsToOtherClipEdgeAndScreenEdge_ShowsGuide_AltDoesNot()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (window, overlay) = await PhotoAtHalf(ui, plugin);
            try
            {
                // 글자를 하나 넣어 오른쪽으로(X 600) 옮겨 둔다 — 사진 오른쪽 가장자리를 글자 상자 왼쪽 가장자리에 붙여 본다
                const string text = "붙기";
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
                ui.Run(() => SelectClip(window, n => n.Contains(text)));
                await ui.WaitUntil(() => (string)Get(Get(Session(window), "PrimaryClip"), "Text") == text && Ui.ById<TextBox>(window, "PropX") is TextBox box && box.IsVisible, Wait, "글자 고름");
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "PropX");
                    Ui.Focus(box);
                    box.Text = "600";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                object Title() => ProjectClips(window).First(c => (string)Get(c, "Text") == text);
                object Photo() => ProjectClips(window).First(c => (string)Get(c, "Path") is string path && path.EndsWith("red.png"));
                await ui.WaitUntil(() => (double)Get(Title(), "X") == 600, Wait, "글자 X 600");
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "글자 틀");
                var textLeft = ui.Run(() => ((Point[])Invoke(overlay, "HandlePoints"))[0].X);

                ui.Run(() => SelectClip(window, n => n.Contains("red.png")));
                await ui.WaitUntil(() => Get(Get(Session(window), "PrimaryClip"), "Path") is string path && path.EndsWith("red.png") && (bool)Get(overlay, "HasFrame"), Wait, "사진 고름");
                var corners = ui.Run(() => (Point[])Invoke(overlay, "HandlePoints"));
                var center = Center(corners);
                // 사진 오른쪽 가장자리를 글자 왼쪽 가장자리 3px(화면) 앞까지 끌면 그 가장자리에 붙고 안내선이 그 자리에
                ui.Run(() =>
                {
                    var to = new Point(center.X + textLeft - corners[1].X - 3, center.Y + 1);
                    Assert.True((bool)Invoke(overlay, "PointerDown", center));
                    Invoke(overlay, "PointerMove", to);
                    var (guideX, guideY) = ((double?, double?))Invoke(overlay, "GuidePositions");
                    Assert.NotNull(guideX);
                    Assert.Equal(textLeft, guideX.Value, 1);
                    // 세로는 화면 가운데에 붙는다
                    Assert.NotNull(guideY);
                    Ui.Snap(window, plugin.ShotFolder, "snap-to-clip-edge");
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (double)Get(Photo(), "X") != 0, Wait, "옮김");
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame"), Wait, "틀 다시");
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "HandlePoints");
                    Assert.Equal(textLeft, now[1].X, 0);
                    Assert.Equal(0.0, (double)Get(Photo(), "Y"));
                });

                // 화면 왼쪽 가장자리 2px 앞까지 끌면 화면 가장자리(0)에 붙는다
                var (zoom, left) = ui.Run(() =>
                {
                    var project = Project(window);
                    var z = Math.Min(overlay.ActualWidth / (int)Get(project, "Width"), overlay.ActualHeight / (int)Get(project, "Height"));
                    return (z, (overlay.ActualWidth - (int)Get(project, "Width") * z) / 2);
                });
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "HandlePoints");
                    var middle = Center(now);
                    Assert.True((bool)Invoke(overlay, "PointerDown", middle));
                    Invoke(overlay, "PointerMove", new Point(middle.X - (now[0].X - left) + 2, middle.Y));
                    var (guideX, _) = ((double?, double?))Invoke(overlay, "GuidePositions");
                    Assert.NotNull(guideX);
                    Assert.Equal(left, guideX.Value, 1);
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && Math.Abs(((Point[])Invoke(overlay, "HandlePoints"))[0].X - left) <= 1, Wait, "화면 왼쪽 가장자리");
                var snappedX = ui.Run(() => (double)Get(Photo(), "X"));

                // Alt를 누르는 동안은 붙지 않는다(안내선도 없음)
                ui.Run(() =>
                {
                    var now = (Point[])Invoke(overlay, "HandlePoints");
                    var middle = Center(now);
                    SetModifiers(overlay, ModifierKeys.Alt);
                    Assert.True((bool)Invoke(overlay, "PointerDown", middle));
                    Invoke(overlay, "PointerMove", new Point(middle.X + 4 * zoom, middle.Y + zoom));
                    Assert.Equal(((double?)null, (double?)null), ((double?, double?))Invoke(overlay, "GuidePositions"));
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                    SetModifiers(overlay, null);
                });
                await ui.WaitUntil(() => (double)Get(Photo(), "X") == snappedX + 4, Wait, "Alt는 붙지 않음");
                Assert.Equal(1.0, ui.Run(() => (double)Get(Photo(), "Y")));
                _out.WriteLine("붙은 X " + snappedX + " · 화면 1px = 프로젝트 " + (1 / zoom).ToString("0.00") + "px");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
