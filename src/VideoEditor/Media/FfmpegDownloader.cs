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
        public static async Task<string> Download(string target, IProgress<Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await DownloadCore(target, progress, cancellation, handler).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<string> DownloadCore(string target, IProgress<Status> progress, CancellationToken cancellation, HttpMessageHandler handler)
        {
            using (var http = GitHubDownload.Client(handler))
            {
                progress?.Report(new Status { Stage = "릴리스 정보 확인" });
                var releaseJson = await GitHubDownload.GetText(http, ReleaseApi, cancellation).ConfigureAwait(false);
                var build = FfmpegText.ChooseBuild(releaseJson);
                if (build == null)
                    throw new InvalidOperationException("내려받을 FFmpeg 빌드를 찾지 못했습니다(GitHub BtbN/FFmpeg-Builds 릴리스에 Windows 빌드가 없음).");
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
                    return Extract(zip, target);
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
                    if (parts.Length == 3 && parts[1] == "bin")
                        entry.ExtractToFile(Path.Combine(staging, name), true);
                    else if (parts.Length == 2 && name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(Path.Combine(target, name), true);
                }
            }
            if (!File.Exists(Path.Combine(staging, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")))
                throw new InvalidDataException("내려받은 zip에 ffmpeg 실행 파일이 없습니다.");
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
