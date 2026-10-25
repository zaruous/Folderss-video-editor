using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 동영상 효과 묶음 "흔들림·줌": 카메라 흔들림·지진·하트비트·빠른 줌·줌 바운스 — 클립 그림을 틀 안에서 옮기고 돌리고 키운다.
    /// 그리기: FFmpeg perspective 하나(프레임마다 네 모서리를 수식으로 정해 확대·돌림·옮김을 한 번에 — 1/256픽셀 단위 선형 보간이라 느린 줌도 정수 계단 없이 매끄럽다).
    /// 크기 바꾸기(scale)를 잇지 않고 출력은 늘 받은 크기다. 그림 밖을 읽으면 perspective는 가장자리 픽셀을 늘이므로, 흔들림은 흔드는 만큼 미리 키워 그림 밖을 읽지 않고
    /// 줌은 늘 1배 이상이다(빈 가장자리가 보이지 않음).
    /// perspective 수식에는 시각(t)이 없어 프레임 번호(in, 1부터)로 잰다: 클립 안 시각 = (in − 1) ÷ fps + 앞선 초.
    /// 효과가 받는 흐름은 fps로 고르게 맞춘 뒤라 n번째 프레임이 n ÷ fps초다 — 그래서 한 장 그리기와 이어 그리기가 같다.
    /// [화면에 맞춤]이면 틀 가운데의 그림 자리만 잘라 움직이고 다시 투명하게 채운다(<see cref="EffectRender.PictureWidth"/>) — 빈 곳은 그대로 투명하고 그림은 제자리에서 흔들리거나 커진다.
    /// 사람에게만은 없다(그림 전체를 움직이는 효과라 사람 자리만 따로 움직일 수 없다 — <see cref="VideoEffectDef.AllowsPersonOnly"/>).
    /// </summary>
    public static class ShakeZoom
    {
        public const string Group = "흔들림·줌";
        public const string CameraShakeId = "camera-shake";
        public const string EarthquakeId = "earthquake";
        public const string HeartbeatId = "heartbeat";
        public const string PunchZoomId = "punch-zoom";
        public const string ZoomBounceId = "zoom-bounce";

        // 흔드는 거리는 그림 크기의 이만큼까지(더 흔들면 너무 키워야 한다)
        private const double MaxShare = 0.2;

        // 카메라 흔들림: 서로 맞지 않는 빠르기(Hz)·위상·무게의 사인을 더한 곡선(가로·세로·돌림) — 손에 든 카메라처럼 부드럽게 출렁인다
        private static readonly Wave[] HandX = { new Wave(0.67, 0.4, 1), new Wave(1.61, 2.3, 0.45), new Wave(3.37, 4.9, 0.15) };
        private static readonly Wave[] HandY = { new Wave(0.83, 1.7, 1), new Wave(1.97, 5.1, 0.4), new Wave(3.71, 0.9, 0.15) };
        private static readonly Wave[] HandTurn = { new Wave(0.47, 3.1, 1), new Wave(1.29, 0.6, 0.4) };

        // 지진: 1초에 9번(빠르기 1) 새 자리로 홱 옮긴다 — 마디의 앞 45% 동안 옮기고 나머지는 머문다. 여기에 빠른 출렁임을 25% 섞는다
        private const double JoltRate = 9;
        private const double JoltMove = 0.45;
        private const double JoltShare = 0.75;
        private static readonly double[] JoltSeed = { 0, 31.7, 57.3 };
        private static readonly Wave[] QuakeX = { new Wave(2.3, 1.3, 1) };
        private static readonly Wave[] QuakeY = { new Wave(2.9, 0.2, 1) };
        // 지진의 세로 흔들림(가로의 몫), 세기 1px마다 도는 각도(도, 2.5도까지), 흐림이 흐림 값 그대로 드는 흔들림 거리(출력 픽셀 — 덜 흔들면 그만큼 약하게)
        private const double QuakeVertical = 0.8;
        private const double QuakeTurnPerPixel = 0.025;
        private const double QuakeTurnMax = 2.5;
        private const double QuakeBlurReach = 20;

        // 하트비트: 한 번 뛸 때 이만큼(초) 빨리 커졌다가 이만큼을 시간 상수로 가라앉는다(박자가 빠르면 한 박의 1/8까지). 두 번째 박동은 이만큼 뒤(한 박의 1/3까지)
        private const double BeatAttack = 0.07;
        private const double BeatDecay = 0.12;
        private const double BeatGap = 0.22;

        public static readonly VideoEffectDef CameraShake = new VideoEffectDef(CameraShakeId, "카메라 흔들림", Group,
            "손에 든 카메라처럼 화면이 부드럽게 출렁입니다(가로·세로·조금 돌기). 흔드는 만큼 살짝 키워 가장자리가 비지 않습니다.", 1,
            new[]
            {
                EffectParam.Number("horizontal", "가로 세기", 0, 150, 18, "px", "가로로 흔들리는 가장 먼 거리"),
                EffectParam.Number("vertical", "세로 세기", 0, 150, 12, "px", "세로로 흔들리는 가장 먼 거리"),
                EffectParam.Number("rotate", "회전", 0, 8, 0.8, "°", "좌우로 기우는 가장 큰 각도(0 = 돌지 않음)"),
                EffectParam.Number("speed", "빠르기", 0.2, 4, 1, "배", "출렁이는 빠르기(1 = 1초에 한 번 남짓)")
            },
            CameraShakeRender,
            // 썸네일(작은 그림, 1.5초)에서도 출렁임이 보이게 크고 빠르게
            new Dictionary<string, double> { ["horizontal"] = 60, ["vertical"] = 45, ["rotate"] = 3, ["speed"] = 2 })
        { AllowsPersonOnly = false, Since = 2 };

        public static readonly VideoEffectDef Earthquake = new VideoEffectDef(EarthquakeId, "지진", Group,
            "땅이 흔들리듯 화면이 세고 빠르게 홱홱 흔들립니다(조금 흐려져 움직임이 번짐). 흔드는 만큼 키워 가장자리가 비지 않습니다.", 1,
            new[]
            {
                EffectParam.Number("amount", "세기", 0, 200, 30, "px", "흔들리는 가장 먼 거리(세로는 가로의 80%, 세기만큼 조금 돌기도 함)"),
                EffectParam.Number("speed", "빠르기", 0.3, 3, 1, "배", "홱 옮기는 빠르기(1 = 1초에 9번)"),
                EffectParam.Number("blur", "흐림", 0, 100, 40, "%", "움직임이 번진 듯 흐리게(0 = 흐리지 않음 — 조금 빠름)")
            },
            EarthquakeRender,
            new Dictionary<string, double> { ["amount"] = 70, ["blur"] = 30 })
        { AllowsPersonOnly = false, Since = 2 };

        public static readonly VideoEffectDef Heartbeat = new VideoEffectDef(HeartbeatId, "하트비트", Group,
            "심장이 뛰듯 박자에 맞춰 화면이 쿵 커졌다가 가라앉습니다(두 번째 작은 박동까지).", 1,
            new[]
            {
                EffectParam.Number("amount", "세기", 1, 50, 12, "%", "한 번 뛸 때 커지는 정도"),
                EffectParam.Number("bpm", "박자", 30, 200, 80, "bpm", "1분에 뛰는 횟수"),
                EffectParam.Number("second", "두 번째 박동", 0, 100, 60, "%", "곧이어 뛰는 작은 박동의 세기(0 = 한 번만)"),
                EffectParam.Number("centerx", "중심 가로", 0, 100, 50, "%", "커지는 중심(그림 왼쪽 0 ~ 오른쪽 100)"),
                EffectParam.Number("centery", "중심 세로", 0, 100, 50, "%", "커지는 중심(그림 위 0 ~ 아래 100)")
            },
            HeartbeatRender,
            // 썸네일(1.5초)에 세 번 뛰게
            new Dictionary<string, double> { ["amount"] = 40, ["bpm"] = 120 })
        { AllowsPersonOnly = false, Since = 2 };

        public static readonly VideoEffectDef PunchZoom = new VideoEffectDef(PunchZoomId, "빠른 줌", Group,
            "정한 때에 화면이 확 커져 들어가 그대로 머뭅니다(배율이 −면 커진 데서 원래 크기로 확 빠져나옴).", 1,
            new[]
            {
                EffectParam.Number("zoom", "배율", -200, 200, 40, "%", "더 키우는 정도(+ = 확대해 들어가 머묾, − = 그만큼 확대된 데서 원래 크기로 빠져나옴)"),
                EffectParam.Number("duration", "길이", 0.05, 2, 0.3, "초", "커지는(빠져나오는) 데 걸리는 시간"),
                EffectParam.Number("start", "시작", 0, 60, 0, "초", "클립 처음부터 몇 초 뒤에 줌하는지"),
                EffectParam.Number("centerx", "중심 가로", 0, 100, 50, "%", "줌의 중심(그림 왼쪽 0 ~ 오른쪽 100)"),
                EffectParam.Number("centery", "중심 세로", 0, 100, 50, "%", "줌의 중심(그림 위 0 ~ 아래 100)")
            },
            PunchZoomRender,
            // 썸네일: 잠깐 그대로였다가 확 커져 머문다
            new Dictionary<string, double> { ["zoom"] = 80, ["start"] = 0.3, ["duration"] = 0.25 })
        { AllowsPersonOnly = false, Since = 2 };

        public static readonly VideoEffectDef ZoomBounce = new VideoEffectDef(ZoomBounceId, "줌 바운스", Group,
            "화면이 탄력 있게 튀며 커졌다가 다시 튀며 돌아오기를 되풀이합니다(반복 횟수를 정하면 마지막에는 커진 채 머묾).", 1,
            new[]
            {
                EffectParam.Number("zoom", "배율", 5, 100, 25, "%", "커지는 정도"),
                EffectParam.Number("period", "주기", 0.4, 4, 1.6, "초", "튀어 커졌다가 돌아오기까지 걸리는 시간(앞 절반 커지고 뒤 절반 돌아옴)"),
                EffectParam.Number("elastic", "탄성", 0, 100, 60, "%", "넘쳤다 되돌아오는 출렁임(0 = 출렁이지 않고 부드럽게)"),
                EffectParam.Number("repeat", "반복", 0, 10, 0, "회", "튀는 횟수(0 = 끝없이, 1 = 한 번 튀어 커진 채 머묾, 2 이상이면 그만큼 되풀이한 뒤 커진 채 머묾)"),
                EffectParam.Number("centerx", "중심 가로", 0, 100, 50, "%", "커지는 중심(그림 왼쪽 0 ~ 오른쪽 100)"),
                EffectParam.Number("centery", "중심 세로", 0, 100, 50, "%", "커지는 중심(그림 위 0 ~ 아래 100)")
            },
            ZoomBounceRender,
            new Dictionary<string, double> { ["zoom"] = 40, ["period"] = 1.5 })
        { AllowsPersonOnly = false, Since = 2 };

        /// <summary>사인 하나: 빠르기(Hz), 위상(라디안), 무게.</summary>
        private readonly struct Wave
        {
            public Wave(double frequency, double phase, double weight)
            {
                Frequency = frequency;
                Phase = phase;
                Weight = weight;
            }

            public double Frequency { get; }
            public double Phase { get; }
            public double Weight { get; }
        }

        /// <summary>
        /// 한 프레임의 움직임: 확대 배율(1 이상), 돌림(라디안), 원본에서 읽는 자리를 옮긴 거리(그림 픽셀 — 그림은 반대쪽으로 움직여 보인다).
        /// 출력의 점 p는 원본의 c + R(돌림)·(p − c) ÷ 배율 + 옮김을 읽는다(c = 중심).
        /// </summary>
        public readonly struct Pose
        {
            public Pose(double zoom, double angle, double x, double y)
            {
                Zoom = zoom;
                Angle = angle;
                X = x;
                Y = y;
            }

            public double Zoom { get; }
            public double Angle { get; }
            public double X { get; }
            public double Y { get; }
        }

        // ---------- 움직임 계산(그리는 수식과 같은 식 — 시험이 그린 그림과 맞춰 본다) ----------

        /// <summary>
        /// 흔들어도 그림 밖을 읽지 않는 확대 배율: width×height 그림에서 옮김이 가로 dx·세로 dy(픽셀), 돌림이 angle(라디안)까지일 때.
        /// 읽는 자리(그림 ÷ 배율을 돌리고 옮긴 사각)의 바깥 상자가 그림 안에 들어야 한다 — 가로: (W·cos + H·sin) ÷ 배율 ÷ 2 + dx ≤ W ÷ 2(cos ≤ 1로 넉넉히).
        /// </summary>
        public static double CoverZoom(double width, double height, double dx, double dy, double angle)
        {
            var sin = Math.Sin(Math.Abs(angle));
            var across = (width + height * sin) / Math.Max(1, width - 2 * Math.Abs(dx));
            var down = (height + width * sin) / Math.Max(1, height - 2 * Math.Abs(dy));
            return Math.Max(1, Math.Max(across, down));
        }

        /// <summary>카메라 흔들림이 흔드는 가장 먼 거리(그림 픽셀 — 그림의 20%까지)와 가장 큰 돌림(라디안).</summary>
        public static (double X, double Y, double Angle) CameraShakeReach(ClipEffect effect, int width, int height, double pixelScale)
        {
            var def = CameraShake;
            return (Math.Min(MaxShare * width, VideoEffects.Number(effect, def, "horizontal") * pixelScale),
                Math.Min(MaxShare * height, VideoEffects.Number(effect, def, "vertical") * pixelScale),
                VideoEffects.Number(effect, def, "rotate") * Math.PI / 180);
        }

        /// <summary>카메라 흔들림의 time초(클립 안 시각) 움직임 — 확대는 흔드는 거리에 맞춘 고정 배율.</summary>
        public static Pose CameraShakePose(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            var (x, y, angle) = CameraShakeReach(effect, width, height, pixelScale);
            var speed = VideoEffects.Number(effect, CameraShake, "speed");
            return new Pose(CoverZoom(width, height, x, y, angle), angle * Waves(HandTurn, time, speed), x * Waves(HandX, time, speed), y * Waves(HandY, time, speed));
        }

        /// <summary>지진이 흔드는 가장 먼 거리(그림 픽셀 — 그림의 20%까지)와 가장 큰 돌림(라디안).</summary>
        public static (double X, double Y, double Angle) EarthquakeReach(ClipEffect effect, int width, int height, double pixelScale)
        {
            var amount = VideoEffects.Number(effect, Earthquake, "amount");
            return (Math.Min(MaxShare * width, amount * pixelScale), Math.Min(MaxShare * height, amount * QuakeVertical * pixelScale),
                Math.Min(QuakeTurnMax, amount * QuakeTurnPerPixel) * Math.PI / 180);
        }

        /// <summary>지진의 time초(클립 안 시각) 움직임.</summary>
        public static Pose EarthquakePose(ClipEffect effect, double time, int width, int height, double pixelScale)
        {
            var (x, y, angle) = EarthquakeReach(effect, width, height, pixelScale);
            var rate = JoltRate * VideoEffects.Number(effect, Earthquake, "speed");
            var speed = VideoEffects.Number(effect, Earthquake, "speed");
            return new Pose(CoverZoom(width, height, x, y, angle),
                angle * Jolt(time, rate, JoltSeed[2]),
                x * (JoltShare * Jolt(time, rate, JoltSeed[0]) + (1 - JoltShare) * Waves(QuakeX, time, speed)),
                y * (JoltShare * Jolt(time, rate, JoltSeed[1]) + (1 - JoltShare) * Waves(QuakeY, time, speed)));
        }

        /// <summary>
        /// 지진의 흐림: 3×3 흐림의 가로·세로 이웃 무게(0~8 — 가운데 8에 견줘, 8이면 세 칸을 고르게 섞음). 0, 0이면 흐리지 않는다.
        /// 흐림 값에 흔드는 거리를 곱한다(출력 픽셀 20px부터 흐림 값 그대로) — 줄인 재생 크기·작은 흔들림에서는 그만큼 약하다.
        /// </summary>
        public static (int X, int Y) EarthquakeBlur(ClipEffect effect, int width, int height, double pixelScale)
        {
            var blur = VideoEffects.Number(effect, Earthquake, "blur") / 100;
            var (x, y, _) = EarthquakeReach(effect, width, height, pixelScale);
            return ((int)Math.Round(8 * blur * Math.Min(1, x / QuakeBlurReach)), (int)Math.Round(8 * blur * Math.Min(1, y / QuakeBlurReach)));
        }

        /// <summary>하트비트의 time초 확대 배율: 1 + 세기 × (박동 + 두 번째 박동).</summary>
        public static double HeartbeatZoom(ClipEffect effect, double time)
        {
            var (period, attack, decay, gap) = BeatTiming(VideoEffects.Number(effect, Heartbeat, "bpm"));
            var s = time - period * Math.Floor(time / period);
            double Beat(double x) => x < 0 ? 0 : x < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * x / attack) : Math.Exp(-(x - attack) / decay);
            return 1 + VideoEffects.Number(effect, Heartbeat, "amount") / 100 * (Beat(s) + VideoEffects.Number(effect, Heartbeat, "second") / 100 * Beat(s - gap));
        }

        /// <summary>박자 bpm의 한 박(초)과 커지는 시간·가라앉는 시간 상수·두 번째 박동까지의 시간 — 빠른 박자에서도 다음 박 전에 가라앉게 한 박에 맞춰 줄인다.</summary>
        private static (double Period, double Attack, double Decay, double Gap) BeatTiming(double bpm)
        {
            var period = 60 / bpm;
            return (period, Math.Min(BeatAttack, period / 8), Math.Min(BeatDecay, period / 8), Math.Min(BeatGap, period / 3));
        }

        /// <summary>빠른 줌의 time초 확대 배율: 시작부터 길이 동안 빨리 시작해 천천히 멈추게(4차 곡선) 1 → 1 + 배율, 배율이 −면 1 + |배율| → 1.</summary>
        public static double PunchZoomAt(ClipEffect effect, double time)
        {
            var amount = VideoEffects.Number(effect, PunchZoom, "zoom") / 100;
            var u = Math.Max(0, Math.Min(1, (time - VideoEffects.Number(effect, PunchZoom, "start")) / VideoEffects.Number(effect, PunchZoom, "duration")));
            var rest = Math.Pow(1 - u, 4);
            return 1 + Math.Abs(amount) * (amount >= 0 ? 1 - rest : rest);
        }

        /// <summary>빠른 줌의 중심(그림 픽셀).</summary>
        public static (double X, double Y) PunchZoomCenter(ClipEffect effect, int width, int height)
        {
            return ZoomCenter(effect, PunchZoom, width, height);
        }

        /// <summary>줌 효과(빠른 줌·하트비트·줌 바운스)의 중심(그림 픽셀) — 중심 값이 없는 예전 프로젝트는 기본값(가운데).</summary>
        public static (double X, double Y) ZoomCenter(ClipEffect effect, VideoEffectDef def, int width, int height)
        {
            return (VideoEffects.Number(effect, def, "centerx") / 100 * width, VideoEffects.Number(effect, def, "centery") / 100 * height);
        }

        /// <summary>
        /// 탄성 곡선(0 → 1): 1 − e^(−감쇠·u)·cos(출렁임·u)·(1 − u). 탄성이 클수록 덜 감쇠하고 더 많이 출렁인다(1 넘게 넘쳤다 돌아옴). u = 1에서 꼭 1이다.
        /// 0 아래로는 내려가지 않는다(곱한 값의 크기가 1 이하).
        /// </summary>
        public static double Elastic(double u, double elastic)
        {
            var (damping, swing) = ElasticShape(elastic);
            return 1 - Math.Exp(-damping * u) * Math.Cos(swing * u) * (1 - u);
        }

        private static (double Damping, double Swing) ElasticShape(double elastic)
        {
            return (3 + 3 * (1 - elastic), Math.PI * (1 + 3 * elastic));
        }

        /// <summary>
        /// 줌 바운스의 time초 확대 배율. 주기의 앞 절반은 1 → 1 + 배율(탄성 곡선 — 넘치면 더 커짐), 뒤 절반은 돌아오며 1배 바닥에서 튄다(|1 − 곡선| — 1배 아래로 가지 않음).
        /// 반복이 N(1 이상)이면 N번째 주기의 앞 절반에서 커진 뒤 그대로 머문다.
        /// </summary>
        public static double ZoomBounceAt(ClipEffect effect, double time)
        {
            var amount = VideoEffects.Number(effect, ZoomBounce, "zoom") / 100;
            var period = VideoEffects.Number(effect, ZoomBounce, "period");
            var elastic = VideoEffects.Number(effect, ZoomBounce, "elastic") / 100;
            var repeat = Math.Floor(VideoEffects.Number(effect, ZoomBounce, "repeat"));
            var half = period / 2;
            if (repeat >= 1 && time >= (repeat - 1) * period)
                return 1 + amount * Elastic(Math.Min(1, (time - (repeat - 1) * period) / half), elastic);
            var phase = time - period * Math.Floor(time / period);
            return phase < half ? 1 + amount * Elastic(phase / half, elastic) : 1 + amount * Math.Abs(1 - Elastic((phase - half) / half, elastic));
        }

        /// <summary>사인들의 합(−1~1): Σ 무게·sin(2π·빠르기·배·시각 + 위상) ÷ Σ 무게.</summary>
        private static double Waves(Wave[] waves, double time, double speed)
        {
            double sum = 0, total = 0;
            foreach (var wave in waves)
            {
                sum += wave.Weight * Math.Sin(2 * Math.PI * wave.Frequency * speed * time + wave.Phase);
                total += wave.Weight;
            }
            return sum / total;
        }

        /// <summary>마디 k의 정해진 값(−1~1) — 같은 마디면 늘 같은 값(random 대신).</summary>
        private static double Hash(double k, double seed)
        {
            var x = Math.Sin(k * 12.9898 + seed) * 43758.5453;
            return 2 * (x - Math.Floor(x)) - 1;
        }

        /// <summary>홱홱 옮기기(−1~1): 1초에 rate번 새 자리(Hash)로 — 마디의 앞 45% 동안 (1 − cos)/2로 옮기고 나머지는 머문다.</summary>
        private static double Jolt(double time, double rate, double seed)
        {
            var x = time * rate;
            var k = Math.Floor(x);
            var move = Math.Min(1, (x - k) / JoltMove);
            var from = Hash(k, seed);
            return from + (Hash(k + 1, seed) - from) * (0.5 - 0.5 * Math.Cos(Math.PI * move));
        }

        // ---------- 필터 조각 ----------

        private static string CameraShakeRender(EffectRender r)
        {
            var picture = Picture(r);
            var (x, y, angle) = CameraShakeReach(r.Effect, picture.Width, picture.Height, r.PixelScale);
            var speed = r.Number("speed");
            var time = Clock(r);
            return Warp(r, picture, N(CoverZoom(picture.Width, picture.Height, x, y, angle)),
                angle > 0 ? N(angle) + "*" + WavesText(HandTurn, time, speed) : null,
                N(x) + "*" + WavesText(HandX, time, speed), N(y) + "*" + WavesText(HandY, time, speed),
                picture.Width / 2.0, picture.Height / 2.0);
        }

        private static string EarthquakeRender(EffectRender r)
        {
            var picture = Picture(r);
            var (x, y, angle) = EarthquakeReach(r.Effect, picture.Width, picture.Height, r.PixelScale);
            var speed = r.Number("speed");
            var rate = JoltRate * speed;
            var time = Clock(r);
            string Mix(int axis, Wave[] waves) => "(" + N(JoltShare) + "*" + JoltText(time, rate, JoltSeed[axis]) + "+" + N(1 - JoltShare) + "*" + WavesText(waves, time, speed) + ")";
            // 흐림: 가로 [a 8 a] × 세로 [b 8 b]를 곱한 3×3 행렬로 R·G·B만(알파는 그대로 — 행렬을 주지 않은 면은 FFmpeg가 그대로 둔다). 나누기는 FFmpeg가 행렬의 합으로
            var (a, b) = EarthquakeBlur(r.Effect, picture.Width, picture.Height, r.PixelScale);
            var blur = "";
            if (a > 0 || b > 0)
            {
                var matrix = string.Join(" ", new double[] { a * b, 8 * b, a * b, 8 * a, 64, 8 * a, a * b, 8 * b, a * b }.Select(N));
                blur = string.Format(CultureInfo.InvariantCulture, ",convolution=0m='{0}':1m='{0}':2m='{0}'", matrix);
            }
            return Warp(r, picture, N(CoverZoom(picture.Width, picture.Height, x, y, angle)),
                angle > 0 ? N(angle) + "*" + JoltText(time, rate, JoltSeed[2]) : null,
                N(x) + "*" + Mix(0, QuakeX), N(y) + "*" + Mix(1, QuakeY),
                picture.Width / 2.0, picture.Height / 2.0, blur);
        }

        private static string HeartbeatRender(EffectRender r)
        {
            var picture = Picture(r);
            var (period, attack, decay, gap) = BeatTiming(r.Number("bpm"));
            var s = "mod(" + Clock(r) + "," + N(period) + ")";
            string Beat(string x) => string.Format(CultureInfo.InvariantCulture, "if(lt({0},0),0,if(lt({0},{1}),0.5-0.5*cos({2}*{0}),exp(({1}-{0})/{3})))",
                x, N(attack), N(Math.PI / attack), N(decay));
            var zoom = "1+" + N(r.Number("amount") / 100) + "*(" + Beat(s) + "+" + N(r.Number("second") / 100) + "*" + Beat("(" + s + "-" + N(gap) + ")") + ")";
            var (cx, cy) = ZoomCenter(r.Effect, Heartbeat, picture.Width, picture.Height);
            return Warp(r, picture, zoom, null, "0", "0", cx, cy);
        }

        private static string PunchZoomRender(EffectRender r)
        {
            var picture = Picture(r);
            var amount = r.Number("zoom") / 100;
            var rest = "pow(1-clip((" + Clock(r) + "-" + N(r.Number("start")) + ")/" + N(r.Number("duration")) + ",0,1),4)";
            var zoom = "1+" + N(Math.Abs(amount)) + "*" + (amount >= 0 ? "(1-" + rest + ")" : rest);
            var (cx, cy) = PunchZoomCenter(r.Effect, picture.Width, picture.Height);
            return Warp(r, picture, zoom, null, "0", "0", cx, cy);
        }

        private static string ZoomBounceRender(EffectRender r)
        {
            var picture = Picture(r);
            var amount = N(r.Number("zoom") / 100);
            var period = r.Number("period");
            var half = period / 2;
            var (damping, swing) = ElasticShape(r.Number("elastic") / 100);
            string Curve(string u) => string.Format(CultureInfo.InvariantCulture, "(1-exp(-{1}*{0})*cos({2}*{0})*(1-{0}))", u, N(damping), N(swing));
            var time = Clock(r);
            var phase = "mod(" + time + "," + N(period) + ")";
            var loop = string.Format(CultureInfo.InvariantCulture, "if(lt({0},{1}),{2},abs(1-{3}))", phase, N(half), Curve("(" + phase + "/" + N(half) + ")"), Curve("((" + phase + "-" + N(half) + ")/" + N(half) + ")"));
            var repeat = Math.Floor(r.Number("repeat"));
            if (repeat >= 1)
            {
                // 마지막 주기의 앞 절반에서 커진 뒤 머문다
                var last = N((repeat - 1) * period);
                loop = string.Format(CultureInfo.InvariantCulture, "if(gte({0},{1}),{2},{3})", time, last, Curve("min(1,(" + time + "-" + last + ")/" + N(half) + ")"), loop);
            }
            var (cx, cy) = ZoomCenter(r.Effect, ZoomBounce, picture.Width, picture.Height);
            return Warp(r, picture, "1+" + amount + "*" + loop, null, "0", "0", cx, cy);
        }

        /// <summary>
        /// 효과가 움직일 그림 자리(틀 안 — 너비, 높이, 왼쪽, 위): [화면에 맞춤]이면 가운데의 그림 크기(EffectChain의 pad와 같이 (틀 − 그림) ÷ 2를 버림한 자리), 아니면 틀 전체.
        /// </summary>
        private static (int Width, int Height, int X, int Y) Picture(EffectRender r)
        {
            if (r.PictureWidth < 2 || r.PictureHeight < 2 || (r.PictureWidth >= r.Width && r.PictureHeight >= r.Height))
                return (r.Width, r.Height, 0, 0);
            var width = Math.Min(r.Width, r.PictureWidth);
            var height = Math.Min(r.Height, r.PictureHeight);
            return (width, height, (r.Width - width) / 2, (r.Height - height) / 2);
        }

        /// <summary>클립 안 시각(초)의 수식: perspective에는 t가 없어 프레임 번호(in, 1부터)로 — (in − 1) ÷ fps + 앞선 초.</summary>
        private static string Clock(EffectRender r)
        {
            return "((in-1)/" + N(r.Fps) + "+" + N(r.TimeOffset) + ")";
        }

        private static string WavesText(Wave[] waves, string time, double speed)
        {
            var text = new StringBuilder("((");
            double total = 0;
            for (var i = 0; i < waves.Length; i++)
            {
                if (i > 0)
                    text.Append('+');
                text.AppendFormat(CultureInfo.InvariantCulture, "{0}*sin({1}*{2}+{3})", N(waves[i].Weight), N(2 * Math.PI * waves[i].Frequency * speed), time, N(waves[i].Phase));
                total += waves[i].Weight;
            }
            return text.Append(")/").Append(N(total)).Append(')').ToString();
        }

        private static string JoltText(string time, double rate, double seed)
        {
            var x = "(" + time + "*" + N(rate) + ")";
            string Hash(string k) => "(2*mod(sin((" + k + ")*12.9898+" + N(seed) + ")*43758.5453,1)-1)";
            var k0 = "floor(" + x + ")";
            var move = "min(1,(" + x + "-" + k0 + ")/" + N(JoltMove) + ")";
            return "(" + Hash(k0) + "+(" + Hash(k0 + "+1") + "-" + Hash(k0) + ")*(0.5-0.5*cos(" + N(Math.PI) + "*" + move + ")))";
        }

        /// <summary>
        /// 그림을 움직이는 필터 조각: [맞춤이면 그림 자리만 잘라] → perspective(네 모서리를 프레임마다 수식으로) [→ after] [→ 틀 크기로 투명하게 채움].
        /// zoom·angle·x·y는 수식 글(angle이 null이면 돌리지 않음), (cx, cy)는 확대·돌림의 중심(그림 픽셀).
        /// perspective(sense=source)는 출력 모서리 (0,0)·(W,0)·(0,H)·(W,H)에 원본의 네 점을 보낸다 — 픽셀 가운데 기준으로
        /// 출력 점 q(= 모서리 + 0.5)가 읽는 원본 = c + R·(q − c) ÷ zoom + (x, y)에서 0.5를 뺀 자리(배율 1·돌림 0·옮김 0이면 원래 그림 그대로).
        /// </summary>
        private static string Warp(EffectRender r, (int Width, int Height, int X, int Y) picture, string zoom, string angle, string x, string y, double cx, double cy, string after = "")
        {
            var text = new StringBuilder();
            var cropped = picture.Width != r.Width || picture.Height != r.Height;
            if (cropped)
                text.AppendFormat(CultureInfo.InvariantCulture, ",crop={0}:{1}:{2}:{3}", picture.Width, picture.Height, picture.X, picture.Y);
            text.Append(",perspective=");
            var corners = new[] { (0.5, 0.5), (picture.Width + 0.5, 0.5), (0.5, picture.Height + 0.5), (picture.Width + 0.5, picture.Height + 0.5) };
            for (var i = 0; i < corners.Length; i++)
            {
                var ax = "(" + N(corners[i].Item1 - cx) + ")";
                var ay = "(" + N(corners[i].Item2 - cy) + ")";
                string across, down;
                if (angle == null)
                {
                    across = ax;
                    down = ay;
                }
                else
                {
                    var cos = "cos(" + angle + ")";
                    var sin = "sin(" + angle + ")";
                    across = "(" + ax + "*" + cos + "-" + ay + "*" + sin + ")";
                    down = "(" + ax + "*" + sin + "+" + ay + "*" + cos + ")";
                }
                text.AppendFormat(CultureInfo.InvariantCulture, "x{0}='{1}+{2}/({3})+({4})':y{0}='{5}+{6}/({3})+({7})':",
                    i, N(cx - 0.5), across, zoom, x, N(cy - 0.5), down, y);
            }
            text.Append("eval=frame").Append(after);
            if (cropped)
                text.AppendFormat(CultureInfo.InvariantCulture, ",pad={0}:{1}:{2}:{3}:color=black@0", r.Width, r.Height, picture.X, picture.Y);
            return text.ToString();
        }

        private static string N(double value)
        {
            return RenderGraph.N(value);
        }
    }
}
