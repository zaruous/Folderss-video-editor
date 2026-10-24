using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>사용자에게 보일 편집 거부 사유(잠긴 트랙, 겹침 등). 편집은 적용되지 않는다.</summary>
    public sealed class EditException : Exception
    {
        public EditException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 타임라인 편집 연산(화면과 무관한 순수 로직). 규칙:
    /// - 한 트랙의 클립은 겹치지 않는다. 겹치게 놓으면 겹친 부분을 덮어쓴다(잘라 냄).
    /// - 주 트랙(동영상 1)은 마그네틱이 켜져 있으면 빈틈 없이 0부터 이어진다 — 넣으면 뒤가 밀리고, 빼거나 줄이면 당겨진다.
    /// - 잠긴 트랙은 고칠 수 없다(EditException).
    /// </summary>
    public static class TimelineEdits
    {
        public const double MinSpeed = 0.1;
        public const double MaxSpeed = 10;

        // ---------- 만들기 ----------

        /// <summary>미디어로 클립을 만든다. 이미지는 imageFrames 길이(원본 제한 없음).</summary>
        public static Clip CreateClip(MediaInfo media, FrameRate rate, long start, long imageFrames)
        {
            var clip = new Clip
            {
                Kind = ClipKind.Media,
                Path = media.Path,
                MediaKind = media.Kind,
                HasVideo = media.Kind != MediaKind.Audio && media.HasVideo,
                HasAudio = media.HasAudio,
                AudioChannels = media.AudioChannels,
                Start = Math.Max(0, start)
            };
            if (media.Kind == MediaKind.Image)
            {
                clip.HasVideo = true;
                clip.SourceOut = Math.Max(1, imageFrames);
            }
            else
            {
                var length = Math.Max(1, rate.ToFrames(media.DurationSeconds));
                clip.SourceLength = length;
                clip.SourceOut = length;
            }
            return clip;
        }

        public static Clip CreateText(string text, long start, long frames)
        {
            return new Clip
            {
                Kind = ClipKind.Text,
                Text = text,
                HasVideo = true,
                Start = Math.Max(0, start),
                SourceOut = Math.Max(1, frames)
            };
        }

        /// <summary>오디오 트랙에 놓을 클립인지(오디오 파일, 분리한 오디오).</summary>
        public static bool IsAudioClip(Clip clip)
        {
            return clip.Kind == ClipKind.Media && (clip.MediaKind == MediaKind.Audio || clip.AudioOnly);
        }

        public static bool Accepts(Track track, Clip clip)
        {
            if (track.Kind == TrackKind.Audio)
                return IsAudioClip(clip);
            // 자막 트랙에는 자막(글자 클립)만
            if (track.Subtitles)
                return clip.Kind == ClipKind.Text;
            return !IsAudioClip(clip);
        }

        private const string SubtitleOnly = "자막 트랙에는 자막(글자)만 놓을 수 있습니다.";

        /// <summary>동영상 파일 클립을 오디오 트랙에 놓을 때: 같은 원본의 소리만 쓰는 클립으로 바꾼다.</summary>
        public static void MakeAudioOnly(Clip clip)
        {
            clip.AudioOnly = true;
            clip.HasVideo = false;
            clip.TransitionIn = 0;
            clip.X = clip.Y = clip.Rotation = 0;
            clip.Scale = clip.Opacity = 1;
        }

        // ---------- 놓기·옮기기 ----------

        /// <summary>편집 도중 클립이 사라졌을 때(다른 편집으로 지워짐) 알아들을 수 있는 이유로 거부한다.</summary>
        private static void RequireClip(Clip clip)
        {
            if (clip == null)
                throw new EditException("클립이 없습니다(이미 지워졌을 수 있습니다).");
        }

        public static void EnsureEditable(Track track)
        {
            if (track == null)
                throw new EditException("타임라인에 없는 클립입니다.");
            if (track.Locked)
                throw new EditException("'" + track.Name + "' 트랙이 잠겨 있습니다.");
        }

        private static bool IsMagnetic(Project project, Track track)
        {
            return project.Magnetic && project.IsMain(track);
        }

        /// <summary>
        /// 클립을 트랙에 놓는다. 마그네틱 주 트랙이면 start에 가장 가까운 클립 경계에 끼워 넣고 뒤를 민다.
        /// 아니면 start에 놓고 겹치는 부분을 덮어쓴다.
        /// </summary>
        public static void Place(Project project, Track track, Clip clip, long start)
        {
            EnsureEditable(track);
            if (!Accepts(track, clip))
                throw new EditException(track.Subtitles ? SubtitleOnly : track.Kind == TrackKind.Audio ? "오디오 트랙에는 소리 클립만 놓을 수 있습니다." : "동영상 트랙에는 오디오 파일을 놓을 수 없습니다.");
            start = Math.Max(0, start);
            if (IsMagnetic(project, track))
            {
                track.Sort();
                var index = track.Clips.FindIndex(c => c.Start + c.Duration / 2.0 > start);
                if (index < 0)
                    index = track.Clips.Count;
                track.Clips.Insert(index, clip);
                Compact(track);
                return;
            }
            clip.Start = start;
            ClearRange(track, start, clip.End);
            track.Clips.Add(clip);
            track.Sort();
        }

        /// <summary>트랙 끝에 붙인다(미디어 패널의 "타임라인에 추가").</summary>
        public static void Append(Project project, Track track, Clip clip)
        {
            var end = track.Clips.Select(c => c.End).DefaultIfEmpty(0).Max();
            Place(project, track, clip, end);
        }

        public static void Move(Project project, Clip clip, Track target, long newStart)
        {
            var source = project.TrackOf(clip);
            if (source == null)
                throw new EditException("타임라인에 없는 클립입니다.");
            EnsureEditable(source);
            EnsureEditable(target);
            if (!Accepts(target, clip))
                throw new EditException(target.Subtitles ? SubtitleOnly : target.Kind == TrackKind.Audio ? "오디오 트랙에는 소리 클립만 놓을 수 있습니다." : "동영상 트랙에는 오디오 클립을 놓을 수 없습니다.");
            source.Clips.Remove(clip);
            if (IsMagnetic(project, source))
                Compact(source);
            Place(project, target, clip, newStart);
        }

        /// <summary>목록 순서대로 0부터 빈틈 없이 붙인다.</summary>
        public static void Compact(Track track)
        {
            long position = 0;
            foreach (var clip in track.Clips)
            {
                clip.Start = position;
                position = clip.End;
            }
        }

        /// <summary>[from, to) 구간을 비운다: 안에 든 클립은 지우고 걸친 클립은 잘라 낸다.</summary>
        public static void ClearRange(Track track, long from, long to)
        {
            if (to <= from)
                return;
            foreach (var clip in track.Clips.Where(c => c.Start < to && c.End > from).ToList())
            {
                if (clip.Start >= from && clip.End <= to)
                {
                    track.Clips.Remove(clip);
                }
                else if (clip.Start < from && clip.End > to)
                {
                    var right = SplitAt(clip, to);
                    SetEnd(clip, from);
                    track.Clips.Add(right);
                }
                else if (clip.Start < from)
                {
                    SetEnd(clip, from);
                }
                else
                {
                    SetStart(clip, to);
                }
            }
            track.Sort();
        }

        // ---------- 길이 다루기(검사 없는 기본 연산) ----------

        /// <summary>시작은 그대로 두고 타임라인 길이를 frames로(원본 끝을 옮김).</summary>
        internal static void SetDuration(Clip clip, long frames)
        {
            frames = Math.Max(1, frames);
            clip.SourceOut = clip.SourceIn + (long)Math.Round(frames * clip.Speed, MidpointRounding.AwayFromZero);
            // 속도 반올림으로 한 프레임 어긋나면 원본 끝을 한 칸씩 맞춘다
            for (var i = 0; i < 4 && clip.Duration > frames; i++)
                clip.SourceOut--;
            for (var i = 0; i < 4 && clip.Duration < frames; i++)
                clip.SourceOut++;
            Normalize(clip);
        }

        internal static void SetEnd(Clip clip, long newEnd)
        {
            SetDuration(clip, newEnd - clip.Start);
        }

        /// <summary>끝은 그대로 두고 시작을 옮긴다(원본 시작도 같이 옮김).</summary>
        internal static void SetStart(Clip clip, long newStart)
        {
            var end = clip.End;
            var delta = newStart - clip.Start;
            clip.SourceIn += (long)Math.Round(delta * clip.Speed, MidpointRounding.AwayFromZero);
            clip.Start = newStart;
            SetDuration(clip, end - newStart);
        }

        /// <summary>원본 제한이 없는 클립(이미지·글자)은 원본 구간을 0부터로 맞춘다.</summary>
        private static void Normalize(Clip clip)
        {
            if (!clip.HasSourceLimit)
            {
                clip.SourceOut -= clip.SourceIn;
                clip.SourceIn = 0;
            }
        }

        /// <summary>frame에서 둘로 나눠 오른쪽 조각을 돌려준다(트랙에 넣지 않음). 왼쪽은 clip이 그대로 남는다.</summary>
        internal static Clip SplitAt(Clip clip, long frame)
        {
            var right = clip.CloneWithNewId();
            var originalOut = clip.SourceOut;
            SetEnd(clip, frame);
            right.Start = frame;
            right.SourceIn = clip.SourceOut;
            right.SourceOut = originalOut;
            if (!right.HasSourceLimit)
            {
                right.SourceIn = 0;
                right.SourceOut = originalOut - clip.SourceOut;
            }
            right.FadeIn = 0;
            right.TransitionIn = 0;
            clip.FadeOut = 0;
            return right;
        }

        // ---------- 편집 명령 ----------

        /// <summary>frame에 걸친 클립을 나눈다(잠긴 트랙은 건너뜀). 만든 오른쪽 조각들을 돌려준다.</summary>
        public static List<Clip> Split(Project project, IEnumerable<Clip> clips, long frame)
        {
            var made = new List<Clip>();
            foreach (var clip in clips.ToList())
            {
                if (frame <= clip.Start || frame >= clip.End)
                    continue;
                var track = project.TrackOf(clip);
                if (track == null || track.Locked)
                    continue;
                var right = SplitAt(clip, frame);
                track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
                made.Add(right);
            }
            return made;
        }

        /// <summary>앞을 다듬는다(newStart로). 실제로 적용된 시작을 돌려준다. 마그네틱 주 트랙이면 다듬은 만큼 당겨진다.</summary>
        public static long TrimStart(Project project, Clip clip, long newStart)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            newStart = Math.Min(newStart, clip.End - 1);
            newStart = Math.Max(newStart, 0);
            if (clip.HasSourceLimit)
                newStart = Math.Max(newStart, clip.Start - (long)Math.Floor(clip.SourceIn / clip.Speed));
            var magnetic = IsMagnetic(project, track);
            if (!magnetic)
            {
                var previous = track.Clips.Where(c => c != clip && c.End <= clip.Start).Select(c => c.End).DefaultIfEmpty(0).Max();
                newStart = Math.Max(newStart, previous);
            }
            SetStart(clip, newStart);
            if (magnetic)
                Compact(track);
            return clip.Start;
        }

        /// <summary>뒤를 다듬는다(newEnd로). 실제로 적용된 끝을 돌려준다.</summary>
        public static long TrimEnd(Project project, Clip clip, long newEnd)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            newEnd = Math.Max(newEnd, clip.Start + 1);
            if (clip.HasSourceLimit)
                newEnd = Math.Min(newEnd, clip.Start + (long)Math.Floor((clip.SourceLength - clip.SourceIn) / clip.Speed));
            var magnetic = IsMagnetic(project, track);
            if (!magnetic)
            {
                var next = track.Clips.Where(c => c != clip && c.Start >= clip.End).Select(c => c.Start).DefaultIfEmpty(long.MaxValue).Min();
                newEnd = Math.Min(newEnd, next);
            }
            SetEnd(clip, newEnd);
            if (magnetic)
                Compact(track);
            return clip.End;
        }

        /// <summary>재생 헤드까지 앞(Alt+[) 또는 뒤(Alt+])를 다듬는다. 재생 헤드가 클립 안이 아니면 false.</summary>
        public static bool TrimToPlayhead(Project project, Clip clip, long playhead, bool trimStart)
        {
            if (playhead <= clip.Start || playhead >= clip.End)
                return false;
            if (trimStart)
                TrimStart(project, clip, playhead);
            else
                TrimEnd(project, clip, playhead);
            return true;
        }

        /// <summary>
        /// 클립을 지운다. ripple이면(또는 마그네틱 주 트랙이면) 지운 길이만큼 같은 트랙의 뒤 클립을 당긴다.
        /// 잠긴 트랙의 클립은 건너뛴다. 지운 개수를 돌려준다.
        /// </summary>
        public static int Delete(Project project, IEnumerable<Clip> clips, bool ripple)
        {
            var deleted = 0;
            foreach (var group in clips.ToList().GroupBy(project.TrackOf).Where(g => g.Key != null && !g.Key.Locked))
            {
                var track = group.Key;
                foreach (var clip in group.OrderByDescending(c => c.Start))
                {
                    if (!track.Clips.Remove(clip))
                        continue;
                    deleted++;
                    if (ripple && !IsMagnetic(project, track))
                    {
                        foreach (var later in track.Clips.Where(c => c.Start >= clip.End))
                            later.Start -= clip.Duration;
                    }
                }
                if (IsMagnetic(project, track))
                    Compact(track);
            }
            return deleted;
        }

        /// <summary>frame이 든 빈틈을 없앤다(뒤 클립을 당김). 빈틈이 아니면 false.</summary>
        public static bool CloseGap(Project project, Track track, long frame)
        {
            EnsureEditable(track);
            if (track.Clips.Any(c => c.Contains(frame)))
                return false;
            var gapStart = track.Clips.Where(c => c.End <= frame).Select(c => c.End).DefaultIfEmpty(0).Max();
            var nextClip = track.Clips.Where(c => c.Start > frame).OrderBy(c => c.Start).FirstOrDefault();
            if (nextClip == null)
                return false;
            var gap = nextClip.Start - gapStart;
            if (gap <= 0)
                return false;
            foreach (var clip in track.Clips.Where(c => c.Start >= nextClip.Start))
                clip.Start -= gap;
            return true;
        }

        /// <summary>속도를 바꾼다(원본 구간은 그대로, 길이가 바뀜). 이미지·글자는 속도가 없다. 뒤 클립과 겹치면 EditException.</summary>
        public static void SetSpeed(Project project, Clip clip, double speed)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            if (!clip.HasSourceLimit)
                throw new EditException("이미지·글자 클립은 속도를 바꿀 수 없습니다. 길이를 늘이거나 줄이세요.");
            speed = Math.Max(MinSpeed, Math.Min(MaxSpeed, speed));
            var oldEnd = clip.End;
            var oldSpeed = clip.Speed;
            clip.Speed = speed;
            if (IsMagnetic(project, track))
            {
                Compact(track);
                return;
            }
            var next = track.Clips.Where(c => c != clip && c.Start >= oldEnd).Select(c => c.Start).DefaultIfEmpty(long.MaxValue).Min();
            if (clip.End > next)
            {
                clip.Speed = oldSpeed;
                throw new EditException("뒤 클립과 겹쳐서 속도를 바꿀 수 없습니다. 뒤 클립을 옮긴 뒤 다시 하세요.");
            }
        }

        /// <summary>동영상 클립의 소리를 오디오 트랙으로 분리한다(Ctrl+Alt+D). 빈 오디오 트랙이 없으면 새로 만든다.</summary>
        public static Clip DetachAudio(Project project, Clip clip)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            if (clip.Kind != ClipKind.Media || !clip.HasVideo || !clip.HasAudio || clip.AudioDetached || clip.AudioOnly)
                throw new EditException("소리가 있는 동영상 클립에서만 오디오를 분리할 수 있습니다.");
            var audio = clip.CloneWithNewId();
            MakeAudioOnly(audio);
            var target = project.AudioTracks.FirstOrDefault(t => !t.Locked && !t.Clips.Any(c => c.Start < audio.End && c.End > audio.Start));
            if (target == null)
                target = AddTrack(project, TrackKind.Audio);
            target.Clips.Add(audio);
            target.Sort();
            clip.AudioDetached = true;
            return audio;
        }

        // ---------- 트랙 ----------

        /// <summary>트랙을 더한다(동영상은 맨 위 — 자막 트랙이 있으면 그 아래라 자막이 늘 위에 그려진다, 오디오는 맨 아래).</summary>
        public static Track AddTrack(Project project, TrackKind kind)
        {
            var track = new Track { Kind = kind };
            if (kind == TrackKind.Video)
            {
                var firstSubtitles = project.VideoTracks.FindIndex(t => t.Subtitles);
                if (firstSubtitles > 0)
                    project.VideoTracks.Insert(firstSubtitles, track);
                else
                    project.VideoTracks.Add(track);
            }
            else
            {
                project.AudioTracks.Add(track);
            }
            project.RenameTracks();
            return track;
        }

        /// <summary>
        /// 자막 큐들로 맨 위에 새 자막 트랙을 만든다(시각 그대로). 겹치는 큐는 앞 큐를 다음 큐 시작에서 끊고, 시작이 같으면 한 큐로 합친다.
        /// 놓을 큐가 없으면 null.
        /// </summary>
        public static Track ImportSubtitles(Project project, IReadOnlyList<SubtitleCue> cues)
        {
            var rate = project.Rate;
            var clips = new List<Clip>();
            foreach (var cue in cues.OrderBy(c => c.Start))
            {
                var start = rate.ToFrames(cue.Start);
                var end = rate.ToFrames(cue.End);
                // 한 프레임도 안 되는 큐는 보이지 않는다(자동 자막의 10ms 큐 등) — 버린다
                if (end <= start)
                    continue;
                var previous = clips.Count > 0 ? clips[clips.Count - 1] : null;
                if (previous != null && start <= previous.Start)
                {
                    // 같은 프레임에 시작하면 한 큐로 — 같은 글은 한 번만, 합쳐도 2,000자까지
                    if (!previous.Text.Contains(cue.Text, StringComparison.Ordinal))
                        previous.Text = SubtitleFile.Cut(previous.Text + "\n" + cue.Text, SubtitleFile.MaxTextLength);
                    previous.SourceOut = Math.Max(previous.SourceOut, end - previous.Start);
                    continue;
                }
                if (previous != null && previous.End > start)
                    previous.SourceOut = start - previous.Start;
                clips.Add(CreateText(cue.Text, start, end - start));
            }
            if (clips.Count == 0)
                return null;
            var track = new Track { Kind = TrackKind.Video, Subtitles = true };
            track.Clips.AddRange(clips);
            project.VideoTracks.Add(track);
            project.RenameTracks();
            return track;
        }

        public static void RemoveTrack(Project project, Track track)
        {
            if (project.IsMain(track))
                throw new EditException("주 트랙(동영상 1)은 지울 수 없습니다.");
            EnsureEditable(track);
            if (!project.VideoTracks.Remove(track))
                project.AudioTracks.Remove(track);
            project.RenameTracks();
        }

        // ---------- 찾기·붙기 ----------

        public static Clip ClipAt(Track track, long frame)
        {
            return track.Clips.FirstOrDefault(c => c.Contains(frame));
        }

        /// <summary>모든 편집점(클립 시작·끝, 0) — ↑/↓ 이동용.</summary>
        public static List<long> EditPoints(Project project)
        {
            return project.AllClips.SelectMany(c => new[] { c.Start, c.End }).Concat(new long[] { 0 }).Distinct().OrderBy(f => f).ToList();
        }

        /// <summary>붙을 후보: 0, 재생 헤드, 마커, In/Out, 다른 클립의 시작·끝.</summary>
        public static List<long> SnapTargets(Project project, long playhead, ICollection<Clip> exclude)
        {
            var targets = new List<long> { 0, playhead };
            targets.AddRange(project.Markers.Select(m => m.Frame));
            if (project.MarkIn.HasValue)
                targets.Add(project.MarkIn.Value);
            if (project.MarkOut.HasValue)
                targets.Add(project.MarkOut.Value);
            foreach (var clip in project.AllClips)
            {
                if (exclude != null && exclude.Contains(clip))
                    continue;
                targets.Add(clip.Start);
                targets.Add(clip.End);
            }
            return targets;
        }

        /// <summary>frame을 threshold 안의 가장 가까운 후보에 붙인다. 붙었으면 그 후보, 아니면 frame.</summary>
        public static long Snap(IEnumerable<long> targets, long frame, long threshold, out bool snapped)
        {
            snapped = false;
            var best = frame;
            var bestDistance = threshold + 1;
            foreach (var target in targets)
            {
                var distance = Math.Abs(target - frame);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = target;
                    snapped = true;
                }
            }
            return best;
        }

        /// <summary>옮기는 클립의 시작이나 끝 중 가까운 쪽을 붙여 새 시작을 돌려준다.</summary>
        public static long SnapMove(IEnumerable<long> targets, long proposedStart, long duration, long threshold, out long snappedAt)
        {
            snappedAt = -1;
            var list = targets as IList<long> ?? targets.ToList();
            bool startSnapped, endSnapped;
            var start = Snap(list, proposedStart, threshold, out startSnapped);
            var end = Snap(list, proposedStart + duration, threshold, out endSnapped);
            if (startSnapped && (!endSnapped || Math.Abs(start - proposedStart) <= Math.Abs(end - proposedStart - duration)))
            {
                snappedAt = start;
                return start;
            }
            if (endSnapped)
            {
                snappedAt = end;
                return end - duration;
            }
            return proposedStart;
        }
    }
}
