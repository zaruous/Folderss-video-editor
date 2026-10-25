using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>시험용 그리개: 미리 만든 PNG를 복사한다(없으면 몇 바이트만 쓴다). 그린 횟수를 센다.</summary>
    internal sealed class FakeTextRasterizer : ITextRasterizer
    {
        private static int _versions = 1000;
        private readonly string _source;
        private readonly int _delay;
        private readonly Exception _error;
        private int _calls;

        public FakeTextRasterizer(string source = null, int delay = 0, Exception error = null)
        {
            _source = source;
            _delay = delay;
            _error = error;
        }

        // 인스턴스마다 버전이 달라 같은 시험 프로세스의 다른 시험과 캐시 파일을 나누지 않는다
        public int Version { get; } = Interlocked.Increment(ref _versions);

        public int Calls { get { return Volatile.Read(ref _calls); } }

        /// <summary>마지막 Render가 끝남(제한 시간을 넘겨 버려진 그리기도 끝까지 돈다).</summary>
        public ManualResetEventSlim Done { get; } = new ManualResetEventSlim();

        public void Render(TextImageRequest request, string pngPath, CancellationToken cancellation)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                // 취소 신호를 보지 않는 그리개 흉내
                if (_delay > 0)
                    Thread.Sleep(_delay);
                if (_source != null)
                    File.Copy(_source, pngPath);
                else
                    File.WriteAllBytes(pngPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                // 반쯤 쓴 뒤 실패하는 그리개 흉내
                if (_error != null)
                    throw _error;
            }
            finally
            {
                Done.Set();
            }
        }
    }

    public class StyledTextRenderTests : IDisposable
    {
        private const string CacheName = @"^st-[0-9a-f]{64}\.png$";
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-styled-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        private static (Project Project, Clip Text) StyledProject(string text = "안녕 ABC")
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateText(text, 0, 30);
            clip.Style = TextStyle.FromLegacy(clip);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static RenderOptions Options(ITextRasterizer rasterizer, string temp = @"C:\Temp", int width = 640, int height = 360)
        {
            return new RenderOptions { From = 0, To = 30, Width = width, Height = height, Audio = false, FontFile = @"C:\Windows\Fonts\malgun.ttf", TempFolder = temp, TextRasterizer = rasterizer };
        }

        /// <summary>필터 문자열(글을 담는 임시 파일 이름은 매번 다르므로 지운다).</summary>
        private static string Filter(Project project, RenderOptions options)
        {
            return System.Text.RegularExpressions.Regex.Replace(RenderGraph.Build(project, options).Filter, "fv-text-[0-9a-f]+", "fv-text-*");
        }

        private static string ImagePath(RenderPlan plan)
        {
            return Assert.Single(plan.TextImages).Key;
        }

        // ---------- 그래프 ----------

        [Fact]
        public void Styled_ReadsPngAndPads_WithoutDrawtext()
        {
            var (project, _) = StyledProject();
            var rasterizer = new FakeTextRasterizer();
            var plan = RenderGraph.Build(project, Options(rasterizer));
            var path = ImagePath(plan);
            Assert.Matches(CacheName, Path.GetFileName(path));
            Assert.Equal(new[] { "-loop", "1", "-framerate", "30/1", "-t", "1", "-f", "image2", "-pattern_type", "none", "-i", path }, Assert.Single(plan.Inputs));
            Assert.Contains("[0:v]setpts=PTS-STARTPTS,format=rgba,crop=w='min(iw,640)':h='min(ih,360)',pad=w=640:h=360:x=(ow-iw)/2:y=(oh-ih)/2:color=black@0.0", plan.Filter);
            Assert.DoesNotContain("drawtext", plan.Filter);
            Assert.Empty(plan.TextFiles);
            Assert.Same(rasterizer, plan.TextRasterizer);
            // 이미지 클립과 같은 공통 체인(맞춤 × 크기 → overlay)을 탄다
            Assert.Contains("scale=w=640:h=360:force_original_aspect_ratio=decrease", plan.Filter);
            Assert.Contains("[base][c0]overlay=", plan.Filter);
        }

        [Fact]
        public void Styled_RequestUsesOutputPixels()
        {
            var (project, clip) = StyledProject();
            clip.FontSize = 80;
            clip.Style.Shadow.Distance = 9;
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer(), width: 320, height: 180));
            var request = Assert.Single(plan.TextImages).Value;
            Assert.Equal("안녕 ABC", request.Text);
            Assert.Equal(40, request.FontPixelSize, 6);
            Assert.Equal(0.5, request.EffectScale, 6);
            Assert.Equal(320, request.CanvasWidth);
            Assert.Equal(180, request.CanvasHeight);
            // 길이 값은 프로젝트 픽셀 그대로 두고 그리개가 EffectScale을 곱한다
            Assert.Equal(9, request.Style.Shadow.Distance);
            // 요청의 스타일은 깊은 복사 — 그린 뒤 클립을 고쳐도 요청은 그대로
            Assert.NotSame(clip.Style, request.Style);
            clip.Style.Shadow.Distance = 50;
            Assert.Equal(9, request.Style.Shadow.Distance);
        }

        [Fact]
        public void Styled_RequestStyleIsNormalized()
        {
            var (project, clip) = StyledProject();
            clip.Style.Outline.Width = 1e9;
            clip.Style.FontFamily = @"\\host\share\#evil";
            var request = Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer())).TextImages).Value;
            Assert.Equal(50, request.Style.Outline.Width);
            Assert.Equal(TextStyle.DefaultFont, request.Style.FontFamily);
        }

        [Fact]
        public void HugeCanvas_RequestsUnderPixelLimit_AndScalesBack()
        {
            var (project, clip) = StyledProject();
            project.Width = 8192;
            project.Height = 8192;
            clip.FontSize = 400;
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer(), width: 8192, height: 8192));
            var request = Assert.Single(plan.TextImages).Value;
            Assert.True((double)request.CanvasWidth * request.CanvasHeight <= StyledTextLayout.MaxPixels, request.CanvasWidth + "×" + request.CanvasHeight);
            // 줄인 비율만큼 글자·효과도 줄여 그리고, 그래프가 다시 키워 캔버스 크기에 놓는다
            Assert.Equal(400.0 * request.CanvasWidth / 8192, request.FontPixelSize, 6);
            Assert.Equal((double)request.CanvasWidth / 8192, request.EffectScale, 6);
            Assert.Contains("format=rgba,scale=w='iw*8192/4000':h='ih*8192/4000',crop=", plan.Filter);
            Assert.Contains("pad=w=8192:h=8192", plan.Filter);
            // 4K(830만 화소)는 그대로
            project.Width = 3840;
            project.Height = 2160;
            var uhd = RenderGraph.Build(project, Options(new FakeTextRasterizer(), width: 3840, height: 2160));
            Assert.Equal(3840, Assert.Single(uhd.TextImages).Value.CanvasWidth);
            Assert.DoesNotContain("scale=w='iw*", uhd.Filter);
        }

        [Fact]
        public void Build_DoesNotWriteFiles()
        {
            var (project, _) = StyledProject();
            var rasterizer = new FakeTextRasterizer();
            var plan = RenderGraph.Build(project, Options(rasterizer, _folder));
            Assert.Single(plan.TextImages);
            Assert.False(Directory.Exists(_folder));
            Assert.Equal(0, rasterizer.Calls);
        }

        [Fact]
        public void OneStyleValue_ChangesPath_AndSameValueReusesIt()
        {
            var (project, clip) = StyledProject();
            var rasterizer = new FakeTextRasterizer();
            var before = ImagePath(RenderGraph.Build(project, Options(rasterizer)));
            clip.Style.Shadow.Distance += 1;
            var changed = ImagePath(RenderGraph.Build(project, Options(rasterizer)));
            Assert.NotEqual(before, changed);
            clip.Style.Shadow.Distance -= 1;
            Assert.Equal(before, ImagePath(RenderGraph.Build(project, Options(rasterizer))));
        }

        [Fact]
        public void NoRasterizerOrNoStyle_KeepsDrawtextGraph()
        {
            var (project, clip) = StyledProject();
            clip.Style = null;
            var expected = Filter(project, Options(null));
            Assert.Contains("drawtext", expected);
            // 스타일이 없으면 그리개가 있어도 지금과 같다
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer()));
            Assert.Empty(plan.TextImages);
            Assert.Empty(plan.Inputs);
            Assert.Equal(expected, Filter(project, Options(new FakeTextRasterizer())));
            // 그리개가 없으면 스타일이 있어도 지금과 같다(글자 색 = 첫 채우기 색)
            clip.Style = TextStyle.FromLegacy(clip);
            Assert.Equal(expected, Filter(project, Options(null)));
            Assert.Empty(RenderGraph.Build(project, Options(null)).TextImages);
        }

        [Theory]
        [InlineData("#00ff00", "'0x00FF00'")]
        [InlineData("#8000FF00", "'0x00FF0080'")]
        public void NoRasterizer_DrawtextUsesFirstFillColor(string color, string expected)
        {
            var (project, clip) = StyledProject();
            clip.TextColor = "#FF0000";
            clip.Style.Fill.Colors = new List<string> { color, "#0000FF" };
            Assert.Contains("fontcolor=" + expected, RenderGraph.Build(project, Options(null)).Filter);
        }

        [Fact]
        public void LongText_FallsBackToDrawtext()
        {
            var (project, clip) = StyledProject(new string('가', RenderGraph.MaxStyledTextLength));
            var rasterizer = new FakeTextRasterizer();
            Assert.Single(RenderGraph.Build(project, Options(rasterizer)).TextImages);
            clip.Text += "나";
            var plan = RenderGraph.Build(project, Options(rasterizer));
            Assert.Empty(plan.TextImages);
            Assert.Contains("drawtext", plan.Filter);
        }

        // ---------- 키 ----------

        private static TextImageRequest Request()
        {
            var style = new TextStyle();
            style.Normalize();
            return new TextImageRequest { Text = "가나 AB", Style = style, FontPixelSize = 72, EffectScale = 1, CanvasWidth = 1920, CanvasHeight = 1080 };
        }

        [Fact]
        public void Key_IsLowerHexSha256_AndStable()
        {
            var key = Request().Key(1);
            Assert.Matches("^[0-9a-f]{64}$", key);
            Assert.Equal(key, Request().Key(1));
            Assert.NotEqual(key, Request().Key(2));
            var culture = CultureInfo.CurrentCulture;
            try
            {
                // 소수점이 쉼표인 문화권에서도 같은 키
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var request = Request();
                request.FontPixelSize = 72.5;
                var german = request.Key(1);
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                Assert.Equal(german, request.Key(1));
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        [Fact]
        public void Key_ChangesWithEveryPixelValue()
        {
            var edits = new List<Action<TextImageRequest>>
            {
                r => r.Text = "가나 AC",
                r => r.FontPixelSize = 73,
                r => r.EffectScale = 0.5,
                r => r.CanvasWidth = 1280,
                r => r.CanvasHeight = 720,
                r => r.Style.FontFamily = "Arial",
                r => r.Style.Bold = true,
                r => r.Style.Italic = true,
                r => r.Style.LetterSpacing = 10,
                r => r.Style.LineSpacing = 1.5,
                r => r.Style.Align = TextAlign.Left,
                r => r.Style.Fill.Kind = FillKind.Linear,
                r => r.Style.Fill.Colors = new List<string> { "#FFFFFF", "#000000" },
                r => r.Style.Fill.Angle = 90,
                r => r.Style.Outline.Enabled = true,
                r => r.Style.Outline.Color = "#FF0000",
                r => r.Style.Outline.Width = 5,
                r => r.Style.Outline.Join = LineJoinKind.Miter,
                r => r.Style.Shadow.Enabled = true,
                r => r.Style.Shadow.Color = "#FF0000",
                r => r.Style.Shadow.Distance = 7,
                r => r.Style.Shadow.Angle = 90,
                r => r.Style.Shadow.Blur = 7,
                r => r.Style.Shadow.Opacity = 0.5,
                r => r.Style.Glow.Enabled = true,
                r => r.Style.Glow.Color = "#FF0000",
                r => r.Style.Glow.Size = 13,
                r => r.Style.Glow.Opacity = 0.5,
                r => r.Style.Reflection.Enabled = true,
                r => r.Style.Reflection.Size = 60,
                r => r.Style.Reflection.Gap = 5,
                r => r.Style.Reflection.Opacity = 0.5,
                r => r.Style.Warp.Kind = WarpKind.Wave,
                r => r.Style.Warp.Amount = 30
            };
            var keys = new HashSet<string> { Request().Key(1) };
            foreach (var edit in edits)
            {
                var request = Request();
                edit(request);
                Assert.True(keys.Add(request.Key(1)), "키가 겹침: " + keys.Count);
            }
        }

        [Fact]
        public void Key_CoversEveryProperty()
        {
            // 요청·스타일에 필드를 더하고 키에 넣기를 잊으면 여기서 걸린다(예전 PNG를 잘못 다시 쓰게 됨)
            var baseline = Request().Key(1);
            var paths = PropertyPaths(typeof(TextImageRequest), new List<PropertyInfo>()).ToList();
            Assert.True(paths.Count >= 34, "속성 " + paths.Count + "개");
            foreach (var path in paths)
            {
                var request = Request();
                object owner = request;
                foreach (var property in path.Take(path.Count - 1))
                    owner = property.GetValue(owner);
                var last = path[path.Count - 1];
                last.SetValue(owner, Changed(last.PropertyType, last.GetValue(owner)));
                Assert.True(request.Key(1) != baseline, string.Join(".", path.Select(p => p.Name)) + "이(가) 키에 들어가지 않습니다");
            }
        }

        /// <summary>쓸 수 있는 속성의 경로(스타일 묶음은 안으로 들어간다).</summary>
        private static IEnumerable<List<PropertyInfo>> PropertyPaths(Type type, List<PropertyInfo> prefix)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                var path = new List<PropertyInfo>(prefix) { property };
                if (property.PropertyType.IsClass && property.PropertyType.Namespace == typeof(TextStyle).Namespace)
                {
                    foreach (var inner in PropertyPaths(property.PropertyType, path))
                        yield return inner;
                }
                else
                {
                    yield return path;
                }
            }
        }

        /// <summary>정규화해도 원래와 다른 값.</summary>
        private static object Changed(Type type, object value)
        {
            if (type == typeof(bool))
                return !(bool)value;
            if (type == typeof(double))
                return (double)value == 0 ? 1.0 : (double)value / 2;
            if (type == typeof(int))
                return (int)value + 2;
            if (type == typeof(string))
                return ((string)value ?? "").StartsWith("#", StringComparison.Ordinal) ? "#123456" : "다른 값";
            if (type == typeof(List<string>))
                return new List<string>((List<string>)value) { "#123456" };
            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                return values.GetValue((Array.IndexOf(values, value) + 1) % values.Length);
            }
            throw new InvalidOperationException("시험이 바꿀 줄 모르는 형식입니다: " + type);
        }

        [Fact]
        public void Key_DistinguishesLoneSurrogates()
        {
            var a = Request();
            a.Text = "\uD800";
            var b = Request();
            b.Text = "\uDC00";
            Assert.NotEqual(a.Key(1), b.Key(1));
        }

        // ---------- 보안 ----------

        [Theory]
        [InlineData(@"..\..\x")]
        [InlineData("../../x/\0\u0001\r\n")]
        [InlineData("\uD800 짝 없는 대리 문자")]
        [InlineData("C:\\Windows\\win.ini:$DATA")]
        public void MaliciousText_FileNameIsHashInsideCacheFolder(string text)
        {
            var (project, clip) = StyledProject(text);
            clip.Id = @"..\..\evil";
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer(), _folder));
            var path = ImagePath(plan);
            Assert.Matches(CacheName, Path.GetFileName(path));
            Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "styled-text")), Path.GetFullPath(Path.GetDirectoryName(path)));
            Assert.DoesNotContain("evil", path);
        }

        [Fact]
        public void TenThousandChars_KeyIsHash_AndDrawtextFileStaysInTemp()
        {
            var text = string.Concat(Enumerable.Repeat(@"..\x" + "\u0001", 2000));
            var request = Request();
            request.Text = text;
            Assert.Matches("^[0-9a-f]{64}$", request.Key(1));
            var (project, _) = StyledProject(text);
            var plan = RenderGraph.Build(project, Options(new FakeTextRasterizer(), _folder));
            Assert.Empty(plan.TextImages);
            var file = Assert.Single(plan.TextFiles).Key;
            Assert.Equal(Path.GetFullPath(_folder), Path.GetFullPath(Path.GetDirectoryName(file)));
        }

        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static string Touch(string folder, string name)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "x");
            return path;
        }

        [Fact]
        public void Cleanup_RemovesOnlyCacheFilesOfEndedProcesses()
        {
            // 끝난 프로세스(이런 pid는 없다)
            var dead = Path.Combine(_folder, "2147483644", "styled-text");
            var png = Touch(dead, "st-" + Hash + ".png");
            var part = Touch(dead, "st-" + Hash + ".png." + Guid.NewGuid().ToString("N") + ".part");
            var keep = new List<string>
            {
                Touch(dead, "notes.txt"),
                Touch(dead, "st-" + Hash.Substring(1) + ".png"),
                Touch(dead, "st-" + new string('g', 64) + ".png"),
                Touch(dead, "st-" + Hash + ".png.bak"),
                Touch(dead, "xst-" + Hash + ".png"),
                // 하위 폴더 안은 보지 않는다
                Touch(Path.Combine(dead, "sub"), "st-" + Hash + ".png"),
                // 살아 있는 프로세스(이 시험)
                Touch(Path.Combine(_folder, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "styled-text"), "st-" + Hash + ".png"),
                // pid가 숫자가 아닌 폴더
                Touch(Path.Combine(_folder, "abc", "styled-text"), "st-" + Hash + ".png"),
                Touch(Path.Combine(_folder, "-4", "styled-text"), "st-" + Hash + ".png")
            };
            TextImageCache.Cleanup(_folder);
            Assert.False(File.Exists(png));
            Assert.False(File.Exists(part));
            Assert.All(keep, path => Assert.True(File.Exists(path), path));
        }

        [Fact]
        public void Cleanup_DoesNotFollowLinks()
        {
            var outside = Path.Combine(_folder, "outside");
            var target = Touch(outside, "st-" + Hash + ".png");
            var pid = Path.Combine(_folder, "root", "2147483640");
            Directory.CreateDirectory(pid);
            var link = Path.Combine(pid, "styled-text");
            if (!TryLink(link, outside))
                return;
            try
            {
                Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
                TextImageCache.Cleanup(Path.Combine(_folder, "root"));
                Assert.True(File.Exists(target));
            }
            finally
            {
                // 링크 자체만 지운다(뒷정리의 재귀 삭제가 링크를 따라가지 않게)
                Directory.Delete(link);
            }
        }

        /// <summary>폴더 링크를 만든다. Windows에서 심볼릭 링크 권한이 없으면(개발자 모드 꺼짐) 권한이 필요 없는 정션으로.</summary>
        private static bool TryLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                if (!OperatingSystem.IsWindows())
                    return false;
            }
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
                info.ArgumentList.Add(arg);
            using (var process = System.Diagnostics.Process.Start(info))
                process.WaitForExit();
            return Directory.Exists(link);
        }

        [Fact]
        public void Cleanup_MissingFolder_DoesNothing()
        {
            TextImageCache.Cleanup(Path.Combine(_folder, "none"));
            TextImageCache.Cleanup(null);
        }

        // ---------- 캐시 ----------

        private string CachePath(TextImageRequest request, ITextRasterizer rasterizer)
        {
            return Path.Combine(_folder, "styled-text", "st-" + request.Key(rasterizer.Version) + ".png");
        }

        [Fact]
        public async Task Ensure_SameRequestTwiceAtOnce_RendersOnce()
        {
            var rasterizer = new FakeTextRasterizer(delay: 300);
            var request = Request();
            var path = CachePath(request, rasterizer);
            using (var start = new Barrier(2))
            {
                var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    TextImageCache.Ensure(rasterizer, path, request, CancellationToken.None);
                })).ToArray();
                await Task.WhenAll(tasks);
            }
            Assert.Equal(1, rasterizer.Calls);
            Assert.True(File.Exists(path));
            // 있으면 다시 그리지 않는다
            TextImageCache.Ensure(rasterizer, path, request, CancellationToken.None);
            Assert.Equal(1, rasterizer.Calls);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)));
        }

        [Fact]
        public void Ensure_RasterizerThrows_LeavesNoFiles()
        {
            var rasterizer = new FakeTextRasterizer(error: new InvalidOperationException("그리기 실패"));
            var request = Request();
            var path = CachePath(request, rasterizer);
            Assert.Throws<InvalidOperationException>(() => TextImageCache.Ensure(rasterizer, path, request, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)));
        }

        [Fact]
        public void Ensure_Cancelled_Throws()
        {
            var rasterizer = new FakeTextRasterizer();
            var request = Request();
            var path = CachePath(request, rasterizer);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => TextImageCache.Ensure(rasterizer, path, request, cancel.Token));
            }
            Assert.Equal(0, rasterizer.Calls);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Ensure_RasterizerIgnoresTimeout_ReturnsWithoutHoldingGate()
        {
            // 취소 신호를 보지 않는 그리개(2초)라도 제한(0.2초)이 지나면 기다리지 않고 돌아온다
            var slow = new FakeTextRasterizer(delay: 2000);
            var request = Request();
            var path = CachePath(request, slow);
            var watch = Stopwatch.StartNew();
            Assert.Throws<TimeoutException>(() => TextImageCache.Ensure(slow, path, request, CancellationToken.None, TimeSpan.FromMilliseconds(200)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), "기다린 시간 " + watch.Elapsed);
            // 문을 쥐고 있지 않으므로 같은 경로를 바로 다시 그릴 수 있다
            var fast = new FakeTextRasterizer();
            TextImageCache.Ensure(fast, path, request, CancellationToken.None);
            Assert.Equal(1, fast.Calls);
            // 늦게 끝난 그리기가 쓴 .part는 끝난 뒤 지운다
            Assert.True(slow.Done.Wait(TimeSpan.FromSeconds(10)));
            var folder = Path.GetDirectoryName(path);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Directory.GetFiles(folder, "*.part").Length > 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.Equal(new[] { path }, Directory.GetFiles(folder));
        }

        [Fact]
        public void PrepareTextImages_FillsCacheOnce()
        {
            // 재생 전에 배경에서 그려 두면, 같은 크기의 재생 시작(StartVideo)은 캐시만 읽는다
            var (project, _) = StyledProject();
            var rasterizer = new FakeTextRasterizer();
            PreviewRenderer.PrepareTextImages(project, 0, 30, 320, 180, rasterizer, CancellationToken.None);
            Assert.Equal(1, rasterizer.Calls);
            PreviewRenderer.PrepareTextImages(project, 10, 30, 320, 180, rasterizer, CancellationToken.None);
            Assert.Equal(1, rasterizer.Calls);
            PreviewRenderer.PrepareTextImages(project, 0, 30, 320, 180, null, CancellationToken.None);
        }

        [Fact]
        public void Ensure_EmptyPng_IsNotCached()
        {
            Directory.CreateDirectory(_folder);
            var empty = Path.Combine(_folder, "empty.png");
            File.WriteAllBytes(empty, new byte[0]);
            var rasterizer = new FakeTextRasterizer(empty);
            var request = Request();
            var path = CachePath(request, rasterizer);
            Assert.Throws<InvalidOperationException>(() => TextImageCache.Ensure(rasterizer, path, request, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)));
        }

        [Theory]
        [InlineData("st-0000000000000000000000000000000000000000000000000000000000000000.png\n")]
        [InlineData("st-x.png")]
        [InlineData("..\\st-0123.png")]
        [InlineData("evil.png")]
        public void Ensure_RejectsNonCacheName(string name)
        {
            var rasterizer = new FakeTextRasterizer();
            Assert.Throws<ArgumentException>(() => TextImageCache.Ensure(rasterizer, Path.Combine(_folder, name), Request(), CancellationToken.None));
            Assert.Equal(0, rasterizer.Calls);
        }

        [Fact]
        public void Trim_OverLimit_RemovesOldestUnusedFirst()
        {
            var folder = Path.Combine(_folder, "styled-text");
            var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            string Cache(char c, int minutesAgo)
            {
                var path = Path.Combine(folder, "st-" + new string(c, 64) + ".png");
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, new byte[100]);
                File.SetLastAccessTimeUtc(path, now.AddMinutes(-minutesAgo));
                return path;
            }
            var oldest = Cache('a', 60);
            var old = Cache('b', 30);
            var recent = Cache('c', 1);
            var other = Touch(folder, "notes.txt");
            // 300바이트 중 200바이트까지: 가장 오래된 것 하나만 지우면 된다
            TextImageCache.Trim(folder, 200, now);
            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(old));
            // 한도가 아주 작아도 5분 안에 쓴 파일과 패턴 밖 파일은 남긴다
            TextImageCache.Trim(folder, 1, now);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
        }

        // ---------- 배치 ----------

        [Fact]
        public void Layout_PlainText_IsEvenBoxCenteredInPng()
        {
            var style = new TextStyle();
            var layout = StyledTextLayout.Fit(101.2, 40.6, style, 1, 1920, 1080);
            Assert.Equal(0, layout.Width % 2);
            Assert.Equal(0, layout.Height % 2);
            Assert.InRange(layout.Width, 102, 104);
            Assert.InRange(layout.Height, 42, 44);
            Assert.Equal(layout.Width / 2.0, layout.OriginX + 101.2 / 2, 6);
            Assert.Equal(layout.Height / 2.0, layout.OriginY + 40.6 / 2, 6);
        }

        [Fact]
        public void Layout_EffectMarginsAreSymmetric()
        {
            var style = new TextStyle();
            style.Shadow.Enabled = true;
            style.Shadow.Distance = 20;
            style.Shadow.Angle = 0;
            style.Shadow.Blur = 5;
            var spread = StyledTextLayout.Spread(style, 1, 50);
            // 그림자는 오른쪽으로 20 + 흐림 5 — 왼쪽도 같게 맞춘다
            Assert.Equal(25, spread.Horizontal, 6);
            Assert.Equal(5, spread.Vertical, 6);
            var layout = StyledTextLayout.Fit(100, 50, style, 1, 1920, 1080);
            Assert.Equal(25 + StyledTextLayout.EdgeMargin, layout.OriginX, 6);
            Assert.Equal(5 + StyledTextLayout.EdgeMargin, layout.OriginY, 6);
            // EffectScale이 반이면 여백도 반
            Assert.Equal(12.5, StyledTextLayout.Spread(style, 0.5, 25).Horizontal, 6);
        }

        [Fact]
        public void Layout_AllEffectsWiden_AndOffMeansNoMargin()
        {
            var style = new TextStyle();
            Assert.Equal(0, StyledTextLayout.Spread(style, 1, 50).Horizontal);
            Assert.Equal(0, StyledTextLayout.Spread(style, 1, 50).Vertical);
            style.Outline.Enabled = true;
            style.Outline.Width = 10;
            // 윤곽선은 글자 밖으로 두께만큼(펜은 두 배 두께, 바깥 절반만 보임), 각지게는 모서리 끝까지 두 배
            Assert.Equal(10, StyledTextLayout.Spread(style, 1, 50).Horizontal, 6);
            style.Outline.Join = LineJoinKind.Miter;
            Assert.Equal(20, StyledTextLayout.Spread(style, 1, 50).Horizontal, 6);
            style.Outline.Enabled = false;
            style.Glow.Enabled = true;
            style.Glow.Size = 12;
            Assert.True(StyledTextLayout.Spread(style, 1, 50).Horizontal >= 12);
            style.Glow.Enabled = false;
            style.Reflection.Enabled = true;
            style.Reflection.Size = 50;
            style.Reflection.Gap = 4;
            // 반사는 아래로 간격 + 높이 50% — 위아래 대칭이므로 위에도 같은 여백
            var reflection = StyledTextLayout.Spread(style, 1, 40);
            Assert.Equal(0, reflection.Horizontal);
            Assert.Equal(24, reflection.Vertical, 6);
        }

        [Fact]
        public void Layout_LargerThanCanvas_IsCut()
        {
            var style = new TextStyle();
            style.Shadow.Enabled = true;
            style.Shadow.Distance = 200;
            var layout = StyledTextLayout.Fit(5000, 3000, style, 2, 1920, 1080);
            Assert.Equal(1920, layout.Width);
            Assert.Equal(1080, layout.Height);
            Assert.Equal(1920 / 2.0, layout.OriginX + 2500, 6);
            // 빈 글·이상한 값이어도 2×2 이상
            var empty = StyledTextLayout.Fit(0, double.NaN, style, double.PositiveInfinity, 1920, 1080);
            Assert.True(empty.Width >= 2 && empty.Height >= 2);
            Assert.True(empty.Width <= 1920 && empty.Height <= 1080);
        }

        [Fact]
        public void ShadowOffset_ZeroDegreesIsRight_Clockwise()
        {
            var shadow = new TextShadow { Distance = 10, Angle = 0 };
            var right = StyledTextLayout.ShadowOffset(shadow, 1);
            Assert.Equal(10, right.X, 6);
            Assert.Equal(0, right.Y, 6);
            shadow.Angle = 90;
            var down = StyledTextLayout.ShadowOffset(shadow, 0.5);
            Assert.Equal(0, down.X, 6);
            Assert.Equal(5, down.Y, 6);
        }
    }

    /// <summary>실제 FFmpeg + 빨간 PNG(200×80)를 돌려주는 가짜 그리개.</summary>
    public class StyledTextFfmpegTests
    {
        private readonly ITestOutputHelper _out;

        public StyledTextFfmpegTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly Lazy<string> LazyRedText = new Lazy<string>(() =>
        {
            var path = Path.Combine(FfmpegMedia.Folder, "red-text.png");
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfmpegPath,
                new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=200x80", "-frames:v", "1", path },
                TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException("시험 PNG를 만들지 못했습니다: " + result.Error);
            return path;
        });

        private static (Project Project, Clip Text) StyledProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = 320;
            project.Height = 240;
            var clip = TimelineEdits.CreateText("가나다", 0, 30);
            clip.Style = TextStyle.FromLegacy(clip);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static byte[] Frame(Project project, long frame, ITextRasterizer rasterizer)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, 320, 240, CancellationToken.None, rasterizer).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        private static bool IsRed(byte[] bgra, int x, int y)
        {
            var i = (y * 320 + x) * 4;
            return bgra[i + 2] > 200 && bgra[i + 1] < 60 && bgra[i] < 60;
        }

        /// <summary>빨간 영역의 (왼쪽, 위, 오른쪽, 아래) — 끝 포함.</summary>
        private static (int Left, int Top, int Right, int Bottom) RedBox(byte[] bgra)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var y = 0; y < 240; y++)
            {
                for (var x = 0; x < 320; x++)
                {
                    if (!IsRed(bgra, x, y))
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            return (left, top, right, bottom);
        }

        [FfmpegFact]
        public void StyledText_CenterIsRed_AndBackgroundIsBlack()
        {
            var (project, _) = StyledProject();
            var rasterizer = new FakeTextRasterizer(LazyRedText.Value);
            var frame = Frame(project, 15, rasterizer);
            Assert.True(IsRed(frame, 160, 120), "가운데는 빨강");
            var corner = (frame[2], frame[1], frame[0]);
            Assert.True(corner.Item1 < 20 && corner.Item2 < 20 && corner.Item3 < 20, "모서리는 검정 " + corner);
            var box = RedBox(frame);
            _out.WriteLine("빨간 영역 " + box);
            Assert.Equal((60, 80, 259, 159), box);
            Assert.Equal(1, rasterizer.Calls);
        }

        [FfmpegFact]
        public void StyledText_MovesAndScalesLikeImage()
        {
            var (project, clip) = StyledProject();
            var rasterizer = new FakeTextRasterizer(LazyRedText.Value);
            var center = RedBox(Frame(project, 10, rasterizer));
            clip.X = 50;
            var moved = RedBox(Frame(project, 10, rasterizer));
            Assert.Equal(center.Left + 50, moved.Left);
            Assert.Equal(center.Top, moved.Top);
            clip.X = 0;
            clip.Scale = 0.5;
            var half = RedBox(Frame(project, 10, rasterizer));
            _out.WriteLine("가운데 " + center + " · 옮김 " + moved + " · 반 " + half);
            Assert.InRange(half.Right - half.Left + 1, 98, 102);
            Assert.InRange(half.Bottom - half.Top + 1, 38, 42);
            Assert.True(IsRed(Frame(project, 10, rasterizer), 160, 120));
            // 위치·크기는 PNG 바깥(그래프)에서 바뀌므로 다시 그리지 않는다
            Assert.Equal(1, rasterizer.Calls);
        }

        [FfmpegFact]
        public async Task StyledText_PercentInTempPath_StillReadsPng()
        {
            // 임시 폴더 경로의 %1d를 파일 이름 패턴으로 읽으면 "파일이 없다"로 실패한다
            var (project, _) = StyledProject();
            var rasterizer = new FakeTextRasterizer(LazyRedText.Value);
            var temp = Path.Combine(FfmpegMedia.Folder, "pct%1d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 15, To = 16, Width = 320, Height = 240, Audio = false, Preview = true, TempFolder = temp, TextRasterizer = rasterizer });
            string script;
            var args = RenderCommand.Arguments(FfmpegMedia.Tool, plan, new[] { "-map", "[vout]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out script);
            try
            {
                var frame = await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath, args, TimeSpan.FromMinutes(1), CancellationToken.None);
                Assert.True(IsRed(frame, 160, 120));
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }

        [FfmpegFact]
        public void StyledText_VisibleOnLastFrame()
        {
            var (project, clip) = StyledProject();
            var rasterizer = new FakeTextRasterizer(LazyRedText.Value);
            Assert.True(IsRed(Frame(project, clip.End - 1, rasterizer), 160, 120));
        }

        [FfmpegFact]
        public async Task Preview_EqualsExport()
        {
            var (project, _) = StyledProject();
            var rasterizer = new FakeTextRasterizer(LazyRedText.Value);
            var preview = Frame(project, 15, rasterizer);
            var output = Path.Combine(FfmpegMedia.Folder, "styled-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await Exporter.Export(FfmpegMedia.Tool, project, new ExportSettings
                {
                    OutputPath = output,
                    Width = 320,
                    Height = 240,
                    Encoder = "mpeg4",
                    Quality = 0,
                    From = 0,
                    To = project.Duration
                }, null, CancellationToken.None, rasterizer);
                var exported = await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath,
                    new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", "0.5", "-i", output, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" },
                    TimeSpan.FromMinutes(1), CancellationToken.None);
                Assert.Equal(preview.Length, exported.Length);
                long diff = 0;
                for (var i = 0; i < preview.Length; i++)
                {
                    if (i % 4 != 3)
                        diff += Math.Abs(preview[i] - exported[i]);
                }
                var mean = diff / (preview.Length * 0.75);
                _out.WriteLine("평균 차이 " + mean);
                Assert.True(mean < 4, "미리 보기와 내보내기의 평균 차이 " + mean);
                // 같은 PNG를 쓴다(캐시)
                Assert.Equal(1, rasterizer.Calls);
            }
            finally
            {
                File.Delete(output);
            }
        }
    }
}
