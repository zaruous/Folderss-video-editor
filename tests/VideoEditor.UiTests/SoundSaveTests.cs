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
    /// <summary>사운드를 파일로 저장(#34): 진행 창과 취소.</summary>
    public class SoundSaveTests
    {
        private readonly ITestOutputHelper _out;

        public SoundSaveTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static async Task Import(UiThread ui, Window window, string path)
        {
            var name = Path.GetFileName(path);
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, path, Wait);
            await ui.WaitUntil(() => MediaItem(window, name) != null, Wait, name + " 가져오기");
        }

        private static FrameworkElement MediaItem(Window window, string name)
        {
            return Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetName(i) == name);
        }

        private static Window Progress()
        {
            return Ui.Windows().FirstOrDefault(w => w.IsVisible && w.Title == "사운드 저장");
        }

        private static string Codec(string path)
        {
            return RunTool("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path).Trim();
        }

        /// <summary>
        /// 진행 창과 취소(#34): 긴 소리(30분, 8kHz)를 4분의 1 속도로 저장하면(WAV로 다시 인코딩 — 이 PC에서 2초 남짓) 진행 창이 진행률을 보이고,
        /// [취소]하면 FFmpeg를 끝내 저장한 파일·임시 파일이 남지 않고 미디어에도 더하지 않는다(덮어쓰기로 고른 원래 파일이 남는 것은 단위 시험이 본다).
        /// </summary>
        [EditorFact]
        public async Task SaveClipSound_ProgressWindow_CancelLeavesNothing()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var source = Path.Combine(MediaFolder, "long-tone.wav");
                if (!File.Exists(source))
                    RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000:duration=1800", "-ac", "1", "-c:a", "pcm_s16le", source);
                await Import(ui, window, source);
                ui.Run(() =>
                {
                    Ui.ById<ListBox>(window, "MediaList").SelectedItem = MediaItem(window, "long-tone.wav");
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains("long-tone.wav")), Wait, "긴 소리 클립");
                ui.Run(() => SelectClip(window, n => n.Contains("long-tone.wav")));
                await ui.WaitFor(() => Ui.ById<TabControl>(window, "PropertyTabs"), Wait, "속성 패널");
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").Items.OfType<TabItem>().First(t => (string)t.Header == "속도").IsSelected = true);
                var speed = await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropSpeed") is TextBox shown && shown.IsVisible ? shown : null, Wait, "속도 칸");
                ui.Run(() =>
                {
                    Ui.Focus(speed);
                    speed.Text = "0.25";
                    Ui.Press(speed, System.Windows.Input.Key.Enter);
                });
                object Clip() => ProjectClips(window).First(c => (string)Get(c, "Name") == "long-tone.wav");
                await ui.WaitUntil(() => (double)Get(Clip(), "Speed") == 0.25, Wait, "4분의 1 속도");

                var folder = Path.Combine(plugin.Root, "cancel-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(folder);
                var output = Path.Combine(folder, "slow.wav");
                ui.Post(() =>
                {
                    var view = Ui.ById<FrameworkElement>(window, "VideoEditorView");
                    var project = Project(window);
                    var clip = Clip();
                    var track = project.GetType().GetMethod("TrackOf").Invoke(project, new[] { clip });
                    var menu = (ContextMenu)view.GetType().GetMethod("ClipMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, new[] { clip, track, (object)0L });
                    menu.IsOpen = true;
                    var item = menu.Items.OfType<MenuItem>().First(m => Ui.HeaderText(m).StartsWith("사운드를 파일로 저장", StringComparison.Ordinal));
                    menu.IsOpen = false;
                    Ui.Invoke(item);
                });
                await NativeDialogs.SaveAs(ui, output, Wait);
                var progress = await ui.WaitFor(Progress, Wait, "진행 창");
                await ui.WaitUntil(() => (Ui.ById<TextBlock>(progress, "SoundSaveStatus")?.Text ?? "").Contains("%"), Wait, "진행률");
                ui.Run(() =>
                {
                    var bar = Ui.ById<ProgressBar>(progress, "SoundSaveProgress");
                    _out.WriteLine("취소 전: " + Ui.ById<TextBlock>(progress, "SoundSaveStatus").Text + " · 막대 " + bar.Value.ToString("0"));
                    Assert.False(bar.IsIndeterminate);
                    Assert.InRange(bar.Value, 0, 99.9);
                    Ui.Snap(progress, plugin.ShotFolder, "sound-save-progress");
                    Ui.Click(Ui.ById<Button>(progress, "SoundSaveCancelButton"));
                });
                await ui.WaitUntil(() => Progress() == null, Wait, "진행 창 닫힘");
                await ui.WaitUntil(() => Status(window).StartsWith("사운드 저장을 취소했습니다", StringComparison.Ordinal), Wait, "취소 알림");
                Assert.Empty(Directory.GetFiles(folder));
                Assert.Null(ui.Run(() => MediaItem(window, "slow.wav")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
