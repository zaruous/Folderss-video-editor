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
    /// 찾은 yt-dlp와 함께 쓸 JS 실행기(Deno → Node). 찾는 순서는 FFmpeg와 같이 설치된 것이 먼저:
    /// 설정한 경로 → FOLDERSS_VE_YTDLP → PATH → winget·scoop 설치 위치 → 플러그인이 내려받은 폴더.
    /// 인자는 목록으로 넘기고(셸 없음) URL은 "--" 뒤에 둔다. 사용자 yt-dlp 설정 파일은 읽지 않는다(--ignore-config).
    /// </summary>
    public sealed class YtDlpTool
    {
        /// <summary>플러그인이 받은 사본은 이 기간이 지나면 새 릴리스로 바꾼다(유튜브가 바뀌면 금방 낡음).</summary>
        public static readonly TimeSpan RefreshAge = TimeSpan.FromDays(30);

        private YtDlpTool()
        {
        }

        public string Path { get; private set; }
        public string Version { get; private set; }
        /// <summary>플러그인이 내려받은 사본(직접 새로 받을 수 있음). 아니면 사용자 설치본(업데이트 안내만).</summary>
        public bool Downloaded { get; private set; }
        /// <summary>--js-runtimes 값("deno:경로", "node:경로"). 못 찾으면 null.</summary>
        public string JsRuntime { get; private set; }

        public bool IsStale
        {
            get
            {
                try
                {
                    return Downloaded && DateTime.UtcNow - File.GetLastWriteTimeUtc(Path) > RefreshAge;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static string ExeName(string name)
        {
            return OperatingSystem.IsWindows() ? name + ".exe" : name;
        }

        /// <summary>yt-dlp 실행 파일(또는 그것이 있는 폴더)을 열어 버전을 읽는다. 쓸 수 없으면 null.</summary>
        public static YtDlpTool TryOpen(string fileOrFolder, string downloadedFolder = null, string denoFolder = null)
        {
            if (string.IsNullOrWhiteSpace(fileOrFolder))
                return null;
            try
            {
                var path = Directory.Exists(fileOrFolder) ? System.IO.Path.Combine(fileOrFolder, ExeName("yt-dlp")) : fileOrFolder;
                if (!File.Exists(path))
                    return null;
                path = System.IO.Path.GetFullPath(path);
                var version = FfmpegRunner.RunText(path, new[] { "--ignore-config", "--version" }, TimeSpan.FromSeconds(30), CancellationToken.None, Configure).GetAwaiter().GetResult();
                var line = (version.Output ?? "").Trim().Split('\n')[0].Trim();
                if (version.ExitCode != 0 || line.Length == 0)
                    return null;
                return new YtDlpTool
                {
                    Path = path,
                    Version = line,
                    Downloaded = downloadedFolder != null && IsInside(path, downloadedFolder),
                    JsRuntime = FindJsRuntime(denoFolder)
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsInside(string path, string folder)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                return System.IO.Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>찾을 곳(yt-dlp 실행 파일 경로, 순서대로, 중복 없이).</summary>
        public static List<string> Candidates(string configured, string downloadedFolder)
        {
            var list = new List<string>();
            void Add(string fileOrFolder)
            {
                if (string.IsNullOrWhiteSpace(fileOrFolder))
                    return;
                var path = fileOrFolder.Trim().Trim('"');
                // 폴더면 그 안의 yt-dlp, 아니면 실행 파일 경로로 본다(이름이 yt-dlp로 끝나는 폴더도 폴더로)
                if (Directory.Exists(path) || !path.EndsWith(ExeName("yt-dlp"), StringComparison.OrdinalIgnoreCase))
                    path = System.IO.Path.Combine(path, ExeName("yt-dlp"));
                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
                    list.Add(path);
            }
            Add(configured);
            Add(Environment.GetEnvironmentVariable("FOLDERSS_VE_YTDLP"));
            foreach (var folder in PathFolders())
                Add(folder);
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Add(System.IO.Path.Combine(local, "Microsoft", "WinGet", "Links"));
                // winget이 yt-dlp.yt-dlp를 푼 폴더(PATH가 아직 Folderss에 반영되지 않았을 때)
                var packages = System.IO.Path.Combine(local, "Microsoft", "WinGet", "Packages");
                try
                {
                    if (Directory.Exists(packages))
                    {
                        foreach (var package in Directory.EnumerateDirectories(packages, "yt-dlp*"))
                            Add(package);
                    }
                }
                catch (Exception)
                {
                    // 권한 없는 폴더는 건너뜀
                }
                Add(System.IO.Path.Combine(home, "scoop", "shims"));
            }
            Add(downloadedFolder);
            return list;
        }

        public static YtDlpTool Locate(string configured, string downloadedFolder, string denoFolder)
        {
            foreach (var path in Candidates(configured, downloadedFolder))
            {
                if (!File.Exists(path))
                    continue;
                var tool = TryOpen(path, downloadedFolder, denoFolder);
                if (tool != null)
                    return tool;
            }
            return null;
        }

        /// <summary>PATH의 절대 경로 폴더들(상대 경로는 작업 폴더에 따라 달라지므로 뺀다).</summary>
        private static IEnumerable<string> PathFolders()
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
                .Select(f => f.Trim().Trim('"'))
                .Where(System.IO.Path.IsPathRooted);
        }

        /// <summary>
        /// 유튜브에 쓸 JS 실행기(yt-dlp 2025.11부터 권장): Deno(플러그인이 받은 것 → PATH → ~/.deno/bin → winget) → Node(PATH → Program Files).
        /// 실행해 보지는 않는다(프로세스 시작이 느린 PC가 있음). 없으면 null.
        /// </summary>
        public static string FindJsRuntime(string denoFolder)
        {
            var deno = new List<string>();
            if (!string.IsNullOrEmpty(denoFolder))
                deno.Add(denoFolder);
            deno.AddRange(PathFolders());
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                deno.Add(System.IO.Path.Combine(home, ".deno", "bin"));
            if (OperatingSystem.IsWindows())
                deno.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
            var found = deno.Select(f => System.IO.Path.Combine(f, ExeName("deno"))).FirstOrDefault(File.Exists);
            if (found != null)
                return "deno:" + found;
            var node = PathFolders().ToList();
            if (OperatingSystem.IsWindows())
                node.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
            found = node.Select(f => System.IO.Path.Combine(f, ExeName("node"))).FirstOrDefault(File.Exists);
            return found != null ? "node:" + found : null;
        }

        /// <summary>yt-dlp 출력은 UTF-8로 받는다(한글 제목이 깨지지 않게).</summary>
        private static void Configure(ProcessStartInfo info)
        {
            info.StandardOutputEncoding = Encoding.UTF8;
            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["PYTHONIOENCODING"] = "utf-8";
        }

        private IEnumerable<string> CommonArgs()
        {
            var args = new List<string> { "--ignore-config", "--encoding", "utf-8" };
            if (JsRuntime != null)
                args.AddRange(new[] { "--js-runtimes", JsRuntime });
            return args;
        }

        /// <summary>검색어로 유튜브를 검색한다(영상은 받지 않음, 결과 count개까지). HasMore: 요청한 수만큼 와서 더 있을 수 있음.</summary>
        public async Task<(List<YtSearchResult> Results, bool HasMore)> Search(string query, YtSearchOptions options, int count, CancellationToken cancellation)
        {
            var args = CommonArgs().Concat(new[] { "--flat-playlist", "-J", "--playlist-end", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--", YtDlpText.SearchUrl(query, options) });
            var result = await FfmpegRunner.RunText(Path, args, TimeSpan.FromSeconds(90), cancellation, Configure).ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                throw new YtDlpException(YtDlpText.Explain(result.Error));
            var results = YtDlpText.ParseSearch(result.Output, out var entries);
            return (results, entries >= count);
        }

        /// <summary>영상 하나의 제목·길이·화질(yt-dlp -J). 재생목록 주소는 거절한다.</summary>
        public async Task<YtVideoInfo> Info(string url, CancellationToken cancellation)
        {
            var args = CommonArgs().Concat(new[] { "-J", "--no-playlist", "--", url });
            var result = await FfmpegRunner.RunText(Path, args, TimeSpan.FromSeconds(120), cancellation, Configure).ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                throw new YtDlpException(YtDlpText.Explain(result.Error));
            var info = YtDlpText.ParseVideo(result.Output);
            if (info.IsPlaylist)
                throw new YtDlpException(new YtDlpError("재생목록 주소입니다. 영상 하나의 주소를 넣으세요.", false));
            return info;
        }

        /// <summary>
        /// 영상을 folder에 받는다(maxHeight 이하 영상 + 소리 → FFmpeg로 mp4 하나). 받는 동안은 ".fvdl-…" 임시 이름을 쓰고,
        /// 끝나면 제목으로 바꾼 최종 경로를 돌려준다. 취소·실패하면 프로세스를 끝내고 임시 파일을 지운다.
        /// </summary>
        public async Task<string> Download(string url, int maxHeight, string ffmpegFolder, string folder, IProgress<double> progress, CancellationToken cancellation)
        {
            Directory.CreateDirectory(folder);
            folder = System.IO.Path.GetFullPath(folder);
            var prefix = ".fvdl-" + Guid.NewGuid().ToString("N");
            var args = CommonArgs().ToList();
            args.AddRange(new[]
            {
                "--no-playlist", "--newline", "--progress", "--progress-template", YtDlpText.ProgressTemplate,
                "-f", YtDlpText.FormatSelector(maxHeight), "--merge-output-format", "mp4",
                // 폴더는 -P로(경로에 %가 있어도 출력 템플릿으로 읽히지 않게)
                "-P", folder, "-o", prefix + ".%(ext)s",
                "--print", "after_move:" + YtDlpText.FilePrefix + "%(filepath)s",
                "--print", "after_move:" + TitlePrefix + "%(title)s"
            });
            if (!string.IsNullOrEmpty(ffmpegFolder))
                args.AddRange(new[] { "--ffmpeg-location", ffmpegFolder });
            args.AddRange(new[] { "--", url });
            string file = null, title = null;
            try
            {
                using (var process = FfmpegRunner.Start(Path, args, true, false, Configure))
                using (cancellation.Register(() => FfmpegRunner.Kill(process)))
                {
                    var error = process.StandardError.ReadToEndAsync();
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        var percent = YtDlpText.ParseProgress(line);
                        if (percent != null)
                            progress?.Report(percent.Value);
                        else if (line.StartsWith(YtDlpText.FilePrefix, StringComparison.Ordinal))
                            file = line.Substring(YtDlpText.FilePrefix.Length).Trim();
                        else if (line.StartsWith(TitlePrefix, StringComparison.Ordinal))
                            title = line.Substring(TitlePrefix.Length).Trim();
                    }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    var errorText = await error.ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        throw new YtDlpException(YtDlpText.Explain(errorText));
                }
                if (file == null || !File.Exists(file) || !string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file)), folder, StringComparison.OrdinalIgnoreCase)
                    || !System.IO.Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
                    throw new YtDlpException(new YtDlpError("yt-dlp가 받은 파일을 찾지 못했습니다.", false));
                var final = YtDlpText.UniquePath(folder, YtDlpText.SafeFileName(title), System.IO.Path.GetExtension(file));
                File.Move(file, final);
                return final;
            }
            catch (Exception)
            {
                await Cleanup(folder, prefix).ConfigureAwait(false);
                throw;
            }
        }

        private const string TitlePrefix = "FVTITLE ";

        /// <summary>임시 이름(prefix로 시작)의 파일을 지운다. 끝낸 프로세스가 파일을 바로 놓지 않을 수 있어 몇 번 다시 해 본다.</summary>
        private static async Task Cleanup(string folder, string prefix)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                string[] left;
                try
                {
                    left = Directory.GetFiles(folder, prefix + "*");
                }
                catch (Exception)
                {
                    return;
                }
                foreach (var path in left)
                    RenderCommand.TryDelete(path);
                if (left.All(p => !File.Exists(p)))
                    return;
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
    }

    /// <summary>yt-dlp가 실패함 — 알기 쉬운 메시지와, 새 yt-dlp로 나아질 수 있는지.</summary>
    public sealed class YtDlpException : Exception
    {
        public YtDlpException(YtDlpError error) : base(error.Message)
        {
            Outdated = error.Outdated;
        }

        public bool Outdated { get; }
    }
}
