using System;
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
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// [인물 마스크 만들기](타임라인·미디어 패널 오른쪽 메뉴): 모델이 없으면 내려받기 창(크기·라이선스·출처·이 PC에서만), 있으면 진행 창에서 만들고
    /// 상태 줄에 알린다. 같은 원본을 다시 부르면 만들어 둔 마스크를 쓴다. 실제로 만드는 시험은 FOLDERSS_VE_PERSONMASK(모델 폴더)를 줄 때만.
    /// </summary>
    public class PersonMaskTests
    {
        private const string EnvFolder = "FOLDERSS_VE_PERSONMASK";
        private const string ModelFile = "modnet-fa2fa546.onnx";

        // ---------- 도우미 ----------

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

        private static async Task AddToTimeline(UiThread ui, Window window, string name)
        {
            var before = ui.Run(() => Clips(window).Count);
            ui.Run(() =>
            {
                Ui.ById<ListBox>(window, "MediaList").SelectedItem = MediaItem(window, name);
                Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
            });
            await ui.WaitUntil(() => Clips(window).Count == before + 1, Wait, name + " 클립 추가");
        }

        private static object ClipNamed(Window window, string name)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Path") != null && Path.GetFileName((string)Get(c, "Path")) == name);
        }

        /// <summary>타임라인 오른쪽 메뉴(오른쪽 클릭과 같은 EditorView.ClipMenu).</summary>
        private static ContextMenu ClipMenu(Window window, object clip)
        {
            var view = Ui.ById<FrameworkElement>(window, "VideoEditorView");
            var project = Project(window);
            var track = project.GetType().GetMethod("TrackOf").Invoke(project, new[] { clip });
            return (ContextMenu)view.GetType().GetMethod("ClipMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, new[] { clip, track, (object)0L });
        }

        private static List<string> MenuNames(ContextMenu menu)
        {
            return menu.Items.OfType<MenuItem>().Select(Ui.HeaderText).ToList();
        }

        private static void InvokeMenu(ContextMenu menu, string startsWith)
        {
            menu.IsOpen = true;
            var item = menu.Items.OfType<MenuItem>().FirstOrDefault(m => Ui.HeaderText(m).StartsWith(startsWith, StringComparison.Ordinal));
            menu.IsOpen = false;
            if (item == null)
                throw new InvalidOperationException("메뉴 항목 '" + startsWith + "'가 없습니다. 있는 것: " + string.Join(" · ", MenuNames(menu)));
            Ui.Invoke(item);
        }

        private static string MaskRoot(PluginUnderTest plugin)
        {
            return Path.Combine(plugin.Manager.DataDirectory, "person-mask");
        }

        // ---------- 시험 ----------

        /// <summary>
        /// 모델이 없으면(데이터 폴더에도 FOLDERSS_VE_PERSONMASK에도) 내려받기 창이 뜬다: 모델·크기·라이선스·출처와 "이 PC에서만".
        /// [취소]하면 아무것도 받지 않고(파일·폴더가 생기지 않음) 상태 줄에 까닭을 알린다. 오디오에는 메뉴가 없다.
        /// </summary>
        [EditorFact]
        public async Task PersonMask_ModelMissing_AsksConsent_CancelLeavesNothing()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var env = Environment.GetEnvironmentVariable(EnvFolder);
            Environment.SetEnvironmentVariable(EnvFolder, null);
            Window window = null;
            try
            {
                window = await Open(ui, plugin);
                await Import(ui, window, Path.Combine(MediaFolder, "tone.wav"));
                await Import(ui, window, Path.Combine(MediaFolder, "red.png"));
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = MediaItem(window, "tone.wav");
                    Assert.DoesNotContain("인물 마스크 만들기", Ui.ContextMenu(list, null));
                    list.SelectedItem = MediaItem(window, "red.png");
                    Assert.Contains("인물 마스크 만들기", Ui.ContextMenu(list, null));
                });
                await AddToTimeline(ui, window, "red.png");
                Assert.Contains("인물 마스크 만들기", ui.Run(() => MenuNames(ClipMenu(window, ClipNamed(window, "red.png")))));

                // 다른 시험(효과 탭의 [인물 마스크 만들기])이 먼저 돌았으면 마스크 폴더가 이미 있을 수 있다 — 누르기 전후의 파일을 비교한다
                string[] Files() => Directory.Exists(MaskRoot(plugin)) ? Directory.GetFiles(MaskRoot(plugin), "*", SearchOption.AllDirectories).OrderBy(f => f).ToArray() : new string[0];
                var before = Files();
                ui.Post(() => InvokeMenu(ClipMenu(window, ClipNamed(window, "red.png")), "인물 마스크 만들기"));
                var dialog = await ui.WaitFor(() => Ui.Window("인물 분리 모델 내려받기"), Wait, "모델 내려받기 창");
                ui.Run(() =>
                {
                    var details = Ui.ById<TextBlock>(dialog, "PersonMaskModelDetails").Text;
                    Assert.Contains("MODNet", details);
                    Assert.Contains("약 25MB", details);
                    Assert.Contains("라이선스: Apache-2.0", details);
                    Assert.Contains("Hugging Face Xenova/modnet", details);
                    Assert.Contains("ZHKKKe/MODNet", details);
                    Assert.Contains(Ui.Texts(dialog), t => t.StartsWith("모든 처리는 이 PC에서 합니다", StringComparison.Ordinal));
                    Assert.True(Ui.ById<Button>(dialog, "PersonMaskDownloadButton").IsEnabled);
                    Ui.Snap(dialog, plugin.ShotFolder, "person-mask-consent");
                    Ui.Click(Ui.ById<Button>(dialog, "PersonMaskSetupCancelButton"));
                });
                await ui.WaitUntil(() => Ui.Window("인물 분리 모델 내려받기") == null, Wait, "내려받기 창 닫힘");
                await ui.WaitUntil(() => Status(window).StartsWith("인물 마스크를 만들지 않았습니다", StringComparison.Ordinal), Wait, "안 만듦 알림");
                Assert.Null(ui.Run(() => Ui.Window("인물 마스크 만들기")));
                Assert.Equal(before, Files());
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvFolder, env);
                await CloseEditor(ui, window);
                if (Directory.Exists(MaskRoot(plugin)))
                    Directory.Delete(MaskRoot(plugin), true);
            }
        }

        /// <summary>
        /// FOLDERSS_VE_PERSONMASK의 모델로: 타임라인 클립 오른쪽 메뉴 → 진행 창(프레임 진행률) → 다 되면 닫히고 상태 줄에 알림, 마스크(.mkv) 하나.
        /// 미디어 패널에서 같은 원본을 다시 부르면 창 없이 만들어 둔 것을 쓴다(파일이 그대로). 모델은 데이터 폴더에 받지 않는다.
        /// </summary>
        [EditorFact]
        public async Task PersonMask_LocalModel_ShowsProgress_ThenReusesCache()
        {
            var env = Environment.GetEnvironmentVariable(EnvFolder);
            if (string.IsNullOrEmpty(env) || (!File.Exists(Path.Combine(env, ModelFile)) && !File.Exists(Path.Combine(env, "models", ModelFile))))
                return;
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            Window window = null;
            try
            {
                window = await Open(ui, plugin);
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await AddToTimeline(ui, window, "testsrc.mp4");
                ui.Run(() => SelectClip(window, n => n.Contains("testsrc.mp4")));

                ui.Post(() => InvokeMenu(ClipMenu(window, ClipNamed(window, "testsrc.mp4")), "인물 마스크 만들기"));
                var dialog = await ui.WaitFor(() => Ui.Window("인물 마스크 만들기"), Wait, "인물 마스크 진행 창");
                // 프레임이 몇 장 지나간 진행률(0장이 아닌)
                await ui.WaitUntil(() => Ui.Window("인물 마스크 만들기") == null
                                         || (Ui.ById<TextBlock>(dialog, "PersonMaskStatus").Text.Contains("프레임") && !Ui.ById<TextBlock>(dialog, "PersonMaskStatus").Text.Contains("프레임 0 /")),
                    Wait, "프레임 진행률");
                ui.Run(() =>
                {
                    if (Ui.Window("인물 마스크 만들기") == null)
                        return;
                    Assert.StartsWith("원본: testsrc.mp4 — 동영상 640×360, 3초", Ui.ById<TextBlock>(dialog, "PersonMaskSource").Text);
                    Assert.StartsWith("인물 분리 중 — 프레임 ", Ui.ById<TextBlock>(dialog, "PersonMaskStatus").Text);
                    Ui.Snap(dialog, plugin.ShotFolder, "person-mask-progress");
                });
                await ui.WaitUntil(() => Ui.Window("인물 마스크 만들기") == null, TimeSpan.FromMinutes(3), "다 만들어 진행 창 닫힘");
                await ui.WaitUntil(() => Status(window).StartsWith("인물 마스크를 만들었습니다 — 사람에게만 효과가 사람 위에만 그려집니다(testsrc.mp4, ", StringComparison.Ordinal),
                    Wait, "만듦 알림");
                // 마스크 하나와 그 원본 기록(.source) 하나 — 기록은 원본을 지웠거나 바꾼 마스크 정리에 쓴다(#30)
                var masks = Directory.GetFiles(Path.Combine(MaskRoot(plugin), "masks"), "*.mkv");
                Assert.Single(masks);
                Assert.Single(Directory.GetFiles(Path.Combine(MaskRoot(plugin), "masks"), "*.source"));
                var written = File.GetLastWriteTimeUtc(masks[0]);
                Assert.False(Directory.Exists(Path.Combine(MaskRoot(plugin), "models")), "FOLDERSS_VE_PERSONMASK의 모델을 두고 또 받았습니다.");
                // ONNX Runtime은 zip에 든 것(runtimes\win-x64\native)을 불렀다 — Windows 폴더의 다른 onnxruntime.dll이 아니라
                var loaded = (string)plugin.Assembly.GetType("VideoEditor.Media.OnnxRuntimeLibrary", true).GetProperty("LoadedPath").GetValue(null);
                Assert.Equal(Path.Combine(plugin.PluginDirectory, "runtimes", "win-x64", "native", "onnxruntime.dll"), loaded, ignoreCase: true);

                // 미디어 패널에서 같은 원본: 다시 만들지 않는다(진행 창 없이 바로 알림)
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = MediaItem(window, "testsrc.mp4");
                });
                ui.Post(() => Ui.ContextMenu(Ui.ById<ListBox>(window, "MediaList"), "인물 마스크 만들기"));
                await ui.WaitUntil(() => Status(window).StartsWith("인물 마스크를 이미 만들어 두었습니다(testsrc.mp4)", StringComparison.Ordinal), Wait, "만들어 둔 마스크 알림");
                Assert.Null(ui.Run(() => Ui.Window("인물 마스크 만들기")));
                Assert.Equal(written, File.GetLastWriteTimeUtc(masks[0]));
                Assert.Single(Directory.GetFiles(Path.Combine(MaskRoot(plugin), "masks"), "*.mkv"));
                Assert.Single(Directory.GetFiles(Path.Combine(MaskRoot(plugin), "masks"), "*.source"));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                if (Directory.Exists(MaskRoot(plugin)))
                    Directory.Delete(MaskRoot(plugin), true);
            }
        }
    }
}
