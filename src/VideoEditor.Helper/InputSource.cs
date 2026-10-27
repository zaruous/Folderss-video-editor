using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace VideoEditor.Helper
{
    /// <summary>
    /// 렌더 계획의 입력 인수 묶음(ffmpeg 명령줄의 "-ss 1.5 -t 2 -i 파일" 같은 것)을 읽은 것. 플러그인의 RenderGraph가 쓰는 옵션만 안다 —
    /// 모르는 옵션이면 거절해 플러그인이 ffmpeg.exe로 돌아가게 한다(모르는 채 다르게 그리지 않게).
    /// </summary>
    internal sealed class InputSpec
    {
        public string Path { get; private set; }
        /// <summary>-f(강제 디멀티플렉서, 예: image2).</summary>
        public string Format { get; private set; }
        /// <summary>-ss(초, 파일 시작 시각 기준).</summary>
        public double? Start { get; private set; }
        /// <summary>-sseof(초, 파일 끝 기준 — 음수).</summary>
        public double? StartFromEnd { get; private set; }
        /// <summary>-t(초).</summary>
        public double? Duration { get; private set; }
        /// <summary>-loop 1.</summary>
        public bool Loop { get; private set; }
        /// <summary>-framerate.</summary>
        public AVRational? FrameRate { get; private set; }
        /// <summary>-pattern_type(image2).</summary>
        public string PatternType { get; private set; }

        public static InputSpec Parse(IList<string> args)
        {
            var spec = new InputSpec();
            for (var i = 0; i < args.Count; i++)
            {
                var option = args[i];
                if (i + 1 >= args.Count)
                    throw new InvalidOperationException("입력 옵션에 값이 없습니다: " + option);
                var value = args[++i];
                switch (option)
                {
                    case "-i":
                        spec.Path = value;
                        break;
                    case "-f":
                        spec.Format = value;
                        break;
                    case "-ss":
                        spec.Start = Seconds(value, option);
                        break;
                    case "-sseof":
                        spec.StartFromEnd = Seconds(value, option);
                        break;
                    case "-t":
                        spec.Duration = Seconds(value, option);
                        break;
                    case "-loop":
                        spec.Loop = value != "0";
                        break;
                    case "-framerate":
                        spec.FrameRate = Rational(value, option);
                        break;
                    case "-pattern_type":
                        spec.PatternType = value;
                        break;
                    default:
                        throw new InvalidOperationException("도우미가 모르는 입력 옵션입니다: " + option);
                }
            }
            if (string.IsNullOrEmpty(spec.Path))
                throw new InvalidOperationException("입력 파일(-i)이 없습니다.");
            return spec;
        }

        private static double Seconds(string value, string option)
        {
            double seconds;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                throw new InvalidOperationException(option + " 값을 읽지 못했습니다: " + value);
            return seconds;
        }

        private static AVRational Rational(string value, string option)
        {
            var parts = value.Split('/');
            int num, den = 1;
            if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out num)
                || (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out den)) || num <= 0 || den <= 0)
            {
                double fps;
                if (parts.Length == 1 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) && fps > 0)
                    return Native.Q((int)Math.Round(fps * 1000), 1000);
                throw new InvalidOperationException(option + " 값을 읽지 못했습니다: " + value);
            }
            return Native.Q(num, den);
        }

        /// <summary>같은 열기 조건(파일·디멀티플렉서)이면 풀의 열린 것을 다시 쓸 수 있다.</summary>
        public string PoolKey(AVMediaType type)
        {
            return type + "|" + (Format ?? "") + "|" + (PatternType ?? "") + "|" + Path.ToUpperInvariant();
        }
    }

    /// <summary>
    /// 열어 둔 입력 하나: 파일(디멀티플렉서)과 고른 스트림(화면 또는 소리) 하나의 디코더. 위치를 옮겨(-ss) 프레임을 차례로 꺼낸다.
    /// 그림 파일(image2·*_pipe)은 한 장을 풀어 두고 -loop 1이면 -framerate 간격으로 되풀이한다(ffmpeg의 image2 loop와 같은 시각).
    /// 한 번에 한 작업만 쓴다(<see cref="InputPool"/>이 지킨다).
    /// </summary>
    internal sealed unsafe class InputSource : IDisposable
    {
        private AVFormatContext* _format;
        private AVCodecContext* _codec;
        private AVStream* _stream;
        private int _index;
        private AVPacket* _packet;
        private AVFrame* _held;
        private AVFrame* _still;
        private bool _demuxEof;
        private bool _flushSent;
        private long _lastPts = ffmpeg.AV_NOPTS_VALUE;
        private long _loopOffset;
        private long _stillCount;
        private bool _used;

        private InputSource()
        {
        }

        public string Key { get; private set; }
        public string Path { get; private set; }
        public AVMediaType Type { get; private set; }
        public long FileSize { get; private set; }
        public long FileTime { get; private set; }
        public DateTime LastUsed { get; set; }
        public bool Busy { get; set; }
        /// <summary>읽다가 오류가 나 다시 쓰지 않을 것(풀에서 뺀다).</summary>
        public bool Broken { get; set; }
        /// <summary>그림 파일(한 장).</summary>
        public bool IsStill { get; private set; }
        public bool Loop { get; private set; }
        /// <summary>버퍼 소스에 알릴 시간 단위(스트림 시간 단위, 그림은 1/프레임 레이트).</summary>
        public AVRational TimeBase { get; private set; }
        public AVRational FrameRate { get; private set; }
        /// <summary>파일 시작 시각(초) — -ss는 여기서부터 센다.</summary>
        public double StartSeconds { get; private set; }
        /// <summary>파일 길이(초, 모르면 null) — -sseof에 쓴다.</summary>
        public double? DurationSeconds { get; private set; }
        /// <summary>회전 정보(스트림의 displaymatrix, 없으면 null).</summary>
        public int[] DisplayMatrix { get; private set; }

        public static InputSource Open(InputSpec spec, AVMediaType type)
        {
            var source = new InputSource { Key = spec.PoolKey(type), Path = spec.Path, Type = type, LastUsed = DateTime.UtcNow };
            try
            {
                source.OpenCore(spec);
                var file = new FileInfo(spec.Path);
                source.FileSize = file.Length;
                source.FileTime = file.LastWriteTimeUtc.Ticks;
                return source;
            }
            catch (Exception)
            {
                source.Dispose();
                throw;
            }
        }

        private void OpenCore(InputSpec spec)
        {
            AVInputFormat* iformat = null;
            if (spec.Format != null)
            {
                iformat = ffmpeg.av_find_input_format(spec.Format);
                if (iformat == null)
                    throw new InvalidOperationException("모르는 입력 형식입니다: " + spec.Format);
            }
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            if (spec.PatternType != null)
                ffmpeg.av_dict_set(&options, "pattern_type", spec.PatternType, 0);
            AVFormatContext* format = null;
            var result = ffmpeg.avformat_open_input(&format, spec.Path, iformat, &options);
            ffmpeg.av_dict_free(&options);
            Native.Check(result, "파일 열기 " + System.IO.Path.GetFileName(spec.Path));
            _format = format;
            Native.Check(ffmpeg.avformat_find_stream_info(_format, null), "스트림 정보");
            _index = ffmpeg.av_find_best_stream(_format, Type, -1, -1, null, 0);
            if (_index < 0)
                throw new InvalidOperationException((Type == AVMediaType.AVMEDIA_TYPE_VIDEO ? "화면" : "소리") + " 스트림이 없습니다: " + System.IO.Path.GetFileName(spec.Path));
            _stream = _format->streams[_index];
            // 쓰지 않는 스트림은 읽지 않는다(ffmpeg도 같다)
            for (var i = 0; i < _format->nb_streams; i++)
            {
                if (i != _index)
                    _format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }
            var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException("디코더가 없습니다: " + _stream->codecpar->codec_id);
            _codec = ffmpeg.avcodec_alloc_context3(codec);
            Native.Check(ffmpeg.avcodec_parameters_to_context(_codec, _stream->codecpar), "디코더 설정");
            _codec->pkt_timebase = _stream->time_base;
            _codec->thread_count = 0;
            _codec->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            Native.Check(ffmpeg.avcodec_open2(_codec, codec, null), "디코더 열기");
            _packet = ffmpeg.av_packet_alloc();

            var name = Native.Text(_format->iformat->name) ?? "";
            IsStill = Type == AVMediaType.AVMEDIA_TYPE_VIDEO && (name == "image2" || name.EndsWith("_pipe", StringComparison.Ordinal));
            TimeBase = _stream->time_base;
            FrameRate = Type == AVMediaType.AVMEDIA_TYPE_VIDEO ? ffmpeg.av_guess_frame_rate(_format, _stream, null) : Native.Q(0, 1);
            StartSeconds = _format->start_time != ffmpeg.AV_NOPTS_VALUE ? _format->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
            DurationSeconds = _format->duration != ffmpeg.AV_NOPTS_VALUE ? _format->duration / (double)ffmpeg.AV_TIME_BASE : (double?)null;
            var matrix = ffmpeg.av_packet_side_data_get(_stream->codecpar->coded_side_data, _stream->codecpar->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
            if (matrix != null && matrix->size >= 36)
                DisplayMatrix = Matrix(matrix->data);
        }

        private static int[] Matrix(byte* data)
        {
            var values = new int[9];
            for (var i = 0; i < 9; i++)
                values[i] = ((int*)data)[i];
            return values;
        }

        /// <summary>파일이 바뀌었는지(크기·고친 시각) — 바뀌었으면 다시 연다.</summary>
        public bool FileChanged()
        {
            try
            {
                var file = new FileInfo(Path);
                return !file.Exists || file.Length != FileSize || file.LastWriteTimeUtc.Ticks != FileTime;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 한 작업에서 쓰기 전에: 되풀이·프레임 레이트를 맞추고 seekSeconds(파일 시작 시각 기준 초, 없으면 처음)로 옮긴 뒤 디코더를 비운다.
        /// 앞서 꺼내 둔 프레임도 버린다.
        /// </summary>
        public void Reset(InputSpec spec, double? seekSeconds)
        {
            Loop = spec.Loop;
            if (IsStill)
            {
                FrameRate = spec.FrameRate ?? Native.Q(25, 1);
                TimeBase = ffmpeg.av_inv_q(FrameRate);
            }
            FreeHeld();
            _demuxEof = false;
            _flushSent = false;
            _lastPts = ffmpeg.AV_NOPTS_VALUE;
            _loopOffset = 0;
            _stillCount = 0;
            if (IsStill)
            {
                // 그림은 한 번 풀어 둔 것을 되풀이한다 — 되감지 않는다
                _used = true;
                return;
            }
            if (seekSeconds.HasValue || _used)
            {
                var seconds = StartSeconds + Math.Max(0, seekSeconds ?? 0);
                var target = (long)Math.Round(seconds * ffmpeg.AV_TIME_BASE);
                var result = ffmpeg.avformat_seek_file(_format, -1, long.MinValue, target, target, 0);
                if (result < 0 && _used)
                {
                    // 되감지 못하는 파일(파이프 등) — 다시 열게 한다
                    Broken = true;
                    Native.Check(result, "위치 옮기기");
                }
                ffmpeg.avcodec_flush_buffers(_codec);
            }
            _used = true;
        }

        /// <summary>첫 프레임을 풀어 들고 있는다(버퍼 소스 설정용). 하나도 없으면 null.</summary>
        public AVFrame* Peek()
        {
            if (_held != null)
                return _held;
            var frame = ffmpeg.av_frame_alloc();
            if (Decode(frame))
            {
                _held = frame;
                return _held;
            }
            ffmpeg.av_frame_free(&frame);
            return null;
        }

        /// <summary>다음 프레임을 into로 옮긴다. 끝이면 false.</summary>
        public bool Next(AVFrame* into)
        {
            if (_held != null)
            {
                ffmpeg.av_frame_move_ref(into, _held);
                FreeHeld();
                return true;
            }
            return Decode(into);
        }

        private void FreeHeld()
        {
            var held = _held;
            _held = null;
            if (held != null)
                ffmpeg.av_frame_free(&held);
        }

        /// <summary>버퍼 소스를 디코더 값으로 설정할 때(프레임이 하나도 없을 때) 쓸 형식·크기.</summary>
        public void DefaultParameters(AVBufferSrcParameters* parameters)
        {
            if (Type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                parameters->format = (int)_codec->pix_fmt;
                parameters->width = Math.Max(2, _codec->width);
                parameters->height = Math.Max(2, _codec->height);
                parameters->sample_aspect_ratio = _codec->sample_aspect_ratio.num > 0 ? _codec->sample_aspect_ratio : Native.Q(1, 1);
                parameters->color_space = _codec->colorspace;
                parameters->color_range = _codec->color_range;
            }
            else
            {
                parameters->format = (int)_codec->sample_fmt;
                parameters->sample_rate = _codec->sample_rate;
                ffmpeg.av_channel_layout_copy(&parameters->ch_layout, &_codec->ch_layout);
            }
        }

        private bool Decode(AVFrame* into)
        {
            if (!IsStill)
                return DecodeStream(into);
            if (_stillCount > 0 && !Loop)
                return false;
            if (_still == null)
            {
                if (!DecodeStream(into))
                    return false;
                _still = ffmpeg.av_frame_alloc();
                Native.Check(ffmpeg.av_frame_ref(_still, into), "그림 복사");
            }
            else
            {
                ffmpeg.av_frame_unref(into);
                Native.Check(ffmpeg.av_frame_ref(into, _still), "그림 참조");
            }
            into->pts = _stillCount;
            into->best_effort_timestamp = _stillCount;
            into->duration = 1;
            _stillCount++;
            return true;
        }

        private bool DecodeStream(AVFrame* into)
        {
            while (true)
            {
                var result = ffmpeg.avcodec_receive_frame(_codec, into);
                if (result >= 0)
                {
                    Stamp(into);
                    return true;
                }
                if (result == ffmpeg.AVERROR_EOF)
                {
                    if (!Loop || IsStill)
                        return false;
                    // 되풀이: 처음으로 되감고 시각을 이어 붙인다(애니메이션 그림 등)
                    _loopOffset = _lastPts == ffmpeg.AV_NOPTS_VALUE ? 0 : _lastPts + Step(into);
                    var start = _format->start_time != ffmpeg.AV_NOPTS_VALUE ? _format->start_time : 0;
                    if (ffmpeg.avformat_seek_file(_format, -1, long.MinValue, start, start, 0) < 0)
                        return false;
                    ffmpeg.avcodec_flush_buffers(_codec);
                    _demuxEof = false;
                    _flushSent = false;
                    continue;
                }
                if (!Native.IsAgain(result))
                {
                    Broken = true;
                    Native.Check(result, "디코딩");
                }
                if (_demuxEof)
                {
                    if (_flushSent)
                        return false;
                    _flushSent = true;
                    ffmpeg.avcodec_send_packet(_codec, null);
                    continue;
                }
                result = ffmpeg.av_read_frame(_format, _packet);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    _demuxEof = true;
                    continue;
                }
                if (result < 0)
                {
                    Broken = true;
                    Native.Check(result, "읽기");
                }
                if (_packet->stream_index != _index)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }
                result = ffmpeg.avcodec_send_packet(_codec, _packet);
                ffmpeg.av_packet_unref(_packet);
                if (result < 0 && !Native.IsAgain(result) && result != ffmpeg.AVERROR_EOF)
                {
                    // 깨진 패킷 하나는 건너뛴다(ffmpeg도 경고만 내고 계속한다)
                    continue;
                }
            }
        }

        /// <summary>프레임 시각: 디코더가 추정한 시각, 없으면 앞 프레임에 이어서.</summary>
        private void Stamp(AVFrame* frame)
        {
            var pts = frame->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                pts = frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                pts = _lastPts == ffmpeg.AV_NOPTS_VALUE ? 0 : _lastPts + Step(frame) - _loopOffset;
            frame->pts = pts + _loopOffset;
            frame->best_effort_timestamp = frame->pts;
            _lastPts = frame->pts;
        }

        /// <summary>프레임 하나의 길이(시간 단위).</summary>
        private long Step(AVFrame* frame)
        {
            if (frame->duration > 0)
                return frame->duration;
            if (Type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                return frame->sample_rate > 0 ? ffmpeg.av_rescale_q(Math.Max(1, frame->nb_samples), Native.Q(1, frame->sample_rate), TimeBase) : 1;
            return FrameRate.num > 0 ? Math.Max(1, ffmpeg.av_rescale_q(1, ffmpeg.av_inv_q(FrameRate), TimeBase)) : 1;
        }

        public void Dispose()
        {
            FreeHeld();
            var still = _still;
            _still = null;
            if (still != null)
                ffmpeg.av_frame_free(&still);
            var packet = _packet;
            _packet = null;
            if (packet != null)
                ffmpeg.av_packet_free(&packet);
            var codec = _codec;
            _codec = null;
            if (codec != null)
                ffmpeg.avcodec_free_context(&codec);
            var format = _format;
            _format = null;
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>
    /// 열어 둔 입력들: 같은 파일·스트림을 다음 요청이 다시 쓴다(열기·스트림 정보 읽기를 건너뜀). 같은 파일을 한 계획에서 두 번 쓰면(화면 + 소리) 따로 연다.
    /// 쓰지 않은 지 오래된 것은 닫아 파일을 잡아 두지 않는다(사용자가 파일을 바꾸거나 지울 수 있게).
    /// </summary>
    internal sealed class InputPool
    {
        private const int MaxOpen = 12;
        private readonly object _gate = new object();
        private readonly List<InputSource> _sources = new List<InputSource>();

        public InputSource Acquire(InputSpec spec, AVMediaType type)
        {
            var key = spec.PoolKey(type);
            lock (_gate)
            {
                foreach (var source in _sources.Where(s => !s.Busy && s.Key == key).ToList())
                {
                    if (source.Broken || source.FileChanged())
                    {
                        _sources.Remove(source);
                        source.Dispose();
                        continue;
                    }
                    source.Busy = true;
                    source.LastUsed = DateTime.UtcNow;
                    return source;
                }
                // 너무 많이 열려 있으면 가장 오래 안 쓴 것부터 닫는다
                foreach (var idle in _sources.Where(s => !s.Busy).OrderBy(s => s.LastUsed).ToList())
                {
                    if (_sources.Count < MaxOpen)
                        break;
                    _sources.Remove(idle);
                    idle.Dispose();
                }
                var opened = InputSource.Open(spec, type);
                opened.Busy = true;
                _sources.Add(opened);
                return opened;
            }
        }

        public void Release(InputSource source)
        {
            if (source == null)
                return;
            lock (_gate)
            {
                source.Busy = false;
                source.LastUsed = DateTime.UtcNow;
                if (source.Broken)
                {
                    _sources.Remove(source);
                    source.Dispose();
                }
            }
        }

        /// <summary>idle보다 오래 안 쓴 것을 닫는다.</summary>
        public void EvictIdle(TimeSpan idle)
        {
            lock (_gate)
            {
                var limit = DateTime.UtcNow - idle;
                foreach (var source in _sources.Where(s => !s.Busy && s.LastUsed < limit).ToList())
                {
                    _sources.Remove(source);
                    source.Dispose();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _sources.Count;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                foreach (var source in _sources.Where(s => !s.Busy).ToList())
                {
                    _sources.Remove(source);
                    source.Dispose();
                }
            }
        }
    }
}
