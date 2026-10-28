using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 속성 패널 [화면] 탭의 사용자 키프레임(#4): 위치 X·Y·크기·회전·불투명도 줄마다 키프레임 단추(◆ — 재생 헤드에 키프레임이 있음, ◇ — 없음, 키프레임이 있는 값은 강조 색).
    /// 누르면 재생 헤드 시각에 지금 값으로 넣거나 뺀다. 키프레임이 있는 값은 재생 헤드 시각의 값(사이 값)을 보이고, 값을 바꾸면 그 시각의 키프레임을 고치거나 새로 넣는다.
    /// 아래에 재생 헤드의 키프레임 이징(다음 키프레임까지)과 앞·다음 키프레임으로 가기. 재생 헤드가 움직이면 값을 다시 채운다. 모두 실행 취소 한 단계씩.
    /// </summary>
    internal sealed partial class PropertyPanel
    {
        // 재생 헤드가 움직일 때 다시 채울 것(고른 클립이 바뀌면 비운다): 값 줄(키프레임이 있을 때만)과 단추·이징 줄(늘)
        private readonly List<(Action Refresh, bool Values)> _keyRefreshers = new List<(Action, bool)>();

        private void RefreshKeyframes()
        {
            if (_keyRefreshers.Count == 0)
                return;
            var keyed = ClipKeyframes.Has(_session.PrimaryClip);
            var was = _updating;
            _updating = true;
            try
            {
                foreach (var (refresh, values) in _keyRefreshers)
                {
                    if (keyed || !values)
                        refresh();
                }
            }
            finally
            {
                _updating = was;
            }
        }

        /// <summary>재생 헤드의 클립 안 시각(클립 안으로 자름).</summary>
        private long KeyFrame(Clip clip)
        {
            return Math.Max(0, Math.Min(clip.Duration - 1, _session.Playhead - clip.Start));
        }

        /// <summary>
        /// 키프레임을 둘 수 있는 숫자 줄: 값 = 재생 헤드 시각의 값 × factor(위치 y면 Y), 바꾸면 toModel(적은 값)을 키프레임(있으면)·고정 값으로. 줄 오른쪽 끝에 ◆ 단추.
        /// </summary>
        private UIElement KeyRow(string label, string automationId, double min, double max, string unit, string id,
            KeyframeProperty property, bool y, double factor, Func<double, double> toModel)
        {
            var values = new List<Action>();
            var outer = (Panel)NumberRow(label, automationId, min, max, unit, id,
                c => ClipKeyframes.At(c, property, KeyFrame(c), y) * factor,
                (c, v) =>
                {
                    var frame = KeyFrame(c);
                    if (property != KeyframeProperty.Position)
                        ClipKeyframes.Set(c, property, frame, toModel(v));
                    else if (y)
                        ClipKeyframes.SetPosition(c, frame, ClipKeyframes.At(c, property, frame), toModel(v));
                    else
                        ClipKeyframes.SetPosition(c, frame, toModel(v), ClipKeyframes.At(c, property, frame, true));
                }, null, values);
            foreach (var refresh in values)
                _keyRefreshers.Add((refresh, true));
            // ◆ 단추는 줄의 오른쪽 끝(단위 오른쪽) — 좁은 패널에서는 숫자 줄이 슬라이더를 아랫줄로 내리고 이름·숫자 칸·단위·◆가 한 줄에 남는다
            var row = (DockPanel)outer.Children[0];
            var mark = new TextBlock { Text = "◇", FontSize = 13 };
            var button = new Button { Content = mark, Width = 22, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetAutomationId(button, automationId + "Key");
            AutomationProperties.SetName(button, label + " 키프레임");
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Insert(1, button);
            button.Click += (s, e) => ToggleKeyframe(id, property);
            void RefreshButton()
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var inside = c.Contains(_session.Playhead);
                var here = inside && ClipKeyframes.Find(c, property, KeyFrame(c)) != null;
                var text = here ? "◆" : "◇";
                if (mark.Text != text)
                    mark.Text = text;
                button.IsEnabled = inside;
                if (ClipKeyframes.Has(c, property))
                    mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                else
                    mark.ClearValue(TextBlock.ForegroundProperty);
                var name = ClipKeyframes.Name(property);
                var tip = !inside ? "재생 헤드가 클립 안에 있어야 키프레임을 넣습니다."
                    : here ? "재생 헤드(" + _session.Project.Rate.Timecode(_session.Playhead) + ")의 " + name + " 키프레임을 뺍니다."
                    : "재생 헤드(" + _session.Project.Rate.Timecode(_session.Playhead) + ")에 " + name + " 키프레임을 넣습니다(지금 값으로).";
                if (!Equals(button.ToolTip, tip))
                    button.ToolTip = tip;
            }
            _refreshers.Add(RefreshButton);
            _keyRefreshers.Add((RefreshButton, false));
            return outer;
        }

        /// <summary>◆: 재생 헤드 시각에 키프레임을 넣거나 뺀다(실행 취소 한 단계).</summary>
        private void ToggleKeyframe(string id, KeyframeProperty property)
        {
            var clip = _session.Project.FindClip(id);
            if (clip == null)
                return;
            if (!clip.Contains(_session.Playhead))
            {
                _commands.Status("재생 헤드가 클립 안에 있어야 키프레임을 넣습니다.");
                return;
            }
            var frame = KeyFrame(clip);
            var adding = ClipKeyframes.Find(clip, property, frame) == null;
            _commands.EditClip(id, (p, c) => ClipKeyframes.Toggle(c, property, frame), null);
            var after = _session.Project.FindClip(id);
            if (after != null && (ClipKeyframes.Find(after, property, frame) != null) == adding)
                _commands.Status(ClipKeyframes.Name(property) + " 키프레임을 " + (adding ? "넣었습니다" : "뺐습니다") + " (" + _session.Project.Rate.Timecode(_session.Playhead) + ").");
        }

        /// <summary>
        /// 키프레임 도구(위치·크기·회전·불투명도 줄 아래): 안내, 그리고 키프레임이 있으면 재생 헤드 키프레임의 이징(다음 키프레임까지 — 그 시각의 키프레임 모두)과 앞·다음 키프레임으로 가기.
        /// </summary>
        private UIElement KeyframeTools(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            var eases = Enum.GetValues(typeof(AnimationEase)).Cast<AnimationEase>().ToList();
            var combo = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
            AutomationProperties.SetAutomationId(combo, "PropKeyEase");
            AutomationProperties.SetName(combo, "키프레임 이징");
            foreach (var ease in eases)
                combo.Items.Add(ClipAnimations.EaseName(ease));
            combo.ToolTip = "재생 헤드의 키프레임에서 다음 키프레임까지 값이 바뀌는 모양";
            combo.SelectionChanged += (s, e) =>
            {
                if (_updating || combo.SelectedIndex < 0)
                    return;
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var frame = KeyFrame(c);
                var ease = eases[combo.SelectedIndex];
                if (c.Keyframes?.Any(k => k.Frame == frame && k.Ease != ease) == true)
                    Edit(id, x => ClipKeyframes.SetEase(x, frame, ease), null);
            };
            var easeRow = Row("키 이징", combo);
            panel.Children.Add(easeRow);
            var previous = UiKit.TextButton("◀ 앞 키", "PropKeyPrev", () => GoToKeyframe(id, false));
            previous.Margin = new Thickness(0);
            var next = UiKit.TextButton("다음 키 ▶", "PropKeyNext", () => GoToKeyframe(id, true));
            var moves = new WrapPanel { Margin = new Thickness(84, 2, 0, 2) };
            moves.Children.Add(previous);
            moves.Children.Add(next);
            panel.Children.Add(moves);
            var hint = UiKit.Label("◆: 재생 헤드에 키프레임을 넣거나 뺍니다. 키프레임이 있는 값은 그 시각의 값을 보이고, 바꾸면(미리 보기에서 끌어도) 그 시각의 키프레임을 고치거나 새로 넣습니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 2, 0, 0);
            panel.Children.Add(hint);
            void Refresh()
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var keyed = ClipKeyframes.Has(c);
                easeRow.Visibility = moves.Visibility = keyed ? Visibility.Visible : Visibility.Collapsed;
                if (!keyed)
                    return;
                var frame = KeyFrame(c);
                var here = c.Contains(_session.Playhead) ? c.Keyframes.FirstOrDefault(k => k.Frame == frame) : null;
                combo.IsEnabled = here != null;
                var was = _updating;
                _updating = true;
                try
                {
                    combo.SelectedIndex = here == null ? -1 : eases.IndexOf(here.Ease);
                }
                finally
                {
                    _updating = was;
                }
                var frames = ClipKeyframes.Frames(c).Where(f => f >= 0 && f < c.Duration).ToList();
                var at = _session.Playhead - c.Start;
                previous.IsEnabled = frames.Any(f => f < at);
                next.IsEnabled = frames.Any(f => f > at);
            }
            _refreshers.Add(Refresh);
            _keyRefreshers.Add((Refresh, false));
            return panel;
        }

        /// <summary>재생 헤드를 앞·다음 키프레임(클립 안)으로.</summary>
        private void GoToKeyframe(string id, bool forward)
        {
            var c = _session.Project.FindClip(id);
            if (c == null)
                return;
            var at = _session.Playhead - c.Start;
            var frames = ClipKeyframes.Frames(c).Where(f => f >= 0 && f < c.Duration).ToList();
            long? target = forward ? frames.Where(f => f > at).Select(f => (long?)f).FirstOrDefault() : frames.Where(f => f < at).Select(f => (long?)f).LastOrDefault();
            if (target == null)
                return;
            _session.Playhead = c.Start + target.Value;
            _commands.Status("키프레임 " + _session.Project.Rate.Timecode(_session.Playhead));
        }
    }
}
