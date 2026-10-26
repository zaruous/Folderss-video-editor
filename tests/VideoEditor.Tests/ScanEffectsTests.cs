using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>스캔 계열 효과(쇼크웨이브 스캔·SF 스캔·레이 스캐닝·글리치 스캔): 목록·매개변수, 필터 조각의 규칙(쓰면 안 되는 함수·크기 바꾸기·라벨), 시각·모양 계산.</summary>
    public class ScanEffectsTests
    {
        private static readonly VideoEffectDef[] Family = { ShockwaveScan.Def, SciFiScan.Def, RayScan.Def, GlitchScan.Def };

        public static IEnumerable<object[]> Ids()
        {
            return Family.Select(d => new object[] { d.Id });
        }

        private static EffectRender Render(VideoEffectDef def, ClipEffect effect, int width, int height, double pixelScale, string mask = null, double offset = 0)
        {
            return new EffectRender
            {
                Effect = effect,
                Def = def,
                Width = width,
                Height = height,
                PixelScale = pixelScale,
                Rate = "30/1",
                Fps = 30,
                Seconds = 2,
                TimeOffset = offset,
                Label = "fx0e0",
                Mask = mask
            };
        }

        [Fact]
        public void Catalog_ScanFamilyFollowsScanLine_InOrder_WithTypedParams()
        {
            Assert.Same(ScanLine.Def, VideoEffects.All[0]);
            Assert.Equal(new[] { "shockwave-scan", "scifi-scan", "ray-scan", "glitch-scan" }, VideoEffects.All.Skip(1).Take(4).Select(d => d.Id));
            Assert.Equal(new[] { "쇼크웨이브 스캔", "SF 스캔", "레이 스캐닝", "글리치 스캔" }, Family.Select(d => d.Name));
            foreach (var def in Family)
            {
                Assert.Same(def, VideoEffects.Find(def.Id));
                Assert.Equal("스캔·빛", def.Group);
                Assert.False(string.IsNullOrWhiteSpace(def.Description));
                Assert.DoesNotContain("\n", def.Description);
                Assert.True(VideoEffects.IsValidId(def.Id));
                Assert.All(def.Params, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
                Assert.All(def.Params, p => Assert.False(string.IsNullOrWhiteSpace(p.Hint)));
                Assert.All(def.Params.Where(p => p.Kind == EffectParamKind.Number), p =>
                {
                    Assert.True(p.Min < p.Max, def.Id + "." + p.Key);
                    Assert.InRange(p.Default, p.Min, p.Max);
                    Assert.Matches("^[a-z][a-z0-9]{0,23}$", p.Key);
                });
                Assert.Equal(def.Params.Count, def.Params.Select(p => p.Key).Distinct().Count());
                // 썸네일 값은 있는 매개변수의 범위 안 값이고, 1.5초 띠에 한 번 훑고 쉬는 것까지 보이게 주기 1.5초
                Assert.All(def.PreviewValues, v => Assert.InRange(v.Value, def.Param(v.Key).Min, def.Param(v.Key).Max));
                Assert.Equal(1.5, def.PreviewValues["period"]);
                Assert.NotNull(def.Param("opacity"));
                Assert.Equal("%", def.Param("opacity").Unit);
            }
            Assert.Equal(VideoEffects.All.Count, VideoEffects.All.Select(d => d.Id).Distinct().Count());
            // 색이 있는 효과(글리치는 색이 없다)
            Assert.Equal("#3FD0FF", ShockwaveScan.Def.ColorParam.DefaultColor);
            Assert.Equal("#3CE6FF", SciFiScan.Def.ColorParam.DefaultColor);
            Assert.Equal("#FF3355", RayScan.Def.ColorParam.DefaultColor);
            Assert.Null(GlitchScan.Def.ColorParam);
            Assert.Equal(new[] { "glow", "core", "refract", "period", "opacity", "cx", "cy", "color" }, ShockwaveScan.Def.Params.Select(p => p.Key));
            Assert.Equal(new[] { "band", "grid", "lines", "glow", "period", "opacity", "color" }, SciFiScan.Def.Params.Select(p => p.Key));
            Assert.Equal(new[] { "glow", "core", "trail", "rays", "dir", "period", "opacity", "color" }, RayScan.Def.Params.Select(p => p.Key));
            Assert.Equal(new[] { "band", "amount", "split", "period", "opacity" }, GlitchScan.Def.Params.Select(p => p.Key));
        }

        [Fact]
        public void Values_SaveAndClean_LikeOtherEffects()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            foreach (var def in Family)
                VideoEffects.Add(project, clip, def);
            VideoEffects.SetNumber(clip.Effects[2], "dir", 9);
            VideoEffects.SetNumber(clip.Effects[0], "cx", -5);
            var back = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project)).FindClip(clip.Id).Effects;
            Assert.Equal(Family.Select(d => d.Id), back.Select(e => e.Id));
            Assert.Equal(3, back[2].Values["dir"]);
            Assert.Equal(0, back[0].Values["cx"]);
            Assert.Null(back[3].Color);
            Assert.Equal("#3CE6FF", back[1].Color);
            Assert.Equal(4, VideoEffects.Renderable(clip).Count);
        }

        /// <summary>조각에서 "[라벨]"을 모두.</summary>
        private static List<string> Labels(string text)
        {
            return Regex.Matches(text, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>여러 크기·값·사람에게만에서 조각을 만든다.</summary>
        private static IEnumerable<(string Case, EffectRender Render)> Cases(VideoEffectDef def)
        {
            var sizes = new[] { (1920, 1080, 1.0), (640, 360, 1.0 / 3), (333, 187, 0.2), (128, 72, 0.16), (8, 6, 0.01), (2, 2, 0.01), (1080, 1920, 1.0), (3840, 2160, 2.0) };
            foreach (var (w, h, k) in sizes)
            {
                foreach (var setting in new[] { "기본", "최소", "최대" })
                {
                    foreach (var mask in new[] { null, "fx0m0" })
                    {
                        var effect = VideoEffects.Create(def);
                        if (setting != "기본")
                        {
                            foreach (var p in def.Params.Where(p => p.Kind != EffectParamKind.Color))
                                VideoEffects.SetNumber(effect, p.Key, setting == "최소" ? p.Min : p.Max);
                        }
                        yield return (def.Id + " " + w + "x" + h + " " + setting + (mask != null ? " 사람에게만" : ""), Render(def, effect, w, h, k, mask, 1.25));
                    }
                }
            }
            for (var dir = 0; dir < 4; dir++)
            {
                var effect = VideoEffects.Create(def);
                VideoEffects.SetNumber(effect, "dir", dir);
                yield return (def.Id + " 방향 " + dir, Render(def, effect, 640, 360, 1.0 / 3, null, 1.25));
            }
        }

        [Fact]
        public void Shockwave_DirectionMap_IsDrawnCoarse_AndStretchedSmoothly()
        {
            // 큰 틀에서 방향 지도를 q×q칸마다 그려 가장 가까운 칸으로 늘이면(remap) 고리 안쪽에 계단이 졌다(#29) —
            // 양선형으로 늘이면 같은 비용에 방향 오차(평균)가 1/6 남짓(실측·계산은 이슈 #29)
            var effect = VideoEffects.Create(ShockwaveScan.Def);
            VideoEffects.SetNumber(effect, "refract", 100);
            string Recipe(int w, int h) => ShockwaveScan.Def.Recipe(new EffectRender { Effect = effect, Def = ShockwaveScan.Def, Width = w, Height = h, PixelScale = 1, Rate = "30/1", Fps = 30, Seconds = 2, TimeOffset = 0, Label = "fx0" });
            var hd = Recipe(1920, 1080);
            Assert.Contains("s=480x270:", hd);
            Assert.Contains("[fx0t]scale=1920:1080:flags=bilinear,crop=1920:1080:0:0,tpad=", hd);
            // 빛 모양 표의 remap 하나뿐(방향 지도는 remap으로 늘이지 않음)
            Assert.Single(Regex.Matches(hd, "remap"));
            // 나누어떨어지지 않는 크기: 칸을 올림해 늘인 뒤 틀 크기로 자른다
            Assert.Contains("[fx0t]scale=1284:724:flags=bilinear,crop=1281:721:0:0,", Recipe(1281, 721));
            // 중간 크기는 2×2칸, 작은 틀은 픽셀마다(늘이지 않음)
            Assert.Contains("[fx0t]scale=640:360:flags=bilinear", Recipe(640, 360));
            Assert.DoesNotContain("scale=", Recipe(320, 180));
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void Recipe_FollowsTheRules_NoForbiddenFunctions_NoScale_LabelsOwnedAndUsedOnce(string id)
        {
            var def = VideoEffects.Find(id);
            foreach (var (name, render) in Cases(def))
            {
                var text = def.Recipe(render);
                // 지금 흐름에 이어 붙이고(라벨로 끊거나 ","로 잇기), 마지막 필터의 출력에는 라벨이 없다
                Assert.True(text.StartsWith("[" + render.Label, StringComparison.Ordinal) || text.StartsWith(",", StringComparison.Ordinal), name + ": " + text.Substring(0, Math.Min(80, text.Length)));
                Assert.False(text.EndsWith("]", StringComparison.Ordinal), name);
                Assert.DoesNotContain(";;", text);
                // 조각마다 값이 섞이거나 그릴 때마다 달라지는 함수, 숫자가 아닌 숫자
                foreach (var bad in new[] { "st(", "ld(", "random", "time(", "NaN", "Infinity", "∞", "E+", "E-" })
                    Assert.False(text.Contains(bad, StringComparison.Ordinal), name + ": '" + bad + "'");
                // 크기 바꾸기는 클립 흐름에 잇지 않는다 — 따로 그린 방향 지도(쇼크웨이브, 한 장)를 매끄럽게 늘이는 것만
                foreach (Match scale in Regex.Matches(text, "scale="))
                    Assert.Equal("[" + render.Label + "t]scale=", text.Substring(scale.Index - render.Label.Length - 3, render.Label.Length + 9));
                // 라벨은 이 효과의 것(앞머리)이고 한 번 내고 한 번 쓴다. 마스크 라벨은 받은 것을 한 번만 쓴다
                var labels = Labels(text);
                foreach (var group in labels.GroupBy(l => l))
                {
                    if (group.Key == render.Mask)
                    {
                        Assert.Single(group);
                        continue;
                    }
                    Assert.StartsWith(render.Label, group.Key);
                    Assert.True(group.Count() == 2, name + ": [" + group.Key + "] " + group.Count() + "번");
                }
                if (render.Mask != null)
                    Assert.Contains(render.Mask, labels);
                // 수식의 시각은 클립 안 시각(t + 앞선 초)
                Assert.Contains("t+1.25", text);
            }
        }

        [Fact]
        public void Graph_ClipChainKeepsOneScale_AndTimeIsClipLocal()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            clip.SourceOut = 90;
            clip.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, clip, 30);
            foreach (var def in Family)
                VideoEffects.Add(project, clip, def);
            // 클립 안 2초(프레임 90)부터 한 장
            var filter = RenderGraph.Build(project, new RenderOptions { From = 90, To = 91, Width = project.Width, Height = project.Height, Audio = false, Preview = true, TempFolder = "T:/tmp" }).Filter;
            var clipChain = filter.Split(";\n").Single(c => c.Contains("[fx0e0a]"));
            // 클립 흐름의 크기 바꾸기는 체인 앞(원본 → 틀 맞춤) 하나뿐 — 효과는 클립 흐름에 크기 바꾸기를 잇지 않는다(따로 그린 방향 지도 "[…t]scale"은 빼고 센다)
            Assert.Single(Regex.Matches(clipChain, @"(?<!t\])scale="));
            Assert.Single(Regex.Matches(clipChain, @"t\]scale="));
            Assert.Contains("mod(t+2,", clipChain);
            for (var i = 0; i < Family.Length; i++)
                Assert.Contains("[fx0e" + i, clipChain);
        }

        // ---------- 시각·모양 ----------

        [Fact]
        public void Shockwave_RadiusGrowsFromCenter_ToBeyondTheFarthestCorner_ThenRests()
        {
            var effect = VideoEffects.Create(ShockwaveScan.Def);
            var shape = ShockwaveScan.Measure(effect, 1920, 1080, 1);
            Assert.Equal(960, shape.CenterX, 6);
            Assert.Equal(Math.Sqrt(960 * 960 + 540 * 540), shape.Reach, 6);
            Assert.True(shape.End > shape.Reach + shape.Glow);
            Assert.Equal(20, shape.Refract);
            Assert.Equal(0, ShockwaveScan.Radius(effect, 0, 1920, 1080, 1));
            var last = -1.0;
            for (var t = 0.0; t < 1.76; t += 0.02)
            {
                var radius = ShockwaveScan.Radius(effect, t, 1920, 1080, 1);
                Assert.True(radius >= last);
                last = radius;
            }
            // 처음엔 빠르고 끝에선 느리다
            Assert.True(ShockwaveScan.Radius(effect, 0.1, 1920, 1080, 1) > 4 * (ShockwaveScan.Radius(effect, 1.7, 1920, 1080, 1) - ShockwaveScan.Radius(effect, 1.6, 1920, 1080, 1)));
            // 쉬는 동안(주기 끝 20%)은 세기 0, 다시 처음부터
            Assert.Equal(0, ShockwaveScan.Strength(2.0, 2.2));
            Assert.Equal(1, ShockwaveScan.Strength(0.5, 2.2));
            Assert.Equal(ShockwaveScan.Radius(effect, 0.3, 1920, 1080, 1), ShockwaveScan.Radius(effect, 2.5, 1920, 1080, 1), 6);
            // 중심을 옮기면 가장 먼 모서리까지
            VideoEffects.SetNumber(effect, "cx", 0);
            VideoEffects.SetNumber(effect, "cy", 100);
            Assert.Equal(Math.Sqrt(1920.0 * 1920 + 1080 * 1080), ShockwaveScan.Measure(effect, 1920, 1080, 1).Reach, 6);
            // 미는 거리는 8비트 칸에 담기는 한도 안
            VideoEffects.SetNumber(effect, "refract", 100);
            Assert.InRange(ShockwaveScan.Measure(effect, 7680, 4320, 4).Refract, 1, 120);
        }

        [Fact]
        public void BandsAndBeam_MoveTheRightWay_AndFitTheBox()
        {
            var sf = VideoEffects.Create(SciFiScan.Def);
            var glitch = VideoEffects.Create(GlitchScan.Def);
            Assert.True(SciFiScan.BandTop(sf, 1.3, 320, 240, 1) > SciFiScan.BandTop(sf, 1.0, 320, 240, 1) + 5);
            Assert.True(GlitchScan.BandTop(glitch, 1.2, 320, 240, 1) > GlitchScan.BandTop(glitch, 0.9, 320, 240, 1) + 5);
            Assert.Equal(-SciFiScan.Measure(sf, 320, 240, 1).Window, SciFiScan.BandTop(sf, 0, 320, 240, 1));
            Assert.Equal(240, GlitchScan.BandTop(glitch, 2.3, 320, 240, 1));
            // 창은 틀보다 높지 않다(아주 작은 틀·띠 100%에서도)
            VideoEffects.SetNumber(sf, "band", 100);
            VideoEffects.SetNumber(sf, "glow", 100);
            foreach (var (w, h, k) in new[] { (320, 240, 1.0), (8, 6, 0.01), (2, 2, 0.01), (128, 72, 0.16) })
            {
                Assert.InRange(SciFiScan.Measure(sf, w, h, k).Window, 2, h);
                Assert.InRange(GlitchScan.Measure(glitch, w, h, k).Band, 2, h);
            }
            VideoEffects.SetNumber(glitch, "amount", 100);
            VideoEffects.SetNumber(glitch, "split", 60);
            // 2배로 내보내면 한도도 두 배(두 번에 나눠 민다 — #29): 1080p의 120px이 240px
            Assert.Equal(240, GlitchScan.Measure(glitch, 3840, 2160, 2).Shift);
            Assert.Equal(120, GlitchScan.Measure(glitch, 1920, 1080, 1).Shift);
            Assert.Equal(120, GlitchScan.Measure(glitch, 3840, 2160, 2).Split);

            // 레이: 방향마다 나아가는 쪽으로
            var ray = VideoEffects.Create(RayScan.Def);
            var expected = new[] { +1, -1, +1, -1 };
            for (var dir = 0; dir < 4; dir++)
            {
                VideoEffects.SetNumber(ray, "dir", dir);
                Assert.Equal(dir < 2, RayScan.Measure(ray, 320, 240, 1).Vertical);
                var a = RayScan.BeamPosition(ray, 0.8, 320, 240, 1);
                var b = RayScan.BeamPosition(ray, 1.1, 320, 240, 1);
                Assert.True(Math.Sign(b - a) == expected[dir], "방향 " + dir + ": " + a + " → " + b);
            }
            // 소수 방향은 가까운 정수로
            VideoEffects.SetNumber(ray, "dir", 1.6);
            Assert.Equal(2, RayScan.Measure(ray, 320, 240, 1).Direction);
        }

        [Fact]
        public void PreviewArguments_UseTheSameRecipes_AndKeysDiffer()
        {
            foreach (var def in Family)
            {
                var args = VideoEffects.PreviewArguments(def);
                var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
                Assert.StartsWith("[0]format=gbrap", graph);
                Assert.Contains("mod(t+0,1.5)/1.2", graph);
                Assert.EndsWith(",format=rgb24,tile=15x1", graph);
            }
            Assert.Equal(VideoEffects.All.Count, VideoEffects.All.Select(VideoEffects.PreviewKey).Distinct().Count());
        }
    }
}
