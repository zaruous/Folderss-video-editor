using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Folderss.Plugins;

namespace VideoEditor.UiTests.Headless
{
    /// <summary>
    /// 시험할 플러그인(프로세스에 하나 — Folderss처럼 한 번 로드). zip을 풀어 Folderss와 같은 <see cref="PluginLoadContext"/>로 부르고
    /// <see cref="IFolderssPlugin.Initialize"/>에 시험용 <see cref="TestPluginManager"/>를 준다. 플러그인 형식은 다른 로드 컨텍스트에 있으므로
    /// 시험은 WPF 기본 형식·이름·AutomationId로만 화면을 찾는다(내부 형식을 직접 쓰지 않음 — Folderss 안과 같은 조건).
    /// </summary>
    internal sealed class PluginUnderTest
    {
        private static PluginUnderTest _instance;
        private static readonly object Gate = new object();

        private PluginUnderTest(UiThread ui)
        {
            Root = Path.Combine(HeadlessEnvironment.WorkFolder, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            PluginDirectory = Path.Combine(Root, "plugin");
            ZipFile.ExtractToDirectory(HeadlessEnvironment.PluginZip, PluginDirectory);
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginDirectory, "plugin.json"))))
            {
                Id = manifest.RootElement.GetProperty("id").GetString();
                Title = manifest.RootElement.GetProperty("name").GetString();
                var assemblyPath = Path.Combine(PluginDirectory, manifest.RootElement.GetProperty("assembly").GetString());
                Context = new PluginLoadContext(Id, assemblyPath);
                Assembly = Context.LoadFromAssemblyPath(assemblyPath);
                var type = Assembly.GetType(manifest.RootElement.GetProperty("type").GetString(), true);
                Manager = new TestPluginManager(Id, PluginDirectory, Path.Combine(Root, "plugin-data"));
                Plugin = ui.Run(() =>
                {
                    var plugin = (IFolderssPlugin)Activator.CreateInstance(type);
                    plugin.Initialize(Manager);
                    return plugin;
                });
            }
            ShotFolder = Path.Combine(Root, "shots");
            Directory.CreateDirectory(ShotFolder);
        }

        public static PluginUnderTest Load(UiThread ui)
        {
            lock (Gate)
                return _instance ?? (_instance = new PluginUnderTest(ui));
        }

        public string Id { get; }
        /// <summary>plugin.json의 name(Folderss 메뉴 이름이자 팝업 창 제목).</summary>
        public string Title { get; }
        /// <summary>이번 실행의 작업 폴더(플러그인·데이터·캡처가 이 아래). 시험 대상 파일도 여기 만든다.</summary>
        public string Root { get; }
        public string PluginDirectory { get; }
        public string ShotFolder { get; }
        public PluginLoadContext Context { get; }
        public Assembly Assembly { get; }
        public IFolderssPlugin Plugin { get; }
        public TestPluginManager Manager { get; }

        /// <summary>
        /// ⋯ 메뉴 > 플러그인으로 연 것처럼 <see cref="IFolderssPlugin.CreateView"/>를 팝업 창에 담아 보인다(UI 스레드에서 부른다).
        /// 크기·제목·테마 연결은 Folderss PluginManager.ShowPluginWindow와 같다. 숨은 데스크톱이면 사용자 화면에는 보이지 않는다.
        /// </summary>
        public Window OpenView(double width = 900, double height = 600)
        {
            var view = Plugin.CreateView();
            if (view == null)
                throw new InvalidOperationException("CreateView()가 null을 반환했습니다.");
            var window = new Window
            {
                Title = Title,
                Width = width,
                Height = height,
                MinWidth = 320,
                MinHeight = 240,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = view
            };
            if (HeadlessEnvironment.Debugging)
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = 0;
                window.Top = 0;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
            }
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            window.Show();
            return window;
        }

        /// <summary>테마를 바꾼다(색 사전 + 플러그인이 GetAppSettings로 읽는 "theme"). UI 스레드에서 부른다. 이름은 <see cref="UiThread.Themes"/>.</summary>
        public void ApplyTheme(string name)
        {
            UiThread.ApplyTheme(name);
            Manager.AppSettings["theme"] = name;
        }
    }

    /// <summary>Folderss PluginManager.PluginLoadContext와 같다: 계약은 기본 컨텍스트에서, 나머지·네이티브 DLL은 플러그인의 .deps.json으로.</summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string ContractName = typeof(IFolderssPlugin).Assembly.GetName().Name;
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginId, string assemblyPath) : base("plugin:" + pluginId)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        /// <summary>이 컨텍스트가 불러온 네이티브 DLL 경로(zip에 넣은 네이티브 DLL이 실제로 쓰이는지 시험이 볼 때).</summary>
        public List<string> NativeLoads { get; } = new List<string>();

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, ContractName, StringComparison.OrdinalIgnoreCase))
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path == null)
                return IntPtr.Zero;
            lock (NativeLoads)
                NativeLoads.Add(path);
            return LoadUnmanagedDllFromPath(path);
        }
    }

    /// <summary>
    /// 시험용 <see cref="IPluginManager"/>: 설정은 데이터 폴더의 settings.json(Folderss와 같은 평평한 문자열 사전), 본체 설정은 <see cref="AppSettings"/>,
    /// 폴더 패널은 <see cref="TestFolderPanel"/>, 설정 탭은 <see cref="SettingsPages"/>에 모아 둔다. 시험은 OpenView 전에 설정을 넣어 둘 수 있다.
    /// </summary>
    internal sealed class TestPluginManager : IPluginManager
    {
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public TestPluginManager(string id, string pluginDirectory, string dataDirectory)
        {
            PluginId = id;
            PluginDirectory = pluginDirectory;
            DataDirectory = dataDirectory;
            Directory.CreateDirectory(dataDirectory);
        }

        public string PluginId { get; }
        public string PluginDirectory { get; }
        public string DataDirectory { get; }
        /// <summary><see cref="GetAppSettings"/>가 돌려주는 본체 설정. 키는 계약의 GetAppSettings 주석(theme, git.*, diff.*, console.*) — 시험에 필요한 것만 넣는다.</summary>
        public Dictionary<string, string> AppSettings { get; } = new Dictionary<string, string> { { "theme", "Black" } };
        public List<IPluginSettingsPage> SettingsPages { get; } = new List<IPluginSettingsPage>();
        public List<TestFolderPanel> FolderPanels { get; } = new List<TestFolderPanel>();

        public string GetSetting(string key)
        {
            lock (_gate)
            {
                string value;
                return _settings.TryGetValue(key, out value) ? value : null;
            }
        }

        public void SetSetting(string key, string value)
        {
            lock (_gate)
            {
                if (value == null)
                    _settings.Remove(key);
                else
                    _settings[key] = value;
                File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public IReadOnlyDictionary<string, string> GetAllSettings()
        {
            lock (_gate)
                return new Dictionary<string, string>(_settings);
        }

        public IReadOnlyDictionary<string, string> GetAppSettings()
        {
            return new Dictionary<string, string>(AppSettings);
        }

        public IFolderPanel CreateFolderPanel(string path)
        {
            var panel = new TestFolderPanel(path);
            FolderPanels.Add(panel);
            return panel;
        }

        public void AddSettingsPage(IPluginSettingsPage page)
        {
            SettingsPages.Add(page);
        }
    }

    /// <summary>
    /// 시험용 폴더 패널: 폴더 안 항목 이름을 목록(AutomationId FolderPanelList)으로만 보인다 — Folderss 폴더 패널의 모양·기능(열기·메뉴 등)은 없다.
    /// 시험은 <see cref="NavigateTo"/>나 목록 선택으로 플러그인 쪽 처리(PathChanged·SelectedPaths)를 돌린다. UI 스레드에서 쓴다.
    /// </summary>
    internal sealed class TestFolderPanel : IFolderPanel
    {
        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Extended };

        public TestFolderPanel(string path)
        {
            System.Windows.Automation.AutomationProperties.SetAutomationId(_list, "FolderPanelList");
            Load(path);
        }

        public FrameworkElement View { get { return _list; } }

        public string CurrentPath { get; private set; }

        public IReadOnlyList<string> SelectedPaths
        {
            get { return _list.SelectedItems.Cast<ListBoxItem>().Select(item => (string)item.Tag).ToList(); }
        }

        public void NavigateTo(string path)
        {
            Load(path);
            PathChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler PathChanged;

        private void Load(string path)
        {
            CurrentPath = path;
            _list.Items.Clear();
            if (!Directory.Exists(path))
                return;
            foreach (var entry in Directory.EnumerateDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                         .Concat(Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)))
                _list.Items.Add(new ListBoxItem { Content = Path.GetFileName(entry), Tag = entry });
        }
    }
}
