using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>프로젝트 JSON(*.fvproj). 저장할 때 파일 경로는 프로젝트 파일 기준 상대 경로로(폴더째 옮겨도 열리게) 바꾼다.</summary>
    public static class ProjectFile
    {
        public const string Extension = ".fvproj";
        public const string DialogFilter = "동영상 편집 프로젝트 (*.fvproj)|*.fvproj";

        private static readonly JsonSerializerOptions Options = CreateOptions(true);
        private static readonly JsonSerializerOptions CompactOptions = CreateOptions(false);

        private static JsonSerializerOptions CreateOptions(bool indented)
        {
            var options = new JsonSerializerOptions { WriteIndented = indented };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        /// <summary>메모리 안 상태(실행 취소용) — 경로는 그대로.</summary>
        public static string Snapshot(Project project)
        {
            return JsonSerializer.Serialize(project, CompactOptions);
        }

        public static Project FromSnapshot(string json)
        {
            var project = JsonSerializer.Deserialize<Project>(json, CompactOptions);
            Validate(project);
            return project;
        }

        public static void Save(Project project, string path)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            var copy = FromSnapshot(Snapshot(project));
            // 예전 형식 파일을 열어 저장해도 지금 형식으로 쓴다 — 애니메이션·키프레임·아트 클립이 있을 때만 새 형식(없으면 예전 플러그인에서도 열린다)
            copy.Version = ClipAnimations.Any(copy) || ClipKeyframes.Any(copy) ? Project.AnimationVersion
                : copy.AllClips.Any(c => c.Kind == ClipKind.Art) ? Project.ArtVersion : Project.CurrentVersion;
            foreach (var media in copy.Media)
                media.Path = Project.Relative(folder, media.Path);
            foreach (var clip in copy.AllClips)
                clip.Path = Project.Relative(folder, clip.Path);
            var json = JsonSerializer.Serialize(copy, Options);
            // 저장 중 실패해도 원래 파일이 남게 임시 파일에 쓰고 바꾼다
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        public static Project Load(string path)
        {
            Project project;
            try
            {
                project = JsonSerializer.Deserialize<Project>(File.ReadAllText(path), Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("프로젝트 파일 형식이 아닙니다: " + ex.Message, ex);
            }
            if (project == null)
                throw new InvalidDataException("프로젝트 파일이 비어 있습니다.");
            if (project.Version > Project.AnimationVersion)
                throw new InvalidDataException("더 새 버전의 플러그인에서 만든 프로젝트입니다(형식 " + project.Version + ").");
            Validate(project);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            foreach (var media in project.Media)
                media.Path = Project.Absolute(folder, media.Path);
            foreach (var clip in project.AllClips)
                clip.Path = Project.Absolute(folder, clip.Path);
            return project;
        }

        /// <summary>읽은 프로젝트가 편집 규칙을 만족하게 고친다(빠진 트랙·잘못된 값).</summary>
        private static void Validate(Project project)
        {
            if (project.RateNumerator <= 0 || project.RateDenominator <= 0)
            {
                project.RateNumerator = 30;
                project.RateDenominator = 1;
            }
            if (project.Rate.Fps < 1 || project.Rate.Fps > 240)
                project.Rate = FrameRate.Default;
            project.Width = Math.Max(16, Math.Min(8192, project.Width - project.Width % 2));
            project.Height = Math.Max(16, Math.Min(8192, project.Height - project.Height % 2));
            if (project.SampleRate < 8000 || project.SampleRate > 192000)
                project.SampleRate = 48000;
            if (project.VideoTracks.Count == 0)
                project.VideoTracks.Add(new Track { Kind = TrackKind.Video });
            foreach (var track in project.VideoTracks)
                track.Kind = TrackKind.Video;
            foreach (var track in project.AudioTracks)
            {
                track.Kind = TrackKind.Audio;
                track.Subtitles = false;
            }
            // 주 트랙(동영상 1)은 자막 트랙일 수 없고, 자막 트랙에는 글자만 있어야 한다(아니면 보통 트랙으로 — 사진·소리가 숨지 않게)
            project.VideoTracks[0].Subtitles = false;
            foreach (var track in project.VideoTracks.Where(t => t.Subtitles && t.Clips.Any(c => c.Kind != ClipKind.Text)))
                track.Subtitles = false;
            foreach (var track in project.AllTracks.Where(t => !t.Subtitles))
                track.WordHighlight = false;
            // Id는 16진수 짧은 글이고 겹치지 않아야 한다(프로젝트 파일은 남이 만든 것일 수 있음)
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var item in project.AllTracks.Select(t => (object)t).Concat(project.AllClips).Concat(project.Markers))
            {
                var id = item is Track t ? t.Id : item is Clip c ? c.Id : ((Marker)item).Id;
                if (string.IsNullOrEmpty(id) || id.Length > 32 || !id.All(Uri.IsHexDigit) || !ids.Add(id))
                {
                    id = Track.NewId();
                    ids.Add(id);
                    if (item is Track track)
                        track.Id = id;
                    else if (item is Clip clip)
                        clip.Id = id;
                    else
                        ((Marker)item).Id = id;
                }
            }
            foreach (var clip in project.AllClips)
            {
                clip.FontSize = Math.Max(4, Math.Min(1000, clip.FontSize));
                clip.Scale = Math.Max(0.01, Math.Min(20, clip.Scale));
                if (clip.Speed < TimelineEdits.MinSpeed || clip.Speed > TimelineEdits.MaxSpeed)
                    clip.Speed = 1;
                if (clip.SourceOut <= clip.SourceIn)
                    clip.SourceOut = clip.SourceIn + 1;
                // 전환 효과: id 형식이 아니면 디졸브로. 모르는 id는 남겨 둔다(효과 파일이 있는 PC에서는 그 효과로).
                // 함께 저장된 정의는 남이 만든 것일 수 있으므로 검사를 거친 것만(id가 다르면 버림)
                if (!Transitions.IsValidId(clip.Transition))
                    clip.Transition = Transitions.DefaultId;
                if (clip.TransitionDef != null)
                {
                    var def = Transitions.Check(clip.TransitionDef, out _);
                    clip.TransitionDef = def != null && def.Id == clip.Transition ? def : null;
                }
                clip.TransitionIn = Math.Max(0, clip.TransitionIn);
                if (clip.Kind == ClipKind.Art)
                {
                    clip.Art ??= new ArtShape();
                    clip.Art.Normalize();
                    clip.Style ??= TimelineEdits.ArtStyle();
                }
                else
                {
                    clip.Art = null;
                }
                if (clip.Kind != ClipKind.Text && clip.Kind != ClipKind.Art)
                    clip.Style = null;
                else
                    clip.Style?.Normalize();
                // 글자 상자: 글자 클립에만, NaN·무한대·음수는 자동(0), 너무 큰 값은 상한까지, 모르는 세로 자리는 가운데
                clip.TextBoxWidth = clip.Kind == ClipKind.Text ? ClipLayout.TextBoxLength(clip.TextBoxWidth) : 0;
                clip.TextBoxHeight = clip.Kind == ClipKind.Text ? ClipLayout.TextBoxLength(clip.TextBoxHeight) : 0;
                if (clip.Kind != ClipKind.Text || !Enum.IsDefined(clip.TextVerticalAlign))
                    clip.TextVerticalAlign = TextVerticalAlign.Middle;
                clip.Words = CleanWords(clip);
                // 동영상 효과: 동영상·사진 클립에만, 값은 효과 범위 안으로 — 모르는 효과는 남겨 두고 그리지 않는다(VideoEffects.Clean)
                clip.Effects = VideoEffects.Clean(clip);
                // 애니메이션: 화면 클립에만, 효과·분류·값을 규칙 안으로(예전 파일은 없음)
                clip.Animations = ClipAnimations.Clean(clip);
                // 사용자 키프레임: 화면 클립에만, 아는 값·범위 안 숫자만(예전 파일은 없음)
                clip.Keyframes = ClipKeyframes.Clean(clip);
            }
            // 자막 트랙의 큐는 트랙 공통 모양으로 입히므로 움직이지 않는다
            foreach (var clip in project.VideoTracks.Where(t => t.Subtitles).SelectMany(t => t.Clips))
            {
                clip.Animations = null;
                clip.Keyframes = null;
            }
            // 사운드 분리 링크: 같은 원본의 영상·사운드 짝만(예전 프로젝트는 링크 없음)
            ClipLinks.Normalize(project);
            foreach (var track in project.AllTracks)
                track.Sort();
            project.RenameTracks();
        }

        /// <summary>단어 시각(남이 만든 파일일 수 있음): 글자 클립에만, 빈 단어는 빼고 단어는 100자·1,000개까지, 끝은 시작 뒤로, 시작 순으로.</summary>
        private static System.Collections.Generic.List<ClipWord> CleanWords(Clip clip)
        {
            if (clip.Kind != ClipKind.Text || clip.Words == null)
                return null;
            var words = clip.Words.Where(w => w != null && !string.IsNullOrWhiteSpace(w.Text) && w.Text.Length <= 100).Take(1000).ToList();
            foreach (var word in words)
            {
                word.Start = Math.Max(-100_000_000L, Math.Min(100_000_000L, word.Start));
                word.End = Math.Max(word.Start, Math.Min(100_000_000L, word.End));
            }
            return words.Count == 0 ? null : words.OrderBy(w => w.Start).ToList();
        }
    }

    /// <summary>실행 취소/다시 실행: 편집 전 프로젝트 상태(JSON)를 쌓는다.</summary>
    public sealed class EditHistory
    {
        private readonly System.Collections.Generic.List<string> _undo = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> _redo = new System.Collections.Generic.List<string>();

        public int Limit { get; set; } = 300;
        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }

        public void Record(string before)
        {
            _undo.Add(before);
            if (_undo.Count > Limit)
                _undo.RemoveAt(0);
            _redo.Clear();
        }

        public string Undo(string current)
        {
            var state = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(current);
            return state;
        }

        public string Redo(string current)
        {
            var state = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(current);
            return state;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }
    }
}
