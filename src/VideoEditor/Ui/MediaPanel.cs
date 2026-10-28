using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folderss.Plugins;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미디어 패널(왼쪽 위): [미디어] 가져온 파일 목록(썸네일·종류·길이 — 두 번 누르면 타임라인 끝에 추가, 끌어서 원하는 자리에),
    /// [폴더] Folderss 폴더 패널에서 골라 가져오기, [글자] 글자 클립 만들기, [효과] 동영상 효과(썸네일 — 새 효과는 "새로"), [전환] 전환 효과(썸네일).
    /// 도구 막대에 [URL에서 가져오기](직접 링크·유튜브).
    /// </summary>
    internal sealed class MediaPanel : DockPanel
    {
        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly EditorCommands _commands;
        private readonly ListBox _list = new ListBox { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly TabControl _tabs = new TabControl();
        private readonly TabItem _effectsTab;
        private Point _dragStart;
        private bool _dragFromItem;
        private bool _folderLoaded;
        private bool _menuFilled;

        public MediaPanel(EditorSession session, EditorServices services, EditorCommands commands)
        {
            _session = session;
            _services = services;
            _commands = commands;
            AutomationProperties.SetAutomationId(_tabs, "MediaTabs");
            // 탭이 여섯이라 머리글 여백을 줄인다(1280×800 창의 264px 패널에서도 탭 줄이 한 줄에 — 여백 8이면 제 폭 합 285px로 두 줄, 5면 249px)
            UiKit.StyleTabs(_tabs, 5);
            _tabs.Items.Add(new TabItem { Header = "미디어", Content = MediaTab() });
            var folderTab = new TabItem { Header = "폴더", Content = new TextBlock { Text = "불러오는 중…", Margin = new Thickness(8) } };
            _tabs.Items.Add(folderTab);
            _tabs.Items.Add(new TabItem { Header = "글자", Content = TextTab() });
            var artTab = new TabItem { Header = "요소", Content = ArtTab() };
            AutomationProperties.SetAutomationId(artTab, "ArtTab");
            _tabs.Items.Add(artTab);
            // 효과·전환 탭은 처음 볼 때 만든다(효과마다 썸네일을 FFmpeg로 그리므로 열지 않으면 그리지 않게)
            _effectsTab = new TabItem { Header = "효과", Content = new TextBlock { Text = "불러오는 중…", Margin = new Thickness(8) } };
            AutomationProperties.SetAutomationId(_effectsTab, "EffectsTab");
            _tabs.Items.Add(_effectsTab);
            var transitionsTab = new TabItem { Header = "전환", Content = new TextBlock { Text = "불러오는 중…", Margin = new Thickness(8) } };
            AutomationProperties.SetAutomationId(transitionsTab, "TransitionsTab");
            _tabs.Items.Add(transitionsTab);
            _tabs.SelectionChanged += (s, e) =>
            {
                if (e.OriginalSource != _tabs)
                    return;
                // 탭을 바꾸면 효과 칸 미리 보기(미리 보기 화면)와 썸네일 움직임을 멈춘다
                _commands.EffectHover.Cancel();
                StopPreview();
                if (_tabs.SelectedItem == folderTab && !_folderLoaded)
                {
                    _folderLoaded = true;
                    folderTab.Content = FolderTab();
                }
                if (_tabs.SelectedItem == _effectsTab && _showNewEffects == null)
                    _effectsTab.Content = EffectsTab();
                if (_tabs.SelectedItem == transitionsTab && _refillTransitions == null)
                    transitionsTab.Content = TransitionsTab();
            };
            Children.Add(_tabs);
            session.Changed += (s, e) => Refill();
            // FFmpeg를 찾으면(또는 바꾸면) 썸네일·전환 목록(FFmpeg 버전에 따라 쓸 수 있는 효과)을 다시
            EventHandler toolChanged = (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                Refill();
                _refillTransitions?.Invoke();
            }));
            // 효과를 본 것으로 적으면(이 창이나 다른 편집 창에서) "새로" 표시를 다시
            EventHandler seenChanged = (s, e) => ShowNewEffects();
            _previewTimer.Tick += (s, e) => PreviewTick();
            Loaded += (s, e) =>
            {
                services.ToolChanged -= toolChanged;
                services.ToolChanged += toolChanged;
                services.SeenEffectsChanged -= seenChanged;
                services.SeenEffectsChanged += seenChanged;
                ShowNewEffects();
            };
            Unloaded += (s, e) =>
            {
                services.ToolChanged -= toolChanged;
                services.SeenEffectsChanged -= seenChanged;
                StopPreview();
                _commands.EffectHover.Cancel();
            };
            Refill();
            ShowNewEffects();
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
            _list.MouseDoubleClick += (s, e) =>
            {
                if (OnItem(_list, e))
                    AddSelected();
            };
            _list.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _dragStart = e.GetPosition(_list);
                _dragFromItem = OnItem(_list, e);
            };
            _list.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !_dragFromItem || _list.SelectedItems.Count == 0)
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
            _list.ContextMenuOpening += (s, e) =>
            {
                FillMenu(_list.ContextMenu);
                _menuFilled = true;
            };
            // 오른쪽 클릭이 아닌 길로 열려도(자동화·화면 시험의 IsOpen) 지금 고른 미디어로 채운다
            _list.ContextMenu.Opened += (s, e) =>
            {
                if (!_menuFilled)
                    FillMenu(_list.ContextMenu);
                _menuFilled = false;
            };
            _list.AllowDrop = true;
            _list.Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                    _ = _commands.ImportPaths(files, null, -1);
            };
            var empty = new TextBlock
            {
                Text = "[가져오기] 또는 파일을 여기·타임라인에 끌어 놓으세요.\n동영상·사진(이미지)·오디오를 쓸 수 있습니다. 자막(SRT·VTT)은 자막 트랙으로 들어갑니다.",
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

        /// <summary>마우스가 목록 항목 위에서 눌렸는지(스크롤바를 끌 때 끌어 놓기·두 번 누르기가 시작되지 않게).</summary>
        private static bool OnItem(ItemsControl list, MouseEventArgs e)
        {
            return e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) != null;
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
            // 사운드 분리: 소리가 있는 동영상에서만
            if (selected.Any(ClipLinks.CanSplit))
                menu.Items.Add(Item("사운드 분리해서 배치", () => _commands.PlaceSoundSplit(selected)));
            if (selected.Length == 1 && ClipLinks.CanSplit(selected[0]))
                menu.Items.Add(Item("사운드를 파일로 저장…", () => _ = _commands.SaveSound(selected[0])));
            // 인물 마스크: 동영상·사진 하나
            if (selected.Length == 1 && (selected[0].Kind == MediaKind.Video || selected[0].Kind == MediaKind.Image))
                menu.Items.Add(Item("인물 마스크 만들기", () => _ = _commands.BuildPersonMaskFor(selected[0])));
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
            // 화면과 비율이 다르면 알린다(맞춤이면 빈 곳, 채우기면 잘리는 곳)
            var differs = media.AspectDiffers(_session.Project.Width, _session.Project.Height);
            text.Children.Add(UiKit.Label(info + (differs ? " · 화면과 비율 다름" : "") + (used ? " · 사용 중" : ""), true));
            row.Children.Add(text);
            row.ToolTip = media.Path + (differs
                ? "\n화면(" + _session.Project.Width + "×" + _session.Project.Height + ")과 비율이 달라 [화면에 맞춤]이면 빈 곳이, [화면 채우기]면 잘리는 곳이 생깁니다.\n클립 오른쪽 메뉴 [화면 맞춤 방식]에서 고르세요."
                : "");
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

        // ---------- 요소 ----------

        // [요소] 탭 묶음: 제목과 칸 묶음의 AutomationId(도형은 1단계 그대로 ArtTiles)
        private static readonly (string Group, string AutomationId)[] ArtGroups =
        {
            ("도형", "ArtTiles"), ("말풍선", "ArtTilesBubble"), ("강조", "ArtTilesMark"), ("프레임·배경", "ArtTilesFrame")
        };

        /// <summary>
        /// [요소] 도형·말풍선·강조·프레임·배경 칸(묶음마다 제목): 두 번 누르면 재생 헤드에, 타임라인으로 끌어 놓으면 그 줄·자리에 넣는다(<see cref="EditorCommands.AddArt"/>).
        /// 칸의 그림은 모양 윤곽(<see cref="ArtGeometry"/>)을 처음 채우기 색으로 그대로 그린 것이라 FFmpeg·그리개를 기다리지 않는다.
        /// </summary>
        private UIElement ArtTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            // 좁은 패널(1280×800 창의 264px)에서 잘리지 않게 줄을 나눈다
            var hint = UiKit.Label("두 번 누르면 재생 헤드 위치에, 타임라인으로 끌어 놓으면 그 자리에 넣습니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(hint, "ArtHint");
            panel.Children.Add(hint);
            foreach (var (group, automationId) in ArtGroups)
            {
                panel.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
                var tiles = new WrapPanel();
                AutomationProperties.SetAutomationId(tiles, automationId);
                foreach (ArtKind kind in Enum.GetValues(typeof(ArtKind)))
                {
                    if (ArtShape.Group(kind) == group)
                        tiles.Children.Add(ArtTile(kind));
                }
                panel.Children.Add(tiles);
            }
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        /// <summary>
        /// 칸 그림의 모양: 처음 모양이되 화면 크기 요소(집중선·화면 테두리)는 16:9 작은 상자로, 두께 있는 요소는 칸에서 보일 만큼 굵게(가장 긴 변의 6%).
        /// </summary>
        private static ArtShape TileShape(ArtKind kind)
        {
            var shape = ArtShape.Default(kind);
            if (kind == ArtKind.FocusLines || kind == ArtKind.ScreenBorder || kind == ArtKind.LowerThird)
            {
                shape.Width = 320;
                shape.Height = kind == ArtKind.LowerThird ? 60 : 180;
            }
            if (ArtShape.UsesThickness(kind) && kind != ArtKind.Line && kind != ArtKind.Arrow)
                shape.Thickness = Math.Max(shape.Thickness, Math.Max(shape.Width, shape.Height) * 0.06);
            return shape;
        }

        private Button ArtTile(ArtKind kind)
        {
            const double Icon = 44;
            var name = ArtShape.DisplayName(kind);
            var shape = TileShape(kind);
            var scale = Icon / Math.Max(shape.Width, shape.Height);
            // 처음 채우기 색 — 흰색처럼 밝은 색(말풍선·화면 테두리)은 어두운 윤곽선을 둘러 밝은 테마에서도 보이게
            var color = UiKit.ParseColor(TimelineEdits.ArtStyle(kind).Fill.Colors[0], Color.FromRgb(0x3B, 0x82, 0xF6));
            var light = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255 > 0.8;
            var path = new System.Windows.Shapes.Path
            {
                Data = StyledTextRasterizer.ArtPath(ArtGeometry.Outline(shape, scale)),
                Fill = new SolidColorBrush(color),
                Stroke = light ? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) : null,
                StrokeThickness = light ? 1 : 0
            };
            var picture = new Grid { Width = Icon, Height = Icon, Margin = new Thickness(0, 2, 0, 2) };
            path.HorizontalAlignment = HorizontalAlignment.Center;
            path.VerticalAlignment = VerticalAlignment.Center;
            picture.Children.Add(path);
            var content = new StackPanel();
            content.Children.Add(picture);
            content.Children.Add(new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = content, Width = 64, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4), ToolTip = name };
            AutomationProperties.SetAutomationId(button, "ArtTile" + kind);
            AutomationProperties.SetName(button, name);
            button.MouseDoubleClick += (s, e) =>
            {
                _commands.AddArt(kind);
                e.Handled = true;
            };
            var dragStart = new Point();
            var pressed = false;
            button.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dragStart = e.GetPosition(button);
                pressed = true;
            };
            button.PreviewMouseLeftButtonUp += (s, e) => pressed = false;
            button.PreviewMouseMove += (s, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed)
                    return;
                var point = e.GetPosition(button);
                if (Math.Abs(point.X - dragStart.X) < 6 && Math.Abs(point.Y - dragStart.Y) < 6)
                    return;
                pressed = false;
                var data = new DataObject();
                data.SetData(TimelineCanvas.ArtDataFormat, kind.ToString());
                DragDrop.DoDragDrop(button, data, DragDropEffects.Copy);
            };
            return button;
        }

        // ---------- 글자 ----------

        /// <summary>
        /// [글자] 글을 쓰고 [글자 추가] — 아래 스타일(스타일 모음 + 내 스타일)을 고르면 그 모양으로 넣는다(고른 단추를 다시 누르면 풀려 꾸미지 않은 글자).
        /// 스타일 단추를 두 번 누르면 바로 넣는다. 썸네일은 이 탭이 처음 보일 때 그리고, 내 스타일은 탭이 보일 때마다 설정을 다시 읽는다.
        /// </summary>
        private UIElement TextTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(UiKit.Label("글자(타이틀)를 재생 헤드 위치의 덮어 쓰기 트랙에 넣습니다.", true));
            var box = new TextBox { Text = "제목", AcceptsReturn = true, MinHeight = 60, Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(box, "NewTextBox");
            panel.Children.Add(box);
            TextStyle chosen = null;
            Button chosenTile = null;
            var styleNote = UiKit.Label("스타일: 꾸미지 않음", true);
            AutomationProperties.SetAutomationId(styleNote, "NewTextStyleNote");
            styleNote.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(styleNote);
            panel.Children.Add(UiKit.TextButton("글자 추가", "AddTextButton", () => _commands.AddText(box.Text, chosen)));

            // 고른 단추는 강조 테두리로, 다시 누르면 풀린다
            void Choose(Button tile, string name, TextStyle style)
            {
                if (chosenTile != null)
                    chosenTile.ClearValue(Control.BorderBrushProperty);
                if (tile == chosenTile)
                {
                    chosenTile = null;
                    chosen = null;
                    styleNote.Text = "스타일: 꾸미지 않음";
                    return;
                }
                chosenTile = tile;
                chosen = style;
                tile.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
                styleNote.Text = "스타일: " + name;
            }
            var tiles = new List<(Border Frame, TextStyle Style, string ImageId)>();
            Button Tile(string name, TextStyle style, string automationId)
            {
                var (button, frame) = StyleTiles.Tile(name, automationId);
                button.BorderThickness = new Thickness(2);
                button.Click += (s, e) => Choose(button, name, style);
                // 두 번 누르면 그 스타일로 바로 넣는다(첫 누름에 골라졌다가 둘째 누름에 풀리지 않게 고른 채로 둔다)
                button.MouseDoubleClick += (s, e) =>
                {
                    if (chosenTile != button)
                        Choose(button, name, style);
                    _commands.AddText(box.Text, style);
                    e.Handled = true;
                };
                tiles.Add((frame, style, automationId + "Image"));
                return button;
            }

            var presetsTitle = UiKit.Label("스타일 모음", false);
            presetsTitle.Margin = new Thickness(0, 10, 0, 4);
            panel.Children.Add(presetsTitle);
            var presets = new WrapPanel();
            AutomationProperties.SetAutomationId(presets, "NewTextStyles");
            var all = TextStylePresets.All;
            for (var i = 0; i < all.Count; i++)
                presets.Children.Add(Tile(all[i].Name, all[i].Style, "NewTextStyle" + i));
            panel.Children.Add(presets);

            var mineTitle = UiKit.Label("내 스타일", false);
            mineTitle.Margin = new Thickness(0, 8, 0, 4);
            panel.Children.Add(mineTitle);
            var mine = new WrapPanel();
            AutomationProperties.SetAutomationId(mine, "NewTextMine");
            panel.Children.Add(mine);
            var shown = false;
            void Refill()
            {
                // 고른 것이 내 스타일이면 다시 읽을 때 푼다(다른 곳에서 지웠을 수 있다)
                if (chosenTile != null && mine.Children.Contains(chosenTile))
                    Choose(chosenTile, null, null);
                mine.Children.Clear();
                tiles.RemoveAll(t => t.ImageId.StartsWith("NewTextMine", StringComparison.Ordinal));
                var styles = _services?.TextStyles() ?? new List<(string Name, TextStyle Style)>();
                for (var i = 0; i < styles.Count; i++)
                {
                    mine.Children.Add(Tile(styles[i].Name, styles[i].Style, "NewTextMine" + i));
                    if (shown)
                        StyleTiles.ShowThumbnail(_services, tiles[tiles.Count - 1].Frame, styles[i].Style, "NewTextMine" + i + "Image");
                }
                mineTitle.Visibility = mine.Visibility = styles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            Refill();
            panel.IsVisibleChanged += (s, e) =>
            {
                if (!panel.IsVisible)
                    return;
                if (!shown)
                {
                    shown = true;
                    foreach (var tile in tiles)
                        StyleTiles.ShowThumbnail(_services, tile.Frame, tile.Style, tile.ImageId);
                }
                Refill();
            };
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        // ---------- 효과 ----------

        // [묶음]에서 아직 보지 않은 효과만 보는 항목(새 효과가 있을 때만)
        private const string NewGroup = "새로 추가됨";

        /// <summary>
        /// [효과] 탭: 동영상 효과(클립 그림 위에 그리는 효과 — <see cref="VideoEffects"/>). 효과는 묶음(<see cref="VideoEffectDef.Group"/>)마다 제목 아래 같은 크기 칸으로 보이고,
        /// [묶음]에서 한 묶음만 고를 수 있다. 칸에서 골라 [고른 클립에 넣기]·두 번 누르기, 또는 타임라인의 클립으로 끌어 놓는다(동영상·사진 클립에만, 값은 속성 패널의 [효과] 탭에서).
        /// [고른 클립에 넣기]는 동영상·사진 클립을 골랐을 때만 켜지고, 고른 클립에 이미 든 효과는 썸네일 둘레와 이름을 강조색으로 보인다.
        /// 아직 보지 않은 효과(<see cref="VideoEffectNews"/>)는 칸에 "새로"를 달고, 탭 맨 위 알림 줄의 [보기]는 [묶음]을 "새로 추가됨"(새 효과만)으로, [확인]은 모두 본 것으로 한다.
        /// 동영상·사진 클립을 고른 채 칸에 마우스를 올려 두면 그 효과를 얹은 모습을 미리 보기 화면에 잠깐 돌려 보인다(<see cref="EffectHoverPreview"/>).
        /// 썸네일은 그 효과를 합성 그림에 실제로 그린 띠다(마우스를 올리면 움직임). 탭 전체가 한 번에 스크롤된다(안쪽 목록은 스스로 스크롤하지 않는다).
        /// </summary>
        private UIElement EffectsTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            AutomationProperties.SetAutomationId(scroll, "EffectsTabScroll");

            var list = new ListBox { BorderThickness = new Thickness(0), Margin = new Thickness(0, 2, 0, 0) };
            AutomationProperties.SetAutomationId(list, "VideoEffectList");
            AutomationProperties.SetName(list, "동영상 효과");
            list.SetResourceReference(BackgroundProperty, "PanelBackground");
            list.ItemsPanel = TileGrid.PanelTemplate();
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            PassWheel(list, scroll);
            var tiles = new List<ListBoxItem>();
            var badges = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
            foreach (var def in VideoEffects.All)
            {
                var badge = NewBadge(def.Id);
                badges[def.Id] = badge;
                var item = VideoEffectTile(def, badge);
                // 두 번 누르면 그 효과를 고른 클립에(묶음 제목을 두 번 눌러서는 넣지 않게 칸마다 받는다)
                item.MouseDoubleClick += (s, e) =>
                {
                    list.SelectedItem = item;
                    _commands.AddVideoEffect(def.Id);
                    e.Handled = true;
                };
                // 마우스를 올려 두면 고른 동영상·사진 클립에 그 효과를 얹어 미리 보기 화면에, 떠나면 원래 화면
                item.MouseEnter += (s, e) => _commands.EffectHover.Hover(def);
                item.MouseLeave += (s, e) => _commands.EffectHover.Leave(def);
                tiles.Add(item);
            }
            // 묶음마다 제목(이름 · 개수 ───) — 묶음 차례는 효과 목록에 처음 나오는 차례.
            // 칸 목록을 ItemsSource로 준다(Items에 바로 넣으면 묶을 수 없다 — 묶기는 목록 보기(ListCollectionView)에서만 된다)
            list.ItemsSource = tiles;
            list.GroupStyle.Add(TileGroupStyle());
            list.Items.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Tag.Group"));
            list.SelectedIndex = 0;
            VideoEffectDef Chosen() => list.SelectedItem is ListBoxItem chosen ? (VideoEffectDef)chosen.Tag : VideoEffects.All[0];

            // 묶음 [모든 효과 ▾] — 새 효과가 있으면 "새로 추가됨"도
            var groups = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(groups, "VideoEffectGroup");
            AutomationProperties.SetName(groups, "동영상 효과 묶음");
            const string allGroups = "모든 효과";

            // 맨 위 알림 줄(새 효과가 있을 때만): 새 동영상 효과 N개가 추가되었습니다 [보기] [확인]
            var noticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(noticeText, "VideoEffectNewText");
            var noticeButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            noticeButtons.Children.Add(UiKit.TextButton("보기", "VideoEffectNewShow", () => groups.SelectedItem = NewGroup));
            // [확인]: 지금 있는 효과를 모두 본 것으로("새로 추가됨"을 보던 중이면 모든 효과로 돌아간다)
            noticeButtons.Children.Add(UiKit.TextButton("확인", "VideoEffectNewDismiss", () =>
            {
                if (groups.SelectedItem as string == NewGroup)
                    groups.SelectedItem = allGroups;
                _services.MarkEffectsSeen(VideoEffects.All.Select(d => d.Id));
            }));
            // 화면 낭독은 앞뒤 없이도 뜻이 통하게
            AutomationProperties.SetName(noticeButtons.Children[0], "새 효과 보기");
            AutomationProperties.SetName(noticeButtons.Children[1], "새 효과 모두 확인");
            foreach (Button button in noticeButtons.Children)
            {
                button.MinWidth = 0;
                button.Padding = new Thickness(8, 1, 8, 1);
            }
            ((Button)noticeButtons.Children[0]).ToolTip = "[묶음]을 \"" + NewGroup + "\"으로 — 새 효과만 봅니다.";
            ((Button)noticeButtons.Children[1]).ToolTip = "모든 효과를 본 것으로 하고 \"새로\" 표시를 지웁니다.";
            var noticeRow = new DockPanel();
            DockPanel.SetDock(noticeButtons, Dock.Right);
            noticeRow.Children.Add(noticeButtons);
            noticeRow.Children.Add(noticeText);
            var notice = new Border { Child = noticeRow, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 2, 0, 4) };
            notice.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            notice.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            AutomationProperties.SetAutomationId(notice, "VideoEffectNewNotice");
            panel.Children.Add(notice);

            // 묶음 [모든 효과 ▾], 아래 줄에 한 줄 설명 ……… [고른 클립에 넣기]
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var groupLabel = UiKit.Label("묶음 ", true);
            DockPanel.SetDock(groupLabel, Dock.Left);
            row.Children.Add(groupLabel);
            row.Children.Add(groups);
            panel.Children.Add(row);
            var apply = UiKit.TextButton("고른 클립에 넣기", "VideoEffectApplyButton", () => _commands.AddVideoEffect(Chosen().Id));
            apply.Margin = new Thickness(6, 0, 0, 0);
            apply.Padding = new Thickness(8, 1, 8, 1);
            apply.MinWidth = 0;
            apply.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetShowOnDisabled(apply, true);
            var hint = UiKit.Label("고른 동영상·사진 클립 위에 그립니다 · 클립으로 끌어 놓아도 됩니다", true);
            hint.TextTrimming = TextTrimming.CharacterEllipsis;
            hint.ToolTip = "동영상 효과는 클립 그림 위에 그려져 클립과 함께 옮기고 돌고 사라집니다(글자 클립에는 넣지 않습니다).\n"
                + "효과를 타임라인의 클립으로 끌어 놓거나, 두 번 눌러 고른 클립에 넣습니다. 값은 속성 패널의 [효과] 탭에서 바꿉니다.\n"
                + "동영상·사진 클립을 고른 채 효과에 마우스를 올려 두면 그 클립에 얹은 모습을 미리 보기 화면에 잠깐 보입니다.";
            var applyRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(apply, Dock.Right);
            applyRow.Children.Add(apply);
            applyRow.Children.Add(hint);
            panel.Children.Add(applyRow);
            panel.Children.Add(list);

            // "새로 추가됨"을 고르면 그때 새 효과였던 칸만 — 보는 동안 본 효과가 되어도(미리 보기·넣기) 칸이 사라지지 않게 고를 때 정해 둔다
            var filling = false;
            void Filter()
            {
                StopPreview();
                var group = groups.SelectedItem as string;
                if (group == NewGroup)
                {
                    var shown = new HashSet<string>(_services.NewEffects().Select(d => d.Id), StringComparer.Ordinal);
                    list.Items.Filter = o => shown.Contains(((VideoEffectDef)((ListBoxItem)o).Tag).Id);
                }
                else
                {
                    list.Items.Filter = group == null || group == allGroups ? null : new Predicate<object>(o => ((VideoEffectDef)((ListBoxItem)o).Tag).Group == group);
                }
                if (list.SelectedItem == null && list.Items.Count > 0)
                    list.SelectedIndex = 0;
            }
            // 묶음 목록: 모든 효과, (새 효과가 있거나 보는 중이면) 새로 추가됨, 묶음들. 고른 항목이 빠지면 모든 효과로
            void FillGroups(bool hasNew)
            {
                var chosen = groups.SelectedItem as string ?? allGroups;
                var names = new List<string> { allGroups };
                if (hasNew || chosen == NewGroup)
                    names.Add(NewGroup);
                names.AddRange(VideoEffects.Groups());
                if (names.SequenceEqual(groups.Items.Cast<string>()))
                    return;
                filling = true;
                groups.Items.Clear();
                foreach (var name in names)
                    groups.Items.Add(name);
                groups.SelectedItem = names.Contains(chosen) ? chosen : allGroups;
                filling = false;
                if ((string)groups.SelectedItem != chosen)
                    Filter();
            }
            groups.SelectionChanged += (s, e) =>
            {
                if (filling)
                    return;
                Filter();
                // "새로 추가됨"을 떠났는데 새 효과가 더 없으면 그 항목을 뺀다(고르기가 끝난 뒤에)
                if (groups.SelectedItem as string != NewGroup)
                    Dispatcher.BeginInvoke(new Action(() => FillGroups(_services.NewEffects().Count > 0)));
            };

            // 새 효과 표시: 칸의 "새로", 알림 줄, 묶음의 "새로 추가됨"
            void ShowNew(List<VideoEffectDef> news)
            {
                var ids = new HashSet<string>(news.Select(d => d.Id), StringComparer.Ordinal);
                foreach (var item in tiles)
                {
                    var id = ((VideoEffectDef)item.Tag).Id;
                    badges[id].Visibility = ids.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
                    AutomationProperties.SetHelpText(item, ids.Contains(id) ? "새로 추가된 효과" : "");
                }
                noticeText.Text = "새 동영상 효과 " + news.Count + "개가 추가되었습니다";
                notice.Visibility = news.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                FillGroups(news.Count > 0);
            }
            _showNewEffects = ShowNew;
            ShowNew(_services.NewEffects());

            // 고른 클립에 따라: 넣기 단추(동영상·사진 클립을 골랐을 때만), 이미 든 효과 강조
            void ShowSelection()
            {
                var clips = _session.SelectedClips.Where(VideoEffects.CanHave).ToList();
                apply.IsEnabled = clips.Count > 0;
                apply.ToolTip = clips.Count > 0 ? "고른 효과를 고른 동영상·사진 클립에 넣습니다(두 번 눌러도 됩니다)." : "타임라인에서 동영상·사진 클립을 고르세요(글자 클립에는 넣지 않습니다).";
                var applied = new HashSet<string>(clips.Where(c => c.Effects != null).SelectMany(c => c.Effects).Where(e => e != null).Select(e => e.Id), StringComparer.Ordinal);
                foreach (var item in tiles)
                    MarkApplied(item, applied.Contains(((VideoEffectDef)item.Tag).Id));
            }
            _session.SelectionChanged += (s, e) => ShowSelection();
            _session.Changed += (s, e) => ShowSelection();
            ShowSelection();

            // 끌어서 타임라인의 클립에 놓으면 그 클립에(누르면 효과 칸 미리 보기는 그만)
            var dragStart = new Point();
            var fromItem = false;
            list.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _commands.EffectHover.Cancel();
                dragStart = e.GetPosition(list);
                fromItem = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem;
            };
            list.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !fromItem || list.SelectedItem == null)
                    return;
                var point = e.GetPosition(list);
                if (Math.Abs(point.X - dragStart.X) < 6 && Math.Abs(point.Y - dragStart.Y) < 6)
                    return;
                StopPreview();
                var data = new DataObject();
                data.SetData(TimelineCanvas.VideoEffectDataFormat, Chosen().Id);
                DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
            };
            return scroll;
        }

        /// <summary>칸의 "새로" 표시(썸네일 왼쪽 위, 강조색 바탕) — 아직 보지 않은 효과의 칸만 보인다.</summary>
        private static FrameworkElement NewBadge(string id)
        {
            var text = new TextBlock { Text = "새로", FontSize = 10, FontWeight = FontWeights.SemiBold };
            // 글은 패널 바탕색(어두운 테마는 어둡게, 밝은 테마는 밝게) — 강조색 위에서 잘 읽히게
            text.SetResourceReference(TextBlock.ForegroundProperty, "PanelBackground");
            var badge = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(badge, "VideoEffectNew_" + id);
            AutomationProperties.SetName(badge, "새로");
            return badge;
        }

        /// <summary>새 효과 표시를 다시: [효과] 탭 머리글(새 효과가 있으면 강조색 점), 효과 탭을 만들었으면 칸의 "새로"·알림 줄·묶음.</summary>
        private void ShowNewEffects()
        {
            var news = _services.NewEffects();
            if (news.Count == 0)
            {
                _effectsTab.Header = "효과";
                _effectsTab.ToolTip = null;
                AutomationProperties.SetName(_effectsTab, "효과");
            }
            else
            {
                // 효과 • (점은 강조색) — 좁은 패널에서도 탭 줄이 넘치지 않게 개수는 툴팁·자동화 이름에
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new TextBlock { Text = "효과" });
                var dot = new TextBlock { Text = "●", FontSize = 8, Margin = new Thickness(3, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                AutomationProperties.SetAutomationId(dot, "EffectsTabNewMark");
                header.Children.Add(dot);
                _effectsTab.Header = header;
                _effectsTab.ToolTip = "새 동영상 효과 " + news.Count + "개";
                AutomationProperties.SetName(_effectsTab, "효과 (새 효과 " + news.Count + "개)");
            }
            _showNewEffects?.Invoke(news);
        }

        /// <summary>
        /// [전환] 탭: 전환 효과를 썸네일에서 고르고 [고른 클립에 넣기]·[모든 클립 사이에] — 또는 효과를 타임라인의 클립으로 끌어 놓는다.
        /// 클립 앞(앞 클립에서 바뀌어 들어오는 곳)에 들어가고, 앞 클립이 그 길이만큼 밑에 더 그려질 뿐 타임라인 위치는 바뀌지 않는다.
        /// 효과는 데이터(기본 효과 + 데이터 폴더\transitions\*.json)에서 오고, 썸네일은 그 효과를 실제로 그린 띠다(마우스를 올리면 움직임). 탭 전체가 한 번에 스크롤된다.
        /// </summary>
        private UIElement TransitionsTab()
        {
            var top = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
            var scroll = new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            AutomationProperties.SetAutomationId(scroll, "TransitionsTabScroll");
            // 설명은 한 줄(전체는 툴팁) — 좁은 패널에서도 썸네일이 많이 보이게
            var hint = UiKit.Label("클립 앞에 넣습니다(타임라인 위치는 그대로) · 타임라인의 클립으로 끌어 놓아도 됩니다", true);
            hint.TextTrimming = TextTrimming.CharacterEllipsis;
            hint.ToolTip = "전환 효과는 클립 앞(앞 클립에서 바뀌어 들어오는 곳)에 들어갑니다. 앞 클립이 그 길이만큼 밑에 더 그려질 뿐 타임라인 위치는 바뀌지 않습니다.\n"
                + "효과를 타임라인의 클립으로 끌어 놓거나, 두 번 눌러 고른 클립에 넣습니다. 썸네일에 마우스를 올리면 효과가 움직입니다.";
            top.Children.Add(hint);

            var seconds = new TextBox { Text = UiKit.Number(Transitions.DefaultSeconds), VerticalContentAlignment = VerticalAlignment.Center, Width = 44 };
            AutomationProperties.SetAutomationId(seconds, "TransitionSeconds");
            AutomationProperties.SetName(seconds, "전환 길이(초)");
            var groups = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(groups, "TransitionGroup");
            AutomationProperties.SetName(groups, "효과 묶음");
            // 길이 [ ] 초 · 묶음 [          ]
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var length = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            length.Children.Add(UiKit.Label("길이 "));
            length.Children.Add(seconds);
            length.Children.Add(UiKit.Label(" 초", true));
            foreach (FrameworkElement child in length.Children)
                child.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(length, Dock.Left);
            row.Children.Add(length);
            var groupLabel = UiKit.Label("묶음 ", true);
            groupLabel.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(groupLabel, Dock.Left);
            row.Children.Add(groupLabel);
            row.Children.Add(groups);
            top.Children.Add(row);

            var list = new ListBox { BorderThickness = new Thickness(0) };
            AutomationProperties.SetAutomationId(list, "TransitionList");
            AutomationProperties.SetName(list, "전환 효과");
            list.SetResourceReference(BackgroundProperty, "PanelBackground");
            // 썸네일을 같은 크기 칸으로 가로로 채우고 줄을 바꾼다(가로 스크롤 없음, 세로는 탭 전체가 스크롤)
            list.ItemsPanel = TileGrid.PanelTemplate();
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            PassWheel(list, scroll);

            TransitionDef Chosen() => list.SelectedItem is ListBoxItem chosen ? (TransitionDef)chosen.Tag : Transitions.Dissolve;
            // 길이는 0.1~10초(잘못 쓰면 기본 길이)
            double Seconds() => UiKit.TryNumber(seconds.Text, out var value) ? Math.Max(0.1, Math.Min(10, value)) : Transitions.DefaultSeconds;

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            buttons.Children.Add(UiKit.TextButton("고른 클립에 넣기", "TransitionApplyButton", () => _commands.ApplyTransition(Chosen().Id, Seconds())));
            buttons.Children.Add(UiKit.TextButton("모든 클립 사이에", "TransitionAllButton", () => _commands.ApplyTransitionBetween(Chosen().Id, Seconds())));
            buttons.Children.Add(UiKit.TextButton("전환 빼기", "TransitionRemoveButton", _commands.RemoveTransition));
            foreach (FrameworkElement button in buttons.Children)
                button.Margin = new Thickness(0, 0, 4, 4);
            top.Children.Add(buttons);
            top.Children.Add(list);

            // 아래: 사용자 효과 폴더(*.json을 넣으면 효과가 늘어난다)와 읽지 못한 파일 알림
            var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var problems = UiKit.Label("", true);
            problems.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(problems, "TransitionProblems");
            var folderButtons = new WrapPanel();
            folderButtons.Children.Add(UiKit.TextButton("효과 폴더 열기", "TransitionFolderButton", _commands.OpenTransitionFolder));
            bottom.Children.Add(folderButtons);
            bottom.Children.Add(problems);
            top.Children.Add(bottom);

            // 목록 채우기: 고른 묶음의 효과 중 지금 FFmpeg로 그릴 수 있는 것(FFmpeg를 모르면 모두)
            const string allGroups = "모든 효과";
            void Fill()
            {
                var keep = (list.SelectedItem as ListBoxItem)?.Tag is TransitionDef was ? was.Id : Transitions.DefaultId;
                var major = _services.Tool?.MajorVersion ?? 0;
                var usable = Transitions.All.Where(d => major == 0 || d.Ffmpeg <= major).ToList();
                var group = groups.SelectedItem as string ?? allGroups;
                var names = new[] { allGroups }.Concat(usable.Select(d => d.Group).Where(g => g != null).Distinct()).ToList();
                if (!names.SequenceEqual(groups.Items.Cast<string>()))
                {
                    _fillingTransitions = true;
                    groups.Items.Clear();
                    foreach (var name in names)
                        groups.Items.Add(name);
                    groups.SelectedItem = names.Contains(group) ? group : allGroups;
                    _fillingTransitions = false;
                    group = (string)groups.SelectedItem;
                }
                StopPreview();
                list.Items.Clear();
                var shown = usable.Where(d => group == allGroups || d.Group == group).ToList();
                // 썸네일은 사용자 효과부터 그리기를 청한다(같은 효과는 한 번만 그리므로 칸을 만들 때 같은 그림을 받는다)
                foreach (var def in Transitions.PreviewOrder(shown))
                    _ = _services.TransitionPreview(def);
                foreach (var def in shown)
                    list.Items.Add(TransitionTile(def));
                list.SelectedItem = list.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((TransitionDef)i.Tag).Id == keep) ?? list.Items.Cast<ListBoxItem>().FirstOrDefault();
                var errors = _services.TransitionErrors;
                problems.Text = errors.Count == 0 ? "" : "효과 파일에서 " + errors.Count + "개를 건너뛰었습니다(마우스를 올리면 까닭).";
                problems.ToolTip = errors.Count == 0 ? null : string.Join("\n", errors.Take(30));
                problems.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            groups.SelectionChanged += (s, e) =>
            {
                if (!_fillingTransitions)
                    Fill();
            };
            folderButtons.Children.Add(UiKit.TextButton("다시 읽기", "TransitionReloadButton", () =>
            {
                _services.LoadTransitions();
                Fill();
                var user = Transitions.All.Count(d => !d.BuiltIn);
                _commands.Status("전환 효과를 다시 읽었습니다: " + Transitions.All.Count + "개(사용자 효과 " + user + "개)"
                    + (_services.TransitionErrors.Count > 0 ? ", 건너뜀 " + _services.TransitionErrors.Count + "개" : "") + ".");
            }));
            foreach (FrameworkElement button in folderButtons.Children)
                button.Margin = new Thickness(0, 0, 4, 0);
            _refillTransitions = Fill;
            Fill();

            // 두 번 누르면 고른 클립에, 끌어서 타임라인의 클립에 놓으면 그 클립에
            list.MouseDoubleClick += (s, e) =>
            {
                if (OnItem(list, e))
                    _commands.ApplyTransition(Chosen().Id, Seconds());
            };
            var dragStart = new Point();
            var fromItem = false;
            list.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dragStart = e.GetPosition(list);
                fromItem = OnItem(list, e);
            };
            list.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !fromItem || list.SelectedItem == null)
                    return;
                var point = e.GetPosition(list);
                if (Math.Abs(point.X - dragStart.X) < 6 && Math.Abs(point.Y - dragStart.Y) < 6)
                    return;
                StopPreview();
                var data = new DataObject();
                data.SetData(TimelineCanvas.TransitionDataFormat, Chosen().Id + "|" + UiKit.Number(Seconds()));
                DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
            };
            return scroll;
        }

        /// <summary>고른 클립에 든 효과의 칸: 썸네일 둘레와 이름을 강조색으로(자동화 ItemStatus "고른 클립에 있음").</summary>
        private static void MarkApplied(ListBoxItem item, bool applied)
        {
            var content = (StackPanel)item.Content;
            var frame = (Border)content.Children[0];
            var name = (TextBlock)content.Children[1];
            if (applied)
            {
                frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                name.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            }
            else
            {
                frame.BorderBrush = Brushes.Transparent;
                name.ClearValue(TextBlock.ForegroundProperty);
            }
            name.FontWeight = applied ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetItemStatus(item, applied ? "고른 클립에 있음" : "");
        }

        /// <summary>묶음 제목(이름 · 개수 ─────): 동영상 효과 목록이 묶음마다 칸 위에 단다.</summary>
        private static GroupStyle TileGroupStyle()
        {
            var line = new FrameworkElementFactory(typeof(DockPanel));
            line.SetValue(MarginProperty, new Thickness(2, 6, 4, 2));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            name.SetValue(TextBlock.FontSizeProperty, 11.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            name.SetValue(DockProperty, Dock.Left);
            var number = new FrameworkElementFactory(typeof(TextBlock));
            number.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ItemCount") { StringFormat = " {0}" });
            number.SetValue(TextBlock.FontSizeProperty, 11.0);
            number.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            number.SetValue(DockProperty, Dock.Left);
            var rule = new FrameworkElementFactory(typeof(Border));
            rule.SetValue(HeightProperty, 1.0);
            rule.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            rule.SetValue(MarginProperty, new Thickness(8, 1, 0, 0));
            rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            line.AppendChild(name);
            line.AppendChild(number);
            line.AppendChild(rule);
            return new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = line }, HidesIfEmpty = true };
        }

        /// <summary>안쪽 목록 위에서 굴린 바퀴를 탭 스크롤로 넘긴다(목록은 높이를 다 써서 스크롤할 것이 없는데도 바퀴를 먹는다).</summary>
        private static void PassWheel(ListBox list, ScrollViewer scroll)
        {
            list.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                scroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = list });
            };
        }

        // 효과 썸네일: 정지 화면은 효과가 한창인 칸, 마우스를 올리면 띠의 칸을 차례로 보인다(한 번에 하나만)
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        private Image _previewImage;
        private BitmapSource[] _previewFrames;
        private int _previewStill;
        private int _previewTick;
        private bool _fillingTransitions;
        private Action _refillTransitions;
        private Action<List<VideoEffectDef>> _showNewEffects;

        private ListBoxItem TransitionTile(TransitionDef def)
        {
            var tip = def.Name + (def.Group != null ? " · " + def.Group : "");
            if (def.IsExpression)
                tip += "\n수식으로 그리는 효과 — 내장 효과보다 그리기가 느립니다.";
            if (!def.BuiltIn)
                tip += "\n사용자 효과(효과 폴더)";
            return Tile(def, def.Name, tip, "Transition_" + def.Id, _services.TransitionPreview(def),
                Transitions.PreviewWidth, Transitions.PreviewHeight, Transitions.PreviewFrames, Transitions.PreviewStill);
        }

        private ListBoxItem VideoEffectTile(VideoEffectDef def, FrameworkElement badge)
        {
            return Tile(def, def.Name, def.Name + (def.Group != null ? " · " + def.Group : "") + "\n" + def.Description, "VideoEffect_" + def.Id, _services.EffectPreview(def),
                VideoEffects.PreviewWidth, VideoEffects.PreviewHeight, VideoEffects.PreviewFrames, VideoEffects.PreviewStill, badge);
        }

        /// <summary>
        /// 효과 썸네일 단추 하나(전환·동영상 효과 공용): 칸 폭을 따라 16:9 썸네일과 아래에 이름. 미리 보기 띠가 그려지면 정지 칸을 보이고, 마우스를 올리면 칸을 차례로 보인다.
        /// 썸네일 틀은 늘 2px 둘레를 두고(평소 투명) 고른 클립에 든 효과만 강조색으로 칠한다 — 강조해도 칸 크기가 바뀌지 않게.
        /// badge(동영상 효과의 "새로" 표시)가 있으면 썸네일 위에 겹친다.
        /// </summary>
        private ListBoxItem Tile(object tag, string title, string tip, string automationId, System.Threading.Tasks.Task<BitmapSource> preview, int width, int height, int count, int still,
            FrameworkElement badge = null)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            UIElement picture = image;
            if (badge != null)
            {
                var layers = new Grid();
                layers.Children.Add(image);
                layers.Children.Add(badge);
                picture = layers;
            }
            var frame = new ThumbFrame { Child = picture, CornerRadius = new CornerRadius(3), ClipToBounds = true, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
            frame.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            var name = new TextBlock { Text = title, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            var content = new StackPanel { Margin = new Thickness(1) };
            content.Children.Add(frame);
            content.Children.Add(name);
            var item = new ListBoxItem { Content = content, Tag = tag, ToolTip = tip, Padding = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(item, automationId);
            AutomationProperties.SetName(item, title);
            BitmapSource[] frames = null;
            preview.ContinueWith(t =>
            {
                if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || t.Result == null)
                    return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    frames = Frames(t.Result, width, height, count);
                    if (frames != null && _previewImage != image)
                        image.Source = frames[still];
                }));
            });
            item.MouseEnter += (s, e) =>
            {
                if (frames == null)
                    return;
                StopPreview();
                _previewImage = image;
                _previewFrames = frames;
                _previewStill = still;
                _previewTick = 0;
                image.Source = frames[0];
                _previewTimer.Start();
            };
            item.MouseLeave += (s, e) =>
            {
                if (_previewImage == image)
                    StopPreview();
            };
            return item;
        }

        /// <summary>미리 보기 띠를 칸마다 자른다(띠 크기가 맞지 않으면 null).</summary>
        private static BitmapSource[] Frames(BitmapSource strip, int width, int height, int count)
        {
            if (strip.PixelWidth != width * count || strip.PixelHeight != height)
                return null;
            var frames = new BitmapSource[count];
            for (var i = 0; i < frames.Length; i++)
            {
                var crop = new CroppedBitmap(strip, new Int32Rect(i * width, 0, width, height));
                crop.Freeze();
                frames[i] = crop;
            }
            return frames;
        }

        private void PreviewTick()
        {
            if (_previewImage == null)
            {
                _previewTimer.Stop();
                return;
            }
            // 칸을 차례로 보이고 마지막 칸에서 잠깐 멈췄다가 처음부터
            _previewTick = (_previewTick + 1) % (_previewFrames.Length + 6);
            _previewImage.Source = _previewFrames[Math.Min(_previewTick, _previewFrames.Length - 1)];
        }

        private void StopPreview()
        {
            _previewTimer.Stop();
            if (_previewImage != null && _previewFrames != null)
                _previewImage.Source = _previewFrames[_previewStill];
            _previewImage = null;
            _previewFrames = null;
        }
    }
}
