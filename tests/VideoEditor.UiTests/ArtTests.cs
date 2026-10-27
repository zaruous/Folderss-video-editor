using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
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
    /// 아트 요소 1단계(#7, Windows 실측): 미디어 패널 [요소] 탭(탭 여섯이 한 줄, 두 번 누르기·끌어 놓기로 넣기), 속성 패널 [모양]·[꾸미기] 탭, 미리 보기.
    /// 끌어 놓기 이벤트는 만들 수 없어 타임라인의 처리기(ArtDropped)를 직접 부른다. 두 번 누르기는 단추에 MouseDoubleClick을 올린다.
    /// </summary>
    public class ArtTests
    {
        private readonly ITestOutputHelper _out;

        public ArtTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static List<object> ArtClips(Window window)
        {
            return ProjectClips(window).Where(c => Get(c, "Kind").ToString() == "Art").ToList();
        }

        private static object TrackOf(Window window, object clip)
        {
            var project = Project(window);
            return project.GetType().GetMethod("TrackOf").Invoke(project, new[] { clip });
        }

        private static IList Tracks(Window window, string property)
        {
            return (IList)Get(Project(window), property);
        }

        private static object Kind(PluginUnderTest plugin, string name)
        {
            return Enum.Parse(plugin.Assembly.GetType("VideoEditor.Core.ArtKind", true), name);
        }

        /// <summary>미리 보기 그림의 (x, y) 픽셀(R, G, B) — 위치는 프로젝트 픽셀(1920×1080)로 준다. UI 스레드에서.</summary>
        private static (int R, int G, int B) PreviewAt(Window window, double projectX, double projectY)
        {
            var bitmap = (BitmapSource)Ui.ById<Image>(window, "PreviewImage").Source;
            var x = (int)(projectX * bitmap.PixelWidth / 1920.0);
            var y = (int)(projectY * bitmap.PixelHeight / 1080.0);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[2], pixel[1], pixel[0]);
        }

        private static bool Blue((int R, int G, int B) p)
        {
            return p.B > 180 && p.R < 120 && p.G > 90 && p.G < 170;
        }

        private static async Task GoTo(UiThread ui, Window window, string timecode)
        {
            ui.Run(() =>
            {
                var box = Ui.ById<TextBox>(window, "TimecodeBox");
                Ui.Focus(box);
                box.Text = timecode;
                Ui.Press(box, System.Windows.Input.Key.Enter);
            });
            await Task.Delay(100);
        }

        /// <summary>미디어 패널 탭 머리(UI 스레드): 탭 수, 패널 폭, 탭마다 윗변·폭 — 윗변이 모두 같으면 한 줄.</summary>
        private static (int Count, double Panel, List<double> Tops, string Detail) TabRow(Window window)
        {
            var tabs = Ui.ById<TabControl>(window, "MediaTabs");
            window.UpdateLayout();
            var items = tabs.Items.Cast<TabItem>().ToList();
            var tops = items.Select(t => t.TranslatePoint(new Point(0, 0), tabs).Y).ToList();
            // 두 줄이 되면 탭 줄은 줄마다 패널 폭으로 늘려 놓는다 — 제 폭(제약 없이 잰 폭)의 합을 따로 본다
            var natural = items.Select(t =>
            {
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return t.DesiredSize.Width;
            }).ToList();
            var detail = string.Join(", ", items.Select((t, i) => Ui.ContentText(t.Header) + " " + natural[i].ToString("0") + "px@" + tops[i].ToString("0")))
                         + " · 제 폭 합 " + natural.Sum().ToString("0") + "px";
            return (items.Count, tabs.ActualWidth, tops, detail);
        }

        /// <summary>[요소] 탭이 더해져 탭이 여섯이 되어도 1280×800 창의 미디어 패널에서 탭 머리가 한 줄이다(#7).</summary>
        [EditorFact]
        public async Task MediaTabs_SixTabs_StayOnOneRow()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                ui.Run(() =>
                {
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "ArtTab");
                    var row = TabRow(window);
                    _out.WriteLine(string.Format("미디어 패널 {0:0}px: {1}", row.Panel, row.Detail));
                    Ui.Snap(window, plugin.ShotFolder, "media-tabs-row");
                    Assert.Equal(6, row.Count);
                    Assert.True(row.Tops.Max() - row.Tops.Min() < 1, "탭 줄이 둘로 나뉨 — " + row.Detail);
                });
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task ArtTab_DoubleClickAddsAtPlayhead_DropPicksTheTrack_ShapeAndStyleTabs()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                ui.Run(() =>
                {
                    var tabs = Ui.ById<TabControl>(window, "MediaTabs");
                    tabs.SelectedItem = Ui.ById<TabItem>(window, "ArtTab");
                    Assert.Same(Ui.ById<TabItem>(window, "ArtTab"), tabs.Items[3]);
                    window.UpdateLayout();
                    Assert.Equal(8, Ui.ById<WrapPanel>(window, "ArtTiles").Children.Cast<Button>().Count());
                    Ui.Snap(window, plugin.ShotFolder, "art-tab");
                });

                // 재생 헤드 1초에서 [사각형]을 두 번 누르면 거기에 들어가고 골라진다
                await GoTo(ui, window, "00:00:01:00");
                ui.Run(() => Ui.ById<Button>(window, "ArtTileRectangle").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent }));
                await ui.WaitUntil(() => ArtClips(window).Count == 1, Wait, "사각형 넣기");
                var rectangle = ui.Run(() => ArtClips(window)[0]);
                ui.Run(() =>
                {
                    Assert.Equal(30L, (long)Get(rectangle, "Start"));
                    Assert.StartsWith("사각형을(를) 넣었습니다.", Status(window));
                    Assert.False(ReferenceEquals(TrackOf(window, rectangle), Tracks(window, "VideoTracks")[0]), "주 트랙이 아닌 덮어 쓰기 트랙");
                    var tabs = Ui.ById<TabControl>(window, "PropertyTabs").Items.Cast<TabItem>().Select(t => (string)t.Header).ToList();
                    Assert.Contains("모양", tabs);
                    Assert.Contains("꾸미기", tabs);
                });

                // 미리 보기: 화면 가운데가 파랑(기본 채우기), 사각형 밖은 검정
                await ui.WaitUntil(() => PreviewHasPicture(window) && Blue(PreviewAt(window, 960, 540)), Wait, "사각형 미리 보기");
                ui.Run(() =>
                {
                    Assert.False(Blue(PreviewAt(window, 960 - 260, 540)), "사각형 왼쪽 밖");
                    Assert.True(Blue(PreviewAt(window, 760 + 8, 390 + 8)), "모서리 안쪽(반경 0)");
                    Ui.Snap(window, plugin.ShotFolder, "art-rectangle-preview");
                });

                // [꾸미기]: 글꼴·휘기 묶음은 숨기고 채우기·윤곽선·그림자·광선·반사는 보인다
                ui.Run(() =>
                {
                    var tabs = Ui.ById<TabControl>(window, "PropertyTabs");
                    var style = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "꾸미기");
                    tabs.SelectedItem = style;
                    window.UpdateLayout();
                    // 묶음 제목(굵은 글)만 — "글꼴"은 줄 이름에도 있다
                    var sections = Ui.FindAll<TextBlock>((DependencyObject)style.Content, t => t.FontWeight == FontWeights.SemiBold && new[] { "글꼴", "휘기", "채우기", "윤곽선", "그림자", "광선", "반사" }.Contains(t.Text))
                        .GroupBy(t => t.Text).ToDictionary(g => g.Key, g => g.First().Visibility);
                    Assert.Equal(Visibility.Collapsed, sections["글꼴"]);
                    Assert.Equal(Visibility.Collapsed, sections["휘기"]);
                    foreach (var shown in new[] { "채우기", "윤곽선", "그림자", "광선", "반사" })
                        Assert.Equal(Visibility.Visible, sections[shown]);
                    Ui.Snap(window, plugin.ShotFolder, "art-style-tab");
                    tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "모양");
                });

                // [모양]: 모서리 반경 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계 — 모서리 안쪽이 비워진다
                await ui.WaitFor(() => Ui.ById<Slider>(window, "ArtCornerRadiusSlider"), Wait, "모서리 슬라이더");
                ui.Run(() =>
                {
                    Assert.Equal("400", Ui.ById<TextBox>(window, "ArtWidth").Text);
                    Assert.Equal("300", Ui.ById<TextBox>(window, "ArtHeight").Text);
                    var slider = Ui.ById<Slider>(window, "ArtCornerRadiusSlider");
                    foreach (var value in new[] { 50.0, 100.0, 150.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(ArtClips(window)[0], "Art"), "CornerRadius") - 150) < 1e-6, Wait, "모서리 150");
                await ui.WaitUntil(() => !Blue(PreviewAt(window, 760 + 8, 390 + 8)) && Blue(PreviewAt(window, 960, 540)), Wait, "둥근 모서리 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "art-shape-tab-rounded"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => (double)Get(Get(ArtClips(window)[0], "Art"), "CornerRadius") == 0, Wait, "실행 취소 한 번에 반경 0");
                var undone = System.Diagnostics.Stopwatch.StartNew();
                await ui.WaitUntil(() => Blue(PreviewAt(window, 760 + 8, 390 + 8)), Wait, "실행 취소한 모서리 미리 보기");
                _out.WriteLine("실행 취소 뒤 미리 보기가 모서리를 되돌리기까지 " + undone.ElapsedMilliseconds + "ms");

                // 끌어 놓기: 주 트랙(비었음)에 별 → 주 트랙(마그네틱이라 0에 붙음), 오디오 트랙에 화살표 → 동영상 트랙으로
                var canvas = ui.Run(() => Canvas(window));
                var dropped = ui.Run(() => (Delegate)canvas.GetType().GetField("ArtDropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas));
                var main = ui.Run(() => Tracks(window, "VideoTracks")[0]);
                ui.Run(() => dropped.DynamicInvoke(Kind(plugin, "Star"), main, 90L));
                await ui.WaitUntil(() => ArtClips(window).Count == 2, Wait, "별 놓기");
                var audio = ui.Run(() => Tracks(window, "AudioTracks")[0]);
                ui.Run(() => dropped.DynamicInvoke(Kind(plugin, "Arrow"), audio, 60L));
                await ui.WaitUntil(() => ArtClips(window).Count == 3, Wait, "화살표 놓기");
                ui.Run(() =>
                {
                    var clips = ArtClips(window);
                    var star = clips.Single(c => Get(Get(c, "Art"), "Kind").ToString() == "Star");
                    var arrow = clips.Single(c => Get(Get(c, "Art"), "Kind").ToString() == "Arrow");
                    Assert.Same(main, TrackOf(window, star));
                    Assert.Equal(0L, (long)Get(star, "Start"));
                    var arrowTrack = TrackOf(window, arrow);
                    Assert.Equal("Video", Get(arrowTrack, "Kind").ToString());
                    Assert.Equal(60L, (long)Get(arrow, "Start"));
                    _out.WriteLine("타임라인: " + string.Join(" | ", Clips(window)));
                    Ui.Snap(window, plugin.ShotFolder, "art-dropped");
                });
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => ArtClips(window).Count == 2, Wait, "화살표 실행 취소");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
