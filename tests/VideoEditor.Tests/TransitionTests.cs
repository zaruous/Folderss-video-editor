using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>전환 효과: 디졸브가 아닌 효과는 xfade로 잇고, 타임라인 위치는 그대로다.</summary>
    public class TransitionTests
    {
        /// <summary>a.mp4 [0, 60) → b.mp4 [60, 120) — b 앞에 kind 전환 15프레임(0.5초).</summary>
        private static (Project Project, Clip A, Clip B) TwoClips(TransitionKind kind, long frames = 15)
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60, "a.mp4");
            var b = TestProjects.VideoClip(0, 60, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            TimelineEdits.SetTransition(project, b, kind, frames);
            return (project, a, b);
        }

        private static RenderPlan Build(Project project, long from, long to)
        {
            return RenderGraph.Build(project, new RenderOptions { From = from, To = to, Width = 640, Height = 360, Audio = false });
        }

        private static int Count(string text, string part)
        {
            return Regex.Matches(text, Regex.Escape(part)).Count;
        }

        // ---------- 그래프 ----------

        [Fact]
        public void Wipe_UsesXfadeBetweenBakedCanvases_AndKeepsTimeline()
        {
            var (project, a, b) = TwoClips(TransitionKind.WipeLeft);
            var plan = Build(project, 0, 120);
            // 타임라인 위치는 그대로, 앞 클립만 전환 길이만큼 더 읽는다(꼬리)
            Assert.Equal(60, b.Start);
            Assert.Equal(new[] { "-ss", "0", "-t", "2.5", "-i", "a.mp4" }, plan.Inputs[0]);
            // 두 클립을 캔버스 크기로 제자리에 얹은 뒤 묶음에서 xfade(묶음은 0초에 시작하므로 offset = b 시작 2초)
            Assert.Contains("format=gbrap,premultiply=inplace=1[k0]", plan.Filter);
            Assert.Contains("[k0][k1]xfade=transition=wipeleft:duration=0.5:offset=2[x1]", plan.Filter);
            Assert.Contains("[x1]unpremultiply=inplace=1,format=rgba,setpts=PTS-STARTPTS+round(0/TB)[c2]", plan.Filter);
            Assert.Contains("[base][c2]overlay=x=0:y=0:eof_action=pass[o2]", plan.Filter);
            // 디졸브의 알파 페이드는 없다
            Assert.DoesNotContain("fade=t=in", plan.Filter);
        }

        [Fact]
        public void WindowInsideTransition_StartsBothAtTransitionStart()
        {
            var (project, _, _) = TwoClips(TransitionKind.SlideUp);
            var plan = Build(project, 65, 66);
            // a는 전환이 시작하는 60프레임(원본 2초)부터 꼬리 끝까지, b는 자기 처음부터
            Assert.Equal(new[] { "-ss", "2", "-t", "0.5", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Equal(new[] { "-ss", "0", "-t", "0.2", "-i", "b.mp4" }, plan.Inputs[1]);
            Assert.Contains("xfade=transition=slideup:duration=0.5:offset=0[x1]", plan.Filter);
            // 묶음은 그리는 구간보다 5프레임 앞에서 시작한다(앞부분은 바탕 시작 전이라 버려짐)
            Assert.Contains("setpts=PTS-STARTPTS+round(-0.166667/TB)[c2]", plan.Filter);
        }

        [Fact]
        public void WindowOutsideTransition_HasNoXfade()
        {
            var (project, _, _) = TwoClips(TransitionKind.WipeLeft);
            var before = Build(project, 10, 11);
            Assert.DoesNotContain("xfade", before.Filter);
            Assert.Single(before.Inputs);
            var after = Build(project, 100, 101);
            Assert.DoesNotContain("xfade", after.Filter);
            Assert.Single(after.Inputs);
        }

        [Fact]
        public void FirstClip_TransitionsFromTransparent()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60, "a.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.SetTransition(project, a, TransitionKind.CircleOpen, 15);
            var plan = Build(project, 0, 60);
            Assert.Contains("color=c=black@0.0:s=640x360:r=30/1:d=0.5,format=gbrap[t0]", plan.Filter);
            Assert.Contains("[t0][k0]xfade=transition=circleopen:duration=0.5:offset=0[x0]", plan.Filter);
        }

        [Fact]
        public void ChainedTransitions_FormOneGroup()
        {
            var (project, _, b) = TwoClips(TransitionKind.WipeRight);
            var c = TestProjects.VideoClip(0, 60, "c.mp4");
            TimelineEdits.Place(project, project.MainTrack, c, 999);
            TimelineEdits.SetTransition(project, c, TransitionKind.ZoomIn, 30);
            var plan = Build(project, 0, 180);
            Assert.Contains("[k0][k1]xfade=transition=wiperight:duration=0.5:offset=2[x1]", plan.Filter);
            Assert.Contains("[x1][k2]xfade=transition=zoomin:duration=1:offset=4[x2]", plan.Filter);
            Assert.Equal(1, Count(plan.Filter, "overlay=x=0:y=0"));
        }

        [Fact]
        public void DissolveThenWipe_MixesAlphaFadeAndXfade()
        {
            var (project, _, b) = TwoClips(TransitionKind.Fade);
            var c = TestProjects.VideoClip(0, 60, "c.mp4");
            TimelineEdits.Place(project, project.MainTrack, c, 999);
            TimelineEdits.SetTransition(project, c, TransitionKind.WipeLeft, 15);
            var plan = Build(project, 0, 180);
            // b는 a 위로 서서히 나타나고(디졸브), 그 b에서 c로 닦아낸다
            Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", plan.Filter);
            Assert.Contains("[k1][k2]xfade=transition=wipeleft:duration=0.5:offset=2[x2]", plan.Filter);
            Assert.Contains("setpts=PTS-STARTPTS+round(2/TB)[c3]", plan.Filter);
        }

        [Fact]
        public void Dissolve_StillUsesAlphaFade()
        {
            var (project, _, _) = TwoClips(TransitionKind.Fade);
            var plan = Build(project, 0, 120);
            Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", plan.Filter);
            Assert.DoesNotContain("xfade", plan.Filter);
        }

        [Fact]
        public void EveryKind_HasNameAndFilter()
        {
            Assert.Equal(Enum.GetValues<TransitionKind>().Length, Transitions.All.Count);
            Assert.Equal(Transitions.All.Count, Transitions.All.Select(Transitions.Name).Distinct().Count());
            Assert.All(Transitions.All, kind => Assert.Matches("^[a-z]+$", Transitions.FilterName(kind)));
            Assert.False(Transitions.UsesXfade(TransitionKind.Fade));
            Assert.True(Transitions.UsesXfade(TransitionKind.WipeLeft));
        }

        // ---------- 편집 ----------

        [Fact]
        public void SetTransition_ClampsToClipLength_ZeroRemoves_KeepsPositions()
        {
            var (project, a, b) = TwoClips(TransitionKind.Pixelize, 999);
            Assert.Equal(60, b.TransitionIn);
            Assert.Equal(TransitionKind.Pixelize, b.Transition);
            Assert.Equal((0L, 60L), (a.Start, b.Start));
            TimelineEdits.SetTransition(project, b, TransitionKind.Pixelize, 0);
            Assert.Equal(0, b.TransitionIn);
        }

        [Fact]
        public void SetTransition_RejectsSubtitlesAndLockedTracks()
        {
            var (project, _, b) = TwoClips(TransitionKind.WipeLeft);
            var subtitles = TimelineEdits.ImportSubtitles(project, new[] { new SubtitleCue { Start = 0, End = 1, Text = "가" } });
            Assert.Throws<EditException>(() => TimelineEdits.SetTransition(project, subtitles.Clips[0], TransitionKind.WipeLeft, 15));
            project.MainTrack.Locked = true;
            Assert.Throws<EditException>(() => TimelineEdits.SetTransition(project, b, TransitionKind.SlideLeft, 15));
        }

        [Fact]
        public void SetTransitionsBetween_OnlyAdjacentClips()
        {
            var project = TestProjects.Empty(magnetic: false);
            var a = TestProjects.Add(project, project.MainTrack, 0, 30, "a.mp4");
            var b = TestProjects.Add(project, project.MainTrack, 30, 30, "b.mp4");
            var c = TestProjects.Add(project, project.MainTrack, 90, 30, "c.mp4");
            var count = TimelineEdits.SetTransitionsBetween(project, project.MainTrack, TransitionKind.FadeBlack, 9);
            Assert.Equal(1, count);
            Assert.Equal(0, a.TransitionIn);
            Assert.Equal((TransitionKind.FadeBlack, 9L), (b.Transition, b.TransitionIn));
            Assert.Equal(0, c.TransitionIn);
        }

        [Fact]
        public void SetTransition_IsUndoable()
        {
            var session = new EditorSession();
            var b = TestProjects.VideoClip(0, 60, "b.mp4");
            session.Execute(p =>
            {
                TimelineEdits.Place(p, p.MainTrack, TestProjects.VideoClip(0, 60, "a.mp4"), 0);
                TimelineEdits.Place(p, p.MainTrack, b, 999);
            });
            Assert.True(session.Execute(p => TimelineEdits.SetTransition(p, p.FindClip(b.Id), TransitionKind.Radial, 20)));
            Assert.Equal(TransitionKind.Radial, session.Project.FindClip(b.Id).Transition);
            Assert.True(session.Undo());
            Assert.Equal(0, session.Project.FindClip(b.Id).TransitionIn);
        }

        [Fact]
        public void ProjectFile_KeepsKind_AndUnknownBecomesDissolve()
        {
            var (project, _, _) = TwoClips(TransitionKind.SmoothRight);
            var json = JsonNode.Parse(ProjectFile.Snapshot(project)).AsObject();
            Assert.Equal("SmoothRight", (string)json["VideoTracks"][0]["Clips"][1]["Transition"]);
            var folder = Path.Combine(Path.GetTempPath(), "fv-transition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "a" + ProjectFile.Extension);
                File.WriteAllText(path, json.ToJsonString());
                Assert.Equal(TransitionKind.SmoothRight, ProjectFile.Load(path).MainTrack.Clips[1].Transition);
                json["VideoTracks"][0]["Clips"][1]["Transition"] = 999;
                json["VideoTracks"][0]["Clips"][1]["TransitionIn"] = -5;
                File.WriteAllText(path, json.ToJsonString());
                var loaded = ProjectFile.Load(path).MainTrack.Clips[1];
                Assert.Equal(TransitionKind.Fade, loaded.Transition);
                Assert.Equal(0, loaded.TransitionIn);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    /// <summary>실제 FFmpeg로 전환을 그려 본다(파란 동영상 → 빨간 사진).</summary>
    public class TransitionFfmpegTests
    {
        private readonly ITestOutputHelper _out;

        public TransitionFfmpegTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>파란 동영상 [0, 60) → 빨간 사진 [60, 120), 사진 앞에 kind 전환 30프레임.</summary>
        private static Project BlueToRed(TransitionKind kind)
        {
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var blue = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.BlueVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, blue, 0);
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            red.Scale = 4;
            TimelineEdits.Place(project, project.MainTrack, red, 999);
            TimelineEdits.SetTransition(project, red, kind, 30);
            return project;
        }

        private static byte[] Frame(Project project, long frame)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, 320, 240, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        private static (int R, int G, int B) Pixel(byte[] bgra, int x, int y, int offset = 0)
        {
            var i = offset + (y * 320 + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        private static bool IsRed((int R, int G, int B) p) { return p.R > 200 && p.B < 60; }

        private static bool IsBlue((int R, int G, int B) p) { return p.B > 200 && p.R < 60; }

        [FfmpegFact]
        public void Wipe_MidwayShowsBothSides_ThenRed()
        {
            var project = BlueToRed(TransitionKind.WipeLeft);
            Assert.Equal(60, project.MainTrack.Clips[1].Start);
            var before = Frame(project, 50);
            var mid = Frame(project, 75);
            var after = Frame(project, 100);
            _out.WriteLine("전 " + Pixel(before, 160, 120) + " · 중간 왼쪽 " + Pixel(mid, 20, 120) + " 오른쪽 " + Pixel(mid, 300, 120) + " · 뒤 " + Pixel(after, 160, 120));
            Assert.True(IsBlue(Pixel(before, 160, 120)));
            // 왼쪽으로 닦아내기: 새 화면(빨강)이 오른쪽부터 드러난다
            Assert.True(IsBlue(Pixel(mid, 20, 120)), "중간 왼쪽은 아직 파랑");
            Assert.True(IsRed(Pixel(mid, 300, 120)), "중간 오른쪽은 이미 빨강");
            Assert.True(IsRed(Pixel(after, 160, 120)));
        }

        [FfmpegFact]
        public async Task PreviewFrame_EqualsContinuousRender_InsideTransition()
        {
            // 한 장만 그린 미리 보기와 재생처럼 이어 그린 같은 프레임이 같아야 한다(전환 도중부터 그려도 진행이 맞음)
            // 61·64·70처럼 시작 초를 TB로 나눈 값이 0.99999가 되는 프레임도 본다(잘리면 한 프레임 앞이 나온다)
            var project = BlueToRed(TransitionKind.CircleOpen);
            const int size = 320 * 240 * 4;
            var pixels = new byte[size * 90];
            List<string> files;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 0, 90, 320, 240, out files))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var read = 0;
                while (read < pixels.Length)
                {
                    var n = await process.StandardOutput.BaseStream.ReadAsync(pixels, read, pixels.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                await process.WaitForExitAsync();
                Assert.True(read == pixels.Length, "읽은 바이트 " + read + " · " + await errors);
            }
            foreach (var file in files)
                RenderCommand.TryDelete(file);
            foreach (var frame in new long[] { 61, 64, 66, 70, 75, 89 })
            {
                var single = Frame(project, frame);
                long diff = 0;
                for (var i = 0; i < size; i++)
                {
                    if (i % 4 != 3)
                        diff += Math.Abs(single[i] - pixels[frame * size + i]);
                }
                var mean = diff / (size * 0.75);
                _out.WriteLine(frame + "프레임: 가운데 " + Pixel(single, 160, 120) + " 모서리 " + Pixel(single, 2, 2) + " · 평균 차이 " + mean);
                Assert.True(mean < 1, frame + "프레임 평균 차이 " + mean);
                if (frame == 75)
                {
                    // 원 열기: 가운데는 새 화면(빨강), 모서리는 아직 앞 화면(파랑)
                    Assert.True(IsRed(Pixel(single, 160, 120)));
                    Assert.True(IsBlue(Pixel(single, 2, 2)));
                }
            }
        }

        [FfmpegFact]
        public void TransitionFromTransparent_IsNotDarkened()
        {
            // 투명에서 흐리며 바뀌는 첫 클립: 절반 진행이면 검은 바탕 위 빨강이 절반 밝기(알파를 곱하지 않고 섞으면 1/4로 어두워진다)
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            red.Scale = 4;
            TimelineEdits.Place(project, project.MainTrack, red, 0);
            TimelineEdits.SetTransition(project, red, TransitionKind.Blur, 30);
            var center = Pixel(Frame(project, 15), 160, 120);
            _out.WriteLine("가운데 " + center);
            Assert.InRange(center.R, 100, 160);
        }

        [FfmpegFact]
        public void FirstClip_OpensFromBlack()
        {
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            red.Scale = 4;
            TimelineEdits.Place(project, project.MainTrack, red, 0);
            TimelineEdits.SetTransition(project, red, TransitionKind.CircleOpen, 30);
            var mid = Frame(project, 15);
            _out.WriteLine("가운데 " + Pixel(mid, 160, 120) + " 모서리 " + Pixel(mid, 2, 2));
            Assert.True(IsRed(Pixel(mid, 160, 120)));
            var corner = Pixel(mid, 2, 2);
            Assert.True(corner.R < 30 && corner.G < 30 && corner.B < 30, "모서리는 아직 검정 " + corner);
        }
    }
}
