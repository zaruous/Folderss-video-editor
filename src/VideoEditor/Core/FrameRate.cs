using System;
using System.Globalization;

namespace VideoEditor.Core
{
    /// <summary>
    /// 프로젝트 프레임 레이트(유리수 — 29.97은 30000/1001). 타임라인의 모든 위치·길이는 이 레이트의 정수 프레임이라
    /// 부동소수 누적 오차로 컷이 한 프레임 밀리지 않는다. 초는 FFmpeg에 넘길 때만 계산한다.
    /// </summary>
    public readonly struct FrameRate : IEquatable<FrameRate>
    {
        public FrameRate(int numerator, int denominator)
        {
            if (numerator <= 0 || denominator <= 0)
                throw new ArgumentOutOfRangeException(nameof(numerator), "프레임 레이트는 0보다 커야 합니다.");
            Numerator = numerator;
            Denominator = denominator;
        }

        public int Numerator { get; }
        public int Denominator { get; }

        public double Fps { get { return (double)Numerator / Denominator; } }

        /// <summary>타임코드의 프레임 자리(29.97 → 30). 드롭 프레임은 쓰지 않는다.</summary>
        public int NominalFps { get { return (int)Math.Round(Fps); } }

        public static readonly FrameRate Default = new FrameRate(30, 1);

        public double ToSeconds(long frames)
        {
            return frames * (double)Denominator / Numerator;
        }

        /// <summary>초 → 가장 가까운 프레임.</summary>
        public long ToFrames(double seconds)
        {
            return (long)Math.Round(seconds * Numerator / Denominator, MidpointRounding.AwayFromZero);
        }

        /// <summary>FFmpeg 인수에 쓰는 초(소수 6자리, 문화권 무관).</summary>
        public string SecondsText(long frames)
        {
            return Seconds(ToSeconds(frames));
        }

        public static string Seconds(double seconds)
        {
            return seconds.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>"30000/1001" 같은 FFmpeg 표기.</summary>
        public string Rational { get { return Numerator + "/" + Denominator; } }

        /// <summary>"30", "29.97", "30000/1001", "24000/1001" 같은 글을 읽는다(ffprobe r_frame_rate 포함). 못 읽거나 0이면 false.</summary>
        public static bool TryParse(string text, out FrameRate rate)
        {
            rate = Default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            var slash = text.IndexOf('/');
            if (slash > 0)
            {
                int num, den;
                if (int.TryParse(text.Substring(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out num)
                    && int.TryParse(text.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out den)
                    && num > 0 && den > 0)
                {
                    rate = Reduce(num, den);
                    return true;
                }
                return false;
            }
            double fps;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) || fps <= 0 || fps > 1000)
                return false;
            // 흔한 NTSC 레이트는 정확한 유리수로
            foreach (var ntsc in new[] { 24, 30, 60, 120 })
            {
                if (Math.Abs(fps - ntsc * 1000.0 / 1001) < 0.005)
                {
                    rate = new FrameRate(ntsc * 1000, 1001);
                    return true;
                }
            }
            if (Math.Abs(fps - Math.Round(fps)) < 0.0001)
            {
                rate = new FrameRate((int)Math.Round(fps), 1);
                return true;
            }
            rate = Reduce((int)Math.Round(fps * 1000), 1000);
            return true;
        }

        private static FrameRate Reduce(int num, int den)
        {
            int a = num, b = den;
            while (b != 0)
            {
                var t = a % b;
                a = b;
                b = t;
            }
            return new FrameRate(num / a, den / a);
        }

        /// <summary>프레임 → "HH:MM:SS:FF".</summary>
        public string Timecode(long frames)
        {
            var negative = frames < 0;
            if (negative)
                frames = -frames;
            // 실제 시간과 맞추려고 초 자리는 "그 초의 첫 프레임(ToFrames(s))이 frames 이하인 가장 큰 s", 프레임 자리는 그 초 안의 순번.
            // 그러면 프레임 자리는 늘 0 ~ (그 초의 프레임 수 - 1)이라 TryParseTimecode로 되읽힌다.
            var totalSeconds = (long)Math.Floor(ToSeconds(frames) + 1e-9);
            while (ToFrames(totalSeconds + 1) <= frames)
                totalSeconds++;
            while (totalSeconds > 0 && ToFrames(totalSeconds) > frames)
                totalSeconds--;
            var ff = frames - ToFrames(totalSeconds);
            var text = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}:{3:00}",
                totalSeconds / 3600, totalSeconds / 60 % 60, totalSeconds % 60, ff);
            return negative ? "-" + text : text;
        }

        /// <summary>"HH:MM:SS:FF", "MM:SS:FF", "SS:FF", "HH:MM:SS.mmm"(소수 초)을 프레임으로. 못 읽으면 false.</summary>
        public bool TryParseTimecode(string text, out long frames)
        {
            frames = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            var parts = text.Split(':');
            if (parts.Length == 0 || parts.Length > 4)
                return false;
            // 마지막 자리에 소수점이 있으면 초 표기(00:01:02.5)
            if (parts[parts.Length - 1].Contains("."))
            {
                double seconds = 0;
                for (var i = 0; i < parts.Length; i++)
                {
                    double value;
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0)
                        return false;
                    seconds = seconds * 60 + value;
                }
                frames = ToFrames(seconds);
                return true;
            }
            var numbers = new long[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;
            }
            if (parts.Length == 1)
            {
                frames = numbers[0];
                return true;
            }
            var ff = numbers[numbers.Length - 1];
            if (ff >= NominalFps)
                return false;
            long secondsPart = 0;
            for (var i = 0; i < numbers.Length - 1; i++)
                secondsPart = secondsPart * 60 + numbers[i];
            frames = ToFrames(secondsPart) + ff;
            return true;
        }

        public bool Equals(FrameRate other)
        {
            return (long)Numerator * other.Denominator == (long)other.Numerator * Denominator;
        }

        public override bool Equals(object obj)
        {
            return obj is FrameRate other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Fps.GetHashCode();
        }

        public override string ToString()
        {
            return Denominator == 1 ? Numerator.ToString(CultureInfo.InvariantCulture) : Fps.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
