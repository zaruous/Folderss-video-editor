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
    /// 인물 분리 모델(ONNX) 실행: 모델 크기 RGB 프레임 → 사람일 정도(0~1). CPU로만 돌린다(GPU 드라이버·추가 런타임 없이).
    /// 한 번에 한 프레임 — 만드는 쪽(<see cref="PersonMaskService"/>)이 한 스레드에서 차례로 부른다.
    /// </summary>
    public sealed class PersonSegmenter : IDisposable
    {
        private readonly SessionOptions _options;
        private readonly InferenceSession _session;
        private readonly RunOptions _run = new RunOptions();
        private readonly string _input;
        private readonly string _output;
        private float[] _tensor = Array.Empty<float>();

        private PersonSegmenter(string modelPath)
        {
            _options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Threads(Environment.ProcessorCount),
                InterOpNumThreads = 1
            };
            try
            {
                // 프레임 사이(파이프 읽기·쓰기)에 추론 스레드가 헛돌며 CPU를 잡지 않게 — FFmpeg·편집기 화면과 나눠 쓴다(속도는 같음)
                _options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                _session = new InferenceSession(modelPath, _options);
                _input = _session.InputMetadata.Keys.First();
                _output = _session.OutputMetadata.Keys.First();
            }
            catch
            {
                _session?.Dispose();
                _options.Dispose();
                _run.Dispose();
                throw;
            }
        }

        /// <summary>모델을 연다. ONNX Runtime을 불러오지 못하거나 모델이 깨졌으면 무엇을 할지 알리는 예외.</summary>
        public static PersonSegmenter Open(string modelPath)
        {
            OnnxRuntimeLibrary.Ensure();
            try
            {
                return new PersonSegmenter(modelPath);
            }
            catch (OnnxRuntimeException ex)
            {
                throw new InvalidDataException("인물 분리 모델을 열지 못했습니다(파일이 깨졌을 수 있습니다): " + ex.Message, ex);
            }
        }

        /// <summary>
        /// 추론 스레드 수: 물리 코어 수쯤(논리 코어의 절반, 1~8). 논리 코어를 다 쓰면 하이퍼스레딩끼리 다퉈 오히려 느리다
        /// (6코어 12스레드에서 448×256 한 장: 6스레드 약 64ms, 12스레드 120ms 넘음).
        /// </summary>
        public static int Threads(int processors)
        {
            return Math.Clamp(processors / 2, 1, 8);
        }

        /// <summary>rgb(모델 크기 rgb24) → alpha(너비×높이, 0~1).</summary>
        public void Run(byte[] rgb, int width, int height, float[] alpha)
        {
            var size = width * height * 3;
            if (_tensor.Length != size)
                _tensor = new float[size];
            PersonMaskText.FillInput(rgb, _tensor, width, height);
            using (var input = OrtValue.CreateTensorValueFromMemory(_tensor, new long[] { 1, 3, height, width }))
            using (var results = _session.Run(_run, new[] { _input }, new[] { input }, new[] { _output }))
            {
                var output = results[0].GetTensorDataAsSpan<float>();
                if (output.Length < width * height)
                    throw new InvalidDataException("인물 분리 모델의 출력 크기가 맞지 않습니다.");
                output.Slice(0, width * height).CopyTo(alpha);
            }
        }

        public void Dispose()
        {
            _session.Dispose();
            _options.Dispose();
            _run.Dispose();
        }
    }

    /// <summary>
    /// ONNX Runtime 네이티브 DLL(onnxruntime.dll) 불러오기. 플러그인 zip의 runtimes\win-x64\native\에 든 것을 경로로 먼저 불러 두고
    /// ONNX Runtime 관리 DLL의 DllImport가 그것을 쓰게 한다 — Windows 폴더(System32)에 다른 버전의 onnxruntime.dll이 있어도
    /// 그것을 잡지 않는다(버전이 다르면 API가 맞지 않아 멈춘다). 그다음에 원격 측정(Windows TraceLogging)을 끈다.
    /// </summary>
    public static class OnnxRuntimeLibrary
    {
        private static readonly object Gate = new object();
        private static IntPtr _handle;

        public const string MissingVcRuntime = "인물 분리 엔진(ONNX Runtime)을 불러오지 못했습니다. Microsoft Visual C++ 재배포 패키지(x64)가 필요합니다 — "
                                               + "설치한 뒤 다시 시도하세요: " + SpeechText.VcRedistUrl;

        /// <summary>불러온 onnxruntime.dll 경로(아직이면 null).</summary>
        public static string LoadedPath { get; private set; }

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
        public static List<string> Candidates(string managedFolder, string envFolder)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(managedFolder))
            {
                list.Add(Path.Combine(managedFolder, "runtimes", RuntimeId, "native", FileName));
                list.Add(Path.Combine(managedFolder, FileName));
            }
            if (!string.IsNullOrWhiteSpace(envFolder))
                list.Add(Path.Combine(envFolder, FileName));
            return list;
        }

        /// <summary>네이티브 DLL을 불러 두고(한 번만) DllImport가 그것을 쓰게 한다. 못 찾거나 못 부르면 InvalidOperationException.</summary>
        public static void Ensure()
        {
            lock (Gate)
            {
                if (_handle != IntPtr.Zero)
                    return;
                var managed = typeof(InferenceSession).Assembly;
                var path = Candidates(Path.GetDirectoryName(managed.Location), Environment.GetEnvironmentVariable(PersonMaskText.EnvFolder)).FirstOrDefault(File.Exists);
                if (path == null)
                    throw new InvalidOperationException("이 PC(" + RuntimeId + ")에서 쓸 인물 분리 엔진(" + FileName + ")이 플러그인에 없습니다. Windows x64에서만 쓸 수 있습니다.");
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
            }
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            var bare = Path.GetFileNameWithoutExtension(name);
            return string.Equals(bare, "onnxruntime", StringComparison.OrdinalIgnoreCase) || string.Equals(bare, "libonnxruntime", StringComparison.OrdinalIgnoreCase)
                ? _handle : IntPtr.Zero;
        }
    }
}
