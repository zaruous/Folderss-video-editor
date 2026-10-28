using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>글자 전용 강조(물결·굵게 깜빡이기) 하나와 그 구간(<see cref="AnimationMotion.EmphasisWindow"/> — 클립 안 프레임).</summary>
    public sealed class TextEmphasis
    {
        public ClipAnimation Animation { get; set; }
        public long Start { get; set; }
        /// <summary>한 번의 길이(장) — 모습은 이 길이마다 되풀이된다.</summary>
        public long Cycle { get; set; }
        public long End { get; set; }
    }

    /// <summary>
    /// 단어·글자별 애니메이션 한 장의 값(그리개 요청 <see cref="TextImageRequest.Animation"/>): 나타나기·사라지기(단위를 나누는 것만)·글자 전용 강조와 그 구간(클립 안 프레임),
    /// 프레임 레이트, 그릴 장(클립 안 프레임). 그리개는 <see cref="Poses"/>로 글자 요소마다의 모습을 받아 그리고,
    /// <see cref="States"/>의 모든 장에서 움직이는 범위를 모아 한 클립의 장들을 같은 크기로 그린다(이어 붙이는 입력이 같은 크기여야 한다).
    /// </summary>
    public sealed class TextAnimationFrame
    {
        public ClipAnimation In { get; set; }
        public ClipAnimation Out { get; set; }
        public long InStart { get; set; }
        public long InLength { get; set; }
        public long OutStart { get; set; }
        public long OutLength { get; set; }
        /// <summary>글자 전용 강조(없으면 null — 키가 예전과 같다).</summary>
        public List<TextEmphasis> Emphases { get; set; }
        public int RateNumerator { get; set; } = 30;
        public int RateDenominator { get; set; } = 1;
        /// <summary>그릴 장(클립 시작부터의 프레임).</summary>
        public long Frame { get; set; }
        /// <summary>
        /// 0 이상이면 그 강조 하나만 그린다(나타나기·사라지기는 다 나타난 모습) — 쉬는 동안 되풀이하는 강조의 한 번을 그려 되풀이해 쓴다.
        /// -1이면 그 장의 모든 효과.
        /// </summary>
        public int Solo { get; set; } = -1;

        /// <summary>이 장이 쓰는 값만 담은 사본(클립의 애니메이션을 그대로 쥐지 않는다).</summary>
        public TextAnimationFrame At(long frame)
        {
            return At(frame, -1);
        }

        /// <summary>강조 solo 하나만 그리는 장(<see cref="Solo"/>).</summary>
        public TextAnimationFrame At(long frame, int solo)
        {
            var copy = (TextAnimationFrame)MemberwiseClone();
            copy.In = In?.Clone();
            copy.Out = Out?.Clone();
            copy.Emphases = Emphases?.Select(e => new TextEmphasis { Animation = e.Animation?.Clone(), Start = e.Start, Cycle = e.Cycle, End = e.End }).ToList();
            copy.Frame = frame;
            copy.Solo = solo;
            return copy;
        }

        /// <summary>
        /// 글자 클립의 단어·글자별 나타나기·사라지기와 글자 전용 강조(없으면 null). 구간은 <see cref="AnimationMotion.Window"/>·<see cref="AnimationMotion.EmphasisWindow"/>와 같다.
        /// </summary>
        public static TextAnimationFrame Of(Clip clip, FrameRate rate)
        {
            if (clip == null || clip.Kind != ClipKind.Text)
                return null;
            var appear = ClipAnimations.In(clip);
            var vanish = ClipAnimations.Out(clip);
            if (!AnimationMotion.IsPerUnit(appear))
                appear = null;
            if (!AnimationMotion.IsPerUnit(vanish))
                vanish = null;
            var emphases = ClipAnimations.Emphases(clip).Where(AnimationMotion.IsPerUnit).ToList();
            if (appear == null && vanish == null && emphases.Count == 0)
                return null;
            var frame = new TextAnimationFrame { RateNumerator = rate.Numerator, RateDenominator = rate.Denominator };
            if (appear != null)
            {
                frame.In = appear.Clone();
                (frame.InStart, frame.InLength) = AnimationMotion.Window(clip, appear);
            }
            if (vanish != null)
            {
                frame.Out = vanish.Clone();
                (frame.OutStart, frame.OutLength) = AnimationMotion.Window(clip, vanish);
            }
            if (emphases.Count > 0)
            {
                frame.Emphases = emphases.Select(e =>
                {
                    var (start, cycle, end) = AnimationMotion.EmphasisWindow(clip, e);
                    return new TextEmphasis { Animation = e.Clone(), Start = start, Cycle = cycle, End = end };
                }).ToList();
            }
            return frame;
        }

        /// <summary>구간 진행(0~1): 시작 장 0, 구간 바로 뒤 장 1. 길이가 0이면 시작부터 1.</summary>
        public static double Progress(long frame, long start, long length)
        {
            if (length <= 0)
                return frame >= start ? 1 : 0;
            return Math.Max(0, Math.Min(1, (frame - start) / (double)length));
        }

        /// <summary>
        /// frame 장에서 글자 요소(<see cref="TextUnits.Elements"/> 차례)마다의 모습. size = 글자 크기(출력 픽셀), pixel = 프로젝트 픽셀 하나의 출력 픽셀.
        /// 나타나기는 구간 앞에서 "없음", 사라지기는 구간 뒤에서 "없음"이다. 강조는 나타나기 위에 더하고, <see cref="Solo"/>면 그 강조만.
        /// </summary>
        public UnitPose[] Poses(string text, double size, long frame, double pixel = 1)
        {
            var count = TextUnits.Elements(text).Count;
            var poses = Enumerable.Repeat(UnitPose.Rest, count).ToArray();
            if (In != null && Solo < 0)
            {
                var (units, total) = TextUnits.Split(text, In.Unit);
                var p = Progress(frame, InStart, InLength);
                for (var i = 0; i < count; i++)
                    poses[i] = AnimationMotion.InPose(In, p, units[i], total, size, pixel);
            }
            var emphasis = EmphasisPoses(text, size, frame);
            for (var i = 0; i < count; i++)
                poses[i] = poses[i].Then(emphasis[i]);
            if (Out != null && Solo < 0)
            {
                var (units, total) = TextUnits.Split(text, Out.Unit);
                var p = Progress(frame, OutStart, OutLength);
                for (var i = 0; i < count; i++)
                    poses[i] = poses[i].Then(AnimationMotion.OutPose(Out, p, units[i], total, size, pixel));
            }
            return poses;
        }

        /// <summary>frame 장에서 글자 요소마다 강조만의 모습(강조가 없으면 모두 제자리). <see cref="Solo"/>면 그 강조만.</summary>
        public UnitPose[] EmphasisPoses(string text, double size, long frame)
        {
            var count = TextUnits.Elements(text).Count;
            var poses = Enumerable.Repeat(UnitPose.Rest, count).ToArray();
            if (Emphases == null)
                return poses;
            for (var k = 0; k < Emphases.Count; k++)
            {
                var emphasis = Emphases[k];
                if (Solo >= 0 && k != Solo || emphasis.Animation == null)
                    continue;
                var (units, total) = TextUnits.Split(text, emphasis.Animation.Unit);
                for (var i = 0; i < count; i++)
                    poses[i] = poses[i].Then(AnimationMotion.EmphasisPose(emphasis.Animation, frame, emphasis.Start, emphasis.Cycle, emphasis.End, units[i], total, size));
            }
            return poses;
        }

        /// <summary>단위 번호(요소마다)와 단위 수 — 같은 단위의 요소는 같이 움직인다(가운데도 같이 쓴다). 나타나기와 사라지기의 단위가 다르면 둘 다 본다.</summary>
        public (int[] In, int[] Out) Units(string text)
        {
            return (In == null ? null : TextUnits.Split(text, In.Unit).UnitOf, Out == null ? null : TextUnits.Split(text, Out.Unit).UnitOf);
        }

        /// <summary>움직이는 모든 장(나타나기·사라지기 구간의 장과 구간이 끝난 장, 강조 구간의 장 — 차례대로 한 번씩).</summary>
        public IEnumerable<long> Frames()
        {
            var ranges = new List<(long From, long To)>();
            if (In != null)
                ranges.Add((InStart, InStart + Math.Max(0, InLength) + 1));
            if (Out != null)
                ranges.Add((OutStart, OutStart + Math.Max(0, OutLength) + 1));
            foreach (var emphasis in Emphases ?? new List<TextEmphasis>())
                ranges.Add((emphasis.Start, emphasis.End));
            long next = long.MinValue;
            foreach (var (from, to) in ranges.OrderBy(r => r.From))
            {
                for (var f = Math.Max(from, next); f < to; f++)
                    yield return f;
                next = Math.Max(next, to);
            }
        }

        /// <summary>
        /// 그릴 수 있는 모든 모습의 장 — 그리개가 그림 크기를 정할 때 본다(모든 장이 같은 크기): <see cref="Frames"/>의 장과,
        /// 강조마다 그 강조 하나만 그리는 한 번(<see cref="Solo"/>).
        /// </summary>
        public IEnumerable<TextAnimationFrame> States()
        {
            foreach (var frame in Frames())
                yield return At(frame);
            for (var k = 0; k < (Emphases?.Count ?? 0); k++)
            {
                var emphasis = Emphases[k];
                for (var f = emphasis.Start; f < Math.Min(emphasis.End, emphasis.Start + emphasis.Cycle); f++)
                    yield return At(f, k);
            }
        }

        internal void AppendKey(StringBuilder json, bool frame)
        {
            Append(json, "anim.in", In);
            Append(json, "anim.out", Out);
            TextImageRequest.Number(json, "anim.inStart", InStart);
            TextImageRequest.Number(json, "anim.inLength", InLength);
            TextImageRequest.Number(json, "anim.outStart", OutStart);
            TextImageRequest.Number(json, "anim.outLength", OutLength);
            TextImageRequest.Number(json, "anim.rateNumerator", RateNumerator);
            TextImageRequest.Number(json, "anim.rateDenominator", RateDenominator);
            // 강조·홀로 그리기는 있을 때만 적는다 — 나타나기·사라지기만 있는 묶음의 키(이미 그린 장)는 그대로
            for (var k = 0; k < (Emphases?.Count ?? 0); k++)
            {
                var name = "anim.emphasis" + k;
                Append(json, name, Emphases[k].Animation);
                TextImageRequest.Number(json, name + ".start", Emphases[k].Start);
                TextImageRequest.Number(json, name + ".cycle", Emphases[k].Cycle);
                TextImageRequest.Number(json, name + ".end", Emphases[k].End);
            }
            if (frame)
            {
                TextImageRequest.Number(json, "anim.frame", Frame);
                if (Solo >= 0)
                    TextImageRequest.Number(json, "anim.solo", Solo);
            }
        }

        private static void Append(StringBuilder json, string name, ClipAnimation animation)
        {
            if (animation == null)
            {
                TextImageRequest.Quoted(json, name, "none");
                return;
            }
            TextImageRequest.Quoted(json, name + ".kind", animation.Kind.ToString());
            TextImageRequest.Quoted(json, name + ".effect", animation.Effect.ToString());
            TextImageRequest.Quoted(json, name + ".direction", animation.Direction.ToString());
            TextImageRequest.Quoted(json, name + ".unit", animation.Unit.ToString());
            TextImageRequest.Number(json, name + ".stagger", animation.Stagger);
            TextImageRequest.Number(json, name + ".duration", animation.Duration);
            TextImageRequest.Number(json, name + ".delay", animation.Delay);
            TextImageRequest.Number(json, name + ".repeat", animation.Repeat);
            TextImageRequest.Quoted(json, name + ".ease", animation.Ease.ToString());
            TextImageRequest.Number(json, name + ".amount", animation.Amount);
        }
    }
}
