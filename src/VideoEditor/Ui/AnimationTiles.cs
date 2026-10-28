using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 애니메이션 효과 썸네일(속성 패널 [애니메이션] 탭): 작은 화면에 "가나다"를 그 효과의 반쯤 모습으로 보이고, 마우스를 올리면 움직임을 되풀이해 보인다.
    /// 움직임은 렌더와 같은 계산(<see cref="AnimationMotion"/>)이다 — 한 번에 움직이는 효과는 글 전체, 글자별 효과(타자기·흩어지기)는 글자마다.
    /// </summary>
    internal sealed class AnimationTile : Button
    {
        private const double StageWidth = 64;
        private const double StageHeight = 36;
        private const double LetterSize = 12;
        // 썸네일의 "화면"은 320픽셀 폭으로 본다(흔들기 폭 등 프로젝트 픽셀 값을 줄여 보이게)
        private const double ScreenScale = StageWidth / 320;
        private static readonly string[] Letters = { "가", "나", "다" };

        private readonly AnimationEffectDef _def;
        private readonly AnimationKind _kind;
        private readonly Canvas _stage;
        private readonly StackPanel _word;
        private readonly TextBlock[] _letters;
        private readonly Border _box;
        private readonly Clip _clip;
        private readonly long _period;
        private readonly long _still;
        private DispatcherTimer _timer;
        private long _tick;

        public AnimationTile(AnimationEffectDef def, AnimationKind kind, bool perUnit, string automationId)
        {
            _def = def;
            _kind = kind;
            // 테마의 단추 모양(형식 이름으로 찾는 기본 스타일은 파생 형식에 붙지 않는다)
            SetResourceReference(StyleProperty, typeof(Button));
            Padding = new Thickness(2);
            Margin = new Thickness(0, 0, 4, 4);
            Width = 74;
            ToolTip = def.Description;
            AutomationProperties.SetAutomationId(this, automationId);
            AutomationProperties.SetName(this, def.DisplayName(kind));
            _stage = new Canvas { Width = StageWidth, Height = StageHeight, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)) };
            _word = new StackPanel { Orientation = Orientation.Horizontal };
            _letters = Letters.Select(l => new TextBlock
            {
                Text = l,
                FontSize = LetterSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
                RenderTransformOrigin = new Point(0.5, 0.5)
            }).ToArray();
            foreach (var letter in _letters)
                _word.Children.Add(letter);
            _word.RenderTransformOrigin = new Point(0.5, 0.5);
            _stage.Children.Add(_word);
            _word.Loaded += (s, e) => Place();
            _box = new Border { Child = _stage, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = def.DisplayName(kind), FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            var content = new StackPanel();
            content.Children.Add(_box);
            content.Children.Add(label);
            Content = content;

            // 썸네일용 가짜 클립(30fps): 나타나기는 앞에서, 사라지기는 뒤에서, 강조는 두 번
            var animation = ClipAnimations.Create(def, kind, FrameRate.Default);
            animation.Duration = Math.Min(animation.Duration, 30);
            animation.Unit = perUnit ? (def.DefaultUnit == AnimationUnit.All ? AnimationUnit.Character : def.DefaultUnit) : AnimationUnit.All;
            if (animation.Unit != AnimationUnit.All && animation.Stagger <= 0)
                animation.Stagger = def.DefaultStagger;
            if (kind == AnimationKind.Emphasis)
                animation.Repeat = def.Repeats ? 2 : 1;
            var length = kind == AnimationKind.Emphasis ? animation.Duration * animation.Repeat : animation.Duration;
            _period = length + 20;
            _clip = TimelineEdits.CreateText(string.Concat(Letters), 0, _period);
            animation.Delay = kind == AnimationKind.Out ? 4 : 0;
            _clip.Animations = new List<ClipAnimation> { animation };
            // 멈춘 썸네일: 움직임이 반쯤인 장
            _still = kind == AnimationKind.Out ? _period - 4 - animation.Duration / 2 : animation.Duration * 2 / 5;
            Show(_still);
            MouseEnter += (s, e) => Start();
            MouseLeave += (s, e) => Stop();
            Unloaded += (s, e) => Stop();
        }

        /// <summary>고른 효과(지금 클립의 나타나기·사라지기)면 테두리를 강조색으로.</summary>
        public void SetSelected(bool selected)
        {
            if (selected)
                _box.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else
                _box.BorderBrush = Brushes.Transparent;
        }

        private void Start()
        {
            if (_timer != null)
                return;
            _tick = 0;
            _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) =>
            {
                Show(_tick);
                _tick = (_tick + 1) % _period;
            };
            _timer.Start();
        }

        private void Stop()
        {
            _timer?.Stop();
            _timer = null;
            Show(_still);
        }

        private void Place()
        {
            _word.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_word, (StageWidth - _word.DesiredSize.Width) / 2);
            Canvas.SetTop(_word, (StageHeight - _word.DesiredSize.Height) / 2);
        }

        /// <summary>frame 장의 모습을 보인다.</summary>
        private void Show(long frame)
        {
            var text = TextAnimationFrame.Of(_clip, FrameRate.Default);
            if (text != null)
            {
                Reset(_word);
                var poses = text.Poses(_clip.Text, LetterSize, frame, ScreenScale);
                for (var i = 0; i < _letters.Length; i++)
                    Apply(_letters[i], poses[i]);
                return;
            }
            foreach (var letter in _letters)
                Reset(letter);
            var motion = AnimationMotion.ObjectMotion(_clip, false);
            var scale = motion.Scale?.Eval(frame) ?? 1;
            var rotation = motion.Rotation?.Eval(frame) ?? 0;
            var x = (motion.ShiftX?.Eval(frame) ?? 0) * ScreenScale;
            var y = (motion.ShiftY?.Eval(frame) ?? 0) * ScreenScale;
            _word.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _word.DesiredSize;
            foreach (var (weight, dx, dy) in motion.Fly)
            {
                var w = weight.Eval(frame);
                x += w * dx * (StageWidth + size.Width) / 2;
                y += w * dy * (StageHeight + size.Height) / 2;
            }
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(scale, scale));
            group.Children.Add(new RotateTransform(rotation));
            group.Children.Add(new TranslateTransform(x, y));
            _word.RenderTransform = group;
            _word.Opacity = Math.Max(0, Math.Min(1, motion.Opacity?.Eval(frame) ?? 1));
            Geometry clip = null;
            foreach (var (visible, dx, dy) in motion.Wipes)
            {
                var v = Math.Max(0, Math.Min(1, visible.Eval(frame)));
                var rect = new Rect(0, 0, size.Width, size.Height);
                if (dx != 0)
                    rect = new Rect(dx < 0 ? 0 : size.Width * (1 - v), rect.Top, size.Width * v, rect.Height);
                if (dy != 0)
                    rect = new Rect(rect.Left, dy < 0 ? 0 : size.Height * (1 - v), rect.Width, size.Height * v);
                clip = clip == null ? new RectangleGeometry(rect) : (Geometry)new CombinedGeometry(GeometryCombineMode.Intersect, clip, new RectangleGeometry(rect));
            }
            _word.Clip = clip;
            _word.Effect = Blur((motion.Blur?.Eval(frame) ?? 0) * ScreenScale);
        }

        /// <summary>흐림(썸네일 픽셀 시그마)의 흐림 효과(없으면 null).</summary>
        private static Effect Blur(double sigma)
        {
            var radius = Math.Max(0, sigma) * 3;
            return radius > 0.05 ? new BlurEffect { Radius = radius } : null;
        }

        private static void Reset(UIElement element)
        {
            element.RenderTransform = Transform.Identity;
            element.Opacity = 1;
            element.Clip = null;
            element.Effect = null;
        }

        private static void Apply(TextBlock letter, UnitPose pose)
        {
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(pose.Scale, pose.Scale));
            group.Children.Add(new RotateTransform(pose.Rotation));
            group.Children.Add(new TranslateTransform(pose.X, pose.Y));
            letter.RenderTransform = group;
            letter.Opacity = Math.Max(0, Math.Min(1, pose.Opacity));
            letter.Effect = Blur(pose.Blur);
            if (pose.Visible >= 1)
            {
                letter.Clip = null;
                return;
            }
            letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = letter.DesiredSize;
            var rect = new Rect(0, 0, size.Width, size.Height);
            var visible = Math.Max(0, pose.Visible);
            if (pose.Split)
            {
                if (pose.WipeX != 0)
                    rect = new Rect(size.Width * (1 - visible) / 2, 0, size.Width * visible, size.Height);
                if (pose.WipeY != 0)
                    rect = new Rect(rect.Left, size.Height * (1 - visible) / 2, rect.Width, size.Height * visible);
                letter.Clip = new RectangleGeometry(rect);
                return;
            }
            if (pose.WipeX != 0)
                rect = new Rect(pose.WipeX < 0 ? 0 : size.Width * (1 - visible), 0, size.Width * visible, size.Height);
            if (pose.WipeY != 0)
                rect = new Rect(rect.Left, pose.WipeY < 0 ? 0 : size.Height * (1 - visible), rect.Width, size.Height * visible);
            letter.Clip = new RectangleGeometry(rect);
        }
    }
}
