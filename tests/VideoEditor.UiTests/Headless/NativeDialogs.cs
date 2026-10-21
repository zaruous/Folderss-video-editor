using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace VideoEditor.UiTests.Headless
{
    /// <summary>
    /// Windows 기본 파일 대화상자(WPF가 아닌 #32770 창)는 WPF 트리에 없으므로 Win32로 채운다.
    /// UI 자동화 클라이언트는 숨은 데스크톱의 창을 보지 못해(RootElement는 입력 데스크톱 기준) 쓰지 않는다.
    /// UI 스레드가 만든 창만 본다(EnumThreadWindows) — 다른 프로세스·사용자 화면은 건드리지 않는다. 시험 스레드에서 부른다.
    /// </summary>
    internal static class NativeDialogs
    {
        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_COMMAND = 0x0111;
        private const int IDOK = 1;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>열기 대화상자가 뜨면 파일 이름에 path를 넣고 [열기]를 누른다. 대화상자 제목을 돌려준다.</summary>
        public static Task<string> Open(UiThread ui, string path, TimeSpan timeout)
        {
            return Fill(ui, path, timeout);
        }

        /// <summary>저장 대화상자가 뜨면 파일 이름에 path를 넣고 [저장]을 누른다. 대화상자 제목을 돌려준다.</summary>
        public static Task<string> SaveAs(UiThread ui, string path, TimeSpan timeout)
        {
            return Fill(ui, path, timeout);
        }

        private static async Task<string> Fill(UiThread ui, string path, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            IntPtr dialog;
            while ((dialog = ThreadWindows(ui.NativeThreadId).FirstOrDefault(h => ClassOf(h) == "#32770" && IsWindowVisible(h))) == IntPtr.Zero)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("파일 대화상자가 " + timeout.TotalSeconds + "초 안에 뜨지 않았습니다. UI 스레드의 창: "
                        + string.Join(" · ", ThreadWindows(ui.NativeThreadId).Select(h => ClassOf(h) + " '" + TextOf(h) + "'" + (IsWindowVisible(h) ? "" : "(숨김)"))));
                await Task.Delay(150);
            }
            // 대화상자가 안을 다 만들 때까지 잠깐(공용 항목 대화상자는 DirectUI 안에 편집 칸을 늦게 만든다)
            IntPtr edit = IntPtr.Zero;
            while ((edit = FileNameEdit(dialog)) == IntPtr.Zero)
            {
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException("파일 대화상자에서 파일 이름 칸을 찾지 못했습니다: " + Describe(dialog));
                await Task.Delay(100);
            }
            var title = TextOf(dialog);
            SendMessageW(edit, WM_SETTEXT, IntPtr.Zero, path);
            await Task.Delay(100);
            var ok = Children(dialog).FirstOrDefault(h => GetDlgCtrlID(h) == IDOK && ClassOf(h) == "Button");
            PostMessageW(dialog, WM_COMMAND, (IntPtr)IDOK, ok);
            return title;
        }

        /// <summary>파일 이름 칸: 저장은 콤보(1001)·열기는 콤보(1148) 안의 Edit. 없으면 보이는 첫 Edit.</summary>
        private static IntPtr FileNameEdit(IntPtr dialog)
        {
            var edits = Children(dialog).Where(h => ClassOf(h) == "Edit").ToList();
            foreach (var edit in edits)
            {
                for (var parent = GetParent(edit); parent != IntPtr.Zero && parent != dialog; parent = GetParent(parent))
                {
                    var id = GetDlgCtrlID(parent);
                    if (id == 1148 || id == 1001)
                        return edit;
                }
                var own = GetDlgCtrlID(edit);
                if (own == 1148 || own == 1001)
                    return edit;
            }
            return edits.FirstOrDefault(IsWindowVisible);
        }

        private static List<IntPtr> ThreadWindows(uint threadId)
        {
            var found = new List<IntPtr>();
            EnumThreadWindows(threadId, (h, l) =>
            {
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static List<IntPtr> Children(IntPtr parent)
        {
            var found = new List<IntPtr>();
            EnumChildWindows(parent, (h, l) =>
            {
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string ClassOf(IntPtr hWnd)
        {
            var name = new StringBuilder(256);
            GetClassNameW(hWnd, name, name.Capacity);
            return name.ToString();
        }

        private static string TextOf(IntPtr hWnd)
        {
            var text = new StringBuilder(512);
            GetWindowTextW(hWnd, text, text.Capacity);
            return text.ToString();
        }

        private static string Describe(IntPtr dialog)
        {
            return string.Join(" · ", Children(dialog).Take(60).Select(h => ClassOf(h) + "#" + GetDlgCtrlID(h)));
        }
    }
}
