using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>URL에서 가져오기: yt-dlp·Deno 내려받기(가짜 GitHub), 직접 링크 내려받기(가짜 서버), yt-dlp 찾는 순서.</summary>
    public class UrlImportTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-url-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        /// <summary>주소 → 응답 만들기. 요청 주소를 기록한다.</summary>
        private sealed class FakeServer : HttpMessageHandler
        {
            public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new Dictionary<string, Func<HttpResponseMessage>>();
            public List<string> Requested { get; } = new List<string>();

            public void File(string url, byte[] body)
            {
                Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            }

            public void Redirect(string url, string location)
            {
                Routes[url] = () =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                    return response;
                };
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri.ToString();
                Requested.Add(url);
                // GitHub(yt-dlp·Deno)는 플러그인 이름, 직접 링크·썸네일은 브라우저 User-Agent(도구 이름을 403으로 막는 사이트가 있음)
                var agent = request.Headers.UserAgent.ToString();
                if (request.RequestUri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                    Assert.Contains("Folderss-video-editor", agent);
                else
                    Assert.StartsWith("Mozilla/5.0", agent);
                var response = Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        private static string Sha(byte[] data)
        {
            return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        }

        private static string Release(string name, int size, string checksums)
        {
            return "{\"assets\":[{\"name\":\"" + checksums + "\",\"browser_download_url\":\"https://github.com/dl/" + checksums + "\",\"size\":100},"
                   + "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://github.com/dl/" + name + "\",\"size\":" + size + "}]}";
        }

        // ---------- yt-dlp·Deno 내려받기 ----------

        private FakeServer YtDlpServer(byte[] exe, string checksum = null)
        {
            var server = new FakeServer();
            server.File(YtDlpDownloader.ReleaseApi, Encoding.UTF8.GetBytes(Release("yt-dlp.exe", exe.Length, "SHA2-256SUMS")));
            server.File("https://github.com/dl/SHA2-256SUMS", Encoding.UTF8.GetBytes(new string('1', 64) + "  yt-dlp\n" + (checksum ?? Sha(exe)) + "  yt-dlp.exe\n"));
            server.File("https://github.com/dl/yt-dlp.exe", exe);
            return server;
        }

        [Fact]
        public async Task DownloadYtDlp_VerifiesAndReplaces()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "yt-dlp.exe"), "old");
            var exe = Encoding.UTF8.GetBytes("new yt-dlp");
            var path = await YtDlpDownloader.DownloadYtDlp(_folder, null, CancellationToken.None, YtDlpServer(exe));
            Assert.Equal(Path.Combine(_folder, "yt-dlp.exe"), path);
            Assert.Equal("new yt-dlp", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".part"));
        }

        [Fact]
        public async Task DownloadYtDlp_ChecksumMismatch_KeepsOldCopy()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "yt-dlp.exe"), "old");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                YtDlpDownloader.DownloadYtDlp(_folder, null, CancellationToken.None, YtDlpServer(Encoding.UTF8.GetBytes("evil"), new string('0', 64))));
            Assert.Equal("old", File.ReadAllText(Path.Combine(_folder, "yt-dlp.exe")));
            Assert.False(File.Exists(Path.Combine(_folder, "yt-dlp.exe.part")));
        }

        [Fact]
        public async Task DownloadDeno_ExtractsOnlyDenoExe()
        {
            byte[] zip;
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    foreach (var name in new[] { "deno.exe", "../evil.exe", "readme.txt" })
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
                            writer.Write("content of " + name);
                    }
                }
                zip = buffer.ToArray();
            }
            var server = new FakeServer();
            const string asset = YtDlpDownloader.DenoAsset;
            server.File(YtDlpDownloader.DenoReleaseApi, Encoding.UTF8.GetBytes(Release(asset, zip.Length, asset + ".sha256sum")));
            server.File("https://github.com/dl/" + asset + ".sha256sum",
                Encoding.UTF8.GetBytes("\r\nAlgorithm : SHA256\r\nHash      : " + Sha(zip).ToUpperInvariant() + "\r\nPath      : C:\\a\\deno\\target\\release\\" + asset + "\r\n"));
            server.File("https://github.com/dl/" + asset, zip);
            var path = await YtDlpDownloader.DownloadDeno(_folder, null, CancellationToken.None, server);
            Assert.Equal(Path.Combine(_folder, "deno.exe"), path);
            Assert.Equal("content of deno.exe", File.ReadAllText(path));
            Assert.Equal(new[] { "deno.exe" }, Array.ConvertAll(Directory.GetFiles(_folder), Path.GetFileName));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder), "evil.exe")));
        }

        // ---------- 직접 링크 ----------

        private static Task<bool> Yes(string path)
        {
            return Task.FromResult(true);
        }

        [Fact]
        public async Task Direct_FollowsHttpsRedirect_AndUsesContentDispositionName()
        {
            var server = new FakeServer();
            server.Redirect("https://example.com/v.mp4", "/real/v.mp4");
            server.Routes["https://example.com/real/v.mp4"] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) };
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "고양이: 1편.mp4" };
                return response;
            };
            var reports = new List<UrlDownloader.Status>();
            var path = await UrlDownloader.Download(new Uri("https://example.com/v.mp4"), _folder, Yes, new SyncProgress<UrlDownloader.Status>(reports.Add), CancellationToken.None, server);
            Assert.Equal(Path.Combine(_folder, "고양이 1편.mp4"), path);
            Assert.Equal(1000, new FileInfo(path).Length);
            Assert.Single(Directory.GetFiles(_folder));
            Assert.Contains(reports, r => r.Done == 1000 && r.Total == 1000);
        }

        [Fact]
        public async Task Direct_NonMediaName_GetsMp4Extension()
        {
            var server = new FakeServer();
            server.Routes["https://example.com/get.mp4"] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "..\\run.exe" };
                return response;
            };
            var path = await UrlDownloader.Download(new Uri("https://example.com/get.mp4"), _folder, Yes, null, CancellationToken.None, server);
            Assert.Equal(Path.Combine(_folder, "run.exe.mp4"), path);
        }

        [Theory]
        [InlineData("http://example.com/v.mp4")]
        [InlineData("ftp://example.com/v.mp4")]
        public async Task Direct_RejectsRedirectAwayFromHttps(string location)
        {
            var server = new FakeServer();
            server.Redirect("https://example.com/v.mp4", location);
            server.File(location, new byte[10]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UrlDownloader.Download(new Uri("https://example.com/v.mp4"), _folder, Yes, null, CancellationToken.None, server));
            Assert.DoesNotContain(location, server.Requested);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task Direct_TooManyRedirects()
        {
            var server = new FakeServer();
            for (var i = 0; i <= UrlDownloader.MaxRedirects + 1; i++)
                server.Redirect("https://example.com/" + i, "https://example.com/" + (i + 1));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UrlDownloader.Download(new Uri("https://example.com/0"), _folder, Yes, null, CancellationToken.None, server));
            Assert.Equal(UrlDownloader.MaxRedirects + 1, server.Requested.Count);
        }

        [Fact]
        public async Task Direct_SizeLimit_LeavesNothing()
        {
            var server = new FakeServer();
            server.File("https://example.com/big.mp4", new byte[5000]);
            // 크기를 알려 주지 않는 응답(스트림)도 받는 도중에 멈춘다
            server.Routes["https://example.com/stream.mp4"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[300000])) };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UrlDownloader.Download(new Uri("https://example.com/big.mp4"), _folder, Yes, null, CancellationToken.None, server, 1000));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UrlDownloader.Download(new Uri("https://example.com/stream.mp4"), _folder, Yes, null, CancellationToken.None, server, 100000));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task Direct_NotMedia_DeletesFile()
        {
            var server = new FakeServer();
            server.File("https://example.com/page.mp4", Encoding.UTF8.GetBytes("<html>login</html>"));
            string checkedPath = null;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                UrlDownloader.Download(new Uri("https://example.com/page.mp4"), _folder, p => { checkedPath = p; return Task.FromResult(false); }, null, CancellationToken.None, server));
            Assert.Contains("동영상·오디오가 아니", error.Message);
            Assert.Equal(Path.Combine(_folder, "page.mp4"), checkedPath);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task Direct_Cancel_LeavesNoPart()
        {
            var server = new FakeServer();
            var cancel = new CancellationTokenSource();
            server.Routes["https://example.com/slow.mp4"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream(cancel)) };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UrlDownloader.Download(new Uri("https://example.com/slow.mp4"), _folder, Yes, null, cancel.Token, server));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task FetchSmall_OnlyAllowedHostAndSize()
        {
            var server = new FakeServer();
            server.File("https://i.ytimg.com/vi/a/hq.jpg", new byte[100]);
            Assert.Equal(100, (await UrlDownloader.FetchSmall("https://i.ytimg.com/vi/a/hq.jpg", "i.ytimg.com", 1000, CancellationToken.None, server)).Length);
            await Assert.ThrowsAsync<InvalidDataException>(() => UrlDownloader.FetchSmall("https://i.ytimg.com/vi/a/hq.jpg", "i.ytimg.com", 50, CancellationToken.None, server));
            await Assert.ThrowsAsync<ArgumentException>(() => UrlDownloader.FetchSmall("https://evil.example/x.jpg", "i.ytimg.com", 1000, CancellationToken.None, server));
            await Assert.ThrowsAsync<ArgumentException>(() => UrlDownloader.FetchSmall("http://i.ytimg.com/x.jpg", "i.ytimg.com", 1000, CancellationToken.None, server));
        }

        // ---------- yt-dlp 찾는 순서 ----------

        [Fact]
        public void Candidates_ConfiguredFirst_DownloadedLast()
        {
            var old = Environment.GetEnvironmentVariable("FOLDERSS_VE_YTDLP");
            try
            {
                Environment.SetEnvironmentVariable("FOLDERSS_VE_YTDLP", Path.Combine(_folder, "env"));
                var list = YtDlpTool.Candidates(Path.Combine(_folder, "configured"), Path.Combine(_folder, "downloaded"));
                var exe = YtDlpTool.ExeName("yt-dlp");
                Assert.Equal(Path.Combine(_folder, "configured", exe), list[0]);
                Assert.Equal(Path.Combine(_folder, "env", exe), list[1]);
                Assert.Equal(Path.Combine(_folder, "downloaded", exe), list[list.Count - 1]);
                // 실행 파일 경로로 줘도 같은 것
                Assert.Equal(list[0], YtDlpTool.Candidates(Path.Combine(_folder, "configured", exe), null)[0]);
                // 이름이 yt-dlp로 끝나는 폴더는 폴더로 본다
                var folder = Path.Combine(_folder, "yt-dlp");
                Directory.CreateDirectory(folder);
                Assert.Equal(Path.Combine(folder, exe), YtDlpTool.Candidates(folder, null)[0]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FOLDERSS_VE_YTDLP", old);
            }
        }

        /// <summary>읽을 때마다 조금씩 주고, 처음 읽은 뒤 취소를 건다.</summary>
        private sealed class EndlessStream : Stream
        {
            private readonly CancellationTokenSource _cancel;

            public EndlessStream(CancellationTokenSource cancel)
            {
                _cancel = cancel;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _cancel.Cancel();
                return Math.Min(count, 10);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>Progress&lt;T&gt;는 스레드 풀로 넘겨 순서가 섞이므로 바로 부르는 것.</summary>
    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }
}
