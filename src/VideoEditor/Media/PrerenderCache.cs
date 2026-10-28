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
    /// <summary>미리 그릴 전환 하나(<see cref="PrerenderQueue"/>): 캐시 이름, 그릴 계획(멈춘 화면 그래프로 구간 전체), 보일 이름, 장 수, 타임라인 시작 프레임.</summary>
    public sealed class PrerenderJob
    {
        public string Key { get; set; }
        public RenderPlan Plan { get; set; }
        public string Name { get; set; }
        public long Frames { get; set; }
        public long Start { get; set; }
    }

    /// <summary>
    /// 무거운 수식 전환 구간을 미리 그린 캐시(<see cref="TransitionPrerender"/>): 폴더\pr-&lt;해시 64자&gt;.mkv(FFV1 BGRA, 모든 장이 키 프레임 — 무손실).
    /// - 이름(<see cref="Key"/>)은 그 구간을 그리는 계획 전체(필터 그래프·입력 인수·원본 파일의 크기와 고친 시각·글 파일 내용·FFmpeg 판)의 해시다 —
    ///   클립·전환·효과·글자·원본 파일 무엇이 바뀌어도 이름이 달라져 옛 캐시를 쓰지 않는다(따로 지우지 않고 오래 안 쓴 것부터 청소).
    /// - 쓰기는 .part에 그린 뒤 이름을 바꿔 넣고(FFmpeg가 끝까지 성공한 것만), 지우기는 이름 패턴이 맞는 파일만 한다.
    /// - 크기 상한(<see cref="MaxBytes"/>)을 넘으면 오래 안 쓴 것부터 지운다(5분 안에 쓴 것은 재생이 읽는 중일 수 있어 남긴다).
    /// </summary>
    public sealed class PrerenderCache
    {
        /// <summary>기본 크기 상한: 960×540 전환 1초가 FFV1로 20~40MB, 전체 화질 1080p는 그 네 배.</summary>
        public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

        private static readonly TimeSpan KeepRecent = TimeSpan.FromMinutes(5);
        private static readonly Regex CacheName = new Regex("^pr-[0-9a-f]{64}\\.mkv\\z", RegexOptions.CultureInvariant);
        private static readonly Regex PartName = new Regex("^pr-[0-9a-f]{64}\\.mkv\\.[0-9a-f]{32}\\.part\\z", RegexOptions.CultureInvariant);

        public PrerenderCache(string folder, long maxBytes = DefaultMaxBytes)
        {
            Folder = folder;
            MaxBytes = maxBytes;
        }

        public string Folder { get; }
        public long MaxBytes { get; }

        /// <summary>캐시 파일 경로(있든 없든).</summary>
        public string PathOf(string key)
        {
            return Path.Combine(Folder, "pr-" + key + ".mkv");
        }

        /// <summary>있으면 경로(마지막 사용 시각을 지금으로 — 청소가 지우지 않게), 없으면 null.</summary>
        public string Find(string key)
        {
            var path = PathOf(key);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0)
                    return null;
                try
                {
                    File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // 재생이 읽는 중이면 못 고칠 수 있다 — 캐시는 그대로 쓴다
                }
                return path;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// 구간을 그리는 계획의 캐시 이름(SHA-256 64자). 계획 글에서 그릴 때마다 바뀌는 것(글 파일의 임시 이름)은 내용 해시로, 원본 파일은 전체 경로·크기·고친 시각으로,
        /// 꾸민 글자 PNG는 파일 이름(그림 요청의 해시)으로 바꿔 넣는다. ffmpegVersion이 다르면(다른 FFmpeg) 다른 이름이다.
        /// </summary>
        public static string Key(RenderPlan plan, string ffmpegVersion)
        {
            var filter = plan.Filter ?? "";
            foreach (var file in plan.TextFiles)
                filter = filter.Replace(RenderGraph.FilterValue(file.Key), "text:" + Hash(file.Value ?? ""));
            var text = new StringBuilder("pr1|").Append(ffmpegVersion ?? "").Append('\n').Append(filter).Append('\n');
            foreach (var input in plan.Inputs)
            {
                for (var i = 0; i < input.Count; i++)
                {
                    if (input[i] == "-i" && i + 1 < input.Count)
                        text.Append("-i ").Append(Source(plan, input[++i]));
                    else
                        text.Append(input[i]);
                    text.Append(' ');
                }
                text.Append('\n');
            }
            return Hash(text.ToString());
        }

        private static string Source(RenderPlan plan, string path)
        {
            if (plan.TextImages.ContainsKey(path))
                return "image:" + Path.GetFileName(path);
            try
            {
                var file = new FileInfo(path);
                return "file:" + file.FullName + "|" + (file.Exists ? file.Length + "|" + file.LastWriteTimeUtc.Ticks : "none");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return "file:" + path;
            }
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        /// <summary>
        /// 재생 [from, to)를 width×height로 그릴 때 쓸 미리 그린 구간(있는 캐시)과 미리 그릴 일(없는 캐시, 재생 헤드에서 가까운 차례).
        /// 구간마다 멈춘 화면과 같은 렌더 옵션으로 계획을 만들어 이름을 낸다(파일 크기·시각을 읽으므로 UI 스레드 밖에서 부른다).
        /// </summary>
        public (List<PrerenderedSpan> Spans, List<PrerenderJob> Jobs) Survey(FfmpegTool tool, Project project, long from, long to, int width, int height,
            ITextRasterizer textRasterizer)
        {
            var spans = new List<PrerenderedSpan>();
            var jobs = new List<PrerenderJob>();
            foreach (var window in TransitionPrerender.Windows(project, from, to, tool.MajorVersion))
            {
                var plan = RenderGraph.Build(project, StillOptions(tool, window.Start, window.End, width, height, textRasterizer));
                var key = Key(plan, tool.VersionLine);
                var path = Find(key);
                if (path != null)
                    spans.Add(new PrerenderedSpan { ClipId = window.Clip.Id, Start = window.Start, End = window.End, Path = path });
                else
                    jobs.Add(new PrerenderJob { Key = key, Plan = plan, Name = window.Transition.Name, Frames = window.End - window.Start, Start = window.Start });
            }
            return (spans, jobs);
        }

        /// <summary>멈춘 화면(<see cref="PreviewRenderer.Frame"/>)과 같은 렌더 옵션으로 [from, to) — 재생 축소 없이 그리므로 미리 보기·내보내기와 같은 그림이다.</summary>
        public static RenderOptions StillOptions(FfmpegTool tool, long from, long to, int width, int height, ITextRasterizer textRasterizer)
        {
            return new RenderOptions
            {
                From = from,
                To = to,
                Width = width,
                Height = height,
                Audio = false,
                Preview = true,
                FontFile = RenderEnvironment.DefaultFont(),
                TempFolder = RenderEnvironment.TempFolder(),
                FfmpegMajor = tool.MajorVersion,
                TextRasterizer = textRasterizer,
                PersonMasks = RenderEnvironment.PersonMasks
            };
        }

        /// <summary>
        /// 계획을 그려 key 캐시로 둔다(FFV1 BGRA, 모든 장이 키 프레임 — 재생이 구간 도중에서 시작해도 그 장부터 읽는다). 다 그린 장 수를 frames에 알린다.
        /// FFmpeg는 낮은 우선순위로 돌린다(재생과 함께 돌 때 재생을 덜 방해하게). 실패·취소하면 아무것도 남기지 않는다.
        /// </summary>
        public async Task<string> Render(FfmpegTool tool, RenderPlan plan, string key, IProgress<long> frames, CancellationToken cancellation)
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(key);
            var part = path + "." + Guid.NewGuid().ToString("N") + ".part";
            string script = null;
            try
            {
                var args = RenderCommand.Arguments(tool, plan, new[]
                {
                    "-map", "[vout]", "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "bgra",
                    "-progress", "pipe:1", "-nostats", "-f", "matroska", part
                }, out script, cancellation);
                using (var process = FfmpegRunner.Start(tool.FfmpegPath, args, true))
                {
                    LowerPriority(process);
                    var error = process.StandardError.ReadToEndAsync();
                    using (cancellation.Register(() => FfmpegRunner.Kill(process)))
                    {
                        string line;
                        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.Substring(6).Trim(), out var done))
                                frames?.Report(done);
                        }
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new FfmpegException("전환을 미리 그리지 못했습니다.", await error.ConfigureAwait(false));
                }
                try
                {
                    // 덮어쓰지 않는다 — 다른 창·실행이 같은 이름을 먼저 그렸으면 같은 그림이다(재생이 연 파일을 바꾸려다 실패하지 않게)
                    File.Move(part, path, false);
                }
                catch (IOException) when (File.Exists(path))
                {
                }
                Trim(MaxBytes, DateTime.UtcNow);
                return path;
            }
            finally
            {
                RenderCommand.TryDelete(script);
                foreach (var file in plan.TextFiles.Keys)
                    RenderCommand.TryDelete(file);
                RenderCommand.TryDelete(part);
            }
        }

        private static void LowerPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // 이미 끝났거나 권한이 없으면 보통 우선순위로
            }
        }

        /// <summary>캐시가 maxBytes를 넘으면 마지막 사용이 5분 넘은 파일부터 지운다(사용 중이라 못 지우면 건너뜀).</summary>
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

        /// <summary>편집기를 시작할 때(배경): partAge보다 오래된 .part(끊긴 그리기)를 지우고 크기 상한으로 줄인다.</summary>
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

        // 마지막 사용 = 접근·수정 시각 중 늦은 것(찾을 때 접근 시각을 적는다)
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

    /// <summary>미리 그리기 상태(<see cref="PrerenderQueue.Status"/>): 그리는 전환 이름, 그린 장 수·전체 장 수, 그다음에 기다리는 전환 수.</summary>
    public sealed class PrerenderStatus
    {
        public string Name { get; set; }
        public long Done { get; set; }
        public long Total { get; set; }
        public int Waiting { get; set; }
    }

    /// <summary>
    /// 미리 그리기 일 줄(재생 미리 보기 하나에 하나): 한 번에 하나씩 배경에서 그린다. 새로 청하면(<see cref="Request"/>) 기다리던 일을 바꾸고,
    /// 그리던 일이 새 목록에 없으면(그새 편집해 이름이 바뀜 등) 그만둔다. 실패한 이름은 이 줄에서 다시 그리지 않는다(같은 실패를 되풀이하지 않게).
    /// 알림(<see cref="Changed"/>·<see cref="Finished"/>)은 배경 스레드에서 온다.
    /// </summary>
    public sealed class PrerenderQueue : IDisposable
    {
        private readonly PrerenderCache _cache;
        private readonly object _gate = new object();
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);
        private List<PrerenderJob> _waiting = new List<PrerenderJob>();
        private PrerenderJob _running;
        private CancellationTokenSource _runningCancel;
        private FfmpegTool _tool;
        private bool _working;
        private bool _disposed;
        private PrerenderStatus _status;

        public PrerenderQueue(PrerenderCache cache)
        {
            _cache = cache;
        }

        public PrerenderCache Cache { get { return _cache; } }

        /// <summary>지금 그리는 것(없으면 null).</summary>
        public PrerenderStatus Status { get { return _status; } }

        /// <summary>상태가 바뀜(그리기 시작·장 수·끝 — 끝나면 <see cref="Status"/>가 null).</summary>
        public event Action Changed;

        /// <summary>일 하나가 끝남: 그린 일과 실패 까닭(성공이면 null). 그만둔 일은 알리지 않는다.</summary>
        public event Action<PrerenderJob, Exception> Finished;

        /// <summary>기다리는 일을 jobs로 바꾼다(앞에서부터 그림). 그리던 일이 jobs에 없으면 그만둔다.</summary>
        public void Request(FfmpegTool tool, IReadOnlyList<PrerenderJob> jobs)
        {
            lock (_gate)
            {
                if (_disposed || tool == null)
                    return;
                _tool = tool;
                var keys = new HashSet<string>(jobs.Select(j => j.Key), StringComparer.Ordinal);
                _waiting = jobs.Where(j => !_failed.Contains(j.Key) && (_running == null || _running.Key != j.Key))
                    .GroupBy(j => j.Key).Select(g => g.First()).ToList();
                if (_running != null && !keys.Contains(_running.Key))
                    _runningCancel?.Cancel();
                if (!_working && _waiting.Count > 0)
                {
                    _working = true;
                    Task.Run(Work);
                }
            }
        }

        /// <summary>기다리는 일을 비우고 그리던 것을 그만둔다.</summary>
        public void Cancel()
        {
            Request(_tool, Array.Empty<PrerenderJob>());
        }

        private async Task Work()
        {
            while (true)
            {
                PrerenderJob job;
                CancellationTokenSource cancel;
                FfmpegTool tool;
                int waiting;
                lock (_gate)
                {
                    if (_disposed || _waiting.Count == 0)
                    {
                        _working = false;
                        _running = null;
                        _status = null;
                        break;
                    }
                    job = _waiting[0];
                    _waiting.RemoveAt(0);
                    _running = job;
                    cancel = _runningCancel = new CancellationTokenSource();
                    tool = _tool;
                    waiting = _waiting.Count;
                }
                SetStatus(new PrerenderStatus { Name = job.Name, Done = 0, Total = job.Frames, Waiting = waiting });
                Exception error = null;
                var cancelled = false;
                try
                {
                    if (_cache.Find(job.Key) == null)
                    {
                        await _cache.Render(tool, job.Plan, job.Key, new FrameProgress(done =>
                        {
                            int left;
                            lock (_gate)
                                left = _waiting.Count;
                            SetStatus(new PrerenderStatus { Name = job.Name, Done = Math.Min(done, job.Frames), Total = job.Frames, Waiting = left });
                        }), cancel.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    error = ex;
                    lock (_gate)
                        _failed.Add(job.Key);
                }
                finally
                {
                    lock (_gate)
                    {
                        _running = null;
                        _runningCancel = null;
                    }
                    cancel.Dispose();
                }
                if (!cancelled)
                {
                    try
                    {
                        Finished?.Invoke(job, error);
                    }
                    catch (Exception)
                    {
                        // 받는 쪽의 오류로 줄이 멈추지 않게
                    }
                }
            }
            // 상태는 위에서(문 안에서) 비웠다 — 그새 새 일이 상태를 적었으면 덮지 않는다
            RaiseChanged();
        }

        private void SetStatus(PrerenderStatus status)
        {
            _status = status;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // 받는 쪽의 오류로 줄이 멈추지 않게
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _waiting.Clear();
                _runningCancel?.Cancel();
            }
        }

        /// <summary>그린 장 수를 받는 곳(동기 컨텍스트 없이 그 자리에서 부른다).</summary>
        private sealed class FrameProgress : IProgress<long>
        {
            private readonly Action<long> _report;

            public FrameProgress(Action<long> report)
            {
                _report = report;
            }

            public void Report(long value)
            {
                _report(value);
            }
        }
    }
}
