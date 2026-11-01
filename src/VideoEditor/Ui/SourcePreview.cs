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
    /// - 미디어(<see cref="ShowMedia"/>): 미디어 패널에서 누른 사진은 파일을 바로 읽어 원본 그대로(FFmpeg 없어도), 동영상은 대표 프레임 한 장(썸네일과 같은 시각).
    /// - 클립(<see cref="ShowClip"/>): 타임라인에서 누른 클립 하나만 남긴 프로젝트 사본을 한 장 그린다(위치·크기·효과 그대로, 다른 트랙에 가리지 않음).
    ///   재생 헤드가 클립 안이면 그 시각, 밖이면 클립 가운데(들어오기·나가기 애니메이션을 피해). 그 클립을 고치면 다시 그린다.
    /// - 닫히는 때: <see cref="Close"/>(Esc·× 단추), 재생 시작, 재생 헤드 이동, 보이던 클립·미디어가 프로젝트에서 없어짐, 보이던 클립의 고르기가 풀림.
    /// 그리기는 UI 스레드 밖에서, 늦게 온 그림(다른 것을 고른 뒤)은 버린다.
    /// </summary>
    internal sealed class SourcePreview
    {
        // 클립을 고치는 동안(슬라이더 끌기) 장마다 그리지 않게 잠깐 모았다가 다시 그린다
        private static readonly TimeSpan RedrawDelay = TimeSpan.FromMilliseconds(150);

        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly PreviewPlayer _player;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _redraw;
        private CancellationTokenSource _job;

        public SourcePreview(EditorSession session, EditorServices services, PreviewPlayer player, Dispatcher dispatcher)
        {
            _session = session;
            _services = services;
            _player = player;
            _dispatcher = dispatcher;
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

        /// <summary>미리 보기 화면에 보일 그림(아직 그리는 중이거나 닫혔으면 null).</summary>
        public BitmapSource Frame { get; private set; }

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
            _redraw.Stop();
            var job = Begin();
            ClipId = null;
            MediaPath = media.Path;
            var size = media.Width > 0 && media.Height > 0 ? " · " + media.Width + "×" + media.Height : "";
            Label = "원본 · " + media.Name + size;
            var width = Math.Max(160, _player.OutputSize(_session.Project).Width);
            Load(job, () => media.Kind == MediaKind.Image ? ReadPicture(media.Path) : Decode(MediaPictures.Thumbnail(tool, media, Math.Min(1, media.DurationSeconds / 3), width, job.Token)));
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>클립 하나만 그려 보인다. 화면에 그릴 수 없는 클립(분리한 사운드·오디오)이거나 FFmpeg가 없으면 열지 않고 false.</summary>
        public bool ShowClip(Clip clip)
        {
            if (clip == null || !clip.IsVisual || clip.Duration <= 0 || _services.Tool == null)
                return false;
            Begin();
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
            if (!IsOpen)
                return;
            Label = null;
            Frame = null;
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
                Frame = picture;
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

        private static BitmapSource Decode(Task<byte[]> png)
        {
            return EditorServices.Decode(png.GetAwaiter().GetResult());
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
