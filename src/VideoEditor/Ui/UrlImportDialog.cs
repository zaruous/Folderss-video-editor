using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// [URL에서 가져오기] 창. [주소] 직접 링크(https 미디어 파일) 또는 유튜브 등 yt-dlp가 지원하는 주소, [검색] 유튜브 검색(yt-dlp, 영상은 받지 않음).
    /// 아래쪽은 두 탭이 함께 쓴다: 고른 영상 · 화질 · [가져오기] · 진행률 · [취소]. 받은 파일은 미디어 목록에 더한다.
    /// </summary>
    internal sealed class UrlImportDialog : Window
    {
        private const int PageSize = 20;
        private static readonly int[] CommonHeights = { 2160, 1440, 1080, 720, 480, 360 };

        private readonly EditorServices _services;
        private readonly Func<Task<FfmpegTool>> _needFfmpeg;
        private readonly Func<string[], Task<int>> _import;
        private readonly int _projectHeight;
        private readonly string _folder;

        private readonly TabControl _tabs = new TabControl();
        private readonly TextBox _url = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _query = new ComboBox { IsEditable = true, MinWidth = 300 };
        private readonly ComboBox _uploaded = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox _length = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
        private readonly CheckBox _creativeCommons = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        private readonly ListBox _results = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly Button _more;
        private readonly TextBlock _chosen = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _quality = new ComboBox { MinWidth = 220 };
        private readonly Button _importButton;
        private readonly Button _close;
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 8, 0, 0), Maximum = 100, Visibility = Visibility.Collapsed };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly List<UIElement> _inputs = new List<UIElement>();

        private CancellationTokenSource _cancel;
        private CancellationTokenSource _thumbnails;
        private Target _target;
        private string _searchedQuery;
        private YtSearchOptions _searchedOptions;
        private int _searchedCount;

        /// <summary>가져올 것: 주소, 종류, 제목·길이(알면), 화질 목록(yt-dlp, 모르면 흔한 값).</summary>
        private sealed class Target
        {
            public Uri Url;
            public UrlKind Kind;
            public string Title;
            public double Duration;
            public List<int> Heights;
        }

        public UrlImportDialog(EditorServices services, Func<Task<FfmpegTool>> needFfmpeg, Func<string[], Task<int>> import, int projectHeight, string folder)
        {
            _services = services;
            _needFfmpeg = needFfmpeg;
            _import = import;
            _projectHeight = projectHeight;
            _folder = folder;
            Title = "URL에서 가져오기";
            Width = 720;
            Height = 640;
            MinWidth = 520;
            MinHeight = 420;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            _more = UiKit.TextButton("더 보기", "SearchMoreButton", () => _ = Search(_searchedCount + PageSize, false));
            _importButton = UiKit.TextButton("가져오기", "UrlImportButton", () => _ = Import());
            _close = UiKit.TextButton("닫기", "UrlCloseButton", CloseOrCancel);
            _close.IsCancel = true;

            var root = new DockPanel { Margin = new Thickness(14) };
            var bottom = Bottom();
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            AutomationProperties.SetAutomationId(_tabs, "UrlImportTabs");
            UiKit.StyleTabs(_tabs);
            _tabs.Items.Add(new TabItem { Header = "주소", Content = AddressTab() });
            _tabs.Items.Add(new TabItem { Header = "검색", Content = SearchTab() });
            root.Children.Add(_tabs);
            Content = root;
            SetTarget(null);
            Closing += (s, e) =>
            {
                _cancel?.Cancel();
                _thumbnails?.Cancel();
            };
            Loaded += (s, e) => _url.Focus();
        }

        // ---------- 주소 ----------

        private UIElement AddressTab()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            var label = UiKit.Label("동영상 주소 — 직접 링크(https://…/video.mp4) 또는 유튜브 등 yt-dlp가 지원하는 사이트");
            label.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(label);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var check = UiKit.TextButton("확인", "UrlCheckButton", () => _ = CheckAddress());
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            AutomationProperties.SetAutomationId(_url, "UrlBox");
            AutomationProperties.SetName(_url, "동영상 주소");
            _url.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    _ = CheckAddress();
                    e.Handled = true;
                }
            };
            _url.TextChanged += (s, e) =>
            {
                if (_target != null && _tabs.SelectedIndex == 0)
                    SetTarget(null);
            };
            row.Children.Add(_url);
            panel.Children.Add(row);
            var hint = UiKit.Label("유튜브 주소는 [확인]을 누르면 제목·길이·화질 목록을 먼저 보여 줍니다. 재생목록은 받지 않습니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(hint);
            _inputs.Add(_url);
            _inputs.Add(check);
            return panel;
        }

        private async Task CheckAddress()
        {
            var kind = YtDlpText.Classify(_url.Text, out var uri);
            if (kind == UrlKind.Invalid)
            {
                SetTarget(null);
                _status.Text = "주소를 확인하세요. https 주소만 받을 수 있습니다(직접 링크는 https만, 유튜브 등은 http(s)).";
                return;
            }
            if (kind == UrlKind.Direct)
            {
                SetTarget(new Target { Url = uri, Kind = kind, Title = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) });
                _status.Text = "직접 링크입니다. [가져오기]를 누르면 받아서 미디어 목록에 더합니다.";
                return;
            }
            await Busy("영상 정보를 확인하는 중…", async token =>
            {
                var tool = await NeedYtDlp();
                if (tool == null)
                    return;
                var info = await Task.Run(() => tool.Info(uri.ToString(), token), token);
                SetTarget(new Target { Url = uri, Kind = kind, Title = info.Title, Duration = info.DurationSeconds, Heights = info.Heights });
                _status.Text = info.Heights.Count == 0 ? "화질 정보가 없어 가장 좋은 것으로 받습니다." : "화질을 고르고 [가져오기]를 누르세요.";
            });
        }

        // ---------- 검색 ----------

        private UIElement SearchTab()
        {
            var panel = new DockPanel { Margin = new Thickness(10) };
            var top = new StackPanel();
            var row = new DockPanel();
            var search = UiKit.TextButton("검색", "SearchButton", () => _ = Search(PageSize, true));
            DockPanel.SetDock(search, Dock.Right);
            row.Children.Add(search);
            AutomationProperties.SetAutomationId(_query, "SearchQueryBox");
            AutomationProperties.SetName(_query, "검색어");
            _query.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    _ = Search(PageSize, true);
                    e.Handled = true;
                }
            };
            FillRecent();
            row.Children.Add(_query);
            top.Children.Add(row);

            var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            foreach (var label in new[] { "올린 날짜 전체", "오늘", "이번 주", "이번 달", "올해" })
                _uploaded.Items.Add(label);
            _uploaded.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_uploaded, "SearchUploadedBox");
            _length.Items.Add("길이 전체");
            _length.Items.Add("짧음(4분 미만)");
            _length.Items.Add("중간(4~20분)");
            _length.Items.Add("김(20분 넘음)");
            _length.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_length, "SearchLengthBox");
            _creativeCommons.Content = new TextBlock { Text = "크리에이티브 커먼즈만" };
            AutomationProperties.SetAutomationId(_creativeCommons, "SearchCreativeCommonsCheck");
            filters.Children.Add(_uploaded);
            filters.Children.Add(_length);
            filters.Children.Add(_creativeCommons);
            var clear = UiKit.TextButton("최근 검색어 지우기", "ClearRecentSearchesButton", () =>
            {
                _services.SaveSetting(EditorServices.RecentSearchesKey, null);
                FillRecent();
                _status.Text = "최근 검색어를 지웠습니다.";
            });
            clear.Margin = new Thickness(12, 0, 0, 0);
            filters.Children.Add(clear);
            top.Children.Add(filters);
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);

            DockPanel.SetDock(_more, Dock.Bottom);
            _more.HorizontalAlignment = HorizontalAlignment.Center;
            _more.Margin = new Thickness(0, 6, 0, 0);
            _more.Visibility = Visibility.Collapsed;
            panel.Children.Add(_more);
            AutomationProperties.SetAutomationId(_results, "SearchResultList");
            AutomationProperties.SetName(_results, "검색 결과");
            _results.SetResourceReference(BackgroundProperty, "PanelBackground");
            _results.SelectionChanged += (s, e) =>
            {
                if (_results.SelectedItem is FrameworkElement item && item.Tag is YtSearchResult result)
                    SetTarget(new Target { Url = new Uri(result.Url), Kind = UrlKind.YouTube, Title = result.Title, Duration = result.DurationSeconds, Heights = CommonHeights.ToList() });
            };
            // 두 번 누르면 기본 브라우저로 열어 미리 본다(편집기 안에서는 재생하지 않음)
            _results.MouseDoubleClick += (s, e) =>
            {
                if (_results.SelectedItem is FrameworkElement item && item.Tag is YtSearchResult result)
                    OpenInBrowser(result.Url);
            };
            panel.Children.Add(_results);
            _inputs.AddRange(new UIElement[] { _query, search, _uploaded, _length, _creativeCommons, clear, _more, _results });
            return panel;
        }

        private void FillRecent()
        {
            var text = _query.Text;
            _query.Items.Clear();
            foreach (var query in YtDlpText.ReadRecent(_services.Setting(EditorServices.RecentSearchesKey)))
                _query.Items.Add(query);
            _query.Text = text;
        }

        private YtSearchOptions Options()
        {
            var lengths = new[] { YtLength.Any, YtLength.Short, YtLength.Medium, YtLength.Long };
            var uploaded = new[] { YtUploaded.Any, YtUploaded.Today, YtUploaded.Week, YtUploaded.Month, YtUploaded.Year };
            return new YtSearchOptions
            {
                Uploaded = uploaded[Math.Max(0, _uploaded.SelectedIndex)],
                Length = lengths[Math.Max(0, _length.SelectedIndex)],
                CreativeCommons = _creativeCommons.IsChecked == true
            };
        }

        /// <summary>검색한다. fresh면 지금 입력한 검색어·필터로 새로, 아니면 지난 검색을 count개로 늘려 다시 받는다([더 보기]).</summary>
        private async Task Search(int count, bool fresh)
        {
            var query = fresh ? (_query.Text ?? "").Trim() : _searchedQuery;
            if (string.IsNullOrEmpty(query))
            {
                _status.Text = "검색어를 넣으세요.";
                return;
            }
            var options = fresh ? Options() : _searchedOptions;
            await Busy("검색하는 중…", async token =>
            {
                var tool = await NeedYtDlp();
                if (tool == null)
                    return;
                var (results, hasMore) = await Task.Run(() => tool.Search(query, options, count, token), token);
                _searchedQuery = query;
                _searchedOptions = options;
                _searchedCount = count;
                if (fresh)
                {
                    _services.SaveSetting(EditorServices.RecentSearchesKey, YtDlpText.AddRecent(_services.Setting(EditorServices.RecentSearchesKey), query));
                    FillRecent();
                }
                ShowResults(results);
                _more.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
                _status.Text = results.Count == 0 ? "검색 결과가 없습니다." : results.Count + "개 — 골라서 [가져오기], 두 번 누르면 브라우저로 미리 봅니다.";
            });
        }

        private void ShowResults(List<YtSearchResult> results)
        {
            var selected = (_results.SelectedItem as FrameworkElement)?.Tag as YtSearchResult;
            _thumbnails?.Cancel();
            _thumbnails = new CancellationTokenSource();
            var token = _thumbnails.Token;
            _results.Items.Clear();
            foreach (var result in results)
            {
                var item = ResultItem(result, token);
                _results.Items.Add(item);
                if (selected != null && selected.Id == result.Id)
                    _results.SelectedItem = item;
            }
        }

        private FrameworkElement ResultItem(YtSearchResult result, CancellationToken token)
        {
            var row = new DockPanel { Tag = result, Margin = new Thickness(2), Background = Brushes.Transparent };
            AutomationProperties.SetName(row, result.Title);
            var picture = new Image { Stretch = Stretch.UniformToFill };
            var frame = new Border { Child = picture, Width = 120, Height = 68, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true };
            frame.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
            DockPanel.SetDock(frame, Dock.Left);
            row.Children.Add(frame);
            if (result.ThumbnailUrl != null)
            {
                var url = result.ThumbnailUrl;
                Task.Run(async () => EditorServices.Decode(await UrlDownloader.FetchSmall(url, YtDlpText.ThumbnailHost, 512 * 1024, token)), token).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null && !token.IsCancellationRequested)
                        Dispatcher.BeginInvoke(new Action(() => picture.Source = t.Result));
                }, TaskScheduler.Default);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = UiKit.Label(result.Title);
            title.FontWeight = FontWeights.SemiBold;
            text.Children.Add(title);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(result.Channel))
                parts.Add(result.Channel);
            parts.Add(result.IsLive ? "실시간" : result.DurationSeconds > 0 ? Duration(result.DurationSeconds) : "길이 모름");
            if (result.ViewCount >= 0)
                parts.Add("조회수 " + Count(result.ViewCount) + "회");
            if (result.Uploaded != null)
                parts.Add(result.Uploaded.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            text.Children.Add(UiKit.Label(string.Join(" · ", parts), true));
            row.Children.Add(text);
            row.ToolTip = result.Title + "\n" + result.Url;
            return row;
        }

        private void OpenInBrowser(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !YtDlpText.IsYouTubeHost(uri.Host))
                return;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _status.Text = "브라우저를 열지 못했습니다: " + ex.Message;
            }
        }

        // ---------- 아래쪽: 고른 영상 · 화질 · 가져오기 ----------

        private UIElement Bottom()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetAutomationId(_chosen, "UrlChosenText");
            panel.Children.Add(_chosen);
            var row = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_importButton);
            buttons.Children.Add(_close);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var quality = new StackPanel { Orientation = Orientation.Horizontal };
            quality.Children.Add(UiKit.Label("화질  "));
            AutomationProperties.SetAutomationId(_quality, "UrlQualityBox");
            AutomationProperties.SetName(_quality, "화질");
            quality.Children.Add(_quality);
            row.Children.Add(quality);
            panel.Children.Add(row);
            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "UrlImportStatus");
            panel.Children.Add(_status);
            var where = UiKit.Label("저장 위치: " + _folder, true);
            where.TextWrapping = TextWrapping.Wrap;
            where.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(where);
            _inputs.Add(_quality);
            return panel;
        }

        private void SetTarget(Target target)
        {
            _target = target;
            _quality.Items.Clear();
            _importButton.IsEnabled = target != null && _cancel == null;
            if (target == null)
            {
                _chosen.Text = "가져올 영상을 고르세요.";
                _quality.IsEnabled = false;
                return;
            }
            _chosen.Text = "고른 것: " + (string.IsNullOrEmpty(target.Title) ? target.Url.ToString() : target.Title) + (target.Duration > 0 ? " · " + Duration(target.Duration) : "");
            if (target.Kind == UrlKind.Direct || target.Heights == null || target.Heights.Count == 0)
            {
                _quality.Items.Add(new ComboBoxItem { Content = target.Kind == UrlKind.Direct ? "원본 그대로" : "가장 좋은 것", Tag = 0 });
                _quality.SelectedIndex = 0;
                _quality.IsEnabled = false;
                return;
            }
            var chosen = YtDlpText.ChooseHeight(target.Heights, _projectHeight);
            foreach (var height in target.Heights)
            {
                var label = height + "p" + (height == chosen ? " (프로젝트 " + _projectHeight + "p 이하 최고)" : "");
                _quality.Items.Add(new ComboBoxItem { Content = label, Tag = height });
                if (height == chosen)
                    _quality.SelectedIndex = _quality.Items.Count - 1;
            }
            _quality.Items.Add(new ComboBoxItem { Content = "가장 좋은 것(제한 없음)", Tag = 0 });
            _quality.IsEnabled = _cancel == null;
        }

        private async Task Import()
        {
            var target = _target;
            if (target == null)
                return;
            var height = (_quality.SelectedItem as ComboBoxItem)?.Tag is int h ? h : 0;
            await Busy("받는 중…", async token =>
            {
                var ffmpeg = await _needFfmpeg();
                if (ffmpeg == null)
                {
                    _status.Text = "FFmpeg가 있어야 받은 파일을 확인하고 합칠 수 있습니다.";
                    return;
                }
                string path;
                if (target.Kind == UrlKind.Direct)
                {
                    var progress = new Progress<UrlDownloader.Status>(s =>
                    {
                        _progress.IsIndeterminate = s.Total <= 0;
                        if (s.Total > 0)
                            _progress.Value = 100.0 * s.Done / s.Total;
                        _status.Text = "받는 중… " + (s.Done / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" + (s.Total > 0 ? " / " + (s.Total / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : "");
                    });
                    path = await Task.Run(() => UrlDownloader.Download(target.Url, _folder, IsMedia(ffmpeg), progress, token), token);
                }
                else
                {
                    var tool = await NeedYtDlp();
                    if (tool == null)
                        return;
                    path = await DownloadWithYtDlp(tool, target, height, ffmpeg, token);
                    if (path == null)
                        return;
                }
                _status.Text = "미디어 목록에 더하는 중…";
                var added = await _import(new[] { path });
                _status.Text = added > 0 ? "가져왔습니다: " + path : "받았지만 미디어로 읽지 못했습니다: " + path;
            });
        }

        private static Func<string, Task<bool>> IsMedia(FfmpegTool ffmpeg)
        {
            return async path =>
            {
                try
                {
                    var info = await MediaProbe.Probe(ffmpeg, path, CancellationToken.None).ConfigureAwait(false);
                    return info.Kind != MediaKind.Image;
                }
                catch (Exception)
                {
                    return false;
                }
            };
        }

        /// <summary>yt-dlp로 받는다. 낡은 yt-dlp라서 실패했고 플러그인이 받은 사본이면 새 릴리스로 바꾸고 한 번 더 해 본다.</summary>
        private async Task<string> DownloadWithYtDlp(YtDlpTool tool, Target target, int height, FfmpegTool ffmpeg, CancellationToken token)
        {
            var progress = new Progress<double>(p =>
            {
                _progress.IsIndeterminate = false;
                _progress.Value = p;
                _status.Text = "받는 중… " + p.ToString("0.0", CultureInfo.InvariantCulture) + "% (영상과 소리를 따로 받은 뒤 합칩니다)";
            });
            try
            {
                return await Task.Run(() => tool.Download(target.Url.ToString(), height, ffmpeg.Folder, _folder, progress, token), token);
            }
            catch (YtDlpException ex) when (ex.Outdated)
            {
                if (!tool.Downloaded)
                {
                    _status.Text = ex.Message + "\n설치한 yt-dlp를 업데이트하세요(예: winget upgrade yt-dlp.yt-dlp 또는 yt-dlp -U). 지금 버전: " + tool.Version;
                    return null;
                }
                _status.Text = ex.Message + " — 새 yt-dlp를 받는 중…";
                tool = await UpdateYtDlp(token);
                if (tool == null)
                    return null;
                _status.Text = "새 yt-dlp(" + tool.Version + ")로 다시 받는 중…";
                return await Task.Run(() => tool.Download(target.Url.ToString(), height, ffmpeg.Folder, _folder, progress, token), token);
            }
        }

        /// <summary>플러그인이 받은 yt-dlp를 최신 릴리스로 바꾼다. 실패하면 상태 줄에 알리고 null.</summary>
        private async Task<YtDlpTool> UpdateYtDlp(CancellationToken token)
        {
            try
            {
                await Task.Run(() => YtDlpDownloader.DownloadYtDlp(_services.YtDlpFolder, null, token), token);
                return await _services.RelocateYtDlpAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _status.Text = "yt-dlp를 새로 받지 못했습니다: " + ex.Message;
                return null;
            }
        }

        /// <summary>yt-dlp를 마련한다(없으면 설치 창, 안내를 확인하지 않았으면 안내 창). 플러그인이 받은 사본이 오래됐으면 먼저 새로 받는다.</summary>
        private async Task<YtDlpTool> NeedYtDlp()
        {
            var tool = _services.YtDlp ?? await _services.LocateYtDlpAsync();
            if (tool == null || !_services.YtDlpNoticeAccepted)
            {
                if (!YtDlpSetupDialog.Ensure(_services, this))
                {
                    _status.Text = _services.YtDlp == null ? "yt-dlp가 없어 할 수 없습니다." : "안내를 확인해야 받을 수 있습니다.";
                    return null;
                }
                tool = _services.YtDlp;
            }
            if (tool.IsStale)
            {
                var previous = _status.Text;
                _status.Text = "yt-dlp가 " + (int)YtDlpTool.RefreshAge.TotalDays + "일 넘게 지나 새 릴리스로 바꾸는 중…";
                // 오래된 사본도 쓸 수는 있으므로 새로 받지 못해도 계속한다
                tool = await UpdateYtDlp(CancellationToken.None) ?? tool;
                _status.Text = previous;
            }
            return tool;
        }

        // ---------- 진행 상태 ----------

        /// <summary>작업 하나를 돌린다: 입력을 끄고 [닫기]를 [취소]로, 끝나면 되돌린다. 오류는 상태 줄에.</summary>
        private async Task Busy(string message, Func<CancellationToken, Task> work)
        {
            if (_cancel != null)
                return;
            _cancel = new CancellationTokenSource();
            foreach (var input in _inputs)
                input.IsEnabled = false;
            _importButton.IsEnabled = false;
            _close.Content = new TextBlock { Text = "취소" };
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = true;
            _status.Text = message;
            try
            {
                await work(_cancel.Token);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "취소했습니다.";
            }
            catch (YtDlpException ex)
            {
                _status.Text = ex.Message;
            }
            catch (Exception ex)
            {
                _status.Text = "실패했습니다: " + ex.Message;
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                foreach (var input in _inputs)
                    input.IsEnabled = true;
                _quality.IsEnabled = _quality.Items.Count > 1;
                _importButton.IsEnabled = _target != null;
                _close.Content = new TextBlock { Text = "닫기" };
                _progress.Visibility = Visibility.Collapsed;
            }
        }

        private void CloseOrCancel()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                return;
            }
            Close();
        }

        private static string Duration(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }

        /// <summary>조회수를 짧게: 1.2만, 3.4억.</summary>
        private static string Count(long value)
        {
            if (value >= 100_000_000)
                return (value / 100_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "억";
            if (value >= 10_000)
                return (value / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "만";
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
