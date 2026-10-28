using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>글리치 효과 묶음(RGB 분할·블록 글리치·줄 밀림·나쁜 TV 신호): 목록·매개변수, 렌더 그래프 글(자리·시각·금지 함수·scale 하나·사람에게만), 시각 해시.</summary>
    public class GlitchEffectsTests
    {
        private static readonly VideoEffectDef[] Glitches = { RgbSplit.Def, BlockGlitch.Def, LineSlip.Def, BadSignal.Def };

        private sealed class FakeMasks : IPersonMaskProvider
        {
            private readonly string _source;

            public FakeMasks(string source)
            {
                _source = source;
            }

            public string Find(string sourcePath)
            {
                return sourcePath == _source ? "C:/masks/m.mkv" : null;
            }
        }

        private static (Project Project, Clip Clip) VideoProject(long frames = 90)
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateClip(TestProjects.Video("a.mp4"), project.Rate, 0, 0);
            clip.SourceOut = frames;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static string Graph(Project project, long from, long to, IPersonMaskProvider masks = null)
        {
            return RenderGraph.Build(project, new RenderOptions { From = from, To = to, Width = project.Width, Height = project.Height, Audio = false, Preview = true, TempFolder = "T:/tmp", PersonMasks = masks }).Filter;
        }

        private static ClipEffect With(Clip clip, VideoEffectDef def, Action<ClipEffect> change = null)
        {
            var effect = VideoEffects.Create(def);
            change?.Invoke(effect);
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(effect);
            return effect;
        }

        // ---------- 목록·매개변수 ----------

        [Fact]
        public void Catalog_HasTheFourGlitches_InOrderAfterScanLine_WithTypedParams()
        {
            Assert.Same(ScanLine.Def, VideoEffects.All[0]);
            var indexes = Glitches.Select(d => VideoEffects.All.ToList().IndexOf(d)).ToList();
            Assert.All(indexes, i => Assert.True(i > 0));
            Assert.Equal(indexes.OrderBy(i => i), indexes);
            Assert.Equal(new[] { "rgb-split", "block-glitch", "line-slip", "bad-tv" }, Glitches.Select(d => d.Id));
            Assert.Equal(new[] { "RGB 분할", "블록 글리치", "줄 밀림", "나쁜 TV 신호" }, Glitches.Select(d => d.Name));
            Assert.All(Glitches, d =>
            {
                Assert.Same(d, VideoEffects.Find(d.Id));
                Assert.Equal("글리치", d.Group);
                Assert.True(VideoEffects.IsValidId(d.Id));
                Assert.False(string.IsNullOrWhiteSpace(d.Description));
                Assert.DoesNotContain("\n", d.Description);
                Assert.Null(d.ColorParam);
                Assert.All(d.Params, p =>
                {
                    Assert.Equal(EffectParamKind.Number, p.Kind);
                    Assert.Matches("^[a-z][a-z0-9]*$", p.Key);
                    Assert.InRange(p.Default, p.Min, p.Max);
                    Assert.True(p.Max > p.Min);
                    Assert.False(string.IsNullOrWhiteSpace(p.Name));
                    Assert.False(string.IsNullOrWhiteSpace(p.Unit));
                    Assert.False(string.IsNullOrWhiteSpace(p.Hint));
                });
                // 썸네일 값은 있는 매개변수의 범위 안
                Assert.NotEmpty(d.PreviewValues);
                Assert.All(d.PreviewValues, v => Assert.InRange(v.Value, d.Param(v.Key).Min, d.Param(v.Key).Max));
            });
            Assert.Equal(new[] { "amount", "angle", "jitter", "glow" }, RgbSplit.Def.Params.Select(p => p.Key));
            Assert.Equal(new[] { "amount", "block", "rate", "color" }, BlockGlitch.Def.Params.Select(p => p.Key));
            Assert.Equal(new[] { "amount", "thick", "rate", "fringe" }, LineSlip.Def.Params.Select(p => p.Key));
            Assert.Equal(new[] { "noise", "jitter", "roll", "rate", "scan", "bleed" }, BadSignal.Def.Params.Select(p => p.Key));
            Assert.Equal(VideoEffects.All.Count, VideoEffects.All.Select(d => d.Id).Distinct().Count());
        }

        [Fact]
        public void Values_RoundTrip_AndAreClamped()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            foreach (var def in Glitches)
                VideoEffects.Add(project, clip, def);
            VideoEffects.SetNumber(clip.Effects[0], "amount", 500);
            VideoEffects.SetNumber(clip.Effects[3], "roll", -3);
            clip.Effects[2].PersonOnly = true;
            var back = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project)).FindClip(clip.Id).Effects;
            Assert.Equal(Glitches.Select(d => d.Id), back.Select(e => e.Id));
            Assert.Equal(60, back[0].Values["amount"]);
            Assert.Equal(0, back[3].Values["roll"]);
            Assert.True(back[2].PersonOnly);
            Assert.Null(back[1].Color);
            Assert.Equal(64, back[1].Values["block"]);
        }

        // ---------- 렌더 그래프 ----------

        private static readonly Regex Forbidden = new Regex(@"\b(st|ld|random|time)\(", RegexOptions.CultureInvariant);

        [Fact]
        public void Graph_EachGlitch_IsAfterTheFitAndBeforeRotate_WithoutForbiddenFunctionsOrASecondScale()
        {
            foreach (var def in Glitches)
            {
                var (project, clip) = VideoProject();
                clip.Fit = ClipFit.Fill;
                clip.Rotation = 20;
                With(clip, def);
                var filter = Graph(project, 0, 60);
                var chain = filter.Split(";\n").Single(c => c.Contains("[fx0e0"));
                var fit = chain.IndexOf("crop=640:360,format=gbrap", StringComparison.Ordinal);
                var rotate = chain.IndexOf(",rotate=", StringComparison.Ordinal);
                Assert.True(fit >= 0 && fit < chain.IndexOf("fx0e0", StringComparison.Ordinal) && chain.IndexOf("fx0e0", StringComparison.Ordinal) < rotate, def.Id + ": " + chain);
                Assert.DoesNotMatch(Forbidden, chain);
                // noise 필터를 쓰지 않는다(프레임 수로 무늬가 바뀌고, 같은 그래프의 다른 흐름을 깨뜨린 적이 있다)
                Assert.DoesNotContain("noise=", chain);
                // 조각마다: 클립 체인(첫 조각)과 수식으로 그린 지도 체인(color로 시작 또는 칸 값을 읽는 2단계)에만 scale이 하나씩, 나머지(클립 흐름)에는 없다
                var pieces = chain.Split(';');
                Assert.Equal(1, Count(pieces[0], "scale="));
                foreach (var piece in pieces.Skip(1))
                {
                    var scales = Count(piece, "scale=");
                    Assert.True(scales <= 1, def.Id + ": " + piece);
                    if (scales == 1)
                        Assert.True(piece.StartsWith("color=", StringComparison.Ordinal) || piece.StartsWith("[fx0e0k", StringComparison.Ordinal), def.Id + ": " + piece);
                }
            }
        }

        private static int Count(string text, string part)
        {
            var count = 0;
            for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        [Fact]
        public void Graph_TimeIsTheClipLocalFrameNumber()
        {
            var (project, clip) = VideoProject();
            project.MainTrack.Clips.Clear();
            TimelineEdits.Place(project, project.MainTrack, clip, 30);
            foreach (var def in Glitches)
            {
                clip.Effects = null;
                With(clip, def);
                // 처음부터 그리면 흐름 0초 = 클립 시작, 클립 안 2초(프레임 90)부터 한 장이면 + 2초 — 수식은 그 프레임 번호(반올림)로만 시각을 쓴다
                Assert.Contains("floor((t+0)*30+0.5)", Graph(project, 0, 120).Replace("(T+", "(t+"));
                var seek = Graph(project, 90, 91).Replace("(T+", "(t+");
                Assert.Contains("floor((t+2)*30+0.5)", seek);
                Assert.DoesNotContain("(t+0)", seek);
            }
            // 칸 번호: 블록 글리치는 1초에 8번(글리치가 일어날지)·16번(조각 무늬)
            clip.Effects = null;
            With(clip, BlockGlitch.Def);
            var block = Graph(project, 0, 120);
            Assert.Contains("floor(floor((T+0)*30+0.5)*0.266667+0.001)", block);
            Assert.Contains("floor(floor((T+0)*30+0.5)*0.533333+0.001)", block);
        }

        [Fact]
        public void Graph_RgbSplit_PadsCropsAndMergesPlanes()
        {
            var (project, clip) = VideoProject();
            var effect = With(clip, RgbSplit.Def, e =>
            {
                VideoEffects.SetNumber(e, "amount", 10);
                VideoEffects.SetNumber(e, "jitter", 0);
                VideoEffects.SetNumber(e, "glow", 0);
            });
            var filter = Graph(project, 0, 30);
            // 640×360 틀(배율 1): 10px — 늘일 폭 = 10 + 1
            Assert.Contains("[fx0e0w]pad=662:382:11:11,fillborders=left=11:right=11:top=11:bottom=11:mode=smear,split=2[fx0e0qr][fx0e0qb]", filter);
            Assert.Contains("mergeplanes=format=gbrap:map0s=0:map0p=0:map1s=1:map1p=1:map2s=2:map2p=2:map3s=0:map3p=3", filter);
            Assert.DoesNotContain("avgblur", filter);
            // 빛 번짐: 1.6배 멀리 민 빨강·파랑(늘일 폭도 16 + 1)을 흐려 밝은 쪽으로
            VideoEffects.SetNumber(effect, "glow", 50);
            filter = Graph(project, 0, 30);
            Assert.Contains("pad=674:394:17:17,", filter);
            Assert.Contains("split=4[fx0e0qr][fx0e0qb][fx0e0qr2][fx0e0qb2]", filter);
            Assert.Contains("avgblur=sizeX=6:planes=6", filter);
            Assert.Contains("blend=all_mode=lighten:all_opacity=0.4", filter);
            // 거리 0이면 그리지 않는다
            VideoEffects.SetNumber(effect, "amount", 0);
            Assert.DoesNotContain("[fx0e0", Graph(project, 0, 30));
        }

        [Fact]
        public void Graph_BlockAndLine_UseOneDisplace_NeutralMapsKeepTheAlpha()
        {
            var (project, clip) = VideoProject();
            With(clip, BlockGlitch.Def);
            var block = Graph(project, 0, 30);
            Assert.Equal(1, Count(block, "displace="));
            Assert.Contains("[fx0e0a][fx0e0x][fx0e0y]displace=edge=smear", block);
            Assert.Contains("shuffleplanes=map0=2:map1=2:map2=2:map3=2", block);
            Assert.Contains("blend=c0_mode=grainmerge:c1_mode=grainmerge:c2_mode=grainmerge:c3_mode=normal", block);
            // 색 어긋남 0이면 색 바꾸기 지도·섞기가 없다
            VideoEffects.SetNumber(clip.Effects[0], "color", 0);
            Assert.DoesNotContain("grainmerge", Graph(project, 0, 30));
            // 빈도 0 또는 세기 0이면 그리지 않는다
            VideoEffects.SetNumber(clip.Effects[0], "rate", 0);
            Assert.DoesNotContain("[fx0e0", Graph(project, 0, 30));

            clip.Effects = null;
            With(clip, LineSlip.Def);
            var line = Graph(project, 0, 30);
            Assert.Contains("[fx0e0a][fx0e0x][fx0e0y]displace=edge=smear", line);
            // 세로 지도는 모든 플레인 128 — 알파도(0x80808080), 그래서 알파가 세로로 밀리지 않는다
            Assert.Contains("color=c=0x80808080:s=640x360", line);
            Assert.Contains("color=c=black:s=1x", line);
        }

        [Fact]
        public void Graph_BadSignal_RollsWithVstack_NoiseIsDrawnOnce_ScanlinesStayInPlace()
        {
            var (project, clip) = VideoProject();
            var effect = With(clip, BadSignal.Def);
            var filter = Graph(project, 0, 60);
            var bar = BadSignal.BarHeight(360);
            Assert.Contains("color=c=black@0:s=640x" + bar, filter);
            Assert.Contains("vstack=3,crop=640:360:0:", filter);
            // 잡음 판: 한 장(d = 반 프레임)을 수식으로 그려 구간 끝까지 늘이고 자른다 — 높이는 주사선 간격의 배수로만
            Assert.Equal(4, BadSignal.ScanPeriod(1));
            Assert.Equal(2, BadSignal.ScanPeriod(0.25));
            // 판(768×488)은 절반 크기로 그려 두 배로(가장 가까운 화소) 키운다 — 곁 사슬이라 클립 사슬의 scale 하나 규칙과 상관없다
            Assert.Contains("color=c=black:s=384x244:r=30/1:d=0.016667,format=gray,geq=lum=", filter);
            Assert.Contains(",scale=iw*2:ih*2:flags=neighbor,format=gbrap,tpad=", filter);
            Assert.Contains("mod(2*Y,4)", filter);
            Assert.Contains("tpad=stop_mode=clone:stop_duration=2,crop=640:360:", filter);
            Assert.Contains("'4*floor(32*", filter);
            Assert.Contains(",colorchannelmixer=", filter);
            // 롤링·흔들림을 끄면 쌓지 않고, 노이즈·주사선을 끄면 판이 없고, 색 번짐을 끄면 채도 줄이기가 없다
            foreach (var key in new[] { "noise", "scan", "bleed", "roll" })
                VideoEffects.SetNumber(effect, key, 0);
            filter = Graph(project, 0, 60);
            Assert.Contains("vstack=3", filter);
            Assert.DoesNotContain("geq=lum", filter);
            Assert.DoesNotContain("colorchannelmixer", filter);
            Assert.Contains("displace=edge=smear", filter);
            VideoEffects.SetNumber(effect, "jitter", 0);
            Assert.DoesNotContain("[fx0e0", Graph(project, 0, 60));
        }

        [Fact]
        public void Graph_PersonOnly_MergesTheGlitchOverTheOriginal_RgbOnly()
        {
            foreach (var def in Glitches)
            {
                var (project, clip) = VideoProject();
                With(clip, def, e => e.PersonOnly = true);
                var filter = Graph(project, 0, 30, new FakeMasks("a.mp4"));
                // 섞는 마스크 = 사람 마스크와 그것을 그림과 똑같이 민 것 중 큰 값(밀려 나간 사람 픽셀도 보이게)
                Assert.Contains("[fx0m0]split[fx0e0mk][fx0e0mj];", filter);
                Assert.Contains("[fx0e0mj][fx0e0mt]blend=all_mode=lighten[fx0e0ml];[fx0e0o][fx0e0g][fx0e0ml]maskedmerge=planes=7", filter);
                Assert.Contains(def == RgbSplit.Def ? "[fx0e0mk]extractplanes=g,split=" : "[fx0e0xm][fx0e0ym]displace=edge=smear", filter);
                // 마스크가 없으면 클립 전체에(섞기 없음)
                Assert.DoesNotContain("maskedmerge", Graph(project, 0, 30, new FakeMasks("other.mp4")));
            }
            // 그릴 것이 없어도(세기 0) 마스크 흐름은 써야 한다 — 원본끼리 섞어 그림은 그대로
            var (p2, c2) = VideoProject();
            With(c2, BlockGlitch.Def, e =>
            {
                e.PersonOnly = true;
                VideoEffects.SetNumber(e, "amount", 0);
            });
            var pass = Graph(p2, 0, 30, new FakeMasks("a.mp4"));
            Assert.Contains(",split[fx0e0o][fx0e0g];[fx0e0o][fx0e0g][fx0m0]maskedmerge=planes=7", pass);
            Assert.DoesNotContain("displace", pass);
        }

        [Fact]
        public void Graph_AllGlitchesOnOneClip_AndTheThumbnailRecipe()
        {
            var (project, clip) = VideoProject();
            foreach (var def in Glitches)
                With(clip, def, e => e.PersonOnly = true);
            var filter = Graph(project, 0, 30, new FakeMasks("a.mp4"));
            // 마스크는 효과마다 하나씩 나눠 쓴다
            Assert.Contains("split=4[fx0m0][fx0m1][fx0m2][fx0m3]", filter);
            for (var i = 0; i < 4; i++)
            {
                Assert.Contains("[fx0m" + i + "]split[fx0e" + i + "mk][fx0e" + i + "mj];", filter);
                Assert.Contains("[fx0e" + i + "o][fx0e" + i + "g][fx0e" + i + "ml]maskedmerge=planes=7", filter);
            }
            foreach (var def in Glitches)
            {
                var args = VideoEffects.PreviewArguments(def);
                var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
                Assert.StartsWith("[0]format=gbrap", graph);
                Assert.EndsWith(",format=rgb24,tile=15x1", graph);
                Assert.Contains("floor((t+0)*10+0.5)", graph.Replace("(T+", "(t+"));
                Assert.DoesNotMatch(Forbidden, graph);
                Assert.Equal(24, VideoEffects.PreviewKey(def).Length);
            }
            Assert.Equal(Glitches.Length, Glitches.Select(VideoEffects.PreviewKey).Distinct().Count());
        }

        // ---------- 시각 해시 ----------

        [Fact]
        public void Hash_MatchesTheFfmpegFormula_AndBurstsFollowTheRate()
        {
            Assert.Equal("mod(abs(sin(x)*43758.5453),1)", Glitch.HashExpr("x"));
            for (var x = 0.0; x < 50; x += 0.37)
                Assert.InRange(Glitch.Hash(x), 0, 1);
            // 칸 번호: 30fps에서 1초에 8번 → 프레임 0~3은 0번 칸, 4번 프레임부터 1번 칸(3.75장마다)
            Assert.Equal(0, Glitch.Slot(3, 30, 8));
            Assert.Equal(1, Glitch.Slot(4, 30, 8));
            Assert.Equal(8, Glitch.Slot(30, 30, 8));
            // 빈도 0이면 늘 조용하고 100이면 늘 글리치, 그 사이는 대략 그 비율
            var effect = VideoEffects.Create(BlockGlitch.Def);
            VideoEffects.SetNumber(effect, "rate", 0);
            Assert.All(Enumerable.Range(0, 300), f => Assert.False(BlockGlitch.IsBurst(effect, f, 30)));
            VideoEffects.SetNumber(effect, "rate", 100);
            Assert.All(Enumerable.Range(0, 300), f => Assert.True(BlockGlitch.IsBurst(effect, f, 30)));
            VideoEffects.SetNumber(effect, "rate", 35);
            var share = Enumerable.Range(0, 3000).Count(f => BlockGlitch.IsBurst(effect, f, 30)) / 3000.0;
            Assert.InRange(share, 0.2, 0.5);
            // 롤링: 칸(1초) 앞 60%에 말려 올라가고 남은 동안 그대로
            var tv = VideoEffects.Create(BadSignal.Def);
            VideoEffects.SetNumber(tv, "rate", 100);
            Assert.Equal(0, BadSignal.Roll(tv, 30, 30).Progress, 6);
            Assert.Equal(0.5, BadSignal.Roll(tv, 39, 30).Progress, 3);
            Assert.Equal(1, BadSignal.Roll(tv, 50, 30).Progress, 6);
            Assert.True(BadSignal.Roll(tv, 50, 30).Rolling);
        }

        // ---------- displace 한도 넘기(#29) ----------

        [Fact]
        public void Push_OnePassIsTheOldText_TwoPassesAddUpToTheClampedOffset()
        {
            Assert.Equal(127, Glitch.Limit(1));
            Assert.Equal(127, Glitch.Limit(0.5));
            Assert.Equal(254, Glitch.Limit(2));
            Assert.Equal(254, Glitch.Limit(4));
            Assert.Equal("clip(128+E,0,255)", Glitch.Push("E", 0, 127));
            // 두 번에 나눈 앞·뒤의 합 = round(E)를 ±한도로 자른 값, 각각 128 ± 127 안
            foreach (var e in new[] { -300, -254, -201, -127, -1, 0, 1, 77, 127, 200, 253, 254, 400 })
            {
                var total = Math.Max(-254, Math.Min(254, e));
                var first = total - (int)Math.Truncate(total / 2.0);
                var second = (int)Math.Truncate(total / 2.0);
                Assert.Equal(total, first + second);
                Assert.InRange(128 + first, 1, 255);
                Assert.InRange(128 + second, 1, 255);
            }
            Assert.Equal("(128+clip(round(E),-254,254)-trunc(clip(round(E),-254,254)/2))", Glitch.Push("E", 1, 254));
            Assert.Equal("(128+trunc(clip(round(E),-254,254)/2))", Glitch.Push("E", 2, 254));
        }

        [Fact]
        public void LineSlip_OnePassAtProjectSize_TwoPassesWhenExportedLarger()
        {
            var effect = VideoEffects.Create(LineSlip.Def);
            VideoEffects.SetNumber(effect, "amount", 100);
            string Recipe(double scale) => LineSlip.Def.Recipe(new EffectRender { Effect = effect, Def = LineSlip.Def, Width = (int)(640 * scale), Height = (int)(360 * scale), PixelScale = scale, Rate = "30/1", Fps = 30, Seconds = 2, TimeOffset = 0, Label = "fx0" });
            var one = Recipe(1);
            Assert.Single(Regex.Matches(one, "displace="));
            Assert.DoesNotContain("trunc(", one);
            var two = Recipe(2);
            Assert.Equal(2, Regex.Matches(two, "displace=").Count);
            Assert.Contains("trunc(", two);
            // 작은 세기는 2배에서도 한도 안이라 한 번
            VideoEffects.SetNumber(effect, "amount", 30);
            Assert.Single(Regex.Matches(Recipe(2), "displace="));
        }

        [Fact]
        public void BadSignalAndGlitchScan_TwoPassesOnlyWhenExportedLargerPastTheLimit()
        {
            foreach (var def in new[] { BadSignal.Def, GlitchScan.Def })
            {
                var effect = VideoEffects.Create(def);
                foreach (var param in def.Params)
                    VideoEffects.SetNumber(effect, param.Key, param.Max);
                string Recipe(double scale) => def.Recipe(new EffectRender { Effect = effect, Def = def, Width = (int)(640 * scale), Height = (int)(360 * scale), PixelScale = scale, Rate = "30/1", Fps = 30, Seconds = 2, TimeOffset = 0, Label = "fx0" });
                // 프로젝트 크기는 예전처럼 한 번(한도 127px에서 잘림 그대로)
                Assert.Single(Regex.Matches(Recipe(1), "displace="));
                Assert.DoesNotContain("trunc(", Recipe(1));
                // 2배로 내보내면 두 번에 나눠 254px까지
                var two = Recipe(2);
                Assert.Equal(2, Regex.Matches(two, "displace=").Count);
                Assert.Contains(",-254,254)", two);
            }
        }
    }
}
