<#
  헤드리스 UI 시험: 플러그인만(Folderss 없이) 숨은 데스크톱에서 띄워 tests/*.UiTests를 돌린다.
  - 시험 프로젝트를 빌드하면 플러그인도 같은 구성으로 빌드되어 zip이 새로 만들어진다(시험 프로젝트 csproj의 PluginName).
  - 숨은 데스크톱은 CreateDesktop으로 만들고 사용자 화면은 바꾸지 않는다(SwitchDesktop 안 함) — 창·마우스·포커스를 건드리지 않는다.
  - 도는 동안 사용자 데스크톱(Default)에 시험 프로세스(testhost)의 창이 하나라도 뜨는지 감시해, 뜨면 실패로 알린다.
  - 캡처(PNG)와 기록은 -Work 폴더(기본: 임시 폴더\folderss-uitest\<플러그인 폴더 이름>) 아래 run-<시각>\shots, ui-test-<시각>.log에 남는다.

  - 시험 하나가 -HangMinutes(기본 5분)를 넘기면 vstest가 시험 프로세스를 끝내고 실패로 알린다(모달 창 등으로 멈춰도 끝없이 기다리지 않게).

  사용: pwsh scripts/ui-test.ps1 [-Filter <dotnet test 필터>] [-Configuration Debug|Release] [-Work <폴더>] [-HangMinutes 5]
#>
param(
    [string]$Filter = '',
    [string]$Configuration = 'Debug',
    [string]$Work = '',
    [int]$HangMinutes = 5
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$projects = @(Get-ChildItem (Join-Path $repo 'tests\*\*.UiTests.csproj'))
if ($projects.Count -ne 1) { throw "tests\*\*.UiTests.csproj가 하나여야 합니다(찾은 개수: $($projects.Count))" }
$project = $projects[0].FullName
if (-not $Work) {
    $manifests = @(Get-ChildItem (Join-Path $repo 'src\*\plugin.json'))
    $name = if ($manifests.Count -eq 1) { $manifests[0].Directory.Name } else { 'plugin' }
    $Work = Join-Path ([IO.Path]::GetTempPath()) "folderss-uitest\$name"
}

dotnet build $project -c $Configuration --nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw '빌드 실패(플러그인 또는 UI 시험 프로젝트)' }

New-Item -ItemType Directory -Force $Work | Out-Null
$env:FOLDERSS_UI_WORK = $Work

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class HiddenDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    /// 숨은 데스크톱에서 명령을 실행하고 끝날 때까지 기다린다. 그동안 사용자 데스크톱(이 스크립트가 있는 곳)에 testhost 창이 보이면 leaks에 남긴다.
    public static int Run(string desktopName, string commandLine, string directory, List<string> leaks)
    {
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("CreateDesktop 실패: " + Marshal.GetLastWin32Error());
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = desktopName };
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, directory, ref si, out pi))
                throw new InvalidOperationException("CreateProcess 실패: " + Marshal.GetLastWin32Error());
            try
            {
                while (WaitForSingleObject(pi.hProcess, 500) != 0)
                {
                    EnumWindows((h, l) =>
                    {
                        if (!IsWindowVisible(h)) return true;
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        try
                        {
                            var name = Process.GetProcessById((int)pid).ProcessName;
                            if (name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
                            {
                                var text = new StringBuilder(256); GetWindowTextW(h, text, 256);
                                var entry = name + ":" + text;
                                if (!leaks.Contains(entry)) leaks.Add(entry);
                            }
                        }
                        catch (ArgumentException) { }
                        return true;
                    }, IntPtr.Zero);
                }
                uint code; GetExitCodeProcess(pi.hProcess, out code);
                return (int)code;
            }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }
        finally { CloseDesktop(desktop); }
    }
}
'@

$log = Join-Path $Work ("ui-test-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$filterArg = if ($Filter) { " --filter `"$Filter`"" } else { '' }
$cmd = "cmd.exe /c dotnet test `"$project`" -c $Configuration --no-build --nologo --logger `"console;verbosity=normal`" --blame-hang-timeout ${HangMinutes}m --blame-hang-dump-type none$filterArg > `"$log`" 2>&1"
$leaks = New-Object 'System.Collections.Generic.List[string]'
$desktopName = 'folderss-uitest-' + $PID
Write-Host "숨은 데스크톱 '$desktopName'에서 실행 — 기록: $log"
$code = [HiddenDesktop]::Run($desktopName, $cmd, $repo, $leaks)
Get-Content $log -Encoding UTF8 | Select-String -Pattern '통과|실패|건너뜀|Passed|Failed|Skipped|error|오류|캡처|\[' | ForEach-Object { $_.Line }
$run = Get-ChildItem $Work -Directory -Filter 'run-*' | Sort-Object Name | Select-Object -Last 1
if ($run) { Write-Host "캡처: $(Join-Path $run.FullName 'shots')" }
if ($leaks.Count -gt 0) {
    Write-Host ("사용자 화면에 시험 창이 보였습니다: " + ($leaks -join ', ')) -ForegroundColor Red
    exit 2
}
Write-Host "사용자 화면에 뜬 시험 창: 없음"
exit $code
