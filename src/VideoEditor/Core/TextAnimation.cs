using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 단어·글자별 애니메이션 한 장의 값(그리개 요청 <see cref="TextImageRequest.Animation"/>): 나타나기·사라지기(단위를 나누는 것만)와 그 구간(클립 안 프레임),
    /// 프레임 레이트, 그릴 장(클립 안 프레임). 그리개는 <see cref="Poses"/>로 글자 요소마다의 모습을 받아 그리고,
    /// <see cref="Frames"/>의 모든 장에서 움직이는 범위를 모아 한 클립의 장들을 같은 크기로 그린다(이어 붙이는 입력이 같은 크기여야 한다).
    /// </summary>
    public sealed class TextAnimationFrame
    {
        public ClipAnimation In { get; set; }
        public ClipAnimation Out { get; set; }
        public long InStart { get; set; }
        public long InLength { get; set; }
        public long OutStart { get; set; }
        public long OutLength { get; set; }
        public int RateNumerator { get; set; } = 30;
        public int RateDenominator { get; set; } = 1;
        /// <summary>그릴 장(클립 시작부터의 프레임).</summary>
        public long Frame { get; set; }

        /// <summary>이 장이 쓰는 값만 담은 사본(클립의 애니메이션을 그대로 쥐지 않는다).</summary>
        public TextAnimationFrame At(long frame)
        {
            var copy = (TextAnimationFrame)MemberwiseClone();
            copy.In = In?.Clone();
            copy.Out = Out?.Clone();
            copy.Frame = frame;
            return copy;
        }

        /// <summary>
        /// 글자 클립의 단어·글자별 나타나기·사라지기(없으면 null). 구간은 <see cref="AnimationMotion.Window"/>와 같다.
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
            if (appear == null && vanish == null)
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
        /// 나타나기는 구간 앞에서 "없음", 사라지기는 구간 뒤에서 "없음"이다.
        /// </summary>
        public UnitPose[] Poses(string text, double size, long frame, double pixel = 1)
        {
            var count = TextUnits.Elements(text).Count;
            var poses = Enumerable.Repeat(UnitPose.Rest, count).ToArray();
            if (In != null)
            {
                var (units, total) = TextUnits.Split(text, In.Unit);
                var p = Progress(frame, InStart, InLength);
                for (var i = 0; i < count; i++)
                    poses[i] = AnimationMotion.InPose(In, p, units[i], total, size, pixel);
            }
            if (Out != null)
            {
                var (units, total) = TextUnits.Split(text, Out.Unit);
                var p = Progress(frame, OutStart, OutLength);
                for (var i = 0; i < count; i++)
                    poses[i] = poses[i].Then(AnimationMotion.OutPose(Out, p, units[i], total, size, pixel));
            }
            return poses;
        }

        /// <summary>단위 번호(요소마다)와 단위 수 — 같은 단위의 요소는 같이 움직인다(가운데도 같이 쓴다). 나타나기와 사라지기의 단위가 다르면 둘 다 본다.</summary>
        public (int[] In, int[] Out) Units(string text)
        {
            return (In == null ? null : TextUnits.Split(text, In.Unit).UnitOf, Out == null ? null : TextUnits.Split(text, Out.Unit).UnitOf);
        }

        /// <summary>움직이는 모든 장(구간 안의 장과 구간이 끝난 장) — 그리개가 그림 크기를 정할 때 본다.</summary>
        public IEnumerable<long> Frames()
        {
            if (In != null)
            {
                for (var f = InStart; f <= InStart + Math.Max(0, InLength); f++)
                    yield return f;
            }
            if (Out != null)
            {
                for (var f = OutStart; f <= OutStart + Math.Max(0, OutLength); f++)
                    yield return f;
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
            if (frame)
                TextImageRequest.Number(json, "anim.frame", Frame);
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
