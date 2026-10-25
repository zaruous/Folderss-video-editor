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
