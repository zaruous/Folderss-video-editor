using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>같은 이름으로 사진을 바꿔 넣고 프로젝트를 다시 열면 미디어 크기를 다시 읽는다(낡은 크기를 보이지 않게).</summary>
    public class MediaRefreshTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public MediaRefreshTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>
        /// 예전 프로젝트의 화면 시작 다시 읽기(#30, Windows 실측): 읽은 판(ProbeVersion)이 없는 동영상 8개짜리 프로젝트를 열면
        /// 목록은 바로 보이고, 동영상마다 배경에서 다시 읽어 판 1이 된다. 그동안 편집 창(UI 스레드)은 막히지 않는다.
        /// </summary>
        [EditorFact]
        public async Task OldProject_VideosReadAgainInBackground_EditorStaysResponsive()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var folder = Path.Combine(plugin.Root, "old-probe-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            var names = Enumerable.Range(1, 8).Select(i => "v" + i + ".mp4").ToArray();
            foreach (var name in names)
                File.Copy(Path.Combine(MediaFolder, "testsrc.mp4"), Path.Combine(folder, name));
            var project = Path.Combine(folder, "old.fvproj");
            var window = await Open(ui, plugin);
            try
            {
                plugin.Manager.SetSetting("lastFolder", folder);
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, string.Join(" ", names.Select(n => "\"" + n + "\"")), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == names.Length, Wait, "동영상 8개 가져오기");
                ui.Post(() => Ui.Menu(window, "파일", "다른 이름으로 저장"));
                await NativeDialogs.SaveAs(ui, project, Wait);
                await ui.WaitUntil(() => File.Exists(project), Wait, "저장");

                // 예전 프로젝트로: 미디어의 읽은 판(ProbeVersion)을 지운다
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(project));
                var removed = 0;
                foreach (var media in node["Media"].AsArray())
                {
                    if (media.AsObject().Remove("ProbeVersion"))
                        removed++;
                }
                Assert.Equal(names.Length, removed);
                File.WriteAllText(project, node.ToJsonString());

                int ProbeVersion(object media) => (int)Get(media, "ProbeVersion");
                System.Collections.Generic.List<object> Media() => ((System.Collections.IEnumerable)Get(Project(window), "Media")).Cast<object>().ToList();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                ui.Post(() => Ui.Menu(window, "파일", "프로젝트 열기"));
                await NativeDialogs.Open(ui, project, Wait);
                var dialogDone = watch.ElapsedMilliseconds;
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == names.Length && (string)Get(Session(window), "FilePath") == project, Wait, "목록");
                var listed = watch.ElapsedMilliseconds;
                // 다시 읽는 동안 UI 스레드가 응답하는지(왕복 시간의 최대)
                var worst = 0L;
                var readAgain = -1L;
                while (watch.Elapsed < TimeSpan.FromMinutes(2))
                {
                    var ping = System.Diagnostics.Stopwatch.StartNew();
                    var done = ui.Run(() => Media().All(m => ProbeVersion(m) == 1));
                    worst = Math.Max(worst, ping.ElapsedMilliseconds);
                    if (done)
                    {
                        readAgain = watch.ElapsedMilliseconds;
                        break;
                    }
                    await Task.Delay(50);
                }
                Assert.True(readAgain >= 0, "2분 안에 모두 다시 읽지 못했습니다");
                ui.Run(() =>
                {
                    Assert.False((bool)Get(Session(window), "Dirty"));
                    Ui.Snap(window, plugin.ShotFolder, "old-project-reread");
                });
                _out.WriteLine(string.Format("예전 프로젝트(동영상 {0}개) 열기: 대화상자 뒤 목록까지 {1}ms · 모두 다시 읽기까지 {2}ms(동영상당 약 {3}ms) · 그동안 UI 스레드 왕복 최대 {4}ms",
                    names.Length, listed - dialogDone, readAgain - dialogDone, (readAgain - dialogDone) / names.Length, worst));
                Assert.True(worst < 500, "UI 스레드 응답 " + worst + "ms");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                plugin.Manager.SetSetting("lastFolder", MediaFolder);
                await CloseEditor(ui, window);
            }
        }
        [EditorFact]
        public async Task ReopenedProject_RereadsReplacedPhotoSize()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var folder = Path.Combine(plugin.Root, "refresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var photo = Path.Combine(folder, "scene.png");
            File.Copy(Path.Combine(MediaFolder, "red.png"), photo);
            var project = Path.Combine(folder, "scene.fvproj");
            var window = await Open(ui, plugin);
            try
            {
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, photo, Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == 1, Wait, "가져오기");
                var media = ui.Run(() => ((System.Collections.IEnumerable)Get(Project(window), "Media")).Cast<object>().Single());
                var (width, height) = ui.Run(() => ((int)Get(media, "Width"), (int)Get(media, "Height")));
                Assert.True((long)ui.Run(() => Get(media, "FileSize")) > 0);
                ui.Post(() => Ui.Menu(window, "파일", "다른 이름으로 저장"));
                await NativeDialogs.SaveAs(ui, project, Wait);
                await ui.WaitUntil(() => File.Exists(project), Wait, "저장");

                // 같은 이름으로 세로 사진(300×600)을 넣고 다시 연다
                RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=300x600", "-frames:v", "1", photo);
                ui.Post(() => Ui.Menu(window, "파일", "프로젝트 열기"));
                await NativeDialogs.Open(ui, project, Wait);
                await ui.WaitUntil(() => Status(window).StartsWith("바뀐 미디어 1개의 크기·길이를 다시 읽었습니다: scene.png", StringComparison.Ordinal), Wait, "다시 읽기");
                ui.Run(() =>
                {
                    var reread = ((System.Collections.IEnumerable)Get(Project(window), "Media")).Cast<object>().Single();
                    Assert.Equal((300, 600), ((int)Get(reread, "Width"), (int)Get(reread, "Height")));
                    Assert.NotEqual((width, height), (300, 600));
                    // 다시 읽기는 편집이 아니다(실행 취소·저장 안 함 표시 없음)
                    Assert.False((bool)Get(Session(window), "Dirty"));
                    // 화면(가로)과 비율이 달라 미디어 목록에 알린다
                    var item = Ui.ById<ListBox>(window, "MediaList").Items.Cast<System.Windows.FrameworkElement>().Single();
                    Assert.Contains(Ui.Texts(item), t => t.Contains("화면과 비율 다름"));
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
