using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 동영상 효과 "스캔 라인": 가는 흰 선(속)을 넓은 색 빛(번짐)이 감싼 띠가 클립 위에서 아래로 훑고 지나간다.
    /// 선은 아래로 휜다(가운데가 처진 얕은 U — 휘어짐 0이면 곧은 선). 빨리 들어와 가운데서 머물다 빨리 나가고, 잠깐 보이지 않다가 다시 처음부터.
    /// 어두운 곳을 지날 때는 그 어둠만큼 위아래로 지그재그로 떨린다(일그러짐). 선은 스크린으로 섞어 밝게만 하고 클립의 알파는 그대로다(투명한 곳은 투명하게).
    /// 그리기: 휜 띠를 한 장만 수식으로 그려(속 = R, 번짐 = G) 색을 입혀 구간 끝까지 늘이고, 검은 틀 위에 시각 수식의 높이로 얹어 클립과 스크린으로 섞는다.
    /// 일그러짐은 띠 크기에서만 — 띠 자리의 클립 밝기로 어둠 지도를 만들어 지그재그 무늬와 곱하고 띠를 위아래로 민다(FFmpeg displace, ±127px까지).
    /// </summary>
    public static class ScanLine
    {
        public const string Id = "scan-line";
        /// <summary>주기 끝에서 선이 보이지 않는 쉼(주기의 몫).</summary>
        public const double Gap = 0.2;
        // 휘어짐 100에서 가운데가 처지는 깊이(틀 높이의 몫)
        private const double BendMax = 0.2;
        // 일그러짐 100에서의 떨림 폭(프로젝트 픽셀), 지그재그 마디 간격(프로젝트 픽셀), 무늬가 바뀌는 횟수(초당)
        private const double JitterMax = 80;
        private const double JitterKnot = 16;
        private const int JitterRate = 12;
        // 밝기(0~255)가 DarkStart보다 어두우면 떨리기 시작해 DarkFull에서 다 떨린다
        private const int DarkStart = 140;
        private const int DarkFull = 40;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "스캔 라인", "스캔·빛",
            "가는 흰 선과 색 빛의 띠가 위에서 아래로 훑고 지나갑니다(아래로 휘고, 어두운 곳에서는 떨림). 선은 화면을 밝게만 합니다.", 1,
            new[]
            {
                EffectParam.Number("glow", "빛 번짐", 0, 150, 40, "px", "흰 선 둘레 색 빛의 폭(0 = 빛 없이 선만)"),
                EffectParam.Number("core", "굵기", 1, 20, 3, "px", "가운데 흰 선의 굵기"),
                EffectParam.Number("bend", "휘어짐", 0, 100, 35, "%", "선 가운데가 아래로 처지는 정도(0 = 곧은 선)"),
                EffectParam.Number("jitter", "일그러짐", 0, 100, 40, "%", "어두운 곳을 지날 때 위아래로 떨리는 정도(0 = 떨리지 않음)"),
                EffectParam.Number("period", "속도", 0.8, 6, 2.4, "초", "한 번 훑고 다시 시작할 때까지 걸리는 시간(작을수록 빠름)"),
                EffectParam.Number("opacity", "불투명도", 0, 100, 100, "%", "선과 빛의 세기"),
                EffectParam.ColorParam("color", "색", "#2F7BFF", "빛 번짐의 색(가운데 선은 흰색)")
            },
            Render,
            // 썸네일 띠(1.5초)에 한 번 훑고 쉬는 것까지 다 보이게, 작은 그림에서도 선과 빛이 보이게 조금 굵고 넓게
            new Dictionary<string, double> { ["period"] = 1.5, ["glow"] = 70, ["core"] = 8, ["jitter"] = 25 })
        { Since = 1 };

        /// <summary>선 모양(출력 픽셀): 속 반폭, 번짐 폭, 처짐 깊이, 떨림 폭(0 = 떨리지 않음), 띠 위아래 여백(선 끝에서 띠 가장자리까지), 띠 높이.</summary>
        public readonly struct Shape
        {
            public Shape(double core, double glow, double bend, int jitter, double margin, int stripHeight)
            {
                Core = core;
                Glow = glow;
                Bend = bend;
                Jitter = jitter;
                Margin = margin;
                StripHeight = stripHeight;
            }

            public double Core { get; }
            public double Glow { get; }
            public double Bend { get; }
            public int Jitter { get; }
            public double Margin { get; }
            public int StripHeight { get; }
        }

        /// <summary>
        /// width×height 틀, 픽셀 배율 pixelScale에서의 선 모양. 번짐은 띠 가장자리에서 0이 된다(띠 위아래에 딱딱한 줄이 생기지 않게 — 여백 ≥ 번짐 폭).
        /// 떨림은 클립 그림에서 띠 높이만큼 잘라 어둠을 재므로 띠가 틀보다 높으면 떨림 폭을 줄인다(1px 아래면 떨리지 않음).
        /// </summary>
        public static Shape Measure(ClipEffect effect, int width, int height, double pixelScale)
        {
            var core = Math.Max(0.5, VideoEffects.Number(effect, Def, "core") * pixelScale / 2);
            var glow = VideoEffects.Number(effect, Def, "glow") * pixelScale;
            if (glow < 1)
                glow = 0;
            var bend = VideoEffects.Number(effect, Def, "bend") / 100 * BendMax * height;
            var jitter = (int)Math.Min(127, Math.Round(VideoEffects.Number(effect, Def, "jitter") / 100 * JitterMax * pixelScale));
            var body = Math.Max(core, glow) + 1;
            if (jitter > 0 && Math.Ceiling(bend + 2 * (body + jitter)) > height)
                jitter = Math.Max(0, (int)Math.Floor((height - bend - 2 * body) / 2) - 1);
            if (jitter < 1)
                jitter = 0;
            var margin = body + jitter;
            return new Shape(core, glow, bend, jitter, margin, Math.Max(2, (int)Math.Ceiling(bend + 2 * margin)));
        }

        /// <summary>
        /// 훑는 자리(0 = 띠가 틀 위에 막 숨음, 1 = 틀 아래로 다 지나감): 클립 안 시각 time초, 주기 period초.
        /// 주기의 앞 80%에 훑고(빨리 들어와 가운데서 머물다 빨리 나감 — 3차 곡선 75%에 등속 25%를 섞음) 나머지는 쉰다.
        /// </summary>
        public static double Progress(double time, double period)
        {
            var phase = time - period * Math.Floor(time / period);
            var u = Math.Min(1, phase / (period * (1 - Gap)));
            return 0.25 * u + 0.75 * (0.5 + 4 * Math.Pow(u - 0.5, 3));
        }

        /// <summary>시각 time초에 띠 윗변의 y(틀 픽셀) — 위에서 다 숨은 자리(-띠 높이)부터 아래로 다 지나간 자리(틀 높이)까지.</summary>
        public static double StripTop(double time, double period, int height, int stripHeight)
        {
            return -stripHeight + (height + stripHeight) * Progress(time, period);
        }

        /// <summary>
        /// 시각 time초에 x열에서 가운데 흰 선이 지나는 줄(틀의 픽셀 줄 번호, 소수 — 떨림 빼고). 선은 양끝이 높고 가운데가 처진다.
        /// FFmpeg처럼 띠 윗변은 정수 픽셀로 자르고(overlay) 띠 안에서는 픽셀 가운데로 잰다.
        /// </summary>
        public static double LineY(ClipEffect effect, double time, int width, int height, double pixelScale, double x)
        {
            var shape = Measure(effect, width, height, pixelScale);
            var across = (2 * x + 1) / width - 1;
            var top = Math.Truncate(StripTop(time, VideoEffects.Number(effect, Def, "period"), height, shape.StripHeight));
            return top + shape.Margin + shape.Bend * (1 - across * across) - 0.5;
        }

        /// <summary>필터 조각(<see cref="VideoEffects"/>의 규칙). 받는 흐름은 틀 크기 gbrap.</summary>
        private static string Render(EffectRender r)
        {
            var shape = Measure(r.Effect, r.Width, r.Height, r.PixelScale);
            var p = r.Label;
            int w = r.Width, h = shape.StripHeight;
            var seconds = N(r.Seconds);
            var once = N(0.5 / r.Fps);
            var period = r.Number("period");
            // 훑는 자리(Progress와 같은 식) — 시각은 클립 안 시각(t + 앞에서 건너뛴 초)
            var u = string.Format(CultureInfo.InvariantCulture, "min(1,mod(t+{0},{1})/{2})", N(r.TimeOffset), N(period), N(period * (1 - Gap)));
            var ease = string.Format(CultureInfo.InvariantCulture, "(0.25*{0}+0.75*(0.5+4*pow({0}-0.5,3)))", u);
            var text = new StringBuilder();
            text.Append(shape.Jitter > 0 ? ",split[" + p + "a][" + p + "d];" : "[" + p + "a];");

            // 띠 한 장: 가로 위치 -1~1에서 선 높이 = 여백 + 처짐 × (1 - x²), 선까지의 거리는 기울기만큼 줄여 굵기가 고르게
            var across = "((2*X+1)/W-1)";
            var distance = string.Format(CultureInfo.InvariantCulture, "abs(Y+0.5-({0}+{1}*(1-{2}*{2})))/hypot(1,{3}*{2})", N(shape.Margin), N(shape.Bend), across, N(4 * shape.Bend / w));
            var core = string.Format(CultureInfo.InvariantCulture, "255*clip({0}-{1},0,1)", N(shape.Core + 0.5), distance);
            var glow = shape.Glow > 0 ? string.Format(CultureInfo.InvariantCulture, "255*pow(max(0,1-{0}/{1}),2)", distance, N(shape.Glow)) : "0";
            // 색 입히기: 속(R)은 흰색, 번짐(G)은 색 — 둘 다 불투명도만큼
            var opacity = r.Number("opacity") / 100;
            var (cr, cg, cb) = VideoEffects.Channels(r.Color);
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrp,geq=r='{4}':g='{5}':b=0,colorchannelmixer=rr={6}:rg={7}:rb=0:gr={6}:gg={8}:gb=0:br={6}:bg={9}:bb=0,tpad=stop_mode=clone:stop_duration={10}[{11}s];",
                w, h, r.Rate, once, core, glow, N(opacity), N(opacity * cr), N(opacity * cg), N(opacity * cb), seconds, p);
            var strip = p + "s";

            if (shape.Jitter > 0)
            {
                // 일그러짐: 지그재그 무늬(마디마다 정해진 높이를 잇는 꺾은선, 가운데 128) × 띠 자리 클립의 어둠 → 위아래로 미는 지도
                var knot = N(Math.Max(3, JitterKnot * r.PixelScale));
                var cell = "floor(X/" + knot + ")";
                var fraction = "(X/" + knot + "-" + cell + ")";
                string Height(string index) => "(2*mod(abs(sin((" + index + ")*12.9898)*43758.5453),1)-1)";
                var pattern = string.Format(CultureInfo.InvariantCulture, "128+{0}*({1}*(1-{3})+{2}*{3})", shape.Jitter, Height(cell), Height(cell + "+1"), fraction);
                // 무늬는 두 배 너비 한 줄을 그려 띠 높이로 늘이고, 1초에 12번 다른 자리를 잘라 쓴다(같은 클립 시각이면 같은 자리)
                text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x1:r={1}:d={2},format=gray,geq=lum='{3}',scale={0}:{4}:flags=neighbor,tpad=stop_mode=clone:stop_duration={5},crop={6}:{4}:'mod(floor((t+{7})*{8}+0.001)*397,{6})':0[{9}p];",
                    2 * w, r.Rate, once, pattern, h, seconds, w, N(r.TimeOffset), JitterRate, p);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}d]crop={1}:{2}:0:'-oh+(ih+oh)*{3}',format=gray,lut=c0='32*clip(({4}-val)/{5},0,1)'[{0}k];",
                    p, w, h, ease, DarkStart, DarkStart - DarkFull);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}p][{0}k]blend=all_mode=multiply128,format=gbrp[{0}y];", p);
                text.AppendFormat(CultureInfo.InvariantCulture, "color=c=0x808080:s={0}x{1}:r={2}:d={3},format=gbrp[{4}x];", w, h, r.Rate, seconds, p);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}s][{0}x][{0}y]displace=edge=smear[{0}t];", p);
                strip = p + "t";
            }

            // 검은 틀에 띠를 시각의 높이로 얹는다(사람에게만이면 마스크를 곱해 사람 자리만 남김)
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap[{4}c];", w, r.Height, r.Rate, seconds, p);
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}c][{1}]overlay=x=0:y='-h+(H+h)*{2}':format=gbrp", p, strip, ease);
            if (r.Mask != null)
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}l];[{0}l][{1}]blend=all_mode=multiply", p, r.Mask);
            // 스크린으로 섞는다 — 알파는 클립 것 그대로(선 쪽 알파 255를 곱함)
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}v];[{0}v][{0}a]blend=c0_mode=screen:c1_mode=screen:c2_mode=screen:c3_mode=multiply", p);
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
