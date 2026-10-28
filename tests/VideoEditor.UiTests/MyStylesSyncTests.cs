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
    /// 내 스타일을 여러 편집 창 사이에 곧바로 반영(#34): 한 창에서 저장·지우면 다른 창의 속성 패널 [꾸미기]·미디어 패널 [글자] 줄이 탭을 다시 보지 않아도 바뀐다.
    /// </summary>
    public class MyStylesSyncTests
    {
        private const string MyStylesKey = "textStyles";
        private readonly ITestOutputHelper _out;

        public MyStylesSyncTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Window DialogTitled(string title)
        {
            return Ui.Windows().FirstOrDefault(w => w.IsVisible && w.Title == title);
        }

        /// <summary>글자 클립을 넣고 [꾸미기] 탭을 연다(미디어 패널은 [글자] 탭에 머문다).</summary>
        private static async Task AddTextAndShowStyles(UiThread ui, Window window, string text)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
            ui.Run(() =>
            {
                Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
            });
            await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
            await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "꾸미기 탭");
            ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
            await ui.WaitFor(() => Ui.ById<Button>(window, "StyleSaveMine") is Button shown && shown.IsVisible ? shown : null, Wait, "[내 스타일로 저장]");
        }

        [EditorFact]
        public async Task MyStyles_SavedOrRemovedInOneWindow_OtherWindowFollowsAtOnce()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SetSetting(MyStylesKey, null);
            Window first = null, second = null;
            try
            {
                first = await Open(ui, plugin);
                second = await Open(ui, plugin);
                await AddTextAndShowStyles(ui, second, "둘째 창");
                await AddTextAndShowStyles(ui, first, "첫째 창");
                Assert.Null(ui.Run(() => Ui.ById<Button>(second, "StyleMine0")));
                Assert.Null(ui.Run(() => Ui.ById<Button>(second, "NewTextMine0")));

                // 첫째 창에서 저장 → 둘째 창의 두 줄에 곧바로(둘째 창의 탭은 그대로 둔 채)
                ui.Post(() => Ui.Click(Ui.ById<Button>(first, "StyleSaveMine")));
                var input = await ui.WaitFor(() => DialogTitled("내 스타일로 저장"), Wait, "이름 창");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(input, "DialogInputBox").Text = "창 사이";
                    Ui.Click(Ui.ById<Button>(input, "DialogOkButton"));
                });
                await ui.WaitFor(() => Ui.ById<Button>(second, "StyleMine0"), Wait, "둘째 창 속성 패널의 내 스타일");
                await ui.WaitFor(() => Ui.ById<Button>(second, "NewTextMine0"), Wait, "둘째 창 미디어 패널의 내 스타일");
                ui.Run(() =>
                {
                    Assert.Equal("창 사이", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(second, "StyleMine0")));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<TextBlock>(second, "StyleMineHint").Visibility);
                    // 둘째 창 미디어 패널에서 그 스타일을 고른다
                    Ui.Click(Ui.ById<Button>(second, "NewTextMine0"));
                });
                // 자동화 Invoke는 클릭을 디스패처에 넣어 나중에 돌린다 — 결과는 기다려서 본다
                await ui.WaitUntil(() => Ui.ById<TextBlock>(second, "NewTextStyleNote").Text == "스타일: 창 사이", Wait, "둘째 창에서 고른 내 스타일");
                // 썸네일도 그린다(둘째 창의 탭이 이미 보이는 중)
                await ui.WaitUntil(() => Ui.ById<Image>(second, "StyleMine0Image")?.Source != null, Wait, "둘째 창 썸네일");
                ui.Run(() => Ui.Snap(second, plugin.ShotFolder, "my-styles-sync"));

                // 하나 더 저장해도 둘째 창에서 고른 것은 그대로
                ui.Post(() => Ui.Click(Ui.ById<Button>(first, "StyleSaveMine")));
                input = await ui.WaitFor(() => DialogTitled("내 스타일로 저장"), Wait, "이름 창");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(input, "DialogInputBox").Text = "둘째";
                    Ui.Click(Ui.ById<Button>(input, "DialogOkButton"));
                });
                await ui.WaitFor(() => Ui.ById<Button>(second, "NewTextMine1"), Wait, "둘째 창에 둘째 스타일");
                Assert.Equal("스타일: 창 사이", ui.Run(() => Ui.ById<TextBlock>(second, "NewTextStyleNote").Text));

                // 첫째 창에서 지우면 둘째 창에서도 사라지고 고른 것이 풀린다
                ui.Run(() => Ui.ContextMenu(Ui.ById<Button>(first, "StyleMine0"), "지우기"));
                await ui.WaitUntil(() => Ui.ById<Button>(second, "StyleMine1") == null, Wait, "둘째 창에서도 지움");
                ui.Run(() =>
                {
                    Assert.Equal("둘째", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(second, "StyleMine0")));
                    Assert.Equal("둘째", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(second, "NewTextMine0")));
                    Assert.Null(Ui.ById<Button>(second, "NewTextMine1"));
                    Assert.Equal("스타일: 꾸미지 않음", Ui.ById<TextBlock>(second, "NewTextStyleNote").Text);
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, second);
                await CloseEditor(ui, first);
                plugin.Manager.SetSetting(MyStylesKey, null);
            }
        }
    }
}
