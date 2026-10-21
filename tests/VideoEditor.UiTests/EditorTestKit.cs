using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.UiTests.Headless;
using Xunit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 편집기 화면 시험 도우미: 개발용 FFmpeg(FOLDERSS_VE_FFMPEG)와 시험 미디어, 타임라인 클립 읽기·고르기, 키 누르기.
    /// FOLDERSS_VE_FFMPEG는 시험 프로세스에서 지운다 — 플러그인이 환경 변수로 FFmpeg를 찾지 않게 하고(FFmpeg 없는 상황 시험), 설정으로만 준다.
    /// </summary>
    internal static class EditorTestKit
    {
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
        public static readonly string FfmpegFolder;
        private static readonly Lazy<string> LazyMedia = new Lazy<string>(CreateMedia);

        static EditorTestKit()
        {
            FfmpegFolder = Environment.GetEnvironmentVariable("FOLDERSS_VE_FFMPEG");
            Environment.SetEnvironmentVariable("FOLDERSS_VE_FFMPEG", null);
        }

        public static string MediaFolder { get { return LazyMedia.Value; } }

        private static string Exe(string name)
        {
            return Path.Combine(FfmpegFolder, name + ".exe");
        }

        private static string CreateMedia()
        {
            var folder = Path.Combine(HeadlessEnvironment.WorkFolder, "media");
            Directory.CreateDirectory(folder);
            Ffmpeg("-f", "lavfi", "-i", "testsrc=size=640x360:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-c:v", "mpeg4", "-q:v", "4", "-c:a", "aac", "-shortest", Path.Combine(folder, "testsrc.mp4"));
            Ffmpeg("-f", "lavfi", "-i", "color=c=red:size=400x300", "-frames:v", "1", Path.Combine(folder, "red.png"));
            Ffmpeg("-f", "lavfi", "-i", "sine=frequency=330:duration=4", "-c:a", "pcm_s16le", Path.Combine(folder, "tone.wav"));
            return folder;
        }

        private static string Ffmpeg(params string[] args)
        {
            return RunTool("ffmpeg", new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args).ToArray());
        }

        public static string RunTool(string name, params string[] args)
        {
            var info = new ProcessStartInfo(Exe(name)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(name + " 실패: " + error);
                return output;
            }
        }

        /// <summary>ffprobe로 길이(초).</summary>
        public static double Duration(string path)
        {
            var text = RunTool("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path);
            return double.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>편집 창을 연다. withFfmpeg면 개발용 FFmpeg를 설정에 넣고 찾을 때까지 기다린다.</summary>
        public static async Task<Window> Open(UiThread ui, PluginUnderTest plugin, bool withFfmpeg = true)
        {
            plugin.Manager.SetSetting("ffmpegPath", withFfmpeg ? FfmpegFolder : Path.Combine(plugin.Root, "no-ffmpeg"));
            plugin.Manager.SetSetting("playerMuted", "true");
            plugin.Manager.SetSetting("lastFolder", withFfmpeg ? MediaFolder : plugin.Root);
            var window = ui.Run(() => plugin.OpenView(1280, 800));
            if (withFfmpeg)
                await ui.WaitUntil(() => FfmpegStatus(window).StartsWith("FFmpeg 9", StringComparison.Ordinal) || FfmpegStatus(window).StartsWith("FFmpeg 개발", StringComparison.Ordinal), Wait, "FFmpeg 찾기");
            return window;
        }

        /// <summary>
        /// 편집 창을 닫는다(시험 끝 finally). 열린 대화상자를 먼저 닫고, "저장할까요?"가 뜨면 [저장 안 함]을 누른다 —
        /// ui.Run으로 바로 닫으면 그 모달 창이 UI 스레드를 잡아 시험이 끝나지 않는다.
        /// </summary>
        public static async Task CloseEditor(UiThread ui, Window window)
        {
            if (window == null)
                return;
            ui.Post(() =>
            {
                foreach (var dialog in Ui.Windows().Where(w => w != window && w.IsVisible && w.Owner == window))
                    dialog.Close();
                Ui.Close(window);
            });
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var closed = ui.Run(() =>
                {
                    var ask = Ui.Windows().FirstOrDefault(w => w.IsVisible && w.Title == "동영상 편집" && Ui.ById<Button>(w, "AskButton1") != null);
                    if (ask != null)
                        Ui.Click(Ui.ById<Button>(ask, "AskButton1"));
                    return !Ui.Windows().Contains(window);
                });
                if (closed)
                    return;
                await Task.Delay(100);
            }
            throw new TimeoutException("편집 창이 닫히지 않았습니다.");
        }

        /// <summary>미리 보기 화면에 검정이 아닌 픽셀이 있는지(실제로 그려졌는지). UI 스레드에서.</summary>
        public static bool PreviewHasPicture(Window window)
        {
            var image = Ui.ById<System.Windows.Controls.Image>(window, "PreviewImage");
            var bitmap = image?.Source as System.Windows.Media.Imaging.BitmapSource;
            if (bitmap == null || image.Visibility != Visibility.Visible)
                return false;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 40 || pixels[i + 1] > 40 || pixels[i + 2] > 40)
                    return true;
            }
            return false;
        }

        public static string FfmpegStatus(Window window)
        {
            return Ui.ContentText(Ui.ById<Button>(window, "FfmpegStatusButton").Content) ?? "";
        }

        public static string Status(Window window)
        {
            return Ui.ById<TextBlock>(window, "StatusText").Text;
        }

        public static FrameworkElement Canvas(Window window)
        {
            return Ui.ById<FrameworkElement>(window, "TimelineCanvas");
        }

        /// <summary>타임라인 클립 이름들("트랙 · 이름 · 시작–끝", 위 트랙부터). UI 스레드에서 부른다.</summary>
        public static List<string> Clips(Window window)
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(Canvas(window));
            peer.ResetChildrenCache();
            return (peer.GetChildren() ?? new List<AutomationPeer>()).Select(p => p.GetName()).ToList();
        }

        /// <summary>이름에 text가 든 클립을 고른다(UI 스레드).</summary>
        public static void SelectClip(Window window, Func<string, bool> match)
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(Canvas(window));
            peer.ResetChildrenCache();
            var clip = (peer.GetChildren() ?? new List<AutomationPeer>()).FirstOrDefault(p => match(p.GetName()));
            Assert.True(clip != null, "클립이 없습니다. 있는 것: " + string.Join(" | ", Clips(window)));
            ((ISelectionItemProvider)clip.GetPattern(PatternInterface.SelectionItem)).Select();
        }

        /// <summary>타임라인에 포커스를 두고 키를 누른다(UI 스레드). 모달 창을 여는 키는 ui.Post로 부른다.</summary>
        public static void Key(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            var canvas = Canvas(window);
            Ui.Focus(canvas);
            Ui.Press(canvas, key, modifiers);
        }

        // ---------- 플러그인 내부 상태(리플렉션 — 플러그인 형식은 다른 로드 컨텍스트) ----------

        public static object Session(Window window)
        {
            var canvas = Canvas(window);
            return canvas.GetType().GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
        }

        public static object Get(object target, string property)
        {
            return target.GetType().GetProperty(property).GetValue(target);
        }

        public static List<object> ProjectClips(Window window)
        {
            var project = Get(Session(window), "Project");
            return ((IEnumerable)Get(project, "AllClips")).Cast<object>().ToList();
        }

        public static object Project(Window window)
        {
            return Get(Session(window), "Project");
        }

        public static Rect ClipBounds(Window window, object clip)
        {
            var canvas = Canvas(window);
            return (Rect)canvas.GetType().GetMethod("ClipBounds").Invoke(canvas, new[] { Get(clip, "Id") });
        }

        /// <summary>마우스 없이 타임라인 끌기(PointerDown → Move → Up).</summary>
        public static void Drag(Window window, Point from, Point to, ModifierKeys modifiers = ModifierKeys.Alt)
        {
            var canvas = Canvas(window);
            var type = canvas.GetType();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetMethod("PointerDown", flags).Invoke(canvas, new object[] { from, ModifierKeys.None });
            type.GetMethod("PointerMove", flags).Invoke(canvas, new object[] { new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), modifiers });
            type.GetMethod("PointerMove", flags).Invoke(canvas, new object[] { to, modifiers });
            type.GetMethod("PointerUp", flags).Invoke(canvas, new object[] { to, modifiers });
        }
    }

    /// <summary>숨은 데스크톱 + 개발용 FFmpeg가 있을 때만 도는 시험.</summary>
    public sealed class EditorFactAttribute : FactAttribute
    {
        public EditorFactAttribute()
        {
            var ui = new UiFactAttribute();
            if (ui.Skip != null)
                Skip = ui.Skip;
            else if (string.IsNullOrEmpty(EditorTestKit.FfmpegFolder) || !File.Exists(Path.Combine(EditorTestKit.FfmpegFolder, "ffmpeg.exe")))
                Skip = "FOLDERSS_VE_FFMPEG(ffmpeg 폴더)가 없어 편집기 화면 시험을 건너뜁니다.";
        }
    }
}
