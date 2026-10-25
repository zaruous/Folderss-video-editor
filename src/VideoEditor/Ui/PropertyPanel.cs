using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 속성 패널(오른쪽 위): 고른 클립의 [화면] 위치·크기·회전·불투명도·크로스페이드, [소리] 음량·페이드, [속도·길이], [글자].
    /// 아무것도 고르지 않으면 안내(프로젝트 설정은 왼쪽 프로젝트 트리). 값을 바꾸면 바로 편집(실행 취소 가능 — 슬라이더를 끄는 동안은 한 단계로 합침).
    /// 편집 때마다 다시 만들지 않고 값만 갱신한다(끄는 슬라이더가 사라지지 않게).
    /// </summary>
    internal sealed class PropertyPanel : DockPanel
    {
        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly EditorCommands _commands;
        private readonly ContentControl _body = new ContentControl();
        private readonly List<Action> _refreshers = new List<Action>();
        private string _shownKey;
        private bool _updating;

        public PropertyPanel(EditorSession session, EditorServices services, EditorCommands commands)
        {
            _session = session;
            _services = services;
            _commands = commands;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
            Children.Add(scroll);
            session.SelectionChanged += (s, e) => Update();
            session.Changed += (s, e) => Update();
            Update();
        }

        private Clip Selected()
        {
            return _session.PrimaryClip;
        }

        private void Update()
        {
            var clips = _session.SelectedClips;
            // 하나, 또는 링크된 영상·사운드 짝이면 누른 쪽
            var clip = _session.PrimaryClip;
            var key = clip != null ? "clip:" + clip.Id : clips.Count > 1 ? "many:" + clips.Count : "project";
            if (key == _shownKey)
            {
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
                return;
            }
            _shownKey = key;
            _refreshers.Clear();
            _body.Content = clip != null ? ClipView(clip) : clips.Count > 1 ? Note(clips.Count + "개 클립을 골랐습니다. 하나를 고르면 속성을 바꿀 수 있습니다.") : ProjectView();
        }

        private static UIElement Note(string text)
        {
            var block = UiKit.Label(text, true);
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(10);
            return block;
        }

        // ---------- 프로젝트 ----------

        // 프로젝트 설정(화면·소리·타임라인·파일)은 맨 왼쪽 프로젝트 트리로 옮겼다 — 여기서는 안내만
        private UIElement ProjectView()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(Title("속성"));
            panel.Children.Add(Note("클립을 고르면 위치·크기·소리·속도를 바꿀 수 있습니다.\n프로젝트 설정(해상도·프레임 레이트·소리·파일)은 왼쪽 프로젝트 트리에서 바꿉니다."));
            var settings = UiKit.TextButton("프로젝트 트리 열기", "ProjectSettingsButton", _commands.ProjectSettings);
            settings.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Margin = new Thickness(10, 0, 0, 0);
            panel.Children.Add(settings);
            return panel;
        }

        // ---------- 클립 ----------

        private UIElement ClipView(Clip clip)
        {
            var id = clip.Id;
            var panel = new DockPanel();
            var head = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };
            var title = Title(clip.Name);
            head.Children.Add(title);
            var timing = UiKit.Label("", true);
            AutomationProperties.SetAutomationId(timing, "ClipTimingText");
            head.Children.Add(timing);
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var rate = _session.Project.Rate;
                title.Text = c.Name;
                var kind = _session.Project.TrackOf(c)?.Subtitles == true ? "자막" : Kind(c);
                timing.Text = kind + " · " + rate.Timecode(c.Start) + " ~ " + rate.Timecode(c.End) + " (" + rate.Timecode(c.Duration) + ")";
            });
            DockPanel.SetDock(head, Dock.Top);
            panel.Children.Add(head);

            var tabs = new TabControl { Margin = new Thickness(6, 0, 6, 6) };
            AutomationProperties.SetAutomationId(tabs, "PropertyTabs");
            UiKit.StyleTabs(tabs);
            // 자막 트랙의 큐는 트랙 공통 모양으로 입히므로 글만 고친다(크기·색·위치·꾸미기는 쓰지 않음)
            if (_session.Project.TrackOf(clip)?.Subtitles == true)
            {
                tabs.Items.Add(new TabItem { Header = "자막", Content = SubtitleTab(id) });
            }
            else
            {
                if (clip.Kind == ClipKind.Text)
                {
                    tabs.Items.Add(new TabItem { Header = "글자", Content = TextTab(id) });
                    var styleTab = new TabItem { Header = "꾸미기", Content = StyleTab(id) };
                    AutomationProperties.SetAutomationId(styleTab, "StyleTab");
                    tabs.Items.Add(styleTab);
                }
                if (clip.IsVisual)
                    tabs.Items.Add(new TabItem { Header = "화면", Content = VisualTab(id) });
            }
            if (clip.Kind == ClipKind.Media && clip.HasAudio)
                tabs.Items.Add(new TabItem { Header = "소리", Content = AudioTab(id) });
            tabs.Items.Add(new TabItem { Header = clip.HasSourceLimit ? "속도" : "길이", Content = TimeTab(id, clip.HasSourceLimit) });
            tabs.SelectedIndex = 0;
            panel.Children.Add(tabs);
            // 처음 값을 채우는 것은 편집이 아니다 — 콤보의 SelectionChanged 등이 Edit를 부르지 않게(고르기만 해도 실행 취소 단계·덮어쓰기가 생기지 않게)
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
            return panel;
        }

        private static string Kind(Clip clip)
        {
            if (clip.Kind == ClipKind.Text)
                return "글자";
            if (clip.AudioOnly)
                return "분리한 사운드";
            switch (clip.MediaKind)
            {
                case MediaKind.Image: return "사진·이미지";
                case MediaKind.Audio: return "오디오";
                default: return "동영상";
            }
        }

        private UIElement VisualTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(NumberRow("위치 X", "PropX", -4000, 4000, "px", id, c => c.X, (c, v) => c.X = v));
            panel.Children.Add(NumberRow("위치 Y", "PropY", -4000, 4000, "px", id, c => c.Y, (c, v) => c.Y = v));
            panel.Children.Add(NumberRow("크기", "PropScale", 5, 400, "%", id, c => c.Scale * 100, (c, v) => c.Scale = Math.Max(0.05, v / 100)));
            // 맞춤 방식(동영상·사진): 화면에 맞춤 · 화면 채우기 · 늘이기
            if (_session.Project.FindClip(id)?.Kind == ClipKind.Media)
            {
                var fits = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
                AutomationProperties.SetAutomationId(fits, "PropFit");
                AutomationProperties.SetName(fits, "맞춤 방식");
                var fitKinds = new[] { ClipFit.Fit, ClipFit.Fill, ClipFit.Blur, ClipFit.Stretch };
                foreach (var kind in fitKinds)
                    fits.Items.Add(new ComboBoxItem { Content = EditorServices.FitName(kind), ToolTip = EditorServices.FitHint(kind) });
                fits.SelectionChanged += (s, e) =>
                {
                    if (_updating || fits.SelectedIndex < 0)
                        return;
                    var fit = fitKinds[fits.SelectedIndex];
                    var current = _session.Project.FindClip(id);
                    if (current != null && current.Fit != fit)
                        Edit(id, c => c.Fit = fit, null);
                };
                _refreshers.Add(() =>
                {
                    var c = _session.Project.FindClip(id);
                    if (c == null)
                        return;
                    var was = _updating;
                    _updating = true;
                    try
                    {
                        fits.SelectedIndex = Array.IndexOf(fitKinds, c.Fit);
                    }
                    finally
                    {
                        _updating = was;
                    }
                });
                var fitRow = new DockPanel();
                var fitLabel = UiKit.Label("맞춤 방식");
                fitLabel.Width = 84;
                fitRow.Children.Add(fitLabel);
                fitRow.Children.Add(fits);
                panel.Children.Add(fitRow);
            }
            panel.Children.Add(NumberRow("회전", "PropRotation", -360, 360, "°", id, c => c.Rotation, (c, v) => c.Rotation = v));
            panel.Children.Add(NumberRow("불투명도", "PropOpacity", 0, 100, "%", id, c => c.Opacity * 100, (c, v) => c.Opacity = Math.Max(0, Math.Min(1, v / 100))));
            // 전환 효과 종류(길이는 아래 줄) — 효과 탭에서 고르거나 끌어 놓아도 된다
            var kinds = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
            AutomationProperties.SetAutomationId(kinds, "PropTransitionKind");
            AutomationProperties.SetName(kinds, "전환 효과");
            // 보이는 효과: 효과 목록 + 이 클립에만 저장된 효과(효과 파일이 없는 PC에서 연 프로젝트)
            var shown = new List<TransitionDef>();
            kinds.SelectionChanged += (s, e) =>
            {
                if (_updating || kinds.SelectedIndex < 0 || kinds.SelectedIndex >= shown.Count)
                    return;
                var def = shown[kinds.SelectedIndex];
                var current = _session.Project.FindClip(id);
                if (current != null && current.Transition != def.Id)
                    Edit(id, c => Transitions.Assign(c, def), null);
            };
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                // 값을 보이는 것만으로 편집이 일어나지 않게
                var was = _updating;
                _updating = true;
                try
                {
                    var defs = Transitions.All.ToList();
                    if (Transitions.Find(c.Transition) == null && c.TransitionDef != null && c.TransitionDef.Id == c.Transition)
                        defs.Add(c.TransitionDef);
                    if (!defs.SequenceEqual(shown))
                    {
                        shown.Clear();
                        shown.AddRange(defs);
                        kinds.Items.Clear();
                        foreach (var def in shown)
                            kinds.Items.Add(def.Name);
                    }
                    kinds.SelectedIndex = Math.Max(0, shown.FindIndex(d => d.Id == c.Transition));
                }
                finally
                {
                    _updating = was;
                }
            });
            var kindRow = new DockPanel();
            var kindLabel = UiKit.Label("전환 효과");
            kindLabel.Width = 84;
            kindRow.Children.Add(kindLabel);
            kindRow.Children.Add(kinds);
            panel.Children.Add(kindRow);
            panel.Children.Add(NumberRow("전환 길이", "PropTransition", 0, 10, "초", id,
                c => _session.Project.Rate.ToSeconds(c.TransitionIn),
                (c, v) => c.TransitionIn = Math.Min(c.Duration, _session.Project.Rate.ToFrames(Math.Max(0, v)))));
            var hint = UiKit.Label("전환: 같은 트랙의 바로 앞 클립에서 이 길이만큼 겹쳐 바뀝니다(앞 클립이 밑에 더 그려지고 타임라인 위치는 그대로). 소리도 그동안 겹쳐 바뀝니다. 앞 클립이 없으면 바탕에서 바뀝니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(hint);
            var reset = UiKit.TextButton("화면 재설정", "PropResetVisual", () => Edit(id, c =>
            {
                c.X = c.Y = c.Rotation = 0;
                c.Scale = c.Opacity = 1;
            }, null));
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(reset);
            return panel;
        }

        private UIElement AudioTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(NumberRow("음량", "PropVolume", 0, 200, "%", id, c => c.Volume * 100, (c, v) => c.Volume = Math.Max(0, Math.Min(2, v / 100))));
            panel.Children.Add(NumberRow("페이드 인", "PropFadeIn", 0, 30, "초", id,
                c => _session.Project.Rate.ToSeconds(c.FadeIn), (c, v) => c.FadeIn = Math.Min(c.Duration, _session.Project.Rate.ToFrames(Math.Max(0, v)))));
            panel.Children.Add(NumberRow("페이드 아웃", "PropFadeOut", 0, 30, "초", id,
                c => _session.Project.Rate.ToSeconds(c.FadeOut), (c, v) => c.FadeOut = Math.Min(c.Duration, _session.Project.Rate.ToFrames(Math.Max(0, v)))));
            var detached = UiKit.Label("", true);
            detached.TextWrapping = TextWrapping.Wrap;
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                detached.Text = c != null && c.AudioDetached
                    ? "사운드를 분리해 오디오 트랙에 두었습니다 — 이 클립에서는 소리가 나지 않습니다. 다시 합치려면 영상과 사운드를 함께 고르고 오른쪽 클릭 [사운드 합치기]."
                    : "";
            });
            panel.Children.Add(detached);
            return panel;
        }

        private UIElement TimeTab(string id, bool hasSource)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            if (hasSource)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                var label = UiKit.Label("속도");
                label.Width = 84;
                row.Children.Add(label);
                var box = new TextBox { Width = 60, VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(box, "PropSpeed");
                AutomationProperties.SetName(box, "속도 배율");
                row.Children.Add(box);
                row.Children.Add(UiKit.Label(" 배", true));
                foreach (var preset in new[] { 0.5, 1.0, 1.5, 2.0 })
                {
                    var value = preset;
                    var button = UiKit.TextButton("×" + UiKit.Number(preset), "PropSpeedPreset" + UiKit.Number(preset * 10, "0"), () => _commands.SetSpeed(id, value));
                    button.MinWidth = 40;
                    button.Padding = new Thickness(4, 2, 4, 2);
                    row.Children.Add(button);
                }
                void Commit()
                {
                    double value;
                    if (!_updating && UiKit.TryNumber(box.Text, out value))
                        _commands.SetSpeed(id, value);
                }
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Commit();
                        e.Handled = true;
                    }
                };
                box.LostFocus += (s, e) => Commit();
                _refreshers.Add(() =>
                {
                    var c = _session.Project.FindClip(id);
                    if (c != null && !box.IsKeyboardFocusWithin)
                        box.Text = UiKit.Number(c.Speed);
                });
                panel.Children.Add(row);
                var hint = UiKit.Label("속도를 바꾸면 길이가 바뀝니다(소리도 같이, 음높이는 그대로).", true);
                hint.TextWrapping = TextWrapping.Wrap;
                panel.Children.Add(hint);
            }
            else
            {
                panel.Children.Add(NumberRow("길이", "PropDuration", 0.1, 600, "초", id,
                    c => _session.Project.Rate.ToSeconds(c.Duration),
                    (c, v) => { }, (p, c, v) => TimelineEdits.TrimEnd(p, c, c.Start + Math.Max(1, p.Rate.ToFrames(v)))));
                var hint = UiKit.Label("사진·이미지·글자는 길이를 자유롭게 바꿀 수 있습니다(타임라인에서 끝을 끌어도 됨).", true);
                hint.TextWrapping = TextWrapping.Wrap;
                panel.Children.Add(hint);
            }
            return panel;
        }

        /// <summary>글 칸(여러 줄). 포커스가 빠질 때 바뀌었으면 고친다(그대로면 편집하지 않음).</summary>
        private TextBox ContentBox(string id, string automationId, string name)
        {
            var box = new TextBox { AcceptsReturn = true, MinHeight = 60, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetName(box, name);
            box.LostFocus += (s, e) =>
            {
                var text = box.Text;
                var current = _session.Project.FindClip(id);
                if (current != null && text != (current.Text ?? ""))
                    Edit(id, c => c.Text = text, null);
            };
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c != null && !box.IsKeyboardFocusWithin)
                    box.Text = c.Text ?? "";
            });
            return box;
        }

        private UIElement SubtitleTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(ContentBox(id, "PropSubtitleText", "자막 내용"));
            var hint = UiKit.Label("자막 트랙의 큐는 트랙 공통 모양(흰 글자·검은 윤곽선, 화면 아래 가운데)으로 입힙니다. 긴 줄은 화면 폭에 맞춰 저절로 나뉩니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(hint);
            return panel;
        }

        private UIElement TextTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(ContentBox(id, "PropText", "글자 내용"));
            panel.Children.Add(NumberRow("글자 크기", "PropFontSize", 8, 400, "px", id, c => c.FontSize, (c, v) => c.FontSize = Math.Max(4, v)));
            var colors = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetAutomationId(colors, "PropTextColor");
            AutomationProperties.SetName(colors, "글자 색");
            var presets = new[] { ("흰색", "#FFFFFF"), ("검정", "#000000"), ("노랑", "#FACC15"), ("빨강", "#EF4444"), ("파랑", "#3B82F6"), ("초록", "#22C55E") };
            foreach (var preset in presets)
                colors.Items.Add(preset.Item1);
            colors.SelectionChanged += (s, e) =>
            {
                if (_updating || colors.SelectedIndex < 0)
                    return;
                var hex = presets[colors.SelectedIndex].Item2;
                Edit(id, c =>
                {
                    c.TextColor = hex;
                    // 꾸민 글자는 채우기 색으로 그리므로 같이 바꾼다(단색)
                    if (c.Style != null)
                    {
                        c.Style.Fill ??= new TextFill();
                        c.Style.Fill.Kind = FillKind.Solid;
                        c.Style.Fill.Colors = new List<string> { hex };
                    }
                }, null);
            };
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c != null)
                    colors.SelectedIndex = Array.FindIndex(presets, p => string.Equals(p.Item2, c.TextColor, StringComparison.OrdinalIgnoreCase));
            });
            var row = new DockPanel();
            var label = UiKit.Label("글자 색");
            label.Width = 84;
            row.Children.Add(label);
            row.Children.Add(colors);
            panel.Children.Add(row);
            return panel;
        }

        /// <summary>
        /// [꾸미기] 꾸민 글자 스타일 — 맨 위 스타일 모음(미리 만든 스타일 단추), 글꼴 묶음(글꼴·굵게·기울임·자간·행간·정렬),
        /// 채우기 묶음(단색·선형·원형 그라데이션·없음, 색 1~4, 각도), 윤곽선 묶음(켜기·색·두께·이음), 그림자 묶음(켜기·색·거리·방향·흐림·불투명도),
        /// 맨 끝에 휘기 묶음(종류·정도).
        /// 스타일이 없는 글자(예전 방식)는 처음 고칠 때 지금 색을 이어받은 스타일을 만든다(그때부터 그리개로 그림).
        /// </summary>
        private UIElement StyleTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(Section("스타일 모음"));
            panel.Children.Add(PresetGallery(id));
            var warning = UiKit.Label("이 PC에 없는 글꼴이라 맑은 고딕으로 그립니다.", true);
            warning.TextWrapping = TextWrapping.Wrap;
            warning.Margin = new Thickness(0, 2, 0, 6);
            AutomationProperties.SetAutomationId(warning, "StyleFontWarning");
            panel.Children.Add(warning);
            panel.Children.Add(Section("글꼴"));

            var fonts = new ComboBox
            {
                ItemsSource = StyledTextRasterizer.Fonts,
                // 시스템 글꼴이 수백 개라 보이는 것만 만든다
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)))
            };
            AutomationProperties.SetAutomationId(fonts, "StyleFontBox");
            AutomationProperties.SetName(fonts, "글꼴");
            fonts.SelectionChanged += (s, e) =>
            {
                if (!_updating && fonts.SelectedItem is FontChoice choice)
                    Edit(id, c => StyleOf(c).FontFamily = choice.Name, null);
            };
            panel.Children.Add(Row("글꼴", fonts));

            var bold = new CheckBox { Content = "굵게", Margin = new Thickness(0, 0, 12, 0) };
            AutomationProperties.SetAutomationId(bold, "StyleBold");
            bold.Click += (s, e) => Edit(id, c => StyleOf(c).Bold = bold.IsChecked == true, null);
            var italic = new CheckBox { Content = "기울임" };
            AutomationProperties.SetAutomationId(italic, "StyleItalic");
            italic.Click += (s, e) => Edit(id, c => StyleOf(c).Italic = italic.IsChecked == true, null);
            var checks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            checks.Children.Add(bold);
            checks.Children.Add(italic);
            panel.Children.Add(Row("모양", checks));

            panel.Children.Add(NumberRow("자간", "StyleLetterSpacing", -200, 1000, "‰", id,
                c => c.Style?.LetterSpacing ?? 0, (c, v) => StyleOf(c).LetterSpacing = Math.Max(-200, Math.Min(1000, v))));
            panel.Children.Add(NumberRow("행간", "StyleLineSpacing", 50, 300, "%", id,
                c => (c.Style?.LineSpacing ?? 1.2) * 100, (c, v) => StyleOf(c).LineSpacing = Math.Max(0.5, Math.Min(3, v / 100))));

            // 순서는 TextAlign(Left·Center·Right)과 같다
            var align = new ComboBox();
            AutomationProperties.SetAutomationId(align, "StyleAlign");
            AutomationProperties.SetName(align, "정렬");
            foreach (var name in new[] { "왼쪽", "가운데", "오른쪽" })
                align.Items.Add(name);
            align.SelectionChanged += (s, e) =>
            {
                if (!_updating && align.SelectedIndex >= 0)
                {
                    var value = (TextAlign)align.SelectedIndex;
                    Edit(id, c => StyleOf(c).Align = value, null);
                }
            };
            panel.Children.Add(Row("정렬", align));

            // 채우기: 단색·선형·원형 그라데이션·없음 — 색은 1개(단색) 또는 2~4개(그라데이션), 선형은 각도
            panel.Children.Add(Section("채우기"));
            var fillKinds = new[] { FillKind.Solid, FillKind.Linear, FillKind.Radial, FillKind.None };
            var fillKind = new ComboBox();
            AutomationProperties.SetAutomationId(fillKind, "StyleFillKind");
            AutomationProperties.SetName(fillKind, "채우기");
            foreach (var name in new[] { "단색", "선형 그라데이션", "원형 그라데이션", "없음" })
                fillKind.Items.Add(name);
            fillKind.SelectionChanged += (s, e) =>
            {
                if (_updating || fillKind.SelectedIndex < 0)
                    return;
                var kind = fillKinds[fillKind.SelectedIndex];
                Edit(id, c =>
                {
                    var fill = FillOf(c);
                    fill.Kind = kind;
                    // 그라데이션은 색이 둘 이상이어야 보인다 — 하나뿐이면 첫 색과 대비되는 색을 더한다
                    if ((kind == FillKind.Linear || kind == FillKind.Radial) && fill.Colors.Count < 2)
                        fill.Colors.Add(IsLight(fill.Colors.FirstOrDefault()) ? "#3B82F6" : "#FFFFFF");
                }, null);
            };
            panel.Children.Add(Row("종류", fillKind));
            var colorRows = new List<ColorRow>();
            for (var i = 0; i < TextStyle.MaxFillColors; i++)
            {
                var index = i;
                var row = UiKit.ColorRow("색 " + (i + 1), "StyleFillColor" + (i + 1),
                    () =>
                    {
                        var c = _session.Project.FindClip(id);
                        var colors = c?.Style?.Fill?.Colors;
                        return colors != null && colors.Count > 0 ? colors[Math.Min(index, colors.Count - 1)] : c?.TextColor ?? "#FFFFFF";
                    },
                    hex => Edit(id, c =>
                    {
                        var fill = FillOf(c);
                        while (fill.Colors.Count <= index)
                            fill.Colors.Add(fill.Colors.LastOrDefault() ?? "#FFFFFF");
                        fill.Colors[index] = hex;
                        // 색 1은 [글자] 탭의 글자 색과 같은 값(두 탭이 어긋나지 않게)
                        if (index == 0)
                            c.TextColor = hex;
                    }, null));
                colorRows.Add(row);
                panel.Children.Add(row);
            }
            var addColor = UiKit.TextButton("색 추가", "StyleFillAdd", () => Edit(id, c =>
            {
                var fill = FillOf(c);
                if (fill.Colors.Count < TextStyle.MaxFillColors)
                    fill.Colors.Add(fill.Colors.LastOrDefault() ?? "#FFFFFF");
            }, null));
            var removeColor = UiKit.TextButton("색 빼기", "StyleFillRemove", () => Edit(id, c =>
            {
                var fill = FillOf(c);
                if (fill.Colors.Count > 2)
                    fill.Colors.RemoveAt(fill.Colors.Count - 1);
            }, null));
            var colorButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(80, 2, 0, 2) };
            colorButtons.Children.Add(addColor);
            colorButtons.Children.Add(removeColor);
            panel.Children.Add(colorButtons);
            var angleRow = NumberRow("각도", "StyleFillAngle", 0, 360, "°", id,
                c => c.Style?.Fill?.Angle ?? 0, (c, v) => FillOf(c).Angle = Math.Max(0, Math.Min(360, v)));
            panel.Children.Add(angleRow);
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var was = _updating;
                _updating = true;
                try
                {
                    var kind = c.Style?.Fill?.Kind ?? FillKind.Solid;
                    var count = c.Style?.Fill?.Colors?.Count ?? 1;
                    var gradient = kind == FillKind.Linear || kind == FillKind.Radial;
                    fillKind.SelectedIndex = Array.IndexOf(fillKinds, kind);
                    // 단색은 색 1, 그라데이션은 색 수만큼, 없음은 색 칸 없음
                    var shown = kind == FillKind.None ? 0 : gradient ? Math.Max(2, count) : 1;
                    for (var i = 0; i < colorRows.Count; i++)
                    {
                        colorRows[i].Visibility = i < shown ? Visibility.Visible : Visibility.Collapsed;
                        colorRows[i].Refresh();
                    }
                    colorButtons.Visibility = gradient ? Visibility.Visible : Visibility.Collapsed;
                    addColor.IsEnabled = count < TextStyle.MaxFillColors;
                    removeColor.IsEnabled = count > 2;
                    angleRow.IsEnabled = kind == FillKind.Linear;
                }
                finally
                {
                    _updating = was;
                }
            });

            // 윤곽선: 켜기 · 색 · 두께(0~50px) · 이음(둥글게·각지게·깎아서) — 색·두께·이음은 켰을 때만 고친다
            panel.Children.Add(Section("윤곽선"));
            var outlineOn = new CheckBox { Content = "윤곽선 그리기", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(outlineOn, "StyleOutlineOn");
            outlineOn.Click += (s, e) => Edit(id, c => OutlineOf(c).Enabled = outlineOn.IsChecked == true, null);
            panel.Children.Add(Row("윤곽선", outlineOn));
            var outlineColor = UiKit.ColorRow("색", "StyleOutlineColor",
                () => _session.Project.FindClip(id)?.Style?.Outline?.Color ?? "#000000",
                hex => Edit(id, c => OutlineOf(c).Color = hex, null));
            panel.Children.Add(outlineColor);
            var outlineWidth = NumberRow("두께", "StyleOutlineWidth", 0, 50, "px", id,
                c => c.Style?.Outline?.Width ?? 4, (c, v) => OutlineOf(c).Width = Math.Max(0, Math.Min(50, v)));
            panel.Children.Add(outlineWidth);
            // 순서는 아래 배열(둥글게·각지게·깎아서)
            var joins = new[] { LineJoinKind.Round, LineJoinKind.Miter, LineJoinKind.Bevel };
            var outlineJoin = new ComboBox();
            AutomationProperties.SetAutomationId(outlineJoin, "StyleOutlineJoin");
            AutomationProperties.SetName(outlineJoin, "이음");
            foreach (var name in new[] { "둥글게", "각지게", "깎아서" })
                outlineJoin.Items.Add(name);
            outlineJoin.SelectionChanged += (s, e) =>
            {
                if (_updating || outlineJoin.SelectedIndex < 0)
                    return;
                var join = joins[outlineJoin.SelectedIndex];
                Edit(id, c => OutlineOf(c).Join = join, null);
            };
            var joinRow = Row("이음", outlineJoin);
            panel.Children.Add(joinRow);
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var was = _updating;
                _updating = true;
                try
                {
                    var outline = c.Style?.Outline;
                    var on = outline?.Enabled == true;
                    outlineOn.IsChecked = on;
                    outlineColor.Refresh();
                    outlineJoin.SelectedIndex = Math.Max(0, Array.IndexOf(joins, outline?.Join ?? LineJoinKind.Round));
                    outlineColor.IsEnabled = on;
                    outlineWidth.IsEnabled = on;
                    joinRow.IsEnabled = on;
                }
                finally
                {
                    _updating = was;
                }
            });

            // 그림자: 켜기 · 색 · 거리(0~200px) · 방향(0~360°, 0° = 오른쪽, 시계 방향) · 흐림(0~100) · 불투명도(0~100%) — 켰을 때만 고친다
            panel.Children.Add(Section("그림자"));
            var shadowOn = new CheckBox { Content = "그림자 그리기", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(shadowOn, "StyleShadowOn");
            shadowOn.Click += (s, e) => Edit(id, c => ShadowOf(c).Enabled = shadowOn.IsChecked == true, null);
            panel.Children.Add(Row("그림자", shadowOn));
            var shadowColor = UiKit.ColorRow("색", "StyleShadowColor",
                () => _session.Project.FindClip(id)?.Style?.Shadow?.Color ?? "#000000",
                hex => Edit(id, c => ShadowOf(c).Color = hex, null));
            panel.Children.Add(shadowColor);
            var shadowRows = new[]
            {
                NumberRow("거리", "StyleShadowDistance", 0, 200, "px", id,
                    c => c.Style?.Shadow?.Distance ?? 6, (c, v) => ShadowOf(c).Distance = Math.Max(0, Math.Min(200, v))),
                NumberRow("방향", "StyleShadowAngle", 0, 360, "°", id,
                    c => c.Style?.Shadow?.Angle ?? 45, (c, v) => ShadowOf(c).Angle = Math.Max(0, Math.Min(360, v))),
                NumberRow("흐림", "StyleShadowBlur", 0, 100, "px", id,
                    c => c.Style?.Shadow?.Blur ?? 6, (c, v) => ShadowOf(c).Blur = Math.Max(0, Math.Min(100, v))),
                NumberRow("불투명도", "StyleShadowOpacity", 0, 100, "%", id,
                    c => (c.Style?.Shadow?.Opacity ?? 0.6) * 100, (c, v) => ShadowOf(c).Opacity = Math.Max(0, Math.Min(1, v / 100)))
            };
            foreach (var row in shadowRows)
                panel.Children.Add(row);
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var was = _updating;
                _updating = true;
                try
                {
                    var on = c.Style?.Shadow?.Enabled == true;
                    shadowOn.IsChecked = on;
                    shadowColor.Refresh();
                    shadowColor.IsEnabled = on;
                    foreach (var row in shadowRows)
                        row.IsEnabled = on;
                }
                finally
                {
                    _updating = was;
                }
            });

            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                // 값을 보이는 것만으로 편집이 일어나지 않게(스타일이 생기지 않게)
                var was = _updating;
                _updating = true;
                try
                {
                    var family = c.Style?.FontFamily ?? TextStyle.DefaultFont;
                    fonts.SelectedItem = StyledTextRasterizer.Fonts.FirstOrDefault(f => f.Names.Contains(family, StringComparer.OrdinalIgnoreCase));
                    warning.Visibility = StyledTextRasterizer.IsInstalled(family) ? Visibility.Collapsed : Visibility.Visible;
                    bold.IsChecked = c.Style?.Bold == true;
                    italic.IsChecked = c.Style?.Italic == true;
                    align.SelectedIndex = (int)(c.Style?.Align ?? TextAlign.Center);
                }
                finally
                {
                    _updating = was;
                }
            });

            // 휘기(맨 끝 묶음): 종류 · 정도(-100~100%) — 정도는 휘기가 있을 때만 고친다
            panel.Children.Add(Section("휘기"));
            // 보이는 순서는 아래 배열(enum 순서에 기대지 않는다)
            var warpKinds = new[] { WarpKind.None, WarpKind.ArchUp, WarpKind.ArchDown, WarpKind.Circle, WarpKind.Wave, WarpKind.Bulge, WarpKind.Pinch, WarpKind.Slant, WarpKind.Stairs };
            var warpKind = new ComboBox();
            AutomationProperties.SetAutomationId(warpKind, "StyleWarpKind");
            AutomationProperties.SetName(warpKind, "휘기");
            foreach (var name in new[] { "없음", "아치 위", "아치 아래", "원", "물결", "부풀리기", "오므리기", "기울이기", "계단" })
                warpKind.Items.Add(name);
            warpKind.SelectionChanged += (s, e) =>
            {
                if (_updating || warpKind.SelectedIndex < 0)
                    return;
                var kind = warpKinds[warpKind.SelectedIndex];
                Edit(id, c => WarpOf(c).Kind = kind, null);
            };
            panel.Children.Add(Row("종류", warpKind));
            var warpAmount = NumberRow("정도", "StyleWarpAmount", -100, 100, "%", id,
                c => c.Style?.Warp?.Amount ?? 50, (c, v) => WarpOf(c).Amount = Math.Max(-100, Math.Min(100, v)));
            panel.Children.Add(warpAmount);
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var was = _updating;
                _updating = true;
                try
                {
                    var kind = c.Style?.Warp?.Kind ?? WarpKind.None;
                    warpKind.SelectedIndex = Math.Max(0, Array.IndexOf(warpKinds, kind));
                    warpAmount.IsEnabled = kind != WarpKind.None;
                }
                finally
                {
                    _updating = was;
                }
            });
            return panel;
        }

        // 스타일 모음 단추: 어두운 바탕(흰 글자·속 빈 글자도 보이게)에 썸네일을 반 크기로 놓는다.
        // 썸네일 PNG의 가운데가 글자 가운데라 가운데 맞춤으로 두면 모두 같은 크기·위치로 보인다(넘치는 효과 여백은 잘림)
        private const double PresetTileWidth = 96;
        private const double PresetTileHeight = 44;
        private const double PresetThumbnailScale = 0.5;
        private static readonly System.Windows.Media.Brush PresetBackdrop = FrozenBrush(System.Windows.Media.Color.FromRgb(0x50, 0x50, 0x50));

        /// <summary>
        /// 스타일 모음: 미리 만든 스타일 단추(<see cref="TextStylePresets"/>). 누르면 글자 내용·크기는 그대로 두고 스타일을 통째로 바꾼다(실행 취소 한 단계).
        /// 단추 그림은 그리개로 그린 썸네일 — 탭이 처음 보일 때 배경에서 그리고, 다 그리기 전이나 실패하면 이름 글자만 보인다.
        /// </summary>
        private UIElement PresetGallery(string id)
        {
            var gallery = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetAutomationId(gallery, "StylePresets");
            var tiles = new List<(Border Frame, TextStyle Style, string ImageId)>();
            var presets = TextStylePresets.All;
            for (var i = 0; i < presets.Count; i++)
            {
                var (name, style) = presets[i];
                var fallback = new TextBlock
                {
                    Text = name,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                var frame = new Border { Width = PresetTileWidth, Height = PresetTileHeight, Background = PresetBackdrop, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = fallback };
                var button = new Button { Content = frame, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4), ToolTip = name };
                AutomationProperties.SetAutomationId(button, "StylePreset" + i);
                AutomationProperties.SetName(button, name);
                button.Click += (s, e) => Edit(id, c =>
                {
                    c.Style = style.Clone();
                    // 색 1은 [글자] 탭의 글자 색과 같은 값(두 탭이 어긋나지 않게)
                    c.TextColor = c.Style.Fill.Colors[0];
                }, null);
                tiles.Add((frame, style, "StylePreset" + i + "Image"));
                gallery.Children.Add(button);
            }
            // 썸네일은 이 탭이 처음 보일 때 그린다(클립을 고르기만 하거나 다른 탭을 볼 때는 그리기 스레드를 쓰지 않게)
            var requested = false;
            gallery.Loaded += (s, e) =>
            {
                if (requested || _services == null)
                    return;
                requested = true;
                foreach (var tile in tiles)
                    ShowThumbnail(tile.Frame, tile.Style, tile.ImageId);
            };
            return gallery;
        }

        /// <summary>썸네일을 단추 틀에 넣는다(이미 그려 둔 것이면 바로, 아니면 다 그린 뒤 UI 스레드에서). 그리개가 없거나 실패하면 이름 글자를 그대로 둔다.</summary>
        private void ShowThumbnail(Border frame, TextStyle style, string automationId)
        {
            void Show(System.Windows.Media.Imaging.BitmapSource picture)
            {
                var image = new Image
                {
                    Source = picture,
                    Width = picture.PixelWidth * PresetThumbnailScale,
                    Height = picture.PixelHeight * PresetThumbnailScale,
                    Stretch = System.Windows.Media.Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);
                AutomationProperties.SetAutomationId(image, automationId);
                frame.Child = image;
            }
            var task = _services.TextStylePreview(style);
            if (task.IsCompleted)
            {
                if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && task.Result != null)
                    Show(task.Result);
                return;
            }
            task.ContinueWith(t =>
            {
                if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || t.Result == null)
                    return;
                Dispatcher.BeginInvoke(new Action(() => Show(t.Result)));
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private static System.Windows.Media.Brush FrozenBrush(System.Windows.Media.Color color)
        {
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>고칠 클립의 스타일(없으면 지금 색을 이어받아 만든다).</summary>
        private static TextStyle StyleOf(Clip clip)
        {
            return clip.Style ??= TextStyle.FromLegacy(clip);
        }

        /// <summary>고칠 클립의 채우기(스타일·채우기가 없으면 만든다, 색이 없으면 흰색 하나).</summary>
        private static TextFill FillOf(Clip clip)
        {
            var fill = StyleOf(clip).Fill ??= new TextFill();
            fill.Colors ??= new List<string>();
            if (fill.Colors.Count == 0)
                fill.Colors.Add("#FFFFFF");
            return fill;
        }

        /// <summary>고칠 클립의 윤곽선(스타일·윤곽선이 없으면 만든다).</summary>
        private static TextOutline OutlineOf(Clip clip)
        {
            return StyleOf(clip).Outline ??= new TextOutline();
        }

        /// <summary>고칠 클립의 그림자(스타일·그림자가 없으면 만든다).</summary>
        private static TextShadow ShadowOf(Clip clip)
        {
            return StyleOf(clip).Shadow ??= new TextShadow();
        }

        /// <summary>밝은 색인지(사람 눈 밝기 0.6 넘음) — 그라데이션 둘째 색을 대비되게 고를 때.</summary>
        private static bool IsLight(string hex)
        {
            var color = UiKit.ParseColor(TextStyle.NormalizeColor(hex, "#FFFFFF"), System.Windows.Media.Colors.White);
            return (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255 > 0.6;
        }

        /// <summary>고칠 클립의 휘기(스타일·휘기가 없으면 만든다).</summary>
        private static TextWarpSetting WarpOf(Clip clip)
        {
            return StyleOf(clip).Warp ??= new TextWarpSetting();
        }

        // ---------- 공통 ----------

        private static TextBlock Section(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
        }

        /// <summary>이름(폭 84) · 컨트롤 한 줄.</summary>
        private static UIElement Row(string label, UIElement control)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var name = UiKit.Label(label);
            name.Width = 84;
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(control);
            return row;
        }

        private static TextBlock Title(string text)
        {
            return new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        private void Edit(string id, Action<Clip> change, string mergeKey)
        {
            _commands.EditClip(id, (p, c) => change(c), mergeKey);
        }

        /// <summary>이름 · 슬라이더 · 숫자 칸 · 단위 한 줄. edit이 있으면 그것으로(프로젝트 편집), 없으면 set으로 클립 값을 바꾼다.</summary>
        private UIElement NumberRow(string label, string automationId, double min, double max, string unit, string id,
            Func<Clip, double> get, Action<Clip, double> set, Action<Project, Clip, double> edit = null)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var name = UiKit.Label(label);
            name.Width = 84;
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var unitText = UiKit.Label(unit, true);
            unitText.Width = 26;
            unitText.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(unitText, Dock.Right);
            row.Children.Add(unitText);
            var box = new TextBox { Width = 62, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetName(box, label);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            var slider = new Slider { Minimum = min, Maximum = max, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(slider, automationId + "Slider");
            AutomationProperties.SetName(slider, label);
            row.Children.Add(slider);
            var mergeKey = automationId + ":" + id;
            // 칸에 마지막으로 보인 값 — 적은 글이 이것과 다를 때만 편집한다(포커스를 둔 채 실행 취소한 뒤 칸을 떠나도 옛 값을 다시 적용하지 않게)
            string shown = null;
            void Apply(double value, string merge)
            {
                if (edit != null)
                    _commands.EditClip(id, (p, c) => edit(p, c, value), merge);
                else
                    _commands.EditClip(id, (p, c) => set(c, value), merge);
            }
            slider.ValueChanged += (s, e) =>
            {
                if (!_updating)
                    Apply(Math.Round(e.NewValue, 2), mergeKey);
            };
            void Commit()
            {
                double value;
                if (_updating)
                    return;
                // 보이던 값 그대로면 편집하지 않는다(칸을 지나가기만 해도 실행 취소 단계·꾸민 글자 스타일이 생기지 않게), 꺼진·숨은 칸도
                if (box.Text.Trim() == shown || !box.IsEnabled || !box.IsVisible)
                    return;
                if (UiKit.TryNumber(box.Text, out value))
                {
                    Apply(value, null);
                    var applied = _session.Project.FindClip(id);
                    if (applied != null)
                        box.Text = shown = UiKit.Number(get(applied));
                }
                else
                {
                    Refresh();
                }
            }
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
            };
            box.LostFocus += (s, e) => Commit();
            void Refresh()
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var value = get(c);
                var was = _updating;
                _updating = true;
                try
                {
                    slider.Value = Math.Max(min, Math.Min(max, value));
                    // 포커스가 있어도 사용자가 고치지 않은 글이면 새 값으로(실행 취소가 칸에 보이게)
                    if (!box.IsKeyboardFocusWithin || box.Text == shown)
                        box.Text = UiKit.Number(value);
                    shown = UiKit.Number(value);
                }
                finally
                {
                    _updating = was;
                }
            }
            _refreshers.Add(Refresh);
            return row;
        }
    }
}
