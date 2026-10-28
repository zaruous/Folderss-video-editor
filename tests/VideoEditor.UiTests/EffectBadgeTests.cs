using System;
using System.Collections;
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
    /// <summary>타임라인 클립의 동영상 효과 배지와 화면 낭독 이름(#34).</summary>
    public class EffectBadgeTests
    {
        private readonly ITestOutputHelper _out;

        public EffectBadgeTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static object Red(Window window)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png");
        }

        private static Rect Badge(Window window)
        {
            var canvas = Canvas(window);
            return (Rect)canvas.GetType().GetMethod("EffectBadgeBounds", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(canvas, new[] { Get(Red(window), "Id") });
        }

        private static string RedName(Window window)
        {
            return Clips(window).First(n => n.Contains("red.png"));
        }

        [EditorFact]
        public async Task TimelineClip_ShowsEffectBadge_AndSpeaksEffectNames()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
                await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "red.png"), Wait);
                await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == 1, Wait, "가져오기");
                ui.Run(() =>
                {
                    Ui.ById<ListBox>(window, "MediaList").SelectedIndex = 0;
                    Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
                });
                await ui.WaitUntil(() => Clips(window).Count == 1, Wait, "사진 클립");
                // 효과가 없으면 배지도 이름도 없다
                Assert.DoesNotContain("· 효과", ui.Run(() => RedName(window)));
                Assert.True(ui.Run(() => Badge(window)).IsEmpty);

                // [효과] 탭에서 스캔 라인을 넣는다
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "EffectsTab"));
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록");
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("red.png"));
                    Ui.Click(Ui.ById<Button>(window, "VideoEffectApplyButton"));
                });
                await ui.WaitUntil(() => ((IList)Get(Red(window), "Effects"))?.Count == 1, Wait, "효과 하나");
                var effectName = ui.Run(() =>
                {
                    var id = (string)Get(((IList)Get(Red(window), "Effects"))[0], "Id");
                    var def = plugin.Assembly.GetType("VideoEditor.Core.VideoEffects", true).GetMethod("Find").Invoke(null, new object[] { id });
                    return (string)Get(def, "Name");
                });
                await ui.WaitUntil(() => !Badge(window).IsEmpty, Wait, "효과 배지");
                ui.Run(() =>
                {
                    var name = RedName(window);
                    _out.WriteLine(name);
                    Assert.EndsWith(" · 효과 " + effectName, name);
                    // 배지는 클립 안 오른쪽 위
                    var badge = Badge(window);
                    var clip = ClipBounds(window, Red(window));
                    _out.WriteLine("클립 " + clip + " · 배지 " + badge);
                    Assert.True(clip.Contains(badge), "배지가 클립 안: " + badge + " / " + clip);
                    Assert.InRange(clip.Right - badge.Right, 0, 8);
                    Assert.InRange(badge.Top - clip.Top, 0, 4);
                });
                var single = ui.Run(() => Badge(window));

                // 하나 더 넣으면 "✦2"(배지가 넓어짐)와 두 이름(그리는 차례)
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "VideoEffectApplyButton")));
                await ui.WaitUntil(() => ((IList)Get(Red(window), "Effects"))?.Count == 2, Wait, "효과 둘");
                await ui.WaitUntil(() => Badge(window).Width > single.Width, Wait, "배지 ✦2");
                Assert.EndsWith(" · 효과 " + effectName + ", " + effectName, ui.Run(() => RedName(window)));
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "timeline-effect-badge"));

                // 실행 취소로 효과를 다 빼면 배지와 이름도 사라진다
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Get(Red(window), "Effects") == null && Badge(window).IsEmpty, Wait, "배지 사라짐");
                Assert.DoesNotContain("· 효과", ui.Run(() => RedName(window)));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
