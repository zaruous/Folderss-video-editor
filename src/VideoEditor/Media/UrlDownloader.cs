using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 직접 링크(https://…/video.mp4) 내려받기와 검색 썸네일 받기. https만 쓰고, 리디렉션은 직접 따라가며 매번 https인지 본다(최대 5번).
    /// 크기·시간 한도를 두고, 받는 동안은 ".fvdl-….part"에 쓴 뒤 미디어인지 확인되면 최종 이름으로 바꾼다.
    /// </summary>
    public static class UrlDownloader
    {
        /// <summary>
        /// 직접 링크·썸네일은 브라우저 User-Agent로 받는다. 도구 이름 User-Agent를 403으로 막는 사이트가 흔하다(yt-dlp도 브라우저 값을 씀).
        /// GitHub에서 FFmpeg·yt-dlp·Deno를 받을 때는 플러그인 이름(GitHubDownload.UserAgent) 그대로.
        /// </summary>
        public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        public const int MaxRedirects = 5;
        public const long MaxBytes = 8L * 1024 * 1024 * 1024;
        public static readonly TimeSpan Timeout = TimeSpan.FromHours(2);
        private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(() => NewClient(null));

        public sealed class Status
        {
            public long Done { get; set; }
            public long Total { get; set; }
        }

        private static HttpClient NewClient(HttpMessageHandler handler)
        {
            // 리디렉션은 직접 따라가며 https인지 본다. 프록시는 본체와 같은 방식(시스템 프록시 + Windows 자격 증명)
            var http = new HttpClient(handler ?? HostNetwork.Handler(false), handler == null) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            return http;
        }

        /// <summary>
        /// url을 folder에 받아 최종 경로를 돌려준다. isMedia(경로)가 false면 파일을 지우고 InvalidDataException.
        /// 파일 이름은 Content-Disposition, 없으면 주소의 마지막 부분에서 쓸 수 없는 글자를 뺀 것.
        /// </summary>
        public static async Task<string> Download(Uri url, string folder, Func<string, Task<bool>> isMedia, IProgress<Status> progress,
                                                  CancellationToken cancellation, HttpMessageHandler handler = null, long maxBytes = MaxBytes)
        {
            Directory.CreateDirectory(folder);
            var part = Path.Combine(folder, ".fvdl-" + Guid.NewGuid().ToString("N") + ".part");
            string final = null;
            var http = handler == null ? Shared.Value : NewClient(handler);
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timer.CancelAfter(Timeout);
                try
                {
                    using (var response = await Get(http, url, timer.Token).ConfigureAwait(false))
                    {
                        var total = response.Content.Headers.ContentLength ?? -1;
                        if (total > maxBytes)
                            throw new InvalidDataException("파일이 너무 큽니다(" + (total / 1048576) + "MB, 한도 " + (maxBytes / 1048576) + "MB).");
                        using (var input = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false))
                        using (var output = File.Create(part))
                        {
                            var buffer = new byte[1 << 16];
                            long done = 0, lastReport = 0;
                            int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timer.Token).ConfigureAwait(false)) > 0)
                            {
                                done += read;
                                if (done > maxBytes)
                                    throw new InvalidDataException("파일이 너무 큽니다(한도 " + (maxBytes / 1048576) + "MB).");
                                await output.WriteAsync(buffer, 0, read, timer.Token).ConfigureAwait(false);
                                if (done - lastReport > 512 * 1024 || done == total)
                                {
                                    lastReport = done;
                                    progress?.Report(new Status { Done = done, Total = total });
                                }
                            }
                        }
                        var name = FileName(response, url);
                        final = YtDlpText.UniquePath(folder, YtDlpText.SafeFileName(Path.GetFileNameWithoutExtension(name), "내려받은 동영상"), Path.GetExtension(name));
                    }
                    File.Move(part, final);
                    if (isMedia != null && !await isMedia(final).ConfigureAwait(false))
                        throw new InvalidDataException("받은 파일이 동영상·오디오가 아니라 지웠습니다.");
                    return final;
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    RenderCommand.TryDelete(part);
                    RenderCommand.TryDelete(final);
                    throw new TimeoutException("내려받기가 " + Timeout.TotalHours + "시간 안에 끝나지 않았습니다.");
                }
                catch (Exception)
                {
                    RenderCommand.TryDelete(part);
                    RenderCommand.TryDelete(final);
                    throw;
                }
            }
        }

        /// <summary>작은 파일(썸네일)을 받는다. https이고 host가 allowedHost인 주소만, limit 바이트까지. 리디렉션은 따라가지 않는다.</summary>
        public static async Task<byte[]> FetchSmall(string url, string allowedHost, long limit, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != allowedHost)
                throw new ArgumentException("허용하지 않는 주소입니다: " + url);
            var http = handler == null ? Shared.Value : NewClient(handler);
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timer.CancelAfter(TimeSpan.FromSeconds(20));
                using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if ((response.Content.Headers.ContentLength ?? 0) > limit)
                        throw new InvalidDataException("파일이 너무 큽니다.");
                    using (var input = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        var chunk = new byte[1 << 14];
                        int read;
                        while ((read = await input.ReadAsync(chunk, 0, chunk.Length, timer.Token).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + read > limit)
                                throw new InvalidDataException("파일이 너무 큽니다.");
                            buffer.Write(chunk, 0, read);
                        }
                        return buffer.ToArray();
                    }
                }
            }
        }

        /// <summary>GET을 보내고 리디렉션을 직접 따라간다(https인 곳만, MaxRedirects번까지). 성공 응답을 돌려준다.</summary>
        private static async Task<HttpResponseMessage> Get(HttpClient http, Uri url, CancellationToken cancellation)
        {
            for (var hop = 0; ; hop++)
            {
                if (url.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("https 주소만 받을 수 있습니다: " + url);
                var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code >= 300 && code < 400 && response.Headers.Location != null)
                {
                    var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(url, response.Headers.Location);
                    response.Dispose();
                    if (hop >= MaxRedirects)
                        throw new InvalidOperationException("리디렉션이 너무 많습니다(" + MaxRedirects + "번 넘음).");
                    url = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    throw new HttpRequestException("내려받지 못했습니다: HTTP " + code + (response.StatusCode == HttpStatusCode.NotFound ? " (찾을 수 없음)" : ""));
                }
                return response;
            }
        }

        /// <summary>저장할 이름(확장자 포함): Content-Disposition → 마지막 주소의 파일 이름. 미디어 확장자가 아니면 .mp4를 붙임.</summary>
        private static string FileName(HttpResponseMessage response, Uri requested)
        {
            var disposition = response.Content.Headers.ContentDisposition;
            var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
            if (string.IsNullOrWhiteSpace(name))
                name = Uri.UnescapeDataString(Path.GetFileName((response.RequestMessage?.RequestUri ?? requested).AbsolutePath));
            name = Path.GetFileName(name.Replace('\\', '/'));
            // 미디어 확장자가 아니면(.exe 등) .mp4를 붙인다 — 받은 파일이 실행 파일 이름으로 남지 않게
            return YtDlpText.IsMediaExtension(Path.GetExtension(name)) ? name : name + ".mp4";
        }
    }
}
