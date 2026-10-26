using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoEditor.Core
{
    /// <summary>FFmpeg가 내놓는 글(ffprobe JSON, -version, -encoders, -progress)과 FFmpeg 빌드 릴리스 목록을 읽는다. 프로세스는 돌리지 않는다.</summary>
    public static class FfmpegText
    {
        private static readonly HashSet<string> ImageFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image2", "png_pipe", "jpeg_pipe", "bmp_pipe", "webp_pipe", "tiff_pipe", "gif_pipe", "jpegls_pipe", "pam_pipe", "qoi_pipe", "svg_pipe"
        };

        private static readonly HashSet<string> ImageCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "png", "mjpeg", "bmp", "webp", "tiff", "jpegls", "qoi", "jpeg2000", "svg"
        };

        /// <summary>ffprobe -show_format -show_streams -print_format json 결과 → 미디어 정보. 소리·화면이 모두 없으면 null.</summary>
        public static MediaInfo ParseProbe(string path, string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                var info = new MediaInfo { Path = path, ProbeVersion = MediaInfo.CurrentProbe };
                double formatDuration = 0;
                double formatStart = 0;
                var formatName = "";
                if (root.TryGetProperty("format", out var format))
                {
                    formatDuration = Number(format, "duration");
                    formatStart = Number(format, "start_time");
                    formatName = Text(format, "format_name");
                }
                JsonElement? video = null;
                double audioDuration = 0;
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        var type = Text(stream, "codec_type");
                        if (type == "video" && video == null && !IsAttachedPicture(stream))
                            video = stream;
                        else if (type == "audio" && !info.HasAudio)
                        {
                            info.HasAudio = true;
                            info.AudioCodec = Text(stream, "codec_name");
                            info.AudioChannels = (int)Number(stream, "channels");
                            audioDuration = Number(stream, "duration");
                        }
                    }
                }
                if (video.HasValue)
                {
                    var v = video.Value;
                    info.HasVideo = true;
                    info.VideoCodec = Text(v, "codec_name");
                    info.Width = (int)Number(v, "width");
                    info.Height = (int)Number(v, "height");
                    if (Rotation(v) % 180 != 0)
                    {
                        var w = info.Width;
                        info.Width = info.Height;
                        info.Height = w;
                    }
                    // 화면이 파일 시작(가장 이른 스트림)보다 늦게 시작하는 초 — 이상한 값·수 밀리초 차이는 0
                    var videoStart = Number(v, "start_time") - formatStart;
                    info.VideoStart = double.IsFinite(videoStart) && videoStart > 0.0005 && videoStart < 86400 ? Math.Round(videoStart, 6) : 0;
                    var rateText = Text(v, "avg_frame_rate");
                    if (!FrameRate.TryParse(rateText, out var rate))
                        rateText = Text(v, "r_frame_rate");
                    if (FrameRate.TryParse(rateText, out rate) && rate.Fps < 400)
                        info.FrameRate = rate.Rational;
                }
                var duration = formatDuration > 0 ? formatDuration : (video.HasValue ? Number(video.Value, "duration") : audioDuration);
                var formats = formatName.Split(',');
                var still = video.HasValue && !info.HasAudio
                            && (formats.Any(ImageFormats.Contains) || (ImageCodecs.Contains(info.VideoCodec ?? "") && FrameCount(video.Value) <= 1 && duration < 0.5));
                if (still)
                {
                    info.Kind = MediaKind.Image;
                    info.DurationSeconds = 0;
                    info.FrameRate = null;
                }
                else if (info.HasVideo)
                {
                    info.Kind = MediaKind.Video;
                    info.DurationSeconds = duration;
                }
                else if (info.HasAudio)
                {
                    info.Kind = MediaKind.Audio;
                    info.DurationSeconds = duration;
                }
                else
                {
                    return null;
                }
                return info;
            }
        }

        private static bool IsAttachedPicture(JsonElement stream)
        {
            return stream.TryGetProperty("disposition", out var disposition)
                   && disposition.TryGetProperty("attached_pic", out var attached)
                   && attached.ValueKind == JsonValueKind.Number && attached.GetInt32() == 1;
        }

        private static long FrameCount(JsonElement stream)
        {
            var text = Text(stream, "nb_frames");
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) ? frames : 1;
        }

        /// <summary>휴대폰 동영상의 회전(tags.rotate 또는 side_data의 rotation).</summary>
        private static int Rotation(JsonElement stream)
        {
            if (stream.TryGetProperty("tags", out var tags) && int.TryParse(Text(tags, "rotate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rotate))
                return Math.Abs(rotate);
            if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sideData.EnumerateArray())
                {
                    if (item.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number)
                        return Math.Abs((int)rotation.GetDouble());
                }
            }
            return 0;
        }

        private static string Text(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return "";
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static double Number(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
        }

        // ---------- 버전·인코더·진행률 ----------

        /// <summary>"ffmpeg version 7.1-full_build…", "ffmpeg version n9.0.2-31-…" → 주 버전. "N-12345-g…"(개발판)는 99, 못 읽으면 0.</summary>
        public static int MajorVersion(string versionOutput)
        {
            var match = Regex.Match(versionOutput ?? "", @"version\s+n?(\d+)\.(\d+)");
            if (match.Success)
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(versionOutput ?? "", @"version\s+N-\d+"))
                return 99;
            match = Regex.Match(versionOutput ?? "", @"version\s+n?(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>ffmpeg -encoders 출력에서 인코더 이름 목록(" V....D libx264  …").</summary>
        public static HashSet<string> Encoders(string encodersOutput)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (encodersOutput ?? "").Split('\n'))
            {
                var match = Regex.Match(line, @"^\s*[VAS][F\.][S\.][X\.][B\.][D\.]\s+([A-Za-z0-9_]\S*)");
                if (match.Success)
                    result.Add(match.Groups[1].Value);
            }
            return result;
        }

        /// <summary>ffmpeg -filters 출력 → 필터 이름들(" T.C drawtext  V->V  ..." 줄의 둘째 칸).</summary>
        public static HashSet<string> Filters(string filtersOutput)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (filtersOutput ?? "").Split('\n'))
            {
                var match = Regex.Match(line, @"^\s*[T\.][S\.][C\.]?\s+([A-Za-z0-9_]+)\s+\S*->\S*");
                if (match.Success)
                    result.Add(match.Groups[1].Value);
            }
            return result;
        }

        /// <summary>-progress 출력 한 줄("out_time_us=1234567") → 초. 해당 줄이 아니면 null.</summary>
        public static double? ProgressSeconds(string line)
        {
            if (line == null)
                return null;
            foreach (var key in new[] { "out_time_us=", "out_time_ms=" })
            {
                if (line.StartsWith(key, StringComparison.Ordinal)
                    && long.TryParse(line.Substring(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro))
                    return micro / 1_000_000.0;
            }
            return null;
        }

        // ---------- FFmpeg 빌드 내려받기 ----------

        /// <summary>
        /// GitHub BtbN/FFmpeg-Builds 최신 릴리스(JSON)에서 Windows 64비트 GPL 공유 빌드 zip을 고른다 — 정식 버전(nX.Y)의 가장 높은 것,
        /// 없으면 master. 이름·주소·크기와 checksums.sha256 주소를 돌려준다. 못 찾으면 null.
        /// </summary>
        public static FfmpegBuild ChooseBuild(string releaseJson)
        {
            using (var document = JsonDocument.Parse(releaseJson))
            {
                if (!document.RootElement.TryGetProperty("assets", out var assets))
                    return null;
                var all = assets.EnumerateArray().Select(a => new
                {
                    Name = Text(a, "name"),
                    Url = Text(a, "browser_download_url"),
                    Size = (long)Number(a, "size")
                }).ToList();
                var checksums = all.FirstOrDefault(a => a.Name == "checksums.sha256");
                var best = all.Select(a => new { Asset = a, Match = Regex.Match(a.Name, @"^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-shared-\d+\.\d+\.zip$") })
                    .Where(x => x.Match.Success)
                    .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                    .ThenByDescending(x => int.Parse(x.Match.Groups[2].Value, CultureInfo.InvariantCulture))
                    .Select(x => x.Asset)
                    .FirstOrDefault()
                    ?? all.FirstOrDefault(a => a.Name == "ffmpeg-master-latest-win64-gpl-shared.zip");
                if (best == null || checksums == null || !FromGitHub(best.Url) || !FromGitHub(checksums.Url))
                    return null;
                return new FfmpegBuild { Name = best.Name, Url = best.Url, Size = best.Size, ChecksumsUrl = checksums.Url };
            }
        }

        /// <summary>https://github.com/... 주소인지(내려받기는 GitHub 릴리스에서만).</summary>
        internal static bool FromGitHub(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                   && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.Ordinal) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));
        }

        /// <summary>checksums.sha256("해시  파일이름" 줄들)에서 파일의 SHA-256(소문자 16진수). 없으면 null.</summary>
        public static string FindChecksum(string checksums, string fileName)
        {
            foreach (var line in (checksums ?? "").Split('\n'))
            {
                var match = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (match.Success && string.Equals(match.Groups[2].Value.Trim(), fileName, StringComparison.Ordinal))
                    return match.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }
    }

    public sealed class FfmpegBuild
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public string ChecksumsUrl { get; set; }
    }
}
