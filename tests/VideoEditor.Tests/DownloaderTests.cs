using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Media;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>FFmpeg 내려받기: 네트워크 없이 가짜 응답으로 릴리스 고르기·체크섬·압축 풀기를 본다.</summary>
    public class DownloaderTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-dl-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static byte[] MakeZip(params (string Name, string Content)[] entries)
        {
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in entries)
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open()))
                            writer.Write(entry.Content);
                    }
                }
                return buffer.ToArray();
            }
        }

        private sealed class FakeServer : HttpMessageHandler
        {
            public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>();
            public List<string> Requested { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri.ToString();
                Requested.Add(url);
                Assert.Contains("Folderss-video-editor", request.Headers.UserAgent.ToString());
                return Task.FromResult(Files.TryGetValue(url, out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static string Exe(string name)
        {
            return OperatingSystem.IsWindows() ? name + ".exe" : name;
        }

        private FakeServer Server(byte[] zip, string checksum = null)
        {
            const string name = "ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip";
            var server = new FakeServer();
            var release = "{\"assets\":[{\"name\":\"checksums.sha256\",\"browser_download_url\":\"https://github.com/dl/checksums.sha256\",\"size\":10},"
                          + "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://github.com/dl/" + name + "\",\"size\":" + zip.Length + "}]}";
            server.Files[FfmpegDownloader.ReleaseApi] = Encoding.UTF8.GetBytes(release);
            checksum = checksum ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            server.Files["https://github.com/dl/checksums.sha256"] = Encoding.UTF8.GetBytes(checksum + "  " + name + "\n");
            server.Files["https://github.com/dl/" + name] = zip;
            return server;
        }

        [Fact]
        public async Task Download_VerifiesAndExtractsBinOnly()
        {
            var zip = MakeZip(
                ("ffmpeg-n9.0/bin/" + Exe("ffmpeg"), "ff"),
                ("ffmpeg-n9.0/bin/" + Exe("ffprobe"), "fp"),
                ("ffmpeg-n9.0/bin/avcodec.dll", "dll"),
                ("ffmpeg-n9.0/LICENSE.txt", "GPL"),
                ("ffmpeg-n9.0/doc/readme.html", "doc"),
                ("ffmpeg-n9.0/bin/../../evil.txt", "x"));
            var stages = new List<string>();
            var bin = await FfmpegDownloader.Download(_folder, new SyncProgress(s => stages.Add(s.Stage)), CancellationToken.None, Server(zip));
            Assert.Equal(Path.Combine(_folder, "bin"), bin);
            Assert.Equal("ff", File.ReadAllText(Path.Combine(bin, Exe("ffmpeg"))));
            Assert.True(File.Exists(Path.Combine(bin, "avcodec.dll")));
            Assert.True(File.Exists(Path.Combine(_folder, "LICENSE.txt")));
            Assert.False(Directory.Exists(Path.Combine(_folder, "doc")));
            Assert.False(File.Exists(Path.Combine(_folder, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder), "evil.txt")));
            Assert.False(File.Exists(Path.Combine(_folder, "download.zip.part")));
            Assert.Contains(stages, s => s.StartsWith("내려받는 중", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Download_ChecksumMismatch_Throws_AndLeavesNothing()
        {
            var zip = MakeZip(("x/bin/" + Exe("ffmpeg"), "ff"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FfmpegDownloader.Download(_folder, null, CancellationToken.None, Server(zip, new string('0', 64))));
            Assert.False(Directory.Exists(Path.Combine(_folder, "bin")));
            Assert.False(File.Exists(Path.Combine(_folder, "download.zip.part")));
        }

        [Fact]
        public async Task Download_ZipWithoutFfmpeg_Throws()
        {
            var zip = MakeZip(("x/bin/readme.txt", "no exe"));
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegDownloader.Download(_folder, null, CancellationToken.None, Server(zip)));
        }

        /// <summary>Progress&lt;T&gt;는 스레드 풀로 넘겨 순서가 섞이므로 바로 부르는 것.</summary>
        private sealed class SyncProgress : IProgress<FfmpegDownloader.Status>
        {
            private readonly Action<FfmpegDownloader.Status> _report;

            public SyncProgress(Action<FfmpegDownloader.Status> report)
            {
                _report = report;
            }

            public void Report(FfmpegDownloader.Status value)
            {
                _report(value);
            }
        }
    }
}
