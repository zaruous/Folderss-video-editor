using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>음성 인식 자막: 릴리스·모델 고르기, 진행률, 큐 시각 옮기기·나누기, 내려받기(가짜 응답).</summary>
    public class SpeechTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-speech-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static string Release(string tag, string published, bool prerelease, string assets)
        {
            return "{\"tag_name\":\"" + tag + "\",\"published_at\":\"" + published + "\",\"prerelease\":" + (prerelease ? "true" : "false")
                   + ",\"draft\":false,\"assets\":[" + assets + "]}";
        }

        private static string Asset(string name, string url, long size, string digest)
        {
            return "{\"name\":\"" + name + "\",\"browser_download_url\":\"" + url + "\",\"size\":" + size + (digest == null ? "" : ",\"digest\":\"" + digest + "\"") + "}";
        }

        private static readonly string Sha = new string('a', 64);

        [Fact]
        public void ChooseRelease_PicksTheBuildOfTheLatestVersion()
        {
            // 버전 태그(v1.9.5)에는 파일이 없고 같은 때 만든 빌드(b5454)에 붙는다 — 그 뒤의 더 새 빌드(b5460, 시험판)는 고르지 않는다
            var json = "[" + string.Join(",",
                Release("b5460", "2026-10-08T10:00:00Z", true, Asset("whisper-bin-x64.zip", "https://github.com/ggml-org/whisper.cpp/releases/download/b5460/whisper-bin-x64.zip", 9, "sha256:" + Sha)),
                Release("v1.9.5", "2026-10-06T17:39:17Z", false, ""),
                Release("b5454", "2026-10-06T16:51:27Z", true, Asset("whisper-bin-x64.zip", "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip", 8928640, "sha256:" + Sha.ToUpperInvariant())),
                Release("b5130", "2026-09-11T05:29:10Z", true, Asset("whisper-bin-x64.zip", "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip", 8, "sha256:" + Sha))) + "]";
            var asset = SpeechText.ChooseRelease(json, SpeechText.BinaryAsset);
            Assert.Equal("https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip", asset.Url);
            Assert.Equal((8928640L, Sha), (asset.Size, asset.Sha256));
        }

        [Fact]
        public void ChooseRelease_WithoutVersionTag_TakesNewest_AndSkipsUnverifiable()
        {
            var json = "[" + string.Join(",",
                // SHA-256이 없거나 GitHub 주소가 아니면 쓰지 않는다
                Release("b3", "2026-10-03T00:00:00Z", true, Asset("whisper-bin-x64.zip", "https://github.com/x/b3.zip", 1, null)),
                Release("b2", "2026-10-02T00:00:00Z", true, Asset("whisper-bin-x64.zip", "https://evil.example/b2.zip", 1, "sha256:" + Sha)),
                Release("b1", "2026-10-01T00:00:00Z", true, Asset("whisper-bin-x64.zip", "https://github.com/x/b1.zip", 1, "sha256:" + Sha))) + "]";
            Assert.Equal("https://github.com/x/b1.zip", SpeechText.ChooseRelease(json, SpeechText.BinaryAsset).Url);
            Assert.Null(SpeechText.ChooseRelease("[]", SpeechText.BinaryAsset));
            Assert.Null(SpeechText.ChooseRelease("{\"message\":\"rate limit\"}", SpeechText.BinaryAsset));
        }

        [Fact]
        public void Models_AreFixedHuggingFaceFiles()
        {
            Assert.Equal("small", SpeechText.Models[0].Id);
            Assert.Equal(SpeechText.Models.Count, SpeechText.Models.Select(m => m.Id).Distinct().Count());
            Assert.All(SpeechText.Models, m =>
            {
                Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
                Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-", m.Url);
                Assert.True(m.Size > 10_000_000);
            });
            Assert.Same(SpeechText.Models[0], SpeechText.Model("nothing"));
            Assert.Equal("ko", SpeechText.Language("xx"));
        }

        [Theory]
        [InlineData("whisper_print_progress_callback: progress =  37%", 37)]
        [InlineData("whisper_print_progress_callback: progress = 100%", 100)]
        [InlineData("whisper_full_with_state: auto-detected language: ko", null)]
        [InlineData("progress = 250%", null)]
        public void ParseProgress(string line, int? expected)
        {
            Assert.Equal(expected, SpeechText.ParseProgress(line));
        }

        [Fact]
        public void Arguments_WriteSrtAndProgress()
        {
            var args = SpeechText.Arguments("m.bin", "a.wav", "out", "xx", 0);
            Assert.Equal(new[] { "-m", "m.bin", "-f", "a.wav", "-l", "ko", "-t", "1", "-osrt", "-of", "out", "-pp" }, args);
            Assert.Equal(8, SpeechText.Threads(64));
            Assert.Equal(1, SpeechText.Threads(1));
        }

        [Theory]
        [InlineData("Release/whisper-cli.exe", true)]
        [InlineData("Release/whisper.dll", true)]
        [InlineData("Release/ggml-cpu-haswell.dll", true)]
        [InlineData("Release/whisper-server.exe", false)]
        [InlineData("Release/SDL2.dll", false)]
        [InlineData("Release/ggml evil.dll", false)]
        public void ShouldExtract_OnlyCliAndItsLibraries(string entry, bool expected)
        {
            Assert.Equal(expected, SpeechText.ShouldExtract(entry));
        }

        [Fact]
        public void ToTimeline_ShiftsBySpeed_AndCutsAtClipEnd()
        {
            // 10초에 놓은 2배속 클립: 원본 2~4초 → 타임라인 11~12초, 클립 끝(14초) 밖은 자르고 버린다
            var cues = new[]
            {
                new SubtitleCue { Start = 2, End = 4, Text = " 안녕하세요 " },
                new SubtitleCue { Start = 6, End = 10, Text = "끝에 걸침" },
                new SubtitleCue { Start = 9, End = 12, Text = "밖" },
                new SubtitleCue { Start = 0, End = 1, Text = "   " }
            };
            var result = SpeechText.ToTimeline(cues, 10, 2, 14);
            Assert.Equal(2, result.Count);
            Assert.Equal((11.0, 12.0, "안녕하세요"), (result[0].Start, result[0].End, result[0].Text));
            Assert.Equal((13.0, 14.0), (result[1].Start, result[1].End));
        }

        [Theory]
        [InlineData("[음악]", true)]
        [InlineData("(박수)", true)]
        [InlineData("♪ ♪", true)]
        [InlineData("[끝]", true)]
        [InlineData("안녕하세요 (웃음)", false)]
        [InlineData("[1] 첫째 (이유)", false)]
        [InlineData("다음 시간에", false)]
        public void IsNoise_OnlyBracketedOrMusicMarks(string text, bool expected)
        {
            Assert.Equal(expected, SpeechText.IsNoise(text));
        }

        [Fact]
        public void SplitLong_SplitsAtSentenceEnds_ProportionallyInTime()
        {
            var cue = new SubtitleCue { Start = 0, End = 10, Text = "십 년 넘게 형제처럼 지내던 후배가 거래처를 가로채 독립했습니다, 억울해서 피가 거꾸로 솟았습니다." };
            var result = SpeechText.SplitLong(new[] { cue, new SubtitleCue { Start = 10, End = 12, Text = "짧은 큐" } });
            Assert.Equal(3, result.Count);
            Assert.Equal("십 년 넘게 형제처럼 지내던 후배가 거래처를 가로채 독립했습니다", result[0].Text);
            Assert.Equal("억울해서 피가 거꾸로 솟았습니다.", result[1].Text);
            Assert.Equal(0, result[0].Start);
            Assert.Equal(result[0].End, result[1].Start);
            Assert.Equal(10, result[1].End);
            Assert.InRange(result[0].End, 6, 7.5);
            Assert.Equal("짧은 큐", result[2].Text);
            // 나눌 곳이 없으면 그대로
            var single = SpeechText.SplitLong(new[] { new SubtitleCue { Start = 0, End = 3, Text = new string('가', 50) } });
            Assert.Single(single);
        }

        // ---------- 내려받기(가짜 응답) ----------

        private sealed class FakeServer : HttpMessageHandler
        {
            public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(Files.TryGetValue(request.RequestUri.ToString(), out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static byte[] Zip(params (string Name, string Content)[] entries)
        {
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    foreach (var (name, content) in entries)
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
                            writer.Write(content);
                    }
                }
                return buffer.ToArray();
            }
        }

        private static FakeServer Server(byte[] zip, string sha = null)
        {
            const string url = "https://github.com/ggml-org/whisper.cpp/releases/download/b1/whisper-bin-x64.zip";
            var server = new FakeServer();
            sha = sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            server.Files[SpeechText.ReleasesApi] = Encoding.UTF8.GetBytes("[" + Release("b1", "2026-10-01T00:00:00Z", true, Asset(SpeechText.BinaryAsset, url, zip.Length, "sha256:" + sha)) + "]");
            server.Files[url] = zip;
            return server;
        }

        [Fact]
        public async Task DownloadTool_VerifiesAndExtractsCliAndLibrariesOnly()
        {
            var zip = Zip(("Release/whisper-cli.exe", "cli"), ("Release/whisper.dll", "w"), ("Release/ggml-base.dll", "g"),
                ("Release/whisper-server.exe", "server"), ("Release/../../evil.dll", "x"));
            var tool = await SpeechRecognizer.DownloadTool(_folder, null, CancellationToken.None, Server(zip), verify: false);
            Assert.Equal(Path.Combine(_folder, "whisper-cli.exe"), tool.CliPath);
            Assert.Equal("cli", File.ReadAllText(tool.CliPath));
            Assert.True(File.Exists(Path.Combine(_folder, "ggml-base.dll")));
            Assert.False(File.Exists(Path.Combine(_folder, "whisper-server.exe")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder), "evil.dll")));
            Assert.Empty(Directory.GetFiles(_folder, "*.part"));
            Assert.Empty(Directory.GetFiles(_folder, "*.new"));
        }

        [Fact]
        public async Task DownloadTool_ChecksumMismatchOrMissingCli_Throws_AndLeavesNothing()
        {
            var zip = Zip(("Release/whisper-cli.exe", "cli"));
            await Assert.ThrowsAsync<InvalidDataException>(() => SpeechRecognizer.DownloadTool(_folder, null, CancellationToken.None, Server(zip, new string('0', 64)), verify: false));
            Assert.Null(WhisperTool.Locate(_folder));
            var withoutCli = Zip(("Release/whisper.dll", "w"));
            await Assert.ThrowsAsync<InvalidDataException>(() => SpeechRecognizer.DownloadTool(_folder, null, CancellationToken.None, Server(withoutCli), verify: false));
            Assert.Null(WhisperTool.Locate(_folder));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task DownloadModel_WrongContent_IsRejected()
        {
            // 모델의 크기·SHA-256은 고정 — 다른 내용이 오면 쓰지 않는다
            var model = SpeechText.Models[0];
            var server = new FakeServer();
            server.Files[model.Url] = Encoding.UTF8.GetBytes("not a model");
            await Assert.ThrowsAsync<InvalidDataException>(() => SpeechRecognizer.DownloadModel(_folder, model, null, CancellationToken.None, server));
            Assert.False(WhisperTool.HasModel(_folder, model));
            Assert.Empty(Directory.GetFiles(WhisperTool.ModelFolder(_folder)));
        }
    }

    /// <summary>실제 whisper.cpp로 인식해 본다 — FOLDERSS_VE_WHISPER에 whisper-cli.exe가 든 폴더(models\에 모델 하나 이상)를 줄 때만.</summary>
    public sealed class WhisperFactAttribute : FactAttribute
    {
        public WhisperFactAttribute()
        {
            if (FfmpegMedia.Tool == null || SpeechIntegrationTests.Whisper == null || SpeechIntegrationTests.Model == null)
                Skip = "whisper.cpp를 찾지 못해 건너뜁니다(FOLDERSS_VE_WHISPER에 whisper-cli.exe와 models\\ggml-*.bin이 든 폴더).";
        }
    }

    public class SpeechIntegrationTests
    {
        private readonly ITestOutputHelper _out;

        public SpeechIntegrationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        public static WhisperTool Whisper { get { return WhisperTool.Locate(Environment.GetEnvironmentVariable("FOLDERSS_VE_WHISPER")); } }

        public static string Model
        {
            get
            {
                var tool = Whisper;
                return tool == null ? null : SpeechText.Models.Select(m => WhisperTool.ModelPath(tool.Folder, m)).FirstOrDefault(File.Exists);
            }
        }

        [WhisperFact]
        public async Task RecognizeClip_RunsTheWholePipeline()
        {
            // 말이 없는 440Hz 소리: 큐가 없거나 몇 개뿐이어도 되고, 소리 꺼내기 → 인식 → SRT 읽기가 끝까지 돈다(진행률 포함)
            await SpeechRecognizer.Check(Whisper, CancellationToken.None);
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 30);
            var stages = new List<string>();
            var cues = await SpeechRecognizer.RecognizeClip(FfmpegMedia.Tool, Whisper, Model, "ko", clip, project.Rate,
                new SyncProgress(s => stages.Add(s.Stage + " " + s.Done)), CancellationToken.None);
            _out.WriteLine(string.Join(" / ", stages.Distinct()) + " · 큐 " + cues.Count + ": " + string.Join(" | ", cues.Select(c => c.Start + "~" + c.End + " " + c.Text)));
            Assert.Contains(stages, s => s.StartsWith("음성 인식 중", StringComparison.Ordinal));
            Assert.Equal(30, clip.Start);
            Assert.All(cues, c => Assert.InRange(c.Start, 1.0, 3.0));
        }

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
