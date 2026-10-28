using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>미리 그릴 전환 구간 하나: 수식 전환이 든 클립(들어오는 클립)과 그 전환이 그려지는 타임라인 프레임 [Start, End).</summary>
    public sealed class PrerenderWindow
    {
        public Track Track { get; set; }
        public Clip Clip { get; set; }
        public TransitionDef Transition { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
    }

    /// <summary>
    /// 재생 때 쓸 미리 그린 전환 구간: 들어오는 클립의 Id, 타임라인 프레임 [Start, End), 캐시 파일(그 구간의 완성 화면 — 멈춘 화면 그래프로 그린 BGRA를 프레임마다 한 장, 무손실).
    /// </summary>
    public sealed class PrerenderedSpan
    {
        public string ClipId { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// 무거운 수식 전환 구간 미리 그리기(재생용). 수식 전환은 픽셀마다 수식을 풀어 재생이 끊긴다 — 줄인 크기로 섞어도(<see cref="RenderGraph.PlaybackShrink"/>)
    /// 이 PC 기준 960×540 하트가 장당 75ms(이징 inout이면 212ms)라 실시간이 안 된다. 그래서 재생할 구간의 전환을 따로 미리 그려 두고(캐시), 재생은 그것을 읽는다.
    /// - 캐시는 그 구간의 완성 화면이다: 멈춘 화면과 같은 그래프(재생 축소 없음)로 [Start, End)를 그린 것이라 미리 보기·내보내기와 같은 그림이다.
    /// - 재생 그래프(<see cref="RenderOptions.Prerendered"/>)는 그 전환을 가벼운 xfade로 잇고(구간 밖에서 xfade는 앞·뒤 그림을 그대로 넘긴다 — 결과가 같다),
    ///   묶음을 줄이지 않으며, 맨 끝에서 구간 동안만 캐시 그림을 화면 전체에 덮는다(불투명 — 그대로 복사됨). 그래서 재생 화면이 구간 안팎 모두 멈춘 화면과 같다.
    /// - 캐시 입력은 도우미가 아는 옵션(-ss·-i)만 쓴다.
    /// </summary>
    public static class TransitionPrerender
    {
        /// <summary>미리 그린 전환 대신 잇는 xfade — 구간 동안은 캐시 그림이 덮어 보이지 않으므로 가장 가벼운 내장 효과(섞기)로.</summary>
        public const string HiddenXfade = "transition=fade";

        /// <summary>
        /// [from, to)에 걸치는 무거운 전환(수식 xfade — <see cref="TransitionDef.IsExpression"/>) 구간. 렌더 그래프가 xfade로 그리는 것과 같은 조건이다
        /// (보이는 동영상 트랙의 화면 클립, 디졸브가 아닌 전환, 길이 = min(전환 길이, 클립 길이)). 재생 헤드에서 가까운 차례(시작 프레임 순).
        /// </summary>
        public static List<PrerenderWindow> Windows(Project project, long from, long to, int ffmpegMajor)
        {
            var result = new List<PrerenderWindow>();
            if (project == null || to <= from)
                return result;
            foreach (var track in project.VideoTracks)
            {
                if (track.Hidden || track.Subtitles)
                    continue;
                foreach (var clip in track.Clips.Where(c => c.IsVisual))
                {
                    var length = Math.Min(clip.TransitionIn, clip.Duration);
                    if (length <= 0 || from >= clip.Start + length || to <= clip.Start)
                        continue;
                    var def = Transitions.Resolve(clip, ffmpegMajor);
                    if (def.IsDissolve || !def.IsExpression)
                        continue;
                    result.Add(new PrerenderWindow { Track = track, Clip = clip, Transition = def, Start = clip.Start, End = clip.Start + length });
                }
            }
            return result.OrderBy(w => w.Start).ToList();
        }

        /// <summary>이 클립의 전환이 미리 그린 구간으로 덮이는지(재생 미리 보기에서만).</summary>
        internal static bool Covers(RenderOptions options, Clip clip)
        {
            return Spans(options).Any(s => s.ClipId == clip.Id);
        }

        /// <summary>쓸 미리 그린 구간: 재생 미리 보기이고 그리는 구간에 걸치는 것만.</summary>
        private static IEnumerable<PrerenderedSpan> Spans(RenderOptions options)
        {
            if (!options.Playback || !options.Preview || options.Prerendered == null)
                return Enumerable.Empty<PrerenderedSpan>();
            return options.Prerendered.Where(s => s != null && !string.IsNullOrEmpty(s.Path) && s.End > s.Start && s.Start < options.To && s.End > options.From);
        }

        /// <summary>
        /// 동영상 그래프의 끝: layer를 출력 형식으로 [vout]에. 미리 그린 구간이 있으면 BGRA로 바꾼 뒤 구간마다 캐시 그림을 덮는다 —
        /// 캐시(완성 화면, 알파 255)를 RGB 얹기로 덮으면 그 픽셀이 그대로 복사된다. 구간 밖(enable이 거짓)은 얹지 않고 그대로 넘긴다.
        /// </summary>
        internal static void Finish(RenderOptions options, RenderPlan plan, FrameRate rate, string layer, List<string> filters)
        {
            var spans = Spans(options).OrderBy(s => s.Start).ToList();
            if (spans.Count == 0)
            {
                filters.Add("[" + layer + "]format=" + (options.Preview ? "bgra" : "yuv420p") + "[vout]");
                return;
            }
            filters.Add("[" + layer + "]format=bgra[pv0]");
            for (var i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                var first = Math.Max(span.Start, options.From);
                var skip = first - span.Start;
                // 캐시는 [Start, End)를 프레임마다 한 장. 재생이 구간 도중에서 시작하면 그만큼 건너뛴다 —
                // 컨테이너 시각이 ms로 반올림돼 있어(1/30초 = 33.3ms) 반 프레임 앞을 찾으면 그 프레임부터 나온다
                var input = skip > 0
                    ? RenderGraph.AddInput(plan, "-ss", RenderGraph.N(rate.ToSeconds(skip) - 0.5 / rate.Fps), "-i", span.Path)
                    : RenderGraph.AddInput(plan, "-i", span.Path);
                // 프레임 레이트로 고르게 맞춘 뒤(반올림한 시각을 정확한 프레임 칸에) 구간이 시작하는 자리로 옮긴다
                filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}:v]setpts=PTS-STARTPTS,fps={1},setpts=PTS+round({2}/TB),format=bgra[pc{3}]",
                    input, rate.Rational, RenderGraph.N(rate.ToSeconds(first - options.From)), i));
                // 구간 [first, End)의 프레임에서만(경계는 반 프레임 안쪽 — 시각 오차에 흔들리지 않게)
                var from = RenderGraph.N((first - options.From - 0.5) / rate.Fps);
                var to = RenderGraph.N((span.End - options.From - 0.5) / rate.Fps);
                filters.Add(string.Format(CultureInfo.InvariantCulture, "[pv{0}][pc{0}]overlay=x=0:y=0:format=rgb:eof_action=pass:enable='between(t,{1},{2})'{3}",
                    i, from, to, i + 1 < spans.Count ? "[pv" + (i + 1) + "]" : "[vout]"));
            }
        }
    }
}
