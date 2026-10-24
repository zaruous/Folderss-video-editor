using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DevHost
{
    /// <summary>
    /// 헤드리스 실행(--headless): 사용자 화면에 창을 띄우지 않고 플러그인을 불러 그려 본다.
    /// 부모(보이는 데스크톱)는 숨은 데스크톱을 만들어 자신을 그 안에서 다시 실행하고 기다린다 — 화면 시험(scripts/ui-test.ps1)과 같은 방식으로,
    /// SwitchDesktop은 하지 않아 사용자 화면·마우스·포커스를 건드리지 않는다. 자식은 화면이 자리 잡으면 (--shot이면 PNG로 찍고) 스스로 끝낸다.
    /// 종료 코드: 0 성공, 1 플러그인 UI 오류가 있었음, 2 불러오기 실패, 3 시간 초과.
    /// </summary>
    internal static class Headless
    {
        public const int Ok = 0;
        public const int PluginErrors = 1;
        public const int LoadFailed = 2;
        public const int TimedOut = 3;

        private const int UoiName = 2;
        private const uint GenericAll = 0x10000000;
        private const uint WaitTimeout = 0x102;
        private const int StdOutputHandle = -11;
        private const uint FileTypeDisk = 1;
        private const uint FileTypePipe = 3;

        /// <summary>이 스레드가 숨은 데스크톱(이름이 Default가 아닌 데스크톱)에 붙어 있는지.</summary>
        public static bool OnHiddenDesktop
        {
            get
            {
                var buffer = new StringBuilder(256);
                if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UoiName, buffer, buffer.Capacity * 2, out _))
                    return false;
                var name = buffer.ToString();
                return name.Length > 0 && !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>부모: 숨은 데스크톱에서 같은 명령줄(+ --log)로 자신을 실행하고, 자식의 기록을 콘솔에 옮기고 종료 코드를 돌려준다.</summary>
        public static int RunOnHiddenDesktop(string[] args)
        {
            // 창 프로그램(WinExe)이라 콘솔이 없다 — 표준 출력이 파일·파이프로 넘어왔으면 거기에, 아니면 띄운 콘솔(cmd·bat)에 붙어 쓴다
            var output = GetStdHandle(StdOutputHandle);
            var type = output == IntPtr.Zero || output == new IntPtr(-1) ? 0 : GetFileType(output);
            if (type == FileTypeDisk || type == FileTypePipe)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            else
                AttachConsole(-1);
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[헤드리스] " + ex.Message);
                return LoadFailed;
            }
            var log = Path.Combine(Path.GetTempPath(), "folderss-devhost-" + Guid.NewGuid().ToString("N") + ".log");
            var commandLine = Environment.CommandLine + " --log \"" + log + "\"";
            var desktopName = "folderss-devhost-" + Environment.ProcessId;
            Console.WriteLine("[헤드리스] 숨은 데스크톱 '" + desktopName + "'에서 실행 — 최대 " + options.TimeoutSeconds + "초");
            int code;
            var desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
            if (desktop == IntPtr.Zero)
            {
                Console.WriteLine("[헤드리스] 숨은 데스크톱을 만들지 못했습니다: " + Marshal.GetLastWin32Error());
                return LoadFailed;
            }
            try
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = desktopName };
                if (!CreateProcessW(Environment.ProcessPath, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref startup, out var process))
                {
                    Console.WriteLine("[헤드리스] DevHost를 띄우지 못했습니다: " + Marshal.GetLastWin32Error());
                    return LoadFailed;
                }
                try
                {
                    if (WaitForSingleObject(process.hProcess, (uint)(options.TimeoutSeconds * 1000)) == WaitTimeout)
                    {
                        // 숨은 데스크톱이라 사람이 닫을 수 없는 모달 창 등에서 멈춘 것 — 끝낸다
                        TerminateProcess(process.hProcess, TimedOut);
                        WaitForSingleObject(process.hProcess, 5000);
                        code = TimedOut;
                    }
                    else
                    {
                        GetExitCodeProcess(process.hProcess, out var exit);
                        code = (int)exit;
                    }
                }
                finally
                {
                    CloseHandle(process.hThread);
                    CloseHandle(process.hProcess);
                }
            }
            finally
            {
                CloseDesktop(desktop);
            }
            if (File.Exists(log))
            {
                foreach (var line in File.ReadAllLines(log, Encoding.UTF8))
                    Console.WriteLine(line);
                TryDelete(log);
            }
            Console.WriteLine("[헤드리스] " + Describe(code) + " (종료 코드 " + code + ")");
            return code;
        }

        private static string Describe(int code)
        {
            switch (code)
            {
                case Ok:
                    return "성공";
                case PluginErrors:
                    return "플러그인 UI 오류가 있었습니다";
                case LoadFailed:
                    return "플러그인을 불러오지 못했습니다";
                case TimedOut:
                    return "시간 초과 — 사람이 닫아야 하는 창(모달)에서 멈췄을 수 있습니다";
                default:
                    return "알 수 없는 종료";
            }
        }

        /// <summary>자식: 화면이 자리 잡을 때까지(유휴 + settle) 기다렸다가 다른 창을 적고, --shot이면 플러그인 화면을 PNG로 찍고 창을 닫는다.</summary>
        public static void FinishWhenSettled(Window window, FrameworkElement view, Options options, Action<string> log)
        {
            window.Loaded += (s, e) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, window.Dispatcher) { Interval = TimeSpan.FromSeconds(options.SettleSeconds) };
                timer.Tick += (_, __) =>
                {
                    timer.Stop();
                    foreach (var other in Application.Current.Windows.OfType<Window>().Where(w => w != window && w.IsVisible))
                        log("열린 창: " + other.Title);
                    if (options.Shot != null)
                    {
                        try
                        {
                            Capture(window, view, options.Shot);
                            log("캡처: " + options.Shot + " (" + (int)Math.Ceiling(view.ActualWidth) + "×" + (int)Math.Ceiling(view.ActualHeight) + ")");
                        }
                        catch (Exception ex)
                        {
                            log("캡처하지 못했습니다: " + ex.Message);
                        }
                    }
                    window.Close();
                };
                timer.Start();
            }));
        }

        /// <summary>플러그인 화면(CreateView가 준 요소)을 창 배경 위에 그려 PNG로(96 DPI). 요소의 창 안 위치와 상관없이 왼쪽 위부터.</summary>
        private static void Capture(Window window, FrameworkElement view, string path)
        {
            var width = Math.Max(1, (int)Math.Ceiling(view.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(view.ActualHeight));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new Rect(0, 0, width, height);
                context.DrawRectangle(window.Background, null, area);
                context.DrawRectangle(new VisualBrush(view), null, area);
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(path))
                encoder.Save(stream);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr attributes);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, int flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, int code);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr handle);
    }
}
