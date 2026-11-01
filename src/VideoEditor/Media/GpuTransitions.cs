using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// GPU 전환(xfade_opencl)을 쓸 수 있는지 FFmpeg마다 한 번 알아보고, 쓸 수 있으면 커널 파일 경로를 준다.
    /// - 알아보는 동안과 쓸 수 없을 때는 null — 렌더는 지금처럼 CPU 수식(재생은 미리 그리기)으로 그린다. 결과는 같은 그림이다.
    /// - 알아보기: 커널 파일을 임시 폴더에 쓰고, 작은 두 화면을 커널 하나로 섞어 본다(OpenCL 장치 열기·커널 컴파일·내리기까지).
    /// - FOLDERSS_VE_NO_GPU_TRANSITIONS=1이면 쓰지 않는다(문제가 생긴 PC에서 끄는 길·시험).
    /// </summary>
    public static class GpuTransitions
    {
        private static readonly object Gate = new object();
        // FFmpeg 경로 → 알아본 결과(커널 파일 경로, 쓸 수 없으면 null). 알아보는 중이면 Pending에만 있다
        private static readonly Dictionary<string, string> Results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task> Pending = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        /// <summary>마지막으로 알아본 결과를 사람이 읽을 글로(상태 표시·로그).</summary>
        public static string Status { get; private set; } = "GPU 전환: 알아보기 전";

        /// <summary>GPU 전환을 쓸지(기본: 환경 변수 FOLDERSS_VE_NO_GPU_TRANSITIONS=1이 아니면 씀). 끄면 모두 CPU 수식 — 시험이 그림을 고정할 때도.</summary>
        public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("FOLDERSS_VE_NO_GPU_TRANSITIONS") != "1";

        /// <summary>
        /// 이 FFmpeg로 GPU 전환을 쓸 수 있으면 커널 파일 경로, 아니면 null. 처음 부르면 배경에서 알아보기 시작하고 그동안은 null을 준다
        /// (UI 스레드에서 불러도 멈추지 않는다 — 다음 렌더부터 GPU).
        /// </summary>
        public static string KernelFile(FfmpegTool tool)
        {
            if (tool == null || !Enabled)
                return null;
            lock (Gate)
            {
                if (Results.TryGetValue(tool.FfmpegPath, out var path))
                    return path != null && File.Exists(path) ? path : null;
                if (!Pending.ContainsKey(tool.FfmpegPath))
                    Pending[tool.FfmpegPath] = Task.Run(() => Probe(tool));
                return null;
            }
        }

        /// <summary>알아보기가 끝날 때까지 기다린 뒤 결과(시험·시작 때 미리 알아 둘 때).</summary>
        public static async Task<string> KernelFileAsync(FfmpegTool tool)
        {
            if (tool == null || !Enabled)
                return null;
            Task pending;
            lock (Gate)
            {
                if (Results.TryGetValue(tool.FfmpegPath, out var path))
                    return path;
                if (!Pending.TryGetValue(tool.FfmpegPath, out pending))
                    Pending[tool.FfmpegPath] = pending = Task.Run(() => Probe(tool));
            }
            await pending.ConfigureAwait(false);
            return KernelFile(tool);
        }

        private static async Task Probe(FfmpegTool tool)
        {
            string result = null;
            string status;
            try
            {
                result = await TryKernels(tool).ConfigureAwait(false);
                status = result != null ? "GPU 전환: 사용(OpenCL)" : "GPU 전환: 쓸 수 없음(CPU로 그림)";
            }
            catch (Exception ex)
            {
                status = "GPU 전환: 쓸 수 없음(CPU로 그림) — " + ex.Message;
            }
            lock (Gate)
            {
                Results[tool.FfmpegPath] = result;
                Pending.Remove(tool.FfmpegPath);
                Status = status;
            }
        }

        private static string FirstLine(string text)
        {
            var line = (text ?? "").Trim().Split('\n')[0].Trim();
            return line.Length > 200 ? line.Substring(0, 200) + "…" : line.Length == 0 ? "FFmpeg가 실패했습니다" : line;
        }

        private static async Task<string> TryKernels(FfmpegTool tool)
        {
            var source = Transitions.KernelSource;
            var kernel = Regex.Match(source, @"__kernel\s+void\s+([A-Za-z][A-Za-z0-9_]*)");
            if (!kernel.Success || tool.Filters == null || !tool.Filters.Contains("xfade_opencl") || !tool.Filters.Contains("hwupload"))
                return null;
            // 내용이 같으면 같은 이름(여러 번 쓰지 않게). 이 프로세스의 임시 폴더라 끝나면 정리된다
            string hash;
            using (var sha = SHA256.Create())
                hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(source))).Substring(0, 12).ToLowerInvariant();
            var path = Path.Combine(RenderEnvironment.TempFolder(), "transitions-" + hash + ".cl");
            if (!File.Exists(path))
                File.WriteAllText(path, source, new UTF8Encoding(false));
            var script = Path.Combine(RenderEnvironment.TempFolder(), "gpu-probe-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(script,
                "[0]format=rgba,hwupload[a];[1]format=rgba,hwupload[b];[a][b]xfade_opencl=transition=custom:source=" + RenderGraph.FilterValue(path)
                + ":kernel=" + kernel.Groups[1].Value + ":duration=0.2:offset=0.1,hwdownload,format=rgba[vout]",
                new UTF8Encoding(false));
            try
            {
                var args = new List<string>
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-init_hw_device", "opencl=gpu", "-filter_hw_device", "gpu",
                    "-f", "lavfi", "-i", "color=c=black:s=64x36:r=10:d=0.5", "-f", "lavfi", "-i", "color=c=white:s=64x36:r=10:d=0.5"
                };
                args.AddRange(tool.FilterScriptArgs(script));
                args.AddRange(new[] { "-map", "[vout]", "-f", "null", "-" });
                var run = await FfmpegRunner.RunText(tool.FfmpegPath, args, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                if (run.ExitCode != 0)
                    throw new InvalidOperationException(FirstLine(run.Error));
                return path;
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }
    }
}
