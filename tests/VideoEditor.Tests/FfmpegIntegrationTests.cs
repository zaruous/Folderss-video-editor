using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>실제 FFmpeg가 있을 때만(FOLDERSS_VE_FFMPEG = ffmpeg 폴더, 없으면 PATH 등 설치 위치).</summary>
    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (FfmpegMedia.Tool == null)
                Skip = "FFmpeg를 찾지 못해 건너뜁니다(FOLDERSS_VE_FFMPEG에 ffmpeg 폴더를 주거나 PATH에 두세요).";
        }
    }

    /// <summary>시험 미디어를 lavfi로 한 번 만든다: 테스트 화면+소리 2초, 파란 화면 2초, 빨간 사진, 440Hz 소리 3초.</summary>
    internal static class FfmpegMedia
    {
        private static readonly Lazy<FfmpegTool> LazyTool = new Lazy<FfmpegTool>(() => FfmpegTool.Locate(null, null));
        private static readonly Lazy<string> LazyFolder = new Lazy<string>(Create);

        public static FfmpegTool Tool { get { return LazyTool.Value; } }
        public static string Folder { get { return LazyFolder.Value; } }
        public static string TestVideo { get { return Path.Combine(Folder, "testsrc.mp4"); } }
        public static string BlueVideo { get { return Path.Combine(Folder, "blue.mp4"); } }
        public static string RedImage { get { return Path.Combine(Folder, "red.png"); } }
        public static string Tone { get { return Path.Combine(Folder, "tone.wav"); } }

        private static string Create()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-media-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            Run("-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "mpeg4", "-q:v", "4", "-c:a", "aac", "-shortest", Path.Combine(folder, "testsrc.mp4"));
            Run("-f", "lavfi", "-i", "color=c=blue:size=320x240:rate=30:duration=2", "-c:v", "mpeg4", "-q:v", "2", Path.Combine(folder, "blue.mp4"));
            Run("-f", "lavfi", "-i", "color=c=red:size=200x200", "-frames:v", "1", Path.Combine(folder, "red.png"));
            Run("-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "pcm_s16le", Path.Combine(folder, "tone.wav"));
            return folder;
        }

        private static void Run(params string[] args)
        {
            var all = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            all.AddRange(args);
            var result = FfmpegRunner.RunText(Tool.FfmpegPath, all, TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException("시험 미디어를 만들지 못했습니다: " + result.Error);
        }

        public static MediaInfo Probe(string path)
        {
            return MediaProbe.Probe(Tool, path, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 1080p 효과 하나가 장당 더 써도 되는 시간(ms). 효과 없이 장당 6ms 안팎인 PC에서는 60ms,
        /// 그보다 몇 배 느린 시험 기계(CI)에서는 효과 없는 시간의 4배까지 — 효과 비용도 기계 속도만큼 늘기 때문.
        /// </summary>
        public static double CostBudget(double plain)
        {
            return Math.Max(60, plain * 4);
        }
    }

    public class FfmpegIntegrationTests
    {
        private readonly ITestOutputHelper _out;

        public FfmpegIntegrationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Project NewProject()
        {
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            return project;
        }

        private static Clip Add(Project project, Track track, string path, long start, long imageFrames = 30)
        {
            var media = FfmpegMedia.Probe(path);
            var clip = TimelineEdits.CreateClip(media, project.Rate, start, imageFrames);
            TimelineEdits.Place(project, track, clip, start);
            return clip;
        }

        /// <summary>(x, y) 픽셀의 R, G, B.</summary>
        private static (int R, int G, int B) Pixel(byte[] bgra, int width, int x, int y)
        {
            var i = (y * width + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        private static byte[] Frame(Project project, long frame, int width = 64, int height = 48)
        {
            return PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, width, height, CancellationToken.None).GetAwaiter().GetResult();
        }

        [FfmpegFact]
        public void Tool_IsUsable()
        {
            var tool = FfmpegMedia.Tool;
            _out.WriteLine(tool.VersionLine + " · " + tool.Folder + " · 인코더 " + tool.AutoVideoEncoder());
            Assert.True(tool.MajorVersion > 0);
            Assert.True(tool.HasEncoder("aac"));
        }

        [FfmpegFact]
        public void Probe_Kinds()
        {
            var video = FfmpegMedia.Probe(FfmpegMedia.TestVideo);
            Assert.Equal(MediaKind.Video, video.Kind);
            Assert.True(video.HasAudio);
            Assert.Equal(320, video.Width);
            Assert.Equal("30/1", video.FrameRate);
            Assert.InRange(video.DurationSeconds, 1.9, 2.2);
            Assert.Equal(MediaKind.Image, FfmpegMedia.Probe(FfmpegMedia.RedImage).Kind);
            var tone = FfmpegMedia.Probe(FfmpegMedia.Tone);
            Assert.Equal(MediaKind.Audio, tone.Kind);
            Assert.InRange(tone.DurationSeconds, 2.9, 3.1);
        }

        [FfmpegFact]
        public void Frame_OverlayImageOnVideo_PositionAndOpacity()
        {
            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var image = Add(project, overlay, FfmpegMedia.RedImage, 0, 30);
            image.Scale = 0.5;
            var frame = Frame(project, 10);
            Assert.Equal(64 * 48 * 4, frame.Length);
            var center = Pixel(frame, 64, 32, 24);
            var corner = Pixel(frame, 64, 2, 2);
            _out.WriteLine("가운데 " + center + " · 모서리 " + corner);
            Assert.True(center.R > 200 && center.B < 60, "가운데는 빨간 사진");
            Assert.True(corner.B > 200 && corner.R < 60, "모서리는 파란 동영상");

            // 오른쪽으로 옮기면 가운데는 다시 파랑
            image.X = 120;
            var moved = Pixel(Frame(project, 10), 64, 32, 24);
            Assert.True(moved.B > 200 && moved.R < 60, "옮긴 뒤 가운데 " + moved);

            // 불투명도 50%면 섞인다
            image.X = 0;
            image.Opacity = 0.5;
            var mixed = Pixel(Frame(project, 10), 64, 32, 24);
            _out.WriteLine("반투명 " + mixed);
            Assert.InRange(mixed.R, 90, 170);
            Assert.InRange(mixed.B, 90, 170);
        }

        [FfmpegFact]
        public void Frame_Gap_IsBlack_AndHiddenTrackDisappears()
        {
            var project = NewProject();
            project.Magnetic = false;
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 30);
            var gap = Pixel(Frame(project, 5), 64, 32, 24);
            Assert.True(gap.R < 20 && gap.G < 20 && gap.B < 20, "빈틈은 검정 " + gap);
            var blue = Pixel(Frame(project, 40), 64, 32, 24);
            Assert.True(blue.B > 200);
            project.MainTrack.Hidden = true;
            var hidden = Pixel(Frame(project, 40), 64, 32, 24);
            Assert.True(hidden.B < 20, "숨긴 트랙 " + hidden);
        }

        [FfmpegFact]
        public void Frame_Text_DrawsGlyphs()
        {
            var project = NewProject();
            var text = TimelineEdits.CreateText("가나다 ABC", 0, 30);
            text.FontSize = 60;
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var frame = Frame(project, 0, 160, 120);
            var bright = 0;
            for (var i = 0; i < frame.Length; i += 4)
            {
                if (frame[i] > 200 && frame[i + 1] > 200 && frame[i + 2] > 200)
                    bright++;
            }
            _out.WriteLine("흰 픽셀 " + bright);
            Assert.True(bright > 50);
        }

        [FfmpegFact]
        public void Crossfade_MidpointMixesBothClips()
        {
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var red = Add(project, project.MainTrack, FfmpegMedia.RedImage, 999, 60);
            red.TransitionIn = 30;
            Assert.Equal(blue.End, red.Start);
            var mid = Pixel(Frame(project, red.Start + 15), 64, 32, 24);
            _out.WriteLine("크로스페이드 가운데 " + mid);
            Assert.InRange(mid.R, 80, 180);
            Assert.InRange(mid.B, 80, 180);
            var after = Pixel(Frame(project, red.Start + 40), 64, 32, 24);
            Assert.True(after.R > 200 && after.B < 60);
        }

        [FfmpegFact]
        public async Task Export_SplitTrimSpeed_HasExactLength()
        {
            var project = NewProject();
            var a = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            Add(project, project.MainTrack, FfmpegMedia.RedImage, 999, 45);
            Add(project, project.AudioTracks[0], FfmpegMedia.Tone, 0);
            // 1초에서 나누고 앞 조각 지우기 → 1초 + 사진 1.5초
            var right = TimelineEdits.Split(project, new[] { a }, 30).Single();
            TimelineEdits.Delete(project, new[] { a }, ripple: true);
            Assert.Equal(0, right.Start);
            // 오른쪽 조각을 2배속 → 15프레임
            TimelineEdits.SetSpeed(project, right, 2);
            Assert.Equal(60, project.MainTrack.Clips.Max(c => c.End));
            var output = Path.Combine(FfmpegMedia.Folder, "export-" + Guid.NewGuid().ToString("N") + ".mp4");
            var reports = new List<double>();
            await Exporter.Export(FfmpegMedia.Tool, project, new ExportSettings
            {
                OutputPath = output,
                Width = 320,
                Height = 240,
                From = 0,
                To = project.Duration
            }, new Progress<double>(reports.Add), CancellationToken.None);
            var info = FfmpegMedia.Probe(output);
            _out.WriteLine("내보낸 길이 " + info.DurationSeconds + "초 · " + info.VideoCodec + "/" + info.AudioCodec);
            Assert.Equal(MediaKind.Video, info.Kind);
            Assert.True(info.HasAudio);
            Assert.Equal(320, info.Width);
            // 오디오 트랙의 소리(3초)가 더 길어 타임라인 길이는 3초(90프레임)
            Assert.Equal(90, project.Duration);
            Assert.InRange(info.DurationSeconds, 2.95, 3.1);
            var frames = await FfmpegRunner.RunText(FfmpegMedia.Tool.FfprobePath,
                new[] { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", output },
                TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal("90", frames.Output.Trim());
            File.Delete(output);
        }

        [FfmpegFact]
        public async Task Export_Cancel_KeepsExistingFile_AndRemovesPart()
        {
            var project = NewProject();
            var image = Add(project, project.MainTrack, FfmpegMedia.RedImage, 0, 30 * 600);
            var output = Path.Combine(FfmpegMedia.Folder, "cancel-" + Guid.NewGuid().ToString("N") + ".mp4");
            // 덮어쓰기로 고른 원래 파일은 취소하면 그대로 남아야 한다(.part에 쓰고 성공해야 바꿔 넣음)
            File.WriteAllText(output, "원래 파일");
            using (var cancel = new CancellationTokenSource())
            {
                var task = Exporter.Export(FfmpegMedia.Tool, project, new ExportSettings { OutputPath = output, Width = 320, Height = 240, From = 0, To = image.End },
                    new Progress<double>(p => { if (p > 0.01) cancel.Cancel(); }), cancel.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            }
            Assert.Equal("원래 파일", File.ReadAllText(output));
            Assert.False(File.Exists(output + ".part"));
            File.Delete(output);
        }

        [FfmpegFact]
        public async Task Audio_MuteVolumeAndFade()
        {
            // 시험 소리(lavfi sine)의 크기는 1/8
            var source = (await MediaPictures.Waveform(FfmpegMedia.Tool, FfmpegMedia.Tone, 10, CancellationToken.None)).Max();
            var project = NewProject();
            var tone = Add(project, project.AudioTracks[0], FfmpegMedia.Tone, 0);
            var full = await Peak(project, 30, 60);
            tone.Volume = 0.25;
            var quarter = await Peak(project, 30, 60);
            tone.Volume = 1;
            tone.FadeIn = 30;
            var fadeStart = await Peak(project, 0, 3);
            _out.WriteLine("원본 " + source + " · 그래프 " + full + " · 25% " + quarter + " · 페이드 처음 " + fadeStart);
            Assert.InRange(full, source * 0.9, source * 1.1);
            Assert.InRange(quarter, source * 0.2, source * 0.3);
            Assert.True(fadeStart < source * 0.15, "페이드 인 처음은 작다");
            project.AudioTracks[0].Muted = true;
            Assert.True(await Peak(project, 30, 60) < 0.001);
        }

        private static async Task<double> Peak(Project project, long from, long to)
        {
            var plan = RenderGraph.Build(project, new RenderOptions { From = from, To = to, Width = 16, Height = 16, Video = false, Preview = true });
            string script;
            var args = RenderCommand.Arguments(FfmpegMedia.Tool, plan, new[] { "-map", "[aout]", "-f", "s16le", "-ac", "2", "-ar", "48000", "pipe:1" }, out script);
            try
            {
                var pcm = await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath, args, TimeSpan.FromMinutes(1), CancellationToken.None);
                Assert.InRange(pcm.Length, (int)(plan.Seconds * 48000 * 4) - 4096, (int)(plan.Seconds * 48000 * 4) + 4096);
                return MediaPictures.Peaks(pcm, pcm.Length / 2).DefaultIfEmpty(0).Max();
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }

        [FfmpegFact]
        public async Task Thumbnail_And_Waveform()
        {
            var png = await MediaPictures.Thumbnail(FfmpegMedia.Tool, FfmpegMedia.Probe(FfmpegMedia.TestVideo), 0.5, 96, CancellationToken.None);
            Assert.True(png.Length > 100);
            Assert.Equal(0x89, png[0]);
            var peaks = await MediaPictures.Waveform(FfmpegMedia.Tool, FfmpegMedia.Tone, 50, CancellationToken.None);
            Assert.InRange(peaks.Length, 145, 155);
            // lavfi sine의 크기는 1/8
            Assert.InRange(peaks.Average(), 0.11, 0.14);
        }
    }
}
