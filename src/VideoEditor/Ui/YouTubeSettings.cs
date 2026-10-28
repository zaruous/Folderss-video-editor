using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 유튜브 올리기 설정: 사용자 자신의 OAuth 클라이언트 ID(설정에 그대로)·보안 비밀번호(DPAPI로 암호화해 설정에),
    /// 로그인(새로 고침 토큰 — 데이터 폴더\youtube\token.dat, DPAPI). 편집 창·설정 탭·내보내기 창이 같은 계정 개체를 쓴다.
    /// </summary>
    internal static class YouTubeSettings
    {
        public const string ClientIdKey = "youtubeClientId";
        public const string ClientSecretKey = "youtubeClientSecret";
        public const string GuideUrl = "https://github.com/zaruous/Folderss-video-editor/blob/main/docs/youtube-upload.md";
        private static readonly ConditionalWeakTable<EditorServices, YouTubeAccount> Accounts = new ConditionalWeakTable<EditorServices, YouTubeAccount>();

        public static YouTubeAccount Account(EditorServices services)
        {
            return Accounts.GetValue(services, s =>
            {
                var store = new YouTubeTokenStore(Path.Combine(s.Manager.DataDirectory, "youtube"));
                return new YouTubeAccount(() => new YouTubeClient(s.Setting(ClientIdKey), store.UnprotectText(s.Setting(ClientSecretKey))), store);
            });
        }

        /// <summary>
        /// 클라이언트 ID·보안 비밀번호를 설정에 저장한다(둘 다 비우면 지움). 클라이언트 ID가 바뀌면 그 전 로그인은 쓸 수 없어 지운다.
        /// 저장했으면 null, 아니면 알릴 글.
        /// </summary>
        public static string Save(EditorServices services, YouTubeClient client)
        {
            var account = Account(services);
            var old = account.Client;
            if (client.Id == old.Id && client.Secret == old.Secret)
                return null;
            if (client.Id.Length > 0 || client.Secret.Length > 0)
            {
                var problem = client.Problem();
                if (problem != null)
                    return problem;
            }
            try
            {
                services.Manager.SetSetting(ClientIdKey, client.Id.Length == 0 ? null : client.Id);
                services.Manager.SetSetting(ClientSecretKey, account.Store.ProtectText(client.Secret));
                if (client.Id != old.Id)
                {
                    account.Forget();
                    account.Store.Delete();
                }
                return null;
            }
            catch (Exception ex)
            {
                return "유튜브 클라이언트 설정을 저장하지 못했습니다: " + ex.Message;
            }
        }

        public static string SignedInText(YouTubeAccount account)
        {
            if (!account.Client.IsComplete)
                return "유튜브: 클라이언트 ID·보안 비밀번호를 넣지 않았습니다.";
            var at = account.SignedInAt;
            return at == null ? "유튜브: 로그인 안 됨 — 처음 올릴 때 브라우저에서 로그인합니다." : "유튜브: 로그인됨 (" + at.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "에 로그인)";
        }

        /// <summary>시스템 브라우저로 연다. 열었으면 null, 아니면 알릴 글.</summary>
        public static string OpenInBrowser(Uri url)
        {
            if (url == null || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                return "열 수 없는 주소입니다.";
            try
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                return "브라우저를 열지 못했습니다: " + ex.Message;
            }
        }

        /// <summary>로그아웃하고 결과를 글로.</summary>
        public static async System.Threading.Tasks.Task<string> SignOut(YouTubeAccount account)
        {
            try
            {
                var revoked = await account.SignOut(CancellationToken.None);
                return revoked ? "로그아웃했습니다(구글에서도 권한을 취소함)."
                               : "이 PC의 로그인을 지웠습니다. 구글에 권한 취소를 보내지 못했으니 구글 계정 > 보안 > 타사 앱 연결에서 지우세요.";
            }
            catch (Exception ex)
            {
                return "로그아웃하지 못했습니다: " + ex.Message;
            }
        }
    }

    /// <summary>클라이언트 ID·보안 비밀번호 칸과 [JSON 파일에서 불러오기…] · [설정 방법]. 설정 탭과 올리기 창이 같이 쓴다.</summary>
    internal sealed class YouTubeClientFields : StackPanel
    {
        private readonly TextBox _id = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        private readonly PasswordBox _secret = new PasswordBox { Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        public YouTubeClientFields(YouTubeClient current, string idPrefix)
        {
            Children.Add(UiKit.Label("클라이언트 ID (…" + YouTubeClient.IdSuffix + ")"));
            _id.Text = current.Id;
            AutomationProperties.SetAutomationId(_id, idPrefix + "ClientId");
            AutomationProperties.SetName(_id, "클라이언트 ID");
            Children.Add(_id);
            var secretLabel = UiKit.Label("클라이언트 보안 비밀번호");
            secretLabel.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(secretLabel);
            // Folderss 테마에는 PasswordBox 스타일이 없어 글 칸과 같은 색을 직접 건다
            _secret.SetResourceReference(Control.BackgroundProperty, "ControlBackground");
            _secret.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            _secret.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            _secret.SetResourceReference(PasswordBox.CaretBrushProperty, "PrimaryText");
            _secret.Password = current.Secret;
            AutomationProperties.SetAutomationId(_secret, idPrefix + "ClientSecret");
            AutomationProperties.SetName(_secret, "클라이언트 보안 비밀번호");
            Children.Add(_secret);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var import = UiKit.TextButton("JSON 파일에서 불러오기…", idPrefix + "ImportJson", ImportJson);
            import.Margin = new Thickness(0);
            buttons.Children.Add(import);
            buttons.Children.Add(UiKit.TextButton("설정 방법 보기", idPrefix + "Guide", () => Show(YouTubeSettings.OpenInBrowser(new Uri(YouTubeSettings.GuideUrl)))));
            Children.Add(buttons);
            AutomationProperties.SetAutomationId(_status, idPrefix + "ClientStatus");
            Children.Add(_status);
        }

        public YouTubeClient Client { get { return new YouTubeClient(_id.Text, _secret.Password); } }

        public void Show(string message)
        {
            if (message != null)
                _status.Text = message;
        }

        /// <summary>Google Cloud 콘솔에서 내려받은 client_secret_….json에서 ID·비밀을 채운다(저장은 따로).</summary>
        private void ImportJson()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "OAuth 클라이언트 JSON 파일 고르기", Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 64 * 1024)
                    throw new YouTubeException("OAuth 클라이언트 JSON 파일이 아닙니다(너무 큼).");
                var client = YouTubeClient.FromJson(File.ReadAllText(dialog.FileName));
                _id.Text = client.Id;
                _secret.Password = client.Secret;
                _status.Text = client.Problem() ?? "불러왔습니다. 저장하면 씁니다.";
            }
            catch (Exception ex)
            {
                _status.Text = ex is YouTubeException ? ex.Message : "파일을 읽지 못했습니다: " + ex.Message;
            }
        }
    }

    /// <summary>설정 탭의 "유튜브에 올리기" 부분: 클라이언트 ID·보안 비밀번호, 로그인 상태, [로그아웃]. 값은 설정 창의 [저장]에서 저장한다.</summary>
    internal sealed class YouTubeSettingsSection
    {
        private readonly EditorServices _services;
        private readonly YouTubeClientFields _fields;

        public YouTubeSettingsSection(EditorServices services)
        {
            _services = services;
            var account = YouTubeSettings.Account(services);
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var label = UiKit.Label("유튜브에 올리기 — 자기 Google Cloud 프로젝트의 OAuth 클라이언트(데스크톱 앱 유형). 할당량은 그 프로젝트 몫입니다");
            label.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(label);
            _fields = new YouTubeClientFields(account.Client, "SettingsYouTube") { Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(_fields);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var signOut = UiKit.TextButton("로그아웃", "SettingsYouTubeSignOut", () => { });
            DockPanel.SetDock(signOut, Dock.Right);
            row.Children.Add(signOut);
            var state = UiKit.Label(YouTubeSettings.SignedInText(account), true);
            state.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(state, "SettingsYouTubeAccount");
            row.Children.Add(state);
            panel.Children.Add(row);
            signOut.IsEnabled = account.SignedInAt != null;
            signOut.Click += async (s, e) =>
            {
                signOut.IsEnabled = false;
                state.Text = "로그아웃하는 중…";
                var message = await YouTubeSettings.SignOut(account);
                state.Text = YouTubeSettings.SignedInText(account);
                _fields.Show(message);
                signOut.IsEnabled = account.SignedInAt != null;
            };
            View = panel;
        }

        public FrameworkElement View { get; }

        /// <summary>설정 창 [저장]: 클라이언트 값이 바뀌었으면 저장한다. 잘못된 값이면 예외(설정 저장 실패 메시지에 모임).</summary>
        public void Save()
        {
            var error = YouTubeSettings.Save(_services, _fields.Client);
            if (error != null)
                throw new InvalidOperationException(error);
        }
    }
}
