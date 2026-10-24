using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using Folderss.Plugins;

namespace DevHost
{
    /// <summary>
    /// 개발용 호스트: 플러그인 zip 하나를 Folderss와 같은 방식으로 불러 팝업 창(900×600)에 띄운다.
    /// 사용: DevHost.exe [플러그인.zip] [--theme 이름] [--data 폴더] [--fresh] [--headless [--shot 파일.png] [--settle 초] [--timeout 초]]
    /// </summary>
    internal static class Program
    {
        public static readonly string[] Themes = { "Black", "Light", "Catppuccin", "Dracula", "GitHub", "Nord", "Solarized" };

        [STAThread]
        private static int Main(string[] args)
        {
            var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            // 보이는 화면에서 --headless로 불렸으면 숨은 데스크톱에서 다시 띄우고 결과만 알린다
            if (headless && !Headless.OnHiddenDesktop)
                return Headless.RunOnHiddenDesktop(args);

            Action<string> log = line => Console.Error.WriteLine(line);
            var errors = 0;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            try
            {
                var options = Options.Parse(args);
                if (headless)
                {
                    // 숨은 데스크톱에서는 하드웨어 렌더링이 그려지지 않을 수 있다(화면 시험과 같음)
                    System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    if (options.Log != null)
                        log = line => File.AppendAllText(options.Log, line + Environment.NewLine, System.Text.Encoding.UTF8);
                }
                // Folderss App.xaml과 같은 순서: 색 사전 → 컨트롤 스타일
                app.Resources.MergedDictionaries.Add(LoadTheme(options.Theme));
                app.Resources.MergedDictionaries.Add(LoadTheme("Controls"));
                // Folderss처럼 플러그인의 UI 예외는 알리고 계속 실행한다(레이아웃 중 예외는 되풀이되므로 같은 것은 한 번만).
                // 헤드리스에서는 대화상자 대신 기록에 남긴다 — 숨은 데스크톱의 대화상자는 아무도 닫을 수 없다
                var shown = new HashSet<string>();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    e.Handled = true;
                    errors++;
                    if (!shown.Add(e.Exception.GetType().FullName + ": " + e.Exception.Message))
                        return;
                    if (headless)
                        log("플러그인 오류: " + e.Exception);
                    else
                        MessageBox.Show(e.Exception.ToString(), "플러그인 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                };
                var plugin = HostedPlugin.Load(options);
                var window = plugin.CreateWindow();
                if (headless)
                {
                    log("불러옴: " + plugin.Title + " (" + plugin.Manager.PluginId + ") · 테마 " + options.Theme + " · 데이터 " + plugin.Manager.DataDirectory);
                    Headless.FinishWhenSettled(window, plugin.View, options, log);
                }
                var code = app.Run(window);
                return headless && errors > 0 ? Headless.PluginErrors : code;
            }
            catch (Exception ex)
            {
                if (headless)
                {
                    log("불러오지 못했습니다: " + ex);
                    return Headless.LoadFailed;
                }
                MessageBox.Show(ex.ToString(), "DevHost", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        /// <summary>테마를 바꾼다 — Folderss ThemeManager처럼 맨 앞 사전(색)을 갈아 끼운다. DynamicResource로 묶인 색은 바로 따라온다.</summary>
        public static void ApplyTheme(string name)
        {
            Application.Current.Resources.MergedDictionaries[0] = LoadTheme(name);
        }

        /// <summary>Themes 폴더의 사전(색 사전 이름 또는 Controls). csproj가 "Themes.&lt;이름&gt;.xaml"로 넣는다.</summary>
        private static ResourceDictionary LoadTheme(string name)
        {
            var resource = "Themes." + name + ".xaml";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                    throw new InvalidOperationException("테마 리소스가 없습니다: " + resource);
                return (ResourceDictionary)XamlReader.Load(stream);
            }
        }
    }

    internal sealed class Options
    {
        private const string Usage = "사용: DevHost.exe [플러그인.zip] [--theme Black|Light|Catppuccin|Dracula|GitHub|Nord|Solarized] [--data 폴더] [--fresh]"
                                     + " [--headless [--shot 파일.png] [--settle 초(기본 2)] [--timeout 초(기본 60)]]";

        public string Zip { get; private set; }
        public string Theme { get; private set; } = "Black";
        /// <summary>플러그인 데이터 폴더(설정). null이면 %LOCALAPPDATA%\Folderss-devhost\&lt;id&gt;\plugin-data.</summary>
        public string Data { get; private set; }
        /// <summary>빈 데이터 폴더로 시작(첫 실행 흉내 — 이번 실행이 끝나면 다음 시작 때 지운다).</summary>
        public bool Fresh { get; private set; }
        /// <summary>사용자 화면에 띄우지 않고(숨은 데스크톱) 그려 본 뒤 끝낸다.</summary>
        public bool Headless { get; private set; }
        /// <summary>헤드리스: 화면이 자리 잡으면 플러그인 화면을 이 PNG로 찍는다.</summary>
        public string Shot { get; private set; }
        /// <summary>헤드리스: 창이 뜨고 유휴가 된 뒤 더 기다릴 시간(미리 보기 등 비동기로 그리는 것).</summary>
        public double SettleSeconds { get; private set; } = 2;
        /// <summary>헤드리스: 이보다 오래 걸리면(모달 창 등) 끝낸다.</summary>
        public int TimeoutSeconds { get; private set; } = 60;
        /// <summary>헤드리스 자식이 기록을 남길 파일(부모가 정해 넘긴다).</summary>
        public string Log { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--theme":
                        var theme = Value(args, ++i);
                        options.Theme = Program.Themes.FirstOrDefault(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase))
                                        ?? throw new ArgumentException("모르는 테마입니다: " + theme + "\n" + Usage);
                        break;
                    case "--data":
                        options.Data = Path.GetFullPath(Value(args, ++i));
                        break;
                    case "--fresh":
                        options.Fresh = true;
                        break;
                    case "--headless":
                        options.Headless = true;
                        break;
                    case "--shot":
                        options.Shot = Path.GetFullPath(Value(args, ++i));
                        break;
                    case "--settle":
                        options.SettleSeconds = Number(args, ++i, 0, 600);
                        break;
                    case "--timeout":
                        options.TimeoutSeconds = (int)Number(args, ++i, 5, 3600);
                        break;
                    case "--log":
                        options.Log = Path.GetFullPath(Value(args, ++i));
                        break;
                    case "-h":
                    case "--help":
                    case "/?":
                        throw new ArgumentException(Usage);
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal) || options.Zip != null)
                            throw new ArgumentException("모르는 인수입니다: " + args[i] + "\n" + Usage);
                        options.Zip = Path.GetFullPath(args[i]);
                        break;
                }
            }
            options.Zip ??= typeof(Options).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => a.Key == "PluginZip").Select(a => a.Value).FirstOrDefault();
            if (string.IsNullOrEmpty(options.Zip) || !File.Exists(options.Zip))
                throw new FileNotFoundException("플러그인 zip이 없습니다: " + options.Zip + "\n플러그인을 먼저 빌드하세요(scripts\\run-host.bat은 빌드부터 합니다).");
            return options;
        }

        private static string Value(string[] args, int index)
        {
            if (index >= args.Length)
                throw new ArgumentException(args[index - 1] + " 뒤에 값이 없습니다.\n" + Usage);
            return args[index];
        }

        private static double Number(string[] args, int index, double min, double max)
        {
            var text = Value(args, index);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                throw new ArgumentException(args[index - 1] + " 값은 " + min + "~" + max + " 사이의 숫자여야 합니다: " + text + "\n" + Usage);
            return value;
        }
    }

    /// <summary>불러온 플러그인 하나(Folderss PluginManager.TryLoad와 같은 순서: 풀기 → 로드 컨텍스트 → 형식 → Initialize).</summary>
    internal sealed class HostedPlugin
    {
        private HostedPlugin(string title, IFolderssPlugin plugin, DevPluginManager manager)
        {
            Title = title;
            Plugin = plugin;
            Manager = manager;
        }

        public string Title { get; }
        public IFolderssPlugin Plugin { get; }
        public DevPluginManager Manager { get; }
        /// <summary>CreateView가 준 플러그인 화면(<see cref="CreateWindow"/> 뒤).</summary>
        public FrameworkElement View { get; private set; }

        public static HostedPlugin Load(Options options)
        {
            string id, name, assemblyName, typeName;
            using (var archive = ZipFile.OpenRead(options.Zip))
            {
                var entry = archive.GetEntry("plugin.json") ?? throw new InvalidDataException("zip에 plugin.json이 없습니다: " + options.Zip);
                using (var stream = entry.Open())
                using (var manifest = JsonDocument.Parse(stream))
                {
                    id = manifest.RootElement.GetProperty("id").GetString();
                    name = manifest.RootElement.TryGetProperty("name", out var value) ? value.GetString() : id;
                    assemblyName = manifest.RootElement.GetProperty("assembly").GetString();
                    typeName = manifest.RootElement.GetProperty("type").GetString();
                }
            }
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folderss-devhost", FolderName(id));
            // 실행마다 따로 푼다 — 앞 실행이 아직 떠 있으면 그 DLL이 잠겨 있다. 끝난 실행의 폴더는 여기서 지운다
            var runs = Path.Combine(home, "runs");
            RemoveEndedRuns(runs);
            var run = Path.Combine(runs, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(run))
                Directory.Delete(run, true);
            var directory = Path.Combine(run, "plugin");
            ZipFile.ExtractToDirectory(options.Zip, directory);
            var data = options.Data ?? (options.Fresh ? Path.Combine(run, "plugin-data") : Path.Combine(home, "plugin-data"));

            var assemblyPath = Path.Combine(directory, assemblyName);
            var context = new PluginLoadContext(id, assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(typeName, true);
            var plugin = Activator.CreateInstance(type) as IFolderssPlugin
                         ?? throw new InvalidOperationException(typeName + "이 IFolderssPlugin을 구현하지 않습니다. Folderss.PluginContract를 다른 버전으로 참조했을 수 있습니다.");
            var manager = new DevPluginManager(id, directory, data);
            manager.AppSettings["theme"] = options.Theme;
            plugin.Initialize(manager);
            return new HostedPlugin(name, plugin, manager);
        }

        /// <summary>Folderss ShowPluginWindow와 같은 팝업 창 + 위쪽에 호스트 막대(테마·플러그인 설정·데이터 폴더).</summary>
        public Window CreateWindow()
        {
            var view = View = Plugin.CreateView() ?? throw new InvalidOperationException("CreateView()가 null을 반환했습니다.");
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
            var theme = new ComboBox { ItemsSource = Program.Themes, SelectedItem = Manager.AppSettings["theme"], Width = 120, ToolTip = "Folderss 테마" };
            theme.SelectionChanged += (s, e) =>
            {
                var name = (string)theme.SelectedItem;
                Program.ApplyTheme(name);
                Manager.AppSettings["theme"] = name;
            };
            var settings = new Button { Content = "플러그인 설정…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            settings.Click += (s, e) => ShowSettings(Window.GetWindow(settings));
            var data = new Button { Content = "데이터 폴더", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), ToolTip = Manager.DataDirectory };
            data.Click += (s, e) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Manager.DataDirectory } });
            bar.Children.Add(new TextBlock { Text = "DevHost", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            bar.Children.Add(theme);
            bar.Children.Add(settings);
            bar.Children.Add(data);
            var top = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
            top.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            top.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            top.SetResourceReference(TextElement.ForegroundProperty, "SecondaryText");
            DockPanel.SetDock(top, Dock.Top);
            var root = new DockPanel { LastChildFill = true };
            root.Children.Add(top);
            root.Children.Add(view);

            var window = new Window
            {
                Title = Title + " — DevHost",
                Width = 900,
                Height = 600 + 34,
                MinWidth = 320,
                MinHeight = 240,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = root
            };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            return window;
        }

        /// <summary>
        /// Folderss 설정 창(SettingsWindow)과 같은 짜임: 왼쪽 목록(페이지 제목) + 오른쪽 내용(여백 24·16) + 아래 단추 줄.
        /// 기본 TabControl은 테마가 꾸미지 않아 흰 바탕이 되므로 쓰지 않는다. [저장]이면 모든 페이지의 Save.
        /// </summary>
        private void ShowSettings(Window owner)
        {
            if (Manager.SettingsPages.Count == 0)
            {
                MessageBox.Show(owner, "이 플러그인은 설정 화면을 등록하지 않았습니다.", Title);
                return;
            }
            var nav = new ListBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(6, 8, 6, 8) };
            var itemStyle = new Style(typeof(ListBoxItem), (Style)Application.Current.TryFindResource(typeof(ListBoxItem)));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
            itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
            nav.ItemContainerStyle = itemStyle;
            var navBorder = new Border { Child = nav, BorderThickness = new Thickness(0, 0, 1, 0) };
            navBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBackground");
            navBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var content = new Grid();
            Grid.SetColumn(content, 1);
            var panels = new List<Border>();
            foreach (var page in Manager.SettingsPages)
            {
                FrameworkElement view;
                try
                {
                    view = page.CreateView() ?? new TextBlock { Text = "설정 화면이 없습니다 (CreateView()가 null)." };
                }
                catch (Exception ex)
                {
                    view = new TextBlock { Text = "플러그인 설정 화면을 만들지 못했습니다.\n\n" + ex, TextWrapping = TextWrapping.Wrap };
                }
                var panel = new Border { Margin = new Thickness(24, 16, 24, 16), Child = view, Visibility = Visibility.Collapsed };
                panels.Add(panel);
                content.Children.Add(panel);
                nav.Items.Add(new ListBoxItem { Content = new TextBlock { Text = page.Title } });
            }
            nav.SelectionChanged += (s, e) =>
            {
                for (var i = 0; i < panels.Count; i++)
                    panels[i].Visibility = i == nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            };
            nav.SelectedIndex = 0;

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            main.ColumnDefinitions.Add(new ColumnDefinition());
            main.Children.Add(navBorder);
            main.Children.Add(content);

            var save = new Button { Content = "저장", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 80 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            var footer = new Border { Child = buttons, Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(Border.BackgroundProperty, "SurfaceBackground");
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            DockPanel.SetDock(footer, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(footer);
            root.Children.Add(main);

            var dialog = new Window
            {
                Title = Title + " 설정",
                Width = 720,
                Height = 540,
                MinWidth = 580,
                MinHeight = 420,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = false,
                Content = root
            };
            dialog.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            dialog.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            dialog.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            save.Click += (s, e) =>
            {
                try
                {
                    foreach (var page in Manager.SettingsPages)
                        page.Save();
                    dialog.DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, ex.ToString(), "설정을 저장하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            dialog.ShowDialog();
        }

        /// <summary>plugin.json의 id를 폴더 이름으로(경로 문자는 바꾼다).</summary>
        private static string FolderName(string id)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string((id ?? "").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim('.', ' ');
            return name.Length == 0 ? "plugin" : name;
        }

        /// <summary>runs 아래 끝난 실행(숫자 pid 폴더)의 폴더를 지운다. 지우지 못하면(아직 쓰는 중 등) 다음 시작 때.</summary>
        private static void RemoveEndedRuns(string runs)
        {
            if (!Directory.Exists(runs))
                return;
            foreach (var folder in new DirectoryInfo(runs).EnumerateDirectories().ToList())
            {
                if ((folder.Attributes & FileAttributes.ReparsePoint) != 0
                    || !int.TryParse(folder.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    || pid == Environment.ProcessId || IsRunning(pid))
                    continue;
                try
                {
                    folder.Delete(true);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
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
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>
    /// 개발용 <see cref="IPluginManager"/>: 설정은 데이터 폴더의 settings.json(Folderss와 같은 평평한 문자열 사전 — 다음 실행에도 남는다),
    /// 본체 설정은 <see cref="AppSettings"/>(theme만), 설정 탭은 <see cref="SettingsPages"/>에 모은다.
    /// </summary>
    internal sealed class DevPluginManager : IPluginManager
    {
        private readonly Dictionary<string, string> _settings;
        private readonly string _settingsPath;
        private readonly object _gate = new object();

        public DevPluginManager(string id, string pluginDirectory, string dataDirectory)
        {
            PluginId = id;
            PluginDirectory = pluginDirectory;
            DataDirectory = dataDirectory;
            Directory.CreateDirectory(dataDirectory);
            _settingsPath = Path.Combine(dataDirectory, "settings.json");
            _settings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(_settingsPath))
            {
                try
                {
                    foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath)) ?? new Dictionary<string, string>())
                        _settings[pair.Key] = pair.Value;
                }
                catch (JsonException)
                {
                    // 망가진 설정은 버리고 빈 설정으로 시작한다(Folderss와 같음)
                }
            }
        }

        public string PluginId { get; }
        public string PluginDirectory { get; }
        public string DataDirectory { get; }
        public Dictionary<string, string> AppSettings { get; } = new Dictionary<string, string> { { "theme", "Black" } };
        public List<IPluginSettingsPage> SettingsPages { get; } = new List<IPluginSettingsPage>();

        public string GetSetting(string key)
        {
            lock (_gate)
                return _settings.TryGetValue(key, out var value) ? value : null;
        }

        public void SetSetting(string key, string value)
        {
            lock (_gate)
            {
                if (value == null)
                    _settings.Remove(key);
                else
                    _settings[key] = value;
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
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
            return new DevFolderPanel(path);
        }

        public void AddSettingsPage(IPluginSettingsPage page)
        {
            SettingsPages.Add(page);
        }
    }

    /// <summary>개발용 폴더 패널: 폴더 안 항목 이름 목록만(두 번 누르면 하위 폴더로). Folderss 폴더 패널의 모양·메뉴는 없다.</summary>
    internal sealed class DevFolderPanel : IFolderPanel
    {
        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Extended };

        public DevFolderPanel(string path)
        {
            _list.MouseDoubleClick += (s, e) =>
            {
                if (_list.SelectedItem is ListBoxItem item && Directory.Exists((string)item.Tag))
                    NavigateTo((string)item.Tag);
            };
            Load(string.IsNullOrEmpty(path) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : path);
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
            try
            {
                foreach (var entry in Directory.EnumerateDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .Concat(Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)))
                    _list.Items.Add(new ListBoxItem { Content = Path.GetFileName(entry), Tag = entry });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 읽을 수 없는 폴더는 빈 목록
            }
        }
    }
}
