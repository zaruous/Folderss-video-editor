using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 꾸민 글자의 한계(#34): 긴 글(2,000자 초과) 안내.
    /// </summary>
    public class StyledTextLimitsTests
    {
        private readonly ITestOutputHelper _out;

        public StyledTextLimitsTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static async Task<object> AddText(UiThread ui, Window window, string text)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
            ui.Run(() =>
            {
                Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
            });
            await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
            return ui.Run(() => Get(ProjectClips(window).First(c => (string)Get(c, "Text") == text), "Id"));
        }

        private static object ClipById(Window window, object id)
        {
            return ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
        }

        /// <summary>[글자] 탭 글 칸에 적고 칸을 떠나 고친다.</summary>
        private static void SetText(Window window, string text)
        {
            var box = Ui.ById<TextBox>(window, "PropText");
            Ui.Focus(box);
            box.Text = text;
            Ui.Focus(Canvas(window));
        }

        [EditorFact]
        public async Task LongText_NoticeListsWhatIsNotDrawn_ShortTextHasNone()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddText(ui, window, "긴 글 안내");
                // 네온(채우기 없음 + 윤곽선 + 광선)
                await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "꾸미기 탭");
                ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<Button>(window, "StylePreset8"), Wait, "스타일 모음");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset8")));
                await ui.WaitUntil(() => Get(ClipById(window, id), "Style") is object style && (bool)Get(Get(style, "Glow"), "Enabled"), Wait, "네온");
                Assert.Equal(Visibility.Collapsed, ui.Run(() => Ui.ById<TextBlock>(window, "StyleLongTextNotice").Visibility));

                // 2,001자 → 두 탭 모두 안내, 이 클립이 쓰는 것만
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").Items.OfType<TabItem>().First(t => (string)t.Header == "글자").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropText") is TextBox shown && shown.IsVisible ? shown : null, Wait, "글 칸");
                ui.Run(() => SetText(window, new string('가', 2001)));
                await ui.WaitUntil(() => ((string)Get(ClipById(window, id), "Text")).Length == 2001, Wait, "2,001자");
                var notice = await ui.WaitFor(() => Ui.ById<TextBlock>(window, "PropTextLongNotice") is TextBlock shown && shown.Visibility == Visibility.Visible ? shown : null, Wait, "긴 글 안내");
                var text = ui.Run(() => notice.Text);
                _out.WriteLine(text);
                Assert.StartsWith("글이 2,000자를 넘어", text);
                Assert.Contains("광선", text);
                Assert.Contains("속 빈 글자", text);
                Assert.DoesNotContain("반사", text);
                Assert.Equal(text, ui.Run(() => System.Windows.Automation.AutomationProperties.GetName(notice)));
                Assert.Equal(Visibility.Visible, ui.Run(() => Ui.ById<TextBlock>(window, "StyleLongTextNotice").Visibility));
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "long-text-notice"));

                // 2,000자 → 안내가 사라진다
                ui.Run(() => SetText(window, new string('가', 2000)));
                await ui.WaitUntil(() => ((string)Get(ClipById(window, id), "Text")).Length == 2000, Wait, "2,000자");
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "PropTextLongNotice").Visibility == Visibility.Collapsed, Wait, "안내 사라짐");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
