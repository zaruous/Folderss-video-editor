using System;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>프로젝트 해상도·프레임 레이트 바꾸기.</summary>
    public static class ProjectSettings
    {
        /// <summary>
        /// 프레임 레이트를 바꾼다. 위치·길이는 모두 프레임 정수라서 초 기준으로 다시 계산한다(재생 시간이 그대로 유지됨).
        /// 반올림으로 생긴 한 프레임 겹침은 뒤 클립을 밀어 없앤다.
        /// </summary>
        public static void ChangeRate(Project project, FrameRate rate)
        {
            var old = project.Rate;
            if (old.Equals(rate))
                return;
            long Convert(long frames)
            {
                return rate.ToFrames(old.ToSeconds(frames));
            }
            foreach (var clip in project.AllClips)
                ConvertClip(clip, old, rate);
            foreach (var marker in project.Markers)
                marker.Frame = Convert(marker.Frame);
            if (project.MarkIn.HasValue)
                project.MarkIn = Convert(project.MarkIn.Value);
            if (project.MarkOut.HasValue)
                project.MarkOut = Convert(project.MarkOut.Value);
            project.Rate = rate;
            // 반올림으로 밀린 클립의 링크된 짝이 따라간다
            var anchors = ClipLinks.Anchors(project);
            foreach (var track in project.AllTracks)
            {
                track.Sort();
                if (project.Magnetic && project.IsMain(track))
                {
                    TimelineEdits.Compact(track);
                    continue;
                }
                for (var i = 1; i < track.Clips.Count; i++)
                {
                    if (track.Clips[i].Start < track.Clips[i - 1].End)
                        track.Clips[i].Start = track.Clips[i - 1].End;
                }
            }
            ClipLinks.Follow(project, anchors);
        }

        /// <summary>클립 하나의 프레임 값을 from 레이트에서 to 레이트로(초 기준). 다른 프로젝트에 붙여 넣을 때도 쓴다.</summary>
        public static void ConvertClip(Clip clip, FrameRate from, FrameRate to)
        {
            if (from.Equals(to))
                return;
            long Convert(long frames)
            {
                return to.ToFrames(from.ToSeconds(frames));
            }
            var end = Convert(clip.End);
            clip.Start = Convert(clip.Start);
            clip.SourceIn = Convert(clip.SourceIn);
            clip.SourceOut = Math.Max(clip.SourceIn + 1, Convert(clip.SourceOut));
            if (clip.SourceLength > 0)
                clip.SourceLength = Math.Max(clip.SourceOut, Convert(clip.SourceLength));
            clip.FadeIn = Convert(clip.FadeIn);
            clip.FadeOut = Convert(clip.FadeOut);
            clip.TransitionIn = Convert(clip.TransitionIn);
            if (clip.Animations != null)
            {
                foreach (var animation in clip.Animations)
                {
                    animation.Duration = Math.Max(1, Math.Min(ClipAnimations.MaxFrames, Convert(animation.Duration)));
                    animation.Delay = Math.Min(ClipAnimations.MaxFrames, Convert(animation.Delay));
                }
            }
            // 타임라인 끝이 초 기준 위치에 오도록 원본 끝을 맞춘다(속도 반올림 보정)
            if (end > clip.Start)
                TimelineEdits.SetDuration(clip, end - clip.Start);
        }

        public static void ChangeSize(Project project, int width, int height)
        {
            if (width < 16 || height < 16 || width > 8192 || height > 8192)
                throw new EditException("해상도는 16~8192 사이여야 합니다.");
            project.Width = width - width % 2;
            project.Height = height - height % 2;
        }

        /// <summary>
        /// 빈 프로젝트에 처음 동영상을 넣을 때 해상도·프레임 레이트를 그 동영상에 맞춘다.
        /// 맞췄으면 true. 이미 정했거나 클립이 있으면 그대로.
        /// </summary>
        public static bool AdoptFirstVideo(Project project, MediaInfo media)
        {
            if (project.SettingsChosen || project.AllClips.Any() || media.Kind != MediaKind.Video || media.Width < 16 || media.Height < 16)
                return false;
            project.Width = media.Width - media.Width % 2;
            project.Height = media.Height - media.Height % 2;
            if (FrameRate.TryParse(media.FrameRate, out var rate) && rate.Fps >= 10 && rate.Fps <= 120)
                project.Rate = rate;
            project.SettingsChosen = true;
            return true;
        }
    }
}
