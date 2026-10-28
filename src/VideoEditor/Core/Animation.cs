using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>
    /// 애니메이션 분류: 나타나기(클립 시작 기준)·강조(시작 시각 기준, 반복)·사라지기(클립 끝 기준 — 나타나기를 거꾸로 돌린 움직임)·
    /// 이동 경로(시작 시각 기준, 반복 — 경로를 따라 옮긴다, <see cref="MotionPaths"/>).
    /// </summary>
    public enum AnimationKind
    {
        In,
        Emphasis,
        Out,
        Path
    }

    /// <summary>
    /// 효과. 나타나기·사라지기: 서서히·날아오기·확대·튀어 오르기·닦아내기·돌며 나타나기·타자기·흩어지기·떠오르기·커지며 돌기·가운데서 갈라지기·흐림에서 선명하게·그려지기,
    /// 사라지기만: 떨어지기,
    /// 강조: 맥박·흔들기·회전·크게·작게·투명도 바꾸기·색 바꾸기·광선 깜빡임·물결·굵게 깜빡이기.
    /// 이동 경로: 직선·호·원·물결·사용자 경로(이름 앞에 Path — 강조 효과 이름과 겹치지 않게).
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
        Transparency,
        PathLine,
        PathArc,
        PathCircle,
        PathWave,
        PathCustom,
        // 2단계(#4): 남은 효과
        Rise,
        GrowTurn,
        Split,
        Unblur,
        ColorChange,
        Glow,
        Wave,
        BoldFlash,
        Fall,
        Draw
    }

    /// <summary>
    /// 방향: 나타나기는 들어오는 쪽, 사라지기는 나가는 쪽. 회전·돌며 나타나기·커지며 돌기는 왼쪽 = 시계 반대, 오른쪽 = 시계 방향,
    /// 흔들기·가운데서 갈라지기는 왼쪽·오른쪽 = 가로(좌우로), 위·아래 = 세로(위아래로).
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
        /// <summary>이동 경로의 점(프로젝트 픽셀, 클립 자리 기준 — 첫 점이 (0, 0)). 경로가 아니면 null.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PathPoint> Points { get; set; }
        /// <summary>이동 경로의 되돌아오기: 한 번 안에서 끝까지 갔다가 시작 자리로 돌아온다.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool RoundTrip { get; set; }
        /// <summary>색("#RRGGBB" — 색 바꾸기·광선 깜빡임). 색을 쓰지 않는 효과는 null.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Color { get; set; }

        public ClipAnimation Clone()
        {
            var copy = (ClipAnimation)MemberwiseClone();
            copy.Points = Points?.Select(p => p?.Clone()).ToList();
            return copy;
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
        /// <summary>강조 효과인지(아니면 나타나기·사라지기·이동 경로).</summary>
        public bool Emphasis { get; init; }
        /// <summary>이동 경로인지(<see cref="ClipAnimations.Paths"/>).</summary>
        public bool Path { get; init; }
        /// <summary>글자 클립에만 넣는 효과(타자기·흩어지기·물결·굵게 깜빡이기) — 그리개가 글자를 장마다 그린다.</summary>
        public bool TextOnly { get; init; }
        /// <summary>단어·글자로 나눠야만 하는 효과(타자기·흩어지기·물결) — "한 번에"로 저장된 것은 글자별로 읽는다.</summary>
        public bool NeedsUnits { get; init; }
        /// <summary>색을 고르는 효과의 기본 색("#RRGGBB" — 색 바꾸기·광선 깜빡임). 색을 쓰지 않으면 null.</summary>
        public string DefaultColor { get; init; }
        /// <summary>사라지기에만 쓰는 효과(떨어지기 — 거꾸로 돌린 나타나기가 없다).</summary>
        public bool OutOnly { get; init; }
        /// <summary>획을 그리는 효과(그려지기) — 선·화살표·동그라미 치기·밑줄 요소에만 넣고(<see cref="ArtShape.Draws"/>) 그리개가 장마다 그린다.</summary>
        public bool Strokes { get; init; }
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

        /// <summary>이 분류에 넣을 수 있는 효과인지(강조는 강조 효과, 이동 경로는 경로, 나타나기·사라지기는 그 밖).</summary>
        public bool Fits(AnimationKind kind)
        {
            return Path ? kind == AnimationKind.Path : kind != AnimationKind.Path && Emphasis == (kind == AnimationKind.Emphasis) && (!OutOnly || kind == AnimationKind.Out);
        }
    }

    /// <summary>
    /// 클립 애니메이션 목록과 규칙: 한 클립에 나타나기 1 + 강조 여러 개(<see cref="MaxEmphasis"/>) + 사라지기 1 + 이동 경로 1.
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
                TextOnly = true, NeedsUnits = true, DefaultSeconds = 1.5, DefaultUnit = AnimationUnit.Character, DefaultStagger = 100 },
            new AnimationEffectDef { Effect = AnimationEffect.Scatter, Name = "모여들기", OutName = "흩어지기", Description = "글자가 사방에서 돌며 모여들고, 사방으로 흩어집니다(글자 클립).",
                TextOnly = true, NeedsUnits = true, DefaultEase = AnimationEase.Out, DefaultSeconds = 1.0, DefaultUnit = AnimationUnit.Character, DefaultStagger = 30,
                Amount = new AnimationAmount("거리", 20, 300, 100, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Rise, Name = "떠오르기", OutName = "흐려지며 가라앉기", Description = "아래에서 떠오르며 서서히 보이고, 흐려지며 아래로 가라앉습니다.",
                DefaultEase = AnimationEase.Out, DefaultSeconds = 0.7, Amount = new AnimationAmount("거리", 5, 500, 60, "px") },
            new AnimationEffectDef { Effect = AnimationEffect.GrowTurn, Name = "커지며 돌기", OutName = "작아지며 돌기", Description = "작은 크기에서 돌며 커져 제자리에 서고, 돌며 작아져 사라집니다.",
                Directions = Turns, DefaultEase = AnimationEase.Out, DefaultSeconds = 0.7, Amount = new AnimationAmount("회전", 0, 1080, 90, "°") },
            new AnimationEffectDef { Effect = AnimationEffect.Split, Name = "가운데서 갈라지기", OutName = "가운데로 닫히기", Description = "가운데에서 양쪽으로 열리며 드러나고, 양쪽에서 가운데로 닫히며 사라집니다.",
                Directions = new[] { AnimationDirection.Left, AnimationDirection.Up }, DefaultEase = AnimationEase.Out, DefaultSeconds = 0.6 },
            new AnimationEffectDef { Effect = AnimationEffect.Unblur, Name = "흐림에서 선명하게", OutName = "흐릿하게 사라지기", Description = "흐릿하게 나타나 선명해지고, 흐려지며 사라집니다.",
                DefaultEase = AnimationEase.Out, DefaultSeconds = 0.8, Amount = new AnimationAmount("흐림", 1, 100, 16, "px") },
            new AnimationEffectDef { Effect = AnimationEffect.Draw, Name = "그려지기", OutName = "지워지기", Description = "선·화살표·동그라미 치기·밑줄이 펜으로 긋듯 한쪽 끝부터 그려지고, 그린 차례를 거꾸로 거두며 지워집니다(요소 클립).",
                Strokes = true, DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.8 },
            new AnimationEffectDef { Effect = AnimationEffect.Fall, Name = "떨어지기", Description = "기울며 점점 빨리 떨어져 화면 아래로 사라집니다(글자는 하나씩).", OutOnly = true,
                Directions = Turns, DefaultEase = AnimationEase.Out, DefaultSeconds = 0.8, Amount = new AnimationAmount("기울기", 0, 180, 25, "°") },
            new AnimationEffectDef { Effect = AnimationEffect.Pulse, Name = "맥박", Description = "커졌다 작아지기를 되풀이합니다.", Emphasis = true, Repeats = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, DefaultRepeat = 2, Amount = new AnimationAmount("커지는 정도", 1, 100, 15, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Shake, Name = "흔들기", Description = "좌우(또는 위아래)로 흔들리다 멈춥니다.", Emphasis = true, Repeats = true,
                Directions = new[] { AnimationDirection.Left, AnimationDirection.Up }, DefaultSeconds = 0.6, Amount = new AnimationAmount("흔들림 폭", 1, 200, 20, "px") },
            new AnimationEffectDef { Effect = AnimationEffect.Rotate, Name = "회전", Description = "제자리에서 돕니다.", Emphasis = true, Repeats = true,
                Directions = Turns, DefaultEase = AnimationEase.InOut, DefaultSeconds = 1.0, Amount = new AnimationAmount("회전", 1, 1080, 360, "°") },
            new AnimationEffectDef { Effect = AnimationEffect.Grow, Name = "크게·작게", Description = "정한 크기로 바뀐 뒤 그대로 남습니다.", Emphasis = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, Amount = new AnimationAmount("크기", 10, 400, 150, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Transparency, Name = "투명도 바꾸기", Description = "정한 불투명도로 바뀐 뒤 그대로 남습니다.", Emphasis = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, Amount = new AnimationAmount("불투명도", 0, 100, 40, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.ColorChange, Name = "색 바꾸기", Description = "밝기를 지키며 정한 색으로 물든 뒤 그대로 남습니다(흰색은 그 색, 검은 윤곽은 그대로).", Emphasis = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, DefaultColor = "#EF4444", Amount = new AnimationAmount("세기", 0, 100, 100, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.Glow, Name = "광선 깜빡임", Description = "둘레에 빛이 번쩍였다 사라지기를 되풀이합니다.", Emphasis = true, Repeats = true,
                DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.6, DefaultRepeat = 2, DefaultColor = "#FFE066", Amount = new AnimationAmount("광선 크기", 2, 100, 16, "px") },
            new AnimationEffectDef { Effect = AnimationEffect.Wave, Name = "물결", Description = "글자가 차례로 솟았다 내려앉아 물결칩니다(글자 클립).", Emphasis = true, Repeats = true,
                TextOnly = true, NeedsUnits = true, DefaultEase = AnimationEase.InOut, DefaultSeconds = 1.0, DefaultRepeat = 2, DefaultUnit = AnimationUnit.Character, DefaultStagger = 40,
                Amount = new AnimationAmount("높이", 5, 200, 40, "%") },
            new AnimationEffectDef { Effect = AnimationEffect.BoldFlash, Name = "굵게 깜빡이기", Description = "글자가 굵어졌다 돌아오기를 되풀이합니다(글자 클립 — 단어·글자별로 차례로도).", Emphasis = true, Repeats = true,
                TextOnly = true, DefaultEase = AnimationEase.InOut, DefaultSeconds = 0.5, DefaultRepeat = 2, DefaultStagger = 30, Amount = new AnimationAmount("굵기", 1, 30, 8, "%") }
        };

        /// <summary>이동 경로 목록(탭에 보이는 차례). 나타나기·강조·사라지기 목록(<see cref="All"/>)과 따로 둔다.</summary>
        public static IReadOnlyList<AnimationEffectDef> Paths { get; } = new[]
        {
            new AnimationEffectDef { Effect = AnimationEffect.PathLine, Path = true, Name = "직선", Description = "곧은 선을 따라 옮겨 갑니다. 미리 보기에서 끝점을 끌어 고칩니다.",
                Repeats = true, DefaultEase = AnimationEase.InOut, DefaultSeconds = 2.0 },
            new AnimationEffectDef { Effect = AnimationEffect.PathArc, Path = true, Name = "호", Description = "둥근 길(원의 한 조각)을 따라 옮겨 갑니다. 미리 보기에서 가운데 점과 끝점을 끌어 고칩니다.",
                Repeats = true, DefaultEase = AnimationEase.InOut, DefaultSeconds = 2.0 },
            new AnimationEffectDef { Effect = AnimationEffect.PathCircle, Path = true, Name = "원", Description = "원을 한 바퀴 돌아 제자리로 옵니다. 미리 보기에서 맞은편 점을 끌어 크기와 자리를 고칩니다.",
                Directions = Turns, Repeats = true, DefaultSeconds = 2.0 },
            new AnimationEffectDef { Effect = AnimationEffect.PathWave, Path = true, Name = "물결", Description = "오르내리며 옮겨 갑니다. 미리 보기에서 끝점을 끌어 고칩니다.",
                Repeats = true, DefaultSeconds = 2.0, Amount = new AnimationAmount("물결 높이", 0, 1000, 60, "px") },
            new AnimationEffectDef { Effect = AnimationEffect.PathCustom, Path = true, Name = "사용자 경로",
                Description = "찍은 점을 차례로 이어 옮겨 갑니다. 미리 보기에서 점을 끌고, Shift+누르기로 끝에 더하고, 선을 두 번 눌러 사이에 더하고, 점을 두 번 눌러 뺍니다.",
                Repeats = true, DefaultSeconds = 2.0 }
        };

        public static AnimationEffectDef Def(AnimationEffect effect)
        {
            return All.FirstOrDefault(d => d.Effect == effect) ?? Paths.FirstOrDefault(d => d.Effect == effect);
        }

        /// <summary>목록 이름: "나타나기 · 서서히".</summary>
        public static string Name(ClipAnimation animation)
        {
            var def = animation == null ? null : Def(animation.Effect);
            return def == null ? "" : KindName(animation.Kind) + " · " + def.DisplayName(animation.Kind);
        }

        public static string KindName(AnimationKind kind)
        {
            return kind == AnimationKind.In ? "나타나기" : kind == AnimationKind.Out ? "사라지기" : kind == AnimationKind.Path ? "이동 경로" : "강조";
        }

        /// <summary>속성 패널에 보일 방향 이름(효과마다 뜻이 다르다).</summary>
        public static string DirectionName(AnimationEffect effect, AnimationDirection direction)
        {
            if (effect == AnimationEffect.Spin || effect == AnimationEffect.Rotate || effect == AnimationEffect.PathCircle || effect == AnimationEffect.GrowTurn || effect == AnimationEffect.Fall)
                return direction == AnimationDirection.Left ? "시계 반대" : "시계 방향";
            if (effect == AnimationEffect.Shake)
                return direction == AnimationDirection.Up || direction == AnimationDirection.Down ? "세로" : "가로";
            if (effect == AnimationEffect.Split)
                return direction == AnimationDirection.Up || direction == AnimationDirection.Down ? "위아래로" : "좌우로";
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

        /// <summary>이 클립에 이 효과를 넣을 수 있는지(타자기·흩어지기·물결·굵게 깜빡이기는 글자 클립만, 그려지기는 선·화살표·동그라미 치기·밑줄 요소만).</summary>
        public static bool Allows(Clip clip, AnimationEffectDef def)
        {
            return CanHave(clip) && def != null && (!def.TextOnly || clip.Kind == ClipKind.Text)
                && (!def.Strokes || clip.Kind == ClipKind.Art && clip.Art != null && ArtShape.Draws(clip.Art.Kind));
        }

        /// <summary>이 분류에 쓰는 효과인지(강조는 강조끼리, 사라지기 전용은 사라지기에만).</summary>
        public static bool Fits(AnimationEffectDef def, AnimationKind kind)
        {
            return def != null && def.Fits(kind);
        }

        /// <summary>[애니메이션] 탭의 분류 칸에 보일 효과인지(분류가 맞고 이 클립에 넣을 수 있음).</summary>
        public static bool Offers(Clip clip, AnimationEffectDef def, AnimationKind kind)
        {
            return Fits(def, kind) && Allows(clip, def);
        }

        /// <summary>기본값으로 새 애니메이션(분류가 효과와 맞지 않으면 null). 이동 경로는 높이 1080 화면 기준 기본 점(<see cref="MotionPaths.DefaultPoints"/>).</summary>
        public static ClipAnimation Create(AnimationEffectDef def, AnimationKind kind, FrameRate rate, Clip clip = null)
        {
            if (!Fits(def, kind))
                return null;
            if (def.Path)
            {
                return new ClipAnimation
                {
                    Kind = kind,
                    Effect = def.Effect,
                    Direction = def.DefaultDirection,
                    Duration = Math.Max(1, Math.Min(MaxFrames, rate.ToFrames(def.DefaultSeconds))),
                    Repeat = def.DefaultRepeat,
                    Ease = def.DefaultEase,
                    Amount = def.Amount?.Default ?? 0,
                    Points = MotionPaths.DefaultPoints(def.Effect)
                };
            }
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
                Amount = def.Amount?.Default ?? 0,
                Color = def.DefaultColor
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

        /// <summary>이동 경로(한 클립에 하나 — 없으면 null).</summary>
        public static ClipAnimation PathOf(Clip clip)
        {
            return clip?.Animations?.FirstOrDefault(a => a.Kind == AnimationKind.Path);
        }

        public static List<ClipAnimation> Emphases(Clip clip)
        {
            return clip?.Animations?.Where(a => a.Kind == AnimationKind.Emphasis).ToList() ?? new List<ClipAnimation>();
        }

        /// <summary>나타나기·사라지기·이동 경로를 바꾼다(null이면 뺀다). 목록 차례는 나타나기 → 강조 → 사라지기 → 이동 경로.</summary>
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
                .Concat(list.Where(a => a.Kind == AnimationKind.Out))
                .Concat(list.Where(a => a.Kind == AnimationKind.Path)).ToList();
            return ordered.Count == 0 ? null : ordered;
        }

        /// <summary>애니메이션이 든 클립이 있는지(프로젝트 형식 4로 저장할지).</summary>
        public static bool Any(Project project)
        {
            return project.AllClips.Any(c => c.Animations != null && c.Animations.Count > 0);
        }

        /// <summary>
        /// 읽은 값(남이 만든 파일일 수 있음)을 규칙에 맞게: 보이는 클립에만, 효과와 분류가 맞는 것만, 나타나기·사라지기·이동 경로는 첫 하나씩, 강조는 <see cref="MaxEmphasis"/>개까지.
        /// 단어·글자별은 글자 클립만(아니면 한 번에), 타자기·흩어지기는 글자 클립만. 숫자는 범위 안으로(NaN·무한대는 기본값). 경로 점은 <see cref="MotionPaths.Clean"/>로,
        /// 경로가 아니면 점·되돌아오기를 버린다. 남는 것이 없으면 null.
        /// </summary>
        public static List<ClipAnimation> Clean(Clip clip)
        {
            if (clip?.Animations == null || !CanHave(clip))
                return null;
            var result = new List<ClipAnimation>();
            foreach (var item in clip.Animations)
            {
                var def = item == null ? null : Def(item.Effect);
                if (def == null || !Enum.IsDefined(item.Kind) || !Fits(def, item.Kind) || !Allows(clip, def))
                    continue;
                if (item.Kind != AnimationKind.Emphasis && result.Any(a => a.Kind == item.Kind))
                    continue;
                if (item.Kind == AnimationKind.Emphasis && result.Count(a => a.Kind == AnimationKind.Emphasis) >= MaxEmphasis)
                    continue;
                var a = item.Clone();
                if (!def.Directions.Contains(a.Direction))
                    a.Direction = def.DefaultDirection;
                // 강조는 글자 전용(물결·굵게 깜빡이기)만 단위를 나눈다
                if (clip.Kind != ClipKind.Text || !Enum.IsDefined(a.Unit) || def.Path || (def.Emphasis && !def.TextOnly))
                    a.Unit = AnimationUnit.All;
                if (def.NeedsUnits && a.Unit == AnimationUnit.All)
                    a.Unit = AnimationUnit.Character;
                if (!Enum.IsDefined(a.Ease))
                    a.Ease = def.DefaultEase;
                a.Stagger = double.IsFinite(a.Stagger) ? Math.Max(0, Math.Min(100, a.Stagger)) : def.DefaultStagger;
                a.Duration = Math.Max(1, Math.Min(MaxFrames, a.Duration));
                a.Delay = Math.Max(0, Math.Min(MaxFrames, a.Delay));
                a.Repeat = def.Repeats ? Math.Max(0, Math.Min(MaxRepeat, a.Repeat)) : 1;
                a.Amount = def.Amount == null ? 0 : double.IsFinite(a.Amount) ? Math.Max(def.Amount.Min, Math.Min(def.Amount.Max, a.Amount)) : def.Amount.Default;
                if (def.Path)
                {
                    a.Stagger = 0;
                    a.Points = MotionPaths.Clean(a.Effect, a.Points);
                }
                else
                {
                    a.Points = null;
                    a.RoundTrip = false;
                }
                a.Color = def.DefaultColor == null ? null : Rgb(a.Color, def.DefaultColor);
                result.Add(a);
            }
            return Order(result);
        }

        /// <summary>색 글("#RRGGBB" — "#AARRGGBB"는 알파를 뺀다). 아니면 fallback.</summary>
        public static string Rgb(string color, string fallback)
        {
            var normalized = TextStyle.NormalizeColor(color, null);
            if (normalized == null)
                return fallback;
            return normalized.Length == 9 ? "#" + normalized.Substring(3) : normalized;
        }

        /// <summary>나눈 클립: 왼쪽 조각은 사라지기를, 오른쪽 조각은 나타나기를 뺀다(강조·이동 경로는 둘 다 남는다 — 오른쪽은 그 조각의 시작부터 다시).</summary>
        internal static void Split(Clip left, Clip right)
        {
            if (left.Animations != null)
                left.Animations = Order(left.Animations.Where(a => a.Kind != AnimationKind.Out).ToList());
            if (right.Animations != null)
                right.Animations = Order(right.Animations.Where(a => a.Kind != AnimationKind.In).ToList());
        }
    }
}
