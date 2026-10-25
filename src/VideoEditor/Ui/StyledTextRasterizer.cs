using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 꾸민 글자 그리개(WPF): 글자 윤곽(FormattedText.BuildGeometry)을 칠해 투명 배경 PNG로 쓴다.
    /// 기하만 칠하므로 ClearType·힌팅·모니터 배율과 상관없다(pixelsPerDip 1, 96 DPI 고정).
    /// 그리기는 배경 STA 스레드 하나에서 하나씩 한다 — UI 스레드를 쓰지 않고(큰 그림이 편집 화면을 멈추지 않게), 요청마다 스레드를 만들지 않는다.
    /// </summary>
    internal sealed class StyledTextRasterizer : ITextRasterizer, IDisposable
    {
        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);
        private static readonly Lazy<FontChoice[]> LazyFonts = new Lazy<FontChoice[]>(LoadFonts);
        private static readonly Lazy<HashSet<string>> LazyFontNames = new Lazy<HashSet<string>>(() =>
            new HashSet<string>(LazyFonts.Value.SelectMany(f => f.Names), StringComparer.OrdinalIgnoreCase));
        private readonly object _gate = new object();
        private Dispatcher _dispatcher;
        private bool _disposed;

        /// <summary>그리는 방식을 바꾸면 올린다(캐시 키에 들어가 예전 PNG를 쓰지 않게).</summary>
        public int Version { get { return 3; } }

        /// <summary>어느 스레드에서 불러도 된다. 배경 그리기 스레드에 맡기고 끝날 때까지(최대 30초) 기다린다.</summary>
        public void Render(TextImageRequest request, string pngPath, CancellationToken cancellation)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            var thread = DrawingThread();
            // 그리기 스레드 안에서 다시 불리면 기다리면 안 된다(자기 자신을 기다려 멈춤) — 바로 그린다
            if (thread.CheckAccess())
            {
                Draw(request, pngPath, cancellation);
                return;
            }
            var operation = thread.InvokeAsync(() => Draw(request, pngPath, cancellation), DispatcherPriority.Normal, cancellation);
            bool done;
            try
            {
                done = operation.Task.Wait(RenderTimeout, cancellation);
            }
            catch (AggregateException)
            {
                // 그리다 난 예외 — 아래에서 원래 예외로 다시 던진다
                done = true;
            }
            catch (OperationCanceledException)
            {
                Abandon(operation, pngPath);
                throw;
            }
            if (!done)
            {
                Abandon(operation, pngPath);
                throw new TimeoutException("꾸민 글자를 그리는 데 " + RenderTimeout.TotalSeconds + "초가 넘게 걸렸습니다.");
            }
            operation.Task.GetAwaiter().GetResult();
        }

        /// <summary>기다리기를 그만둔 그리기: 시작 전이면 취소하고, 이미 도는 중이면 끝난 뒤 쓰던 파일을 지운다(늦게 쓴 .part가 남지 않게).</summary>
        private static void Abandon(DispatcherOperation operation, string pngPath)
        {
            operation.Abort();
            operation.Task.ContinueWith(_ =>
            {
                try
                {
                    File.Delete(pngPath);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }, TaskScheduler.Default);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
                _dispatcher = null;
            }
        }

        /// <summary>그리기 스레드(처음 쓸 때 만든다).</summary>
        private Dispatcher DrawingThread()
        {
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(StyledTextRasterizer));
                if (_dispatcher != null)
                    return _dispatcher;
                Dispatcher dispatcher = null;
                using (var ready = new ManualResetEventSlim())
                {
                    var thread = new Thread(() =>
                    {
                        dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        ready.Set();
                        System.Windows.Threading.Dispatcher.Run();
                    })
                    {
                        IsBackground = true,
                        Name = "styled-text"
                    };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    ready.Wait();
                }
                return _dispatcher = dispatcher;
            }
        }

        // ---------- 글꼴 ----------

        /// <summary>글꼴 칸에 보일 시스템 글꼴(보이는 이름순). Name은 스타일에 저장하는 이름.</summary>
        public static IReadOnlyList<FontChoice> Fonts { get { return LazyFonts.Value; } }

        /// <summary>이 PC에 있는 글꼴 이름인지(영문·한글 이름 모두). 경로·URI 같은 이름은 아니다.</summary>
        public static bool IsInstalled(string name)
        {
            return TextStyle.IsSafeFontName(name) && LazyFontNames.Value.Contains(name);
        }

        /// <summary>그릴 글꼴: 이 PC에 없거나 쓸 수 없는 이름이면 맑은 고딕.</summary>
        public static string Resolve(string name)
        {
            return IsInstalled(name) ? name : TextStyle.DefaultFont;
        }

        private static FontChoice[] LoadFonts()
        {
            var korean = System.Windows.Markup.XmlLanguage.GetLanguage("ko-KR");
            return System.Windows.Media.Fonts.SystemFontFamilies
                .Select(family =>
                {
                    string display;
                    if (!family.FamilyNames.TryGetValue(korean, out display))
                        display = family.Source;
                    return new FontChoice(family.Source, display, family.FamilyNames.Values.Concat(new[] { family.Source }).ToArray());
                })
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        // ---------- 그리기(그리기 스레드) ----------

        private static void Draw(TextImageRequest request, string pngPath, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var style = request.Style?.Clone() ?? new TextStyle();
            style.Normalize();
            var size = Clamp(request.FontPixelSize, 0.5, 4000, 1);
            var typeface = new Typeface(new FontFamily(Resolve(style.FontFamily)),
                style.Italic ? FontStyles.Italic : FontStyles.Normal, style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var lineHeight = size * style.LineSpacing;
            var spacing = style.LetterSpacing / 1000 * size;

            // 줄마다 조각(자간이 0이면 줄 통째 — 커닝을 살린다, 아니면 글자 단위)을 재고 정렬해 놓는다
            var lines = Lines(request.Text).Select(line => Measure(line, typeface, size, spacing)).ToList();
            var boxWidth = lines.Select(l => l.Width).DefaultIfEmpty(0).Max();
            var boxHeight = lines.Count * lineHeight;
            var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var x = style.Align == TextAlign.Left ? 0 : style.Align == TextAlign.Right ? boxWidth - line.Width : (boxWidth - line.Width) / 2;
                // 글꼴 줄 높이를 행간 칸 가운데에 두고, 조각들은 기준선을 맞춘다
                var top = i * lineHeight + (lineHeight - line.Height) / 2;
                foreach (var piece in line.Pieces)
                    geometry.Children.Add(piece.Text.BuildGeometry(new Point(x + piece.Offset, top + line.Baseline - piece.Text.Baseline)));
            }
            cancellation.ThrowIfCancellationRequested();

            // PNG 가운데 = 배치 상자 가운데. 글자 모양이 상자 밖으로 나가도(기울임·행간이 좁을 때) 잘리지 않게 대칭으로 넓힌다
            var ink = geometry.Bounds;
            var centerX = boxWidth / 2;
            var centerY = boxHeight / 2;
            var halfWidth = Math.Max(centerX, ink.IsEmpty ? 0 : Math.Max(centerX - ink.Left, ink.Right - centerX));
            var halfHeight = Math.Max(centerY, ink.IsEmpty ? 0 : Math.Max(centerY - ink.Top, ink.Bottom - centerY));
            var layout = StyledTextLayout.Fit(halfWidth * 2, halfHeight * 2, style, request.EffectScale, request.CanvasWidth, request.CanvasHeight);
            if ((double)layout.Width * layout.Height > StyledTextLayout.MaxPixels)
                throw new InvalidOperationException("꾸민 글자 그림이 너무 큽니다: " + layout.Width + "×" + layout.Height);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(layout.OriginX + halfWidth - centerX, layout.OriginY + halfHeight - centerY));
                // 윤곽선을 먼저, 채우기를 위에 — 펜 두께의 바깥 절반(= 윤곽선 두께)만 보인다. 그림자는 다음 작업에서 더한다
                var outline = OutlinePen(style.Outline, request.EffectScale);
                if (outline != null && !ink.IsEmpty)
                    DrawOutline(context, outline, geometry, style.Fill);
                // 채우기(단색·선형·원형, 없음이면 칠하지 않음)
                var fill = FillBrush(style.Fill);
                if (fill != null)
                    context.DrawGeometry(fill, null, geometry);
                context.Pop();
            }
            var bitmap = new RenderTargetBitmap(layout.Width, layout.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            cancellation.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(pngPath))
                encoder.Save(stream);
        }

        /// <summary>줄 나눔: CR·LF와 FormattedText가 줄을 바꾸는 유니코드 줄·문단 구분자·NEL(그래야 줄마다 정렬이 맞다).</summary>
        private static List<string> Lines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Split('\r', '\n', '\u2028', '\u2029', '\u0085').ToList();
        }

        /// <summary>오른쪽→왼쪽 글자(히브리어·아랍어 등)가 들었는지 — 글자 단위로 나눠 놓으면 순서와 아랍어 이음이 깨진다.</summary>
        private static bool HasRightToLeft(string text)
        {
            foreach (var ch in text)
            {
                if (ch >= '\u0590' && ch <= '\u08FF' || ch >= '\uFB1D' && ch <= '\uFDFF' || ch >= '\uFE70' && ch <= '\uFEFF')
                    return true;
            }
            return false;
        }

        private static Line Measure(string text, Typeface typeface, double size, double spacing)
        {
            var line = new Line();
            // 오른쪽→왼쪽 글자가 든 줄은 자간을 넣지 않고 통째로 놓는다
            if (spacing == 0 || HasRightToLeft(text))
            {
                if (text.Length > 0)
                    line.Add(Format(text, typeface, size), 0);
            }
            else
            {
                // 글자 단위(StringInfo 텍스트 요소 — 한글 음절·결합 문자·대리 쌍을 쪼개지 않는다)로 재고 사이를 띄운다
                var offset = 0.0;
                var elements = StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext())
                {
                    var piece = Format(elements.GetTextElement(), typeface, size);
                    if (line.Pieces.Count > 0)
                        offset += spacing;
                    line.Add(piece, offset);
                    offset += piece.WidthIncludingTrailingWhitespace;
                }
            }
            if (line.Pieces.Count == 0)
            {
                // 빈 줄도 높이는 같게
                line.Fit(Format(" ", typeface, size));
            }
            return line;
        }

        private static FormattedText Format(string text, Typeface typeface, double size)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
        }

        /// <summary>
        /// 채우기 붓: 단색은 첫 색, 선형·원형 그라데이션은 색을 고르게 놓은 정지점(색이 하나뿐이면 단색), 없음은 null.
        /// 그라데이션은 글자 모양의 경계 상자 기준(선형 끝점은 <see cref="TextEffects.GradientPoints"/>, 원형은 가운데·반지름 0.5).
        /// </summary>
        private static Brush FillBrush(TextFill fill)
        {
            if (fill.Kind == FillKind.None)
                return null;
            if (fill.Kind == FillKind.Solid || fill.Colors.Count < 2)
                return SolidBrush(fill.Colors[0]);
            var stops = new GradientStopCollection();
            var positions = TextEffects.GradientStops(fill.Colors.Count);
            for (var i = 0; i < fill.Colors.Count; i++)
                stops.Add(new GradientStop(UiKit.ParseColor(fill.Colors[i], Colors.White), positions[i]));
            GradientBrush brush;
            if (fill.Kind == FillKind.Linear)
            {
                var (startX, startY, endX, endY) = TextEffects.GradientPoints(fill.Angle);
                brush = new LinearGradientBrush(stops, new Point(startX, startY), new Point(endX, endY));
            }
            else
            {
                brush = new RadialGradientBrush(stops) { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            }
            brush.MappingMode = BrushMappingMode.RelativeToBoundingBox;
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 윤곽선 펜: 두께 = 윤곽선 두께 × 효과 배율 × 2(글자 윤곽 양쪽으로 반씩 — 바깥쪽 절반이 윤곽선 두께), 이음은 둥글게·각지게·깎아서,
        /// 각진 이음 한도는 <see cref="StyledTextLayout.MiterLimit"/>(여백 계산과 같은 값). 꺼졌거나 두께가 0이면 null.
        /// </summary>
        private static Pen OutlinePen(TextOutline outline, double effectScale)
        {
            if (!outline.Enabled)
                return null;
            // 효과 배율은 여백 계산(StyledTextLayout)과 같게 — 유한한 양수만, 아니면 0
            var thickness = outline.Width * (double.IsFinite(effectScale) && effectScale > 0 ? effectScale : 0) * 2;
            if (!(thickness > 0))
                return null;
            var pen = new Pen(SolidBrush(outline.Color), thickness)
            {
                LineJoin = outline.Join == LineJoinKind.Miter ? PenLineJoin.Miter : outline.Join == LineJoinKind.Bevel ? PenLineJoin.Bevel : PenLineJoin.Round,
                MiterLimit = StyledTextLayout.MiterLimit
            };
            pen.Freeze();
            return pen;
        }

        /// <summary>
        /// 윤곽선을 그린다. 채우기가 불투명하면 글자 아래에 그대로(채우기가 안쪽 절반을 덮어 가장자리 이음새가 없다),
        /// 채우기가 비치면(없음·반투명 색) 글자 밖으로만 잘라 그린다 — 안쪽 절반이 비쳐 보이지 않게, 없음이면 속이 빈 글자.
        /// </summary>
        private static void DrawOutline(DrawingContext context, Pen pen, Geometry geometry, TextFill fill)
        {
            if (IsOpaque(fill))
            {
                context.DrawGeometry(null, pen, geometry);
                return;
            }
            var outside = geometry.GetRenderBounds(pen);
            outside.Inflate(1, 1);
            var clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outside), geometry);
            clip.Freeze();
            context.PushClip(clip);
            context.DrawGeometry(null, pen, geometry);
            context.Pop();
        }

        /// <summary>채우기가 글자를 다 덮는지: 없음이 아니고 쓰는 색이 모두 불투명.</summary>
        private static bool IsOpaque(TextFill fill)
        {
            if (fill.Kind == FillKind.None)
                return false;
            var colors = fill.Kind == FillKind.Solid ? fill.Colors.Take(1) : fill.Colors;
            return colors.All(c => UiKit.ParseColor(c, Colors.White).A == 255);
        }

        private static Brush SolidBrush(string color)
        {
            var brush = new SolidColorBrush(UiKit.ParseColor(color, Colors.White));
            brush.Freeze();
            return brush;
        }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            return double.IsFinite(value) ? Math.Max(min, Math.Min(max, value)) : fallback;
        }

        /// <summary>
        /// 한 줄: 조각(글 + 줄 안 x 위치)들과 폭, 글꼴 줄 높이·기준선. 조각마다 글꼴이 다를 수 있어(대체 글꼴)
        /// 기준선 위는 가장 높은 것, 아래는 가장 깊은 것으로 잰다 — 자간이 있든 없든 세로 위치가 같게.
        /// </summary>
        private sealed class Line
        {
            private double _descent;

            public List<(FormattedText Text, double Offset)> Pieces { get; } = new List<(FormattedText, double)>();
            public double Width { get; private set; }
            public double Baseline { get; private set; }
            public double Height { get { return Baseline + _descent; } }

            public void Add(FormattedText text, double offset)
            {
                Pieces.Add((text, offset));
                Width = Math.Max(Width, offset + text.WidthIncludingTrailingWhitespace);
                Fit(text);
            }

            /// <summary>높이만 맞춘다(빈 줄).</summary>
            public void Fit(FormattedText text)
            {
                Baseline = Math.Max(Baseline, text.Baseline);
                _descent = Math.Max(_descent, text.Height - text.Baseline);
            }
        }
    }

    /// <summary>글꼴 칸의 항목: 저장하는 이름(영문), 보이는 이름(한국어 이름이 있으면), 이 글꼴의 모든 이름.</summary>
    internal sealed class FontChoice
    {
        public FontChoice(string name, string display, string[] names)
        {
            Name = name;
            Display = display;
            Names = names;
        }

        public string Name { get; }
        public string Display { get; }
        public string[] Names { get; }

        public override string ToString()
        {
            return Display;
        }
    }
}
