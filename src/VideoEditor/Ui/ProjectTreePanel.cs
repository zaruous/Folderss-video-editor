using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 프로젝트 트리(편집 창 맨 왼쪽): 프로젝트 설정을 폴더 트리로 — 📁 프로젝트 › 화면 · 소리 · 타임라인 · 파일.
    /// 폴더마다 지금 값을 요약해 보이고, 고르면 아래에서 바로 바꾼다(편집은 실행 취소 가능).
    /// </summary>
    internal sealed class ProjectTreePanel : DockPanel
    {
        public const string Screen = "화면";
        public const string Sound = "소리";
        public const string Timeline = "타임라인";
        public const string File = "파일";

        private const string FolderGlyph = "";
        private const string OpenFolderGlyph = "";

        internal static readonly (string Label, int Width, int Height)[] SizePresets =
        {
            ("1920×1080 (16:9 FHD)", 1920, 1080),
            ("1280×720 (16:9 HD)", 1280, 720),
            ("3840×2160 (16:9 4K)", 3840, 2160),
            ("1080×1920 (9:16 세로 — 쇼츠·릴스)", 1080, 1920),
            ("1080×1080 (1:1 정사각형)", 1080, 1080),
            ("1440×1080 (4:3)", 1440, 1080)
        };

        internal static readonly string[] RatePresets = { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60" };
        private static readonly int[] SampleRates = { 44100, 48000 };

        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly EditorCommands _commands;
        private readonly TreeView _tree = new TreeView { BorderThickness = new Thickness(0), Padding = new Thickness(2, 4, 2, 4) };
        private readonly ContentControl _detail = new ContentControl();
        private readonly Dictionary<string, TreeViewItem> _nodes = new Dictionary<string, TreeViewItem>();
        private readonly Dictionary<string, TextBlock> _summaries = new Dictionary<string, TextBlock>();
        private readonly List<Action> _refreshers = new List<Action>();
        private TreeViewItem _root;
        private TextBlock _rootName;
        private string _shown;
        private bool _updating;

        public ProjectTreePanel(EditorSession session, EditorServices services, EditorCommands commands)
        {
            _session = session;
            _services = services;
            _commands = commands;
            AutomationProperties.SetAutomationId(this, "ProjectTreePanel");

            var title = UiKit.Label("프로젝트");
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(10, 8, 10, 4);
            SetDock(title, Dock.Top);
            Children.Add(title);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40, GridUnitType.Star), MinHeight = 120 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60, GridUnitType.Star), MinHeight = 120 });
            AutomationProperties.SetAutomationId(_tree, "ProjectTree");
            AutomationProperties.SetName(_tree, "프로젝트 트리");
            _tree.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            _tree.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            // 가로 스크롤 막대(테마 없는 밝은 막대) 대신 넘치는 요약은 잘라 보인다
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
            grid.Children.Add(_tree);
            var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            splitter.SetResourceReference(BackgroundProperty, "BorderBrush");
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);
            AutomationProperties.SetAutomationId(_detail, "ProjectTreeDetail");
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail };
            Grid.SetRow(scroll, 2);
            grid.Children.Add(scroll);
            Children.Add(grid);

            BuildTree();
            _tree.SelectedItemChanged += (s, e) => ShowSelected();
            session.Changed += (s, e) => Refresh();
            Refresh();
            Select(Screen);
        }

        /// <summary>폴더 하나를 고르고 그 설정을 아래에 보인다([프로젝트 설정…] 메뉴가 화면 폴더로 부른다).</summary>
        public void Select(string name)
        {
            if (!_nodes.TryGetValue(name, out var item))
                return;
            _root.IsExpanded = true;
            item.IsSelected = true;
            item.BringIntoView();
        }

        // ---------- 트리 ----------

        private void BuildTree()
        {
            _rootName = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            _root = new TreeViewItem { Header = Header(OpenFolderGlyph, _rootName, null), IsExpanded = true };
            AutomationProperties.SetAutomationId(_root, "ProjectTreeRoot");
            AutomationProperties.SetName(_root, "프로젝트");
            foreach (var name in new[] { Screen, Sound, Timeline, File })
            {
                var summary = UiKit.Label("", true);
                summary.Margin = new Thickness(8, 0, 0, 0);
                summary.TextTrimming = TextTrimming.CharacterEllipsis;
                _summaries[name] = summary;
                var item = new TreeViewItem { Header = Header(FolderGlyph, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center }, summary), Tag = name };
                AutomationProperties.SetAutomationId(item, "ProjectTree_" + name);
                AutomationProperties.SetName(item, name);
                _nodes[name] = item;
                _root.Items.Add(item);
            }
            _tree.Items.Add(_root);
        }

        private static FrameworkElement Header(string glyph, TextBlock name, TextBlock summary)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var icon = new TextBlock { Text = glyph, FontFamily = UiKit.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            row.Children.Add(icon);
            row.Children.Add(name);
            if (summary != null)
                row.Children.Add(summary);
            return row;
        }

        /// <summary>프로젝트가 바뀌면 폴더 요약과 보이는 설정 값을 고친다(입력 중인 칸은 그대로).</summary>
        private void Refresh()
        {
            var p = _session.Project;
            _rootName.Text = (_session.FilePath == null ? "새 프로젝트" : Path.GetFileNameWithoutExtension(_session.FilePath)) + (_session.Dirty ? " *" : "");
            _summaries[Screen].Text = p.Width + "×" + p.Height + " · " + p.Rate + " fps";
            _summaries[Sound].Text = p.SampleRate + " Hz";
            var length = TimeSpan.FromSeconds(Math.Round(p.Rate.ToSeconds(p.Duration)));
            _summaries[Timeline].Text = (length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss")) + (p.Magnetic ? " · 마그네틱" : "");
            _summaries[File].Text = _session.FilePath == null ? "저장 안 함" : Path.GetFileName(_session.FilePath);
            _updating = true;
            try
            {
                foreach (var refresh in _refreshers)
                    refresh();
            }
            finally
            {
                _updating = false;
            }
        }

        private void ShowSelected()
        {
            var name = (_tree.SelectedItem as TreeViewItem)?.Tag as string;
            if (name == _shown && name != null)
                return;
            _shown = name;
            _refreshers.Clear();
            switch (name)
            {
                case Screen: _detail.Content = ScreenDetail(); break;
                case Sound: _detail.Content = SoundDetail(); break;
                case Timeline: _detail.Content = TimelineDetail(); break;
                case File: _detail.Content = FileDetail(); break;
                default: _detail.Content = Note("폴더를 고르면 그 설정을 여기서 바꿉니다."); break;
            }
            Refresh();
        }

        // ---------- 화면: 해상도 · 프레임 레이트 ----------

        private UIElement ScreenDetail()
        {
            var panel = Section(Screen);
            panel.Children.Add(UiKit.Label("해상도"));
            var size = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
            AutomationProperties.SetAutomationId(size, "TreeSizeBox");
            AutomationProperties.SetName(size, "해상도");
            foreach (var preset in SizePresets)
                size.Items.Add(preset.Label);
            size.Items.Add("직접 입력");
            var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            var width = new TextBox { Width = 64 };
            var height = new TextBox { Width = 64 };
            AutomationProperties.SetAutomationId(width, "TreeWidthBox");
            AutomationProperties.SetAutomationId(height, "TreeHeightBox");
            custom.Children.Add(width);
            custom.Children.Add(UiKit.Label(" × "));
            custom.Children.Add(height);
            size.SelectionChanged += (s, e) =>
            {
                if (size.SelectedIndex >= 0 && size.SelectedIndex < SizePresets.Length)
                {
                    width.Text = SizePresets[size.SelectedIndex].Width.ToString();
                    height.Text = SizePresets[size.SelectedIndex].Height.ToString();
                }
            };
            panel.Children.Add(size);
            panel.Children.Add(custom);
            panel.Children.Add(UiKit.Label("프레임 레이트"));
            var rate = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
            AutomationProperties.SetAutomationId(rate, "TreeRateBox");
            AutomationProperties.SetName(rate, "프레임 레이트");
            panel.Children.Add(rate);
            var note = UiKit.Label("프레임 레이트를 바꾸면 클립 위치는 초 기준으로 다시 맞춥니다.", true);
            note.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(note);
            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(error, "TreeScreenError");
            var apply = UiKit.TextButton("적용", "TreeApplyScreenButton", () =>
            {
                if (!int.TryParse(width.Text, out var w) || !int.TryParse(height.Text, out var h) || w < 16 || h < 16 || w > 8192 || h > 8192)
                {
                    error.Text = "해상도는 16~8192 사이의 숫자여야 합니다.";
                    return;
                }
                var chosen = _session.Project.Rate;
                if (rate.SelectedIndex >= 0 && rate.SelectedIndex < RatePresets.Length)
                    FrameRate.TryParse(RatePresets[rate.SelectedIndex], out chosen);
                error.Text = "";
                _commands.ApplyScreen(w, h, chosen);
            });
            apply.HorizontalAlignment = HorizontalAlignment.Left;
            apply.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(apply);
            panel.Children.Add(error);
            // 프로젝트 값으로 다시 채운다(입력 중인 칸은 건드리지 않음)
            _refreshers.Add(() =>
            {
                if (width.IsKeyboardFocused || height.IsKeyboardFocused)
                    return;
                var p = _session.Project;
                width.Text = p.Width.ToString();
                height.Text = p.Height.ToString();
                var index = Array.FindIndex(SizePresets, x => x.Width == p.Width && x.Height == p.Height);
                size.SelectedIndex = index >= 0 ? index : SizePresets.Length;
                rate.Items.Clear();
                foreach (var preset in RatePresets)
                    rate.Items.Add(preset + " fps");
                var current = RatePresets.Select(x => { FrameRate.TryParse(x, out var r); return r; }).ToList().FindIndex(r => r.Equals(p.Rate));
                if (current < 0)
                {
                    rate.Items.Add(p.Rate + " fps (지금)");
                    current = rate.Items.Count - 1;
                }
                rate.SelectedIndex = current;
            });
            return panel;
        }

        // ---------- 소리: 샘플 레이트 ----------

        private UIElement SoundDetail()
        {
            var panel = Section(Sound);
            panel.Children.Add(UiKit.Label("샘플 레이트"));
            var rate = new ComboBox { Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
            AutomationProperties.SetAutomationId(rate, "TreeSampleRateBox");
            AutomationProperties.SetName(rate, "샘플 레이트");
            foreach (var value in SampleRates)
                rate.Items.Add(value + " Hz");
            rate.SelectionChanged += (s, e) =>
            {
                if (!_updating && rate.SelectedIndex >= 0 && SampleRates[rate.SelectedIndex] != _session.Project.SampleRate)
                    _commands.SetSampleRate(SampleRates[rate.SelectedIndex]);
            };
            panel.Children.Add(rate);
            var note = UiKit.Label("미리 보기와 내보내기 소리를 이 값으로 섞습니다. 영상용은 보통 48000 Hz입니다.", true);
            note.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(note);
            _refreshers.Add(() => rate.SelectedIndex = Array.IndexOf(SampleRates, _session.Project.SampleRate));
            return panel;
        }

        // ---------- 타임라인: 마그네틱 · 요약 · 사진 기본 길이 ----------

        private UIElement TimelineDetail()
        {
            var panel = Section(Timeline);
            var magnetic = new CheckBox { Content = new TextBlock { Text = "마그네틱 주 트랙 — 동영상 1에 빈틈 없이 붙임", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 2, 0, 8) };
            AutomationProperties.SetAutomationId(magnetic, "TreeMagneticCheck");
            magnetic.Checked += (s, e) => { if (!_updating) _commands.SetMagnetic(true); };
            magnetic.Unchecked += (s, e) => { if (!_updating) _commands.SetMagnetic(false); };
            panel.Children.Add(magnetic);
            var info = UiKit.Label("", true);
            info.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(info, "TreeTimelineInfo");
            panel.Children.Add(info);
            var secondsLabel = UiKit.Label("사진·이미지·글자 클립 기본 길이(초) — 편집기 전체 설정");
            secondsLabel.TextWrapping = TextWrapping.Wrap;
            secondsLabel.Margin = new Thickness(0, 12, 0, 2);
            panel.Children.Add(secondsLabel);
            var seconds = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Text = UiKit.Number(_services.ImageSeconds) };
            AutomationProperties.SetAutomationId(seconds, "TreeImageSecondsBox");
            void SaveSeconds()
            {
                if (UiKit.TryNumber(seconds.Text, out var value) && value > 0 && value <= 3600)
                {
                    if (_services.SaveSetting(EditorServices.ImageSecondsKey, UiKit.Number(value)))
                        _commands.Status("사진·글자 기본 길이: " + UiKit.Number(value) + "초");
                }
                else
                {
                    seconds.Text = UiKit.Number(_services.ImageSeconds);
                    _commands.Status("기본 길이는 0보다 크고 3600초 이하여야 합니다.");
                }
            }
            seconds.LostKeyboardFocus += (s, e) => SaveSeconds();
            seconds.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    SaveSeconds();
                    e.Handled = true;
                }
            };
            panel.Children.Add(seconds);
            _refreshers.Add(() =>
            {
                var p = _session.Project;
                magnetic.IsChecked = p.Magnetic;
                info.Text = "길이 " + p.Rate.Timecode(p.Duration) + " · 클립 " + p.AllClips.Count() + "개\n동영상 트랙 " + p.VideoTracks.Count
                            + " · 오디오 트랙 " + p.AudioTracks.Count + " · 마커 " + p.Markers.Count;
            });
            return panel;
        }

        // ---------- 파일: 프로젝트 파일 · 다운로드 폴더 ----------

        private UIElement FileDetail()
        {
            var panel = Section(File);
            panel.Children.Add(UiKit.Label("프로젝트 파일"));
            var path = UiKit.Label("", true);
            path.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(path, "TreeFilePathText");
            panel.Children.Add(path);
            var fileButtons = Buttons();
            fileButtons.Children.Add(UiKit.TextButton("저장", "TreeSaveButton", () => _commands.Save()));
            fileButtons.Children.Add(UiKit.TextButton("다른 이름으로…", "TreeSaveAsButton", () => _commands.SaveAs()));
            var openProject = UiKit.TextButton("폴더 열기", "TreeOpenProjectFolderButton", () =>
            {
                var error = UiKit.OpenFolder(Path.GetDirectoryName(_session.FilePath));
                if (error != null)
                    _commands.Status(error);
            });
            fileButtons.Children.Add(openProject);
            panel.Children.Add(fileButtons);

            var downloadLabel = UiKit.Label("다운로드 폴더 (URL·유튜브에서 받은 파일)");
            downloadLabel.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(downloadLabel);
            var download = UiKit.Label("", true);
            download.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(download, "TreeDownloadFolderText");
            panel.Children.Add(download);
            var downloadButtons = Buttons();
            downloadButtons.Children.Add(UiKit.TextButton("폴더 열기", "TreeOpenDownloadFolderButton", () =>
            {
                var error = UiKit.OpenFolder(_services.MediaDownloadFolder(_session.FilePath));
                if (error != null)
                    _commands.Status(error);
            }));
            downloadButtons.Children.Add(UiKit.TextButton("바꾸기…", "TreeChangeDownloadFolderButton", () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "다운로드 폴더 고르기" };
                var current = _services.MediaDownloadFolder(_session.FilePath);
                if (Directory.Exists(current))
                    dialog.InitialDirectory = current;
                if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                    return;
                _services.SaveSetting(EditorServices.DownloadFolderKey, dialog.FolderName);
                Refresh();
                _commands.Status("다운로드 폴더: " + dialog.FolderName);
            }));
            var reset = UiKit.TextButton("기본값", "TreeDefaultDownloadFolderButton", () =>
            {
                _services.SaveSetting(EditorServices.DownloadFolderKey, null);
                Refresh();
                _commands.Status("다운로드 폴더를 기본값으로 되돌렸습니다.");
            });
            downloadButtons.Children.Add(reset);
            panel.Children.Add(downloadButtons);
            _refreshers.Add(() =>
            {
                path.Text = _session.FilePath ?? "아직 저장하지 않았습니다.";
                openProject.IsEnabled = _session.FilePath != null;
                var custom = _services.HasDownloadFolder;
                download.Text = _services.MediaDownloadFolder(_session.FilePath)
                                + (custom ? "\n(지정한 폴더)" : _session.FilePath == null ? "\n(저장 안 한 프로젝트 — 플러그인 데이터 폴더)" : "\n(프로젝트 옆 media)");
                reset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            });
            return panel;
        }

        // ---------- 공용 ----------

        private static StackPanel Section(string title)
        {
            var panel = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
            var head = UiKit.Label(title);
            head.FontWeight = FontWeights.SemiBold;
            head.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(head);
            return panel;
        }

        private static StackPanel Buttons()
        {
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        }

        private static UIElement Note(string text)
        {
            var block = UiKit.Label(text, true);
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(10);
            return block;
        }
    }
}
