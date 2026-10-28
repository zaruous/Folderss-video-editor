using System.Linq;
using System.Windows;
using System.Windows.Input;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미리 보기 틀을 옮길 때 붙기(#4, <see cref="PreviewSnap"/>): 화면 가운데·가장자리와 그 재생 헤드에 보이는 다른 클립의 가장자리·가운데에 붙고,
    /// 붙은 선에 안내선을 보인다. Alt를 누르는 동안은 붙지 않는다(타임라인과 같은 규칙). 여럿을 함께 옮기면 누른 클립을 기준으로 하고 무리는 붙을 선에서 뺀다.
    /// </summary>
    internal sealed partial class TransformOverlay
    {
        // 이번 끌기의 붙을 선(누를 때 비우고 처음 움직일 때 한 번 모은다 — 글자 상자를 재는 일이 끄는 동안 되풀이되지 않게)
        private PreviewSnap.Lines _snapLines;

        /// <summary>끌어 옮길 자리(x, y — 프로젝트 픽셀)를 붙여 _x·_y에 넣고 안내선을 보인다.</summary>
        private void SnapMove(double x, double y)
        {
            if ((Modifiers() & ModifierKeys.Alt) != 0)
            {
                _x = x;
                _y = y;
                ShowSnapGuides(null, null);
                return;
            }
            var project = _session.Project;
            var (zoom, _, _) = Screen();
            if (_snapLines == null)
            {
                var moving = _groupMoving ? _group.Select(c => c.Id) : new[] { _clip.Id };
                _snapLines = PreviewSnap.Targets(project, _session.Playhead, moving, c => TextSize(c, c.TextBoxWidth, c.TextBoxHeight));
            }
            // 옮기는 클립의 보이는 영역 크기·회전(지금 틀 — 크기는 화면 픽셀이라 프로젝트 픽셀로)
            var frame = Frame();
            var snapped = PreviewSnap.Move(project, x, y, frame.Width / zoom, frame.Height / zoom, frame.Rotation, _snapLines, SnapPixels / zoom);
            _x = snapped.X;
            _y = snapped.Y;
            ShowSnapGuides(snapped.GuideX, snapped.GuideY);
        }

        /// <summary>붙은 선(프로젝트 픽셀 — 세로선 x, 가로선 y)에 안내선. null이면 그 안내선을 숨긴다.</summary>
        private void ShowSnapGuides(double? x, double? y)
        {
            var project = _session.Project;
            var (zoom, left, top) = Screen();
            _guideX.Visibility = x.HasValue ? Visibility.Visible : Visibility.Collapsed;
            _guideY.Visibility = y.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (x.HasValue)
            {
                _guideX.X1 = _guideX.X2 = left + x.Value * zoom;
                _guideX.Y1 = top;
                _guideX.Y2 = top + project.Height * zoom;
            }
            if (y.HasValue)
            {
                _guideY.Y1 = _guideY.Y2 = top + y.Value * zoom;
                _guideY.X1 = left;
                _guideY.X2 = left + project.Width * zoom;
            }
        }

        /// <summary>보이는 안내선(화면 픽셀 — 세로선의 x, 가로선의 y, 숨었으면 null) — 시험용.</summary>
        internal (double? X, double? Y) GuidePositions()
        {
            return (_guideX.Visibility == Visibility.Visible ? _guideX.X1 : (double?)null, _guideY.Visibility == Visibility.Visible ? _guideY.Y1 : (double?)null);
        }
    }
}
