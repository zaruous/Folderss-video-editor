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
    }
}
