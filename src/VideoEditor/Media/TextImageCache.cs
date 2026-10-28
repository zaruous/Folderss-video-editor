using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 꾸민 글자 PNG 캐시(프로세스별 임시 폴더의 styled-text\st-&lt;해시&gt;.png, 단어·글자별 애니메이션의 장은 sa-&lt;묶음 해시&gt;-i00012.png 등). 있으면 다시 그리지 않는다.
    /// 쓰기는 .part에 그린 뒤 옮기고, 지우기는 알려진 이름 패턴의 파일만 — 재귀 삭제·링크 따라가기는 하지 않는다.
    /// </summary>
    public static class TextImageCache
    {
        public const string FolderName = "styled-text";
        /// <summary>실행 중 캐시 용량 상한. 넘으면 한동안 쓰지 않은 파일부터 지운다.</summary>
        public const long MaxBytes = 300L * 1024 * 1024;

        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan KeepRecent = TimeSpan.FromMinutes(5);
        // \z: $는 끝의 줄바꿈 앞에서도 맞는다
        // 애니메이션 장: 나타나기·사라지기 번호(i·o), 쉬는 장(s), 강조 한 번의 차례(w강조-), 강조가 겹친 구간의 장(m시작-)
        private const string Names = "(?:st-[0-9a-f]{64}|sa-[0-9a-f]{64}-(?:[io][0-9]{5}|s|w[0-9]{1,2}-[0-9]{5}|m[0-9]{5}-[0-9]{5}))\\.png";
        private static readonly Regex CacheName = new Regex("^" + Names + "\\z", RegexOptions.CultureInvariant);
        private static readonly Regex PartName = new Regex("^" + Names + "\\.[0-9a-f]{32}\\.part\\z", RegexOptions.CultureInvariant);
        // 같은 경로는 한 번만 그리도록 경로 해시로 나눈 문(경로마다 만들면 끝없이 쌓인다)
        private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        /// <summary>path에 request의 PNG가 있게 한다. 없으면 그리개로 그린다(30초 제한). 같은 경로로 동시에 들어오면 한 번만 그린다.</summary>
        public static void Ensure(ITextRasterizer rasterizer, string path, TextImageRequest request, CancellationToken cancellation)
        {
            Ensure(rasterizer, path, request, cancellation, RenderTimeout);
        }

        internal static void Ensure(ITextRasterizer rasterizer, string path, TextImageRequest request, CancellationToken cancellation, TimeSpan timeout)
        {
            if (rasterizer == null)
                throw new ArgumentNullException(nameof(rasterizer));
            if (string.IsNullOrEmpty(path) || !CacheName.IsMatch(Path.GetFileName(path)))
                throw new ArgumentException("꾸민 글자 캐시 파일 이름이 아닙니다.", nameof(path));
            cancellation.ThrowIfCancellationRequested();
            if (Used(path))
                return;
            var gate = Gates[(StringComparer.OrdinalIgnoreCase.GetHashCode(path) & int.MaxValue) % Gates.Length];
            gate.Wait(cancellation);
            try
            {
                if (Used(path))
                    return;
                var folder = Path.GetDirectoryName(path);
                Directory.CreateDirectory(folder);
                var part = path + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    Render(rasterizer, request, part, cancellation, timeout);
                    var drawn = new FileInfo(part);
                    if (!drawn.Exists || drawn.Length == 0)
                        throw new InvalidOperationException("꾸민 글자 그리개가 PNG를 만들지 않았습니다.");
                    try
                    {
                        // 덮어쓰지 않는다 — FFmpeg가 연 파일을 바꾸려다 실패하지 않게. 이미 있으면 같은 키의 같은 그림이다
                        File.Move(part, path, false);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                    }
                }
                finally
                {
                    RenderCommand.TryDelete(part);
                }
                Trim(folder, MaxBytes, DateTime.UtcNow);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 그리개를 부르고 제한 시간까지만 기다린다. 그리개가 취소 신호를 보지 않아도 넘기면 멈추라고 알리고 돌아온다(문을 계속 쥐지 않게) —
        /// 늦게 끝나면 그때 .part를 지운다. 그리개 자신의 예외는 그대로 던진다.
        /// </summary>
        private static void Render(ITextRasterizer rasterizer, TextImageRequest request, string part, CancellationToken cancellation, TimeSpan timeout)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var render = Task.Run(() => rasterizer.Render(request, part, stop.Token));
            bool done;
            try
            {
                done = render.Wait(timeout, cancellation);
            }
            catch (AggregateException)
            {
                // 그리개가 예외로 끝남 — 아래 GetResult가 원래 예외를 다시 던진다
                done = true;
            }
            catch (OperationCanceledException)
            {
                done = false;
            }
            if (!done)
            {
                stop.Cancel();
                render.ContinueWith(_ =>
                {
                    RenderCommand.TryDelete(part);
                    stop.Dispose();
                }, TaskScheduler.Default);
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException("꾸민 글자를 그리는 데 " + timeout.TotalSeconds + "초가 넘게 걸렸습니다.");
            }
            stop.Dispose();
            render.GetAwaiter().GetResult();
        }

        /// <summary>있으면 마지막 사용 시각을 지금으로 적고 true(NTFS는 접근 시각을 잘 갱신하지 않으므로 직접 적는다).</summary>
        private static bool Used(string path)
        {
            if (!File.Exists(path))
                return false;
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                // 확인한 사이에 지워졌다 — 다시 그린다
                return false;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // FFmpeg가 읽는 중이면 못 고칠 수 있다 — 캐시는 그대로 쓴다
            }
            return true;
        }

        /// <summary>folder의 캐시가 maxBytes를 넘으면, 마지막 사용이 5분 넘은 파일부터 지운다(사용 중이면 건너뜀).</summary>
        internal static void Trim(string folder, long maxBytes, DateTime nowUtc)
        {
            List<FileInfo> files;
            try
            {
                files = CacheFiles(new DirectoryInfo(folder), CacheName).ToList();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            var total = files.Sum(f => f.Length);
            foreach (var file in files.Where(f => nowUtc - f.LastAccessTimeUtc > KeepRecent).OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= maxBytes)
                    break;
                // 지운 뒤에는 FileInfo가 크기를 다시 읽으려다 실패하므로 먼저 적어 둔다
                var length = file.Length;
                if (TryDelete(file))
                    total -= length;
            }
        }

        /// <summary>
        /// 시작할 때 한 번: tempRoot(…\folderss-video-editor) 아래 이미 끝난 프로세스(숫자 pid 폴더)의 styled-text에서
        /// 캐시 이름 패턴의 파일만 지운다. 살아 있는 pid 폴더·하위 폴더·링크는 건드리지 않는다.
        /// </summary>
        public static void Cleanup(string tempRoot)
        {
            if (string.IsNullOrEmpty(tempRoot) || !Directory.Exists(tempRoot))
                return;
            try
            {
                foreach (var process in new DirectoryInfo(tempRoot).EnumerateDirectories().ToList())
                {
                    if (IsLink(process) || !int.TryParse(process.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || IsRunning(pid))
                        continue;
                    var folder = new DirectoryInfo(Path.Combine(process.FullName, FolderName));
                    if (!folder.Exists || IsLink(folder))
                        continue;
                    foreach (var file in CacheFiles(folder, CacheName).Concat(CacheFiles(folder, PartName)).ToList())
                        TryDelete(file);
                    // 비었을 때만 지워진다(재귀 삭제 아님)
                    TryRemoveEmpty(folder);
                    TryRemoveEmpty(process);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 임시 폴더 정리 — 못 해도 다음 시작 때
            }
        }

        /// <summary>folder 바로 아래(하위 폴더 제외)의 pattern에 맞는 보통 파일(링크 제외).</summary>
        private static IEnumerable<FileInfo> CacheFiles(DirectoryInfo folder, Regex pattern)
        {
            if (!folder.Exists)
                return Enumerable.Empty<FileInfo>();
            return folder.EnumerateFiles().Where(f => pattern.IsMatch(f.Name) && !IsLink(f));
        }

        private static bool IsLink(FileSystemInfo info)
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static bool IsRunning(int pid)
        {
            if (pid == Environment.ProcessId)
                return true;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                // 권한 등으로 알 수 없으면 살아 있다고 본다
                return true;
            }
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

        private static void TryRemoveEmpty(DirectoryInfo folder)
        {
            try
            {
                if (!folder.EnumerateFileSystemInfos().Any())
                    folder.Delete(false);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 그새 무언가 생겼거나 사용 중
            }
        }
    }
}
