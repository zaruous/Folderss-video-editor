using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 사운드 분리로 나눈 영상·사운드 클립의 링크(두 클립이 같은 <see cref="Clip.LinkId"/>). 규칙:
    /// - 짝은 영상(소리를 끈 동영상 클립) 하나와 사운드(같은 원본의 소리만 쓰는 오디오 트랙 클립) 하나다.
    /// - 링크된 짝은 함께 고르고 옮기기·다듬기·분할·삭제·속도를 같이 한다. 주 트랙이 밀리거나 당겨져도 짝이 따라간다(편집 전 두 클립의 시간 차이를 지킴).
    /// - 링크를 풀면(<see cref="Clip.Unlinked"/>) 따로 움직이고, 어긋난 만큼을 보이며 싱크 맞추기로 되돌린다.
    /// - 잠긴 트랙의 짝은 움직이지 않는다(어긋남으로 보인다). 따라가는 클립은 다른 클립을 덮어쓰지 않는다(자리가 차 있으면 빈 트랙으로).
    /// </summary>
    public static class ClipLinks
    {
        /// <summary>사운드를 분리할 수 있는 미디어(소리가 있는 동영상).</summary>
        public static bool CanSplit(MediaInfo media)
        {
            return media != null && media.Kind == MediaKind.Video && media.HasVideo && media.HasAudio;
        }

        /// <summary>짝의 영상 쪽: 사운드를 분리해 소리를 끈 동영상 클립.</summary>
        public static bool IsVideoSide(Clip clip)
        {
            return clip != null && clip.Kind == ClipKind.Media && !clip.AudioOnly && clip.HasVideo && clip.HasAudio && clip.AudioDetached;
        }

        /// <summary>짝의 사운드 쪽: 동영상 파일의 소리만 쓰는 오디오 트랙 클립.</summary>
        public static bool IsSoundSide(Clip clip)
        {
            return clip != null && clip.Kind == ClipKind.Media && clip.AudioOnly && clip.HasAudio;
        }

        /// <summary>두 클립을 새 짝으로 묶는다.</summary>
        public static void Link(Clip video, Clip sound)
        {
            video.LinkId = sound.LinkId = Track.NewId();
            video.Unlinked = sound.Unlinked = false;
        }

        /// <summary>짝(링크를 풀었어도). 없으면 null.</summary>
        public static Clip Partner(Project project, Clip clip)
        {
            if (clip?.LinkId == null)
                return null;
            return project.AllClips.FirstOrDefault(c => c != clip && c.LinkId == clip.LinkId);
        }

        /// <summary>링크된(풀지 않은) 짝. 없으면 null.</summary>
        public static Clip LinkedPartner(Project project, Clip clip)
        {
            return clip != null && !clip.Unlinked ? Partner(project, clip) : null;
        }

        /// <summary>모든 짝(클립 Id → 짝) — 타임라인을 그릴 때 한 번에.</summary>
        public static Dictionary<string, Clip> Partners(Project project)
        {
            var result = new Dictionary<string, Clip>();
            foreach (var group in project.AllClips.Where(c => c.LinkId != null).GroupBy(c => c.LinkId))
            {
                var list = group.ToList();
                if (list.Count != 2)
                    continue;
                result[list[0].Id] = list[1];
                result[list[1].Id] = list[0];
            }
            return result;
        }

        /// <summary>클립들 + 그 링크된 짝(같은 클립은 한 번만).</summary>
        public static List<Clip> WithLinked(Project project, IEnumerable<Clip> clips)
        {
            var list = clips.Where(c => c != null).Distinct().ToList();
            if (!list.Any(c => c.LinkId != null && !c.Unlinked))
                return list;
            var partners = Partners(project);
            foreach (var clip in list.ToList())
            {
                if (!clip.Unlinked && partners.TryGetValue(clip.Id, out var partner) && !list.Contains(partner))
                    list.Add(partner);
            }
            return list;
        }

        /// <summary>링크된 짝은 하나만(앞의 것) — 짝을 함께 고치는 편집을 고른 클립마다 부를 때 두 번 하지 않게.</summary>
        public static List<Clip> OnePerPair(Project project, IEnumerable<Clip> clips)
        {
            var result = new List<Clip>();
            foreach (var clip in clips.Where(c => c != null))
            {
                var partner = LinkedPartner(project, clip);
                if ((partner == null || !result.Contains(partner)) && !result.Contains(clip))
                    result.Add(clip);
            }
            return result;
        }

        // ---------- 어긋남 ----------

        /// <summary>원본 0프레임이 타임라인에 오는 자리(프레임) — 짝의 이 값이 같으면 싱크가 맞다. 다듬기·분할로는 바뀌지 않는다.</summary>
        public static double Anchor(Clip clip)
        {
            return clip.Start - clip.SourceIn / clip.Speed;
        }

        /// <summary>clip이 짝보다 늦은 프레임(+ 늦음, − 이름, 0 = 싱크가 맞음).</summary>
        public static long Offset(Clip clip, Clip partner)
        {
            return (long)Math.Round(Anchor(clip) - Anchor(partner), MidpointRounding.AwayFromZero);
        }

        /// <summary>어긋남 표시: 1초 안이면 "+3f", 넘으면 "−0:00:01.05"(시:분:초.프레임). 빼기는 U+2212.</summary>
        public static string FormatOffset(long frames, FrameRate rate)
        {
            if (frames == 0)
                return "0f";
            var sign = frames > 0 ? "+" : "−";
            var size = Math.Abs(frames);
            if (size < rate.ToFrames(1))
                return sign + size.ToString(CultureInfo.InvariantCulture) + "f";
            // 타임코드("HH:MM:SS:FF")와 같은 초·프레임 자리
            var parts = rate.Timecode(size).Split(':');
            var hours = long.Parse(parts[0], CultureInfo.InvariantCulture);
            return sign + hours.ToString(CultureInfo.InvariantCulture) + ":" + parts[1] + ":" + parts[2] + "." + parts[3];
        }

        // ---------- 따라가기 ----------

        /// <summary>편집 전 링크된 클립의 자리(<see cref="Anchor"/>) — 편집 뒤 <see cref="Follow"/>에 넘긴다.</summary>
        internal static Dictionary<string, double> Anchors(Project project)
        {
            var anchors = new Dictionary<string, double>();
            foreach (var clip in project.AllClips.Where(c => c.LinkId != null && !c.Unlinked))
                anchors[clip.Id] = Anchor(clip);
            return anchors;
        }

        private static bool IsMagneticMain(Project project, Track track)
        {
            return track != null && project.Magnetic && project.IsMain(track);
        }

        /// <summary>
        /// 편집 뒤 링크된 짝의 시간 차이가 before(편집 전 <see cref="Anchors"/>)와 달라졌으면 따라갈 클립을 옮겨 되돌린다.
        /// 이끄는 쪽: 마그네틱 주 트랙의 클립 → lead(직접 고친 클립) → 혼자 움직인 클립 → 영상. independent면 lead의 짝은 그대로 둔다(그 한 번만 따로).
        /// 따라갈 클립은 모두 들어 낸 뒤 놓는다(서로 덮어쓰지 않게). 잠긴 트랙의 클립은 따라가지 않는다.
        /// </summary>
        internal static void Follow(Project project, Dictionary<string, double> before, Clip lead = null, bool independent = false)
        {
            if (before == null || before.Count == 0)
                return;
            var moves = new List<(Clip Clip, Track Track, long Delta)>();
            var seen = new HashSet<string>();
            foreach (var clip in project.AllClips.ToList())
            {
                if (clip.LinkId == null || clip.Unlinked || !seen.Add(clip.LinkId))
                    continue;
                var partner = LinkedPartner(project, clip);
                if (partner == null || !before.TryGetValue(clip.Id, out var a0) || !before.TryGetValue(partner.Id, out var b0))
                    continue;
                if (independent && lead != null && (lead == clip || lead == partner))
                    continue;
                var da = Anchor(clip) - a0;
                var db = Anchor(partner) - b0;
                if (Math.Abs(da - db) < 0.5)
                    continue;
                var leader = Leader(project, clip, partner, da, db, lead);
                var follower = leader == clip ? partner : clip;
                var delta = (long)Math.Round(leader == clip ? da - db : db - da, MidpointRounding.AwayFromZero);
                var track = project.TrackOf(follower);
                if (track == null || track.Locked || IsMagneticMain(project, track))
                    continue;
                moves.Add((follower, track, delta));
            }
            if (moves.Count == 0)
                return;
            foreach (var move in moves)
                move.Track.Clips.Remove(move.Clip);
            foreach (var move in moves)
                Reposition(project, move.Clip, move.Track, move.Delta);
        }

        private static Clip Leader(Project project, Clip a, Clip b, double da, double db, Clip lead)
        {
            var magneticA = IsMagneticMain(project, project.TrackOf(a));
            var magneticB = IsMagneticMain(project, project.TrackOf(b));
            if (magneticA != magneticB)
                return magneticA ? a : b;
            if (lead == a || lead == b)
                return lead;
            if (Math.Abs(db) < 0.5)
                return a;
            if (Math.Abs(da) < 0.5)
                return b;
            return IsSoundSide(a) ? b : a;
        }

        /// <summary>
        /// 트랙에서 들어 낸 클립을 delta만큼 옮겨 다시 놓는다. 0 앞으로 나가는 부분은 잘라 낸다(싱크는 그대로).
        /// 자리에 다른 클립이 있으면 덮어쓰지 않고 같은 종류의 빈 트랙(없으면 새 트랙)으로.
        /// </summary>
        private static void Reposition(Project project, Clip clip, Track track, long delta)
        {
            var start = clip.Start + delta;
            if (start < 0)
            {
                if (clip.End + delta <= 0)
                    throw new EditException("링크된 '" + clip.Name + "'이(가) 타임라인 처음(0) 앞으로 나가 함께 옮길 수 없습니다. 링크를 해제하고 옮기세요.");
                TimelineEdits.SetStart(clip, clip.Start - start);
            }
            clip.Start += delta;
            var target = FreeTrack(project, track, clip);
            target.Clips.Add(clip);
            target.Sort();
        }

        /// <summary>
        /// 짝에 맞춘 자리(start)에 놓는다 — 붙여 넣은 영상이 주 트랙 경계로 당겨져 사운드가 0 앞으로 가야 하면 0 앞 부분을 잘라 내 싱크를 지킨다(0에 고정하면 그만큼 어긋남).
        /// 통째로 0 앞이면 놓지 않고 거절한다.
        /// </summary>
        public static void PlaceInSync(Project project, Track track, Clip clip, long start)
        {
            if (start < 0)
            {
                if (clip.Duration + start <= 0)
                    throw new EditException("링크된 '" + clip.Name + "'이(가) 타임라인 처음(0) 앞으로 나가 붙여 넣을 수 없습니다.");
                TimelineEdits.SetStart(clip, clip.Start - start);
                start = 0;
            }
            TimelineEdits.Place(project, track, clip, start);
        }

        private static Track FreeTrack(Project project, Track track, Clip clip)
        {
            bool Free(Track t) => !t.Clips.Any(c => c.Start < clip.End && c.End > clip.Start);
            if (Free(track))
                return track;
            var tracks = track.Kind == TrackKind.Audio ? project.AudioTracks : project.VideoTracks;
            return tracks.FirstOrDefault(t => t != track && !t.Locked && !IsMagneticMain(project, t) && TimelineEdits.Accepts(t, clip) && Free(t))
                   ?? TimelineEdits.AddTrack(project, track.Kind);
        }

        /// <summary>
        /// 링크된 짝이 마그네틱 주 트랙에 있으면(이 클립은 아니고 짝의 트랙이 잠기지 않음) 그 짝 — 주 트랙은 경계에 붙어 자리가 정해지므로
        /// 이 클립을 옮기거나 다듬거나 속도를 바꾸면 짝을 바꾸고 이 클립이 따라간다. 아니면 null.
        /// </summary>
        internal static Clip MagneticPartner(Project project, Clip clip)
        {
            var partner = LinkedPartner(project, clip);
            var track = partner == null ? null : project.TrackOf(partner);
            return track != null && !track.Locked && IsMagneticMain(project, track) && !IsMagneticMain(project, project.TrackOf(clip)) ? partner : null;
        }

        /// <summary>
        /// 옮기는 클립의 링크된 짝이 마그네틱 주 트랙에 있으면 짝이 자리를 정한다: 짝을 같은 만큼 옮기고(가까운 경계로)
        /// 이 클립은 target에서 짝을 따라간다. 그렇게 했으면 true(아니면 false — 보통대로 옮긴다).
        /// </summary>
        internal static bool MoveWithMagneticPartner(Project project, Clip clip, Track source, Track target, long newStart)
        {
            var partner = MagneticPartner(project, clip);
            if (partner == null)
                return false;
            var partnerTrack = project.TrackOf(partner);
            var before = Anchor(partner);
            source.Clips.Remove(clip);
            // 이 클립을 들어 냈으므로 짝을 옮길 때는 이 짝이 따라가지 않는다(다른 짝은 따라감)
            TimelineEdits.Move(project, partner, partnerTrack, partner.Start + (newStart - clip.Start));
            var shift = (long)Math.Round(Anchor(partner) - before, MidpointRounding.AwayFromZero);
            var start = clip.Start + shift;
            if (start < 0)
            {
                if (clip.End + shift <= 0)
                    throw new EditException("'" + clip.Name + "'이(가) 타임라인 처음(0) 앞으로 나가 옮길 수 없습니다.");
                TimelineEdits.SetStart(clip, clip.Start - start);
                start = 0;
            }
            TimelineEdits.Place(project, target, clip, start);
            return true;
        }

        /// <summary>
        /// 다듬은 뒤: 링크된 짝의 같은 쪽 끝을 같은 원본 길이만큼 다듬고 밀리거나 당겨진 짝이 따라간다. 줄이는 짝은 먼저 다듬고(따라갈 자리가 비게),
        /// 늘이는 짝은 따라간 뒤에 다듬는다(옆 클립이 비켜 준 뒤). 늘이는 짝이 옆 클립에 막혀 덜 늘었으면 이 클립도 그만큼만(두 길이가 같게 — 잠긴 짝은 맞추지 않음).
        /// independent면 짝은 그대로.
        /// </summary>
        internal static void AfterTrim(Project project, Clip clip, long sourceDelta, bool start, Dictionary<string, double> anchors, bool independent)
        {
            var shrink = start ? sourceDelta > 0 : sourceDelta < 0;
            if (!independent && shrink)
                TrimPartner(project, clip, sourceDelta, start);
            Follow(project, anchors, clip, independent);
            if (independent || shrink)
                return;
            var partner = LinkedPartner(project, clip);
            var track = partner == null ? null : project.TrackOf(partner);
            var edge = partner == null ? 0 : start ? partner.SourceIn : partner.SourceOut;
            TrimPartner(project, clip, sourceDelta, start);
            if (track == null || track.Locked)
                return;
            var grown = start ? edge - partner.SourceIn : partner.SourceOut - edge;
            var excess = (long)Math.Round((Math.Abs(sourceDelta) - grown) / clip.Speed, MidpointRounding.AwayFromZero);
            if (excess <= 0)
                return;
            if (start)
                TimelineEdits.TrimStart(project, clip, clip.Start + excess, true);
            else
                TimelineEdits.TrimEnd(project, clip, clip.End - excess, true);
        }

        /// <summary>링크된 짝의 같은 쪽 끝을 같은 원본 길이만큼 다듬는다(짝의 트랙이 잠겼으면 그대로).</summary>
        private static void TrimPartner(Project project, Clip clip, long sourceDelta, bool start)
        {
            var partner = LinkedPartner(project, clip);
            var track = partner == null ? null : project.TrackOf(partner);
            if (track == null || track.Locked || sourceDelta == 0)
                return;
            var frames = (long)Math.Round(sourceDelta / partner.Speed, MidpointRounding.AwayFromZero);
            if (start)
                TimelineEdits.TrimStart(project, partner, partner.Start + frames, true);
            else
                TimelineEdits.TrimEnd(project, partner, partner.End + frames, true);
        }

        /// <summary>
        /// 속도를 바꾼 뒤: 링크된 짝도 같은 속도로(짝의 트랙이 잠겼으면 그대로) 하고 밀리거나 당겨진 짝이 따라간다.
        /// 길어진 짝이 그래도 뒤 클립과 겹치면 덮어쓰지 않고 빈 트랙으로 옮긴다.
        /// </summary>
        internal static void SpeedPartner(Project project, Clip clip, Dictionary<string, double> anchors)
        {
            var partner = LinkedPartner(project, clip);
            var track = partner == null ? null : project.TrackOf(partner);
            var changed = track != null && !track.Locked && partner.HasSourceLimit && Math.Abs(partner.Speed - clip.Speed) > 1e-9;
            if (changed)
            {
                partner.Speed = clip.Speed;
                if (IsMagneticMain(project, track))
                    TimelineEdits.Compact(track);
            }
            Follow(project, anchors, clip);
            // 따라가며 트랙을 바꿨을 수 있다 — 지금 트랙에서 겹침을 본다
            track = changed ? project.TrackOf(partner) : null;
            if (track != null && !IsMagneticMain(project, track) && track.Clips.Any(c => c != partner && c.Start < partner.End && c.End > partner.Start))
            {
                track.Clips.Remove(partner);
                Reposition(project, partner, track, 0);
            }
        }

        /// <summary>나눈 클립의 오른쪽 조각들: 짝이 함께 나뉘었으면 오른쪽 조각 둘도 새 짝으로(왼쪽은 원래 짝 그대로).</summary>
        internal static void LinkPieces(Dictionary<Clip, Clip> pieces)
        {
            foreach (var group in pieces.Keys.Where(c => c.LinkId != null).GroupBy(c => c.LinkId))
            {
                var lefts = group.ToList();
                if (lefts.Count != 2)
                    continue;
                var id = Track.NewId();
                foreach (var left in lefts)
                {
                    pieces[left].LinkId = id;
                    pieces[left].Unlinked = left.Unlinked;
                }
            }
        }

        /// <summary>목록 안에서 짝의 번호(같은 LinkId가 정확히 둘인 클립끼리, 없으면 -1) — 복사한 클립을 붙여 넣을 때.</summary>
        public static int[] PartnerIndexes(IReadOnlyList<Clip> clips)
        {
            var result = Enumerable.Repeat(-1, clips.Count).ToArray();
            for (var i = 0; i < clips.Count; i++)
            {
                var link = clips[i].LinkId;
                if (link == null)
                    continue;
                var same = Enumerable.Range(0, clips.Count).Where(j => clips[j].LinkId == link).ToList();
                if (same.Count == 2)
                    result[i] = same[0] == i ? same[1] : same[0];
            }
            return result;
        }

        /// <summary>붙여 넣은 사본들의 짝: 원본 짝 둘이 함께 복사됐으면 사본 둘도 새 LinkId로 묶는다(링크 해제 상태도 그대로).</summary>
        public static void CopyLinks(IReadOnlyList<Clip> originals, IReadOnlyList<Clip> copies)
        {
            var ids = new Dictionary<string, string>();
            var partners = PartnerIndexes(originals);
            for (var i = 0; i < originals.Count && i < copies.Count; i++)
            {
                var link = originals[i].LinkId;
                if (link == null || partners[i] < 0)
                    continue;
                if (!ids.TryGetValue(link, out var id))
                    ids[link] = id = Track.NewId();
                copies[i].LinkId = id;
                copies[i].Unlinked = originals[i].Unlinked;
            }
        }

        /// <summary>짝을 잃은 클립(짝을 지웠거나 덮어씀)의 링크를 푼다.</summary>
        internal static void Clean(Project project)
        {
            foreach (var group in project.AllClips.Where(c => c.LinkId != null).GroupBy(c => c.LinkId).Where(g => g.Count() != 2).ToList())
            {
                foreach (var clip in group)
                {
                    clip.LinkId = null;
                    clip.Unlinked = false;
                }
            }
        }

        /// <summary>
        /// 읽은 프로젝트의 링크를 고친다(남이 만든 파일일 수 있음): LinkId는 16진수 32자까지, 같은 원본의 영상·사운드 둘이어야 짝이고
        /// 아니면 링크를 뺀다. 한쪽만 풀렸으면 둘 다 푼 것으로.
        /// </summary>
        public static void Normalize(Project project)
        {
            foreach (var clip in project.AllClips)
            {
                if (clip.LinkId != null && (clip.LinkId.Length == 0 || clip.LinkId.Length > 32 || !clip.LinkId.All(Uri.IsHexDigit)))
                    clip.LinkId = null;
            }
            foreach (var group in project.AllClips.Where(c => c.LinkId != null).GroupBy(c => c.LinkId).ToList())
            {
                var list = group.ToList();
                var video = list.FirstOrDefault(IsVideoSide);
                var sound = list.FirstOrDefault(IsSoundSide);
                var ok = list.Count == 2 && video != null && sound != null && string.Equals(video.Path, sound.Path, StringComparison.OrdinalIgnoreCase);
                var unlinked = list.Any(c => c.Unlinked);
                foreach (var clip in list)
                {
                    if (!ok)
                        clip.LinkId = null;
                    clip.Unlinked = ok && unlinked;
                }
            }
            foreach (var clip in project.AllClips.Where(c => c.LinkId == null))
                clip.Unlinked = false;
        }

        // ---------- 명령 ----------

        /// <summary>
        /// 미디어 패널 [사운드 분리해서 배치]: 영상은 주 트랙 start에(마그네틱이면 가까운 경계), 사운드는 영상과 같은 시간에
        /// 빈 오디오 트랙(없으면 새 트랙)에 놓고 링크한다. 놓은 영상 클립을 돌려준다.
        /// </summary>
        public static Clip PlaceSeparated(Project project, MediaInfo media, long start)
        {
            if (!CanSplit(media))
                throw new EditException((media?.Name ?? "미디어") + "은(는) 소리가 있는 동영상이 아니어서 사운드를 분리할 수 없습니다.");
            var video = TimelineEdits.CreateClip(media, project.Rate, start, 0);
            TimelineEdits.Place(project, project.MainTrack, video, start);
            TimelineEdits.DetachAudio(project, video);
            return video;
        }

        /// <summary>링크 해제: 고른 클립의 링크된 짝을 따로 움직이게 한다. 푼 짝 수.</summary>
        public static int Unlink(Project project, IEnumerable<Clip> clips)
        {
            var count = 0;
            foreach (var clip in clips.Where(c => c != null).ToList())
            {
                var partner = LinkedPartner(project, clip);
                if (partner == null)
                    continue;
                TimelineEdits.EnsureEditable(project.TrackOf(clip));
                TimelineEdits.EnsureEditable(project.TrackOf(partner));
                clip.Unlinked = partner.Unlinked = true;
                count++;
            }
            if (count == 0)
                throw new EditException("링크된 영상·사운드 클립을 고르세요.");
            return count;
        }

        /// <summary>다시 링크: 링크를 푼 짝을 지금 어긋난 그대로 다시 묶는다. 묶은 짝 수.</summary>
        public static int Relink(Project project, IEnumerable<Clip> clips)
        {
            var count = 0;
            foreach (var clip in clips.Where(c => c != null && c.Unlinked).ToList())
            {
                var partner = Partner(project, clip);
                if (partner == null || !clip.Unlinked)
                    continue;
                TimelineEdits.EnsureEditable(project.TrackOf(clip));
                TimelineEdits.EnsureEditable(project.TrackOf(partner));
                clip.Unlinked = partner.Unlinked = false;
                count++;
            }
            if (count == 0)
                throw new EditException("링크를 푼 영상·사운드 클립을 고르세요.");
            return count;
        }

        /// <summary>싱크 맞추기: 어긋난 짝마다 사운드를 옮겨 영상과 시간을 맞춘다(덮어쓰지 않음). 맞춘 짝 수.</summary>
        public static int Sync(Project project, IEnumerable<Clip> clips)
        {
            var count = 0;
            var seen = new HashSet<string>();
            foreach (var clip in clips.Where(c => c?.LinkId != null).ToList())
            {
                if (!seen.Add(clip.LinkId))
                    continue;
                var partner = Partner(project, clip);
                if (partner == null)
                    continue;
                var sound = IsSoundSide(clip) ? clip : partner;
                var video = sound == clip ? partner : clip;
                var offset = Offset(sound, video);
                if (offset == 0)
                    continue;
                var track = project.TrackOf(sound);
                TimelineEdits.EnsureEditable(track);
                track.Clips.Remove(sound);
                Reposition(project, sound, track, -offset);
                count++;
            }
            if (count == 0)
                throw new EditException("어긋난 영상·사운드 짝이 없습니다.");
            return count;
        }

        /// <summary>
        /// 합칠 수 있는 짝(영상, 사운드): 같은 LinkId인 짝(링크를 풀었어도), 또는 둘 다 링크가 없고 같은 원본·같은 구간(시작·원본 구간·속도)인 영상·사운드.
        /// </summary>
        public static List<(Clip Video, Clip Sound)> RejoinPairs(IEnumerable<Clip> clips)
        {
            var list = clips.Where(c => c != null).Distinct().ToList();
            var sounds = list.Where(IsSoundSide).ToList();
            var pairs = new List<(Clip, Clip)>();
            foreach (var video in list.Where(IsVideoSide))
            {
                var sound = video.LinkId != null
                    ? sounds.FirstOrDefault(s => s.LinkId == video.LinkId)
                    : sounds.FirstOrDefault(s => s.LinkId == null && SameRange(video, s));
                if (sound == null)
                    continue;
                sounds.Remove(sound);
                pairs.Add((video, sound));
            }
            return pairs;
        }

        private static bool SameRange(Clip video, Clip sound)
        {
            return string.Equals(video.Path, sound.Path, StringComparison.OrdinalIgnoreCase) && video.Start == sound.Start
                   && video.SourceIn == sound.SourceIn && video.SourceOut == sound.SourceOut && Math.Abs(video.Speed - sound.Speed) < 1e-9;
        }

        /// <summary>
        /// 사운드 합치기: 짝마다 사운드 클립을 지우고 영상 클립의 소리를 다시 켠다. 사운드에서 고친 음량·페이드는 영상으로 옮긴다
        /// (분리한 동안 들은 소리 그대로). 합친 짝 수.
        /// </summary>
        public static int Rejoin(Project project, IEnumerable<Clip> clips)
        {
            var pairs = RejoinPairs(clips);
            if (pairs.Count == 0)
                throw new EditException("합칠 영상·사운드 짝이 없습니다. 사운드를 분리한 영상과 그 사운드(링크된 것이나 같은 구간인 것)를 함께 고르세요.");
            foreach (var (video, sound) in pairs)
            {
                var soundTrack = project.TrackOf(sound);
                TimelineEdits.EnsureEditable(project.TrackOf(video));
                TimelineEdits.EnsureEditable(soundTrack);
                soundTrack.Clips.Remove(sound);
                video.AudioDetached = false;
                video.Volume = sound.Volume;
                video.FadeIn = sound.FadeIn;
                video.FadeOut = sound.FadeOut;
                video.LinkId = null;
                video.Unlinked = false;
            }
            return pairs.Count;
        }
    }

    /// <summary>
    /// 사운드를 파일로 꺼낼 때의 확장자와 FFmpeg 인수(프로세스는 돌리지 않는다 — Media의 SoundExtractor가 돌린다).
    /// 원본 오디오를 다시 인코딩하지 않고(-c:a copy) 코덱에 맞는 그릇에 담는 것이 먼저고, 안 되거나 속도를 바꿨으면 WAV(16비트)로 쓴다.
    /// </summary>
    public static class SoundExtract
    {
        /// <summary>코덱에 맞는 그릇: aac → .m4a, mp3 → .mp3, opus → .opus, vorbis → .ogg, flac → .flac, pcm → .wav, 그 밖 → .mka.</summary>
        public static string ExtensionFor(string codec)
        {
            var name = (codec ?? "").Trim().ToLowerInvariant();
            switch (name)
            {
                case "aac": return ".m4a";
                case "mp3": return ".mp3";
                case "opus": return ".opus";
                case "vorbis": return ".ogg";
                case "flac": return ".flac";
                default: return name.StartsWith("pcm_", StringComparison.Ordinal) ? ".wav" : ".mka";
            }
        }

        /// <summary>
        /// 다시 인코딩하지 않고 꺼낼지: 속도가 1일 때만. .wav로 저장하면 PCM 원본만 그대로(다른 코덱을 WAV에 담으면 못 여는 프로그램이 많다).
        /// </summary>
        public static bool ShouldCopy(string extension, string codec, double speed)
        {
            if (Math.Abs(speed - 1) > 1e-9)
                return false;
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                return (codec ?? "").StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        /// <summary>
        /// FFmpeg 인수: 첫 오디오 스트림만(-vn). from·seconds는 원본 초(from 0 = 처음부터, seconds ≤ 0 = 끝까지).
        /// copy면 -c:a copy, 아니면 속도(atempo — 0.5보다 느리면 여러 번)를 걸어 WAV(pcm_s16le)로.
        /// </summary>
        public static List<string> Arguments(string input, string output, bool copy, double from, double seconds, double speed)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
            if (from > 0)
                args.AddRange(new[] { "-ss", FrameRate.Seconds(from) });
            if (seconds > 0)
                args.AddRange(new[] { "-t", FrameRate.Seconds(seconds) });
            args.AddRange(new[] { "-protocol_whitelist", "file", "-i", input, "-map", "0:a:0", "-vn" });
            if (copy)
            {
                args.AddRange(new[] { "-c:a", "copy" });
            }
            else
            {
                var tempo = RenderGraph.Tempo(speed);
                if (tempo.Count > 0)
                    args.AddRange(new[] { "-af", string.Join(",", tempo.Select(t => "atempo=" + RenderGraph.N(t))) });
                args.AddRange(new[] { "-c:a", "pcm_s16le" });
            }
            args.Add(output);
            return args;
        }
    }
}
