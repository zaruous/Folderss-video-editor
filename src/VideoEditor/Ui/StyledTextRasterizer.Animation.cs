using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 단어·글자별 애니메이션 한 장(#4): 글자 요소(빈칸이 아닌 것 — 한글은 음절)마다 윤곽을 따로 만들어 그 장의 모습(옮김·크기·회전·불투명도·닦아내기)으로 그린다.
    /// - 자리: 줄 통째 조각(자간 0)은 줄 안 자리(BuildHighlightGeometry)에 요소를 하나씩 놓는다 — 커닝 자리 그대로. 오른쪽→왼쪽 글자가 든 줄은 줄 통째로 첫 요소와 같이 움직인다.
    /// - 크기: 한 클립의 모든 장이 같은 크기(이어 붙이기) — 움직이는 모든 장(<see cref="TextAnimationFrame.Frames"/>)에서 요소가 가는 범위를 모아 대칭으로 넓힌다.
    /// - 칠하기: 정지 그림과 같은 층(반사 → 그림자 → 광선 → 본문). 그라데이션은 글 전체 상자 기준(요소마다 따로 칠하지 않게)이고 요소와 함께 움직인다.
    ///   같은 모습의 요소는 한 묶음으로 그린다 — 다 나타난 장은 묶음 하나라 그림자·광선이 정지 그림처럼 한 번에 흐려진다.
    /// </summary>
    internal sealed partial class StyledTextRasterizer
    {
        /// <summary>요소 하나: 윤곽(배치 상자 좌표)과 그 경계.</summary>
        private sealed class Element
        {
            public Geometry Geometry;
            public Rect Bounds;
        }

        private static void DrawAnimated(TextImageRequest request, string pngPath, CancellationToken cancellation)
        {
            var style = request.Style?.Clone() ?? new TextStyle();
            style.Normalize();
            var size = Clamp(request.FontPixelSize, 0.5, 4000, 1);
            var arranged = Arrange(request.Text, style, size, request.WrapWidth, request.BoxHeight, request.VerticalAlign);
            var elements = Elements(arranged, style, size, cancellation);
            if (style.Warp.Kind != WarpKind.None && style.Warp.Amount != 0 && arranged.Block.Width > 0 && arranged.Block.Height > 0)
            {
                foreach (var element in elements.Where(e => e.Geometry != null))
                {
                    var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                    group.Children.Add(element.Geometry);
                    element.Geometry = Warp(group, style.Warp, arranged.Block, cancellation);
                    element.Bounds = element.Geometry.Bounds;
                }
            }
            var all = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var element in elements.Where(e => e.Geometry != null))
                all.Children.Add(element.Geometry);
            all.Freeze();
            var ink = all.Bounds;
            var animation = request.Animation;
            var units = Units(request.Text, animation, elements);

            // 그림 크기: 상자·정지 윤곽에 움직이는 모든 장의 범위를 더해 대칭으로(모든 장이 같은 크기)
            var centerX = arranged.BoxWidth / 2;
            var centerY = arranged.BoxHeight / 2;
            var reach = ink;
            foreach (var frame in animation.Frames())
            {
                cancellation.ThrowIfCancellationRequested();
                var poses = Poses(animation, units, elements.Count, size, frame);
                for (var i = 0; i < elements.Count; i++)
                {
                    if (elements[i].Geometry == null || poses[i].Pose.IsRest)
                        continue;
                    var moved = Transform(poses[i].Pose, poses[i].Center).TransformBounds(elements[i].Bounds);
                    if (!moved.IsEmpty)
                        reach.Union(moved);
                }
            }
            var halfWidth = Math.Max(centerX, reach.IsEmpty ? 0 : Math.Max(centerX - reach.Left, reach.Right - centerX));
            var halfHeight = Math.Max(centerY, reach.IsEmpty ? 0 : Math.Max(centerY - reach.Top, reach.Bottom - centerY));
            var layout = StyledTextLayout.Fit(halfWidth * 2, halfHeight * 2, style, request.EffectScale, request.CanvasWidth, request.CanvasHeight);
            if ((double)layout.Width * layout.Height > StyledTextLayout.MaxPixels)
                throw new InvalidOperationException("꾸민 글자 그림이 너무 큽니다: " + layout.Width + "×" + layout.Height);
            var origin = new Vector(layout.OriginX + halfWidth - centerX, layout.OriginY + halfHeight - centerY);

            // 이 장: 같은 모습의 요소끼리 묶는다(보이지 않는 요소는 뺀다)
            var current = Poses(animation, units, elements.Count, size, animation.Frame);
            var groups = new List<(UnitPose Pose, Point Center, GeometryGroup Geometry, Rect Bounds)>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < elements.Count; i++)
            {
                var (pose, center) = current[i];
                if (elements[i].Geometry == null || pose.Opacity <= 0 || pose.Visible <= 0 || pose.Scale <= 0)
                    continue;
                var key = pose.IsRest ? "rest" : string.Join("|", new[] { pose.X, pose.Y, pose.Scale, pose.Rotation, pose.Opacity, pose.Visible, pose.WipeX, pose.WipeY, center.X, center.Y }
                    .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
                if (!index.TryGetValue(key, out var at))
                {
                    at = groups.Count;
                    index[key] = at;
                    groups.Add((pose, center, new GeometryGroup { FillRule = FillRule.Nonzero }, Rect.Empty));
                }
                var group = groups[at];
                group.Geometry.Children.Add(elements[i].Geometry);
                // 닦아내기는 단위 상자 기준
                group.Bounds = Rect.Union(group.Bounds, units.Boxes[i]);
                groups[at] = group;
            }

            var shadows = new List<Visual>();
            var front = new List<Visual>();
            var body = new DrawingVisual();
            var fill = Absolute(FillBrush(style.Fill), ink);
            var outline = OutlinePen(style.Outline, request.EffectScale, null);
            using (var context = body.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(origin.X, origin.Y));
                foreach (var (pose, center, geometry, bounds) in groups)
                {
                    cancellation.ThrowIfCancellationRequested();
                    geometry.Freeze();
                    var transform = Transform(pose, center);
                    var clip = Wipe(pose, bounds);
                    // 그림자·광선은 옮긴 윤곽으로 그리개의 층 함수를 그대로 쓴다(층 하나에 흐림 하나)
                    Geometry moved = clip == null ? geometry : new CombinedGeometry(GeometryCombineMode.Intersect, geometry, new RectangleGeometry(clip.Value));
                    moved = moved.Clone();
                    moved.Transform = transform;
                    moved.Freeze();
                    var movedInk = moved.Bounds;
                    AddLayer(shadows, ShadowLayer(moved, movedInk, style, request.EffectScale, origin), pose.Opacity);
                    AddLayer(front, GlowLayer(moved, movedInk, style, request.EffectScale, origin), pose.Opacity);
                    context.PushTransform(transform);
                    if (clip != null)
                        context.PushClip(new RectangleGeometry(clip.Value));
                    context.PushOpacity(pose.Opacity);
                    if (outline != null && !geometry.Bounds.IsEmpty)
                        DrawOutline(context, outline, geometry, style.Fill);
                    if (fill != null)
                        context.DrawGeometry(fill, null, geometry);
                    context.Pop();
                    if (clip != null)
                        context.Pop();
                    context.Pop();
                }
                context.Pop();
            }
            front.Add(body);
            var layers = new List<Visual>(shadows);
            layers.AddRange(front);
            // 반사는 정지 윤곽 아래를 축으로(요소가 움직여도 바닥은 그대로)
            var reflection = ReflectionLayer(front, all, ink, style, request.EffectScale, origin, layout.Width, layout.Height, cancellation);
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

        /// <summary>층 하나(없으면 건너뜀)를 불투명도와 함께 더한다.</summary>
        private static void AddLayer(List<Visual> layers, Visual layer, double opacity)
        {
            if (layer == null)
                return;
            if (opacity >= 1)
            {
                layers.Add(layer);
                return;
            }
            var container = new ContainerVisual { Opacity = opacity };
            container.Children.Add(layer);
            layers.Add(container);
        }

        /// <summary>단위 가운데를 기준으로 크기·회전, 그다음 옮김.</summary>
        private static Transform Transform(UnitPose pose, Point center)
        {
            var group = new TransformGroup();
            if (pose.Scale != 1)
                group.Children.Add(new ScaleTransform(pose.Scale, pose.Scale, center.X, center.Y));
            if (pose.Rotation != 0)
                group.Children.Add(new RotateTransform(pose.Rotation, center.X, center.Y));
            if (pose.X != 0 || pose.Y != 0)
                group.Children.Add(new TranslateTransform(pose.X, pose.Y));
            group.Freeze();
            return group;
        }

        /// <summary>닦아내기로 보이는 사각형(단위 상자 좌표, 다 보이면 null).</summary>
        private static Rect? Wipe(UnitPose pose, Rect box)
        {
            if (pose.Visible >= 1 || box.IsEmpty)
                return null;
            var rect = box;
            // 상자 밖으로 번진 획(기울임 등)까지 덮게 1px씩 넓힌 뒤 보이는 쪽만
            rect.Inflate(1, 1);
            if (pose.WipeX != 0)
            {
                var width = rect.Width * pose.Visible;
                rect = new Rect(pose.WipeX < 0 ? rect.Left : rect.Right - width, rect.Top, width, rect.Height);
            }
            if (pose.WipeY != 0)
            {
                var height = rect.Height * pose.Visible;
                rect = new Rect(rect.Left, pose.WipeY < 0 ? rect.Top : rect.Bottom - height, rect.Width, height);
            }
            return rect;
        }

        /// <summary>글 전체 상자 기준 그라데이션(요소를 따로 칠해도 정지 그림과 같은 색) — 단색·없음은 그대로.</summary>
        private static Brush Absolute(Brush brush, Rect bounds)
        {
            if (!(brush is GradientBrush gradient) || gradient.MappingMode != BrushMappingMode.RelativeToBoundingBox || bounds.IsEmpty)
                return brush;
            var copy = gradient.Clone();
            copy.MappingMode = BrushMappingMode.Absolute;
            Point Map(Point p) => new Point(bounds.X + p.X * bounds.Width, bounds.Y + p.Y * bounds.Height);
            if (copy is LinearGradientBrush linear)
            {
                linear.StartPoint = Map(linear.StartPoint);
                linear.EndPoint = Map(linear.EndPoint);
            }
            else if (copy is RadialGradientBrush radial)
            {
                radial.Center = Map(radial.Center);
                radial.GradientOrigin = Map(radial.GradientOrigin);
                radial.RadiusX *= bounds.Width;
                radial.RadiusY *= bounds.Height;
            }
            copy.Freeze();
            return copy;
        }

        /// <summary>
        /// 줄마다 빈칸이 아닌 글자 요소의 윤곽(<see cref="TextUnits.Elements"/>와 같은 차례). 글자 단위 조각(자간)은 조각 그대로,
        /// 줄 통째 조각은 요소를 따로 만들어 줄 안 자리에 기준선을 맞춰 놓는다. 오른쪽→왼쪽 글자가 든 조각은 첫 요소가 조각 통째를 갖는다.
        /// </summary>
        private static List<Element> Elements(Arrangement arranged, TextStyle style, double size, CancellationToken cancellation)
        {
            var typeface = new Typeface(new FontFamily(Resolve(style.FontFamily)),
                style.Italic ? FontStyles.Italic : FontStyles.Normal, style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var result = new List<Element>();
            foreach (var line in arranged.Lines)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var (formatted, offset) in line.Pieces)
                {
                    var text = formatted.Text;
                    var parts = new List<(int Start, int Length)>();
                    var enumerator = StringInfo.GetTextElementEnumerator(text);
                    while (enumerator.MoveNext())
                    {
                        var element = enumerator.GetTextElement();
                        if (!TextUnits.IsBlank(element))
                            parts.Add((enumerator.ElementIndex, element.Length));
                    }
                    if (parts.Count == 0)
                        continue;
                    var whole = new Point(line.X + offset, line.Top + line.Baseline - formatted.Baseline);
                    if (parts.Count == 1 && parts[0].Length == text.Length || HasRightToLeft(text))
                    {
                        var geometry = formatted.BuildGeometry(whole);
                        result.Add(new Element { Geometry = geometry, Bounds = geometry.Bounds });
                        for (var i = 1; i < parts.Count; i++)
                            result.Add(new Element { Bounds = Rect.Empty });
                        continue;
                    }
                    foreach (var (start, length) in parts)
                    {
                        var piece = new FormattedText(text.Substring(start, length), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
                        var highlight = formatted.BuildHighlightGeometry(new Point(0, 0), start, length);
                        var x = highlight == null || highlight.Bounds.IsEmpty ? 0 : highlight.Bounds.Left;
                        var geometry = piece.BuildGeometry(new Point(line.X + offset + x, line.Top + line.Baseline - piece.Baseline));
                        result.Add(new Element { Geometry = geometry, Bounds = geometry.Bounds });
                    }
                }
            }
            return result;
        }

        /// <summary>요소마다의 단위 번호(나타나기·사라지기)와 단위 가운데, 단위 상자(닦아내기 기준).</summary>
        private sealed class UnitLayout
        {
            public int[] In;
            public int[] Out;
            public Point[] InCenters;
            public Point[] OutCenters;
            public Rect[] Boxes;
        }

        private static UnitLayout Units(string text, TextAnimationFrame animation, List<Element> elements)
        {
            var (inUnits, outUnits) = animation.Units(text);
            var layout = new UnitLayout { In = inUnits, Out = outUnits, Boxes = new Rect[elements.Count] };
            Rect[] Boxes(int[] units)
            {
                if (units == null)
                    return null;
                var boxes = Enumerable.Repeat(Rect.Empty, units.Length == 0 ? 0 : units.Max() + 1).ToArray();
                for (var i = 0; i < elements.Count; i++)
                {
                    var unit = UnitOf(units, i);
                    if (unit >= 0 && !elements[i].Bounds.IsEmpty)
                        boxes[unit] = Rect.Union(boxes[unit], elements[i].Bounds);
                }
                return boxes;
            }
            var inBoxes = Boxes(inUnits);
            var outBoxes = Boxes(outUnits);
            Point Center(Rect box) => box.IsEmpty ? new Point() : new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            layout.InCenters = inBoxes?.Select(Center).ToArray();
            layout.OutCenters = outBoxes?.Select(Center).ToArray();
            for (var i = 0; i < elements.Count; i++)
            {
                // 닦아내기 상자: 움직이는 쪽(나타나기를 먼저)의 단위 상자
                var boxes = inBoxes ?? outBoxes;
                var units = inUnits ?? outUnits;
                var unit = UnitOf(units, i);
                layout.Boxes[i] = unit >= 0 ? boxes[unit] : elements[i].Bounds;
            }
            return layout;
        }

        /// <summary>요소 i의 단위(글과 그리개의 요소 수가 다르면 마지막 단위).</summary>
        private static int UnitOf(int[] units, int i)
        {
            return units == null || units.Length == 0 ? -1 : units[Math.Min(i, units.Length - 1)];
        }

        /// <summary>
        /// frame 장에서 요소마다의 모습과 그 모습의 기준(단위 가운데). 나타나기와 사라지기 구간은 겹치지 않으므로 움직이는 쪽 하나를 쓴다
        /// (둘 다 쉬면 그대로).
        /// </summary>
        private static (UnitPose Pose, Point Center)[] Poses(TextAnimationFrame animation, UnitLayout units, int count, double size, long frame)
        {
            var result = new (UnitPose, Point)[count];
            var inCount = units.InCenters?.Length ?? 0;
            var outCount = units.OutCenters?.Length ?? 0;
            var inProgress = TextAnimationFrame.Progress(frame, animation.InStart, animation.InLength);
            var outProgress = TextAnimationFrame.Progress(frame, animation.OutStart, animation.OutLength);
            for (var i = 0; i < count; i++)
            {
                var pose = UnitPose.Rest;
                var center = new Point();
                if (animation.In != null)
                {
                    var unit = UnitOf(units.In, i);
                    var appear = AnimationMotion.InPose(animation.In, inProgress, unit, inCount, size);
                    if (!appear.IsRest)
                    {
                        pose = appear;
                        center = units.InCenters[unit];
                    }
                }
                if (animation.Out != null)
                {
                    var unit = UnitOf(units.Out, i);
                    var vanish = AnimationMotion.OutPose(animation.Out, outProgress, unit, outCount, size);
                    if (!vanish.IsRest)
                    {
                        center = pose.IsRest ? units.OutCenters[unit] : center;
                        pose = pose.Then(vanish);
                    }
                }
                result[i] = (pose, center);
            }
            return result;
        }
    }
}
