using Folderss.Plugins;
using System.Windows;
using VideoEditor.Ui;

namespace VideoEditor
{
    /// <summary>
    /// 플러그인 진입점. plugin.json의 "type"(네임스페이스.클래스)과 이름이 같아야 한다.
    /// 실행할 때마다 새 편집 창(EditorView)을 만든다 — 창마다 프로젝트가 따로다. FFmpeg 정보·썸네일 캐시는 함께 쓴다.
    /// </summary>
    public sealed class VideoEditorPlugin : IFolderssPlugin
    {
        private EditorServices _services;

        public void Initialize(IPluginManager manager)
        {
            _services = new EditorServices(manager);
            manager.AddSettingsPage(new EditorSettingsPage(_services));
        }

        public FrameworkElement CreateView()
        {
            return new EditorView(_services);
        }
    }
}
