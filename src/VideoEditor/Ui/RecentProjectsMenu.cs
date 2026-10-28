using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 파일 › 최근 프로젝트 열기 ▸ 하위 메뉴: 최근에 열거나 저장한 .fvproj(최근 것부터 10개), 맨 아래 [목록 지우기].
    /// 목록은 플러그인 설정에 두고 하위 메뉴를 열 때마다 다시 읽는다 — 다른 편집 창에서 열거나 저장한 것도 보인다.
    /// 없는 파일은 흐리게 보이고, 누르면 알린 뒤 목록에서 뺄지 묻는다.
    /// </summary>
    internal sealed class RecentProjectsMenu
    {
        public const string SettingKey = "recentProjects";
        private const string Title = "최근 프로젝트 열기";
        private const double MissingOpacity = 0.5;

        // 네트워크 경로의 있는지 확인(몇 초 걸릴 수 있어 뒤에서) — 같은 경로를 겹쳐 확인하지 않게 창들이 함께 쓴다
        private static readonly ConcurrentDictionary<string, Task<bool>> NetworkChecks = new ConcurrentDictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

        private readonly EditorServices _services;
        private readonly EditorCommands _commands;
        private readonly FrameworkElement _owner;

        public RecentProjectsMenu(EditorServices services, EditorCommands commands, FrameworkElement owner)
        {
            _services = services;
            _commands = commands;
            _owner = owner;
            Item = new MenuItem { Header = new TextBlock { Text = Title } };
            AutomationProperties.SetName(Item, Title);
            AutomationProperties.SetAutomationId(Item, "RecentProjectsMenu");
            Item.SubmenuOpened += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, Item))
                    Refresh();
            };
            // 항목이 하나는 있어야 하위 메뉴로 열린다
            Refresh();
        }

        /// <summary>파일 메뉴에 넣을 항목.</summary>
        public MenuItem Item { get; }

        /// <summary>열거나 저장한 프로젝트를 맨 앞에 기록한다(EditorCommands.OpenPath·SaveTo). 복구 파일·저장 안 한 새 프로젝트는 부르지 않는다.</summary>
        public static void Remember(EditorServices services, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // 이상한 경로는 받은 그대로
            }
            services.SaveSetting(SettingKey, RecentProjects.Add(services.Setting(SettingKey), path, DateTime.Now));
        }

        private void Refresh()
        {
            Item.Items.Clear();
            var list = RecentProjects.Read(_services.Setting(SettingKey));
            var labels = RecentProjects.Labels(list.ConvertAll(r => r.Path));
            for (var i = 0; i < list.Count; i++)
            {
                var recent = list[i];
                var item = new MenuItem
                {
                    Header = RecentProjects.MenuHeader(i, labels[i]),
                    ToolTip = recent.Path + "\n마지막으로 연 시각: " + recent.OpenedAt.ToString("yyyy-MM-dd HH:mm")
                };
                AutomationProperties.SetName(item, labels[i]);
                AutomationProperties.SetAutomationId(item, "RecentProject" + i);
                var path = recent.Path;
                item.Click += (s, e) => Open(path);
                CheckExists(item, path);
                Item.Items.Add(item);
            }
            if (list.Count == 0)
            {
                var empty = new MenuItem { Header = new TextBlock { Text = "(최근 프로젝트 없음)" }, IsEnabled = false };
                AutomationProperties.SetAutomationId(empty, "RecentProjectsEmpty");
                Item.Items.Add(empty);
            }
            Item.Items.Add(new Separator());
            var clear = new MenuItem { Header = new TextBlock { Text = "목록 지우기" }, IsEnabled = list.Count > 0 };
            AutomationProperties.SetName(clear, "목록 지우기");
            AutomationProperties.SetAutomationId(clear, "RecentProjectsClear");
            clear.Click += (s, e) => Clear();
            Item.Items.Add(clear);
        }

        /// <summary>없는 파일은 흐리게. 로컬 드라이브는 바로 보고, 네트워크 경로는 뒤에서 확인해 끝나면 흐리게 한다(메뉴를 막지 않음).</summary>
        private void CheckExists(MenuItem item, string path)
        {
            if (!IsNetwork(path))
            {
                if (!File.Exists(path))
                    Dim(item);
                return;
            }
            var check = NetworkChecks.GetOrAdd(path, p => Task.Run(() => File.Exists(p)));
            check.ContinueWith(t =>
            {
                // 끝난 확인은 버린다 — 다음에 메뉴를 열 때 다시 본다
                NetworkChecks.TryRemove(new KeyValuePair<string, Task<bool>>(path, check));
                if (t.Status == TaskStatus.RanToCompletion && !t.Result)
                    item.Dispatcher.BeginInvoke(new Action(() => Dim(item)));
            }, TaskScheduler.Default);
        }

        private static void Dim(MenuItem item)
        {
            item.Opacity = MissingOpacity;
            AutomationProperties.SetItemStatus(item, "파일 없음");
            item.ToolTip = item.ToolTip + "\n(파일을 찾을 수 없습니다)";
        }

        /// <summary>\\서버\공유 또는 연결한 네트워크 드라이브. 드라이브 종류를 모르면 네트워크로 친다(UI 스레드에서 오래 기다리지 않게).</summary>
        private static bool IsNetwork(string path)
        {
            if (RecentProjects.IsUncPath(path))
                return true;
            try
            {
                var root = Path.GetPathRoot(path);
                return string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private async void Open(string path)
        {
            try
            {
                // 네트워크 경로는 뒤에서 확인한다(그동안 편집 창이 멈추지 않게)
                var exists = IsNetwork(path) ? await Task.Run(() => File.Exists(path)) : File.Exists(path);
                if (!exists)
                {
                    var answer = UiKit.Ask(_owner, Title, "프로젝트 파일을 찾을 수 없습니다 — 옮기거나 지웠을 수 있습니다.\n" + path + "\n\n최근 목록에서 뺄까요?",
                        "목록에서 빼기", "그대로 두기");
                    if (answer == 0)
                    {
                        _services.SaveSetting(SettingKey, RecentProjects.Remove(_services.Setting(SettingKey), path));
                        _commands.Status("최근 목록에서 뺐습니다: " + path);
                    }
                    return;
                }
                // [프로젝트 열기…]와 같이 저장 안 한 편집을 먼저 묻는다. 연 경로는 OpenPath가 맨 앞으로 기록한다
                if (!_commands.ConfirmDiscard())
                    return;
                _commands.OpenPath(path);
            }
            catch (Exception ex)
            {
                // async void에서 새는 예외는 Folderss를 끝낼 수 있다
                _commands.Status("최근 프로젝트를 열지 못했습니다: " + ex.Message);
            }
        }

        private void Clear()
        {
            _services.SaveSetting(SettingKey, null);
            _commands.Status("최근 프로젝트 목록을 지웠습니다.");
        }
    }
}
