using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>애니메이션 분류: 나타나기(클립 시작 기준)·강조(시작 시각 기준, 반복)·사라지기(클립 끝 기준 — 나타나기를 거꾸로 돌린 움직임).</summary>
    public enum AnimationKind
    {
        In,
        Emphasis,
        Out
    }

    /// <summary>
    /// 효과. 나타나기·사라지기: 서서히·날아오기·확대·튀어 오르기·닦아내기·돌며 나타나기·타자기·흩어지기, 강조: 맥박·흔들기·회전·크게·작게·투명도 바꾸기.
    /// 저장된 이름이 바뀌면 예전 프로젝트에서 효과가 빠지므로 이름을 바꾸지 않는다(뒤에만 더한다).
    /// </summary>
    public enum AnimationEffect
    {
        Fade,
        Fly,
        Zoom,
        Bounce,
        Wipe,
        Spin,
        Typewriter,
        Scatter,
        Pulse,
        Shake,
        Rotate,
        Grow,
        Transparency
    }

    /// <summary>
    /// 방향: 나타나기는 들어오는 쪽, 사라지기는 나가는 쪽. 회전·돌며 나타나기는 왼쪽 = 시계 반대, 오른쪽 = 시계 방향, 흔들기는 왼쪽·오른쪽 = 가로, 위·아래 = 세로.
    /// </summary>
    public enum AnimationDirection
    {
        Left,
        Right,
        Up,
        Down,
        UpLeft,
        UpRight,
        DownLeft,
        DownRight
    }

    /// <summary>움직이는 단위(글자 클립만 단어·글자): 한 번에 / 단어별 / 글자별(한글은 음절).</summary>
    public enum AnimationUnit
    {
        All,
        Word,
        Character
    }

    /// <summary>이징: 일정·서서히 시작·서서히 끝·둘 다·튕김·탄성.</summary>
    public enum AnimationEase
    {
        Linear,
        In,
        Out,
        InOut,
        Bounce,
        Elastic
    }

    /// <summary>
    /// 클립에 넣은 애니메이션 하나(값만). 길이·지연은 프로젝트 프레임. 그리는 방법은 <see cref="AnimationMotion"/>(시간식)과 꾸민 글자 그리개(글자별)에 있다.
    /// </summary>
    public sealed class ClipAnimation
    {
        [JsonConverter(typeof(LenientEnumConverter<AnimationKind>))]
        public AnimationKind Kind { get; set; }
        /// <summary>효과 — 모르는 이름(새 판의 효과)이면 이 항목만 버린다(목록을 너그럽게 읽음).</summary>
        public AnimationEffect Effect { get; set; }
        [JsonConverter(typeof(LenientEnumConverter<AnimationDirection>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AnimationDirection Direction { get; set; }
        [JsonConverter(typeof(LenientEnumConverter<AnimationUnit>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AnimationUnit Unit { get; set; }
        /// <summary>단어·글자 사이 지연(%): 0 = 모두 함께, 100 = 앞 단위가 끝나면 다음 단위가 시작.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Stagger { get; set; }
        /// <summary>길이(프레임). 강조는 한 번의 길이.</summary>
        public long Duration { get; set; }
        /// <summary>지연(프레임): 나타나기·강조는 클립 시작에서, 사라지기는 클립 끝까지 남기는 시간.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long Delay { get; set; }
        /// <summary>반복 횟수(강조만, 0 = 클립 끝까지). 나타나기·사라지기는 1.</summary>
        public int Repeat { get; set; } = 1;
        [JsonConverter(typeof(LenientEnumConverter<AnimationEase>))]
        public AnimationEase Ease { get; set; }
        /// <summary>정도(효과마다 뜻과 단위가 다르다 — <see cref="AnimationEffectDef.Amount"/>). 정도가 없는 효과는 0.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Amount { get; set; }

        public ClipAnimation Clone()
        {
            return (ClipAnimation)MemberwiseClone();
        }
    }

    /// <summary>효과의 정도 매개변수(속성 패널의 숫자 줄).</summary>
    public sealed class AnimationAmount
    {
        public AnimationAmount(string name, double min, double max, double value, string unit)
        {
            Name = name;
            Min = min;
            Max = max;
            Default = value;
            Unit = unit;
        }

        public string Name { get; }
        public double Min { get; }
        public double Max { get; }
        public double Default { get; }
        public string Unit { get; }
    }

    /// <summary>효과 하나의 정의: 보일 이름, 어느 분류에 쓰는지, 고를 수 있는 방향·기본값.</summary>
    public sealed class AnimationEffectDef
    {
        public AnimationEffect Effect { get; init; }
        /// <summary>나타나기·강조에서 보일 이름.</summary>
        public string Name { get; init; }
        /// <summary>사라지기에서 보일 이름(없으면 <see cref="Name"/>).</summary>
        public string OutName { get; init; }
        public string Description { get; init; }
        /// <summary>강조 효과인지(아니면 나타나기·사라지기).</summary>
        public bool Emphasis { get; init; }
        /// <summary>글자별로만 움직이는 효과(타자기·흩어지기) — 글자 클립에만 넣는다.</summary>
        public bool TextOnly { get; init; }
        /// <summary>고를 수 있는 방향(비었으면 방향 없음). 첫 값이 기본.</summary>
        public IReadOnlyList<AnimationDirection> Directions { get; init; } = Array.Empty<AnimationDirection>();
        public AnimationEase DefaultEase { get; init; }
        public double DefaultSeconds { get; init; } = 0.5;
        public AnimationUnit DefaultUnit { get; init; }
        /// <summary>단위를 나눌 때의 기본 지연(%).</summary>
        public double DefaultStagger { get; init; } = 50;
        public int DefaultRepeat { get; init; } = 1;
        /// <summary>강조를 되풀이할 수 있는지(크게·작게·투명도 바꾸기는 바뀐 채 남는다).</summary>
        public bool Repeats { get; init; }
        public AnimationAmount Amount { get; init; }

        public string DisplayName(AnimationKind kind)
        {
            return kind == AnimationKind.Out && OutName != null ? OutName : Name;
        }

        public AnimationDirection DefaultDirection { get { return Directions.Count > 0 ? Directions[0] : AnimationDirection.Left; } }
    }

    /// <summary>
    /// 클립 애니메이션 목록과 규칙: 한 클립에 나타나기 1 + 강조 여러 개(<see cref="MaxEmphasis"/>) + 사라지기 1.
    /// 단어·글자별은 글자 클립만, 그 밖의 클립은 한 번에. 프로젝트 파일은 남이 만든 것일 수 있어 읽을 때 <see cref="Clean"/>으로 범위 안에 넣는다.
    /// </summary>
    public static class ClipAnimations
    {
        /// <summary>강조 효과 수 상한.</summary>
        public const int MaxEmphasis = 8;
        /// <summary>길이·지연 상한(프레임) — 글자별 그림 파일 번호(5자리)가 넘치지 않게.</summary>
        public const long MaxFrames = 99999;
        /// <summary>반복 횟수 상한.</summary>
        public const int MaxRepeat = 1000;

        private static readonly AnimationDirection[] EightWays =
        {
            AnimationDirection.Down, AnimationDirection.Up, AnimationDirection.Left, AnimationDirection.Right,
            AnimationDirection.DownLeft, AnimationDirection.DownRight, AnimationDirection.UpLeft, AnimationDirection.UpRight
        };

        private static readonly AnimationDirection[] FourWays = { AnimationDirection.Up, AnimationDirection.Down, AnimationDirection.Left, AnimationDirection.Right };
        private static readonly AnimationDirection[] Turns = { AnimationDirection.Right, AnimationDirection.Left };

        /// <summary>효과 목록(탭에 보이는 차례).</summary>
        public static IReadOnlyList<AnimationEffectDef> All { get; } = new[]
        {
            new AnimationEffectDef { Effect = AnimationEffect.Fade, Name = "서서히", Description = "투명에서 서서히 보이고, 서서히 투명해집니다.", DefaultSeconds = 0.5 },
            new AnimationEffectDef { Effect = AnimationEffect.Fly, Name = "날아오기", OutName = "날아가기", Description = "화면 밖에서 날아와 제자리에 멈추고, 화면 밖으로 날아갑니다.",
                Directions = EightWays, DefaultEase = AnimationEase.Out, DefaultSeconds = 0.6 },
            new AnimationEffectDef { Effect = AnimationEffect.Zoom, Name = "확대", OutName = "축소", Description = "점에서 커지며 나타나고, 작아지며 사라집니다.",
                DefaultEase = AnimationEase.Out, DefaultSeconds = 0.5 },
            new AnimationEffectDef { Effect = AnimationEffect.Bounce, Name = "튀어 오르기", OutName = "튀어 나가기", Description = "화면 밖에서 떨어져 바닥에 튀며 멈춥니다.",
                Directions = FourWays, DefaultEase = AnimationEase.Bounce, DefaultSeconds = 1.0 },
            new AnimationEffectDef { Effect = AnimationEffect.Wipe, Name = "닦아내기", Description = "한쪽 가장자리부터 드러나고, 같은 쪽으로 지워집니다.",
                Directions = new[] { AnimationDirection.Left, AnimationDirection.Right, AnimationDirection.Up, AnimationDirection.Down,
                    AnimationDirection.UpLeft, AnimationDirection.UpRight, AnimationDirection.DownLeft, AnimationDirection.DownRight }, DefaultSeconds = 0.6 },
            new AnimationEffectDef { Effect = AnimationEffect.Spin, Name = "돌며 나타나기", OutName = "돌며 사라지기", Description = "돌면서 커지며 나타나고, 돌면서 작아지며 사라집니다.",
                Directions = Turns, DefaultEase = AnimationEase.Out, DefaultSeconds = 0.8, Amount = new AnimationAmount("회전", 0, 1080, 360, "°") },
            new AnimationEffectDef { Effect = AnimationEffect.Typewriter, Name = "타자기", OutName = "타자기 지우기", Description = "글자가 하나씩 찍히고, 뒤에서부터 하나씩 지워집니다(글자 클립).",
                TextOnly = true, DefaultSeconds = 1.5, DefaultUnit = AnimationUnit.Character, DefaultStagger = 100 },
            new AnimationEffectDef { Effect = AnimationEffect.Scatter, Name = "모여들기", OutName = "흩어지기", Description = "글자가 사방에서 돌며 모여들고, 사방으로 흩어집니다(글자 클립).",
                TextOnly = true, DefaultEase = AnimationEase.Out, DefaultSeconds = 1.0, DefaultUnit = AnimationUnit.Character, DefaultStagger = 30,
                Amount = new AnimationAmount("거리", 20, 300, 100, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Pulse, Name = "맥박", Description = "커졌다 작아지기를 되풀이합니다.", Emphasis = true, Repeats = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, DefaultRepeat = 2, Amount = new AnimationAmount("커지는 정도", 1, 100, 15, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Shake, Name = "흔들기", Description = "좌우(또는 위아래)로 흔들리다 멈춥니다.", Emphasis = true, Repeats = true,
                Directions = new[] { AnimationDirection.Left, AnimationDirection.Up }, DefaultSeconds = 0.6, Amount = new AnimationAmount("흔들림 폭", 1, 200, 20, "px") },
            new AnimationEffectDef { Effect = AnimationEffect.Rotate, Name = "회전", Description = "제자리에서 돕니다.", Emphasis = true, Repeats = true,
                Directions = Turns, DefaultEase = AnimationEase.InOut, DefaultSeconds = 1.0, Amount = new AnimationAmount("회전", 1, 1080, 360, "°") },
            new AnimationEffectDef { Effect = AnimationEffect.Grow, Name = "크게·작게", Description = "정한 크기로 바뀐 뒤 그대로 남습니다.", Emphasis = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, Amount = new AnimationAmount("크기", 10, 400, 150, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Transparency, Name = "투명도 바꾸기", Description = "정한 불투명도로 바뀐 뒤 그대로 남습니다.", Emphasis = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, Amount = new AnimationAmount("불투명도", 0, 100, 40, "%") }
        };

        public static AnimationEffectDef Def(AnimationEffect effect)
        {
            return All.FirstOrDefault(d => d.Effect == effect);
        }

        /// <summary>목록 이름: "나타나기 · 서서히".</summary>
        public static string Name(ClipAnimation animation)
        {
            var def = animation == null ? null : Def(animation.Effect);
            return def == null ? "" : KindName(animation.Kind) + " · " + def.DisplayName(animation.Kind);
        }

        public static string KindName(AnimationKind kind)
        {
            return kind == AnimationKind.In ? "나타나기" : kind == AnimationKind.Out ? "사라지기" : "강조";
        }

        /// <summary>속성 패널에 보일 방향 이름(효과마다 뜻이 다르다).</summary>
        public static string DirectionName(AnimationEffect effect, AnimationDirection direction)
        {
            if (effect == AnimationEffect.Spin || effect == AnimationEffect.Rotate)
                return direction == AnimationDirection.Left ? "시계 반대" : "시계 방향";
            if (effect == AnimationEffect.Shake)
                return direction == AnimationDirection.Up || direction == AnimationDirection.Down ? "세로" : "가로";
            switch (direction)
            {
                case AnimationDirection.Left: return "왼쪽";
                case AnimationDirection.Right: return "오른쪽";
                case AnimationDirection.Up: return "위";
                case AnimationDirection.Down: return "아래";
                case AnimationDirection.UpLeft: return "왼쪽 위";
                case AnimationDirection.UpRight: return "오른쪽 위";
                case AnimationDirection.DownLeft: return "왼쪽 아래";
                default: return "오른쪽 아래";
            }
        }

        public static string UnitName(AnimationUnit unit)
        {
            return unit == AnimationUnit.Word ? "단어별" : unit == AnimationUnit.Character ? "글자별" : "한 번에";
        }

        public static string EaseName(AnimationEase ease)
        {
            switch (ease)
            {
                case AnimationEase.In: return "서서히 시작";
                case AnimationEase.Out: return "서서히 끝";
                case AnimationEase.InOut: return "서서히 시작·끝";
                case AnimationEase.Bounce: return "튕김";
                case AnimationEase.Elastic: return "탄성";
                default: return "일정";
            }
        }

        /// <summary>애니메이션을 넣을 수 있는 클립: 화면에 보이는 클립(동영상·사진·글자·요소 — 오디오·분리한 사운드는 아님).</summary>
        public static bool CanHave(Clip clip)
        {
            return clip != null && clip.IsVisual;
        }

        /// <summary>이 클립에 이 효과를 넣을 수 있는지(타자기·흩어지기는 글자 클립만).</summary>
        public static bool Allows(Clip clip, AnimationEffectDef def)
        {
            return CanHave(clip) && def != null && (!def.TextOnly || clip.Kind == ClipKind.Text);
        }

        /// <summary>기본값으로 새 애니메이션(분류가 효과와 맞지 않으면 null).</summary>
        public static ClipAnimation Create(AnimationEffectDef def, AnimationKind kind, FrameRate rate, Clip clip = null)
        {
            if (def == null || def.Emphasis != (kind == AnimationKind.Emphasis))
                return null;
            var unit = clip == null || clip.Kind == ClipKind.Text ? def.DefaultUnit : AnimationUnit.All;
            return new ClipAnimation
            {
                Kind = kind,
                Effect = def.Effect,
                Direction = def.DefaultDirection,
                Unit = unit,
                Stagger = unit == AnimationUnit.All ? 0 : def.DefaultStagger,
                Duration = Math.Max(1, Math.Min(MaxFrames, rate.ToFrames(def.DefaultSeconds))),
                Repeat = def.Emphasis ? def.DefaultRepeat : 1,
                Ease = def.DefaultEase,
                Amount = def.Amount?.Default ?? 0
            };
        }

        public static ClipAnimation In(Clip clip)
        {
            return clip?.Animations?.FirstOrDefault(a => a.Kind == AnimationKind.In);
        }

        public static ClipAnimation Out(Clip clip)
        {
            return clip?.Animations?.FirstOrDefault(a => a.Kind == AnimationKind.Out);
        }

        public static List<ClipAnimation> Emphases(Clip clip)
        {
            return clip?.Animations?.Where(a => a.Kind == AnimationKind.Emphasis).ToList() ?? new List<ClipAnimation>();
        }

        /// <summary>나타나기·사라지기를 바꾼다(null이면 뺀다). 목록 차례는 나타나기 → 강조 → 사라지기.</summary>
        public static void Set(Clip clip, AnimationKind kind, ClipAnimation animation)
        {
            if (kind == AnimationKind.Emphasis)
                throw new ArgumentException("강조는 Add·Remove로 고칩니다.", nameof(kind));
            var list = clip.Animations?.Where(a => a.Kind != kind).ToList() ?? new List<ClipAnimation>();
            if (animation != null)
            {
                animation.Kind = kind;
                list.Add(animation);
            }
            clip.Animations = Order(list);
        }

        /// <summary>강조를 하나 더한다(상한이면 EditException).</summary>
        public static void AddEmphasis(Clip clip, ClipAnimation animation)
        {
            var list = clip.Animations?.ToList() ?? new List<ClipAnimation>();
            if (list.Count(a => a.Kind == AnimationKind.Emphasis) >= MaxEmphasis)
                throw new EditException("강조 효과는 " + MaxEmphasis + "개까지 넣을 수 있습니다.");
            animation.Kind = AnimationKind.Emphasis;
            list.Add(animation);
            clip.Animations = Order(list);
        }

        /// <summary>index번째 강조를 뺀다.</summary>
        public static void RemoveEmphasis(Clip clip, int index)
        {
            var target = Emphases(clip).ElementAtOrDefault(index);
            if (target == null)
                return;
            var list = clip.Animations.Where(a => !ReferenceEquals(a, target)).ToList();
            clip.Animations = Order(list);
        }

        private static List<ClipAnimation> Order(List<ClipAnimation> list)
        {
            var ordered = list.Where(a => a.Kind == AnimationKind.In)
                .Concat(list.Where(a => a.Kind == AnimationKind.Emphasis))
                .Concat(list.Where(a => a.Kind == AnimationKind.Out)).ToList();
            return ordered.Count == 0 ? null : ordered;
        }

        /// <summary>애니메이션이 든 클립이 있는지(프로젝트 형식 4로 저장할지).</summary>
        public static bool Any(Project project)
        {
            return project.AllClips.Any(c => c.Animations != null && c.Animations.Count > 0);
        }

        /// <summary>
        /// 읽은 값(남이 만든 파일일 수 있음)을 규칙에 맞게: 보이는 클립에만, 효과와 분류가 맞는 것만, 나타나기·사라지기는 첫 하나씩, 강조는 <see cref="MaxEmphasis"/>개까지.
        /// 단어·글자별은 글자 클립만(아니면 한 번에), 타자기·흩어지기는 글자 클립만. 숫자는 범위 안으로(NaN·무한대는 기본값). 남는 것이 없으면 null.
        /// </summary>
        public static List<ClipAnimation> Clean(Clip clip)
        {
            if (clip?.Animations == null || !CanHave(clip))
                return null;
            var result = new List<ClipAnimation>();
            foreach (var item in clip.Animations)
            {
                var def = item == null ? null : Def(item.Effect);
                if (def == null || !Enum.IsDefined(item.Kind) || def.Emphasis != (item.Kind == AnimationKind.Emphasis) || !Allows(clip, def))
                    continue;
                if (item.Kind != AnimationKind.Emphasis && result.Any(a => a.Kind == item.Kind))
                    continue;
                if (item.Kind == AnimationKind.Emphasis && result.Count(a => a.Kind == AnimationKind.Emphasis) >= MaxEmphasis)
                    continue;
                var a = item.Clone();
                if (!def.Directions.Contains(a.Direction))
                    a.Direction = def.DefaultDirection;
                if (clip.Kind != ClipKind.Text || !Enum.IsDefined(a.Unit))
                    a.Unit = AnimationUnit.All;
                if (def.TextOnly && a.Unit == AnimationUnit.All)
                    a.Unit = AnimationUnit.Character;
                if (!Enum.IsDefined(a.Ease))
                    a.Ease = def.DefaultEase;
                a.Stagger = double.IsFinite(a.Stagger) ? Math.Max(0, Math.Min(100, a.Stagger)) : def.DefaultStagger;
                a.Duration = Math.Max(1, Math.Min(MaxFrames, a.Duration));
                a.Delay = Math.Max(0, Math.Min(MaxFrames, a.Delay));
                a.Repeat = def.Repeats ? Math.Max(0, Math.Min(MaxRepeat, a.Repeat)) : 1;
                a.Amount = def.Amount == null ? 0 : double.IsFinite(a.Amount) ? Math.Max(def.Amount.Min, Math.Min(def.Amount.Max, a.Amount)) : def.Amount.Default;
                result.Add(a);
            }
            return Order(result);
        }

        /// <summary>나눈 클립: 왼쪽 조각은 사라지기를, 오른쪽 조각은 나타나기를 뺀다(강조는 둘 다 남는다 — 오른쪽은 그 조각의 시작부터 다시).</summary>
        internal static void Split(Clip left, Clip right)
        {
            if (left.Animations != null)
                left.Animations = Order(left.Animations.Where(a => a.Kind != AnimationKind.Out).ToList());
            if (right.Animations != null)
                right.Animations = Order(right.Animations.Where(a => a.Kind != AnimationKind.In).ToList());
        }
    }
}
