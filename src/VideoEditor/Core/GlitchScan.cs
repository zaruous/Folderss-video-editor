using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 동영상 효과 "글리치 스캔": 가로 띠가 위에서 아래로 내려가며 띠 안의 그림을 깨뜨린다 — 가로 조각이 좌우로 밀리고(조각 높이가 제각각, 1초에 12번 바뀜),
    /// 빨강·파랑이 좌우로 어긋나고, 조각마다 밝기가 흔들리고, 가끔 네모 덩어리가 옆으로 뜯겨 나간다. 띠 가장자리는 날카롭다(띠 밖은 그대로). 움직임은 스캔 라인과 같다.
    /// 그리기: 띠 자리만 잘라(띠 창) 띠 크기에서 밀기(displace)·색 어긋남(rgbashift)·덩어리 바꾸기(swaprect)·밝기 무늬 섞기를 하고 제자리에 얹은 뒤 클립의 알파를 되돌린다.
    /// 밀기·밝기 무늬는 줄마다만 다른 한 장 그림을 시각으로 정한 자리에서 잘라 쓴다(같은 클립 시각이면 같은 무늬).
    /// </summary>
    public static class GlitchScan
    {
        public const string Id = "glitch-scan";
        // 세기 100에서 조각이 밀리는 최대 거리(프로젝트 픽셀) — FFmpeg displace 한도 127px까지
        private const double ShiftMax = 120;
        // 조각 높이(프로젝트 픽셀)와 무늬가 바뀌는 횟수(초당)
        private const double SliceHeight = 8;
        private const int Rate = 12;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "글리치 스캔", "스캔·빛",
            "가로 띠가 위에서 아래로 내려가며 띠 안의 그림을 깨뜨립니다(가로 조각 밀림·빨강/파랑 어긋남·밝기 흔들림). 띠 가장자리는 날카롭습니다.", 1,
            new[]
            {
                EffectParam.Number("band", "띠 높이", 3, 60, 18, "%", "그림이 깨지는 띠의 높이(틀 높이의 몫)"),
                EffectParam.Number("amount", "세기", 0, 100, 60, "%", "조각이 가로로 밀리는 정도(100 = 120px)"),
                EffectParam.Number("split", "색 분리", 0, 60, 12, "px", "빨강과 파랑이 좌우로 어긋나는 거리(0 = 어긋나지 않음)"),
                EffectParam.Number("period", "속도", 0.8, 6, 2.4, "초", "한 번 훑고 다시 시작할 때까지 걸리는 시간(작을수록 빠름)"),
                EffectParam.Number("opacity", "불투명도", 0, 100, 100, "%", "깨진 그림이 보이는 정도")
            },
            Render,
            // 썸네일 띠(1.5초)에 한 번 훑고 쉬는 것까지, 작은 그림에서도 밀림과 색 어긋남이 보이게
            new Dictionary<string, double> { ["period"] = 1.5, ["band"] = 45, ["amount"] = 100, ["split"] = 40 })
        { Since = 2 };

        /// <summary>띠 모양(출력 픽셀): 띠 높이, 밀리는 최대 거리(0 = 밀지 않음), 색 어긋남 거리, 조각 높이.</summary>
        public readonly struct Shape
        {
            public Shape(int band, int shift, int split, int slice)
            {
                Band = band;
                Shift = shift;
                Split = split;
                Slice = slice;
            }

            public int Band { get; }
            public int Shift { get; }
            public int Split { get; }
            public int Slice { get; }
        }

        /// <summary>width×height 틀, 픽셀 배율 pixelScale에서의 띠 모양.</summary>
        public static Shape Measure(ClipEffect effect, int width, int height, double pixelScale)
        {
            var band = Math.Max(2, Math.Min(height, (int)Math.Round(VideoEffects.Number(effect, Def, "band") / 100 * height)));
            var shift = (int)Math.Min(127, Math.Round(VideoEffects.Number(effect, Def, "amount") / 100 * ShiftMax * pixelScale));
            var split = (int)Math.Min(255, Math.Round(VideoEffects.Number(effect, Def, "split") * pixelScale));
            var slice = Math.Max(2, (int)Math.Round(SliceHeight * pixelScale));
            return new Shape(band, shift, split, slice);
        }

        /// <summary>시각 time초에 띠 윗변의 y(틀 픽셀, 정수 — FFmpeg가 쓰는 값). 띠는 [윗변, 윗변 + 띠 높이)의 줄이다.</summary>
        public static int BandTop(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            var shape = Measure(effect, width, height, pixelScale);
            return (int)Math.Floor(ScanLine.StripTop(time, VideoEffects.Number(effect, Def, "period"), height, shape.Band));
        }

        /// <summary>필터 조각(<see cref="VideoEffects"/>의 규칙). 받는 흐름은 틀 크기 gbrap.</summary>
        private static string Render(EffectRender r)
        {
            var shape = Measure(r.Effect, r.Width, r.Height, r.PixelScale);
            var p = r.Label;
            int w = r.Width, hb = shape.Band;
            var once = ScanKit.Once(r);
            var hold = ScanKit.Hold(r);
            var opacity = r.Number("opacity") / 100;
            var amount = r.Number("amount") / 100;
            var top = string.Format(CultureInfo.InvariantCulture, "floor({0}+{1}*{2})", -hb, r.Height + hb, ScanKit.Ease(r, r.Number("period")));
            var y1 = string.Format(CultureInfo.InvariantCulture, "clip({0},0,{1})", top, r.Height - hb);
            var shift = string.Format(CultureInfo.InvariantCulture, "{0}+{1}-{2}", hb, y1, top);
            var text = new StringBuilder(ScanKit.OpenBand(r, hb, y1));
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}w]split[{0}e][{0}g];", p);

            // 무늬(띠 높이의 네 배, 줄마다만 다름): 미는 지도(128 ± 거리 — 조각의 반쯤과 세 배 높이 덩어리 몇이 좌우로)와 밝기(조각마다 128 ± 흔들림, 가끔 밝은 줄)
            var cell = "floor(Y/" + shape.Slice + ")";
            var block = "floor(Y/" + (3 * shape.Slice) + ")";
            var slice = ScanKit.Hash(cell, 1.3);
            var chunk = ScanKit.Hash(block, 7.1);
            var push = string.Format(CultureInfo.InvariantCulture, "clip(128+if(gt({0},0.55),(2*mod({0}*7.13,1)-1)*{2},0)+if(gt({1},0.7),(2*mod({1}*5.37,1)-1)*{3},0),0,255)",
                slice, chunk, shape.Shift, N(0.7 * shape.Shift));
            var shake = N(18 + 22 * amount);
            var brightness = string.Format(CultureInfo.InvariantCulture, "if(gt({0},0.97),235,128+(2*{1}-1)*{2})", ScanKit.Hash("Y", 9.1), ScanKit.Hash(cell, 3.7), shake);
            var length = 4 * hb;
            text.Append(ScanKit.Rows(r, w, length, p + "r", (push, push, push, push, p + "x0"), (brightness, brightness, brightness, "255", p + "f0")));
            var pick = ScanKit.Shuffle(r, Rate, length - hb + 1);
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}x0]crop={1}:{2}:0:'{3}'[{0}x];[{0}f0]crop={1}:{2}:0:'{3}'[{0}f];", p, w, hb, pick);
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=0x808080:s={0}x{1}:r={2}:d={3},format=gbrap{4}[{5}y];", w, hb, r.Rate, once, hold, p);

            // 밀기 → 색 어긋남 → 덩어리(띠 안 네모 하나를 옆 자리와 바꿈, 1초에 12번 자리가 바뀌고 그중 2/3쯤에만) → 밝기 무늬
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}g][{0}x][{0}y]displace=edge=smear", p);
            if (shape.Split > 0)
                text.AppendFormat(CultureInfo.InvariantCulture, ",rgbashift=rh={0}:bh={1}", shape.Split, -shape.Split);
            var frame = string.Format(CultureInfo.InvariantCulture, "floor((t+{0})*{1}+0.001)", N(r.TimeOffset), Rate);
            var blockWidth = string.Format(CultureInfo.InvariantCulture, "max(1,floor({0}*(0.08+0.22*{1})))", w, ScanKit.Hash(frame, 1));
            var blockHeight = string.Format(CultureInfo.InvariantCulture, "max(1,floor({0}*(0.2+0.5*{1})))", hb, ScanKit.Hash(frame, 2));
            var x1 = string.Format(CultureInfo.InvariantCulture, "floor(({0}-{1})*{2})", w, blockWidth, ScanKit.Hash(frame, 3));
            var by = string.Format(CultureInfo.InvariantCulture, "floor(({0}-{1})*{2})", hb, blockHeight, ScanKit.Hash(frame, 5));
            var x2 = string.Format(CultureInfo.InvariantCulture, "clip({0}+(2*{1}-1)*{2},0,{3}-{4})", x1, ScanKit.Hash(frame, 6), Math.Max(8, 2 * shape.Shift), w, blockWidth);
            if (shape.Shift > 0)
                text.AppendFormat(CultureInfo.InvariantCulture, ",swaprect=w='{0}':h='{1}':x1='{2}':y1='{3}':x2='{4}':y2='{3}':enable='gt({5},0.35)'", blockWidth, blockHeight, x1, by, x2, ScanKit.Hash(frame, 4));
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}d];[{0}f][{0}d]blend=all_mode=overlay:c3_mode=screen[{0}q];", p);

            // 날카로운 띠 자리(창 높이의 세 배 — 가운데 1/3이 띠, 값 = 불투명도). 알파 면은 255라 섞은 창의 알파가 255(얹을 때 다 덮게)
            var level = (int)Math.Round(255 * opacity);
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=0x{0:X2}{0:X2}{0:X2}:s={1}x{2}:r={3}:d={4},format=gbrap,pad={1}:{5}:0:{2}:color=black{6},crop={1}:{2}:0:'{7}'[{8}m];",
                level, w, hb, r.Rate, once, 3 * hb, hold, shift, p);
            var mask = p + "m";
            if (r.Mask != null)
            {
                // 사람에게만: 띠 자리에 마스크(창 자리)를 곱한다
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}m][{0}n]blend=all_mode=multiply[{0}m1];", p);
                mask = p + "m1";
            }
            text.AppendFormat(CultureInfo.InvariantCulture, "[{0}e][{0}q][{1}]maskedmerge[{0}s];", p, mask);
            text.Append(ScanKit.CloseBand(r, p + "s", y1));
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
