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
    /// </summary>
    public static class SoundExtractor
    {
        /// <summary>
        /// input의 소리를 output에 저장하고 실제로 쓴 경로를 돌려준다(그대로 꺼내지 못했으면 확장자를 .wav로 바꾼 경로).
        /// from·seconds는 원본 초(seconds ≤ 0 = 끝까지), codec은 ffprobe의 오디오 코덱 이름(모르면 null).
        /// </summary>
        public static async Task<string> Extract(FfmpegTool tool, string input, string codec, string output, double from, double seconds, double speed, CancellationToken cancellation)
        {
            if (SoundExtract.ShouldCopy(Path.GetExtension(output), codec, speed)
                && await Run(tool, input, output, true, from, seconds, speed, cancellation).ConfigureAwait(false) == null)
                return output;
            var wav = Path.ChangeExtension(output, ".wav");
            var error = await Run(tool, input, wav, false, from, seconds, speed, cancellation).ConfigureAwait(false);
            if (error != null)
                throw new FfmpegException("사운드를 꺼내지 못했습니다.", error);
            return wav;
        }

        /// <summary>
        /// 옆의 임시 이름(같은 확장자 — FFmpeg가 그릇을 고른다)에 쓰고 다 되면 바꿔 넣는다: 실패해도 덮어쓰기로 고른 원래 파일이 남는다.
        /// 성공하면 null, 실패하면 FFmpeg 오류 글.
        /// </summary>
        private static async Task<string> Run(FfmpegTool tool, string input, string output, bool copy, double from, double seconds, double speed, CancellationToken cancellation)
        {
            var full = Path.GetFullPath(output);
            var temp = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(full));
            try
            {
                var result = await FfmpegRunner.RunText(tool.FfmpegPath, SoundExtract.Arguments(input, temp, copy, from, seconds, speed), TimeSpan.FromMinutes(30), cancellation).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
                    return string.IsNullOrWhiteSpace(result.Error) ? "FFmpeg 오류(" + result.ExitCode + ")" : result.Error;
                File.Move(temp, full, true);
                return null;
            }
            finally
            {
                RenderCommand.TryDelete(temp);
            }
        }
    }
}
