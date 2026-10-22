using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using VideoEditor.Core;
using Track = VideoEditor.Core.Track;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 편집 창 전체: 메뉴 막대 / 미디어 · 플레이어 · 속성 / 타임라인 / 상태 줄. 단축키는 흔히 쓰는 영상 편집기 단축키를 따른다.
    /// 처음 열 때 FFmpeg를 찾고, 없으면 설치 창을 띄운다. 창을 닫을 때 저장하지 않은 변경이 있으면 묻는다.
    /// </summary>
    internal sealed class EditorView : DockPanel
    {
        private readonly EditorServices _services;
        private readonly EditorSession _session = new EditorSession();
        private readonly PreviewPlayer _player;
        private readonly EditorCommands _commands;
        private readonly TimelineView _timeline;
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        private readonly Button _ffmpegButton;
        private readonly EventHandler _toolChanged;
        private Window _window;
        private Window _host;
        private bool _setupOffered;
        private bool _hostClosing;
        private readonly RecoveryStore _recovery;
        private readonly System.Windows.Threading.DispatcherTimer _autosave;
        private readonly ProjectTreePanel _projectTree;
        private readonly ColumnDefinition _treeColumn;
        private readonly Border _treeBorder;
        private readonly GridSplitter _treeSplitter;
        private MenuItem _treeMenuItem;

        public EditorView(EditorServices services)
        {
            _services = services;
            _player = new PreviewPlayer(services, Dispatcher)
            {
                Muted = services.Setting(EditorServices.PlayerMutedKey) == "true"
            };
            double scale;
            if (UiKit.TryNumber(services.Setting(EditorServices.PreviewScaleKey), out scale) && scale > 0 && scale <= 1)
                _player.Scale = scale;
            _commands = new EditorCommands(_session, services, _player, this);
            _timeline = new TimelineView(_session, services, _commands);
            _commands.Timeline = _timeline;
            Focusable = true;
            FocusVisualStyle = null;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            TextElement.SetFontFamily(this, new System.Windows.Media.FontFamily("Segoe UI"));
            SetResourceReference(TextElement.ForegroundProperty, "PrimaryText");
            AutomationProperties.SetAutomationId(this, "VideoEditorView");

            var menu = BuildMenu();
            SetDock(menu, Dock.Top);
            Children.Add(menu);

            // 상태 줄
            var statusBar = new DockPanel { Margin = new Thickness(8, 2, 4, 2) };
            _ffmpegButton = UiKit.TextButton("FFmpeg", "FfmpegStatusButton", _commands.FfmpegSetup);
            _ffmpegButton.Padding = new Thickness(6, 1, 6, 1);
            DockPanel.SetDock(_ffmpegButton, Dock.Right);
            statusBar.Children.Add(_ffmpegButton);
            AutomationProperties.SetAutomationId(_info, "ProjectStatusText");
            _info.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            DockPanel.SetDock(_info, Dock.Right);
            statusBar.Children.Add(_info);
            AutomationProperties.SetAutomationId(_status, "StatusText");
            statusBar.Children.Add(_status);
            var statusBorder = UiKit.Panel(statusBar, "SurfaceBackground");
            statusBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            SetDock(statusBorder, Dock.Bottom);
            Children.Add(statusBorder);

            // 위: 미디어 | 플레이어 | 속성,  아래: 타임라인
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26, GridUnitType.Star), MinWidth = 160 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50, GridUnitType.Star), MinWidth = 240 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Star), MinWidth = 180 });
            var media = UiKit.Panel(new MediaPanel(_session, services, _commands));
            top.Children.Add(media);
            top.Children.Add(Splitter(1, true));
            var player = UiKit.Panel(new PlayerPanel(_session, services, _commands, _player), "WindowBackground");
            Grid.SetColumn(player, 2);
            top.Children.Add(player);
            top.Children.Add(Splitter(3, true));
            var properties = UiKit.Panel(new PropertyPanel(_session, _commands));
            Grid.SetColumn(properties, 4);
            top.Children.Add(properties);

            var main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(55, GridUnitType.Star), MinHeight = 160 });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45, GridUnitType.Star), MinHeight = 150 });
            main.Children.Add(top);
            var rowSplitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            rowSplitter.SetResourceReference(BackgroundProperty, "BorderBrush");
            Grid.SetRow(rowSplitter, 1);
            main.Children.Add(rowSplitter);
            var timeline = UiKit.Panel(_timeline);
            Grid.SetRow(timeline, 2);
            main.Children.Add(timeline);

            // 맨 왼쪽: 프로젝트 트리(위아래로 길게) | 나머지(미디어 · 플레이어 · 속성 / 타임라인)
            _projectTree = new ProjectTreePanel(_session, services, _commands);
            _treeColumn = new ColumnDefinition { Width = new GridLength(18, GridUnitType.Star), MinWidth = 180 };
            var outer = new Grid();
            outer.ColumnDefinitions.Add(_treeColumn);
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82, GridUnitType.Star), MinWidth = 600 });
            _treeBorder = UiKit.Panel(_projectTree);
            outer.Children.Add(_treeBorder);
            _treeSplitter = Splitter(1, true);
            outer.Children.Add(_treeSplitter);
            Grid.SetColumn(main, 2);
            outer.Children.Add(main);
            Children.Add(outer);
            ShowProjectTree(services.Setting(EditorServices.ProjectTreeHiddenKey) != "true", false);
            _commands.ProjectSettingsRequested += () =>
            {
                ShowProjectTree(true, true);
                _projectTree.Select(ProjectTreePanel.Screen);
            };

            _commands.StatusChanged += text => _status.Text = text;
            _timeline.Canvas.StatusMessage += _commands.Status;
            _timeline.Canvas.MediaDropped += (paths, track, frame) => _ = _commands.ImportPaths(paths, track, frame);
            _timeline.Canvas.ContextRequested += OpenClipMenu;
            _session.Changed += (s, e) =>
            {
                UpdateInfo();
                _commands.RefreshPreview();
            };
            _session.PlayheadChanged += (s, e) => _commands.RefreshPreview();
            _player.Position += frame =>
            {
                _session.Playhead = frame;
                _timeline.Canvas.EnsureVisible(frame);
            };
            _player.Message += _commands.Status;
            _toolChanged = (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateInfo();
                _commands.RefreshPreview();
            }));
            services.ToolChanged += _toolChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
            // 창을 닫으면 함께 쓰는 EditorServices에서 떼어 낸다(닫은 창이 남아 미리 보기를 그리지 않게)
            Unloaded += (s, e) =>
            {
                _services.ToolChanged -= _toolChanged;
                _player.Dispose();
                _autosave.Stop();
                if (_host != null)
                    _host.Closing -= OnHostClosing;
                _recovery.Detach();
            };
            // 복구용 자동 저장: 저장하지 않은 변경이 있으면 2분마다, 저장하거나 버리면 지운다
            _recovery = new RecoveryStore(services);
            _autosave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _autosave.Tick += (s, e) =>
            {
                if (_session.Dirty)
                    _recovery.Write(_session);
            };
            _autosave.Start();
            _session.Changed += (s, e) =>
            {
                if (!_session.Dirty)
                    _recovery.Clear();
            };
            UpdateInfo();
            _status.Text = "미디어를 가져오세요(Ctrl+I) — 동영상·사진·오디오를 타임라인에 놓고 Ctrl+E로 내보냅니다.";
        }

        /// <summary>프로젝트 트리를 보이거나 숨긴다(보기 › 프로젝트 트리). remember면 설정에 기억한다.</summary>
        private void ShowProjectTree(bool visible, bool remember)
        {
            _treeBorder.Visibility = _treeSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _treeColumn.MinWidth = visible ? 180 : 0;
            _treeColumn.Width = visible ? new GridLength(18, GridUnitType.Star) : new GridLength(0);
            if (_treeMenuItem != null)
                _treeMenuItem.IsChecked = visible;
            if (remember)
                _services.SaveSetting(EditorServices.ProjectTreeHiddenKey, visible ? null : "true");
        }

        private GridSplitter Splitter(int column, bool vertical)
        {
            var splitter = new GridSplitter { Width = 5, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Center, ResizeDirection = GridResizeDirection.Columns };
            splitter.SetResourceReference(BackgroundProperty, "BorderBrush");
            Grid.SetColumn(splitter, column);
            return splitter;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadedCore();
            }
            catch (Exception ex)
            {
                // async void에서 새는 예외는 Folderss를 끝낼 수 있다
                _status.Text = "편집 창을 준비하지 못했습니다: " + ex.Message;
            }
        }

        private async System.Threading.Tasks.Task LoadedCore()
        {
            _services.ToolChanged -= _toolChanged;
            _services.ToolChanged += _toolChanged;
            _window = Window.GetWindow(this);
            if (_window != null)
            {
                _window.Closing -= OnWindowClosing;
                _window.Closing += OnWindowClosing;
                if (_window.Width < 1100)
                    _window.Width = Math.Min(1280, SystemParameters.WorkArea.Width);
                if (_window.Height < 700)
                    _window.Height = Math.Min(800, SystemParameters.WorkArea.Height);
                // Folderss 주 창(이 창의 주인)이 정말 닫히면 플러그인 창도 묻지 않고 닫힌다 — 그 전에 복구용으로 써 둔다
                _host = _window.Owner;
                if (_host != null)
                {
                    _host.Closing -= OnHostClosing;
                    _host.Closing += OnHostClosing;
                }
            }
            UpdateInfo();
            Keyboard.Focus(_timeline.Canvas);
            var tool = await _services.LocateAsync();
            UpdateInfo();
            if (tool == null && !_setupOffered)
            {
                _setupOffered = true;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                _commands.FfmpegSetup();
            }
            OfferRecovery();
            _commands.RefreshPreview();
        }

        /// <summary>지난번에 저장하지 않고 닫힌 편집이 있으면 복구를 묻는다(가장 최근 것).</summary>
        private void OfferRecovery()
        {
            var pending = _recovery.Pending();
            if (pending.Count == 0)
                return;
            var entry = pending[0];
            var name = string.IsNullOrEmpty(entry.OriginalPath) ? "새 프로젝트" : Path.GetFileName(entry.OriginalPath);
            var answer = UiKit.Ask(this, "프로젝트 복구",
                "저장하지 않고 닫힌 편집이 있습니다: " + name + " (" + entry.SavedAt.ToString("yyyy-MM-dd HH:mm") + ").\n복구할까요?",
                "복구", "지우기", "나중에");
            if (answer == 0)
                _commands.OpenRecovered(entry);
            else if (answer == 1)
                entry.Delete();
        }

        /// <summary>Folderss 주 창이 닫히기 시작(트레이로 숨기기는 Cancel=true라 건너뜀): 저장 안 한 편집을 복구용으로 써 둔다.</summary>
        private void OnHostClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
                return;
            _hostClosing = true;
            _player.Stop();
            if (_session.Dirty)
                _recovery.Write(_session);
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_hostClosing)
            {
                // Folderss가 끝나는 중이라 물을 수 없다 — 복구 파일은 OnHostClosing에서 써 두었다
                _player.Dispose();
                return;
            }
            if (!_commands.ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }
            // 저장했거나 "저장 안 함"을 골랐다 — 복구 파일은 필요 없다
            _recovery.Clear();
            _player.Dispose();
        }

        private void UpdateInfo()
        {
            var project = _session.Project;
            _info.Text = project.Width + "×" + project.Height + " · " + project.Rate + " fps · " + project.Rate.Timecode(project.Duration);
            var tool = _services.Tool;
            ((TextBlock)_ffmpegButton.Content).Text = tool == null ? "FFmpeg 없음 — 설치" : "FFmpeg " + (tool.MajorVersion >= 99 ? "개발판" : tool.MajorVersion.ToString());
            _ffmpegButton.ToolTip = tool == null ? "FFmpeg를 설치하거나 지정합니다" : tool.VersionLine + "\n" + tool.Folder;
            var window = _window ?? Window.GetWindow(this);
            if (window != null)
            {
                var name = _session.FilePath != null ? Path.GetFileNameWithoutExtension(_session.FilePath) : "새 프로젝트";
                window.Title = "동영상 편집 — " + name + (_session.Dirty ? " *" : "");
            }
        }

        // ---------- 메뉴 ----------

        private Menu BuildMenu()
        {
            var menu = new Menu { Padding = new Thickness(2) };
            AutomationProperties.SetAutomationId(menu, "MainMenu");
            menu.SetResourceReference(BackgroundProperty, "SurfaceBackground");
            menu.Items.Add(Top("파일",
                Item("새 프로젝트", "Ctrl+N", _commands.New),
                Item("프로젝트 열기…", "Ctrl+O", _commands.Open),
                Item("저장", "Ctrl+S", () => _commands.Save()),
                Item("다른 이름으로 저장…", "Ctrl+Shift+S", () => _commands.SaveAs()),
                null,
                Item("미디어 가져오기…", "Ctrl+I", _commands.Import),
                Item("URL에서 가져오기…", "", _commands.ImportFromUrl),
                Item("내보내기…", "Ctrl+E", _commands.Export),
                null,
                Item("프로젝트 설정…", "", _commands.ProjectSettings),
                Item("FFmpeg 설정…", "", _commands.FfmpegSetup)));
            menu.Items.Add(Top("편집",
                Item("실행 취소", "Ctrl+Z", _commands.Undo),
                Item("다시 실행", "Ctrl+Y", _commands.Redo),
                null,
                Item("잘라내기", "Ctrl+X", () => _commands.Copy(true)),
                Item("복사", "Ctrl+C", () => _commands.Copy(false)),
                Item("붙여넣기", "Ctrl+V", _commands.Paste),
                Item("삭제", "Delete", () => _commands.Delete(false)),
                Item("리플 삭제", "Shift+Delete", () => _commands.Delete(true)),
                Item("빈틈 닫기", "Alt+Delete", _commands.CloseGap),
                Item("모두 선택", "Ctrl+A", _commands.SelectAll),
                null,
                Item("분할", "Ctrl+B", _commands.Split),
                Item("재생 헤드까지 앞 다듬기", "Alt+[", () => _commands.TrimToPlayhead(true)),
                Item("재생 헤드까지 뒤 다듬기", "Alt+]", () => _commands.TrimToPlayhead(false)),
                Item("속도·길이…", "Ctrl+R", _commands.ChangeSpeed),
                Item("오디오 분리", "Ctrl+Alt+D", _commands.DetachAudio),
                Item("클립 소리 끄기/켜기", "Ctrl+Shift+M", _commands.ToggleClipMute),
                Item("시계 방향 90° 회전", "Ctrl+Alt+→", () => _commands.Rotate(90)),
                Item("반시계 방향 90° 회전", "Ctrl+Alt+←", () => _commands.Rotate(-90))));
            menu.Items.Add(Top("표시",
                Item("마커 추가", "M", _commands.AddMarker),
                Item("마커 편집…", "Shift+M", _commands.EditMarker),
                Item("이전 마커", "Shift+↑", () => _commands.JumpMarker(-1)),
                Item("다음 마커", "Shift+↓", () => _commands.JumpMarker(1)),
                null,
                Item("In 점", "I", _commands.MarkIn),
                Item("Out 점", "O", _commands.MarkOut),
                Item("In/Out 지우기", "", _commands.ClearInOut),
                null,
                Item("글자 추가", "", () => _commands.AddText("제목"))));
            _treeMenuItem = Item("프로젝트 트리", "", () => ShowProjectTree(!_treeMenuItem.IsChecked, true));
            _treeMenuItem.IsCheckable = false;
            menu.Items.Add(Top("보기",
                _treeMenuItem,
                null,
                Item("재생/일시 정지", "Space", _commands.TogglePlay),
                Item("정지", "Ctrl+/", _commands.StopPlayback),
                null,
                Item("확대", "Ctrl+=", () => _timeline.Zoom(1.25)),
                Item("축소", "Ctrl+-", () => _timeline.Zoom(0.8)),
                Item("타임라인에 맞춤", "Shift+Z", _timeline.Canvas.ZoomToFit),
                null,
                Item("동영상 트랙 추가", "", () => _commands.AddTrack(TrackKind.Video)),
                Item("오디오 트랙 추가", "", () => _commands.AddTrack(TrackKind.Audio))));
            menu.Items.Add(Top("도움말", Item("단축키…", "F1", ShowShortcuts)));
            return menu;
        }

        private static MenuItem Top(string header, params MenuItem[] items)
        {
            var top = new MenuItem { Header = new TextBlock { Text = header } };
            AutomationProperties.SetName(top, header);
            foreach (var item in items)
            {
                if (item == null)
                    top.Items.Add(new Separator());
                else
                    top.Items.Add(item);
            }
            return top;
        }

        private static MenuItem Item(string header, string gesture, Action click)
        {
            var item = new MenuItem { Header = new TextBlock { Text = header }, InputGestureText = gesture };
            AutomationProperties.SetName(item, header);
            item.Click += (s, e) => click();
            return item;
        }

        private void OpenClipMenu(Clip clip, Track track, long frame)
        {
            var menu = new ContextMenu { PlacementTarget = _timeline.Canvas };
            if (clip != null)
            {
                menu.Items.Add(Item("분할", "Ctrl+B", _commands.Split));
                menu.Items.Add(Item("잘라내기", "Ctrl+X", () => _commands.Copy(true)));
                menu.Items.Add(Item("복사", "Ctrl+C", () => _commands.Copy(false)));
                menu.Items.Add(Item("삭제", "Delete", () => _commands.Delete(false)));
                menu.Items.Add(Item("리플 삭제", "Shift+Delete", () => _commands.Delete(true)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("속도·길이…", "Ctrl+R", _commands.ChangeSpeed));
                if (clip.Kind == ClipKind.Media && clip.HasVideo && clip.HasAudio && !clip.AudioDetached)
                    menu.Items.Add(Item("오디오 분리", "Ctrl+Alt+D", _commands.DetachAudio));
                if (clip.Kind == ClipKind.Media)
                    menu.Items.Add(Item("탐색기에서 보기", "Ctrl+Shift+R", () => _commands.Reveal(clip.Path)));
            }
            else
            {
                _session.Playhead = frame;
                menu.Items.Add(Item("붙여넣기", "Ctrl+V", _commands.Paste));
                menu.Items.Add(Item("빈틈 닫기", "Alt+Delete", _commands.CloseGap));
                menu.Items.Add(Item("미디어 가져오기…", "Ctrl+I", _commands.Import));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("동영상 트랙 추가", "", () => _commands.AddTrack(TrackKind.Video)));
                menu.Items.Add(Item("오디오 트랙 추가", "", () => _commands.AddTrack(TrackKind.Audio)));
            }
            menu.IsOpen = true;
        }

        private void ShowShortcuts()
        {
            UiKit.Inform(this, "단축키",
                "재생/일시 정지 Space · 정지 Ctrl+/ · 프레임 ←/→ · 1초 Shift+←/→ · 편집점 ↑/↓ · 처음/끝 Home/End\n"
                + "분할 Ctrl+B · 삭제 Delete · 리플 삭제 Shift+Delete · 빈틈 닫기 Alt+Delete · 앞/뒤 다듬기 Alt+[ / Alt+]\n"
                + "실행 취소 Ctrl+Z · 다시 실행 Ctrl+Y · 잘라내기/복사/붙여넣기 Ctrl+X/C/V · 모두 선택 Ctrl+A\n"
                + "속도 Ctrl+R · 오디오 분리 Ctrl+Alt+D · 클립 소리 Ctrl+Shift+M · 회전 Ctrl+Alt+←/→\n"
                + "마커 M · 마커 편집 Shift+M · 이전/다음 마커 Shift+↑/↓ · In I · Out O\n"
                + "확대 Ctrl+= · 축소 Ctrl+- · 맞춤 Shift+Z\n"
                + "새로 Ctrl+N · 열기 Ctrl+O · 저장 Ctrl+S · 다른 이름 Ctrl+Shift+S · 가져오기 Ctrl+I · 내보내기 Ctrl+E\n\n"
                + "타임라인: 휠 = 가로 이동, Ctrl+휠 = 확대/축소, Shift+휠 = 세로 이동, 끄는 동안 Alt = 붙기 잠시 끔");
        }

        // ---------- 단축키 ----------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            var ctrl = (mods & ModifierKeys.Control) != 0;
            var shift = (mods & ModifierKeys.Shift) != 0;
            var alt = (mods & ModifierKeys.Alt) != 0;
            // 글 입력 칸에서는 글자·이동 키를 그 칸에 둔다(파일 명령만 받음)
            var typing = Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ComboBoxItem;
            Action action = null;
            if (ctrl && !alt)
            {
                switch (key)
                {
                    case Key.N: action = _commands.New; break;
                    case Key.O: action = _commands.Open; break;
                    case Key.S: action = shift ? (Action)(() => _commands.SaveAs()) : () => _commands.Save(); break;
                    case Key.I: action = _commands.Import; break;
                    case Key.E: action = _commands.Export; break;
                }
                if (!typing && action == null)
                {
                    switch (key)
                    {
                        case Key.Z: action = shift ? (Action)_commands.Redo : _commands.Undo; break;
                        case Key.Y: action = _commands.Redo; break;
                        case Key.X: action = () => _commands.Copy(true); break;
                        case Key.C: action = () => _commands.Copy(false); break;
                        case Key.V: action = _commands.Paste; break;
                        case Key.A: action = _commands.SelectAll; break;
                        case Key.B: action = _commands.Split; break;
                        case Key.R: action = _commands.ChangeSpeed; break;
                        case Key.M: if (shift) action = _commands.ToggleClipMute; break;
                        case Key.OemPlus: case Key.Add: action = () => _timeline.Zoom(1.25); break;
                        case Key.OemMinus: case Key.Subtract: action = () => _timeline.Zoom(0.8); break;
                        case Key.Oem2: case Key.Divide: action = _commands.StopPlayback; break;
                    }
                }
            }
            else if (ctrl && alt && !typing)
            {
                switch (key)
                {
                    case Key.D: action = _commands.DetachAudio; break;
                    case Key.Right: action = () => _commands.Rotate(90); break;
                    case Key.Left: action = () => _commands.Rotate(-90); break;
                }
            }
            else if (alt && !typing)
            {
                switch (key)
                {
                    case Key.OemOpenBrackets: action = () => _commands.TrimToPlayhead(true); break;
                    case Key.OemCloseBrackets: action = () => _commands.TrimToPlayhead(false); break;
                    case Key.Delete: action = _commands.CloseGap; break;
                }
            }
            else if (!typing)
            {
                switch (key)
                {
                    case Key.Space: action = _commands.TogglePlay; break;
                    case Key.Left: action = shift ? (Action)(() => _commands.StepSeconds(-1)) : () => _commands.Step(-1); break;
                    case Key.Right: action = shift ? (Action)(() => _commands.StepSeconds(1)) : () => _commands.Step(1); break;
                    case Key.Up: action = shift ? (Action)(() => _commands.JumpMarker(-1)) : () => _commands.JumpEditPoint(-1); break;
                    case Key.Down: action = shift ? (Action)(() => _commands.JumpMarker(1)) : () => _commands.JumpEditPoint(1); break;
                    case Key.Home: action = shift ? (Action)(() => _commands.GoSelectedClip(false)) : () => _commands.GoTo(0); break;
                    case Key.End: action = shift ? (Action)(() => _commands.GoSelectedClip(true)) : () => _commands.GoTo(_session.Project.Duration); break;
                    case Key.Delete: case Key.Back: action = () => _commands.Delete(shift); break;
                    case Key.Escape: action = _session.ClearSelection; break;
                    case Key.I: action = _commands.MarkIn; break;
                    case Key.O: action = _commands.MarkOut; break;
                    case Key.M: action = shift ? (Action)_commands.EditMarker : _commands.AddMarker; break;
                    case Key.Z: if (shift) action = _timeline.Canvas.ZoomToFit; break;
                    case Key.F1: action = ShowShortcuts; break;
                }
            }
            if (action == null)
                return;
            e.Handled = true;
            action();
        }
    }
}
