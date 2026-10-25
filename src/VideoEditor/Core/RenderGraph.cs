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
        /// <summary>글자 클립의 글을 담을 임시 파일 폴더.</summary>
        public string TempFolder { get; set; }
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
    }

    /// <summary>
    /// 프로젝트 → FFmpeg 필터 그래프. 미리 보기와 내보내기가 같은 그래프를 쓰므로 보이는 것과 결과가 같다.
    /// 동영상: 검은 바탕 위에 동영상 트랙 1(주 트랙)부터 위 트랙 순서로 클립을 덮는다(같은 트랙은 시간 순 — 크로스페이드는 뒤 클립이 위).
    /// 오디오: 무음 바탕에 소리 나는 클립을 모두 섞는다(정규화 없이 더함).
    /// </summary>
    public static class RenderGraph
    {
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
                void CloseGroup()
                {
                    // 묶음은 캔버스 크기라 제자리(0, 0)에 얹는다. 알파를 곱한 색으로 섞었으니 얹기 전에 되돌린다
                    // 시작 시각은 프레임 단위로 반올림한다(초를 TB로 나눈 값이 0.99999처럼 되면 잘려 한 프레임 일찍 나온다)
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}]unpremultiply=inplace=1,format=rgba,setpts=PTS-STARTPTS+round({1}/TB)[c{2}]",
                        group, N(rate.ToSeconds(groupStart - options.From)), n));
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}][c{1}]overlay=x=0:y=0:eof_action=pass[o{1}]", layer, n));
                    layer = "o" + n;
                    n++;
                    group = null;
                }
                for (var i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    var next = i + 1 < clips.Count ? clips[i + 1] : null;
                    // 뒤 클립이 전환으로 들어오면 그동안 이 클립이 밑에 계속 보여야 한다(타임라인 위치는 그대로)
                    var tail = next != null && next.Start == clip.End ? Math.Min(next.TransitionIn, next.Duration) : 0;
                    var transition = Math.Min(clip.TransitionIn, clip.Duration);
                    // 디졸브가 아닌 전환은 앞 클립과 xfade로 잇는다 — 전환 구간이 그리는 구간에 걸칠 때만
                    var xfadeIn = Transitions.UsesXfade(clip.Transition) && Overlaps(clip.Start, transition, options);
                    var xfadeOut = tail > 0 && Transitions.UsesXfade(next.Transition) && Overlaps(next.Start, tail, options);
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
                    // 화면에 맞춤(비율 유지) × 크기 배율
                    var scale = Math.Max(0.01, clip.Scale);
                    chain.AppendFormat(CultureInfo.InvariantCulture,
                        ",scale=w={0}:h={1}:force_original_aspect_ratio=decrease,format=rgba",
                        Math.Max(2, (int)Math.Round(options.Width * scale)), Math.Max(2, (int)Math.Round(options.Height * scale)));
                    if (Math.Abs(clip.Rotation) > 0.01)
                    {
                        var radians = N(clip.Rotation * Math.PI / 180);
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",rotate={0}:c=none:ow='rotw({0})':oh='roth({0})'", radians);
                    }
                    if (clip.Opacity < 0.999)
                        chain.AppendFormat(CultureInfo.InvariantCulture, ",colorchannelmixer=aa={0}", N(Math.Max(0, clip.Opacity)));
                    // 디졸브: 앞 클립(밑에 더 그려짐) 위로 이 클립이 서서히 나타난다
                    if (transition > 0 && segStart == clip.Start && !Transitions.UsesXfade(clip.Transition))
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
                    chain.AppendFormat(CultureInfo.InvariantCulture, ",setpts=PTS-STARTPTS[s{0}]", n);
                    filters.Add(chain.ToString());
                    filters.Add(string.Format(CultureInfo.InvariantCulture, "color=c=black@0.0:s={0}x{1}:r={2}:d={3},format=rgba[g{4}]",
                        options.Width, options.Height, fps, N(rate.ToSeconds(segEnd - segStart)), n));
                    filters.Add(string.Format(CultureInfo.InvariantCulture,
                        "[g{0}][s{0}]overlay=x='(main_w-overlay_w)/2+{1}':y='(main_h-overlay_h)/2+{2}':eof_action=pass,format=gbrap,premultiply=inplace=1[k{0}]",
                        n, N(clip.X * k), N(clip.Y * k)));
                    var piece = "k" + n;
                    if (xfadeIn)
                    {
                        if (group == null)
                        {
                            // 앞에 붙은 클립이 없으면 투명 화면에서 바뀐다(아래 트랙·바탕이 비친다)
                            filters.Add(string.Format(CultureInfo.InvariantCulture, "color=c=black@0.0:s={0}x{1}:r={2}:d={3},format=gbrap[t{4}]",
                                options.Width, options.Height, fps, N(rate.ToSeconds(transition)), n));
                            group = "t" + n;
                            groupStart = clip.Start;
                        }
                        // offset: 묶음 흐름에서 이 클립(=전환)이 시작하는 초
                        filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}][{1}]xfade=transition={2}:duration={3}:offset={4}[x{5}]",
                            group, piece, Transitions.FilterName(clip.Transition), N(rate.ToSeconds(transition)), N(rate.ToSeconds(clip.Start - groupStart)), n));
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
                // 파일 이름에 클립 Id(프로젝트 파일 값)를 쓰지 않는다 — 조작된 Id로 다른 폴더의 파일을 덮어쓰지 못하게
                var textFile = Path.Combine(options.TempFolder ?? Path.GetTempPath(), "fv-text-" + Guid.NewGuid().ToString("N") + ".txt");
                plan.TextFiles[textFile] = clip.Text ?? "";
                var size = Math.Max(4, clip.FontSize * options.Width / project.Width);
                var font = string.IsNullOrEmpty(options.FontFile) ? "" : "fontfile=" + FilterValue(options.FontFile) + ":";
                return string.Format(CultureInfo.InvariantCulture,
                    "color=c=black@0.0:s={0}x{1}:r={2}:d={3},format=rgba,drawtext={4}textfile={5}:expansion=none:fontsize={6}:fontcolor={7}:x=(w-text_w)/2:y=(h-text_h)/2:line_spacing={8}",
                    options.Width, options.Height, fps, N(rate.ToSeconds(segFrames)), font, FilterValue(textFile),
                    N(size), FilterValue(TextColor(clip.TextColor)), N(size * 0.2));
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
                foreach (var clip in track.Clips.OrderBy(c => c.Start))
                {
                    var audible = track.Kind == TrackKind.Audio
                        ? clip.Kind == ClipKind.Media && clip.HasAudio && clip.Volume > 0
                        : clip.IsAudible && !clip.AudioOnly;
                    if (!audible)
                        continue;
                    var segStart = Math.Max(clip.Start, options.From);
                    var segEnd = Math.Min(clip.End, options.To);
                    if (segEnd <= segStart)
                        continue;
                    var sourceStart = clip.SourceIn + (long)Math.Round((segStart - clip.Start) * clip.Speed);
                    var sourceFrames = Math.Max(1, (long)Math.Round((segEnd - segStart) * clip.Speed));
                    var input = AddInput(plan, "-ss", N(rate.ToSeconds(sourceStart)), "-t", N(rate.ToSeconds(sourceFrames)), "-i", clip.Path);
                    var chain = new StringBuilder();
                    chain.AppendFormat(CultureInfo.InvariantCulture, "[{0}:a]asetpts=PTS-STARTPTS", input);
                    foreach (var tempo in Tempo(clip.Speed))
                        chain.Append(",atempo=").Append(N(tempo));
                    chain.Append(",volume=").Append(VolumeExpression(rate, clip, segStart));
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
        /// </summary>
        internal static string VolumeExpression(FrameRate rate, Clip clip, long segStart)
        {
            var volume = N(Math.Max(0, Math.Min(2, clip.Volume)));
            var fadeIn = Math.Min(clip.FadeIn, clip.Duration);
            var fadeOut = Math.Min(clip.FadeOut, clip.Duration);
            if (fadeIn <= 0 && fadeOut <= 0)
                return volume;
            var offset = N(rate.ToSeconds(segStart - clip.Start));
            var expression = new StringBuilder("'").Append(volume);
            if (fadeIn > 0)
                expression.AppendFormat(CultureInfo.InvariantCulture, "*min(1,max(0,(t+{0})/{1}))", offset, N(rate.ToSeconds(fadeIn)));
            if (fadeOut > 0)
                expression.AppendFormat(CultureInfo.InvariantCulture, "*min(1,max(0,({0}-t-{1})/{2}))", N(rate.ToSeconds(clip.Duration)), offset, N(rate.ToSeconds(fadeOut)));
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

        /// <summary>"#RRGGBB" → FFmpeg 색(0xRRGGBB). 잘못된 값은 흰색.</summary>
        internal static string TextColor(string color)
        {
            if (!string.IsNullOrEmpty(color) && color.Length == 7 && color[0] == '#'
                && int.TryParse(color.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                return "0x" + color.Substring(1).ToUpperInvariant();
            return "white";
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
