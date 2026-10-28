using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>
    /// 시험용 그리개: 200×80 단색 PNG를 C#으로 쓴다. 애니메이션 장이면 빨강 = 40 + 7 × 장 번호(클립 안)라 어느 장이 어디에 이어졌는지 색으로 보인다.
    /// 정지 그림은 빨강 255. 그린 횟수를 센다.
    /// </summary>
    internal sealed class FrameColorRasterizer : ITextRasterizer
    {
        private static int _versions = 5000;
        private int _calls;

        public int Version { get; } = Interlocked.Increment(ref _versions);

        public int Calls { get { return Volatile.Read(ref _calls); } }

        public static int Red(long frame)
        {
            return (int)Math.Min(255, 40 + 7 * frame);
        }

        public void Render(TextImageRequest request, string pngPath, CancellationToken cancellation)
        {
            Interlocked.Increment(ref _calls);
            var red = request.Animation == null ? 255 : Red(request.Animation.Frame);
            File.WriteAllBytes(pngPath, Png(200, 80, (byte)red, 0, 0, 255));
        }

        /// <summary>단색 RGBA PNG(필터 없음, zlib).</summary>
        public static byte[] Png(int width, int height, byte r, byte g, byte b, byte a)
        {
            var raw = new byte[height * (width * 4 + 1)];
            for (var y = 0; y < height; y++)
            {
                var row = y * (width * 4 + 1);
                for (var x = 0; x < width; x++)
                {
                    raw[row + 1 + x * 4] = r;
                    raw[row + 2 + x * 4] = g;
                    raw[row + 3 + x * 4] = b;
                    raw[row + 4 + x * 4] = a;
                }
            }
            byte[] compressed;
            using (var memory = new MemoryStream())
            {
                using (var zlib = new ZLibStream(memory, CompressionLevel.Fastest, true))
                    zlib.Write(raw, 0, raw.Length);
                compressed = memory.ToArray();
            }
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                var header = new byte[13];
                BigEndian(header, 0, width);
                BigEndian(header, 4, height);
                header[8] = 8;
                header[9] = 6;
                Chunk(png, "IHDR", header);
                Chunk(png, "IDAT", compressed);
                Chunk(png, "IEND", Array.Empty<byte>());
                return png.ToArray();
            }
        }

        private static void BigEndian(byte[] buffer, int at, int value)
        {
            buffer[at] = (byte)(value >> 24);
            buffer[at + 1] = (byte)(value >> 16);
            buffer[at + 2] = (byte)(value >> 8);
            buffer[at + 3] = (byte)value;
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            var length = new byte[4];
            BigEndian(length, 0, data.Length);
            png.Write(length);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(body);
            var crc = new byte[4];
            BigEndian(crc, 0, (int)Crc(body));
            png.Write(crc);
        }

        private static uint Crc(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc ^= value;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>#4 애니메이션을 실제 FFmpeg로: 동영상·사진의 끝 위치·크기, 닦아내기·서서히, 글자별 장 잇기, 미리 보기 = 내보내기.</summary>
    public class AnimationRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;

        public AnimationRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Project NewProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = Width;
            project.Height = Height;
            return project;
        }

        private static Clip Add(Project project, Track track, string path, long start, long imageFrames = 30)
        {
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(path), project.Rate, start, imageFrames);
            TimelineEdits.Place(project, track, clip, start);
            return clip;
        }

        private static ClipAnimation Animation(AnimationEffect effect, AnimationKind kind, long frames, AnimationEase ease = AnimationEase.Linear, Clip clip = null)
        {
            var animation = ClipAnimations.Create(ClipAnimations.Def(effect), kind, FrameRate.Default, clip);
            animation.Duration = frames;
            animation.Ease = ease;
            return animation;
        }

        private static byte[] Frame(Project project, long frame, ITextRasterizer rasterizer = null)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None, rasterizer).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        /// <summary>조건에 맞는 픽셀의 (왼쪽, 위, 오른쪽, 아래) — 없으면 (-1, -1, -1, -1).</summary>
        private static (int Left, int Top, int Right, int Bottom) Box(byte[] bgra, Func<int, int, int, bool> match)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var i = (y * Width + x) * 4;
                    if (!match(bgra[i + 2], bgra[i + 1], bgra[i]))
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            return right < 0 ? (-1, -1, -1, -1) : (left, top, right, bottom);
        }

        private static (int Left, int Top, int Right, int Bottom) BlueBox(byte[] bgra)
        {
            return Box(bgra, (r, g, b) => b > 150 && r < 90 && g < 90);
        }

        private static int MaxDiff(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            var max = 0;
            for (var i = 0; i < a.Length; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        /// <summary>파란 동영상(320×240)을 반 크기로 가운데 — 정지 상자 (80, 60)-(239, 179).</summary>
        private static (Project Project, Clip Clip) BlueProject()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            clip.Scale = 0.5;
            return (project, clip);
        }

        [FfmpegFact]
        public void VideoFly_StartsOffscreen_EndsExactlyWhereTheClipRests()
        {
            var (project, clip) = BlueProject();
            var still = Frame(project, 12);
            Assert.Equal((80, 60, 239, 179), BlueBox(still));
            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Fly, AnimationKind.In, 10) };
            clip.Animations[0].Direction = AnimationDirection.Left;
            Assert.Equal((-1, -1, -1, -1), BlueBox(Frame(project, 0)));
            // 반쯤: 오른쪽 가장자리가 화면 왼쪽 끝(-160)에서 제자리(80)까지의 가운데 — x = 80 + 0.5 × (−160 − 80) = −40
            var half = BlueBox(Frame(project, 5));
            _out.WriteLine("반쯤 " + half);
            Assert.Equal((0, 60, 119, 179), half);
            // 지정한 장(10)에 정확히 제자리 — 애니메이션이 없는 그림과 바이트까지 같다
            Assert.Equal(0, MaxDiff(still, Frame(project, 10)));
            Assert.Equal(0, MaxDiff(still, Frame(project, 12)));
        }

        [FfmpegFact]
        public void VideoZoomAndWipe_HalfwayHalf_EndExactlyFullSize()
        {
            var (project, clip) = BlueProject();
            var still = Frame(project, 12);
            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Zoom, AnimationKind.In, 10) };
            Assert.Equal((-1, -1, -1, -1), BlueBox(Frame(project, 0)));
            var half = BlueBox(Frame(project, 5));
            _out.WriteLine("확대 반쯤 " + half);
            Assert.InRange(half.Right - half.Left + 1, 79, 81);
            Assert.InRange(half.Bottom - half.Top + 1, 59, 61);
            Assert.InRange((half.Left + half.Right) / 2.0, 158.5, 160.5);
            Assert.Equal(0, MaxDiff(still, Frame(project, 10)));

            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Wipe, AnimationKind.In, 10) };
            clip.Animations[0].Direction = AnimationDirection.Left;
            var wipe = BlueBox(Frame(project, 5));
            _out.WriteLine("닦아내기 반쯤 " + wipe);
            Assert.Equal((80, 60, 159, 179), wipe);
            Assert.Equal(0, MaxDiff(still, Frame(project, 10)));
            // 사라지기(아래로): 끝에서 두 번째 장까지 남은 만큼만
            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Wipe, AnimationKind.Out, 10) };
            clip.Animations[0].Direction = AnimationDirection.Down;
            var vanish = BlueBox(Frame(project, clip.Duration - 5));
            _out.WriteLine("닦아내기 사라지기 반쯤 " + vanish);
            Assert.Equal((80, 120, 239, 179), vanish);
        }

        [FfmpegFact]
        public void ImageFade_HalfAlphaMidway_RotateEmphasis_HoldsTheAngle()
        {
            var project = NewProject();
            var red = Add(project, project.MainTrack, FfmpegMedia.RedImage, 0, 30);
            red.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Fade, AnimationKind.In, 10) };
            var middle = Frame(project, 5);
            var i = (120 * Width + 160) * 4;
            _out.WriteLine("서서히 반쯤 R=" + middle[i + 2]);
            Assert.InRange(middle[i + 2], 120, 135);
            Assert.True(Frame(project, 10)[i + 2] > 240);

            var (blue, clip) = BlueProject();
            var rotate = Animation(AnimationEffect.Rotate, AnimationKind.Emphasis, 10);
            rotate.Amount = 90;
            clip.Animations = new List<ClipAnimation> { rotate };
            var turned = BlueBox(Frame(blue, 15));
            _out.WriteLine("90° 돈 뒤 " + turned);
            Assert.InRange(turned.Right - turned.Left + 1, 118, 122);
            Assert.InRange(turned.Bottom - turned.Top + 1, 158, 162);
        }

        [FfmpegFact]
        public void Typewriter_JoinsFramesInOrder_HoldsBeforeAndAfter()
        {
            // 글자별: 장 번호를 색에 담은 그리개로 어느 장이 어느 자리에 이어졌는지 본다(지연 3, 길이 12 — 3~14장이 구간, 15장부터 쉬는 장)
            var project = NewProject();
            var text = TimelineEdits.CreateText("가나다", 0, 30);
            text.Style = TextStyle.FromLegacy(text);
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var typewriter = Animation(AnimationEffect.Typewriter, AnimationKind.In, 12, AnimationEase.Linear, text);
            typewriter.Delay = 3;
            text.Animations = new List<ClipAnimation> { typewriter };
            var rasterizer = new FrameColorRasterizer();
            int Expected(long f) => FrameColorRasterizer.Red(f < 3 ? 3 : f < 15 ? f : 15);
            int RedAt(byte[] bgra) => bgra[(120 * Width + 160) * 4 + 2];
            // 재생처럼 이어 그리기(그림 묶음 + 이어 붙이기)
            List<byte[]> frames;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 0, 30, Width, Height, out var files, rasterizer))
            {
                process.BeginErrorReadLine();
                frames = new List<byte[]>();
                var size = Width * Height * 4;
                while (true)
                {
                    var buffer = new byte[size];
                    if (!HelperProtocol.ReadExactly(process.StandardOutput.BaseStream, buffer, size))
                        break;
                    frames.Add(buffer);
                }
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Equal(30, frames.Count);
            _out.WriteLine("빨강: " + string.Join(" ", frames.Select(RedAt)));
            // 장마다 빨강이 7씩 다르다 — 색 변환 반올림(±4) 안에서 그 장의 색
            for (var f = 0; f < 30; f++)
                Assert.InRange(RedAt(frames[f]), Expected(f) - 4, Expected(f) + 4);
            // 한 장씩 그려도 같은 장
            foreach (var f in new long[] { 0, 3, 8, 14, 15, 29 })
                Assert.InRange(RedAt(Frame(project, f, rasterizer)), Expected(f) - 4, Expected(f) + 4);
        }

        [FfmpegFact]
        public async Task StyledText_InAndOut_PreviewEqualsExport()
        {
            // 완료 기준: 꾸민 글자에 나타나기·사라지기를 넣으면 미리 보기와 내보내기가 같다(프레임 픽셀 비교)
            var project = NewProject();
            var text = TimelineEdits.CreateText("가나다", 0, 30);
            text.Style = TextStyle.FromLegacy(text);
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var scatter = Animation(AnimationEffect.Scatter, AnimationKind.Out, 10, AnimationEase.Out, text);
            text.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Fly, AnimationKind.In, 12, AnimationEase.Out, text), scatter };
            text.Animations[0].Direction = AnimationDirection.Left;
            var rasterizer = new FrameColorRasterizer();
            var checks = new long[] { 4, 9, 16, 24, 27 };
            var previews = checks.Select(f => Frame(project, f, rasterizer)).ToList();
            var output = Path.Combine(FfmpegMedia.Folder, "anim-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                var drawn = new List<(int, int)>();
                await Exporter.Export(FfmpegMedia.Tool, project, new ExportSettings
                {
                    OutputPath = output,
                    Width = Width,
                    Height = Height,
                    Encoder = "mpeg4",
                    Quality = 0,
                    From = 0,
                    To = project.Duration
                }, null, CancellationToken.None, rasterizer, (done, total) => drawn.Add((done, total)));
                _out.WriteLine("내보내기 전에 그린 장 " + drawn.Count + (drawn.Count > 0 ? " (" + drawn[^1] + ")" : ""));
                for (var i = 0; i < checks.Length; i++)
                {
                    var exported = await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath,
                        new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\," + checks[i] + ")", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" },
                        TimeSpan.FromMinutes(1), CancellationToken.None);
                    Assert.Equal(previews[i].Length, exported.Length);
                    long diff = 0;
                    for (var k = 0; k < exported.Length; k++)
                    {
                        if (k % 4 != 3)
                            diff += Math.Abs(previews[i][k] - exported[k]);
                    }
                    var mean = diff / (exported.Length * 0.75);
                    _out.WriteLine(checks[i] + "장: 평균 차이 " + mean.ToString("0.00"));
                    Assert.True(mean < 4, checks[i] + "장 미리 보기와 내보내기의 평균 차이 " + mean);
                }
            }
            finally
            {
                File.Delete(output);
            }
        }

        /// <summary>미리 보기(한 장씩)와 내보낸 파일의 같은 장을 비교한다 — 색 채널 평균 차이가 4 미만(내보내기 부호화 손실 안).</summary>
        private async Task AssertPreviewEqualsExport(Project project, long[] checks, ITextRasterizer rasterizer = null)
        {
            var previews = checks.Select(f => Frame(project, f, rasterizer)).ToList();
            var output = Path.Combine(FfmpegMedia.Folder, "anim-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await Exporter.Export(FfmpegMedia.Tool, project, new ExportSettings
                {
                    OutputPath = output,
                    Width = Width,
                    Height = Height,
                    Encoder = "mpeg4",
                    Quality = 0,
                    From = 0,
                    To = project.Duration
                }, null, CancellationToken.None, rasterizer);
                for (var i = 0; i < checks.Length; i++)
                {
                    var exported = await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath,
                        new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\," + checks[i] + ")", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" },
                        TimeSpan.FromMinutes(1), CancellationToken.None);
                    Assert.Equal(previews[i].Length, exported.Length);
                    long diff = 0;
                    for (var k = 0; k < exported.Length; k++)
                    {
                        if (k % 4 != 3)
                            diff += Math.Abs(previews[i][k] - exported[k]);
                    }
                    var mean = diff / (exported.Length * 0.75);
                    _out.WriteLine(checks[i] + "장: 평균 차이 " + mean.ToString("0.00"));
                    Assert.True(mean < 4, checks[i] + "장 미리 보기와 내보내기의 평균 차이 " + mean);
                }
            }
            finally
            {
                File.Delete(output);
            }
        }

        [FfmpegFact]
        public void VideoRiseSplitGrowTurnUnblur_HalfwayShapes_EndExactlyAtRest()
        {
            var (project, clip) = BlueProject();
            var still = Frame(project, 12);
            // 반투명해도 보이게 파랑을 느슨하게 찾는다
            (int, int, int, int) Faint(byte[] bgra) => Box(bgra, (r, g, b) => b > 40 && r < 40 && g < 40);

            // 떠오르기: 40px 아래에서 — 반쯤 20px 아래·반투명
            var rise = Animation(AnimationEffect.Rise, AnimationKind.In, 10);
            rise.Amount = 40;
            clip.Animations = new List<ClipAnimation> { rise };
            var half = Faint(Frame(project, 5));
            _out.WriteLine("떠오르기 반쯤 " + half);
            Assert.Equal((80, 80, 239, 199), half);
            Assert.Equal(0, MaxDiff(still, Frame(project, 10)));

            // 가운데서 갈라지기(좌우로): 처음엔 없고, 반쯤 가운데 절반(가로 160의 40~119), 끝 장은 그대로
            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Split, AnimationKind.In, 10) };
            Assert.Equal((-1, -1, -1, -1), Faint(Frame(project, 0)));
            var opened = BlueBox(Frame(project, 5));
            _out.WriteLine("갈라지기 반쯤 " + opened);
            Assert.Equal((120, 60, 199, 179), opened);
            Assert.Equal(0, MaxDiff(still, Frame(project, 10)));
            // 가운데로 닫히기(위아래로): 사라지기 반쯤 세로 가운데 절반
            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Split, AnimationKind.Out, 10) };
            clip.Animations[0].Direction = AnimationDirection.Up;
            var closing = BlueBox(Frame(project, clip.Duration - 5));
            _out.WriteLine("닫히기 반쯤 " + closing);
            Assert.Equal((80, 90, 239, 149), closing);

            // 커지며 돌기: 처음엔 투명, 끝 장은 그대로
            clip.Animations = new List<ClipAnimation> { Animation(AnimationEffect.GrowTurn, AnimationKind.In, 10) };
            Assert.Equal((-1, -1, -1, -1), Faint(Frame(project, 0)));
            Assert.Equal(0, MaxDiff(still, Frame(project, 10)));

            // 흐림에서 선명하게: 흐린 장은 가장자리가 번지고(파랑이 상자 밖에도), 끝 장은 바이트까지 그대로
            var unblur = Animation(AnimationEffect.Unblur, AnimationKind.In, 10);
            unblur.Amount = 8;
            clip.Animations = new List<ClipAnimation> { unblur };
            var blurred = Frame(project, 4);
            var spread = Box(blurred, (r, g, b) => b > 8 && r < 40 && g < 40);
            _out.WriteLine("흐림 4장 " + spread + " · 가운데 B=" + blurred[(120 * Width + 160) * 4]);
            Assert.True(spread.Left < 76 && spread.Right > 243 && spread.Top < 56 && spread.Bottom > 183, "흐림이 번지지 않음 " + spread);
            Assert.True(blurred[(120 * Width + 160) * 4] > 180, "가운데는 파랑");
            // 끝 장: 자리는 그대로. 번질 자리만큼 투명하게 넓힌 채라(한 장과 내보내기가 같은 자리에 놓이게) 바탕(yuv420)의 색 나누기가
            // 가장자리 한 픽셀의 색만 조금 바꾼다
            foreach (var f in new long[] { 10, 12 })
            {
                var end = Frame(project, f);
                Assert.Equal(BlueBox(still), BlueBox(end));
                Assert.InRange(MaxDiff(still, end), 0, 20);
            }
        }

        [FfmpegFact]
        public async Task NewEntrancesAndExits_PreviewEqualsExport()
        {
            // 사진 클립 넷(각 30장): 나타나기·사라지기 새 효과를 모두 한 번씩(사라지기 = 거꾸로)
            var project = NewProject();
            var pairs = new[]
            {
                (AnimationEffect.Rise, AnimationEffect.Unblur),
                (AnimationEffect.Unblur, AnimationEffect.Rise),
                (AnimationEffect.Split, AnimationEffect.GrowTurn),
                (AnimationEffect.GrowTurn, AnimationEffect.Split)
            };
            var checks = new List<long>();
            for (var i = 0; i < pairs.Length; i++)
            {
                var start = i * 30L;
                var image = Add(project, project.MainTrack, FfmpegMedia.RedImage, start, 30);
                image.Scale = 0.5;
                image.Animations = new List<ClipAnimation>
                {
                    Animation(pairs[i].Item1, AnimationKind.In, 10, AnimationEase.Out), Animation(pairs[i].Item2, AnimationKind.Out, 10, AnimationEase.InOut)
                };
                checks.AddRange(new[] { start + 4, start + 15, start + 26 });
            }
            await AssertPreviewEqualsExport(project, checks.ToArray());
        }

        /// <summary>(x, y) 픽셀의 (R, G, B).</summary>
        private static (int R, int G, int B) Rgb(byte[] bgra, int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        [FfmpegFact]
        public void ImageColorChange_HoldsTheTint_GlowFlashesAroundAndLeaves()
        {
            var project = NewProject();
            var red = Add(project, project.MainTrack, FfmpegMedia.RedImage, 0, 40);
            red.Scale = 0.5;
            var still = Frame(project, 0);
            var box = Box(still, (r, g, b) => r > 150);
            _out.WriteLine("빨간 상자 " + box);
            // 초록으로 물들이기(밝기 0.299): 반쯤은 빨강 반 + 초록 × 밝기 반, 끝난 뒤 (0, 76, 0)에 머문다
            var tint = Animation(AnimationEffect.ColorChange, AnimationKind.Emphasis, 10);
            tint.Color = "#00FF00";
            red.Animations = new List<ClipAnimation> { tint };
            Assert.Equal(0, MaxDiff(still, Frame(project, 0)));
            var half = Rgb(Frame(project, 5), 160, 120);
            var done = Rgb(Frame(project, 30), 160, 120);
            _out.WriteLine("반쯤 " + half + " · 끝 " + done);
            Assert.InRange(half.R, 122, 134);
            Assert.InRange(half.G, 33, 44);
            Assert.InRange(done.R, 0, 5);
            Assert.InRange(done.G, 70, 82);

            // 광선 깜빡임(노랑, 크기 20): 한 번의 가운데에서 상자 밖이 노랗게 빛나고, 끝난 뒤엔 자리 그대로·빛 없음
            var glow = Animation(AnimationEffect.Glow, AnimationKind.Emphasis, 10);
            glow.Amount = 20;
            glow.Repeat = 1;
            red.Animations = new List<ClipAnimation> { glow };
            var flash = Frame(project, 5);
            var outside = Rgb(flash, box.Left - 4, 120);
            _out.WriteLine("광선 가운데 상자 밖 " + outside + " · 상자 안 " + Rgb(flash, 160, 120));
            Assert.True(outside.R > 80 && outside.G > 60 && outside.B < outside.G, "상자 밖이 노랗게 " + outside);
            var inside = Rgb(flash, 160, 120);
            Assert.True(inside.R > 240 && inside.G < 15 && inside.B < 15, "상자 안은 빨강 그대로 " + inside);
            var after = Frame(project, 20);
            Assert.Equal(box, Box(after, (r, g, b) => r > 150));
            Assert.Equal((0, 0, 0), Rgb(after, box.Left - 4, 120));
            Assert.InRange(MaxDiff(still, after), 0, 20);
        }

        [FfmpegFact]
        public void WaveText_OneCycleLoops_InPlaybackAndOneFrame()
        {
            // 물결(10장마다, 클립 끝까지) + 타자기(0~11장): 장 번호를 색에 담은 그리개 — 나타나기 장은 그 장, 그 뒤는 한 번 안의 차례(장 mod 10)를 되풀이
            var project = NewProject();
            var text = TimelineEdits.CreateText("가나다", 0, 40);
            text.Style = TextStyle.FromLegacy(text);
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var wave = Animation(AnimationEffect.Wave, AnimationKind.Emphasis, 10, AnimationEase.InOut, text);
            wave.Repeat = 0;
            text.Animations = new List<ClipAnimation> { Animation(AnimationEffect.Typewriter, AnimationKind.In, 12, AnimationEase.Linear, text), wave };
            var rasterizer = new FrameColorRasterizer();
            int Expected(long f) => FrameColorRasterizer.Red(f < 12 ? f : f % 10);
            int RedAt(byte[] bgra) => bgra[(120 * Width + 160) * 4 + 2];
            List<byte[]> frames;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 0, 40, Width, Height, out var files, rasterizer))
            {
                process.BeginErrorReadLine();
                frames = new List<byte[]>();
                var size = Width * Height * 4;
                while (true)
                {
                    var buffer = new byte[size];
                    if (!HelperProtocol.ReadExactly(process.StandardOutput.BaseStream, buffer, size))
                        break;
                    frames.Add(buffer);
                }
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Equal(40, frames.Count);
            _out.WriteLine("빨강: " + string.Join(" ", frames.Select(RedAt)) + " · 그린 장 " + rasterizer.Calls);
            for (var f = 0; f < 40; f++)
                Assert.InRange(RedAt(frames[f]), Expected(f) - 4, Expected(f) + 4);
            foreach (var f in new long[] { 3, 12, 19, 25, 39 })
                Assert.InRange(RedAt(Frame(project, f, rasterizer)), Expected(f) - 4, Expected(f) + 4);
        }

        [FfmpegFact]
        public async Task NewEmphases_PreviewEqualsExport()
        {
            // 사진: 색 바꾸기 + 광선 깜빡임, 글자: 물결(되풀이) + 굵게 깜빡이기(겹치는 동안은 장마다)
            var project = NewProject();
            var image = Add(project, project.MainTrack, FfmpegMedia.RedImage, 0, 40);
            image.Scale = 0.4;
            image.X = -60;
            var tint = Animation(AnimationEffect.ColorChange, AnimationKind.Emphasis, 10, AnimationEase.InOut);
            tint.Delay = 4;
            var glow = Animation(AnimationEffect.Glow, AnimationKind.Emphasis, 8, AnimationEase.InOut);
            glow.Delay = 20;
            image.Animations = new List<ClipAnimation> { tint, glow };
            var text = TimelineEdits.CreateText("가나다", 0, 40);
            text.Style = TextStyle.FromLegacy(text);
            text.X = 80;
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), text, 0);
            var wave = Animation(AnimationEffect.Wave, AnimationKind.Emphasis, 10, AnimationEase.InOut, text);
            wave.Repeat = 0;
            var bold = Animation(AnimationEffect.BoldFlash, AnimationKind.Emphasis, 6, AnimationEase.InOut, text);
            bold.Delay = 14;
            bold.Repeat = 1;
            text.Animations = new List<ClipAnimation> { wave, bold };
            await AssertPreviewEqualsExport(project, new long[] { 2, 8, 16, 24, 33 }, new FrameColorRasterizer());
        }

        [Fact]
        public void Cache_RedrawsWhenSettingsChange_ReusesWhenReverted()
        {
            // 완료 기준: 효과 설정을 바꾸면 캐시를 다시 만들고, 되돌리면 이전 캐시를 다시 쓴다
            var folder = Path.Combine(Path.GetTempPath(), "fv-anim-cache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var project = NewProject();
                var text = TimelineEdits.CreateText("가나다", 0, 30);
                TimelineEdits.Place(project, project.MainTrack, text, 0);
                var typewriter = Animation(AnimationEffect.Typewriter, AnimationKind.In, 10, AnimationEase.Linear, text);
                text.Animations = new List<ClipAnimation> { typewriter };
                var rasterizer = new FrameColorRasterizer();
                var progress = new List<(int Done, int Total)>();
                void Draw()
                {
                    var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 30, Width = Width, Height = Height, Audio = false, TempFolder = folder, TextRasterizer = rasterizer });
                    RenderCommand.DrawTextImages(plan, CancellationToken.None, (done, total) => progress.Add((done, total)));
                }
                Draw();
                Assert.Equal(11, rasterizer.Calls);
                Assert.Equal((11, 11), progress[^1]);
                Draw();
                Assert.Equal(11, rasterizer.Calls);
                typewriter.Duration = 12;
                Draw();
                Assert.Equal(11 + 13, rasterizer.Calls);
                typewriter.Duration = 10;
                Draw();
                Assert.Equal(11 + 13, rasterizer.Calls);
                // 단위를 바꿔도(글자별 → 단어별) 새로
                typewriter.Unit = AnimationUnit.Word;
                Draw();
                Assert.Equal(11 + 13 + 11, rasterizer.Calls);
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }
    }

    /// <summary>미리 보기 도우미가 애니메이션 그래프(sendcmd·pad/crop·rotate 식·scale eval=frame·그림 묶음 + concat)를 ffmpeg.exe와 같게 그리는지.</summary>
    [Collection(PreviewHelperCollection.Name)]
    public class AnimationHelperTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;
        private readonly PreviewHelperFixture _fixture;

        public AnimationHelperTests(ITestOutputHelper output, PreviewHelperFixture fixture)
        {
            _out = output;
            _fixture = fixture;
        }

        private static Project AnimatedProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = Width;
            project.Height = Height;
            var video = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, video, 0);
            video.Scale = 0.6;
            ClipAnimation Make(AnimationEffect effect, AnimationKind kind, long frames, Clip clip = null)
            {
                var a = ClipAnimations.Create(ClipAnimations.Def(effect), kind, project.Rate, clip);
                a.Duration = frames;
                return a;
            }
            video.Animations = new List<ClipAnimation> { Make(AnimationEffect.Spin, AnimationKind.In, 15), Make(AnimationEffect.Wipe, AnimationKind.Out, 15), Make(AnimationEffect.Shake, AnimationKind.Emphasis, 10) };
            var image = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 5, 40);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), image, 5);
            image.Scale = 0.4;
            image.Animations = new List<ClipAnimation> { Make(AnimationEffect.Fly, AnimationKind.In, 10), Make(AnimationEffect.Fade, AnimationKind.Out, 10), Make(AnimationEffect.Pulse, AnimationKind.Emphasis, 8) };
            var text = TimelineEdits.CreateText("가나다", 10, 40);
            text.Style = TextStyle.FromLegacy(text);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), text, 10);
            text.Animations = new List<ClipAnimation> { Make(AnimationEffect.Typewriter, AnimationKind.In, 12, text), Make(AnimationEffect.Zoom, AnimationKind.Out, 10, text) };
            // 2단계 효과: 흐림(gblur 시그마·세로 시그마 명령, 흐린 장에서만 알파 곱하기), 가운데서 갈라지기(닦아내기 둘), 커지며 돌기(넓힌 회전)
            var sharp = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 15, 40);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), sharp, 15);
            sharp.Scale = 0.3;
            sharp.Animations = new List<ClipAnimation> { Make(AnimationEffect.Unblur, AnimationKind.In, 12), Make(AnimationEffect.GrowTurn, AnimationKind.Out, 10) };
            var opening = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 2, 40);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), opening, 2);
            opening.Scale = 0.25;
            opening.X = -80;
            opening.Animations = new List<ClipAnimation> { Make(AnimationEffect.Split, AnimationKind.In, 10), Make(AnimationEffect.Rise, AnimationKind.Out, 10) };
            // 강조: 색 바꾸기(3×3 값 명령)·광선(나눠 흐린 층을 밑에), 물결(한 번을 그린 그림 묶음을 -loop 1로 되풀이)
            var tint = Make(AnimationEffect.ColorChange, AnimationKind.Emphasis, 8);
            tint.Delay = 5;
            var glow = Make(AnimationEffect.Glow, AnimationKind.Emphasis, 8);
            glow.Delay = 20;
            sharp.Animations.Add(tint);
            sharp.Animations.Add(glow);
            var waving = TimelineEdits.CreateText("물결", 0, 50);
            waving.Style = TextStyle.FromLegacy(waving);
            waving.Y = 60;
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), waving, 0);
            var wave = Make(AnimationEffect.Wave, AnimationKind.Emphasis, 7, waving);
            wave.Repeat = 0;
            waving.Animations = new List<ClipAnimation> { wave };
            return project;
        }

        private static List<byte[]> ReadFrames(Stream input)
        {
            var frames = new List<byte[]>();
            var size = Width * Height * 4;
            var reading = Task.Run(() =>
            {
                while (true)
                {
                    var buffer = new byte[size];
                    if (!HelperProtocol.ReadExactly(input, buffer, size))
                        break;
                    frames.Add(buffer);
                }
            });
            Assert.True(reading.Wait(TimeSpan.FromSeconds(60)), "흐름을 다 읽지 못했습니다(멈춤)");
            return frames;
        }

        [HelperFact]
        public void Playback_AnimatedClips_SameAsFfmpegExe()
        {
            var helper = _fixture.Helper;
            var project = AnimatedProject();
            var rasterizer = new FrameColorRasterizer();
            const long from = 3, to = 55;
            var failures = new List<HelperException>();
            List<byte[]> actual;
            using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, from, to, Width, Height, rasterizer, failures.Add))
            {
                Assert.True(stream.UsesHelper, "도우미 흐름" + (failures.Count > 0 ? ": " + failures[0].Message : ""));
                actual = ReadFrames(stream.Input);
            }
            List<byte[]> expected;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, from, to, Width, Height, out var files, rasterizer))
            {
                process.BeginErrorReadLine();
                expected = ReadFrames(process.StandardOutput.BaseStream);
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Empty(failures);
            _out.WriteLine("장수: ffmpeg.exe " + expected.Count + " · 도우미 " + actual.Count);
            Assert.Equal(to - from, expected.Count);
            Assert.Equal(expected.Count, actual.Count);
            var worst = 0;
            for (var i = 0; i < expected.Count; i++)
            {
                for (var k = 0; k < expected[i].Length; k++)
                    worst = Math.Max(worst, Math.Abs(expected[i][k] - actual[i][k]));
            }
            _out.WriteLine("가장 큰 채널 차이 " + worst);
            Assert.True(worst <= 3, "가장 큰 채널 차이 " + worst);
            // 한 장도(재생 헤드)
            foreach (var frame in new long[] { 8, 20, 47 })
            {
                var viaHelper = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None, rasterizer, helper, failures.Add).GetAwaiter().GetResult();
                var viaFfmpeg = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None, rasterizer).GetAwaiter().GetResult();
                Assert.Empty(failures);
                var max = viaHelper.Zip(viaFfmpeg, (a, b) => Math.Abs(a - b)).Max();
                _out.WriteLine(frame + "장: 가장 큰 차이 " + max);
                Assert.True(max <= 3, frame + "장 " + max);
            }
        }
    }
}
