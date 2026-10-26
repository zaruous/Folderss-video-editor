using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 인물 마스크(사람만 따낸 흑백 마스크, 흰색 = 사람)를 이 PC에서 만들어 두고 찾는다 — 동영상·사진을 인터넷으로 보내지 않는다.
    /// 만들기: 원본 → FFmpeg(모델 크기 RGB 프레임) → MODNet(ONNX Runtime, CPU) → 시간 평활 → FFmpeg(FFV1 흑백 .mkv, 사진은 흑백 PNG).
    /// 찾기(<see cref="Find"/>)는 디스크만 본다 — 마스크는 데이터 폴더\person-mask\masks에 원본 경로·크기·고친 시각·모델로 지은 이름(<see cref="PersonMaskText.CacheName"/>)으로 있다.
    /// 모델은 데이터 폴더\person-mask\models(시험은 FOLDERSS_VE_PERSONMASK 폴더가 먼저).
    /// </summary>
    public sealed class PersonMaskService : IPersonMaskProvider
    {
        // 만들기는 CPU를 다 쓰므로 한 번에 하나(편집 창이 여럿이어도)
        private static readonly SemaphoreSlim BuildGate = new SemaphoreSlim(1);
        private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(1);

        /// <param name="folder">데이터 폴더\person-mask. 만들거나 내려받을 때만 폴더를 만든다.</param>
        public PersonMaskService(string folder)
        {
            Folder = folder;
        }

        public string Folder { get; }
        public string ModelFolder { get { return Path.Combine(Folder, "models"); } }
        public string MaskFolder { get { return Path.Combine(Folder, "masks"); } }

        /// <summary>
        /// 원본의 마스크가 준비돼 있으면 그 경로(원본이 그 뒤 바뀌었거나 만드는 중(.part)이면 null). 디스크만 본다.
        /// 찾은 마스크는 사용 시각을 지금으로 적는다(캐시 상한 정리가 오래 안 쓴 것부터 지운다 — NTFS는 접근 시각을 잘 갱신하지 않음).
        /// </summary>
        public string Find(string sourcePath)
        {
            var name = CacheName(sourcePath);
            if (name == null)
                return null;
            foreach (var image in new[] { false, true })
            {
                var path = Path.Combine(MaskFolder, name + PersonMaskText.Extension(image));
                if (File.Exists(path))
                {
                    try
                    {
                        File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // FFmpeg가 읽는 중이면 못 고칠 수 있다 — 마스크는 그대로 쓴다
                    }
                    return path;
                }
            }
            return null;
        }

        /// <summary>지금 원본 파일(크기·고친 시각)로 지은 캐시 이름. 원본이 없거나 못 읽으면 null.</summary>
        private static string CacheName(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath))
                    return null;
                var file = new FileInfo(sourcePath);
                return file.Exists ? PersonMaskText.CacheName(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException
                                       || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        // ---------- 모델 ----------

        /// <summary>모델을 찾을 곳(순서대로): FOLDERSS_VE_PERSONMASK 폴더(바로 아래·models 아래), 데이터 폴더\person-mask\models.</summary>
        public List<string> ModelCandidates(string envFolder)
        {
            var file = PersonMaskText.Model.File;
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(envFolder))
            {
                list.Add(Path.Combine(envFolder, file));
                list.Add(Path.Combine(envFolder, "models", file));
            }
            list.Add(Path.Combine(ModelFolder, file));
            return list;
        }

        /// <summary>쓸 모델 파일(크기까지 맞아야 — 받다 끊긴 것은 .part로만 남는다). 없으면 null.</summary>
        public string ModelPath()
        {
            return ModelCandidates(Environment.GetEnvironmentVariable(PersonMaskText.EnvFolder)).FirstOrDefault(HasModel);
        }

        public static bool HasModel(string path)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length == PersonMaskText.Model.Size;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// 모델을 데이터 폴더\person-mask\models에 받는다(받아 두었으면 그대로). 고정한 크기·SHA-256이 맞아야 쓴다.
        /// 실패·취소하면 받던 파일(.part)을 지우고, 비게 된 폴더도 치운다.
        /// </summary>
        public async Task<string> DownloadModel(IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            var model = PersonMaskText.Model;
            var final = Path.Combine(ModelFolder, model.File);
            if (HasModel(final))
                return final;
            await DownloadGate.WaitAsync(cancellation).ConfigureAwait(false);
            var part = final + ".part";
            try
            {
                if (HasModel(final))
                    return final;
                Directory.CreateDirectory(ModelFolder);
                using (var http = GitHubDownload.Client(handler))
                    await GitHubDownload.Save(http, model.Url, model.Size, model.Sha256, part, "내려받는 중 — 인물 분리 모델(" + model.Name + ")", progress, cancellation).ConfigureAwait(false);
                File.Move(part, final, true);
                return final;
            }
            finally
            {
                RenderCommand.TryDelete(part);
                RemoveIfEmpty(ModelFolder);
                RemoveIfEmpty(Folder);
                DownloadGate.Release();
            }
        }

        private static void RemoveIfEmpty(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 빈 폴더 — 못 지워도 그만
            }
        }

        // ---------- 만들기 ----------

        /// <summary>
        /// media(동영상·사진)의 마스크를 만들어 경로를 돌려준다(이미 있으면 그대로). 진행률은 프레임 수(Done/Total).
        /// 만드는 동안은 옆의 .part에 쓰고 다 되면 바꿔 넣는다 — 취소·실패하면 .part를 지운다. 같은 원본의 옛 마스크(원본을 바꿔 넣기 전 것)는 지운다.
        /// </summary>
        public async Task<string> Build(FfmpegTool tool, MediaInfo media, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            if (media == null || (media.Kind != MediaKind.Video && media.Kind != MediaKind.Image))
                throw new InvalidOperationException("인물 마스크는 동영상·사진에만 만들 수 있습니다.");
            var model = ModelPath() ?? throw new InvalidOperationException("인물 분리 모델이 없습니다. 먼저 내려받으세요.");
            var image = media.Kind == MediaKind.Image;
            await BuildGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var name = CacheName(media.Path) ?? throw new FileNotFoundException("원본 파일이 없습니다: " + media.Name);
                var final = Path.Combine(MaskFolder, name + PersonMaskText.Extension(image));
                if (File.Exists(final))
                    return final;
                Directory.CreateDirectory(MaskFolder);
                var part = final + ".part";
                try
                {
                    await Task.Run(() => Segment(tool, model, media, image, part, progress, cancellation), cancellation).ConfigureAwait(false);
                    // 만드는 동안 원본을 바꿔 넣었으면 이 마스크는 옛 원본의 것
                    if (CacheName(media.Path) != name)
                        throw new InvalidOperationException("만드는 동안 원본 파일이 바뀌었습니다. 다시 만드세요: " + media.Name);
                    File.Move(part, final, true);
                    RemoveOld(final);
                    RecordSource(final, media.Path);
                    return final;
                }
                finally
                {
                    RenderCommand.TryDelete(part);
                }
            }
            finally
            {
                BuildGate.Release();
            }
        }

        /// <summary>
        /// 원본 → 마스크 파일(output). 디코더·인코더 FFmpeg를 함께 띄워 한 프레임씩 흘린다(디코딩·인코딩은 다른 프로세스라 추론과 겹친다).
        /// 취소하면 두 프로세스를 끝낸다.
        /// </summary>
        private static void Segment(FfmpegTool tool, string model, MediaInfo media, bool image, string output,
                                    IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation)
        {
            var (width, height) = PersonMaskText.InputSize(media.Width, media.Height, PersonMaskText.Model.ShortSide);
            var rate = image ? FrameRate.Default : PersonMaskText.MaskRate(media.FrameRate);
            var total = image ? 1 : PersonMaskText.FrameCount(media.DurationSeconds, rate);
            progress?.Report(new FfmpegDownloader.Status { Stage = "인물 분리 모델 여는 중" });
            using (var segmenter = PersonSegmenter.Open(model))
            using (var decoder = FfmpegRunner.Start(tool.FfmpegPath, PersonMaskText.DecodeArguments(media.Path, width, height, rate, image), true))
            using (var encoder = FfmpegRunner.Start(tool.FfmpegPath, PersonMaskText.EncodeArguments(width, height, rate, output, image), false, true))
            using (cancellation.Register(() =>
                   {
                       FfmpegRunner.Kill(decoder);
                       FfmpegRunner.Kill(encoder);
                   }))
            {
                var decodeErrors = decoder.StandardError.ReadToEndAsync();
                var encodeErrors = encoder.StandardError.ReadToEndAsync();
                var rgb = new byte[width * height * 3];
                var alpha = new float[width * height];
                var mask = new byte[width * height];
                byte[] previous = null;
                long done = 0;
                var input = decoder.StandardOutput.BaseStream;
                var sink = encoder.StandardInput.BaseStream;
                var shown = Stopwatch.StartNew();
                var broken = false;
                progress?.Report(new FfmpegDownloader.Status { Stage = "인물 분리 중", Done = 0, Total = total });
                try
                {
                    while (ReadFrame(input, rgb))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        segmenter.Run(rgb, width, height, alpha);
                        PersonMaskText.ToMask(alpha, mask, previous);
                        sink.Write(mask, 0, mask.Length);
                        // 방금 쓴 마스크가 다음 프레임의 앞 프레임
                        if (previous == null)
                        {
                            previous = mask;
                            mask = new byte[previous.Length];
                        }
                        else
                        {
                            (previous, mask) = (mask, previous);
                        }
                        done++;
                        if (shown.ElapsedMilliseconds >= 200)
                        {
                            shown.Restart();
                            progress?.Report(new FfmpegDownloader.Status { Stage = "인물 분리 중", Done = done, Total = Math.Max(total, done) });
                        }
                    }
                    // 다 썼다고 알린다(인코더가 파일을 마무리하고 끝난다)
                    encoder.StandardInput.Close();
                }
                catch (IOException)
                {
                    // 취소로 프로세스를 끝냈거나 인코더가 먼저 끝남 — 아래에서 가린다
                    broken = true;
                    FfmpegRunner.Kill(decoder);
                }
                catch
                {
                    // 취소·추론 실패: 두 FFmpeg를 끝내고 인코더가 .part를 놓을 때까지 잠깐 기다린다(지울 수 있게)
                    FfmpegRunner.Kill(decoder);
                    FfmpegRunner.Kill(encoder);
                    encoder.WaitForExit(5000);
                    throw;
                }
                decoder.WaitForExit();
                encoder.WaitForExit();
                cancellation.ThrowIfCancellationRequested();
                if (broken && encoder.ExitCode != 0)
                    throw new FfmpegException("인물 마스크를 쓰지 못했습니다.", encodeErrors.GetAwaiter().GetResult());
                if (decoder.ExitCode != 0)
                    throw new FfmpegException("원본 화면을 읽지 못했습니다: " + media.Name, decodeErrors.GetAwaiter().GetResult());
                if (done == 0)
                    throw new InvalidDataException("원본에서 화면을 읽지 못했습니다: " + media.Name);
                if (encoder.ExitCode != 0)
                    throw new FfmpegException("인물 마스크를 쓰지 못했습니다.", encodeErrors.GetAwaiter().GetResult());
                progress?.Report(new FfmpegDownloader.Status { Stage = "인물 분리 중", Done = done, Total = done });
            }
        }

        /// <summary>프레임 하나를 꽉 채워 읽는다. 끝(또는 중간에 끊김)이면 false.</summary>
        private static bool ReadFrame(Stream input, byte[] buffer)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = input.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0)
                    return false;
                filled += read;
            }
            return true;
        }

        /// <summary>keep과 같은 원본(경로 해시)의 다른 마스크 — 원본을 바꿔 넣기 전에 만든 것 — 를 지운다.</summary>
        private void RemoveOld(string keep)
        {
            try
            {
                var prefix = Path.GetFileName(keep).Substring(0, 17);
                foreach (var file in Directory.EnumerateFiles(MaskFolder, prefix + "*"))
                {
                    if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        RenderCommand.TryDelete(file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 옛 마스크는 쓰이지 않는다 — 못 지워도 다음에
            }
        }

        /// <summary>마스크 캐시 크기 상한(기본). 넘으면 오래 안 쓴 것부터 지운다.</summary>
        public const long MaxCacheBytes = 10L * 1024 * 1024 * 1024;
        // 이 안에 쓴 마스크는 상한을 넘어도 남긴다(다시 만드는 데 몇 분 걸린다)
        private static readonly TimeSpan KeepRecent = TimeSpan.FromDays(1);
        private const string SourceExtension = ".source";
        private static readonly System.Text.RegularExpressions.Regex MaskName =
            new System.Text.RegularExpressions.Regex("^[0-9a-f]{16}-[0-9a-f]{16}\\.(mkv|png)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>마스크 옆에 원본 경로를 적어 둔다(마스크 이름은 경로의 해시라 원본을 알 수 없다 — 원본을 지운 마스크를 정리하려고). 못 적어도 마스크는 쓴다.</summary>
        internal static void RecordSource(string mask, string sourcePath)
        {
            try
            {
                File.WriteAllText(SourceRecord(mask), Path.GetFullPath(sourcePath));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                // 기록이 없으면 크기 상한으로만 정리된다
            }
        }

        private static string SourceRecord(string mask)
        {
            return Path.Combine(Path.GetDirectoryName(mask), Path.GetFileNameWithoutExtension(mask) + SourceExtension);
        }

        /// <summary>
        /// 편집기가 시작할 때(배경에서): 끊긴 만들기·내려받기가 남긴 .part 가운데 age 넘게 그대로인 것(만드는 중인 파일은 계속 바뀐다),
        /// 원본이 지워졌거나 바뀐 마스크(원본 기록으로 — 원본 폴더째 없으면 빠진 드라이브일 수 있어 남김, 기록이 없는 예전 마스크는 남김), 마스크 없이 남은 기록을 지운다.
        /// 그래도 maxBytes를 넘으면 오래 안 쓴 마스크부터 지운다(하루 안에 쓴 것은 남김).
        /// </summary>
        public void Cleanup(TimeSpan age, long maxBytes = MaxCacheBytes)
        {
            foreach (var folder in new[] { MaskFolder, ModelFolder })
            {
                try
                {
                    if (!Directory.Exists(folder))
                        continue;
                    foreach (var part in Directory.EnumerateFiles(folder, "*.part"))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > age)
                            RenderCommand.TryDelete(part);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 다음 시작 때 다시
                }
            }
            try
            {
                if (Directory.Exists(MaskFolder))
                    TrimMasks(maxBytes);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 다음 시작 때 다시
            }
        }

        private void TrimMasks(long maxBytes)
        {
            var folder = new DirectoryInfo(MaskFolder);
            var masks = folder.EnumerateFiles().Where(f => MaskName.IsMatch(f.Name)).ToList();
            var names = new HashSet<string>(masks.Select(f => Path.GetFileNameWithoutExtension(f.Name)), StringComparer.OrdinalIgnoreCase);
            foreach (var record in folder.EnumerateFiles("*" + SourceExtension).ToList())
            {
                if (!names.Contains(Path.GetFileNameWithoutExtension(record.Name)))
                    RenderCommand.TryDelete(record.FullName);
            }
            var alive = new List<FileInfo>();
            foreach (var mask in masks)
            {
                if (IsOrphan(mask))
                    Remove(mask);
                else
                    alive.Add(mask);
            }
            var total = alive.Sum(f => f.Length);
            var now = DateTime.UtcNow;
            foreach (var mask in alive.OrderBy(LastUsed))
            {
                if (total <= maxBytes || now - LastUsed(mask) < KeepRecent)
                    break;
                var length = mask.Length;
                if (Remove(mask))
                    total -= length;
            }
        }

        /// <summary>원본 기록이 있고, 원본 폴더는 있는데 파일이 없거나 지금 원본으로 지은 이름이 이 마스크가 아니면(원본이 바뀜) true.</summary>
        private static bool IsOrphan(FileInfo mask)
        {
            var record = SourceRecord(mask.FullName);
            if (!File.Exists(record))
                return false;
            var source = File.ReadAllText(record).Trim();
            if (source.Length == 0)
                return false;
            if (!File.Exists(source))
                return Directory.Exists(Path.GetDirectoryName(source));
            return !string.Equals(CacheName(source) + mask.Extension, mask.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime LastUsed(FileInfo file)
        {
            return file.LastAccessTimeUtc > file.LastWriteTimeUtc ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;
        }

        /// <summary>마스크와 그 원본 기록을 지운다. 마스크를 지웠으면 true(쓰는 중이면 못 지움).</summary>
        private static bool Remove(FileInfo mask)
        {
            var path = mask.FullName;
            RenderCommand.TryDelete(path);
            if (File.Exists(path))
                return false;
            RenderCommand.TryDelete(SourceRecord(path));
            return true;
        }
    }
}
