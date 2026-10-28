using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 동영상 효과 "SF 스캔": 가로 띠가 위에서 아래로 내려가며 띠 안의 그림을 홀로그램처럼 바꾼다 — 푸른 빛깔(밝기만 남긴 색 입히기), 가는 가로 주사선, 옅은 격자.
    /// 띠 아래 끝(앞머리)에는 밝은 흰 선과 색 빛이 있고, 띠 위쪽(지나온 쪽)으로 갈수록 원래 그림으로 돌아온다. 움직임은 스캔 라인과 같다(빨리 들어와 가운데서 머물다 빨리 나가고 잠깐 쉼).
    /// 그리기: 띠 자리만 잘라(띠 창) 띠 크기에서 색 입히기·무늬 섞기를 하고, 띠 모양(섞는 정도·앞머리 빛)은 한 장 그림을 시각의 자리로 잘라 쓴다. 고친 띠를 제자리에 얹고 클립의 알파를 되돌린다.
    /// 주사선·격자는 화면에 붙어 있다(띠가 지나가며 드러냄).
    /// </summary>
    public static class SciFiScan
    {
        public const string Id = "scifi-scan";
        // 홀로그램 색 입히기: 밝기에 곱하는 세기, 원래 색을 남기는 몫
        private const double Gain = 1.3;
        private const double Keep = 0.15;
        // 띠 안에서 원래 그림으로 돌아오는 구간(띠 높이의 몫 — 위쪽 끝에서 이만큼에 걸쳐 홀로그램이 차오름)
        private const double Rise = 0.6;
        // 띠 안을 옅게 밝히는 색 빛
        private const double Fill = 0.12;
        // 주사선(프로젝트 픽셀 간격)과 격자(프로젝트 픽셀 간격)
        private const double LineGap = 3;
        private const double GridGap = 48;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "SF 스캔", "스캔·빛",
            "가로 띠가 위에서 아래로 내려가며 지나는 자리를 홀로그램처럼(푸른 빛깔·가는 주사선·옅은 격자) 바꿉니다. 띠 앞머리에는 밝은 선이 빛납니다.", 1,
            new[]
            {
                EffectParam.Number("band", "띠 높이", 5, 100, 35, "%", "홀로그램으로 바뀌는 띠의 높이(틀 높이의 몫)"),
                EffectParam.Number("grid", "격자", 0, 100, 40, "%", "옅은 격자 선의 세기(0 = 격자 없음)"),
                EffectParam.Number("lines", "주사선", 0, 100, 50, "%", "가는 가로 주사선의 세기(0 = 주사선 없음)"),
                EffectParam.Number("glow", "빛 번짐", 0, 100, 20, "px", "앞머리 흰 선 둘레 색 빛의 폭"),
                EffectParam.Number("period", "속도", 0.8, 8, 3, "초", "한 번 훑고 다시 시작할 때까지 걸리는 시간(작을수록 빠름)"),
                EffectParam.Number("opacity", "불투명도", 0, 100, 100, "%", "홀로그램과 빛의 세기"),
                EffectParam.ColorParam("color", "색", "#3CE6FF", "홀로그램 빛깔과 앞머리 빛의 색(가운데 선은 흰색)")
            },
            Render,
            // 썸네일 띠(1.5초)에 한 번 훑고 쉬는 것까지, 작은 그림에서도 격자와 빛이 보이게
            new Dictionary<string, double> { ["period"] = 1.5, ["band"] = 45, ["grid"] = 80, ["lines"] = 70, ["glow"] = 40 })
        { Since = 2 };

        /// <summary>띠 모양(출력 픽셀): 홀로그램 띠 높이, 창 높이(띠 + 앞머리 빛 여백), 앞머리 선 반폭, 빛 번짐 폭, 주사선 간격, 격자 간격.</summary>
        public readonly struct Shape
        {
            public Shape(int band, int window, double core, double glow, int lineGap, int gridGap)
            {
                Band = band;
                Window = window;
                Core = core;
                Glow = glow;
                LineGap = lineGap;
                GridGap = gridGap;
            }

            public int Band { get; }
            public int Window { get; }
            public double Core { get; }
            public double Glow { get; }
            public int LineGap { get; }
            public int GridGap { get; }
        }

        /// <summary>width×height 틀, 픽셀 배율 pixelScale에서의 띠 모양. 창은 틀보다 높지 않다.</summary>
        public static Shape Measure(ClipEffect effect, int width, int height, double pixelScale)
        {
            var core = Math.Max(0.5, pixelScale);
            var glow = VideoEffects.Number(effect, Def, "glow") * pixelScale;
            if (glow < 1)
                glow = 0;
            var margin = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(core, glow)) + 1);
            var band = Math.Max(1, Math.Min(height - margin, (int)Math.Round(VideoEffects.Number(effect, Def, "band") / 100 * height)));
            var lineGap = Math.Max(2, (int)Math.Round(LineGap * pixelScale));
            var gridGap = lineGap * Math.Max(3, (int)Math.Round(GridGap * pixelScale / lineGap));
            return new Shape(band, band + margin, core, glow, lineGap, gridGap);
        }

        /// <summary>시각 time초에 띠(창) 윗변의 y(틀 픽셀, 정수 — FFmpeg가 쓰는 값). 위에서 다 숨은 자리부터 아래로 다 지나간 자리까지.</summary>
        public static int BandTop(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            var shape = Measure(effect, width, height, pixelScale);
            return (int)Math.Floor(ScanLine.StripTop(time, VideoEffects.Number(effect, Def, "period"), height, shape.Window));
        }

        /// <summary>시각 time초에 앞머리 흰 선의 가운데(틀의 픽셀 모서리 좌표 — 이 줄 바로 위까지가 홀로그램).</summary>
        public static int EdgeY(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            return BandTop(effect, time, width, height, pixelScale) + Measure(effect, width, height, pixelScale).Band;
        }

        /// <summary>필터 조각(<see cref="VideoEffects"/>의 규칙). 받는 흐름은 틀 크기 gbrap.</summary>
        private static string Render(EffectRender r)
        {
            var shape = Measure(r.Effect, r.Width, r.Height, r.PixelScale);
            var p = r.Label;
            int w = r.Width, hs = shape.Window, hb = shape.Band;
            var once = ScanKit.Once(r);
            var hold = ScanKit.Hold(r);
            var opacity = r.Number("opacity") / 100;
            var (cr, cg, cb) = VideoEffects.Channels(r.Color);
            // 창 윗변(정수): 띠가 틀 밖에 걸치면 창은 틀 안으로 붙이고, 띠 모양은 그만큼 어긋난 자리를 잘라 맞춘다
            var top = string.Format(CultureInfo.InvariantCulture, "floor({0}+{1}*{2})", -hs, r.Height + hs, ScanKit.Ease(r, r.Number("period")));
            var y1 = string.Format(CultureInfo.InvariantCulture, "clip({0},0,{1})", top, r.Height - hs);
            var shift = string.Format(CultureInfo.InvariantCulture, "{0}+{1}-{2}", hs, y1, top);
            var text = new StringBuilder(ScanKit.OpenBand(r, hs, y1));

            // 홀로그램: 밝기만 남겨 색을 입히고(원래 색 15%), 화면에 붙은 주사선·격자 무늬와 overlay로 섞는다(무늬 128 = 그대로, 어두우면 어둡게·밝으면 밝게)
            var mix = Gain * (1 - Keep);
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}w]split[{0}e][{0}h];[{0}h]colorchannelmixer=rr={1}:rg={2}:rb={3}:gr={4}:gg={5}:gb={6}:br={7}:bg={8}:bb={9}[{0}t];", p,
                N(Keep + mix * 0.299 * cr), N(mix * 0.587 * cr), N(mix * 0.114 * cr),
                N(mix * 0.299 * cg), N(Keep + mix * 0.587 * cg), N(mix * 0.114 * cg),
                N(mix * 0.299 * cb), N(mix * 0.587 * cb), N(Keep + mix * 0.114 * cb));
            var dark = N(128 * (1 - 0.55 * r.Number("lines") / 100));
            var bright = N(128 + 127 * 0.75 * r.Number("grid") / 100);
            var pattern = string.Format(CultureInfo.InvariantCulture, "if(eq(mod(Y,{0}),0),{1},if(eq(mod(X,{2}),0)+eq(mod(Y,{2}),{3}),{4},128))", shape.LineGap, dark, shape.GridGap, shape.LineGap / 2, bright);
            // 무늬는 격자 한 칸만큼 더 높게 한 장 그려(R 면만 수식, 나머지 면에 복사) 창 윗변 mod 격자 간격 자리를 잘라 쓴다 — 화면에 붙어 있게
            text.Append(r.Still(string.Format(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=r='{4}':g=0:b=0:a=255",
                w, hs + shape.GridGap, r.Rate, once, pattern), "gbrap"));
            text.AppendFormat(CultureInfo.InvariantCulture, ",shuffleplanes=map0=2:map1=2:map2=2:map3=3{0},crop={1}:{2}:0:'mod({3},{4})'[{5}g];", hold, w, hs, y1, shape.GridGap, p);
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}g][{0}t]blend=all_mode=overlay:c3_mode=normal:c3_opacity=0[{0}q];", p);

            // 띠 모양(창 높이의 세 배 — 가운데 1/3이 띠): 섞는 정도(위 끝에서 차올라 앞머리까지 1, 그 아래 0)와 앞머리 빛(흰 선 + 색 빛 + 띠 안 옅은 색)
            var b = "(Y-" + hs + ")";
            var profile = string.Format(CultureInfo.InvariantCulture, "if(between({0},0,{1}),pow(min(1,({0}+1)/{2}),2),0)", b, hb - 1, N(Rise * hb));
            var distance = string.Format(CultureInfo.InvariantCulture, "abs({0}+0.5-{1})", b, hb);
            var core = string.Format(CultureInfo.InvariantCulture, "clip({0}-{1},0,1)", N(shape.Core + 0.5), distance);
            var glow = shape.Glow > 0 ? string.Format(CultureInfo.InvariantCulture, "pow(max(0,1-{0}/{1}),2)", distance, N(shape.Glow)) : "0";
            string Light(double c) => string.Format(CultureInfo.InvariantCulture, "{0}*min(1,{1}+{2}*({3}+{4}*{5}))", N(255 * opacity), core, N(c), glow, N(Fill), profile);
            var amount = N(255 * opacity) + "*" + profile;
            text.Append(ScanKit.Rows(r, w, 3 * hs, p + "r", (amount, amount, amount, "0", p + "m0"), (Light(cr), Light(cg), Light(cb), "255", p + "l0")));
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}m0]crop={1}:{2}:0:'{3}'[{0}m];[{0}l0]crop={1}:{2}:0:'{3}'[{0}l];", p, w, hs, shift);
            var mask = p + "m";
            var light = p + "l";
            if (r.Mask != null)
            {
                // 사람에게만: 섞는 정도와 빛 모두에 마스크(창 자리)를 곱한다
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}n]split[{0}n1][{0}n2];[{0}m][{0}n1]blend=all_mode=multiply[{0}m1];[{0}l][{0}n2]blend=all_mode=multiply[{0}l1];", p);
                mask = p + "m1";
                light = p + "l1";
            }
            // 섞는 정도의 알파 면은 0 — 섞은 창의 알파는 원래 것, 빛(알파 255)과 스크린으로 섞어 알파 255(얹을 때 다 덮게)
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}e][{0}q][{1}]maskedmerge[{0}x];[{2}][{0}x]blend=c0_mode=screen:c1_mode=screen:c2_mode=screen:c3_mode=screen[{0}s];", p, mask, light);
            text.Append(ScanKit.CloseBand(r, p + "s", y1));
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
