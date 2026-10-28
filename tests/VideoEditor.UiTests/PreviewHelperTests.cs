using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>편집기 화면 시험 + FOLDERSS_VE_FFMPEG에 도우미가 쓸 공유 DLL(shared 빌드)이 있을 때만.</summary>
    public sealed class HelperEditorFactAttribute : FactAttribute
    {
        // 플러그인의 PreviewHelper.Libraries와 같다(FFmpeg 9 — 플러그인 형식은 다른 로드 컨텍스트라 직접 부르지 않는다)
        private static readonly string[] Libraries = { "avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll", "avfilter-12.dll" };

        public HelperEditorFactAttribute()
        {
            var editor = new EditorFactAttribute();
            if (editor.Skip != null)
                Skip = editor.Skip;
            else if (!HasLibraries)
                Skip = "FOLDERSS_VE_FFMPEG에 공유 DLL(avcodec-63.dll 등)이 없어 미리 보기 도우미 화면 시험을 건너뜁니다(shared 빌드의 bin을 주세요).";
        }

        public static bool HasLibraries
        {
            get { return Libraries.All(name => File.Exists(Path.Combine(FfmpegFolder, name))); }
        }
    }

    /// <summary>편집기 화면 시험 + FOLDERSS_VE_FFMPEG에 공유 DLL이 없을 때만(ffmpeg.exe만 있는 빌드 — winget Gyan full_build 등).</summary>
    public sealed class NoHelperEditorFactAttribute : FactAttribute
    {
        public NoHelperEditorFactAttribute()
        {
            var editor = new EditorFactAttribute();
            if (editor.Skip != null)
                Skip = editor.Skip;
            else if (HelperEditorFactAttribute.HasLibraries)
                Skip = "FOLDERSS_VE_FFMPEG에 공유 DLL이 있어 건너뜁니다(도우미 없는 FFmpeg에서만 보는 시험).";
        }
    }

    /// <summary>
    /// 미리 보기 도우미(#1)를 편집기 안에서: FFmpeg 단추의 "· 도우미", 재생 헤드 이동 시간, 도우미가 끝나면 ffmpeg.exe로 그리고 5초 뒤 다시 띄움,
    /// 편집 창을 닫으면 도우미도 끝남.
    /// </summary>
    public class PreviewHelperTests
    {
        private readonly ITestOutputHelper _out;

        public PreviewHelperTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object View(Window window)
        {
            return Ui.ById<FrameworkElement>(window, "VideoEditorView");
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }

        /// <summary>살아 있는 도우미의 프로세스 번호(없으면 0). UI 스레드에서.</summary>
        private static int HelperPid(Window window)
        {
            var helper = Get(Field(View(window), "_services"), "Helper");
            return helper == null ? 0 : (int)Get(helper, "ProcessId");
        }

        private static string HelperStatus(Window window)
        {
            return (string)Get(Field(View(window), "_services"), "HelperStatus");
        }

        private static long ShownFrame(Window window)
        {
            return (long)Get(Field(View(window), "_player"), "ShownFrame");
        }

        private static long Playhead(Window window)
        {
            return (long)Get(Session(window), "Playhead");
        }

        /// <summary>→ 키(다음 프레임)를 누르고 미리 보기 화면이 그 프레임이 될 때까지 걸린 시간(ms). 10ms마다 본다.</summary>
        private static async Task<double> StepAndWait(UiThread ui, Window window)
        {
            var watch = Stopwatch.StartNew();
            var target = ui.Run(() =>
            {
                Key(window, System.Windows.Input.Key.Right);
                return Playhead(window);
            });
            while (ui.Run(() => ShownFrame(window)) != target)
            {
                if (watch.Elapsed > Wait)
                    throw new TimeoutException(target + "번 프레임이 그려지지 않았습니다.");
                await Task.Delay(10);
            }
            return watch.Elapsed.TotalMilliseconds;
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        [HelperEditorFact]
        public async Task Helper_DrawsSeeks_FallsBackWhenKilled_RestartsAndEndsWithEditor()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            var pid = 0;
            try
            {
                // 편집기를 열면 도우미가 뜨고 FFmpeg 단추에 보인다
                await ui.WaitUntil(() => FfmpegStatus(window).EndsWith("· 도우미", StringComparison.Ordinal), Wait, "도우미 시작");
                Assert.StartsWith("미리 보기 도우미: 쓰는 중 — FFmpeg ", ui.Run(() => HelperStatus(window)));
                pid = ui.Run(() => HelperPid(window));
                Assert.NotEqual(0, pid);

                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "testsrc.mp4"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == "testsrc.mp4"), Wait, "가져오기");
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = list.Items.Cast<FrameworkElement>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == "testsrc.mp4");
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Count == 1 && PreviewHasPicture(window), Wait, "첫 화면");

                // 재생 헤드 이동(→ 키 — 40ms 모으기 포함)
                var helperSteps = new List<double>();
                for (var i = 0; i < 8; i++)
                    helperSteps.Add(await StepAndWait(ui, window));
                _out.WriteLine("도우미로 재생 헤드 이동: " + string.Join(" · ", helperSteps.Select(v => v.ToString("0"))) + "ms → 중앙 " + Median(helperSteps).ToString("0") + "ms");
                Assert.True(Median(helperSteps) < 300, "편집기 안 재생 헤드 이동 중앙값 " + Median(helperSteps).ToString("0") + "ms");
                Assert.Equal(pid, ui.Run(() => HelperPid(window)));
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "helper-in-use"));

                // 도우미가 끝나면(작업 관리자에서 끝냄) 알리고, 그동안은 ffmpeg.exe로 그린다
                using (var process = Process.GetProcessById(pid))
                    process.Kill();
                await ui.WaitUntil(() => !FfmpegStatus(window).EndsWith("· 도우미", StringComparison.Ordinal), Wait, "도우미 끝남 알림");
                var died = ui.Run(() => HelperStatus(window));
                _out.WriteLine(died);
                Assert.StartsWith("미리 보기 도우미: 끝남 — ", died);
                Assert.Contains("다음 미리 보기 때 다시 띄움", died);
                var fallbackSteps = new List<double>();
                for (var i = 0; i < 2; i++)
                    fallbackSteps.Add(await StepAndWait(ui, window));
                _out.WriteLine("도우미 없이(ffmpeg.exe) 재생 헤드 이동: " + string.Join(" · ", fallbackSteps.Select(v => v.ToString("0"))) + "ms");
                Assert.Equal(0, ui.Run(() => HelperPid(window)));
                Assert.True(PreviewHasPictureNow(ui, window), "ffmpeg.exe로 그린 화면");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "helper-died-fallback"));

                // 5초 뒤 다음 미리 보기 때 다시 띄운다
                await Task.Delay(TimeSpan.FromSeconds(5.5));
                await StepAndWait(ui, window);
                await ui.WaitUntil(() => FfmpegStatus(window).EndsWith("· 도우미", StringComparison.Ordinal), Wait, "도우미 다시 시작");
                var restarted = ui.Run(() => HelperPid(window));
                Assert.NotEqual(0, restarted);
                Assert.NotEqual(pid, restarted);
                pid = restarted;
                _out.WriteLine("다시 띄운 도우미 " + pid + " · 다음 이동 " + (await StepAndWait(ui, window)).ToString("0") + "ms");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }

            // 편집 창을 다 닫으면 도우미도 끝난다
            if (pid != 0)
            {
                Process process = null;
                try
                {
                    process = Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    // 이미 끝남
                }
                using (process)
                    Assert.True(process == null || process.WaitForExit(10000), "편집 창을 닫으면 도우미가 끝난다");
            }
        }

        [HelperEditorFact]
        public async Task Helper_EditorClosedAndReopenedRightAway_StartsAgain()
        {
            // 창을 닫아 끝낸 도우미는 실패가 아니다 — 곧바로 다시 열어도 미리 보기를 기다리지 않고 띄운다
            // (예전에는 띄울 때 걸어 둔 5초 대기가 남아, 재생 헤드를 움직이기 전까지 ffmpeg.exe로만 그렸다)
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await ui.WaitUntil(() => FfmpegStatus(window).EndsWith("· 도우미", StringComparison.Ordinal), Wait, "도우미 시작");
            }
            finally
            {
                await CloseEditor(ui, window);
            }
            window = await Open(ui, plugin);
            try
            {
                var watch = Stopwatch.StartNew();
                await ui.WaitUntil(() => FfmpegStatus(window).EndsWith("· 도우미", StringComparison.Ordinal), TimeSpan.FromSeconds(30), "다시 연 창의 도우미");
                _out.WriteLine("다시 연 창에서 도우미까지 " + watch.ElapsedMilliseconds + "ms");
                Assert.NotEqual(0, ui.Run(() => HelperPid(window)));
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        private static bool PreviewHasPictureNow(UiThread ui, Window window)
        {
            return ui.Run(() => PreviewHasPicture(window));
        }

        [NoHelperEditorFact]
        public async Task NoSharedDlls_NoHelper_DrawsWithFfmpegExe()
        {
            // 공유 DLL이 없는 FFmpeg면 도우미를 띄우지 않고 예전처럼 ffmpeg.exe로 그린다(이유는 FFmpeg 단추 도움말에)
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await ui.WaitUntil(() => HelperStatus(window) != "미리 보기 도우미: 아직 시작하지 않음", Wait, "도우미 상태");
                Assert.Equal("미리 보기 도우미: 이 FFmpeg에 공유 DLL(shared 빌드)이 없어 ffmpeg.exe로 그림", ui.Run(() => HelperStatus(window)));
                Assert.False(ui.Run(() => FfmpegStatus(window)).EndsWith("· 도우미", StringComparison.Ordinal));
                Assert.Contains("공유 DLL", ui.Run(() => (string)Ui.ById<Button>(window, "FfmpegStatusButton").ToolTip));
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "testsrc.mp4"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == "testsrc.mp4"), Wait, "가져오기");
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = list.Items.Cast<FrameworkElement>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == "testsrc.mp4");
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Count == 1 && PreviewHasPicture(window), Wait, "첫 화면");
                var steps = new List<double>();
                for (var i = 0; i < 3; i++)
                    steps.Add(await StepAndWait(ui, window));
                _out.WriteLine("ffmpeg.exe로 재생 헤드 이동: " + string.Join(" · ", steps.Select(v => v.ToString("0"))) + "ms");
                Assert.Equal(0, ui.Run(() => HelperPid(window)));
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "helper-no-shared-dlls"));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
