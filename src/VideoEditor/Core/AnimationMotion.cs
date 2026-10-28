using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 시간식(클립 안 장 번호 τ의 함수 — 클립 시작이 0). 같은 나무로 C#에서 값을 구하고(시험·효과 썸네일·불투명도 명령) FFmpeg 식 글로도 쓴다.
    /// FFmpeg 식에서 τ는 필터의 시각 변수 t를 장 번호로 반올림해 그 흐름이 시작한 장을 더한 글(<see cref="Write"/>의 time)로 들어간다 —
    /// 초로 계산하면 구간 끝 장의 진행이 0.9999…가 되어(overlay는 위치를 버림으로 정수로 만든다) 끝 자리에서 한 픽셀 어긋났다. 장 번호는 정수라 시작·끝에서 정확히 0·1이다.
    /// </summary>
    public abstract class MotionExpr
    {
        public abstract double Eval(double time);

        internal abstract void Write(StringBuilder text, string time);

        /// <summary>상수면 그 값(아니면 null) — 식을 줄이는 데 쓴다.</summary>
        internal virtual double? Constant { get { return null; } }

        /// <summary>FFmpeg 식 글(time = τ를 나타내는 글, 예: "(t+0.5)").</summary>
        public string ToFfmpeg(string time)
        {
            var text = new StringBuilder();
            Write(text, time);
            return text.ToString();
        }

        // ---------- 만들기(상수는 미리 계산) ----------

        public static MotionExpr Num(double value)
        {
            return new ConstExpr(value);
        }

        public static MotionExpr Time { get; } = new TimeExpr();

        public static MotionExpr operator +(MotionExpr a, MotionExpr b)
        {
            if (a.Constant == 0)
                return b;
            if (b.Constant == 0)
                return a;
            return Fold('+', a, b, (x, y) => x + y);
        }

        public static MotionExpr operator -(MotionExpr a, MotionExpr b)
        {
            if (b.Constant == 0)
                return a;
            return Fold('-', a, b, (x, y) => x - y);
        }

        public static MotionExpr operator *(MotionExpr a, MotionExpr b)
        {
            if (a.Constant == 1)
                return b;
            if (b.Constant == 1)
                return a;
            if (a.Constant == 0 || b.Constant == 0)
                return Num(0);
            return Fold('*', a, b, (x, y) => x * y);
        }

        public static MotionExpr operator /(MotionExpr a, MotionExpr b)
        {
            if (b.Constant == 1)
                return a;
            return Fold('/', a, b, (x, y) => x / y);
        }

        public static implicit operator MotionExpr(double value)
        {
            return Num(value);
        }

        private static MotionExpr Fold(char op, MotionExpr a, MotionExpr b, Func<double, double, double> apply)
        {
            if (a.Constant.HasValue && b.Constant.HasValue)
                return Num(apply(a.Constant.Value, b.Constant.Value));
            return new BinaryExpr(op, a, b, apply);
        }

        public static MotionExpr Call(string name, Func<double[], double> apply, params MotionExpr[] args)
        {
            if (args.All(a => a.Constant.HasValue))
                return Num(apply(args.Select(a => a.Constant.Value).ToArray()));
            return new CallExpr(name, apply, args);
        }

        public static MotionExpr Min(MotionExpr a, MotionExpr b)
        {
            return Call("min", v => Math.Min(v[0], v[1]), a, b);
        }

        public static MotionExpr Max(MotionExpr a, MotionExpr b)
        {
            return Call("max", v => Math.Max(v[0], v[1]), a, b);
        }

        /// <summary>0~1로 자른다.</summary>
        public static MotionExpr Clip01(MotionExpr x)
        {
            return Call("clip", v => Math.Max(v[1], Math.Min(v[2], v[0])), x, 0, 1);
        }

        public static MotionExpr Sin(MotionExpr x)
        {
            return Call("sin", v => Math.Sin(v[0]), x);
        }

        public static MotionExpr Floor(MotionExpr x)
        {
            return Call("floor", v => Math.Floor(v[0]), x);
        }

        public static MotionExpr Pow(MotionExpr x, MotionExpr y)
        {
            return Call("pow", v => Math.Pow(v[0], v[1]), x, y);
        }

        /// <summary>a &lt; b면 1, 아니면 0.</summary>
        public static MotionExpr Lt(MotionExpr a, MotionExpr b)
        {
            return Call("lt", v => v[0] < v[1] ? 1 : 0, a, b);
        }

        /// <summary>a ≥ b면 1, 아니면 0.</summary>
        public static MotionExpr Gte(MotionExpr a, MotionExpr b)
        {
            return Call("gte", v => v[0] >= v[1] ? 1 : 0, a, b);
        }

        /// <summary>a ≤ b면 1, 아니면 0.</summary>
        public static MotionExpr Lte(MotionExpr a, MotionExpr b)
        {
            return Call("lte", v => v[0] <= v[1] ? 1 : 0, a, b);
        }

        /// <summary>조건이 0이 아니면 a, 0이면 b(FFmpeg if — 고른 쪽만 계산).</summary>
        public static MotionExpr If(MotionExpr condition, MotionExpr a, MotionExpr b)
        {
            if (condition.Constant.HasValue)
                return condition.Constant.Value != 0 ? a : b;
            return new IfExpr(condition, a, b);
        }

        private sealed class ConstExpr : MotionExpr
        {
            private readonly double _value;

            public ConstExpr(double value)
            {
                _value = value;
            }

            internal override double? Constant { get { return _value; } }

            public override double Eval(double time)
            {
                return _value;
            }

            internal override void Write(StringBuilder text, string time)
            {
                var number = RenderGraph.N(_value);
                if (number.StartsWith("-", StringComparison.Ordinal))
                    text.Append('(').Append(number).Append(')');
                else
                    text.Append(number);
            }
        }

        private sealed class TimeExpr : MotionExpr
        {
            public override double Eval(double time)
            {
                return time;
            }

            internal override void Write(StringBuilder text, string time)
            {
                text.Append(time);
            }
        }

        private sealed class BinaryExpr : MotionExpr
        {
            private readonly char _op;
            private readonly MotionExpr _a;
            private readonly MotionExpr _b;
            private readonly Func<double, double, double> _apply;

            public BinaryExpr(char op, MotionExpr a, MotionExpr b, Func<double, double, double> apply)
            {
                _op = op;
                _a = a;
                _b = b;
                _apply = apply;
            }

            public override double Eval(double time)
            {
                return _apply(_a.Eval(time), _b.Eval(time));
            }

            internal override void Write(StringBuilder text, string time)
            {
                text.Append('(');
                _a.Write(text, time);
                text.Append(_op);
                _b.Write(text, time);
                text.Append(')');
            }
        }

        private sealed class CallExpr : MotionExpr
        {
            private readonly string _name;
            private readonly Func<double[], double> _apply;
            private readonly MotionExpr[] _args;

            public CallExpr(string name, Func<double[], double> apply, MotionExpr[] args)
            {
                _name = name;
                _apply = apply;
                _args = args;
            }

            public override double Eval(double time)
            {
                return _apply(_args.Select(a => a.Eval(time)).ToArray());
            }

            internal override void Write(StringBuilder text, string time)
            {
                text.Append(_name).Append('(');
                for (var i = 0; i < _args.Length; i++)
                {
                    if (i > 0)
                        text.Append(',');
                    _args[i].Write(text, time);
                }
                text.Append(')');
            }
        }

        private sealed class IfExpr : MotionExpr
        {
            private readonly MotionExpr _condition;
            private readonly MotionExpr _a;
            private readonly MotionExpr _b;

            public IfExpr(MotionExpr condition, MotionExpr a, MotionExpr b)
            {
                _condition = condition;
                _a = a;
                _b = b;
            }

            public override double Eval(double time)
            {
                return _condition.Eval(time) != 0 ? _a.Eval(time) : _b.Eval(time);
            }

            internal override void Write(StringBuilder text, string time)
            {
                text.Append("if(");
                _condition.Write(text, time);
                text.Append(',');
                _a.Write(text, time);
                text.Append(',');
                _b.Write(text, time);
                text.Append(')');
            }
        }
    }

    /// <summary>
    /// 한 번에 움직이는 것(클립 전체)의 시간식: 크기 배율·더할 회전(도)·불투명도 배율·더할 위치(프로젝트 픽셀)·화면 밖으로 가는 정도(날아오기)·보이는 비율(닦아내기).
    /// null인 값은 움직이지 않는다(그래프에 그 필터를 넣지 않는다).
    /// </summary>
    public sealed class ObjectMotion
    {
        public MotionExpr Scale { get; set; }
        public MotionExpr Rotation { get; set; }
        public MotionExpr Opacity { get; set; }
        public MotionExpr ShiftX { get; set; }
        public MotionExpr ShiftY { get; set; }
        /// <summary>날아오기: 무게(1 = 그 방향 화면 밖 — 오른쪽 가장자리가 화면 왼쪽 끝 등, 0 = 제자리)와 방향(-1·0·1).</summary>
        public List<(MotionExpr Weight, int Dx, int Dy)> Fly { get; } = new List<(MotionExpr, int, int)>();
        /// <summary>닦아내기: 보이는 비율(0~1)과 드러나기 시작하는 쪽(-1 = 왼쪽·위, 1 = 오른쪽·아래).</summary>
        public List<(MotionExpr Visible, int Dx, int Dy)> Wipes { get; } = new List<(MotionExpr, int, int)>();
        /// <summary>크기가 0까지 가는지(확대·돌며 나타나기) — 0이면 그래프가 화면 밖으로 치운다(1픽셀 점이 남지 않게).</summary>
        public bool CanVanish { get; set; }
        /// <summary>흐림(가우스 시그마, 프로젝트 픽셀 — 0이면 선명). 그래프가 장마다 값을 보낸다.</summary>
        public MotionExpr Blur { get; set; }
        /// <summary>색 바꾸기: 세기(0~1 — 0이면 그대로)와 물들일 색("#RRGGBB").</summary>
        public List<(MotionExpr Amount, string Color)> Tints { get; } = new List<(MotionExpr, string)>();
        /// <summary>광선: 세기(0~1 — 0이면 없음), 크기(프로젝트 픽셀), 색("#RRGGBB").</summary>
        public List<(MotionExpr Strength, double Size, string Color)> Glows { get; } = new List<(MotionExpr, double, string)>();

        public bool IsEmpty
        {
            get
            {
                return Scale == null && Rotation == null && Opacity == null && ShiftX == null && ShiftY == null && Fly.Count == 0 && Wipes.Count == 0 && Blur == null
                    && Tints.Count == 0 && Glows.Count == 0;
            }
        }
    }

    /// <summary>
    /// 글자 단위 하나의 모습(출력 픽셀): 옮김·크기·회전(도, 단위 가운데 기준)·불투명도·닦아내기(보이는 비율과 쪽 — Split이면 가운데에서 그 축으로)·흐림(가우스 시그마)·
    /// 굵기(윤곽을 그만큼 넓힘).
    /// </summary>
    public struct UnitPose
    {
        public double X;
        public double Y;
        public double Scale;
        public double Rotation;
        public double Opacity;
        public double Visible;
        public int WipeX;
        public int WipeY;
        public bool Split;
        public double Blur;
        public double Bold;

        public static UnitPose Rest { get { return new UnitPose { Scale = 1, Opacity = 1, Visible = 1 }; } }

        public bool IsRest
        {
            get { return X == 0 && Y == 0 && Scale == 1 && Rotation == 0 && Opacity == 1 && Visible >= 1 && Blur == 0 && Bold == 0; }
        }

        /// <summary>두 모습을 겹친다(옮김·회전·흐림·굵기는 더하고 크기·불투명도는 곱함, 닦아내기는 덜 보이는 쪽).</summary>
        public UnitPose Then(UnitPose other)
        {
            var wipe = other.Visible < Visible ? other : this;
            return new UnitPose
            {
                X = X + other.X,
                Y = Y + other.Y,
                Scale = Scale * other.Scale,
                Rotation = Rotation + other.Rotation,
                Opacity = Opacity * other.Opacity,
                Visible = wipe.Visible,
                WipeX = wipe.WipeX,
                WipeY = wipe.WipeY,
                Split = wipe.Split,
                Blur = Blur + other.Blur,
                Bold = Bold + other.Bold
            };
        }
    }

    /// <summary>
    /// 애니메이션의 시간 계산(순수 함수). 나타나기는 진행 p(0 → 1)에 이징을 건 e로 "없음 → 제자리", 사라지기는 나타나기를 거꾸로 돌린 것(e = 이징(1 − p)),
    /// 강조는 한 번의 길이마다 되풀이한다. 구간 끝(τ ≥ 끝)에서는 p = 1이라 정확히 끝 모습에 닿는다.
    /// </summary>
    public static class AnimationMotion
    {
        /// <summary>커지며 돌기가 시작하는 크기(제자리의 몇 배).</summary>
        public const double GrowTurnStart = 0.2;

        // ---------- 이징 ----------

        /// <summary>이징(p: 0~1 → 0~1). 서서히 시작 p², 서서히 끝 1 − (1 − p)², 둘 다 smoothstep(전환 효과의 이징과 같은 식), 튕김·탄성은 끝에서 튀거나 출렁인다.</summary>
        public static double Ease(AnimationEase ease, double p)
        {
            p = Math.Max(0, Math.Min(1, p));
            switch (ease)
            {
                case AnimationEase.In:
                    return p * p;
                case AnimationEase.Out:
                    return 1 - (1 - p) * (1 - p);
                case AnimationEase.InOut:
                    return p * p * (3 - 2 * p);
                case AnimationEase.Bounce:
                    const double n = 7.5625, d = 2.75;
                    if (p < 1 / d)
                        return n * p * p;
                    if (p < 2 / d)
                        return n * (p - 1.5 / d) * (p - 1.5 / d) + 0.75;
                    if (p < 2.5 / d)
                        return n * (p - 2.25 / d) * (p - 2.25 / d) + 0.9375;
                    return n * (p - 2.625 / d) * (p - 2.625 / d) + 0.984375;
                case AnimationEase.Elastic:
                    if (p <= 0)
                        return 0;
                    if (p >= 1)
                        return 1;
                    return Math.Pow(2, -10 * p) * Math.Sin((10 * p - 0.75) * (2 * Math.PI / 3)) + 1;
                default:
                    return p;
            }
        }

        /// <summary><see cref="Ease(AnimationEase, double)"/>와 같은 계산의 시간식(p는 0~1로 자른 식).</summary>
        public static MotionExpr Ease(AnimationEase ease, MotionExpr p)
        {
            switch (ease)
            {
                case AnimationEase.In:
                    return p * p;
                case AnimationEase.Out:
                    return 1 - (1 - p) * (1 - p);
                case AnimationEase.InOut:
                    return p * p * (3 - 2 * p);
                case AnimationEase.Bounce:
                    const double n = 7.5625, d = 2.75;
                    MotionExpr Part(double shift, double add) => n * (p - shift / d) * (p - shift / d) + add;
                    return MotionExpr.If(MotionExpr.Lt(p, 1 / d), n * p * p,
                        MotionExpr.If(MotionExpr.Lt(p, 2 / d), Part(1.5, 0.75),
                            MotionExpr.If(MotionExpr.Lt(p, 2.5 / d), Part(2.25, 0.9375), Part(2.625, 0.984375))));
                case AnimationEase.Elastic:
                    return MotionExpr.If(MotionExpr.Lte(p, 0), 0, MotionExpr.If(MotionExpr.Gte(p, 1), 1,
                        MotionExpr.Pow(2, -10 * p) * MotionExpr.Sin((10 * p - 0.75) * (2 * Math.PI / 3)) + 1));
                default:
                    return p;
            }
        }

        // ---------- 구간 ----------

        /// <summary>
        /// 나타나기·사라지기의 구간(클립 안 프레임 [Start, Start + Length)). 나타나기는 지연 뒤부터, 사라지기는 끝에서 지연만큼 앞에서 끝난다.
        /// 사라지기는 나타나기가 끝난 뒤에서 시작한다(짧은 클립에서 둘이 겹치면 사라지기를 줄인다).
        /// </summary>
        public static (long Start, long Length) Window(Clip clip, ClipAnimation animation)
        {
            var duration = clip.Duration;
            if (animation.Kind == AnimationKind.Out)
            {
                var end = Math.Max(0, duration - Math.Max(0, animation.Delay));
                var start = Math.Max(0, end - Math.Max(1, animation.Duration));
                var before = ClipAnimations.In(clip);
                if (before != null)
                {
                    var (inStart, inLength) = Window(clip, before);
                    start = Math.Min(end, Math.Max(start, inStart + inLength));
                }
                return (start, end - start);
            }
            var begin = Math.Min(duration, Math.Max(0, animation.Delay));
            return (begin, Math.Min(Math.Max(1, animation.Duration), duration - begin));
        }

        /// <summary>강조의 구간: 시작, 한 번의 길이, 끝(반복을 다 한 뒤 또는 클립 끝).</summary>
        public static (long Start, long Cycle, long End) EmphasisWindow(Clip clip, ClipAnimation animation)
        {
            var duration = clip.Duration;
            var start = Math.Min(duration, Math.Max(0, animation.Delay));
            var cycle = Math.Max(1, animation.Duration);
            var def = ClipAnimations.Def(animation.Effect);
            long count = def != null && def.Repeats ? animation.Repeat : 1;
            if (count <= 0)
                count = (duration - start + cycle - 1) / cycle;
            return (start, cycle, Math.Min(duration, start + Math.Max(1, count) * cycle));
        }

        /// <summary>구간 진행(0~1, 시간식 — 장 번호). 길이가 0이면 시작에서 바로 1.</summary>
        private static MotionExpr Progress(long start, long length)
        {
            var time = MotionExpr.Time;
            return length > 0 ? MotionExpr.Clip01((time - start) / length) : MotionExpr.Gte(time, start);
        }

        private static (int X, int Y) Vector(AnimationDirection direction)
        {
            switch (direction)
            {
                case AnimationDirection.Left: return (-1, 0);
                case AnimationDirection.Right: return (1, 0);
                case AnimationDirection.Up: return (0, -1);
                case AnimationDirection.Down: return (0, 1);
                case AnimationDirection.UpLeft: return (-1, -1);
                case AnimationDirection.UpRight: return (1, -1);
                case AnimationDirection.DownLeft: return (-1, 1);
                default: return (1, 1);
            }
        }

        /// <summary>
        /// 그리개가 장마다 그리는 효과인지: 단어·글자별 나타나기·사라지기, 글자 전용 강조(물결·굵게 깜빡이기 — 한 번에여도 글자 모양이 바뀐다).
        /// </summary>
        public static bool IsPerUnit(ClipAnimation animation)
        {
            if (animation == null)
                return false;
            if (animation.Kind == AnimationKind.Emphasis)
                return ClipAnimations.Def(animation.Effect)?.TextOnly == true;
            return animation.Unit != AnimationUnit.All;
        }

        /// <summary>그리개가 장마다 그리는 효과(<see cref="IsPerUnit"/>)가 든 글자 클립인지.</summary>
        public static bool HasPerUnit(Clip clip)
        {
            return clip != null && clip.Kind == ClipKind.Text
                && (IsPerUnit(ClipAnimations.In(clip)) || IsPerUnit(ClipAnimations.Out(clip)) || ClipAnimations.Emphases(clip).Any(IsPerUnit));
        }

        // ---------- 클립 전체 ----------

        /// <summary>
        /// 클립 전체의 움직임(시간식, τ = 클립 시작부터의 장 번호). perUnitDrawn이면 그리개가 그리는 효과(<see cref="IsPerUnit"/>)는 그림에 들어가므로 빼고,
        /// 아니면(그리개 없음·긴 글·글자가 아닌 클립) 한 번에 움직이는 비슷한 모습으로 대신한다 — 타자기는 왼쪽부터 닦아내기, 흩어지기는 서서히,
        /// 물결은 글 전체가 솟았다 내려앉기, 굵게 깜빡이기는 조금 커졌다 돌아오기.
        /// </summary>
        public static ObjectMotion ObjectMotion(Clip clip, bool perUnitDrawn)
        {
            var motion = new ObjectMotion();
            if (clip?.Animations == null)
                return motion;
            MotionExpr scale = null, rotation = null, opacity = null, shiftX = null, shiftY = null;
            void Scale(MotionExpr factor)
            {
                scale = scale == null ? factor : scale * factor;
            }
            void Rotate(MotionExpr degrees)
            {
                rotation = rotation == null ? degrees : rotation + degrees;
            }
            void Fade(MotionExpr factor)
            {
                opacity = opacity == null ? factor : opacity * factor;
            }
            void Blur(MotionExpr sigma)
            {
                motion.Blur = motion.Blur == null ? sigma : motion.Blur + sigma;
            }
            foreach (var animation in clip.Animations)
            {
                var def = ClipAnimations.Def(animation.Effect);
                if (def == null)
                    continue;
                if (perUnitDrawn && IsPerUnit(animation))
                    continue;
                if (animation.Kind == AnimationKind.Emphasis)
                {
                    Emphasis(clip, animation, def, motion, Scale, Rotate, Fade, (dx, dy) =>
                    {
                        if (dx != null)
                            shiftX = shiftX == null ? dx : shiftX + dx;
                        if (dy != null)
                            shiftY = shiftY == null ? dy : shiftY + dy;
                    });
                    continue;
                }
                var (start, length) = Window(clip, animation);
                var p = Progress(start, length);
                // 진행 → 제자리에 가까운 정도(나타나기: p, 사라지기: 1 − p)
                var rest = animation.Kind == AnimationKind.In ? p : 1 - p;
                var e = Ease(animation.Ease, rest);
                var (vx, vy) = Vector(animation.Direction);
                switch (animation.Effect)
                {
                    case AnimationEffect.Fade:
                    case AnimationEffect.Scatter:
                        Fade(e);
                        break;
                    case AnimationEffect.Fly:
                    case AnimationEffect.Bounce:
                        motion.Fly.Add((1 - e, vx, vy));
                        break;
                    case AnimationEffect.Zoom:
                        Scale(e);
                        motion.CanVanish = true;
                        break;
                    case AnimationEffect.Wipe:
                        motion.Wipes.Add((e, vx, vy));
                        break;
                    case AnimationEffect.Typewriter:
                        motion.Wipes.Add((rest, -1, 0));
                        break;
                    case AnimationEffect.Spin:
                        Scale(e);
                        Rotate((animation.Direction == AnimationDirection.Left ? 1 : -1) * animation.Amount * (1 - e));
                        motion.CanVanish = true;
                        break;
                    case AnimationEffect.Rise:
                        // 거리(프로젝트 픽셀)만큼 아래에서
                        shiftY = shiftY == null ? animation.Amount * (1 - e) : shiftY + animation.Amount * (1 - e);
                        Fade(e);
                        break;
                    case AnimationEffect.GrowTurn:
                        Scale(1 - (1 - GrowTurnStart) * (1 - e));
                        Rotate((animation.Direction == AnimationDirection.Left ? 1 : -1) * animation.Amount * (1 - e));
                        Fade(e);
                        break;
                    case AnimationEffect.Split:
                    {
                        // 가운데에서 열림 = 왼쪽부터 (1 + e) / 2 와 오른쪽부터 (1 + e) / 2 가 겹친 곳. 처음(e = 0)은 반씩이 맞닿아 한 줄이 남을 수 있어 0으로
                        var half = MotionExpr.If(MotionExpr.Lte(e, 0), 0, (1 + e) / 2);
                        var vertical = animation.Direction == AnimationDirection.Up || animation.Direction == AnimationDirection.Down;
                        motion.Wipes.Add((half, vertical ? 0 : -1, vertical ? -1 : 0));
                        motion.Wipes.Add((half, vertical ? 0 : 1, vertical ? 1 : 0));
                        break;
                    }
                    case AnimationEffect.Unblur:
                        Blur(animation.Amount * MotionExpr.Max(0, 1 - e));
                        // 흐린 동안 먼저 보이기 시작한다(반쯤에 다 보임)
                        Fade(MotionExpr.Min(1, 2 * e));
                        break;
                    case AnimationEffect.Fall:
                        // 사라지기 전용: 화면 아래 밖으로, 기울기만큼 기울며(서서히 끝 이징을 거꾸로 돌리면 점점 빨라진다)
                        motion.Fly.Add((1 - e, 0, 1));
                        Rotate((animation.Direction == AnimationDirection.Left ? 1 : -1) * animation.Amount * (1 - e));
                        break;
                }
            }
            motion.Scale = scale;
            motion.Rotation = rotation;
            motion.Opacity = opacity;
            motion.ShiftX = shiftX;
            motion.ShiftY = shiftY;
            return motion;
        }

        private static void Emphasis(Clip clip, ClipAnimation animation, AnimationEffectDef def, ObjectMotion motion,
            Action<MotionExpr> scale, Action<MotionExpr> rotate, Action<MotionExpr> fade, Action<MotionExpr, MotionExpr> shift)
        {
            var (start, cycle, end) = EmphasisWindow(clip, animation);
            var time = MotionExpr.Time;
            var u = (time - start) / cycle;
            // 되풀이하는 효과의 한 번 안 진행(구간 밖은 0 — 맥박·흔들기는 0에서 제자리)
            var inside = MotionExpr.Gte(time, start) * MotionExpr.Lt(time, end);
            var phase = MotionExpr.If(inside, u - MotionExpr.Floor(u), 0);
            var sign = animation.Direction == AnimationDirection.Left ? -1 : 1;
            switch (animation.Effect)
            {
                case AnimationEffect.Pulse:
                    scale(1 + animation.Amount / 100 * MotionExpr.Sin(Math.PI * Ease(animation.Ease, phase)));
                    break;
                case AnimationEffect.Shake:
                {
                    var e = Ease(animation.Ease, phase);
                    var swing = animation.Amount * MotionExpr.Sin(6 * Math.PI * e) * (1 - e);
                    var vertical = animation.Direction == AnimationDirection.Up || animation.Direction == AnimationDirection.Down;
                    shift(vertical ? null : swing, vertical ? swing : null);
                    break;
                }
                case AnimationEffect.Rotate:
                {
                    // 다 돈 횟수 + 지금 한 번의 진행 — 끝나면 마지막 각도에 남는다(360°의 배수면 제자리)
                    var count = Math.Max(1, (end - start + cycle - 1) / cycle);
                    var turns = MotionExpr.Clip01(u / count) * count;
                    var done = MotionExpr.Min(MotionExpr.Floor(turns), count);
                    rotate(sign * animation.Amount * (done + MotionExpr.If(MotionExpr.Lt(turns, count), Ease(animation.Ease, turns - done), 0)));
                    break;
                }
                case AnimationEffect.Grow:
                    scale(1 + (animation.Amount / 100 - 1) * Ease(animation.Ease, MotionExpr.Clip01(u)));
                    break;
                case AnimationEffect.Transparency:
                    fade(1 + (animation.Amount / 100 - 1) * Ease(animation.Ease, MotionExpr.Clip01(u)));
                    break;
                case AnimationEffect.ColorChange:
                    motion.Tints.Add((animation.Amount / 100 * Ease(animation.Ease, MotionExpr.Clip01(u)), ClipAnimations.Rgb(animation.Color, def.DefaultColor)));
                    break;
                case AnimationEffect.Glow:
                    motion.Glows.Add((MotionExpr.Sin(Math.PI * Ease(animation.Ease, phase)), animation.Amount, ClipAnimations.Rgb(animation.Color, def.DefaultColor)));
                    break;
                case AnimationEffect.Wave:
                    // 그리개가 없을 때(긴 글 등): 글 전체가 글자 크기의 정도(%)만큼 솟았다 내려앉는다
                    shift(null, -clip.FontSize * animation.Amount / 100 * MotionExpr.Sin(Math.PI * Ease(animation.Ease, phase)));
                    break;
                case AnimationEffect.BoldFlash:
                    // 그리개가 없을 때: 굵기 대신 조금 커졌다 돌아온다
                    scale(1 + animation.Amount / 100 * MotionExpr.Sin(Math.PI * Ease(animation.Ease, phase)));
                    break;
            }
        }

        /// <summary>
        /// 글자 전용 강조(물결·굵게 깜빡이기)의 단위 하나의 모습(frame = 클립 안 장, start·cycle·end = <see cref="EmphasisWindow"/>, size = 글자 크기(출력 픽셀)).
        /// 한 번의 길이 안에서 단위마다 지연(%)만큼 늦게 시작해 그 몫의 시간 동안 솟았다(굵어졌다) 돌아온다 — 한 번의 시작·끝에서 모두 제자리라 되풀이가 이어진다.
        /// </summary>
        public static UnitPose EmphasisPose(ClipAnimation animation, long frame, long start, long cycle, long end, int index, int count, double size)
        {
            var pose = UnitPose.Rest;
            if (frame < start || frame >= end || cycle <= 0)
                return pose;
            var u = (frame - start) / (double)cycle;
            var phase = u - Math.Floor(u);
            count = Math.Max(1, count);
            index = Math.Max(0, Math.Min(count - 1, index));
            var stagger = Math.Max(0, Math.Min(100, animation.Stagger)) / 100;
            var span = 1 / (1 + (count - 1) * stagger);
            var q = Math.Max(0, Math.Min(1, (phase - index * stagger * span) / span));
            var swell = Math.Sin(Math.PI * Ease(animation.Ease, q));
            switch (animation.Effect)
            {
                case AnimationEffect.Wave:
                    pose.Y = -size * animation.Amount / 100 * swell;
                    break;
                case AnimationEffect.BoldFlash:
                    pose.Bold = Math.Max(0, size * animation.Amount / 100 * swell);
                    break;
            }
            return pose;
        }

        // ---------- 단어·글자별 ----------

        /// <summary>
        /// 단위 하나의 나타나기 모습(p = 구간 진행 0~1, index/count = 단위 차례·수, size = 글자 크기(출력 픽셀) — 옮기는 거리의 기준,
        /// pixel = 프로젝트 픽셀 하나의 출력 픽셀 — 정도가 픽셀인 효과(떠오르기 거리·흐림)가 한 번에 움직일 때와 같은 크기로).
        /// 단위마다 지연(%)만큼 늦게 시작하고 그 몫의 시간 동안 움직인다 — 마지막 단위도 구간 끝에 끝난다. 사라지기는 <see cref="OutPose"/>(시간을 거꾸로).
        /// </summary>
        public static UnitPose InPose(ClipAnimation animation, double p, int index, int count, double size, double pixel = 1)
        {
            count = Math.Max(1, count);
            index = Math.Max(0, Math.Min(count - 1, index));
            var stagger = Math.Max(0, Math.Min(100, animation.Stagger)) / 100;
            var span = 1 / (1 + (count - 1) * stagger);
            var begin = index * stagger * span;
            var q = Math.Max(0, Math.Min(1, (p - begin) / span));
            var e = Ease(animation.Ease, q);
            var pose = UnitPose.Rest;
            var (vx, vy) = Vector(animation.Direction);
            switch (animation.Effect)
            {
                case AnimationEffect.Fade:
                    pose.Opacity = e;
                    break;
                case AnimationEffect.Fly:
                    pose.X = vx * (1 - e) * 1.5 * size;
                    pose.Y = vy * (1 - e) * 1.5 * size;
                    pose.Opacity = Math.Min(1, q * 3);
                    break;
                case AnimationEffect.Bounce:
                    pose.X = vx * (1 - e) * 1.2 * size;
                    pose.Y = vy * (1 - e) * 1.2 * size;
                    pose.Opacity = Math.Min(1, q * 4);
                    break;
                case AnimationEffect.Zoom:
                    pose.Scale = e;
                    break;
                case AnimationEffect.Wipe:
                    pose.Visible = e;
                    pose.WipeX = vx;
                    pose.WipeY = vy;
                    break;
                case AnimationEffect.Spin:
                    pose.Scale = e;
                    pose.Rotation = (animation.Direction == AnimationDirection.Left ? 1 : -1) * animation.Amount * (1 - e);
                    break;
                case AnimationEffect.Typewriter:
                    // 자기 차례가 오면 바로 찍힌다
                    pose.Opacity = q > 0 ? 1 : 0;
                    break;
                case AnimationEffect.Scatter:
                {
                    var angle = 2 * Math.PI * Hash(index, 1);
                    var distance = (2 + 2 * Hash(index, 2)) * size * animation.Amount / 100;
                    pose.X = Math.Cos(angle) * distance * (1 - e);
                    pose.Y = Math.Sin(angle) * distance * (1 - e);
                    pose.Rotation = (Hash(index, 3) * 2 - 1) * 240 * (1 - e);
                    pose.Opacity = Math.Min(1, q * 2);
                    break;
                }
                case AnimationEffect.Rise:
                    // 거리(프로젝트 픽셀)만큼 아래에서
                    pose.Y = (1 - e) * animation.Amount * pixel;
                    pose.Opacity = Math.Max(0, Math.Min(1, e));
                    break;
                case AnimationEffect.GrowTurn:
                    pose.Scale = 1 - (1 - GrowTurnStart) * (1 - e);
                    pose.Rotation = (animation.Direction == AnimationDirection.Left ? 1 : -1) * animation.Amount * (1 - e);
                    pose.Opacity = Math.Max(0, Math.Min(1, e));
                    break;
                case AnimationEffect.Split:
                {
                    var vertical = animation.Direction == AnimationDirection.Up || animation.Direction == AnimationDirection.Down;
                    pose.Visible = Math.Max(0, e);
                    pose.Split = true;
                    pose.WipeX = vertical ? 0 : 1;
                    pose.WipeY = vertical ? 1 : 0;
                    break;
                }
                case AnimationEffect.Unblur:
                    pose.Blur = animation.Amount * Math.Max(0, 1 - e) * pixel;
                    pose.Opacity = Math.Max(0, Math.Min(1, 2 * e));
                    break;
                case AnimationEffect.Fall:
                    // 글자 크기의 4배 아래로, 글자마다 조금씩 다르게 기울며 끝에서 흐려진다
                    pose.Y = (1 - e) * 4 * size;
                    pose.Rotation = (animation.Direction == AnimationDirection.Left ? 1 : -1) * animation.Amount * (0.6 + 0.8 * Hash(index, 4)) * (1 - e);
                    pose.Opacity = Math.Min(1, q * 3);
                    break;
            }
            return pose;
        }

        /// <summary>사라지기 = 나타나기를 거꾸로(p = 구간 진행 0~1) — 타자기는 뒤 글자부터 지워진다.</summary>
        public static UnitPose OutPose(ClipAnimation animation, double p, int index, int count, double size, double pixel = 1)
        {
            return InPose(animation, 1 - p, index, count, size, pixel);
        }

        /// <summary>단위 차례로 정해지는 0~1 값(흩어지는 방향·거리·회전) — 어느 PC에서나 같다.</summary>
        internal static double Hash(int index, int salt)
        {
            unchecked
            {
                var x = (uint)index * 0x9E3779B1u + (uint)salt * 0x85EBCA6Bu;
                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;
                return (x & 0xFFFFFF) / 16777216.0;
            }
        }
    }

    /// <summary>
    /// 글자 나누기: 글자 요소(StringInfo — 한글 음절·결합 문자·대리 쌍을 쪼개지 않음) 가운데 빈칸이 아닌 것만 차례로 센다.
    /// 그리개도 줄을 나눈 뒤 같은 차례로 세므로(줄 나눔은 빈칸만 버린다) 단위 번호가 맞는다.
    /// </summary>
    public static class TextUnits
    {
        /// <summary>빈칸 요소인지(모든 문자가 공백 — 줄 바꿈 포함).</summary>
        public static bool IsBlank(string element)
        {
            return element.All(char.IsWhiteSpace);
        }

        /// <summary>빈칸이 아닌 글자 요소(차례대로).</summary>
        public static List<string> Elements(string text)
        {
            var result = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(text ?? "");
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                if (!IsBlank(element))
                    result.Add(element);
            }
            return result;
        }

        /// <summary>
        /// 빈칸이 아닌 글자 요소마다 단위 번호와 단위 수: 글자별은 요소마다, 단어별은 빈칸(줄 바꿈 포함)으로 나뉜 덩어리마다, 한 번에는 모두 0.
        /// </summary>
        public static (int[] UnitOf, int Count) Split(string text, AnimationUnit unit)
        {
            var units = new List<int>();
            var count = 0;
            var gap = true;
            var enumerator = StringInfo.GetTextElementEnumerator(text ?? "");
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                if (IsBlank(element))
                {
                    gap = true;
                    continue;
                }
                if (unit == AnimationUnit.Character || (unit == AnimationUnit.Word && gap) || count == 0)
                    count++;
                gap = false;
                units.Add(unit == AnimationUnit.All ? 0 : count - 1);
            }
            return (units.ToArray(), unit == AnimationUnit.All ? Math.Min(1, count) : count);
        }
    }
}
