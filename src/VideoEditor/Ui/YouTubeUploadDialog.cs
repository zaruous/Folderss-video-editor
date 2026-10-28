using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// [유튜브에 올리기] 창: 파일 · 제목(기본: 파일 이름) · 설명 · 공개 범위(기본 비공개) · 아동용 여부(꼭 고름) → [올리기].
    /// 로그인하지 않았으면 먼저 시스템 브라우저로 구글 로그인. 올리는 동안 진행률과 [취소], 끝나면 영상 링크.
    /// 클라이언트 ID·보안 비밀번호가 없으면 설정 방법 안내와 입력 칸부터 보인다.
    /// </summary>
    internal sealed class YouTubeUploadDialog : Window
    {
        private readonly EditorServices _services;
        private readonly YouTubeAccount _account;
        private readonly StackPanel _setup = new StackPanel { Margin = new Thickness(18) };
        private readonly StackPanel _main = new StackPanel { Margin = new Thickness(18) };
        private readonly YouTubeClientFields _fields;
        private readonly TextBox _path = new TextBox { IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBox _title = new TextBox { MaxLength = YouTubeVideo.MaxTitle };
        private readonly TextBox _description = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = YouTubeVideo.MaxDescriptionBytes
        };
        private readonly ComboBox _privacy = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly RadioButton _kidsNo = new RadioButton { GroupName = "YouTubeKids", Margin = new Thickness(0, 2, 16, 2) };
        private readonly RadioButton _kidsYes = new RadioButton { GroupName = "YouTubeKids", Margin = new Thickness(0, 2, 0, 2) };
        private readonly TextBlock _accountText = UiKit.Label("", true);
        private readonly Button _signOut;
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 10, 0, 0), Maximum = 100, Visibility = Visibility.Collapsed };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly DockPanel _linkRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly TextBox _link = new TextBox { IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly Button _upload;
        private readonly Button _close;
        private readonly List<UIElement> _inputs = new List<UIElement>();
        private CancellationTokenSource _cancel;
        private bool _uploaded;
        private bool _closeWhenDone;

        public YouTubeUploadDialog(EditorServices services, string path)
        {
            _services = services;
            _account = YouTubeSettings.Account(services);
            Title = "유튜브에 올리기";
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            // ---------- 설정 안내(클라이언트가 없을 때) ----------
            _setup.Children.Add(new TextBlock { Text = "유튜브에 올리려면 먼저 설정하세요", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            _setup.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "플러그인에는 구글 API 클라이언트가 들어 있지 않습니다. 자기 Google Cloud 프로젝트에서 YouTube Data API v3를 켜고 "
                       + "\"데스크톱 앱\" 유형의 OAuth 클라이언트를 만들어 그 ID와 보안 비밀번호를 넣으세요. 할당량은 그 프로젝트 몫입니다.\n\n"
                       + "검증받지 않은 프로젝트로 올린 동영상은 공개 범위와 상관없이 비공개로 묶입니다. 자세한 순서는 [설정 방법 보기]."
            });
            _fields = new YouTubeClientFields(_account.Client, "YouTube") { Margin = new Thickness(0, 12, 0, 0) };
            _setup.Children.Add(_fields);
            var setupButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var save = UiKit.TextButton("저장하고 계속", "YouTubeSaveClientButton", SaveClient);
            save.IsDefault = true;
            setupButtons.Children.Add(save);
            setupButtons.Children.Add(UiKit.TextButton("닫기", "YouTubeSetupCloseButton", Close));
            _setup.Children.Add(setupButtons);

            // ---------- 올리기 ----------
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            void Add(string label, FrameworkElement field, string automationId)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var text = UiKit.Label(label);
                text.Margin = new Thickness(0, 4, 12, 4);
                text.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(text, row);
                grid.Children.Add(text);
                field.Margin = new Thickness(0, 4, 0, 4);
                if (automationId != null)
                {
                    AutomationProperties.SetAutomationId(field, automationId);
                    AutomationProperties.SetName(field, label);
                }
                Grid.SetRow(field, row);
                Grid.SetColumn(field, 1);
                grid.Children.Add(field);
                row++;
            }

            var pathRow = new DockPanel();
            var browse = UiKit.TextButton("찾아보기…", "YouTubeBrowseButton", Browse);
            DockPanel.SetDock(browse, Dock.Right);
            pathRow.Children.Add(browse);
            AutomationProperties.SetAutomationId(_path, "YouTubePathBox");
            AutomationProperties.SetName(_path, "올릴 파일");
            pathRow.Children.Add(_path);
            Add("파일", pathRow, null);
            Add("제목", _title, "YouTubeTitleBox");
            Add("설명", _description, "YouTubeDescriptionBox");
            _privacy.Items.Add(new ComboBoxItem { Content = "비공개 — 나와 지정한 사람만", Tag = "private" });
            _privacy.Items.Add(new ComboBoxItem { Content = "일부 공개 — 링크가 있는 사람", Tag = "unlisted" });
            _privacy.Items.Add(new ComboBoxItem { Content = "공개", Tag = "public" });
            _privacy.SelectedIndex = 0;
            Add("공개 범위", _privacy, "YouTubePrivacyBox");
            var kids = new StackPanel();
            var kidsChoices = new StackPanel { Orientation = Orientation.Horizontal };
            _kidsNo.Content = new TextBlock { Text = "아니요, 아동용이 아닙니다" };
            _kidsYes.Content = new TextBlock { Text = "예, 아동용입니다" };
            AutomationProperties.SetAutomationId(_kidsNo, "YouTubeKidsNo");
            AutomationProperties.SetAutomationId(_kidsYes, "YouTubeKidsYes");
            kidsChoices.Children.Add(_kidsNo);
            kidsChoices.Children.Add(_kidsYes);
            kids.Children.Add(kidsChoices);
            var kidsHint = UiKit.Label("유튜브가 꼭 묻는 값입니다. 아동용이면 댓글·맞춤 광고 등 일부 기능이 꺼집니다.", true);
            kidsHint.TextWrapping = TextWrapping.Wrap;
            kids.Children.Add(kidsHint);
            Add("아동용", kids, null);
            _main.Children.Add(grid);

            var notice = UiKit.Label("검증받지 않은 Google Cloud 프로젝트로 올린 동영상은 공개 범위와 상관없이 비공개로 묶입니다. "
                                     + "올리기 한 번에 그 프로젝트 할당량 약 1,600 단위(하루 기본 10,000)를 씁니다.", true);
            notice.TextWrapping = TextWrapping.Wrap;
            notice.Margin = new Thickness(0, 8, 0, 0);
            _main.Children.Add(notice);
            var accountRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var accountButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _signOut = UiKit.TextButton("로그아웃", "YouTubeSignOutButton", () => _ = SignOut());
            var change = UiKit.TextButton("클라이언트 바꾸기…", "YouTubeChangeClientButton", () => ShowSetup(true));
            accountButtons.Children.Add(_signOut);
            accountButtons.Children.Add(change);
            DockPanel.SetDock(accountButtons, Dock.Right);
            accountRow.Children.Add(accountButtons);
            _accountText.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(_accountText, "YouTubeAccountText");
            accountRow.Children.Add(_accountText);
            _main.Children.Add(accountRow);
            _main.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "YouTubeStatus");
            _main.Children.Add(_status);
            var linkButtons = new StackPanel { Orientation = Orientation.Horizontal };
            linkButtons.Children.Add(UiKit.TextButton("열기", "YouTubeOpenLinkButton", () => Report(YouTubeSettings.OpenInBrowser(LinkUri()))));
            linkButtons.Children.Add(UiKit.TextButton("복사", "YouTubeCopyLinkButton", CopyLink));
            DockPanel.SetDock(linkButtons, Dock.Right);
            _linkRow.Children.Add(linkButtons);
            AutomationProperties.SetAutomationId(_link, "YouTubeLinkBox");
            AutomationProperties.SetName(_link, "링크");
            _linkRow.Children.Add(_link);
            _main.Children.Add(_linkRow);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _upload = UiKit.TextButton("올리기", "YouTubeUploadButton", () => _ = Upload());
            _upload.IsDefault = true;
            _close = UiKit.TextButton("닫기", "YouTubeCloseButton", CloseOrCancel);
            _close.IsCancel = true;
            buttons.Children.Add(_upload);
            buttons.Children.Add(_close);
            _main.Children.Add(buttons);
            _inputs.AddRange(new UIElement[] { browse, _title, _description, _privacy, _kidsNo, _kidsYes, change });

            _kidsNo.Checked += (s, e) => UpdateButtons();
            _kidsYes.Checked += (s, e) => UpdateButtons();
            SetPath(path);
            UpdateAccount();
            ShowSetup(!_account.Client.IsComplete);
            Closing += (s, e) =>
            {
                if (_cancel == null)
                    return;
                e.Cancel = true;
                if (_cancel != null && UiKit.Ask(this, "유튜브에 올리기", "올리는 중입니다. 멈추고 닫을까요? 지금까지 보낸 것은 버려집니다.", "멈추기", "계속 올리기") == 0)
                {
                    // 멈춘 뒤(Busy가 끝나면) 닫는다
                    _closeWhenDone = true;
                    _cancel?.Cancel();
                }
            };
        }

        /// <summary>path를 올리는 창을 띄운다(내보내기가 끝난 뒤 등).</summary>
        public static void Show(EditorServices services, DependencyObject owner, string path)
        {
            var dialog = new YouTubeUploadDialog(services, path);
            var window = UiKit.OwnerOf(owner);
            if (window != null && window.IsLoaded)
                dialog.Owner = window;
            dialog.ShowDialog();
        }

        /// <summary>[파일 > 유튜브에 올리기…]: 이미 있는 동영상 파일을 골라 올린다. folder는 처음 보일 폴더(없으면 내 비디오).</summary>
        public static void PickAndShow(EditorServices services, DependencyObject owner, string folder)
        {
            var path = Pick(UiKit.OwnerOf(owner), folder);
            if (path != null)
                Show(services, owner, path);
        }

        private static string Pick(Window owner, string folder)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "유튜브에 올릴 동영상",
                Filter = "동영상 (*.mp4;*.mov;*.mkv;*.webm;*.avi)|*.mp4;*.mov;*.mkv;*.webm;*.avi|모든 파일 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };
            return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
        }

        private void ShowSetup(bool setup)
        {
            Content = setup ? _setup : _main;
            if (setup)
                _fields.Show(_account.Client.IsComplete ? null : "넣은 값은 Folderss 설정 > 동영상 편집에서도 바꿀 수 있습니다.");
        }

        private void SaveClient()
        {
            var client = _fields.Client;
            var error = client.Problem() ?? YouTubeSettings.Save(_services, client);
            if (error != null)
            {
                _fields.Show(error);
                return;
            }
            UpdateAccount();
            ShowSetup(false);
            _status.Text = "저장했습니다. [올리기]를 누르면 브라우저에서 구글 로그인부터 합니다.";
        }

        private void SetPath(string path)
        {
            _path.Text = path ?? "";
            _title.Text = string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
            if (_title.Text.Length > YouTubeVideo.MaxTitle)
                _title.Text = _title.Text.Substring(0, YouTubeVideo.MaxTitle);
            _uploaded = false;
            _linkRow.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }

        private void Browse()
        {
            var path = Pick(this, SafeFolder(_path.Text));
            if (path != null)
                SetPath(path);
        }

        private static string SafeFolder(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void UpdateAccount()
        {
            _accountText.Text = YouTubeSettings.SignedInText(_account);
            _signOut.Visibility = _account.SignedInAt != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateButtons()
        {
            // 아동용 여부를 고르기 전, 올리는 중, 다 올린 뒤(같은 파일을 두 번 올려 할당량을 쓰지 않게)에는 꺼 둔다
            _upload.IsEnabled = _cancel == null && !_uploaded && (_kidsNo.IsChecked == true || _kidsYes.IsChecked == true) && _path.Text.Length > 0;
            _signOut.IsEnabled = _cancel == null;
        }

        private YouTubeVideo Video()
        {
            return new YouTubeVideo
            {
                Title = _title.Text,
                Description = _description.Text,
                Privacy = (_privacy.SelectedItem as ComboBoxItem)?.Tag as string,
                MadeForKids = _kidsYes.IsChecked == true ? true : _kidsNo.IsChecked == true ? false : (bool?)null
            };
        }

        private async Task Upload()
        {
            var path = _path.Text;
            if (!File.Exists(path))
            {
                _status.Text = "올릴 파일이 없습니다: " + path;
                return;
            }
            var video = Video();
            var problem = video.Problem();
            if (problem != null)
            {
                _status.Text = problem;
                return;
            }
            await Busy(async token =>
            {
                try
                {
                    _status.Text = "로그인을 확인하는 중…";
                    await _account.AccessToken(false, token);
                }
                catch (YouTubeException ex) when (ex.NeedsSignIn)
                {
                    await SignIn(token);
                }
                var uploader = new YouTubeUploader(_account.Http, _account.AccessToken);
                var watch = Stopwatch.StartNew();
                var progress = new Progress<YouTubeUploader.Status>(s =>
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = s.Total > 0 ? 100.0 * s.Sent / s.Total : 0;
                    var speed = watch.Elapsed.TotalSeconds > 1 ? s.Sent / 1048576.0 / watch.Elapsed.TotalSeconds : 0;
                    _status.Text = s.Note ?? string.Format(CultureInfo.InvariantCulture, "올리는 중… {0:0}% ({1:0.0} / {2:0.0} MB{3})",
                        _progress.Value, s.Sent / 1048576.0, s.Total / 1048576.0, speed > 0 ? string.Format(CultureInfo.InvariantCulture, ", {0:0.0} MB/s", speed) : "");
                });
                _status.Text = "올리기를 시작하는 중…";
                var result = await uploader.Upload(path, video, progress, token);
                _uploaded = true;
                var message = "올렸습니다: " + video.Title.Trim() + (string.IsNullOrEmpty(result.ChannelTitle) ? "" : " (채널 " + result.ChannelTitle + ")")
                              + ". 유튜브가 처리를 마치기까지 몇 분 걸릴 수 있습니다.";
                if (result.Privacy != null && result.Privacy != video.Privacy)
                    message += "\n요청한 공개 범위와 달리 \"" + PrivacyName(result.Privacy) + "\"로 올라갔습니다. 검증받지 않은 Google Cloud 프로젝트로 올리면 비공개로 묶입니다(설정 방법 안내 참고).";
                _status.Text = message;
                ShowLink(result.Link);
            });
        }

        private async Task SignIn(CancellationToken token)
        {
            _status.Text = "브라우저에서 구글 계정으로 로그인하고 권한을 허락하세요. 마치면 이 창으로 돌아옵니다… (멈추려면 [취소])";
            await _account.SignIn(url => Dispatcher.Invoke(() =>
            {
                var error = YouTubeSettings.OpenInBrowser(url);
                if (error != null)
                {
                    _status.Text = error + " 아래 주소를 브라우저에 붙여 넣어 로그인하세요.";
                    ShowLink(url);
                }
            }), token);
            _linkRow.Visibility = Visibility.Collapsed;
            UpdateAccount();
            Activate();
        }

        private async Task SignOut()
        {
            if (_cancel != null)
                return;
            _signOut.IsEnabled = false;
            _status.Text = "로그아웃하는 중…";
            _status.Text = await YouTubeSettings.SignOut(_account);
            UpdateAccount();
            UpdateButtons();
        }

        private static string PrivacyName(string privacy)
        {
            switch (privacy)
            {
                case "private": return "비공개";
                case "unlisted": return "일부 공개";
                case "public": return "공개";
                default: return privacy;
            }
        }

        private void ShowLink(Uri url)
        {
            _link.Text = url.AbsoluteUri;
            _linkRow.Visibility = Visibility.Visible;
        }

        private Uri LinkUri()
        {
            return Uri.TryCreate(_link.Text, UriKind.Absolute, out var uri) ? uri : null;
        }

        private void CopyLink()
        {
            try
            {
                Clipboard.SetText(_link.Text);
                _status.Text = "링크를 복사했습니다.";
            }
            catch (Exception ex)
            {
                _status.Text = "복사하지 못했습니다: " + ex.Message;
            }
        }

        private void Report(string error)
        {
            if (error != null)
                _status.Text = error;
        }

        /// <summary>작업 하나를 돌린다: 입력을 끄고 [닫기]를 [취소]로, 끝나면 되돌린다. 오류는 상태 줄에.</summary>
        private async Task Busy(Func<CancellationToken, Task> work)
        {
            if (_cancel != null)
                return;
            _cancel = new CancellationTokenSource();
            foreach (var input in _inputs)
                input.IsEnabled = false;
            UpdateButtons();
            _linkRow.Visibility = Visibility.Collapsed;
            _close.Content = new TextBlock { Text = "취소" };
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = true;
            try
            {
                await work(_cancel.Token);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "멈췄습니다.";
            }
            catch (YouTubeException ex)
            {
                _status.Text = ex.Message;
            }
            catch (Exception ex)
            {
                _status.Text = "올리지 못했습니다: " + ex.Message;
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                foreach (var input in _inputs)
                    input.IsEnabled = true;
                _close.Content = new TextBlock { Text = "닫기" };
                _progress.Visibility = Visibility.Collapsed;
                UpdateAccount();
                UpdateButtons();
                if (_closeWhenDone)
                    _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }

        private void CloseOrCancel()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                return;
            }
            Close();
        }
    }
}
