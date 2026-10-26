using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>파일 정보 읽기(ffprobe).</summary>
    public static class MediaProbe
    {
        public static async Task<MediaInfo> Probe(FfmpegTool tool, string path, CancellationToken cancellation)
        {
            var result = await FfmpegRunner.RunText(tool.FfprobePath,
                new[] { "-v", "error", "-protocol_whitelist", "file", "-print_format", "json", "-show_format", "-show_streams", path },
                TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new FfmpegException("파일을 읽지 못했습니다: " + Path.GetFileName(path), result.Error);
            var info = FfmpegText.ParseProbe(path, result.Output);
            if (info == null)
                throw new FfmpegException("동영상·오디오·이미지가 아닙니다: " + Path.GetFileName(path), result.Error);
            try
            {
                var file = new FileInfo(path);
                info.FileSize = file.Length;
                info.FileTime = file.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 크기를 못 읽으면 다음에 다시 읽는다
            }
            return info;
        }

        /// <summary>
        /// 다시 읽어야 하는지: 읽은 뒤 파일이 바뀜(크기·고친 시각, 모르면 바뀐 것으로) 또는 예전 판으로 읽은 동영상(화면 시작을 기록하지 않음 — 인물 마스크가 어긋날 수 있다).
        /// 파일이 없으면 false.
        /// </summary>
        public static bool IsStale(MediaInfo media)
        {
            try
            {
                var file = new FileInfo(media.Path);
                return file.Exists && (media.FileSize == 0 || file.Length != media.FileSize || file.LastWriteTimeUtc.Ticks != media.FileTime
                                       || (media.Kind == MediaKind.Video && media.HasVideo && media.ProbeVersion < MediaInfo.CurrentProbe));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>글자 클립용 글꼴과 임시 파일 폴더.</summary>
    public static class RenderEnvironment
    {
        /// <summary>미리 보기·재생·내보내기가 쓰는 인물 마스크 제공자(편집기가 시작할 때 정한다, 없으면 null).</summary>
        public static IPersonMaskProvider PersonMasks { get; set; }

        /// <summary>한글이 나오는 글꼴(Windows: 맑은 고딕), 없으면 흔한 글꼴. 못 찾으면 null(FFmpeg 기본 글꼴).</summary>
        public static string DefaultFont()
        {
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                candidates.Add(Path.Combine(fonts, "malgun.ttf"));
                candidates.Add(Path.Combine(fonts, "arial.ttf"));
            }
            candidates.Add("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
            candidates.Add("/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf");
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>프로세스별 임시 폴더들이 모이는 곳(시작할 때 끝난 프로세스의 캐시를 정리 — <see cref="TextImageCache.Cleanup"/>).</summary>
        public static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "folderss-video-editor");
        }

        /// <summary>이 프로세스의 임시 폴더(필터 스크립트·글자 파일·꾸민 글자 PNG 캐시).</summary>
        public static string TempFolder()
        {
            var folder = Path.Combine(TempRoot(), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>렌더 계획을 실행할 FFmpeg 인수로: 입력들 + 필터 스크립트 파일 + 출력 인수.</summary>
    public static class RenderCommand
    {
        /// <summary>
        /// 꾸민 글자 PNG를 캐시에 그려 두고, 글자 파일과 필터 스크립트를 쓰고 인수를 만든다.
        /// 쓴 스크립트 경로를 script로 돌려준다(끝나면 지운다). PNG는 캐시라서 지우지 않는다.
        /// </summary>
        public static List<string> Arguments(FfmpegTool tool, RenderPlan plan, IEnumerable<string> outputArgs, out string script, CancellationToken cancellation = default)
        {
            // 자막은 libass로 입힌다 — 없으면 FFmpeg의 "No such filter" 대신 무엇을 할지 알린다
            if (plan.UsesLibass && !tool.HasFilter("ass"))
                throw new FfmpegException("이 FFmpeg에는 자막을 입히는 기능(libass)이 없습니다. [FFmpeg 설정]에서 내려받은 FFmpeg나 libass가 든 빌드를 쓰세요.", null);
            // 그리다 실패하면 아무 임시 파일도 남기지 않도록 먼저 그린다
            DrawTextImages(plan, cancellation);
            foreach (var file in plan.TextFiles)
                File.WriteAllText(file.Key, file.Value, new UTF8Encoding(false));
            script = Path.Combine(RenderEnvironment.TempFolder(), "graph-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(script, plan.Filter, new UTF8Encoding(false));
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
            foreach (var input in plan.Inputs)
            {
                // 입력은 로컬 파일만 열게 한다(재생 목록처럼 생긴 파일이 네트워크·다른 경로를 열지 못하게)
                var at = input.IndexOf("-i");
                for (var i = 0; i < input.Count; i++)
                {
                    if (i == at)
                        args.AddRange(new[] { "-protocol_whitelist", "file" });
                    args.Add(input[i]);
                }
            }
            args.AddRange(tool.FilterScriptArgs(script));
            args.AddRange(outputArgs);
            return args;
        }

        /// <summary>계획의 꾸민 글자 PNG를 캐시에 그려 둔다(있으면 그대로).</summary>
        public static void DrawTextImages(RenderPlan plan, CancellationToken cancellation)
        {
            foreach (var image in plan.TextImages)
                TextImageCache.Ensure(plan.TextRasterizer, image.Key, image.Value, cancellation);
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // 임시 파일 — 지우지 못해도 다음 정리 때
            }
        }
    }

    /// <summary>내보내기 설정.</summary>
    public sealed class ExportSettings
    {
        public string OutputPath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>null이면 프로젝트 프레임 레이트.</summary>
        public string FrameRate { get; set; }
        /// <summary>"auto"면 <see cref="FfmpegTool.AutoVideoEncoder"/>.</summary>
        public string Encoder { get; set; } = "auto";
        /// <summary>0 높음, 1 보통, 2 낮음.</summary>
        public int Quality { get; set; } = 1;
        public long From { get; set; }
        public long To { get; set; }
    }

    /// <summary>MP4 내보내기(H.264 + AAC). -progress로 진행률을 읽고, 취소하면 만들던 파일을 지운다.</summary>
    public static class Exporter
    {
        /// <summary>해상도별 기준 비트레이트(Mbps) — 품질 값이 비트레이트인 인코더용(Media Foundation).</summary>
        private static double Mbps(int height, int quality)
        {
            var baseRate = height >= 2000 ? 40 : height >= 1400 ? 16 : height >= 1000 ? 10 : height >= 700 ? 6 : 3;
            return baseRate * new[] { 1.6, 1.0, 0.6 }[Math.Max(0, Math.Min(2, quality))];
        }

        public static List<string> VideoCodecArgs(string encoder, int quality, int height)
        {
            quality = Math.Max(0, Math.Min(2, quality));
            switch (encoder)
            {
                case "libx264":
                    return new List<string> { "-c:v", "libx264", "-preset", "medium", "-crf", new[] { "18", "23", "28" }[quality] };
                case "h264_nvenc":
                    return new List<string> { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", new[] { "19", "24", "30" }[quality], "-b:v", "0" };
                case "h264_qsv":
                    return new List<string> { "-c:v", "h264_qsv", "-global_quality", new[] { "20", "25", "30" }[quality] };
                case "h264_amf":
                    return new List<string> { "-c:v", "h264_amf", "-rc", "cqp", "-qp_i", new[] { "18", "23", "28" }[quality], "-qp_p", new[] { "20", "25", "30" }[quality] };
                case "mpeg4":
                    return new List<string> { "-c:v", "mpeg4", "-q:v", new[] { "2", "5", "9" }[quality] };
                default:
                    // h264_mf, libopenh264 등: 비트레이트로
                    return new List<string> { "-c:v", encoder, "-b:v", RenderGraph.N(Mbps(height, quality)) + "M" };
            }
        }

        public static List<string> OutputArgs(FfmpegTool tool, ExportSettings settings, bool hasAudio)
        {
            return OutputArgs(tool, settings, hasAudio, settings.OutputPath);
        }

        /// <summary>output에 쓴다(형식은 mp4로 고정 — 임시 이름 .part에 써도 되게).</summary>
        public static List<string> OutputArgs(FfmpegTool tool, ExportSettings settings, bool hasAudio, string output)
        {
            var encoder = settings.Encoder == "auto" || string.IsNullOrEmpty(settings.Encoder) ? tool.AutoVideoEncoder() : settings.Encoder;
            var args = new List<string> { "-map", "[vout]" };
            if (hasAudio)
                args.AddRange(new[] { "-map", "[aout]" });
            args.AddRange(VideoCodecArgs(encoder, settings.Quality, settings.Height));
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            if (!string.IsNullOrEmpty(settings.FrameRate))
                args.AddRange(new[] { "-r", settings.FrameRate });
            if (hasAudio)
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });
            args.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "-f", "mp4", output });
            return args;
        }

        public static async Task Export(FfmpegTool tool, Project project, ExportSettings settings, IProgress<double> progress, CancellationToken cancellation,
            ITextRasterizer textRasterizer = null)
        {
            if (settings.To <= settings.From)
                throw new InvalidOperationException("내보낼 구간이 비어 있습니다. 타임라인에 클립을 놓으세요.");
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = settings.From,
                To = settings.To,
                Width = settings.Width,
                Height = settings.Height,
                FontFile = RenderEnvironment.DefaultFont(),
                TempFolder = RenderEnvironment.TempFolder(),
                FfmpegMajor = tool.MajorVersion,
                TextRasterizer = textRasterizer,
                PersonMasks = RenderEnvironment.PersonMasks
            });
            string script;
            // 만드는 동안은 옆의 .part에 쓰고 다 되면 바꿔 넣는다 — 실패·취소해도 원래 있던 파일(덮어쓰기로 고른 것)이 남는다
            var part = settings.OutputPath + ".part";
            var args = RenderCommand.Arguments(tool, plan, OutputArgs(tool, settings, plan.HasAudio, part), out script, cancellation);
            try
            {
                using (var process = FfmpegRunner.Start(tool.FfmpegPath, args, true))
                {
                    var error = process.StandardError.ReadToEndAsync();
                    using (cancellation.Register(() => FfmpegRunner.Kill(process)))
                    {
                        string line;
                        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            var seconds = FfmpegText.ProgressSeconds(line);
                            if (seconds.HasValue && plan.Seconds > 0)
                                progress?.Report(Math.Min(1, seconds.Value / plan.Seconds));
                        }
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new FfmpegException("내보내기에 실패했습니다.", await error.ConfigureAwait(false));
                    File.Move(part, settings.OutputPath, true);
                    progress?.Report(1);
                }
            }
            finally
            {
                RenderCommand.TryDelete(script);
                foreach (var file in plan.TextFiles.Keys)
                    RenderCommand.TryDelete(file);
                RenderCommand.TryDelete(part);
            }
        }
    }

    /// <summary>미리 보기: 한 프레임(BGRA)·재생용 화면 스트림·소리 스트림.</summary>
    public static class PreviewRenderer
    {
        /// <summary>frame 위치의 화면을 BGRA 바이트(width×height×4)로.</summary>
        public static async Task<byte[]> Frame(FfmpegTool tool, Project project, long frame, int width, int height, CancellationToken cancellation,
            ITextRasterizer textRasterizer = null)
        {
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = frame,
                To = frame + 1,
                Width = width,
                Height = height,
                Audio = false,
                Preview = true,
                FontFile = RenderEnvironment.DefaultFont(),
                TempFolder = RenderEnvironment.TempFolder(),
                FfmpegMajor = tool.MajorVersion,
                TextRasterizer = textRasterizer,
                PersonMasks = RenderEnvironment.PersonMasks
            });
            string script;
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out script, cancellation);
            try
            {
                var bytes = await FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                return bytes.Length >= (long)width * height * 4 ? bytes : null;
            }
            finally
            {
                RenderCommand.TryDelete(script);
                foreach (var file in plan.TextFiles.Keys)
                    RenderCommand.TryDelete(file);
            }
        }

        /// <summary>
        /// 재생 전에 [from, to) 구간의 꾸민 글자 PNG를 캐시에 그려 둔다 — UI 스레드가 아닌 곳에서 부른다.
        /// 그 뒤 같은 크기로 <see cref="StartVideo"/>를 부르면 캐시만 읽어 바로 시작한다.
        /// </summary>
        public static void PrepareTextImages(Project project, long from, long to, int width, int height, ITextRasterizer textRasterizer, CancellationToken cancellation)
        {
            if (textRasterizer == null || to <= from)
                return;
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = from,
                To = to,
                Width = width,
                Height = height,
                Audio = false,
                Preview = true,
                TempFolder = RenderEnvironment.TempFolder(),
                TextRasterizer = textRasterizer,
                PersonMasks = RenderEnvironment.PersonMasks
            });
            RenderCommand.DrawTextImages(plan, cancellation);
        }

        /// <summary>재생: from부터 끝까지 화면을 BGRA 원시 프레임으로 내보내는 FFmpeg를 시작한다(표준 출력에서 읽는다).</summary>
        public static Process StartVideo(FfmpegTool tool, Project project, long from, long to, int width, int height, out List<string> tempFiles,
            ITextRasterizer textRasterizer = null)
        {
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = from,
                To = to,
                Width = width,
                Height = height,
                Audio = false,
                Preview = true,
                Playback = true,
                FontFile = RenderEnvironment.DefaultFont(),
                TempFolder = RenderEnvironment.TempFolder(),
                FfmpegMajor = tool.MajorVersion,
                TextRasterizer = textRasterizer,
                PersonMasks = RenderEnvironment.PersonMasks
            });
            string script;
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out script);
            tempFiles = new List<string>(plan.TextFiles.Keys) { script };
            return FfmpegRunner.Start(tool.FfmpegPath, args, true);
        }

        /// <summary>재생: from부터 끝까지 소리를 48kHz 16비트 스테레오 PCM으로.</summary>
        public static Process StartAudio(FfmpegTool tool, Project project, long from, long to, out string script)
        {
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = from,
                To = to,
                Width = 16,
                Height = 16,
                Video = false,
                Preview = true
            });
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[aout]", "-f", "s16le", "-ac", "2", "-ar", project.SampleRate.ToString(CultureInfo.InvariantCulture), "pipe:1" }, out script);
            return FfmpegRunner.Start(tool.FfmpegPath, args, true);
        }
    }

    /// <summary>미디어 패널·타임라인의 썸네일(PNG)과 오디오 파형.</summary>
    public static class MediaPictures
    {
        public static Task<byte[]> Thumbnail(FfmpegTool tool, MediaInfo media, double seconds, int width, CancellationToken cancellation)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
            if (media.Kind == MediaKind.Video && seconds > 0)
                args.AddRange(new[] { "-ss", RenderGraph.N(seconds) });
            args.AddRange(new[] { "-protocol_whitelist", "file", "-i", media.Path, "-frames:v", "1", "-vf", "scale=" + width + ":-2", "-f", "image2pipe", "-c:v", "png", "pipe:1" });
            return FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromSeconds(30), cancellation);
        }

        /// <summary>전환 효과 미리 보기 띠(PNG, <see cref="Transitions.PreviewFrames"/>칸을 가로로).</summary>
        public static Task<byte[]> TransitionStrip(FfmpegTool tool, TransitionDef def, CancellationToken cancellation)
        {
            return FfmpegRunner.RunBytes(tool.FfmpegPath, Transitions.PreviewArguments(def), TimeSpan.FromSeconds(30), cancellation);
        }

        /// <summary>동영상 효과 미리 보기 띠(PNG, <see cref="VideoEffects.PreviewFrames"/>칸을 가로로).</summary>
        public static Task<byte[]> EffectStrip(FfmpegTool tool, VideoEffectDef def, CancellationToken cancellation)
        {
            return FfmpegRunner.RunBytes(tool.FfmpegPath, VideoEffects.PreviewArguments(def), TimeSpan.FromSeconds(30), cancellation);
        }

        /// <summary>오디오 크기(0~1)를 초당 peaksPerSecond개로. 소리 없는 파일이면 빈 배열.</summary>
        public static async Task<float[]> Waveform(FfmpegTool tool, string path, int peaksPerSecond, CancellationToken cancellation)
        {
            const int sampleRate = 8000;
            var bytes = await FfmpegRunner.RunBytes(tool.FfmpegPath,
                new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-i", path, "-vn", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1" },
                TimeSpan.FromMinutes(5), cancellation).ConfigureAwait(false);
            return Peaks(bytes, sampleRate / peaksPerSecond);
        }

        public static float[] Peaks(byte[] pcm16, int samplesPerPeak)
        {
            var samples = pcm16.Length / 2;
            var count = (samples + samplesPerPeak - 1) / samplesPerPeak;
            var peaks = new float[count];
            for (var i = 0; i < count; i++)
            {
                var max = 0;
                var end = Math.Min(samples, (i + 1) * samplesPerPeak);
                for (var s = i * samplesPerPeak; s < end; s++)
                {
                    var value = Math.Abs((int)BitConverter.ToInt16(pcm16, s * 2));
                    if (value > max)
                        max = value;
                }
                peaks[i] = max / 32768f;
            }
            return peaks;
        }
    }
}
