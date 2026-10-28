using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미리 보기 틀의 회전 손잡이(#4): 틀 위쪽 가운데에서 바깥으로 띄운 둥근 점(줄기로 이어짐). 끌면 틀 가운데를 두고 돌린다 —
    /// 15° 단위로 붙고 Shift를 누르는 동안은 자유롭게(0.1°). 놓을 때 한 번 편집(실행 취소 한 단계), Esc는 취소, 끄는 동안 상태 줄에 각도.
    /// 점이 미리 보기 밖으로 나가면(화면을 꽉 채운 클립 등) 틀 안쪽으로 띄운다.
    /// </summary>
    internal sealed partial class TransformOverlay
    {
        // 회전 손잡이의 Hit 번호와 끌기 번호(_drag)
        private const int RotationHit = 20;
        private const int RotateDrag = 6;
        // 틀 위쪽 옆에서 손잡이까지(화면 픽셀)와 붙는 각도 단위(도)
        private const double RotationReach = 24;
        private const double RotationStep = 15;
        private readonly Line _rotationStem = new Line { StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Ellipse _rotationKnob = new Ellipse { Width = Handle + 2, Height = Handle + 2, Fill = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        // 끄는 동안: 돌리는 가운데(화면 픽셀), 마지막 포인터 각도, 끌기 시작부터 돈 각도(한 바퀴를 넘어도 이어서 셈)
        private Point _rotateCenter;
        private double _rotateLast, _rotateTurn;

        /// <summary>시험용 수식 키(null이면 지금 누른 키) — 리플렉션으로 Shift·Alt를 누른 것처럼 끈다.</summary>
        internal ModifierKeys? TestModifiers { get; set; }

        private ModifierKeys Modifiers()
        {
            return TestModifiers ?? Keyboard.Modifiers;
        }

        /// <summary>손잡이를 만든다(생성자에서 — 틀·손잡이 위에 그린다).</summary>
        private void CreateRotationHandle()
        {
            _rotationStem.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _rotationKnob.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_rotationKnob, "TransformRotationHandle");
            Children.Add(_rotationStem);
            Children.Add(_rotationKnob);
        }

        private void ShowRotationHandle(bool visible)
        {
            _rotationStem.Visibility = _rotationKnob.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>손잡이 자리(화면 픽셀)와 줄기가 시작하는 위쪽 옆 가운데. 틀이 없으면 null.</summary>
        private (Point Knob, Point Base)? RotationPoints()
        {
            if (_clip == null)
                return null;
            var frame = Frame();
            var corners = Corners(frame);
            var top = Edges(corners)[0];
            // 클립의 위쪽(시계 방향 회전, y는 아래로)
            var radians = frame.Rotation * Math.PI / 180;
            var up = new Vector(Math.Sin(radians), -Math.Cos(radians));
            var knob = top + up * RotationReach;
            if (knob.X < 0 || knob.Y < 0 || knob.X > ActualWidth || knob.Y > ActualHeight)
                knob = top - up * RotationReach;
            return (knob, top);
        }

        private void LayoutRotationHandle()
        {
            var points = RotationPoints();
            if (points == null)
                return;
            var (knob, top) = points.Value;
            _rotationStem.X1 = top.X;
            _rotationStem.Y1 = top.Y;
            _rotationStem.X2 = knob.X;
            _rotationStem.Y2 = knob.Y;
            SetLeft(_rotationKnob, knob.X - _rotationKnob.Width / 2);
            SetTop(_rotationKnob, knob.Y - _rotationKnob.Height / 2);
        }

        /// <summary>점이 회전 손잡이 위인지(둥근 점 크기 + 3px).</summary>
        private bool HitRotation(Point point)
        {
            var points = RotationPoints();
            return points != null && (point - points.Value.Knob).Length <= _rotationKnob.Width / 2 + 3;
        }

        /// <summary>회전 손잡이 자리(화면 픽셀) — 시험용. 틀이 없으면 null.</summary>
        internal Point? RotationHandlePoint()
        {
            return RotationPoints()?.Knob;
        }

        /// <summary>회전 끌기를 시작한다(PointerDown에서 — 시작 값은 이미 잡아 둠).</summary>
        private void StartRotate(Point point)
        {
            _rotateCenter = Frame().Center;
            _rotateLast = Math.Atan2(point.Y - _rotateCenter.Y, point.X - _rotateCenter.X) * 180 / Math.PI;
            _rotateTurn = 0;
        }

        /// <summary>끄는 동안: 포인터가 가운데를 돈 만큼 돌린다(15° 단위, Shift면 0.1°). 가운데에 너무 가까우면 각도가 튀므로 건너뛴다.</summary>
        private void RotateMove(Point point)
        {
            var offset = point - _rotateCenter;
            if (offset.Length < 3)
                return;
            var angle = Math.Atan2(offset.Y, offset.X) * 180 / Math.PI;
            var delta = ((angle - _rotateLast) % 360 + 540) % 360 - 180;
            _rotateLast = angle;
            _rotateTurn += delta;
            var raw = _startRotation + _rotateTurn;
            _rotation = (Modifiers() & ModifierKeys.Shift) != 0 ? Math.Round(raw, 1) : Math.Round(raw / RotationStep) * RotationStep;
        }

        private string RotateStatus()
        {
            return "회전 " + UiKit.Number(_rotation) + "° (15° 단위, Shift는 자유롭게 · 놓으면 바뀝니다, Esc는 취소)";
        }

        /// <summary>회전 손잡이를 놓음: 바뀌었으면 한 번 편집한다(실행 취소 한 단계).</summary>
        private bool RotateUp()
        {
            var (id, rotation) = (_clip.Id, _rotation);
            var moved = _pointerMoved && rotation != _startRotation;
            EndDrag();
            if (!moved)
                return true;
            _commands.EditClip(id, (p, c) => SetValue(c, KeyframeProperty.Rotation, rotation, true), null);
            _commands.Status("회전 " + UiKit.Number(rotation) + "°");
            return true;
        }
    }
}
