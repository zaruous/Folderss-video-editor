@echo off
setlocal EnableExtensions
for /f "tokens=2 delims=:" %%a in ('chcp') do set "OLDCP=%%a"
set "OLDCP=%OLDCP: =%"
set "OLDCP=%OLDCP:.=%"
chcp 65001 >nul
rem (위 세 줄) 원래 콘솔 코드 페이지를 적어 두었다가 끝날 때 되돌린다 — 이 창을 연 셸에도 남으므로
rem ─────────────────────────────────────────────────────────────────────────────
rem 개발용 호스트(tools\DevHost)로 이 플러그인 하나만 띄운다 — Folderss를 설치하거나 실행하지 않아도 된다.
rem 빌드(플러그인 zip + DevHost) 뒤 DevHost.exe가 zip을 Folderss와 같은 방식으로 불러 팝업 창에 담는다.
rem 플러그인 설정·데이터는 실제 Folderss와 따로 LOCALAPPDATA\Folderss-devhost\(플러그인 id) 폴더에 남는다.
rem
rem 사용: run-host.bat [Debug 또는 Release] [--wait] [--theme 이름] [--data 폴더] [--fresh] [플러그인.zip]
rem                    [--headless [--shot 파일.png] [--settle 초] [--timeout 초]]
rem   --theme    Black(기본) Light Catppuccin Dracula GitHub Nord Solarized
rem   --data     플러그인 데이터 폴더를 직접 고른다
rem   --fresh    빈 데이터 폴더로 시작(첫 실행 흉내)
rem   --wait     창을 닫을 때까지 기다린다(종료 코드를 받을 때)
rem   --headless 사용자 화면에 띄우지 않는다(숨은 데스크톱). 화면이 자리 잡으면 스스로 끝나고 결과를 여기에 쓴다
rem              종료 코드 0 성공 · 1 플러그인 UI 오류 · 2 불러오기 실패 · 3 시간 초과(모달 창 등)
rem   --shot     (헤드리스) 플러그인 화면을 PNG로 찍는다
rem   --settle   (헤드리스) 창이 뜬 뒤 더 기다릴 초(기본 2 — 미리 보기 등 늦게 그려지는 것)
rem   --timeout  (헤드리스) 이보다 오래 걸리면 끝낸다(기본 60초)
rem 예: run-host.bat --headless --fresh --theme Light --shot shots\light.png
rem ─────────────────────────────────────────────────────────────────────────────
set "ROOT=%~dp0.."
set "CONFIG=Debug"
set "WAIT="
set "ARGS="
set "CODE=0"

:parse
if "%~1"=="" goto build
if /i "%~1"=="Debug"   (set "CONFIG=Debug"   & shift & goto parse)
if /i "%~1"=="Release" (set "CONFIG=Release" & shift & goto parse)
if /i "%~1"=="--wait"  (set "WAIT=1"         & shift & goto parse)
rem 헤드리스는 결과(종료 코드·기록)를 받아야 하므로 끝날 때까지 기다린다
if /i "%~1"=="--headless" set "WAIT=1"
set ARGS=%ARGS% %1
shift
goto parse

:build
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [오류] dotnet을 찾지 못했습니다. .NET 8 SDK를 설치하세요.
    goto fail
)
echo [빌드] 플러그인 + DevHost (%CONFIG%)
dotnet build "%ROOT%\tools\DevHost\DevHost.csproj" -c %CONFIG% --nologo -v q -clp:NoSummary
if errorlevel 1 (
    echo [오류] 빌드에 실패했습니다.
    goto fail
)

set "EXE=%ROOT%\tools\DevHost\bin\%CONFIG%\net8.0-windows\DevHost.exe"
if not exist "%EXE%" (
    echo [오류] DevHost.exe가 없습니다: %EXE%
    goto fail
)

echo [실행] DevHost.exe%ARGS%
if not defined WAIT goto detach
"%EXE%"%ARGS%
set "CODE=%errorlevel%"
goto end

:detach
start "" "%EXE%"%ARGS%
goto end

:fail
set "CODE=1"
rem 탐색기에서 두 번 눌러 실행했으면 창이 바로 닫히지 않게
echo %cmdcmdline% | find /i "%~0" >nul && pause

:end
if defined OLDCP chcp %OLDCP% >nul
exit /b %CODE%
