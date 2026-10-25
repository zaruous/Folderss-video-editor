using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    public enum MediaKind
    {
        Video,
        Audio,
        Image
    }

    /// <summary>가져온 파일 하나(ffprobe로 읽은 정보). 미디어 패널의 항목이자 클립의 원본.</summary>
    public sealed class MediaInfo
    {
        public string Path { get; set; }
        public MediaKind Kind { get; set; }
        /// <summary>원본 길이(초). 이미지는 0(길이 제한 없음).</summary>
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>동영상의 프레임 레이트 글("30000/1001"). 없으면 null.</summary>
        public string FrameRate { get; set; }
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        /// <summary>오디오 채널 수(0 = 모름).</summary>
        public int AudioChannels { get; set; }

        [JsonIgnore]
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
    }

    public enum TrackKind
    {
        Video,
        Audio
    }

    /// <summary>트랙. 동영상 트랙은 위(번호가 큰) 트랙이 아래를 덮는다. 클립은 시작 프레임 순서로 겹치지 않게 둔다.</summary>
    public sealed class Track
    {
        public string Id { get; set; } = NewId();
        public TrackKind Kind { get; set; }
        public string Name { get; set; }
        /// <summary>출력 끄기(동영상: 안 보임, 오디오 트랙에서는 쓰지 않음).</summary>
        public bool Hidden { get; set; }
        /// <summary>소리 끄기(이 트랙 클립의 소리를 빼고 내보냄).</summary>
        public bool Muted { get; set; }
        /// <summary>잠금(이 트랙의 클립을 옮기거나 고칠 수 없음).</summary>
        public bool Locked { get; set; }
        /// <summary>자막 트랙(동영상 트랙만): 글자 클립을 큐로 보고 트랙 공통 모양으로 화면 아래에 입힌다(libass). 글자 클립만 놓인다.</summary>
        public bool Subtitles { get; set; }
        /// <summary>자막 트랙: 말하는 단어를 노란색으로 강조(단어 시각이 있는 큐 — 음성 인식으로 만든 것).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool WordHighlight { get; set; }
        public double Height { get; set; } = 56;
        public List<Clip> Clips { get; set; } = new List<Clip>();

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public void Sort()
        {
            Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
    }

    public enum ClipKind
    {
        /// <summary>파일(동영상·오디오·이미지).</summary>
        Media,
        /// <summary>글자(타이틀).</summary>
        Text
    }

    /// <summary>
    /// 타임라인의 클립. 위치·길이는 프로젝트 프레임. 원본 구간은 [SourceIn, SourceOut)(프로젝트 프레임 단위의 원본 시간)이고
    /// 타임라인 길이 = 원본 구간 / 속도. 이미지·글자는 원본 길이 제한이 없다(SourceLength = 0).
    /// </summary>
    public sealed class Clip
    {
        public string Id { get; set; } = Track.NewId();
        public ClipKind Kind { get; set; }
        public string Path { get; set; }
        public MediaKind MediaKind { get; set; }
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }
        /// <summary>원본 오디오 채널 수(1이면 모노 — 양쪽에 같은 크기로 낸다). 0 = 모름.</summary>
        public int AudioChannels { get; set; }
        /// <summary>원본 전체 길이(프레임). 0이면 제한 없음(이미지·글자).</summary>
        public long SourceLength { get; set; }

        public long Start { get; set; }
        public long SourceIn { get; set; }
        public long SourceOut { get; set; }
        public double Speed { get; set; } = 1;

        // 소리
        /// <summary>음량 배율(1 = 원래, 0 = 무음, 최대 2).</summary>
        public double Volume { get; set; } = 1;
        /// <summary>오디오를 오디오 트랙으로 분리함(이 클립에서는 소리를 내지 않음).</summary>
        public bool AudioDetached { get; set; }
        /// <summary>오디오 트랙에 놓인 클립인데 원본이 동영상 파일(분리한 오디오).</summary>
        public bool AudioOnly { get; set; }
        public long FadeIn { get; set; }
        public long FadeOut { get; set; }

        // 화면
        /// <summary>앞 클립에서 이 클립으로 겹쳐 바뀌는 길이(프레임). 앞 클립이 그만큼 밑에 더 그려지고, 타임라인 위치는 그대로다.</summary>
        public long TransitionIn { get; set; }
        /// <summary>전환 효과 id(<see cref="Transitions"/>, 기본 디졸브 = 예전 크로스페이드).</summary>
        [JsonConverter(typeof(LenientConverter<string>))]
        public string Transition { get; set; } = Transitions.DefaultId;
        /// <summary>사용자 효과(효과 파일)의 정의 — 그 파일이 없는 PC에서 열어도 같게 그리도록 함께 저장한다. 기본 효과는 null.</summary>
        [JsonConverter(typeof(LenientConverter<TransitionDef>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TransitionDef TransitionDef { get; set; }
        /// <summary>화면 가운데에서 떨어진 정도(프로젝트 픽셀).</summary>
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>크기 배율(1 = 화면에 맞춤).</summary>
        public double Scale { get; set; } = 1;
        /// <summary>시계 방향 회전(도).</summary>
        public double Rotation { get; set; }
        public double Opacity { get; set; } = 1;

        // 글자
        public string Text { get; set; }
        public double FontSize { get; set; } = 72;
        public string TextColor { get; set; } = "#FFFFFF";
        /// <summary>꾸밈 스타일. null이면 꾸미지 않은 글자(예전 방식).</summary>
        public TextStyle Style { get; set; }

        [JsonIgnore]
        public long Duration
        {
            get
            {
                var frames = (long)Math.Round((SourceOut - SourceIn) / Speed, MidpointRounding.AwayFromZero);
                return Math.Max(1, frames);
            }
        }

        [JsonIgnore]
        public long End { get { return Start + Duration; } }

        [JsonIgnore]
        public string Name
        {
            get { return Kind == ClipKind.Text ? (string.IsNullOrEmpty(Text) ? "글자" : Text.Split('\n')[0]) : System.IO.Path.GetFileName(Path); }
        }

        /// <summary>화면에 보이는 클립(동영상·이미지·글자 — 오디오 트랙의 클립은 아님).</summary>
        [JsonIgnore]
        public bool IsVisual { get { return !AudioOnly && (Kind == ClipKind.Text || HasVideo); } }

        /// <summary>소리를 내는 클립.</summary>
        [JsonIgnore]
        public bool IsAudible { get { return Kind == ClipKind.Media && HasAudio && !AudioDetached && Volume > 0; } }

        /// <summary>원본 길이 제한이 있는지(동영상·오디오).</summary>
        [JsonIgnore]
        public bool HasSourceLimit { get { return SourceLength > 0; } }

        public bool Contains(long frame)
        {
            return frame >= Start && frame < End;
        }

        /// <summary>글자 클립(자막 큐)의 단어 시각 — 음성 인식으로 만든 큐에만. 클립 시작부터의 프레임.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ClipWord> Words { get; set; }

        public Clip CloneWithNewId()
        {
            var copy = (Clip)MemberwiseClone();
            copy.Id = Track.NewId();
            copy.Style = Style?.Clone();
            copy.Words = Words?.Select(w => new ClipWord { Text = w.Text, Start = w.Start, End = w.End }).ToList();
            return copy;
        }

        /// <summary>단어 시각을 frames만큼 앞으로 당긴다(클립 시작을 뒤로 옮겼을 때 — 단어가 소리의 같은 자리에 남게).</summary>
        internal void ShiftWords(long frames)
        {
            if (Words == null)
                return;
            foreach (var word in Words)
            {
                word.Start -= frames;
                word.End -= frames;
            }
        }
    }

    /// <summary>자막 큐의 단어 하나와 그 시각(클립 시작부터의 프레임, [Start, End)).</summary>
    public sealed class ClipWord
    {
        public string Text { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
    }

    public sealed class Marker
    {
        public string Id { get; set; } = Track.NewId();
        public long Frame { get; set; }
        public string Name { get; set; } = "";
        public string Color { get; set; } = "#3B82F6";
        public string Comment { get; set; } = "";
    }

    /// <summary>편집 프로젝트(*.fvproj로 저장). 동영상 트랙 0번이 주 트랙(맨 아래)이다.</summary>
    public sealed class Project
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int RateNumerator { get; set; } = 30;
        public int RateDenominator { get; set; } = 1;
        public int SampleRate { get; set; } = 48000;
        /// <summary>주 트랙 마그네틱(빈틈 없이 붙음).</summary>
        public bool Magnetic { get; set; } = true;
        /// <summary>프로젝트 설정(해상도·프레임 레이트)을 첫 동영상에 맞춰 정했는지.</summary>
        public bool SettingsChosen { get; set; }

        public List<MediaInfo> Media { get; set; } = new List<MediaInfo>();
        public List<Track> VideoTracks { get; set; } = new List<Track>();
        public List<Track> AudioTracks { get; set; } = new List<Track>();
        public List<Marker> Markers { get; set; } = new List<Marker>();
        public long? MarkIn { get; set; }
        public long? MarkOut { get; set; }

        [JsonIgnore]
        public FrameRate Rate
        {
            get { return new FrameRate(RateNumerator, RateDenominator); }
            set
            {
                RateNumerator = value.Numerator;
                RateDenominator = value.Denominator;
            }
        }

        [JsonIgnore]
        public Track MainTrack { get { return VideoTracks[0]; } }

        [JsonIgnore]
        public IEnumerable<Track> AllTracks { get { return VideoTracks.Concat(AudioTracks); } }

        [JsonIgnore]
        public IEnumerable<Clip> AllClips { get { return AllTracks.SelectMany(t => t.Clips); } }

        /// <summary>타임라인 길이(가장 늦게 끝나는 클립).</summary>
        [JsonIgnore]
        public long Duration { get { return AllClips.Select(c => c.End).DefaultIfEmpty(0).Max(); } }

        /// <summary>새 프로젝트: 동영상 트랙 1개, 오디오 트랙 1개.</summary>
        public static Project CreateDefault()
        {
            var project = new Project();
            project.VideoTracks.Add(new Track { Kind = TrackKind.Video, Name = "동영상 1" });
            project.AudioTracks.Add(new Track { Kind = TrackKind.Audio, Name = "오디오 1" });
            return project;
        }

        public Track TrackOf(Clip clip)
        {
            return AllTracks.FirstOrDefault(t => t.Clips.Contains(clip));
        }

        public Clip FindClip(string id)
        {
            return AllClips.FirstOrDefault(c => c.Id == id);
        }

        public Track FindTrack(string id)
        {
            return AllTracks.FirstOrDefault(t => t.Id == id);
        }

        public bool IsMain(Track track)
        {
            return VideoTracks.Count > 0 && ReferenceEquals(VideoTracks[0], track);
        }

        public MediaInfo FindMedia(string path)
        {
            return Media.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>트랙 이름을 순번대로 다시 붙인다(동영상 1, 2…/오디오 1, 2…).</summary>
        public void RenameTracks()
        {
            // 자막 트랙은 "자막"(여러 개면 "자막 2"…)이고 동영상 번호에서 뺀다
            var subtitleTracks = VideoTracks.Count(t => t.Subtitles);
            int video = 0, subtitle = 0;
            foreach (var track in VideoTracks)
            {
                if (track.Subtitles)
                    track.Name = subtitleTracks == 1 ? "자막" : "자막 " + ++subtitle;
                else
                    track.Name = "동영상 " + ++video;
            }
            for (var i = 0; i < AudioTracks.Count; i++)
                AudioTracks[i].Name = "오디오 " + (i + 1);
        }

        /// <summary>파일 경로를 프로젝트 파일 기준 상대 경로로 바꿔 저장할 수 있게 한다(같은 드라이브일 때).</summary>
        public static string Relative(string projectFolder, string path)
        {
            if (string.IsNullOrEmpty(projectFolder) || string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path))
                return path;
            var relative = System.IO.Path.GetRelativePath(projectFolder, path);
            return System.IO.Path.IsPathRooted(relative) ? path : relative;
        }

        public static string Absolute(string projectFolder, string path)
        {
            if (string.IsNullOrEmpty(projectFolder) || string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path))
                return path;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectFolder, path));
        }
    }
}
