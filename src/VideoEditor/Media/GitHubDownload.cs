using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Media
{
    /// <summary>GitHub 릴리스 파일 내려받기(FFmpeg·yt-dlp·Deno 공용): 크기 한도를 지키며 받고 SHA-256을 맞춰 본다.</summary>
    internal static class GitHubDownload
    {
        public const string UserAgent = "Folderss-video-editor/0.1 (+https://github.com/zaruous/Folderss-video-editor)";

        public static HttpClient Client(HttpMessageHandler handler)
        {
            var http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromMinutes(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return http;
        }

        public static async Task<string> GetText(HttpClient http, string url, CancellationToken cancellation)
        {
            using (var response = await http.GetAsync(url, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// url을 path에 받는다. 릴리스에 적힌 크기(size)보다 16MB 넘게 크면 멈춘다(잘못된 응답이 디스크를 채우지 않게).
        /// SHA-256이 expected와 다르면 InvalidDataException. path는 실패해도 지우지 않는다(부르는 쪽이 지움).
        /// </summary>
        public static async Task Save(HttpClient http, string url, long size, string expected, string path, string stage,
                                      IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            string actual;
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? size;
                var limit = Math.Max(size, 1L) + 16L * 1024 * 1024;
                if (total > limit)
                    throw new InvalidDataException("내려받을 파일이 예상보다 큽니다(" + total + " 바이트).");
                using (var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
                using (var output = File.Create(path))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    var lastReport = 0L;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, cancellation).ConfigureAwait(false);
                        sha.AppendData(buffer, 0, read);
                        done += read;
                        if (done > limit)
                            throw new InvalidDataException("내려받은 크기가 예상보다 큽니다.");
                        if (done - lastReport > 512 * 1024 || done == total)
                        {
                            lastReport = done;
                            progress?.Report(new FfmpegDownloader.Status { Stage = stage, Done = done, Total = total });
                        }
                    }
                    actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
            }
            if (actual != expected)
                throw new InvalidDataException("내려받은 파일의 SHA-256이 맞지 않습니다. 다시 시도하세요.\n기대 " + expected + "\n실제 " + actual);
        }
    }
}
