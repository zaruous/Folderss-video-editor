using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 편집기 화면 시험(숨은 데스크톱). Folderss가 하듯 zip을 불러 편집 창을 열고, Win32 파일 대화상자·키·자동화 피어로 조작한다.
    /// 캡처는 실행 폴더의 shots(ui-test.ps1 출력 끝에 경로).
    /// </summary>
    public class SmokeHeadlessTests
    {
        private readonly ITestOutputHelper _out;

        public SmokeHeadlessTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>
        /// 디버깅 진입점: Visual Studio 테스트 탐색기에서 [디버그]로 실행하면 편집 창이 화면에 뜨고 닫을 때까지 기다린다.
        /// </summary>
        [UiFact]
        public async Task OpenView()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin, withFfmpeg: !string.IsNullOrEmpty(FfmpegFolder));
            try
            {
                await ui.WaitUntil(() => window.IsLoaded, Wait, "편집 창");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "open-view"));
                if (HeadlessEnvironment.Debugging)
                    await ui.WaitUntil(() => !Ui.Windows().Contains(window), TimeSpan.FromHours(8), "디버깅 창 닫힘");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [UiFact]
        public async Task MissingFfmpeg_ShowsSetupPopup_AndLaterKeepsEditorUsable()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            // 이 PC에 FFmpeg가 설치돼 있어도(winget 등) 없는 상황을 만든다: 설정 경로·내려받은 폴더만 보게
            Environment.SetEnvironmentVariable("FOLDERSS_VE_FFMPEG_SEARCH", "off");
            Window window = null;
            try
            {
                window = await Open(ui, plugin, withFfmpeg: false);
                Window popup;
                try
                {
                    popup = await ui.WaitFor(() => Ui.Window("FFmpeg 설치"), Wait, "FFmpeg 설치 창");
                }
                catch (TimeoutException)
                {
                    _out.WriteLine("FFmpeg 상태: " + ui.Run(() => FfmpegStatus(window) + " / " + Ui.ById<Button>(window, "FfmpegStatusButton").ToolTip)
                                   + " · env " + Environment.GetEnvironmentVariable("FOLDERSS_VE_FFMPEG") + " · 상태 줄: " + ui.Run(() => Status(window))
                                   + " · 창: " + ui.Run(() => string.Join(", ", Ui.Windows().Select(w => w.Title + (w.IsVisible ? "" : "(숨김)")))));
                    throw;
                }
                ui.Run(() =>
                {
                    Assert.NotNull(Ui.ById<Button>(popup, "DownloadFfmpegButton"));
                    Assert.NotNull(Ui.ById<Button>(popup, "BrowseFfmpegButton"));
                    Ui.Snap(popup, plugin.ShotFolder, "ffmpeg-setup");
                    Ui.Click(Ui.ById<Button>(popup, "FfmpegLaterButton"));
                });
                await ui.WaitUntil(() => Ui.Window("FFmpeg 설치") == null, Wait, "설치 창 닫힘");
                await ui.WaitUntil(() => FfmpegStatus(window).StartsWith("FFmpeg 없음", StringComparison.Ordinal), Wait, "상태 줄: FFmpeg 없음");
                ui.Run(() =>
                {
                    var placeholder = Ui.ById<TextBlock>(window, "PreviewPlaceholder");
                    Assert.Contains("FFmpeg", placeholder.Text);
                    Ui.Snap(window, plugin.ShotFolder, "no-ffmpeg");
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                Environment.SetEnvironmentVariable("FOLDERSS_VE_FFMPEG_SEARCH", null);
                await CloseEditor(ui, window);
            }
        }

        /// <summary>S1·S3: 가져오기(Win32 대화상자) → 타임라인에 추가 → 분할·삭제·실행 취소 → 내보내기(ffprobe로 길이 확인).</summary>
        [EditorFact]
        public async Task ImportEditExport()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            void Step(string text) => File.AppendAllText(Path.Combine(plugin.Root, "steps.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + text + Environment.NewLine);
            Step("열기");
            var window = await Open(ui, plugin);
            Step("열림");
            try
            {
                // Ctrl+I → 파일 열기 대화상자(여러 개)
                Step("단계 1");
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                Step("단계 2");
                var title = await NativeDialogs.Open(ui, "\"testsrc.mp4\" \"red.png\" \"tone.wav\"", Wait);
                _out.WriteLine("대화상자: " + title);
                Step("단계 3");
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == 3, Wait, "미디어 3개");
                ui.Run(() => Assert.StartsWith("640×360 · 30 fps", Ui.ById<TextBlock>(window, "ProjectStatusText").Text));
                // 미디어 목록 썸네일(동영상·사진)
                await ui.WaitUntil(() => Ui.FindAll<Image>(Ui.ById<ListBox>(window, "MediaList")).Count(i => i.Source != null) >= 2, Wait, "미디어 썸네일 2개");
                // 첫 동영상에 맞춰 프로젝트 설정(640×360, 30fps). 단추 누름은 디스패처에 맡겨지므로 하나씩 누르고 기다린다
                foreach (var name in new[] { "testsrc.mp4", "red.png", "tone.wav" })
                    await AddFromMediaList(ui, window, name);
                var clips = ui.Run(() => Clips(window));
                _out.WriteLine(string.Join("\n", clips));
                Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00", clips);
                // 사진 기본 길이 5초, 주 트랙(마그네틱)에 이어 붙음
                Assert.Contains("동영상 1 · red.png · 00:00:03:00–00:00:08:00", clips);
                Assert.Contains("오디오 1 · tone.wav · 00:00:00:00–00:00:04:00", clips);
                Step("단계 5");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "미리 보기 화면");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "1-imported"));

                // 1초로 가서 동영상 나누기(Ctrl+B) → 앞 조각 지우기(Delete, 마그네틱이라 당겨짐) → 실행 취소
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "TimecodeBox");
                    Ui.Focus(box);
                    box.Text = "00:00:01:00";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                Step("단계 6");
                await ui.WaitUntil(() => Ui.ById<TextBox>(window, "TimecodeBox").Text == "00:00:01:00", Wait, "재생 헤드 1초");
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("testsrc.mp4"));
                    Key(window, System.Windows.Input.Key.B, ModifierKeys.Control);
                });
                Step("단계 7");
                await ui.WaitUntil(() => Clips(window).Count == 4, Wait, "분할 뒤 4개");
                ui.Run(() =>
                {
                    Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:01:00", Clips(window));
                    SelectClip(window, n => n.Contains("testsrc.mp4 · 00:00:00:00"));
                    Key(window, System.Windows.Input.Key.Delete);
                });
                Step("단계 8");
                await ui.WaitUntil(() => Clips(window).Count == 3, Wait, "삭제 뒤 3개");
                ui.Run(() =>
                {
                    Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:02:00", Clips(window));
                    Assert.Contains("동영상 1 · red.png · 00:00:02:00–00:00:07:00", Clips(window));
                    Ui.Snap(window, plugin.ShotFolder, "2-split-delete");
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                Step("단계 9");
                await ui.WaitUntil(() => Clips(window).Count == 4, Wait, "실행 취소");
                ui.Run(() => Key(window, System.Windows.Input.Key.Y, ModifierKeys.Control));
                Step("단계 10");
                await ui.WaitUntil(() => Clips(window).Count == 3, Wait, "다시 실행");

                // 내보내기(Ctrl+E)
                Step("단계 11");
                ui.Post(() => Key(window, System.Windows.Input.Key.E, ModifierKeys.Control));
                Step("단계 12");
                var export = await ui.WaitFor(() => Ui.Window("내보내기"), Wait, "내보내기 창");
                var output = Path.Combine(plugin.Root, "export.mp4");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(export, "ExportPathBox").Text = output;
                    Ui.Snap(export, plugin.ShotFolder, "3-export-dialog");
                    Ui.Click(Ui.ById<Button>(export, "ExportStartButton"));
                });
                Step("단계 13");
                await ui.WaitUntil(() => Ui.ById<TextBlock>(export, "ExportStatus").Text.StartsWith("내보냈습니다", StringComparison.Ordinal)
                                         || Ui.ById<TextBlock>(export, "ExportStatus").Text.StartsWith("내보내지 못했", StringComparison.Ordinal), TimeSpan.FromMinutes(3), "내보내기 끝");
                var status = ui.Run(() => Ui.ById<TextBlock>(export, "ExportStatus").Text);
                _out.WriteLine(status);
                ui.Run(() =>
                {
                    Ui.Snap(export, plugin.ShotFolder, "4-exported");
                    Ui.Click(Ui.ById<Button>(export, "ExportCloseButton"));
                });
                Assert.StartsWith("내보냈습니다", status);
                // 타임라인 = 동영상 2초 + 사진 5초 = 7초(오디오 4초가 더 짧음)
                Assert.InRange(Duration(output), 6.95, 7.1);
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>S2: 재생(소리 끔)하면 재생 헤드가 움직이고 Space로 멈춘다. 화살표·Home/End.</summary>
        [EditorFact]
        public async Task Preview_PlayPause_AndKeys()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await AddVideo(ui, window);
                ui.Run(() => Key(window, System.Windows.Input.Key.Right, ModifierKeys.Shift));
                await ui.WaitUntil(() => Ui.ById<TextBox>(window, "TimecodeBox").Text == "00:00:01:00", Wait, "Shift+→ 1초");
                ui.Run(() =>
                {
                    Key(window, System.Windows.Input.Key.Right);
                    Key(window, System.Windows.Input.Key.Right);
                });
                await ui.WaitUntil(() => Ui.ById<TextBox>(window, "TimecodeBox").Text == "00:00:01:02", Wait, "→ 두 번");
                ui.Run(() => Key(window, System.Windows.Input.Key.Home));
                await ui.WaitUntil(() => Ui.ById<TextBox>(window, "TimecodeBox").Text == "00:00:00:00", Wait, "Home");
                ui.Run(() => Key(window, System.Windows.Input.Key.Space));
                await ui.WaitUntil(() => string.CompareOrdinal(Ui.ById<TextBox>(window, "TimecodeBox").Text, "00:00:00:20") > 0, Wait, "재생으로 재생 헤드 이동");
                ui.Run(() =>
                {
                    Ui.Snap(window, plugin.ShotFolder, "playing");
                    Key(window, System.Windows.Input.Key.Space);
                });
                var stopped = ui.Run(() => Ui.ById<TextBox>(window, "TimecodeBox").Text);
                await Task.Delay(500);
                Assert.Equal(stopped, ui.Run(() => Ui.ById<TextBox>(window, "TimecodeBox").Text));
                ui.Run(() => Key(window, System.Windows.Input.Key.End));
                await ui.WaitUntil(() => Ui.ById<TextBox>(window, "TimecodeBox").Text == "00:00:03:00", Wait, "End");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        private static async Task AddVideo(UiThread ui, Window window, string name = "testsrc.mp4")
        {
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, Path.Combine(MediaFolder, name), Wait);
            await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == name), Wait, name + " 가져오기");
            await AddFromMediaList(ui, window, name);
        }

        /// <summary>미디어 목록에서 name을 골라 [타임라인에 추가]를 누르고 클립이 생길 때까지 기다린다.</summary>
        private static async Task AddFromMediaList(UiThread ui, Window window, string name)
        {
            var before = ui.Run(() => Clips(window).Count);
            ui.Run(() =>
            {
                var list = Ui.ById<ListBox>(window, "MediaList");
                list.SelectedItem = list.Items.Cast<FrameworkElement>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == name);
                Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
            });
            await ui.WaitUntil(() => Clips(window).Count == before + 1, Wait, name + " 클립 추가");
        }

        /// <summary>S3·S4·S5: 덮어 쓰기 트랙의 사진을 끌어 옮기고 끝을 늘인 뒤, 속성(불투명도)을 바꾸고 실행 취소.</summary>
        [EditorFact]
        public async Task DragTrim_Overlay_Properties()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await AddVideo(ui, window);
                // 사진을 덮어 쓰기 트랙(동영상 2)에: 트랙 추가 후 그 트랙으로 끌어 옮긴다
                await AddVideo(ui, window, "red.png");
                ui.Run(() => Assert.Contains("동영상 1 · red.png · 00:00:03:00–00:00:08:00", Clips(window)));
                ui.Run(() =>
                {
                    var add = (MenuItem)((MenuItem)Ui.Find<Menu>(window).Items[3]).Items.Cast<object>().OfType<MenuItem>().First(m => Ui.HeaderText(m) == "동영상 트랙 추가");
                    Ui.Invoke(add);
                });
                await ui.WaitUntil(() => (int)Get(Get(Project(window), "VideoTracks"), "Count") == 2, Wait, "동영상 트랙 2");
                ui.Run(() =>
                {
                    var image = ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png");
                    var bounds = ClipBounds(window, image);
                    // 위(동영상 2) 줄로, 왼쪽으로 끌어 1초 지점에
                    var canvas = Canvas(window);
                    var from = new Point(bounds.Left + 20, bounds.Top + bounds.Height / 2);
                    var x = (double)canvas.GetType().GetMethod("FrameToX").Invoke(canvas, new object[] { 30L }) + 20;
                    Drag(window, from, new Point(x, Canvas_RowTop(window, "동영상 2") + 10));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.StartsWith("동영상 2 · red.png", StringComparison.Ordinal)), Wait, "사진이 동영상 2로");
                var moved = ui.Run(() => Clips(window).First(n => n.StartsWith("동영상 2", StringComparison.Ordinal)));
                _out.WriteLine(moved);
                Assert.Equal("동영상 2 · red.png · 00:00:01:00–00:00:06:00", moved);
                // 주 트랙은 마그네틱이라 사진이 빠진 자리가 닫힘
                ui.Run(() => Assert.DoesNotContain(Clips(window), n => n.StartsWith("동영상 1 · red.png", StringComparison.Ordinal)));

                // 끝을 오른쪽으로 2초(60px × 2 = 30프레임/초) 늘이기
                ui.Run(() =>
                {
                    var image = ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png");
                    var bounds = ClipBounds(window, image);
                    var from = new Point(bounds.Right - 2, bounds.Top + bounds.Height / 2);
                    Drag(window, from, new Point(from.X + 120, from.Y));
                });
                await ui.WaitUntil(() => Clips(window).Contains("동영상 2 · red.png · 00:00:01:00–00:00:08:00"), Wait, "끝 늘이기");

                // 속성: 불투명도 50 → 실행 취소
                ui.Run(() => SelectClip(window, n => n.StartsWith("동영상 2 · red.png", StringComparison.Ordinal)));
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropOpacity"), Wait, "속성 패널");
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "PropOpacity");
                    Ui.Focus(box);
                    box.Text = "50";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png"), "Opacity") - 0.5) < 1e-6, Wait, "불투명도 50%");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "overlay-opacity"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png"), "Opacity") - 1) < 1e-6, Wait, "실행 취소");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        private static double Canvas_RowTop(Window window, string trackName)
        {
            var canvas = Canvas(window);
            var rows = (System.Collections.IEnumerable)canvas.GetType().GetMethod("Rows").Invoke(canvas, null);
            foreach (var row in rows)
            {
                if ((string)Get(row, "Label") == trackName)
                    return (double)Get(row, "Top");
            }
            throw new InvalidOperationException(trackName + " 줄이 없습니다.");
        }

        /// <summary>S6: 글자 클립, 마커(M), In/Out(I/O), 내보내기 창의 In~Out 범위.</summary>
        [EditorFact]
        public async Task Text_Marker_InOut()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await AddVideo(ui, window);
                ui.Run(() =>
                {
                    var tabs = Ui.ById<TabControl>(window, "MediaTabs");
                    tabs.SelectedIndex = 2;
                });
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "안녕하세요";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains("안녕하세요")), Wait, "글자 클립");
                ui.Run(() =>
                {
                    Assert.Contains("동영상 2 · 안녕하세요 · 00:00:00:00–00:00:05:00", Clips(window));
                    Key(window, System.Windows.Input.Key.Right, ModifierKeys.Shift);
                    Key(window, System.Windows.Input.Key.I);
                    Key(window, System.Windows.Input.Key.M);
                    Key(window, System.Windows.Input.Key.Right, ModifierKeys.Shift);
                    Key(window, System.Windows.Input.Key.O);
                });
                await ui.WaitUntil(() => (long?)Get(Project(window), "MarkOut") == 60, Wait, "Out 점");
                ui.Run(() =>
                {
                    Assert.Equal(30L, (long?)Get(Project(window), "MarkIn"));
                    Assert.Equal(1, (int)Get(Get(Project(window), "Markers"), "Count"));
                    Ui.Snap(window, plugin.ShotFolder, "text-marker-inout");
                });
                ui.Post(() => Key(window, System.Windows.Input.Key.E, ModifierKeys.Control));
                var export = await ui.WaitFor(() => Ui.Window("내보내기"), Wait, "내보내기 창");
                ui.Run(() =>
                {
                    var range = Ui.ById<ComboBox>(export, "ExportRangeBox");
                    Assert.StartsWith("In~Out (00:00:01:00 – 00:00:02:00)", range.SelectedItem.ToString());
                    Ui.Click(Ui.ById<Button>(export, "ExportCloseButton"));
                });
                await ui.WaitUntil(() => Ui.Window("내보내기") == null, Wait, "내보내기 창 닫힘");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>테마 7종으로 편집 창 캡처(색이 테마를 따르는지 눈으로 본다).</summary>
        [EditorFact]
        public async Task Themes()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await AddVideo(ui, window);
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "미리 보기");
                foreach (var theme in UiThread.Themes)
                {
                    ui.Run(() => plugin.ApplyTheme(theme));
                    await Task.Delay(300);
                    ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "theme-" + theme));
                }
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => plugin.ApplyTheme("Black"));
                await CloseEditor(ui, window);
            }
        }
    }
}
