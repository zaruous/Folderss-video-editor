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

        /// <summary>아트 요소 클립: 모양 기본값과 단색 채우기(파랑) 스타일.</summary>
        public static Clip CreateArt(ArtKind kind, long start, long frames)
        {
            return new Clip
            {
                Kind = ClipKind.Art,
                Art = ArtShape.Default(kind),
                Style = ArtStyle(),
                HasVideo = true,
                Start = Math.Max(0, start),
                SourceOut = Math.Max(1, frames)
            };
        }

        /// <summary>
        /// 아트 요소를 start에 넣는다(요소 탭에서 두 번 누르기·끌어 놓기). 트랙: preferred(끌어 놓은 줄)가 쓸 수 있는 동영상 트랙이고 그 구간이 비었으면 거기,
        /// 아니면 그 구간이 빈 덮어 쓰기 트랙(주 트랙은 빼고 — 주 트랙에 놓아 뒤 클립을 밀지 않게), 없으면 새 동영상 트랙. 넣은 클립을 돌려준다.
        /// </summary>
        public static Clip AddArt(Project project, ArtKind kind, long start, long frames, Track preferred = null)
        {
            var clip = CreateArt(kind, start, frames);
            bool Free(Track t) => t.Kind == TrackKind.Video && !t.Locked && !t.Subtitles && !t.Clips.Any(c => c.Start < clip.End && c.End > clip.Start);
            var track = preferred != null && Free(preferred) ? preferred
                : project.VideoTracks.Skip(1).FirstOrDefault(Free) ?? AddTrack(project, TrackKind.Video);
            Place(project, track, clip, clip.Start);
            return clip;
        }

        /// <summary>아트 요소의 처음 스타일(파란 단색 채우기).</summary>
        public static TextStyle ArtStyle()
        {
            var style = new TextStyle();
            style.Fill.Colors = new List<string> { "#3B82F6" };
            return style;
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
            clip.Effects = null;
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
        /// 아니면 start에 놓고 겹치는 부분을 덮어쓴다. follow면 밀린 클립의 링크된 짝이 따라간다(스스로 따라가게 하는 옮기기는 false).
        /// </summary>
        public static void Place(Project project, Track track, Clip clip, long start, bool follow = true)
        {
            EnsureEditable(track);
            if (!Accepts(track, clip))
                throw new EditException(track.Subtitles ? SubtitleOnly : track.Kind == TrackKind.Audio ? "오디오 트랙에는 소리 클립만 놓을 수 있습니다." : "동영상 트랙에는 오디오 파일을 놓을 수 없습니다.");
            start = Math.Max(0, start);
            if (IsMagnetic(project, track))
            {
                // 밀린 클립의 링크된 짝(사운드)이 따라간다
                var anchors = follow ? ClipLinks.Anchors(project) : null;
                track.Sort();
                var index = track.Clips.FindIndex(c => c.Start + c.Duration / 2.0 > start);
                if (index < 0)
                    index = track.Clips.Count;
                track.Clips.Insert(index, clip);
                Compact(track);
                ClipLinks.Follow(project, anchors, clip);
                return;
            }
            clip.Start = start;
            ClearRange(track, start, clip.End);
            track.Clips.Add(clip);
            track.Sort();
            // 덮어써서 지운 클립의 짝은 링크를 푼다
            ClipLinks.Clean(project);
        }

        /// <summary>트랙 끝에 붙인다(미디어 패널의 "타임라인에 추가").</summary>
        public static void Append(Project project, Track track, Clip clip)
        {
            var end = track.Clips.Select(c => c.End).DefaultIfEmpty(0).Max();
            Place(project, track, clip, end);
        }

        /// <summary>클립을 옮긴다. 링크된 짝도 같은 만큼 따라간다 — independent(끌기 시작할 때 Alt)면 그 한 번만 이 클립만.</summary>
        public static void Move(Project project, Clip clip, Track target, long newStart, bool independent = false)
        {
            var source = project.TrackOf(clip);
            if (source == null)
                throw new EditException("타임라인에 없는 클립입니다.");
            EnsureEditable(source);
            EnsureEditable(target);
            if (!Accepts(target, clip))
                throw new EditException(target.Subtitles ? SubtitleOnly : target.Kind == TrackKind.Audio ? "오디오 트랙에는 소리 클립만 놓을 수 있습니다." : "동영상 트랙에는 오디오 클립을 놓을 수 없습니다.");
            // 링크된 짝이 마그네틱 주 트랙에 있으면 짝이 자리를 정하고 이 클립이 따라간다
            if (!independent && ClipLinks.MoveWithMagneticPartner(project, clip, source, target, newStart))
                return;
            var anchors = ClipLinks.Anchors(project);
            source.Clips.Remove(clip);
            if (IsMagnetic(project, source))
                Compact(source);
            Place(project, target, clip, newStart, false);
            ClipLinks.Follow(project, anchors, clip, independent);
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
            clip.ShiftWords(delta);
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
            right.ShiftWords(frame - clip.Start);
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
            var pieces = new Dictionary<Clip, Clip>();
            // 링크된 짝도 함께 나눈다(오른쪽 조각 둘은 새 짝)
            foreach (var clip in ClipLinks.WithLinked(project, clips))
            {
                if (frame <= clip.Start || frame >= clip.End)
                    continue;
                var track = project.TrackOf(clip);
                if (track == null || track.Locked)
                    continue;
                var right = SplitAt(clip, frame);
                track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
                made.Add(right);
                pieces[clip] = right;
            }
            ClipLinks.LinkPieces(pieces);
            return made;
        }

        /// <summary>
        /// 앞을 다듬는다(newStart로). 실제로 적용된 시작을 돌려준다. 마그네틱 주 트랙이면 다듬은 만큼 당겨진다.
        /// 링크된 짝도 같은 원본 길이만큼 다듬는다(independent면 이 클립만).
        /// </summary>
        public static long TrimStart(Project project, Clip clip, long newStart, bool independent = false)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            // 링크된 짝이 마그네틱 주 트랙에 있으면 짝을 다듬는다(이 클립이 따라감)
            if (!independent && ClipLinks.MagneticPartner(project, clip) is Clip lead)
            {
                TrimStart(project, lead, lead.Start + (newStart - clip.Start));
                return clip.Start;
            }
            var anchors = ClipLinks.Anchors(project);
            var sourceIn = clip.SourceIn;
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
            ClipLinks.AfterTrim(project, clip, clip.SourceIn - sourceIn, true, anchors, independent);
            return clip.Start;
        }

        /// <summary>뒤를 다듬는다(newEnd로). 실제로 적용된 끝을 돌려준다. 링크된 짝도 같은 원본 길이만큼 다듬는다(independent면 이 클립만).</summary>
        public static long TrimEnd(Project project, Clip clip, long newEnd, bool independent = false)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            if (!independent && ClipLinks.MagneticPartner(project, clip) is Clip lead)
            {
                TrimEnd(project, lead, lead.End + (newEnd - clip.End));
                return clip.End;
            }
            var anchors = ClipLinks.Anchors(project);
            var sourceOut = clip.SourceOut;
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
            ClipLinks.AfterTrim(project, clip, clip.SourceOut - sourceOut, false, anchors, independent);
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
        /// 잠긴 트랙의 클립은 건너뛴다. 링크된 짝도 함께 지운다. 지운 개수를 돌려준다.
        /// </summary>
        public static int Delete(Project project, IEnumerable<Clip> clips, bool ripple)
        {
            var anchors = ClipLinks.Anchors(project);
            var deleted = 0;
            foreach (var group in ClipLinks.WithLinked(project, clips).GroupBy(project.TrackOf).Where(g => g.Key != null && !g.Key.Locked))
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
            // 짝이 잠긴 트랙에 있어 남았으면 링크를 풀고, 당겨진 클립의 짝은 따라간다
            ClipLinks.Clean(project);
            ClipLinks.Follow(project, anchors);
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
            var anchors = ClipLinks.Anchors(project);
            foreach (var clip in track.Clips.Where(c => c.Start >= nextClip.Start))
                clip.Start -= gap;
            ClipLinks.Follow(project, anchors);
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
            // 링크된 짝도 같은 속도로 — 짝이 마그네틱 주 트랙에 있으면 짝의 속도를 바꾸고 이 클립이 따라간다
            if (ClipLinks.MagneticPartner(project, clip) is Clip lead)
            {
                SetSpeed(project, lead, speed);
                return;
            }
            var anchors = ClipLinks.Anchors(project);
            var oldEnd = clip.End;
            var oldSpeed = clip.Speed;
            clip.Speed = speed;
            if (IsMagnetic(project, track))
            {
                Compact(track);
                ClipLinks.SpeedPartner(project, clip, anchors);
                return;
            }
            var next = track.Clips.Where(c => c != clip && c.Start >= oldEnd).Select(c => c.Start).DefaultIfEmpty(long.MaxValue).Min();
            if (clip.End > next)
            {
                clip.Speed = oldSpeed;
                throw new EditException("뒤 클립과 겹쳐서 속도를 바꿀 수 없습니다. 뒤 클립을 옮긴 뒤 다시 하세요.");
            }
            ClipLinks.SpeedPartner(project, clip, anchors);
        }

        /// <summary>
        /// 동영상 클립의 소리를 오디오 트랙으로 분리한다(사운드 분리, Ctrl+Alt+D). 빈 오디오 트랙이 없으면 새로 만든다.
        /// 영상과 사운드는 링크되어 함께 움직인다(<see cref="ClipLinks"/>).
        /// </summary>
        public static Clip DetachAudio(Project project, Clip clip)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            if (clip.Kind != ClipKind.Media || !clip.HasVideo || !clip.HasAudio || clip.AudioDetached || clip.AudioOnly)
                throw new EditException("소리가 있는 동영상 클립에서만 사운드를 분리할 수 있습니다.");
            var audio = clip.CloneWithNewId();
            MakeAudioOnly(audio);
            var target = project.AudioTracks.FirstOrDefault(t => !t.Locked && !t.Clips.Any(c => c.Start < audio.End && c.End > audio.Start));
            if (target == null)
                target = AddTrack(project, TrackKind.Audio);
            target.Clips.Add(audio);
            target.Sort();
            clip.AudioDetached = true;
            ClipLinks.Link(clip, audio);
            return audio;
        }

        // ---------- 전환 ----------

        /// <summary>
        /// 클립 앞에 전환 효과를 넣는다(frames = 0이면 뺀다). 앞 클립이 붙어 있으면 그 클립에서, 없으면 아래 트랙·바탕에서 바뀐다.
        /// 앞 클립이 그 길이만큼 밑에 더 그려질 뿐 타임라인 위치는 바뀌지 않는다. 길이는 클립 길이까지.
        /// id는 효과 목록의 것이거나 클립에 이미 든 효과(효과 파일이 없는 PC에서 연 프로젝트)여야 한다.
        /// </summary>
        public static void SetTransition(Project project, Clip clip, string id, long frames)
        {
            RequireClip(clip);
            var track = project.TrackOf(clip);
            EnsureEditable(track);
            if (!clip.IsVisual || track.Kind == TrackKind.Audio)
                throw new EditException("화면이 있는 클립에만 전환을 넣을 수 있습니다.");
            if (track.Subtitles)
                throw new EditException("자막 큐에는 전환을 넣지 않습니다.");
            var def = Transitions.Find(id);
            if (def != null)
                Transitions.Assign(clip, def);
            else if (id != clip.Transition)
                throw new EditException("모르는 전환 효과입니다: " + id);
            clip.TransitionIn = Math.Max(0, Math.Min(frames, clip.Duration));
        }

        /// <summary>트랙에서 앞 클립과 붙어 있는 클립마다(클립과 클립 사이마다) 전환을 넣는다. 넣은 곳 수.</summary>
        public static int SetTransitionsBetween(Project project, Track track, string id, long frames)
        {
            EnsureEditable(track);
            var clips = track.Clips.Where(c => c.IsVisual).OrderBy(c => c.Start).ToList();
            var count = 0;
            for (var i = 1; i < clips.Count; i++)
            {
                if (clips[i].Start != clips[i - 1].End)
                    continue;
                SetTransition(project, clips[i], id, frames);
                count++;
            }
            return count;
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

        /// <summary>빈 자막 트랙을 자막 구역(동영상 트랙들 위) 맨 위에 더한다.</summary>
        public static Track AddSubtitleTrack(Project project)
        {
            var track = new Track { Kind = TrackKind.Video, Subtitles = true };
            project.VideoTracks.Add(track);
            project.RenameTracks();
            return track;
        }

        /// <summary>
        /// 자막 큐들을 자막 구역에 놓는다(시각 그대로): target이 자막 트랙이면 그 트랙에(겹치는 큐는 덮어씀), 아니면 맨 위 새 자막 트랙에.
        /// 파일 안에서 겹치는 큐는 앞 큐를 다음 큐 시작에서 끊고, 시작이 같으면 한 큐로 합친다. 놓을 큐가 없으면 null.
        /// </summary>
        public static Track ImportSubtitles(Project project, IReadOnlyList<SubtitleCue> cues, Track target = null)
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
                    // 합친 큐는 단어 시각이 맞지 않는다
                    previous.Words = null;
                    // 같은 프레임에 시작하면 한 큐로 — 같은 글은 한 번만, 합쳐도 2,000자까지
                    if (!previous.Text.Contains(cue.Text, StringComparison.Ordinal))
                        previous.Text = SubtitleFile.Cut(previous.Text + "\n" + cue.Text, SubtitleFile.MaxTextLength);
                    previous.SourceOut = Math.Max(previous.SourceOut, end - previous.Start);
                    continue;
                }
                if (previous != null && previous.End > start)
                    previous.SourceOut = start - previous.Start;
                var text = CreateText(cue.Text, start, end - start);
                text.Words = cue.Words?.Select(w => new ClipWord { Text = w.Text, Start = rate.ToFrames(w.Start) - start, End = rate.ToFrames(w.End) - start }).ToList();
                clips.Add(text);
            }
            if (clips.Count == 0)
                return null;
            if (target != null && target.Subtitles)
            {
                EnsureEditable(target);
                foreach (var clip in clips)
                    Place(project, target, clip, clip.Start);
                return target;
            }
            var track = AddSubtitleTrack(project);
            track.Clips.AddRange(clips);
            return track;
        }

        /// <summary>화면 클립(동영상·사진)의 맞춤 방식을 바꾼다. 바꾼 클립 수.</summary>
        public static int SetFit(Project project, IEnumerable<Clip> clips, ClipFit fit)
        {
            if (!Enum.IsDefined(fit))
                throw new EditException("모르는 맞춤 방식입니다.");
            var count = 0;
            foreach (var clip in clips.Where(c => c != null && c.Kind == ClipKind.Media && c.IsVisual))
            {
                EnsureEditable(project.TrackOf(clip));
                clip.Fit = fit;
                count++;
            }
            return count;
        }

        /// <summary>자막 트랙의 [말하는 단어 강조]를 켜거나 끈다.</summary>
        public static void SetWordHighlight(Project project, Track track, bool on)
        {
            if (track == null || !track.Subtitles)
                throw new EditException("자막 트랙에서만 말하는 단어를 강조할 수 있습니다.");
            EnsureEditable(track);
            track.WordHighlight = on;
        }

        public static void RemoveTrack(Project project, Track track)
        {
            if (project.IsMain(track))
                throw new EditException("주 트랙(동영상 1)은 지울 수 없습니다.");
            EnsureEditable(track);
            if (!project.VideoTracks.Remove(track))
                project.AudioTracks.Remove(track);
            ClipLinks.Clean(project);
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
