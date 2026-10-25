using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>인물 마스크: 고정한 모델, 모델 입력 크기·마스크 레이트·프레임 수, 캐시 이름과 찾기, FFmpeg 인수, 모델 입력·출력 변환, 내려받기(가짜 응답).</summary>
    public class PersonMaskTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-personmask-" + Guid.NewGuid().ToString("N"));

        public PersonMaskTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        [Fact]
        public void Model_IsPinned_ToRevision_SizeAndSha256()
        {
            var model = PersonMaskText.Model;
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            Assert.InRange(model.Size, 1L << 20, 200L << 20);
            // 리비전을 고정한 주소(main 같은 움직이는 이름이 아님)
            Assert.StartsWith("https://huggingface.co/", model.Url, StringComparison.Ordinal);
            Assert.Contains("/resolve/" + model.Revision + "/", model.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("/resolve/main/", model.Url, StringComparison.Ordinal);
            Assert.Equal("Apache-2.0", model.License);
            Assert.EndsWith(".onnx", model.File, StringComparison.Ordinal);
            Assert.Equal(model.File, Path.GetFileName(model.File));
            Assert.Contains(model.Revision.Substring(0, 8), model.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(model.Attribution));
            Assert.Equal(256, model.ShortSide);
        }

        [Theory]
        [InlineData(1920, 1080, 448, 256)]
        [InlineData(1080, 1920, 256, 448)]
        [InlineData(640, 360, 448, 256)]
        [InlineData(1440, 1080, 352, 256)]
        [InlineData(1000, 1000, 256, 256)]
        [InlineData(2560, 1080, 512, 224)]
        [InlineData(100, 50, 512, 256)]
        [InlineData(0, 0, 448, 256)]
        public void InputSize_ShortSide256_LongSideCapped_MultipleOf32(int width, int height, int expectedWidth, int expectedHeight)
        {
            var (w, h) = PersonMaskText.InputSize(width, height);
            Assert.Equal(expectedWidth, w);
            Assert.Equal(expectedHeight, h);
            Assert.Equal(0, w % PersonMaskText.Multiple);
            Assert.Equal(0, h % PersonMaskText.Multiple);
            Assert.True(Math.Max(w, h) <= PersonMaskText.MaxLongSide);
        }

        [Theory]
        [InlineData("30000/1001", "30000/1001")]
        [InlineData("25/1", "25/1")]
        [InlineData("60/1", "60/1")]
        [InlineData("120/1", "60/1")]
        [InlineData("240", "60/1")]
        [InlineData("100", "50/1")]
        [InlineData("120000/1001", "60000/1001")]
        [InlineData("1323000/44123", "1323000/44123")]
        [InlineData(null, "30/1")]
        [InlineData("garbage", "30/1")]
        public void MaskRate_IsSourceAverage_HalvedAbove60(string source, string expected)
        {
            Assert.Equal(expected, PersonMaskText.MaskRate(source).Rational);
        }

        [Fact]
        public void FrameCount_CoversTheWholeSource()
        {
            Assert.Equal(90, PersonMaskText.FrameCount(3.0, new FrameRate(30, 1)));
            Assert.Equal(50, PersonMaskText.FrameCount(2.0, new FrameRate(25, 1)));
            Assert.Equal(31, PersonMaskText.FrameCount(1.01, new FrameRate(30, 1)));
            Assert.Equal(300, PersonMaskText.FrameCount(10.01, new FrameRate(30000, 1001)));
            Assert.Equal(1, PersonMaskText.FrameCount(0, new FrameRate(30, 1)));
            Assert.Equal(1, PersonMaskText.FrameCount(double.NaN, new FrameRate(30, 1)));
        }

        [Fact]
        public void CacheName_DependsOnPath_SizeTime_AndModel()
        {
            var path = Path.Combine(_folder, "영상 원본.mp4");
            var name = PersonMaskText.CacheName(path, 1000, 42);
            Assert.Matches("^[0-9a-f]{16}-[0-9a-f]{16}$", name);
            Assert.Equal(name, PersonMaskText.CacheName(path, 1000, 42));
            // 원본 이름을 파일 이름에 쓰지 않는다
            Assert.DoesNotContain("영상", name, StringComparison.Ordinal);
            // 대소문자만 다른 경로는 같은 원본(Windows)
            Assert.Equal(name, PersonMaskText.CacheName(path.ToUpperInvariant(), 1000, 42));
            Assert.Equal(PersonMaskText.PathKey(path), name.Substring(0, 16));
            // 크기·고친 시각·모델이 바뀌면 뒤가 바뀌고 앞(경로)은 그대로
            foreach (var other in new[]
                     {
                         PersonMaskText.CacheName(path, 1001, 42), PersonMaskText.CacheName(path, 1000, 43),
                         PersonMaskText.CacheName(path, 1000, 42, new PersonMaskModel { Id = "other" })
                     })
            {
                Assert.NotEqual(name, other);
                Assert.Equal(name.Substring(0, 17), other.Substring(0, 17));
            }
            Assert.NotEqual(name.Substring(0, 16), PersonMaskText.CacheName(Path.Combine(_folder, "다른.mp4"), 1000, 42).Substring(0, 16));
            Assert.Equal(".mkv", PersonMaskText.Extension(false));
            Assert.Equal(".png", PersonMaskText.Extension(true));
        }

        [Fact]
        public void DecodeArguments_FixedRateFromFirstFrame_OneScale_RgbToPipe()
        {
            var source = Path.Combine(_folder, "a b.mp4");
            var args = PersonMaskText.DecodeArguments(source, 448, 256, new FrameRate(30000, 1001), false);
            var input = args.IndexOf("-i");
            Assert.Equal(source, args[input + 1]);
            Assert.Equal(new[] { "-protocol_whitelist", "file" }, args.Skip(input - 2).Take(2));
            Assert.Equal("0:V:0", args[args.IndexOf("-map") + 1]);
            Assert.Contains("-an", args);
            var filter = args[args.IndexOf("-vf") + 1];
            // 첫 화면부터(파일 0초부터 첫 화면으로 채우지 않음 — 화면이 늦게 시작하는 원본은 그래프가 VideoStart만큼 당겨 읽는다)
            Assert.Equal("fps=30000/1001,scale=448:256:flags=bilinear", filter);
            // 한 사슬에 scale은 하나만(두 scale을 이으면 FFmpeg가 멈추던 일)
            Assert.Single(Regex.Matches(filter, "scale="));
            Assert.Equal(new[] { "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }, args.Skip(args.Count - 5));

            var image = PersonMaskText.DecodeArguments(source, 352, 256, FrameRate.Default, true);
            Assert.Equal("1", image[image.IndexOf("-frames:v") + 1]);
            Assert.Equal("scale=352:256:flags=bilinear", image[image.IndexOf("-vf") + 1]);
            Assert.DoesNotContain(image, a => a.StartsWith("fps=", StringComparison.Ordinal));
            Assert.Equal("pipe:1", image.Last());
        }

        [Fact]
        public void EncodeArguments_GrayFromPipe_Ffv1EveryFrameKey_OrPng()
        {
            var output = Path.Combine(_folder, "m.mkv.part");
            var args = PersonMaskText.EncodeArguments(448, 256, new FrameRate(25, 1), output, false);
            Assert.Equal(new[] { "-f", "rawvideo", "-pix_fmt", "gray", "-s", "448x256", "-framerate", "25/1", "-i", "pipe:0" },
                args.Skip(args.IndexOf("-f")).Take(10));
            Assert.Equal("ffv1", args[args.IndexOf("-c:v") + 1]);
            Assert.Equal("1", args[args.IndexOf("-g") + 1]);
            Assert.Equal("gray", args[args.LastIndexOf("-pix_fmt") + 1]);
            // .part에 써도 형식이 정해져 있다
            Assert.Equal(new[] { "-f", "matroska", output }, args.Skip(args.Count - 3));

            var png = PersonMaskText.EncodeArguments(352, 256, FrameRate.Default, output, true);
            Assert.Equal("png", png[png.IndexOf("-c:v") + 1]);
            Assert.Equal(new[] { "-f", "image2", "-update", "1", output }, png.Skip(png.Count - 5));
            Assert.Equal("1", png[png.IndexOf("-frames:v") + 1]);
        }

        [Fact]
        public void FillInput_IsPlanarRgb_NormalizedToMinusOneOne()
        {
            // 2×1: (0, 255, 128) (255, 0, 51)
            var rgb = new byte[] { 0, 255, 128, 255, 0, 51 };
            var input = new float[6];
            PersonMaskText.FillInput(rgb, input, 2, 1);
            Assert.Equal(-1f, input[0]);
            Assert.Equal(1f, input[1]);
            Assert.Equal(1f, input[2]);
            Assert.Equal(-1f, input[3]);
            Assert.Equal(128 / 127.5f - 1f, input[4], 5);
            Assert.Equal(51 / 127.5f - 1f, input[5], 5);
            Assert.Throws<ArgumentException>(() => PersonMaskText.FillInput(new byte[5], input, 2, 1));
        }

        [Fact]
        public void ToMask_SnapsNearBlackWhite_SmoothsSmallChanges_FollowsBigOnes()
        {
            var mask = new byte[5];
            PersonMaskText.ToMask(new[] { 0.01f, 0.99f, 0.5f, float.NaN, 2f }, mask, null);
            Assert.Equal(new byte[] { 0, 255, 128, 0, 255 }, mask);

            // 앞 프레임: 작게 바뀐 곳은 절반 남짓만, 크게(움직임) 바뀐 곳은 바로
            var previous = new byte[] { 100, 0, 255, 0, 0 };
            PersonMaskText.ToMask(new[] { 120 / 255f, 1f, 0f, 0.02f, 0.6f }, mask, previous);
            Assert.InRange(mask[0], 110, 113);
            Assert.Equal(255, mask[1]);
            Assert.Equal(0, mask[2]);
            Assert.Equal(0, mask[3]);
            Assert.InRange(mask[4], 150, 154);
            Assert.Throws<ArgumentException>(() => PersonMaskText.ToMask(new float[2], mask, null));
        }

        [Fact]
        public void Threads_AboutPhysicalCores()
        {
            Assert.Equal(1, PersonSegmenter.Threads(1));
            Assert.Equal(1, PersonSegmenter.Threads(2));
            Assert.Equal(6, PersonSegmenter.Threads(12));
            Assert.Equal(8, PersonSegmenter.Threads(32));
        }

        [Fact]
        public void OnnxRuntimeCandidates_PluginRuntimesFolderFirst()
        {
            var list = OnnxRuntimeLibrary.Candidates(@"C:\plugin", @"C:\env");
            Assert.Equal(Path.Combine(@"C:\plugin", "runtimes", OnnxRuntimeLibrary.RuntimeId, "native", OnnxRuntimeLibrary.FileName), list[0]);
            Assert.Equal(Path.Combine(@"C:\plugin", OnnxRuntimeLibrary.FileName), list[1]);
            Assert.Equal(Path.Combine(@"C:\env", OnnxRuntimeLibrary.FileName), list[2]);
            Assert.Equal(2, OnnxRuntimeLibrary.Candidates(@"C:\plugin", null).Count);
            Assert.Matches("^(win|linux|osx)-[a-z0-9]+$", OnnxRuntimeLibrary.RuntimeId);
        }

        // ---------- 찾기·캐시 ----------

        private string Source(string name, string content = "원본")
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static string Expected(PersonMaskService service, string source, bool image)
        {
            var file = new FileInfo(source);
            return Path.Combine(service.MaskFolder, PersonMaskText.CacheName(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks) + PersonMaskText.Extension(image));
        }

        [Fact]
        public void Find_ReadyMask_MissingStaleAndPartIgnored()
        {
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            var video = Source("v.mp4");
            Assert.Null(service.Find(video));
            Assert.Null(service.Find(null));
            Assert.Null(service.Find(Path.Combine(_folder, "없는 파일.mp4")));
            // 찾기는 폴더를 만들지 않는다
            Assert.False(Directory.Exists(service.Folder));

            Directory.CreateDirectory(service.MaskFolder);
            var mask = Expected(service, video, false);
            File.WriteAllText(mask + ".part", "만드는 중");
            Assert.Null(service.Find(video));
            File.WriteAllText(mask, "마스크");
            Assert.Equal(mask, service.Find(video));

            // 원본을 바꿔 넣으면(크기·고친 시각) 옛 마스크는 쓰지 않는다
            File.WriteAllText(video, "바꿔 넣은 원본 — 길이가 다르다");
            Assert.Null(service.Find(video));
            File.SetLastWriteTimeUtc(video, DateTime.UtcNow.AddMinutes(-5));
            Assert.Null(service.Find(video));

            // 사진은 .png
            var photo = Source("p.jpg");
            File.WriteAllText(Expected(service, photo, true), "png");
            Assert.Equal(Expected(service, photo, true), service.Find(photo));
        }

        [Fact]
        public void ModelCandidates_EnvFolderFirst_HasModelChecksSize()
        {
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            var env = Path.Combine(_folder, "env");
            var list = service.ModelCandidates(env);
            Assert.Equal(new[]
            {
                Path.Combine(env, PersonMaskText.Model.File), Path.Combine(env, "models", PersonMaskText.Model.File),
                Path.Combine(service.ModelFolder, PersonMaskText.Model.File)
            }, list);
            Assert.Single(service.ModelCandidates(null));

            Directory.CreateDirectory(service.ModelFolder);
            var path = list[2];
            using (var file = File.Create(path))
                file.SetLength(PersonMaskText.Model.Size - 1);
            Assert.False(PersonMaskService.HasModel(path));
            using (var file = File.OpenWrite(path))
                file.SetLength(PersonMaskText.Model.Size);
            Assert.True(PersonMaskService.HasModel(path));
            Assert.False(PersonMaskService.HasModel(Path.Combine(env, "없음.onnx")));
        }

        [Fact]
        public void Cleanup_RemovesOldPartsOnly()
        {
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            service.Cleanup(TimeSpan.FromHours(1));
            Directory.CreateDirectory(service.MaskFolder);
            Directory.CreateDirectory(service.ModelFolder);
            var old = Path.Combine(service.MaskFolder, "a-b.mkv.part");
            var fresh = Path.Combine(service.MaskFolder, "c-d.mkv.part");
            var oldModel = Path.Combine(service.ModelFolder, PersonMaskText.Model.File + ".part");
            var ready = Path.Combine(service.MaskFolder, "e-f.mkv");
            foreach (var file in new[] { old, fresh, oldModel, ready })
                File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(oldModel, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(ready, DateTime.UtcNow.AddDays(-30));
            service.Cleanup(TimeSpan.FromHours(1));
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(oldModel));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(ready));
        }

        [Fact]
        public async Task Build_RefusesAudio()
        {
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            var media = new MediaInfo { Path = Source("t.wav"), Kind = MediaKind.Audio, HasAudio = true };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.Build(null, media, null, CancellationToken.None));
            Assert.False(Directory.Exists(service.Folder));
        }

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

        [Fact]
        public async Task DownloadModel_WrongContent_IsRejected_AndLeavesNothing()
        {
            // 모델의 크기·SHA-256은 고정 — 다른 내용이 오면 쓰지 않고, 받던 파일과 빈 폴더도 남기지 않는다
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            var server = new FakeServer();
            server.Files[PersonMaskText.Model.Url] = Encoding.UTF8.GetBytes("not a model");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadModel(null, CancellationToken.None, server));
            Assert.False(Directory.Exists(service.Folder));
            // 주소가 없으면(404) HTTP 오류
            await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadModel(null, CancellationToken.None, new FakeServer()));
            Assert.False(Directory.Exists(service.Folder));
        }
    }

    /// <summary>실제 모델로 인물 마스크를 만들어 본다 — FOLDERSS_VE_PERSONMASK에 모델 파일이 든 폴더를 줄 때만(FFmpeg도 있어야).</summary>
    public sealed class PersonMaskFactAttribute : FactAttribute
    {
        public PersonMaskFactAttribute()
        {
            if (FfmpegMedia.Tool == null || PersonMaskIntegrationTests.Model == null)
                Skip = "인물 분리 모델을 찾지 못해 건너뜁니다(FOLDERSS_VE_PERSONMASK에 " + PersonMaskText.Model.File + "가 든 폴더).";
        }
    }

    public class PersonMaskIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-personmask-it-" + Guid.NewGuid().ToString("N"));

        public PersonMaskIntegrationTests(ITestOutputHelper output)
        {
            _out = output;
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        public static string Model
        {
            get
            {
                var env = Environment.GetEnvironmentVariable(PersonMaskText.EnvFolder);
                return string.IsNullOrWhiteSpace(env) ? null : new PersonMaskService(env).ModelCandidates(env).Take(2).FirstOrDefault(PersonMaskService.HasModel);
            }
        }

        /// <summary>
        /// 사람처럼 생긴 그림(머리·머리카락·목·어깨·몸통, 밝은 벽 앞)을 FFmpeg로 그린다. 가운데 x = centerX, 머리 가운데 y = 128, 몸통은 y 205부터 아래로.
        /// 시험용 사진이 없어 그리는 것 — 모델은 이것도 사람으로 따낸다.
        /// </summary>
        internal static string Person(string folder, string name, int width, int centerX)
        {
            var x = "(X-" + centerX + ")";
            var head = "lte(pow(" + x + "/46,2)+pow((Y-128)/58,2),1)";
            var hair = "lte(pow(" + x + "/52,2)+pow((Y-112)/56,2),1)*lt(Y,112)";
            var neck = "lt(abs" + x + ",20)*gt(Y,175)*lt(Y,215)";
            var body = "gt(Y,205)*lt(abs" + x + ",70+(Y-205)*1.1)*lte(pow(max(abs" + x + "-70,0)/60,2)+pow(max(250-Y,0)/45,2),1)";
            string Channel(int hairValue, int skin, int neckValue, int shirt, string wall)
            {
                return "if(" + hair + "," + hairValue + ",if(" + head + "," + skin + ",if(" + neck + "," + neckValue + ",if(" + body + "," + shirt + "," + wall + "))))";
            }
            var path = Path.Combine(folder, name);
            Ffmpeg("-f", "lavfi", "-i", "color=c=black:s=" + width + "x360,format=rgb24",
                "-vf", "geq=r='" + Channel(45, 225, 215, 40, "200-Y/8") + "':g='" + Channel(32, 175, 165, 72, "190-Y/8") + "':b='" + Channel(25, 145, 135, 140, "170-Y/10") + "'",
                "-frames:v", "1", path);
            return path;
        }

        private static string Ffmpeg(params string[] args)
        {
            var all = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            all.AddRange(args);
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfmpegPath, all, TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(result.ExitCode == 0, "FFmpeg 실패: " + result.Error);
            return result.Output;
        }

        private static string Probe(params string[] args)
        {
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfprobePath, new[] { "-v", "error" }.Concat(args), TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(result.ExitCode == 0, "ffprobe 실패: " + result.Error);
            return result.Output.Trim();
        }

        /// <summary>마스크의 seconds 위치 프레임(흑백 원시 바이트).</summary>
        private static byte[] MaskFrame(string mask, double seconds, int width, int height)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error" };
            if (seconds > 0)
                args.AddRange(new[] { "-ss", seconds.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-i", mask, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" });
            var bytes = FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath, args, TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(width * height, bytes.Length);
            return bytes;
        }

        /// <summary>마스크에서 (원본 좌표) 사각형의 평균 밝기.</summary>
        private static double Mean(byte[] mask, int maskWidth, int maskHeight, int sourceWidth, int sourceHeight, int x0, int y0, int x1, int y1)
        {
            double sum = 0;
            var count = 0;
            for (var y = y0 * maskHeight / sourceHeight; y < y1 * maskHeight / sourceHeight; y++)
            {
                for (var x = x0 * maskWidth / sourceWidth; x < x1 * maskWidth / sourceWidth; x++)
                {
                    sum += mask[y * maskWidth + x];
                    count++;
                }
            }
            return sum / Math.Max(1, count);
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

        [PersonMaskFact]
        public async Task Image_MaskIsGrayPng_WhiteOverThePerson_RebuiltWhenSourceChanges()
        {
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            var photo = Person(_folder, "사람 사진.png", 640, 320);
            var media = await MediaProbe.Probe(FfmpegMedia.Tool, photo, CancellationToken.None);
            Assert.Equal(MediaKind.Image, media.Kind);
            var watch = Stopwatch.StartNew();
            var mask = await service.Build(FfmpegMedia.Tool, media, null, CancellationToken.None);
            _out.WriteLine("사진 마스크 " + watch.ElapsedMilliseconds + "ms · " + OnnxRuntimeLibrary.LoadedPath);
            Assert.Equal(".png", Path.GetExtension(mask));
            Assert.Equal(mask, service.Find(photo));
            Assert.Equal("png,448,256,gray", Probe("-show_entries", "stream=codec_name,pix_fmt,width,height", "-of", "csv=p=0", mask));
            var pixels = MaskFrame(mask, 0, 448, 256);
            var head = Mean(pixels, 448, 256, 640, 360, 300, 110, 340, 150);
            var body = Mean(pixels, 448, 256, 640, 360, 260, 280, 380, 350);
            var wall = Math.Max(Mean(pixels, 448, 256, 640, 360, 0, 0, 120, 120), Mean(pixels, 448, 256, 640, 360, 540, 0, 640, 200));
            _out.WriteLine("머리 " + head + " · 몸통 " + body + " · 벽 " + wall);
            Assert.True(head > 200 && body > 200, "사람 자리가 희지 않습니다: 머리 " + head + ", 몸통 " + body);
            Assert.True(wall < 30, "배경이 검지 않습니다: " + wall);

            // 원본을 바꿔 넣으면 옛 마스크는 쓰지 않고, 새로 만들면 옛 것은 지운다
            File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(service.Find(photo));
            var rebuilt = await service.Build(FfmpegMedia.Tool, media, null, CancellationToken.None);
            Assert.NotEqual(mask, rebuilt);
            Assert.False(File.Exists(mask));
            Assert.Single(Directory.GetFiles(service.MaskFolder));
        }

        [PersonMaskFact]
        public async Task Video_MaskHasSourceTimeline_FollowsThePerson_CachedAndCancellable()
        {
            var service = new PersonMaskService(Path.Combine(_folder, "person-mask"));
            // 960×360 그림에서 640×360 창을 초당 60픽셀씩 옮긴다 — 사람(가운데 x 480)이 화면에서 왼쪽으로 움직인다
            var still = Person(_folder, "still.png", 960, 480);
            var video = Path.Combine(_folder, "moving.mp4");
            Ffmpeg("-loop", "1", "-framerate", "25", "-t", "2", "-i", still, "-vf", "crop=w=640:h=360:x='60*t':y=0", "-c:v", "mpeg4", "-q:v", "2", video);
            var media = await MediaProbe.Probe(FfmpegMedia.Tool, video, CancellationToken.None);
            Assert.Equal(MediaKind.Video, media.Kind);

            // 처음 진행률이 오면 취소 — 만들던 파일이 남지 않는다
            using (var cancel = new CancellationTokenSource())
            {
                var canceled = service.Build(FfmpegMedia.Tool, media, new SyncProgress(s =>
                {
                    if (s.Stage == "인물 분리 중")
                        cancel.Cancel();
                }), cancel.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            }
            Assert.Null(service.Find(video));
            Assert.Empty(Directory.GetFiles(service.MaskFolder));

            var reports = new List<FfmpegDownloader.Status>();
            var watch = Stopwatch.StartNew();
            var mask = await service.Build(FfmpegMedia.Tool, media, new SyncProgress(s => { lock (reports) reports.Add(s); }), CancellationToken.None);
            var elapsed = watch.ElapsedMilliseconds;
            Assert.Equal(".mkv", Path.GetExtension(mask));
            Assert.Equal(mask, service.Find(video));
            var last = reports.Last();
            _out.WriteLine("동영상 마스크 " + elapsed + "ms(" + last.Done + "프레임, 한 장 약 " + elapsed / Math.Max(1, last.Done) + "ms) · " + new FileInfo(mask).Length + "바이트");
            Assert.Equal(50, last.Total);
            Assert.Equal(50, last.Done);

            // 원본과 같은 시간축: 흑백 FFV1, 프레임 수·길이가 같다
            Assert.Equal("ffv1,448,256,gray", Probe("-show_entries", "stream=codec_name,pix_fmt,width,height", "-of", "csv=p=0", mask));
            var frames = Probe("-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", mask);
            Assert.Equal(Probe("-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", video), frames);
            var duration = double.Parse(Probe("-show_entries", "format=duration", "-of", "csv=p=0", mask), CultureInfo.InvariantCulture);
            Assert.InRange(duration, media.DurationSeconds - 0.05, media.DurationSeconds + 0.05);

            // 사람을 따라간다: 첫 프레임에서 몸통 오른쪽(x 600)은 사람, 마지막 프레임(1.96초, 117픽셀 옮김)에서는 벽
            var first = MaskFrame(mask, 0, 448, 256);
            var end = MaskFrame(mask, 1.96, 448, 256);
            var headFirst = Mean(first, 448, 256, 640, 360, 460, 110, 500, 150);
            var headEnd = Mean(end, 448, 256, 640, 360, 343, 110, 383, 150);
            var rightFirst = Mean(first, 448, 256, 640, 360, 590, 290, 610, 330);
            var rightEnd = Mean(end, 448, 256, 640, 360, 590, 290, 610, 330);
            var wall = Mean(end, 448, 256, 640, 360, 0, 0, 100, 100);
            _out.WriteLine("머리 " + headFirst + " → " + headEnd + " · 몸통 오른쪽 " + rightFirst + " → " + rightEnd + " · 벽 " + wall);
            Assert.True(headFirst > 200 && headEnd > 200, "머리가 희지 않습니다");
            Assert.True(rightFirst > 200, "첫 프레임 몸통 오른쪽이 희지 않습니다: " + rightFirst);
            Assert.True(rightEnd < 40, "마지막 프레임 몸통이 옮겨 가지 않았습니다: " + rightEnd);
            Assert.True(wall < 30, "배경이 검지 않습니다: " + wall);

            // 다시 부르면 만들어 둔 것을 그대로(다시 만들지 않음)
            var written = File.GetLastWriteTimeUtc(mask);
            watch.Restart();
            Assert.Equal(mask, await service.Build(FfmpegMedia.Tool, media, null, CancellationToken.None));
            Assert.True(watch.ElapsedMilliseconds < 1000, "캐시를 쓰지 않았습니다: " + watch.ElapsedMilliseconds + "ms");
            Assert.Equal(written, File.GetLastWriteTimeUtc(mask));
            Assert.Single(Directory.GetFiles(service.MaskFolder));
        }
    }
}
