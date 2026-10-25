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

        public void Select(IEnumerable<Clip> clips, bool add = false)
        {
            if (!add)
                _selection.Clear();
            foreach (var clip in clips)
                _selection.Add(clip.Id);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleSelect(Clip clip)
        {
            if (!_selection.Remove(clip.Id))
                _selection.Add(clip.Id);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
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
