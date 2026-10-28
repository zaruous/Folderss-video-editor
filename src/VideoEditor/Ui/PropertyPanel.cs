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
    /// 속성 패널(오른쪽 위): 고른 클립의 [화면] 위치·크기·회전·불투명도·크로스페이드, [효과] 동영상 효과(동영상·사진), [소리] 음량·페이드, [속도·길이], [글자].
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
        // 다시 만드는 줄(효과 탭)은 갱신을 자기 목록에 모은다 — 갱신하는 도중에 _refreshers를 바꾸지 않게(null이면 _refreshers)
        private List<Action> _collect;
        // 지금 보이는 클립의 [효과] 탭(동영상·사진 클립이 아니면 null)
        private TabItem _effectsTab;
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
            // 효과 탭에서 동영상 효과를 넣으면 그 클립의 [효과] 탭을 연다(값을 바로 조절하게)
            commands.VideoEffectsApplied += ids =>
            {
                if (_effectsTab != null && Selected() is Clip clip && ids.Contains(clip.Id))
                    _effectsTab.IsSelected = true;
            };
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
            _effectsTab = null;
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
                if (clip.Kind == ClipKind.Art)
                {
                    tabs.Items.Add(new TabItem { Header = "모양", Content = ShapeTab(id, clip.Art?.Kind ?? ArtKind.Rectangle) });
                    var styleTab = new TabItem { Header = "꾸미기", Content = StyleTab(id, true) };
                    AutomationProperties.SetAutomationId(styleTab, "StyleTab");
                    tabs.Items.Add(styleTab);
                }
                if (clip.Kind == ClipKind.Text)
                {
                    tabs.Items.Add(new TabItem { Header = "글자", Content = TextTab(id) });
                    var styleTab = new TabItem { Header = "꾸미기", Content = StyleTab(id) };
                    AutomationProperties.SetAutomationId(styleTab, "StyleTab");
                    tabs.Items.Add(styleTab);
                }
                if (clip.IsVisual)
                    tabs.Items.Add(new TabItem { Header = "화면", Content = VisualTab(id) });
                // 동영상 효과(동영상·사진 클립만 — 글자 클립에는 넣지 않음)
                if (VideoEffects.CanHave(clip))
                {
                    _effectsTab = new TabItem { Header = "효과", Content = EffectsTab(id) };
                    AutomationProperties.SetAutomationId(_effectsTab, "ClipEffectsTab");
                    tabs.Items.Add(_effectsTab);
                }
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
            if (clip.Kind == ClipKind.Art)
                return "요소";
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
            // 전환 효과 종류(길이는 아래 줄) — [전환] 탭에서 고르거나 끌어 놓아도 된다
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

        /// <summary>
        /// [효과] 이 클립의 동영상 효과(동영상·사진 클립): 효과마다 이름·[빼기], 매개변수 줄(숫자는 슬라이더 — 끄는 동안 실행 취소 한 단계, 색은 팔레트·16진), [사람에게만].
        /// "사람에게만"을 켠 효과가 있으면 아래에 원본의 인물 마스크 상태와 [인물 마스크 만들기]. 효과를 더하거나 빼면(실행 취소 포함) 줄을 다시 만들고, 값만 바뀌면 값만 갱신한다.
        /// 효과를 더하는 곳은 미디어 패널의 [효과] 탭(두 번 누르기·끌어 놓기).
        /// </summary>
        private UIElement EffectsTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            var empty = UiKit.Label("효과가 없습니다. 왼쪽 [효과] 탭의 동영상 효과를 두 번 누르거나 이 클립으로 끌어 놓으세요.", true);
            empty.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(empty, "EffectEmpty");
            panel.Children.Add(empty);
            var rows = new StackPanel();
            panel.Children.Add(rows);

            // 인물 마스크: 클립 원본 하나에 하나 — "사람에게만" 효과가 있을 때만, 원본마다 한 번 디스크에서 확인하고 기억한다
            // (슬라이더를 끌 때마다 확인하면 원본이 끊긴 네트워크 폴더에 있을 때 편집이 멈춘다). [인물 마스크 만들기] 뒤에는 다시 확인한다
            var maskBox = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var maskStatus = UiKit.Label("", true);
            maskStatus.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(maskStatus, "EffectMaskStatus");
            maskBox.Children.Add(Row("인물 마스크", maskStatus));
            var maskHint = UiKit.Label("마스크가 없으면 효과를 클립 전체에 칠합니다. 만들고 나면 사람 자리(마스크의 흰 곳)에만 칠합니다.", true);
            maskHint.TextWrapping = TextWrapping.Wrap;
            maskHint.Margin = new Thickness(0, 2, 0, 4);
            maskBox.Children.Add(maskHint);
            string checkedPath = null;
            var ready = false;
            void ShowMask(bool again)
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                if (again || !string.Equals(checkedPath, c.Path, StringComparison.OrdinalIgnoreCase))
                {
                    checkedPath = c.Path;
                    try
                    {
                        ready = !string.IsNullOrEmpty(VideoEditor.Media.RenderEnvironment.PersonMasks?.Find(c.Path));
                    }
                    catch (Exception)
                    {
                        // 마스크를 확인하지 못하면 없는 것으로 보인다(그리기도 같음)
                        ready = false;
                    }
                }
                maskStatus.Text = ready ? "인물 마스크 준비됨" : "인물 마스크가 없습니다";
            }
            Button build = null;
            build = UiKit.TextButton("인물 마스크 만들기", "EffectMaskBuild", async () =>
            {
                build.IsEnabled = false;
                try
                {
                    await _commands.BuildPersonMask(id);
                }
                catch (Exception ex)
                {
                    _commands.Status("인물 마스크를 만들지 못했습니다: " + ex.Message);
                }
                finally
                {
                    build.IsEnabled = true;
                    ShowMask(true);
                }
            });
            build.HorizontalAlignment = HorizontalAlignment.Left;
            build.Margin = new Thickness(0);
            maskBox.Children.Add(build);
            panel.Children.Add(maskBox);

            var hint = UiKit.Label("효과는 클립 그림 위에 차례로 그려져(아래 효과가 위) 클립과 함께 옮기고 돌고 사라집니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(hint);

            var local = new List<Action>();
            string shown = null;
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var effects = c.Effects ?? new List<ClipEffect>();
                // 효과를 더하거나 빼거나 순서가 바뀐 때만 줄을 다시 만든다(값만 바뀌면 그대로 — 끄는 슬라이더가 사라지지 않게)
                var signature = string.Join("|", effects.Select(e => e?.Id));
                if (signature != shown)
                {
                    shown = signature;
                    local.Clear();
                    rows.Children.Clear();
                    _collect = local;
                    try
                    {
                        for (var i = 0; i < effects.Count; i++)
                            rows.Children.Add(EffectRows(id, i, effects.Count, effects[i]?.Id, local));
                    }
                    finally
                    {
                        _collect = null;
                    }
                }
                var was = _updating;
                _updating = true;
                try
                {
                    foreach (var refresh in local)
                        refresh();
                }
                finally
                {
                    _updating = was;
                }
                empty.Visibility = effects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                var personal = effects.Any(e => e != null && e.PersonOnly && VideoEffects.Find(e.Id)?.AllowsPersonOnly == true);
                maskBox.Visibility = personal ? Visibility.Visible : Visibility.Collapsed;
                if (personal)
                    ShowMask(false);
            });
            return panel;
        }

        /// <summary>효과 하나의 줄들(count개 중 index번째, effectId — 그 자리의 효과가 바뀌었으면 고치지 않는다). 갱신은 local에 모은다.</summary>
        private UIElement EffectRows(string id, int index, int count, string effectId, List<Action> local)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var def = VideoEffects.Find(effectId);
            ClipEffect EffectOf(Clip c)
            {
                return c?.Effects != null && index < c.Effects.Count && c.Effects[index]?.Id == effectId ? c.Effects[index] : null;
            }
            void Change(Action<ClipEffect> change, string mergeKey)
            {
                Edit(id, c =>
                {
                    var effect = EffectOf(c);
                    if (effect != null)
                        change(effect);
                }, mergeKey);
            }
            var head = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            // [위로]·[아래로]·[빼기] — 이름이 다 보일 자리가 없으면(좁은 패널) 다음 줄 오른쪽으로 내린다
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var remove = UiKit.TextButton("빼기", "EffectRemove" + index, () => Edit(id, c =>
            {
                if (EffectOf(c) != null)
                    VideoEffects.Remove(c, index);
            }, null));
            remove.MinWidth = 50;
            remove.Padding = new Thickness(6, 1, 6, 1);
            // 순서 바꾸기(뒤 효과가 앞 효과 위에 그린다) — 실행 취소 한 단계
            foreach (var (text, to) in new[] { ("아래로", index + 1), ("위로", index - 1) })
            {
                var target = to;
                var move = UiKit.TextButton(text, "EffectMove" + (target > index ? "Down" : "Up") + index, () => _commands.EditClip(id, (p, c) =>
                {
                    if (EffectOf(c) != null)
                        VideoEffects.Move(p, c, index, target);
                }, null));
                move.MinWidth = 0;
                move.Padding = new Thickness(6, 1, 6, 1);
                move.IsEnabled = target >= 0 && target < count;
                buttons.Children.Insert(0, move);
            }
            buttons.Children.Add(remove);
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            var title = Section(def?.Name ?? "알 수 없는 효과(" + effectId + ")");
            title.ToolTip = def?.Description;
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(title);
            // 이름 옆에 작게 묶음(효과 탭의 묶음 제목과 같은 이름) — 어느 갈래의 효과인지
            var group = UiKit.Label(def?.Group ?? "", true);
            group.FontSize = 11;
            group.Margin = new Thickness(6, 1, 4, 0);
            group.ToolTip = def?.Description;
            AutomationProperties.SetAutomationId(group, "EffectGroup" + index);
            head.Children.Add(group);
            box.Children.Add(head);
            var lower = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Collapsed };
            box.Children.Add(lower);
            head.SizeChanged += (s, e) => FoldRight(head, lower, buttons, TextWidth(title) + TextWidth(group));
            if (def == null)
            {
                var note = UiKit.Label("이 판에서 그리지 못하는 효과입니다(새 판에서 만든 효과일 수 있습니다). 그대로 두면 저장할 때 남고, 빼도 됩니다.", true);
                note.TextWrapping = TextWrapping.Wrap;
                box.Children.Add(note);
                return box;
            }
            var prefix = "Effect" + index + "_";
            foreach (var param in def.Params)
            {
                var key = param.Key;
                if (param.Kind == EffectParamKind.Color)
                {
                    var color = UiKit.ColorRow(param.Name, prefix + key,
                        () => VideoEffects.ColorOf(EffectOf(_session.Project.FindClip(id)), def),
                        hex => Change(e => VideoEffects.SetColor(e, hex), null));
                    color.ToolTip = param.Hint;
                    local.Add(color.Refresh);
                    box.Children.Add(color);
                    continue;
                }
                if (param.Kind == EffectParamKind.Choice)
                {
                    var choice = new ComboBox { ToolTip = param.Hint };
                    AutomationProperties.SetAutomationId(choice, prefix + key);
                    AutomationProperties.SetName(choice, param.Name);
                    foreach (var option in param.Options)
                        choice.Items.Add(option);
                    choice.SelectionChanged += (s, e) =>
                    {
                        if (_updating || choice.SelectedIndex < 0)
                            return;
                        var value = choice.SelectedIndex;
                        if (VideoEffects.Number(EffectOf(_session.Project.FindClip(id)), def, key) != value)
                            Change(effect => VideoEffects.SetNumber(effect, key, value), null);
                    };
                    local.Add(() => choice.SelectedIndex = (int)VideoEffects.Number(EffectOf(_session.Project.FindClip(id)), def, key));
                    box.Children.Add(Row(param.Name, choice));
                    continue;
                }
                var row = NumberRow(param.Name, prefix + key, param.Min, param.Max, param.Unit, id,
                    c => VideoEffects.Number(EffectOf(c), def, key),
                    (c, v) =>
                    {
                        var effect = EffectOf(c);
                        if (effect != null)
                            VideoEffects.SetNumber(effect, key, v);
                    });
                if (row is FrameworkElement element)
                    element.ToolTip = param.Hint;
                box.Children.Add(row);
            }
            // 그림 전체를 움직이는 효과(흔들림·줌)는 사람 자리만 따로 움직일 수 없어 [사람에게만]이 없다
            if (!def.AllowsPersonOnly)
                return box;
            var person = new CheckBox { Content = "사람에게만", VerticalAlignment = VerticalAlignment.Center, ToolTip = "원본의 인물 마스크가 있으면 사람 자리에만 칠합니다(없으면 클립 전체)." };
            AutomationProperties.SetAutomationId(person, index == 0 ? "EffectPersonOnly" : "EffectPersonOnly" + index);
            person.Click += (s, e) =>
            {
                var on = person.IsChecked == true;
                Change(effect => effect.PersonOnly = on, null);
            };
            local.Add(() => person.IsChecked = EffectOf(_session.Project.FindClip(id))?.PersonOnly == true);
            box.Children.Add(Row("칠할 곳", person));
            return box;
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
            panel.Children.Add(TextBoxRows(id));
            return panel;
        }

        /// <summary>
        /// [글자] 글자 상자: 너비(줄 나눔)·높이(0~4000px, 0 = 자동)·세로 정렬(위·가운데·아래 — 높이를 정했을 때만)과 넘침 안내.
        /// 클립의 값이라 스타일을 바꿔도 남는다. 미리 보기 틀의 옆 손잡이로 바꾼 값도 여기에 보인다.
        /// </summary>
        private UIElement TextBoxRows(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(NumberRow("상자 너비", "PropTextBoxWidth", 0, ClipLayout.MaxTextBox, "px", id, c => c.TextBoxWidth, (c, v) => c.TextBoxWidth = ClipLayout.TextBoxLength(v)));
            panel.Children.Add(NumberRow("상자 높이", "PropTextBoxHeight", 0, ClipLayout.MaxTextBox, "px", id, c => c.TextBoxHeight, (c, v) => c.TextBoxHeight = ClipLayout.TextBoxLength(v)));
            // 보이는 순서는 아래 배열(enum 순서에 기대지 않는다)
            var verticals = new[] { TextVerticalAlign.Top, TextVerticalAlign.Middle, TextVerticalAlign.Bottom };
            var vertical = new ComboBox();
            AutomationProperties.SetAutomationId(vertical, "PropTextVerticalAlign");
            AutomationProperties.SetName(vertical, "세로 정렬");
            foreach (var name in new[] { "위", "가운데", "아래" })
                vertical.Items.Add(name);
            vertical.SelectionChanged += (s, e) =>
            {
                if (_updating || vertical.SelectedIndex < 0)
                    return;
                var value = verticals[vertical.SelectedIndex];
                var current = _session.Project.FindClip(id);
                if (current != null && current.TextVerticalAlign != value)
                    Edit(id, c => c.TextVerticalAlign = value, null);
            };
            var verticalRow = Row("세로 정렬", vertical);
            panel.Children.Add(verticalRow);
            var overflow = UiKit.Label("글이 상자보다 깁니다", true);
            overflow.TextWrapping = TextWrapping.Wrap;
            overflow.Margin = new Thickness(84, 0, 0, 4);
            AutomationProperties.SetAutomationId(overflow, "PropTextOverflow");
            panel.Children.Add(overflow);
            var hint = UiKit.Label("상자 0 = 자동(너비: 줄을 나누지 않음, 높이: 글 높이). 너비를 정하면 낱말 단위로 줄을 나눕니다. 미리 보기에서 글자를 누르고 틀의 옆 손잡이를 끌어도 됩니다(두 번 누르면 자동).", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 2, 0, 4);
            panel.Children.Add(hint);
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
                    var index = Array.IndexOf(verticals, c.TextVerticalAlign);
                    vertical.SelectedIndex = index >= 0 ? index : 1;
                    verticalRow.IsEnabled = c.TextBoxHeight > 0;
                    // 넘침: 그리개와 같은 배치로 잰 글 높이가 상자보다 높으면(긴 글은 drawtext라 상자를 쓰지 않는다)
                    var longer = c.TextBoxHeight > 0 && (c.Text ?? "").Length <= RenderGraph.MaxStyledTextLength
                        && StyledTextRasterizer.MeasureClip(c, c.TextBoxWidth, c.TextBoxHeight).TextHeight > c.TextBoxHeight + 0.5;
                    overflow.Visibility = longer ? Visibility.Visible : Visibility.Collapsed;
                }
                finally
                {
                    _updating = was;
                }
            });
            return panel;
        }

        /// <summary>
        /// [꾸미기] 꾸민 글자 스타일 — 맨 위 스타일 모음(미리 만든 스타일 단추)과 내 스타일(저장한 스타일 단추·[내 스타일로 저장]), 글꼴 묶음(글꼴·굵게·기울임·자간·행간·정렬),
        /// 채우기 묶음(단색·선형·원형 그라데이션·없음, 색 1~4, 각도), 윤곽선 묶음(켜기·색·두께·이음), 그림자 묶음(켜기·색·거리·방향·흐림·불투명도),
        /// 광선 묶음(켜기·색·크기·불투명도), 반사 묶음(켜기·크기·간격·불투명도), 맨 끝에 휘기 묶음(종류·정도).
        /// 스타일이 없는 글자(예전 방식)는 처음 고칠 때 지금 색을 이어받은 스타일을 만든다(그때부터 그리개로 그림).
        /// </summary>
        private UIElement StyleTab(string id, bool art = false)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(Section("스타일 모음"));
            // 아트 요소는 썸네일을 글자 대신 도형 견본(둥근 사각형)으로 — 모양에 쓰는 꾸미기만 보인다
            var sample = art ? ArtShape.StyleSample() : null;
            panel.Children.Add(PresetGallery(id, sample));
            panel.Children.Add(MyStyles(id, sample));
            // 아트 요소는 글꼴·휘기 묶음을 숨긴다(모양에는 쓰지 않음 — 스타일은 글자와 같은 것을 쓴다)
            var fontFrom = panel.Children.Count;
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

            var fontTo = panel.Children.Count;
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

            // 광선: 켜기 · 색 · 크기(0~100px) · 불투명도(0~100%) — 켰을 때만 고친다
            panel.Children.Add(Section("광선"));
            var glowOn = new CheckBox { Content = "광선 그리기", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(glowOn, "StyleGlowOn");
            glowOn.Click += (s, e) => Edit(id, c => GlowOf(c).Enabled = glowOn.IsChecked == true, null);
            panel.Children.Add(Row("광선", glowOn));
            var glowColor = UiKit.ColorRow("색", "StyleGlowColor",
                () => _session.Project.FindClip(id)?.Style?.Glow?.Color ?? "#FFD54F",
                hex => Edit(id, c => GlowOf(c).Color = hex, null));
            panel.Children.Add(glowColor);
            var glowRows = new[]
            {
                NumberRow("크기", "StyleGlowSize", 0, 100, "px", id,
                    c => c.Style?.Glow?.Size ?? 12, (c, v) => GlowOf(c).Size = Math.Max(0, Math.Min(100, v))),
                NumberRow("불투명도", "StyleGlowOpacity", 0, 100, "%", id,
                    c => (c.Style?.Glow?.Opacity ?? 0.8) * 100, (c, v) => GlowOf(c).Opacity = Math.Max(0, Math.Min(1, v / 100)))
            };
            foreach (var row in glowRows)
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
                    var on = c.Style?.Glow?.Enabled == true;
                    glowOn.IsChecked = on;
                    glowColor.Refresh();
                    glowColor.IsEnabled = on;
                    foreach (var row in glowRows)
                        row.IsEnabled = on;
                }
                finally
                {
                    _updating = was;
                }
            });

            // 반사: 켜기 · 크기(글자 높이의 10~100%) · 간격(0~100px) · 불투명도(0~100%) — 켰을 때만 고친다
            panel.Children.Add(Section("반사"));
            var reflectionOn = new CheckBox { Content = "반사 그리기", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(reflectionOn, "StyleReflectionOn");
            reflectionOn.Click += (s, e) => Edit(id, c => ReflectionOf(c).Enabled = reflectionOn.IsChecked == true, null);
            panel.Children.Add(Row("반사", reflectionOn));
            var reflectionRows = new[]
            {
                NumberRow("크기", "StyleReflectionSize", 10, 100, "%", id,
                    c => c.Style?.Reflection?.Size ?? 50, (c, v) => ReflectionOf(c).Size = Math.Max(10, Math.Min(100, v))),
                NumberRow("간격", "StyleReflectionGap", 0, 100, "px", id,
                    c => c.Style?.Reflection?.Gap ?? 4, (c, v) => ReflectionOf(c).Gap = Math.Max(0, Math.Min(100, v))),
                NumberRow("불투명도", "StyleReflectionOpacity", 0, 100, "%", id,
                    c => (c.Style?.Reflection?.Opacity ?? 0.4) * 100, (c, v) => ReflectionOf(c).Opacity = Math.Max(0, Math.Min(1, v / 100)))
            };
            foreach (var row in reflectionRows)
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
                    var on = c.Style?.Reflection?.Enabled == true;
                    reflectionOn.IsChecked = on;
                    foreach (var row in reflectionRows)
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
                    warning.Visibility = art || StyledTextRasterizer.IsInstalled(family) ? Visibility.Collapsed : Visibility.Visible;
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
            var warpFrom = panel.Children.Count;
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
            if (art)
            {
                for (var i = fontFrom; i < panel.Children.Count; i++)
                {
                    if (i < fontTo || i >= warpFrom)
                        panel.Children[i].Visibility = Visibility.Collapsed;
                }
            }
            return panel;
        }

        /// <summary>
        /// [모양] 아트 요소의 상자(너비·높이)와 종류별 값 — 사각형: 모서리 반경, 다각형·별: 변·뿔 수, 별: 안쪽 비율, 선·화살표: 두께·점선, 화살표: 머리 크기.
        /// 길이는 프로젝트 픽셀(크기 배율 전). 미리 보기 틀의 노란 점(모양 손잡이)으로도 바꾼다 — 모서리 반경, 선·화살표 양 끝, 별 안쪽.
        /// </summary>
        private UIElement ShapeTab(string id, ArtKind kind)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            ArtShape Shape(Clip c) => c.Art ??= ArtShape.Default(kind);
            var line = kind == ArtKind.Line || kind == ArtKind.Arrow;
            panel.Children.Add(NumberRow("너비", "ArtWidth", 1, 4000, "px", id, c => c.Art?.Width ?? 400, (c, v) => Shape(c).Width = Math.Max(1, Math.Min(ClipLayout.MaxTextBox, v))));
            panel.Children.Add(NumberRow("높이", "ArtHeight", 1, 4000, "px", id, c => c.Art?.Height ?? 300, (c, v) => Shape(c).Height = Math.Max(1, Math.Min(ClipLayout.MaxTextBox, v))));
            if (kind == ArtKind.Rectangle)
                panel.Children.Add(NumberRow("모서리", "ArtCornerRadius", 0, 1000, "px", id, c => c.Art?.CornerRadius ?? 0, (c, v) => Shape(c).CornerRadius = Math.Max(0, v)));
            if (kind == ArtKind.Polygon || kind == ArtKind.Star)
                panel.Children.Add(NumberRow(kind == ArtKind.Star ? "뿔 수" : "변 수", "ArtSides", 3, 12, "", id, c => c.Art?.Sides ?? 5,
                    (c, v) => Shape(c).Sides = (int)Math.Round(Math.Max(3, Math.Min(12, v)))));
            if (kind == ArtKind.Star)
                panel.Children.Add(NumberRow("안쪽", "ArtInnerRatio", 10, 95, "%", id, c => (c.Art?.InnerRatio ?? 0.5) * 100,
                    (c, v) => Shape(c).InnerRatio = Math.Max(0.1, Math.Min(0.95, v / 100))));
            if (line)
            {
                panel.Children.Add(NumberRow("두께", "ArtThickness", 1, 200, "px", id, c => c.Art?.Thickness ?? 12,
                    (c, v) => Shape(c).Thickness = Math.Max(1, Math.Min(ArtShape.MaxThickness, v))));
                var dashed = new CheckBox { Content = "점선" };
                AutomationProperties.SetAutomationId(dashed, "ArtDashed");
                dashed.Click += (s, e) => Edit(id, c => Shape(c).Dashed = dashed.IsChecked == true, null);
                panel.Children.Add(Row("선", dashed));
                _refreshers.Add(() => dashed.IsChecked = _session.Project.FindClip(id)?.Art?.Dashed == true);
            }
            if (kind == ArtKind.Arrow)
                panel.Children.Add(NumberRow("머리", "ArtHeadSize", 1.5, 10, "×", id, c => c.Art?.HeadSize ?? 3, (c, v) => Shape(c).HeadSize = Math.Max(1.5, Math.Min(10, v))));
            var hint = UiKit.Label(line ? "선·화살표는 상자 가운데 높이에서 왼쪽 끝 → 오른쪽 끝으로 긋습니다. 미리 보기에서 양 끝의 노란 점을 끌면 길이·방향이 바뀌고 반대쪽 끝은 그대로입니다([화면] 탭의 회전으로도)."
                : kind == ArtKind.Rectangle || kind == ArtKind.Star ? "미리 보기 틀의 노란 점을 끌어 " + (kind == ArtKind.Star ? "안쪽 비율" : "모서리 반경") + "을 바꿀 수도 있습니다. 색·윤곽선·그림자는 [꾸미기] 탭에서 고칩니다."
                : "색·윤곽선·그림자는 [꾸미기] 탭에서 고칩니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 4, 0, 4);
            panel.Children.Add(hint);
            return panel;
        }

        /// <summary>
        /// 스타일 모음: 미리 만든 스타일 단추(<see cref="TextStylePresets"/>). 누르면 글자 내용·크기는 그대로 두고 스타일을 통째로 바꾼다(실행 취소 한 단계).
        /// 단추 그림은 그리개로 그린 썸네일 — 탭이 처음 보일 때 배경에서 그리고, 다 그리기 전이나 실패하면 이름 글자만 보인다. sample이 있으면(아트 요소) 그 도형 견본으로 그린다.
        /// </summary>
        private UIElement PresetGallery(string id, ArtShape sample)
        {
            var gallery = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetAutomationId(gallery, "StylePresets");
            var tiles = new List<(Border Frame, TextStyle Style, string ImageId)>();
            var presets = TextStylePresets.All;
            for (var i = 0; i < presets.Count; i++)
            {
                var (name, style) = presets[i];
                var (button, frame) = StyleTiles.Tile(name, "StylePreset" + i);
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
                    StyleTiles.ShowThumbnail(_services, tile.Frame, tile.Style, tile.ImageId, sample);
            };
            return gallery;
        }

        /// <summary>
        /// 내 스타일(스타일 모음 아래): 저장한 스타일 단추(설정 textStyles — <see cref="TextStyleLibrary"/>)와 [내 스타일로 저장]. 단추는 스타일 모음과 같은 틀·썸네일이고
        /// 누르면 스타일 모음처럼 글자 내용·크기는 그대로 두고 스타일을 통째로 바꾼다(실행 취소 한 단계). 오른쪽 클릭 [지우기]로 지운다 —
        /// 저장·지우기는 프로젝트가 아닌 플러그인 설정이라 실행 취소에 남지 않는다. 탭이 보일 때마다 설정을 다시 읽는다(다른 편집 창에서 저장·지운 것도 보이게).
        /// </summary>
        private UIElement MyStyles(string id, ArtShape sample)
        {
            var box = new StackPanel();
            box.Children.Add(Section("내 스타일"));
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetAutomationId(row, "StyleMine");
            box.Children.Add(row);
            var hint = UiKit.Label("지금 스타일을 [내 스타일로 저장]하면 여기에 단추로 남아 다른 글자에도 한 번에 입힐 수 있습니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, 4);
            AutomationProperties.SetAutomationId(hint, "StyleMineHint");
            box.Children.Add(hint);
            // 썸네일은 이 탭이 보인 뒤부터 그린다(스타일 모음과 같음)
            var shown = false;
            void Rebuild()
            {
                row.Children.Clear();
                var styles = _services?.TextStyles() ?? new List<(string Name, TextStyle Style)>();
                for (var i = 0; i < styles.Count; i++)
                {
                    var (name, style) = styles[i];
                    var (button, frame) = StyleTiles.Tile(name, "StyleMine" + i);
                    button.Click += (s, e) => Edit(id, c =>
                    {
                        c.Style = style.Clone();
                        // 색 1은 [글자] 탭의 글자 색과 같은 값(두 탭이 어긋나지 않게)
                        c.TextColor = c.Style.Fill.Colors[0];
                    }, null);
                    var index = i;
                    var rename = new MenuItem { Header = new TextBlock { Text = "이름 바꾸기…" } };
                    rename.Click += (s, e) =>
                    {
                        var newName = Dialogs.Input(this, "내 스타일 이름 바꾸기", "스타일 이름(1~40자)", name);
                        if (newName != null)
                            ChangeMine(list => TextStyleLibrary.Rename(list, name, newName), "내 스타일 이름을 바꿨습니다: " + name + " → " + newName.Trim(), Rebuild);
                    };
                    var earlier = new MenuItem { Header = new TextBlock { Text = "앞으로" }, IsEnabled = index > 0 };
                    earlier.Click += (s, e) => ChangeMine(list => TextStyleLibrary.Move(list, TextStyleLibrary.IndexOf(list, name), index - 1) ? null : "", null, Rebuild);
                    var later = new MenuItem { Header = new TextBlock { Text = "뒤로" }, IsEnabled = index < styles.Count - 1 };
                    later.Click += (s, e) => ChangeMine(list => TextStyleLibrary.Move(list, TextStyleLibrary.IndexOf(list, name), index + 1) ? null : "", null, Rebuild);
                    var remove = new MenuItem { Header = new TextBlock { Text = "지우기" } };
                    remove.Click += (s, e) => RemoveMine(name, Rebuild);
                    button.ContextMenu = new ContextMenu();
                    button.ContextMenu.Items.Add(rename);
                    button.ContextMenu.Items.Add(earlier);
                    button.ContextMenu.Items.Add(later);
                    button.ContextMenu.Items.Add(new Separator());
                    button.ContextMenu.Items.Add(remove);
                    row.Children.Add(button);
                    if (shown)
                        StyleTiles.ShowThumbnail(_services, frame, style, "StyleMine" + i + "Image", sample);
                }
                row.Visibility = styles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                hint.Visibility = styles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            box.Loaded += (s, e) =>
            {
                shown = _services != null;
                Rebuild();
            };
            Rebuild();
            var save = UiKit.TextButton("내 스타일로 저장", "StyleSaveMine", () => SaveMine(id, Rebuild));
            save.Margin = new Thickness(0, 0, 0, 6);
            // 다른 PC로 옮기기: 설정과 같은 JSON 파일
            var import = UiKit.TextButton("가져오기…", "StyleImportMine", () => ImportMine(Rebuild));
            var export = UiKit.TextButton("내보내기…", "StyleExportMine", ExportMine);
            foreach (var button in new[] { import, export })
            {
                button.MinWidth = 0;
                button.Margin = new Thickness(4, 0, 0, 6);
            }
            var buttons = new WrapPanel();
            buttons.Children.Add(save);
            buttons.Children.Add(import);
            buttons.Children.Add(export);
            box.Children.Add(buttons);
            return box;
        }

        /// <summary>내 스타일을 다시 읽어 고치고(change가 알릴 글을 돌려주면 그대로 두고 알림 — 빈 글이면 알리지 않음) 설정에 쓴다. 설정이라 실행 취소에 남지 않는다.</summary>
        private void ChangeMine(Func<List<(string Name, TextStyle Style)>, string> change, string done, Action changed)
        {
            var styles = _services.TextStyles();
            var error = change(styles);
            if (error != null)
            {
                if (error.Length > 0)
                    UiKit.Inform(this, "내 스타일", error);
                changed();
                return;
            }
            if (!_services.SaveTextStyles(styles))
            {
                _commands.Status("내 스타일을 설정에 저장하지 못했습니다.");
                return;
            }
            if (done != null)
                _commands.Status(done);
            changed();
        }

        /// <summary>[가져오기…]: 내보낸 파일의 스타일 가운데 같은 이름이 없는 것만 더한다(<see cref="TextStyleLibrary.Import"/>).</summary>
        private void ImportMine(Action imported)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "내 스타일 가져오기", Filter = StyleFileFilter };
            if (dialog.ShowDialog(UiKit.OwnerOf(this)) != true)
                return;
            string json;
            try
            {
                // UTF-8은 한 글자가 4바이트까지 — 그보다 큰 파일은 읽지 않는다(읽기 규칙의 글 길이 상한)
                json = new System.IO.FileInfo(dialog.FileName).Length > TextStyleLibrary.MaxJsonLength * 4L ? null : System.IO.File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                UiKit.Inform(this, "내 스타일", "파일을 읽지 못했습니다: " + ex.Message);
                return;
            }
            TextStyleLibrary.ImportResult result = null;
            ChangeMine(list =>
            {
                result = TextStyleLibrary.Import(list, json);
                return result.Added > 0 ? null : result.Message;
            }, null, imported);
            if (result != null && result.Added > 0)
                UiKit.Inform(this, "내 스타일", result.Message);
        }

        /// <summary>[내보내기…]: 내 스타일을 설정과 같은 JSON 파일로 쓴다.</summary>
        private void ExportMine()
        {
            var styles = _services.TextStyles();
            if (styles.Count == 0)
            {
                UiKit.Inform(this, "내 스타일", "내보낼 내 스타일이 없습니다.");
                return;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "내 스타일 내보내기", Filter = StyleFileFilter, DefaultExt = ".json", FileName = "내 스타일.json" };
            if (dialog.ShowDialog(UiKit.OwnerOf(this)) != true)
                return;
            try
            {
                System.IO.File.WriteAllText(dialog.FileName, TextStyleLibrary.Write(styles));
                _commands.Status("내 스타일 " + styles.Count + "개를 내보냈습니다: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                UiKit.Inform(this, "내 스타일", "파일에 쓰지 못했습니다: " + ex.Message);
            }
        }

        private const string StyleFileFilter = "내 스타일 (*.json)|*.json|모든 파일 (*.*)|*.*";

        /// <summary>
        /// [내 스타일로 저장]: 이름을 묻고(같은 이름이면 덮어쓸지 묻는다) 누른 때의 스타일 사본을 설정에 넣는다 — 스타일이 없는 글자는 지금 색을 이어받은 스타일로.
        /// 이름이 규칙에 맞지 않거나 50개가 찼으면 알리고 저장하지 않는다. 설정을 쓰지 못하면 상태 줄에 알린다.
        /// </summary>
        private void SaveMine(string id, Action saved)
        {
            var clip = _session.Project.FindClip(id);
            if (clip == null || _services == null)
                return;
            // 클립의 스타일을 같이 쓰지 않게 사본. 저장은 편집이 아니므로 스타일이 없는 글자에도 스타일을 만들어 넣지 않는다
            var style = clip.Style?.Clone() ?? TextStyle.FromLegacy(clip);
            var name = Dialogs.Input(this, "내 스타일로 저장", "스타일 이름(1~40자)", TextStyleLibrary.NewName(_services.TextStyles()));
            if (name == null)
                return;
            var clean = TextStyleLibrary.CleanName(name);
            if (clean == null)
            {
                UiKit.Inform(this, "내 스타일", TextStyleLibrary.NameRule);
                return;
            }
            // 이름을 묻는 동안 다른 편집 창에서 고쳤을 수 있어 다시 읽는다
            var styles = _services.TextStyles();
            var index = TextStyleLibrary.IndexOf(styles, clean);
            if (index >= 0 && UiKit.Ask(this, "내 스타일 덮어쓰기", "'" + styles[index].Name + "' 스타일이 이미 있습니다. 지금 스타일로 덮어쓸까요?", "덮어쓰기", "취소") != 0)
                return;
            var error = TextStyleLibrary.AddOrReplace(styles, clean, style);
            if (error != null)
            {
                UiKit.Inform(this, "내 스타일", error);
                return;
            }
            if (!_services.SaveTextStyles(styles))
            {
                _commands.Status("내 스타일을 설정에 저장하지 못했습니다.");
                return;
            }
            _commands.Status("내 스타일에 저장했습니다: " + clean);
            saved();
        }

        /// <summary>내 스타일 [지우기]: 설정에서 뺀다. 묻지 않는다 — 설정이라 실행 취소로 되돌릴 수 없음을 상태 줄에 알린다.</summary>
        private void RemoveMine(string name, Action removed)
        {
            var styles = _services.TextStyles();
            if (!TextStyleLibrary.Remove(styles, name))
            {
                // 다른 편집 창에서 이미 지웠다 — 줄만 다시 그린다
                _commands.Status("내 스타일에 없습니다(이미 지웠습니다): " + name);
                removed();
                return;
            }
            if (!_services.SaveTextStyles(styles))
            {
                _commands.Status("내 스타일을 설정에서 지우지 못했습니다.");
                return;
            }
            _commands.Status("내 스타일에서 지웠습니다: " + name + " (설정이라 실행 취소로 되돌릴 수 없습니다)");
            removed();
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

        /// <summary>고칠 클립의 광선(스타일·광선이 없으면 만든다).</summary>
        private static TextGlow GlowOf(Clip clip)
        {
            return StyleOf(clip).Glow ??= new TextGlow();
        }

        /// <summary>고칠 클립의 반사(스타일·반사가 없으면 만든다).</summary>
        private static TextReflection ReflectionOf(Clip clip)
        {
            return StyleOf(clip).Reflection ??= new TextReflection();
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

        /// <summary>글이 잘리지 않고 다 보이는 폭(지금 글꼴로 잰 글 폭 + 바깥 여백).</summary>
        private static double TextWidth(TextBlock text)
        {
            var formatted = new System.Windows.Media.FormattedText(text.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
                new System.Windows.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black,
                System.Windows.Media.VisualTreeHelper.GetDpi(text).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace + text.Margin.Left + text.Margin.Right + text.Padding.Left + text.Padding.Right + 1;
        }

        /// <summary>
        /// 줄(line)의 폭에서 오른쪽 부분(part)을 빼고 남는 폭이 need보다 좁으면 part를 아랫줄(lower)로 내리고, 넓어지면 다시 올린다 — 좁은 패널에서도 이름·슬라이더가 쓸 만한 폭을 갖게.
        /// part는 line의 첫 자식(Dock.Right)으로 들어가 있다가 lower로 옮겨진다. 폭은 지금 그려진 값(ActualWidth)만 쓴다(크기가 바뀌는 중에 다시 재지 않게).
        /// </summary>
        private static void FoldRight(DockPanel line, DockPanel lower, FrameworkElement part, double need)
        {
            var folded = lower.Children.Contains(part);
            var fold = line.ActualWidth - part.ActualWidth < need;
            if (fold == folded || part.ActualWidth <= 0)
                return;
            if (fold)
            {
                line.Children.Remove(part);
                lower.Children.Add(part);
            }
            else
            {
                lower.Children.Remove(part);
                line.Children.Insert(0, part);
            }
            lower.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
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

        // 숫자 줄의 슬라이더가 이보다 좁아지면 아랫줄로 내린다
        private const double MinSlider = 100;

        /// <summary>
        /// 이름 · 슬라이더 · 숫자 칸 · 단위 한 줄(좁으면 슬라이더만 아랫줄). edit이 있으면 그것으로(프로젝트 편집), 없으면 set으로 클립 값을 바꾼다.
        /// </summary>
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
            // 좁은 패널(1280×800 창 등)에서는 이름·숫자 칸·단위를 빼면 슬라이더가 19px까지 줄어 쓸 수 없었다 — MinSlider보다 좁으면 아랫줄에 줄 폭 전체로
            var outer = new StackPanel { Margin = row.Margin };
            row.Margin = new Thickness(0);
            var lower = new DockPanel { Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
            outer.Children.Add(row);
            outer.Children.Add(lower);
            outer.SizeChanged += (s, e) =>
            {
                var fixedWidth = name.ActualWidth + box.ActualWidth + box.Margin.Left + unitText.ActualWidth + unitText.Margin.Left;
                var fold = outer.ActualWidth - fixedWidth < MinSlider;
                if (fold == lower.Children.Contains(slider) || name.ActualWidth <= 0)
                    return;
                (fold ? (Panel)row : lower).Children.Remove(slider);
                (fold ? (Panel)lower : row).Children.Add(slider);
                lower.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
            };
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
            (_collect ?? _refreshers).Add(Refresh);
            return outer;
        }
    }
}
