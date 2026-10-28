using System;
using System.Windows;
using System.Windows.Media;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 읽은 스티커(<see cref="Sticker"/>)를 WPF 그림으로: viewBox를 상자(픽셀)에 늘여 경로마다 제 색으로 채우고 선을 긋는다(벡터 — 크게 그려도 그 크기로 다시 그림).
    /// 그리개(스티커 PNG)와 [요소] 탭 칸 그림이 같이 쓴다.
    /// </summary>
    internal static class StickerDrawing
    {
        /// <summary>
        /// 상자 width×height(픽셀)에 늘인 그림과 칠한 곳 전체(채우기 + 선이 칠하는 곳 — 꾸미기의 윤곽선·그림자·광선이 그 둘레에 그린다).
        /// 선 두께는 가로·세로 배율의 기하 평균만큼 커진다.
        /// </summary>
        public static (Drawing Drawing, Geometry Silhouette) Build(Sticker sticker, double width, double height)
        {
            double sx = width / sticker.Width, sy = height / sticker.Height;
            var scale = Math.Sqrt(Math.Abs(sx * sy));
            Point Map(double x, double y) => new Point((x - sticker.MinX) * sx, (y - sticker.MinY) * sy);
            var drawing = new DrawingGroup();
            var silhouette = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var path in sticker.Paths)
            {
                var geometry = new PathGeometry { FillRule = path.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
                foreach (var figure in path.Figures)
                {
                    var shape = new PathFigure { StartPoint = Map(figure.StartX, figure.StartY), IsClosed = figure.Closed, IsFilled = true };
                    foreach (var segment in figure.Segments)
                    {
                        if (segment.Cubic)
                            shape.Segments.Add(new BezierSegment(Map(segment.X1, segment.Y1), Map(segment.X2, segment.Y2), Map(segment.X, segment.Y), true));
                        else
                            shape.Segments.Add(new LineSegment(Map(segment.X, segment.Y), true));
                    }
                    geometry.Figures.Add(shape);
                }
                geometry.Freeze();
                var fill = path.Fill == null ? null : Brush(path.Fill);
                Pen pen = null;
                if (path.Stroke != null && path.StrokeWidth * scale > 0)
                {
                    var cap = path.LineCap == "round" ? PenLineCap.Round : path.LineCap == "square" ? PenLineCap.Square : PenLineCap.Flat;
                    pen = new Pen(Brush(path.Stroke), path.StrokeWidth * scale)
                    {
                        StartLineCap = cap,
                        EndLineCap = cap,
                        LineJoin = path.LineJoin == "round" ? PenLineJoin.Round : path.LineJoin == "bevel" ? PenLineJoin.Bevel : PenLineJoin.Miter,
                        MiterLimit = path.MiterLimit
                    };
                    pen.Freeze();
                }
                drawing.Children.Add(new GeometryDrawing(fill, pen, geometry));
                if (fill != null)
                    silhouette.Children.Add(geometry);
                if (pen != null)
                    silhouette.Children.Add(geometry.GetWidenedPathGeometry(pen));
            }
            drawing.Freeze();
            silhouette.Freeze();
            return (drawing, silhouette);
        }

        private static Brush Brush(string color)
        {
            var brush = new SolidColorBrush(UiKit.ParseColor(color, Colors.Black));
            brush.Freeze();
            return brush;
        }
    }
}
