using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 편집 중인 상태: 프로젝트·실행 취소·선택·재생 헤드·파일 경로. 화면은 이것을 보고 그린다.
    /// 프로젝트를 바꾸는 일은 모두 <see cref="Execute"/>로 해서 실행 취소에 남긴다.
    /// </summary>
    public sealed class EditorSession
    {
        private readonly EditHistory _history = new EditHistory();
        private readonly HashSet<string> _selection = new HashSet<string>();
        private long _playhead;

        public EditorSession()
        {
            Project = Project.CreateDefault();
        }

        public Project Project { get; private set; }
        public string FilePath { get; private set; }
        public bool Dirty { get; private set; }
        /// <summary>마지막 편집이 거부된 이유(EditException 메시지).</summary>
        public string LastError { get; private set; }

        public event EventHandler Changed;
        public event EventHandler PlayheadChanged;
        public event EventHandler SelectionChanged;

        public bool CanUndo { get { return _history.CanUndo; } }
        public bool CanRedo { get { return _history.CanRedo; } }

        public long Playhead
        {
            get { return _playhead; }
            set
            {
                value = Math.Max(0, value);
                if (value == _playhead)
                    return;
                _playhead = value;
                PlayheadChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyCollection<string> SelectedIds { get { return _selection; } }

        public List<Clip> SelectedClips
        {
            get { return Project.AllClips.Where(c => _selection.Contains(c.Id)).ToList(); }
        }

        public bool IsSelected(Clip clip)
        {
            return _selection.Contains(clip.Id);
        }

        /// <summary>클립을 고른다. 링크된 짝(사운드 분리)도 함께 고르고, 처음 클립을 <see cref="PrimaryClip"/>으로.</summary>
        public void Select(IEnumerable<Clip> clips, bool add = false)
        {
            if (!add)
                _selection.Clear();
            var list = clips.Where(c => c != null).ToList();
            foreach (var clip in ClipLinks.WithLinked(Project, list))
                _selection.Add(clip.Id);
            if (list.Count > 0)
                _primary = _anchor = list[0].Id;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>고르기를 켜고 끈다(Ctrl+누르기) — 링크된 짝도 함께.</summary>
        public void ToggleSelect(Clip clip)
        {
            var clips = ClipLinks.WithLinked(Project, new[] { clip });
            if (_selection.Contains(clip.Id))
            {
                foreach (var c in clips)
                    _selection.Remove(c.Id);
            }
            else
            {
                foreach (var c in clips)
                    _selection.Add(c.Id);
                _primary = _anchor = clip.Id;
            }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private string _primary;
        // Shift+누르기 범위의 기준 클립 — 보통 누르기·Ctrl+누르기로 정하고, 범위를 고르는 동안은 그대로 둔다
        private string _anchor;

        /// <summary>
        /// 범위 고르기(Shift+누르기): 기준 클립(마지막으로 보통 누른 클립)부터 target까지 — 두 클립의 트랙과 그 사이 트랙(타임라인에 보이는 차례)에서
        /// 시작이 두 클립의 시작 사이에 드는 클립 모두. 기준이 없으면 target 하나. add면 지금 고른 것에 더한다. 기준은 그대로, 속성 패널은 target을 보인다.
        /// </summary>
        public void SelectRange(Clip target, bool add = false)
        {
            if (target == null)
                return;
            var anchor = _anchor != null ? Project.FindClip(_anchor) : null;
            if (anchor == null)
            {
                Select(new[] { target }, add);
                return;
            }
            var order = DisplayTracks(Project);
            var a = order.IndexOf(Project.TrackOf(anchor));
            var b = order.IndexOf(Project.TrackOf(target));
            if (a < 0 || b < 0)
            {
                Select(new[] { target }, add);
                return;
            }
            var from = Math.Min(anchor.Start, target.Start);
            var to = Math.Max(anchor.Start, target.Start);
            var range = new List<Clip> { target };
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                range.AddRange(order[i].Clips.Where(c => c != target && c.Start >= from && c.Start <= to));
            if (!add)
                _selection.Clear();
            foreach (var clip in ClipLinks.WithLinked(Project, range))
                _selection.Add(clip.Id);
            _primary = target.Id;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>타임라인에 보이는 트랙 차례: 동영상 트랙(위 번호부터) → 오디오 트랙.</summary>
        public static List<Track> DisplayTracks(Project project)
        {
            var tracks = Enumerable.Reverse(project.VideoTracks).ToList();
            tracks.AddRange(project.AudioTracks);
            return tracks;
        }

        /// <summary>
        /// 속성 패널에서 함께 고칠 수 있는 클립의 종류: 자막 큐 · 글자 · 요소(모양별) · 분리한 사운드 · 동영상 · 사진 · 오디오.
        /// </summary>
        public static string PropertyKind(Project project, Clip clip)
        {
            if (project.TrackOf(clip)?.Subtitles == true)
                return "subtitle";
            if (clip.Kind == ClipKind.Text)
                return "text";
            if (clip.Kind == ClipKind.Art)
                return "art:" + (clip.Art?.Kind ?? ArtKind.Rectangle);
            if (clip.AudioOnly)
                return "audio-only";
            return "media:" + clip.MediaKind;
        }

        /// <summary>
        /// 속성 패널이 함께 고칠 클립들(첫째가 값을 보일 클립 — <see cref="PropertyClip"/>). 하나(또는 링크된 짝)면 그 하나,
        /// 여럿이면 링크된 영상·사운드 짝은 마지막으로 누른 쪽과 같은 쪽만 남기고 남은 것이 모두 같은 종류(<see cref="PropertyKind"/>)일 때 그것들. 아니면 빈 목록.
        /// </summary>
        public List<Clip> PropertyGroup
        {
            get
            {
                var single = PrimaryClip;
                if (single != null)
                    return new List<Clip> { single };
                var clips = SelectedClips;
                if (clips.Count < 2)
                    return new List<Clip>();
                var primary = clips.FirstOrDefault(c => c.Id == _primary) ?? clips[0];
                var ids = new HashSet<string>(clips.Select(c => c.Id));
                // 짝이 함께 골라졌으면 누른 쪽(영상 또는 사운드)과 같은 쪽만
                var group = clips.Where(c =>
                {
                    var partner = ClipLinks.LinkedPartner(Project, c);
                    return partner == null || !ids.Contains(partner.Id) || c.AudioOnly == primary.AudioOnly;
                }).ToList();
                var kind = PropertyKind(Project, primary);
                if (group.Count < 2 || group.Any(c => PropertyKind(Project, c) != kind))
                    return new List<Clip>();
                group.Remove(primary);
                group.Insert(0, primary);
                return group;
            }
        }

        /// <summary>속성 패널이 값을 보일 클립(<see cref="PropertyGroup"/>의 첫째) — 없으면 null.</summary>
        public Clip PropertyClip { get { return PropertyGroup.FirstOrDefault(); } }

        /// <summary>이미 고른 클립을 다시 눌렀다: 속성 패널이 그 클립을 보이게(링크된 짝 가운데 누른 쪽).</summary>
        public void SetPrimary(Clip clip)
        {
            if (clip == null || !_selection.Contains(clip.Id))
                return;
            _anchor = clip.Id;
            if (clip.Id == _primary)
                return;
            _primary = clip.Id;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 속성 패널·변형 틀이 보일 클립: 하나만 골랐으면 그것, 링크된 짝 둘만 골랐으면 마지막으로 누른 쪽(모르면 영상). 아니면 null.
        /// </summary>
        public Clip PrimaryClip
        {
            get
            {
                var clips = SelectedClips;
                if (clips.Count == 1)
                    return clips[0];
                if (clips.Count != 2 || clips[0].LinkId == null || clips[0].LinkId != clips[1].LinkId || clips[0].Unlinked)
                    return null;
                return clips.FirstOrDefault(c => c.Id == _primary) ?? clips.FirstOrDefault(c => !c.AudioOnly) ?? clips[0];
            }
        }

        public void ClearSelection()
        {
            if (_selection.Count == 0)
                return;
            _selection.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 편집을 하나 실행한다. 바뀐 게 있으면 실행 취소에 남기고 Changed를 낸다. EditException이면 되돌리고 false(LastError에 이유).
        /// </summary>
        public bool Execute(Action<Project> edit)
        {
            return Execute(edit, null);
        }

        private string _lastMergeKey;
        private DateTime _lastMergeTime;

        /// <summary>
        /// mergeKey가 바로 앞 편집과 같고 2초 안이면 실행 취소 한 단계로 합친다 — 슬라이더를 끄는 동안 단계가 수십 개 쌓이지 않게.
        /// </summary>
        public bool Execute(Action<Project> edit, string mergeKey)
        {
            var merge = mergeKey != null && mergeKey == _lastMergeKey && DateTime.UtcNow - _lastMergeTime < TimeSpan.FromSeconds(2) && _history.CanUndo;
            LastError = null;
            var before = ProjectFile.Snapshot(Project);
            try
            {
                edit(Project);
            }
            catch (EditException ex)
            {
                Project = ProjectFile.FromSnapshot(before);
                LastError = ex.Message;
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }
            catch (Exception ex)
            {
                // 예상하지 못한 오류도 편집 전으로 되돌린다(반쯤 바뀐 프로젝트가 남지 않게)
                Project = ProjectFile.FromSnapshot(before);
                LastError = "편집하지 못했습니다: " + ex.Message;
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }
            if (ProjectFile.Snapshot(Project) == before)
                return false;
            if (!merge)
                _history.Record(before);
            _lastMergeKey = mergeKey;
            _lastMergeTime = DateTime.UtcNow;
            Dirty = true;
            PruneSelection();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// 사용자 편집이 아닌 것(파일에서 다시 읽은 미디어 정보)을 바꾼다 — 실행 취소에 남기지 않고 저장 안 한 표시도 하지 않는다.
        /// </summary>
        public void Refresh(Action<Project> change)
        {
            change(Project);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool Undo()
        {
            _lastMergeKey = null;
            if (!_history.CanUndo)
                return false;
            Project = ProjectFile.FromSnapshot(_history.Undo(ProjectFile.Snapshot(Project)));
            Dirty = true;
            PruneSelection();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool Redo()
        {
            if (!_history.CanRedo)
                return false;
            Project = ProjectFile.FromSnapshot(_history.Redo(ProjectFile.Snapshot(Project)));
            Dirty = true;
            PruneSelection();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void New()
        {
            Reset(Project.CreateDefault(), null);
        }

        /// <summary>
        /// 복구 파일을 연다: 내용은 복구 파일에서, 저장할 곳은 원래 프로젝트 파일(없으면 새 프로젝트). 아직 저장하지 않은 상태로 둔다.
        /// </summary>
        public void OpenRecovered(string recoveryPath, string originalPath)
        {
            Reset(ProjectFile.Load(recoveryPath), string.IsNullOrWhiteSpace(originalPath) ? null : originalPath);
            Dirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Open(string path)
        {
            Reset(ProjectFile.Load(path), path);
        }

        public void Save(string path)
        {
            ProjectFile.Save(Project, path);
            FilePath = path;
            Dirty = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Reset(Project project, string path)
        {
            Project = project;
            FilePath = path;
            Dirty = false;
            _history.Clear();
            _selection.Clear();
            _playhead = 0;
            Changed?.Invoke(this, EventArgs.Empty);
            PlayheadChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void PruneSelection()
        {
            var ids = new HashSet<string>(Project.AllClips.Select(c => c.Id));
            if (_selection.RemoveWhere(id => !ids.Contains(id)) > 0)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
