using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Media;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>실제 yt-dlp가 있을 때만(FOLDERSS_VE_YTDLP 또는 PATH). 유튜브가 아니라 이 PC의 HTTP 시험 서버에서 받는다.</summary>
    public sealed class YtDlpFactAttribute : FactAttribute
    {
        public YtDlpFactAttribute()
        {
            if (YtDlpIntegrationTests.Tool == null)
                Skip = "yt-dlp를 찾지 못해 건너뜁니다(FOLDERSS_VE_YTDLP에 yt-dlp 경로를 주거나 PATH에 두세요).";
        }
    }

    public class YtDlpIntegrationTests : IDisposable
    {
        private static readonly Lazy<YtDlpTool> LazyTool = new Lazy<YtDlpTool>(() => YtDlpTool.Locate(null, null, null));
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-ytdlp-" + Guid.NewGuid().ToString("N"));

        public static YtDlpTool Tool { get { return LazyTool.Value; } }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        /// <summary>/clip.mp4를 주는 HTTP 서버. delay가 있으면 64KB마다 쉬며 천천히 준다.</summary>
        private sealed class Server : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly byte[] _body;
            private readonly TimeSpan _delay;

            public Server(int size, TimeSpan delay)
            {
                _body = new byte[size];
                new Random(1).NextBytes(_body);
                _delay = delay;
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = "http://127.0.0.1:" + port + "/clip.mp4";
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();
                _ = Task.Run(Serve);
            }

            public string Url { get; }

            private async Task Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            context.Response.ContentType = "video/mp4";
                            context.Response.ContentLength64 = _body.Length;
                            if (context.Request.HttpMethod != "HEAD")
                            {
                                for (var offset = 0; offset < _body.Length; offset += 65536)
                                {
                                    await context.Response.OutputStream.WriteAsync(_body, offset, Math.Min(65536, _body.Length - offset));
                                    if (_delay > TimeSpan.Zero)
                                        await Task.Delay(_delay);
                                }
                            }
                            context.Response.Close();
                        }
                        catch (Exception)
                        {
                            // 받는 쪽이 끊음(취소 시험)
                        }
                    });
                }
            }

            public void Dispose()
            {
                _listener.Stop();
            }
        }

        [YtDlpFact]
        public async Task Download_RenamesToTitle_AndReportsProgress()
        {
            using (var server = new Server(2_000_000, TimeSpan.Zero))
            {
                var reports = new List<double>();
                var path = await Tool.Download(server.Url, 720, null, _folder, new SyncProgress<double>(reports.Add), CancellationToken.None);
                Assert.Equal(Path.Combine(_folder, "clip.mp4"), path);
                Assert.Equal(2_000_000, new FileInfo(path).Length);
                Assert.Single(Directory.GetFiles(_folder));
                Assert.Contains(100.0, reports);
            }
        }

        [YtDlpFact]
        public async Task Download_Cancel_LeavesNoFiles()
        {
            using (var server = new Server(50_000_000, TimeSpan.FromMilliseconds(100)))
            using (var cancel = new CancellationTokenSource())
            {
                var progress = new SyncProgress<double>(p => cancel.Cancel());
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool.Download(server.Url, 0, null, _folder, progress, cancel.Token));
                Assert.Empty(Directory.GetFiles(_folder));
            }
        }

        [YtDlpFact]
        public async Task Download_Failure_IsExplained()
        {
            var error = await Assert.ThrowsAsync<YtDlpException>(() => Tool.Download("http://127.0.0.1:9/none.mp4", 0, null, _folder, null, CancellationToken.None));
            Assert.Contains("네트워크", error.Message);
            Assert.Empty(Directory.GetFiles(_folder));
        }
    }
}
