using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Media
{
    /// <summary>
    /// 미리 보기 도우미 프로세스(플러그인 zip의 helper\VideoEditor.Helper.exe)와의 연결. 편집기를 열 때 한 번 띄워 두고,
    /// 재생 헤드 이동(<see cref="Frame"/>)과 재생(<see cref="Stream"/>)을 ffmpeg.exe를 새로 띄우지 않고 그린다.
    /// 도우미가 죽으면(<see cref="IsAlive"/> false) 부르는 쪽이 ffmpeg.exe 방식으로 돌아가고 <see cref="Died"/>로 다시 띄운다.
    /// FFmpeg 공유 DLL(shared 빌드)이 있는 폴더에서만 쓸 수 있다(<see cref="HasLibraries"/>).
    /// </summary>
    public sealed class PreviewHelper : IDisposable
    {
        public const string ExeName = "VideoEditor.Helper.exe";
        /// <summary>도우미의 FFmpeg 바인딩(FFmpeg.AutoGen 9.0)이 맞는 FFmpeg 주 버전 — <see cref="Libraries"/>의 DLL 이름이 여기에 묶여 있다.</summary>
        public const int FfmpegMajor = 9;
        /// <summary>도우미의 FFmpeg 바인딩(FFmpeg.AutoGen 9.0)이 맞는 공유 DLL — 다른 주 버전의 DLL이면 쓸 수 없다.</summary>
        public static readonly string[] Libraries = { "avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll", "avfilter-12.dll" };

        private readonly Process _process;
        private readonly Stream _input;
        private readonly ConcurrentDictionary<int, Pending> _pending = new ConcurrentDictionary<int, Pending>();
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();
        private readonly TaskCompletionSource<string> _ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _writeGate = new object();
        private int _nextId;
        private int _disposed;
        private volatile bool _dead;

        private sealed class Pending
        {
            public readonly TaskCompletionSource<byte[]> Frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<string> Done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private PreviewHelper(Process process, string folder)
        {
            _process = process;
            _input = process.StandardInput.BaseStream;
            Folder = folder;
        }

        /// <summary>도우미가 쓰는 FFmpeg DLL 폴더.</summary>
        public string Folder { get; }
        /// <summary>도우미가 불러온 FFmpeg 판(준비된 뒤).</summary>
        public string Version { get; private set; }
        public int ProcessId { get { return _process.Id; } }
        public bool IsAlive { get { return !_dead && _disposed == 0; } }
        /// <summary>도우미가 끝남(죽음·끊김) — 이유 글. 다른 스레드에서 온다.</summary>
        public event Action<string> Died;
        /// <summary>도우미의 기록(표준 오류·L 응답).</summary>
        public event Action<string> Log;

        /// <summary>플러그인 폴더의 도우미 실행 파일(없으면 null).</summary>
        public static string FindExecutable(string pluginDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory) || !OperatingSystem.IsWindows())
                return null;
            var path = Path.Combine(pluginDirectory, "helper", ExeName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>FFmpeg 폴더에 도우미가 쓸 공유 DLL이 다 있는지(ffmpeg.exe만 있는 빌드면 false).</summary>
        public static bool HasLibraries(string ffmpegFolder)
        {
            return !string.IsNullOrEmpty(ffmpegFolder) && OperatingSystem.IsWindows() && Libraries.All(name => File.Exists(Path.Combine(ffmpegFolder, name)));
        }

        /// <summary>도우미를 띄우고 준비될 때까지 기다린다(프로세스 시작이 느린 PC에서는 몇 초). 못 띄우면 예외.</summary>
        public static async Task<PreviewHelper> StartAsync(string exe, string ffmpegFolder, TimeSpan readyTimeout, CancellationToken cancellation)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            info.ArgumentList.Add("--ffmpeg");
            info.ArgumentList.Add(ffmpegFolder);
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            var process = Process.Start(info);
            if (process == null)
                throw new InvalidOperationException("도우미를 실행하지 못했습니다: " + exe);
            JobObject.Add(process);
            var helper = new PreviewHelper(process, ffmpegFolder);
            process.ErrorDataReceived += (s, e) => helper.OnStderr(e.Data);
            process.BeginErrorReadLine();
            new Thread(helper.ReadLoop) { IsBackground = true, Name = "preview-helper" }.Start();
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timer.CancelAfter(readyTimeout);
                using (timer.Token.Register(() => helper._ready.TrySetCanceled()))
                {
                    try
                    {
                        helper.Version = await helper._ready.Task.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var reason = helper.RecentStderr();
                        helper.Dispose();
                        cancellation.ThrowIfCancellationRequested();
                        throw new HelperException("도우미가 준비되지 않았습니다" + (reason.Length > 0 ? ": " + reason : " (" + ex.Message + ")"), true);
                    }
                }
            }
            return helper;
        }

        private void OnStderr(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            _stderr.Enqueue(line.Trim());
            while (_stderr.Count > 8)
                _stderr.TryDequeue(out _);
            Log?.Invoke(line.Trim());
        }

        private string RecentStderr()
        {
            return string.Join(" ", _stderr);
        }

        private void ReadLoop()
        {
            string reason;
            try
            {
                var output = _process.StandardOutput.BaseStream;
                while (true)
                {
                    int id;
                    byte kind;
                    byte[] payload;
                    if (!HelperProtocol.ReadResponse(output, out id, out kind, out payload))
                    {
                        reason = "도우미가 끝났습니다" + (_process.HasExited ? "(종료 코드 " + _process.ExitCode + ")" : "");
                        break;
                    }
                    Dispatch(id, kind, payload);
                }
            }
            catch (Exception ex)
            {
                reason = "도우미 연결이 끊겼습니다: " + ex.Message;
            }
            var stderr = RecentStderr();
            if (stderr.Length > 0)
                reason += " — " + stderr;
            _dead = true;
            _ready.TrySetException(new HelperException(reason, true));
            foreach (var pending in _pending.Values)
            {
                pending.Frame.TrySetException(new HelperException(reason, true));
                pending.Done.TrySetResult(reason);
            }
            _pending.Clear();
            if (_disposed == 0)
                Died?.Invoke(reason);
        }

        private void Dispatch(int id, byte kind, byte[] payload)
        {
            Pending pending;
            switch (kind)
            {
                case HelperProtocol.KindReady:
                    string version = null;
                    try
                    {
                        using (var document = JsonDocument.Parse(payload))
                        {
                            JsonElement element;
                            if (document.RootElement.TryGetProperty("protocol", out element) && element.GetInt32() != HelperProtocol.Version)
                            {
                                _ready.TrySetException(new HelperException("도우미의 메시지 형식이 다릅니다(" + element.GetInt32() + " ≠ " + HelperProtocol.Version + ").", true));
                                return;
                            }
                            if (document.RootElement.TryGetProperty("version", out element))
                                version = element.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                        // 판을 못 읽어도 준비된 것으로
                    }
                    _ready.TrySetResult(version ?? "");
                    break;
                case HelperProtocol.KindFrame:
                    if (_pending.TryRemove(id, out pending))
                    {
                        pending.Frame.TrySetResult(payload);
                        pending.Done.TrySetResult(null);
                    }
                    break;
                case HelperProtocol.KindDone:
                    if (_pending.TryRemove(id, out pending))
                    {
                        pending.Done.TrySetResult(null);
                        pending.Frame.TrySetCanceled();
                    }
                    break;
                case HelperProtocol.KindError:
                    var text = Encoding.UTF8.GetString(payload ?? Array.Empty<byte>());
                    if (_pending.TryRemove(id, out pending))
                    {
                        pending.Frame.TrySetException(new HelperException(text, false));
                        pending.Done.TrySetResult(text);
                    }
                    else
                        Log?.Invoke(text);
                    break;
                case HelperProtocol.KindLog:
                    Log?.Invoke(Encoding.UTF8.GetString(payload ?? Array.Empty<byte>()));
                    break;
            }
        }

        private void Send(HelperRequest request)
        {
            if (!IsAlive)
                throw new HelperException("도우미가 없습니다.", true);
            try
            {
                lock (_writeGate)
                    HelperProtocol.WriteRequest(_input, request);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                _dead = true;
                throw new HelperException("도우미에 보내지 못했습니다: " + ex.Message, true);
            }
        }

        /// <summary>화면 한 장(BGRA, plan.Width×plan.Height). 취소하면 도우미의 작업도 멈춘다.</summary>
        public async Task<byte[]> Frame(HelperPlan plan, CancellationToken cancellation)
        {
            plan.Video = true;
            if (plan.MaxFrames <= 0)
                plan.MaxFrames = 1;
            var id = Interlocked.Increment(ref _nextId);
            var pending = new Pending();
            _pending[id] = pending;
            try
            {
                Send(new HelperRequest { Op = HelperProtocol.OpFrame, Id = id, Plan = plan });
                using (cancellation.Register(() => { Stop(id); pending.Frame.TrySetCanceled(); }))
                {
                    var payload = await pending.Frame.Task.ConfigureAwait(false);
                    if (payload.Length < 8)
                        throw new HelperException("화면 응답이 짧습니다.", false);
                    var width = BitConverter.ToInt32(payload, 0);
                    var height = BitConverter.ToInt32(payload, 4);
                    if (width != plan.Width || height != plan.Height || payload.Length != 8 + width * height * 4)
                        throw new HelperException("화면 크기가 다릅니다: " + width + "×" + height + " (기대 " + plan.Width + "×" + plan.Height + ")", false);
                    var pixels = new byte[payload.Length - 8];
                    Buffer.BlockCopy(payload, 8, pixels, 0, pixels.Length);
                    return pixels;
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// 흐름(재생): 도우미가 명명 파이프로 화면(BGRA 연속) 또는 소리(16비트 스테레오 PCM)를 쓴다. 다 읽거나 멈추면 Dispose.
        /// 파이프는 이 사용자만 열 수 있다.
        /// </summary>
        public async Task<HelperStream> Stream(HelperPlan plan, CancellationToken cancellation)
        {
            var name = "folderss-ve-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            var server = CreateServer(name);
            var id = Interlocked.Increment(ref _nextId);
            var pending = new Pending();
            _pending[id] = pending;
            try
            {
                var connect = server.WaitForConnectionAsync(cancellation);
                Send(new HelperRequest { Op = HelperProtocol.OpStream, Id = id, Plan = plan, Pipe = name });
                // 도우미가 붙거나, 그 전에 오류로 끝나거나
                var finished = await Task.WhenAny(connect, pending.Done.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellation)).ConfigureAwait(false);
                if (finished == connect)
                {
                    await connect.ConfigureAwait(false);
                    return new HelperStream(this, id, server, pending.Done.Task);
                }
                Stop(id);
                server.Dispose();
                if (finished == pending.Done.Task)
                    throw new HelperException(pending.Done.Task.Result ?? "도우미가 흐름을 시작하지 못했습니다.", !IsAlive);
                cancellation.ThrowIfCancellationRequested();
                throw new HelperException("도우미가 파이프에 붙지 않았습니다.", !IsAlive);
            }
            catch (Exception)
            {
                _pending.TryRemove(id, out _);
                server.Dispose();
                throw;
            }
        }

        private static NamedPipeServerStream CreateServer(string name)
        {
            const int bufferSize = 4 << 20;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, bufferSize, 0, security);
                }
                catch (Exception)
                {
                    // 보안 설명자를 못 만들면 기본 권한으로
                }
            }
            return new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, bufferSize, 0);
        }

        /// <summary>작업을 멈추라고 알린다(응답은 기다리지 않음).</summary>
        public void Stop(int id)
        {
            try
            {
                if (IsAlive)
                    Send(new HelperRequest { Op = HelperProtocol.OpStop, Id = id });
            }
            catch (HelperException)
            {
                // 이미 죽었으면 멈출 것도 없다
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                if (!_dead)
                {
                    lock (_writeGate)
                        HelperProtocol.WriteRequest(_input, new HelperRequest { Op = HelperProtocol.OpQuit });
                }
            }
            catch (Exception)
            {
                // 보내지 못하면 끝낸다
            }
            _dead = true;
            try
            {
                if (!_process.WaitForExit(1500))
                    FfmpegRunner.Kill(_process);
            }
            catch (Exception)
            {
                // 이미 끝남
            }
            foreach (var pending in _pending.Values)
            {
                pending.Frame.TrySetException(new HelperException("도우미를 끝냈습니다.", true));
                pending.Done.TrySetResult("도우미를 끝냈습니다.");
            }
            _pending.Clear();
            _process.Dispose();
        }

        internal void Release(int id)
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>도우미가 명명 파이프로 보내는 흐름(화면 BGRA 또는 소리 PCM). Dispose하면 도우미의 작업을 멈추고 파이프를 닫는다.</summary>
    public sealed class HelperStream : IDisposable
    {
        private readonly PreviewHelper _helper;
        private readonly int _id;
        private readonly NamedPipeServerStream _pipe;
        private readonly Task<string> _done;
        private int _disposed;

        internal HelperStream(PreviewHelper helper, int id, NamedPipeServerStream pipe, Task<string> done)
        {
            _helper = helper;
            _id = id;
            _pipe = pipe;
            _done = done;
        }

        public Stream Input { get { return _pipe; } }

        /// <summary>도우미가 보낸 오류(끝난 뒤, 없으면 null).</summary>
        public string Error
        {
            get { return _done.IsCompleted && _done.Status == TaskStatus.RanToCompletion ? _done.Result : null; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _helper.Stop(_id);
            _helper.Release(_id);
            try
            {
                _pipe.Dispose();
            }
            catch (Exception)
            {
                // 이미 닫힘
            }
        }
    }

    /// <summary>도우미 작업이 실패함. <see cref="HelperDied"/>면 도우미 자체가 없어진 것(다시 띄운다), 아니면 그 작업만 실패(그래프 오류 등).</summary>
    public sealed class HelperException : Exception
    {
        public HelperException(string message, bool helperDied) : base(message)
        {
            HelperDied = helperDied;
        }

        public bool HelperDied { get; }
    }
}
