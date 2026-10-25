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
    /// <summary>미리 보기 틀: 고른 사진을 미리 보기에서 끌어 옮기고 모서리를 끌어 크기를 바꾼다(놓을 때 한 번 편집).</summary>
    public class TransformOverlayTests
    {
        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Invoke(target, args);
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
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
