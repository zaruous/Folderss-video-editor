using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 내보내기 창(Ctrl+E): 파일, 해상도, 프레임 레이트, 품질, 인코더, 범위(전체 / In~Out). [내보내기] 중에는 진행률과 [취소].
    /// 프로젝트는 연 순간의 사본으로 내보낸다(내보내는 동안 편집해도 결과가 섞이지 않음).
    /// </summary>
    internal sealed class ExportDialog : Window
    {
        private readonly EditorServices _services;
        private readonly Project _project;
        private readonly TextBox _path = new TextBox { MinWidth = 300, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _size = new ComboBox { MinWidth = 180 };
        private readonly ComboBox _rate = new ComboBox { MinWidth = 180 };
        private readonly ComboBox _quality = new ComboBox { MinWidth = 180 };
        private readonly ComboBox _encoder = new ComboBox { MinWidth = 180 };
        private readonly ComboBox _range = new ComboBox { MinWidth = 180 };
        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 10, 0, 0), Maximum = 100, Visibility = Visibility.Collapsed };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Button _start;
        private readonly Button _close;
        private readonly Button _openFolder;
        private CancellationTokenSource _cancel;

        public ExportDialog(EditorServices services, Project project, string suggestedPath)
        {
            _services = services;
            _project = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            Title = "내보내기";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var grid = new Grid { Margin = new Thickness(18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            void Add(string label, FrameworkElement field, string automationId)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var text = UiKit.Label(label);
                text.Margin = new Thickness(0, 4, 12, 4);
                Grid.SetRow(text, row);
                grid.Children.Add(text);
                field.Margin = new Thickness(0, 4, 0, 4);
                AutomationProperties.SetAutomationId(field, automationId);
                AutomationProperties.SetName(field, label);
                Grid.SetRow(field, row);
                Grid.SetColumn(field, 1);
                grid.Children.Add(field);
                row++;
            }

            var pathRow = new DockPanel();
            var browse = UiKit.TextButton("찾아보기…", "ExportBrowseButton", Browse);
            DockPanel.SetDock(browse, Dock.Right);
            pathRow.Children.Add(browse);
            pathRow.Children.Add(_path);
            AutomationProperties.SetAutomationId(_path, "ExportPathBox");
            _path.Text = suggestedPath;
            Add("파일", pathRow, "ExportPathRow");

            foreach (var size in Sizes(_project))
                _size.Items.Add(size);
            _size.SelectedIndex = 0;
            Add("해상도", _size, "ExportSizeBox");

            _rate.Items.Add(new Choice("프로젝트 (" + _project.Rate + " fps)", null));
            foreach (var fps in new[] { "24", "25", "30", "50", "60" })
                _rate.Items.Add(new Choice(fps + " fps", fps));
            _rate.SelectedIndex = 0;
            Add("프레임 레이트", _rate, "ExportRateBox");

            _quality.Items.Add(new Choice("높음 (파일이 큼)", "0"));
            _quality.Items.Add(new Choice("보통", "1"));
            _quality.Items.Add(new Choice("낮음 (파일이 작음)", "2"));
            _quality.SelectedIndex = 1;
            Add("품질", _quality, "ExportQualityBox");

            var tool = services.Tool;
            _encoder.Items.Add(new Choice("자동 (" + (tool?.AutoVideoEncoder() ?? "?") + ")", "auto"));
            foreach (var name in new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "mpeg4" })
            {
                if (tool != null && tool.HasEncoder(name))
                    _encoder.Items.Add(new Choice(EncoderLabel(name), name));
            }
            _encoder.SelectedIndex = 0;
            Add("인코더", _encoder, "ExportEncoderBox");

            _range.Items.Add(new Choice("전체 (" + _project.Rate.Timecode(_project.Duration) + ")", "all"));
            if (_project.MarkIn.HasValue || _project.MarkOut.HasValue)
            {
                var from = _project.MarkIn ?? 0;
                var to = _project.MarkOut ?? _project.Duration;
                _range.Items.Add(new Choice("In~Out (" + _project.Rate.Timecode(from) + " – " + _project.Rate.Timecode(to) + ")", "inout"));
                _range.SelectedIndex = 1;
            }
            else
            {
                _range.SelectedIndex = 0;
            }
            Add("범위", _range, "ExportRangeBox");

            var panel = new StackPanel();
            panel.Children.Add(grid);
            var below = new StackPanel { Margin = new Thickness(18, 0, 18, 18) };
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            below.Children.Add(_summary);
            below.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "ExportStatus");
            below.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _openFolder = UiKit.TextButton("폴더 열기", "ExportOpenFolderButton", OpenFolder);
            _openFolder.Visibility = Visibility.Collapsed;
            _start = UiKit.TextButton("내보내기", "ExportStartButton", () => _ = Start());
            _start.IsDefault = true;
            _close = UiKit.TextButton("닫기", "ExportCloseButton", CloseOrCancel);
            _close.IsCancel = true;
            buttons.Children.Add(_openFolder);
            buttons.Children.Add(_start);
            buttons.Children.Add(_close);
            below.Children.Add(buttons);
            panel.Children.Add(below);
            Content = panel;
            foreach (var combo in new[] { _size, _rate, _quality, _range })
                combo.SelectionChanged += (s, e) => UpdateSummary();
            UpdateSummary();
            Closing += (s, e) =>
            {
                if (_cancel != null)
                {
                    e.Cancel = true;
                    _cancel.Cancel();
                }
            };
        }

        /// <summary>내보내기 결과 파일(성공했을 때).</summary>
        public string ExportedPath { get; private set; }

        private sealed class Choice
        {
            public Choice(string label, string value)
            {
                Label = label;
                Value = value;
            }

            public string Label { get; }
            public string Value { get; }
            public override string ToString() { return Label; }
        }

        private sealed class SizeChoice
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public string Label { get; set; }
            public override string ToString() { return Label; }
        }

        /// <summary>프로젝트 비율을 지킨 해상도 목록(프로젝트 → 2160p … 480p, 프로젝트보다 큰 것은 뺌 — 키우면 흐려질 뿐).</summary>
        private static IEnumerable<SizeChoice> Sizes(Project project)
        {
            yield return new SizeChoice { Width = project.Width, Height = project.Height, Label = "프로젝트 (" + project.Width + "×" + project.Height + ")" };
            foreach (var height in new[] { 2160, 1440, 1080, 720, 480, 360 })
            {
                if (height >= project.Height)
                    continue;
                var width = (int)Math.Round(project.Width * (double)height / project.Height / 2) * 2;
                yield return new SizeChoice { Width = width, Height = height, Label = height + "p (" + width + "×" + height + ")" };
            }
        }

        private static string EncoderLabel(string name)
        {
            switch (name)
            {
                case "libx264": return "H.264 (x264, 소프트웨어)";
                case "h264_nvenc": return "H.264 NVIDIA (nvenc)";
                case "h264_qsv": return "H.264 Intel (Quick Sync)";
                case "h264_amf": return "H.264 AMD (AMF)";
                case "h264_mf": return "H.264 Windows (Media Foundation)";
                default: return "MPEG-4 Part 2 (호환용)";
            }
        }

        private (long From, long To) Range()
        {
            var choice = (Choice)_range.SelectedItem;
            if (choice != null && choice.Value == "inout")
                return (_project.MarkIn ?? 0, _project.MarkOut ?? _project.Duration);
            return (0, _project.Duration);
        }

        private void UpdateSummary()
        {
            var range = Range();
            var size = (SizeChoice)_size.SelectedItem;
            _summary.Text = "MP4 (H.264 + AAC) · " + size.Width + "×" + size.Height + " · 길이 " + _project.Rate.Timecode(range.To - range.From)
                            + (range.To <= range.From ? " — 내보낼 것이 없습니다(타임라인에 클립을 놓으세요)" : "");
            _start.IsEnabled = range.To > range.From && _cancel == null;
        }

        private void Browse()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "내보낼 파일",
                Filter = "MP4 동영상 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                FileName = Path.GetFileName(_path.Text),
                InitialDirectory = SafeFolder(_path.Text)
            };
            if (dialog.ShowDialog(this) == true)
                _path.Text = dialog.FileName;
        }

        private static string SafeFolder(string path)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                return Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }
            catch (Exception)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }
        }

        private async Task Start()
        {
            var tool = _services.Tool;
            if (tool == null && !FfmpegSetupDialog.Show(_services, this))
                return;
            tool = _services.Tool;
            var path = (_path.Text ?? "").Trim();
            if (path.Length == 0 || !Path.IsPathRooted(path))
            {
                _status.Text = "내보낼 파일 경로를 정하세요([찾아보기…]).";
                return;
            }
            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                path += ".mp4";
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                _status.Text = "파일 경로가 올바르지 않습니다.";
                return;
            }
            if (_project.AllClips.Select(c => c.Path).Concat(_project.Media.Select(m => m.Path)).Any(source => SamePath(source, path)))
            {
                _status.Text = "편집에 쓰는 원본 파일과 같은 이름입니다. 다른 이름을 고르세요.";
                return;
            }
            if (File.Exists(path) && UiKit.Ask(this, "내보내기", Path.GetFileName(path) + " 파일이 이미 있습니다. 덮어쓸까요?", "덮어쓰기", "취소") != 0)
                return;
            var range = Range();
            var size = (SizeChoice)_size.SelectedItem;
            var settings = new ExportSettings
            {
                OutputPath = path,
                Width = size.Width,
                Height = size.Height,
                FrameRate = ((Choice)_rate.SelectedItem).Value,
                Quality = int.Parse(((Choice)_quality.SelectedItem).Value, CultureInfo.InvariantCulture),
                Encoder = ((Choice)_encoder.SelectedItem).Value,
                From = range.From,
                To = range.To
            };
            _cancel = new CancellationTokenSource();
            _start.IsEnabled = false;
            _openFolder.Visibility = Visibility.Collapsed;
            _close.Content = new TextBlock { Text = "취소" };
            _progress.Visibility = Visibility.Visible;
            _progress.Value = 0;
            _status.Text = "내보내는 중… (" + tool.AutoVideoEncoder() + ")";
            var watch = Stopwatch.StartNew();
            var progress = new Progress<double>(p =>
            {
                _progress.Value = p * 100;
                _status.Text = string.Format("내보내는 중… {0:0}% ({1:0}초)", p * 100, watch.Elapsed.TotalSeconds);
            });
            try
            {
                var token = _cancel.Token;
                await Task.Run(() => Exporter.Export(tool, _project, settings, progress, token, _services.TextRasterizer), token);
                ExportedPath = path;
                _status.Text = "내보냈습니다: " + path + string.Format(" ({0:0.0}MB, {1:0.0}초)", new FileInfo(path).Length / 1048576.0, watch.Elapsed.TotalSeconds);
                _openFolder.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "내보내기를 취소했습니다.";
            }
            catch (Exception ex)
            {
                _status.Text = "내보내지 못했습니다: " + ex.Message;
            }
            finally
            {
                _cancel = null;
                _close.Content = new TextBlock { Text = "닫기" };
                UpdateSummary();
            }
        }

        private static bool SamePath(string source, string path)
        {
            try
            {
                return !string.IsNullOrEmpty(source) && string.Equals(Path.GetFullPath(source), path, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void CloseOrCancel()
        {
            if (_cancel != null)
                _cancel.Cancel();
            else
                Close();
        }

        private void OpenFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", ExportedPath }, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                _status.Text = "폴더를 열지 못했습니다: " + ex.Message;
            }
        }
    }
}
