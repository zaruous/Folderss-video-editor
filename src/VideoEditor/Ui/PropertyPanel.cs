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
    /// 아무것도 고르지 않으면 프로젝트 설정. 값을 바꾸면 바로 편집(실행 취소 가능 — 슬라이더를 끄는 동안은 한 단계로 합침).
    /// 편집 때마다 다시 만들지 않고 값만 갱신한다(끄는 슬라이더가 사라지지 않게).
    /// </summary>
    internal sealed class PropertyPanel : DockPanel
    {
        private readonly EditorSession _session;
        private readonly EditorCommands _commands;
        private readonly ContentControl _body = new ContentControl();
        private readonly List<Action> _refreshers = new List<Action>();
        private string _shownKey;
        private bool _updating;

        public PropertyPanel(EditorSession session, EditorCommands commands)
        {
            _session = session;
            _commands = commands;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
            Children.Add(scroll);
            session.SelectionChanged += (s, e) => Update();
            session.Changed += (s, e) => Update();
            Update();
        }

        private Clip Selected()
        {
            var clips = _session.SelectedClips;
            return clips.Count == 1 ? clips[0] : null;
        }

        private void Update()
        {
            var clips = _session.SelectedClips;
            var clip = clips.Count == 1 ? clips[0] : null;
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

        private UIElement ProjectView()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(Title("프로젝트"));
            var info = UiKit.Label("", true);
            info.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(info, "ProjectInfoText");
            _refreshers.Add(() =>
            {
                var p = _session.Project;
                info.Text = p.Width + "×" + p.Height + " · " + p.Rate + " fps · 길이 " + p.Rate.Timecode(p.Duration)
                            + "\n클립 " + p.AllClips.Count() + "개 · 동영상 트랙 " + p.VideoTracks.Count + " · 오디오 트랙 " + p.AudioTracks.Count;
            });
            panel.Children.Add(info);
            var settings = UiKit.TextButton("프로젝트 설정…", "ProjectSettingsButton", _commands.ProjectSettings);
            settings.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(settings);
            panel.Children.Add(Note("클립을 고르면 위치·크기·소리·속도를 바꿀 수 있습니다."));
            _refreshers[0]();
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
                timing.Text = Kind(c) + " · " + rate.Timecode(c.Start) + " ~ " + rate.Timecode(c.End) + " (" + rate.Timecode(c.Duration) + ")";
            });
            DockPanel.SetDock(head, Dock.Top);
            panel.Children.Add(head);

            var tabs = new TabControl { Margin = new Thickness(6, 0, 6, 6) };
            AutomationProperties.SetAutomationId(tabs, "PropertyTabs");
            UiKit.StyleTabs(tabs);
            if (clip.Kind == ClipKind.Text)
                tabs.Items.Add(new TabItem { Header = "글자", Content = TextTab(id) });
            if (clip.IsVisual)
                tabs.Items.Add(new TabItem { Header = "화면", Content = VisualTab(id) });
            if (clip.Kind == ClipKind.Media && clip.HasAudio)
                tabs.Items.Add(new TabItem { Header = "소리", Content = AudioTab(id) });
            tabs.Items.Add(new TabItem { Header = clip.HasSourceLimit ? "속도" : "길이", Content = TimeTab(id, clip.HasSourceLimit) });
            tabs.SelectedIndex = 0;
            panel.Children.Add(tabs);
            foreach (var refresh in _refreshers)
                refresh();
            return panel;
        }

        private static string Kind(Clip clip)
        {
            if (clip.Kind == ClipKind.Text)
                return "글자";
            if (clip.AudioOnly)
                return "분리한 오디오";
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
            panel.Children.Add(NumberRow("회전", "PropRotation", -360, 360, "°", id, c => c.Rotation, (c, v) => c.Rotation = v));
            panel.Children.Add(NumberRow("불투명도", "PropOpacity", 0, 100, "%", id, c => c.Opacity * 100, (c, v) => c.Opacity = Math.Max(0, Math.Min(1, v / 100))));
            panel.Children.Add(NumberRow("크로스페이드", "PropTransition", 0, 10, "초", id,
                c => _session.Project.Rate.ToSeconds(c.TransitionIn),
                (c, v) => c.TransitionIn = Math.Min(c.Duration, _session.Project.Rate.ToFrames(Math.Max(0, v)))));
            var hint = UiKit.Label("크로스페이드: 같은 트랙의 바로 앞 클립에서 겹쳐 바뀝니다(앞 클립 끝이 이 길이만큼 이어짐).", true);
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
                detached.Text = c != null && c.AudioDetached ? "이 클립의 소리는 오디오 트랙으로 분리되어 여기서는 나지 않습니다." : "";
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

        private UIElement TextTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            var box = new TextBox { AcceptsReturn = true, MinHeight = 60, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetAutomationId(box, "PropText");
            AutomationProperties.SetName(box, "글자 내용");
            box.LostFocus += (s, e) =>
            {
                var text = box.Text;
                Edit(id, c => c.Text = text, null);
            };
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c != null && !box.IsKeyboardFocusWithin)
                    box.Text = c.Text ?? "";
            });
            panel.Children.Add(box);
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
                Edit(id, c => c.TextColor = hex, null);
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

        // ---------- 공통 ----------

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
                if (UiKit.TryNumber(box.Text, out value))
                    Apply(value, null);
                else
                    Refresh();
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
                    if (!box.IsKeyboardFocusWithin)
                        box.Text = UiKit.Number(value);
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
