using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Folderss.Plugins;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 편집 창들이 함께 쓰는 것: Folderss 관리자, 찾은 FFmpeg, 썸네일·파형 캐시, 설정 값.
    /// FFmpeg는 백그라운드에서 찾고(ToolChanged), 창마다 다시 찾지 않는다.
    /// </summary>
    internal sealed class EditorServices
    {
        public const string FfmpegPathKey = "ffmpegPath";
        public const string ImageSecondsKey = "imageSeconds";
        public const string PhotoFitKey = "photoFit";
        public const string PlayerMutedKey = "playerMuted";
        public const string PreviewScaleKey = "previewScale";
        public const string LastFolderKey = "lastFolder";
        public const string YtDlpPathKey = "ytDlpPath";
        public const string YtDlpNoticeKey = "ytDlpNoticeAccepted";
        public const string RecentSearchesKey = "youtubeRecentSearches";
        public const string DownloadFolderKey = "downloadFolder";
        public const string ProjectTreeHiddenKey = "projectTreeHidden";

        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _thumbnails = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<float[]>> _waveforms = new ConcurrentDictionary<string, Task<float[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _transitionPreviews = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.Ordinal);
        private Task<FfmpegTool> _locating;
        private Task<YtDlpTool> _locatingYtDlp;
        // 썸네일·파형 FFmpeg를 한꺼번에 수십 개 띄우지 않게
        private static readonly SemaphoreSlim PictureGate = new SemaphoreSlim(3);

        private readonly Lazy<StyledTextRasterizer> _textRasterizer = new Lazy<StyledTextRasterizer>(() => new StyledTextRasterizer());

        public EditorServices(IPluginManager manager)
        {
            Manager = manager;
            LoadTransitions();
            // 앞서 끝난 실행이 남긴 꾸민 글자 PNG 캐시를 치운다(살아 있는 실행의 폴더는 건드리지 않음).
            // 시스템 글꼴 목록도 미리 읽어 둔다 — 처음 글자 클립을 고를 때 UI 스레드가 멈추지 않게
            Task.Run(() =>
            {
                TextImageCache.Cleanup(RenderEnvironment.TempRoot());
                _ = StyledTextRasterizer.Fonts;
            });
        }

        public IPluginManager Manager { get; }

        /// <summary>꾸민 글자 그리개 — 편집 창이 여러 개여도 하나(그리기 스레드 하나)를 같이 쓴다.</summary>
        public ITextRasterizer TextRasterizer { get { return _textRasterizer.Value; } }

        /// <summary>찾은 FFmpeg(없으면 null).</summary>
        public FfmpegTool Tool { get; private set; }

        public event EventHandler ToolChanged;

        /// <summary>플러그인이 내려받는 곳(데이터 폴더\ffmpeg).</summary>
        public string DownloadFolder { get { return Path.Combine(Manager.DataDirectory, "ffmpeg"); } }

        /// <summary>찾은 yt-dlp(없으면 null).</summary>
        public YtDlpTool YtDlp { get; private set; }

        /// <summary>플러그인이 yt-dlp·Deno를 내려받는 곳(데이터 폴더\yt-dlp, 데이터 폴더\deno).</summary>
        public string YtDlpFolder { get { return Path.Combine(Manager.DataDirectory, "yt-dlp"); } }
        public string DenoFolder { get { return Path.Combine(Manager.DataDirectory, "deno"); } }

        /// <summary>음성 인식(whisper.cpp)과 모델을 두는 곳(데이터 폴더\whisper, 모델은 그 아래 models).</summary>
        public string WhisperFolder { get { return Path.Combine(Manager.DataDirectory, "whisper"); } }

        private WhisperTool _whisper;

        /// <summary>받아 둔 whisper.cpp(없으면 null).</summary>
        public WhisperTool Whisper
        {
            get { return _whisper != null && File.Exists(_whisper.CliPath) ? _whisper : (_whisper = WhisperTool.Locate(WhisperFolder)); }
            set { _whisper = value; }
        }

        /// <summary>유튜브 약관·권리 안내를 확인했는지(한 번 확인하면 설정에 기억).</summary>
        public bool YtDlpNoticeAccepted
        {
            get { return Setting(YtDlpNoticeKey) == "true"; }
            set { SaveSetting(YtDlpNoticeKey, value ? "true" : null); }
        }

        /// <summary>URL에서 받은 파일을 둘 곳: 지정한 다운로드 폴더 → 프로젝트 파일 옆 media 폴더 → 저장 안 한 프로젝트면 데이터 폴더\media.</summary>
        public string MediaDownloadFolder(string projectPath)
        {
            return YtDlpText.DownloadFolder(Setting(DownloadFolderKey), projectPath, Manager.DataDirectory);
        }

        /// <summary>다운로드 폴더를 지정하지 않았을 때 쓰는 곳(프로젝트 옆 media 또는 데이터 폴더\media).</summary>
        public string DefaultMediaDownloadFolder(string projectPath)
        {
            return YtDlpText.DownloadFolder(null, projectPath, Manager.DataDirectory);
        }

        /// <summary>다운로드 폴더를 직접 지정했는지.</summary>
        public bool HasDownloadFolder
        {
            get { return !string.IsNullOrWhiteSpace(Setting(DownloadFolderKey)); }
        }

        /// <summary>yt-dlp를 찾는다(이미 찾았으면 그것). 백그라운드에서 돌고, 같은 때 여러 번 불러도 한 번만 찾는다.</summary>
        public Task<YtDlpTool> LocateYtDlpAsync()
        {
            var current = YtDlp;
            if (current != null && File.Exists(current.Path))
                return Task.FromResult(current);
            lock (_waveforms)
            {
                if (_locatingYtDlp == null || _locatingYtDlp.IsCompleted)
                {
                    var configured = Setting(YtDlpPathKey);
                    var downloaded = YtDlpFolder;
                    var deno = DenoFolder;
                    _locatingYtDlp = Task.Run(() =>
                    {
                        var tool = YtDlpTool.Locate(configured, downloaded, deno);
                        YtDlp = tool;
                        return tool;
                    });
                }
                return _locatingYtDlp;
            }
        }

        /// <summary>쓸 yt-dlp를 정한다. remember면 설정에 경로를 저장한다(직접 지정).</summary>
        public void SetYtDlp(YtDlpTool tool, bool remember)
        {
            YtDlp = tool;
            if (tool != null && remember)
                SaveSetting(YtDlpPathKey, tool.Path);
        }

        /// <summary>yt-dlp를 다시 찾는다(설치·업데이트·Deno를 받은 뒤).</summary>
        public Task<YtDlpTool> RelocateYtDlpAsync()
        {
            YtDlp = null;
            return LocateYtDlpAsync();
        }

        public string Setting(string key)
        {
            try
            {
                return Manager.GetSetting(key);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>설정 저장. 실패해도(디스크 등) 편집은 계속한다 — 상태 줄에 알린다.</summary>
        public bool SaveSetting(string key, string value)
        {
            try
            {
                Manager.SetSetting(key, value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>사진을 타임라인에 놓을 때 맞춤 방식(기본: 화면 채우기 — 사진이 화면을 꽉 채움).</summary>
        public ClipFit PhotoFit
        {
            get { return Enum.TryParse<ClipFit>(Setting(PhotoFitKey), out var fit) && Enum.IsDefined(fit) ? fit : ClipFit.Fill; }
        }

        /// <summary>맞춤 방식의 보일 이름.</summary>
        public static string FitName(ClipFit fit)
        {
            return fit == ClipFit.Fill ? "화면 채우기" : fit == ClipFit.Stretch ? "늘이기" : fit == ClipFit.Blur ? "흐린 배경으로 채우기" : "화면에 맞춤";
        }

        /// <summary>맞춤 방식 설명(메뉴·툴팁).</summary>
        public static string FitHint(ClipFit fit)
        {
            return fit == ClipFit.Fill ? "비율을 지켜 꽉 채우고 넘치는 곳은 잘라 냄"
                : fit == ClipFit.Stretch ? "비율을 무시하고 화면 크기로"
                : fit == ClipFit.Blur ? "다 보이게 하고 남는 곳은 같은 그림을 흐리게 깔아 채움"
                : "비율을 지켜 다 보이게(남는 곳은 빔)";
        }

        /// <summary>이미지·사진 클립 기본 길이(초, 기본 5).</summary>
        public double ImageSeconds
        {
            get
            {
                double seconds;
                return double.TryParse(Setting(ImageSecondsKey), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0 && seconds <= 3600 ? seconds : 5;
            }
        }

        /// <summary>
        /// FFmpeg를 찾는다(이미 찾았고 설정 경로가 그대로면 그것). 설정한 경로가 바뀌었으면 다시 찾는다.
        /// 백그라운드에서 돌고, 같은 때 여러 번 불러도 한 번만 찾는다.
        /// </summary>
        public Task<FfmpegTool> LocateAsync()
        {
            var configured = Setting(FfmpegPathKey);
            var current = Tool;
            if (current != null && File.Exists(current.FfmpegPath)
                && (string.IsNullOrWhiteSpace(configured) || SameFolder(configured, current.Folder)))
                return Task.FromResult(current);
            lock (_thumbnails)
            {
                if (_locating == null || _locating.IsCompleted)
                {
                    var download = Path.Combine(DownloadFolder, "bin");
                    _locating = Task.Run(() => FfmpegTool.Locate(configured, download)).ContinueWith(t =>
                    {
                        var tool = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                        SetTool(tool, false);
                        return tool;
                    }, TaskScheduler.Default);
                }
                return _locating;
            }
        }

        private static bool SameFolder(string configured, string folder)
        {
            try
            {
                var path = File.Exists(configured) ? Path.GetDirectoryName(Path.GetFullPath(configured)) : Path.GetFullPath(configured);
                return string.Equals(path.TrimEnd('\\', '/'), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>쓸 FFmpeg를 정한다. remember면 설정에 폴더를 저장한다(직접 지정·내려받기).</summary>
        public void SetTool(FfmpegTool tool, bool remember)
        {
            Tool = tool;
            if (tool != null && remember)
                SaveSetting(FfmpegPathKey, tool.Folder);
            var handlers = ToolChanged;
            if (handlers == null)
                return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // 창 하나의 처리 오류가 다른 창·FFmpeg 찾기를 막지 않게
                }
            }
        }

        /// <summary>미디어 썸네일(캐시). FFmpeg가 없거나 실패하면 null.</summary>
        public Task<BitmapSource> Thumbnail(MediaInfo media)
        {
            var tool = Tool;
            if (tool == null || media.Kind == MediaKind.Audio)
                return Task.FromResult<BitmapSource>(null);
            return _thumbnails.GetOrAdd(media.Path, key => Task.Run(async () =>
            {
                await PictureGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var seconds = media.Kind == MediaKind.Video ? Math.Min(1, media.DurationSeconds / 3) : 0;
                    var png = await MediaPictures.Thumbnail(tool, media, seconds, 160, CancellationToken.None).ConfigureAwait(false);
                    return Decode(png);
                }
                catch (Exception)
                {
                    // 실패는 저장하지 않는다(다음에 다시 시도)
                    _thumbnails.TryRemove(media.Path, out _);
                    return null;
                }
                finally
                {
                    PictureGate.Release();
                }
            }));
        }

        /// <summary>오디오 파형(초당 50개, 캐시). 소리가 없거나 실패하면 null.</summary>
        public Task<float[]> Waveform(string path)
        {
            var tool = Tool;
            if (tool == null)
                return Task.FromResult<float[]>(null);
            return _waveforms.GetOrAdd(path, key => Task.Run(async () =>
            {
                await PictureGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await MediaPictures.Waveform(tool, path, WaveformPeaksPerSecond, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _waveforms.TryRemove(path, out _);
                    return null;
                }
                finally
                {
                    PictureGate.Release();
                }
            }));
        }

        public const int WaveformPeaksPerSecond = 50;

        // ---------- 전환 효과 ----------

        /// <summary>사용자 전환 효과 폴더(데이터 폴더\transitions) — *.json을 넣고 [다시 읽기]를 누르면 효과 탭에 더해진다.</summary>
        public string TransitionFolder { get { return Path.Combine(Manager.DataDirectory, "transitions"); } }

        /// <summary>마지막으로 사용자 효과 폴더를 읽을 때 건너뛴 정의·파일과 그 까닭.</summary>
        public IReadOnlyList<string> TransitionErrors { get; private set; } = Array.Empty<string>();

        /// <summary>사용자 효과 폴더를 (다시) 읽는다.</summary>
        public void LoadTransitions()
        {
            try
            {
                TransitionErrors = Transitions.LoadFolder(TransitionFolder);
            }
            catch (Exception ex)
            {
                TransitionErrors = new[] { "효과 폴더를 읽지 못했습니다: " + ex.Message };
            }
        }

        /// <summary>
        /// 효과 미리 보기 띠(<see cref="Transitions.PreviewFrames"/>칸). 그리는 방법이 같으면 한 번만 그려 데이터 폴더\cache\transitions에 둔다.
        /// FFmpeg가 없거나 실패하면 null(다음에 다시 시도).
        /// </summary>
        public Task<BitmapSource> TransitionPreview(TransitionDef def)
        {
            var key = Transitions.PreviewKey(def);
            var file = Path.Combine(Manager.DataDirectory, "cache", "transitions", key + ".png");
            var tool = Tool;
            if (tool == null && !File.Exists(file))
                return Task.FromResult<BitmapSource>(null);
            return _transitionPreviews.GetOrAdd(key, k => Task.Run(async () =>
            {
                try
                {
                    if (File.Exists(file))
                        return Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
                    byte[] png;
                    await PictureGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        png = await MediaPictures.TransitionStrip(tool, def, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        PictureGate.Release();
                    }
                    var picture = Decode(png);
                    if (picture != null)
                    {
                        // 다 쓴 뒤에 이름을 바꿔 넣는다(쓰다 끊겨도 깨진 캐시가 남지 않게)
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        var part = file + "." + Guid.NewGuid().ToString("N") + ".part";
                        await File.WriteAllBytesAsync(part, png).ConfigureAwait(false);
                        try
                        {
                            File.Move(part, file, false);
                        }
                        catch (IOException)
                        {
                            File.Delete(part);
                        }
                    }
                    return picture;
                }
                catch (Exception)
                {
                    _transitionPreviews.TryRemove(k, out _);
                    return null;
                }
            }));
        }

        public static BitmapSource Decode(byte[] png)
        {
            if (png == null || png.Length == 0)
                return null;
            var image = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
    }
}
