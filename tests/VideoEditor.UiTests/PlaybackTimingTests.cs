using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 재생 속도: 재생 헤드가 실제 시간만큼만 간다(화면이 소리보다 앞서 달리지 않는다).
    /// 예전에는 FFmpeg가 실시간보다 빨리 그리면 아직 보여 줄 시각이 안 된 프레임까지 틱마다 보여 줘서 약 2배로 달렸다.
    /// </summary>
    public class PlaybackTimingTests
    {
        private readonly ITestOutputHelper _out;

        public PlaybackTimingTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>"시:분:초:프레임"(30fps) → 프레임.</summary>
        private static long Frames(string timecode)
        {
            var parts = timecode.Split(':').Select(int.Parse).ToArray();
            return ((parts[0] * 60L + parts[1]) * 60 + parts[2]) * 30 + parts[3];
        }

        [EditorFact]
        public async Task Playback_PlayheadFollowsWallClock()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                // 글자 클립(5초)은 FFmpeg가 실시간보다 훨씬 빨리 그린다 — 앞서 달리면 바로 드러난다
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "재생 속도";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains("재생 속도")), Wait, "글자 클립");
                ui.Run(() =>
                {
                    Key(window, System.Windows.Input.Key.Home);
                    Key(window, System.Windows.Input.Key.Space);
                });
                await ui.WaitUntil(() => Frames(Ui.ById<TextBox>(window, "TimecodeBox").Text) >= 6, Wait, "재생 시작");
                // 시작 지연을 빼고 두 시점 사이의 진행만 비교한다
                var watch = Stopwatch.StartNew();
                var first = Frames(ui.Run(() => Ui.ById<TextBox>(window, "TimecodeBox").Text));
                await Task.Delay(1500);
                var second = Frames(ui.Run(() => Ui.ById<TextBox>(window, "TimecodeBox").Text));
                var wall = watch.Elapsed.TotalSeconds;
                var played = (second - first) / 30.0;
                _out.WriteLine(string.Format("실제 {0:0.00}초 동안 재생 헤드 {1:0.00}초({2} → {3}프레임)", wall, played, first, second));
                Assert.InRange(played / wall, 0.8, 1.2);
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
