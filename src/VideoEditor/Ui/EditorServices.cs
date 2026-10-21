using System;
using System.Collections.Concurrent;
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
        public const string PlayerMutedKey = "playerMuted";
        public const string PreviewScaleKey = "previewScale";
        public const string LastFolderKey = "lastFolder";

        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _thumbnails = new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<float[]>> _waveforms = new ConcurrentDictionary<string, Task<float[]>>(StringComparer.OrdinalIgnoreCase);
        private Task<FfmpegTool> _locating;
        // 썸네일·파형 FFmpeg를 한꺼번에 수십 개 띄우지 않게
        private static readonly SemaphoreSlim PictureGate = new SemaphoreSlim(3);

        public EditorServices(IPluginManager manager)
        {
            Manager = manager;
        }

        public IPluginManager Manager { get; }

        /// <summary>찾은 FFmpeg(없으면 null).</summary>
        public FfmpegTool Tool { get; private set; }

        public event EventHandler ToolChanged;

        /// <summary>플러그인이 내려받는 곳(데이터 폴더\ffmpeg).</summary>
        public string DownloadFolder { get { return Path.Combine(Manager.DataDirectory, "ffmpeg"); } }

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
