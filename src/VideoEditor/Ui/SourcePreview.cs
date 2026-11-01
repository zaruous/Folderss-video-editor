using System;
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
    /// 원본 보기: 미리 보기 화면을 잠시 고른 것 하나의 그림으로 바꾼다(프로그램 화면 = 재생 헤드의 합성 화면은 그대로 두고 덮기만).
    /// - 미디어(<see cref="ShowMedia"/>): 미디어 패널에서 누른 사진은 파일을 바로 읽어 원본 그대로(FFmpeg 없어도).
    ///   동영상은 그 미디어 하나만 담은 원본 프로젝트를 원본 전용 플레이어(<see cref="PreviewPlayer"/> 하나 더)로 그린다 — 처음은 대표 프레임(썸네일과 같은 시각),
    ///   원본 위치(<see cref="Seek"/>)·재생(<see cref="TogglePlay"/>)·원본 In/Out(<see cref="MarkIn"/>·<see cref="MarkOut"/>)은 타임라인 재생 헤드와 따로 움직인다.
    ///   In/Out을 정하면 그 미디어를 타임라인에 놓는 길(<see cref="RangeFor"/>)이 그 구간만 놓는다.
    /// - 클립(<see cref="ShowClip"/>): 타임라인에서 누른 클립 하나만 남긴 프로젝트 사본을 한 장 그린다(위치·크기·효과 그대로, 다른 트랙에 가리지 않음).
    ///   재생 헤드가 클립 안이면 그 시각, 밖이면 클립 가운데(들어오기·나가기 애니메이션을 피해). 그 클립을 고치면 다시 그린다.
    /// - 닫히는 때: <see cref="Close"/>(Esc·× 단추), 타임라인 재생 시작, 재생 헤드 이동, 보이던 클립·미디어가 프로젝트에서 없어짐, 보이던 클립의 고르기가 풀림.
    ///   원본 위치를 옮기거나 원본을 재생하는 것은 재생 헤드를 건드리지 않아 닫히지 않는다.
    /// 그리기는 UI 스레드 밖에서, 늦게 온 그림(다른 것을 고른 뒤)은 버린다. 원본 위치를 빨리 옮기면 원본 플레이어가 잠깐 모아 마지막 것만 그린다.
    /// </summary>
    internal sealed class SourcePreview : IDisposable
    {
        // 클립을 고치는 동안(슬라이더 끌기) 장마다 그리지 않게 잠깐 모았다가 다시 그린다
        private static readonly TimeSpan RedrawDelay = TimeSpan.FromMilliseconds(150);

        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly PreviewPlayer _player;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _redraw;
        private CancellationTokenSource _job;
        // 동영상 미디어 보기: 원본 전용 플레이어(타임라인 플레이어와 따로 — 재생 헤드를 옮기지 않는다)와 그 미디어 하나만 담은 원본 프로젝트
        private readonly PreviewPlayer _sourcePlayer;
        private Project _sourceProject;
        // 이번 원본의 첫 장이 왔는지(그 전에는 원본 플레이어 비트맵에 지난 원본 그림이 남아 있을 수 있다)
        private bool _sourceShown;
        // 사진·클립 보기의 그림
        private BitmapSource _picture;

        public SourcePreview(EditorSession session, EditorServices services, PreviewPlayer player, Dispatcher dispatcher)
        {
            _session = session;
            _services = services;
            _player = player;
            _dispatcher = dispatcher;
            _sourcePlayer = new PreviewPlayer(services, dispatcher);
            _sourcePlayer.FrameShown += (s, e) =>
            {
                if (_sourceProject == null)
                    return;
                var first = !_sourceShown;
                _sourceShown = true;
                if (first)
                    Changed?.Invoke(this, EventArgs.Empty);
                TransportChanged?.Invoke(this, EventArgs.Empty);
            };
            _sourcePlayer.BitmapChanged += (s, e) =>
            {
                if (_sourceProject != null && _sourceShown)
                    Changed?.Invoke(this, EventArgs.Empty);
            };
            _sourcePlayer.Position += frame =>
            {
                if (_sourceProject == null)
                    return;
                Position = Math.Max(0, Math.Min(Length - 1, frame));
                TransportChanged?.Invoke(this, EventArgs.Empty);
            };
            _sourcePlayer.StateChanged += (s, e) => TransportChanged?.Invoke(this, EventArgs.Empty);
            _sourcePlayer.Message += text =>
            {
                Failed?.Invoke(text);
                // 첫 장도 그리지 못했으면 "불러오는 중"이 남지 않게 닫는다
                if (_sourceProject != null && !_sourceShown)
                    Close();
            };
            _redraw = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = RedrawDelay };
            _redraw.Tick += (s, e) =>
            {
                _redraw.Stop();
                if (ClipId != null)
                    RenderClip();
            };
            session.PlayheadChanged += (s, e) => Close();
            player.StateChanged += (s, e) =>
            {
                if (player.IsPlaying)
                    Close();
            };
            session.Changed += (s, e) => ProjectChanged();
            session.SelectionChanged += (s, e) =>
            {
                if (ClipId != null && !session.SelectedIds.Contains(ClipId))
                    Close();
            };
        }

        /// <summary>원본 보기가 열려 있다(그림이 아직 오지 않았어도).</summary>
        public bool IsOpen { get { return Label != null; } }

        /// <summary>미리 보기 화면에 보일 그림(아직 그리는 중이거나 닫혔으면 null). 동영상 미디어는 원본 플레이어의 비트맵(옮기거나 재생하면 그 자리에서 바뀜).</summary>
        public BitmapSource Frame
        {
            get
            {
                if (_sourceProject != null)
                    return _sourceShown ? _sourcePlayer.Bitmap : null;
                return _picture;
            }
        }

        /// <summary>동영상 미디어를 보고 있다(원본 위치·재생·In/Out을 쓸 수 있음).</summary>
        public bool IsVideo { get { return _sourceProject != null; } }

        /// <summary>원본 길이(프레임, 프로젝트 레이트 — 클립의 SourceIn/SourceOut과 같은 단위). 동영상 미디어가 아니면 0.</summary>
        public long Length { get { return _sourceProject?.Duration ?? 0; } }

        /// <summary>원본 위치(프레임, 0 ~ <see cref="Length"/>-1).</summary>
        public long Position { get; private set; }

        /// <summary>원본 In 점(이 프레임부터 넣는다). 없으면 처음부터.</summary>
        public long? InPoint { get; private set; }

        /// <summary>원본 Out 점(이 프레임까지 넣는다 — 이 프레임을 포함). 없으면 끝까지.</summary>
        public long? OutPoint { get; private set; }

        /// <summary>원본을 재생하고 있다.</summary>
        public bool IsPlaying { get { return _sourceProject != null && _sourcePlayer.IsPlaying; } }

        /// <summary>원본 화면에 마지막으로 그린 원본 프레임(아직 없으면 -1).</summary>
        public long ShownFrame { get { return _sourceProject != null && _sourceShown ? _sourcePlayer.ShownFrame : -1; } }

        /// <summary>프로젝트 레이트(원본 위치·In/Out의 타임코드).</summary>
        public FrameRate Rate { get { return _session.Project.Rate; } }

        /// <summary>원본 위치·재생 상태·In/Out이 바뀌었다(UI 스레드에서).</summary>
        public event EventHandler TransportChanged;

        /// <summary>화면 위 표시("원본 · 이름 · …"). 닫혔으면 null.</summary>
        public string Label { get; private set; }

        /// <summary>보이는 클립 Id(클립 보기일 때).</summary>
        public string ClipId { get; private set; }

        /// <summary>보이는 미디어 경로(미디어 보기일 때).</summary>
        public string MediaPath { get; private set; }

        /// <summary><see cref="Frame"/>·<see cref="Label"/>이 바뀌었다(UI 스레드에서).</summary>
        public event EventHandler Changed;

        /// <summary>미디어 하나를 원본 그대로 보인다. 오디오는 그림이 없어 열지 않고 false.</summary>
        public bool ShowMedia(MediaInfo media)
        {
            if (media == null || media.Kind == MediaKind.Audio)
                return false;
            var tool = _services.Tool;
            if (media.Kind == MediaKind.Video && tool == null)
                return false;
            // 이미 보고 있는 동영상을 다시 누름(두 번 누르기의 첫 누름 등): 위치·In/Out·재생을 그대로 둔다
            if (_sourceProject != null && string.Equals(MediaPath, media.Path, StringComparison.OrdinalIgnoreCase))
                return true;
            _redraw.Stop();
            var job = Begin();
            StopSource();
            ClipId = null;
            MediaPath = media.Path;
            _picture = null;
            var size = media.Width > 0 && media.Height > 0 ? " · " + media.Width + "×" + media.Height : "";
            Label = "원본 · " + media.Name + size;
            if (media.Kind == MediaKind.Image)
            {
                Load(job, () => ReadPicture(media.Path));
            }
            else
            {
                OpenVideo(media);
                Position = Math.Min(Length - 1, Rate.ToFrames(Math.Min(1, media.DurationSeconds / 3)));
                _sourcePlayer.RequestFrame(_sourceProject, Position);
                TransportChanged?.Invoke(this, EventArgs.Empty);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// 동영상 미디어 하나만 담은 원본 프로젝트를 만든다: 크기는 원본 그대로(가로세로 비율 그대로 보이게), 레이트·샘플레이트는 이 프로젝트 것
        /// (원본 위치가 클립의 SourceIn/SourceOut과 같은 단위). 클립은 <see cref="TimelineEdits.CreateClip"/> — 타임라인에 놓는 클립과 같은 길이.
        /// </summary>
        private void OpenVideo(MediaInfo media)
        {
            var project = Project.CreateDefault();
            var main = _session.Project;
            project.Rate = main.Rate;
            project.SampleRate = main.SampleRate;
            project.Width = media.Width >= 16 ? media.Width / 2 * 2 : main.Width;
            project.Height = media.Height >= 16 ? media.Height / 2 * 2 : main.Height;
            project.Media.Add(media);
            project.MainTrack.Clips.Add(TimelineEdits.CreateClip(media, project.Rate, 0, 1));
            _sourceProject = project;
            _sourceShown = false;
            InPoint = OutPoint = null;
            _sourcePlayer.Scale = _player.Scale;
        }

        /// <summary>원본 위치를 옮겨 그 프레임을 보인다(재생 중이면 멈춤). 재생 헤드는 그대로.</summary>
        public void Seek(long frame)
        {
            if (_sourceProject == null)
                return;
            frame = Math.Max(0, Math.Min(Length - 1, frame));
            if (_sourcePlayer.IsPlaying)
                _sourcePlayer.Stop();
            Position = frame;
            _sourcePlayer.RequestFrame(_sourceProject, frame);
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>원본을 재생하거나 멈춘다(소리는 플레이어의 소리 끄기를 따른다). 끝에 있으면 처음부터.</summary>
        public void TogglePlay()
        {
            if (_sourceProject == null)
                return;
            if (_sourcePlayer.IsPlaying)
            {
                _sourcePlayer.Stop();
                return;
            }
            _sourcePlayer.Muted = _player.Muted;
            _sourcePlayer.Scale = _player.Scale;
            var from = Position >= Length - 1 ? 0 : Position;
            Position = from;
            _sourcePlayer.Play(_sourceProject, from);
        }

        /// <summary>원본 In 점을 지금 원본 위치로(Out이 그 앞이면 지운다).</summary>
        public void MarkIn()
        {
            if (_sourceProject == null)
                return;
            InPoint = Position;
            if (OutPoint < Position)
                OutPoint = null;
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>원본 Out 점을 지금 원본 위치로 — 이 프레임까지 넣는다(In이 그 뒤면 지운다).</summary>
        public void MarkOut()
        {
            if (_sourceProject == null)
                return;
            OutPoint = Position;
            if (InPoint > Position)
                InPoint = null;
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 이 미디어를 타임라인에 놓을 원본 구간 [In, Out)(프레임, rate 단위): 원본 보기가 그 동영상을 보고 있고 In 또는 Out을 정했을 때만, 아니면 null(전체).
        /// </summary>
        public (long In, long Out, FrameRate Rate)? RangeFor(MediaInfo media)
        {
            if (_sourceProject == null || media == null || (!InPoint.HasValue && !OutPoint.HasValue)
                || !string.Equals(MediaPath, media.Path, StringComparison.OrdinalIgnoreCase))
                return null;
            var from = InPoint ?? 0;
            var to = (OutPoint ?? Length - 1) + 1;
            return (from, Math.Max(from + 1, to), _sourceProject.Rate);
        }

        /// <summary>원본 재생을 멈추고 원본 프로젝트를 놓는다(사진·클립 보기로 바꾸거나 닫을 때).</summary>
        private void StopSource()
        {
            if (_sourceProject == null)
                return;
            _sourcePlayer.Stop();
            _sourceProject = null;
            _sourceShown = false;
            InPoint = OutPoint = null;
            Position = 0;
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Close();
            _sourcePlayer.Dispose();
        }

        /// <summary>클립 하나만 그려 보인다. 화면에 그릴 수 없는 클립(분리한 사운드·오디오)이거나 FFmpeg가 없으면 열지 않고 false.</summary>
        public bool ShowClip(Clip clip)
        {
            if (clip == null || !clip.IsVisual || clip.Duration <= 0 || _services.Tool == null)
                return false;
            Begin();
            StopSource();
            MediaPath = null;
            ClipId = clip.Id;
            Label = "원본 · " + clip.Name;
            RenderClip();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>원본 보기를 닫고 프로그램 화면으로.</summary>
        public void Close()
        {
            _redraw.Stop();
            Cancel();
            StopSource();
            if (!IsOpen)
                return;
            Label = null;
            _picture = null;
            ClipId = MediaPath = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void ProjectChanged()
        {
            if (!IsOpen)
                return;
            if (ClipId != null)
            {
                if (_session.Project.FindClip(ClipId) == null)
                    Close();
                else
                    _redraw.Start();
            }
            else if (MediaPath != null && !_session.Project.Media.Any(m => string.Equals(m.Path, MediaPath, StringComparison.OrdinalIgnoreCase)))
            {
                Close();
            }
            else if (_sourceProject != null && !_sourceProject.Rate.Equals(_session.Project.Rate))
            {
                // 프로젝트 레이트가 바뀌면 원본 위치·In/Out의 단위가 달라진다
                Close();
            }
        }

        /// <summary>보이는 클립을 다시 그린다: 그 클립만 남긴 사본에서 한 장.</summary>
        private void RenderClip()
        {
            var tool = _services.Tool;
            var source = _session.Project.FindClip(ClipId);
            if (tool == null || source == null)
            {
                Close();
                return;
            }
            var job = Begin();
            Project copy;
            try
            {
                copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(_session.Project));
            }
            catch (Exception)
            {
                return;
            }
            foreach (var track in copy.AllTracks)
            {
                track.Clips.RemoveAll(c => c.Id != ClipId);
                // 숨긴 트랙의 클립도 고르면 보인다
                if (track.Clips.Count > 0)
                    track.Hidden = false;
            }
            var clip = copy.FindClip(ClipId);
            var playhead = _session.Playhead;
            var frame = clip.Contains(playhead) ? playhead : clip.Start + clip.Duration / 2;
            Label = "원본 · " + clip.Name + " · " + copy.Rate.Timecode(frame);
            var size = _player.OutputSize(copy);
            var rasterizer = _services.TextRasterizer;
            Load(job, () => RenderFrame(tool, copy, frame, size.Width, size.Height, rasterizer, job.Token));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private CancellationTokenSource Begin()
        {
            Cancel();
            _job = new CancellationTokenSource();
            return _job;
        }

        private void Cancel()
        {
            var job = _job;
            _job = null;
            if (job != null)
                ThreadPool.QueueUserWorkItem(_ => job.Cancel());
        }

        /// <summary>UI 스레드 밖에서 그림을 얻어, 그동안 다른 것을 고르지 않았으면 보인다(얻지 못하면 상태 줄에 알리고 닫는다).</summary>
        private void Load(CancellationTokenSource job, Func<BitmapSource> load)
        {
            Task.Run(load).ContinueWith(t => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (job != _job)
                    return;
                var picture = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (picture == null)
                {
                    Failed?.Invoke("원본을 그리지 못했습니다: " + (Label ?? ""));
                    Close();
                    return;
                }
                _picture = picture;
                Changed?.Invoke(this, EventArgs.Empty);
            })), TaskScheduler.Default);
        }

        /// <summary>그리지 못했을 때의 안내(상태 줄).</summary>
        public event Action<string> Failed;

        /// <summary>사진 파일을 바로 읽는다(아주 크면 3840px 폭으로 줄여 읽음).</summary>
        private static BitmapSource ReadPicture(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource picture = decoder.Frames[0];
                if (picture.PixelWidth > 3840)
                {
                    var scale = 3840.0 / picture.PixelWidth;
                    picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
                }
                picture.Freeze();
                return picture;
            }
        }

        /// <summary>사본의 frame 한 장(BGRA)을 흐름 하나로 받는다.</summary>
        private BitmapSource RenderFrame(FfmpegTool tool, Project copy, long frame, int width, int height, ITextRasterizer rasterizer, CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested)
                return null;
            var helper = _services.HelperFor(tool);
            using (var playback = PreviewRenderer.OpenVideo(tool, helper, copy, frame, frame + 1, width, height, rasterizer, ex => _services.ReportHelperFailure(helper, ex)))
            using (cancel.Register(() => playback.Dispose()))
            {
                var buffer = new byte[width * height * 4];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = playback.Input.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                        return null;
                    read += n;
                }
                var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
                picture.Freeze();
                return picture;
            }
        }
    }
}
