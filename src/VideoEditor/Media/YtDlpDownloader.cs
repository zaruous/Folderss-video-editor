using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// yt-dlp가 없을 때 GitHub yt-dlp/yt-dlp 최신 릴리스의 yt-dlp.exe를, JS 실행기가 없을 때 denoland/deno 최신 릴리스의 Windows zip을 받는다.
    /// FFmpeg 내려받기와 같이 GitHub https 주소만, 릴리스에 적힌 크기 한도 안에서 받고, 같은 릴리스의 체크섬과 SHA-256이 맞을 때만 쓴다.
    /// </summary>
    public static class YtDlpDownloader
    {
        public const string ReleaseApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        public const string DenoReleaseApi = "https://api.github.com/repos/denoland/deno/releases/latest";
        public const string YtDlpAsset = "yt-dlp.exe";
        public const string YtDlpChecksums = "SHA2-256SUMS";
        public const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);

        /// <summary>target 폴더(데이터 폴더\yt-dlp)에 yt-dlp.exe를 받아(있으면 바꿔) 경로를 돌려준다.</summary>
        public static async Task<string> DownloadYtDlp(string target, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                using (var http = GitHubDownload.Client(handler))
                {
                    var asset = await Choose(http, ReleaseApi, YtDlpAsset, YtDlpChecksums, "yt-dlp", progress, cancellation).ConfigureAwait(false);
                    var expected = await Checksum(http, asset, cancellation).ConfigureAwait(false);
                    Directory.CreateDirectory(target);
                    var part = Path.Combine(target, YtDlpAsset + ".part");
                    var final = Path.Combine(target, YtDlpAsset);
                    try
                    {
                        await GitHubDownload.Save(http, asset.Url, asset.Size, expected, part, "내려받는 중 — yt-dlp", progress, cancellation).ConfigureAwait(false);
                        File.Move(part, final, true);
                        return final;
                    }
                    finally
                    {
                        RenderCommand.TryDelete(part);
                    }
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>target 폴더(데이터 폴더\deno)에 Deno zip을 받아 deno.exe만 풀고 그 경로를 돌려준다.</summary>
        public static async Task<string> DownloadDeno(string target, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                using (var http = GitHubDownload.Client(handler))
                {
                    var asset = await Choose(http, DenoReleaseApi, DenoAsset, DenoAsset + ".sha256sum", "Deno", progress, cancellation).ConfigureAwait(false);
                    var expected = await Checksum(http, asset, cancellation).ConfigureAwait(false);
                    Directory.CreateDirectory(target);
                    var zip = Path.Combine(target, "deno.zip.part");
                    try
                    {
                        await GitHubDownload.Save(http, asset.Url, asset.Size, expected, zip, "내려받는 중 — Deno", progress, cancellation).ConfigureAwait(false);
                        progress?.Report(new FfmpegDownloader.Status { Stage = "압축 푸는 중" });
                        return ExtractDeno(zip, target);
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
        }

        private static async Task<GitHubAsset> Choose(HttpClient http, string api, string name, string checksums, string label,
                                                      IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            progress?.Report(new FfmpegDownloader.Status { Stage = label + " 릴리스 정보 확인" });
            var json = await GitHubDownload.GetText(http, api, cancellation).ConfigureAwait(false);
            return YtDlpText.ChooseAsset(json, name, checksums)
                   ?? throw new InvalidOperationException("내려받을 " + label + " 파일(" + name + ")을 GitHub 릴리스에서 찾지 못했습니다.");
        }

        private static async Task<string> Checksum(HttpClient http, GitHubAsset asset, CancellationToken cancellation)
        {
            var text = await GitHubDownload.GetText(http, asset.ChecksumsUrl, cancellation).ConfigureAwait(false);
            return YtDlpText.FindChecksum(text, asset.Name) ?? throw new InvalidOperationException("체크섬 목록에 " + asset.Name + "가 없습니다.");
        }

        /// <summary>zip 맨 위의 deno.exe만 target에 푼다.</summary>
        public static string ExtractDeno(string zipPath, string target)
        {
            const string name = "deno.exe";
            var final = Path.Combine(target, name);
            var part = final + ".new";
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry(name) ?? throw new InvalidDataException("내려받은 zip에 deno.exe가 없습니다.");
                entry.ExtractToFile(part, true);
            }
            File.Move(part, final, true);
            return final;
        }
    }
}
