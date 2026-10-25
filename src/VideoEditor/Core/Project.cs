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
        /// <summary>읽을 때의 파일 크기·고친 시각(UTC 틱) — 같은 이름으로 파일을 바꿔 넣었는지 알아보려고(0 = 모름, 예전 프로젝트).</summary>
        public long FileSize { get; set; }
        public long FileTime { get; set; }
        /// <summary>
        /// 동영상 화면이 파일 시작보다 늦게 시작하는 초(스트림 복사로 자른 파일·방송 파일 등, 보통 0 — 0이면 저장하지 않음).
        /// 그래프가 원본을 -ss로 읽으면 그 앞의 빈 구간은 건너뛰므로, 첫 화면부터 만든 인물 마스크는 이만큼 당겨 읽어야 겹친다.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double VideoStart { get; set; }

        [JsonIgnore]
        public string Name { get { return System.IO.Path.GetFileName(Path); } }

        /// <summary>다시 읽은 정보로 바꾼다(경로는 그대로).</summary>
        public void CopyFrom(MediaInfo other)
        {
            Kind = other.Kind;
            DurationSeconds = other.DurationSeconds;
            Width = other.Width;
            Height = other.Height;
            FrameRate = other.FrameRate;
            HasVideo = other.HasVideo;
            HasAudio = other.HasAudio;
            VideoCodec = other.VideoCodec;
            AudioCodec = other.AudioCodec;
            AudioChannels = other.AudioChannels;
            FileSize = other.FileSize;
            FileTime = other.FileTime;
            VideoStart = other.VideoStart;
        }

        /// <summary>화면(프로젝트) 비율과 3% 넘게 다른 그림인지 — 맞춤이면 빈 곳, 채우기면 잘리는 곳이 생긴다.</summary>
        public bool AspectDiffers(int width, int height)
        {
            if (Kind == MediaKind.Audio || Width <= 0 || Height <= 0 || width <= 0 || height <= 0)
                return false;
            var ratio = ((double)Width / Height) / ((double)width / height);
            return ratio > 1.03 || ratio < 1 / 1.03;
        }
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
        /// <summary>사운드 분리로 나눈 영상·사운드 짝(두 클립이 같은 값 — <see cref="ClipLinks"/>). null = 짝 없음(예전 프로젝트).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LinkId { get; set; }
        /// <summary>링크 해제: 짝(LinkId)은 남기고 따로 움직인다 — 어긋남 표시·싱크 맞추기에 쓴다.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Unlinked { get; set; }
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
        /// <summary>크기 배율(1 = 화면 크기의 틀).</summary>
        public double Scale { get; set; } = 1;
        /// <summary>틀에 놓는 방식(맞춤·채우기·늘이기). 예전 프로젝트는 맞춤.</summary>
        [JsonConverter(typeof(LenientEnumConverter<ClipFit>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ClipFit Fit { get; set; }
        /// <summary>시계 방향 회전(도).</summary>
        public double Rotation { get; set; }
        public double Opacity { get; set; } = 1;
        /// <summary>
        /// 동영상 효과(<see cref="VideoEffects"/>, 동영상·사진 클립만) — 틀 크기 그림 위에 차례로 그린다(뒤 효과가 위). 없으면 null(저장할 때 빠져 예전 형식 그대로).
        /// </summary>
        [JsonConverter(typeof(LenientListConverter<ClipEffect>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ClipEffect> Effects { get; set; }

        // 글자
        public string Text { get; set; }
        public double FontSize { get; set; } = 72;
        public string TextColor { get; set; } = "#FFFFFF";
        /// <summary>꾸밈 스타일. null이면 꾸미지 않은 글자(예전 방식).</summary>
        public TextStyle Style { get; set; }
        /// <summary>
        /// 글자 상자 너비(프로젝트 픽셀). 0 = 자동(줄을 나누지 않고 가장 긴 줄 폭). 정하면 줄마다 이 폭에서 낱말 단위로 나누고 정렬은 상자 안에서.
        /// 위치·크기처럼 클립의 값이라 스타일을 바꿔도(스타일 모음·내 스타일) 그대로 남는다.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double TextBoxWidth { get; set; }
        /// <summary>글자 상자 높이(프로젝트 픽셀). 0 = 자동(글 높이). 글이 더 길면 상자 밖으로 넘쳐 그린다(글자 크기는 그대로).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double TextBoxHeight { get; set; }
        /// <summary>글보다 높은 상자에서 글을 놓는 자리(위·가운데·아래). 예전 프로젝트는 가운데.</summary>
        [JsonConverter(typeof(LenientEnumConverter<TextVerticalAlign>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public TextVerticalAlign TextVerticalAlign { get; set; }

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
            // 짝은 사본에 넘기지 않는다(나눈 조각·붙여 넣은 사본은 짝을 따로 정한다)
            copy.LinkId = null;
            copy.Unlinked = false;
            copy.Style = Style?.Clone();
            copy.Effects = Effects?.Select(e => e?.Clone()).ToList();
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
    /// <summary>화면 클립을 화면(또는 크기 배율만큼의 틀)에 놓는 방식.</summary>
    public enum ClipFit
    {
        /// <summary>화면에 맞춤: 비율을 지켜 다 보이게(남는 곳은 비어 아래 트랙·검정이 보임).</summary>
        Fit,
        /// <summary>화면 채우기: 비율을 지켜 꽉 채우고 넘치는 곳은 가운데 기준으로 자름.</summary>
        Fill,
        /// <summary>늘이기: 비율을 무시하고 화면 크기로.</summary>
        Stretch,
        /// <summary>흐린 배경으로 채우기: 비율을 지켜 다 보이게 하고, 남는 곳은 같은 그림을 채우기로 키워 흐리게 깔아 채움(세로 쇼츠에 가로 사진 등).</summary>
        Blur
    }

    /// <summary>글자 상자 안 세로 자리. 기본(0)이 가운데라 예전 프로젝트·저장하지 않은 값은 가운데.</summary>
    public enum TextVerticalAlign
    {
        /// <summary>가운데(기본).</summary>
        Middle,
        /// <summary>위.</summary>
        Top,
        /// <summary>아래.</summary>
        Bottom
    }

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
