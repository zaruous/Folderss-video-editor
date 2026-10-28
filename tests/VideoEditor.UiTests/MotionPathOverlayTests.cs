using System;
using System.Collections;
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
    /// 이동 경로(#4): [애니메이션] 탭의 경로 썸네일로 넣고, 미리 보기 위 점선·점을 끌어 고친다(놓을 때 한 단계 — 실행 취소, Esc는 취소).
    /// 사용자 경로는 Shift+누르기로 끝에 점을 더하고, 점을 두 번 눌러 빼고, 점선을 두 번 눌러 끼운다.
    /// </summary>
    public class MotionPathOverlayTests
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
        private readonly ITestOutputHelper _out;

        public MotionPathOverlayTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return Call(target.GetType().GetMethod(method, Flags), target, args);
        }

        /// <summary>클립의 이동 경로(없으면 null).</summary>
        private static object PathOf(object clip)
        {
            return ((IEnumerable)Get(clip, "Animations"))?.Cast<object>().FirstOrDefault(a => Get(a, "Kind").ToString() == "Path");
        }

        /// <summary>경로 점(프로젝트 픽셀, 클립 자리 기준). 경로가 없으면 빈 배열.</summary>
        private static (double X, double Y)[] Points(object clip)
        {
            var path = PathOf(clip);
            return path == null ? new (double, double)[0]
                : ((IEnumerable)Get(path, "Points")).Cast<object>().Select(p => ((double)Get(p, "X"), (double)Get(p, "Y"))).ToArray();
        }

        [EditorFact]
        public async Task Preview_PathPoints_DragUndoEscape_CustomAddRemove()
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
                ui.Run(() => SelectClip(window, n => n.Contains("red.png")));
                await ui.WaitUntil(() => (bool)Get(overlay, "HasFrame") && overlay.ActualWidth > 0, Wait, "틀");
                object Clip() => ProjectClips(window)[0];
                Assert.False(ui.Run(() => (bool)Get(overlay, "HasPath")));

                // [애니메이션] 탭의 "이동 경로" 묶음: 썸네일 다섯, 직선을 누르면 경로 하나
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").SelectedItem = Ui.ById<TabItem>(window, "AnimationTab"));
                await ui.WaitFor(() => Ui.ById<Button>(window, "AnimPath_PathLine"), Wait, "경로 썸네일");
                ui.Run(() =>
                {
                    Assert.Equal(5, Ui.ById<WrapPanel>(window, "AnimationTilesPath").Children.Count);
                    Ui.Click(Ui.ById<Button>(window, "AnimPath_PathLine"));
                });
                await ui.WaitUntil(() => Points(Clip()).Length == 2, Wait, "직선 경로");
                await ui.WaitUntil(() => (bool)Get(overlay, "HasPath"), Wait, "경로 점선");
                var start = ui.Run(() => Points(Clip()));
                var project = ui.Run(() => Project(window));
                var (width, height) = ui.Run(() => ((int)Get(project, "Width"), (int)Get(project, "Height")));
                var zoom = ui.Run(() => Math.Min(overlay.ActualWidth / width, overlay.ActualHeight / height));
                ui.Run(() =>
                {
                    var points = (Point[])Invoke(overlay, "PathPoints");
                    Assert.Equal(2, points.Length);
                    // 첫 점은 클립 가운데(사진은 화면 전체라 화면 가운데)
                    var center = (Point)Invoke(overlay, "ToScreen", width / 2.0, height / 2.0);
                    Assert.Equal(center.X, points[0].X, 1);
                    Assert.Equal(center.X + start[1].X * zoom, points[1].X, 1);
                    Ui.Snap(window, plugin.ShotFolder, "motion-path-line");
                });

                // 끝점을 끈다: 놓기 전에는 클립 값이 그대로, 놓으면 한 번 편집
                ui.Run(() =>
                {
                    var points = (Point[])Invoke(overlay, "PathPoints");
                    Assert.True((bool)Invoke(overlay, "PointerDown", points[1]));
                    Invoke(overlay, "PointerMove", new Point(points[1].X + 40, points[1].Y + 30));
                    Assert.StartsWith("경로 점 1", Status(window));
                    Assert.Equal(start[1], Points(Clip())[1]);
                    Assert.True((bool)Invoke(overlay, "PointerUp"));
                });
                var expected = (start[1].X + Math.Round(40 / zoom), start[1].Y + Math.Round(30 / zoom));
                await ui.WaitUntil(() => Points(Clip())[1] == expected, Wait, "끝점 옮김");
                _out.WriteLine("끝점 " + start[1] + " → " + expected);

                // 실행 취소 한 번에 끌기 전 점
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Points(Clip())[1] == start[1], Wait, "실행 취소");

                // Esc: 끄는 중에 누르면 취소(편집 없음)
                await ui.WaitUntil(() => (bool)Get(overlay, "HasPath"), Wait, "경로 다시");
                ui.Run(() =>
                {
                    var points = (Point[])Invoke(overlay, "PathPoints");
                    Assert.True((bool)Invoke(overlay, "PointerDown", points[1]));
                    Invoke(overlay, "PointerMove", new Point(points[1].X - 50, points[1].Y));
                    Key(window, System.Windows.Input.Key.Escape);
                    Assert.False((bool)Invoke(overlay, "PointerUp"));
                });
                await ui.WaitUntil(() => Status(window).Contains("경로 점 옮기기를 취소"), Wait, "Esc 취소");
                Assert.Equal(start[1], ui.Run(() => Points(Clip())[1]));

                // 사용자 경로로 바꾸면 점 넷(지그재그). Shift+누르기 자리에 끝 점을 더한다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "AnimPath_PathCustom")));
                await ui.WaitUntil(() => PathOf(Clip()) is object custom && Get(custom, "Effect").ToString() == "PathCustom" && (bool)Get(overlay, "HasPath"), Wait, "사용자 경로");
                var count = ui.Run(() => Points(Clip()).Length);
                ui.Run(() =>
                {
                    var points = (Point[])Invoke(overlay, "PathPoints");
                    Assert.Equal(count, points.Length);
                    Assert.True((bool)Invoke(overlay, "PathAppend", new Point(points[^1].X + 30, points[^1].Y + 30)));
                });
                await ui.WaitUntil(() => Points(Clip()).Length == count + 1, Wait, "끝에 점 더함");

                // 점을 두 번 누르면 뺀다
                ui.Run(() =>
                {
                    var points = (Point[])Invoke(overlay, "PathPoints");
                    Assert.True((bool)Invoke(overlay, "PointerDoubleClick", points[2]));
                });
                await ui.WaitUntil(() => Points(Clip()).Length == count, Wait, "점 뺌");

                // 점선 가운데를 두 번 누르면 끼우고, 한 번 누르기는 밑의 클립을 끌지 않는다
                ui.Run(() =>
                {
                    var points = (Point[])Invoke(overlay, "PathPoints");
                    var middle = new Point((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
                    Assert.False((bool)Invoke(overlay, "PointerDown", middle));
                    Assert.True((bool)Invoke(overlay, "PointerDoubleClick", middle));
                });
                await ui.WaitUntil(() => Points(Clip()).Length == count + 1, Wait, "점 끼움");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "motion-path-custom"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Points(Clip()).Length == count, Wait, "끼우기 실행 취소");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
