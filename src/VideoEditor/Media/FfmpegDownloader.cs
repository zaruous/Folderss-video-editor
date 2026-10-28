using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// FFmpeg가 없을 때 GitHub BtbN/FFmpeg-Builds의 최신 정식 Windows 64비트 빌드를 받아 플러그인 데이터 폴더에 푼다.
    /// HTTPS로만 받고, 같은 릴리스의 checksums.sha256과 SHA-256이 맞을 때만 푼다. zip 안의 bin 폴더 파일만 꺼낸다(경로는 파일 이름만 씀).
    /// </summary>
    public static class FfmpegDownloader
    {
        public const string ReleaseApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
        // 두 창에서 동시에 받으면 같은 임시 파일·폴더를 건드리므로 하나씩
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);

        public sealed class Status
        {
            public string Stage { get; set; }
            public long Done { get; set; }
            public long Total { get; set; }
        }

        /// <summary>target 폴더(예: 데이터 폴더\ffmpeg)에 받아 bin 폴더 경로를 돌려준다.</summary>
        public static Task<string> Download(string target, IProgress<Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            return Download(target, null, zip => Extract(zip, target), progress, cancellation, handler);
        }

        /// <summary>
        /// 미리 보기 도우미가 쓸 공유 DLL만 받는다: 설치된 FFmpeg에 DLL이 없을 때(winget Gyan full_build 등) 그것은 그대로 두고,
        /// 도우미 바인딩과 같은 주 버전(<see cref="PreviewHelper.FfmpegMajor"/>)의 공유 빌드에서 <see cref="PreviewHelper.Libraries"/>만 target\bin에 푼다.
        /// ffmpeg.exe는 받지 않는다 — 가져오기·내보내기는 설치된 FFmpeg로 한다. bin 폴더 경로를 돌려준다.
        /// </summary>
        public static Task<string> DownloadPreviewLibraries(string target, IProgress<Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            return Download(target, PreviewHelper.FfmpegMajor, zip => ExtractLibraries(zip, target), progress, cancellation, handler);
        }

        private static async Task<string> Download(string target, int? major, Func<string, string> extract, IProgress<Status> progress, CancellationToken cancellation,
            HttpMessageHandler handler)
        {
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await DownloadCore(target, major, extract, progress, cancellation, handler).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<string> DownloadCore(string target, int? major, Func<string, string> extract, IProgress<Status> progress, CancellationToken cancellation,
            HttpMessageHandler handler)
        {
            using (var http = GitHubDownload.Client(handler))
            {
                progress?.Report(new Status { Stage = "릴리스 정보 확인" });
                var releaseJson = await GitHubDownload.GetText(http, ReleaseApi, cancellation).ConfigureAwait(false);
                var build = FfmpegText.ChooseBuild(releaseJson, major);
                if (build == null)
                    throw new InvalidOperationException(major == null
                        ? "내려받을 FFmpeg 빌드를 찾지 못했습니다(GitHub BtbN/FFmpeg-Builds 릴리스에 Windows 빌드가 없음)."
                        : "내려받을 FFmpeg " + major + " 공유 빌드를 찾지 못했습니다(GitHub BtbN/FFmpeg-Builds 최신 릴리스에 없음).");
                var checksums = await GitHubDownload.GetText(http, build.ChecksumsUrl, cancellation).ConfigureAwait(false);
                var expected = FfmpegText.FindChecksum(checksums, build.Name);
                if (expected == null)
                    throw new InvalidOperationException("체크섬 목록에 " + build.Name + "가 없습니다.");

                Directory.CreateDirectory(target);
                var zip = Path.Combine(target, "download.zip.part");
                try
                {
                    await GitHubDownload.Save(http, build.Url, build.Size, expected, zip, "내려받는 중 — " + build.Name, progress, cancellation).ConfigureAwait(false);
                    progress?.Report(new Status { Stage = "압축 푸는 중" });
                    return extract(zip);
                }
                finally
                {
                    RenderCommand.TryDelete(zip);
                }
            }
        }

        /// <summary>zip의 "&lt;최상위&gt;/bin/파일"과 LICENSE만 target\bin, target에 푼다. bin 경로를 돌려준다.</summary>
        public static string Extract(string zipPath, string target)
        {
            var ffmpeg = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            return ExtractBin(zipPath, target, name => true, new[] { ffmpeg }, "내려받은 zip에 ffmpeg 실행 파일이 없습니다.");
        }

        /// <summary>zip의 bin에서 미리 보기 도우미의 공유 DLL(<see cref="PreviewHelper.Libraries"/>)과 LICENSE만 target\bin, target에 푼다. 하나라도 없으면 예외.</summary>
        public static string ExtractLibraries(string zipPath, string target)
        {
            return ExtractBin(zipPath, target, name => PreviewHelper.Libraries.Contains(name, StringComparer.OrdinalIgnoreCase), PreviewHelper.Libraries,
                "내려받은 zip에 미리 보기 도우미가 쓸 공유 DLL이 없습니다(" + string.Join(", ", PreviewHelper.Libraries) + ").");
        }

        private static string ExtractBin(string zipPath, string target, Func<string, bool> wanted, string[] required, string missing)
        {
            var bin = Path.Combine(target, "bin");
            var staging = Path.Combine(target, "bin.new");
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var parts = entry.FullName.Replace('\\', '/').Split('/');
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        continue;
                    if (parts.Length == 3 && parts[1] == "bin" && wanted(name))
                        entry.ExtractToFile(Path.Combine(staging, name), true);
                    else if (parts.Length == 2 && name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(Path.Combine(target, name), true);
                }
            }
            if (!required.All(name => File.Exists(Path.Combine(staging, name))))
            {
                Directory.Delete(staging, true);
                throw new InvalidDataException(missing);
            }
            try
            {
                if (Directory.Exists(bin))
                    Directory.Delete(bin, true);
                Directory.Move(staging, bin);
                return bin;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 지금 쓰는 FFmpeg(썸네일·재생)가 bin에서 돌고 있으면 지울 수 없다 — 새 폴더에 두고 그것을 쓴다
                var versioned = Path.Combine(target, "bin-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                Directory.Move(staging, versioned);
                return versioned;
            }
        }
    }
}
