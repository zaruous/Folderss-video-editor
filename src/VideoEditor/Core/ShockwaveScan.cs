using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 동영상 효과 "쇼크웨이브 스캔": 한 점(중심 X·Y)에서 가는 흰 고리와 색 빛이 퍼져 나가 화면 끝까지 훑는다.
    /// 고리 바로 안쪽(물결 폭)은 잠깐 굴절되어 반지름 방향으로 밀려 일렁이고(일그러짐) 조금 밝아진다. 처음엔 빠르게 퍼지다 점점 느려지며 사라지고, 잠깐 쉬었다가 다시 처음부터.
    /// 그리기: 고리 모양은 중심에서의 거리에만 달렸으므로, 거리 지도(한 장, 1/4픽셀 단위)를 주소로 삼아 한 줄짜리 모양 표에서 색을 꺼낸다(remap).
    /// 모양 표는 한 장만 수식으로 그리고(가로 = 고리에서의 거리, 세로 = 세기 32단계), 매 장 시각에 맞는 자리를 잘라(crop은 복사 없음) 쓴다 — 매 장 수식이 없다.
    /// 굴절은 같은 표의 알파 칸(미는 거리)을 꺼내 방향 지도(cos·sin, 한 장)와 곱한 지도로 클립을 민다(displace). 빛은 스크린으로 섞어 밝게만 하고, 클립의 알파는 그대로다.
    /// </summary>
    public static class ShockwaveScan
    {
        public const string Id = "shockwave-scan";
        // 주기 앞쪽에서 세기가 차오르는 몫과 끝에서 사라지는 몫(퍼지는 동안의 몫)
        private const double FadeIn = 0.05;
        private const double FadeOut = 0.3;
        // 세기 단계 수(모양 표의 줄 수)
        private const int Levels = 32;
        // 거리 지도의 단위(1/4픽셀) — 가는 고리의 계단을 줄인다
        private const int Sub = 4;
        // 고리 안쪽 물결 폭 = WakeBase + WakeGlow × 빛 번짐(프로젝트 픽셀)
        private const double WakeBase = 60;
        private const double WakeGlow = 3;
        // 고리 안쪽이 밝아지는 정도, 일그러짐 100에서 미는 거리(프로젝트 픽셀)와 한도(출력 픽셀 — 밀 거리는 8비트 칸에 담는다)
        private const double Lift = 0.3;
        private const double RefractMax = 40;
        private const int RefractLimit = 120;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "쇼크웨이브 스캔", "스캔·빛",
            "한 점에서 빛나는 고리가 퍼져 나가 화면을 훑습니다. 고리 바로 안쪽은 잠깐 굴절되어 일렁이고 조금 밝아집니다.", 1,
            new[]
            {
                EffectParam.Number("glow", "빛 번짐", 0, 150, 30, "px", "고리 둘레 색 빛의 폭(0 = 빛 없이 고리만)"),
                EffectParam.Number("core", "굵기", 1, 20, 3, "px", "가운데 흰 고리의 굵기"),
                EffectParam.Number("refract", "일그러짐", 0, 100, 50, "%", "고리 바로 안쪽이 굴절되어 밀리는 정도(0 = 밀지 않음, 100 = 40px)"),
                EffectParam.Number("period", "속도", 0.8, 6, 2.2, "초", "한 번 퍼지고 다시 시작할 때까지 걸리는 시간(작을수록 빠름)"),
                EffectParam.Number("opacity", "불투명도", 0, 100, 100, "%", "고리·빛·일그러짐의 세기"),
                EffectParam.Number("cx", "중심 X", 0, 100, 50, "%", "고리가 퍼지기 시작하는 가로 자리(0 = 왼쪽 끝, 100 = 오른쪽 끝)"),
                EffectParam.Number("cy", "중심 Y", 0, 100, 50, "%", "고리가 퍼지기 시작하는 세로 자리(0 = 위 끝, 100 = 아래 끝)"),
                EffectParam.ColorParam("color", "색", "#3FD0FF", "고리 둘레 빛과 안쪽이 밝아지는 색(가운데 고리는 흰색)")
            },
            Render,
            // 썸네일 띠(1.5초)에 한 번 퍼지고 쉬는 것까지, 작은 그림에서도 고리와 일렁임이 보이게
            new Dictionary<string, double> { ["period"] = 1.5, ["glow"] = 60, ["core"] = 8, ["refract"] = 100 })
        { Since = 2 };

        /// <summary>
        /// 고리 모양(출력 픽셀): 중심(픽셀 모서리 좌표), 흰 고리 반폭, 빛 번짐 폭, 안쪽 물결 폭, 미는 거리(0 = 밀지 않음),
        /// 중심에서 가장 먼 모서리까지의 거리, 주기 끝 반지름(고리와 빛이 화면 밖으로 다 나가는 반지름).
        /// </summary>
        public readonly struct Shape
        {
            public Shape(double centerX, double centerY, double core, double glow, double wake, int refract, double reach, double end)
            {
                CenterX = centerX;
                CenterY = centerY;
                Core = core;
                Glow = glow;
                Wake = wake;
                Refract = refract;
                Reach = reach;
                End = end;
            }

            public double CenterX { get; }
            public double CenterY { get; }
            public double Core { get; }
            public double Glow { get; }
            public double Wake { get; }
            public int Refract { get; }
            public double Reach { get; }
            public double End { get; }
        }

        /// <summary>width×height 틀, 픽셀 배율 pixelScale에서의 고리 모양.</summary>
        public static Shape Measure(ClipEffect effect, int width, int height, double pixelScale)
        {
            var core = Math.Max(0.5, VideoEffects.Number(effect, Def, "core") * pixelScale / 2);
            var glow = VideoEffects.Number(effect, Def, "glow") * pixelScale;
            if (glow < 1)
                glow = 0;
            var wake = Math.Max(2, (WakeBase + WakeGlow * VideoEffects.Number(effect, Def, "glow")) * pixelScale);
            var refract = (int)Math.Min(RefractLimit, Math.Round(VideoEffects.Number(effect, Def, "refract") / 100 * RefractMax * pixelScale));
            var cx = width * VideoEffects.Number(effect, Def, "cx") / 100;
            var cy = height * VideoEffects.Number(effect, Def, "cy") / 100;
            var reach = Math.Max(Math.Max(Math.Sqrt(cx * cx + cy * cy), Math.Sqrt((width - cx) * (width - cx) + cy * cy)),
                Math.Max(Math.Sqrt(cx * cx + (height - cy) * (height - cy)), Math.Sqrt((width - cx) * (width - cx) + (height - cy) * (height - cy))));
            return new Shape(cx, cy, core, glow, wake, refract, reach, reach + Math.Max(glow, core) + 2);
        }

        /// <summary>주기 안 몫(0~1, 쉬는 동안 1): 클립 안 시각 time초, 주기 period초.</summary>
        public static double Phase(double time, double period)
        {
            var phase = time - period * Math.Floor(time / period);
            return Math.Min(1, phase / (period * (1 - ScanLine.Gap)));
        }

        /// <summary>고리의 세기(0~1, 32단계로 — FFmpeg가 쓰는 값): 처음 5%에 차오르고 끝 30%에 사라진다(쉬는 동안 0).</summary>
        public static double Strength(double time, double period)
        {
            var u = Phase(time, period);
            var strength = Math.Min(1, u / FadeIn) * Math.Min(1, (1 - u) / FadeOut);
            return Math.Round((Levels - 1) * strength, MidpointRounding.AwayFromZero) / (Levels - 1);
        }

        /// <summary>시각 time초에 흰 고리의 반지름(출력 픽셀, 1/4픽셀 단위로 — FFmpeg가 쓰는 값). 빠르게 퍼지다 점점 느려진다(1 − (1 − u)²).</summary>
        public static double Radius(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            var shape = Measure(effect, width, height, pixelScale);
            var u = Phase(time, VideoEffects.Number(effect, Def, "period"));
            var radius = shape.End * (1 - (1 - u) * (1 - u));
            return Math.Round(Sub * radius, MidpointRounding.AwayFromZero) / Sub;
        }

        /// <summary>필터 조각(<see cref="VideoEffects"/>의 규칙). 받는 흐름은 틀 크기 gbrap.</summary>
        private static string Render(EffectRender r)
        {
            var shape = Measure(r.Effect, r.Width, r.Height, r.PixelScale);
            var p = r.Label;
            int w = r.Width, h = r.Height;
            var once = ScanKit.Once(r);
            var hold = ScanKit.Hold(r);
            var period = r.Number("period");
            var opacity = r.Number("opacity") / 100;
            var (cr, cg, cb) = VideoEffects.Channels(r.Color);
            var text = new StringBuilder("[" + p + "a];");

            // 거리 지도(한 장): 픽셀 가운데에서 중심까지의 거리 × 4(반올림) — 모양 표의 가로 주소. 세로 주소는 0
            var length = (int)Math.Ceiling(Sub * shape.Reach) + 2;
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray16,geq=lum='round({4}*hypot(X+0.5-{5},Y+0.5-{6}))'{7}[{8}d];",
                w, h, r.Rate, once, Sub, N(shape.CenterX), N(shape.CenterY), hold, p);
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray16{4}[{5}z];", w, h, r.Rate, once, hold, p);

            // 모양 표(한 장): 가로 X ↔ 고리에서의 거리 s = (X − 원점)/4(바깥이 +), 세로 Y ↔ 세기 Y/31. 원점 = 주기 끝 반지름 × 4
            // 매 장 가로로는 원점 − 반지름×4, 세로로는 세기 단계를 잘라 쓰면 거리 지도 값 그대로가 그 시각의 모양이 된다
            var origin = (int)Math.Ceiling(Sub * shape.End) + 1;
            var s = "((X-" + origin + ")/" + Sub + ")";
            var level = "(Y/" + (Levels - 1) + ")";
            var glow = shape.Glow > 0 ? string.Format(CultureInfo.InvariantCulture, "pow(max(0,1-abs({0})/{1}),2)", s, N(shape.Glow)) : "0";
            var lift = string.Format(CultureInfo.InvariantCulture, "{0}*lt({1},0)*pow(max(0,1+{1}/{2}),2)", N(Lift), s, N(shape.Wake));
            string Light(double c) => string.Format(CultureInfo.InvariantCulture, "if(between({0},{1},{2}),{3}*{4}*min(1,clip({5}-abs({0}),0,1)+{6}*({7}+{8})),0)",
                s, N(-shape.Wake - 1), N(Math.Max(shape.Glow, shape.Core) + 1), N(255 * opacity), level, N(shape.Core + 0.5), N(c), glow, lift);
            // 알파 칸 = 미는 거리(출력 픽셀): 고리 바로 안쪽 물결 폭에서 반 사인 모양
            var push = shape.Refract > 0
                ? string.Format(CultureInfo.InvariantCulture, "if(between({0},{1},0),{2}*{3}*sin(PI*(-{0})/{4}),0)", s, N(-shape.Wake), N(shape.Refract * opacity), level, N(shape.Wake))
                : "0";
            var u = ScanKit.Phase(r, period);
            var radius = string.Format(CultureInfo.InvariantCulture, "({0}*(1-pow(1-{1},2)))", N(shape.End), u);
            var strength = string.Format(CultureInfo.InvariantCulture, "(min(1,{0}/{1})*min(1,(1-{0})/{2}))", u, N(FadeIn), N(FadeOut));
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=r='{4}':g='{5}':b='{6}':a='{7}'{8},crop={9}:1:'{10}-round({11}*{12})':'round({13}*{14})'[{15}r];",
                origin + length, Levels, r.Rate, once, Light(cr), Light(cg), Light(cb), push, hold, length, origin, Sub, radius, Levels - 1, strength, p);
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}r][{0}d][{0}z]remap", p);
            // 사람에게만: 빛과 미는 거리(알파 칸) 모두에 마스크를 곱한다
            if (r.Mask != null)
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}q];[{1}]shuffleplanes=map0=0:map1=1:map2=2:map3=0[{0}n];[{0}q][{0}n]blend=all_mode=multiply", p, r.Mask);

            var bottom = p + "a";
            if (shape.Refract > 0)
            {
                var q = w * h >= 1280 * 720 ? 4 : w * h >= 640 * 360 ? 2 : 1;
                string Direction(string along, string scale, string half) => string.Format(CultureInfo.InvariantCulture, "128+127*({0}*{1}+{2}-{3})/max(0.001,hypot({0}*X+{2}-{4},{0}*Y+{2}-{5}))",
                    scale, along, half, along == "X" ? N(shape.CenterX) : N(shape.CenterY), N(shape.CenterX), N(shape.CenterY));
                text.AppendFormat(CultureInfo.InvariantCulture, ",split[{0}l][{0}m];", p);
                // 방향 지도(한 장, 면 순서 G·B·R·A): G = cos, B = sin(128 ± 127), R·A = 128. 큰 틀에서는 q×q칸마다 하나를 그려 remap으로 늘인다(수식 픽셀 1/q²)
                if (q > 1)
                {
                    int cw = (w + q - 1) / q, ch = (h + q - 1) / q;
                    text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=r=128:g='{4}':b='{5}':a=128[{6}t];",
                        cw, ch, r.Rate, once, Direction("X", N(q), N(q / 2.0)), Direction("Y", N(q), N(q / 2.0)), p);
                    text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray16,geq=lum='floor(X/{4})'[{5}u];", w, h, r.Rate, once, q, p);
                    text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray16,geq=lum='floor(Y/{4})'[{5}j];", w, h, r.Rate, once, q, p);
                    text.AppendFormat(CultureInfo.InvariantCulture, "[{0}t][{0}u][{0}j]remap{1}[{0}c];", p, hold);
                }
                else
                {
                    text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=r=128:g='{4}':b='{5}':a=128{6}[{7}c];",
                        w, h, r.Rate, once, Direction("X", "1", "0.5"), Direction("Y", "1", "0.5"), hold, p);
                }
                // 미는 거리(알파 면)를 펴서 방향과 곱한다 — lut2 한 번에 G 면 = 128 + cos × 거리, B 면 = 128 + sin × 거리(R·A 면 128).
                // 그 G·B 면을 색 세 면에 편 것이 displace의 가로·세로 지도다(알파 면 128 — 알파는 밀지 않음)
                const string scaled = "clip(128+(x-128)*y/127,0,255)";
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}m]shuffleplanes=map0=3:map1=3:map2=3:map3=3[{0}m1];[{0}c][{0}m1]lut2=c0='{1}':c1='{1}':c2=128:c3=128,split[{0}x0][{0}y0];", p, scaled);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}x0]shuffleplanes=map0=0:map1=0:map2=0:map3=3[{0}x];[{0}y0]shuffleplanes=map0=1:map1=1:map2=1:map3=3[{0}y];", p);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}a][{0}x][{0}y]displace=edge=smear[{0}v];[{0}l]", p);
                bottom = p + "v";
            }
            else
            {
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}l];[{0}l]", p);
            }
            // 스크린으로 섞는다 — 알파는 클립 것 그대로(빛 쪽 알파 칸은 미는 거리라 쓰지 않음)
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}]blend=c0_mode=screen:c1_mode=screen:c2_mode=screen:c3_mode=normal:c3_opacity=0", bottom);
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
