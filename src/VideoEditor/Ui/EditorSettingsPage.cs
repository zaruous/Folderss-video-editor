using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Folderss.Plugins;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>Folderss 설정 창의 "동영상 편집" 탭: FFmpeg 경로(비우면 자동으로 찾음), 사진·글자 기본 길이, yt-dlp 경로, 다운로드 폴더.</summary>
    internal sealed class EditorSettingsPage : IPluginSettingsPage
    {
        private readonly EditorServices _services;
        private TextBox _path;
        private TextBox _imageSeconds;
        private TextBox _ytDlp;
        private TextBox _downloadFolder;

        public EditorSettingsPage(EditorServices services)
        {
            _services = services;
        }

        public string Title { get { return "동영상 편집"; } }

        public FrameworkElement CreateView()
        {
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(UiKit.Label("FFmpeg 폴더 (ffmpeg.exe와 ffprobe.exe가 있는 곳) — 비우면 PATH·설치 위치에서 자동으로 찾습니다"));
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var browse = UiKit.TextButton("찾아보기…", "SettingsBrowseFfmpeg", () => { });
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            _path = new TextBox { Text = _services.Setting(EditorServices.FfmpegPathKey) ?? "" };
            AutomationProperties.SetAutomationId(_path, "SettingsFfmpegPath");
            row.Children.Add(_path);
            panel.Children.Add(row);
            var status = UiKit.Label(_services.Tool != null ? "지금 쓰는 FFmpeg: " + _services.Tool.VersionLine + " (" + _services.Tool.Folder + ")" : "FFmpeg를 아직 찾지 못했습니다.", true);
            status.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(status);
            browse.Click += (s, e) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "ffmpeg.exe 고르기", Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일 (*.exe)|*.exe" };
                if (dialog.ShowDialog(Window.GetWindow(panel)) == true)
                    _path.Text = System.IO.Path.GetDirectoryName(dialog.FileName);
            };
            var check = UiKit.TextButton("확인해 보기", "SettingsCheckFfmpeg", () => { });
            check.HorizontalAlignment = HorizontalAlignment.Left;
            check.Margin = new Thickness(0, 6, 0, 12);
            check.Click += async (s, e) =>
            {
                var path = _path.Text.Trim();
                status.Text = "확인하는 중…";
                var tool = await Task.Run(() => string.IsNullOrEmpty(path) ? FfmpegTool.Locate(null, System.IO.Path.Combine(_services.DownloadFolder, "bin")) : FfmpegTool.TryOpen(path));
                status.Text = tool == null ? "쓸 수 있는 FFmpeg가 아닙니다(ffmpeg.exe와 ffprobe.exe가 함께 있어야 함)." : "쓸 수 있습니다: " + tool.VersionLine + " (" + tool.Folder + ")";
            };
            panel.Children.Add(check);
            panel.Children.Add(UiKit.Label("사진·이미지·글자 클립의 기본 길이(초)"));
            _imageSeconds = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Text = UiKit.Number(_services.ImageSeconds), Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(_imageSeconds, "SettingsImageSeconds");
            panel.Children.Add(_imageSeconds);
            var ytDlpLabel = UiKit.Label("yt-dlp 경로 (URL·유튜브에서 가져오기) — 비우면 PATH·winget 설치 위치·플러그인이 받은 것을 찾습니다");
            ytDlpLabel.Margin = new Thickness(0, 12, 0, 0);
            ytDlpLabel.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(ytDlpLabel);
            _ytDlp = new TextBox { Text = _services.Setting(EditorServices.YtDlpPathKey) ?? "", Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(_ytDlp, "SettingsYtDlpPath");
            panel.Children.Add(_ytDlp);
            var ytDlpStatus = UiKit.Label(_services.YtDlp != null ? "지금 쓰는 yt-dlp: " + _services.YtDlp.Version + " (" + _services.YtDlp.Path + ")" : "yt-dlp는 처음 URL에서 가져올 때 찾습니다.", true);
            ytDlpStatus.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(ytDlpStatus);

            var downloadLabel = UiKit.Label("다운로드 폴더 (URL·유튜브에서 받은 파일) — 비우면 프로젝트 파일 옆 media 폴더(저장 안 한 프로젝트면 플러그인 데이터 폴더\\media)");
            downloadLabel.Margin = new Thickness(0, 12, 0, 0);
            downloadLabel.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(downloadLabel);
            var downloadRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var downloadButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var downloadStatus = UiKit.Label("", true);
            downloadStatus.TextWrapping = TextWrapping.Wrap;
            var pick = UiKit.TextButton("찾아보기…", "SettingsBrowseDownloadFolder", () => { });
            var open = UiKit.TextButton("폴더 열기", "SettingsOpenDownloadFolder", () => { });
            downloadButtons.Children.Add(pick);
            downloadButtons.Children.Add(open);
            DockPanel.SetDock(downloadButtons, Dock.Right);
            downloadRow.Children.Add(downloadButtons);
            _downloadFolder = new TextBox { Text = _services.Setting(EditorServices.DownloadFolderKey) ?? "" };
            AutomationProperties.SetAutomationId(_downloadFolder, "SettingsDownloadFolder");
            downloadRow.Children.Add(_downloadFolder);
            panel.Children.Add(downloadRow);
            panel.Children.Add(downloadStatus);
            pick.Click += (s, e) =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "다운로드 폴더 고르기" };
                if (System.IO.Directory.Exists(_downloadFolder.Text.Trim()))
                    dialog.InitialDirectory = _downloadFolder.Text.Trim();
                if (dialog.ShowDialog(Window.GetWindow(panel)) == true)
                    _downloadFolder.Text = dialog.FolderName;
            };
            // 비어 있으면 저장 안 한 프로젝트 기준 기본 폴더(플러그인 데이터 폴더\media)를 연다
            open.Click += (s, e) => downloadStatus.Text = UiKit.OpenFolder(DownloadFolderToOpen()) ?? "";
            return panel;
        }

        private string DownloadFolderToOpen()
        {
            var text = _downloadFolder.Text.Trim();
            return text.Length > 0 && System.IO.Path.IsPathFullyQualified(text) ? text : _services.DefaultMediaDownloadFolder(null);
        }

        public void Save()
        {
            if (_path == null)
                return;
            _services.Manager.SetSetting(EditorServices.FfmpegPathKey, _path.Text.Trim().Length == 0 ? null : _path.Text.Trim());
            double seconds;
            if (UiKit.TryNumber(_imageSeconds.Text, out seconds) && seconds > 0 && seconds <= 3600)
                _services.Manager.SetSetting(EditorServices.ImageSecondsKey, UiKit.Number(seconds));
            var ytDlp = _ytDlp.Text.Trim();
            if (ytDlp != (_services.Setting(EditorServices.YtDlpPathKey) ?? ""))
            {
                _services.Manager.SetSetting(EditorServices.YtDlpPathKey, ytDlp.Length == 0 ? null : ytDlp);
                // 다음에 가져올 때 새 경로로 다시 찾는다
                _services.SetYtDlp(null, false);
            }
            // 다운로드 폴더는 절대 경로만 저장한다(상대 경로·빈 값은 기본값)
            var download = _downloadFolder.Text.Trim();
            _services.Manager.SetSetting(EditorServices.DownloadFolderKey, download.Length > 0 && System.IO.Path.IsPathFullyQualified(download) ? download : null);
            // 다음 편집 창부터 새 경로로 다시 찾는다
            _ = _services.LocateAsync();
        }
    }
}
