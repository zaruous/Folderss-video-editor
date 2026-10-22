using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 유튜브 가져오기·검색 전에 뜨는 창. 유튜브 약관·권리 안내와 체크박스를 두고, 체크해야 버튼이 켜진다.
    /// yt-dlp가 없으면 [내려받아 설치](GitHub yt-dlp/yt-dlp, SHA-256 확인) · [직접 지정…] · [나중에],
    /// 있으면 안내만 보이고 [확인] · [취소]. JS 실행기(Deno·Node)가 없으면 Deno(GitHub denoland/deno)도 함께 받을지 묻는다.
    /// 확인하면 안내를 확인한 것을 설정에 기억한다.
    /// </summary>
    internal sealed class YtDlpSetupDialog : Window
    {
        public const string Notice = "유튜브 약관은 유튜브가 허락한 방식(다운로드 버튼, Premium 오프라인 저장) 밖의 내려받기를 금지합니다. "
                                     + "직접 올린 영상이거나 받을 권리가 있는 영상만 받으세요. 검색도 유튜브에 자동으로 접근하는 것이라 같은 안내를 적용합니다.";

        private readonly EditorServices _services;
        private readonly bool _missing;
        private readonly CheckBox _agree = new CheckBox { Margin = new Thickness(0, 10, 0, 0) };
        private readonly CheckBox _deno = new CheckBox { Margin = new Thickness(0, 10, 0, 0), IsChecked = true };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button _ok;
        private readonly Button _browse;
        private readonly Button _later;
        private CancellationTokenSource _cancel;

        private YtDlpSetupDialog(EditorServices services)
        {
            _services = services;
            _missing = services.YtDlp == null;
            var needRuntime = !_missing ? services.YtDlp.JsRuntime == null : YtDlpTool.FindJsRuntime(services.DenoFolder) == null;
            Title = _missing ? "yt-dlp 설치" : "유튜브에서 가져오기";
            Width = 540;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = _missing ? "yt-dlp가 필요합니다" : "받기 전에 확인하세요", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            if (_missing)
            {
                panel.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "유튜브 등 동영상 사이트에서 검색하고 받으려면 yt-dlp가 필요합니다. 이 PC에서 찾지 못했습니다(설정한 경로, PATH, winget·scoop 설치 위치).\n\n"
                           + "[내려받아 설치]는 GitHub yt-dlp/yt-dlp 최신 릴리스의 yt-dlp.exe(약 18MB)를 받아 같은 릴리스의 SHA2-256SUMS와 맞는지 확인한 뒤 "
                           + "플러그인 데이터 폴더에 둡니다. 직접 설치해도 됩니다: winget install yt-dlp.yt-dlp"
                });
                var folder = UiKit.Label("설치 위치: " + services.YtDlpFolder, true);
                folder.Margin = new Thickness(0, 8, 0, 0);
                folder.TextWrapping = TextWrapping.Wrap;
                panel.Children.Add(folder);
            }
            var notice = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0) };
            notice.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            notice.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            notice.Child = new TextBlock { Text = Notice, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(notice);
            _agree.Content = new TextBlock { Text = "위 내용을 확인했으며, 권리가 있는 영상만 받겠습니다.", TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(_agree, "YtDlpNoticeCheck");
            panel.Children.Add(_agree);
            if (needRuntime)
            {
                _deno.Content = new TextBlock
                {
                    Text = "JS 실행기 Deno도 함께 받기(GitHub denoland/deno, 약 43MB) — yt-dlp는 유튜브에 Deno 같은 JS 실행기를 권장합니다. 이 PC에서 Deno·Node를 찾지 못했습니다.",
                    TextWrapping = TextWrapping.Wrap
                };
                AutomationProperties.SetAutomationId(_deno, "YtDlpDenoCheck");
                panel.Children.Add(_deno);
            }
            else
            {
                _deno.IsChecked = false;
            }
            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "YtDlpSetupStatus");
            panel.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _ok = UiKit.TextButton(_missing ? "내려받아 설치" : "확인", "YtDlpOkButton", () => _ = Confirm());
            _ok.IsDefault = true;
            _browse = UiKit.TextButton("직접 지정…", "BrowseYtDlpButton", () => _ = Browse());
            _later = UiKit.TextButton(_missing ? "나중에" : "취소", "YtDlpLaterButton", Later);
            _later.IsCancel = true;
            buttons.Children.Add(_ok);
            if (_missing)
                buttons.Children.Add(_browse);
            buttons.Children.Add(_later);
            panel.Children.Add(buttons);
            Content = panel;
            // 체크하기 전에는 [내려받아 설치]·[직접 지정…]·[확인]이 꺼져 있다
            _ok.IsEnabled = _browse.IsEnabled = false;
            _agree.Checked += (s, e) => _ok.IsEnabled = _browse.IsEnabled = true;
            _agree.Unchecked += (s, e) => _ok.IsEnabled = _browse.IsEnabled = false;
            Closing += (s, e) => _cancel?.Cancel();
        }

        /// <summary>yt-dlp가 없거나 안내를 아직 확인하지 않았으면 창을 띄운다. 쓸 수 있고 안내를 확인했으면 true.</summary>
        public static bool Ensure(EditorServices services, DependencyObject owner)
        {
            if (services.YtDlp != null && services.YtDlpNoticeAccepted)
                return true;
            var dialog = new YtDlpSetupDialog(services);
            var window = UiKit.OwnerOf(owner);
            if (window != null && window.IsLoaded)
                dialog.Owner = window;
            dialog.ShowDialog();
            return services.YtDlp != null && services.YtDlpNoticeAccepted;
        }

        private async Task Confirm()
        {
            _ok.IsEnabled = _browse.IsEnabled = _agree.IsEnabled = _deno.IsEnabled = false;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            var progress = new Progress<FfmpegDownloader.Status>(Show);
            try
            {
                if (_missing || _deno.IsChecked == true)
                {
                    _later.Content = new TextBlock { Text = "취소" };
                    _progress.Visibility = Visibility.Visible;
                    _progress.IsIndeterminate = true;
                }
                if (_missing)
                    await Task.Run(() => YtDlpDownloader.DownloadYtDlp(_services.YtDlpFolder, progress, token), token);
                if (_deno.IsChecked == true)
                    await Task.Run(() => YtDlpDownloader.DownloadDeno(_services.DenoFolder, progress, token), token);
                if (_missing || _deno.IsChecked == true)
                {
                    var tool = await _services.RelocateYtDlpAsync();
                    if (tool == null)
                        throw new InvalidOperationException("내려받은 yt-dlp를 실행하지 못했습니다: " + _services.YtDlpFolder);
                }
                _services.YtDlpNoticeAccepted = true;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "취소했습니다.";
                Reset();
            }
            catch (Exception ex)
            {
                _status.Text = "내려받지 못했습니다: " + ex.Message;
                Reset();
            }
        }

        private void Show(FfmpegDownloader.Status s)
        {
            if (s.Total > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = 100.0 * s.Done / s.Total;
                _status.Text = s.Stage + string.Format(" ({0:0.0} / {1:0.0} MB)", s.Done / 1048576.0, s.Total / 1048576.0);
            }
            else
            {
                _progress.IsIndeterminate = true;
                _status.Text = s.Stage;
            }
        }

        private void Reset()
        {
            _progress.Visibility = Visibility.Collapsed;
            _agree.IsEnabled = _deno.IsEnabled = true;
            _ok.IsEnabled = _browse.IsEnabled = _agree.IsChecked == true;
            _later.Content = new TextBlock { Text = _missing ? "나중에" : "취소" };
            _cancel = null;
        }

        private async Task Browse()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "yt-dlp.exe 고르기",
                Filter = "yt-dlp.exe|yt-dlp*.exe|실행 파일 (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
                return;
            _ok.IsEnabled = _browse.IsEnabled = false;
            _status.Text = "확인하는 중…";
            YtDlpTool tool;
            try
            {
                var path = dialog.FileName;
                var downloaded = _services.YtDlpFolder;
                var deno = _services.DenoFolder;
                tool = await Task.Run(() => YtDlpTool.TryOpen(path, downloaded, deno));
            }
            finally
            {
                _ok.IsEnabled = _browse.IsEnabled = _agree.IsChecked == true;
            }
            if (tool == null)
            {
                _status.Text = "이 파일은 쓸 수 없습니다(yt-dlp --version이 실패함).";
                return;
            }
            _services.SetYtDlp(tool, true);
            _services.YtDlpNoticeAccepted = true;
            DialogResult = true;
        }

        private void Later()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                return;
            }
            DialogResult = false;
        }
    }
}
