using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>[파일 > 음성 인식으로 자막 만들기]: 고른 클립(또는 타임라인 전체 소리)을 이 PC의 whisper.cpp로 인식해 자막 트랙으로.</summary>
    public class SpeechToTextTests
    {
        [EditorFact]
        public async Task SpeechDialog_ShowsSource_AndRunsWithLocalWhisper()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            var whisperFolder = Path.Combine(plugin.Manager.DataDirectory, "whisper");
            try
            {
                // 소리 있는 클립이 없으면 창 대신 까닭을 알린다
                ui.Post(() => Ui.Menu(window, "파일", "음성 인식으로 자막 만들기"));
                await ui.WaitUntil(() => Status(window).StartsWith("소리가 있는 클립이 없습니다", StringComparison.Ordinal), Wait, "소리 없음 알림");

                // 소리 있는 시험 동영상을 놓고 골라서 연다
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "testsrc.mp4"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == 1, Wait, "가져오기");
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedIndex = 0;
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Count == 1, Wait, "클립 추가");
                ui.Run(() => SelectClip(window, n => n.Contains("testsrc.mp4")));
                ui.Post(() => Ui.Menu(window, "파일", "음성 인식으로 자막 만들기"));
                var dialog = await ui.WaitFor(() => Ui.Window("음성 인식으로 자막 만들기"), Wait, "음성 인식 창");
                ui.Run(() =>
                {
                    Assert.StartsWith("인식할 소리: 고른 클립 — testsrc.mp4 (00:00:00:00 ~ ", Ui.ById<TextBlock>(dialog, "SpeechSource").Text);
                    Assert.True(Ui.ById<RadioButton>(dialog, "SpeechModel_small").IsChecked);
                    Assert.NotNull(Ui.ById<RadioButton>(dialog, "SpeechModel_turbo"));
                    Assert.Equal("한국어", ((ComboBoxItem)Ui.ById<ComboBox>(dialog, "SpeechLanguage").SelectedItem).Content);
                    Assert.NotNull(Ui.ById<Button>(dialog, "SpeechScriptFileButton"));
                    Assert.False(Ui.ById<CheckBox>(dialog, "SpeechHighlight").IsChecked);
                    Ui.Snap(dialog, plugin.ShotFolder, "speech-to-text");
                });

                var whisper = Environment.GetEnvironmentVariable("FOLDERSS_VE_WHISPER");
                var model = string.IsNullOrEmpty(whisper) ? null : Path.Combine(whisper, "models", "ggml-small-q5_1.bin");
                if (model == null || !File.Exists(Path.Combine(whisper, "whisper-cli.exe")) || !File.Exists(model))
                {
                    ui.Run(() => Ui.Click(Ui.ById<Button>(dialog, "SpeechCloseButton")));
                    await ui.WaitUntil(() => Ui.Window("음성 인식으로 자막 만들기") == null, Wait, "창 닫힘");
                    return;
                }

                // 받아 둔 whisper.cpp·모델을 데이터 폴더에 두면 내려받지 않고 바로 인식한다(말이 없는 소리라 큐는 없다)
                Directory.CreateDirectory(Path.Combine(whisperFolder, "models"));
                foreach (var file in Directory.GetFiles(whisper).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(whisperFolder, Path.GetFileName(file)), true);
                // 모델과 말소리 구간 감지 모델(있으면 — 없으면 0.9MB를 받는다)
                foreach (var file in Directory.GetFiles(Path.Combine(whisper, "models"), "ggml-*.bin").Where(f => f == model || f.Contains("silero")))
                    File.Copy(file, Path.Combine(whisperFolder, "models", Path.GetFileName(file)), true);
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(dialog, "SpeechPrompt").Text = " 맹자, 반구저기 ";
                    Ui.ById<TextBox>(dialog, "SpeechScript").Text = "억울해서 피가 거꾸로 솟았습니다.";
                    Ui.Click(Ui.ById<Button>(dialog, "SpeechRunButton"));
                });
                await ui.WaitUntil(() => Ui.Window("음성 인식으로 자막 만들기") == null, TimeSpan.FromMinutes(3), "인식이 끝나 창 닫힘");
                await ui.WaitUntil(() => Status(window).StartsWith("말소리를 찾지 못했습니다(보통(추천) 모델)", StringComparison.Ordinal)
                                         || Status(window).StartsWith("음성 인식으로 자막", StringComparison.Ordinal), Wait, "인식 결과 알림");
                Assert.Equal("small", plugin.Manager.GetSetting("speechModel"));
                Assert.Equal("맹자, 반구저기", plugin.Manager.GetSetting("speechPrompt"));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                if (Directory.Exists(whisperFolder))
                    Directory.Delete(whisperFolder, true);
            }
        }
    }
}
