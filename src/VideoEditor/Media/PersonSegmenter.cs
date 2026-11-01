using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using VideoEditor.Core;

namespace VideoEditor.Media
{
    /// <summary>
    /// 인물 분리 모델(ONNX) 실행: 모델 크기 RGB 프레임 → 사람일 정도(0~1).
    /// GPU(DirectML)를 먼저 쓰고, GPU가 없거나 DirectML을 불러오지 못하거나 세션 만들기·추론이 실패하면 CPU로 돌린다(같은 마스크 — CPU 길은 예전과 같다).
    /// 한 번에 한 프레임 — 만드는 쪽(<see cref="PersonMaskService"/>)이 한 스레드에서 차례로 부른다.
    /// </summary>
    public sealed class PersonSegmenter : IDisposable
    {
        private readonly string _modelPath;
        private readonly RunOptions _run = new RunOptions();
        private SessionOptions _options;
        private InferenceSession _session;
        private string _input;
        private string _output;
        private float[] _tensor = Array.Empty<float>();

        /// <summary>시험용: GPU 길을 일부러 실패시킨다(열 때 또는 첫 추론 때). 평소에는 None.</summary>
        internal static GpuFault SimulatedGpuFault { get; set; }

        internal enum GpuFault { None, Open, Run }

        /// <summary>시험이 일부러 낸 GPU 실패(ONNX Runtime 예외와 같이 다룬다).</summary>
        private sealed class SimulatedGpuException : Exception
        {
            public SimulatedGpuException(string message) : base(message)
            {
            }
        }

        private PersonSegmenter(string modelPath)
        {
            _modelPath = modelPath;
        }

        /// <summary>지금 GPU(DirectML)로 돌리는지.</summary>
        public bool OnGpu { get; private set; }

        /// <summary>보일 처리 장치: "GPU(DirectML · 어댑터 이름)" 또는 "CPU(n스레드)".</summary>
        public string Device { get; private set; }

        /// <summary>GPU를 쓰려 했는데 CPU로 돌아간 까닭(GPU를 끄라고 했거나 GPU로 돌면 null).</summary>
        public string Fallback { get; private set; }

        /// <summary>
        /// 모델을 연다. useGpu면 GPU(DirectML)를 먼저 해 보고 안 되면 CPU로 연다(<see cref="Fallback"/>에 까닭).
        /// ONNX Runtime을 불러오지 못하거나 모델이 깨졌으면 무엇을 할지 알리는 예외.
        /// </summary>
        public static PersonSegmenter Open(string modelPath, bool useGpu = true)
        {
            OnnxRuntimeLibrary.Ensure();
            var segmenter = new PersonSegmenter(modelPath);
            try
            {
                string fallback = null;
                if (useGpu)
                {
                    fallback = OnnxRuntimeLibrary.GpuUnavailable;
                    if (fallback == null)
                    {
                        try
                        {
                            if (SimulatedGpuFault == GpuFault.Open)
                                throw new SimulatedGpuException("시험: GPU 열기 실패");
                            segmenter.Start(true);
                            return segmenter;
                        }
                        catch (Exception ex) when (!(ex is OutOfMemoryException))
                        {
                            fallback = "GPU(DirectML)로 열지 못했습니다: " + ex.Message;
                        }
                    }
                }
                try
                {
                    segmenter.Start(false);
                }
                catch (OnnxRuntimeException ex)
                {
                    throw new InvalidDataException("인물 분리 모델을 열지 못했습니다(파일이 깨졌을 수 있습니다): " + ex.Message, ex);
                }
                segmenter.Fallback = fallback;
                return segmenter;
            }
            catch
            {
                segmenter.Dispose();
                throw;
            }
        }

        /// <summary>세션을 (다시) 만든다. 실패하면 만들던 것을 치우고 예외.</summary>
        private void Start(bool gpu)
        {
            CloseSession();
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Threads(Environment.ProcessorCount),
                InterOpNumThreads = 1,
                // 모양 연산을 CPU에 두었다는 경고 같은 것은 오류 창(stderr)에 남기지 않는다
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };
            InferenceSession session = null;
            try
            {
                // 프레임 사이(파이프 읽기·쓰기)에 추론 스레드가 헛돌며 CPU를 잡지 않게 — FFmpeg·편집기 화면과 나눠 쓴다(속도는 같음)
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                if (gpu)
                {
                    // DirectML은 메모리 패턴 재사용·병렬 실행을 지원하지 않는다. 0번 어댑터 = Windows가 먼저 고르는 GPU
                    options.EnableMemoryPattern = false;
                    options.AppendExecutionProvider_DML(0);
                }
                session = new InferenceSession(_modelPath, options);
                _input = session.InputMetadata.Keys.First();
                _output = session.OutputMetadata.Keys.First();
            }
            catch
            {
                session?.Dispose();
                options.Dispose();
                throw;
            }
            _options = options;
            _session = session;
            OnGpu = gpu;
            Device = gpu ? "GPU(DirectML" + (OnnxRuntimeLibrary.GpuName != null ? " · " + OnnxRuntimeLibrary.GpuName : "") + ")"
                         : "CPU(" + options.IntraOpNumThreads + "스레드)";
        }

        /// <summary>
        /// 추론 스레드 수: 물리 코어 수쯤(논리 코어의 절반, 1~8). 논리 코어를 다 쓰면 하이퍼스레딩끼리 다퉈 오히려 느리다
        /// (6코어 12스레드에서 448×256 한 장: 6스레드 약 64ms, 12스레드 120ms 넘음).
        /// </summary>
        public static int Threads(int processors)
        {
            return Math.Clamp(processors / 2, 1, 8);
        }

        /// <summary>
        /// rgb(모델 크기 rgb24) → alpha(너비×높이, 0~1). GPU 추론이 실패하면 CPU 세션으로 바꿔 같은 프레임을 다시 돌린다(<see cref="Fallback"/>에 까닭) —
        /// 앞 프레임까지 만든 것은 그대로 쓴다.
        /// </summary>
        public void Run(byte[] rgb, int width, int height, float[] alpha)
        {
            var size = width * height * 3;
            if (_tensor.Length != size)
                _tensor = new float[size];
            PersonMaskText.FillInput(rgb, _tensor, width, height);
            if (OnGpu)
            {
                try
                {
                    if (SimulatedGpuFault == GpuFault.Run)
                        throw new SimulatedGpuException("시험: GPU 추론 실패");
                    Infer(width, height, alpha);
                    return;
                }
                catch (Exception ex) when (ex is OnnxRuntimeException || ex is SimulatedGpuException)
                {
                    Fallback = "GPU(DirectML) 추론이 실패해 CPU로 이어 만듭니다: " + ex.Message;
                    Start(false);
                }
            }
            Infer(width, height, alpha);
        }

        private void Infer(int width, int height, float[] alpha)
        {
            using (var input = OrtValue.CreateTensorValueFromMemory(_tensor, new long[] { 1, 3, height, width }))
            using (var results = _session.Run(_run, new[] { _input }, new[] { input }, new[] { _output }))
            {
                var output = results[0].GetTensorDataAsSpan<float>();
                if (output.Length < width * height)
                    throw new InvalidDataException("인물 분리 모델의 출력 크기가 맞지 않습니다.");
                output.Slice(0, width * height).CopyTo(alpha);
            }
        }

        private void CloseSession()
        {
            _session?.Dispose();
            _options?.Dispose();
            _session = null;
            _options = null;
        }

        public void Dispose()
        {
            CloseSession();
            _run.Dispose();
        }
    }

    /// <summary>
    /// ONNX Runtime 네이티브 DLL(onnxruntime.dll — DirectML 판) 불러오기. 플러그인 zip의 runtimes\win-x64\native\에 든 것을 경로로 먼저 불러 두고
    /// ONNX Runtime 관리 DLL의 DllImport가 그것을 쓰게 한다 — Windows 폴더(System32)에 다른 버전의 onnxruntime.dll이 있어도
    /// 그것을 잡지 않는다(버전이 다르면 API가 맞지 않아 멈춘다). 그다음에 원격 측정(Windows TraceLogging)을 끈다.
    /// 같은 폴더의 DirectML.dll도 경로로 먼저 불러 둔다 — onnxruntime.dll은 이름으로 찾으므로 System32의 옛 DirectML.dll을 잡지 않게.
    /// </summary>
    public static class OnnxRuntimeLibrary
    {
        private static readonly object Gate = new object();
        private static IntPtr _handle;

        public const string MissingVcRuntime = "인물 분리 엔진(ONNX Runtime)을 불러오지 못했습니다. Microsoft Visual C++ 재배포 패키지(x64)가 필요합니다 — "
                                               + "설치한 뒤 다시 시도하세요: " + SpeechText.VcRedistUrl;

        /// <summary>DirectML 네이티브 DLL 이름.</summary>
        public const string DirectMLFileName = "DirectML.dll";

        /// <summary>불러온 onnxruntime.dll 경로(아직이면 null).</summary>
        public static string LoadedPath { get; private set; }

        /// <summary>불러온 DirectML.dll 경로(못 불렀거나 아직이면 null).</summary>
        public static string DirectMLPath { get; private set; }

        /// <summary>GPU(DirectML)를 쓸 수 없는 까닭(쓸 수 있거나 아직 <see cref="Ensure"/> 전이면 null).</summary>
        public static string GpuUnavailable { get; private set; }

        /// <summary>DirectML이 쓸 GPU 이름(0번 어댑터). 모르면 null.</summary>
        public static string GpuName { get; private set; }

        /// <summary>이 프로세스의 네이티브 DLL 이름(Windows onnxruntime.dll, Linux libonnxruntime.so, macOS libonnxruntime.dylib).</summary>
        public static string FileName
        {
            get { return OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so"; }
        }

        /// <summary>NuGet 런타임 식별자(win-x64 등) — .deps.json이 네이티브 DLL을 두는 runtimes\&lt;이것&gt;\native.</summary>
        public static string RuntimeId
        {
            get
            {
                var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
                return os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// 찾을 곳(순서대로): 관리 DLL 옆 runtimes\&lt;RID&gt;\native(플러그인 zip·빌드 출력), 관리 DLL 옆, FOLDERSS_VE_PERSONMASK 폴더(시험).
        /// </summary>
        public static List<string> Candidates(string managedFolder, string envFolder, string runtimeId = null)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(managedFolder))
            {
                list.Add(Path.Combine(managedFolder, "runtimes", runtimeId ?? RuntimeId, "native", FileName));
                list.Add(Path.Combine(managedFolder, FileName));
            }
            if (!string.IsNullOrWhiteSpace(envFolder))
                list.Add(Path.Combine(envFolder, FileName));
            return list;
        }

        /// <summary>이 PC용 엔진이 플러그인에 없을 때 알림(플러그인 zip에는 Windows x64 것만 든다 — ARM64 Windows의 ARM64 프로세스 등).</summary>
        public static string NotIncluded(string runtimeId)
        {
            return "이 PC(" + runtimeId + ")에서 쓸 인물 분리 엔진(ONNX Runtime)이 플러그인에 들어 있지 않습니다 — 인물 마스크는 Windows x64에서만 만들 수 있습니다.";
        }

        /// <summary>이 PC용 엔진 DLL이 있으면 null, 없으면 <see cref="NotIncluded"/>. 모델을 내려받기 전에 묻는다(불러오지는 않음).</summary>
        public static string Unsupported()
        {
            if (_handle != IntPtr.Zero)
                return null;
            return Find() != null ? null : NotIncluded(RuntimeId);
        }

        private static string Find()
        {
            var managed = typeof(InferenceSession).Assembly;
            return Candidates(Path.GetDirectoryName(managed.Location), Environment.GetEnvironmentVariable(PersonMaskText.EnvFolder)).FirstOrDefault(File.Exists);
        }

        /// <summary>네이티브 DLL을 불러 두고(한 번만) DllImport가 그것을 쓰게 한다. 못 찾거나 못 부르면 InvalidOperationException.</summary>
        public static void Ensure()
        {
            lock (Gate)
            {
                if (_handle != IntPtr.Zero)
                    return;
                var managed = typeof(InferenceSession).Assembly;
                var path = Find();
                if (path == null)
                    throw new InvalidOperationException(NotIncluded(RuntimeId));
                IntPtr handle;
                try
                {
                    handle = NativeLibrary.Load(path);
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is BadImageFormatException)
                {
                    throw new InvalidOperationException(OperatingSystem.IsWindows() ? MissingVcRuntime : "인물 분리 엔진을 불러오지 못했습니다: " + ex.Message, ex);
                }
                _handle = handle;
                LoadedPath = path;
                try
                {
                    NativeLibrary.SetDllImportResolver(managed, Resolve);
                }
                catch (InvalidOperationException)
                {
                    // 이미 누가 정해 두었다 — 같은 경로를 먼저 불러 두었으니 로드 컨텍스트가 찾는 것도 이것이다
                }
                try
                {
                    OrtEnv.Instance().DisableTelemetryEvents();
                }
                catch (OnnxRuntimeException)
                {
                    // 원격 측정을 지원하지 않는 빌드
                }
                GpuUnavailable = LoadDirectML(Path.GetDirectoryName(path));
            }
        }

        /// <summary>onnxruntime.dll 옆의 DirectML.dll을 불러 둔다. 쓸 수 있으면 null, 아니면 까닭.</summary>
        private static string LoadDirectML(string folder)
        {
            if (!OperatingSystem.IsWindows())
                return "GPU(DirectML)는 Windows에서만 씁니다.";
            var path = Path.Combine(folder, DirectMLFileName);
            if (!File.Exists(path))
                return "플러그인에 DirectML.dll이 없어 GPU를 쓰지 못합니다.";
            if (!NativeLibrary.TryLoad(path, out _))
                return "DirectML.dll을 불러오지 못해 GPU를 쓰지 못합니다(DirectX 12를 지원하는 GPU·드라이버가 필요합니다).";
            DirectMLPath = path;
            var adapter = DxgiAdapter.First();
            if (adapter == null)
                return null; // 어댑터를 읽지 못했다 — DirectML에 맡긴다(못 쓰면 세션 만들기가 실패해 CPU로)
            if (adapter.Value.Software)
                return "GPU가 없어(소프트웨어 그래픽만 있음) CPU로 만듭니다.";
            GpuName = adapter.Value.Name;
            return null;
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            var bare = Path.GetFileNameWithoutExtension(name);
            return string.Equals(bare, "onnxruntime", StringComparison.OrdinalIgnoreCase) || string.Equals(bare, "libonnxruntime", StringComparison.OrdinalIgnoreCase)
                ? _handle : IntPtr.Zero;
        }
    }

    /// <summary>DXGI로 0번 그래픽 어댑터(DirectML이 쓸 것)의 이름과 소프트웨어(GPU 없음) 여부를 읽는다. 읽지 못하면 null.</summary>
    internal static class DxgiAdapter
    {
        private const uint SoftwareFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

        public static (string Name, bool Software)? First()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out var factoryObject) != 0 || !(factoryObject is IDXGIFactory1 factory))
                    return null;
                try
                {
                    if (factory.EnumAdapters1(0, out var adapter) != 0 || adapter == null)
                        return null;
                    try
                    {
                        if (adapter.GetDesc1(out var desc) != 0)
                            return null;
                        return (desc.Description, (desc.Flags & SoftwareFlag) != 0);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is COMException || ex is InvalidCastException)
            {
                return null;
            }
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        // vtable 순서대로(IDXGIObject → IDXGIFactory → IDXGIFactory1). 쓰지 않는 것은 자리만 채운다
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumAdapters();
            void MakeWindowAssociation();
            void GetWindowAssociation();
            void CreateSwapChain();
            void CreateSoftwareAdapter();
            [PreserveSig]
            int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumOutputs();
            void GetDesc();
            void CheckInterfaceSupport();
            [PreserveSig]
            int GetDesc1(out AdapterDesc1 desc);
        }
    }
}
