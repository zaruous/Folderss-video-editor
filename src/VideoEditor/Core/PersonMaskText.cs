using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>인물 분리 모델(ONNX 파일) 하나. 크기·SHA-256은 고정해 두고 내려받은 파일과 맞춰 본다.</summary>
    public sealed class PersonMaskModel
    {
        /// <summary>캐시 이름에 들어가는 모델 구별자(모델이나 리비전이 바뀌면 마스크를 새로 만든다).</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>데이터 폴더\person-mask\models 아래 파일 이름.</summary>
        public string File { get; set; }
        /// <summary>내려받을 곳(리비전을 고정한 주소).</summary>
        public string Url { get; set; }
        public string Revision { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public string License { get; set; }
        /// <summary>창·문서에 적을 출처와 저작자 표시.</summary>
        public string Source { get; set; }
        public string Attribution { get; set; }
        /// <summary>모델에 넣는 그림의 짧은 변(픽셀) — 이보다 크면 느려지고 작으면 가장자리가 뭉개진다.</summary>
        public int ShortSide { get; set; }
    }

    /// <summary>
    /// 인물 마스크(사람만 따낸 흑백 마스크)를 만드는 데 쓰는 것: 고정한 모델, 모델 입력 크기, 마스크 프레임 레이트·프레임 수,
    /// 캐시 파일 이름, FFmpeg 인수(원본 → RGB 프레임, 마스크 프레임 → FFV1 흑백 동영상·PNG), 모델 입력·출력 변환.
    /// 프로세스·네트워크·ONNX Runtime은 쓰지 않는다(Media.PersonMaskService가 돌린다).
    /// </summary>
    public static class PersonMaskText
    {
        /// <summary>시험용: 모델 파일(models\ 아래도 됨)이 든 폴더. 있으면 데이터 폴더보다 먼저 본다.</summary>
        public const string EnvFolder = "FOLDERSS_VE_PERSONMASK";

        /// <summary>마스크 모양(후처리)이 바뀌면 올린다 — 캐시 이름이 바뀌어 예전 마스크를 쓰지 않는다.</summary>
        public const int FormatVersion = 2;

        /// <summary>모델 입력 크기는 이 배수(MODNet은 1/32까지 줄였다 키운다).</summary>
        public const int Multiple = 32;

        /// <summary>모델 입력의 긴 변 상한(아주 긴 화면에서 느려지지 않게).</summary>
        public const int MaxLongSide = 512;

        /// <summary>마스크 프레임 레이트 상한 — 넘으면 절반씩 줄인다(원본 프레임과 시각이 그대로 겹친다).</summary>
        public const double MaxRate = 60;

        /// <summary>
        /// MODNet(사진 인물 매팅, Apache-2.0)의 ONNX 변환(Hugging Face Xenova/modnet, Apache-2.0). 리비전을 고정한 주소에서 받고
        /// 크기·SHA-256은 그 리비전의 LFS 값. 이 PC(6코어)에서 448×256 한 장에 약 60ms.
        /// </summary>
        public static PersonMaskModel Model { get; } = new PersonMaskModel
        {
            Id = "modnet-fa2fa546",
            Name = "MODNet",
            File = "modnet-fa2fa546.onnx",
            Revision = "fa2fa546052fba4c08921230a26cc69a333fca12",
            Url = "https://huggingface.co/Xenova/modnet/resolve/fa2fa546052fba4c08921230a26cc69a333fca12/onnx/model.onnx",
            Size = 25888640,
            Sha256 = "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9",
            License = "Apache-2.0",
            Source = "Hugging Face Xenova/modnet (리비전 fa2fa54)",
            Attribution = "MODNet © Zhanghan Ke 외(github.com/ZHKKKe/MODNet, Apache License 2.0), ONNX 변환 Xenova/modnet(Apache License 2.0)",
            ShortSide = 256
        };

        /// <summary>
        /// 모델에 넣을 크기: 비율을 지켜 짧은 변을 shortSide로(긴 변은 MaxLongSide까지), 두 변 모두 Multiple의 배수로 반올림.
        /// 비율이 조금 어긋나도 렌더 그래프가 마스크를 원본 비율의 클립 그림 크기로 늘여 맞추므로 사람 자리는 그대로 겹친다.
        /// </summary>
        public static (int Width, int Height) InputSize(int width, int height, int shortSide = 256)
        {
            if (width <= 0 || height <= 0)
            {
                width = 16;
                height = 9;
            }
            var scale = shortSide / (double)Math.Min(width, height);
            if (Math.Max(width, height) * scale > MaxLongSide)
                scale = MaxLongSide / (double)Math.Max(width, height);
            return (Snap(width * scale), Snap(height * scale));
        }

        private static int Snap(double value)
        {
            return Math.Max(Multiple, (int)Math.Round(value / Multiple, MidpointRounding.AwayFromZero) * Multiple);
        }

        /// <summary>
        /// 마스크 프레임 레이트: 원본 평균 레이트(가변 레이트도 평균으로 고정), 모르면 30. MaxRate를 넘으면 절반씩(120 → 60, 100 → 50).
        /// </summary>
        public static FrameRate MaskRate(string frameRate)
        {
            if (!FrameRate.TryParse(frameRate, out var rate))
                return FrameRate.Default;
            while (rate.Fps > MaxRate + 0.01)
                rate = rate.Numerator % 2 == 0 ? new FrameRate(rate.Numerator / 2, rate.Denominator) : new FrameRate(rate.Numerator, rate.Denominator * 2);
            return rate;
        }

        /// <summary>원본 길이(초)를 마스크 레이트로 고정했을 때 프레임 수(진행률 분모, 1 이상).</summary>
        public static long FrameCount(double durationSeconds, FrameRate rate)
        {
            if (double.IsNaN(durationSeconds) || durationSeconds <= 0)
                return 1;
            return Math.Max(1, (long)Math.Ceiling(durationSeconds * rate.Numerator / rate.Denominator - 1e-6));
        }

        /// <summary>
        /// 캐시 파일 이름(확장자 없음): "경로 해시-원본 크기·고친 시각·모델 해시". 원본을 바꿔 넣거나 모델이 바뀌면 뒤가 달라져 새로 만든다.
        /// 앞 16자(경로)가 같은 다른 파일은 그 원본의 옛 마스크다(<see cref="PathKey"/>). 파일 이름에 원본 이름을 쓰지 않는다.
        /// </summary>
        public static string CacheName(string fullPath, long size, long lastWriteTicks, PersonMaskModel model = null)
        {
            var identity = string.Join("|", size.ToString(CultureInfo.InvariantCulture), lastWriteTicks.ToString(CultureInfo.InvariantCulture),
                (model ?? Model).Id, FormatVersion.ToString(CultureInfo.InvariantCulture));
            return PathKey(fullPath) + "-" + Hash(identity).Substring(0, 16);
        }

        /// <summary>원본 경로의 해시 16자(Windows처럼 대소문자는 가리지 않는다).</summary>
        public static string PathKey(string fullPath)
        {
            return Hash(Path.GetFullPath(fullPath).ToUpperInvariant()).Substring(0, 16);
        }

        /// <summary>동영상 원본의 마스크는 FFV1 흑백 .mkv, 이미지 원본은 흑백 .png.</summary>
        public static string Extension(bool image)
        {
            return image ? ".png" : ".mkv";
        }

        private static string Hash(string text)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        /// <summary>
        /// 원본 → 모델 크기 RGB 프레임(rgb24 원시 바이트를 표준 출력으로). 동영상은 마스크 레이트로 고정하고(fps) 첫 화면부터 만들어
        /// 마스크 프레임 k가 원본 화면 시작 + k/레이트 초에 놓인다 — 렌더 그래프가 원본과 같은 -ss·-t·속도로, 화면 시작(<see cref="MediaInfo.VideoStart"/>)만큼 당겨 읽으면 사람 자리가 겹친다.
        /// 화면은 첫 동영상 스트림(표지 그림 제외), 회전 정보는 FFmpeg 기본대로 돌려 읽는다(그래프와 같음).
        /// </summary>
        public static List<string> DecodeArguments(string source, int width, int height, FrameRate rate, bool image)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-i", source };
            var scale = "scale=" + width + ":" + height + ":flags=bilinear";
            if (image)
                args.AddRange(new[] { "-frames:v", "1", "-vf", scale });
            else
                args.AddRange(new[] { "-map", "0:V:0", "-an", "-sn", "-dn", "-vf", "fps=" + rate.Rational + "," + scale });
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" });
            return args;
        }

        /// <summary>
        /// 마스크 프레임(gray 원시 바이트, 표준 입력) → output. 동영상은 FFV1(무손실, 모든 프레임이 키 프레임이라 어디서든 바로 읽힘) .mkv,
        /// 이미지는 PNG 한 장. output은 .part일 수 있어 형식을 정해 준다.
        /// </summary>
        public static List<string> EncodeArguments(int width, int height, FrameRate rate, string output, bool image)
        {
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "rawvideo", "-pix_fmt", "gray", "-s", width + "x" + height, "-framerate", rate.Rational, "-i", "pipe:0"
            };
            if (image)
                args.AddRange(new[] { "-frames:v", "1", "-c:v", "png", "-pix_fmt", "gray", "-f", "image2", "-update", "1", output });
            else
                args.AddRange(new[] { "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "gray", "-f", "matroska", output });
            return args;
        }

        /// <summary>RGB 프레임(rgb24) → 모델 입력(1×3×높이×너비, 색마다 따로, -1~1 — MODNet의 평균 0.5·표준 편차 0.5 정규화).</summary>
        public static void FillInput(byte[] rgb, float[] input, int width, int height)
        {
            var plane = width * height;
            if (rgb.Length < plane * 3 || input.Length < plane * 3)
                throw new ArgumentException("프레임 크기가 맞지 않습니다.");
            for (var i = 0; i < plane; i++)
            {
                input[i] = rgb[i * 3] / 127.5f - 1f;
                input[plane + i] = rgb[i * 3 + 1] / 127.5f - 1f;
                input[plane * 2 + i] = rgb[i * 3 + 2] / 127.5f - 1f;
            }
        }

        /// <summary>앞 프레임과 이만큼(0~255) 넘게 달라지면 바로 따라가고, 덜 달라지면 덜 따라가 잔떨림을 줄인다.</summary>
        public const int FollowDifference = 96;

        /// <summary>
        /// 모델 출력(사람일 정도 0~1) → 마스크 바이트(0~255). previous(앞 프레임 마스크)가 있으면 가볍게 시간 평활:
        /// 차이가 작을수록 절반만 따라가고(배경의 잔떨림·깜빡임이 줄어든다) <see cref="FollowDifference"/> 넘게 바뀌면 바로 따라간다(움직임은 늦지 않게).
        /// 거의 0·거의 1은 딱 0·255로 — 평평한 곳이 잘 줄어들고 효과가 배경에 새지 않는다.
        /// </summary>
        public static void ToMask(ReadOnlySpan<float> alpha, byte[] mask, byte[] previous)
        {
            if (alpha.Length < mask.Length || (previous != null && previous.Length < mask.Length))
                throw new ArgumentException("마스크 크기가 맞지 않습니다.");
            for (var i = 0; i < mask.Length; i++)
            {
                var value = alpha[i] * 255f;
                if (float.IsNaN(value) || value < 0)
                    value = 0;
                else if (value > 255)
                    value = 255;
                if (previous != null)
                {
                    var difference = value - previous[i];
                    var follow = 0.5f + 0.5f * Math.Min(1f, Math.Abs(difference) / FollowDifference);
                    value = previous[i] + difference * follow;
                }
                mask[i] = value < 4 ? (byte)0 : value > 251 ? (byte)255 : (byte)(value + 0.5f);
            }
        }
    }
}
