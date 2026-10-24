using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>자막 트랙(이슈 #25): 파일 메뉴로 SRT를 가져와 미리 보기에 입히고, 속성 패널에서 큐 글을 고친다.</summary>
    public class SubtitleTrackTests
    {
        [EditorFact]
        public async Task ImportSrt_FromFileMenu_ShowsOnPreview_AndCueTextIsEditable()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var srt = Path.Combine(plugin.Root, "narration.srt");
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\n첫 번째 자막 FIRST\n\n2\n00:00:02,000 --> 00:00:03,500\n<i>두 번째</i> 자막\n", new UTF8Encoding(false));
            var window = await Open(ui, plugin);
            try
            {
                ui.Post(() => Ui.Menu(window, "파일", "자막 가져오기"));
                await NativeDialogs.Open(ui, srt, Wait);
                await ui.WaitUntil(() => Clips(window).Count(n => n.StartsWith("자막 · ", StringComparison.Ordinal)) == 2, Wait, "자막 트랙의 큐 2개");
                ui.Run(() =>
                {
                    Assert.Contains("자막 · 첫 번째 자막 FIRST · 00:00:00:15–00:00:02:00", Clips(window));
                    Assert.StartsWith("자막 2개를 가져왔습니다", Status(window));
                });

                // 큐 안(1초)으로 가면 미리 보기에 자막이 보인다(빈 타임라인이라 자막 말고는 검정)
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "TimecodeBox");
                    Ui.Focus(box);
                    box.Text = "00:00:01:00";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "미리 보기 자막");

                // 큐를 고르면 [자막] 탭에서 글을 고친다
                ui.Run(() => SelectClip(window, n => n.StartsWith("자막 · 첫 번째", StringComparison.Ordinal)));
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropSubtitleText") is TextBox shown && shown.IsVisible ? shown : null, Wait, "자막 탭");
                ui.Run(() =>
                {
                    Assert.Null(Ui.ById<TextBox>(window, "PropFontSize"));
                    var box = Ui.ById<TextBox>(window, "PropSubtitleText");
                    Ui.Focus(box);
                    box.Text = "고친 자막";
                    Ui.Focus(Canvas(window));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.StartsWith("자막 · 고친 자막", StringComparison.Ordinal)), Wait, "고친 큐");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "subtitle-track"));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
