using System;
using System.Windows;
using System.Windows.Controls;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 효과 썸네일 칸 놓기([효과] 탭의 동영상 효과, [전환] 탭의 전환 효과 목록): 폭에 들어가는 만큼 칸 수를 정하고 남는 폭을 칸에 고루 나눈다 —
    /// 좁은 패널에서도 오른쪽이 비지 않고 모든 칸이 같은 크기다. 줄 높이는 그 줄에서 가장 높은 칸.
    /// </summary>
    internal sealed class TileGrid : Panel
    {
        /// <summary>칸의 가장 좁은 폭과 가장 넓은 폭(넓은 패널에서는 칸이 커지기보다 칸 수가 는다).</summary>
        public const double MinCell = 104;
        public const double MaxCell = 150;

        private int _columns = 1;
        private double _cell = MinCell;

        public static ItemsPanelTemplate PanelTemplate()
        {
            return new ItemsPanelTemplate(new FrameworkElementFactory(typeof(TileGrid)));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var children = InternalChildren;
            var width = double.IsInfinity(availableSize.Width) ? MinCell * Math.Max(1, children.Count) : availableSize.Width;
            _columns = Math.Max(1, (int)Math.Floor(width / MinCell));
            _cell = Math.Max(1, Math.Min(MaxCell, width / _columns));
            double height = 0, row = 0;
            for (var i = 0; i < children.Count; i++)
            {
                children[i].Measure(new Size(_cell, double.PositiveInfinity));
                row = Math.Max(row, children[i].DesiredSize.Height);
                if (i % _columns == _columns - 1 || i == children.Count - 1)
                {
                    height += row;
                    row = 0;
                }
            }
            return new Size(Math.Min(_columns, Math.Max(1, children.Count)) * _cell, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var children = InternalChildren;
            double top = 0;
            for (var first = 0; first < children.Count; first += _columns)
            {
                var last = Math.Min(children.Count, first + _columns);
                double row = 0;
                for (var i = first; i < last; i++)
                    row = Math.Max(row, children[i].DesiredSize.Height);
                for (var i = first; i < last; i++)
                    children[i].Arrange(new Rect((i - first) * _cell, top, _cell, row));
                top += row;
            }
            return finalSize;
        }
    }

    /// <summary>썸네일 틀: 받은 폭에 16:9 높이(칸 폭을 따라 썸네일이 커지고 작아진다). 폭을 모르면 96px.</summary>
    internal sealed class ThumbFrame : Border
    {
        protected override Size MeasureOverride(Size constraint)
        {
            var width = double.IsInfinity(constraint.Width) ? 96 : constraint.Width;
            var size = new Size(width, Math.Round(width * 9 / 16));
            base.MeasureOverride(size);
            return size;
        }
    }
}
