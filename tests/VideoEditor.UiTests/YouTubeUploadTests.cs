using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// [파일 > 유튜브에 올리기…](#9): 클라이언트가 없으면 설정 안내와 입력 칸부터, 저장하면 올리기 칸(제목 = 파일 이름, 비공개, 아동용을 골라야 [올리기]).
    /// 설정 탭의 클라이언트 칸·로그인 상태. 네트워크·로그인은 하지 않는다(가짜 클라이언트 값).
    /// </summary>
    public class YouTubeUploadTests
    {
        private const string ClientId = "1234-uitest.apps.googleusercontent.com";
        private const string ClientSecret = "GOCSPX-uitest-secret";

        [EditorFact]
        public async Task UploadDialog_AsksForClient_ThenShowsPrivateDefaults()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SetSetting("youtubeClientId", null);
            plugin.Manager.SetSetting("youtubeClientSecret", null);
            var folder = Path.Combine(plugin.Root, "youtube-upload");
            Directory.CreateDirectory(folder);
            var video = Path.Combine(folder, "내 첫 영상.mp4");
            File.WriteAllBytes(video, new byte[1024]);
            var window = await Open(ui, plugin);
            Window settings = null;
            try
            {
                ui.Post(() => Ui.Menu(window, "파일", "유튜브에 올리기"));
                await NativeDialogs.Open(ui, video, Wait);
                var dialog = await ui.WaitFor(() => Ui.Window("유튜브에 올리기"), Wait, "유튜브에 올리기 창");

                // 클라이언트가 없으면 안내와 입력 칸부터
                ui.Run(() =>
                {
                    Assert.Null(Ui.ById<TextBox>(dialog, "YouTubeTitleBox"));
                    Assert.NotNull(Ui.ById<Button>(dialog, "YouTubeGuide"));
                    Assert.NotNull(Ui.ById<Button>(dialog, "YouTubeImportJson"));
                    Ui.Snap(dialog, plugin.ShotFolder, "youtube-setup");
                    Ui.ById<TextBox>(dialog, "YouTubeClientId").Text = "잘못된 값";
                    Ui.ById<PasswordBox>(dialog, "YouTubeClientSecret").Password = ClientSecret;
                    Ui.Click(Ui.ById<Button>(dialog, "YouTubeSaveClientButton"));
                });
                // 자동화 Invoke는 클릭을 디스패처에 넣어 나중에 돌린다 — 결과는 기다려서 본다
                await ui.WaitUntil(() => Ui.ById<TextBlock>(dialog, "YouTubeClientStatus").Text.Contains(".apps.googleusercontent.com"), Wait, "잘못된 클라이언트 ID 안내");
                Assert.Null(plugin.Manager.GetSetting("youtubeClientId"));
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(dialog, "YouTubeClientId").Text = ClientId;
                    Ui.Click(Ui.ById<Button>(dialog, "YouTubeSaveClientButton"));
                });
                await ui.WaitUntil(() => Ui.ById<TextBox>(dialog, "YouTubeTitleBox") != null, Wait, "저장 뒤 올리기 칸");

                // 저장하면 올리기 칸: 제목 = 파일 이름, 공개 범위 = 비공개, 아동용을 고르기 전에는 [올리기]가 꺼짐
                ui.Run(() =>
                {
                    Assert.Equal(video, Ui.ById<TextBox>(dialog, "YouTubePathBox").Text);
                    Assert.Equal("내 첫 영상", Ui.ById<TextBox>(dialog, "YouTubeTitleBox").Text);
                    Assert.StartsWith("비공개", Ui.ContentText(((ComboBoxItem)Ui.ById<ComboBox>(dialog, "YouTubePrivacyBox").SelectedItem).Content));
                    Assert.Contains("로그인 안 됨", Ui.ById<TextBlock>(dialog, "YouTubeAccountText").Text);
                    Assert.False(Ui.ById<Button>(dialog, "YouTubeSignOutButton").IsVisible);
                    var upload = Ui.ById<Button>(dialog, "YouTubeUploadButton");
                    Assert.False(upload.IsEnabled);
                    Ui.ById<RadioButton>(dialog, "YouTubeKidsNo").IsChecked = true;
                    Assert.True(upload.IsEnabled);
                    Ui.Snap(dialog, plugin.ShotFolder, "youtube-upload");
                });
                // ID는 그대로, 비밀은 암호화해 설정에
                Assert.Equal(ClientId, plugin.Manager.GetSetting("youtubeClientId"));
                var stored = plugin.Manager.GetSetting("youtubeClientSecret");
                Assert.False(string.IsNullOrEmpty(stored));
                Assert.DoesNotContain(ClientSecret, stored);
                ui.Run(() => Ui.Click(Ui.ById<Button>(dialog, "YouTubeCloseButton")));
                await ui.WaitUntil(() => Ui.Window("유튜브에 올리기") == null, Wait, "올리기 창 닫힘");

                // 설정 탭: 저장한 클라이언트, 로그인 상태, 로그아웃(로그인하지 않았으면 꺼짐). 비우고 저장하면 지운다
                var page = plugin.Manager.SettingsPages.Single(p => p.Title == "동영상 편집");
                settings = ui.Run(() =>
                {
                    // Folderss 설정 창과 같은 높이(540) — 플러그인 화면을 스크롤 없이 담으므로 화면이 스스로 스크롤해야 맨 아래 유튜브 칸에 닿는다
                    var host = new Window { Title = "설정 시험", Width = 720, Height = 540, Content = page.CreateView(), ShowActivated = false, ShowInTaskbar = false };
                    host.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                    host.Show();
                    return host;
                });
                ui.Run(() =>
                {
                    // 유튜브 칸까지 스크롤하면 창 안에 보인다(예전에는 스크롤이 없어 창 아래로 잘렸다)
                    var scroller = Assert.IsType<ScrollViewer>(settings.Content);
                    var field = Ui.ById<TextBox>(settings, "SettingsYouTubeClientId");
                    field.BringIntoView();
                    settings.UpdateLayout();
                    var top = field.TranslatePoint(new Point(0, 0), scroller).Y;
                    Assert.True(scroller.ScrollableHeight > 0, "540 높이에서는 스크롤이 생긴다");
                    Assert.InRange(top, 0, scroller.ViewportHeight - field.ActualHeight);
                    Assert.Equal(ClientId, field.Text);
                    Assert.Equal(ClientSecret, Ui.ById<PasswordBox>(settings, "SettingsYouTubeClientSecret").Password);
                    Assert.Contains("로그인 안 됨", Ui.ById<TextBlock>(settings, "SettingsYouTubeAccount").Text);
                    Assert.False(Ui.ById<Button>(settings, "SettingsYouTubeSignOut").IsEnabled);
                    Ui.Snap(settings, plugin.ShotFolder, "youtube-settings");
                    Ui.ById<TextBox>(settings, "SettingsYouTubeClientId").Text = "";
                    Ui.ById<PasswordBox>(settings, "SettingsYouTubeClientSecret").Password = "";
                    page.Save();
                });
                Assert.Null(plugin.Manager.GetSetting("youtubeClientId"));
                Assert.Null(plugin.Manager.GetSetting("youtubeClientSecret"));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                plugin.Manager.SetSetting("youtubeClientId", null);
                plugin.Manager.SetSetting("youtubeClientSecret", null);
                if (settings != null)
                    ui.Run(() => settings.Close());
                await CloseEditor(ui, window);
            }
        }
    }
}
