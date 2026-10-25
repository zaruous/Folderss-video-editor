using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 동영상 효과 "레이 스캐닝": 레이저 같은 곧은 빛줄기(가운데 흰 선 + 색 빛)가 화면을 가로질러 훑고 지나간다(방향: 왼→오·오→왼·위→아래·아래→위).
    /// 지나온 쪽에는 짧은 꼬리가 남고, 빛에서 옆으로 가는 빛줄기가 흩어져 빛을 따라 흐르며, 빛 위를 오르내리는 반짝임(별 모양 빛)이 있다.
    /// 움직임은 스캔 라인과 같다(빨리 들어와 가운데서 머물다 빨리 나가고 잠깐 쉼). 빛은 스크린으로 섞어 밝게만 하고 클립의 알파는 그대로다.
    /// 그리기: 빛줄기 띠(꼬리·빛줄기 포함)를 한 장만 수식으로 그려(속 = R, 빛 = G) 색을 입히고, 빛줄기가 흐르도록 시각에 맞는 자리를 잘라
    /// 검은 틀에 얹은 뒤 반짝임 그림(한 장)을 얹어 클립과 스크린으로 섞는다.
    /// </summary>
    public static class RayScan
    {
        public const string Id = "ray-scan";
        // 빛줄기 100%에서 옆으로 뻗는 길이(프로젝트 픽셀), 빛줄기가 빛을 따라 흐르는 빠르기(프로젝트 픽셀/초), 빛줄기 두께(프로젝트 픽셀)
        private const double RayMax = 220;
        private const double Drift = 80;
        private const double RayThickness = 2;
        // 꼬리의 밝기(빛 번짐 대비)
        private const double TrailLevel = 0.5;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "레이 스캐닝", "스캔·빛",
            "레이저 같은 빛줄기가 화면을 가로질러 훑고 지나갑니다(빛 번짐·짧은 꼬리·옆으로 흩어지는 빛줄기와 반짝임). 빛은 화면을 밝게만 합니다.", 1,
            new[]
            {
                EffectParam.Number("glow", "빛 번짐", 0, 150, 36, "px", "가운데 흰 선 둘레 색 빛의 폭(0 = 빛 없이 선만)"),
                EffectParam.Number("core", "굵기", 1, 20, 4, "px", "가운데 흰 선의 굵기"),
                EffectParam.Number("trail", "꼬리", 0, 400, 140, "px", "지나온 쪽에 남는 빛 꼬리의 길이(0 = 꼬리 없음)"),
                EffectParam.Number("rays", "빛줄기", 0, 100, 60, "%", "빛에서 옆으로 뻗는 가는 빛줄기와 반짝임의 양(0 = 없음)"),
                EffectParam.Choice("dir", "방향", new[] { "왼쪽 → 오른쪽", "오른쪽 → 왼쪽", "위 → 아래", "아래 → 위" }, 0, "빛이 훑고 지나가는 방향"),
                EffectParam.Number("period", "속도", 0.8, 6, 2.4, "초", "한 번 훑고 다시 시작할 때까지 걸리는 시간(작을수록 빠름)"),
                EffectParam.Number("opacity", "불투명도", 0, 100, 100, "%", "빛의 세기"),
                EffectParam.ColorParam("color", "색", "#FF3355", "빛 번짐·꼬리·빛줄기의 색(가운데 선은 흰색)")
            },
            Render,
            // 썸네일 띠(1.5초)에 한 번 훑고 쉬는 것까지, 작은 그림에서도 빛과 빛줄기가 보이게 굵고 넓게
            new Dictionary<string, double> { ["period"] = 1.5, ["core"] = 10, ["glow"] = 80, ["trail"] = 260, ["rays"] = 100 })
        { Since = 2 };

        /// <summary>
        /// 빛 모양(출력 픽셀): 방향(0~3), 흰 선 반폭, 빛 번짐 폭, 꼬리 길이, 빛줄기 길이(0 = 없음), 띠 두께(움직이는 쪽으로), 띠 안 빛 가운데까지(지나온 쪽 끝에서),
        /// 빛줄기 무늬의 되풀이 길이(빛을 따라), 반짝임 그림 크기(0 = 없음).
        /// </summary>
        public readonly struct Shape
        {
            public Shape(int direction, double core, double glow, double trail, double rays, int thickness, int behind, int repeat, int sparkle)
            {
                Direction = direction;
                Core = core;
                Glow = glow;
                Trail = trail;
                Rays = rays;
                Thickness = thickness;
                Behind = behind;
                Repeat = repeat;
                Sparkle = sparkle;
            }

            public int Direction { get; }
            public double Core { get; }
            public double Glow { get; }
            public double Trail { get; }
            public double Rays { get; }
            public int Thickness { get; }
            public int Behind { get; }
            public int Repeat { get; }
            public int Sparkle { get; }
            /// <summary>빛이 세로줄(왼쪽·오른쪽으로 훑음)인지.</summary>
            public bool Vertical { get { return Direction < 2; } }
        }

        /// <summary>width×height 틀, 픽셀 배율 pixelScale에서의 빛 모양.</summary>
        public static Shape Measure(ClipEffect effect, int width, int height, double pixelScale)
        {
            var direction = (int)Math.Max(0, Math.Min(3, Math.Round(VideoEffects.Number(effect, Def, "dir"), MidpointRounding.AwayFromZero)));
            var core = Math.Max(0.5, VideoEffects.Number(effect, Def, "core") * pixelScale / 2);
            var glow = VideoEffects.Number(effect, Def, "glow") * pixelScale;
            if (glow < 1)
                glow = 0;
            var trail = VideoEffects.Number(effect, Def, "trail") * pixelScale;
            if (trail < 1)
                trail = 0;
            var amount = VideoEffects.Number(effect, Def, "rays") / 100;
            var rays = amount * RayMax * pixelScale;
            if (rays < 1)
                rays = 0;
            var ahead = (int)Math.Ceiling(Math.Max(Math.Max(glow, rays), core)) + 1;
            var behind = (int)Math.Ceiling(Math.Max(Math.Max(glow, rays), Math.Max(trail, core))) + 1;
            var along = direction < 2 ? height : width;
            var sparkle = amount > 0 ? 2 * (int)Math.Ceiling(Math.Max(Math.Max(glow, 0.6 * rays), 4)) + 1 : 0;
            return new Shape(direction, core, glow, trail, rays, ahead + behind, behind, Math.Max(2, along / 2), sparkle);
        }

        /// <summary>
        /// 시각 time초에 빛 가운데 선의 자리(틀의 픽셀 모서리 좌표 — 세로줄이면 x, 가로줄이면 y). 띠는 정수 픽셀로 얹는다(FFmpeg가 쓰는 값).
        /// </summary>
        public static double BeamPosition(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            var shape = Measure(effect, width, height, pixelScale);
            var travel = shape.Vertical ? width : height;
            var progress = ScanLine.Progress(time, VideoEffects.Number(effect, Def, "period"));
            var forward = shape.Direction % 2 == 0;
            var start = Math.Floor(forward ? -shape.Thickness + (travel + shape.Thickness) * progress : travel - (travel + shape.Thickness) * progress);
            return start + (forward ? shape.Behind : shape.Thickness - shape.Behind);
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
            var amount = r.Number("rays") / 100;
            var (cr, cg, cb) = VideoEffects.Channels(r.Color);
            var forward = shape.Direction % 2 == 0;
            var text = new StringBuilder("[" + p + "a];");

            // 띠 한 장(빛을 따라 되풀이 길이만큼 더 길게): 가로지름 좌표(빛에서의 거리 d, 나아가는 쪽이 +)와 빛을 따라가는 좌표
            var acrossPixel = shape.Vertical ? "X" : "Y";
            var alongPixel = shape.Vertical ? "Y" : "X";
            var d = forward
                ? string.Format(CultureInfo.InvariantCulture, "({0}+0.5-{1})", acrossPixel, shape.Behind)
                : string.Format(CultureInfo.InvariantCulture, "({0}-0.5-{1})", shape.Thickness - shape.Behind, acrossPixel);
            var core = string.Format(CultureInfo.InvariantCulture, "255*clip({0}-abs({1}),0,1)", N(shape.Core + 0.5), d);
            var glow = shape.Glow > 0 ? string.Format(CultureInfo.InvariantCulture, "pow(max(0,1-abs({0})/{1}),2)", d, N(shape.Glow)) : "0";
            var trail = shape.Trail > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}*lt({1},0)*pow(max(0,1+{1}/{2}),1.6)", N(TrailLevel), d, N(shape.Trail)) : "0";
            var streak = "0";
            if (shape.Rays > 0)
            {
                // 빛줄기: 두께 칸마다 해시로 고른 몇 줄(양은 빛줄기 값만큼)만, 길이·밝기는 줄마다 다르게 — 되풀이 길이로 감아 띠를 이어 잘라도 끊김이 없다
                var thickness = Math.Max(1, (int)Math.Round(RayThickness * r.PixelScale));
                var cell = string.Format(CultureInfo.InvariantCulture, "floor(mod({0},{1})/{2})", alongPixel, shape.Repeat, thickness);
                var hash = ScanKit.Hash(cell, 2.1);
                var density = 0.08 + 0.14 * amount;
                streak = string.Format(CultureInfo.InvariantCulture, "if(lt(abs({3}),{4}),if(gt({0},{1}),0.9*({0}-{1})/{2}*pow(max(0,1-abs({3})/({4}*(0.3+0.7*mod({0}*17.17,1)))),2),0),0)",
                    hash, N(1 - density), N(density), d, N(shape.Rays));
            }
            var light = string.Format(CultureInfo.InvariantCulture, "255*min(1,{0}+{1}+{2})", glow, trail, streak);
            int stripWidth = shape.Vertical ? shape.Thickness : w + shape.Repeat;
            int stripHeight = shape.Vertical ? h + shape.Repeat : shape.Thickness;
            // 빛줄기 흐름: 1초에 Drift px씩 빛을 따라 다른 자리를 잘라 쓴다(같은 클립 시각이면 같은 자리)
            var flow = string.Format(CultureInfo.InvariantCulture, "mod(floor((t+{0})*{1}),{2})", N(r.TimeOffset), N(Math.Max(1, Drift * r.PixelScale)), shape.Repeat);
            var crop = shape.Vertical
                ? string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}:0:'{2}'", shape.Thickness, h, flow)
                : string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}:'{2}':0", w, shape.Thickness, flow);
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=r='{4}':g='{5}':b=0:a=255,colorchannelmixer=rr={6}:rg={7}:rb=0:gr={6}:gg={8}:gb=0:br={6}:bg={9}:bb=0{10},{11}[{12}s];",
                stripWidth, stripHeight, r.Rate, once, core, light, N(opacity), N(opacity * cr), N(opacity * cg), N(opacity * cb), hold, crop, p);

            // 검은 틀에 띠를 시각의 자리로 얹는다
            var travel = shape.Vertical ? w : h;
            var ease = ScanKit.Ease(r, period);
            var position = forward
                ? string.Format(CultureInfo.InvariantCulture, "floor({0}+{1}*{2})", -shape.Thickness, travel + shape.Thickness, ease)
                : string.Format(CultureInfo.InvariantCulture, "floor({0}-{1}*{2})", travel, travel + shape.Thickness, ease);
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap[{4}c];", w, h, r.Rate, N(r.Seconds), p);
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}c][{0}s]overlay=x='{1}':y='{2}':format=gbrp", p, shape.Vertical ? position : "0", shape.Vertical ? "0" : position);

            if (shape.Sparkle > 0)
            {
                // 반짝임(한 장): 흰 속(R)과 색 빛무리·가로세로 빛살·옅은 대각 빛살(G)을 그려 색을 입히고, 알파 = 밝기(검은 틀 위에서는 그대로 빛)
                var size = shape.Sparkle;
                var half = size / 2.0;
                var radius = string.Format(CultureInfo.InvariantCulture, "hypot(X+0.5-{0},Y+0.5-{0})", N(half));
                var fade = string.Format(CultureInfo.InvariantCulture, "max(0,1-{0}/{1})", radius, N(half));
                var hot = string.Format(CultureInfo.InvariantCulture, "255*clip(1-{0}/{1},0,1)", radius, N(3 * shape.Core + 1.5));
                var spikes = string.Format(CultureInfo.InvariantCulture, "pow({0},2)*(max(0,1-abs(X+0.5-{1})/{2})+max(0,1-abs(Y+0.5-{1})/{2})+0.5*max(0,1-abs(abs(X+0.5-{1})-abs(Y+0.5-{1}))/{3}))",
                    fade, N(half), N(Math.Max(1, 1.2 * r.PixelScale)), N(Math.Max(1, 1.7 * r.PixelScale)));
                var halo = string.Format(CultureInfo.InvariantCulture, "255*min(1,0.8*pow({0},3)+{1})", fade, spikes);
                // 색은 세기를 곱하지 않고(가운데는 흰색 그대로 — 흰 선 위에 얹어도 어두워지지 않게) 알파에만 불투명도·빛줄기 양을 곱한다
                var level = opacity * (0.4 + 0.6 * amount);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}b];color=c=black:s={1}x{1}:r={2}:d={3},format=gbrap,geq=r='{4}':g='{5}':b=0:a=0,"
                    + "colorchannelmixer=rr=1:rg={6}:rb=0:gr=1:gg={7}:gb=0:br=1:bg={8}:bb=0:ar={9}:ag={9}:ab=0:aa=0{10}[{0}f];",
                    p, size, r.Rate, once, hot, halo, N(cr), N(cg), N(cb), N(level), hold);
                // 빛 위를 오르내린다(주기마다 한 번 — 사인)
                var lengthAlong = shape.Vertical ? h : w;
                var slide = string.Format(CultureInfo.InvariantCulture, "floor({0}*(0.5+0.3*sin(2*PI*(t+{1})/{2}))-{3})", lengthAlong, N(r.TimeOffset), N(period), (size - 1) / 2);
                var across = string.Format(CultureInfo.InvariantCulture, "{0}+{1}-{2}", position, forward ? shape.Behind : shape.Thickness - shape.Behind, (size + 1) / 2);
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}b][{0}f]overlay=x='{1}':y='{2}':format=gbrp", p, shape.Vertical ? across : slide, shape.Vertical ? slide : across);
            }
            if (r.Mask != null)
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}l];[{0}l][{1}]blend=all_mode=multiply", p, r.Mask);
            // 스크린으로 섞는다 — 알파는 클립 것 그대로(빛 쪽 알파 255를 곱함)
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}v];[{0}v][{0}a]blend=c0_mode=screen:c1_mode=screen:c2_mode=screen:c3_mode=multiply", p);
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
