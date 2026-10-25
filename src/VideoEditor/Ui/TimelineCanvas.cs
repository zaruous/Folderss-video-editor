using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>타임라인의 한 줄(트랙)과 화면 위치.</summary>
    internal sealed class TimelineRow
    {
        public Track Track { get; set; }
        public double Top { get; set; }
        public double Height { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// 타임라인 그리기·조작(눈금자 + 트랙 줄 + 클립 + 재생 헤드). 위(번호 큰) 동영상 트랙이 위에, 오디오 트랙은 아래에 그린다.
    /// 마우스: 눈금자 끌기 = 재생 헤드 이동, 클립 끌기 = 옮기기(다른 트랙으로도), 가장자리 끌기 = 다듬기(붙기 켜면 편집점·재생 헤드·마커에 붙음),
    /// 휠 = 가로 이동, Ctrl+휠 = 확대/축소, Shift+휠 = 세로 이동. 파일·미디어를 끌어 놓으면 MediaDropped.
    /// 링크된 영상·사운드(사운드 분리)는 함께 옮기고 다듬는다 — 끌기 시작할 때 Alt면 그 한 번만 그 클립만.
    /// 조작은 Pointer* 메서드로 모여 있어 화면 시험이 마우스 없이 부를 수 있다.
    /// </summary>
    internal sealed class TimelineCanvas : FrameworkElement
    {
        public const double RulerHeight = 28;
        public const string MediaDataFormat = "VideoEditor.MediaPaths";
        /// <summary>효과 탭에서 끄는 전환 — "종류|초".</summary>
        public const string TransitionDataFormat = "VideoEditor.Transition";
        private const double EdgeGrip = 7;
        private const double DragThreshold = 3;
        private const double SnapPixels = 8;

        public static readonly DependencyProperty PanelBrushProperty = Brush("PanelBrush");
        public static readonly DependencyProperty RowBrushProperty = Brush("RowBrush");
        public static readonly DependencyProperty RulerBrushProperty = Brush("RulerBrush");
        public static readonly DependencyProperty TextBrushProperty = Brush("TextBrush");
        public static readonly DependencyProperty SubTextBrushProperty = Brush("SubTextBrush");
        public static readonly DependencyProperty LineBrushProperty = Brush("LineBrush");
        public static readonly DependencyProperty AccentBrushProperty = Brush("AccentBrush");

        private static DependencyProperty Brush(string name)
        {
            return DependencyProperty.Register(name, typeof(Brush), typeof(TimelineCanvas),
                new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
        }

        private static readonly Brush VideoBrush = Frozen(Color.FromRgb(0x2F, 0x6F, 0xD8));
        private static readonly Brush ImageBrush = Frozen(Color.FromRgb(0x1E, 0x9E, 0x76));
        private static readonly Brush TextClipBrush = Frozen(Color.FromRgb(0x8E, 0x4D, 0xD6));
        // 자막 구역: 큐는 호박색, 줄은 옅은 호박색(테마와 상관없이 보이게 반투명)
        public static readonly Color SubtitleColor = Color.FromRgb(0xC2, 0x7C, 0x0E);
        private static readonly Brush SubtitleClipBrush = Frozen(SubtitleColor);
        private static readonly Brush SubtitleRowBrush = Frozen(Color.FromArgb(0x1C, 0xF2, 0xA9, 0x00));
        private static readonly Brush AudioBrush = Frozen(Color.FromRgb(0x14, 0x8F, 0x8A));
        private static readonly Brush PlayheadBrush = Frozen(Color.FromRgb(0xEF, 0x44, 0x44));
        private static readonly Brush SnapBrush = Frozen(Color.FromRgb(0xFA, 0xCC, 0x15));
        private static readonly Brush GhostBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0x50, 0x00, 0x00, 0x00));
        private static readonly Brush InOutBrush = Frozen(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
        private static readonly Brush WaveBrush = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        private static readonly Pen SelectedPen = FrozenPen(Brushes.White, 2);
        private static readonly Pen ClipEdgePen = FrozenPen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1);
        private static readonly Pen PlayheadPen = FrozenPen(PlayheadBrush, 1.5);
        private static readonly Pen SnapPen = FrozenPen(SnapBrush, 1);
        private static readonly Pen TransitionPen = FrozenPen(Brushes.White, 1.5);
        // 전환 표시: 두 클립이 만나는 자리에 걸친 노란 배지(⇄) + 전환이 일어나는 구간의 반투명 띠
        private static readonly Brush TransitionBadgeBrush = Frozen(Color.FromRgb(0xFA, 0xCC, 0x15));
        private static readonly Brush TransitionGlyphBrush = Frozen(Color.FromRgb(0x1F, 0x29, 0x37));
        private static readonly Brush TransitionRegionBrush = Frozen(Color.FromArgb(0x55, 0xFA, 0xCC, 0x15));
        private static readonly Pen TransitionBadgePen = FrozenPen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 1);
        private const double BadgeWidth = 26;
        private const double BadgeHeight = 18;
        // 사운드 분리: 짝과 시간이 어긋나면 클립 오른쪽 위에 빨간 배지("+3f")
        private static readonly Brush OffsetBadgeBrush = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Brush brush, double width)
        {
            var pen = new Pen(brush, width);
            pen.Freeze();
            return pen;
        }

        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly HashSet<string> _pendingPictures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private double _pixelsPerFrame = 2;
        private double _scrollX;
        private double _scrollY;

        // 끌기 상태
        private enum DragKind { None, Scrub, Move, TrimStart, TrimEnd }
        private DragKind _drag;
        private bool _dragStarted;
        private Point _downPoint;
        private string _dragClipId;
        private long _dragOriginStart;
        private long _dragDuration;
        private Track _ghostTrack;
        private long _ghostStart;
        private long _ghostEnd;
        private long _snapLine = -1;
        // 링크된 짝(사운드 분리): 함께 움직일 짝, 끌기 시작할 때 Alt면 그 한 번만 따로
        private string _dragPartnerId;
        private bool _dragIndependent;
        private Dictionary<string, Clip> _partners = new Dictionary<string, Clip>();
        // 끌어 놓기 표시
        private Track _dropTrack;
        private long _dropFrame = -1;

        public TimelineCanvas(EditorSession session, EditorServices services)
        {
            _session = session;
            _services = services;
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            AllowDrop = true;
            Snapping = true;
            SetResourceReference(PanelBrushProperty, "PanelBackground");
            SetResourceReference(RowBrushProperty, "SurfaceBackground");
            SetResourceReference(RulerBrushProperty, "ControlBackground");
            SetResourceReference(TextBrushProperty, "PrimaryText");
            SetResourceReference(SubTextBrushProperty, "SecondaryText");
            SetResourceReference(LineBrushProperty, "BorderBrush");
            SetResourceReference(AccentBrushProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(this, "TimelineCanvas");
            AutomationProperties.SetName(this, "타임라인");
            session.Changed += (s, e) => Refresh();
            session.SelectionChanged += (s, e) => Refresh();
            session.PlayheadChanged += (s, e) => InvalidateVisual();
        }

        /// <summary>확대/축소·스크롤이 바뀜(스크롤 막대·트랙 머리글이 따라 움직인다).</summary>
        public event EventHandler ViewChanged;
        /// <summary>상태 줄에 보일 글(편집 거부 이유 등).</summary>
        public event Action<string> StatusMessage;
        /// <summary>파일·미디어를 끌어 놓음: 경로들, 놓은 트랙(줄 아래 빈 곳이면 null), 프레임.</summary>
        public event Action<string[], Track, long> MediaDropped;
        /// <summary>효과 탭의 전환을 클립에 놓음 — ("종류|초", 클립 Id).</summary>
        public event Action<string, string> TransitionDropped;
        /// <summary>오른쪽 클릭: 클립(없으면 null), 트랙(없으면 null), 프레임. 받은 쪽이 메뉴를 연다.</summary>
        public event Action<Clip, Track, long> ContextRequested;

        public bool Snapping { get; set; }

        public double PixelsPerFrame
        {
            get { return _pixelsPerFrame; }
            set
            {
                value = Math.Max(0.01, Math.Min(60, value));
                if (Math.Abs(value - _pixelsPerFrame) < 1e-9)
                    return;
                _pixelsPerFrame = value;
                ClampScroll();
                Changed();
            }
        }

        public double ScrollX
        {
            get { return _scrollX; }
            set
            {
                _scrollX = value;
                ClampScroll();
                Changed();
            }
        }

        public double ScrollY
        {
            get { return _scrollY; }
            set
            {
                _scrollY = value;
                ClampScroll();
                Changed();
            }
        }

        /// <summary>가로로 스크롤할 수 있는 전체 폭(픽셀) — 끝 뒤로 여유를 둔다.</summary>
        public double ContentWidth
        {
            get { return (_session.Project.Duration + _session.Project.Rate.ToFrames(10)) * _pixelsPerFrame; }
        }

        public double ContentHeight
        {
            get { return _session.Project.AllTracks.Sum(t => t.Height); }
        }

        private void Changed()
        {
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Refresh()
        {
            ClampScroll();
            Changed();
            // 화면 낭독기가 보는 클립 목록을 새로(비동기 — 편집을 막지 않게)
            UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
        }

        private void ClampScroll()
        {
            var maxX = Math.Max(0, ContentWidth - ActualWidth + 40);
            _scrollX = Math.Max(0, Math.Min(_scrollX, maxX));
            var maxY = Math.Max(0, ContentHeight - (ActualHeight - RulerHeight));
            _scrollY = Math.Max(0, Math.Min(_scrollY, maxY));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ClampScroll();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---------- 좌표 ----------

        public double FrameToX(long frame)
        {
            return frame * _pixelsPerFrame - _scrollX;
        }

        public long XToFrame(double x)
        {
            return Math.Max(0, (long)Math.Round((x + _scrollX) / _pixelsPerFrame));
        }

        /// <summary>그릴 줄: 동영상 트랙(위 번호부터) → 오디오 트랙.</summary>
        public List<TimelineRow> Rows()
        {
            var rows = new List<TimelineRow>();
            var top = RulerHeight - _scrollY;
            var project = _session.Project;
            for (var i = project.VideoTracks.Count - 1; i >= 0; i--)
            {
                var track = project.VideoTracks[i];
                rows.Add(new TimelineRow { Track = track, Top = top, Height = track.Height, Label = track.Name });
                top += track.Height;
            }
            foreach (var track in project.AudioTracks)
            {
                rows.Add(new TimelineRow { Track = track, Top = top, Height = track.Height, Label = track.Name });
                top += track.Height;
            }
            return rows;
        }

        public TimelineRow RowAt(double y)
        {
            return y < RulerHeight ? null : Rows().FirstOrDefault(r => y >= r.Top && y < r.Top + r.Height);
        }

        /// <summary>클립의 화면 사각형(캔버스 좌표). 없으면 Rect.Empty.</summary>
        public Rect ClipBounds(string clipId)
        {
            foreach (var row in Rows())
            {
                var clip = row.Track.Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip != null)
                    return ClipRect(row, clip.Start, clip.End);
            }
            return Rect.Empty;
        }

        private Rect ClipRect(TimelineRow row, long start, long end)
        {
            var x = FrameToX(start);
            var width = Math.Max(2, (end - start) * _pixelsPerFrame);
            return new Rect(x, row.Top + 3, width, Math.Max(4, row.Height - 6));
        }

        private enum Zone { Body, StartEdge, EndEdge }

        private Clip HitClip(Point point, out TimelineRow hitRow, out Zone zone)
        {
            zone = Zone.Body;
            hitRow = RowAt(point.Y);
            if (hitRow == null)
                return null;
            foreach (var clip in hitRow.Track.Clips)
            {
                var rect = ClipRect(hitRow, clip.Start, clip.End);
                if (point.X < rect.Left || point.X > rect.Right)
                    continue;
                var grip = Math.Min(EdgeGrip, rect.Width / 4);
                if (point.X - rect.Left <= grip)
                    zone = Zone.StartEdge;
                else if (rect.Right - point.X <= grip)
                    zone = Zone.EndEdge;
                return clip;
            }
            return null;
        }

        // ---------- 확대·스크롤 ----------

        /// <summary>anchorX(캔버스 좌표) 아래 프레임이 그대로 있게 확대/축소.</summary>
        public void ZoomBy(double factor, double anchorX)
        {
            var frame = (anchorX + _scrollX) / _pixelsPerFrame;
            _pixelsPerFrame = Math.Max(0.01, Math.Min(60, _pixelsPerFrame * factor));
            _scrollX = frame * _pixelsPerFrame - anchorX;
            ClampScroll();
            Changed();
        }

        /// <summary>프로젝트 전체가 보이게(Shift+Z).</summary>
        public void ZoomToFit()
        {
            var frames = Math.Max(_session.Project.Rate.ToFrames(5), _session.Project.Duration);
            _pixelsPerFrame = Math.Max(0.01, Math.Min(60, (Math.Max(200, ActualWidth) - 40) / frames));
            _scrollX = 0;
            Changed();
        }

        /// <summary>재생 헤드가 화면 밖이면 보이게 스크롤.</summary>
        public void EnsureVisible(long frame)
        {
            var x = FrameToX(frame);
            if (x < 0 || x > ActualWidth - 20)
                ScrollX = frame * _pixelsPerFrame - ActualWidth * 0.1;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                ZoomBy(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(this).X);
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                ScrollY -= e.Delta / 2.0;
            else
                ScrollX -= e.Delta;
            e.Handled = true;
        }

        // ---------- 마우스 ----------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            PointerDown(e.GetPosition(this), Keyboard.Modifiers);
            if (_drag != DragKind.None)
                CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var point = e.GetPosition(this);
            if (_drag != DragKind.None)
            {
                PointerMove(point, Keyboard.Modifiers);
                return;
            }
            // 전환 배지: 효과 이름·길이를 알려 주고 누르면 그 클립을 고른다
            var transition = HitTransition(point);
            if (transition != null)
            {
                Cursor = Cursors.Hand;
                var tip = "전환: " + Transitions.Name(transition) + " · " + UiKit.Number(_session.Project.Rate.ToSeconds(transition.TransitionIn)) + "초 (누르면 이 클립을 골라 속성에서 바꿉니다)";
                if (!Equals(ToolTip, tip))
                    ToolTip = tip;
                return;
            }
            if (ToolTip != null)
                ToolTip = null;
            TimelineRow row;
            Zone zone;
            var clip = HitClip(point, out row, out zone);
            Cursor = clip == null ? (point.Y < RulerHeight ? Cursors.IBeam : null)
                : row.Track.Locked ? Cursors.No
                : zone == Zone.Body ? Cursors.Hand : Cursors.SizeWE;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_drag != DragKind.None)
            {
                PointerUp(e.GetPosition(this), Keyboard.Modifiers);
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_drag != DragKind.None && _dragStarted)
                CancelDrag();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            var point = e.GetPosition(this);
            TimelineRow row;
            Zone zone;
            var clip = HitClip(point, out row, out zone);
            if (clip != null && !_session.IsSelected(clip))
                _session.Select(new[] { clip });
            ContextRequested?.Invoke(clip, row?.Track, XToFrame(point.X));
            e.Handled = true;
        }

        internal void PointerDown(Point point, ModifierKeys modifiers)
        {
            _downPoint = point;
            _dragStarted = false;
            _drag = DragKind.None;
            if (point.Y < RulerHeight)
            {
                _drag = DragKind.Scrub;
                _dragStarted = true;
                _session.Playhead = XToFrame(point.X);
                return;
            }
            // 전환 배지를 누르면 전환이 든 클립을 고른다(끌기는 하지 않음 — 배지는 두 클립에 걸쳐 있다)
            var transition = HitTransition(point);
            if (transition != null)
            {
                _session.Select(new[] { transition });
                StatusMessage?.Invoke("전환: " + Transitions.Name(transition) + " · " + UiKit.Number(_session.Project.Rate.ToSeconds(transition.TransitionIn))
                                      + "초 — 속성 [화면] 탭에서 효과·길이를 바꾸거나 [효과] 탭에서 다른 효과를 넣습니다.");
                return;
            }
            TimelineRow row;
            Zone zone;
            var clip = HitClip(point, out row, out zone);
            if (clip == null)
            {
                if ((modifiers & ModifierKeys.Control) == 0)
                    _session.ClearSelection();
                return;
            }
            if ((modifiers & ModifierKeys.Control) != 0)
                _session.ToggleSelect(clip);
            else if (!_session.IsSelected(clip))
                _session.Select(new[] { clip });
            else
                _session.SetPrimary(clip);
            if (row.Track.Locked)
            {
                StatusMessage?.Invoke("'" + row.Track.Name + "' 트랙이 잠겨 있습니다.");
                return;
            }
            _dragIndependent = (modifiers & ModifierKeys.Alt) != 0;
            _dragPartnerId = ClipLinks.LinkedPartner(_session.Project, clip)?.Id;
            _dragClipId = clip.Id;
            _dragOriginStart = clip.Start;
            _dragDuration = clip.Duration;
            _ghostTrack = row.Track;
            _ghostStart = clip.Start;
            _ghostEnd = clip.End;
            _drag = zone == Zone.StartEdge ? DragKind.TrimStart : zone == Zone.EndEdge ? DragKind.TrimEnd : DragKind.Move;
        }

        internal void PointerMove(Point point, ModifierKeys modifiers)
        {
            if (_drag == DragKind.None)
                return;
            if (_drag == DragKind.Scrub)
            {
                _session.Playhead = XToFrame(point.X);
                return;
            }
            if (!_dragStarted)
            {
                if (Math.Abs(point.X - _downPoint.X) < DragThreshold && Math.Abs(point.Y - _downPoint.Y) < DragThreshold)
                    return;
                _dragStarted = true;
            }
            var project = _session.Project;
            var clip = project.FindClip(_dragClipId);
            if (clip == null)
            {
                CancelDrag();
                return;
            }
            var snap = Snapping && (modifiers & ModifierKeys.Alt) == 0;
            var threshold = (long)Math.Ceiling(SnapPixels / _pixelsPerFrame);
            // 함께 움직일 짝의 지금 자리에는 붙지 않는다
            var partner = _dragIndependent || _dragPartnerId == null ? null : project.FindClip(_dragPartnerId);
            var targets = snap ? TimelineEdits.SnapTargets(project, _session.Playhead, partner == null ? new[] { clip } : new[] { clip, partner }) : new List<long>();
            _snapLine = -1;
            var delta = XToFrame(point.X) - XToFrame(_downPoint.X);
            if (_drag == DragKind.Move)
            {
                var row = RowAt(point.Y);
                if (row != null && !row.Track.Locked && TimelineEdits.Accepts(row.Track, clip))
                    _ghostTrack = row.Track;
                var proposed = Math.Max(0, _dragOriginStart + delta);
                if (snap)
                    proposed = Math.Max(0, TimelineEdits.SnapMove(targets, proposed, _dragDuration, threshold, out _snapLine));
                _ghostStart = proposed;
                _ghostEnd = proposed + _dragDuration;
            }
            else
            {
                // 가장자리는 잡은 자리에서 움직인 만큼 옮긴다(잡을 때 가장자리에서 몇 픽셀 안쪽을 잡아도 튀지 않게)
                var frame = Math.Max(0, (_drag == DragKind.TrimStart ? clip.Start : clip.End) + delta);
                if (snap)
                {
                    bool snapped;
                    var snappedFrame = TimelineEdits.Snap(targets, frame, threshold, out snapped);
                    if (snapped)
                    {
                        frame = snappedFrame;
                        _snapLine = frame;
                    }
                }
                if (_drag == DragKind.TrimStart)
                {
                    _ghostStart = Math.Min(frame, clip.End - 1);
                    _ghostEnd = clip.End;
                }
                else
                {
                    _ghostStart = clip.Start;
                    _ghostEnd = Math.Max(frame, clip.Start + 1);
                }
            }
            InvalidateVisual();
        }

        internal void PointerUp(Point point, ModifierKeys modifiers)
        {
            var drag = _drag;
            var started = _dragStarted;
            var clipId = _dragClipId;
            var track = _ghostTrack;
            var start = _ghostStart;
            var end = _ghostEnd;
            var independent = _dragIndependent;
            var linked = _dragPartnerId != null;
            CancelDrag();
            if (drag == DragKind.None || drag == DragKind.Scrub || !started)
                return;
            bool ok;
            if (drag == DragKind.Move)
                ok = _session.Execute(p => TimelineEdits.Move(p, p.FindClip(clipId), p.FindTrack(track.Id), start, independent));
            else if (drag == DragKind.TrimStart)
                ok = _session.Execute(p => TimelineEdits.TrimStart(p, p.FindClip(clipId), start, independent));
            else
                ok = _session.Execute(p => TimelineEdits.TrimEnd(p, p.FindClip(clipId), end, independent));
            if (!ok && _session.LastError != null)
                StatusMessage?.Invoke(_session.LastError);
            else if (ok && linked && independent)
                StatusMessage?.Invoke("Alt: 링크된 짝은 그대로 두고 이 클립만 " + (drag == DragKind.Move ? "옮겼습니다. 짝과 어긋난 만큼이 클립에 보입니다." : "다듬었습니다."));
        }

        private void CancelDrag()
        {
            _drag = DragKind.None;
            _dragStarted = false;
            _snapLine = -1;
            _ghostTrack = null;
            _dragPartnerId = null;
            _dragIndependent = false;
            InvalidateVisual();
        }

        // ---------- 끌어 놓기 ----------

        private static string[] DroppedPaths(IDataObject data)
        {
            if (data.GetDataPresent(MediaDataFormat))
                return data.GetData(MediaDataFormat) as string[];
            if (data.GetDataPresent(DataFormats.FileDrop))
                return (data.GetData(DataFormats.FileDrop) as string[])?.Where(File.Exists).ToArray();
            return null;
        }

        /// <summary>효과 탭에서 끈 전환("종류|초")을 놓을 클립(놓은 자리의 클립 — 그 클립 앞에 넣는다).</summary>
        private Clip TransitionTarget(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TransitionDataFormat))
                return null;
            var clip = HitClip(e.GetPosition(this), out var row, out _);
            return clip != null && row != null && row.Track.Kind == TrackKind.Video && !row.Track.Subtitles ? clip : null;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (e.Data.GetDataPresent(TransitionDataFormat))
            {
                e.Effects = TransitionTarget(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                return;
            }
            var paths = DroppedPaths(e.Data);
            if (paths == null || paths.Length == 0)
            {
                e.Effects = DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.Copy;
                var point = e.GetPosition(this);
                _dropTrack = RowAt(point.Y)?.Track;
                _dropFrame = XToFrame(point.X);
                InvalidateVisual();
            }
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            base.OnDragLeave(e);
            _dropTrack = null;
            _dropFrame = -1;
            InvalidateVisual();
        }

        protected override void OnDrop(DragEventArgs e)
        {
            base.OnDrop(e);
            if (e.Data.GetDataPresent(TransitionDataFormat))
            {
                var target = TransitionTarget(e);
                if (target != null)
                    TransitionDropped?.Invoke(e.Data.GetData(TransitionDataFormat) as string, target.Id);
                e.Handled = true;
                return;
            }
            var paths = DroppedPaths(e.Data);
            var point = e.GetPosition(this);
            var track = RowAt(point.Y)?.Track;
            var frame = XToFrame(point.X);
            _dropTrack = null;
            _dropFrame = -1;
            InvalidateVisual();
            if (paths != null && paths.Length > 0)
                MediaDropped?.Invoke(paths, track, frame);
            e.Handled = true;
        }

        // ---------- 그리기 ----------

        private double Dpi { get { return VisualTreeHelper.GetDpi(this).PixelsPerDip; } }

        private FormattedText Text(string text, double size, Brush brush, bool bold = false)
        {
            return new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush, Dpi);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
                return;
            var project = _session.Project;
            var rate = project.Rate;
            var panel = (Brush)GetValue(PanelBrushProperty);
            var rowBrush = (Brush)GetValue(RowBrushProperty);
            var line = new Pen((Brush)GetValue(LineBrushProperty), 1);
            var text = (Brush)GetValue(TextBrushProperty);
            var subText = (Brush)GetValue(SubTextBrushProperty);
            dc.DrawRectangle(panel, null, new Rect(0, 0, width, height));
            _partners = ClipLinks.Partners(project);

            // 트랙 줄
            var rows = Rows();
            dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, width, Math.Max(0, height - RulerHeight))));
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                dc.DrawRectangle(i % 2 == 0 ? rowBrush : panel, null, new Rect(0, row.Top, width, row.Height));
                if (row.Track.Subtitles)
                    dc.DrawRectangle(SubtitleRowBrush, null, new Rect(0, row.Top, width, row.Height));
                dc.DrawLine(line, new Point(0, row.Top + row.Height - 0.5), new Point(width, row.Top + row.Height - 0.5));
                // 구역 경계는 이중선: 자막 구역의 마지막 줄, 동영상 구역의 마지막 줄(주 트랙)
                var lastSubtitle = row.Track.Subtitles && (i + 1 >= rows.Count || !rows[i + 1].Track.Subtitles);
                if (lastSubtitle || row.Track.Kind == TrackKind.Video && row.Track == project.MainTrack)
                    dc.DrawLine(line, new Point(0, row.Top + row.Height - 1.5), new Point(width, row.Top + row.Height - 1.5));
            }
            // In/Out 구간
            if (project.MarkIn.HasValue || project.MarkOut.HasValue)
            {
                var inX = FrameToX(project.MarkIn ?? 0);
                var outX = FrameToX(project.MarkOut ?? project.Duration);
                if (outX > inX)
                    dc.DrawRectangle(InOutBrush, null, new Rect(inX, RulerHeight, outX - inX, height - RulerHeight));
            }
            foreach (var row in rows)
            {
                if (row.Top > height || row.Top + row.Height < RulerHeight)
                    continue;
                foreach (var clip in row.Track.Clips)
                {
                    var rect = ClipRect(row, clip.Start, clip.End);
                    if (rect.Right < 0 || rect.Left > width)
                        continue;
                    DrawClip(dc, row, clip, rect, rate);
                }
                // 전환 배지는 양쪽 클립 위에 걸치므로 줄의 클립을 다 그린 뒤에
                foreach (var clip in TransitionClips(row))
                    DrawTransition(dc, row, clip, rate);
                if (row.Track.Locked || row.Track.Hidden || (row.Track.Muted && row.Track.Kind == TrackKind.Audio))
                    dc.DrawRectangle(ShadeBrush, null, new Rect(0, row.Top, width, row.Height));
            }
            // 끄는 중인 클립의 자리
            if (_drag != DragKind.None && _drag != DragKind.Scrub && _dragStarted && _ghostTrack != null)
            {
                var row = rows.FirstOrDefault(r => r.Track == _ghostTrack);
                if (row != null)
                    dc.DrawRoundedRectangle(GhostBrush, SelectedPen, ClipRect(row, _ghostStart, _ghostEnd), 3, 3);
                DrawPartnerGhost(dc, rows, project);
            }
            // 끌어 놓을 자리
            if (_dropFrame >= 0)
            {
                var x = FrameToX(_dropFrame);
                var row = rows.FirstOrDefault(r => r.Track == _dropTrack);
                var top = row?.Top ?? (rows.Count > 0 ? rows[rows.Count - 1].Top + rows[rows.Count - 1].Height : RulerHeight);
                dc.DrawRectangle(GhostBrush, null, new Rect(x, top, 3, row?.Height ?? 40));
            }
            dc.Pop();

            DrawRuler(dc, width, rate, line, subText);
            foreach (var marker in project.Markers)
            {
                var x = FrameToX(marker.Frame);
                if (x < -10 || x > width + 10)
                    continue;
                var brush = new SolidColorBrush(UiKit.ParseColor(marker.Color, Colors.DodgerBlue));
                var dashed = new Pen(brush, 1) { DashStyle = DashStyles.Dash };
                dc.DrawLine(dashed, new Point(x, RulerHeight), new Point(x, height));
                var flag = new StreamGeometry();
                using (var g = flag.Open())
                {
                    g.BeginFigure(new Point(x, 2), true, true);
                    g.LineTo(new Point(x + 9, 2), true, false);
                    g.LineTo(new Point(x + 9, 10), true, false);
                    g.LineTo(new Point(x, 14), true, false);
                }
                dc.DrawGeometry(brush, null, flag);
            }
            if (_snapLine >= 0)
            {
                var x = FrameToX(_snapLine);
                dc.DrawLine(SnapPen, new Point(x, 0), new Point(x, height));
            }
            // 재생 헤드
            var px = FrameToX(_session.Playhead);
            if (px >= -2 && px <= width + 2)
            {
                dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, height));
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(new Point(px - 6, 0), true, true);
                    g.LineTo(new Point(px + 6, 0), true, false);
                    g.LineTo(new Point(px + 6, 10), true, false);
                    g.LineTo(new Point(px, 16), true, false);
                    g.LineTo(new Point(px - 6, 10), true, false);
                }
                dc.DrawGeometry(PlayheadBrush, null, head);
            }
        }

        private void DrawRuler(DrawingContext dc, double width, FrameRate rate, Pen line, Brush subText)
        {
            dc.DrawRectangle((Brush)GetValue(RulerBrushProperty), null, new Rect(0, 0, width, RulerHeight));
            dc.DrawLine(line, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
            // 큰 눈금 간격: 화면에서 90픽셀 이상이 되는 가장 작은 단위
            var fps = rate.Fps;
            var steps = new[] { 1.0 / fps, 5.0 / fps, 10.0 / fps, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
            var stepSeconds = steps.FirstOrDefault(s => s * fps * _pixelsPerFrame >= 90);
            if (stepSeconds <= 0)
                stepSeconds = 3600;
            var stepFrames = Math.Max(1, rate.ToFrames(stepSeconds));
            var minor = Math.Max(1, stepFrames / 5);
            var first = XToFrame(0) / minor * minor;
            var last = XToFrame(width) + minor;
            for (var frame = first; frame <= last; frame += minor)
            {
                var x = FrameToX(frame);
                var major = frame % stepFrames == 0;
                dc.DrawLine(line, new Point(x, major ? RulerHeight - 12 : RulerHeight - 5), new Point(x, RulerHeight));
                if (major)
                    dc.DrawText(Text(rate.Timecode(frame), 10, subText), new Point(x + 3, 3));
            }
        }

        /// <summary>끄는 클립의 링크된 짝이 함께 갈 자리(Alt로 시작했으면 없음).</summary>
        private void DrawPartnerGhost(DrawingContext dc, List<TimelineRow> rows, Project project)
        {
            var clip = project.FindClip(_dragClipId);
            var partner = _dragIndependent || _dragPartnerId == null ? null : project.FindClip(_dragPartnerId);
            var row = partner == null || clip == null ? null : rows.FirstOrDefault(r => r.Track.Clips.Contains(partner));
            if (row == null)
                return;
            long start = partner.Start, end = partner.End;
            if (_drag == DragKind.Move)
            {
                start += _ghostStart - clip.Start;
                end += _ghostStart - clip.Start;
            }
            else if (_drag == DragKind.TrimStart)
            {
                start = Math.Min(end - 1, start + _ghostStart - clip.Start);
            }
            else
            {
                end = Math.Max(start + 1, end + _ghostEnd - clip.End);
            }
            dc.DrawRoundedRectangle(GhostBrush, SelectedPen, ClipRect(row, start, end), 3, 3);
        }

        private void DrawClip(DrawingContext dc, TimelineRow row, Clip clip, Rect rect, FrameRate rate)
        {
            var fill = row.Track.Subtitles ? SubtitleClipBrush
                : clip.Kind == ClipKind.Text ? TextClipBrush
                : TimelineEdits.IsAudioClip(clip) ? AudioBrush
                : clip.MediaKind == MediaKind.Image ? ImageBrush : VideoBrush;
            var selected = _session.IsSelected(clip);
            dc.DrawRoundedRectangle(fill, selected ? SelectedPen : ClipEdgePen, rect, 3, 3);
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            var textLeft = rect.Left + 5;
            // 썸네일(동영상·이미지)
            if (clip.Kind == ClipKind.Media && clip.IsVisual && rect.Height > 24)
            {
                var picture = Thumbnail(clip.Path);
                if (picture != null)
                {
                    var h = rect.Height - 2;
                    var w = Math.Min(rect.Width - 2, h * picture.PixelWidth / Math.Max(1.0, picture.PixelHeight));
                    if (w > 8)
                    {
                        dc.DrawImage(picture, new Rect(rect.Left + 1, rect.Top + 1, w, h));
                        textLeft = rect.Left + w + 5;
                    }
                }
            }
            // 파형(소리 나는 클립)
            if (clip.Kind == ClipKind.Media && clip.HasAudio && (TimelineEdits.IsAudioClip(clip) || (!clip.AudioDetached && rect.Height > 40)))
                DrawWaveform(dc, clip, rect, rate);
            var label = clip.Name;
            if (Math.Abs(clip.Speed - 1) > 1e-9)
                label += "  ×" + UiKit.Number(clip.Speed);
            // 사운드 분리: 영상에 "(사운드 분리)", 링크된 짝에 "링크"
            _partners.TryGetValue(clip.Id, out var partner);
            var linked = partner != null && !clip.Unlinked;
            if (clip.Kind == ClipKind.Media && clip.HasAudio && clip.AudioDetached)
                label += linked ? "  (사운드 분리 · 링크)" : "  (사운드 분리)";
            else if (linked)
                label += "  (링크)";
            dc.DrawText(Text(label, 11, Brushes.White, true), new Point(textLeft, rect.Top + 2));
            if (rect.Height > 30)
                dc.DrawText(Text(rate.Timecode(clip.Duration), 10, WaveBrush), new Point(textLeft, rect.Top + 16));
            // 어긋남: 짝보다 늦으면 +, 이르면 −(링크를 풀고 옮겼거나 잠긴 짝이 따라오지 못함)
            var offset = partner == null ? 0 : ClipLinks.Offset(clip, partner);
            if (offset != 0)
            {
                var badge = Text(ClipLinks.FormatOffset(offset, rate), 10, Brushes.White, true);
                var box = new Rect(Math.Max(rect.Left + 2, rect.Right - badge.Width - 10), rect.Top + 2, badge.Width + 6, badge.Height + 2);
                dc.DrawRoundedRectangle(OffsetBadgeBrush, null, box, 3, 3);
                dc.DrawText(badge, new Point(box.Left + 3, box.Top + 1));
            }
            dc.Pop();
            // 페이드
            if (clip.FadeIn > 0)
                dc.DrawLine(TransitionPen, new Point(rect.Left, rect.Bottom - 1), new Point(rect.Left + Math.Min(rect.Width, clip.FadeIn * _pixelsPerFrame), rect.Bottom - 8));
            if (clip.FadeOut > 0)
                dc.DrawLine(TransitionPen, new Point(rect.Right - Math.Min(rect.Width, clip.FadeOut * _pixelsPerFrame), rect.Bottom - 8), new Point(rect.Right, rect.Bottom - 1));
        }

        /// <summary>줄에서 전환이 있는 화면 클립(자막 트랙 제외).</summary>
        private static IEnumerable<Clip> TransitionClips(TimelineRow row)
        {
            if (row.Track.Kind != TrackKind.Video || row.Track.Subtitles)
                return Enumerable.Empty<Clip>();
            return row.Track.Clips.Where(c => c.TransitionIn > 0 && c.IsVisual);
        }

        /// <summary>전환 배지 자리: 클립 시작(앞 클립과 만나는 자리)에 걸쳐 클립 높이 가운데. 확대 수준과 상관없이 같은 크기.</summary>
        private Rect BadgeRect(TimelineRow row, Clip clip)
        {
            var rect = ClipRect(row, clip.Start, clip.End);
            var x = FrameToX(clip.Start);
            return new Rect(x - BadgeWidth / 2, rect.Top + (rect.Height - BadgeHeight) / 2, BadgeWidth, BadgeHeight);
        }

        /// <summary>
        /// 전환 표시: 전환 구간(클립 앞쪽 전환 길이)에 반투명 노란 띠와 비스듬한 선, 두 클립이 만나는 자리에 노란 ⇄ 배지,
        /// 자리가 있으면 배지 옆에 효과 이름. 방금 넣은 전환은 배지 둘레가 잠깐 반짝인다.
        /// </summary>
        private void DrawTransition(DrawingContext dc, TimelineRow row, Clip clip, FrameRate rate)
        {
            var rect = ClipRect(row, clip.Start, clip.End);
            var w = Math.Min(rect.Width, clip.TransitionIn * _pixelsPerFrame);
            dc.DrawRectangle(TransitionRegionBrush, null, new Rect(rect.Left, rect.Top, Math.Max(2, w), rect.Height));
            dc.DrawLine(TransitionPen, new Point(rect.Left, rect.Bottom), new Point(rect.Left + w, rect.Top));
            var badge = BadgeRect(row, clip);
            var flash = FlashLevel(clip.Id);
            if (flash > 0)
            {
                var glow = new Pen(new SolidColorBrush(Color.FromArgb((byte)(flash * 255), 0xFA, 0xCC, 0x15)), 3);
                var ring = badge;
                ring.Inflate(5, 5);
                dc.DrawRoundedRectangle(null, glow, ring, ring.Height / 2, ring.Height / 2);
            }
            dc.DrawRoundedRectangle(TransitionBadgeBrush, _session.IsSelected(clip) ? SelectedPen : TransitionBadgePen, badge, BadgeHeight / 2, BadgeHeight / 2);
            var glyph = Text("⇄", 12, TransitionGlyphBrush, true);
            dc.DrawText(glyph, new Point(badge.Left + (badge.Width - glyph.Width) / 2, badge.Top + (badge.Height - glyph.Height) / 2));
            // 이름은 배지 오른쪽(클립 안에 자리가 있을 때)
            var name = Text(Transitions.Name(clip) + " " + UiKit.Number(rate.ToSeconds(clip.TransitionIn)) + "초", 10, Brushes.White, true);
            if (rect.Width > BadgeWidth / 2 + name.Width + 10 && rect.Height > 40)
            {
                var label = new Rect(badge.Right + 3, rect.Bottom - name.Height - 4, name.Width + 6, name.Height + 2);
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawRoundedRectangle(ShadeBrush, null, label, 3, 3);
                dc.DrawText(name, new Point(label.Left + 3, label.Top + 1));
                dc.Pop();
            }
        }

        /// <summary>점에 있는 전환 배지의 클립(뒤 클립 — 전환이 들어 있는 쪽).</summary>
        private Clip HitTransition(Point point)
        {
            var row = RowAt(point.Y);
            return row == null ? null : TransitionClips(row).FirstOrDefault(c => BadgeRect(row, c).Contains(point));
        }

        // ---------- 방금 넣은 전환 반짝임 ----------

        private readonly HashSet<string> _flashIds = new HashSet<string>();
        private DateTime _flashStart;
        private DispatcherTimer _flashTimer;
        private static readonly TimeSpan FlashTime = TimeSpan.FromSeconds(1.6);

        /// <summary>전환을 넣은 클립의 배지를 잠깐 반짝여 어디에 들어갔는지 보인다.</summary>
        public void FlashTransitions(IEnumerable<string> clipIds)
        {
            _flashIds.Clear();
            foreach (var id in clipIds)
                _flashIds.Add(id);
            _flashStart = DateTime.UtcNow;
            if (_flashTimer == null)
            {
                _flashTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
                _flashTimer.Tick += (s, e) =>
                {
                    if (DateTime.UtcNow - _flashStart > FlashTime)
                    {
                        _flashIds.Clear();
                        _flashTimer.Stop();
                    }
                    InvalidateVisual();
                };
            }
            _flashTimer.Start();
            InvalidateVisual();
        }

        /// <summary>반짝임 세기(0~1): 세 번 깜빡이며 옅어진다.</summary>
        private double FlashLevel(string clipId)
        {
            if (!_flashIds.Contains(clipId))
                return 0;
            var t = (DateTime.UtcNow - _flashStart).TotalSeconds / FlashTime.TotalSeconds;
            return t >= 1 ? 0 : (1 - t) * (0.5 + 0.5 * Math.Cos(t * 3 * 2 * Math.PI));
        }

        private void DrawWaveform(DrawingContext dc, Clip clip, Rect rect, FrameRate rate)
        {
            var peaks = Waveform(clip.Path);
            if (peaks == null || peaks.Length == 0)
                return;
            var top = TimelineEdits.IsAudioClip(clip) ? rect.Top + 14 : rect.Top + rect.Height * 0.55;
            var bottom = rect.Bottom - 2;
            var mid = (top + bottom) / 2;
            var half = (bottom - top) / 2;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var step = Math.Max(1, (int)(1 / Math.Max(0.1, 1)));
                var left = Math.Max(rect.Left, 0);
                var right = Math.Min(rect.Right, ActualWidth);
                for (var x = left; x < right; x += step)
                {
                    // 화면 x → 클립 안 타임라인 프레임 → 원본 초
                    var frameInClip = (x - rect.Left) / _pixelsPerFrame;
                    var sourceSeconds = rate.ToSeconds(clip.SourceIn) + frameInClip * clip.Speed / rate.Fps;
                    var index = (int)(sourceSeconds * EditorServices.WaveformPeaksPerSecond);
                    if (index < 0 || index >= peaks.Length)
                        continue;
                    var amplitude = Math.Min(1, peaks[index] * clip.Volume * 2.5) * half;
                    g.BeginFigure(new Point(x, mid - amplitude), false, false);
                    g.LineTo(new Point(x, mid + amplitude + 0.5), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(WaveBrush, 1), geometry);
        }

        private BitmapSource Thumbnail(string path)
        {
            var media = _session.Project.FindMedia(path) ?? new MediaInfo { Path = path, Kind = MediaKind.Video };
            var task = _services.Thumbnail(media);
            if (task.IsCompleted)
                return task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : null;
            WhenReady(path, task);
            return null;
        }

        private float[] Waveform(string path)
        {
            var task = _services.Waveform(path);
            if (task.IsCompleted)
                return task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : null;
            WhenReady("wave:" + path, task);
            return null;
        }

        /// <summary>썸네일·파형이 준비되면 한 번 다시 그린다.</summary>
        private void WhenReady(string key, System.Threading.Tasks.Task task)
        {
            if (!_pendingPictures.Add(key))
                return;
            task.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                _pendingPictures.Remove(key);
                InvalidateVisual();
            })));
        }

        // ---------- 접근성(화면 낭독기·화면 시험) ----------

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new TimelinePeer(this);
        }

        internal EditorSession Session { get { return _session; } }

        private sealed class TimelinePeer : FrameworkElementAutomationPeer
        {
            public TimelinePeer(TimelineCanvas owner) : base(owner)
            {
            }

            private TimelineCanvas Canvas { get { return (TimelineCanvas)Owner; } }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.List;
            }

            protected override string GetClassNameCore()
            {
                return "Timeline";
            }

            protected override List<AutomationPeer> GetChildrenCore()
            {
                var session = Canvas.Session;
                var project = session.Project;
                var list = new List<AutomationPeer>();
                foreach (var track in project.VideoTracks.AsEnumerable().Reverse().Concat(project.AudioTracks))
                {
                    foreach (var clip in track.Clips.OrderBy(c => c.Start))
                        list.Add(new ClipPeer(Canvas, clip, track));
                }
                return list;
            }
        }

        /// <summary>클립 하나: 이름 "트랙 · 이름 · 시작–끝", 선택 가능(SelectionItem).</summary>
        private sealed class ClipPeer : AutomationPeer, ISelectionItemProvider
        {
            private readonly TimelineCanvas _canvas;
            private readonly Clip _clip;
            private readonly Track _track;

            public ClipPeer(TimelineCanvas canvas, Clip clip, Track track)
            {
                _canvas = canvas;
                _clip = clip;
                _track = track;
            }

            protected override string GetNameCore()
            {
                var rate = _canvas.Session.Project.Rate;
                var name = _track.Name + " · " + _clip.Name + " · " + rate.Timecode(_clip.Start) + "–" + rate.Timecode(_clip.End);
                // 화면 읽기 프로그램·시험에도 전환이 들어 있다는 것을 알린다
                if (_clip.TransitionIn > 0 && _clip.IsVisual && !_track.Subtitles)
                    name += " · 전환 " + Transitions.Name(_clip) + " " + UiKit.Number(rate.ToSeconds(_clip.TransitionIn)) + "초";
                // 사운드 분리: 링크 상태와 어긋남
                var partner = ClipLinks.Partner(_canvas.Session.Project, _clip);
                if (partner != null)
                {
                    name += _clip.Unlinked ? " · 링크 해제" : " · 링크";
                    var offset = ClipLinks.Offset(_clip, partner);
                    if (offset != 0)
                        name += " · 어긋남 " + ClipLinks.FormatOffset(offset, rate);
                }
                return name;
            }

            protected override string GetAutomationIdCore()
            {
                return "Clip_" + _track.Name.Replace(" ", "") + "_" + _clip.Start;
            }

            public override object GetPattern(PatternInterface patternInterface)
            {
                return patternInterface == PatternInterface.SelectionItem ? this : null;
            }

            public bool IsSelected { get { return _canvas.Session.IsSelected(_clip); } }
            public IRawElementProviderSimple SelectionContainer { get { return null; } }

            public void Select()
            {
                _canvas.Session.Select(new[] { _clip });
            }

            public void AddToSelection()
            {
                _canvas.Session.Select(new[] { _clip }, true);
            }

            public void RemoveFromSelection()
            {
                if (_canvas.Session.IsSelected(_clip))
                    _canvas.Session.ToggleSelect(_clip);
            }

            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.ListItem; }
            protected override string GetClassNameCore() { return "TimelineClip"; }
            protected override string GetAcceleratorKeyCore() { return ""; }
            protected override string GetAccessKeyCore() { return ""; }
            protected override Rect GetBoundingRectangleCore() { return Rect.Empty; }
            protected override List<AutomationPeer> GetChildrenCore() { return null; }
            protected override Point GetClickablePointCore() { return new Point(double.NaN, double.NaN); }
            protected override string GetHelpTextCore() { return ""; }
            protected override string GetItemStatusCore() { return ""; }
            protected override string GetItemTypeCore() { return _track.Subtitles ? "자막" : _clip.Kind == ClipKind.Text ? "글자" : _clip.MediaKind.ToString(); }
            protected override AutomationPeer GetLabeledByCore() { return null; }
            protected override AutomationOrientation GetOrientationCore() { return AutomationOrientation.None; }
            protected override bool HasKeyboardFocusCore() { return false; }
            protected override bool IsContentElementCore() { return true; }
            protected override bool IsControlElementCore() { return true; }
            protected override bool IsEnabledCore() { return true; }
            protected override bool IsKeyboardFocusableCore() { return false; }
            protected override bool IsOffscreenCore() { return false; }
            protected override bool IsPasswordCore() { return false; }
            protected override bool IsRequiredForFormCore() { return false; }
            protected override void SetFocusCore() { }
        }
    }
}
