using System;
using System.Globalization;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 스캔 계열 효과(쇼크웨이브 스캔·SF 스캔·레이 스캐닝·글리치 스캔)가 같이 쓰는 필터 조각.
    /// - 훑는 자리 수식은 <see cref="ScanLine.Progress"/>와 같은 식(빨리 들어와 가운데서 머물다 빨리 나가고, 주기 끝 20%는 쉰다).
    /// - 한 장 그림: 수식(geq)은 구간 첫 프레임 한 장에만 쓰고 tpad로 같은 그림을 구간 끝까지 늘인다(매 장 드는 것은 자르기·얹기·섞기뿐).
    /// - 띠 창: 클립에서 띠 자리를 잘라(crop은 복사 없이 자리만 옮김) 띠 크기에서만 고치고, 제자리에 얹은 뒤 클립의 알파를 되돌려 놓는다.
    /// </summary>
    internal static class ScanKit
    {
        /// <summary>한 장 그림의 길이(구간 첫 프레임 하나만 나오게).</summary>
        public static string Once(EffectRender r)
        {
            return N(0.5 / r.Fps);
        }

        /// <summary>한 장 그림을 구간 끝까지 그대로 늘이는 꼬리.</summary>
        public static string Hold(EffectRender r)
        {
            return ",tpad=stop_mode=clone:stop_duration=" + N(r.Seconds);
        }

        /// <summary>주기 안 몫 u(0~1, 쉬는 동안 1) 수식 — 시각은 클립 안 시각(t + 앞에서 건너뛴 초).</summary>
        public static string Phase(EffectRender r, double period)
        {
            return string.Format(CultureInfo.InvariantCulture, "min(1,mod(t+{0},{1})/{2})", N(r.TimeOffset), N(period), N(period * (1 - ScanLine.Gap)));
        }

        /// <summary>훑는 자리(0 = 막 들어옴, 1 = 다 지나감) 수식 — <see cref="ScanLine.Progress"/>와 같은 식.</summary>
        public static string Ease(EffectRender r, double period)
        {
            return string.Format(CultureInfo.InvariantCulture, "(0.25*{0}+0.75*(0.5+4*pow({0}-0.5,3)))", Phase(r, period));
        }

        /// <summary>
        /// 줄(Y)마다만 다른 한 장 그림(width×height gbrap)을 만드는 조각: 한 열을 수식으로 그리고(점 height개) remap으로 가로로 늘인 뒤 구간 끝까지 늘인다.
        /// 넓은 그림 전체를 수식으로 그리는 것보다 훨씬 싸다. 그림마다 수식 넷(r·g·b·a, Y만 씀)과 라벨 하나.
        /// </summary>
        public static string Rows(EffectRender r, int width, int height, string label, params (string Red, string Green, string Blue, string Alpha, string Label)[] pictures)
        {
            var text = new StringBuilder();
            var once = Once(r);
            var count = pictures.Length;
            // 늘이는 지도: 가로 0(열 하나), 세로 = 줄 번호
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray16{4};", width, height, r.Rate, once, Outputs(label + "x", count));
            text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray16,geq=lum='Y'{4};", width, height, r.Rate, once, Outputs(label + "y", count));
            for (var i = 0; i < count; i++)
            {
                var p = pictures[i];
                var x = count > 1 ? label + "x" + i : label + "x";
                var y = count > 1 ? label + "y" + i : label + "y";
                text.AppendFormat(CultureInfo.InvariantCulture, "color=c=black:s=1x{0}:r={1}:d={2},format=gbrap,geq=r='{3}':g='{4}':b='{5}':a='{6}'[{7}c];[{7}c][{8}][{9}]remap{10}[{7}];",
                    height, r.Rate, once, p.Red, p.Green, p.Blue, p.Alpha, p.Label, x, y, Hold(r));
            }
            return text.ToString();
        }

        // 흐름 하나를 count곳에서 쓸 때의 끝: 하나면 "[이름]", 여럿이면 ",split=n[이름0][이름1]…"
        private static string Outputs(string name, int count)
        {
            if (count <= 1)
                return "[" + name + "]";
            var text = new StringBuilder(",split=" + count);
            for (var i = 0; i < count; i++)
                text.Append("[").Append(name).Append(i).Append("]");
            return text.ToString();
        }

        /// <summary>
        /// 띠 창 열기: 지금 흐름을 셋으로 나눠(바탕 [라벨a], 창 [라벨w], 알파 [라벨k]) 창은 띠 자리(윗변 y1 수식, 높이 height)를 잘라 둔다.
        /// 사람에게만이면 마스크도 같은 자리를 잘라 [라벨n](흰색 = 사람)으로 둔다.
        /// </summary>
        public static string OpenBand(EffectRender r, int height, string top)
        {
            var p = r.Label;
            var text = string.Format(CultureInfo.InvariantCulture, ",split=3[{0}a][{0}b][{0}k];[{0}b]crop={1}:{2}:0:'{3}'[{0}w];", p, r.Width, height, top);
            if (r.Mask != null)
                text += string.Format(CultureInfo.InvariantCulture, "[{0}]crop={1}:{2}:0:'{3}'[{4}n];", r.Mask, r.Width, height, top, p);
            return text;
        }

        /// <summary>
        /// 띠 창 닫기: 고친 창(알파 255여야 함 — 얹을 때 그 자리를 다 덮게)을 제자리에 얹고, 알파는 클립 것으로 되돌린다(투명한 곳은 투명하게, 반투명도 그대로).
        /// 마지막 필터의 출력에는 라벨을 달지 않는다(<see cref="VideoEffects"/>의 규칙).
        /// </summary>
        public static string CloseBand(EffectRender r, string band, string top)
        {
            return string.Format(CultureInfo.InvariantCulture, "[{0}a][{1}]overlay=x=0:y='{2}':format=gbrp[{0}o];[{0}k]alphaextract[{0}z];[{0}o][{0}z]alphamerge", r.Label, band, top);
        }

        /// <summary>시각 수식으로 정한 몇 번째 무늬(1초에 rate번 바뀜, 같은 클립 시각이면 같은 값) — 0~range-1의 정수.</summary>
        public static string Shuffle(EffectRender r, int rate, int range)
        {
            return string.Format(CultureInfo.InvariantCulture, "mod(floor((t+{0})*{1}+0.001)*397,{2})", N(r.TimeOffset), rate, Math.Max(1, range));
        }

        /// <summary>정수 하나의 0~1 해시 수식(같은 수면 같은 값 — random()을 쓰지 않는 의사 난수).</summary>
        public static string Hash(string index, double salt)
        {
            return "mod(abs(sin((" + index + ")*12.9898+" + N(salt) + ")*43758.5453),1)";
        }

        public static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
