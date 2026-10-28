using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 글리치 효과 묶음(RGB 분할·블록 글리치·줄 밀림·나쁜 TV 신호)이 함께 쓰는 것: 시각에서 만드는 흉내 난수, 프레임 번호, 미는 지도, 사람에게만 섞기.
    /// - 난수는 클립 안 프레임 번호(반올림한 정수)와 그 번호로 센 칸 번호에서만 만든다 — random()·noise 필터를 쓰지 않아 한 장 그리기와 이어 그리기가 같은 프레임을 같게 그린다.
    ///   (noise 필터는 프레임 수로 무늬를 바꾸고, 이 FFmpeg 판에서는 같은 그래프의 displace 지도가 스레드에 따라 깨지는 일까지 있어 쓰지 않는다 — 잡음은 수식으로 한 번 그린다.)
    /// - 미는 지도(displace)는 작은 그림을 수식(geq)으로 그려 틀 크기로 늘인 따로 떨어진 체인이다. 클립 체인에는 크기 바꾸기(scale)를 잇지 않는다.
    ///   지도는 클립과 같은 gbrap라 플레인마다(G·B·R·A) 따로 밀 수 있다 — 빨강·파랑만 더 미는 색 어긋남이 displace 한 번으로 된다.
    /// </summary>
    internal static class Glitch
    {
        public const string Group = "글리치";
        // displace가 밀 수 있는 거리(지도 값 0~255, 가운데 128)
        public const int MaxShift = 127;

        /// <summary>흉내 난수 0~1 — FFmpeg 식 <see cref="HashExpr"/>과 같은 계산(시험에서 글리치가 일어나는 때를 미리 안다).</summary>
        public static double Hash(double x)
        {
            var v = Math.Abs(Math.Sin(x) * 43758.5453);
            return v - Math.Floor(v);
        }

        public static string HashExpr(string x)
        {
            return "mod(abs(sin(" + x + ")*43758.5453),1)";
        }

        /// <summary>클립 안 프레임 번호 식(반올림 — 시각 반올림 오차에 흔들리지 않는다). t는 crop·overlay에서 "t", geq에서 "T".</summary>
        public static string FrameExpr(EffectRender r, string t)
        {
            return "floor((" + t + "+" + N(r.TimeOffset) + ")*" + N(r.Fps) + "+0.5)";
        }

        /// <summary>프레임 번호 식 frame에서 1초에 rate번 바뀌는 칸 번호 식.</summary>
        public static string SlotExpr(EffectRender r, string frame, double rate)
        {
            return "floor(" + frame + "*" + N(rate / r.Fps) + "+0.001)";
        }

        /// <summary><see cref="SlotExpr"/>와 같은 값: frame번째 프레임(초당 fps장)이 1초에 rate번 바뀌는 칸의 몇 번째인지.</summary>
        public static long Slot(long frame, double fps, double rate)
        {
            return (long)Math.Floor(frame * double.Parse(N(rate / fps), CultureInfo.InvariantCulture) + 0.001);
        }

        /// <summary>
        /// 미는 지도 체인(앞에 ";"): cols×rows gbrap를 플레인마다 수식으로 그려 틀 크기로 늘인다(neighbor — 칸이 그대로 네모).
        /// 식의 128 = 밀지 않음. 크기 바꾸기는 이 따로 떨어진 체인에 하나뿐이다.
        /// </summary>
        public static string Map(EffectRender r, int cols, int rows, string g, string b, string red, string a, string label)
        {
            return string.Format(CultureInfo.InvariantCulture, ";color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=g='{4}':b='{5}':r='{6}':a='{7}',scale={8}:{9}:flags=neighbor",
                cols, rows, r.Rate, N(r.Seconds), g, b, red, a, r.Width, r.Height) + MapOut(r, label);
        }

        /// <summary>밀지 않는 지도(모든 플레인 128 — 알파까지, 그래서 색은 0x80808080).</summary>
        public static string FlatMap(EffectRender r, string label)
        {
            return string.Format(CultureInfo.InvariantCulture, ";color=c=0x80808080:s={0}x{1}:r={2}:d={3},format=gbrap", r.Width, r.Height, r.Rate, N(r.Seconds)) + MapOut(r, label);
        }

        /// <summary>지도 체인의 끝: [라벨]. 사람에게만이면 사람 마스크도 같은 지도로 밀어야 해서 둘로 나눈다([라벨]·[라벨 + "m"]).</summary>
        public static string MapOut(EffectRender r, string label)
        {
            return r.Mask != null ? ",split[" + label + "][" + label + "m]" : "[" + label + "]";
        }

        /// <summary>
        /// 사람에게만 — 마스크를 그림과 같은 지도로 차례로 미는 조각: [input]에서 받아 [라벨 mt]로 낸다(maps는 <see cref="MapOut"/>으로 나눈 지도 라벨 쌍).
        /// 밀린 마스크는 밀린 사람 픽셀이 떨어진 자리다(플레인마다 — 빨강·파랑만 더 민 색 어긋남도 그 플레인의 자리로).
        /// </summary>
        public static string Displaced(EffectRender r, string input, params (string X, string Y)[] maps)
        {
            var text = new StringBuilder();
            var from = input;
            for (var i = 0; i < maps.Length; i++)
            {
                var to = i + 1 < maps.Length ? r.Label + "mt" + i : r.Label + "mt";
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}][{1}m][{2}m]displace=edge=smear[{3}];", from, maps[i].X, maps[i].Y, to);
                from = to;
            }
            return text.ToString();
        }

        /// <summary>
        /// 시작: 지금 흐름에 라벨을 붙여 끊는다 — 사람에게만이면 원본을 하나 더 남긴다(라벨 + "o").
        /// </summary>
        public static string Begin(EffectRender r, string label)
        {
            return r.Mask != null ? ",split[" + label + "][" + r.Label + "o]" : "[" + label + "]";
        }

        /// <summary>
        /// 끝(사람에게만): 효과를 입힌 흐름을 원본 위에 섞는다 — 섞는 마스크는 사람 마스크(흰색 = 사람)와, 그 마스크를 그림과 똑같이 민 것(moved — [라벨 mk]를 받아
        /// [라벨 mt]로 내는 조각) 중 큰 값이다. 그래서 사람 안쪽은 예전처럼 깨지고, 사람 밖으로 밀려 나간 사람 픽셀(조각·줄·빨강·파랑)도 그 자리에 보인다.
        /// 밀린 사람 픽셀이 떨어지지 않은 바깥은 원본과 바이트까지 같다(배경은 깨지지 않음). R·G·B만 섞고 알파는 원본 그대로(maskedmerge planes=7).
        /// 사람에게만이 아니면 빈 글.
        /// </summary>
        public static string Finish(EffectRender r, string moved)
        {
            if (r.Mask == null)
                return "";
            var p = r.Label;
            return "[" + p + "g];[" + r.Mask + "]split[" + p + "mk][" + p + "mj];" + moved + "[" + p + "mj][" + p + "mt]blend=all_mode=lighten[" + p + "ml];"
                + "[" + p + "o][" + p + "g][" + p + "ml]maskedmerge=planes=7";
        }

        /// <summary>
        /// 그릴 것이 없을 때(세기 0 등): 그대로 둔다. 사람에게만이면 마스크 흐름을 써야 해서(쓰지 않은 라벨은 그래프 오류) 원본끼리 섞는다(그림은 그대로).
        /// </summary>
        public static string Pass(EffectRender r)
        {
            return r.Mask == null ? "" : ",split[" + r.Label + "o][" + r.Label + "g];[" + r.Label + "o][" + r.Label + "g][" + r.Mask + "]maskedmerge=planes=7";
        }

        /// <summary>
        /// 이 틀에서 밀 수 있는 거리(출력 px): 프로젝트 크기의 한도 127px을 효과 배율만큼(두 번에 나눠 밀어 254px까지).
        /// 1080p를 4K로 내보내도 1080p와 같은 비율로 밀린다. 배율 1 이하는 127(한 번 — 예전과 같음).
        /// </summary>
        public static int Limit(double pixelScale)
        {
            return (int)Math.Min(2 * MaxShift, Math.Round(MaxShift * Math.Max(1, pixelScale)));
        }

        /// <summary>
        /// 미는 지도 값(geq 식). pass 0 = 한 번에 미는 지도(clip(128 + 거리), 예전과 같은 글), 1·2 = 두 번에 나눠 미는 앞·뒤 지도 —
        /// 둘의 합이 round(거리)를 ±limit로 자른 값이고 각각 128 ± 127 안이다. 줄마다 가로로만 미는 지도(같은 줄은 같은 거리)는 두 번 밀어도 한 번에 민 것과 같다.
        /// </summary>
        public static string Push(string offset, int pass, int limit)
        {
            if (pass == 0)
                return "clip(128+" + offset + ",0,255)";
            var total = "clip(round(" + offset + "),-" + limit + "," + limit + ")";
            return pass == 1 ? "(128+" + total + "-trunc(" + total + "/2))" : "(128+trunc(" + total + "/2))";
        }

        /// <summary>길이(프로젝트 px)를 이 틀의 출력 픽셀로, displace 한도(127px) 안의 정수로.</summary>
        public static int Shift(double projectPixels, double pixelScale)
        {
            return (int)Math.Min(MaxShift, Math.Round(projectPixels * pixelScale));
        }

        public static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }

    /// <summary>
    /// 동영상 효과 "RGB 분할": 빨강과 파랑을 서로 반대쪽으로 밀어 색이 갈라져 보인다(초록·알파는 제자리).
    /// 흔들림을 주면 갈라지는 거리와 방향이 1초에 12번 튀고, 빛 번짐을 주면 더 멀리 민 빨강·파랑을 흐리게 겹쳐 가장자리에 옅은 빛이 번진다.
    /// 그리기: 그림을 사방으로 늘여(가장자리 번짐) 두고, 빨강·파랑 자리만큼 잘라 낸 흐름에서 그 플레인만 가져와 합친다(pad·crop·mergeplanes — 수식은 자르는 자리에만).
    /// 사람에게만: 갈라진 그림을 사람 자리와, 사람의 빨강·파랑이 밀려 나간 자리에만 섞는다(마스크도 같은 몸통으로 갈라 플레인마다 — 배경은 갈라지지 않음).
    /// </summary>
    public static class RgbSplit
    {
        public const string Id = "rgb-split";
        // 흔들림 100에서 방향이 튀는 폭(라디안), 빛 번짐 쪽 빨강·파랑이 가는 거리(배)
        private const double AngleJitter = 0.6;
        private const double GlowReach = 1.6;
        // 튀는 횟수(초당)
        private const int JitterRate = 12;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "RGB 분할", Glitch.Group,
            "빨강과 파랑을 서로 반대쪽으로 밀어 색이 갈라져 보입니다(흔들림을 주면 갈라짐이 시간에 따라 튀고, 가장자리에 옅은 빛이 번짐).", 1,
            new[]
            {
                EffectParam.Number("amount", "거리", 0, 60, 12, "px", "빨강·파랑이 밀리는 거리(0 = 갈라지지 않음)"),
                EffectParam.Number("angle", "방향", 0, 360, 0, "°", "빨강이 밀리는 방향(0 = 오른쪽, 90 = 아래) — 파랑은 반대쪽"),
                EffectParam.Number("jitter", "흔들림", 0, 100, 30, "%", "갈라지는 거리·방향이 시간에 따라 튀는 정도(0 = 고정)"),
                EffectParam.Number("glow", "빛 번짐", 0, 100, 30, "%", "갈라진 가장자리에 번지는 옅은 빛(0 = 없음)")
            },
            Render,
            // 썸네일(작은 그림)에서도 갈라짐과 튀는 것이 보이게 멀리, 많이 흔들리게
            new Dictionary<string, double> { ["amount"] = 40, ["jitter"] = 60, ["glow"] = 50 })
        { Since = 2 };

        /// <summary>
        /// 프레임 번호 frame(클립 안, 초당 fps장)에서 빨강이 밀리는 거리(출력 px)와 방향(라디안) — 파랑은 반대로. 그리는 식과 같은 계산.
        /// </summary>
        public static (double Distance, double Angle) Offset(ClipEffect effect, long frame, double fps, double pixelScale)
        {
            var d = VideoEffects.Number(effect, Def, "amount") * pixelScale;
            var j = VideoEffects.Number(effect, Def, "jitter") / 100;
            var n = Glitch.Slot(frame, fps, JitterRate);
            var k = (1 - j) + 2 * j * Glitch.Hash(n * 12.9898 + 1.7);
            var angle = VideoEffects.Number(effect, Def, "angle") * Math.PI / 180 + AngleJitter * j * (2 * Glitch.Hash(n * 78.233 + 4.1) - 1);
            return (d * k, angle);
        }

        private static string Render(EffectRender r)
        {
            var p = r.Label;
            var d = r.Number("amount") * r.PixelScale;
            if (d < 0.5)
                return Glitch.Pass(r);
            var j = r.Number("jitter") / 100;
            var glow = r.Number("glow") / 100;
            // 늘일 폭 = 가장 멀리 미는 거리(흔들림 최대 × 빛 번짐 쪽 배) + 1
            var pad = (int)Math.Ceiling(d * (1 + j) * (glow > 0 ? GlowReach : 1)) + 1;
            var frame = Glitch.FrameExpr(r, "t");
            var n = Glitch.SlotExpr(r, frame, JitterRate);
            var k = string.Format(CultureInfo.InvariantCulture, "({0}+{1}*{2})", N(1 - j), N(2 * j), Glitch.HashExpr(n + "*12.9898+1.7"));
            var angle = string.Format(CultureInfo.InvariantCulture, "({0}+{1}*(2*{2}-1))", N(r.Number("angle") * Math.PI / 180), N(AngleJitter * j), Glitch.HashExpr(n + "*78.233+4.1"));
            var dx = N(d) + "*" + k + "*cos(" + angle + ")";
            var dy = N(d) + "*" + k + "*sin(" + angle + ")";
            // 밀 쪽: 빨강(+), 파랑(-) — 빛 번짐이면 더 멀리(×1.6) 민 둘을 하나 더
            var shifts = new List<(double Factor, string Name)> { (1, "r"), (-1, "b") };
            if (glow > 0)
            {
                shifts.Add((GlowReach, "r2"));
                shifts.Add((-GlowReach, "b2"));
            }
            // 늘여 자르고 플레인을 합치는 몸통: head(지금 흐름이면 ",", 마스크면 "[라벨]")의 그림을 라벨 앞머리 q로. 끝 출력에는 라벨을 달지 않는다.
            // 그림: [q w] 늘여 자를 것, [q m] 초록·알파, [q n] 빛 번짐의 초록·알파, (사람에게만이면 섞을 원본 [o]).
            // 마스크는 네 플레인이 같으므로 한 플레인(gray)으로 늘이고 잘라 플레인마다 합친다 — 플레인마다 같은 일을 하는 것과 값은 같고 일은 1/4
            string Body(string q, string head, bool keepOriginal, bool mask)
            {
                var body = new StringBuilder();
                var copies = new List<string> { q + "w", q + "m" };
                if (glow > 0)
                    copies.Add(q + "n");
                if (keepOriginal)
                    copies.Add(p + "o");
                body.Append(head + (mask ? "extractplanes=g," : "") + "split=" + copies.Count + string.Concat(copies.Select(c => "[" + c + "]")) + ";");
                body.AppendFormat(CultureInfo.InvariantCulture, "[{0}w]pad={1}:{2}:{3}:{3},fillborders=left={3}:right={3}:top={3}:bottom={3}:mode=smear,split={4}{5}",
                    q, r.Width + 2 * pad, r.Height + 2 * pad, pad, shifts.Count, string.Concat(shifts.Select(s => "[" + q + "q" + s.Name + "]")));
                foreach (var (factor, name) in shifts)
                {
                    // 자르는 자리를 미는 만큼 반대로 — 잘라 낸 그림은 (dx, dy)만큼 밀려 보인다
                    body.AppendFormat(CultureInfo.InvariantCulture, ";[{0}q{1}]crop={2}:{3}:'{4}-({5})*{6}':'{4}-({5})*{7}'[{0}{1}]",
                        q, name, r.Width, r.Height, pad, N(factor), dx, dy);
                }
                // gbrap 플레인: 0 = G, 1 = B, 2 = R, 3 = A — 초록·알파는 원본, 파랑·빨강은 민 흐름에서(마스크는 한 플레인씩)
                var merge = mask
                    ? "mergeplanes=format=gbrap:map0s=0:map0p=0:map1s=1:map1p=0:map2s=2:map2p=0:map3s=0:map3p=0"
                    : "mergeplanes=format=gbrap:map0s=0:map0p=0:map1s=1:map1p=1:map2s=2:map2p=2:map3s=0:map3p=3";
                body.AppendFormat(CultureInfo.InvariantCulture, ";[{0}m][{0}b][{0}r]{1}", q, merge);
                if (glow > 0)
                {
                    // 빛 번짐: 더 멀리 민 빨강·파랑을 흐려(그 두 플레인만) 밝은 쪽으로 겹친다
                    var radius = Math.Max(1, (int)Math.Round(d * 0.6));
                    body.AppendFormat(CultureInfo.InvariantCulture, "[{0}s];[{0}n][{0}b2][{0}r2]{1},avgblur=sizeX={2}:planes=6[{0}f];[{0}s][{0}f]blend=all_mode=lighten:all_opacity={3}",
                        q, merge, radius, N(0.8 * glow));
                }
                return body.ToString();
            }
            var text = new StringBuilder(Body(p, ",", r.Mask != null, false));
            // 사람에게만: 마스크도 같은 몸통으로 갈라 — 사람의 빨강·파랑이 마스크 밖으로 밀려 나간 자리도 그 플레인에 칠한다
            text.Append(Glitch.Finish(r, Body(p + "mk", "[" + p + "mk]", false, true) + "[" + p + "mt];"));
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }

    /// <summary>
    /// 동영상 효과 "블록 글리치": 짧은 순간(1/8초 칸)마다 정해진 비율로 글리치가 일어나 화면 곳곳의 네모 조각이 옆(가끔 위아래)으로 밀리고,
    /// 조각 안에서 빨강·파랑이 따로 밀리거나 색이 바뀐다. 일어나는 때·조각 자리는 클립 안 프레임 번호의 해시로 정해 늘 같다(1초에 16번 조각이 바뀜).
    /// 그리기: 칸 크기 한 장에 칸마다 값을 수식으로 한 번 계산하고(geq), 그 값을 읽어 지도 셋(가로 밀기 — 플레인마다, 세로 밀기, 색 바꾸기)으로 늘여 displace 한 번 + grainmerge 섞기 한 번.
    /// 사람에게만: 글리치 그림을 사람 자리와, 사람 조각이 밀려 떨어진 자리에만 섞는다(마스크도 같은 지도로 민다).
    /// </summary>
    public static class BlockGlitch
    {
        public const string Id = "block-glitch";
        /// <summary>글리치가 일어날지 정하는 칸(초당), 조각 무늬가 바뀌는 횟수(초당).</summary>
        public const int BurstRate = 8;
        public const int PatternRate = 16;
        // 세기 100에서 미는 거리(프로젝트 px), 색 어긋남 100에서 빨강·파랑이 더 밀리는 거리(프로젝트 px)와 색 바꾸기 세기(0~255)
        private const double ShiftMax = 120;
        private const double ColorShiftMax = 40;
        private const double TintMax = 90;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "블록 글리치", Glitch.Group,
            "짧은 순간마다 화면 곳곳의 네모 조각이 옆으로 밀리고 색이 어긋납니다(일어나는 때와 조각 자리는 시각으로 정해져 늘 같음).", 1,
            new[]
            {
                EffectParam.Number("amount", "세기", 0, 100, 60, "%", "조각이 밀리는 거리와 조각 수(100% = 120px)"),
                EffectParam.Number("block", "블록 크기", 8, 240, 64, "px", "조각의 높이(너비는 그 1~4배)"),
                EffectParam.Number("rate", "빈도", 0, 100, 35, "%", "글리치가 일어나는 때의 비율(0 = 없음, 100 = 늘)"),
                EffectParam.Number("color", "색 어긋남", 0, 100, 50, "%", "조각 안에서 빨강·파랑이 따로 밀리고 색이 바뀌는 정도(0 = 밀리기만)")
            },
            Render,
            // 썸네일 띠(1.5초) 내내 글리치가 보이게 늘, 작은 그림에서도 조각이 보이게 크게
            new Dictionary<string, double> { ["rate"] = 100, ["amount"] = 100, ["block"] = 120, ["color"] = 80 })
        { Since = 2 };

        /// <summary>프레임 번호 frame(클립 안, 초당 fps장)에 글리치가 일어나는지 — 그리는 식과 같은 계산. 경계에서 멀면(margin) 확실하다.</summary>
        public static bool IsBurst(ClipEffect effect, long frame, double fps, double margin = 0)
        {
            return Glitch.Hash(Glitch.Slot(frame, fps, BurstRate) * 12.9898 + 0.31) < VideoEffects.Number(effect, Def, "rate") / 100 - margin;
        }

        /// <summary>글리치가 일어나지 않는 게 확실한지(경계에서 margin만큼 먼 쪽).</summary>
        public static bool IsQuiet(ClipEffect effect, long frame, double fps, double margin = 0)
        {
            return Glitch.Hash(Glitch.Slot(frame, fps, BurstRate) * 12.9898 + 0.31) >= VideoEffects.Number(effect, Def, "rate") / 100 + margin;
        }

        private static string Render(EffectRender r)
        {
            var p = r.Label;
            var amount = r.Number("amount") / 100;
            var rate = r.Number("rate") / 100;
            if (amount <= 0 || rate <= 0)
                return Glitch.Pass(r);
            var shift = Glitch.Shift(amount * ShiftMax, r.PixelScale);
            var color = r.Number("color") / 100;
            var colorShift = Glitch.Shift(color * ColorShiftMax, r.PixelScale);
            var tint = color * TintMax;
            // 칸: 높이 = 블록 크기, 너비 = 그 절반 — 조각은 2~8칸(블록 크기의 1~4배) 너비
            var height = Math.Max(2, r.Number("block") * r.PixelScale);
            var cols = Math.Max(1, (int)Math.Round(r.Width / (height / 2)));
            var rows = Math.Max(1, (int)Math.Round(r.Height / height));
            string H(string x) => Glitch.HashExpr(x);
            var frame = Glitch.FrameExpr(r, "T");
            var n = Glitch.SlotExpr(r, frame, BurstRate);
            var m = Glitch.SlotExpr(r, frame, PatternRate);
            // 일어나는 칸이면 1, 세기는 칸마다 0.4~1배
            var gate = "lt(" + H(n + "*12.9898+0.31") + "," + N(rate) + ")";
            var power = "(0.4+0.6*" + H(n + "*4.898+7.23") + ")";
            // 줄(Y)마다 조각 너비 L칸과 시작 어긋남을 정하고, 조각 번호·줄·무늬 번호로 조각의 열쇠를 만든다
            var length = "(2+floor(7*" + H("Y*3.17+" + m + "*0.618+1.3") + "))";
            var piece = "floor((X+floor(" + length + "*" + H("Y*5.31+" + m + "*0.71+2.9") + "))/" + length + ")";
            var key = "(" + piece + "*7.13+Y*11.71+" + m + "*1.37)";
            var active = gate + "*lt(" + H(key + "+0.5") + "," + N(0.12 + 0.3 * amount) + ")";
            var dx = N(shift) + "*" + power + "*(2*" + H(key + "*1.913+3.3") + "-1)";
            var dy = "lt(" + H(key + "*3.31+5.7") + ",0.35)*" + N(Math.Round(shift * 0.4)) + "*(2*" + H(key + "*4.77+1.1") + "-1)";
            var tinted = tint > 0 ? active + "*lt(" + H(key + "*5.91+2.3") + ",0.6)*(1+floor(3*" + H(key + "*6.37+4.4") + "))" : "0";

            // 1단계 — 칸 크기 한 장에 칸마다 값을 한 번만 계산한다(수식이 길어 지도마다 다시 풀면 1080p에서 장당 30ms 가까이 든다):
            // G = 가로 밀기(128 기준), B = 조각 난수(0 = 조각 아님, 1~255), R = 세로 밀기(128 기준), A = 색 바꾸기(0 = 없음, 1~3 = 자홍·초록·청록)
            var text = new StringBuilder(Glitch.Begin(r, p + "a"));
            text.AppendFormat(CultureInfo.InvariantCulture,
                ";color=c=black:s={0}x{1}:r={2}:d={3},format=gbrap,geq=g='clip(128+{4}*{5},0,255)':b='{4}*(1+floor(254*{6}))':r='clip(128+{4}*{7},0,255)':a='{8}',split={9}[{10}k0][{10}k1]{11}",
                cols, rows, r.Rate, N(r.Seconds), active, dx, H(key + "*2.71+8.1"), dy, tinted, tint > 0 ? 3 : 2, p, tint > 0 ? "[" + p + "k2]" : "");
            // 2단계 — 칸 값을 읽어(짧은 식) 틀 크기로 늘인다(neighbor). 가로 지도: 초록·알파는 칸의 G, 빨강·파랑은 서로 반대로 색 어긋남만큼 더
            var side = "gt(b(X,Y),0)*(2*lt(b(X,Y),128)-1)";
            text.AppendFormat(CultureInfo.InvariantCulture,
                ";[{0}k0]geq=g='g(X,Y)':b='clip(g(X,Y)-{1}*{2},0,255)':r='clip(g(X,Y)+{1}*{2},0,255)':a='g(X,Y)',scale={3}:{4}:flags=neighbor{5}",
                p, N(colorShift), side, r.Width, r.Height, Glitch.MapOut(r, p + "x"));
            // 세로 지도: 모든 플레인이 칸의 R
            text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}k1]shuffleplanes=map0=2:map1=2:map2=2:map3=2,scale={1}:{2}:flags=neighbor{3}", p, r.Width, r.Height, Glitch.MapOut(r, p + "y"));
            if (tint > 0)
            {
                // 색 바꾸기: 조각의 60%에 자홍·초록·청록 가운데 하나를 더한다(128 = 그대로 — grainmerge)
                string Tint(double magenta, double green, double cyan) => string.Format(CultureInfo.InvariantCulture,
                    "128+{0}*if(eq(alpha(X,Y),1),{1},if(eq(alpha(X,Y),2),{2},if(eq(alpha(X,Y),3),{3},0)))", N(tint), N(magenta), N(green), N(cyan));
                text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}k2]geq=g='{1}':b='{2}':r='{3}':a='128',scale={4}:{5}:flags=neighbor[{0}c]",
                    p, Tint(-0.8, 1, 0.3), Tint(1, -0.7, 1), Tint(1, -0.7, -0.8), r.Width, r.Height);
            }
            text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}a][{0}x][{0}y]displace=edge=smear", p);
            if (tint > 0)
                text.AppendFormat(CultureInfo.InvariantCulture, "[{0}d];[{0}d][{0}c]blend=c0_mode=grainmerge:c1_mode=grainmerge:c2_mode=grainmerge:c3_mode=normal", p);
            // 사람에게만: 마스크도 같은 지도로 밀어 사람 조각이 떨어진 자리까지 칠한다(색 바꾸기는 자리를 옮기지 않음)
            text.Append(Glitch.Finish(r, Glitch.Displaced(r, p + "mk", (p + "x", p + "y"))));
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }

    /// <summary>
    /// 동영상 효과 "줄 밀림": 짧은 순간(1/8초 칸)마다 정해진 비율로 가로 줄무늬 조각들이 옆으로 엇갈려 밀린다(밀려 빈 쪽은 가장자리 픽셀이 늘어난다).
    /// 밀린 줄에서는 빨강·파랑이 살짝 갈라진다(색 번짐). 줄 하나의 굵기는 매개변수, 이웃 1~3줄이 함께 밀려 굵기가 다양하다(1초에 16번 바뀜).
    /// 그리기: 1×줄 수 지도를 수식으로 그려 틀 크기로 늘이고 displace 한 번(빈 쪽은 가장자리 픽셀을 늘임 — 감아 돌리면 맞춤의 빈 곳이 그림 반대쪽에 끼어든다. 알파도 함께 밀린다).
    /// 사람에게만: 밀린 그림을 사람 자리와, 사람 줄이 밀려 떨어진 자리에만 섞는다(마스크도 같은 지도로 민다).
    /// </summary>
    public static class LineSlip
    {
        public const string Id = "line-slip";
        public const int BurstRate = 8;
        public const int PatternRate = 16;
        private const double ShiftMax = 120;
        private const double FringeMax = 16;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "줄 밀림", Glitch.Group,
            "짧은 순간마다 가로 줄무늬 조각들이 옆으로 엇갈려 밀립니다(밀린 줄에서는 빨강·파랑이 살짝 번짐).", 1,
            new[]
            {
                EffectParam.Number("amount", "세기", 0, 100, 60, "%", "줄이 밀리는 거리와 밀리는 줄 수(100% = 120px)"),
                EffectParam.Number("thick", "줄 굵기", 2, 200, 24, "px", "밀리는 줄 하나의 굵기(이웃 줄이 함께 밀려 더 굵어지기도)"),
                EffectParam.Number("rate", "빈도", 0, 100, 35, "%", "줄이 밀리는 때의 비율(0 = 없음, 100 = 늘)"),
                EffectParam.Number("fringe", "색 번짐", 0, 100, 40, "%", "밀린 줄에서 빨강·파랑이 갈라지는 정도(0 = 없음)")
            },
            Render,
            new Dictionary<string, double> { ["rate"] = 100, ["amount"] = 100, ["thick"] = 48, ["fringe"] = 80 })
        { Since = 2 };

        public static bool IsBurst(ClipEffect effect, long frame, double fps, double margin = 0)
        {
            return Glitch.Hash(Glitch.Slot(frame, fps, BurstRate) * 12.9898 + 0.77) < VideoEffects.Number(effect, Def, "rate") / 100 - margin;
        }

        public static bool IsQuiet(ClipEffect effect, long frame, double fps, double margin = 0)
        {
            return Glitch.Hash(Glitch.Slot(frame, fps, BurstRate) * 12.9898 + 0.77) >= VideoEffects.Number(effect, Def, "rate") / 100 + margin;
        }

        private static string Render(EffectRender r)
        {
            var p = r.Label;
            var amount = r.Number("amount") / 100;
            var rate = r.Number("rate") / 100;
            if (amount <= 0 || rate <= 0)
                return Glitch.Pass(r);
            // 크게 내보낼 때(배율 > 1) 한도(127px)를 넘으면 두 번에 나눠 민다 — 줄마다 가로로만 밀어 한 번에 민 것과 같다
            var limit = Glitch.Limit(r.PixelScale);
            var reach = (amount * ShiftMax + r.Number("fringe") / 100 * FringeMax) * r.PixelScale;
            var twice = limit > Glitch.MaxShift && reach > Glitch.MaxShift;
            var shift = twice ? (int)Math.Min(limit, Math.Round(amount * ShiftMax * r.PixelScale)) : Glitch.Shift(amount * ShiftMax, r.PixelScale);
            var fringe = twice ? (int)Math.Min(limit, Math.Round(r.Number("fringe") / 100 * FringeMax * r.PixelScale)) : Glitch.Shift(r.Number("fringe") / 100 * FringeMax, r.PixelScale);
            var rows = Math.Max(1, (int)Math.Round(r.Height / Math.Max(1, r.Number("thick") * r.PixelScale)));
            string H(string x) => Glitch.HashExpr(x);
            var frame = Glitch.FrameExpr(r, "T");
            var n = Glitch.SlotExpr(r, frame, BurstRate);
            var m = Glitch.SlotExpr(r, frame, PatternRate);
            var gate = "lt(" + H(n + "*12.9898+0.77") + "," + N(rate) + ")";
            var power = "(0.35+0.65*" + H(n + "*4.898+2.21") + ")";
            // 이웃 1~3줄을 한 묶음으로(무늬마다 묶는 크기·어긋남이 다름) — 묶음마다 밀지·얼마나·어느 쪽으로
            var group = "floor((Y+floor(3*" + H(m + "*0.917+0.5") + "))/(1+floor(3*" + H(m + "*1.37+0.9") + ")))";
            var key = "(" + group + "*9.17+" + m + "*1.73)";
            var active = gate + "*lt(" + H(key + "+0.25") + "," + N(0.25 + 0.35 * amount) + ")";
            var dx = N(shift) + "*" + power + "*(2*" + H(key + "*2.113+1.9") + "-1)";
            string X(string extra, int pass) => Glitch.Push(active + "*(" + dx + extra + ")", pass, limit);

            var text = new StringBuilder(Glitch.Begin(r, p + "a"));
            // 빨강은 오른쪽으로, 파랑은 왼쪽으로 조금 더(지도 값이 128 - 거리면 그만큼 왼쪽 것을 가져와 오른쪽으로 밀려 보인다)
            if (!twice)
            {
                text.Append(Glitch.Map(r, 1, rows, X("", 0), X("+" + N(fringe), 0), X("-" + N(fringe), 0), X("", 0), p + "x"));
                text.Append(Glitch.FlatMap(r, p + "y"));
                text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}a][{0}x][{0}y]displace=edge=smear", p);
            }
            else
            {
                text.Append(Glitch.Map(r, 1, rows, X("", 1), X("+" + N(fringe), 1), X("-" + N(fringe), 1), X("", 1), p + "x"));
                text.Append(Glitch.Map(r, 1, rows, X("", 2), X("+" + N(fringe), 2), X("-" + N(fringe), 2), X("", 2), p + "w"));
                text.Append(Glitch.FlatMap(r, p + "y"));
                text.Append(Glitch.FlatMap(r, p + "z"));
                text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}a][{0}x][{0}y]displace=edge=smear[{0}b];[{0}b][{0}w][{0}z]displace=edge=smear", p);
            }
            // 사람에게만: 마스크도 같은 지도로 밀어 사람 줄이 떨어진 자리까지 칠한다
            text.Append(Glitch.Finish(r, twice ? Glitch.Displaced(r, p + "mk", (p + "x", p + "y"), (p + "w", p + "z")) : Glitch.Displaced(r, p + "mk", (p + "x", p + "y"))));
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }

    /// <summary>
    /// 동영상 효과 "나쁜 TV 신호": 전파가 약한 옛 TV처럼
    /// - 노이즈: 지글거리는 흑백 잡음(한 번 그린 잡음 판을 프레임마다 다른 자리에서 잘라 더함 — grainmerge),
    /// - 흔들림: 가로줄마다 좌우로 떨리고(프레임마다), 천천히 휘고, 가끔(빈도) 한 띠가 크게 찢기며, 화면이 위아래로 잘게 튄다,
    /// - 롤링: 가끔(빈도, 1초 칸) 화면이 위로 말려 올라간다(그림 사이의 빈 띠가 지나감 — 투명해서 아래 트랙·검은 바탕이 보인다. 100%면 한 바퀴 돌아 제자리, 덜하면 밀린 채 있다가 칸 끝에서 돌아옴),
    /// - 주사선: 가로줄을 번갈아 어둡게, 색 번짐: 빨강·파랑이 옆으로 번지고 색이 조금 바랜다.
    /// 그리기: [롤링·위아래 튐] 그림·빈 띠·그림을 세로로 쌓아(vstack) 시각 수식의 높이로 자르고 → 줄 떨림 displace(1×높이 지도, 빨강·파랑은 따로) → [색 번짐] 채도 줄이기
    /// → 잡음+주사선 판(geq 한 장, 주사선은 판을 자르는 높이를 주사선 간격의 배수로 해 제자리) grainmerge.
    /// 사람에게만: 효과를 입힌 그림을 사람 자리와, 사람이 말려 올라가거나 밀려 옮겨 간 자리에만 섞는다(마스크도 같이 말고 민다).
    /// </summary>
    public static class BadSignal
    {
        public const string Id = "bad-tv";
        /// <summary>롤링 칸(초당), 찢김 칸(초당).</summary>
        public const int RollRate = 1;
        public const int TearRate = 4;
        /// <summary>롤링 칸에서 말려 올라가는 데 쓰는 몫(나머지는 그대로 있음).</summary>
        public const double RollTime = 0.6;
        // 잡음 판의 여유(자르는 자리가 움직일 폭, 출력 px)
        private const int NoiseSpare = 128;

        public static readonly VideoEffectDef Def = new VideoEffectDef(Id, "나쁜 TV 신호", Glitch.Group,
            "전파가 약한 옛 TV처럼 지글거리는 잡음, 좌우로 떨리는 줄, 가끔 위로 말려 올라가는 화면, 주사선과 바랜 색 번짐이 섞입니다.", 1,
            new[]
            {
                EffectParam.Number("noise", "노이즈", 0, 100, 35, "%", "지글거리는 흑백 잡음의 세기"),
                EffectParam.Number("jitter", "흔들림", 0, 100, 35, "%", "가로줄이 좌우로 떨리고 휘는 정도(찢김·위아래 튐 포함)"),
                EffectParam.Number("roll", "롤링", 0, 100, 60, "%", "화면이 위로 말려 올라가는 거리(100% = 한 바퀴, 0 = 말리지 않음)"),
                EffectParam.Number("rate", "빈도", 0, 100, 25, "%", "롤링·찢김이 일어나는 때의 비율"),
                EffectParam.Number("scan", "주사선", 0, 100, 40, "%", "가로줄을 번갈아 어둡게 하는 정도"),
                EffectParam.Number("bleed", "색 번짐", 0, 100, 40, "%", "빨강·파랑이 옆으로 번지고 색이 바래는 정도")
            },
            Render,
            // 썸네일 띠(1.5초)에 롤링이 한 번 보이게 늘, 잡음·주사선도 작은 그림에서 보이게 세게
            new Dictionary<string, double> { ["rate"] = 100, ["roll"] = 100, ["noise"] = 60, ["jitter"] = 60, ["scan"] = 100 })
        { Since = 2 };

        /// <summary>프레임 번호 frame(클립 안, 초당 fps장)이 롤링 칸에 드는지와 그 칸에서 말려 올라간 몫(0~1, 롤링 100%면 1 = 한 바퀴).</summary>
        public static (bool Rolling, double Progress) Roll(ClipEffect effect, long frame, double fps)
        {
            var u = frame * double.Parse(N(RollRate / fps), CultureInfo.InvariantCulture);
            var slot = Math.Floor(u + 0.001);
            var rolling = Glitch.Hash(slot * 12.9898 + 5.5) < VideoEffects.Number(effect, Def, "rate") / 100;
            var phase = Math.Min(1, Math.Max(0, u - slot) / RollTime);
            return (rolling, phase * phase * (3 - 2 * phase));
        }

        /// <summary>롤링 때 그림 사이에 지나가는 빈 띠(세로 동기 사이)의 높이(출력 px).</summary>
        public static int BarHeight(int height)
        {
            return Math.Max(2, (int)Math.Round(height * 0.06));
        }

        /// <summary>주사선 간격(출력 px — 어두운 줄과 밝은 줄이 반씩).</summary>
        public static int ScanPeriod(double pixelScale)
        {
            return Math.Max(2, (int)Math.Round(4 * pixelScale));
        }

        private static string Render(EffectRender r)
        {
            var p = r.Label;
            int w = r.Width, h = r.Height;
            var noise = r.Number("noise") / 100;
            var jitter = r.Number("jitter") / 100;
            var roll = r.Number("roll") / 100;
            var rate = r.Number("rate") / 100;
            var scan = r.Number("scan") / 100;
            var bleed = r.Number("bleed") / 100;
            var rolls = roll > 0 && rate > 0;
            if (!rolls && noise <= 0 && jitter <= 0 && scan <= 0 && bleed <= 0)
                return Glitch.Pass(r);
            string H(string x) => Glitch.HashExpr(x);
            var text = new StringBuilder(Glitch.Begin(r, p + "a"));
            var source = p + "a";
            // 사람에게만: 마스크도 그림과 같이 말아 올리고 밀어 사람이 옮겨 간 자리까지 칠한다(잡음·주사선·색 바램은 자리를 옮기지 않음)
            var maskSource = p + "mk";
            var maskRoll = "";
            var bounce = (int)Math.Round(jitter * 3 * r.PixelScale);
            if (rolls || bounce > 0)
            {
                // 롤링: 그림 / 빈 띠(투명) / 그림을 쌓아 두고 자르는 높이를 올린다 — 한 바퀴(그림 + 띠 높이)면 처음과 같은 그림.
                // 위아래 튐(흔들림)도 같은 자르는 높이에 더한다(프레임마다 0~튐 px 위로 — 아래 끝에 빈 띠가 살짝 보인다)
                var bar = BarHeight(h);
                var frame = Glitch.FrameExpr(r, "t");
                var y = "0";
                if (rolls)
                {
                    var u = "(" + frame + "*" + N(RollRate / r.Fps) + ")";
                    var slot = "floor(" + u + "+0.001)";
                    var phase = "min(1,clip(" + u + "-" + slot + ",0,1)/" + N(RollTime) + ")";
                    y = string.Format(CultureInfo.InvariantCulture, "lt({0},{1})*{2}*{3}*{3}*(3-2*{3})", H(slot + "*12.9898+5.5"), N(rate), N((h + bar) * roll), phase);
                }
                if (bounce > 0)
                    y = "mod(" + y + "+floor(" + (bounce + 1) + "*" + H(frame + "*3.111+0.7") + ")," + (h + bar) + ")";
                // [input]을 q 라벨로 말아 올린 [q v]
                string Roll(string input, string q) => string.Format(CultureInfo.InvariantCulture,
                    ";[{0}]split[{1}t][{1}u];color=c=black@0:s={2}x{3}:r={4}:d={5},format=gbrap[{1}k];[{1}t][{1}k][{1}u]vstack=3,crop={2}:{6}:0:'{7}'[{1}v]",
                    input, q, w, bar, r.Rate, N(r.Seconds), h, y);
                text.Append(Roll(p + "a", p));
                source = p + "v";
                maskRoll = Roll(maskSource, p + "mr").Substring(1) + ";";
                maskSource = p + "mrv";
            }

            // 줄 떨림 지도(1×높이): 천천히 휘기 + 줄마다 프레임마다 떨림 + 찢김(빈도, 1/4초 칸에 한 띠) — 시각은 프레임 번호로(같은 프레임이면 같은 값)
            var f = Glitch.FrameExpr(r, "T");
            var time = "(" + f + "*" + N(1 / r.Fps) + ")";
            var wobble = N(jitter * 12 * r.PixelScale) + "*sin(Y*" + N(7.3 / h) + "+" + time + "*3.1)*sin(" + time + "*1.3+0.7)";
            var fine = N(jitter * 3 * r.PixelScale) + "*(2*" + H("Y*0.731+" + f + "*1.917") + "-1)";
            var tears = Glitch.SlotExpr(r, f, TearRate);
            var tear = string.Format(CultureInfo.InvariantCulture, "lt({0},{1})*{2}*(2*lt({3},0.5)-1)*exp(-pow((Y-{4}*{5})/{6},2))",
                H(tears + "*7.31+2.2"), N(rate), N(jitter * 80 * r.PixelScale), H(tears + "*5.13+0.3"), H(tears + "*3.77+9.1"), h, N(Math.Max(1, 0.08 * h)));
            var dx = "(" + wobble + "+" + fine + "+" + tear + ")";
            // 색 번짐: 빨강은 오른쪽으로, 파랑은 왼쪽으로 조금 번진다
            var red = N(bleed * 8 * r.PixelScale);
            var blue = N(bleed * 4 * r.PixelScale);
            // 크게 내보낼 때 한도(127px)를 넘으면 두 번에 나눠 민다(줄마다 가로로만 밀어 한 번에 민 것과 같다)
            var limit = Glitch.Limit(r.PixelScale);
            var twice = limit > Glitch.MaxShift && (jitter * (12 + 3 + 80) + bleed * 8) * r.PixelScale > Glitch.MaxShift;
            string X(string extra, int pass) => Glitch.Push(dx + extra, pass, limit);
            if (!twice)
            {
                text.Append(Glitch.Map(r, 1, h, X("", 0), X("+" + blue, 0), X("-" + red, 0), X("", 0), p + "x"));
                text.Append(Glitch.FlatMap(r, p + "y"));
                text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}][{1}x][{1}y]displace=edge=smear", source, p);
            }
            else
            {
                text.Append(Glitch.Map(r, 1, h, X("", 1), X("+" + blue, 1), X("-" + red, 1), X("", 1), p + "x"));
                text.Append(Glitch.Map(r, 1, h, X("", 2), X("+" + blue, 2), X("-" + red, 2), X("", 2), p + "w"));
                text.Append(Glitch.FlatMap(r, p + "y"));
                text.Append(Glitch.FlatMap(r, p + "z"));
                text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}][{1}x][{1}y]displace=edge=smear[{1}b];[{1}b][{1}w][{1}z]displace=edge=smear", source, p);
            }
            if (bleed > 0)
            {
                // 색이 바랜다: 밝기 쪽으로 섞는다(최대 40%)
                var s = 0.4 * bleed;
                double lr = 0.299 * s, lg = 0.587 * s, lb = 0.114 * s;
                text.AppendFormat(CultureInfo.InvariantCulture, ",colorchannelmixer=rr={0}:rg={1}:rb={2}:gr={3}:gg={4}:gb={2}:br={3}:bg={1}:bb={5}",
                    N(1 - s + lr), N(lg), N(lb), N(lr), N(1 - s + lg), N(1 - s + lb));
            }

            if (noise > 0 || scan > 0)
            {
                // 잡음 + 주사선 판: 여유만큼 큰 판을 한 번만 그려(수식 geq 한 장) 구간 끝까지 늘이고, 프레임마다 다른 자리를 잘라 grainmerge로 더한다(128 = 그대로).
                // 주사선은 판에 새겨 두고 자르는 높이를 주사선 간격의 배수로만 바꿔 화면에서는 늘 같은 줄이 어둡다
                var period = ScanPeriod(r.PixelScale);
                var spare = NoiseSpare - NoiseSpare % period;
                var once = N(0.5 / r.Fps);
                var grain = noise > 0 ? "+" + N(240 * noise) + "*(" + H("X*12.9898+Y*78.233") + "-0.5)" : "";
                // 판은 절반 크기(잡음 알갱이 2×2)로 그려 두 배로 키운다 — 1080p에서 geq가 약 0.23초 → 약 0.06초(멈춘 화면마다 한 번)
                // 주사선은 키운 뒤의 줄 번호(2·Y)로 정해 간격이 짝수면 그대로다
                var dark = scan > 0 ? "-if(lt(mod(2*Y," + period + ")," + period / 2 + "),0," + N(60 * scan) + ")" : "";
                var nf = Glitch.FrameExpr(r, "t");
                // 판(한 장 — 캐시가 있으면 파일로 읽음)
                text.Append("[" + p + "p];" + r.Still(string.Format(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3},format=gray,geq=lum='clip(128{4}{5},0,255)'",
                    (w + spare + 1) / 2, (h + spare + 1) / 2, r.Rate, once, grain, dark), "gray"));
                text.AppendFormat(CultureInfo.InvariantCulture, ",scale=iw*2:ih*2:flags=neighbor,format=gbrap,tpad=stop_mode=clone:stop_duration={1},crop={2}:{3}:'floor({4}*{5})':'{6}*floor({7}*{8})'[{0}n]",
                    p, N(r.Seconds), w, h, spare, H(nf + "*12.9898+0.1"), period, spare / period, H(nf + "*78.233+0.2"));
                text.AppendFormat(CultureInfo.InvariantCulture, ";[{0}p][{0}n]blend=c0_mode=grainmerge:c1_mode=grainmerge:c2_mode=grainmerge:c3_mode=normal", p);
            }
            text.Append(Glitch.Finish(r, maskRoll + (twice ? Glitch.Displaced(r, maskSource, (p + "x", p + "y"), (p + "w", p + "z")) : Glitch.Displaced(r, maskSource, (p + "x", p + "y")))));
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
