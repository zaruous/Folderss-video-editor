using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        public int Version { get { return 7; } }

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
            // 휘기는 효과를 칠하기 전에 글자 윤곽에 먼저 — 채우기·윤곽선(뒤에 더할 효과도)이 모두 휘어진 윤곽을 쓴다. 없음·정도 0이면 그대로(바이트까지 같게)
            if (style.Warp.Kind != WarpKind.None && style.Warp.Amount != 0 && boxWidth > 0 && boxHeight > 0)
            {
                geometry = Warp(geometry, style.Warp, boxWidth, boxHeight, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }

            // PNG 가운데 = 배치 상자 가운데. 글자 모양이 상자 밖으로 나가도(기울임·행간이 좁을 때·휘기) 잘리지 않게 대칭으로 넓힌다
            var ink = geometry.Bounds;
            var centerX = boxWidth / 2;
            var centerY = boxHeight / 2;
            var halfWidth = Math.Max(centerX, ink.IsEmpty ? 0 : Math.Max(centerX - ink.Left, ink.Right - centerX));
            var halfHeight = Math.Max(centerY, ink.IsEmpty ? 0 : Math.Max(centerY - ink.Top, ink.Bottom - centerY));
            var layout = StyledTextLayout.Fit(halfWidth * 2, halfHeight * 2, style, request.EffectScale, request.CanvasWidth, request.CanvasHeight);
            if ((double)layout.Width * layout.Height > StyledTextLayout.MaxPixels)
                throw new InvalidOperationException("꾸민 글자 그림이 너무 큽니다: " + layout.Width + "×" + layout.Height);

            // 층을 아래에서 위로 하나씩 그려 겹친다: 반사 → 그림자 → 앞쪽 층(광선 → 본문(윤곽선 → 채우기)).
            // 반사는 앞쪽 층만 뒤집어 비춘다(그림자는 비추지 않음) — 앞쪽 층을 다 모은 뒤 만들어 맨 아래에 넣는다
            var origin = new Vector(layout.OriginX + halfWidth - centerX, layout.OriginY + halfHeight - centerY);
            var front = new List<Visual>();
            var glow = GlowLayer(geometry, ink, style, request.EffectScale, origin);
            if (glow != null)
                front.Add(glow);
            var body = new DrawingVisual();
            using (var context = body.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(origin.X, origin.Y));
                DrawBody(context, geometry, ink, style, request.EffectScale, null);
                context.Pop();
            }
            front.Add(body);
            var layers = new List<Visual>();
            var shadow = ShadowLayer(geometry, ink, style, request.EffectScale, origin);
            if (shadow != null)
                layers.Add(shadow);
            layers.AddRange(front);
            var reflection = ReflectionLayer(front, geometry, ink, style, request.EffectScale, origin, layout.Width, layout.Height, cancellation);
            if (reflection != null)
                layers.Insert(0, reflection);
            var bitmap = new RenderTargetBitmap(layout.Width, layout.Height, 96, 96, PixelFormats.Pbgra32);
            foreach (var layer in layers)
            {
                cancellation.ThrowIfCancellationRequested();
                bitmap.Render(layer);
            }
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
        /// 본문을 그린다: 윤곽선을 먼저, 채우기를 위에 — 펜 두께의 바깥 절반(= 윤곽선 두께)만 보인다.
        /// paint가 있으면 윤곽선·채우기 색 대신 모두 그 붓으로 칠한다 — 그림자처럼 본문이 칠하는 모양만 필요할 때(채우기가 없으면 윤곽선 고리만).
        /// </summary>
        private static void DrawBody(DrawingContext context, Geometry geometry, Rect ink, TextStyle style, double effectScale, Brush paint)
        {
            var outline = OutlinePen(style.Outline, effectScale, paint);
            if (outline != null && !ink.IsEmpty)
                DrawOutline(context, outline, geometry, style.Fill);
            // 채우기(단색·선형·원형, 없음이면 칠하지 않음)
            var fill = FillBrush(style.Fill);
            if (fill != null)
                context.DrawGeometry(paint ?? fill, null, geometry);
        }

        /// <summary>
        /// 그림자 층: 본문이 칠하는 모양(채우기 영역 + 켰으면 윤곽선, 채우기가 없으면 윤곽선 고리만)을 그림자 색 × 불투명도로 칠해
        /// 거리·각도만큼 옮기고, 가우스 흐림(반지름 = 흐림 × 효과 배율, 상한 <see cref="TextEffects.MaxBlurRadius"/>)을 건다.
        /// 거리·흐림은 여백 계산(<see cref="StyledTextLayout.Spread"/>)과 같은 값이라 그림 가장자리에서 잘리지 않는다. 꺼졌거나 보이지 않으면 null.
        /// </summary>
        private static Visual ShadowLayer(Geometry geometry, Rect ink, TextStyle style, double effectScale, Vector origin)
        {
            var shadow = style.Shadow;
            if (!shadow.Enabled || ink.IsEmpty)
                return null;
            var color = UiKit.ParseColor(shadow.Color, Colors.Black);
            var opacity = color.A / 255.0 * shadow.Opacity;
            if (!(opacity > 0))
                return null;
            // 윤곽선 안쪽 절반과 채우기가 겹친 곳이 진해지지 않게 불투명한 색으로 칠하고, 불투명도는 층 전체에 한 번
            color.A = 255;
            var paint = new SolidColorBrush(color);
            paint.Freeze();
            var offset = StyledTextLayout.ShadowOffset(shadow, effectScale);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(origin.X + offset.X, origin.Y + offset.Y));
                context.PushOpacity(opacity);
                DrawBody(context, geometry, ink, style, effectScale, paint);
                context.Pop();
                context.Pop();
            }
            var radius = TextEffects.BlurRadius(shadow.Blur, effectScale);
            if (radius > 0)
            {
                var blur = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian };
                blur.Freeze();
                visual.Effect = blur;
            }
            return visual;
        }

        /// <summary>
        /// 광선 층(앞쪽 층에서 본문 바로 아래): 본문이 칠하는 모양을 크기만큼 넓혀(윤곽선을 켰으면 그 바깥 가장자리부터) 광선 색 × 불투명도로 칠하고
        /// 가우스 흐림(반지름 = 크기 / 2 × 효과 배율, 상한 <see cref="TextEffects.MaxBlurRadius"/>)을 건다.
        /// 글자 윤곽을 두께 (윤곽선 두께 + 크기) × 2인 펜(<see cref="TextEffects.GlowPenThickness"/>)으로 그리고 채운다 — 이음은 둥글게,
        /// 각지게 윤곽선이면 각지게(한도 <see cref="TextEffects.GlowMiterLimit"/> — 모서리 끝도 윤곽선 모서리 끝 + 크기까지).
        /// 채우기가 없으면(속 빈 글자) 본문은 윤곽선 고리만 칠하므로 광선도 고리 둘레만: 글자 밖은 고리 바깥 가장자리에서, 글자 속은 고리 안쪽 가장자리(글자 윤곽)에서 크기만큼.
        /// 넓이·흐림은 여백 계산(<see cref="StyledTextLayout.Spread"/>)과 같은 값이라 잘리지 않는다. 꺼졌거나 크기·불투명도가 0이거나 본문이 칠하는 것이 없으면 null.
        /// </summary>
        private static Visual GlowLayer(Geometry geometry, Rect ink, TextStyle style, double effectScale, Vector origin)
        {
            var glow = style.Glow;
            if (!glow.Enabled || ink.IsEmpty)
                return null;
            var outline = OutlinePen(style.Outline, effectScale, null);
            var hollow = style.Fill.Kind == FillKind.None;
            // 채우기도 윤곽선도 없으면 본문이 칠하는 것이 없어 넓힐 모양도 없다
            if (hollow && outline == null)
                return null;
            var outlineWidth = outline != null ? style.Outline.Width : 0;
            var thickness = TextEffects.GlowPenThickness(outlineWidth, glow.Size, effectScale);
            var color = UiKit.ParseColor(glow.Color, Color.FromRgb(0xFF, 0xD5, 0x4F));
            var opacity = color.A / 255.0 * glow.Opacity;
            if (!(thickness > 0) || !(opacity > 0))
                return null;
            // 펜과 채우기(속 빈 글자는 두 펜)가 겹친 곳이 진해지지 않게 불투명한 색으로 칠하고, 불투명도는 층 전체에 한 번
            color.A = 255;
            var paint = new SolidColorBrush(color);
            paint.Freeze();
            var miter = outline != null && style.Outline.Join == LineJoinKind.Miter;
            var pen = new Pen(paint, thickness)
            {
                LineJoin = miter ? PenLineJoin.Miter : PenLineJoin.Round,
                MiterLimit = miter ? TextEffects.GlowMiterLimit(outlineWidth, glow.Size) : StyledTextLayout.MiterLimit
            };
            pen.Freeze();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(origin.X, origin.Y));
                context.PushOpacity(opacity);
                if (hollow)
                {
                    // 글자 밖은 펜을 글자 밖으로만 잘라(윤곽선과 같게), 글자 속은 두께 = 크기 × 2인 둥근 펜으로 글자 윤곽 안쪽 크기만큼
                    DrawOutline(context, pen, geometry, style.Fill);
                    var inside = new Pen(paint, TextEffects.GlowPenThickness(0, glow.Size, effectScale)) { LineJoin = PenLineJoin.Round };
                    inside.Freeze();
                    context.DrawGeometry(null, inside, geometry);
                }
                else
                {
                    context.DrawGeometry(paint, pen, geometry);
                }
                context.Pop();
                context.Pop();
            }
            var radius = TextEffects.GlowBlurRadius(glow.Size, effectScale);
            if (radius > 0)
            {
                var blur = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian };
                blur.Freeze();
                visual.Effect = blur;
            }
            return visual;
        }

        /// <summary>
        /// 반사 층(맨 아래): 앞쪽 층(광선·본문 — 그림자는 비추지 않음)을 글자 아래로 뒤집어 비춘다.
        /// 글자 = 본문이 칠하는 모양의 경계(채우기 영역 + 켰으면 윤곽선 펜이 칠하는 곳 — 반사한 윤곽선이 잘리지 않게).
        /// 글자 아래 + 간격/2를 축으로 뒤집어 반사 첫 줄 = 글자 마지막 줄이고, <see cref="StyledTextLayout.ReflectionRect"/>(간격 × 효과 배율,
        /// 높이 = 글자 높이 × 크기%)로 잘라 위 → 아래로 불투명도 → 0이 되게 흐린다. 가로는 자르지 않는다(글자 옆으로 번진 광선도 비치게).
        /// 앞쪽 층은 Visual마다 효과(흐림)가 달려 있어 그림 묶음으로 다시 쓸 수 없으므로, 같은 크기의 중간 비트맵에 한 번 그려 그것을 뒤집는다.
        /// 사각형은 여백 계산(<see cref="StyledTextLayout.Spread"/>)과 같은 함수라 그림 가장자리에서 잘리지 않는다. 꺼졌거나 보이지 않으면 null.
        /// drawtext 대체 경로(그리개 없음·긴 글)는 반사를 그리지 못한다.
        /// </summary>
        private static Visual ReflectionLayer(IReadOnlyList<Visual> front, Geometry geometry, Rect ink, TextStyle style, double effectScale, Vector origin,
            int width, int height, CancellationToken cancellation)
        {
            var reflection = style.Reflection;
            if (!reflection.Enabled || !(reflection.Opacity > 0) || ink.IsEmpty)
                return null;
            var glyph = ink;
            var outline = OutlinePen(style.Outline, effectScale, null);
            if (outline != null)
                glyph.Union(geometry.GetRenderBounds(outline));
            glyph.Offset(origin);
            var rect = StyledTextLayout.ReflectionRect(reflection, effectScale, (glyph.Left, glyph.Top, glyph.Width, glyph.Height));
            if (!(rect.Height > 0))
                return null;
            var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            foreach (var layer in front)
            {
                cancellation.ThrowIfCancellationRequested();
                source.Render(layer);
            }
            source.Freeze();
            var clip = new RectangleGeometry(new Rect(0, rect.Top, width, rect.Height));
            clip.Freeze();
            // 알파만 쓰는 붓: 반사 위쪽 끝 1 → 아래쪽 끝 0(불투명도는 층 전체에 따로)
            var fade = new LinearGradientBrush(Colors.Black, Color.FromArgb(0, 0, 0, 0), new Point(0, rect.Top), new Point(0, rect.Top + rect.Height))
            {
                MappingMode = BrushMappingMode.Absolute
            };
            fade.Freeze();
            // y → (글자 아래 + 반사 위) − y: 글자 아래 줄이 반사 위 줄로
            var flip = new ScaleTransform(1, -1, 0, (glyph.Bottom + rect.Top) / 2);
            flip.Freeze();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushClip(clip);
                context.PushOpacity(reflection.Opacity);
                context.PushOpacityMask(fade);
                context.PushTransform(flip);
                context.DrawImage(source, new Rect(0, 0, width, height));
                context.Pop();
                context.Pop();
                context.Pop();
                context.Pop();
            }
            return visual;
        }

        /// <summary>
        /// 휘기: 글자 윤곽을 꺾은선으로 펴고(허용 오차 0.25px) 그림마다 점을 배치 상자 기준 0~1로 정규화해 <see cref="TextWarp.Map"/>으로 옮긴 뒤 픽셀로 되돌린다.
        /// 긴 직선도 휘게 변을 상자 폭의 1% 이하로 쪼갠다(<see cref="TextWarp.Subdivide"/>) — 가로세로 같은 길이로 쪼개야 한 줄짜리 긴 글에서
        /// 세로 변이 픽셀마다 쪼개지지 않는다(휘기는 세로 변을 곧게 두므로 쪼갤 필요도 없다). 채우기 규칙은 원래 것 — 글자 속 구멍이 그대로 남는다.
        /// </summary>
        private static GeometryGroup Warp(GeometryGroup geometry, TextWarpSetting warp, double boxWidth, double boxHeight, CancellationToken cancellation)
        {
            var flat = geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);
            var aspect = boxHeight / boxWidth;
            var maxLength = boxWidth * 0.01;
            var path = new PathGeometry { FillRule = geometry.FillRule };
            var points = new List<(double U, double V)>();
            foreach (var figure in flat.Figures)
            {
                cancellation.ThrowIfCancellationRequested();
                points.Clear();
                points.Add((figure.StartPoint.X, figure.StartPoint.Y));
                // 편 윤곽에는 직선 조각만 있다
                foreach (var segment in figure.Segments)
                {
                    if (segment is PolyLineSegment poly)
                    {
                        foreach (var p in poly.Points)
                            points.Add((p.X, p.Y));
                    }
                    else if (segment is LineSegment line)
                    {
                        points.Add((line.Point.X, line.Point.Y));
                    }
                }
                var divided = TextWarp.Subdivide(points, maxLength);
                var bent = new PathFigure { IsClosed = figure.IsClosed, IsFilled = figure.IsFilled };
                var rest = new PointCollection(divided.Count - 1);
                for (var i = 0; i < divided.Count; i++)
                {
                    var (u, v) = TextWarp.Map(warp.Kind, warp.Amount, divided[i].U / boxWidth, divided[i].V / boxHeight, aspect);
                    var point = new Point(u * boxWidth, v * boxHeight);
                    if (i == 0)
                        bent.StartPoint = point;
                    else
                        rest.Add(point);
                }
                bent.Segments.Add(new PolyLineSegment { Points = rest });
                path.Figures.Add(bent);
            }
            var result = new GeometryGroup { FillRule = geometry.FillRule };
            result.Children.Add(path);
            result.Freeze();
            return result;
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
        /// 각진 이음 한도는 <see cref="StyledTextLayout.MiterLimit"/>(여백 계산과 같은 값). paint가 있으면 윤곽선 색 대신 그 붓. 꺼졌거나 두께가 0이면 null.
        /// </summary>
        private static Pen OutlinePen(TextOutline outline, double effectScale, Brush paint)
        {
            if (!outline.Enabled)
                return null;
            // 효과 배율은 여백 계산(StyledTextLayout)과 같게 — 유한한 양수만, 아니면 0
            var thickness = outline.Width * (double.IsFinite(effectScale) && effectScale > 0 ? effectScale : 0) * 2;
            if (!(thickness > 0))
                return null;
            var pen = new Pen(paint ?? SolidBrush(outline.Color), thickness)
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
