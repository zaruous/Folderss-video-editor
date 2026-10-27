using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using FFmpeg.AutoGen;
using VideoEditor.Media;

namespace VideoEditor.Helper
{
    /// <summary>
    /// 미리 보기 도우미 프로세스. 사용: VideoEditor.Helper.exe --ffmpeg &lt;DLL 폴더&gt; [--parent &lt;pid&gt;]
    /// 표준 입력으로 요청을 받고 표준 출력으로 응답한다(<see cref="HelperProtocol"/>). 재생 흐름은 플러그인이 만든 명명 파이프에 쓴다.
    /// 부모(Folderss)가 끝나거나 표준 입력이 닫히면 함께 끝난다.
    /// </summary>
    internal static class Program
    {
        private static readonly object WriteGate = new object();
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> Jobs = new ConcurrentDictionary<int, CancellationTokenSource>();
        private static readonly InputPool Pool = new InputPool();
        private static Stream _output;

        private static int Main(string[] args)
        {
            string ffmpegFolder = null;
            var parent = 0;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--ffmpeg")
                    ffmpegFolder = args[++i];
                else if (args[i] == "--parent")
                    int.TryParse(args[++i], out parent);
            }
            if (string.IsNullOrEmpty(ffmpegFolder) || !Directory.Exists(ffmpegFolder))
            {
                Console.Error.WriteLine("사용: VideoEditor.Helper.exe --ffmpeg <FFmpeg DLL 폴더> [--parent <pid>]");
                return 2;
            }
            var input = Console.OpenStandardInput();
            _output = Console.OpenStandardOutput();
            try
            {
                if (OperatingSystem.IsWindows())
                    Native.SetDllDirectoryW(ffmpegFolder);
                ffmpeg.RootPath = ffmpegFolder;
                Log.Install();
                // 필요한 DLL을 지금 다 불러 본다 — 맞지 않는 빌드면 준비 응답 대신 오류로 끝난다
                var version = ffmpeg.av_version_info();
                ffmpeg.avcodec_version();
                ffmpeg.avformat_version();
                ffmpeg.avfilter_version();
                ffmpeg.swscale_version();
                ffmpeg.swresample_version();
                Respond(0, HelperProtocol.KindReady, JsonSerializer.Serialize(new { version, protocol = HelperProtocol.Version, pid = Environment.ProcessId }));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FFmpeg를 불러오지 못했습니다: " + ex);
                return 3;
            }
            if (parent > 0)
                WatchParent(parent);
            // 쓰지 않은 지 오래된 파일은 닫아 둔다(파일을 잡아 두지 않게)
            using (new Timer(_ => Pool.EvictIdle(TimeSpan.FromSeconds(10)), null, 5000, 5000))
            {
                while (true)
                {
                    HelperRequest request;
                    try
                    {
                        request = HelperProtocol.ReadRequest(input);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("요청을 읽지 못했습니다: " + ex.Message);
                        break;
                    }
                    if (request == null || request.Op == HelperProtocol.OpQuit)
                        break;
                    switch (request.Op)
                    {
                        case HelperProtocol.OpFrame:
                        case HelperProtocol.OpStream:
                            StartJob(request);
                            break;
                        case HelperProtocol.OpStop:
                            CancellationTokenSource job;
                            if (Jobs.TryGetValue(request.Id, out job))
                                job.Cancel();
                            break;
                        default:
                            Respond(request.Id, HelperProtocol.KindError, "모르는 요청입니다: " + request.Op);
                            break;
                    }
                }
            }
            foreach (var job in Jobs.Values)
                job.Cancel();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!Jobs.IsEmpty && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            return 0;
        }

        private static void StartJob(HelperRequest request)
        {
            var cancel = new CancellationTokenSource();
            Jobs[request.Id] = cancel;
            var thread = new Thread(() => RunJob(request, cancel.Token)) { IsBackground = true, Name = "job-" + request.Id };
            thread.Start();
        }

        private static void RunJob(HelperRequest request, CancellationToken cancellation)
        {
            try
            {
                if (request.Plan == null)
                    throw new InvalidOperationException("계획이 없습니다.");
                if (request.Op == HelperProtocol.OpFrame)
                {
                    byte[] result = null;
                    if (request.Plan.MaxFrames <= 0)
                        request.Plan.MaxFrames = 1;
                    using (var run = new PlanRun(Pool, request.Plan))
                    {
                        run.Execute(bytes => { if (result == null) result = bytes; }, cancellation);
                        if (result == null)
                            throw new InvalidOperationException("그린 화면이 없습니다.");
                        Respond(request.Id, HelperProtocol.KindFrame, HelperProtocol.FramePayload(run.Width, run.Height, result));
                    }
                    return;
                }
                if (string.IsNullOrEmpty(request.Pipe))
                    throw new InvalidOperationException("파이프 이름이 없습니다.");
                using (var pipe = new NamedPipeClientStream(".", request.Pipe, PipeDirection.Out, PipeOptions.None))
                {
                    pipe.Connect(5000);
                    using (var run = new PlanRun(Pool, request.Plan))
                    {
                        try
                        {
                            run.Execute(bytes => pipe.Write(bytes, 0, bytes.Length), cancellation);
                            pipe.Flush();
                        }
                        catch (IOException)
                        {
                            // 플러그인이 파이프를 닫음(멈춤) — 끝난 것으로
                        }
                    }
                }
                Respond(request.Id, HelperProtocol.KindDone, Array.Empty<byte>());
            }
            catch (OperationCanceledException)
            {
                Respond(request.Id, HelperProtocol.KindDone, Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                Respond(request.Id, HelperProtocol.KindError, ex.Message);
            }
            finally
            {
                CancellationTokenSource removed;
                Jobs.TryRemove(request.Id, out removed);
                removed?.Dispose();
            }
        }

        private static void Respond(int id, byte kind, byte[] payload)
        {
            lock (WriteGate)
                HelperProtocol.WriteResponse(_output, id, kind, payload);
        }

        private static void Respond(int id, byte kind, string text)
        {
            lock (WriteGate)
                HelperProtocol.WriteResponse(_output, id, kind, text);
        }

        /// <summary>부모 프로세스가 끝나면 같이 끝난다(작업 개체가 없을 때의 보조).</summary>
        private static void WatchParent(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                var thread = new Thread(() =>
                {
                    try
                    {
                        process.WaitForExit();
                    }
                    catch (Exception)
                    {
                        // 기다리지 못하면 표준 입력이 닫힐 때 끝난다
                    }
                    Environment.Exit(0);
                }) { IsBackground = true, Name = "watch-parent" };
                thread.Start();
            }
            catch (Exception)
            {
                // 부모를 못 찾으면 표준 입력이 닫힐 때 끝난다
            }
        }
    }
}
