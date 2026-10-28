using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 애니메이션 그리기(#4). 한 번에 움직이는 것은 시간식으로 클립 체인에 넣는다 — 동영상·사진·꾸민 글자·아트 요소가 같은 필터로 움직인다:
    /// 닦아내기(틀 크기에서 pad·crop — 크기를 몰라도 iw로), 회전(rotate, 출력 = 대각선), 불투명도(sendcmd로 장마다 colorchannelmixer 값),
    /// 크기(맨 끝 scale eval=frame — 장마다 크기가 바뀌므로 크기를 바꾸지 못하는 필터 뒤에 둔다), 위치(overlay x·y — 날아오기는 overlay_w·main_w로 화면 밖까지).
    /// 단어·글자별 나타나기·사라지기는 그리개가 장마다 그린 PNG(같은 크기)를 구간마다 이어 붙인다(<see cref="AnimatedText"/>).
    /// 애니메이션이 없는 클립은 이 파일의 어느 것도 그래프에 들어가지 않는다(예전 프로젝트는 그래프 글이 그대로).
    /// </summary>
    public static partial class RenderGraph
    {
        /// <summary>시간식 움직임(없으면 null). 그리개가 그리는 단어·글자별 효과는 빠진다.</summary>
        private static ObjectMotion Motion(Project project, Clip clip, RenderOptions options)
        {
            if (clip.Animations == null || clip.Animations.Count == 0)
                return null;
            var motion = AnimationMotion.ObjectMotion(clip, DrawsUnits(clip, options));
            return motion.IsEmpty ? null : motion;
        }

        /// <summary>단어·글자별 나타나기·사라지기를 그리개가 장마다 그리는지(글자 클립, 그리개 있음, 긴 글 아님, 글자가 있음).</summary>
        private static bool DrawsUnits(Clip clip, RenderOptions options)
        {
            return clip.Kind == ClipKind.Text && AnimationMotion.HasPerUnit(clip) && UsesRasterizer(clip, options) && TextUnits.Elements(clip.Text).Count > 0;
        }

        /// <summary>τ(클립 안 장 번호)를 나타내는 식 글: 필터의 시각 t를 장 번호로 반올림하고 그 흐름이 시작한 클립 안 장(offset)을 더한다.</summary>
        private static string TimeText(FrameRate rate, long offset)
        {
            var frame = "round(t*" + rate.Rational + ")";
            if (offset == 0)
                return frame;
            return "(" + frame + (offset > 0 ? "+" : "-") + Math.Abs(offset).ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// 닦아내기(앞에 ","): 보이는 쪽만 남기고 나머지를 투명하게. 크기를 모르는 그림(꾸민 글자 PNG·맞춤 결과)이라 투명하게 두 배로 넓히고(pad)
        /// 원래 크기로 잘라(crop, x는 장마다) 한쪽을 밀어낸 뒤 반대로 한 번 더 — 보이는 비율이 1이면 그림 그대로다.
        /// </summary>
        private static string WipeChain(ObjectMotion motion, string time)
        {
            var text = new StringBuilder();
            foreach (var (visible, dx, dy) in motion.Wipes)
            {
                var q = visible.ToFfmpeg(time);
                if (dx != 0)
                    text.Append(dx < 0 ? Wipe("w=iw*2:h=ih:x=iw:y=0", "w=iw/2:h=ih:x='round(iw/2*" + q + ")':y=0", "w=iw*2:h=ih:x=0:y=0", "w=iw/2:h=ih:x='round(iw/2*(1-" + q + "))':y=0")
                        : Wipe("w=iw*2:h=ih:x=0:y=0", "w=iw/2:h=ih:x='round(iw/2*(1-" + q + "))':y=0", "w=iw*2:h=ih:x=iw:y=0", "w=iw/2:h=ih:x='round(iw/2*" + q + ")':y=0"));
                if (dy != 0)
                    text.Append(dy < 0 ? Wipe("w=iw:h=ih*2:x=0:y=ih", "w=iw:h=ih/2:x=0:y='round(ih/2*" + q + ")'", "w=iw:h=ih*2:x=0:y=0", "w=iw:h=ih/2:x=0:y='round(ih/2*(1-" + q + "))'")
                        : Wipe("w=iw:h=ih*2:x=0:y=0", "w=iw:h=ih/2:x=0:y='round(ih/2*(1-" + q + "))'", "w=iw:h=ih*2:x=0:y=ih", "w=iw:h=ih/2:x=0:y='round(ih/2*" + q + ")'"));
            }
            return text.ToString();
        }

        private static string Wipe(string pad1, string crop1, string pad2, string crop2)
        {
            return ",pad=" + pad1 + ":color=black@0,crop=" + crop1 + ",pad=" + pad2 + ":color=black@0,crop=" + crop2;
        }

        /// <summary>
        /// 회전(앞에 ","): 클립 회전 + 움직임(도). 출력은 대각선 크기로 고정(각도가 바뀌어도 잘리지 않게, 가운데는 그대로).
        /// - 넓히는 폭은 4의 배수: 가운데가 픽셀 사이에 걸리면 0°에서도 반 픽셀 흐려지고, 바탕(yuv420)의 overlay는 자리를 짝수로 내리므로 반폭도 짝수여야
        ///   움직이지 않는 그림과 같은 자리다. 먼저 투명하게 2픽셀씩 넓힌다 — rotate는 가장자리 밖 한 픽셀을 가장자리 색으로 칠한다.
        /// - 빈 곳은 투명하게 칠한다(c=none은 칠하지 않아 버퍼에 남은 앞 장의 그림이 꼬리처럼 남는다).
        /// - 그리는 구간(offset부터 frames장) 내내 각도가 같으면 그 각도 하나로(0이면 넣지 않는다) — 다 돈 뒤의 한 장이 정지 그림·내보내기와 같게.
        /// </summary>
        private static string RotateChain(ObjectMotion motion, double rotation, string time, long offset, long frames)
        {
            var first = motion.Rotation.Eval(offset);
            var steady = true;
            for (long i = 1; i < frames && steady; i++)
                steady = motion.Rotation.Eval(offset + i) == first;
            if (steady && Math.Abs(rotation + first) <= 0.01)
                return "";
            var degrees = steady ? N(rotation + first) : (rotation + motion.Rotation).ToFfmpeg(time);
            return ",pad=w=iw+4:h=ih+4:x=2:y=2:color=black@0,rotate=a='" + degrees + "*PI/180':c=black@0:ow='iw+4*ceil((hypot(iw,ih)-iw)/4)':oh='ih+4*ceil((hypot(iw,ih)-ih)/4)'";
        }

        /// <summary>
        /// 불투명도(앞에 ","): 클립 불투명도 × 움직임을 장마다 계산해 바뀌는 장에서만 sendcmd로 colorchannelmixer에 보낸다(시간식을 받는 알파 필터가 없다).
        /// 명령 시각은 장 사이(반 장 앞)라 소수 오차에도 그 장부터 바뀐다. 그리는 구간에서 값이 바뀌지 않으면 고정 값 하나(1이면 넣지 않음).
        /// </summary>
        private static string OpacityChain(ObjectMotion motion, double opacity, FrameRate rate, long offset, long frames, int n)
        {
            string Value(long k) => N(Math.Max(0, Math.Min(1, Math.Max(0, opacity) * motion.Opacity.Eval(offset + k))));
            var name = "colorchannelmixer@an" + n;
            var first = Value(0);
            var last = first;
            var commands = new StringBuilder();
            for (long k = 1; k < frames; k++)
            {
                var value = Value(k);
                if (value == last)
                    continue;
                commands.Append(N((k - 0.5) / rate.Fps)).Append(' ').Append(name).Append(" aa ").Append(value).Append(';');
                last = value;
            }
            if (commands.Length == 0)
                return first == "1" ? "" : ",colorchannelmixer=aa=" + first;
            commands.Length--;
            return ",sendcmd=c='" + commands + "'," + name + "=aa=" + first;
        }

        /// <summary>
        /// 모습(앞에 ","): 흐림 — 닦아내기 뒤·회전 앞(틀 크기의 그림)에서. 지금은 흐림 하나다.
        /// k = 출력 픽셀 / 프로젝트 픽셀(재생 축소 포함), offset = 이 흐름이 시작한 클립 안 장, frames = 그리는 장 수, length = 클립 길이(장).
        /// </summary>
        private static string LookChain(ObjectMotion motion, double k, FrameRate rate, long offset, long frames, long length, int n)
        {
            return BlurChain(motion, k, rate, offset, frames, length, n);
        }

        /// <summary>
        /// 흐림(앞에 ","): 장마다 시그마(출력 픽셀)를 계산해 바뀌는 장에서만 sendcmd로 gblur에 보낸다(불투명도와 같은 방법 — 세로 시그마도 함께).
        /// - 번질 자리(클립 전체에서 가장 큰 시그마 × 3, 짝수)만큼 투명하게 넓힌다(가운데는 그대로). 그리는 구간과 상관없이 같은 크기라
        ///   한 장(재생 헤드)과 내보내기가 같은 자리에 놓인다 — 반폭이 짝수라 바탕(yuv420)의 overlay가 짝수로 내리는 자리도 정지 그림과 같다.
        /// - 알파를 곱한 색으로 흐린다(투명한 곳의 검은색이 번져 가장자리가 어두워지지 않게). 알파 곱하기는 흐린 장에서만 켠다 —
        ///   불투명한 255가 254가 되어 다 나타난 장이 정지 그림과 달라지지 않게(시그마 0의 gblur는 그림 그대로다). 그리는 구간 내내 0이면 넓히기만 한다.
        /// </summary>
        private static string BlurChain(ObjectMotion motion, double k, FrameRate rate, long offset, long frames, long length, int n)
        {
            if (motion.Blur == null || frames <= 0)
                return "";
            double Sigma(long frame) => Math.Round(Math.Max(0, Math.Min(1000, motion.Blur.Eval(frame) * k)), 2);
            double widest = 0;
            for (long f = 0; f < length; f++)
                widest = Math.Max(widest, Sigma(f));
            if (widest <= 0)
                return "";
            var margin = 2 * (int)Math.Ceiling(widest * 3 / 2);
            var pad = string.Format(CultureInfo.InvariantCulture, ",pad=w=iw+{0}:h=ih+{0}:x={1}:y={1}:color=black@0", margin * 2, margin);
            var values = new double[frames];
            for (long i = 0; i < frames; i++)
                values[i] = Sigma(offset + i);
            if (values.Max() <= 0)
                return pad;
            var name = "gblur@bl" + n;
            var commands = new StringBuilder();
            var enable = new StringBuilder();
            long run = -1;
            for (long i = 0; i <= frames; i++)
            {
                if (i > 0 && i < frames && values[i] != values[i - 1])
                {
                    var value = N(values[i]);
                    commands.Append(N((i - 0.5) / rate.Fps)).Append(' ').Append(name).Append(" sigma ").Append(value)
                        .Append(", ").Append(name).Append(" sigmaV ").Append(value).Append(';');
                }
                // 흐린 장이 이어진 구간마다 between(장 사이 시각)
                var blurred = i < frames && values[i] > 0;
                if (blurred && run < 0)
                    run = i;
                if (!blurred && run >= 0)
                {
                    enable.Append(enable.Length > 0 ? "+" : "").Append("between(t,").Append(N((run - 0.5) / rate.Fps)).Append(',').Append(N((i - 0.5) / rate.Fps)).Append(')');
                    run = -1;
                }
            }
            var text = new StringBuilder(pad);
            text.AppendFormat(CultureInfo.InvariantCulture, ",format=gbrap,premultiply=inplace=1:enable='{0}'", enable);
            if (commands.Length > 0)
            {
                commands.Length--;
                text.Append(",sendcmd=c='").Append(commands).Append('\'');
            }
            text.Append(',').Append(name).Append("=sigma=").Append(N(values[0])).Append(":sigmaV=").Append(N(values[0]));
            text.AppendFormat(CultureInfo.InvariantCulture, ",unpremultiply=inplace=1:enable='{0}',format=rgba", enable);
            return text.ToString();
        }

        /// <summary>크기(앞에 ","): 장마다 크기를 바꾼다(eval=frame) — 체인 맨 끝에만(뒤의 overlay는 장마다 그림 크기로 가운데를 맞춘다).</summary>
        private static string ScaleChain(ObjectMotion motion, string time)
        {
            var scale = motion.Scale.ToFfmpeg(time);
            return ",scale=w='max(1,round(iw*" + scale + "))':h='max(1,round(ih*" + scale + "))':eval=frame";
        }

        /// <summary>
        /// overlay 위치 식(가운데 + x·y 출력 픽셀). 움직임이 있으면 흔들기(프로젝트 픽셀 × k), 날아오기(무게만큼 그 방향 화면 밖 — 오른쪽 가장자리가 화면 왼쪽 끝 등),
        /// 크기가 0에 가까우면 화면 밖으로 치우기를 더한다. time은 overlay가 보는 시각(바탕 흐름)의 τ 글.
        /// </summary>
        private static (string X, string Y) OverlayPosition(ObjectMotion motion, double x, double y, double k, string time)
        {
            var x0 = "(main_w-overlay_w)/2+" + N(x);
            var y0 = "(main_h-overlay_h)/2+" + N(y);
            if (motion == null)
                return (x0, y0);
            var xs = new StringBuilder(x0);
            var ys = new StringBuilder(y0);
            if (motion.ShiftX != null)
                xs.Append("+").Append((motion.ShiftX * k).ToFfmpeg(time));
            if (motion.ShiftY != null)
                ys.Append("+").Append((motion.ShiftY * k).ToFfmpeg(time));
            foreach (var (weight, dx, dy) in motion.Fly)
            {
                var w = weight.ToFfmpeg(time);
                if (dx != 0)
                    xs.Append("+").Append(w).Append("*(").Append(dx < 0 ? "-overlay_w" : "main_w").Append("-(").Append(x0).Append("))");
                if (dy != 0)
                    ys.Append("+").Append(w).Append("*(").Append(dy < 0 ? "-overlay_h" : "main_h").Append("-(").Append(y0).Append("))");
            }
            if (motion.CanVanish && motion.Scale != null)
                xs.Append("+if(lt(").Append(motion.Scale.ToFfmpeg(time)).Append(",0.001),-100000,0)");
            return (xs.ToString(), ys.ToString());
        }

        /// <summary>
        /// 단어·글자별 애니메이션이 든 글자 클립의 화면 원본: 그리개가 장마다 그린 PNG를 구간별로 이어 붙인다(concat — 모든 장이 같은 크기).
        /// 클립 안 프레임으로 [0, 나타나기 시작) 나타나기 첫 장 · 나타나기 구간 장들 · 쉬는 장 하나 · 사라지기 구간 장들 · [사라지기 끝, 클립 끝) 사라진 장.
        /// 파일 이름은 묶음 키(sa-&lt;해시&gt;-)에 구간 안 번호(i00012·o00003)나 쉬는 장(s) — 같은 설정이면 같은 이름이라 다시 그리지 않는다.
        /// 여러 장은 그림 묶음 입력(-pattern_type sequence, 처음 장부터 이어져 있어야 하므로 앞 장들도 그려 둔다)에서 trim으로 고르고, 한 장이면 그림 한 장을 되풀이한다.
        /// </summary>
        private static string AnimatedText(Project project, RenderOptions options, RenderPlan plan, Clip clip, long segStart, long segEnd)
        {
            var rate = project.Rate;
            // 꾸민 글자와 같은 요청 — 같은 함수로 만들어 장마다 그리는 글자가 정지 그림과 같게(입력은 버리는 계획에)
            var scratch = new RenderPlan();
            StyledText(project, options, scratch, clip, 1);
            var request = scratch.TextImages.Values.Single();
            var animation = TextAnimationFrame.Of(clip, rate);
            var job = request.WithAnimation(animation.At(0)).JobKey(options.TextRasterizer.Version);
            var folder = Path.Combine(options.TempFolder ?? Path.GetTempPath(), "styled-text");
            plan.TextRasterizer = options.TextRasterizer;
            string Image(string suffix, long clipFrame)
            {
                var path = Path.Combine(folder, "sa-" + job + "-" + suffix + ".png");
                plan.TextImages[path] = request.WithAnimation(animation.At(clipFrame));
                return path;
            }
            var duration = clip.Duration;
            long inStart = 0, inEnd = 0, outStart = duration, outEnd = duration;
            if (animation.In != null)
            {
                inStart = animation.InStart;
                inEnd = animation.InStart + animation.InLength;
            }
            if (animation.Out != null)
            {
                outStart = animation.OutStart;
                outEnd = animation.OutStart + animation.OutLength;
            }
            // (구간, 구간 안 번호의 기준 프레임, 묶음 이름 글자 — 0이면 그림 한 장, 그 한 장의 이름·장)
            var pieces = new List<(long Start, long End, long Base, char Window, string Still, long StillFrame)>
            {
                (0, inStart, 0, '\0', "i" + 0.ToString("D5", CultureInfo.InvariantCulture), inStart),
                (inStart, inEnd, inStart, 'i', null, 0),
                (inEnd, outStart, 0, '\0', "s", inEnd),
                (outStart, outEnd, outStart, 'o', null, 0),
                (outEnd, duration, 0, '\0', "o" + Math.Max(0, outEnd - outStart).ToString("D5", CultureInfo.InvariantCulture), outEnd)
            };
            long from = segStart - clip.Start, to = segEnd - clip.Start;
            var chains = new List<string>();
            foreach (var piece in pieces)
            {
                long start = Math.Max(from, piece.Start), end = Math.Min(to, piece.End);
                if (end <= start)
                    continue;
                if (piece.Window == '\0' || end - start == 1)
                {
                    var path = piece.Window == '\0'
                        ? Image(piece.Still, piece.StillFrame)
                        : Image(piece.Window + (start - piece.Base).ToString("D5", CultureInfo.InvariantCulture), start);
                    var still = AddInput(plan, "-loop", "1", "-framerate", rate.Rational, "-t", N((end - start + 0.5) / rate.Fps),
                        "-f", "image2", "-pattern_type", "none", "-i", path);
                    chains.Add(string.Format(CultureInfo.InvariantCulture, "[{0}:v]trim=end_frame={1},setpts=PTS-STARTPTS", still, end - start));
                    continue;
                }
                // 묶음은 0번 장부터 이어져 있어야 읽힌다 — 끝 장까지 모두 그려 둔다
                long first = start - piece.Base, last = end - piece.Base;
                for (long index = 0; index < last; index++)
                    Image(piece.Window + index.ToString("D5", CultureInfo.InvariantCulture), piece.Base + index);
                // 임시 폴더 경로의 %는 파일 이름 패턴이 아니게 %%로
                var pattern = Path.Combine(folder.Replace("%", "%%"), "sa-" + job + "-" + piece.Window + "%05d.png");
                var input = AddInput(plan, "-framerate", rate.Rational, "-f", "image2", "-pattern_type", "sequence", "-i", pattern);
                chains.Add(string.Format(CultureInfo.InvariantCulture, "[{0}:v]trim=start_frame={1}:end_frame={2},setpts=PTS-STARTPTS", input, first, last));
            }
            if (chains.Count == 1)
                return chains[0] + ",format=rgba";
            // 조각을 잇고 장 번호로 시각을 다시 매긴다(조각마다 정확히 그 장 수)
            var labels = new StringBuilder();
            var text = new StringBuilder();
            for (var i = 0; i < chains.Count; i++)
            {
                var label = "[ua" + plan.Inputs.Count + "x" + i + "]";
                text.Append(chains[i]).Append(label).Append(";");
                labels.Append(label);
            }
            text.AppendFormat(CultureInfo.InvariantCulture, "{0}concat=n={1}:v=1:a=0,setpts='round(N*{2}/({3}*TB))',format=rgba",
                labels, chains.Count, rate.Denominator, rate.Numerator);
            return text.ToString();
        }
    }
}
