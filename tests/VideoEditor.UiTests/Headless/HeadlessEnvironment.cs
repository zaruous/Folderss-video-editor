using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

// 시험들이 UI 스레드·Application 리소스(테마)·플러그인 하나를 같이 쓰므로 한 번에 하나씩 돌린다
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VideoEditor.UiTests.Headless
{
    /// <summary>
    /// 헤드리스 시험의 실행 조건. 사용자 화면에 창이 뜨면 안 되므로 <b>숨은 데스크톱</b>(이름이 Default가 아닌 데스크톱)에서 돈다 —
    /// scripts/ui-test.ps1이 CreateDesktop으로 만든 데스크톱에서 dotnet test를 실행한다(SwitchDesktop은 하지 않아 사용자 화면은 그대로).
    /// 디버거가 붙어 있으면(Visual Studio 테스트 탐색기의 [디버그]) 사람이 보고 있는 것이므로 보이는 화면에서도 돈다.
    /// </summary>
    internal static class HeadlessEnvironment
    {
        private const int UoiName = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);

        /// <summary>이 스레드가 붙은 데스크톱 이름("Default"면 사용자 화면).</summary>
        public static string DesktopName()
        {
            var buffer = new StringBuilder(256);
            int needed;
            return GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UoiName, buffer, buffer.Capacity * 2, out needed) ? buffer.ToString() : "";
        }

        public static bool OnHiddenDesktop
        {
            get
            {
                var name = DesktopName();
                return name.Length > 0 && !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>디버거로 실행 중(보이는 화면에 창을 띄우고, 디버깅용 시험은 창을 닫을 때까지 기다린다).</summary>
        public static bool Debugging { get { return Debugger.IsAttached; } }

        /// <summary>시험할 플러그인 zip(FOLDERSS_UI_PLUGIN_ZIP, 없으면 시험 프로젝트와 같은 구성으로 빌드한 플러그인의 zip).</summary>
        public static string PluginZip
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("FOLDERSS_UI_PLUGIN_ZIP");
                if (!string.IsNullOrEmpty(path))
                    return path;
                return typeof(HeadlessEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Where(a => a.Key == "PluginZip").Select(a => a.Value).FirstOrDefault() ?? "";
            }
        }

        /// <summary>시험이 쓰는 폴더(플러그인을 푼 곳·데이터·캡처). FOLDERSS_UI_WORK, 없으면 임시 폴더\folderss-uitest\&lt;zip 이름&gt;.</summary>
        public static string WorkFolder
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("FOLDERSS_UI_WORK");
                if (string.IsNullOrEmpty(path))
                    path = Path.Combine(Path.GetTempPath(), "folderss-uitest", Path.GetFileNameWithoutExtension(PluginZip));
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }

    /// <summary>숨은 데스크톱이거나 디버거가 붙어 있고, 플러그인 zip이 있을 때만 도는 시험.</summary>
    public sealed class UiFactAttribute : FactAttribute
    {
        public UiFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "WPF 화면 시험은 Windows에서만 돕니다.";
            else if (!HeadlessEnvironment.OnHiddenDesktop && !HeadlessEnvironment.Debugging)
                Skip = "헤드리스 시험은 숨은 데스크톱(scripts/ui-test.ps1)이나 디버거로만 돕니다. 지금 데스크톱: " + HeadlessEnvironment.DesktopName();
            else if (!File.Exists(HeadlessEnvironment.PluginZip))
                Skip = "플러그인 zip이 없습니다: " + HeadlessEnvironment.PluginZip + " (시험 프로젝트를 빌드하면 만들어집니다)";
        }
    }
}
