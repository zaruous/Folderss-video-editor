using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 사운드만 파일로 꺼내기(FFmpeg). 원본 오디오를 다시 인코딩하지 않고 꺼내는 것을 먼저 하고(<see cref="SoundExtract.ShouldCopy"/>),
    /// 안 되면(그릇이 코덱을 받지 않음 등) 같은 이름의 .wav로 쓴다. 속도를 바꾼 구간은 처음부터 WAV로(atempo).
    /// 진행률은 FFmpeg -progress(표준 출력)로 읽고, 취소하면 FFmpeg를 끝내고 쓰던 임시 파일을 지운다(#34).
    /// </summary>
    public static class SoundExtractor
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

        /// <summary>
        /// input의 소리를 output에 저장하고 실제로 쓴 경로를 돌려준다(그대로 꺼내지 못했으면 확장자를 .wav로 바꾼 경로).
        /// from·seconds는 원본 초(seconds ≤ 0 = 끝까지), codec은 ffprobe의 오디오 코덱 이름(모르면 null).
        /// volume(<see cref="SoundExtract.ClipVolume"/>)을 주면 클립의 음량·페이드를 넣어 WAV로 쓴다.
        /// progress에는 0~1을 알린다(분모는 <see cref="SoundExtract.OutputSeconds"/> — 끝까지 꺼낼 때는 sourceSeconds(원본 길이)가 있어야 한다).
        /// 취소하면 <see cref="OperationCanceledException"/> — 덮어쓰기로 고른 원래 파일은 그대로 남는다.
        /// </summary>
        public static async Task<string> Extract(FfmpegTool tool, string input, string codec, string output, double from, double seconds, double speed, CancellationToken cancellation,
                                                 string volume = null, IProgress<double> progress = null, double sourceSeconds = 0)
        {
            var total = SoundExtract.OutputSeconds(from, seconds, speed, sourceSeconds);
            if (SoundExtract.ShouldCopy(Path.GetExtension(output), codec, speed, volume != null)
                && await Run(tool, input, output, true, from, seconds, speed, null, progress, total, cancellation).ConfigureAwait(false) == null)
                return output;
            var wav = Path.ChangeExtension(output, ".wav");
            var error = await Run(tool, input, wav, false, from, seconds, speed, volume, progress, total, cancellation).ConfigureAwait(false);
            if (error != null)
                throw new FfmpegException("사운드를 꺼내지 못했습니다.", error);
            return wav;
        }

        /// <summary>
        /// 옆의 임시 이름(같은 확장자 — FFmpeg가 그릇을 고른다)에 쓰고 다 되면 바꿔 넣는다: 실패·취소해도 덮어쓰기로 고른 원래 파일이 남는다.
        /// 성공하면 null, 실패하면 FFmpeg 오류 글. 취소되면(FFmpeg가 이미 끝났어도) 바꿔 넣지 않고 예외.
        /// </summary>
        private static async Task<string> Run(FfmpegTool tool, string input, string output, bool copy, double from, double seconds, double speed, string volume,
                                              IProgress<double> progress, double total, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(output);
            var temp = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(full));
            try
            {
                var args = SoundExtract.Arguments(input, temp, copy, from, seconds, speed, volume);
                // 진행률: 출력 앞에 -progress(표준 출력으로 out_time_us=…, 0.5초마다)
                args.InsertRange(args.Count - 1, new[] { "-progress", "pipe:1", "-nostats" });
                using (var process = FfmpegRunner.Start(tool.FfmpegPath, args, true))
                {
                    var error = process.StandardError.ReadToEndAsync();
                    using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        timer.CancelAfter(Timeout);
                        // 취소·시간 초과면 FFmpeg를 끝낸다 — 표준 출력이 닫혀 읽기가 끝나고, 끝날 때까지 기다린 뒤 임시 파일을 지운다
                        using (timer.Token.Register(() => FfmpegRunner.Kill(process)))
                        {
                            string line;
                            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                var done = FfmpegText.ProgressSeconds(line);
                                if (done.HasValue && total > 0)
                                    progress?.Report(Math.Max(0, Math.Min(1, done.Value / total)));
                            }
                            await process.WaitForExitAsync().ConfigureAwait(false);
                        }
                        cancellation.ThrowIfCancellationRequested();
                        if (timer.IsCancellationRequested)
                            throw new TimeoutException("FFmpeg가 " + Timeout.TotalMinutes + "분 안에 끝나지 않았습니다.");
                    }
                    var message = await error.ConfigureAwait(false);
                    if (process.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
                        return string.IsNullOrWhiteSpace(message) ? "FFmpeg 오류(" + process.ExitCode + ")" : message;
                }
                File.Move(temp, full, true);
                progress?.Report(1);
                return null;
            }
            finally
            {
                RenderCommand.TryDelete(temp);
            }
        }
    }
}
