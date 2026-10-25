using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
            Assert.Equal("https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin", SpeechText.VadModel.Url);
            Assert.Matches("^[0-9a-f]{64}$", SpeechText.VadModel.Sha256);
            Assert.Same(SpeechText.Models[0], SpeechText.Model("nothing"));
            Assert.Equal("ko", SpeechText.Language("xx"));
        }

        [Fact]
        public void PromptFor_DropsNonAsciiWords_UnlessUtf8CodePage()
        {
            // 한국어 Windows(949)에서는 whisper-cli가 한글을 깨진 바이트로 받으므로 영문 낱말만
            Assert.Equal("Folderss, OpenAI", SpeechText.PromptFor("맹자, Folderss 반구저기, OpenAI", 949, out var dropped));
            Assert.True(dropped);
            Assert.Equal("맹자, Folderss 반구저기, OpenAI", SpeechText.PromptFor(" 맹자, Folderss 반구저기, OpenAI ", 65001, out dropped));
            Assert.False(dropped);
            Assert.Equal("Folderss, OpenAI", SpeechText.PromptFor("Folderss, OpenAI", 949, out dropped));
            Assert.False(dropped);
            Assert.Equal("", SpeechText.PromptFor("맹자", 949, out dropped));
            Assert.True(dropped);
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
            Assert.Equal(new[] { "-m", "m.bin", "-f", "a.wav", "-l", "ko", "-t", "1", "-ml", "1", "-sow", "-osrt", "-of", "out", "-pp" }, args);
            // 말소리 구간 감지와 자주 나오는 낱말(제어 문자는 공백, 200자까지 — 인수 하나로 넘겨 다른 옵션이 되지 않는다)
            var more = SpeechText.Arguments("m.bin", "a.wav", "out", "ko", 4, "vad.bin", " 맹자,\n반구저기 -osrt ");
            Assert.Equal(new[] { "--vad", "-vm", "vad.bin", "--prompt", "맹자, 반구저기 -osrt" }, more.Skip(15));
            Assert.Equal(200, SpeechText.CleanPrompt(new string('가', 300)).Length);
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

        private static SubtitleCue W(double start, double end, string text)
        {
            return new SubtitleCue { Start = start, End = end, Text = text };
        }

        [Fact]
        public void Group_BreaksAtSentenceEndsPausesAndLength_KeepingWordTimes()
        {
            var words = new[]
            {
                W(0.36, 1.06, "10년"), W(1.06, 1.25, "넘게"), W(1.25, 1.67, "형제처럼"), W(1.67, 2.40, "지내던"), W(2.40, 3.02, "후배가"),
                W(3.02, 4.10, "거래처를"), W(4.10, 4.76, "가로채"), W(4.76, 6.11, "독립했습니다."),
                // 문장 끝 뒤: 0.3초 쉼(다음 큐 시작까지 늘임)
                W(6.41, 7.00, "억울해서"), W(7.00, 7.40, "피가"), W(7.40, 8.10, "거꾸로"), W(8.10, 9.80, "솟았습니다"),
                // 1초 쉼 → 끊고, 늘이지 않는다
                W(10.80, 11.20, "다음"), W(11.20, 11.60, "말"),
                W(12.00, 12.50, "[음악]")
            };
            var cues = SpeechText.Group(words);
            Assert.Equal(3, cues.Count);
            Assert.Equal("10년 넘게 형제처럼 지내던 후배가 거래처를 가로채 독립했습니다.", cues[0].Text);
            Assert.Equal((0.36, 6.41), (cues[0].Start, cues[0].End));
            Assert.Equal(8, cues[0].Words.Count);
            Assert.Equal(("억울해서 피가 거꾸로 솟았습니다", 6.41, 9.80), (cues[1].Text, cues[1].Start, cues[1].End));
            Assert.Equal("다음 말", cues[2].Text);
            Assert.Equal(11.60, cues[2].End);
        }

        [Fact]
        public void Group_SplitsLongRunsAndHalfFullCommas()
        {
            // maxChars자를 넘는 문장은 비슷한 길이로 나누고 쉼표 뒤를 먼저 고른다(한 단어 꼬리 없음, 큐 끝 쉼표는 지움)
            var text = "가나다라마바 사아자차카타, 파하가나다라 마바사아자차, 카타파하 가나다라마바사 아자차카타파하 가나다";
            var words = text.Split(' ').Select((w, i) => W(i, i + 0.9, w)).ToList();
            var cues = SpeechText.Group(words, maxChars: 20);
            Assert.Equal(new[] { "가나다라마바 사아자차카타", "파하가나다라 마바사아자차", "카타파하 가나다라마바사", "아자차카타파하 가나다" }, cues.Select(c => c.Text));
            // 쉼표가 없으면 이음 어미(-고) 뒤에서
            var clause = SpeechText.Group("회사 문을 박차고 나가 멱살을 잡고 파렴치한 인간이라고 소문이라도 내고 싶었습니다.".Split(' ').Select((w, i) => W(i, i + 0.9, w)).ToList());
            Assert.Equal(new[] { "회사 문을 박차고 나가 멱살을 잡고", "파렴치한 인간이라고 소문이라도 내고 싶었습니다." }, clause.Select(c => c.Text));
            var connective = SpeechText.Group("그 사람 잡으러 다니지 말고 내 지난 3년치 결제 서류부터 다시 꺼내봐라.".Split(' ').Select((w, i) => W(i, i + 0.9, w)).ToList());
            Assert.Equal("그 사람 잡으러 다니지 말고", connective[0].Text);
            // 앞쪽 쉼표에서는 끊지 않는다
            var early = SpeechText.Group(new[] { W(0, 0.5, "그래서,"), W(0.5, 1, "다음으로") }, maxChars: 20);
            Assert.Equal("그래서, 다음으로", Assert.Single(early).Text);
        }

        // ---------- 대본 맞춤 ----------

        private const string Script =
            "# [대본] 맹자의 반구저기 (反求諸己)\n"
            + "- **카테고리**: 인생철학\n"
            + "  > \"화살이 과녁을 빗나가면 과녁을 탓하지 말고 자신을 돌아보라.\"\n"
            + "| 씬 | 비주얼 | 내레이션 대본 |\n"
            + "| :--- | :--- | :--- |\n"
            + "| **Scene 1** | `images/scene1.jpg`<br>비 내리는 밤의 어두운 사무실 (9:16) | 억울해서 피가 거꾸로 솟았습니다.<br>*(자막 강조)* **\"그 사람 잡으러 다니지 말고, 네 서류부터 다시 꺼내 봐라.\"** |\n"
            + "| **Scene 2** | `images/scene2.jpg` | 맹자는 이렇게 말했습니다.<br>\"화살이 과녁을 빗나가면, 과녁을 탓하지 말고 자신을 돌아보라.\" |\n"
            + "#맹자 #인생철학\n";

        [Fact]
        public void ScriptSentences_TakesCellsAndLines_WithoutMarkup()
        {
            var sentences = SpeechText.ScriptSentences(Script);
            Assert.Contains("억울해서 피가 거꾸로 솟았습니다.", sentences);
            // 연출 지시 *(…)*·강조 **·코드 `…`는 빠지고 따옴표는 남는다
            Assert.Contains("\"그 사람 잡으러 다니지 말고, 네 서류부터 다시 꺼내 봐라.\"", sentences);
            Assert.Contains("[대본] 맹자의 반구저기 (反求諸己)", sentences);
            Assert.DoesNotContain(sentences, s => s.Contains("images/") || s.Contains("자막 강조") || s.Contains("*") || s.StartsWith("#", StringComparison.Ordinal));
            // 장면 설명 칸과 내레이션 칸은 섞이지 않는다
            Assert.Contains("비 내리는 밤의 어두운 사무실 (9:16)", sentences);
        }

        [Fact]
        public void AlignScript_TakesTextFromScript_TimesFromSpeech()
        {
            // 인식: 틀린 말("꼽고로 쏟았습니다")과 띄어쓰기가 다른 말, 대본에 없는 말(애드리브)이 섞여 있다
            var words = new[]
            {
                W(0.0, 0.8, "억울해서"), W(0.8, 1.2, "피가"), W(1.2, 1.9, "꼽고로"), W(1.9, 3.0, "쏟았습니다."),
                W(3.5, 3.8, "그"), W(3.8, 4.2, "사람"), W(4.2, 4.7, "잡으러"), W(4.7, 5.2, "다니지"), W(5.2, 5.6, "말고"),
                W(5.6, 5.8, "네"), W(5.8, 6.4, "서류부터"), W(6.4, 6.8, "다시"), W(6.8, 7.5, "꺼내봐라."),
                W(8.5, 9.0, "자,"), W(9.0, 9.6, "여러분"), W(9.6, 10.4, "들어보세요."),
                W(11.0, 11.6, "맹자는"), W(11.6, 12.1, "이렇게"), W(12.1, 13.0, "말했습니다."),
                W(13.3, 14.0, "화살이"), W(14.0, 14.5, "과녁을"), W(14.5, 15.2, "빗나가면"), W(15.2, 15.7, "과녁을"), W(15.7, 16.2, "탓하지"),
                W(16.2, 16.6, "말고"), W(16.6, 17.1, "자신을"), W(17.1, 18.0, "돌아보라.")
            };
            var result = SpeechText.AlignScript(Script, words);
            Assert.Equal(4, result.Matched);
            Assert.Equal(1, result.Unscripted);
            Assert.Equal(new[]
            {
                "억울해서 피가 거꾸로 솟았습니다.",
                "\"그 사람 잡으러 다니지 말고, 네 서류부터 다시 꺼내 봐라.\"",
                "자, 여러분 들어보세요.",
                "맹자는 이렇게 말했습니다.",
                "\"화살이 과녁을 빗나가면, 과녁을 탓하지 말고 자신을 돌아보라.\""
            }, result.Cues.Select(c => c.Text));
            // 시각은 인식한 단어에서(대본 머리글의 같은 인용문이 아니라 말한 순서의 것)
            Assert.Equal(0.0, result.Cues[0].Start);
            Assert.Equal(3.5, result.Cues[1].Start);
            Assert.InRange(result.Cues[1].End, 7.4, 8.5);
            Assert.Equal(13.3, result.Cues[4].Start);
            Assert.Equal(18.0, result.Cues[4].End);
            // 대본 단어마다 시각이 붙는다(단어 강조에 씀)
            Assert.Equal("거꾸로", result.Cues[0].Words[2].Text);
            Assert.Equal((1.2, 1.9), (result.Cues[0].Words[2].Start, result.Cues[0].Words[2].End));
        }

        [Fact]
        public void AlignScript_UnrelatedScript_KeepsRecognizedText()
        {
            var words = new[] { W(0, 1, "오늘은"), W(1, 2, "날씨가"), W(2, 3, "좋습니다.") };
            var result = SpeechText.AlignScript("전혀 다른 대본 문장입니다.\n이것도 말하지 않았습니다.", words);
            Assert.Equal(0, result.Matched);
            Assert.Equal("오늘은 날씨가 좋습니다.", Assert.Single(result.Cues).Text);
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
            var vad = WhisperTool.ModelPath(Whisper.Folder, SpeechText.VadModel);
            var options = new SpeechOptions { Model = Model, Vad = File.Exists(vad) ? vad : null, Language = "ko" };
            var cues = (await SpeechRecognizer.RecognizeClip(FfmpegMedia.Tool, Whisper, options, clip, project.Rate,
                new SyncProgress(s => stages.Add(s.Stage + " " + s.Done)), CancellationToken.None)).Cues;
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

    /// <summary>말하는 단어 강조: 음성 인식 큐의 단어 시각을 클립에 두고, 자막 트랙에서 켜면 지금 단어만 노랗게(libass \t).</summary>
    public class WordHighlightTests
    {
        private readonly ITestOutputHelper _out;

        public WordHighlightTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>0~2초 큐 "가나 다라" — 단어 0~1초, 1~2초(30fps → 0~30, 30~60프레임).</summary>
        private static (Project Project, Track Track, Clip Cue) Spoken(bool highlight = true)
        {
            var project = TestProjects.Empty();
            var cue = new SubtitleCue
            {
                Start = 0, End = 2, Text = "가나 다라",
                Words = new List<SubtitleCue> { new SubtitleCue { Start = 0, End = 1, Text = "가나" }, new SubtitleCue { Start = 1, End = 2, Text = "다라" } }
            };
            var track = TimelineEdits.ImportSubtitles(project, new[] { cue });
            TimelineEdits.SetWordHighlight(project, track, highlight);
            return (project, track, track.Clips[0]);
        }

        [Fact]
        public void ImportedCue_KeepsWordFrames_FromClipStart()
        {
            var (_, _, clip) = Spoken();
            Assert.Equal(new[] { ("가나", 0L, 30L), ("다라", 30L, 60L) }, clip.Words.Select(w => (w.Text, w.Start, w.End)));
        }

        [Fact]
        public void Ass_HighlightsOnlyTheSpokenWord()
        {
            var (project, track, _) = Spoken();
            var ass = SubtitleAss.Build(project, track, 0, project.Duration);
            // 단어마다 {\r}로 앞 변화를 끊고, 말해질 때(반 프레임 앞) 노랑 → 다음 단어 시작에 흰색. 대사 시작에 말하는 단어는 처음부터 노랑
            Assert.Contains("Dialogue: 0,0:00:00.00,0:00:01.98,Default,,0,0,0,,"
                + "{\\r\\1c&H00D7FF&\\t(983,984,\\1c&HFFFFFF&)}가나 {\\r\\t(983,984,\\1c&H00D7FF&)\\t(1983,1984,\\1c&HFFFFFF&)}다라\n", ass);
            // 큐 도중부터 그리면 이미 지난 단어는 흰색 그대로, 시각은 그리는 구간 시작 기준
            var mid = SubtitleAss.Build(project, track, 45, 46);
            Assert.Contains(",,{\\r}가나 {\\r\\1c&H00D7FF&\\t(483,484,\\1c&HFFFFFF&)}다라\n", mid);
        }

        [Fact]
        public void Ass_PlainWhenOffOrTextEdited()
        {
            var (project, track, clip) = Spoken(highlight: false);
            Assert.Contains(",,가나 다라\n", SubtitleAss.Build(project, track, 0, project.Duration));
            TimelineEdits.SetWordHighlight(project, track, true);
            clip.Text = "가나 다라 마바";
            Assert.False(SubtitleAss.CanHighlight(clip));
            Assert.Contains(",,가나 다라 마바\n", SubtitleAss.Build(project, track, 0, project.Duration));
            // 줄바꿈·띄어쓰기만 다르면 그대로 강조
            clip.Text = "가나\n다라";
            Assert.True(SubtitleAss.CanHighlight(clip));
        }

        [Fact]
        public void SplitAndTrim_KeepWordsOnTheSound()
        {
            var (project, track, clip) = Spoken();
            var right = TimelineEdits.Split(project, new[] { clip }, 30).Single();
            Assert.Equal(30, right.Start);
            Assert.Equal(new[] { (-30L, 0L), (0L, 30L) }, right.Words.Select(w => (w.Start, w.End)));
            Assert.Equal(new[] { (0L, 30L), (30L, 60L) }, clip.Words.Select(w => (w.Start, w.End)));
            Assert.NotSame(clip.Words, right.Words);
            // 앞을 다듬으면 단어가 소리의 같은 자리에 남는다(클립 시작 기준이 옮겨짐)
            TimelineEdits.TrimStart(project, clip, 15);
            Assert.Equal(new[] { (-15L, 15L), (15L, 45L) }, clip.Words.Select(w => (w.Start, w.End)));
        }

        [Fact]
        public void ProjectFile_KeepsWords_AndCleansUntrustedOnes()
        {
            var (project, track, _) = Spoken();
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var copy = ProjectFile.FromSnapshot(json.ToJsonString());
            var subtitles = copy.VideoTracks.Single(t => t.Subtitles);
            Assert.True(subtitles.WordHighlight);
            Assert.Equal(2, subtitles.Clips[0].Words.Count);
            // 빈 단어·너무 긴 단어는 빼고, 끝이 시작보다 앞이면 고친다. 자막 트랙이 아니면 강조를 끈다
            var words = json["VideoTracks"][1]["Clips"][0]["Words"].AsArray();
            words.Add(JsonNode.Parse("{\"Text\":\"\",\"Start\":5,\"End\":6}"));
            words.Add(JsonNode.Parse("{\"Text\":\"" + new string('가', 101) + "\",\"Start\":5,\"End\":6}"));
            words[1]["End"] = 10;
            json["VideoTracks"][0]["WordHighlight"] = true;
            var cleaned = ProjectFile.FromSnapshot(json.ToJsonString());
            Assert.False(cleaned.MainTrack.WordHighlight);
            Assert.Equal(new[] { ("가나", 0L, 30L), ("다라", 30L, 30L) }, cleaned.VideoTracks[1].Clips[0].Words.Select(w => (w.Text, w.Start, w.End)));
        }

        [FfmpegFact]
        public void Preview_ShowsTheSpokenWordInYellow()
        {
            var (project, track, _) = Spoken();
            project.Width = 640;
            project.Height = 360;
            int Yellow(byte[] bgra)
            {
                var count = 0;
                for (var i = 0; i < bgra.Length; i += 4)
                {
                    if (bgra[i + 2] > 200 && bgra[i + 1] > 150 && bgra[i] < 80)
                        count++;
                }
                return count;
            }
            var on = Yellow(PreviewRenderer.Frame(FfmpegMedia.Tool, project, 45, 640, 360, CancellationToken.None).GetAwaiter().GetResult());
            TimelineEdits.SetWordHighlight(project, track, false);
            var off = Yellow(PreviewRenderer.Frame(FfmpegMedia.Tool, project, 45, 640, 360, CancellationToken.None).GetAwaiter().GetResult());
            _out.WriteLine("노란 픽셀: 강조 " + on + " · 끔 " + off);
            Assert.True(on > 50, "강조 " + on);
            Assert.Equal(0, off);
        }
    }
}
