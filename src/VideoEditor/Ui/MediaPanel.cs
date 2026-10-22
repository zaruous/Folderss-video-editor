using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Folderss.Plugins;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미디어 패널(왼쪽 위): [미디어] 가져온 파일 목록(썸네일·종류·길이 — 두 번 누르면 타임라인 끝에 추가, 끌어서 원하는 자리에),
    /// [폴더] Folderss 폴더 패널에서 골라 가져오기, [글자] 글자 클립 만들기. 도구 막대에 [URL에서 가져오기](직접 링크·유튜브).
    /// </summary>
    internal sealed class MediaPanel : DockPanel
    {
        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly EditorCommands _commands;
        private readonly ListBox _list = new ListBox { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly TabControl _tabs = new TabControl();
        private Point _dragStart;
        private bool _folderLoaded;

        public MediaPanel(EditorSession session, EditorServices services, EditorCommands commands)
        {
            _session = session;
            _services = services;
            _commands = commands;
            AutomationProperties.SetAutomationId(_tabs, "MediaTabs");
            UiKit.StyleTabs(_tabs);
            _tabs.Items.Add(new TabItem { Header = "미디어", Content = MediaTab() });
            var folderTab = new TabItem { Header = "폴더", Content = new TextBlock { Text = "불러오는 중…", Margin = new Thickness(8) } };
            _tabs.Items.Add(folderTab);
            _tabs.Items.Add(new TabItem { Header = "글자", Content = TextTab() });
            _tabs.SelectionChanged += (s, e) =>
            {
                if (_tabs.SelectedItem == folderTab && !_folderLoaded)
                {
                    _folderLoaded = true;
                    folderTab.Content = FolderTab();
                }
            };
            Children.Add(_tabs);
            session.Changed += (s, e) => Refill();
            EventHandler toolChanged = (s, e) => Dispatcher.BeginInvoke(new Action(Refill));
            Loaded += (s, e) =>
            {
                services.ToolChanged -= toolChanged;
                services.ToolChanged += toolChanged;
            };
            Unloaded += (s, e) => services.ToolChanged -= toolChanged;
            Refill();
        }

        private UIElement MediaTab()
        {
            var panel = new DockPanel();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            bar.Children.Add(UiKit.IconButton(UiKit.GlyphImport, "미디어 가져오기 (Ctrl+I)", "ImportButton", _commands.Import));
            bar.Children.Add(UiKit.IconButton(UiKit.GlyphLink, "URL에서 가져오기(직접 링크·유튜브 주소·유튜브 검색)", "ImportUrlButton", _commands.ImportFromUrl));
            bar.Children.Add(UiKit.IconButton(UiKit.GlyphAdd, "고른 미디어를 타임라인 끝에 추가", "AddToTimelineButton", AddSelected));
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
            AutomationProperties.SetAutomationId(_list, "MediaList");
            AutomationProperties.SetName(_list, "프로젝트 미디어");
            _list.SelectionMode = SelectionMode.Extended;
            _list.SetResourceReference(BackgroundProperty, "PanelBackground");
            _list.MouseDoubleClick += (s, e) => AddSelected();
            _list.PreviewMouseLeftButtonDown += (s, e) => _dragStart = e.GetPosition(_list);
            _list.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _list.SelectedItems.Count == 0)
                    return;
                var point = e.GetPosition(_list);
                if (Math.Abs(point.X - _dragStart.X) < 6 && Math.Abs(point.Y - _dragStart.Y) < 6)
                    return;
                var paths = _list.SelectedItems.Cast<FrameworkElement>().Select(i => ((MediaInfo)i.Tag).Path).ToArray();
                var data = new DataObject();
                data.SetData(TimelineCanvas.MediaDataFormat, paths);
                DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy);
            };
            _list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    AddSelected();
                    e.Handled = true;
                }
            };
            _list.ContextMenu = new ContextMenu();
            _list.ContextMenuOpening += (s, e) => FillMenu(_list.ContextMenu);
            _list.AllowDrop = true;
            _list.Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                    _ = _commands.ImportPaths(files, null, -1);
            };
            var empty = new TextBlock
            {
                Text = "[가져오기] 또는 파일을 여기·타임라인에 끌어 놓으세요.\n동영상·사진(이미지)·오디오를 쓸 수 있습니다.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10),
                IsHitTestVisible = false
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            var grid = new Grid();
            grid.Children.Add(_list);
            grid.Children.Add(empty);
            _list.Items.CurrentChanged += (s, e) => { };
            _session.Changed += (s, e) => empty.Visibility = _session.Project.Media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = _session.Project.Media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            panel.Children.Add(grid);
            return panel;
        }

        private void FillMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            var selected = Selected();
            if (selected.Length == 0)
            {
                menu.Items.Add(Item("미디어 가져오기…", _commands.Import));
                menu.Items.Add(Item("URL에서 가져오기…", _commands.ImportFromUrl));
                return;
            }
            menu.Items.Add(Item("타임라인에 추가", AddSelected));
            menu.Items.Add(Item("탐색기에서 보기", () => _commands.Reveal(selected[0].Path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("미디어에서 빼기", () => _commands.RemoveMedia(selected.Select(m => m.Path).ToArray())));
        }

        private static MenuItem Item(string header, Action click)
        {
            var item = new MenuItem { Header = new TextBlock { Text = header } };
            item.Click += (s, e) => click();
            return item;
        }

        private MediaInfo[] Selected()
        {
            return _list.SelectedItems.Cast<FrameworkElement>().Select(i => (MediaInfo)i.Tag).ToArray();
        }

        private void AddSelected()
        {
            foreach (var media in Selected())
                _commands.AddToTimeline(media);
        }

        /// <summary>목록을 프로젝트 미디어로 다시 채운다(고른 항목은 유지).</summary>
        private void Refill()
        {
            var selected = Selected().Select(m => m.Path).ToList();
            _list.Items.Clear();
            foreach (var media in _session.Project.Media)
            {
                var item = MediaItem(media);
                _list.Items.Add(item);
                if (selected.Contains(media.Path))
                    _list.SelectedItems.Add(item);
            }
        }

        private FrameworkElement MediaItem(MediaInfo media)
        {
            var row = new DockPanel { Tag = media, Margin = new Thickness(2), Background = Brushes.Transparent };
            AutomationProperties.SetName(row, media.Name);
            var picture = new Image { Width = 72, Height = 42, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
            var frame = new Border { Child = picture, Width = 72, Height = 42, Margin = new Thickness(0, 0, 8, 0) };
            frame.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            picture.Margin = new Thickness(0);
            if (media.Kind == MediaKind.Audio)
            {
                frame.Child = new TextBlock { Text = UiKit.GlyphAudio, FontFamily = UiKit.IconFont, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                _services.Thumbnail(media).ContinueWith(t =>
                {
                    if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result != null)
                        Dispatcher.BeginInvoke(new Action(() => picture.Source = t.Result));
                });
            }
            DockPanel.SetDock(frame, Dock.Left);
            row.Children.Add(frame);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(UiKit.Label(media.Name));
            var info = media.Kind == MediaKind.Image ? "사진·이미지 · " + media.Width + "×" + media.Height
                : media.Kind == MediaKind.Audio ? "오디오 · " + Duration(media.DurationSeconds)
                : "동영상 · " + Duration(media.DurationSeconds) + " · " + media.Width + "×" + media.Height + (media.HasAudio ? "" : " · 소리 없음");
            var used = _session.Project.AllClips.Any(c => string.Equals(c.Path, media.Path, StringComparison.OrdinalIgnoreCase));
            text.Children.Add(UiKit.Label(info + (used ? " · 사용 중" : ""), true));
            row.Children.Add(text);
            row.ToolTip = media.Path;
            return row;
        }

        private static string Duration(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }

        // ---------- 폴더(Folderss 폴더 패널) ----------

        private UIElement FolderTab()
        {
            var panel = new DockPanel();
            IFolderPanel folder;
            try
            {
                var start = _services.Setting(EditorServices.LastFolderKey);
                if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                    start = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                folder = _services.Manager.CreateFolderPanel(start);
            }
            catch (Exception ex)
            {
                return new TextBlock { Text = "폴더 패널을 열지 못했습니다: " + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
            }
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            bar.Children.Add(UiKit.TextButton("고른 파일 가져오기", "ImportFromFolderButton", () => _ = _commands.ImportPaths(folder.SelectedPaths.ToArray(), null, -1)));
            var hint = UiKit.Label("  파일을 타임라인으로 끌어 놓아도 됩니다", true);
            bar.Children.Add(hint);
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
            folder.PathChanged += (s, e) => _services.SaveSetting(EditorServices.LastFolderKey, folder.CurrentPath);
            panel.Children.Add(folder.View);
            return panel;
        }

        // ---------- 글자 ----------

        private UIElement TextTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(UiKit.Label("글자(타이틀)를 재생 헤드 위치의 덮어 쓰기 트랙에 넣습니다.", true));
            var box = new TextBox { Text = "제목", AcceptsReturn = true, MinHeight = 60, Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(box, "NewTextBox");
            panel.Children.Add(box);
            panel.Children.Add(UiKit.TextButton("글자 추가", "AddTextButton", () => _commands.AddText(box.Text)));
            return panel;
        }
    }
}
