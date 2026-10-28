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
    /// FFmpeg 창: 찾지 못했을 때(또는 FFmpeg 단추) [내려받아 설치](GitHub BtbN 빌드, SHA-256 확인) · [직접 지정…](ffmpeg.exe 고르기) · [나중에].
    /// 설치·지정에 성공하면 <see cref="EditorServices.SetTool"/>로 바로 쓴다.
    /// FFmpeg가 있으면 지금 것과 미리 보기 도우미 상태를 보이고, 공유 DLL이 없는 FFmpeg 9면(winget Gyan full_build 등) 도우미용 DLL만 받을지 묻는다
    /// ([미리 보기용 DLL 받기] — 설치된 FFmpeg는 그대로).
    /// </summary>
    internal sealed class FfmpegSetupDialog : Window
    {
        private readonly EditorServices _services;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly TextBlock _helperStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel _offer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        private readonly Button _download;
        private readonly Button _browse;
        private readonly Button _later;
        private readonly Button _libraries;
        private readonly string _laterText;
        private readonly EventHandler _helperChanged;
        private CancellationTokenSource _cancel;

        public FfmpegSetupDialog(EditorServices services)
        {
            _services = services;
            var tool = services.Tool;
            Title = tool == null ? "FFmpeg 설치" : "FFmpeg";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = tool == null ? "FFmpeg가 필요합니다" : "FFmpeg", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            const string howTo = "[내려받아 설치]는 GitHub의 BtbN/FFmpeg-Builds 최신 Windows 빌드(약 80MB, GPL)를 받아 체크섬을 확인한 뒤 "
                                 + "플러그인 데이터 폴더에 둡니다. 이미 받아 둔 ffmpeg.exe가 있으면 [직접 지정…]을 누르세요(ffprobe.exe가 같은 폴더에 있어야 합니다).";
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = tool == null
                    ? "동영상 편집은 영상 읽기·미리 보기·내보내기에 FFmpeg를 씁니다. 이 PC에서 찾지 못했습니다"
                      + "(설정한 경로, PATH, winget·scoop·choco 설치 위치).\n\n" + howTo
                    : "지금 쓰는 FFmpeg: " + tool.VersionLine + "\n" + tool.Folder + "\n\n다른 FFmpeg로 바꾸려면: " + howTo
            });
            var folder = UiKit.Label("설치 위치: " + services.DownloadFolder, true);
            folder.Margin = new Thickness(0, 8, 0, 0);
            folder.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(folder);
            if (tool != null)
            {
                // 미리 보기 도우미: 상태와, 공유 DLL이 없는 FFmpeg 9면 도우미용 DLL 받기
                panel.Children.Add(new TextBlock { Text = "미리 보기 도우미", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
                AutomationProperties.SetAutomationId(_helperStatus, "FfmpegHelperStatus");
                _helperStatus.Text = services.HelperStatus;
                panel.Children.Add(_helperStatus);
                _offer.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "이 FFmpeg에는 공유 DLL이 없어, 재생 헤드를 옮기거나 재생할 때마다 ffmpeg.exe를 새로 띄웁니다(PC에 따라 1~2초씩 늦음). "
                           + "[미리 보기용 DLL 받기]는 GitHub BtbN/FFmpeg-Builds의 FFmpeg " + PreviewHelper.FfmpegMajor + " 공유 빌드(약 85MB, GPL)를 받아 체크섬을 확인한 뒤, "
                           + "도우미가 쓸 DLL만 플러그인 데이터 폴더에 둡니다. 가져오기·내보내기는 지금 FFmpeg를 그대로 씁니다."
                });
                var target = UiKit.Label("둘 곳: " + services.PreviewLibrariesFolder, true);
                target.TextWrapping = TextWrapping.Wrap;
                target.Margin = new Thickness(0, 4, 0, 0);
                _offer.Children.Add(target);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                _libraries = UiKit.TextButton("미리 보기용 DLL 받기", "DownloadPreviewLibrariesButton", () => _ = DownloadLibraries());
                row.Children.Add(_libraries);
                var quiet = new CheckBox
                {
                    Content = "편집기를 열 때 알리지 않기",
                    IsChecked = services.Setting(EditorServices.PreviewLibrariesNoticeKey) == "off",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                quiet.SetResourceReference(ForegroundProperty, "PrimaryText");
                AutomationProperties.SetAutomationId(quiet, "PreviewLibrariesQuietCheck");
                quiet.Checked += (s, e) => services.SaveSetting(EditorServices.PreviewLibrariesNoticeKey, "off");
                quiet.Unchecked += (s, e) => services.SaveSetting(EditorServices.PreviewLibrariesNoticeKey, null);
                row.Children.Add(quiet);
                _offer.Children.Add(row);
                _offer.Visibility = services.CanDownloadPreviewLibraries(tool) ? Visibility.Visible : Visibility.Collapsed;
                panel.Children.Add(_offer);
                _helperChanged = (s, e) => Dispatcher.BeginInvoke(new Action(() => _helperStatus.Text = _services.HelperStatus));
                services.HelperChanged += _helperChanged;
                Closed += (s, e) => services.HelperChanged -= _helperChanged;
            }
            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "FfmpegSetupStatus");
            panel.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _download = UiKit.TextButton("내려받아 설치", "DownloadFfmpegButton", () => _ = Download());
            _download.IsDefault = tool == null;
            _browse = UiKit.TextButton("직접 지정…", "BrowseFfmpegButton", () => _ = Browse());
            _laterText = tool == null ? "나중에" : "닫기";
            _later = UiKit.TextButton(_laterText, "FfmpegLaterButton", Later);
            _later.IsCancel = true;
            buttons.Children.Add(_download);
            buttons.Children.Add(_browse);
            buttons.Children.Add(_later);
            panel.Children.Add(buttons);
            Content = panel;
            Closing += (s, e) => _cancel?.Cancel();
        }

        /// <summary>창을 띄워 FFmpeg를 마련한다. 쓸 수 있게 되면 true.</summary>
        public static bool Show(EditorServices services, DependencyObject owner)
        {
            var dialog = new FfmpegSetupDialog(services);
            var window = UiKit.OwnerOf(owner);
            if (window != null && window.IsLoaded)
                dialog.Owner = window;
            dialog.ShowDialog();
            return services.Tool != null;
        }

        /// <summary>내려받기 시작: 단추를 막고 [나중에]를 [취소]로, 진행 막대를 보인다. 진행을 받을 것을 돌려준다.</summary>
        private IProgress<FfmpegDownloader.Status> Begin()
        {
            _download.IsEnabled = _browse.IsEnabled = false;
            if (_libraries != null)
                _libraries.IsEnabled = false;
            _later.Content = new TextBlock { Text = "취소" };
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = true;
            _cancel = new CancellationTokenSource();
            return new Progress<FfmpegDownloader.Status>(s =>
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
            });
        }

        /// <summary>[미리 보기용 DLL 받기]: 도우미용 공유 DLL만 받고 도우미를 띄운다(설치된 FFmpeg는 그대로). 창은 열어 두어 도우미 상태가 바뀌는 것을 보인다.</summary>
        private async Task DownloadLibraries()
        {
            var progress = Begin();
            try
            {
                var token = _cancel.Token;
                var bin = await Task.Run(() => _services.DownloadPreviewLibraries(progress, token), token);
                _status.Text = "미리 보기용 DLL을 받았습니다: " + bin;
                _offer.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "취소했습니다.";
            }
            catch (Exception ex)
            {
                _status.Text = "내려받지 못했습니다: " + ex.Message;
            }
            Reset();
        }

        private async Task Download()
        {
            var progress = Begin();
            try
            {
                var token = _cancel.Token;
                var bin = await Task.Run(() => FfmpegDownloader.Download(_services.DownloadFolder, progress, token), token);
                var tool = await Task.Run(() => FfmpegTool.TryOpen(bin));
                if (tool == null)
                    throw new InvalidOperationException("내려받은 FFmpeg를 실행하지 못했습니다: " + bin);
                _services.SetTool(tool, true);
                _status.Text = "설치했습니다: " + tool.VersionLine;
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

        private void Reset()
        {
            _progress.Visibility = Visibility.Collapsed;
            _download.IsEnabled = _browse.IsEnabled = true;
            if (_libraries != null)
                _libraries.IsEnabled = true;
            _later.Content = new TextBlock { Text = _laterText };
            _cancel = null;
        }

        private async Task Browse()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "ffmpeg.exe 고르기",
                Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일 (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
                return;
            // ffmpeg -version을 돌려 보므로 UI 스레드를 막지 않게(백신 검사 등으로 몇 초 걸릴 수 있음)
            _download.IsEnabled = _browse.IsEnabled = false;
            _status.Text = "확인하는 중…";
            FfmpegTool tool;
            try
            {
                var path = dialog.FileName;
                tool = await Task.Run(() => FfmpegTool.TryOpen(path));
            }
            finally
            {
                _download.IsEnabled = _browse.IsEnabled = true;
            }
            if (tool == null)
            {
                _status.Text = "이 파일은 쓸 수 없습니다. ffmpeg.exe와 ffprobe.exe가 같은 폴더에 있고 FFmpeg 5 이상인지 확인하세요.";
                return;
            }
            _services.SetTool(tool, true);
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
