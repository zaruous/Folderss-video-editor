using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Folderss.Plugins;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>Folderss 설정 창의 "동영상 편집" 탭: FFmpeg 경로(비우면 자동으로 찾음), 사진·글자 기본 길이.</summary>
    internal sealed class EditorSettingsPage : IPluginSettingsPage
    {
        private readonly EditorServices _services;
        private TextBox _path;
        private TextBox _imageSeconds;

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
            return panel;
        }

        public void Save()
        {
            if (_path == null)
                return;
            _services.Manager.SetSetting(EditorServices.FfmpegPathKey, _path.Text.Trim().Length == 0 ? null : _path.Text.Trim());
            double seconds;
            if (UiKit.TryNumber(_imageSeconds.Text, out seconds) && seconds > 0 && seconds <= 3600)
                _services.Manager.SetSetting(EditorServices.ImageSecondsKey, UiKit.Number(seconds));
            // 다음 편집 창부터 새 경로로 다시 찾는다
            _ = _services.LocateAsync();
        }
    }
}
