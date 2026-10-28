using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 파일 › 최근 프로젝트 열기(#41): 저장한 프로젝트를 메뉴에서 다시 열기(저장 안 한 편집은 먼저 묻기), 없는 파일 알림과 빼기, [목록 지우기], 편집 창 둘.
    /// </summary>
    public class RecentProjectsTests
    {
        private const string RecentKey = "recentProjects";
        private const string AskTitle = "최근 프로젝트 열기";

        private static Window DialogTitled(string title)
        {
            return Ui.Windows().FirstOrDefault(w => w.IsVisible && w.Title == title && Ui.ById<Button>(w, "AskButton0") != null);
        }

        /// <summary>설정에 남은 최근 경로(최근 것부터).</summary>
        private static List<string> Stored(PluginUnderTest plugin)
        {
            var json = plugin.Manager.GetSetting(RecentKey);
            if (string.IsNullOrEmpty(json))
                return new List<string>();
            using (var document = JsonDocument.Parse(json))
                return document.RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        }

        private static string Seed(params string[] paths)
        {
            return JsonSerializer.Serialize(paths.Select((p, i) => new Dictionary<string, object> { { "path", p }, { "at", new DateTime(2026, 10, 28, 9, 0, 0).AddMinutes(-i) } }));
        }

        /// <summary>
        /// 파일 › 최근 프로젝트 열기 하위 메뉴를 연다(열 때 설정에서 다시 읽는다). 위 메뉴가 아직 그려지지 않았으면 하위 메뉴는 그려진 뒤에 열리므로 기다린다.
        /// </summary>
        private static async Task<MenuItem> OpenRecent(UiThread ui, Window window)
        {
            // 숨은 데스크톱에서는 메뉴 팝업이 화면에 열리지 않아 SubmenuOpened가 오지 않는다 — 논리 항목을 따라가(Ui.Menu와 같음)
            // 하위 메뉴가 열릴 때와 같은 사건을 일으켜 목록을 새로 채운다
            return await ui.WaitFor(() =>
            {
                var file = Ui.Find<Menu>(window).Items.OfType<MenuItem>().First(m => Ui.HeaderText(m) == "파일");
                var recent = file.Items.OfType<MenuItem>().FirstOrDefault(m => AutomationProperties.GetAutomationId(m) == "RecentProjectsMenu");
                recent?.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, recent));
                return recent;
            }, Wait, "최근 프로젝트 하위 메뉴");
        }

        private static void CloseMenus(Window window)
        {
            foreach (var top in Ui.Find<Menu>(window).Items.OfType<MenuItem>())
                top.IsSubmenuOpen = false;
        }

        private static MenuItem Entry(MenuItem recent, string automationId)
        {
            return recent.Items.OfType<MenuItem>().FirstOrDefault(m => AutomationProperties.GetAutomationId(m) == automationId);
        }

        private static async Task ImportRed(UiThread ui, Window window, int count)
        {
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, Path.Combine(MediaFolder, "red.png"), Wait);
            await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Count == count, Wait, "사진 가져오기");
        }

        private static async Task SaveAs(UiThread ui, Window window, string project)
        {
            ui.Post(() => Ui.Menu(window, "파일", "다른 이름으로 저장"));
            await NativeDialogs.SaveAs(ui, project, Wait);
            await ui.WaitUntil(() => File.Exists(project) && (string)Get(Session(window), "FilePath") == project, Wait, "저장");
        }

        private static string NewFolder(PluginUnderTest plugin)
        {
            var folder = Path.Combine(plugin.Root, "recent-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            return folder;
        }

        [EditorFact]
        public async Task SavedProject_OpensFromRecentMenu_AfterAskingAboutUnsavedEdits()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SetSetting(RecentKey, null);
            var project = Path.Combine(NewFolder(plugin), "최근 시험.fvproj");
            var window = await Open(ui, plugin);
            try
            {
                // 처음엔 빈 목록: "(최근 프로젝트 없음)"과 [목록 지우기] 모두 꺼짐
                var recent = await OpenRecent(ui, window);
                ui.Run(() =>
                {
                    Assert.False(Entry(recent, "RecentProjectsEmpty").IsEnabled);
                    Assert.False(Entry(recent, "RecentProjectsClear").IsEnabled);
                    CloseMenus(window);
                });

                await ImportRed(ui, window, 1);
                await SaveAs(ui, window, project);
                Assert.Equal(new[] { project }, Stored(plugin));

                // 새 프로젝트에 사진을 넣어(저장 안 한 편집) 메뉴에서 연다 → 먼저 묻고 [저장 안 함]이면 연다
                ui.Post(() => Key(window, System.Windows.Input.Key.N, ModifierKeys.Control));
                await ui.WaitUntil(() => Get(Session(window), "FilePath") == null, Wait, "새 프로젝트");
                await ImportRed(ui, window, 1);
                recent = await OpenRecent(ui, window);
                var item = ui.Run(() =>
                {
                    var first = Entry(recent, "RecentProject0");
                    Assert.Equal("1 최근 시험", Ui.HeaderText(first));
                    Assert.Contains(project, (string)first.ToolTip);
                    Assert.Contains("마지막으로 연 시각", (string)first.ToolTip);
                    Assert.Equal(1.0, first.Opacity);
                    Assert.True(Entry(recent, "RecentProjectsClear").IsEnabled);
                    return first;
                });
                ui.Post(() => Ui.Invoke(item));
                var ask = await ui.WaitFor(() => DialogTitled("동영상 편집"), Wait, "저장할까요?");
                ui.Run(() => Ui.Click(Ui.ById<Button>(ask, "AskButton1")));
                await ui.WaitUntil(() => (string)Get(Session(window), "FilePath") == project && !(bool)Get(Session(window), "Dirty"), Wait, "최근 프로젝트 열기");
                Assert.Equal(new[] { project }, Stored(plugin));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                plugin.Manager.SetSetting(RecentKey, null);
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task MissingProject_IsDimmed_AskedAndRemoved_ThenListCleared()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var folder = NewFolder(plugin);
            var gone = Path.Combine(folder, "옮긴 것.fvproj");
            var kept = Path.Combine(folder, "남은 것.fvproj");
            File.WriteAllText(kept, "{}");
            plugin.Manager.SetSetting(RecentKey, Seed(gone, kept));
            var window = await Open(ui, plugin);
            try
            {
                var recent = await OpenRecent(ui, window);
                var item = ui.Run(() =>
                {
                    var missing = Entry(recent, "RecentProject0");
                    var present = Entry(recent, "RecentProject1");
                    Assert.Equal("1 옮긴 것", Ui.HeaderText(missing));
                    Assert.True(missing.Opacity < 1);
                    Assert.Equal("파일 없음", AutomationProperties.GetItemStatus(missing));
                    Assert.Equal(1.0, present.Opacity);
                    return missing;
                });

                // 누르면 알리고 목록에서 뺄지 묻는다 — [그대로 두기]면 남는다
                ui.Post(() => Ui.Invoke(item));
                var ask = await ui.WaitFor(() => DialogTitled(AskTitle), Wait, "없는 파일 알림");
                ui.Run(() =>
                {
                    Assert.Contains(Ui.Texts(ask), t => t.Contains(gone));
                    Ui.Click(Ui.ById<Button>(ask, "AskButton1"));
                });
                await ui.WaitUntil(() => DialogTitled(AskTitle) == null, Wait, "알림 닫힘");
                Assert.Equal(new[] { gone, kept }, Stored(plugin));

                // [목록에서 빼기]
                recent = await OpenRecent(ui, window);
                item = ui.Run(() => Entry(recent, "RecentProject0"));
                ui.Post(() => Ui.Invoke(item));
                ask = await ui.WaitFor(() => DialogTitled(AskTitle), Wait, "없는 파일 알림");
                ui.Run(() => Ui.Click(Ui.ById<Button>(ask, "AskButton0")));
                await ui.WaitUntil(() => Stored(plugin).SequenceEqual(new[] { kept }), Wait, "목록에서 빼기");
                Assert.Null(ui.Run(() => (string)Get(Session(window), "FilePath")));

                // [목록 지우기]
                recent = await OpenRecent(ui, window);
                var clear = ui.Run(() =>
                {
                    Assert.Equal("1 남은 것", Ui.HeaderText(Entry(recent, "RecentProject0")));
                    Assert.Null(Entry(recent, "RecentProject1"));
                    return Entry(recent, "RecentProjectsClear");
                });
                ui.Post(() => Ui.Invoke(clear));
                await ui.WaitUntil(() => plugin.Manager.GetSetting(RecentKey) == null, Wait, "목록 지우기");
                recent = await OpenRecent(ui, window);
                ui.Run(() =>
                {
                    Assert.Null(Entry(recent, "RecentProject0"));
                    Assert.False(Entry(recent, "RecentProjectsEmpty").IsEnabled);
                    CloseMenus(window);
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                plugin.Manager.SetSetting(RecentKey, null);
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task TwoWindows_SavedInOne_ShowsAndOpensInOther()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SetSetting(RecentKey, null);
            var project = Path.Combine(NewFolder(plugin), "두 창.fvproj");
            Window first = null, second = null;
            try
            {
                first = await Open(ui, plugin);
                second = await Open(ui, plugin);
                // 둘째 창의 메뉴를 한 번 열어 둔다(빈 목록) — 다시 열 때 설정에서 새로 읽는지 보려고
                var recent = await OpenRecent(ui, second);
                ui.Run(() =>
                {
                    Assert.NotNull(Entry(recent, "RecentProjectsEmpty"));
                    CloseMenus(second);
                });

                await ImportRed(ui, first, 1);
                await SaveAs(ui, first, project);

                recent = await OpenRecent(ui, second);
                var item = ui.Run(() =>
                {
                    Assert.Null(Entry(recent, "RecentProjectsEmpty"));
                    var only = Entry(recent, "RecentProject0");
                    Assert.Equal("1 두 창", Ui.HeaderText(only));
                    return only;
                });
                ui.Post(() => Ui.Invoke(item));
                await ui.WaitUntil(() => (string)Get(Session(second), "FilePath") == project, Wait, "둘째 창에서 열기");
                await ui.WaitUntil(() => Ui.ById<ListBox>(second, "MediaList").Items.Count == 1, Wait, "둘째 창 미디어");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                plugin.Manager.SetSetting(RecentKey, null);
                await CloseEditor(ui, second);
                await CloseEditor(ui, first);
            }
        }
    }
}
