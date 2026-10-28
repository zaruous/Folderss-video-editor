using System;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미리 보기 틀과 사용자 키프레임(#4): 틀은 재생 헤드 시각의 값(키프레임 사이 값)으로 그리고, 끌어 바꾼 값에 키프레임이 있으면
    /// 그 시각의 키프레임을 고치거나 새로 만든다(없으면 예전처럼 고정 값). 바꾸지 않은 값은 건드리지 않는다 — 옮기기만 했는데 크기 키프레임이 생기지 않게.
    /// </summary>
    internal sealed partial class TransformOverlay
    {
        /// <summary>재생 헤드 시각의 모습(키프레임이 없으면 클립 그대로).</summary>
        private Clip Pose(Clip clip)
        {
            return ClipKeyframes.Pose(clip, KeyFrame(clip));
        }

        /// <summary>재생 헤드의 클립 안 시각(클립 안으로 자름).</summary>
        private long KeyFrame(Clip clip)
        {
            return Math.Max(0, Math.Min(clip.Duration - 1, _session.Playhead - clip.Start));
        }

        /// <summary>
        /// 위치를 바꾼다: 키프레임이 있으면 재생 헤드 시각의 키프레임(changed일 때만), 없으면 고정 값(예전처럼 놓을 때마다 반올림한 값으로).
        /// </summary>
        private void SetPosition(Clip clip, double x, double y, bool changed)
        {
            if (changed || !ClipKeyframes.Has(clip, KeyframeProperty.Position))
                ClipKeyframes.SetPosition(clip, KeyFrame(clip), x, y);
        }

        /// <summary>크기·회전을 바꾼다(<see cref="SetPosition"/>과 같은 규칙).</summary>
        private void SetValue(Clip clip, KeyframeProperty property, double value, bool changed)
        {
            if (changed || !ClipKeyframes.Has(clip, property))
                ClipKeyframes.Set(clip, property, KeyFrame(clip), value);
        }
    }
}
