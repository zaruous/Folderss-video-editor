using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public const string TextStylesKey = "textStyles";
        public const string SeenVideoEffectsKey = "seenVideoEffects";

        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _thumbnails = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<float[]>> _waveforms = new ConcurrentDictionary<string, Task<float[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _transitionPreviews = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _effectPreviews = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _stylePreviews = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.Ordinal);
        private Task<FfmpegTool> _locating;
        private Task<YtDlpTool> _locatingYtDlp;
        // 썸네일·파형 FFmpeg를 한꺼번에 수십 개 띄우지 않게
        private static readonly SemaphoreSlim PictureGate = new SemaphoreSlim(3);
        // 스타일 썸네일은 하나씩 그린다(TextStylePreview)
        private static readonly SemaphoreSlim StylePreviewGate = new SemaphoreSlim(1);
        // 스타일 썸네일 캐시 상한: 스타일 모음 16개 + 내 스타일 50개에 여유
        private const int MaxStylePreviews = 96;

        private readonly Lazy<StyledTextRasterizer> _textRasterizer = new Lazy<StyledTextRasterizer>(() => new StyledTextRasterizer());

        public EditorServices(IPluginManager manager)
        {
            Manager = manager;
            LoadTransitions();
            // 미리 보기·재생·내보내기가 "사람에게만" 효과의 마스크를 여기서 찾는다
            PersonMasks = new PersonMaskService(PersonMaskFolder);
            RenderEnvironment.PersonMasks = PersonMasks;
            // 앞서 끝난 실행이 남긴 꾸민 글자 PNG 캐시와 끊긴 인물 마스크(.part)를 치운다(살아 있는 실행의 폴더는 건드리지 않음).
            // 시스템 글꼴 목록도 미리 읽어 둔다 — 처음 글자 클립을 고를 때 UI 스레드가 멈추지 않게
            Task.Run(() =>
            {
                TextImageCache.Cleanup(RenderEnvironment.TempRoot());
                _ = StyledTextRasterizer.Fonts;
                PersonMasks.Cleanup(TimeSpan.FromHours(1));
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

        /// <summary>인물 분리 모델과 만든 인물 마스크를 두는 곳(데이터 폴더\person-mask — 모델은 models, 마스크는 masks).</summary>
        public string PersonMaskFolder { get { return Path.Combine(Manager.DataDirectory, "person-mask"); } }

        /// <summary>인물 마스크 만들기·찾기(<see cref="RenderEnvironment.PersonMasks"/>로도 쓰인다).</summary>
        public PersonMaskService PersonMasks { get; }

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
            // FFmpeg가 바뀌면 도우미도 그 FFmpeg의 DLL로 다시 띄운다(거듭 실패해 꺼 둔 것도 다시)
            ResetHelper();
            Raise(ToolChanged);
        }

        private void Raise(EventHandler handlers)
        {
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

        // ---------- 미리 보기 도우미 ----------

        private readonly object _helperGate = new object();
        private PreviewHelper _helper;
        private Task _helperStarting;
        private int _helperUsers;
        private int _helperFailures;
        private int _helperCrashes;
        private DateTime _helperRetryAt = DateTime.MinValue;
        /// <summary>도우미가 이만큼 잇달아 그리지 못하면(ffmpeg.exe는 되는데) 끈다 — 요청마다 두 번 그리지 않게.</summary>
        private const int MaxHelperFailures = 5;
        /// <summary>도우미가 이만큼 잇달아 죽으면 FFmpeg를 바꿀 때까지 다시 띄우지 않는다.</summary>
        private const int MaxHelperCrashes = 3;

        /// <summary>살아 있는 미리 보기 도우미(없으면 null). 상태가 바뀌면 <see cref="HelperChanged"/>.</summary>
        public PreviewHelper Helper
        {
            get
            {
                var helper = _helper;
                return helper != null && helper.IsAlive ? helper : null;
            }
        }

        public event EventHandler HelperChanged;

        /// <summary>도우미 상태 글(FFmpeg 단추 도움말): 쓰는 중인지, 아니면 왜 ffmpeg.exe로 그리는지.</summary>
        public string HelperStatus { get; private set; } = "미리 보기 도우미: 아직 시작하지 않음";

        /// <summary>"off"면 공유 DLL이 없는 FFmpeg로 편집기를 열 때 미리 보기용 DLL 받기를 알리지 않는다(FFmpeg 창에서 고름).</summary>
        public const string PreviewLibrariesNoticeKey = "previewLibrariesNotice";
        private int _previewLibrariesNoticed;

        /// <summary>미리 보기 도우미 전용으로 받은 공유 DLL을 두는 곳(데이터 폴더\ffmpeg-preview — DLL은 그 아래 bin). 설치된 FFmpeg는 그대로 둔다.</summary>
        public string PreviewLibrariesFolder { get { return Path.Combine(Manager.DataDirectory, "ffmpeg-preview"); } }

        /// <summary>
        /// 도우미가 쓸 공유 DLL 폴더: 지금 FFmpeg 폴더에 있으면 그것. 없으면 지금 FFmpeg가 도우미와 같은 주 버전일 때만 — 미리 보기와 내보내기가 같게 —
        /// 도우미용으로 받은 것, 플러그인이 받아 둔 FFmpeg의 것. 없으면 null.
        /// </summary>
        public string HelperLibraryFolder(FfmpegTool tool)
        {
            if (tool == null)
                return null;
            if (PreviewHelper.HasLibraries(tool.Folder))
                return tool.Folder;
            if (tool.MajorVersion != PreviewHelper.FfmpegMajor)
                return null;
            return new[] { Path.Combine(PreviewLibrariesFolder, "bin"), Path.Combine(DownloadFolder, "bin") }.FirstOrDefault(PreviewHelper.HasLibraries);
        }

        /// <summary>도우미용 공유 DLL을 받으면 도우미를 쓸 수 있는지: 지금 FFmpeg가 도우미와 같은 주 버전인데 쓸 DLL이 없을 때(winget Gyan full_build 등).</summary>
        public bool CanDownloadPreviewLibraries(FfmpegTool tool)
        {
            return tool != null && tool.MajorVersion == PreviewHelper.FfmpegMajor && HelperLibraryFolder(tool) == null
                   && PreviewHelper.FindExecutable(Manager.PluginDirectory) != null;
        }

        /// <summary>편집기를 열 때 미리 보기용 DLL 받기를 알릴지 — Folderss를 켠 동안 한 번만 true("알리지 않기"를 골랐으면 늘 false).</summary>
        public bool TakePreviewLibrariesNotice()
        {
            if (!CanDownloadPreviewLibraries(Tool) || Setting(PreviewLibrariesNoticeKey) == "off")
                return false;
            return Interlocked.Exchange(ref _previewLibrariesNoticed, 1) == 0;
        }

        /// <summary>도우미용 공유 DLL만 받는다(FFmpeg 창에서 사용자가 누름 — 설치된 FFmpeg는 그대로). 받으면 도우미를 다시 띄운다. bin 경로를 돌려준다.</summary>
        public async Task<string> DownloadPreviewLibraries(IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            var bin = await FfmpegDownloader.DownloadPreviewLibraries(PreviewLibrariesFolder, progress, cancellation).ConfigureAwait(false);
            ResetHelper();
            return bin;
        }

        /// <summary>FFmpeg나 도우미 DLL이 바뀜 — 거듭 실패해 꺼 둔 것도 되돌리고 (다시) 띄운다.</summary>
        private void ResetHelper()
        {
            lock (_helperGate)
            {
                _helperRetryAt = DateTime.MinValue;
                _helperCrashes = 0;
                _helperFailures = 0;
            }
            EnsureHelper();
        }

        /// <summary>편집 창이 열림 — 도우미를 띄워 둔다. 창이 다 닫히면(<see cref="ReleaseHelper"/>) 끝낸다.</summary>
        public void AcquireHelper()
        {
            Interlocked.Increment(ref _helperUsers);
            EnsureHelper();
        }

        public void ReleaseHelper()
        {
            if (Interlocked.Decrement(ref _helperUsers) > 0)
                return;
            PreviewHelper helper;
            lock (_helperGate)
            {
                helper = _helper;
                _helper = null;
                // 일부러 끝낸 것이라 실패가 아니다 — 곧 다시 열어도 바로 띄운다(띄울 때 걸어 둔 5초 대기가 남아 있으면 첫 미리 보기까지 안 뜬다).
                // 거듭 실패하거나 DLL이 없어 꺼 둔 것(MaxValue)은 그대로
                if (_helperRetryAt != DateTime.MaxValue)
                    _helperRetryAt = DateTime.MinValue;
            }
            if (helper == null)
                return;
            SetHelperStatus("미리 보기 도우미: 편집 창을 닫아 끝냄");
            Task.Run(() => helper.Dispose());
        }

        /// <summary>지금 FFmpeg에 맞는 살아 있는 도우미. 없거나 죽었거나 다른 FFmpeg의 것이면 null을 주고 뒤에서 (다시) 띄운다.</summary>
        public PreviewHelper HelperFor(FfmpegTool tool)
        {
            var helper = Helper;
            if (helper != null && tool != null && string.Equals(helper.Folder, HelperLibraryFolder(tool), StringComparison.OrdinalIgnoreCase))
                return helper;
            EnsureHelper();
            return null;
        }

        /// <summary>도우미로 그리는 데 성공함 — 잇단 실패 수를 되돌린다.</summary>
        public void ReportHelperSuccess()
        {
            _helperFailures = 0;
            _helperCrashes = 0;
        }

        /// <summary>도우미 작업이 실패해 ffmpeg.exe로 그렸음: 도우미가 죽었으면 다시 띄우고, 그 작업만 거듭 실패하면 도우미를 끈다.</summary>
        public void ReportHelperFailure(PreviewHelper helper, HelperException error)
        {
            if (helper == null || error == null)
                return;
            if (error.HelperDied)
            {
                // 죽은 것은 Died에서 다룬다(아직 알림이 안 왔으면 다음 요청이 다시 띄운다)
                return;
            }
            if (Interlocked.Increment(ref _helperFailures) < MaxHelperFailures)
            {
                SetHelperStatus("미리 보기 도우미: 그리지 못해 ffmpeg.exe로 그림 — " + error.Message);
                return;
            }
            lock (_helperGate)
            {
                if (_helper != helper)
                    return;
                _helper = null;
                _helperRetryAt = DateTime.MaxValue;
            }
            SetHelperStatus("미리 보기 도우미: 거듭 그리지 못해 껐습니다(ffmpeg.exe로 그림) — " + error.Message);
            Task.Run(() => helper.Dispose());
        }

        /// <summary>
        /// 필요하면 도우미를 띄운다: 편집 창이 있고, FFmpeg를 찾았고 쓸 공유 DLL이 있고(<see cref="HelperLibraryFolder"/>), 도우미가 없거나 죽었거나 다른 DLL의 것일 때. 뒤에서 돈다.
        /// </summary>
        public void EnsureHelper()
        {
            var tool = Tool;
            lock (_helperGate)
            {
                if (_helperUsers <= 0 || tool == null)
                    return;
                var folder = HelperLibraryFolder(tool);
                var current = _helper;
                if (current != null && current.IsAlive && folder != null && string.Equals(current.Folder, folder, StringComparison.OrdinalIgnoreCase))
                    return;
                if (_helperStarting != null && !_helperStarting.IsCompleted)
                    return;
                if (DateTime.UtcNow < _helperRetryAt)
                    return;
                var exe = PreviewHelper.FindExecutable(Manager.PluginDirectory);
                if (exe == null)
                {
                    SetHelperStatus("미리 보기 도우미: 실행 파일(helper\\" + PreviewHelper.ExeName + ")이 없어 ffmpeg.exe로 그림");
                    _helperRetryAt = DateTime.MaxValue;
                    return;
                }
                if (folder == null)
                {
                    SetHelperStatus("미리 보기 도우미: 이 FFmpeg에 공유 DLL(shared 빌드)이 없어 ffmpeg.exe로 그림"
                                    + (tool.MajorVersion == PreviewHelper.FfmpegMajor
                                        ? " — FFmpeg 단추에서 미리 보기용 DLL을 받을 수 있음"
                                        : " — 도우미는 FFmpeg " + PreviewHelper.FfmpegMajor + "의 공유 DLL을 씀"));
                    _helperRetryAt = DateTime.MaxValue;
                    return;
                }
                _helperRetryAt = DateTime.UtcNow.AddSeconds(5);
                _helperStarting = Task.Run(() => StartHelper(exe, folder));
            }
        }

        private async Task StartHelper(string exe, string folder)
        {
            SetHelperStatus("미리 보기 도우미: 시작하는 중");
            PreviewHelper helper;
            try
            {
                helper = await PreviewHelper.StartAsync(exe, folder, TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_helperGate)
                {
                    _helperCrashes++;
                    _helperRetryAt = _helperCrashes >= MaxHelperCrashes ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(15);
                }
                SetHelperStatus("미리 보기 도우미: 띄우지 못해 ffmpeg.exe로 그림 — " + ex.Message);
                return;
            }
            helper.Died += reason => OnHelperDied(helper, reason);
            PreviewHelper stale;
            lock (_helperGate)
            {
                stale = _helper;
                _helper = _helperUsers > 0 ? helper : null;
                _helperFailures = 0;
            }
            if (_helper == null)
            {
                // 띄우는 동안 편집 창이 다 닫힘
                helper.Dispose();
                return;
            }
            if (stale != null)
                _ = Task.Run(() => stale.Dispose());
            SetHelperStatus("미리 보기 도우미: 쓰는 중 — FFmpeg " + helper.Version + " (" + folder + ")");
        }

        private void OnHelperDied(PreviewHelper helper, string reason)
        {
            int crashes;
            lock (_helperGate)
            {
                if (_helper != helper)
                    return;
                _helper = null;
                crashes = ++_helperCrashes;
                _helperRetryAt = crashes >= MaxHelperCrashes ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(5);
            }
            SetHelperStatus("미리 보기 도우미: 끝남 — " + reason + (crashes >= MaxHelperCrashes ? " (거듭 끝나 다시 띄우지 않음)" : " (다음 미리 보기 때 다시 띄움)"));
            Task.Run(() => helper.Dispose());
        }

        private void SetHelperStatus(string text)
        {
            HelperStatus = text;
            Raise(HelperChanged);
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

        /// <summary>사용자 전환 효과 폴더(데이터 폴더\transitions) — *.json을 넣고 [다시 읽기]를 누르면 [전환] 탭에 더해진다.</summary>
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
            return CachedStrip(_transitionPreviews, "transitions", Transitions.PreviewKey(def), tool => MediaPictures.TransitionStrip(tool, def, CancellationToken.None));
        }

        /// <summary>
        /// 동영상 효과 미리 보기 띠(<see cref="VideoEffects.PreviewFrames"/>칸). 전환 띠와 같이 한 번만 그려 데이터 폴더\cache\video-effects에 둔다.
        /// FFmpeg가 없거나 실패하면 null(다음에 다시 시도).
        /// </summary>
        public Task<BitmapSource> EffectPreview(VideoEffectDef def)
        {
            return CachedStrip(_effectPreviews, "video-effects", VideoEffects.PreviewKey(def), tool => MediaPictures.EffectStrip(tool, def, CancellationToken.None));
        }

        /// <summary>미리 보기 띠 캐시: 메모리(key) → 데이터 폴더\cache\folder\key.png → FFmpeg로 그리기(다 쓴 뒤 이름을 바꿔 넣음).</summary>
        private Task<BitmapSource> CachedStrip(ConcurrentDictionary<string, Task<BitmapSource>> memory, string folder, string key, Func<FfmpegTool, Task<byte[]>> draw)
        {
            var file = Path.Combine(Manager.DataDirectory, "cache", folder, key + ".png");
            var tool = Tool;
            if (tool == null && !File.Exists(file))
                return Task.FromResult<BitmapSource>(null);
            return memory.GetOrAdd(key, k => Task.Run(async () =>
            {
                try
                {
                    if (File.Exists(file))
                        return Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
                    byte[] png;
                    await PictureGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        png = await draw(tool).ConfigureAwait(false);
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
                    memory.TryRemove(k, out _);
                    return null;
                }
            }));
        }

        // ---------- 스타일 모음 ----------

        /// <summary>스타일 모음 썸네일에 그리는 글과 글자 크기(픽셀).</summary>
        public const string StylePreviewText = "가나Ab";
        public const double StylePreviewFontSize = 48;
        // 글자 클립의 기본 크기(Clip.FontSize) — 효과 길이를 이 크기 클립을 썸네일 크기로 줄인 비율로 줄인다(기본 크기 클립에서 보이는 모양 그대로)
        private const double DefaultClipFontSize = 72;

        /// <summary>스타일 모음 썸네일 한 장의 그리기 요청. 캔버스는 효과가 잘리지 않을 만큼 넉넉히 — PNG는 글자와 효과 크기만큼만 나온다.</summary>
        public static TextImageRequest StylePreviewRequest(TextStyle style)
        {
            var copy = style?.Clone() ?? new TextStyle();
            copy.Normalize();
            return new TextImageRequest
            {
                Text = StylePreviewText,
                Style = copy,
                FontPixelSize = StylePreviewFontSize,
                EffectScale = StylePreviewFontSize / DefaultClipFontSize,
                CanvasWidth = 640,
                CanvasHeight = 360
            };
        }

        /// <summary>
        /// 스타일 모음 썸네일(투명 PNG를 배경에서 그림). 같은 요청(해시 키)은 한 번만 그려 메모리에 둔다.
        /// 그리기는 꾸민 글자 캐시(30초 제한·.part에 쓰고 옮기기)를 그대로 거치고, 썸네일 때문에 새로 그린 파일은 읽은 뒤 지운다(임시 폴더에 쌓이지 않게).
        /// 그리개가 없거나 실패하면 null(다음에 다시 시도).
        /// </summary>
        public Task<BitmapSource> TextStylePreview(TextStyle style)
        {
            var rasterizer = TextRasterizer;
            if (rasterizer == null)
                return Task.FromResult<BitmapSource>(null);
            var request = StylePreviewRequest(style);
            // 내 스타일(최대 50개)이 바뀔 때마다 새 키가 생기므로 오래 쌓이지 않게 넘치면 비운다(썸네일은 다시 그리면 된다)
            if (_stylePreviews.Count > MaxStylePreviews)
                _stylePreviews.Clear();
            return _stylePreviews.GetOrAdd(request.Key(rasterizer.Version), key => Task.Run(async () =>
            {
                // 썸네일은 하나씩 — 그리기는 어차피 그리개 스레드 하나라, 기다리는 동안 스레드 풀 스레드를 붙잡지 않게 비동기로 줄 세운다
                // (수십 개를 한꺼번에 동기로 기다리면 스레드 풀이 막혀 미리 보기·파형까지 멈추고 일부는 시간 초과로 빠진다)
                await StylePreviewGate.WaitAsync().ConfigureAwait(false);
                string path = null;
                var drawn = false;
                try
                {
                    path = Path.Combine(RenderEnvironment.TempFolder(), TextImageCache.FolderName, "st-" + key + ".png");
                    // 미리 보기·내보내기가 같은 그림을 이미 캐시에 둔 것이면 지우지 않는다
                    drawn = !File.Exists(path);
                    TextImageCache.Ensure(rasterizer, path, request, CancellationToken.None);
                    var picture = Decode(File.ReadAllBytes(path));
                    if (picture == null)
                        _stylePreviews.TryRemove(key, out _);
                    return picture;
                }
                catch (Exception)
                {
                    _stylePreviews.TryRemove(key, out _);
                    return null;
                }
                finally
                {
                    if (drawn)
                        RenderCommand.TryDelete(path);
                    StylePreviewGate.Release();
                }
            }));
        }

        // ---------- 내 스타일 ----------

        /// <summary>내 스타일(설정 textStyles, <see cref="TextStyleLibrary"/>의 규칙으로 읽음 — 없거나 깨졌으면 빈 목록). 부를 때마다 설정을 새로 읽은 새 목록.</summary>
        public List<(string Name, TextStyle Style)> TextStyles()
        {
            return TextStyleLibrary.Read(Setting(TextStylesKey));
        }

        /// <summary>내 스타일을 설정에 쓴다(비면 키를 지운다). 실패하면 false.</summary>
        public bool SaveTextStyles(List<(string Name, TextStyle Style)> styles)
        {
            return SaveSetting(TextStylesKey, styles.Count == 0 ? null : TextStyleLibrary.Write(styles));
        }

        // ---------- 새 동영상 효과 ----------

        // 설정에 쓰지 못한 본 효과(디스크 오류 등) — 이 실행 동안은 본 것으로 둔다
        private readonly HashSet<string> _seenUnsaved = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>본 동영상 효과가 늘었다(효과 탭의 "새로" 표시·알림 줄·탭 머리글을 다시) — 효과를 본 것으로 적은 스레드(UI 스레드)에서.</summary>
        public event EventHandler SeenEffectsChanged;

        /// <summary>이미 본 동영상 효과 id(설정 seenVideoEffects를 <see cref="VideoEffectNews"/>의 규칙으로 — 없거나 깨졌으면 처음 판의 효과만). 부를 때마다 새로 읽은 새 모음.</summary>
        public HashSet<string> SeenEffects()
        {
            var seen = VideoEffectNews.ReadSeen(Setting(SeenVideoEffectsKey));
            seen.UnionWith(_seenUnsaved);
            return seen;
        }

        /// <summary>아직 보지 않은 새 동영상 효과(목록 차례).</summary>
        public List<VideoEffectDef> NewEffects()
        {
            return VideoEffectNews.New(SeenEffects());
        }

        /// <summary>
        /// 효과를 본 것으로 적는다(클립에 넣었거나, 고른 클립에 얹어 1초 넘게 미리 봤거나, 알림 줄의 [확인]). 새로 적은 효과가 있을 때만 설정에 쓰고 <see cref="SeenEffectsChanged"/>.
        /// 설정을 쓰지 못해도 예외를 내지 않고 이 실행 동안은 본 것으로 둔다.
        /// </summary>
        public void MarkEffectsSeen(IEnumerable<string> ids)
        {
            var seen = SeenEffects();
            var added = new List<string>();
            foreach (var id in ids ?? Array.Empty<string>())
            {
                if (VideoEffects.Find(id) != null && seen.Add(id))
                    added.Add(id);
            }
            if (added.Count == 0)
                return;
            if (!SaveSetting(SeenVideoEffectsKey, VideoEffectNews.WriteSeen(seen)))
                _seenUnsaved.UnionWith(added);
            var handlers = SeenEffectsChanged;
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
                    // 창 하나의 처리 오류가 다른 창·넣기 명령을 막지 않게
                }
            }
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
