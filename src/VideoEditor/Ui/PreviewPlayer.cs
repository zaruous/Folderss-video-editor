using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미리 보기: 재생 헤드 위치 한 장(마지막 요청만 그림) + 재생(FFmpeg가 렌더 그래프를 BGRA 원시 프레임과 PCM으로 흘려보냄).
    /// 재생 중 화면은 소리 시계(waveOut이 실제로 낸 위치)에 맞추고, 소리를 끄거나 장치가 없으면 Stopwatch에 맞춘다.
    /// 렌더는 프로젝트 사본으로 한다(편집과 동시에 읽지 않게).
    /// </summary>
    internal sealed class PreviewPlayer : IDisposable
    {
        private readonly EditorServices _services;
        private readonly Dispatcher _dispatcher;
        // 재생 구간의 무거운 수식 전환을 배경에서 미리 그리는 줄(이 창의 것 — 캐시 파일은 편집 창들이 함께 쓴다)
        private readonly PrerenderQueue _prerender;
        private CancellationTokenSource _frameCancel;
        private CancellationTokenSource _prepareCancel;
        private int _frameRequest;
        private Playback _playback;
        private bool _disposed;

        public PreviewPlayer(EditorServices services, Dispatcher dispatcher)
        {
            _services = services;
            _dispatcher = dispatcher;
            _prerender = new PrerenderQueue(services.Prerender);
            _prerender.Changed += () => _dispatcher.BeginInvoke(new Action(() => PrerenderChanged?.Invoke(this, EventArgs.Empty)));
            _prerender.Finished += (job, error) => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed)
                    return;
                Message?.Invoke(error == null
                    ? "전환을 미리 그렸습니다(" + job.Name + ") — 다시 재생하면 그 구간을 매끄럽게 봅니다"
                    : "전환을 미리 그리지 못했습니다(" + job.Name + "): " + error.Message.Split('\n')[0]);
            }));
        }

        /// <summary>배경에서 미리 그리는 전환(없으면 null) — 미리 보기 화면 구석에 진행을 보인다.</summary>
        public PrerenderStatus Prerendering { get { return _prerender.Status; } }

        /// <summary><see cref="Prerendering"/>이 바뀜(UI 스레드에서).</summary>
        public event EventHandler PrerenderChanged;

        /// <summary>그린 화면(크기가 바뀌면 새 비트맵).</summary>
        public WriteableBitmap Bitmap { get; private set; }

        public event EventHandler BitmapChanged;
        /// <summary>재생 중 위치(프로젝트 프레임) — UI 스레드에서.</summary>
        public event Action<long> Position;
        public event EventHandler StateChanged;
        /// <summary>오류·안내(상태 줄).</summary>
        public event Action<string> Message;

        /// <summary>재생 중이거나 재생 전 꾸민 글자를 그리는 중(재생 단추·스페이스가 멈춤으로 동작하게).</summary>
        public bool IsPlaying { get { return _playback != null || _prepareCancel != null; } }

        /// <summary>미리 보기 해상도 배율(1, 0.5, 0.25).</summary>
        public double Scale { get; set; } = 0.5;

        public bool Muted { get; set; }

        /// <summary>마지막으로 그린 프레임 위치(-1이면 아직 없음).</summary>
        public long ShownFrame { get; private set; } = -1;

        /// <summary>미리 보기 화면 크기(프로젝트 크기 × <see cref="Scale"/>, 짝수, 너비 1920까지).</summary>
        public (int Width, int Height) OutputSize(Project project)
        {
            var width = Math.Max(16, (int)Math.Round(project.Width * Scale / 2) * 2);
            width = Math.Min(width, 1920);
            var height = Math.Max(16, (int)Math.Round(width * (double)project.Height / project.Width / 2) * 2);
            return (width, height);
        }

        private void EnsureBitmap(int width, int height)
        {
            if (Bitmap != null && Bitmap.PixelWidth == width && Bitmap.PixelHeight == height)
                return;
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            BitmapChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Show(byte[] bgra, int width, int height, long frame)
        {
            EnsureBitmap(width, height);
            Bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
            ShownFrame = frame;
        }

        /// <summary>frame 위치를 그린다(이전 요청은 버림). UI 스레드에서 부른다.</summary>
        public void RequestFrame(Project project, long frame)
        {
            var tool = _services.Tool;
            // 빈 타임라인은 그리지 않는다(플레이어 패널이 안내 글을 보임)
            if (_disposed || tool == null || IsPlaying || project.Duration == 0)
                return;
            _frameCancel?.Cancel();
            var cancel = _frameCancel = new CancellationTokenSource();
            var request = ++_frameRequest;
            var copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            var size = OutputSize(copy);
            var rasterizer = _services.TextRasterizer;
            // 미리 보기 도우미가 있으면 ffmpeg.exe를 띄우지 않고 그린다(실패하면 ffmpeg.exe로)
            var helper = _services.HelperFor(tool);
            Task.Run(async () =>
            {
                // 연달아 들어오는 요청(끌기·키 반복)은 잠깐 모아 마지막 것만 그린다
                await Task.Delay(40, cancel.Token).ConfigureAwait(false);
                var bytes = await PreviewRenderer.Frame(tool, copy, frame, size.Width, size.Height, cancel.Token, rasterizer, helper,
                    ex => _services.ReportHelperFailure(helper, ex)).ConfigureAwait(false);
                if (helper != null && helper.IsAlive)
                    _services.ReportHelperSuccess();
                return bytes;
            }, cancel.Token).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    if (t.Exception != null && request == _frameRequest)
                        _dispatcher.BeginInvoke(new Action(() => Message?.Invoke("미리 보기를 그리지 못했습니다: " + t.Exception.GetBaseException().Message)));
                    return;
                }
                var bytes = t.Result;
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (request != _frameRequest || IsPlaying || bytes == null)
                        return;
                    Show(bytes, size.Width, size.Height, frame);
                }));
            }, TaskScheduler.Default);
        }

        /// <summary>from부터 재생한다(끝나면 멈춤 — until을 주면 거기까지). UI 스레드에서 부른다.</summary>
        public void Play(Project project, long from, long until = -1)
        {
            Stop();
            var tool = _services.Tool;
            if (_disposed || tool == null)
                return;
            var copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            var to = until > 0 ? Math.Min(until, copy.Duration) : copy.Duration;
            if (from >= to)
                from = 0;
            if (to <= 0)
                return;
            _frameCancel?.Cancel();
            var size = OutputSize(copy);
            EnsureBitmap(size.Width, size.Height);
            var rasterizer = _services.TextRasterizer;
            var texts = copy.AllClips.Any(RenderGraph.NeedsTextImage);
            // 재생 구간에 무거운 수식 전환이 있으면 미리 그린 캐시를 찾는다(없는 것은 배경에서 그리기 시작 — 이번 재생은 지금 방식)
            var heavy = TransitionPrerender.Windows(copy, from, to, tool.MajorVersion).Count > 0;
            if (!texts && !heavy)
            {
                Start(tool, copy, from, to, size.Width, size.Height, rasterizer, null);
                return;
            }
            // 꾸민 글자 PNG는 UI 스레드 밖에서 먼저 그려 둔다 — 재생 FFmpeg는 캐시만 읽어 화면이 멈추지 않는다
            var prepare = _prepareCancel = new CancellationTokenSource();
            // 애니메이션 장은 많아 설정을 바꾼 뒤 처음 재생할 때 시간이 걸린다 — 상태 줄에 진행을 알린다
            void Drawing(int done, int total)
            {
                if (total >= 5 && (done == total || done % 5 == 0))
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (prepare == _prepareCancel)
                            Message?.Invoke(done < total ? "애니메이션을 그리는 중… " + done + "/" + total : "애니메이션을 다 그렸습니다(" + total + "장).");
                    }));
            }
            Task.Run(() =>
            {
                if (texts)
                    PreviewRenderer.PrepareTextImages(copy, from, to, size.Width, size.Height, rasterizer, prepare.Token, Drawing);
                return heavy ? SurveyPrerender(tool, copy, from, to, size.Width, size.Height, rasterizer) : null;
            }, prepare.Token)
                .ContinueWith(t => _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (prepare != _prepareCancel || _disposed)
                        return;
                    _prepareCancel = null;
                    if (t.Status != TaskStatus.RanToCompletion)
                    {
                        if (t.Exception != null)
                            Message?.Invoke("꾸민 글자를 그리지 못했습니다: " + t.Exception.GetBaseException().Message);
                        StateChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    Start(tool, copy, from, to, size.Width, size.Height, rasterizer, t.Result);
                })), TaskScheduler.Default);
            // 준비하는 동안에도 재생 중으로 보인다(재생 단추가 멈춤 모양)
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 재생 구간의 무거운 수식 전환: 미리 그린 캐시가 있는 구간을 돌려주고, 없는 것은 배경에서 그리기 시작한다(재생 헤드에서 가까운 차례).
        /// 캐시를 찾지 못하면(오류) 빈 목록 — 지금 방식(줄여 섞기)으로 재생한다. UI 스레드 밖에서 부른다.
        /// </summary>
        private IReadOnlyList<PrerenderedSpan> SurveyPrerender(FfmpegTool tool, Project copy, long from, long to, int width, int height, ITextRasterizer rasterizer)
        {
            try
            {
                var (spans, jobs) = _services.Prerender.Survey(tool, copy, from, to, width, height, rasterizer);
                // 이미 그리는 중인 일만 다시 청했으면 알리지 않는다(재생을 거듭 눌러도 같은 안내가 되풀이되지 않게)
                if (jobs.Count > 0 && _prerender.Request(tool, jobs) > 0)
                    _dispatcher.BeginInvoke(new Action(() => Message?.Invoke("무거운 전환 " + jobs.Count + "개를 미리 그립니다 — 다 그리면 그 구간을 매끄럽게 재생합니다(진행은 미리 보기 화면 왼쪽 아래)")));
                return spans;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Start(FfmpegTool tool, Project copy, long from, long to, int width, int height, ITextRasterizer rasterizer, IReadOnlyList<PrerenderedSpan> prerendered)
        {
            try
            {
                var helper = _services.HelperFor(tool);
                _playback = new Playback(this, tool, helper, copy, from, to, width, height, Muted, rasterizer, ex => _services.ReportHelperFailure(helper, ex), prerendered);
            }
            catch (Exception ex)
            {
                _playback = null;
                Message?.Invoke("재생하지 못했습니다: " + ex.Message);
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>미리 그린 캐시로 시작한 재생이 한 장도 못 냄(깨진 캐시 등): 그 캐시를 버리고 지금 방식(줄여 섞기)으로 다시 재생한다.</summary>
        private void RetryWithoutPrerender(Playback failed)
        {
            if (_playback != failed)
                return;
            _playback = null;
            failed.Dispose();
            foreach (var span in failed.Prerendered)
                RenderCommand.TryDelete(span.Path);
            Start(failed.Tool, failed.Project, failed.From, failed.To, failed.Width, failed.Height, failed.Rasterizer, null);
        }

        public void Stop()
        {
            // 재생 전 꾸민 글자를 그리는 중이면 그만둔다(끝나도 재생하지 않음)
            var preparing = _prepareCancel;
            _prepareCancel = null;
            preparing?.Cancel();
            var playback = _playback;
            _playback = null;
            playback?.Dispose();
            if (preparing != null || playback != null)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
            _frameCancel?.Cancel();
            _prerender.Dispose();
        }

        /// <summary>재생 한 번: 화면 흐름 + 소리 흐름(도우미의 파이프 또는 ffmpeg.exe 프로세스) + 읽는 스레드 + 표시 타이머.</summary>
        private sealed class Playback : IDisposable
        {
            private readonly PreviewPlayer _owner;
            private readonly long _from;
            private readonly long _to;
            private readonly int _width;
            private readonly int _height;
            private readonly FrameRate _rate;
            private readonly PlaybackStream _video;
            private readonly PlaybackStream _audio;
            private readonly BlockingCollection<(long Index, byte[] Pixels)> _frames = new BlockingCollection<(long, byte[])>(8);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly DispatcherTimer _timer;
            private readonly Stopwatch _clock = new Stopwatch();
            private WaveOutEvent _waveOut;
            private BufferedWaveProvider _buffer;
            private volatile bool _videoDone;
            private bool _started;
            private long _lastShown = -1;
            // 큐에서 꺼냈지만 아직 보여 줄 시각이 안 된 프레임(큐는 들여다보기만 할 수 없어 꺼내 두고 기다린다)
            private (long Index, byte[] Pixels) _pending = (-1, null);
            private int _disposed;

            public Playback(PreviewPlayer owner, FfmpegTool tool, PreviewHelper helper, Project project, long from, long to, int width, int height, bool muted,
                ITextRasterizer rasterizer, Action<HelperException> helperFailed, IReadOnlyList<PrerenderedSpan> prerendered)
            {
                _owner = owner;
                _from = from;
                _to = to;
                _width = width;
                _height = height;
                _rate = project.Rate;
                Tool = tool;
                Project = project;
                Rasterizer = rasterizer;
                Prerendered = prerendered ?? Array.Empty<PrerenderedSpan>();
                try
                {
                    _video = PreviewRenderer.OpenVideo(tool, helper, project, from, to, width, height, rasterizer, helperFailed, Prerendered);
                    new Thread(ReadVideo) { IsBackground = true, Name = "preview-video" }.Start();
                    if (!muted)
                    {
                        try
                        {
                            _buffer = new BufferedWaveProvider(new WaveFormat(project.SampleRate, 16, 2)) { BufferDuration = TimeSpan.FromSeconds(4), DiscardOnBufferOverflow = false };
                            _waveOut = new WaveOutEvent { DesiredLatency = 150 };
                            _waveOut.Init(_buffer);
                            _audio = PreviewRenderer.OpenAudio(tool, helper, project, from, to, helperFailed);
                            new Thread(ReadAudio) { IsBackground = true, Name = "preview-audio" }.Start();
                        }
                        catch (Exception)
                        {
                            // 소리 장치가 없으면 소리 없이 재생한다(시계는 Stopwatch). 띄운 소리 흐름은 끝낸다
                            _audio?.Dispose();
                            _audio = null;
                            _waveOut?.Dispose();
                            _waveOut = null;
                            _buffer = null;
                        }
                    }
                    _timer = new DispatcherTimer(DispatcherPriority.Render, owner._dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
                    _timer.Tick += Tick;
                    _timer.Start();
                }
                catch (Exception)
                {
                    // 중간에 실패하면 이미 띄운 흐름을 끝낸다
                    Dispose();
                    throw;
                }
            }

            // 같은 재생을 캐시 없이 다시 시작할 때 쓴다(RetryWithoutPrerender)
            public FfmpegTool Tool { get; }
            public Project Project { get; }
            public ITextRasterizer Rasterizer { get; }
            public IReadOnlyList<PrerenderedSpan> Prerendered { get; }
            public long From { get { return _from; } }
            public long To { get { return _to; } }
            public int Width { get { return _width; } }
            public int Height { get { return _height; } }

            private void ReadVideo()
            {
                var size = _width * _height * 4;
                long index = 0;
                try
                {
                    var stream = _video.Input;
                    while (!_stop.IsCancellationRequested)
                    {
                        var pixels = new byte[size];
                        var read = 0;
                        while (read < size)
                        {
                            var n = stream.Read(pixels, read, size - read);
                            if (n <= 0)
                                break;
                            read += n;
                        }
                        if (read < size)
                            break;
                        _frames.Add((index++, pixels), _stop.Token);
                    }
                }
                catch (Exception)
                {
                    // 멈춤·프로세스 종료
                }
                _videoDone = true;
            }

            private void ReadAudio()
            {
                var chunk = new byte[16384];
                try
                {
                    var stream = _audio.Input;
                    while (!_stop.IsCancellationRequested)
                    {
                        // 버퍼가 넉넉하면 기다린다(앞서 읽어 두는 양 ≈ 2초)
                        while (_buffer.BufferedDuration > TimeSpan.FromSeconds(2) && !_stop.IsCancellationRequested)
                            Thread.Sleep(20);
                        var n = stream.Read(chunk, 0, chunk.Length);
                        if (n <= 0)
                            break;
                        _buffer.AddSamples(chunk, 0, n);
                    }
                }
                catch (Exception)
                {
                    // 멈춤·프로세스 종료
                }
            }

            /// <summary>지금까지 재생한 초(소리 시계 또는 Stopwatch).</summary>
            private double Elapsed()
            {
                if (_waveOut != null)
                {
                    try
                    {
                        return _waveOut.GetPosition() / (double)_buffer.WaveFormat.AverageBytesPerSecond;
                    }
                    catch (Exception)
                    {
                        return _clock.Elapsed.TotalSeconds;
                    }
                }
                return _clock.Elapsed.TotalSeconds;
            }

            private void Tick(object sender, EventArgs e)
            {
                try
                {
                    TickCore();
                }
                catch (Exception ex)
                {
                    // 디스패처 타이머에서 새는 예외는 Folderss로 간다 — 멈추고 알린다
                    _owner.Message?.Invoke("재생을 멈췄습니다: " + ex.Message);
                    _owner.Stop();
                }
            }

            private void TickCore()
            {
                if (_disposed != 0)
                    return;
                // 화면 흐름이 한 장도 못 내고 끝남(파일 없음·그래프 오류): 멈추고 이유를 알린다
                if (_videoDone && _frames.Count == 0 && _lastShown < 0)
                {
                    // 미리 그린 캐시를 읽었다면 캐시가 깨졌을 수 있다 — 캐시 없이(지금 방식) 다시
                    if (Prerendered.Count > 0)
                    {
                        _owner.RetryWithoutPrerender(this);
                        return;
                    }
                    var error = _video?.Error ?? "";
                    _owner.Message?.Invoke("재생하지 못했습니다" + (error.Length > 0 ? ": " + error : "."));
                    _owner.Stop();
                    return;
                }
                if (!_started)
                {
                    // 첫 화면과 소리가 조금 쌓이면 시작
                    var audioReady = _buffer == null || _buffer.BufferedDuration > TimeSpan.FromMilliseconds(250) || _videoDone;
                    if (_frames.Count == 0 && !_videoDone)
                        return;
                    if (!audioReady)
                        return;
                    _started = true;
                    _clock.Start();
                    _waveOut?.Play();
                }
                var target = (long)Math.Floor(_rate.ToFrames(Elapsed()) + 0.0001);
                // 지금 시각(target) 이하의 가장 늦은 프레임을 보여 준다 — 늦게 온 프레임은 건너뛰고,
                // 앞선 프레임은 들고 있다가 그 시각에 보여 준다(바로 보여 주면 FFmpeg가 실시간보다 빠를 때 화면이 소리보다 앞서 달린다)
                (long Index, byte[] Pixels) frame = (-1, null);
                while (true)
                {
                    (long Index, byte[] Pixels) next;
                    if (_pending.Pixels != null)
                        next = _pending;
                    else if (!_frames.TryTake(out next))
                        break;
                    if (next.Index > target)
                    {
                        _pending = next;
                        break;
                    }
                    _pending = (-1, null);
                    frame = next;
                    if (next.Index == target)
                        break;
                }
                if (frame.Pixels != null)
                {
                    _owner.Show(frame.Pixels, _width, _height, _from + frame.Index);
                    _lastShown = frame.Index;
                    _owner.Position?.Invoke(_from + frame.Index);
                }
                if (_videoDone && _frames.Count == 0 && _pending.Pixels == null && (_lastShown >= target || _from + target >= _to))
                {
                    _owner.Position?.Invoke(Math.Min(_to, _from + Math.Max(_lastShown, 0)));
                    _owner.Stop();
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                _timer?.Stop();
                _stop.Cancel();
                // 흐름을 먼저 끝내 읽는 스레드가 풀리게 한다(프로세스는 끝내고, 도우미는 멈추라고 알리고 파이프를 닫는다)
                _video?.Dispose();
                _audio?.Dispose();
                try
                {
                    _waveOut?.Stop();
                    _waveOut?.Dispose();
                }
                catch (Exception)
                {
                    // 장치 정리 실패는 무시
                }
            }
        }
    }
}
