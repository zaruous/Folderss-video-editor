using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>무엇을 그릴지: 프로젝트의 [From, To) 프레임을 Width×Height로.</summary>
    public sealed class RenderOptions
    {
        public long From { get; set; }
        public long To { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Video { get; set; } = true;
        public bool Audio { get; set; } = true;
        /// <summary>미리 보기(BGRA·s16)면 true, 내보내기(yuv420p)면 false.</summary>
        public bool Preview { get; set; }
        /// <summary>글자 클립에 쓸 글꼴 파일(.ttf/.ttc).</summary>
        public string FontFile { get; set; }
        /// <summary>글자 클립의 글을 담을 임시 파일 폴더. 꾸민 글자 PNG 캐시는 그 아래 styled-text 폴더.</summary>
        public string TempFolder { get; set; }
        /// <summary>쓰는 FFmpeg 주 버전(0이면 모름) — 이보다 새 버전이 필요한 전환 효과는 디졸브로 그린다.</summary>
        public int FfmpegMajor { get; set; }
        /// <summary>
        /// 재생(이어 그리기) — 수식 전환이 든 묶음은 줄인 크기로 섞어 늘인다(수식은 픽셀마다 풀어 실시간보다 느리다, <see cref="RenderGraph.PlaybackShrink"/>).
        /// 멈춘 화면 한 장과 내보내기는 원래 크기.
        /// </summary>
        public bool Playback { get; set; }
        /// <summary>꾸민 글자(글자 상자를 정한 글자도)를 PNG로 그릴 그리개. null이면 스타일이 있어도 drawtext로 그린다(첫 채우기 색만 반영, 글자 상자는 무시).</summary>
        public ITextRasterizer TextRasterizer { get; set; }
    }

    /// <summary>FFmpeg에 줄 입력(파일마다 인수 묶음)과 filter_complex. 출력 라벨은 [vout]·[aout].</summary>
    public sealed class RenderPlan
    {
        public List<List<string>> Inputs { get; } = new List<List<string>>();
        public string Filter { get; set; }
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }
        public double Seconds { get; set; }
        public long Frames { get; set; }
        /// <summary>실행 전에 써 둘 파일(글자 클립의 글, 자막 ASS) — 경로·내용.</summary>
        public Dictionary<string, string> TextFiles { get; } = new Dictionary<string, string>();
        /// <summary>자막 트랙을 libass(ass 필터)로 입힌다 — FFmpeg에 libass가 있어야 한다.</summary>
        public bool UsesLibass { get; set; }
        /// <summary>
        /// 실행 전에 그려 둘 꾸민 글자 PNG — 경로·요청. Build는 그리지 않고 적기만 한다(캐시라서 렌더 뒤에도 지우지 않는다).
        /// </summary>
        public Dictionary<string, TextImageRequest> TextImages { get; } = new Dictionary<string, TextImageRequest>();
        /// <summary><see cref="TextImages"/>를 그릴 그리개.</summary>
        public ITextRasterizer TextRasterizer { get; set; }
    }

    /// <summary>
    /// 프로젝트 → FFmpeg 필터 그래프. 미리 보기와 내보내기가 같은 그래프를 쓰므로 보이는 것과 결과가 같다.
    /// 동영상: 검은 바탕 위에 동영상 트랙 1(주 트랙)부터 위 트랙 순서로 클립을 덮는다(같은 트랙은 시간 순 — 크로스페이드는 뒤 클립이 위).
    /// 오디오: 무음 바탕에 소리 나는 클립을 모두 섞는다(정규화 없이 더함).
    /// </summary>
    public static class RenderGraph
    {
        /// <summary>이보다 긴 글은 꾸민 경로 대신 drawtext로 그린다(글자 상자의 줄 나눔·높이도 무시).</summary>
        public const int MaxStyledTextLength = 2000;

        public static RenderPlan Build(Project project, RenderOptions options)
        {
            if (options.To <= options.From)
                throw new ArgumentException("그릴 구간이 비어 있습니다.");
            var rate = project.Rate;
            var plan = new RenderPlan
            {
                Frames = options.To - options.From,
                Seconds = rate.ToSeconds(options.To - options.From)
            };
            var filters = new List<string>();
            if (options.Video)
                BuildVideo(project, options, plan, filters);
            if (options.Audio)
                BuildAudio(project, options, plan, filters);
            plan.Filter = string.Join(";\n", filters);
            return plan;
        }

        // ---------- 동영상 ----------

        private static void BuildVideo(Project project, RenderOptions options, RenderPlan plan, List<string> filters)
        {
            var rate = project.Rate;
            var k = (double)options.Width / project.Width;
            var fps = rate.Rational;
            filters.Add(string.Format(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}:d={3}[base]",
                options.Width, options.Height, fps, N(plan.Seconds)));
            var layer = "base";
            var n = 0;
            foreach (var track in project.VideoTracks)
            {
                if (track.Hidden)
                    continue;
                if (track.Subtitles)
                {
                    // 자막 트랙: 이 구간의 큐를 ASS 파일 하나로 써 두고(실행 전에 쓰고 끝나면 지움) 이 트랙 자리에서 libass로 입힌다
                    var ass = SubtitleAss.Build(project, track, options.From, options.To);
                    if (ass == null)
                        continue;
                    // 파일 이름에 클립 Id·글을 쓰지 않는다
                    var assFile = Path.Combine(options.TempFolder ?? Path.GetTempPath(), "fv-subs-" + Guid.NewGuid().ToString("N") + ".ass");
                    plan.TextFiles[assFile] = ass;
                    plan.UsesLibass = true;
                    // 높은 프레임 레이트는 ass가 보는 시간을 늘였다가 되돌린다(ASS 시각 단위 1/100초로도 프레임이 구별되게)
                    // setpts를 거치면 프레임 레이트 정보가 없어져 출력이 다른 레이트로 맞춰지므로 되돌린 뒤 fps로 다시 알린다.
                    // fps는 끝 시각(overlay는 마지막 프레임 시각을 넘김)에서 마지막 프레임을 버리므로, 먼저 한 장 복제해 두고(tpad) 끝에서 구간 길이로 자른다(trim)
                    var scale = SubtitleAss.TimeScale(rate);
                    var stretch = scale > 1 ? "tpad=stop=1:stop_mode=clone,setpts=PTS*" + scale + "," : "";
                    var restore = scale > 1
                        ? string.Format(CultureInfo.InvariantCulture, ",setpts=PTS/{0},fps={1},trim=end_frame={2}", scale, rate.Rational, plan.Frames)
                        : "";
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}]{3}ass=filename={1}{4}[o{2}]", layer, FilterValue(assFile), n, stretch, restore));
                    layer = "o" + n;
                    n++;
                    continue;
                }
                var clips = track.Clips.Where(c => c.IsVisual).OrderBy(c => c.Start).ToList();
                // xfade로 잇는 클립 묶음(디졸브가 아닌 전환): 묶음 흐름의 라벨과 그 흐름이 시작하는 타임라인 프레임
                string group = null;
                long groupStart = 0;
                // 재생 중 수식 전환이 든 묶음: 줄인 캔버스로 섞고 얹기 전에 늘인다(멈춘 화면·내보내기는 원래 크기)
                var reduced = false;
                void CloseGroup()
                {
                    // 묶음은 캔버스 크기라 제자리(0, 0)에 얹는다. 알파를 곱한 색으로 섞었으니 얹기 전에 되돌린다
                    // 시작 시각은 프레임 단위로 반올림한다(초를 TB로 나눈 값이 0.99999처럼 되면 잘려 한 프레임 일찍 나온다)
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}]unpremultiply=inplace=1,{3}format=rgba,setpts=PTS-STARTPTS+round({1}/TB)[c{2}]",
                        group, N(rate.ToSeconds(groupStart - options.From)), n, reduced ? "scale=" + options.Width + ":" + options.Height + "," : ""));
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}][c{1}]overlay=x=0:y=0:eof_action=pass[o{1}]", layer, n));
                    layer = "o" + n;
                    n++;
                    group = null;
                    reduced = false;
                }
                // j번째 클립이 앞 클립과 xfade로 이어지는지(들어옴), 뒤 클립이 xfade로 이어지는지(나감) — 전환 구간이 그리는 구간에 걸칠 때만
                bool XfadeIn(int j)
                {
                    return !Transitions.Resolve(clips[j], options.FfmpegMajor).IsDissolve && Overlaps(clips[j].Start, Math.Min(clips[j].TransitionIn, clips[j].Duration), options);
                }
                bool XfadeOut(int j)
                {
                    var after = j + 1 < clips.Count ? clips[j + 1] : null;
                    var length = after != null && after.Start == clips[j].End ? Math.Min(after.TransitionIn, after.Duration) : 0;
                    return length > 0 && !Transitions.Resolve(after, options.FfmpegMajor).IsDissolve && Overlaps(after.Start, length, options);
                }
                // i부터 시작하는 묶음에 수식 전환이 있는지
                bool HasExpression(int i)
                {
                    for (var j = i; j < clips.Count; j++)
                    {
                        if (XfadeIn(j) && Transitions.Resolve(clips[j], options.FfmpegMajor).IsExpression)
                            return true;
                        if (!XfadeOut(j))
                            break;
                    }
                    return false;
                }
                for (var i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    var next = i + 1 < clips.Count ? clips[i + 1] : null;
                    // 뒤 클립이 전환으로 들어오면 그동안 이 클립이 밑에 계속 보여야 한다(타임라인 위치는 그대로)
                    var tail = next != null && next.Start == clip.End ? Math.Min(next.TransitionIn, next.Duration) : 0;
                    var transition = Math.Min(clip.TransitionIn, clip.Duration);
                    var effect = Transitions.Resolve(clip, options.FfmpegMajor);
                    // 디졸브가 아닌 전환은 앞 클립과 xfade로 잇는다 — 전환 구간이 그리는 구간에 걸칠 때만
                    var xfadeIn = XfadeIn(i);
                    var xfadeOut = XfadeOut(i);
                    // 묶음을 시작할 때 절반 크기로 섞을지 정한다 — 줄이기는 틀 계산에서 바로(크기 바꾸기를 두 번 이으면 FFmpeg가 전환 끝에서 멈춘다)
                    if (group == null && (xfadeIn || xfadeOut))
                        reduced = options.Playback && PlaybackShrink(options) > 1 && HasExpression(i);
                    var shrink = reduced && (xfadeIn || xfadeOut) ? PlaybackShrink(options) : 1;
                    var segStart = Math.Max(clip.Start, options.From);
                    var segEnd = Math.Min(clip.End + tail, options.To);
                    // 전환 도중부터 그리면 전환 시간이 맞도록 클립 처음부터 디코드한다(앞부분은 바탕 시작 전이라 버려짐)
                    if (transition > 0 && segStart > clip.Start && segStart < clip.Start + transition)
                        segStart = clip.Start;
                    if (xfadeOut)
                    {
                        // xfade의 앞 흐름은 전환이 시작할 때부터 끝날 때까지 있어야 한다
                        segStart = Math.Min(segStart, next.Start);
                        segEnd = clip.End + tail;
                    }
                    var source = segEnd > segStart ? VisualSource(project, options, plan, clip, segStart, segEnd) : null;
                    if (source == null)
                    {
                        if (group != null)
                            CloseGroup();
                        continue;
                    }
                    var chain = new StringBuilder(source);
                    // 틀 = 화면 × 크기 배율. 맞춤(비율 유지, 다 보이게)·채우기(비율 유지, 넘치는 곳 가운데 기준으로 자름)·늘이기(비율 무시)
                    var scale = Math.Max(0.01, clip.Scale);
                    var boxWidth = Math.Max(2, (int)Math.Round(options.Width * scale / shrink));
                    var boxHeight = Math.Max(2, (int)Math.Round(options.Height * scale / shrink));
                    var fit = clip.Kind == ClipKind.Media ? clip.Fit : ClipFit.Fit;
                    if (UsesRasterizer(clip, options))
                    {
                        // 꾸민 글자 PNG는 가운데 = 글자 상자 가운데이고 크기는 그림 따라 다르다(화면보다 클 수도) — 화면 크기로 맞추지 않고
                        // 크기 배율로 한 번만 줄여 가운데에 얹는다(그리개 화소 배율·재생 축소도 같은 scale에 접는다 — scale을 잇지 않게)
                        var factor = scale / (shrink * StyledCanvas(options).Shrink);
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",scale=w='max(1,iw*{0})':h='max(1,ih*{0})',format=rgba", N(factor));
                    }
                    else if (fit == ClipFit.Blur)
                    {
                        // 흐린 배경: 같은 그림을 둘로 나눠 하나는 틀을 채우게 키워 작게 줄였다 흐리고 늘인 뒤 조금 어둡게, 하나는 맞춤으로 그 가운데에
                        var smallWidth = Math.Max(4, boxWidth / 8);
                        var smallHeight = Math.Max(4, boxHeight / 8);
                        var radius = Math.Max(1, Math.Min(8, Math.Min(smallWidth, smallHeight) / 2 - 1));
                        chain.AppendFormat(CultureInfo.InvariantCulture,
                            ",split[ba{0}][bb{0}];[ba{0}]scale=w={1}:h={2}:force_original_aspect_ratio=increase,crop={1}:{2},scale={3}:{4},boxblur={5}:2,scale={1}:{2},"
                            + "colorchannelmixer=rr=0.8:gg=0.8:bb=0.8,format=rgba[bg{0}];[bb{0}]scale=w={1}:h={2}:force_original_aspect_ratio=decrease,format=rgba[bf{0}];"
                            + "[bg{0}][bf{0}]overlay=x=(main_w-overlay_w)/2:y=(main_h-overlay_h)/2:format=auto,format=rgba",
                            n, boxWidth, boxHeight, smallWidth, smallHeight, radius);
                    }
                    else
                    {
                        chain.AppendFormat(CultureInfo.InvariantCulture,
                            fit == ClipFit.Fill ? ",scale=w={0}:h={1}:force_original_aspect_ratio=increase,crop={0}:{1},format=rgba"
                            : fit == ClipFit.Stretch ? ",scale=w={0}:h={1},setsar=1,format=rgba"
                            : ",scale=w={0}:h={1}:force_original_aspect_ratio=decrease,format=rgba",
                            boxWidth, boxHeight);
                    }
                    if (Math.Abs(clip.Rotation) > 0.01)
                    {
                        var radians = N(clip.Rotation * Math.PI / 180);
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",rotate={0}:c=none:ow='rotw({0})':oh='roth({0})'", radians);
                    }
                    if (clip.Opacity < 0.999)
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",colorchannelmixer=aa={0}", N(Math.Max(0, clip.Opacity)));
                    // 디졸브: 앞 클립(밑에 더 그려짐) 위로 이 클립이 서서히 나타난다
                    if (transition > 0 && segStart == clip.Start && effect.IsDissolve)
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",fade=t=in:st=0:d={0}:alpha=1", N(rate.ToSeconds(transition)));
                    if (!xfadeIn && !xfadeOut)
                    {
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",setpts=PTS-STARTPTS+round({0}/TB)[c{1}]", N(rate.ToSeconds(segStart - options.From)), n);
                        filters.Add(chain.ToString());
                        filters.Add(string.Format(CultureInfo.InvariantCulture,
                            "[{0}][c{1}]overlay=x='(main_w-overlay_w)/2+{2}':y='(main_h-overlay_h)/2+{3}':eof_action=pass[o{1}]",
                            layer, n, N(clip.X * k), N(clip.Y * k)));
                        layer = "o" + n;
                        n++;
                        continue;
                    }
                    // 전환으로 이어지는 클립: 캔버스 크기 투명 바탕에 제자리로 얹어 두고(xfade는 같은 크기·형식의 흐름끼리 잇는다) 묶음에 붙인다
                    // xfade는 알파를 곱한 색으로 섞는다 — 그대로 섞으면 투명한 쪽과 섞이는 자리가 어둡게 된다
                    var canvasWidth = Math.Max(2, options.Width / shrink / 2 * 2);
                    var canvasHeight = Math.Max(2, options.Height / shrink / 2 * 2);
                    chain.AppendFormat(CultureInfo.InvariantCulture, ",setpts=PTS-STARTPTS[s{0}]", n);
                    filters.Add(chain.ToString());
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "color=c=black@0.0:s={0}x{1}:r={2}:d={3},format=rgba[g{4}]",
                        canvasWidth, canvasHeight, fps, N(rate.ToSeconds(segEnd - segStart)), n));
                    filters.Add(string.Format(CultureInfo.InvariantCulture,
                        "[g{0}][s{0}]overlay=x='(main_w-overlay_w)/2+{1}':y='(main_h-overlay_h)/2+{2}':eof_action=pass,format=gbrap,premultiply=inplace=1[k{0}]",
                        n, N(clip.X * k / shrink), N(clip.Y * k / shrink)));
                    var piece = "k" + n;
                    if (xfadeIn)
                    {
                        if (group == null)
                        {
                            // 앞에 붙은 클립이 없으면 투명 화면에서 바뀐다(아래 트랙·바탕이 비친다)
                            filters.Add(string.Format(CultureInfo.InvariantCulture, "color=c=black@0.0:s={0}x{1}:r={2}:d={3},format=gbrap[t{4}]",
                                canvasWidth, canvasHeight, fps, N(rate.ToSeconds(transition)), n));
                            group = "t" + n;
                            groupStart = clip.Start;
                        }
                        // offset: 묶음 흐름에서 이 클립(=전환)이 시작하는 초
                        filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}][{1}]xfade={2}:duration={3}:offset={4}[x{5}]",
                            group, piece, Transitions.XfadeOption(effect), N(rate.ToSeconds(transition)), N(rate.ToSeconds(clip.Start - groupStart)), n));
                        group = "x" + n;
                    }
                    else
                    {
                        group = piece;
                        groupStart = segStart;
                    }
                    n++;
                    if (!xfadeOut)
                        CloseGroup();
                }
                if (group != null)
                    CloseGroup();
            }
            filters.Add("[" + layer + "]format=" + (options.Preview ? "bgra" : "yuv420p") + "[vout]");
            plan.HasVideo = true;
        }

        /// <summary>
        /// 재생 중 수식 전환 묶음을 몇 분의 1 크기로 섞을지: 수식을 푸는 픽셀이 약 14만 개(270×480 남짓)가 되게 — 1/4 화질은 그대로, 1/2은 2, 전체는 4.
        /// </summary>
        internal static int PlaybackShrink(RenderOptions options)
        {
            return Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)options.Width * options.Height / 140000)));
        }

        /// <summary>[start, start + length) 프레임이 그리는 구간에 걸치는지.</summary>
        private static bool Overlaps(long start, long length, RenderOptions options)
        {
            return length > 0 && options.From < start + length && options.To > start;
        }

        /// <summary>클립의 화면 원본(입력 + 시간 맞춤)을 필터 체인 앞부분으로. segEnd가 원본 끝을 넘으면 마지막 화면을 멈춰 채운다.</summary>
        private static string VisualSource(Project project, RenderOptions options, RenderPlan plan, Clip clip, long segStart, long segEnd)
        {
            var rate = project.Rate;
            var fps = rate.Rational;
            var segFrames = segEnd - segStart;
            if (clip.Kind == ClipKind.Text)
            {
                // 꾸민 글자, 또는 글자 상자를 정한 글자(줄 나눔·상자는 그리개만 할 수 있다 — 스타일이 없으면 처음 꾸밀 때와 같은 스타일로).
                // 긴 글(drawtext 대체 경로)은 상자를 무시한다
                if (UsesRasterizer(clip, options))
                    return StyledText(project, options, plan, clip, segFrames);
                // 파일 이름에 클립 Id(프로젝트 파일 값)를 쓰지 않는다 — 조작된 Id로 다른 폴더의 파일을 덮어쓰지 못하게
                var textFile = Path.Combine(options.TempFolder ?? Path.GetTempPath(), "fv-text-" + Guid.NewGuid().ToString("N") + ".txt");
                plan.TextFiles[textFile] = clip.Text ?? "";
                var size = Math.Max(4, clip.FontSize * options.Width / project.Width);
                var font = string.IsNullOrEmpty(options.FontFile) ? "" : "fontfile=" + FilterValue(options.FontFile) + ":";
                return string.Format(CultureInfo.InvariantCulture,
                    "color=c=black@0.0:s={0}x{1}:r={2}:d={3},format=rgba,drawtext={4}textfile={5}:expansion=none:fontsize={6}:fontcolor={7}:x=(w-text_w)/2:y=(h-text_h)/2:line_spacing={8}{9}{10}",
                    options.Width, options.Height, fps, N(rate.ToSeconds(segFrames)), font, FilterValue(textFile),
                    N(size), FilterValue(clip.Style?.Fill?.Kind == FillKind.None ? "black@0.0" : TextColor(clip.Style?.Fill?.Colors?.FirstOrDefault() ?? clip.TextColor)), N(size * 0.2),
                    DrawtextBorder(clip.Style?.Outline, (double)options.Width / project.Width),
                    DrawtextShadow(clip.Style?.Shadow, (double)options.Width / project.Width));
            }
            if (clip.MediaKind == MediaKind.Image)
            {
                var index = AddInput(plan, "-loop", "1", "-framerate", fps, "-t", N(rate.ToSeconds(segFrames)), "-i", clip.Path);
                return "[" + index + ":v]setpts=PTS-STARTPTS";
            }
            // 동영상: 원본 위치 = SourceIn + (그리는 시작 - 클립 시작) × 속도
            var segSeconds = N(rate.ToSeconds(segFrames));
            // 끝에서 마지막 화면을 늘여 채우고(tpad) 정확히 구간 길이로 자른다(trim) — 원본 길이 반올림으로 프레임이 모자라도 구간이 빈틈 없이 찬다
            var hold = ",tpad=stop_mode=clone:stop_duration=" + segSeconds + ",trim=duration=" + segSeconds;
            var sourceStart = clip.SourceIn + (long)Math.Round((segStart - clip.Start) * clip.Speed);
            if (sourceStart >= clip.SourceLength - 1)
            {
                // 원본이 이미 끝난 뒤(크로스페이드 꼬리)부터 그림: 파일 끝 근처를 읽어 마지막 한 장만 남겨 멈춰 둔다
                var last = AddInput(plan, "-sseof", "-0.3", "-i", clip.Path);
                return "[" + last + ":v]reverse,trim=end_frame=1,setpts=PTS-STARTPTS,fps=" + fps + hold;
            }
            var sourceFrames = Math.Max(1, Math.Min((long)Math.Round(segFrames * clip.Speed), clip.SourceLength - sourceStart));
            var input = AddInput(plan, "-ss", N(rate.ToSeconds(sourceStart)), "-t", N(rate.ToSeconds(sourceFrames)), "-i", clip.Path);
            var chain = "[" + input + ":v]setpts=PTS-STARTPTS";
            if (Math.Abs(clip.Speed - 1) > 1e-9)
                chain += ",setpts=PTS/" + N(clip.Speed);
            return chain + ",fps=" + fps + hold;
        }

        /// <summary>
        /// 꾸민 글자: 그리개가 그릴 PNG(캔버스 이하, 가운데 = 글자 배치 상자 가운데)를 이미지처럼 읽어 캔버스 크기 투명 바탕 가운데에 놓는다.
        /// 그러면 뒤의 공통 체인(맞춤 × 크기 → 회전 → 불투명도 → 가운데 + X/Y)이 drawtext 글자와 똑같이 동작한다 — 상자 가운데 = 화면 가운데 + X/Y, 상자 크기 × 크기 배율.
        /// 글자 상자(너비·높이)는 프로젝트 픽셀이라 글자 크기처럼 출력 배율을 곱해 넘긴다.
        /// </summary>
        private static string StyledText(Project project, RenderOptions options, RenderPlan plan, Clip clip, long segFrames)
        {
            var rate = project.Rate;
            var k = (double)options.Width / project.Width;
            var (canvasWidth, canvasHeight, shrink) = StyledCanvas(options);
            var style = (clip.Style ?? TextStyle.FromLegacy(clip)).Clone();
            style.Normalize();
            var boxWidth = ClipLayout.TextBoxLength(clip.TextBoxWidth);
            var boxHeight = ClipLayout.TextBoxLength(clip.TextBoxHeight);
            var request = new TextImageRequest
            {
                Text = clip.Text ?? "",
                Style = style,
                FontPixelSize = clip.FontSize * k * shrink,
                EffectScale = k * shrink,
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight,
                WrapWidth = boxWidth * k * shrink,
                BoxHeight = boxHeight * k * shrink,
                // 높이가 자동이면 세로 자리는 그림에 영향이 없다 — 같은 그림이 다른 캐시 파일이 되지 않게 기본값으로
                VerticalAlign = boxHeight > 0 && Enum.IsDefined(clip.TextVerticalAlign) ? clip.TextVerticalAlign : TextVerticalAlign.Middle
            };
            // 파일 이름에는 해시만 쓴다 — 글·클립 Id(프로젝트 파일 값)가 경로에 섞이지 않게
            var path = Path.Combine(options.TempFolder ?? Path.GetTempPath(), "styled-text", "st-" + request.Key(options.TextRasterizer.Version) + ".png");
            plan.TextImages[path] = request;
            plan.TextRasterizer = options.TextRasterizer;
            // image2 + pattern_type none: 임시 폴더 경로에 %d 같은 글이 있어도 파일 이름 패턴으로 읽지 않게(png_pipe는 이 옵션이 없다)
            var index = AddInput(plan, "-loop", "1", "-framerate", rate.Rational, "-t", N(rate.ToSeconds(segFrames)), "-f", "image2", "-pattern_type", "none", "-i", path);
            // 크기는 그대로 둔다 — 공통 체인이 크기 배율(× 1/화소 배율)로 한 번 줄여 가운데 + X/Y에 얹는다
            return string.Format(CultureInfo.InvariantCulture, "[{0}:v]setpts=PTS-STARTPTS,format=rgba", index);
        }

        /// <summary>글자를 그리개로 그리는지: 꾸민 글자이거나 글자 상자를 정했고, 그리개가 있고, 긴 글(drawtext 대체 경로)이 아닐 때.</summary>
        private static bool UsesRasterizer(Clip clip, RenderOptions options)
        {
            return clip.Kind == ClipKind.Text && (clip.Style != null || ClipLayout.HasTextBox(clip)) && options.TextRasterizer != null
                && (clip.Text ?? "").Length <= MaxStyledTextLength;
        }

        /// <summary>
        /// 꾸민 글자 그림의 최대 크기(그리개 픽셀)와 화소 배율(그리개 픽셀 / 출력 픽셀).
        /// 화면보다 큰 글(큰 상자를 줄여 쓰거나 옮긴 글)도 크기를 바꾸기 전에 잘리지 않게 화면의 몇 배까지 둔다 — 화소 상한 안에서 2의 거듭제곱 배(최대 4배,
        /// 1080p는 2배, 4K는 1배). 클립의 위치·크기·회전과 상관없는 값이라 옮기거나 줄여도 다시 그리지 않는다. 그림은 글만큼만 크다(빈 범위는 쓰지 않음).
        /// 화면이 화소 상한보다 크면(8K 등) 줄여서 그리게 하고(화소 배율 &lt; 1) 공통 체인이 크기 배율과 함께 다시 키운다.
        /// </summary>
        private static (int Width, int Height, double Shrink) StyledCanvas(RenderOptions options)
        {
            double width = options.Width, height = options.Height;
            var shrink = Math.Min(1, Math.Sqrt(StyledTextLayout.MaxPixels / (width * height)));
            var times = 1;
            while (times < 4 && width * height * shrink * shrink * (times * 2) * (times * 2) <= StyledTextLayout.MaxPixels)
                times *= 2;
            return (Math.Max(2, (int)Math.Floor(width * times * shrink)), Math.Max(2, (int)Math.Floor(height * times * shrink)), shrink);
        }

        // ---------- 오디오 ----------

        private static void BuildAudio(Project project, RenderOptions options, RenderPlan plan, List<string> filters)
        {
            var rate = project.Rate;
            var sampleRate = project.SampleRate;
            filters.Add(string.Format(CultureInfo.InvariantCulture, "anullsrc=r={0}:cl=stereo,atrim=0:{1}[abase]", sampleRate, N(plan.Seconds)));
            var labels = new List<string> { "[abase]" };
            var n = 0;
            foreach (var track in project.AllTracks)
            {
                if (track.Muted)
                    continue;
                // 화면 클립 순서(전환은 같은 트랙의 바로 앞 화면 클립에서 바뀐다)
                var visual = track.Kind == TrackKind.Video && !track.Subtitles ? track.Clips.Where(c => c.IsVisual).OrderBy(c => c.Start).ToList() : null;
                foreach (var clip in track.Clips.OrderBy(c => c.Start))
                {
                    var audible = track.Kind == TrackKind.Audio
                        ? clip.Kind == ClipKind.Media && clip.HasAudio && clip.Volume > 0
                        : clip.IsAudible && !clip.AudioOnly;
                    if (!audible)
                        continue;
                    // 전환: 소리도 화면처럼 겹쳐 바뀐다 — 전환이 든 클립은 그동안 커지고, 뒤 클립이 전환으로 들어오면 이 클립 소리가 그 길이만큼 이어지며 준다
                    long crossIn = 0, crossOut = 0;
                    if (visual != null)
                    {
                        crossIn = Math.Min(clip.TransitionIn, clip.Duration);
                        var index = visual.IndexOf(clip);
                        var next = index >= 0 && index + 1 < visual.Count ? visual[index + 1] : null;
                        if (next != null && next.Start == clip.End)
                            crossOut = Math.Min(next.TransitionIn, next.Duration);
                    }
                    var segStart = Math.Max(clip.Start, options.From);
                    var segEnd = Math.Min(clip.End + crossOut, options.To);
                    if (segEnd <= segStart)
                        continue;
                    var sourceStart = clip.SourceIn + (long)Math.Round((segStart - clip.Start) * clip.Speed);
                    var sourceFrames = Math.Max(1, (long)Math.Round((segEnd - segStart) * clip.Speed));
                    var input = AddInput(plan, "-ss", N(rate.ToSeconds(sourceStart)), "-t", N(rate.ToSeconds(sourceFrames)), "-i", clip.Path);
                    var chain = new StringBuilder();
                    chain.AppendFormat(CultureInfo.InvariantCulture, "[{0}:a]asetpts=PTS-STARTPTS", input);
                    foreach (var tempo in Tempo(clip.Speed))
                        chain.Append(",atempo=").Append(N(tempo));
                    chain.Append(",volume=").Append(VolumeExpression(rate, clip, segStart, crossIn, crossOut));
                    // 모노는 양쪽에 같은 크기로(자동 변환은 가운데 채널로 보고 -3dB로 나눈다)
                    if (clip.AudioChannels == 1)
                        chain.Append(",pan=stereo|c0=c0|c1=c0");
                    chain.AppendFormat(CultureInfo.InvariantCulture, ",aresample={0},aformat=sample_rates={0}:channel_layouts=stereo", sampleRate);
                    var delay = (long)Math.Round(rate.ToSeconds(segStart - options.From) * 1000);
                    if (delay > 0)
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",adelay={0}:all=1", delay);
                    chain.AppendFormat(CultureInfo.InvariantCulture, "[a{0}]", n);
                    filters.Add(chain.ToString());
                    labels.Add("[a" + n + "]");
                    n++;
                }
            }
            if (n == 0)
                filters.Add("[abase]anull[aout]");
            else
                filters.Add(string.Join("", labels) + string.Format(CultureInfo.InvariantCulture,
                    "amix=inputs={0}:duration=first:normalize=0:dropout_transition=0,atrim=0:{1}[aout]", labels.Count, N(plan.Seconds)));
            plan.HasAudio = true;
        }

        /// <summary>
        /// 음량 + 페이드 인/아웃을 시간식으로(eval=frame). t는 그리는 구간 시작부터의 초라서 클립 안 시간 = t + 앞에서 건너뛴 초.
        /// 구간이 페이드 도중에서 시작해도 맞는다.
        /// 전환 교차 페이드(프레임): crossIn은 클립 처음부터 커지고(sin), crossOut은 클립 끝 뒤로 이어지며 준다(cos) — 둘을 겹치면 크기감이 일정하다.
        /// </summary>
        internal static string VolumeExpression(FrameRate rate, Clip clip, long segStart, long crossIn = 0, long crossOut = 0)
        {
            var volume = N(Math.Max(0, Math.Min(2, clip.Volume)));
            var fadeIn = Math.Min(clip.FadeIn, clip.Duration);
            var fadeOut = Math.Min(clip.FadeOut, clip.Duration);
            if (fadeIn <= 0 && fadeOut <= 0 && crossIn <= 0 && crossOut <= 0)
                return volume;
            var offset = N(rate.ToSeconds(segStart - clip.Start));
            var expression = new StringBuilder("'").Append(volume);
            if (fadeIn > 0)
                expression.AppendFormat(CultureInfo.InvariantCulture, "*min(1,max(0,(t+{0})/{1}))", offset, N(rate.ToSeconds(fadeIn)));
            if (fadeOut > 0)
                expression.AppendFormat(CultureInfo.InvariantCulture, "*min(1,max(0,({0}-t-{1})/{2}))", N(rate.ToSeconds(clip.Duration)), offset, N(rate.ToSeconds(fadeOut)));
            if (crossIn > 0)
                expression.AppendFormat(CultureInfo.InvariantCulture, "*sin(PI/2*min(1,max(0,(t+{0})/{1})))", offset, N(rate.ToSeconds(crossIn)));
            if (crossOut > 0)
                expression.AppendFormat(CultureInfo.InvariantCulture, "*cos(PI/2*min(1,max(0,(t+{0}-{1})/{2})))", offset, N(rate.ToSeconds(clip.Duration)), N(rate.ToSeconds(crossOut)));
            expression.Append("':eval=frame");
            return expression.ToString();
        }

        /// <summary>atempo 한 번은 0.5~100배 — 0.5보다 느리면 여러 번 건다.</summary>
        internal static List<double> Tempo(double speed)
        {
            var result = new List<double>();
            if (Math.Abs(speed - 1) < 1e-9)
                return result;
            while (speed < 0.5)
            {
                result.Add(0.5);
                speed /= 0.5;
            }
            result.Add(speed);
            return result;
        }

        // ---------- 글·숫자 ----------

        private static int AddInput(RenderPlan plan, params string[] args)
        {
            plan.Inputs.Add(args.ToList());
            return plan.Inputs.Count - 1;
        }

        internal static string N(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 그리개 없이 drawtext로 그릴 때의 윤곽선(":borderw=…:bordercolor=…"): 두께 = 윤곽선 두께 × 출력 배율(정수 픽셀, 최소 1).
        /// drawtext는 이음 모양을 고를 수 없어 이음은 무시하고, 테두리 안쪽을 글자로 덮으므로 채우기가 비치면(없음·반투명) 속에 테두리 색이 보인다.
        /// 꺼졌거나 두께가 0이면 빈 문자열.
        /// </summary>
        internal static string DrawtextBorder(TextOutline outline, double scale)
        {
            if (outline == null || !outline.Enabled || !double.IsFinite(outline.Width) || !double.IsFinite(scale) || outline.Width <= 0 || scale <= 0)
                return "";
            var width = (int)Math.Max(1, Math.Round(Math.Min(outline.Width, 50) * scale));
            return string.Format(CultureInfo.InvariantCulture, ":borderw={0}:bordercolor={1}", width, FilterValue(TextColor(TextStyle.NormalizeColor(outline.Color, "#000000"))));
        }

        /// <summary>
        /// 그리개 없이 drawtext로 그릴 때의 그림자(":shadowx=…:shadowy=…:shadowcolor=…"): 거리 × 출력 배율을 각도대로 옮긴 정수 픽셀,
        /// 색은 그림자 색의 알파 × 불투명도(0xRRGGBB@알파). drawtext는 흐릴 수 없어 흐림은 무시하고, 채우기가 없어도 글자 모양 그림자가 생긴다.
        /// 꺼졌거나 보이지 않으면(불투명도 0) 빈 문자열.
        /// </summary>
        internal static string DrawtextShadow(TextShadow shadow, double scale)
        {
            if (shadow == null || !shadow.Enabled || !double.IsFinite(shadow.Opacity) || !double.IsFinite(scale) || scale <= 0)
                return "";
            var color = TextStyle.NormalizeColor(shadow.Color, "#000000");
            var alpha = Math.Min(1, shadow.Opacity) * (color.Length == 9 ? Convert.ToInt32(color.Substring(1, 2), 16) / 255.0 : 1);
            if (!(alpha > 0))
                return "";
            var offset = TextEffects.ShadowOffset(Math.Min(shadow.Distance, 200) * scale, shadow.Angle);
            return string.Format(CultureInfo.InvariantCulture, ":shadowx={0}:shadowy={1}:shadowcolor={2}",
                (int)Math.Round(offset.X), (int)Math.Round(offset.Y), FilterValue("0x" + color.Substring(color.Length - 6) + "@" + N(alpha)));
        }

        /// <summary>"#RRGGBB" → FFmpeg 색(0xRRGGBB), "#AARRGGBB" → 0xRRGGBBAA. 잘못된 값은 흰색.</summary>
        internal static string TextColor(string color)
        {
            var normalized = TextStyle.NormalizeColor(color, null);
            if (normalized == null)
                return "white";
            if (normalized.Length == 9)
                return "0x" + normalized.Substring(3) + normalized.Substring(1, 2);
            return "0x" + normalized.Substring(1);
        }

        /// <summary>
        /// 필터 옵션 값(경로 등)을 그래프에 넣을 수 있게 두 겹으로 감싼다(FFmpeg "Notes on filtergraph escaping"):
        /// 옵션 단계에서 \ ' :를 \로, 그래프 단계에서 작은따옴표로. 경로의 \는 /로 바꾼다.
        /// </summary>
        public static string FilterValue(string value)
        {
            var option = new StringBuilder();
            foreach (var ch in value.Replace('\\', '/'))
            {
                if (ch == '\\' || ch == '\'' || ch == ':')
                    option.Append('\\');
                option.Append(ch);
            }
            return "'" + option.ToString().Replace("'", "'\\''") + "'";
        }
    }
}
