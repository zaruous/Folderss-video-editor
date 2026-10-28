using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>키프레임을 두는 값(#4 사용자 키프레임). 저장된 이름이 바뀌면 예전 프로젝트에서 키프레임이 빠지므로 이름을 바꾸지 않는다(뒤에만 더한다).</summary>
    public enum KeyframeProperty
    {
        /// <summary>위치(X·Y 함께 — 화면 가운데에서, 프로젝트 픽셀).</summary>
        Position,
        /// <summary>크기 배율(1 = 화면 크기의 틀).</summary>
        Scale,
        /// <summary>시계 방향 회전(도).</summary>
        Rotation,
        /// <summary>불투명도(0~1).</summary>
        Opacity
    }

    /// <summary>
    /// 키프레임 하나: 클립 안 시각(프레임 — 클립 시작이 0)의 값과 다음 키프레임까지의 이징. 위치는 Value = X, Y = Y.
    /// </summary>
    public sealed class ClipKeyframe
    {
        /// <summary>값 — 모르는 이름(새 판의 값)이면 이 항목만 버린다(목록을 너그럽게 읽음).</summary>
        public KeyframeProperty Property { get; set; }
        /// <summary>클립 안 시각(프레임). 클립을 나누거나 앞을 다듬으면 클립 밖에 남을 수 있다(사이 값을 그대로 두려고).</summary>
        public long Frame { get; set; }
        /// <summary>값: 위치는 X, 크기는 배율, 회전은 도, 불투명도는 0~1.</summary>
        public double Value { get; set; }
        /// <summary>위치의 Y(다른 값은 0).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Y { get; set; }
        /// <summary>이 키프레임에서 다음 키프레임까지의 이징(애니메이션과 같은 목록). 기본은 일정(직선).</summary>
        [JsonConverter(typeof(LenientEnumConverter<AnimationEase>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AnimationEase Ease { get; set; }

        public ClipKeyframe Clone()
        {
            return (ClipKeyframe)MemberwiseClone();
        }
    }

    /// <summary>
    /// 클립의 기준 값(위치·크기·회전·불투명도)을 시간식으로 — 키프레임이 있으면 키프레임 사이 값, 없으면 클립의 고정 값(상수).
    /// 렌더 그래프는 이 위에 이동 경로·애니메이션 효과를 더하고(위치·회전) 곱한다(크기·불투명도).
    /// </summary>
    public sealed class TransformBase
    {
        /// <summary>위치 X·Y(화면 가운데에서, 프로젝트 픽셀).</summary>
        public MotionExpr X { get; init; }
        public MotionExpr Y { get; init; }
        /// <summary>크기 배율.</summary>
        public MotionExpr Scale { get; init; }
        /// <summary>시계 방향 회전(도).</summary>
        public MotionExpr Rotation { get; init; }
        /// <summary>불투명도(0~1 — 그래프가 0~1로 자른다).</summary>
        public MotionExpr Opacity { get; init; }
        /// <summary>
        /// 틀(맞춤·그리개 그림을 줄이는 크기)에 쓰는 크기 배율: 고정이면 그 값, 크기 키프레임이 있으면 가장 큰 값 — 키우지 않고 줄이기만 해서 선명하게.
        /// 그래프는 장마다 Scale ÷ BoxScale만큼 더 줄인다.
        /// </summary>
        public double BoxScale { get; init; }
    }

    /// <summary>
    /// 사용자 키프레임(#4 단계 2): 위치(X·Y)·크기·회전·불투명도를 클립 안 시각마다 정하고, 그 사이는 직선 + 키프레임마다 이징으로 잇는다.
    /// 첫 키프레임 앞은 첫 값, 마지막 키프레임 뒤는 마지막 값. 키프레임이 있는 값은 클립의 고정 값(<see cref="Clip.X"/> 등)을 쓰지 않는다.
    /// 값을 고치는 곳(속성 패널·미리 보기 틀)은 <see cref="Set"/>·<see cref="SetPosition"/>을 쓴다 — 키프레임이 있으면 그 시각의 키프레임을 고치거나 새로 넣고, 없으면 고정 값을 바꾼다.
    /// </summary>
    public static class ClipKeyframes
    {
        /// <summary>값 하나의 키프레임 수 상한 — 시간식 길이가 그래프를 너무 키우지 않게.</summary>
        public const int MaxPerProperty = 100;
        /// <summary>키프레임 시각 범위(클립 안 프레임, ±).</summary>
        public const long MaxFrame = 100_000_000;

        public static readonly KeyframeProperty[] Properties = { KeyframeProperty.Position, KeyframeProperty.Scale, KeyframeProperty.Rotation, KeyframeProperty.Opacity };

        public static string Name(KeyframeProperty property)
        {
            switch (property)
            {
                case KeyframeProperty.Position: return "위치";
                case KeyframeProperty.Scale: return "크기";
                case KeyframeProperty.Rotation: return "회전";
                default: return "불투명도";
            }
        }

        /// <summary>키프레임을 둘 수 있는 클립: 화면 클립(자막 트랙의 큐는 트랙 공통 모양이라 부르는 쪽이 뺀다).</summary>
        public static bool CanHave(Clip clip)
        {
            return clip != null && clip.IsVisual;
        }

        /// <summary>키프레임이 있는지(값을 주면 그 값만).</summary>
        public static bool Has(Clip clip, KeyframeProperty? property = null)
        {
            return clip?.Keyframes != null && clip.Keyframes.Any(k => k != null && (property == null || k.Property == property));
        }

        /// <summary>애니메이션·키프레임이 든 클립이 있는지(프로젝트 형식 4로 저장할지 — <see cref="ClipAnimations.Any"/>와 함께).</summary>
        public static bool Any(Project project)
        {
            return project.AllClips.Any(c => Has(c));
        }

        /// <summary>값 하나의 키프레임(시각 차례). 없으면 빈 목록.</summary>
        public static List<ClipKeyframe> Track(Clip clip, KeyframeProperty property)
        {
            return clip?.Keyframes?.Where(k => k != null && k.Property == property).OrderBy(k => k.Frame).ToList() ?? new List<ClipKeyframe>();
        }

        /// <summary>키프레임이 있는 클립 안 시각들(값을 가리지 않고, 차례대로 — 타임라인 표시).</summary>
        public static List<long> Frames(Clip clip)
        {
            return clip?.Keyframes?.Where(k => k != null).Select(k => k.Frame).Distinct().OrderBy(f => f).ToList() ?? new List<long>();
        }

        /// <summary>그 시각의 키프레임(없으면 null).</summary>
        public static ClipKeyframe Find(Clip clip, KeyframeProperty property, long frame)
        {
            return clip?.Keyframes?.FirstOrDefault(k => k != null && k.Property == property && k.Frame == frame);
        }

        // ---------- 사이 값 ----------

        private static double Component(ClipKeyframe key, bool y)
        {
            return y ? key.Y : key.Value;
        }

        /// <summary>
        /// 키프레임 사이 값(frame = 클립 안 시각): 첫 키프레임 앞은 첫 값, 마지막 뒤는 마지막 값, 사이는 앞 값 + (뒤 값 − 앞 값) × 이징(진행).
        /// 키프레임 시각에서는 진행이 0이라 정확히 그 값이다. y면 위치의 Y. 키프레임이 없으면 NaN.
        /// </summary>
        public static double Value(IReadOnlyList<ClipKeyframe> track, double frame, bool y = false)
        {
            if (track == null || track.Count == 0)
                return double.NaN;
            if (frame < track[0].Frame)
                return Component(track[0], y);
            for (var i = 0; i + 1 < track.Count; i++)
            {
                if (frame < track[i + 1].Frame)
                {
                    var (a, b) = (track[i], track[i + 1]);
                    var p = Math.Max(0, Math.Min(1, (frame - a.Frame) / (double)(b.Frame - a.Frame)));
                    return Component(a, y) + (Component(b, y) - Component(a, y)) * AnimationMotion.Ease(a.Ease, p);
                }
            }
            return Component(track[^1], y);
        }

        /// <summary>
        /// <see cref="Value"/>와 같은 계산의 시간식(τ = 클립 안 장 번호). 구간을 고르는 if를 반씩 나눠 쌓아(깊이 log n) 고른 구간만 계산한다.
        /// 마지막 키프레임부터는 상수, 키프레임이 하나면 상수다.
        /// </summary>
        public static MotionExpr Expression(IReadOnlyList<ClipKeyframe> track, bool y = false)
        {
            if (track == null || track.Count == 0)
                throw new ArgumentException("키프레임이 없습니다.", nameof(track));
            var time = MotionExpr.Time;
            // 구간 i(< n − 1): [f_i, f_{i+1}) — 첫 구간은 앞으로 이어져 진행 0(첫 값), 구간 n − 1: f_{n−1}부터 마지막 값
            MotionExpr Piece(int i)
            {
                if (i == track.Count - 1)
                    return Component(track[i], y);
                var (a, b) = (track[i], track[i + 1]);
                var from = Component(a, y);
                var change = Component(b, y) - from;
                if (change == 0)
                    return from;
                var p = MotionExpr.Clip01((time - a.Frame) / (b.Frame - a.Frame));
                return from + change * AnimationMotion.Ease(a.Ease, p);
            }
            MotionExpr Build(int low, int high)
            {
                if (low == high)
                    return Piece(low);
                var middle = (low + high + 1) / 2;
                var (before, after) = (Build(low, middle - 1), Build(middle, high));
                // 양쪽이 같은 상수면(값이 바뀌지 않는 구간) 고를 것이 없다 — 위치 Y만 그대로인 것처럼
                if (before.Constant.HasValue && before.Constant == after.Constant)
                    return before;
                return MotionExpr.If(MotionExpr.Lt(time, track[middle].Frame), before, after);
            }
            return Build(0, track.Count - 1);
        }

        /// <summary>클립의 고정 값(키프레임이 없을 때 쓰는 값).</summary>
        private static double Fixed(Clip clip, KeyframeProperty property, bool y)
        {
            switch (property)
            {
                case KeyframeProperty.Position: return y ? clip.Y : clip.X;
                case KeyframeProperty.Scale: return clip.Scale;
                case KeyframeProperty.Rotation: return clip.Rotation;
                default: return clip.Opacity;
            }
        }

        /// <summary>클립 안 시각 frame의 값(키프레임이 있으면 사이 값, 없으면 고정 값). y면 위치의 Y.</summary>
        public static double At(Clip clip, KeyframeProperty property, long frame, bool y = false)
        {
            var track = Track(clip, property);
            return track.Count > 0 ? Value(track, frame, y) : Fixed(clip, property, y);
        }

        /// <summary>
        /// 그 시각의 모습: 키프레임이 있으면 위치·크기·회전·불투명도를 그 시각 값으로 넣은 사본(키프레임은 뺌 — 프로젝트에 없음), 없으면 클립 그대로.
        /// 미리 보기 틀·고르기·붙기가 재생 헤드의 자리를 쓸 때.
        /// </summary>
        public static Clip Pose(Clip clip, long frame)
        {
            if (!Has(clip))
                return clip;
            var pose = clip.CloneWithNewId();
            pose.Id = clip.Id;
            pose.X = At(clip, KeyframeProperty.Position, frame);
            pose.Y = At(clip, KeyframeProperty.Position, frame, true);
            pose.Scale = At(clip, KeyframeProperty.Scale, frame);
            pose.Rotation = At(clip, KeyframeProperty.Rotation, frame);
            pose.Opacity = At(clip, KeyframeProperty.Opacity, frame);
            pose.Keyframes = null;
            return pose;
        }

        /// <summary>
        /// 렌더 그래프의 기준 값: 키프레임이 있는 값은 시간식, 없는 값은 클립의 고정 값(상수 — 예전 그래프 글 그대로).
        /// 크기·불투명도 키프레임 값은 읽을 때 범위 안으로 고쳐 두었다(<see cref="Clean"/>).
        /// </summary>
        public static TransformBase Base(Clip clip)
        {
            MotionExpr Of(KeyframeProperty property, bool y = false)
            {
                var track = Track(clip, property);
                return track.Count > 0 ? Expression(track, y) : MotionExpr.Num(Fixed(clip, property, y));
            }
            var scales = Track(clip, KeyframeProperty.Scale);
            return new TransformBase
            {
                X = Of(KeyframeProperty.Position),
                Y = Of(KeyframeProperty.Position, true),
                Scale = Of(KeyframeProperty.Scale),
                Rotation = Of(KeyframeProperty.Rotation),
                Opacity = Of(KeyframeProperty.Opacity),
                BoxScale = Math.Max(0.01, scales.Count > 0 ? scales.Max(k => k.Value) : clip.Scale)
            };
        }

        // ---------- 고치기 ----------

        /// <summary>
        /// 값을 바꾼다: 그 값에 키프레임이 있으면 frame(클립 안)의 키프레임을 고치거나 새로 넣고(이징은 일정), 없으면 클립의 고정 값을 바꾼다.
        /// 위치는 <see cref="SetPosition"/>.
        /// </summary>
        public static void Set(Clip clip, KeyframeProperty property, long frame, double value)
        {
            if (property == KeyframeProperty.Position)
                throw new ArgumentException("위치는 SetPosition으로 바꿉니다.", nameof(property));
            if (!Has(clip, property))
                SetFixed(clip, property, value, 0);
            else
                Put(clip, property, frame, value, 0);
        }

        /// <summary>위치를 바꾼다(<see cref="Set"/>과 같은 규칙 — X·Y 함께).</summary>
        public static void SetPosition(Clip clip, long frame, double x, double y)
        {
            if (!Has(clip, KeyframeProperty.Position))
                SetFixed(clip, KeyframeProperty.Position, x, y);
            else
                Put(clip, KeyframeProperty.Position, frame, x, y);
        }

        /// <summary>그 시각의 키프레임을 고치거나 새로 넣는다(상한이면 EditException).</summary>
        private static void Put(Clip clip, KeyframeProperty property, long frame, double value, double y)
        {
            var key = Find(clip, property, frame);
            if (key == null)
            {
                if (Track(clip, property).Count >= MaxPerProperty)
                    throw new EditException(Name(property) + " 키프레임은 " + MaxPerProperty + "개까지 넣을 수 있습니다.");
                key = new ClipKeyframe { Property = property, Frame = frame };
                clip.Keyframes ??= new List<ClipKeyframe>();
                clip.Keyframes.Add(key);
            }
            key.Value = value;
            key.Y = property == KeyframeProperty.Position ? y : 0;
            clip.Keyframes = Order(clip.Keyframes);
        }

        /// <summary>
        /// 그 시각에 키프레임을 넣거나(지금 보이는 값으로) 뺀다. 마지막 키프레임을 빼면 그 값이 고정 값이 된다(화면이 바뀌지 않게). 넣었으면 true.
        /// </summary>
        public static bool Toggle(Clip clip, KeyframeProperty property, long frame)
        {
            var key = Find(clip, property, frame);
            if (key == null)
            {
                var (x, y) = (At(clip, property, frame), property == KeyframeProperty.Position ? At(clip, property, frame, true) : 0);
                Put(clip, property, frame, x, y);
                return true;
            }
            var rest = clip.Keyframes.Where(k => !ReferenceEquals(k, key)).ToList();
            if (!rest.Any(k => k != null && k.Property == property))
                SetFixed(clip, property, key.Value, key.Y);
            clip.Keyframes = Order(rest);
            return false;
        }

        private static void SetFixed(Clip clip, KeyframeProperty property, double value, double y)
        {
            switch (property)
            {
                case KeyframeProperty.Position:
                    clip.X = value;
                    clip.Y = y;
                    break;
                case KeyframeProperty.Scale:
                    clip.Scale = value;
                    break;
                case KeyframeProperty.Rotation:
                    clip.Rotation = value;
                    break;
                default:
                    clip.Opacity = value;
                    break;
            }
        }

        /// <summary>그 시각의 키프레임들(값을 가리지 않고)의 이징 — 다음 키프레임까지. 바꾼 키프레임이 없으면 false.</summary>
        public static bool SetEase(Clip clip, long frame, AnimationEase ease)
        {
            var keys = clip?.Keyframes?.Where(k => k != null && k.Frame == frame).ToList() ?? new List<ClipKeyframe>();
            foreach (var key in keys)
                key.Ease = ease;
            return keys.Count > 0;
        }

        /// <summary>
        /// 그 시각의 키프레임들을 다른 시각으로 옮긴다(타임라인에서 끌기 — 값을 가리지 않고 함께). 옮긴 자리에 같은 값의 키프레임이 있으면 옮긴 것이 대신한다.
        /// </summary>
        public static void MoveFrame(Clip clip, long from, long to)
        {
            if (clip?.Keyframes == null || from == to)
                return;
            to = Math.Max(-MaxFrame, Math.Min(MaxFrame, to));
            var moving = clip.Keyframes.Where(k => k != null && k.Frame == from).ToList();
            if (moving.Count == 0)
                return;
            var kinds = new HashSet<KeyframeProperty>(moving.Select(k => k.Property));
            var list = clip.Keyframes.Where(k => k != null && !(k.Frame == to && kinds.Contains(k.Property))).ToList();
            foreach (var key in moving)
                key.Frame = to;
            clip.Keyframes = Order(list);
        }

        /// <summary>값마다, 시각 차례로(같은 값·같은 시각은 하나 — 뒤의 것). 비었으면 null.</summary>
        private static List<ClipKeyframe> Order(IEnumerable<ClipKeyframe> keys)
        {
            var list = keys.Where(k => k != null).GroupBy(k => (k.Property, k.Frame)).Select(g => g.Last())
                .OrderBy(k => k.Property).ThenBy(k => k.Frame).ToList();
            return list.Count == 0 ? null : list;
        }

        // ---------- 읽기·나누기 ----------

        /// <summary>
        /// 읽은 값(남이 만든 파일일 수 있음)을 규칙에 맞게: 화면 클립에만, 아는 값만, NaN·무한대는 버리고 숫자는 범위 안으로(크기 0.01~20, 불투명도 0~1,
        /// 위치 ±100만, 회전 ±10만, 시각 ±<see cref="MaxFrame"/>), 같은 값·시각은 하나, 값마다 <see cref="MaxPerProperty"/>개까지. 남는 것이 없으면 null.
        /// </summary>
        public static List<ClipKeyframe> Clean(Clip clip)
        {
            if (clip?.Keyframes == null || !CanHave(clip))
                return null;
            var result = new List<ClipKeyframe>();
            foreach (var item in clip.Keyframes)
            {
                if (item == null || !Enum.IsDefined(item.Property) || !double.IsFinite(item.Value) || !double.IsFinite(item.Y))
                    continue;
                var key = item.Clone();
                key.Frame = Math.Max(-MaxFrame, Math.Min(MaxFrame, key.Frame));
                if (!Enum.IsDefined(key.Ease))
                    key.Ease = AnimationEase.Linear;
                switch (key.Property)
                {
                    case KeyframeProperty.Position:
                        key.Value = Math.Max(-1e6, Math.Min(1e6, key.Value));
                        key.Y = Math.Max(-1e6, Math.Min(1e6, key.Y));
                        break;
                    case KeyframeProperty.Scale:
                        key.Value = Math.Max(0.01, Math.Min(20, key.Value));
                        key.Y = 0;
                        break;
                    case KeyframeProperty.Rotation:
                        key.Value = Math.Max(-1e5, Math.Min(1e5, key.Value));
                        key.Y = 0;
                        break;
                    default:
                        key.Value = Math.Max(0, Math.Min(1, key.Value));
                        key.Y = 0;
                        break;
                }
                result.Add(key);
            }
            var ordered = Order(result);
            return ordered?.GroupBy(k => k.Property).SelectMany(g => g.Take(MaxPerProperty)).ToList();
        }

        /// <summary>
        /// 클립 안 [0, 길이) 밖의 키프레임은 사이 값에 쓰이는 가장 가까운 하나씩만 남긴다(앞쪽은 0 앞의 마지막, 뒤쪽은 길이 뒤의 첫 것) — 나눈 조각·다듬은 클립이
        /// 같은 움직임을 그리면서 보이지 않는 키프레임이 쌓이지 않게.
        /// </summary>
        internal static void Prune(Clip clip)
        {
            if (clip?.Keyframes == null)
                return;
            var duration = clip.Duration;
            var kept = new List<ClipKeyframe>();
            foreach (var property in Properties)
            {
                var track = Track(clip, property);
                var before = track.LastOrDefault(k => k.Frame < 0);
                var after = track.FirstOrDefault(k => k.Frame >= duration);
                kept.AddRange(track.Where(k => k.Frame >= 0 && k.Frame < duration || ReferenceEquals(k, before) || ReferenceEquals(k, after)));
            }
            clip.Keyframes = Order(kept);
        }

        /// <summary>클립 시작을 frames만큼 뒤로 옮겼을 때(앞을 다듬음 — 같은 장면에 같은 값이 남게) 키프레임 시각을 그만큼 당긴다.</summary>
        internal static void Shift(Clip clip, long frames)
        {
            if (clip?.Keyframes == null || frames == 0)
                return;
            foreach (var key in clip.Keyframes.Where(k => k != null))
                key.Frame = Math.Max(-MaxFrame, Math.Min(MaxFrame, key.Frame - frames));
            Prune(clip);
        }

        /// <summary>나눈 클립: 왼쪽은 그대로, 오른쪽은 나눈 자리(offset — 왼쪽 클립 안 프레임)만큼 당긴다. 둘 다 밖의 것은 사이 값에 쓰는 것만 남긴다.</summary>
        internal static void Split(Clip left, Clip right, long offset)
        {
            Prune(left);
            Shift(right, offset);
        }
    }
}
