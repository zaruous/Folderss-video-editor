using System;
using System.Collections.Generic;
using System.Windows;

namespace Folderss.Plugins
{
    /// <summary>
    /// 플러그인 진입점. <c>plugin.json</c>의 <c>type</c>에 적은 클래스가 구현하며, 인수 없는 public 생성자가 있어야 한다.
    /// </summary>
    public interface IFolderssPlugin
    {
        /// <summary>
        /// 플러그인을 처음 로드했을 때 한 번 호출된다. 설정 탭이 필요하면 여기서
        /// <see cref="IPluginManager.AddSettingsPage"/>로 등록한다.
        /// </summary>
        void Initialize(IPluginManager manager);

        /// <summary>⋯ 메뉴 > 플러그인에서 선택할 때마다 호출된다. 반환한 요소가 팝업 창의 내용이 된다.</summary>
        FrameworkElement CreateView();
    }

    /// <summary>본체가 플러그인에 제공하는 기능. 플러그인마다 별도 인스턴스이며 설정은 플러그인 ID별로 분리된다.</summary>
    public interface IPluginManager
    {
        /// <summary>plugin.json의 id.</summary>
        string PluginId { get; }

        /// <summary>압축을 푼 플러그인 폴더(읽기 전용으로 취급). 리소스 파일 위치로 쓴다.</summary>
        string PluginDirectory { get; }

        /// <summary>플러그인이 자유롭게 쓸 수 있는 데이터 폴더. 없으면 만들어 준다.</summary>
        string DataDirectory { get; }

        /// <summary>플러그인 설정 값. 없으면 null.</summary>
        string GetSetting(string key);

        /// <summary>플러그인 설정 값을 바로 파일에 저장한다. value가 null이면 지운다. 저장 실패는 예외로 던진다.</summary>
        void SetSetting(string key, string value);

        /// <summary>플러그인 설정 전체(복사본).</summary>
        IReadOnlyDictionary<string, string> GetAllSettings();

        /// <summary>
        /// 본체(Folderss) 설정의 읽기 전용 복사본. 호출할 때마다 저장된 값을 새로 읽는다. 다른 플러그인의 설정은 들어 있지 않다.
        /// 키: theme, git.executablePath, git.baseFolderMode, git.pullMode, git.scanDepth, git.excludedFolders(줄바꿈 구분),
        /// git.logLimit, git.logAllBranches, diff.ignoreWhitespace, diff.fallbackEncoding, diff.viewMode, diff.toolMode,
        /// diff.toolPath, diff.toolArguments, console.preferredProfileKey, console.fontSize.
        /// 값은 문자열이며 bool은 "true"/"false", 선택 값은 이름(예: "FastForwardOnly")이다.
        /// </summary>
        IReadOnlyDictionary<string, string> GetAppSettings();

        /// <summary>본체와 같은 폴더 패널을 새로 만든다. 파일을 열면 메인 창의 뷰어 탭으로 열린다.</summary>
        IFolderPanel CreateFolderPanel(string path);

        /// <summary>설정 창에 탭을 추가한다. <see cref="IFolderssPlugin.Initialize"/>에서 호출한다.</summary>
        void AddSettingsPage(IPluginSettingsPage page);
    }

    /// <summary>플러그인 팝업에 넣을 수 있는 폴더 패널.</summary>
    public interface IFolderPanel
    {
        /// <summary>화면에 붙일 요소.</summary>
        FrameworkElement View { get; }

        string CurrentPath { get; }

        /// <summary>선택한 항목의 전체 경로.</summary>
        IReadOnlyList<string> SelectedPaths { get; }

        void NavigateTo(string path);

        event EventHandler PathChanged;
    }

    /// <summary>설정 창에 추가되는 플러그인 탭.</summary>
    public interface IPluginSettingsPage
    {
        /// <summary>설정 창 왼쪽 목록에 보일 이름.</summary>
        string Title { get; }

        /// <summary>설정 창을 열 때마다 호출된다. 매번 새 요소를 반환해야 한다(이전 창의 요소를 재사용하면 안 됨).</summary>
        FrameworkElement CreateView();

        /// <summary>설정 창에서 저장을 누르면 호출된다. 실패는 예외로 던지면 설정 저장 실패 메시지에 모인다.</summary>
        void Save();
    }
}
