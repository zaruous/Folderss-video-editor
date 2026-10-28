using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FFmpeg.AutoGen;
using VideoEditor.Media;

namespace VideoEditor.Helper
{
    /// <summary>
    /// 계획 하나를 그린다: 필터 그래프를 풀고, 열린 입력(버퍼 소스 + ffmpeg가 -ss·-t에 끼우는 trim + 자동 회전)을 [n:v]·[n:a]에 잇고,
    /// [vout]/[aout]에 버퍼 싱크를 달아 프레임을 꺼낸다. 화면은 ffmpeg의 rawvideo 출력처럼 일정 프레임 레이트로(빠진 장은 앞 장을 되풀이, 겹친 장은 버림),
    /// 소리는 16비트 스테레오 PCM으로 write에 넘긴다.
    /// </summary>
    internal sealed unsafe class PlanRun : IDisposable
    {
        private sealed class Input
        {
            public InputSpec Spec;
            public InputSource Source;
            public AVFilterContext* Buffer;
            public bool Eof;
        }

        private readonly InputPool _pool;
        private readonly HelperPlan _plan;
        private readonly List<Input> _inputs = new List<Input>();
        private AVFilterGraph* _graph;
        private AVFilterContext* _sink;

        public PlanRun(InputPool pool, HelperPlan plan)
        {
            _pool = pool;
            _plan = plan;
        }

        /// <summary>출력 화면 크기(그래프를 맞춘 뒤).</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        public void Execute(Action<byte[]> write, CancellationToken cancellation)
        {
            Configure();
            Run(write, cancellation);
        }

        /// <summary>그래프를 짓고 맞춘다(입력 열기·첫 프레임 풀기 포함). 흐름은 이것이 된 뒤에 파이프에 붙는다 — 여기서 실패하면 플러그인이 ffmpeg.exe로 돌아간다.</summary>
        public void Configure()
        {
            if (string.IsNullOrWhiteSpace(_plan.Filter))
                throw new InvalidOperationException("필터 그래프가 비어 있습니다.");
            _graph = ffmpeg.avfilter_graph_alloc();
            if (_graph == null)
                throw new OutOfMemoryException("필터 그래프");
            AVFilterInOut* open = null;
            AVFilterInOut* outputs = null;
            try
            {
                Native.Check(ffmpeg.avfilter_graph_parse2(_graph, _plan.Filter, &open, &outputs), "필터 그래프 읽기");
                for (var cursor = open; cursor != null; cursor = cursor->next)
                    Connect(cursor);
                if (outputs == null || outputs->next != null)
                    throw new InvalidOperationException("그래프 출력이 하나가 아닙니다.");
                var label = Native.Text(outputs->name) ?? "";
                var expected = _plan.Video ? "vout" : "aout";
                if (label != expected)
                    throw new InvalidOperationException("그래프 출력이 [" + expected + "]가 아닙니다: [" + label + "]");
                ConnectSink(outputs);
            }
            finally
            {
                ffmpeg.avfilter_inout_free(&open);
                ffmpeg.avfilter_inout_free(&outputs);
            }
            Native.Check(ffmpeg.avfilter_graph_config(_graph, null), "필터 그래프 맞추기");
            if (_plan.Video)
            {
                Width = ffmpeg.av_buffersink_get_w(_sink);
                Height = ffmpeg.av_buffersink_get_h(_sink);
                if (Width <= 0 || Height <= 0)
                    throw new InvalidOperationException("출력 크기를 모릅니다.");
            }
        }

        /// <summary>맞춘 그래프를 돌려 프레임을 write에 넘긴다.</summary>
        public void Run(Action<byte[]> write, CancellationToken cancellation)
        {
            if (_sink == null)
                throw new InvalidOperationException("그래프를 맞추지 않았습니다.");
            Pump(write, cancellation);
        }

        /// <summary>[n:v]·[n:a] 입력 하나에 열린 파일을 잇는다.</summary>
        private void Connect(AVFilterInOut* inout)
        {
            var label = Native.Text(inout->name) ?? "";
            var parts = label.Split(':');
            int index;
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || (parts[1] != "v" && parts[1] != "a"))
                throw new InvalidOperationException("모르는 입력 라벨입니다: [" + label + "]");
            if (index < 0 || index >= _plan.Inputs.Count)
                throw new InvalidOperationException("입력 번호가 범위 밖입니다: [" + label + "]");
            var video = parts[1] == "v";
            var type = video ? AVMediaType.AVMEDIA_TYPE_VIDEO : AVMediaType.AVMEDIA_TYPE_AUDIO;
            var spec = InputSpec.Parse(_plan.Inputs[index]);
            var input = new Input { Spec = spec };
            _inputs.Add(input);
            input.Source = _pool.Acquire(spec, type);
            var source = input.Source;
            // -ss는 파일 시작 시각부터, -sseof는 끝에서부터(길이를 모르면 처음부터)
            double? start = spec.Start;
            if (spec.StartFromEnd.HasValue)
                start = source.DurationSeconds.HasValue ? Math.Max(0, source.DurationSeconds.Value + spec.StartFromEnd.Value) : 0;
            source.Reset(spec, start);
            var first = source.Peek();

            // 버퍼 소스: 첫 프레임의 형식·크기(없으면 디코더 값)
            var filter = ffmpeg.avfilter_get_by_name(video ? "buffer" : "abuffer");
            var buffer = ffmpeg.avfilter_graph_alloc_filter(_graph, filter, "in" + _inputs.Count);
            if (buffer == null)
                throw new OutOfMemoryException("버퍼 소스");
            var parameters = ffmpeg.av_buffersrc_parameters_alloc();
            try
            {
                parameters->time_base = source.TimeBase;
                if (first != null)
                {
                    parameters->format = first->format;
                    if (video)
                    {
                        parameters->width = first->width;
                        parameters->height = first->height;
                        parameters->sample_aspect_ratio = first->sample_aspect_ratio.num > 0 ? first->sample_aspect_ratio : Native.Q(1, 1);
                        parameters->color_space = first->colorspace;
                        parameters->color_range = first->color_range;
                    }
                    else
                    {
                        parameters->sample_rate = first->sample_rate;
                        Native.Check(ffmpeg.av_channel_layout_copy(&parameters->ch_layout, &first->ch_layout), "채널 배치");
                    }
                }
                else
                {
                    source.DefaultParameters(parameters);
                }
                if (video)
                    parameters->frame_rate = source.FrameRate;
                Native.Check(ffmpeg.av_buffersrc_parameters_set(buffer, parameters), "버퍼 소스 설정");
            }
            finally
            {
                ffmpeg.av_channel_layout_uninit(&parameters->ch_layout);
                ffmpeg.av_free(parameters);
            }
            Native.Check(ffmpeg.avfilter_init_str(buffer, null), "버퍼 소스");
            input.Buffer = buffer;

            // ffmpeg처럼 -ss·-t를 trim으로(정확한 위치: 키프레임부터 풀고 그 앞은 버림), 회전 정보는 transpose로
            var last = buffer;
            var trim = TrimArguments(spec, source, start);
            if (trim != null)
                last = Chain(last, video ? "trim" : "atrim", trim, "trim" + _inputs.Count);
            if (video)
            {
                foreach (var rotation in Rotation(source, first))
                    last = Chain(last, rotation.Item1, rotation.Item2, rotation.Item1 + _inputs.Count);
            }
            Native.Check(ffmpeg.avfilter_link(last, 0, inout->filter_ctx, (uint)inout->pad_idx), "입력 잇기 [" + label + "]");
        }

        private AVFilterContext* Chain(AVFilterContext* previous, string filterName, string arguments, string name)
        {
            var filter = ffmpeg.avfilter_get_by_name(filterName);
            if (filter == null)
                throw new InvalidOperationException("필터가 없습니다: " + filterName);
            AVFilterContext* context = null;
            Native.Check(ffmpeg.avfilter_graph_create_filter(&context, filter, name, arguments, null, _graph), "필터 " + filterName);
            Native.Check(ffmpeg.avfilter_link(previous, 0, context, 0), "필터 잇기 " + filterName);
            return context;
        }

        /// <summary>-ss·-t → trim 인수("start=…:duration=…"). 둘 다 없으면 null. 그림(되풀이)은 -t만.</summary>
        private static string TrimArguments(InputSpec spec, InputSource source, double? start)
        {
            var parts = new List<string>();
            if (start.HasValue && !source.IsStill)
                parts.Add("start=" + Native.N(source.StartSeconds + start.Value));
            if (spec.Duration.HasValue)
                parts.Add("duration=" + Native.N(Math.Max(0, spec.Duration.Value)));
            return parts.Count == 0 ? null : string.Join(":", parts);
        }

        /// <summary>ffmpeg의 자동 회전(-autorotate)과 같은 필터: 스트림(또는 첫 프레임)의 displaymatrix.</summary>
        private static List<Tuple<string, string>> Rotation(InputSource source, AVFrame* first)
        {
            var result = new List<Tuple<string, string>>();
            var matrix = source.DisplayMatrix;
            if (matrix == null && first != null)
            {
                var side = ffmpeg.av_frame_get_side_data(first, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
                if (side != null && side->size >= 36)
                {
                    matrix = new int[9];
                    for (var i = 0; i < 9; i++)
                        matrix[i] = ((int*)side->data)[i];
                }
            }
            if (matrix == null)
                return result;
            var theta = DisplayRotation(matrix);
            if (double.IsNaN(theta))
                return result;
            theta = -Math.Round(theta);
            theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
            var hflip = matrix[0] < 0;
            var vflip = matrix[4] < 0;
            if (Math.Abs(theta - 90) < 1)
                result.Add(Tuple.Create("transpose", hflip ? "cclock_flip" : "clock"));
            else if (Math.Abs(theta - 180) < 1)
            {
                if (!hflip)
                    result.Add(Tuple.Create("hflip", (string)null));
                if (!vflip)
                    result.Add(Tuple.Create("vflip", (string)null));
            }
            else if (Math.Abs(theta - 270) < 1)
                result.Add(Tuple.Create("transpose", hflip ? "clock_flip" : "cclock"));
            else if (Math.Abs(theta) > 1)
                result.Add(Tuple.Create("rotate", Native.N(theta) + "*PI/180"));
            else
            {
                if (hflip)
                    result.Add(Tuple.Create("hflip", (string)null));
                if (vflip)
                    result.Add(Tuple.Create("vflip", (string)null));
            }
            return result;
        }

        /// <summary>av_display_rotation_get과 같다(도, 반시계 방향이 양수).</summary>
        private static double DisplayRotation(int[] matrix)
        {
            double a = matrix[0] / 65536.0, b = matrix[1] / 65536.0, c = matrix[3] / 65536.0, d = matrix[4] / 65536.0;
            var scale0 = Math.Sqrt(a * a + c * c);
            var scale1 = Math.Sqrt(b * b + d * d);
            if (scale0 == 0 || scale1 == 0)
                return double.NaN;
            return -(Math.Atan2(b / scale1, a / scale0) * 180 / Math.PI);
        }

        private void ConnectSink(AVFilterInOut* output)
        {
            var filter = ffmpeg.avfilter_get_by_name(_plan.Video ? "buffersink" : "abuffersink");
            _sink = ffmpeg.avfilter_graph_alloc_filter(_graph, filter, "out");
            if (_sink == null)
                throw new OutOfMemoryException("버퍼 싱크");
            if (_plan.Video)
            {
                var bgra = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                Native.SetList(_sink, "pixel_formats", "bgra", "pix_fmts", &bgra, sizeof(int));
            }
            else
            {
                var s16 = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
                var rate = _plan.SampleRate > 0 ? _plan.SampleRate : 48000;
                Native.SetList(_sink, "sample_formats", "s16", "sample_fmts", &s16, sizeof(int));
                // 새 이름은 밑줄 없는 samplerates(옛 이진 옵션 sample_rates는 FFmpeg 9에서 없어짐)
                Native.SetList(_sink, "samplerates", rate.ToString(CultureInfo.InvariantCulture), "sample_rates", &rate, sizeof(int));
                Native.SetText(_sink, "channel_layouts", "stereo", "ch_layouts");
            }
            Native.Check(ffmpeg.avfilter_init_str(_sink, null), "버퍼 싱크");
            Native.Check(ffmpeg.avfilter_link(output->filter_ctx, (uint)output->pad_idx, _sink, 0), "출력 잇기");
        }

        /// <summary>그래프를 돌린다: 싱크가 프레임을 달라고 하면 모자란 입력에 한 장씩 넣는다(ffmpeg의 request_oldest 방식).</summary>
        private void Pump(Action<byte[]> write, CancellationToken cancellation)
        {
            var frame = ffmpeg.av_frame_alloc();
            var output = ffmpeg.av_frame_alloc();
            try
            {
                var timeBase = ffmpeg.av_buffersink_get_time_base(_sink);
                var frameRate = _plan.Video ? ffmpeg.av_buffersink_get_frame_rate(_sink) : Native.Q(0, 1);
                long emitted = 0;
                long nextIndex = -1;
                byte[] last = null;
                var stalled = 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = ffmpeg.avfilter_graph_request_oldest(_graph);
                    if (result == ffmpeg.AVERROR_EOF)
                    {
                        Drain(output, write, timeBase, frameRate, ref emitted, ref nextIndex, ref last);
                        return;
                    }
                    if (Native.IsAgain(result))
                    {
                        var fed = false;
                        foreach (var input in _inputs)
                        {
                            if (input.Eof || ffmpeg.av_buffersrc_get_nb_failed_requests(input.Buffer) == 0)
                                continue;
                            fed = true;
                            if (input.Source.Next(frame))
                            {
                                // 8 = AV_BUFFERSRC_FLAG_KEEP_REF(프레임 참조를 우리가 들고 있다가 푼다)
                                Native.Check(ffmpeg.av_buffersrc_add_frame_flags(input.Buffer, frame, 8), "프레임 넣기");
                                ffmpeg.av_frame_unref(frame);
                            }
                            else
                            {
                                Native.Check(ffmpeg.av_buffersrc_add_frame_flags(input.Buffer, null, 0), "입력 끝");
                                input.Eof = true;
                            }
                        }
                        if (!fed && ++stalled > 100000)
                            throw new InvalidOperationException("필터 그래프가 더 나아가지 않습니다.");
                        if (fed)
                            stalled = 0;
                    }
                    else
                    {
                        Native.Check(result, "필터 그래프 실행");
                    }
                    if (Drain(output, write, timeBase, frameRate, ref emitted, ref nextIndex, ref last))
                        return;
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_frame_free(&output);
            }
        }

        /// <summary>싱크에 쌓인 프레임을 다 꺼내 보낸다. 더 보낼 필요가 없으면(장수를 채움·끝) true.</summary>
        private bool Drain(AVFrame* output, Action<byte[]> write, AVRational timeBase, AVRational frameRate, ref long emitted, ref long nextIndex, ref byte[] last)
        {
            while (true)
            {
                var result = ffmpeg.av_buffersink_get_frame_flags(_sink, output, ffmpeg.AV_BUFFERSINK_FLAG_NO_REQUEST);
                if (result == ffmpeg.AVERROR_EOF)
                    return true;
                if (Native.IsAgain(result))
                    return false;
                Native.Check(result, "프레임 꺼내기");
                try
                {
                    if (_plan.Video)
                    {
                        // 일정 프레임 레이트로: 시각 → 장 번호. 빠진 번호는 앞 장을 되풀이하고 앞선 번호(같은 장)는 버린다(ffmpeg -vsync cfr)
                        var index = frameRate.num > 0 && output->pts != ffmpeg.AV_NOPTS_VALUE
                            ? ffmpeg.av_rescale_q(output->pts, timeBase, ffmpeg.av_inv_q(frameRate))
                            : nextIndex < 0 ? 0 : nextIndex;
                        if (nextIndex < 0)
                            nextIndex = index;
                        if (index < nextIndex)
                            continue;
                        while (index > nextIndex && last != null)
                        {
                            write(last);
                            emitted++;
                            nextIndex++;
                            if (_plan.MaxFrames > 0 && emitted >= _plan.MaxFrames)
                                return true;
                        }
                        var pixels = CopyVideo(output);
                        write(pixels);
                        last = pixels;
                        emitted++;
                        nextIndex = index + 1;
                        if (_plan.MaxFrames > 0 && emitted >= _plan.MaxFrames)
                            return true;
                    }
                    else
                    {
                        write(CopyAudio(output));
                    }
                }
                finally
                {
                    ffmpeg.av_frame_unref(output);
                }
            }
        }

        private byte[] CopyVideo(AVFrame* frame)
        {
            var width = frame->width;
            var height = frame->height;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            var source = frame->data[0];
            var lineSize = frame->linesize[0];
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<byte>(source + (long)y * lineSize, stride).CopyTo(pixels.AsSpan(y * stride, stride));
            return pixels;
        }

        private static byte[] CopyAudio(AVFrame* frame)
        {
            var channels = frame->ch_layout.nb_channels;
            var size = frame->nb_samples * channels * 2;
            var pcm = new byte[size];
            new ReadOnlySpan<byte>(frame->data[0], size).CopyTo(pcm);
            return pcm;
        }

        public void Dispose()
        {
            if (_graph != null)
            {
                var graph = _graph;
                ffmpeg.avfilter_graph_free(&graph);
                _graph = null;
            }
            foreach (var input in _inputs)
                _pool.Release(input.Source);
            _inputs.Clear();
        }
    }
}
