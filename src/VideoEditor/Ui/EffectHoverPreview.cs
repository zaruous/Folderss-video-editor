using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 효과 칸 미리 보기: 동영상·사진 클립을 고른 채 [효과] 탭의 칸에 마우스를 잠깐(<see cref="Dwell"/>) 올려 두면, 그 효과를 고른 클립에 얹은 모습을 미리 보기 화면에 돌려 보인다.
    /// - 프로젝트 사본(스냅샷)의 그 클립에 효과를 기본값으로 하나 더 얹는다 — 같은 효과가 이미 있어도 [고른 클립에 넣기]처럼 하나 더(넣으면 보일 모습 그대로).
    ///   사람에게만은 꺼진 채라 인물 마스크를 만들지 않고 클립 전체에 그린다. 실행 취소·저장 안 함 표시·프로젝트 파일·미리 보기 플레이어의 그림은 건드리지 않는다.
    /// - 재생 헤드부터(재생 헤드가 클립 밖이면 클립 처음부터) <see cref="Seconds"/>초를 미리 보기 화면의 절반 크기로 그린다. 재생처럼 FFmpeg 하나가 흘려보내는 장을
    ///   건너뛰며 받아 약 11fps로 <see cref="MaxFrames"/>장까지 모은다(UI 스레드 밖 — UI 스레드는 다 그린 그림을 바꿔 끼우기만).
    ///   1080p 프로젝트(480×270으로)에서 첫 장까지 0.15~0.26초, 2초 전체 0.33~0.47초 — 장마다 FFmpeg를 띄우면 20장에 3~5.5초라 흘려 받는다.
    /// - 받는 대로 보이고, 다 받으면 처음부터 되풀이한다. 화면 위에 "미리 보기: 효과 이름"(플레이어 패널).
    /// - 마우스가 칸을 떠나거나 누를 때, 재생을 시작할 때, 고르기·프로젝트·재생 헤드가 바뀔 때, 탭을 바꿀 때 바로 원래 화면으로 — 그리던 FFmpeg는 끝내고 늦게 온 장은 버린다.
    ///   한 번에 하나만 그리고, 칸을 빠르게 훑고 지나가면(머무는 시간이 짧으면) 그리지 않는다. 재생 중·FFmpeg가 없을 때·글자 클립만 골랐을 때는 하지 않는다.
    /// - <see cref="SeenAfter"/> 넘게 보이면 그 효과를 본 것으로 적는다(효과 탭의 "새로" 표시가 사라짐).
    /// </summary>
    internal sealed class EffectHoverPreview
    {
        /// <summary>칸에 이만큼 머물러야 그리기 시작한다.</summary>
        public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(350);
        /// <summary>그리는 길이(초)와 모으는 장 수 상한.</summary>
        public const double Seconds = 2;
        public const int MaxFrames = 24;
        /// <summary>이만큼 보이면 본 효과로 적는다.</summary>
        public static readonly TimeSpan SeenAfter = TimeSpan.FromSeconds(1);
        // 받는 장의 빠르기(대략) — 프로젝트 프레임 레이트를 이 값에 가까운 정수로 나눠 장을 건너뛴다(30fps → 3장마다, 24fps → 2장마다)
        private const double TargetFps = 11;
        // 끝까지 보인 뒤 마지막 장에서 쉬는 틱 수(되풀이가 시작되는 줄 알게)
        private const int Pause = 4;

        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly PreviewPlayer _player;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _dwell;
        private readonly DispatcherTimer _loop;
        private readonly DispatcherTimer _seen;
        private VideoEffectDef _pending;
        private Job _job;
        private bool _disposed;

        public EffectHoverPreview(EditorSession session, EditorServices services, PreviewPlayer player, Dispatcher dispatcher)
        {
            _session = session;
            _services = services;
            _player = player;
            _dispatcher = dispatcher;
            _dwell = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Dwell };
            _dwell.Tick += (s, e) => Start();
            _loop = new DispatcherTimer(DispatcherPriority.Render, dispatcher);
            _loop.Tick += (s, e) => Advance();
            _seen = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = SeenAfter };
            _seen.Tick += (s, e) => MarkSeen();
            // 보이는 것이 프로젝트와 달라지는 때·재생을 시작할 때는 바로 원래 화면으로
            session.Changed += (s, e) => Cancel();
            session.SelectionChanged += (s, e) => Cancel();
            session.PlayheadChanged += (s, e) => Cancel();
            player.StateChanged += (s, e) =>
            {
                if (player.IsPlaying)
                    Cancel();
            };
        }

        /// <summary>미리 보기 화면에 보일 그림(효과를 얹은 장). 보이지 않을 때는 null.</summary>
        public BitmapSource Frame { get; private set; }

        /// <summary>보이는(또는 그리는) 효과.</summary>
        public VideoEffectDef Effect { get { return _job?.Def; } }

        /// <summary><see cref="Frame"/>이 바뀌었다(보이기 시작·다음 장·끝남) — UI 스레드에서.</summary>
        public event EventHandler Changed;

        /// <summary>마우스가 효과 칸에 들어왔다: 고른 동영상·사진 클립이 있으면 잠깐 머문 뒤 그린다.</summary>
        public void Hover(VideoEffectDef def)
        {
            if (def == null || _disposed || _pending == def || (_job != null && _job.Def == def))
                return;
            Cancel();
            if (_player.IsPlaying || _services.Tool == null || Target() == null)
                return;
            _pending = def;
            _dwell.Start();
        }

        /// <summary>마우스가 효과 칸을 떠났다: 그 효과를 기다리거나 보이는 중이면 원래 화면으로.</summary>
        public void Leave(VideoEffectDef def)
        {
            if (_pending == def || (_job != null && _job.Def == def))
                Cancel();
        }

        /// <summary>기다리기·그리기·보이기를 모두 그만두고 원래 화면으로(그리던 FFmpeg는 끝낸다).</summary>
        public void Cancel()
        {
            _dwell.Stop();
            _pending = null;
            _loop.Stop();
            _seen.Stop();
            var job = _job;
            _job = null;
            if (job == null)
                return;
            // FFmpeg 끝내기(프로세스 트리 훑기)는 UI 스레드 밖에서 — 늦게 오는 장은 job이 바뀌어 버려진다
            ThreadPool.QueueUserWorkItem(_ => job.Cancel.Cancel());
            if (Frame == null)
                return;
            Frame = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _disposed = true;
            Cancel();
        }

        /// <summary>효과를 얹어 볼 클립: 속성 패널이 보이는 클립, 아니면 고른 클립 가운데 처음 동영상·사진 클립.</summary>
        private Clip Target()
        {
            var primary = _session.PrimaryClip;
            return VideoEffects.CanHave(primary) ? primary : _session.SelectedClips.FirstOrDefault(VideoEffects.CanHave);
        }

        private void Start()
        {
            _dwell.Stop();
            var def = _pending;
            _pending = null;
            var tool = _services.Tool;
            var target = Target();
            if (def == null || _disposed || tool == null || target == null || _player.IsPlaying)
                return;
            Project copy;
            try
            {
                copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(_session.Project));
            }
            catch (Exception)
            {
                return;
            }
            var clip = copy.FindClip(target.Id);
            if (clip == null || clip.Duration <= 0)
                return;
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(VideoEffects.Create(def));
            var rate = copy.Rate;
            var playhead = _session.Playhead;
            var from = playhead >= clip.Start && playhead < clip.End ? playhead : clip.Start;
            var to = Math.Min(clip.End, from + Math.Max(1, rate.ToFrames(Seconds)));
            var step = Math.Max(1, (int)Math.Round(rate.Fps / TargetFps));
            var size = _player.OutputSize(copy);
            var width = Math.Max(16, size.Width / 4 * 2);
            var height = Math.Max(16, size.Height / 4 * 2);
            var job = _job = new Job(def, TimeSpan.FromSeconds(step / rate.Fps));
            var rasterizer = _services.TextRasterizer;
            Task.Run(() => Render(job, tool, copy, from, to, width, height, step, rasterizer))
                .ContinueWith(t => _dispatcher.BeginInvoke(new Action(() => Finished(job))), TaskScheduler.Default);
        }

        /// <summary>UI 스레드 밖: FFmpeg 하나로 [from, to)를 BGRA로 흘려 받아 step장마다 하나를 얼린 그림으로 넘긴다(취소하면 FFmpeg를 끝낸다).</summary>
        private void Render(Job job, FfmpegTool tool, Project copy, long from, long to, int width, int height, int step, ITextRasterizer rasterizer)
        {
            List<string> temp = null;
            Process process = null;
            try
            {
                if (job.Cancel.IsCancellationRequested)
                    return;
                process = PreviewRenderer.StartVideo(tool, copy, from, to, width, height, out temp, rasterizer);
                var started = process;
                using (job.Cancel.Token.Register(() => FfmpegRunner.Kill(started)))
                {
                    // 오류 출력은 읽어 버린다(파이프가 차서 멈추지 않게)
                    process.BeginErrorReadLine();
                    var stream = process.StandardOutput.BaseStream;
                    var buffer = new byte[width * height * 4];
                    var kept = 0;
                    for (var index = 0; kept < MaxFrames && !job.Cancel.IsCancellationRequested; index++)
                    {
                        if (!Read(stream, buffer))
                            break;
                        if (index % step != 0)
                            continue;
                        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
                        frame.Freeze();
                        kept++;
                        _dispatcher.BeginInvoke(new Action(() => Add(job, frame)));
                    }
                }
            }
            catch (Exception)
            {
                // 미리 보기일 뿐이다 — 그리지 못하면 원래 화면 그대로
            }
            finally
            {
                if (process != null)
                {
                    FfmpegRunner.Kill(process);
                    process.Dispose();
                }
                foreach (var file in temp ?? new List<string>())
                    RenderCommand.TryDelete(file);
            }
        }

        /// <summary>한 장을 다 읽는다(흐름이 끝나 모자라면 false).</summary>
        private static bool Read(Stream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        private void Add(Job job, BitmapSource frame)
        {
            if (job != _job || _disposed)
                return;
            job.Frames.Add(frame);
            if (job.Frames.Count > 1)
                return;
            // 첫 장: 보이기 시작 — 다음 장은 받는 빠르기대로, 1초 넘게 보이면 본 효과로
            job.Tick = 0;
            Frame = frame;
            _loop.Interval = job.Interval;
            _loop.Start();
            _seen.Start();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Advance()
        {
            var job = _job;
            if (job == null || job.Frames.Count == 0)
            {
                _loop.Stop();
                return;
            }
            var count = job.Frames.Count;
            // 다 받았으면 끝에서 잠깐 쉬고 처음부터, 받는 중이면 받은 데까지(모자라면 마지막 장에서 기다림)
            job.Tick = job.Done ? (job.Tick + 1) % (count + Pause) : Math.Min(job.Tick + 1, count - 1);
            var frame = job.Frames[Math.Min(job.Tick, count - 1)];
            if (ReferenceEquals(frame, Frame))
                return;
            Frame = frame;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Finished(Job job)
        {
            if (job != _job)
                return;
            job.Done = true;
            // 한 장도 못 그렸다(파일 없음·그래프 오류) — 원래 화면 그대로
            if (job.Frames.Count == 0)
                Cancel();
        }

        private void MarkSeen()
        {
            _seen.Stop();
            var job = _job;
            if (job != null && job.Frames.Count > 0)
                _services.MarkEffectsSeen(new[] { job.Def.Id });
        }

        /// <summary>그리기 한 번: 효과, 취소, 받은 장, 보이는 차례.</summary>
        private sealed class Job
        {
            public Job(VideoEffectDef def, TimeSpan interval)
            {
                Def = def;
                Interval = interval;
            }

            public VideoEffectDef Def { get; }
            public TimeSpan Interval { get; }
            public CancellationTokenSource Cancel { get; } = new CancellationTokenSource();
            public List<BitmapSource> Frames { get; } = new List<BitmapSource>();
            public bool Done { get; set; }
            public int Tick { get; set; }
        }
    }
}
