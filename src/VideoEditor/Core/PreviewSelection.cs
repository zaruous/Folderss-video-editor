using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 미리 보기에서 여러 클립을 함께 다루기(#34): Ctrl+누르기로 고른 클립 가운데 미리 보기 틀을 그릴 수 있는 것을 고르고, 함께 옮긴다.
    /// 옮기기는 부르는 쪽이 편집 한 번(<see cref="EditorSession.Execute(Action{Project})"/>) 안에서 부른다 — 실행 취소 한 단계, 잠긴 트랙이면 모두 되돌린다.
    /// </summary>
    public static class PreviewSelection
    {
        /// <summary>
        /// 미리 보기에 틀을 그릴 수 있는 클립: 재생 헤드(frame)에 보이는 화면 클립이고 트랙이 동영상 트랙(숨김·잠금·자막 트랙 아님 — 자막 큐는 libass가 그린다).
        /// </summary>
        public static bool IsFramable(Project project, Clip clip, long frame)
        {
            var track = clip == null ? null : project.TrackOf(clip);
            return track != null && clip.IsVisual && track.Kind == TrackKind.Video && !track.Hidden && !track.Locked && !track.Subtitles && clip.Contains(frame);
        }

        /// <summary>고른 클립 가운데 함께 옮길 것(<see cref="IsFramable"/>) — 렌더 그래프의 쌓는 순서(아래 트랙 → 위 트랙, 같은 트랙은 먼저 시작한 것부터).</summary>
        public static List<Clip> Movable(Project project, IEnumerable<Clip> selected, long frame)
        {
            var chosen = new HashSet<Clip>(selected ?? Enumerable.Empty<Clip>());
            return project.VideoTracks.SelectMany(t => t.Clips.OrderBy(c => c.Start)).Where(c => chosen.Contains(c) && IsFramable(project, c, frame)).ToList();
        }

        /// <summary>
        /// clips를 (dx, dy)(프로젝트 픽셀)만큼 함께 옮긴다 — 자리는 정수로 반올림. 잠긴 트랙의 클립이 있으면 <see cref="EditException"/>(부르는 쪽 편집이 모두 되돌린다).
        /// 잘못된 거리(NaN·무한대)는 0.
        /// </summary>
        public static void Move(Project project, IEnumerable<Clip> clips, double dx, double dy)
        {
            dx = double.IsFinite(dx) ? dx : 0;
            dy = double.IsFinite(dy) ? dy : 0;
            foreach (var clip in clips ?? Enumerable.Empty<Clip>())
            {
                TimelineEdits.EnsureEditable(project.TrackOf(clip));
                clip.X = Math.Round(clip.X + dx);
                clip.Y = Math.Round(clip.Y + dy);
            }
        }
    }
}
