using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 편집기 명령 한곳: 메뉴·도구 막대·단축키·오른쪽 메뉴가 모두 이것을 부른다(같은 동작).
    /// 프로젝트를 바꾸는 일은 <see cref="EditorSession.Execute(Action{Project})"/>로 — 실행 취소에 남고, 거부되면 이유를 상태 줄에.
    /// </summary>
    internal sealed class EditorCommands
    {
        private static List<Clip> _clipboard = new List<Clip>();
        private static List<int> _clipboardTracks = new List<int>();
        // 복사한 클립이 자막 트랙에 있었는지(붙여 넣을 때 트랙 번호가 밀려도 자막 큐는 자막 트랙으로)
        private static List<bool> _clipboardSubtitles = new List<bool>();
        private static FrameRate _clipboardRate = FrameRate.Default;
        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly PreviewPlayer _player;
        private readonly FrameworkElement _owner;

        public EditorCommands(EditorSession session, EditorServices services, PreviewPlayer player, FrameworkElement owner)
        {
            _session = session;
            _services = services;
            _player = player;
            _owner = owner;
        }

        public TimelineView Timeline { get; set; }

        /// <summary>상태 줄 글.</summary>
        public event Action<string> StatusChanged;

        public void Status(string text)
        {
            StatusChanged?.Invoke(text);
        }

        private Project Project { get { return _session.Project; } }

        private bool Run(Action<Project> edit, string done = null, string mergeKey = null)
        {
            _player.Stop();
            var ok = _session.Execute(edit, mergeKey);
            if (!ok && _session.LastError != null)
                Status(_session.LastError);
            else if (ok && done != null)
                Status(done);
            return ok;
        }

        /// <summary>FFmpeg가 있어야 하는 명령 앞에서: 없으면 설치 창을 띄운다.</summary>
        private async Task<FfmpegTool> NeedTool()
        {
            var tool = _services.Tool ?? await _services.LocateAsync();
            if (tool != null)
                return tool;
            return FfmpegSetupDialog.Show(_services, _owner) ? _services.Tool : null;
        }

        public void FfmpegSetup()
        {
            if (FfmpegSetupDialog.Show(_services, _owner))
                Status("FFmpeg: " + _services.Tool.VersionLine);
        }

        // ---------- 실행 취소·선택 ----------

        public void Undo()
        {
            _player.Stop();
            Status(_session.Undo() ? "실행 취소" : "더 취소할 것이 없습니다.");
        }

        public void Redo()
        {
            _player.Stop();
            Status(_session.Redo() ? "다시 실행" : "다시 실행할 것이 없습니다.");
        }

        public void SelectAll()
        {
            _session.Select(Project.AllTracks.Where(t => !t.Locked).SelectMany(t => t.Clips));
        }

        /// <summary>고른 클립, 없으면 재생 헤드 아래 클립(위 트랙 우선, 잠긴 트랙 빼고).</summary>
        private List<Clip> Targets()
        {
            var selected = _session.SelectedClips;
            if (selected.Count > 0)
                return selected;
            var playhead = _session.Playhead;
            var under = Project.VideoTracks.AsEnumerable().Reverse().Concat(Project.AudioTracks)
                .Where(t => !t.Locked)
                .Select(t => TimelineEdits.ClipAt(t, playhead))
                .FirstOrDefault(c => c != null);
            return under == null ? new List<Clip>() : new List<Clip> { under };
        }

        // ---------- 편집 ----------

        public void Split()
        {
            var playhead = _session.Playhead;
            var ids = _session.SelectedClips.Where(c => c.Contains(playhead)).Select(c => c.Id).ToList();
            Run(p =>
            {
                // 고른 클립이 재생 헤드에 걸치지 않으면 재생 헤드 아래의 모든 클립(잠긴 트랙 제외)을 나눈다
                var clips = ids.Count > 0 ? ids.Select(p.FindClip).Where(c => c != null).ToList() : p.AllClips.Where(c => c.Contains(playhead)).ToList();
                if (TimelineEdits.Split(p, clips, playhead).Count == 0)
                    throw new EditException("재생 헤드에 걸친 클립이 없습니다(클립 안에 재생 헤드를 두고 Ctrl+B).");
            }, "분할했습니다.");
        }

        public void Delete(bool ripple)
        {
            var ids = _session.SelectedClips.Select(c => c.Id).ToList();
            if (ids.Count == 0)
            {
                if (ripple)
                    CloseGap();
                else
                    Status("지울 클립을 고르세요.");
                return;
            }
            Run(p =>
            {
                if (TimelineEdits.Delete(p, ids.Select(p.FindClip).Where(c => c != null), ripple) == 0)
                    throw new EditException("잠긴 트랙의 클립은 지울 수 없습니다.");
            }, ripple ? "리플 삭제했습니다(빈틈을 당김)." : "삭제했습니다.");
        }

        /// <summary>재생 헤드가 있는 빈틈을 닫는다(주 트랙부터).</summary>
        public void CloseGap()
        {
            var playhead = _session.Playhead;
            Run(p =>
            {
                foreach (var track in p.VideoTracks.Concat(p.AudioTracks).Where(t => !t.Locked))
                {
                    if (TimelineEdits.CloseGap(p, track, playhead))
                        return;
                }
                throw new EditException("재생 헤드가 빈틈 위에 있지 않습니다.");
            }, "빈틈을 닫았습니다.");
        }

        public void TrimToPlayhead(bool start)
        {
            var playhead = _session.Playhead;
            var ids = Targets().Select(c => c.Id).ToList();
            Run(p =>
            {
                var trimmed = ids.Select(p.FindClip).Where(c => c != null).Count(c => TimelineEdits.TrimToPlayhead(p, c, playhead, start));
                if (trimmed == 0)
                    throw new EditException("재생 헤드가 고른 클립 안에 있어야 합니다.");
            }, start ? "재생 헤드까지 앞을 다듬었습니다." : "재생 헤드까지 뒤를 다듬었습니다.");
        }

        public void EditClip(string id, Action<Project, Clip> change, string mergeKey)
        {
            Run(p =>
            {
                var clip = p.FindClip(id);
                if (clip == null)
                    return;
                TimelineEdits.EnsureEditable(p.TrackOf(clip));
                change(p, clip);
            }, null, mergeKey);
        }

        public void SetSpeed(string id, double speed)
        {
            Run(p => TimelineEdits.SetSpeed(p, p.FindClip(id), speed), "속도 ×" + UiKit.Number(speed));
        }

        /// <summary>속도·길이(Ctrl+R): 동영상·오디오는 속도, 사진·글자는 길이(초)를 묻는다.</summary>
        public void ChangeSpeed()
        {
            var clip = Targets().FirstOrDefault();
            if (clip == null)
            {
                Status("속도를 바꿀 클립을 고르세요.");
                return;
            }
            if (clip.HasSourceLimit)
            {
                var text = Dialogs.Input(_owner, "속도", "속도 배율(0.1 ~ 10). 2 = 두 배 빠르게, 0.5 = 절반 속도", UiKit.Number(clip.Speed));
                double speed;
                if (text != null && UiKit.TryNumber(text, out speed))
                    SetSpeed(clip.Id, speed);
            }
            else
            {
                var text = Dialogs.Input(_owner, "길이", "길이(초)", UiKit.Number(Project.Rate.ToSeconds(clip.Duration)));
                double seconds;
                if (text != null && UiKit.TryNumber(text, out seconds) && seconds > 0)
                    Run(p => TimelineEdits.TrimEnd(p, p.FindClip(clip.Id), clip.Start + Math.Max(1, p.Rate.ToFrames(seconds))), "길이를 바꿨습니다.");
            }
        }

        public void DetachAudio()
        {
            var ids = Targets().Select(c => c.Id).ToList();
            if (ids.Count == 0)
            {
                Status("오디오를 분리할 동영상 클립을 고르세요.");
                return;
            }
            Run(p =>
            {
                foreach (var id in ids)
                    TimelineEdits.DetachAudio(p, p.FindClip(id));
            }, "오디오를 분리했습니다(오디오 트랙).");
        }

        /// <summary>고른 클립 소리 끄기/켜기(Ctrl+Shift+M).</summary>
        public void ToggleClipMute()
        {
            var ids = Targets().Where(c => c.HasAudio).Select(c => c.Id).ToList();
            Run(p =>
            {
                foreach (var clip in ids.Select(p.FindClip).Where(c => c != null))
                {
                    TimelineEdits.EnsureEditable(p.TrackOf(clip));
                    clip.Volume = clip.Volume > 0 ? 0 : 1;
                }
            }, "클립 소리를 바꿨습니다.");
        }

        public void Rotate(double degrees)
        {
            var ids = Targets().Where(c => c.IsVisual).Select(c => c.Id).ToList();
            Run(p =>
            {
                foreach (var clip in ids.Select(p.FindClip).Where(c => c != null))
                {
                    TimelineEdits.EnsureEditable(p.TrackOf(clip));
                    clip.Rotation = (clip.Rotation + degrees) % 360;
                }
            }, "회전했습니다.");
        }

        public void SetMagnetic(bool on)
        {
            Run(p =>
            {
                p.Magnetic = on;
                if (on)
                    TimelineEdits.Compact(p.MainTrack);
            }, on ? "마그네틱 주 트랙: 켬(빈틈 없이 붙음)" : "마그네틱 주 트랙: 끔");
        }

        // ---------- 복사·붙여넣기(플러그인 안에서만 — 시스템 클립보드는 쓰지 않음) ----------

        public void Copy(bool cut)
        {
            var clips = _session.SelectedClips;
            if (clips.Count == 0)
            {
                Status("복사할 클립을 고르세요.");
                return;
            }
            var snapshot = ProjectFile.FromSnapshot(ProjectFile.Snapshot(Project));
            _clipboard = clips.Select(c => snapshot.FindClip(c.Id)).ToList();
            _clipboardRate = Project.Rate;
            _clipboardTracks = clips.Select(c =>
            {
                var track = Project.TrackOf(c);
                return track.Kind == TrackKind.Video ? Project.VideoTracks.IndexOf(track) : -1 - Project.AudioTracks.IndexOf(track);
            }).ToList();
            _clipboardSubtitles = clips.Select(c => Project.TrackOf(c).Subtitles).ToList();
            if (cut)
                Delete(false);
            Status(clips.Count + "개 클립을 " + (cut ? "잘라 냈습니다." : "복사했습니다."));
        }

        public void Paste()
        {
            if (_clipboard.Count == 0)
            {
                Status("붙여 넣을 클립이 없습니다(Ctrl+C로 먼저 복사).");
                return;
            }
            var playhead = _session.Playhead;
            long first;
            var tracks = _clipboardTracks;
            var subtitles = _clipboardSubtitles;
            // 다른 프레임 레이트의 프로젝트(다른 편집 창)에서 복사했으면 초 기준으로 바꾼다
            var rate = _clipboardRate;
            var clips = _clipboard.Select(c =>
            {
                var copy = c.CloneWithNewId();
                Core.ProjectSettings.ConvertClip(copy, rate, Project.Rate);
                return copy;
            }).ToList();
            first = clips.Min(c => c.Start);
            var pasted = new List<string>();
            if (Run(p =>
                {
                    for (var i = 0; i < clips.Count; i++)
                    {
                        var copy = clips[i].CloneWithNewId();
                        pasted.Add(copy.Id);
                        var index = tracks[i];
                        var track = index >= 0
                            ? (index < p.VideoTracks.Count ? p.VideoTracks[index] : p.MainTrack)
                            : (-1 - index < p.AudioTracks.Count ? p.AudioTracks[-1 - index] : p.AudioTracks.FirstOrDefault() ?? TimelineEdits.AddTrack(p, TrackKind.Audio));
                        // 트랙 번호가 밀렸을 수 있다(자막 트랙 아래로 새 트랙이 끼어듦) — 자막 큐는 자막 트랙으로, 다른 클립은 자막 트랙이 아닌 곳으로
                        var subtitle = i < subtitles.Count && subtitles[i];
                        if (track.Kind == TrackKind.Video && track.Subtitles != subtitle)
                            track = subtitle ? p.VideoTracks.FirstOrDefault(t => t.Subtitles) ?? track : p.MainTrack;
                        TimelineEdits.Place(p, track, copy, playhead + copy.Start - first);
                    }
                }, "붙여 넣었습니다."))
                _session.Select(pasted.Select(Project.FindClip).Where(c => c != null));
        }

        // ---------- 트랙 ----------

        public void SetTrackFlag(string trackId, Action<Track> change)
        {
            Run(p =>
            {
                var track = p.FindTrack(trackId);
                if (track != null)
                    change(track);
            });
        }

        public void AddTrack(TrackKind kind)
        {
            Run(p => TimelineEdits.AddTrack(p, kind), (kind == TrackKind.Video ? "동영상" : "오디오") + " 트랙을 더했습니다.");
        }

        // ---------- 전환 ----------

        /// <summary>전환을 넣었다 — 넣은 클립 Id(타임라인이 배지를 반짝인다).</summary>
        public event Action<IReadOnlyList<string>> TransitionsApplied;

        /// <summary>전환을 넣을 수 있는 고른 클립(화면 클립, 자막 큐 제외)의 Id.</summary>
        private List<string> TransitionTargets()
        {
            return _session.SelectedClips
                .Where(c => c.IsVisual && Project.TrackOf(c) is Track t && t.Kind == TrackKind.Video && !t.Subtitles)
                .Select(c => c.Id).ToList();
        }

        /// <summary>[효과] 고른 클립마다 그 앞(앞 클립에서 바뀌어 들어오는 곳)에 전환을 넣는다. 타임라인 위치는 그대로.</summary>
        public void ApplyTransition(string id, double seconds)
        {
            var ids = TransitionTargets();
            if (ids.Count == 0)
            {
                Status("전환을 넣을 클립(바뀌어 들어올 뒤 클립)을 타임라인에서 고르세요. 클립 사이마다 넣으려면 [모든 클립 사이에]를 누르세요.");
                return;
            }
            ApplyTransition(id, seconds, ids);
        }

        /// <summary>효과 탭에서 끈 전환을 클립에 놓음 — 데이터는 "효과 id|초".</summary>
        public void DropTransition(string data, string clipId)
        {
            var parts = (data ?? "").Split('|');
            if (parts.Length != 2 || Transitions.Find(parts[0]) == null || !UiKit.TryNumber(parts[1], out var seconds))
                return;
            ApplyTransition(parts[0], seconds, new[] { clipId });
        }

        private void ApplyTransition(string effect, double seconds, IReadOnlyList<string> ids)
        {
            var def = Transitions.Find(effect);
            if (def == null)
                return;
            var frames = Math.Max(1, Project.Rate.ToFrames(seconds));
            if (Run(p =>
                {
                    foreach (var id in ids)
                        TimelineEdits.SetTransition(p, p.FindClip(id), def.Id, frames);
                }, "'" + def.Name + "' 전환을 " + (ids.Count == 1 ? "클립" : ids.Count + "개 클립") + " 앞에 넣었습니다(" + UiKit.Number(seconds) + "초)."))
                ShowTransitions(ids);
        }

        /// <summary>전환을 넣은 클립을 골라 두고 타임라인 배지를 반짝인다(어디에 들어갔는지 보이게).</summary>
        private void ShowTransitions(IReadOnlyList<string> ids)
        {
            _session.Select(ids.Select(Project.FindClip).Where(c => c != null));
            TransitionsApplied?.Invoke(ids);
        }

        /// <summary>[모든 클립 사이에] 고른 클립의 트랙(없으면 주 트랙)에서 서로 붙어 있는 클립 사이마다 전환을 넣는다.</summary>
        public void ApplyTransitionBetween(string effect, double seconds)
        {
            var def = Transitions.Find(effect);
            if (def == null)
                return;
            var selected = _session.SelectedClips.Select(Project.TrackOf).FirstOrDefault(t => t != null && t.Kind == TrackKind.Video && !t.Subtitles);
            var track = selected ?? Project.MainTrack;
            var clips = track.Clips.Where(c => c.IsVisual).OrderBy(c => c.Start).ToList();
            var ids = clips.Skip(1).Where((c, i) => c.Start == clips[i].End).Select(c => c.Id).ToList();
            var places = ids.Count;
            if (places == 0)
            {
                Status("'" + track.Name + "'에 서로 붙어 있는 클립이 없어 전환을 넣을 곳이 없습니다.");
                return;
            }
            var trackId = track.Id;
            var frames = Math.Max(1, Project.Rate.ToFrames(seconds));
            if (Run(p => TimelineEdits.SetTransitionsBetween(p, p.FindTrack(trackId), def.Id, frames),
                    "'" + def.Name + "' 전환을 '" + track.Name + "'의 클립 사이 " + places + "곳에 넣었습니다(" + UiKit.Number(seconds) + "초)."))
                ShowTransitions(ids);
        }

        /// <summary>[전환 빼기] 고른 클립 앞의 전환을 뺀다.</summary>
        public void RemoveTransition()
        {
            var ids = TransitionTargets().Where(id => Project.FindClip(id).TransitionIn > 0).ToList();
            if (ids.Count == 0)
            {
                Status("고른 클립에 전환이 없습니다.");
                return;
            }
            Run(p =>
            {
                foreach (var id in ids)
                {
                    var clip = p.FindClip(id);
                    TimelineEdits.SetTransition(p, clip, clip.Transition, 0);
                }
            }, "전환을 뺐습니다.");
        }

        /// <summary>[효과 폴더 열기] 사용자 전환 효과 폴더를 만들고(처음이면 쓰는 법 README.txt도) 탐색기로 연다.</summary>
        public void OpenTransitionFolder()
        {
            var folder = _services.TransitionFolder;
            try
            {
                Directory.CreateDirectory(folder);
                var readme = Path.Combine(folder, "README.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme, TransitionFolderReadme.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new System.Text.UTF8Encoding(true));
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
                Status("효과 폴더: *.json에 효과를 적고 [효과] 탭의 [다시 읽기]를 누르세요(쓰는 법은 README.txt).");
            }
            catch (Exception ex)
            {
                Status("효과 폴더를 열지 못했습니다: " + ex.Message);
            }
        }

        private const string TransitionFolderReadme =
@"전환 효과 폴더

이 폴더의 *.json 파일에 효과를 적고 [효과] 탭의 [다시 읽기]를 누르면 효과가 더해집니다.

{
  ""transitions"": [
    { ""id"": ""MyWipe"", ""name"": ""내 대각선 닦기"", ""group"": ""내 효과"", ""xfade"": ""wipetl"" },
    { ""id"": ""Ring"", ""name"": ""고리"", ""group"": ""모양"", ""expr"": ""if(lt(abs(hypot(X-W/2,Y-H/2)/H-0.3),(1-P)*0.8),B,A)"" }
  ]
}

- id: 영문으로 시작하는 영문·숫자·_·- (프로젝트에 저장되는 이름, 기본 효과와 겹치면 안 됨)
- name: 효과 탭에 보일 이름(40자까지), group: 묶음(없으면 ""내 효과"")
- xfade: FFmpeg xfade 효과 이름(wipeleft, circleopen, pixelize 등) — 또는
- expr: 수식. X·Y 픽셀 위치, W·H 화면 크기, P 진행(1에서 0으로), A 앞 화면 값, B 새 화면 값, PLANE 색 평면,
  a0~a3(x,y)·b0~b3(x,y) 다른 위치의 값.
  글자는 영문·숫자·공백과 . , + - * / ^ ( ) 만, 함수는 if ifnot lt lte gt gte eq between clip not abs min max hypot
  sin cos tan asin acos atan atan2 sinh cosh tanh sqrt pow exp log floor ceil round trunc mod sgn squish gauss isnan isinf.
  수식 효과는 내장 효과보다 그리기가 느립니다.
- ffmpeg: 필요한 FFmpeg 주 버전(선택)

사용자 효과를 넣은 클립은 프로젝트 파일에 효과 정의를 함께 저장하므로, 이 폴더가 없는 PC에서도 같게 그려집니다.
";

        /// <summary>[화면 맞춤 방식]: 오른쪽 메뉴를 연 클립(고른 클립 가운데 있으면 고른 클립 모두)의 맞춤 방식을 바꾼다.</summary>
        public void SetFit(ClipFit fit, string clipId = null)
        {
            var selected = _session.SelectedClips.Where(c => c.Kind == ClipKind.Media && c.IsVisual).Select(c => c.Id).ToList();
            var ids = clipId == null || selected.Contains(clipId) ? selected : new List<string> { clipId };
            if (ids.Count == 0)
            {
                Status("맞춤 방식을 바꿀 동영상·사진 클립을 고르세요.");
                return;
            }
            Run(p => TimelineEdits.SetFit(p, ids.Select(p.FindClip), fit),
                "'" + EditorServices.FitName(fit) + "'(으)로 바꿨습니다" + (ids.Count > 1 ? "(클립 " + ids.Count + "개)." : ".") + " — " + EditorServices.FitHint(fit));
        }

        /// <summary>자막 트랙의 [말하는 단어 강조] 켜기·끄기.</summary>
        public void SetWordHighlight(string trackId, bool on)
        {
            Run(p => TimelineEdits.SetWordHighlight(p, p.FindTrack(trackId), on),
                on ? "말하는 단어를 노란색으로 강조합니다(음성 인식으로 만든 큐만 — 글을 고친 큐는 강조하지 않습니다)." : "말하는 단어 강조를 껐습니다.");
        }

        public void AddSubtitleTrack()
        {
            Run(p => TimelineEdits.AddSubtitleTrack(p), "자막 트랙을 더했습니다. SRT·VTT를 끌어 놓거나 [파일 > 자막 가져오기…]로 큐를 넣으세요.");
        }

        public void RemoveTrack(string trackId)
        {
            var track = Project.FindTrack(trackId);
            if (track == null)
                return;
            if (track.Clips.Count > 0 && UiKit.Ask(_owner, "트랙 삭제", "'" + track.Name + "' 트랙에 클립 " + track.Clips.Count + "개가 있습니다. 함께 지울까요?", "지우기", "취소") != 0)
                return;
            Run(p => TimelineEdits.RemoveTrack(p, p.FindTrack(trackId)), "트랙을 지웠습니다.");
        }

        // ---------- 미디어 ----------

        public void Import()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "미디어 가져오기",
                Multiselect = true,
                Filter = "동영상·사진·오디오·자막|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.mts;*.m2ts;*.ts;*.flv;*.3gp;*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff;*.heic;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.srt;*.vtt|모든 파일|*.*",
                InitialDirectory = SafeFolder(_services.Setting(EditorServices.LastFolderKey), Environment.SpecialFolder.MyVideos)
            };
            if (dialog.ShowDialog(UiKit.OwnerOf(_owner)) != true)
                return;
            _services.SaveSetting(EditorServices.LastFolderKey, Path.GetDirectoryName(dialog.FileNames[0]));
            _ = ImportPaths(dialog.FileNames, null, -1);
        }

        /// <summary>[자막 가져오기]: SRT·VTT를 골라 맨 위 새 자막 트랙에 큐로 놓는다.</summary>
        public void ImportSubtitles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "자막 가져오기",
                Filter = "자막(SRT·VTT)|*.srt;*.vtt|모든 파일|*.*",
                InitialDirectory = SafeFolder(_services.Setting(EditorServices.LastFolderKey), Environment.SpecialFolder.MyVideos)
            };
            if (dialog.ShowDialog(UiKit.OwnerOf(_owner)) != true)
                return;
            _services.SaveSetting(EditorServices.LastFolderKey, Path.GetDirectoryName(dialog.FileName));
            ImportSubtitleFile(dialog.FileName);
        }

        /// <summary>
        /// 자막 파일 하나를 읽어 자막 구역으로(10MB까지, UTF-8·UTF-16·CP949): 자막 트랙 위에 놓았으면 그 트랙에, 아니면 새 자막 트랙에.
        /// 시각은 파일 그대로(0초 기준). 놓은 큐 수(실패하면 0).
        /// </summary>
        public int ImportSubtitleFile(string path, Track target = null)
        {
            List<SubtitleCue> cues;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > SubtitleFile.MaxBytes)
                {
                    Status("자막 파일이 너무 큽니다(10MB까지): " + info.Name);
                    return 0;
                }
                cues = SubtitleFile.Parse(SubtitleFile.Decode(File.ReadAllBytes(path)));
            }
            catch (Exception ex)
            {
                // 자막 파일은 남이 만든 것일 수 있다 — 무엇이 나든 편집기를 멈추지 않고 알린다
                Status("자막 파일을 읽지 못했습니다: " + ex.Message);
                return 0;
            }
            if (cues.Count == 0)
            {
                Status("자막을 찾지 못했습니다(SRT·VTT 형식이 아님): " + Path.GetFileName(path));
                return 0;
            }
            var targetId = target != null && target.Subtitles ? target.Id : null;
            return Run(p => TimelineEdits.ImportSubtitles(p, cues, targetId != null ? p.FindTrack(targetId) : null),
                "자막 " + cues.Count + "개를 가져왔습니다(" + Path.GetFileName(path) + ").") ? cues.Count : 0;
        }

        /// <summary>
        /// [음성 인식으로 자막 만들기]: 고른 클립 중 소리 있는 것이 있으면 그 클립만, 없으면 타임라인 전체 소리를 이 PC에서 인식해(whisper.cpp)
        /// 새 자막 트랙에 큐로 놓는다. 시각은 타임라인 위치(클립이면 클립 시작·속도에 맞춤). clipId를 주면 그 클립(오른쪽 메뉴).
        /// </summary>
        public async Task SpeechToText(string clipId = null)
        {
            _player.Stop();
            var clip = (clipId != null ? new[] { Project.FindClip(clipId) } : (IEnumerable<Clip>)_session.SelectedClips)
                .FirstOrDefault(c => c != null && c.Kind == ClipKind.Media && c.HasAudio);
            var project = Project;
            if (clip == null && !project.AllClips.Any(c => c.Kind == ClipKind.Media && c.HasAudio))
            {
                Status("소리가 있는 클립이 없습니다. 내레이션(동영상·오디오)을 타임라인에 놓고 다시 하세요.");
                return;
            }
            var tool = await NeedTool();
            if (tool == null)
                return;
            // 창이 떠 있는 동안 인식하므로 지금 상태의 사본으로(편집과 섞이지 않게)
            var copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            var target = clip != null ? copy.FindClip(clip.Id) : null;
            var rate = copy.Rate;
            var source = target != null
                ? "고른 클립 — " + target.Name + " (" + rate.Timecode(target.Start) + " ~ " + rate.Timecode(target.End) + ")"
                : "타임라인 전체 소리 (00:00:00:00 ~ " + rate.Timecode(copy.Duration) + ", 모든 트랙을 섞은 것)";
            SpeechToTextDialog dialog;
            try
            {
                dialog = new SpeechToTextDialog(_services, source, target == null, (whisper, options, progress, cancellation) => target != null
                    ? SpeechRecognizer.RecognizeClip(tool, whisper, options, target, rate, progress, cancellation)
                    : SpeechRecognizer.RecognizeTimeline(tool, whisper, options, copy, progress, cancellation));
                var owner = UiKit.OwnerOf(_owner);
                if (owner != null && owner.IsLoaded)
                    dialog.Owner = owner;
            }
            catch (Exception ex)
            {
                Status("음성 인식 창을 열지 못했습니다: " + ex.Message);
                return;
            }
            if (dialog.ShowDialog() != true || dialog.Result == null)
                return;
            var result = dialog.Result;
            var cues = result.Cues;
            if (cues.Count == 0)
            {
                Status("말소리를 찾지 못했습니다(" + dialog.Model.Label + " 모델). 말이 있는 클립을 골랐는지, 인식할 말이 맞는지 확인하세요.");
                return;
            }
            var seconds = (int)Math.Round(dialog.Elapsed.TotalSeconds);
            // 대본을 줬으면 몇 문장에 시각을 맞췄는지(대본에 없는 말은 인식한 글로 남는다)
            var script = result.Sentences == 0 ? "틀린 글은 큐를 골라 속성에서 고치세요."
                : result.Matched == 0 ? "대본과 맞는 말을 찾지 못해 인식한 글로 넣었습니다(대본이 이 소리의 것인지 확인하세요)."
                : "대본 문장 " + result.Matched + "개에 시각을 맞췄습니다" + (result.Unscripted > 0 ? "(대본에 없는 말 " + result.Unscripted + "곳은 인식한 글로)." : ".");
            var highlight = dialog.HighlightWords;
            Run(p =>
                {
                    var track = TimelineEdits.ImportSubtitles(p, cues);
                    if (track != null && highlight)
                        track.WordHighlight = true;
                },
                "음성 인식으로 자막 " + cues.Count + "개를 만들었습니다(" + dialog.Model.Label + " 모델, " + (seconds >= 60 ? seconds / 60 + "분 " : "") + seconds % 60 + "초). " + script);
        }

        /// <summary>[URL에서 가져오기]: 직접 링크·유튜브 주소 또는 유튜브 검색으로 받아 미디어 목록에 더한다.</summary>
        public void ImportFromUrl()
        {
            try
            {
                _player.Stop();
                var dialog = new UrlImportDialog(_services, NeedTool, paths => ImportPaths(paths, null, -1), Project.Height, _session.FilePath);
                var owner = UiKit.OwnerOf(_owner);
                if (owner != null && owner.IsLoaded)
                    dialog.Owner = owner;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                Status("URL에서 가져오기 창을 열지 못했습니다: " + ex.Message);
            }
        }

        private static string SafeFolder(string folder, Environment.SpecialFolder fallback)
        {
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : Environment.GetFolderPath(fallback);
        }

        /// <summary>
        /// 파일들을 읽어(ffprobe) 미디어에 더한다. frame ≥ 0이면 그 트랙·위치에 차례로 놓는다(track이 null이면 새 트랙).
        /// 빈 프로젝트에 처음 들어오는 동영상이면 프로젝트 해상도·프레임 레이트를 그것에 맞춘다.
        /// </summary>
        public async Task<int> ImportPaths(string[] paths, Track track, long frame)
        {
            if (paths == null || paths.Length == 0)
                return 0;
            // 자막 파일은 미디어가 아니라 자막 트랙으로
            var subtitleCount = 0;
            foreach (var subtitle in paths.Where(SubtitleFile.IsSubtitleFile))
                subtitleCount += ImportSubtitleFile(subtitle, track) > 0 ? 1 : 0;
            paths = paths.Where(p => !SubtitleFile.IsSubtitleFile(p)).ToArray();
            if (paths.Length == 0)
                return subtitleCount;
            var tool = await NeedTool();
            if (tool == null)
                return 0;
            Status(paths.Length + "개 파일을 읽는 중…");
            var infos = new List<MediaInfo>();
            var failed = new List<string>();
            foreach (var path in paths)
            {
                // 이미 있는 미디어는 그대로 — 파일을 바꿔 넣었으면(같은 이름의 다른 사진) 다시 읽는다
                var known = Project.FindMedia(path);
                if (known != null && !MediaProbe.IsStale(known))
                {
                    infos.Add(known);
                    continue;
                }
                try
                {
                    infos.Add(await MediaProbe.Probe(tool, path, CancellationToken.None));
                }
                catch (Exception)
                {
                    failed.Add(Path.GetFileName(path));
                }
            }
            if (infos.Count == 0)
            {
                Status("가져오지 못했습니다(동영상·사진·오디오가 아님): " + string.Join(", ", failed));
                return 0;
            }
            var trackId = track?.Id;
            string adopted = null;
            var placed = 0;
            if (!Run(p =>
            {
                foreach (var info in infos)
                {
                    var existing = p.FindMedia(info.Path);
                    if (existing == null)
                        p.Media.Add(info);
                    else if (!ReferenceEquals(existing, info))
                        existing.CopyFrom(info);
                }
                var firstVideo = infos.FirstOrDefault(i => i.Kind == MediaKind.Video);
                if (firstVideo != null && Core.ProjectSettings.AdoptFirstVideo(p, firstVideo))
                    adopted = p.Width + "×" + p.Height + " · " + p.Rate + " fps";
                if (frame < 0)
                    return;
                var position = frame;
                Track newTrack = null;
                foreach (var info in infos)
                {
                    var target = trackId != null ? p.FindTrack(trackId) : null;
                    var clip = PlaceMedia(p, info, target, position, ref newTrack);
                    position = clip.End;
                    placed++;
                }
            }))
                return 0;
            var message = infos.Count + "개를 가져왔습니다" + (placed > 0 ? "(타임라인에 놓음)" : "") + ".";
            if (adopted != null)
                message += " 프로젝트 설정을 첫 동영상에 맞췄습니다: " + adopted;
            if (failed.Count > 0)
                message += " 읽지 못한 파일: " + string.Join(", ", failed);
            Status(message);
            return infos.Count;
        }

        /// <summary>미디어 클립을 만든다(사진은 설정의 길이·맞춤 방식 — 기본은 화면 채우기).</summary>
        private Clip NewClip(MediaInfo media, FrameRate rate, long frame)
        {
            var clip = TimelineEdits.CreateClip(media, rate, frame, rate.ToFrames(_services.ImageSeconds));
            if (media.Kind == MediaKind.Image)
                clip.Fit = _services.PhotoFit;
            return clip;
        }

        /// <summary>미디어를 트랙에 놓는다. 종류가 맞지 않으면 알맞은 트랙으로(오디오 → 오디오 트랙, 동영상을 오디오 트랙에 놓으면 소리만).</summary>
        private Clip PlaceMedia(Project p, MediaInfo media, Track target, long frame, ref Track newTrack)
        {
            var clip = NewClip(media, p.Rate, frame);
            var isAudio = media.Kind == MediaKind.Audio;
            // 빈 곳이나 자막 트랙에 놓으면 새 트랙(동영상은 자막 아래) — 앞서 만든 새 트랙은 종류가 같을 때만 다시 쓴다
            if (target == null || target.Subtitles)
            {
                var kind = isAudio ? TrackKind.Audio : TrackKind.Video;
                target = newTrack != null && newTrack.Kind == kind ? newTrack : (newTrack = TimelineEdits.AddTrack(p, kind));
            }
            if (target.Kind == TrackKind.Audio && !isAudio)
            {
                if (!media.HasAudio)
                    throw new EditException(media.Name + "에는 소리가 없어 오디오 트랙에 놓을 수 없습니다.");
                TimelineEdits.MakeAudioOnly(clip);
            }
            else if (target.Kind == TrackKind.Video && isAudio)
            {
                target = p.AudioTracks.FirstOrDefault(t => !t.Locked) ?? TimelineEdits.AddTrack(p, TrackKind.Audio);
            }
            TimelineEdits.Place(p, target, clip, frame);
            return clip;
        }

        /// <summary>미디어를 타임라인 끝에 붙인다(동영상·사진 → 주 트랙, 오디오 → 오디오 트랙).</summary>
        public void AddToTimeline(MediaInfo media)
        {
            Run(p =>
            {
                var clip = NewClip(media, p.Rate, 0);
                var track = media.Kind == MediaKind.Audio
                    ? p.AudioTracks.FirstOrDefault(t => !t.Locked) ?? TimelineEdits.AddTrack(p, TrackKind.Audio)
                    : p.MainTrack;
                TimelineEdits.Append(p, track, clip);
            }, media.Name + "을(를) 타임라인 끝에 놓았습니다.");
        }

        public void RemoveMedia(string[] paths)
        {
            var used = paths.Where(path => Project.AllClips.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))).ToList();
            if (used.Count > 0 && UiKit.Ask(_owner, "미디어에서 빼기", "타임라인에서 쓰는 미디어입니다. 그 클립도 함께 지울까요?", "함께 지우기", "취소") != 0)
                return;
            Run(p =>
            {
                foreach (var path in paths)
                {
                    p.Media.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
                    TimelineEdits.Delete(p, p.AllClips.Where(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)).ToList(), false);
                }
            }, "미디어에서 뺐습니다.");
        }

        public void Reveal(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Status("탐색기를 열지 못했습니다: " + ex.Message);
            }
        }

        /// <summary>글자 클립을 재생 헤드에 덮어 쓰기 트랙으로 넣는다(빈 덮어 쓰기 트랙이 없으면 새로 만듦).</summary>
        public void AddText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Status("넣을 글자를 쓰세요.");
                return;
            }
            var playhead = _session.Playhead;
            string id = null;
            if (Run(p =>
                {
                    var clip = TimelineEdits.CreateText(text.Trim(), playhead, p.Rate.ToFrames(_services.ImageSeconds));
                    id = clip.Id;
                    var track = p.VideoTracks.Skip(1).FirstOrDefault(t => !t.Locked && !t.Subtitles && !t.Clips.Any(c => c.Start < clip.End && c.End > clip.Start))
                                ?? TimelineEdits.AddTrack(p, TrackKind.Video);
                    TimelineEdits.Place(p, track, clip, playhead);
                }, "글자를 넣었습니다."))
                _session.Select(new[] { Project.FindClip(id) });
        }

        // ---------- 마커·In/Out ----------

        public void AddMarker()
        {
            var playhead = _session.Playhead;
            if (Project.Markers.Any(m => m.Frame == playhead))
            {
                EditMarker();
                return;
            }
            Run(p => p.Markers.Add(new Marker { Frame = playhead, Name = "마커 " + (p.Markers.Count + 1) }), "마커를 더했습니다(M).");
        }

        /// <summary>재생 헤드에 가장 가까운 마커를 고친다(Shift+M).</summary>
        public void EditMarker()
        {
            var playhead = _session.Playhead;
            var marker = Project.Markers.OrderBy(m => Math.Abs(m.Frame - playhead)).FirstOrDefault();
            if (marker == null)
            {
                Status("마커가 없습니다(M으로 더하기).");
                return;
            }
            var copy = new Marker { Id = marker.Id, Frame = marker.Frame, Name = marker.Name, Color = marker.Color, Comment = marker.Comment };
            bool deleted;
            if (!Dialogs.EditMarker(_owner, copy, Project.Rate, out deleted))
                return;
            Run(p =>
            {
                var target = p.Markers.FirstOrDefault(m => m.Id == copy.Id);
                if (target == null)
                    return;
                if (deleted)
                {
                    p.Markers.Remove(target);
                    return;
                }
                target.Name = copy.Name;
                target.Color = copy.Color;
                target.Comment = copy.Comment;
            }, deleted ? "마커를 지웠습니다." : "마커를 고쳤습니다.");
        }

        public void JumpMarker(int direction)
        {
            var playhead = _session.Playhead;
            var frames = Project.Markers.Select(m => m.Frame).OrderBy(f => f).ToList();
            var target = direction > 0 ? frames.Where(f => f > playhead).DefaultIfEmpty(-1).First() : frames.Where(f => f < playhead).DefaultIfEmpty(-1).Last();
            if (target >= 0)
                GoTo(target);
        }

        public void MarkIn()
        {
            var playhead = _session.Playhead;
            Run(p =>
            {
                p.MarkIn = playhead;
                if (p.MarkOut.HasValue && p.MarkOut <= playhead)
                    p.MarkOut = null;
            }, "In 점: " + Project.Rate.Timecode(playhead));
        }

        public void MarkOut()
        {
            var playhead = _session.Playhead;
            Run(p =>
            {
                p.MarkOut = playhead;
                if (p.MarkIn.HasValue && p.MarkIn >= playhead)
                    p.MarkIn = null;
            }, "Out 점: " + Project.Rate.Timecode(playhead));
        }

        public void ClearInOut()
        {
            Run(p =>
            {
                p.MarkIn = null;
                p.MarkOut = null;
            }, "In/Out을 지웠습니다.");
        }

        // ---------- 재생·이동 ----------

        public void GoTo(long frame)
        {
            _player.Stop();
            _session.Playhead = Math.Max(0, frame);
            Timeline?.Canvas.EnsureVisible(_session.Playhead);
        }

        public void Step(long frames)
        {
            GoTo(_session.Playhead + frames);
        }

        public void StepSeconds(int seconds)
        {
            GoTo(_session.Playhead + Project.Rate.ToFrames(seconds));
        }

        public void JumpEditPoint(int direction)
        {
            var playhead = _session.Playhead;
            var points = TimelineEdits.EditPoints(Project);
            var target = direction > 0 ? points.Where(f => f > playhead).DefaultIfEmpty(Project.Duration).First() : points.Where(f => f < playhead).DefaultIfEmpty(0).Last();
            GoTo(target);
        }

        public void GoSelectedClip(bool end)
        {
            var clip = _session.SelectedClips.FirstOrDefault();
            if (clip != null)
                GoTo(end ? clip.End - 1 : clip.Start);
        }

        public void TogglePlay()
        {
            if (_player.IsPlaying)
            {
                _player.Stop();
                RefreshPreview();
                return;
            }
            if (_services.Tool == null)
            {
                FfmpegSetup();
                return;
            }
            if (Project.Duration == 0)
            {
                Status("재생할 클립이 없습니다.");
                return;
            }
            _player.Play(Project, _session.Playhead >= Project.Duration ? 0 : _session.Playhead);
        }

        public void StopPlayback()
        {
            _player.Stop();
            RefreshPreview();
        }

        public void RefreshPreview()
        {
            if (!_player.IsPlaying)
                _player.RequestFrame(Project, _session.Playhead);
        }

        /// <summary>스냅샷: 재생 헤드 화면을 원본 해상도 PNG로 저장해 미디어에 더한다.</summary>
        public async void Snapshot()
        {
            try
            {
                var tool = await NeedTool();
                if (tool == null || Project.Duration == 0)
                    return;
                var frame = _session.Playhead;
                var copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(Project));
                var bytes = await Task.Run(() => PreviewRenderer.Frame(tool, copy, frame, copy.Width, copy.Height, CancellationToken.None, _services.TextRasterizer));
                if (bytes == null)
                {
                    Status("스냅샷을 만들지 못했습니다.");
                    return;
                }
                var folder = _session.FilePath != null ? Path.GetDirectoryName(_session.FilePath) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Folderss 스냅샷");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "스냅샷 " + copy.Rate.Timecode(frame).Replace(':', '-') + " " + DateTime.Now.ToString("HHmmss") + ".png");
                var bitmap = BitmapSource.Create(copy.Width, copy.Height, 96, 96, PixelFormats.Bgra32, null, bytes, copy.Width * 4);
                using (var stream = File.Create(path))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(stream);
                }
                await ImportPaths(new[] { path }, null, -1);
                Status("스냅샷을 저장하고 미디어에 더했습니다: " + path);
            }
            catch (Exception ex)
            {
                Status("스냅샷을 만들지 못했습니다: " + ex.Message);
            }
        }

        // ---------- 프로젝트 파일 ----------

        /// <summary>저장하지 않은 변경이 있으면 묻는다. 계속해도 되면 true.</summary>
        public bool ConfirmDiscard()
        {
            if (!_session.Dirty || !Project.AllClips.Any() && Project.Media.Count == 0)
                return true;
            var answer = UiKit.Ask(_owner, "동영상 편집", "저장하지 않은 변경이 있습니다. 저장할까요?", "저장", "저장 안 함", "취소");
            if (answer == 0)
                return Save();
            return answer == 1;
        }

        public void New()
        {
            if (!ConfirmDiscard())
                return;
            _player.Stop();
            _session.New();
            Status("새 프로젝트");
        }

        public void Open()
        {
            if (!ConfirmDiscard())
                return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "프로젝트 열기", Filter = ProjectFile.DialogFilter };
            if (dialog.ShowDialog(UiKit.OwnerOf(_owner)) != true)
                return;
            OpenPath(dialog.FileName);
        }

        /// <summary>복구 파일을 연다. 내용은 이제 이 창이 갖고 자동 저장도 이 창이 하므로 원래 복구 파일은 지운다.</summary>
        public void OpenRecovered(RecoveryStore.Entry entry)
        {
            try
            {
                _player.Stop();
                _session.OpenRecovered(entry.ProjectPath, entry.OriginalPath);
                entry.Delete();
                Status("복구했습니다 — 저장하세요(Ctrl+S)." + (string.IsNullOrEmpty(entry.OriginalPath) ? "" : " 원래 파일: " + entry.OriginalPath));
            }
            catch (Exception ex)
            {
                UiKit.Inform(_owner, "프로젝트 복구", "복구하지 못했습니다: " + ex.Message);
            }
        }

        public void OpenPath(string path)
        {
            try
            {
                _player.Stop();
                _session.Open(path);
                var missing = Project.Media.Count(m => !File.Exists(m.Path));
                Status("열었습니다: " + path + (missing > 0 ? " — 찾을 수 없는 미디어 " + missing + "개" : ""));
                _ = RefreshMediaAsync();
            }
            catch (Exception ex)
            {
                UiKit.Inform(_owner, "프로젝트 열기", "열지 못했습니다: " + ex.Message);
            }
        }

        /// <summary>
        /// 연 프로젝트의 미디어 가운데 읽은 뒤 바뀐 파일(같은 이름으로 바꿔 넣은 사진 등)을 다시 읽어 크기·길이를 고친다 —
        /// 미디어 패널·미리 보기 틀이 낡은 크기를 보이지 않게. 실행 취소에는 남기지 않는다.
        /// </summary>
        private async Task RefreshMediaAsync()
        {
            var tool = _services.Tool ?? await _services.LocateAsync();
            var project = Project;
            if (tool == null)
                return;
            var fresh = new List<MediaInfo>();
            foreach (var media in project.Media.ToList())
            {
                if (!MediaProbe.IsStale(media))
                    continue;
                try
                {
                    fresh.Add(await MediaProbe.Probe(tool, media.Path, CancellationToken.None));
                }
                catch (Exception)
                {
                    // 읽지 못한 파일은 그대로 둔다(미디어가 아닌 것으로 바뀜 등)
                }
            }
            // 그사이 다른 프로젝트를 열었으면 버린다
            if (fresh.Count == 0 || !ReferenceEquals(project, Project))
                return;
            var resized = fresh.Where(f => project.FindMedia(f.Path) is MediaInfo old && (old.Width != f.Width || old.Height != f.Height || Math.Abs(old.DurationSeconds - f.DurationSeconds) > 0.05))
                .Select(f => Path.GetFileName(f.Path)).ToList();
            _session.Refresh(p =>
            {
                foreach (var info in fresh)
                    p.FindMedia(info.Path)?.CopyFrom(info);
            });
            if (resized.Count > 0)
                Status("바뀐 미디어 " + resized.Count + "개의 크기·길이를 다시 읽었습니다: " + string.Join(", ", resized.Take(5)) + (resized.Count > 5 ? " 외" : ""));
        }

        public bool Save()
        {
            return _session.FilePath == null ? SaveAs() : SaveTo(_session.FilePath);
        }

        public bool SaveAs()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "프로젝트 저장",
                Filter = ProjectFile.DialogFilter,
                DefaultExt = ProjectFile.Extension,
                FileName = _session.FilePath != null ? Path.GetFileName(_session.FilePath) : "새 프로젝트" + ProjectFile.Extension,
                InitialDirectory = _session.FilePath != null ? Path.GetDirectoryName(_session.FilePath) : SafeFolder(_services.Setting(EditorServices.LastFolderKey), Environment.SpecialFolder.MyVideos)
            };
            return dialog.ShowDialog(UiKit.OwnerOf(_owner)) == true && SaveTo(dialog.FileName);
        }

        public bool SaveTo(string path)
        {
            try
            {
                _session.Save(path);
                Status("저장했습니다: " + path);
                return true;
            }
            catch (Exception ex)
            {
                UiKit.Inform(_owner, "프로젝트 저장", "저장하지 못했습니다: " + ex.Message);
                return false;
            }
        }

        /// <summary>[프로젝트 설정…]: 맨 왼쪽 프로젝트 트리의 화면 폴더를 연다(EditorView가 받아 트리를 보이고 고른다).</summary>
        public event Action ProjectSettingsRequested;

        public void ProjectSettings()
        {
            ProjectSettingsRequested?.Invoke();
        }

        /// <summary>해상도·프레임 레이트를 바꾼다(프로젝트 트리 › 화면). 바꿨으면 true.</summary>
        public bool ApplyScreen(int width, int height, FrameRate rate)
        {
            return Run(p =>
            {
                Core.ProjectSettings.ChangeSize(p, width, height);
                Core.ProjectSettings.ChangeRate(p, rate);
                p.SettingsChosen = true;
            }, "프로젝트 설정: " + (width - width % 2) + "×" + (height - height % 2) + " · " + rate + " fps");
        }

        /// <summary>소리 샘플 레이트를 바꾼다(프로젝트 트리 › 소리).</summary>
        public void SetSampleRate(int sampleRate)
        {
            Run(p => p.SampleRate = sampleRate, "샘플 레이트: " + sampleRate + " Hz");
        }

        public async void Export()
        {
            try
            {
                var tool = await NeedTool();
                if (tool == null)
                    return;
                if (Project.Duration == 0)
                {
                    Status("내보낼 클립이 없습니다. 미디어를 타임라인에 놓으세요.");
                    return;
                }
                _player.Stop();
                var name = _session.FilePath != null ? Path.GetFileNameWithoutExtension(_session.FilePath) : "내보낸 동영상";
                var folder = _session.FilePath != null ? Path.GetDirectoryName(_session.FilePath) : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                var dialog = new ExportDialog(_services, Project, Path.Combine(folder, name + ".mp4"));
                var owner = UiKit.OwnerOf(_owner);
                if (owner != null && owner.IsLoaded)
                    dialog.Owner = owner;
                dialog.ShowDialog();
                if (dialog.ExportedPath != null)
                    Status("내보냈습니다: " + dialog.ExportedPath);
            }
            catch (Exception ex)
            {
                Status("내보내기 창을 열지 못했습니다: " + ex.Message);
            }
        }
    }
}
