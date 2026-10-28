using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 동영상 효과의 한 장 그림 캐시(<see cref="IStillImageCache"/>): 임시 폴더\effect-stills\fx-&lt;해시 64자&gt;.pam.
    /// - 이름은 그리는 글(color 소스부터 수식까지 — 크기·프레임 레이트·값이 다 들어 있음)과 형식의 SHA-256이라 효과 값이 바뀌면 다른 그림이다.
    /// - PAM(무압축 — 4K 거리 지도 16MB를 약 10ms에 읽음, PNG는 약 60ms)으로 무손실이라 읽은 값이 수식 값과 같다.
    /// - 렌더 그래프가 없는 그림을 적어 두면(<see cref="RenderPlan.StillImages"/>) 배경에서 하나씩 만든다(<see cref="Request"/>, 낮은 우선순위 ffmpeg.exe) —
    ///   이번 그리기는 수식으로, 다음 그리기부터 파일을 읽는다. 실행끼리 같이 쓰고(.part에 쓰고 이름을 바꿈), 크기 상한을 넘으면 오래 안 쓴 것부터 지운다.
    /// </summary>
    public sealed class StillImageCache : IStillImageCache
    {
        public const string FolderName = "effect-stills";
        /// <summary>기본 크기 상한(4K 쇼크웨이브 하나가 그림 셋에 약 20MB).</summary>
        public const long DefaultMaxBytes = 512L * 1024 * 1024;

        private static readonly TimeSpan KeepRecent = TimeSpan.FromMinutes(5);
        private static readonly Regex CacheName = new Regex("^fx-[0-9a-f]{64}\\.pam\\z", RegexOptions.CultureInvariant);
        private static readonly Regex PartName = new Regex("^fx-[0-9a-f]{64}\\.pam\\.[0-9a-f]{32}\\.part\\z", RegexOptions.CultureInvariant);
        // 형식마다 PAM이 담는 픽셀 형식(같은 값으로 오가는 것)
        private static readonly Dictionary<string, string> PamFormats = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gray"] = "gray",
            ["gray16"] = "gray16be",
            ["gbrp"] = "rgb24",
            ["gbrap"] = "rgba"
        };

        private readonly object _gate = new object();
        private readonly Queue<(string Path, StillImageRequest Request, FfmpegTool Tool)> _queue = new Queue<(string, StillImageRequest, FfmpegTool)>();
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _working;

        public StillImageCache(string folder, long maxBytes = DefaultMaxBytes)
        {
            Folder = folder;
            MaxBytes = maxBytes;
        }

        public string Folder { get; }
        public long MaxBytes { get; }

        /// <summary>그림을 담을 수 있는 형식인지(아니면 늘 수식으로 그린다).</summary>
        public static bool Supports(string format)
        {
            return format != null && PamFormats.ContainsKey(format);
        }

        public string PathOf(string generator, string format)
        {
            using (var sha = SHA256.Create())
            {
                var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes("still1|" + format + "|" + generator))).ToLowerInvariant();
                return Path.Combine(Folder, "fx-" + hash + ".pam");
            }
        }

        public bool Has(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0)
                    return false;
                try
                {
                    File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // FFmpeg가 읽는 중이면 못 고칠 수 있다 — 캐시는 그대로 쓴다
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>없는 그림(경로·그릴 것)을 배경에서 하나씩 만든다. 이미 만드는 중·실패한 것은 건너뛴다. 바로 돌아온다.</summary>
        public void Request(FfmpegTool tool, IReadOnlyDictionary<string, StillImageRequest> missing)
        {
            if (tool == null || missing == null || missing.Count == 0)
                return;
            lock (_gate)
            {
                foreach (var item in missing)
                {
                    if (Supports(item.Value.Format) && !_failed.Contains(item.Key) && _pending.Add(item.Key))
                        _queue.Enqueue((item.Key, item.Value, tool));
                }
                if (_working || _queue.Count == 0)
                    return;
                _working = true;
            }
            Task.Run(Work);
        }

        /// <summary>줄에 남은 것이 없을 때까지 기다린다(시험용). 시간 안에 끝나면 true.</summary>
        public bool WaitIdle(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (!_working)
                        return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }

        private async Task Work()
        {
            while (true)
            {
                (string Path, StillImageRequest Request, FfmpegTool Tool) item;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _working = false;
                        return;
                    }
                    item = _queue.Dequeue();
                }
                try
                {
                    if (!Has(item.Path))
                        await Make(item.Tool, item.Path, item.Request, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 만들지 못한 그림은 이 실행에서 다시 청하지 않는다(늘 수식으로 그린다)
                    lock (_gate)
                        _failed.Add(item.Path);
                }
                finally
                {
                    lock (_gate)
                        _pending.Remove(item.Path);
                }
            }
        }

        /// <summary>
        /// 그림 하나를 path에 만든다: 그리는 글을 FFmpeg로 한 장 그려 PAM으로(.part에 쓰고 이름을 바꿈 — 이미 있으면 그대로). 낮은 우선순위로 돌린다.
        /// path는 이 캐시 폴더의 캐시 이름이어야 한다.
        /// </summary>
        public async Task Make(FfmpegTool tool, string path, StillImageRequest request, CancellationToken cancellation)
        {
            if (!Supports(request.Format))
                throw new ArgumentException("담을 수 없는 그림 형식입니다: " + request.Format, nameof(request));
            if (!CacheName.IsMatch(Path.GetFileName(path)) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("한 장 그림 캐시 파일 이름이 아닙니다.", nameof(path));
            Directory.CreateDirectory(Folder);
            var part = path + "." + Guid.NewGuid().ToString("N") + ".part";
            var script = Path.Combine(RenderEnvironment.TempFolder(), "still-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(script, request.Generator + "[still]", new UTF8Encoding(false));
                var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
                args.AddRange(tool.FilterScriptArgs(script));
                args.AddRange(new[] { "-map", "[still]", "-frames:v", "1", "-c:v", "pam", "-pix_fmt", PamFormats[request.Format], "-update", "1", "-f", "image2", part });
                using (var process = FfmpegRunner.Start(tool.FfmpegPath, args, true))
                {
                    try
                    {
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                    catch (Exception)
                    {
                        // 이미 끝났거나 권한이 없으면 보통 우선순위로
                    }
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    using (cancellation.Register(() => FfmpegRunner.Kill(process)))
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    await output.ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new FfmpegException("효과 그림을 만들지 못했습니다.", await error.ConfigureAwait(false));
                }
                var made = new FileInfo(part);
                if (!made.Exists || made.Length == 0)
                    throw new InvalidOperationException("FFmpeg가 효과 그림을 만들지 않았습니다.");
                try
                {
                    // 덮어쓰지 않는다 — FFmpeg가 연 파일을 바꾸려다 실패하지 않게. 이미 있으면 같은 그림이다
                    File.Move(part, path, false);
                }
                catch (IOException) when (File.Exists(path))
                {
                }
                Trim(MaxBytes, DateTime.UtcNow);
            }
            finally
            {
                RenderCommand.TryDelete(script);
                RenderCommand.TryDelete(part);
            }
        }

        /// <summary>캐시가 maxBytes를 넘으면 마지막 사용이 5분 넘은 파일부터 지운다(사용 중이면 건너뜀).</summary>
        internal void Trim(long maxBytes, DateTime nowUtc)
        {
            List<FileInfo> files;
            try
            {
                files = Files(CacheName).ToList();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            var total = files.Sum(f => f.Length);
            foreach (var file in files.Where(f => nowUtc - Used(f) > KeepRecent).OrderBy(Used))
            {
                if (total <= maxBytes)
                    break;
                var length = file.Length;
                if (TryDelete(file))
                    total -= length;
            }
        }

        /// <summary>편집기를 시작할 때(배경): partAge보다 오래된 .part를 지우고 크기 상한으로 줄인다.</summary>
        public void Cleanup(TimeSpan partAge)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var part in Files(PartName).Where(f => now - f.LastWriteTimeUtc > partAge).ToList())
                    TryDelete(part);
                Trim(MaxBytes, now);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 캐시 정리 — 못 해도 다음 시작 때
            }
        }

        private static DateTime Used(FileInfo file)
        {
            return file.LastAccessTimeUtc > file.LastWriteTimeUtc ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;
        }

        /// <summary>폴더 바로 아래(하위 폴더 제외)의 pattern에 맞는 보통 파일(링크 제외).</summary>
        private IEnumerable<FileInfo> Files(Regex pattern)
        {
            var folder = new DirectoryInfo(Folder);
            if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                return Enumerable.Empty<FileInfo>();
            return folder.EnumerateFiles().Where(f => pattern.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0);
        }

        private static bool TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
