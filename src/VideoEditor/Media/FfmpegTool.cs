using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 찾은 FFmpeg(ffmpeg + 같은 폴더의 ffprobe)와 그 버전·인코더. 찾는 순서는 설치된 것이 먼저:
    /// 설정한 경로 → FOLDERSS_VE_FFMPEG → PATH → winget·scoop·choco 등 흔한 설치 위치 → 플러그인이 내려받은 폴더.
    /// </summary>
    public sealed class FfmpegTool
    {
        private FfmpegTool()
        {
        }

        public string Folder { get; private set; }
        public string FfmpegPath { get; private set; }
        public string FfprobePath { get; private set; }
        public int MajorVersion { get; private set; }
        public string VersionLine { get; private set; }
        public HashSet<string> Encoders { get; private set; }
        /// <summary>쓸 수 있는 필터(못 읽었으면 null — 있다고 본다).</summary>
        public HashSet<string> Filters { get; private set; }

        private static string ExeName(string name)
        {
            return OperatingSystem.IsWindows() ? name + ".exe" : name;
        }

        /// <summary>폴더(또는 그 안의 ffmpeg 실행 파일 경로)를 열어 버전·인코더를 읽는다. 쓸 수 없으면 null.</summary>
        public static FfmpegTool TryOpen(string folderOrExe)
        {
            if (string.IsNullOrWhiteSpace(folderOrExe))
                return null;
            try
            {
                var folder = File.Exists(folderOrExe) ? Path.GetDirectoryName(Path.GetFullPath(folderOrExe)) : folderOrExe;
                var ffmpeg = Path.Combine(folder, ExeName("ffmpeg"));
                var ffprobe = Path.Combine(folder, ExeName("ffprobe"));
                if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
                    return null;
                var version = FfmpegRunner.RunText(ffmpeg, new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();
                if (version.ExitCode != 0)
                    return null;
                // amix normalize 등 FFmpeg 5 이상 기능을 쓴다(못 읽은 버전 0은 개발판일 수 있어 허용)
                var major = FfmpegText.MajorVersion(version.Output);
                if (major > 0 && major < 5)
                    return null;
                var encoders = FfmpegRunner.RunText(ffmpeg, new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();
                return new FfmpegTool
                {
                    Folder = folder,
                    FfmpegPath = ffmpeg,
                    FfprobePath = ffprobe,
                    VersionLine = version.Output.Split('\n')[0].Trim(),
                    MajorVersion = FfmpegText.MajorVersion(version.Output),
                    Encoders = FfmpegText.Encoders(encoders.Output),
                    Filters = ReadFilters(ffmpeg)
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>찾을 곳(순서대로, 중복 없이).</summary>
        public static List<string> Candidates(string configured, string downloadedFolder)
        {
            var list = new List<string>();
            void Add(string folder)
            {
                if (!string.IsNullOrWhiteSpace(folder) && !list.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    list.Add(folder);
            }
            Add(configured);
            // FOLDERSS_VE_FFMPEG_SEARCH=off: 설정한 경로와 내려받은 폴더만 본다(설치본이 여럿일 때 하나로 고정, 화면 시험)
            if (string.Equals(Environment.GetEnvironmentVariable("FOLDERSS_VE_FFMPEG_SEARCH"), "off", StringComparison.OrdinalIgnoreCase))
            {
                Add(downloadedFolder);
                return list;
            }
            Add(Environment.GetEnvironmentVariable("FOLDERSS_VE_FFMPEG"));
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                // 상대 경로(., bin 등)는 작업 폴더에 따라 달라져 엉뚱한 실행 파일을 고를 수 있으므로 건너뛴다
                var entry = folder.Trim().Trim('"');
                if (Path.IsPathRooted(entry))
                    Add(entry);
            }
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
                // winget이 Gyan.FFmpeg를 푼 폴더(PATH가 아직 Folderss에 반영되지 않았을 때)
                var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                try
                {
                    if (Directory.Exists(packages))
                    {
                        foreach (var package in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
                            foreach (var bin in Directory.EnumerateDirectories(package, "bin", SearchOption.AllDirectories).Take(4))
                                Add(bin);
                    }
                }
                catch (Exception)
                {
                    // 권한 없는 폴더는 건너뜀
                }
                Add(Path.Combine(home, "scoop", "shims"));
                Add(Path.Combine(home, "scoop", "apps", "ffmpeg", "current", "bin"));
                // C:\ffmpeg\bin·C:\ProgramData\chocolatey\bin처럼 일반 사용자도 만들 수 있는 폴더는 자동으로 찾지 않는다
                // (여럿이 쓰는 PC에서 다른 사용자가 가짜 ffmpeg.exe를 심어 두면 열자마자 실행됨). 그런 곳은 [직접 지정…]으로.
                Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));
            }
            Add(downloadedFolder);
            return list;
        }

        public static FfmpegTool Locate(string configured, string downloadedFolder)
        {
            foreach (var folder in Candidates(configured, downloadedFolder))
            {
                if (!File.Exists(Path.Combine(folder, ExeName("ffmpeg"))))
                    continue;
                var tool = TryOpen(folder);
                if (tool != null)
                    return tool;
            }
            return null;
        }

        /// <summary>필터 그래프를 파일로 주는 인수(FFmpeg 7부터 -/filter_complex, 그 전은 -filter_complex_script).</summary>
        public IEnumerable<string> FilterScriptArgs(string scriptPath)
        {
            return MajorVersion >= 7 ? new[] { "-/filter_complex", scriptPath } : new[] { "-filter_complex_script", scriptPath };
        }

        public bool HasEncoder(string name)
        {
            return Encoders != null && Encoders.Contains(name);
        }

        /// <summary>필터 목록. 못 읽었거나(시간 초과 등) 비어 있으면 null — 모르는 것으로 둔다(FFmpeg 찾기는 실패시키지 않는다).</summary>
        private static HashSet<string> ReadFilters(string ffmpeg)
        {
            try
            {
                var result = FfmpegRunner.RunText(ffmpeg, new[] { "-hide_banner", "-filters" }, TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();
                var filters = result.ExitCode == 0 ? FfmpegText.Filters(result.Output) : null;
                return filters != null && filters.Count > 0 ? filters : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>필터가 있는지(목록을 못 읽었으면 있다고 본다 — FFmpeg가 직접 알린다).</summary>
        public bool HasFilter(string name)
        {
            return Filters == null || Filters.Contains(name);
        }

        /// <summary>쓸 수 있는 H.264 인코더(자동 선택 순서: libx264 → Windows Media Foundation → mpeg4).</summary>
        public string AutoVideoEncoder()
        {
            foreach (var name in new[] { "libx264", "h264_mf", "libopenh264", "mpeg4" })
            {
                if (HasEncoder(name))
                    return name;
            }
            return "mpeg4";
        }
    }

    /// <summary>FFmpeg 프로세스 실행. 셸을 거치지 않고(ArgumentList) 창 없이, 작업 개체에 넣어 돌린다.</summary>
    public static class FfmpegRunner
    {
        public sealed class TextResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }

        /// <summary>configure: 시작 전에 고칠 것(yt-dlp — 표준 출력 인코딩·환경 변수).</summary>
        public static Process Start(string exe, IEnumerable<string> args, bool readOutput, bool writeInput = false, Action<ProcessStartInfo> configure = null)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = readOutput,
                RedirectStandardError = true,
                RedirectStandardInput = writeInput,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            configure?.Invoke(info);
            var process = Process.Start(info);
            if (process == null)
                throw new InvalidOperationException("FFmpeg를 실행하지 못했습니다: " + exe);
            JobObject.Add(process);
            return process;
        }

        /// <summary>끝날 때까지 돌려 표준 출력·오류를 글로 받는다. 시간을 넘기거나 취소되면 프로세스를 끝내고 예외.</summary>
        public static async Task<TextResult> RunText(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellation, Action<ProcessStartInfo> configure = null)
        {
            using (var process = Start(exe, args, true, false, configure))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timer.CancelAfter(timeout);
                    try
                    {
                        await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Kill(process);
                        cancellation.ThrowIfCancellationRequested();
                        throw new TimeoutException(Path.GetFileName(exe) + "가 " + timeout.TotalSeconds + "초 안에 끝나지 않았습니다.");
                    }
                }
                return new TextResult { ExitCode = process.ExitCode, Output = await output.ConfigureAwait(false), Error = await error.ConfigureAwait(false) };
            }
        }

        /// <summary>끝날 때까지 돌려 표준 출력을 바이트로 받는다(썸네일·한 프레임).</summary>
        public static async Task<byte[]> RunBytes(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellation)
        {
            using (var process = Start(exe, args, true))
            {
                var error = process.StandardError.ReadToEndAsync();
                using (var buffer = new MemoryStream())
                using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timer.CancelAfter(timeout);
                    // 동기 파이프 읽기는 토큰을 보지 못하므로, 취소·시간 초과면 프로세스를 끝내 읽기를 끝낸다
                    using var kill = timer.Token.Register(() => Kill(process));
                    try
                    {
                        await process.StandardOutput.BaseStream.CopyToAsync(buffer, timer.Token).ConfigureAwait(false);
                        await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Kill(process);
                        cancellation.ThrowIfCancellationRequested();
                        throw new TimeoutException("FFmpeg가 " + timeout.TotalSeconds + "초 안에 끝나지 않았습니다.");
                    }
                    if (process.ExitCode != 0)
                        throw new FfmpegException("FFmpeg 오류(" + process.ExitCode + ")", await error.ConfigureAwait(false));
                    return buffer.ToArray();
                }
            }
        }

        public static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (Exception)
            {
                // 이미 끝났거나 끝내는 중
            }
        }
    }

    /// <summary>FFmpeg가 실패함 — 메시지와 FFmpeg 오류 출력 끝부분.</summary>
    public sealed class FfmpegException : Exception
    {
        public FfmpegException(string message, string errorOutput) : base(message + Tail(errorOutput))
        {
            ErrorOutput = errorOutput;
        }

        public string ErrorOutput { get; }

        private static string Tail(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            return "\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 6)));
        }
    }
}
