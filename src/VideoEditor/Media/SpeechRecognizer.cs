using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>찾은 whisper.cpp(데이터 폴더\whisper\whisper-cli.exe)와 모델 폴더(데이터 폴더\whisper\models).</summary>
    public sealed class WhisperTool
    {
        private WhisperTool(string folder)
        {
            Folder = folder;
        }

        public string Folder { get; }
        public string CliPath { get { return Path.Combine(Folder, SpeechText.CliName); } }

        /// <summary>folder에 whisper-cli.exe가 있으면 그것(없으면 null).</summary>
        public static WhisperTool Locate(string folder)
        {
            return !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, SpeechText.CliName)) ? new WhisperTool(folder) : null;
        }

        public static string ModelFolder(string folder)
        {
            return Path.Combine(folder, "models");
        }

        public static string ModelPath(string folder, SpeechModel model)
        {
            return Path.Combine(ModelFolder(folder), model.File);
        }

        /// <summary>모델을 받아 두었는지(크기까지 맞아야 — 받다 끊긴 파일은 .part로만 남는다).</summary>
        public static bool HasModel(string folder, SpeechModel model)
        {
            var path = ModelPath(folder, model);
            return File.Exists(path) && new FileInfo(path).Length == model.Size;
        }
    }

    /// <summary>인식에 쓸 것: 모델·말소리 구간 감지 모델 경로, 말, 자주 나오는 낱말.</summary>
    public sealed class SpeechOptions
    {
        public string Model { get; set; }
        /// <summary>말소리 구간 감지(Silero VAD) 모델 경로 — 없으면 소리 전체를 인식.</summary>
        public string Vad { get; set; }
        public string Language { get; set; }
        public string Prompt { get; set; }
    }

    /// <summary>
    /// 음성 인식 자막(whisper.cpp, 이 PC에서만 — 소리를 인터넷으로 보내지 않는다).
    /// 실행 파일은 GitHub ggml-org/whisper.cpp 릴리스(SHA-256 digest 확인), 모델은 Hugging Face ggerganov/whisper.cpp(고정한 SHA-256 확인)에서 받는다.
    /// 인식: FFmpeg로 16kHz 모노 WAV를 만들고 whisper-cli로 SRT를 써서 읽는다.
    /// </summary>
    public static class SpeechRecognizer
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);

        /// <summary>folder(데이터 폴더\whisper)에 whisper.cpp를 받아 풀고 실행해 본다(verify — 시험에서만 끔).</summary>
        public static async Task<WhisperTool> DownloadTool(string folder, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation,
                                                           HttpMessageHandler handler = null, bool verify = true)
        {
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                using (var http = GitHubDownload.Client(handler))
                {
                    progress?.Report(new FfmpegDownloader.Status { Stage = "whisper.cpp 릴리스 정보 확인" });
                    var json = await GitHubDownload.GetText(http, SpeechText.ReleasesApi, cancellation).ConfigureAwait(false);
                    var asset = SpeechText.ChooseRelease(json, SpeechText.BinaryAsset)
                                ?? throw new InvalidOperationException("내려받을 whisper.cpp 파일(" + SpeechText.BinaryAsset + ")을 GitHub 릴리스에서 찾지 못했습니다.");
                    Directory.CreateDirectory(folder);
                    var zip = Path.Combine(folder, "whisper.zip.part");
                    try
                    {
                        await GitHubDownload.Save(http, asset.Url, asset.Size, asset.Sha256, zip, "내려받는 중 — whisper.cpp", progress, cancellation).ConfigureAwait(false);
                        progress?.Report(new FfmpegDownloader.Status { Stage = "압축 푸는 중" });
                        Extract(zip, folder);
                    }
                    finally
                    {
                        RenderCommand.TryDelete(zip);
                    }
                }
            }
            finally
            {
                Gate.Release();
            }
            var tool = WhisperTool.Locate(folder) ?? throw new InvalidDataException("내려받은 묶음에 " + SpeechText.CliName + "가 없습니다.");
            if (verify)
                await Check(tool, cancellation).ConfigureAwait(false);
            return tool;
        }

        /// <summary>whisper-cli.exe와 DLL만 folder에 푼다(묶음 안 폴더는 무시, 다 푼 뒤 바꿔 넣음).</summary>
        public static void Extract(string zipPath, string folder)
        {
            var extracted = new List<string>();
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries.Where(e => SpeechText.ShouldExtract(e.FullName)))
                {
                    var target = Path.Combine(folder, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(target + ".new", true);
                    extracted.Add(target);
                }
            }
            if (!extracted.Any(p => Path.GetFileName(p).Equals(SpeechText.CliName, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var path in extracted)
                    RenderCommand.TryDelete(path + ".new");
                throw new InvalidDataException("내려받은 묶음에 " + SpeechText.CliName + "가 없습니다.");
            }
            foreach (var path in extracted)
                File.Move(path + ".new", path, true);
        }

        /// <summary>whisper-cli --version을 돌려 본다. Visual C++ 런타임이 없으면 무엇을 설치할지 알린다.</summary>
        public static async Task Check(WhisperTool tool, CancellationToken cancellation)
        {
            FfmpegRunner.TextResult result;
            try
            {
                result = await FfmpegRunner.RunText(tool.CliPath, new[] { "--version" }, TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("whisper.cpp를 실행하지 못했습니다: " + ex.Message, ex);
            }
            if (result.ExitCode == SpeechText.DllNotFound)
                throw new InvalidOperationException(MissingRuntime);
            if (result.ExitCode != 0)
                throw new FfmpegException("whisper.cpp를 실행하지 못했습니다(" + result.ExitCode + ").", result.Error);
        }

        public const string MissingRuntime = "whisper.cpp에 필요한 Microsoft Visual C++ 재배포 패키지(x64)가 이 PC에 없습니다. "
                                             + "설치한 뒤 다시 시도하세요: " + SpeechText.VcRedistUrl;

        /// <summary>모델을 folder\models에 받는다(받아 두었으면 그대로). 고정한 크기·SHA-256이 맞아야 쓴다.</summary>
        public static async Task<string> DownloadModel(string folder, SpeechModel model, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            var final = WhisperTool.ModelPath(folder, model);
            if (WhisperTool.HasModel(folder, model))
                return final;
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(WhisperTool.ModelFolder(folder));
                var part = final + ".part";
                try
                {
                    using (var http = GitHubDownload.Client(handler))
                        await GitHubDownload.Save(http, model.Url, model.Size, model.Sha256, part, "내려받는 중 — 음성 인식 모델(" + model.Label + ")", progress, cancellation).ConfigureAwait(false);
                    File.Move(part, final, true);
                    return final;
                }
                finally
                {
                    RenderCommand.TryDelete(part);
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// 클립 하나(내레이션 등)의 소리를 인식해 타임라인 시각의 큐로. 원본의 클립 구간만 읽고, 속도를 바꾼 클립은 시각을 나눠 맞춘다.
        /// </summary>
        public static Task<List<SubtitleCue>> RecognizeClip(FfmpegTool ffmpeg, WhisperTool whisper, SpeechOptions options, Clip clip, FrameRate rate,
                                                            IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            var from = rate.ToSeconds(clip.SourceIn);
            var length = rate.ToSeconds(clip.SourceOut - clip.SourceIn);
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-ss", RenderGraph.N(from), "-t", RenderGraph.N(length),
                "-protocol_whitelist", "file", "-i", clip.Path, "-vn" };
            return Recognize(ffmpeg, whisper, options, wav => { args.AddRange(WavArgs(wav)); return args; }, null,
                rate.ToSeconds(clip.Start), clip.Speed, rate.ToSeconds(clip.End), progress, cancellation);
        }

        /// <summary>타임라인 전체 소리(모든 트랙을 섞은 것)를 인식한다 — 음악이 섞이면 정확도가 떨어진다.</summary>
        public static Task<List<SubtitleCue>> RecognizeTimeline(FfmpegTool ffmpeg, WhisperTool whisper, SpeechOptions options, Project project,
                                                                IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = project.Duration, Width = 16, Height = 16, Video = false });
            string script = null;
            return Recognize(ffmpeg, whisper, options,
                wav => RenderCommand.Arguments(ffmpeg, plan, new[] { "-map", "[aout]" }.Concat(WavArgs(wav)), out script),
                () =>
                {
                    RenderCommand.TryDelete(script);
                    foreach (var file in plan.TextFiles.Keys)
                        RenderCommand.TryDelete(file);
                },
                0, 1, project.Rate.ToSeconds(project.Duration), progress, cancellation);
        }

        private static IEnumerable<string> WavArgs(string wav)
        {
            return new[] { "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", wav };
        }

        private static async Task<List<SubtitleCue>> Recognize(FfmpegTool ffmpeg, WhisperTool whisper, SpeechOptions options, Func<string, IEnumerable<string>> extract,
                                                               Action cleanup, double start, double speed, double end,
                                                               IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            // 임시 파일 이름에 클립·프로젝트 값을 쓰지 않는다
            var name = "fv-speech-" + Guid.NewGuid().ToString("N");
            var wav = Path.Combine(RenderEnvironment.TempFolder(), name + ".wav");
            var output = Path.Combine(RenderEnvironment.TempFolder(), name);
            try
            {
                progress?.Report(new FfmpegDownloader.Status { Stage = "소리 꺼내는 중" });
                var converted = await FfmpegRunner.RunText(ffmpeg.FfmpegPath, extract(wav), TimeSpan.FromMinutes(30), cancellation).ConfigureAwait(false);
                if (converted.ExitCode != 0 || !File.Exists(wav))
                    throw new FfmpegException("소리를 꺼내지 못했습니다.", converted.Error);
                progress?.Report(new FfmpegDownloader.Status { Stage = "음성 인식 중", Done = 0, Total = 100 });
                var args = SpeechText.Arguments(options.Model, wav, output, options.Language, SpeechText.Threads(Environment.ProcessorCount), options.Vad, options.Prompt);
                await Run(whisper, args, progress, cancellation).ConfigureAwait(false);
                var srt = output + ".srt";
                if (!File.Exists(srt))
                    throw new InvalidDataException("음성 인식 결과(SRT)가 없습니다.");
                // 단어(어절)마다 한 큐로 받아 타임라인 시각으로 옮긴 뒤 자막 큐로 묶는다(단어 시각은 큐에 남긴다)
                var words = SpeechText.ToTimeline(SubtitleFile.Parse(File.ReadAllText(srt, Encoding.UTF8)), start, speed, end);
                return SpeechText.Group(words);
            }
            finally
            {
                RenderCommand.TryDelete(wav);
                RenderCommand.TryDelete(output + ".srt");
                cleanup?.Invoke();
            }
        }

        /// <summary>whisper-cli를 끝까지 돌린다: 표준 출력(인식한 글)은 버리고 표준 오류에서 진행률을 읽는다. 취소하면 프로세스를 끝낸다.</summary>
        private static async Task Run(WhisperTool whisper, IReadOnlyList<string> args, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            using (var process = FfmpegRunner.Start(whisper.CliPath, args, true))
            using (cancellation.Register(() => FfmpegRunner.Kill(process)))
            {
                var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                var errors = new StringBuilder();
                string line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var percent = SpeechText.ParseProgress(line);
                    if (percent.HasValue)
                        progress?.Report(new FfmpegDownloader.Status { Stage = "음성 인식 중", Done = percent.Value, Total = 100 });
                    else if (errors.Length < 64 * 1024)
                        errors.AppendLine(line);
                }
                await output.ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (process.ExitCode == SpeechText.DllNotFound)
                    throw new InvalidOperationException(MissingRuntime);
                if (process.ExitCode != 0)
                    throw new FfmpegException("음성 인식에 실패했습니다(" + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ").", errors.ToString());
            }
        }
    }
}
