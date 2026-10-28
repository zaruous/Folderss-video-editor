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
    /// 속성 패널 [애니메이션] 탭(#4): 나타나기·강조·사라지기 분류별 썸네일(마우스를 올리면 움직임), 넣은 효과의 설정(방향·단위·글자 사이 지연·길이·지연·반복·이징·정도·색)과 [빼기],
    /// [미리 보기](이 클립 구간만 재생). 효과를 넣고 빼면 줄을 다시 만들고, 값만 바뀌면 값만 갱신한다(끄는 슬라이더가 사라지지 않게). 모두 실행 취소 한 단계씩.
    /// </summary>
    internal sealed partial class PropertyPanel
    {
        private UIElement AnimationTab(string id)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            var text = _session.Project.FindClip(id)?.Kind == ClipKind.Text;
            var tiles = new List<(AnimationTile Tile, AnimationKind Kind, AnimationEffect Effect)>();
            var rows = new Dictionary<AnimationKind, StackPanel>();
            foreach (var kind in new[] { AnimationKind.In, AnimationKind.Emphasis, AnimationKind.Out })
            {
                panel.Children.Add(Section(ClipAnimations.KindName(kind)));
                var wrap = new WrapPanel();
                AutomationProperties.SetAutomationId(wrap, "AnimationTiles" + kind);
                foreach (var def in ClipAnimations.All.Where(d => d.Emphasis == (kind == AnimationKind.Emphasis) && (text || !d.TextOnly)))
                {
                    var tile = new AnimationTile(def, kind, text && def.TextOnly, "Anim" + kind + "_" + def.Effect);
                    var chosen = def;
                    tile.Click += (s, e) => AddAnimation(id, kind, chosen);
                    tiles.Add((tile, kind, def.Effect));
                    wrap.Children.Add(tile);
                }
                panel.Children.Add(wrap);
                var box = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
                rows[kind] = box;
                panel.Children.Add(box);
            }
            var preview = UiKit.TextButton("미리 보기", "AnimationPreview", () => _commands.PlayClip(id));
            preview.HorizontalAlignment = HorizontalAlignment.Left;
            preview.Margin = new Thickness(0, 2, 0, 4);
            preview.ToolTip = "이 클립 구간만 재생합니다.";
            panel.Children.Add(preview);
            var hint = UiKit.Label(text
                ? "효과는 위치·크기·회전 위에 더해 움직입니다. 글자는 단어·글자별로도 움직입니다(한글은 음절). 타임라인 클립 위쪽 띠의 끝을 끌어 나타나기·사라지기 길이를 바꿉니다."
                : "효과는 위치·크기·회전 위에 더해 움직입니다. 타임라인 클립 위쪽 띠의 끝을 끌어 나타나기·사라지기 길이를 바꿉니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(hint);

            var local = new List<Action>();
            string shown = null;
            _refreshers.Add(() =>
            {
                var c = _session.Project.FindClip(id);
                if (c == null)
                    return;
                var list = c.Animations ?? new List<ClipAnimation>();
                foreach (var (tile, kind, effect) in tiles)
                    tile.SetSelected(kind != AnimationKind.Emphasis && list.Any(a => a.Kind == kind && a.Effect == effect));
                // 효과·단위가 바뀐 때만 줄을 다시 만든다(값만 바뀌면 그대로)
                var signature = string.Join("|", list.Select(a => a.Kind + ":" + a.Effect + ":" + (a.Unit == AnimationUnit.All ? "1" : "n")));
                if (signature != shown)
                {
                    shown = signature;
                    local.Clear();
                    foreach (var box in rows.Values)
                        box.Children.Clear();
                    _collect = local;
                    try
                    {
                        var emphasis = 0;
                        foreach (var animation in list)
                        {
                            var index = animation.Kind == AnimationKind.Emphasis ? emphasis++ : 0;
                            rows[animation.Kind].Children.Add(AnimationRows(id, animation.Kind, index, animation.Effect, text, local));
                        }
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
            });
            return panel;
        }

        /// <summary>썸네일을 눌렀다: 나타나기·사라지기는 바꾸고(같은 효과면 그대로), 강조는 하나 더한다.</summary>
        private void AddAnimation(string id, AnimationKind kind, AnimationEffectDef def)
        {
            Edit(id, c =>
            {
                var animation = ClipAnimations.Create(def, kind, _session.Project.Rate, c);
                if (animation == null)
                    return;
                if (kind == AnimationKind.Emphasis)
                {
                    ClipAnimations.AddEmphasis(c, animation);
                    return;
                }
                var current = kind == AnimationKind.In ? ClipAnimations.In(c) : ClipAnimations.Out(c);
                if (current != null && current.Effect == def.Effect)
                    return;
                // 길이·지연은 바꾸기 전 값을 이어받는다(효과만 바꿔 보기 쉽게)
                if (current != null)
                {
                    animation.Duration = current.Duration;
                    animation.Delay = current.Delay;
                }
                ClipAnimations.Set(c, kind, animation);
            }, null);
            _commands.Status(ClipAnimations.KindName(kind) + " 효과를 넣었습니다: " + def.DisplayName(kind));
        }

        /// <summary>넣은 효과 하나의 줄들(kind의 index번째, effect — 그 자리의 효과가 바뀌었으면 고치지 않는다). 갱신은 local에 모은다.</summary>
        private UIElement AnimationRows(string id, AnimationKind kind, int index, AnimationEffect effect, bool text, List<Action> local)
        {
            var def = ClipAnimations.Def(effect);
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var prefix = "Anim" + kind + (kind == AnimationKind.Emphasis ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + "_";
            ClipAnimation Find(Clip c)
            {
                var animation = kind == AnimationKind.In ? ClipAnimations.In(c) : kind == AnimationKind.Out ? ClipAnimations.Out(c) : ClipAnimations.Emphases(c).ElementAtOrDefault(index);
                return animation != null && animation.Effect == effect ? animation : null;
            }
            void Change(Action<ClipAnimation> change, string mergeKey = null)
            {
                Edit(id, c =>
                {
                    var animation = Find(c);
                    if (animation != null)
                        change(animation);
                }, mergeKey);
            }
            var rate = _session.Project.Rate;

            var head = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = UiKit.TextButton("빼기", prefix + "Remove", () => Edit(id, c =>
            {
                if (Find(c) == null)
                    return;
                if (kind == AnimationKind.Emphasis)
                    ClipAnimations.RemoveEmphasis(c, index);
                else
                    ClipAnimations.Set(c, kind, null);
            }, null));
            remove.MinWidth = 50;
            remove.Padding = new Thickness(6, 1, 6, 1);
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            var title = Section(def.DisplayName(kind));
            title.ToolTip = def.Description;
            head.Children.Add(title);
            box.Children.Add(head);

            ComboBox Choice<T>(string label, string automation, IReadOnlyList<T> values, Func<T, string> name, Func<ClipAnimation, T> get, Action<ClipAnimation, T> set)
            {
                var combo = new ComboBox();
                AutomationProperties.SetAutomationId(combo, prefix + automation);
                AutomationProperties.SetName(combo, label);
                foreach (var value in values)
                    combo.Items.Add(name(value));
                combo.SelectionChanged += (s, e) =>
                {
                    if (_updating || combo.SelectedIndex < 0)
                        return;
                    var value = values[combo.SelectedIndex];
                    var current = Find(_session.Project.FindClip(id));
                    if (current != null && !EqualityComparer<T>.Default.Equals(get(current), value))
                        Change(a => set(a, value));
                };
                local.Add(() =>
                {
                    var current = Find(_session.Project.FindClip(id));
                    if (current != null)
                        combo.SelectedIndex = values.ToList().IndexOf(get(current));
                });
                box.Children.Add(Row(label, combo));
                return combo;
            }

            if (def.Directions.Count > 0)
                Choice("방향", "Direction", def.Directions, d => ClipAnimations.DirectionName(effect, d), a => a.Direction, (a, v) => a.Direction = v);
            var current0 = Find(_session.Project.FindClip(id));
            // 단위: 나타나기·사라지기와 글자 전용 강조(물결·굵게 깜빡이기)
            if (text && (kind != AnimationKind.Emphasis || def.TextOnly))
            {
                var units = def.NeedsUnits ? new[] { AnimationUnit.Word, AnimationUnit.Character } : new[] { AnimationUnit.All, AnimationUnit.Word, AnimationUnit.Character };
                Choice("단위", "Unit", units, ClipAnimations.UnitName, a => a.Unit, (a, v) =>
                {
                    a.Unit = v;
                    if (v != AnimationUnit.All && a.Stagger <= 0)
                        a.Stagger = def.DefaultStagger;
                });
                if (current0 != null && current0.Unit != AnimationUnit.All)
                    box.Children.Add(NumberRow("사이 지연", prefix + "Stagger", 0, 100, "%", id, c => Find(c)?.Stagger ?? 0, (c, v) =>
                    {
                        var a = Find(c);
                        if (a != null)
                            a.Stagger = Math.Max(0, Math.Min(100, v));
                    }));
            }
            box.Children.Add(NumberRow(kind == AnimationKind.Emphasis ? "한 번 길이" : "길이", prefix + "Duration", 0.1, 10, "초", id,
                c => rate.ToSeconds(Find(c)?.Duration ?? 0), (c, v) =>
                {
                    var a = Find(c);
                    if (a != null)
                        a.Duration = Math.Max(1, Math.Min(ClipAnimations.MaxFrames, rate.ToFrames(Math.Max(0, v))));
                }));
            box.Children.Add(NumberRow("지연", prefix + "Delay", 0, 10, "초", id,
                c => rate.ToSeconds(Find(c)?.Delay ?? 0), (c, v) =>
                {
                    var a = Find(c);
                    if (a != null)
                        a.Delay = Math.Max(0, Math.Min(ClipAnimations.MaxFrames, rate.ToFrames(Math.Max(0, v))));
                }));
            if (def.Repeats)
            {
                var repeat = NumberRow("반복", prefix + "Repeat", 0, 20, "회", id, c => Find(c)?.Repeat ?? 1, (c, v) =>
                {
                    var a = Find(c);
                    if (a != null)
                        a.Repeat = (int)Math.Max(0, Math.Min(ClipAnimations.MaxRepeat, Math.Round(v)));
                });
                if (repeat is FrameworkElement element)
                    element.ToolTip = "0이면 클립 끝까지 되풀이합니다.";
                box.Children.Add(repeat);
            }
            if (effect != AnimationEffect.Typewriter)
            {
                var eases = (AnimationEase[])Enum.GetValues(typeof(AnimationEase));
                Choice("이징", "Ease", eases, ClipAnimations.EaseName, a => a.Ease, (a, v) => a.Ease = v);
            }
            if (def.Amount != null)
            {
                var amount = def.Amount;
                box.Children.Add(NumberRow(amount.Name, prefix + "Amount", amount.Min, amount.Max, amount.Unit, id, c => Find(c)?.Amount ?? amount.Default, (c, v) =>
                {
                    var a = Find(c);
                    if (a != null)
                        a.Amount = Math.Max(amount.Min, Math.Min(amount.Max, v));
                }));
            }
            if (def.DefaultColor != null)
            {
                var color = UiKit.ColorRow("색", prefix + "Color", () => Find(_session.Project.FindClip(id))?.Color ?? def.DefaultColor,
                    hex => Change(a => a.Color = ClipAnimations.Rgb(hex, def.DefaultColor)));
                local.Add(color.Refresh);
                box.Children.Add(color);
            }
            return box;
        }
    }
}
