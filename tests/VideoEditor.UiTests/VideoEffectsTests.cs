using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.UiTests.Headless;
using Xunit;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 동영상 효과: [효과] 탭에서 스캔 라인을 클립에 넣고(단추·끌어 놓기) 속성 패널 [효과] 탭에서 값(슬라이더 끄기 = 실행 취소 한 단계)·색·빼기,
    /// [사람에게만]과 인물 마스크 상태·[인물 마스크 만들기]. 글자 클립에는 효과 탭이 없다.
    /// </summary>
    public class VideoEffectsTests
    {
        /// <summary>시험에서 플러그인의 IPersonMaskProvider를 흉내 낸다 — 플러그인 형식은 다른 로드 컨텍스트라 DispatchProxy로 만든다. 어떤 원본이든 Mask를 돌려준다.</summary>
        public class MaskProxy : DispatchProxy
        {
            public string Mask { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "Find" ? Mask : null;
            }
        }

        private static PropertyInfo MaskProvider(PluginUnderTest plugin)
        {
            return plugin.Assembly.GetType("VideoEditor.Media.RenderEnvironment", true).GetProperty("PersonMasks");
        }

        private static object FakeMasks(PluginUnderTest plugin, string mask)
        {
            var proxy = DispatchProxy.Create(plugin.Assembly.GetType("VideoEditor.Core.IPersonMaskProvider", true), typeof(MaskProxy));
            ((MaskProxy)proxy).Mask = mask;
            return proxy;
        }

        private static async Task Import(UiThread ui, Window window, string name)
        {
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, Path.Combine(MediaFolder, name), Wait);
            await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == name), Wait, name + " 가져오기");
            var before = ui.Run(() => Clips(window).Count);
            ui.Run(() =>
            {
                var list = Ui.ById<ListBox>(window, "MediaList");
                list.SelectedItem = list.Items.Cast<FrameworkElement>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == name);
                Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
            });
            await ui.WaitUntil(() => Clips(window).Count == before + 1, Wait, name + " 클립 추가");
        }

        private static object Red(Window window)
        {
            return ProjectClips(window).First(c => (string)Get(c, "Name") == "red.png");
        }

        /// <summary>빨간 사진 클립의 효과 목록(없으면 null).</summary>
        private static IList Effects(Window window)
        {
            return (IList)Get(Red(window), "Effects");
        }

        private static double Value(Window window, int index, string key)
        {
            return (double)((IDictionary)Get(Effects(window)[index], "Values"))[key];
        }

        /// <summary>미리 보기 화면의 픽셀(UI 스레드).</summary>
        private static byte[] PreviewPixels(Window window)
        {
            var image = Ui.ById<Image>(window, "PreviewImage");
            var bitmap = image?.Source as BitmapSource;
            if (bitmap == null)
                return null;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        private static bool Differs(byte[] a, byte[] b)
        {
            return a == null || b == null || a.Length != b.Length || !a.AsSpan().SequenceEqual(b);
        }

        /// <summary>칸의 썸네일 그림(썸네일 틀 안 — "새로" 표시와 겹쳐 있다).</summary>
        private static ImageSource Thumbnail(Window window, string id)
        {
            var tile = Tile(window, id);
            if (tile == null)
                return null;
            var frame = (Border)((StackPanel)tile.Content).Children[0];
            return (frame.Child as Image ?? ((Panel)frame.Child).Children.OfType<Image>().Single()).Source;
        }

        private const string SeenKey = "seenVideoEffects";

        /// <summary>본 효과 기록을 지운다 — 플러그인 설정은 시험 프로세스 내내 남으므로(다른 시험이 넣은 효과는 본 것이 된다) 새 효과를 보는 시험의 앞뒤에.</summary>
        private static void ForgetSeen(PluginUnderTest plugin)
        {
            plugin.Manager.SetSetting(SeenKey, null);
        }

        /// <summary>목록의 모든 효과를 본 것으로(새 효과가 없는 화면).</summary>
        private static void SeeAll(PluginUnderTest plugin)
        {
            plugin.Manager.SetSetting(SeenKey, "[" + string.Join(",", Catalog(plugin).Select(c => "\"" + c.Id + "\"")) + "]");
        }

        /// <summary>칸의 "새로" 표시(칸 안에서 찾는다 — 막 만든 탭이라 자리를 잡기 전에도).</summary>
        private static FrameworkElement Badge(Window window, string id)
        {
            var tile = Tile(window, id);
            return tile == null ? null : Ui.ById<FrameworkElement>(tile, "VideoEffectNew_" + id);
        }

        /// <summary>"새로" 표시가 보이는 칸의 효과 id(목록 차례).</summary>
        private static List<string> Badged(Window window)
        {
            return Ui.ById<ListBox>(window, "VideoEffectList").Items.Cast<ListBoxItem>()
                .Select(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i).Substring("VideoEffect_".Length))
                .Where(id => Badge(window, id)?.Visibility == Visibility.Visible).ToList();
        }

        /// <summary>[효과] 탭 머리글의 자동화 이름(새 효과가 있으면 개수를 붙인다).</summary>
        private static string EffectsHeader(Window window)
        {
            return System.Windows.Automation.AutomationProperties.GetName(Ui.ById<TabItem>(window, "EffectsTab"));
        }

        /// <summary>효과 탭의 동영상 효과 칸(묶음을 고르면 그 묶음 칸만 있다).</summary>
        private static ListBoxItem Tile(Window window, string id)
        {
            return Ui.ById<ListBox>(window, "VideoEffectList")?.Items.Cast<ListBoxItem>()
                .FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "VideoEffect_" + id);
        }

        private static List<string> Tiles(Window window)
        {
            return Ui.ById<ListBox>(window, "VideoEffectList").Items.Cast<ListBoxItem>().Select(System.Windows.Automation.AutomationProperties.GetAutomationId).ToList();
        }

        /// <summary>목록에 보이는 묶음 제목(화면 차례).</summary>
        private static List<string> GroupHeaders(Window window)
        {
            var list = Ui.ById<ListBox>(window, "VideoEffectList");
            list.UpdateLayout();
            return Ui.FindAll<GroupItem>(list, g => g.Visibility == Visibility.Visible).Select(g => (string)((System.Windows.Data.CollectionViewGroup)g.Content).Name).ToList();
        }

        /// <summary>효과 탭 내용 전체(스크롤에 가린 곳까지)를 PNG로 — 시험 창이 작아도 칸 놓기 전체를 볼 수 있게(UI 스레드).</summary>
        private static void SnapTab(Window window, string folder, string name)
        {
            var scroll = Ui.ById<ScrollViewer>(window, "EffectsTabScroll");
            var content = (FrameworkElement)scroll.Content;
            content.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(scroll.TryFindResource("PanelBackground") as Brush ?? Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, name + ".png")))
                encoder.Save(stream);
        }

        /// <summary>플러그인의 효과 목록(id·이름·묶음·처음 나온 판 — 다른 로드 컨텍스트라 리플렉션으로).</summary>
        private static List<(string Id, string Name, string Group, int Since)> Catalog(PluginUnderTest plugin)
        {
            var all = (IEnumerable)plugin.Assembly.GetType("VideoEditor.Core.VideoEffects", true).GetProperty("All").GetValue(null);
            return all.Cast<object>().Select(d => ((string)Get(d, "Id"), (string)Get(d, "Name"), (string)Get(d, "Group"), (int)Get(d, "Since"))).ToList();
        }

        /// <summary>빨간 사진을 놓고 고른 뒤 재생 헤드를 1초로(스캔 라인이 화면 가운데쯤 지날 때) — 미리 보기를 기다린다.</summary>
        private static async Task<byte[]> RedAtOneSecond(UiThread ui, Window window)
        {
            await Import(ui, window, "red.png");
            ui.Run(() =>
            {
                SelectClip(window, n => n.Contains("red.png"));
                var box = Ui.ById<TextBox>(window, "TimecodeBox");
                Ui.Focus(box);
                box.Text = "00:00:01:00";
                Ui.Press(box, System.Windows.Input.Key.Enter);
            });
            await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "사진 미리 보기");
            await Task.Delay(500);
            return ui.Run(() => PreviewPixels(window));
        }

        private static async Task AddScanLineFromTab(UiThread ui, Window window)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "EffectsTab"));
            await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록");
            ui.Run(() =>
            {
                SelectClip(window, n => n.Contains("red.png"));
                Ui.Click(Ui.ById<Button>(window, "VideoEffectApplyButton"));
            });
            await ui.WaitUntil(() => Effects(window)?.Count == 1, Wait, "스캔 라인 넣기");
        }

        [EditorFact]
        public async Task EffectsTab_AddsScanLine_SliderDragIsOneUndoStep_ColorAndRemove()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var before = await RedAtOneSecond(ui, window);
                await AddScanLineFromTab(ui, window);
                // 효과 탭 썸네일(실제로 그린 띠)
                await ui.WaitUntil(() => Thumbnail(window, "scan-line") != null, Wait, "스캔 라인 썸네일");
                ui.Run(() =>
                {
                    Assert.StartsWith("'스캔 라인' 효과를 클립에 넣었습니다", Status(window));
                    Assert.Equal("scan-line", Get(Effects(window)[0], "Id"));
                    Assert.Equal("#2F7BFF", Get(Effects(window)[0], "Color"));
                });
                // 속성 패널의 [효과] 탭이 열리고, 미리 보기에 선이 그려진다
                await ui.WaitUntil(() => Ui.ById<TabItem>(window, "ClipEffectsTab")?.IsSelected == true, Wait, "효과 탭 열림");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "스캔 라인 미리 보기");
                ui.Run(() =>
                {
                    Assert.Equal(Visibility.Collapsed, Ui.ById<TextBlock>(window, "EffectEmpty").Visibility);
                    Assert.Equal("40", Ui.ById<TextBox>(window, "Effect0_glow").Text);
                    Ui.Snap(window, plugin.ShotFolder, "video-effect-scan-line");
                });
                var lit = ui.Run(() => PreviewPixels(window));

                // 빛 번짐 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "Effect0_glowSlider");
                    foreach (var value in new[] { 60.0, 90.0, 120.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs(Value(window, 0, "glow") - 120) < 1e-6, Wait, "빛 번짐 120");
                await ui.WaitUntil(() => Differs(lit, PreviewPixels(window)), Wait, "빛 번짐 미리 보기");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Effects(window)?.Count == 1 && Math.Abs(Value(window, 0, "glow") - 40) < 1e-6, Wait, "실행 취소 한 번에 40");

                // 색: 16진 칸에 적으면 그 색(실행 취소 한 단계)
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "Effect0_color");
                    Ui.Focus(hex);
                    hex.Text = "ff2244";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (string)Get(Effects(window)[0], "Color") == "#FF2244", Wait, "색 바꾸기");

                // 빼기 → 효과 없음(미리 보기도 처음으로) → 실행 취소로 되돌리기
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "EffectRemove0")));
                await ui.WaitUntil(() => Effects(window) == null, Wait, "효과 빼기");
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "EffectEmpty")?.Visibility == Visibility.Visible, Wait, "효과 없음 안내");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "효과 없는 미리 보기");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Effects(window)?.Count == 1 && (string)Get(Effects(window)[0], "Color") == "#FF2244", Wait, "빼기 실행 취소");

                // 효과를 클립에 끌어 놓기(끌어 놓기 이벤트를 만들 수 없어 처리기를 직접 부른다) — 두 번째 효과가 위에 더해진다
                ui.Run(() =>
                {
                    var canvas = Canvas(window);
                    var handler = (Delegate)canvas.GetType().GetField("VideoEffectDropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
                    handler.DynamicInvoke("scan-line", Get(Red(window), "Id"));
                });
                await ui.WaitUntil(() => Effects(window)?.Count == 2, Wait, "끌어 놓은 효과");
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "Effect1_glow"), Wait, "두 번째 효과 줄");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task PersonOnly_ShowsMaskStatus_BuildButtonRunsTheCommand_AndMaskLimitsTheLine()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            var provider = MaskProvider(plugin);
            var original = provider.GetValue(null);
            try
            {
                await RedAtOneSecond(ui, window);
                await AddScanLineFromTab(ui, window);
                await ui.WaitFor(() => Ui.ById<CheckBox>(window, "EffectPersonOnly"), Wait, "사람에게만 칸");
                // 사람에게만을 켜기 전에는 마스크 줄이 없다
                Assert.Equal(Visibility.Collapsed, ui.Run(() => ((FrameworkElement)Ui.ById<Button>(window, "EffectMaskBuild").Parent).Visibility));
                await Task.Delay(500);
                var whole = ui.Run(() => PreviewPixels(window));

                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "EffectPersonOnly")));
                await ui.WaitUntil(() => (bool)Get(Effects(window)[0], "PersonOnly"), Wait, "사람에게만 켜기");
                await ui.WaitUntil(() => Ui.ById<Button>(window, "EffectMaskBuild").IsVisible, Wait, "마스크 만들기 단추");
                // 아직 마스크가 없다 — 클립 전체에 칠한다(미리 보기 그대로)
                ui.Run(() => Assert.Equal("인물 마스크가 없습니다", Ui.ById<TextBlock>(window, "EffectMaskStatus").Text));

                // 왼쪽 절반이 흰 마스크가 준비된 것으로 하고 [인물 마스크 만들기] → 명령이 돌고(상태 줄·창) 끝나면 상태가 "준비됨"
                var mask = Path.Combine(MediaFolder, "mask-left.png");
                RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:size=200x150,format=gray,geq=lum='if(lt(X,100),255,0)'", "-frames:v", "1", mask);
                ui.Run(() => provider.SetValue(null, FakeMasks(plugin, mask)));
                var status = ui.Run(() => Status(window));
                ui.Post(() => Ui.Click(Ui.ById<Button>(window, "EffectMaskBuild")));
                var deadline = DateTime.UtcNow + Wait;
                while (ui.Run(() => Ui.ById<TextBlock>(window, "EffectMaskStatus").Text) != "인물 마스크 준비됨")
                {
                    Assert.True(DateTime.UtcNow < deadline, "마스크 상태가 바뀌지 않았습니다 — 상태 줄: " + ui.Run(() => Status(window)));
                    // 마스크 만들기가 묻는 창을 띄우면 닫는다(명령이 돌았다는 표시)
                    ui.Run(() =>
                    {
                        foreach (var dialog in Ui.Windows().Where(w => w != window && w.IsVisible && w.Owner == window))
                            dialog.Close();
                    });
                    await Task.Delay(200);
                }
                Assert.NotEqual(status, ui.Run(() => Status(window)));

                // 사람에게만을 껐다 켜면(편집 → 다시 그리기) 마스크가 흰 왼쪽에만 선이 남아 미리 보기가 클립 전체에 칠한 것과 다르다
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "EffectPersonOnly")));
                await ui.WaitUntil(() => !(bool)Get(Effects(window)[0], "PersonOnly"), Wait, "사람에게만 끄기");
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "EffectPersonOnly")));
                await ui.WaitUntil(() => (bool)Get(Effects(window)[0], "PersonOnly"), Wait, "사람에게만 다시 켜기");
                await ui.WaitUntil(() => Differs(whole, PreviewPixels(window)), Wait, "마스크로 줄어든 선");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "video-effect-person-only"));

                // 실행 취소 두 번이면 다시 클립 전체에
                foreach (var step in new[] { "켜기", "끄기" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => (bool)Get(Effects(window)[0], "PersonOnly"), Wait, "실행 취소");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => provider.SetValue(null, original));
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task EffectsTab_GroupsTiles_FilterApplyAndDoubleClick_MarksAppliedEffects()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            // 효과 탭의 기본 모양(새 효과 표시는 아래 NewEffects 시험에서): 모든 효과를 본 것으로 두고 연다
            SeeAll(plugin);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, "red.png");
                ui.Run(() =>
                {
                    Session(window).GetType().GetMethod("ClearSelection").Invoke(Session(window), null);
                    Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "EffectsTab");
                });
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록");
                var catalog = Catalog(plugin);
                var groups = catalog.Select(c => c.Group).Distinct().ToList();
                // 칸 차례: 묶음(처음 나오는 차례)마다 목록 차례
                var ordered = catalog.OrderBy(c => groups.IndexOf(c.Group)).ToList();
                ui.Run(() =>
                {
                    // 막 만든 탭이라 자리부터 잡는다
                    Ui.ById<ScrollViewer>(window, "EffectsTabScroll").UpdateLayout();
                    // 묶음 고르기는 "모든 효과"와 묶음들(목록 차례), 처음에는 모든 효과가 묶음 제목 아래에
                    Assert.Equal(new[] { "모든 효과" }.Concat(groups), Ui.ById<ComboBox>(window, "VideoEffectGroup").Items.Cast<string>());
                    Assert.Equal("모든 효과", Ui.ById<ComboBox>(window, "VideoEffectGroup").SelectedItem);
                    Assert.Equal(ordered.Select(c => "VideoEffect_" + c.Id), Tiles(window));
                    Assert.Equal(catalog[0].Name, System.Windows.Automation.AutomationProperties.GetName(Tile(window, catalog[0].Id)));
                    Assert.Contains(catalog[0].Group, (string)Tile(window, catalog[0].Id).ToolTip);
                    // [효과] 탭에는 동영상 효과만(전환 효과는 [전환] 탭 — 열기 전이라 아직 만들지 않음), 새 효과가 없으면 알림 줄·머리글 점도 없다.
                    // 탭 전체가 한 번에 스크롤된다
                    var tab = Ui.ById<TabItem>(window, "EffectsTab");
                    var list = Ui.ById<ListBox>(window, "VideoEffectList");
                    Assert.Same(tab.Content, Ui.ById<ScrollViewer>(window, "EffectsTabScroll"));
                    Assert.NotNull(Ui.ById<ListBox>((DependencyObject)tab.Content, "VideoEffectList"));
                    Assert.Null(Ui.ById<ListBox>(window, "TransitionList"));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<Border>(window, "VideoEffectNewNotice").Visibility);
                    Assert.Equal("효과", tab.Header);
                    Assert.Empty(Badged(window));
                    Assert.True(list.ActualHeight > 0, "동영상 효과 목록 높이 " + list.ActualHeight);
                    var scroll = Ui.ById<ScrollViewer>(window, "EffectsTabScroll");
                    Assert.True(scroll.ExtentHeight > scroll.ViewportHeight, "효과 탭이 스크롤되어야 합니다(효과 14개)");
                    // 칸은 모두 같은 크기다
                    Assert.Single(list.Items.Cast<ListBoxItem>().Select(i => (Math.Round(i.ActualWidth), Math.Round(i.ActualHeight))).Distinct());
                    // 고른 클립이 없으면 넣기 단추는 꺼져 있다
                    Assert.False(Ui.ById<Button>(window, "VideoEffectApplyButton").IsEnabled);
                });
                await ui.WaitUntil(() => Thumbnail(window, catalog[0].Id) != null, Wait, "썸네일");
                await ui.WaitUntil(() => catalog.All(c => Thumbnail(window, c.Id) != null), Wait, "모든 썸네일");
                ui.Run(() =>
                {
                    Ui.Snap(window, plugin.ShotFolder, "video-effects-tab");
                    SnapTab(window, plugin.ShotFolder, "video-effects-tab-full");
                    Assert.Equal(groups, GroupHeaders(window));
                });

                // 바퀴를 목록 위에서 굴려도 탭이 스크롤된다
                ui.Run(() =>
                {
                    var scroll = Ui.ById<ScrollViewer>(window, "EffectsTabScroll");
                    var list = Ui.ById<ListBox>(window, "VideoEffectList");
                    list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                    scroll.UpdateLayout();
                    Assert.True(scroll.VerticalOffset > 0, "스크롤 " + scroll.VerticalOffset);
                    scroll.ScrollToTop();
                });

                // 묶음을 고르면 그 묶음의 칸만(제목도 그 묶음 하나), 고른 칸이 늘 있다 — 다시 모든 효과
                foreach (var group in groups)
                {
                    ui.Run(() => Ui.ById<ComboBox>(window, "VideoEffectGroup").SelectedItem = group);
                    ui.Run(() =>
                    {
                        Assert.Equal(catalog.Where(c => c.Group == group).Select(c => "VideoEffect_" + c.Id), Tiles(window));
                        Assert.Equal(new[] { group }, GroupHeaders(window));
                        Assert.NotNull(Ui.ById<ListBox>(window, "VideoEffectList").SelectedItem);
                    });
                }
                ui.Run(() =>
                {
                    Ui.Snap(window, plugin.ShotFolder, "video-effects-tab-group");
                    SnapTab(window, plugin.ShotFolder, "video-effects-tab-group-full");
                });
                ui.Run(() => Ui.ById<ComboBox>(window, "VideoEffectGroup").SelectedItem = "모든 효과");
                ui.Run(() => Assert.Equal(catalog.Count, Tiles(window).Count));

                // 사진 클립을 고르면 단추가 켜지고, 누르면 고른 효과(첫 효과)가 들어간다 — 그 칸은 "고른 클립에 있음"으로 강조
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("red.png"));
                    Assert.True(Ui.ById<Button>(window, "VideoEffectApplyButton").IsEnabled);
                    Assert.Equal("", System.Windows.Automation.AutomationProperties.GetItemStatus(Tile(window, catalog[0].Id)));
                    Ui.ById<ListBox>(window, "VideoEffectList").SelectedItem = Tile(window, catalog[0].Id);
                    Ui.Click(Ui.ById<Button>(window, "VideoEffectApplyButton"));
                });
                await ui.WaitUntil(() => Effects(window)?.Count == 1, Wait, "단추로 넣기");
                ui.Run(() =>
                {
                    Assert.Equal(catalog[0].Id, Get(Effects(window)[0], "Id"));
                    Assert.Equal("고른 클립에 있음", System.Windows.Automation.AutomationProperties.GetItemStatus(Tile(window, catalog[0].Id)));
                });
                // 속성 패널 [효과] 탭: 효과 이름 옆에 작게 묶음 이름
                await ui.WaitFor(() => Ui.ById<TextBlock>(window, "EffectGroup0"), Wait, "효과 묶음 이름");
                ui.Run(() => Assert.Equal(catalog[0].Group, Ui.ById<TextBlock>(window, "EffectGroup0").Text));

                // 두 번 누르기: 마지막 효과를 고른 클립에 하나 더(그 칸이 골라진다)
                var last = catalog[catalog.Count - 1];
                ui.Run(() => Tile(window, last.Id).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent }));
                await ui.WaitUntil(() => Effects(window)?.Count == 2, Wait, "두 번 눌러 넣기");
                await ui.WaitFor(() => Ui.ById<TextBlock>(window, "EffectGroup1"), Wait, "두 번째 효과 묶음 이름");
                ui.Run(() =>
                {
                    Assert.Equal(last.Id, Get(Effects(window)[1], "Id"));
                    Assert.Same(Tile(window, last.Id), Ui.ById<ListBox>(window, "VideoEffectList").SelectedItem);
                    Assert.Equal("고른 클립에 있음", System.Windows.Automation.AutomationProperties.GetItemStatus(Tile(window, last.Id)));
                    Assert.Equal(last.Group, Ui.ById<TextBlock>(window, "EffectGroup1").Text);
                    Ui.Snap(window, plugin.ShotFolder, "video-effects-applied");
                    SnapTab(window, plugin.ShotFolder, "video-effects-applied-full");
                });

                // 고르기를 풀면 단추가 꺼지고 강조도 사라진다
                ui.Run(() =>
                {
                    Session(window).GetType().GetMethod("ClearSelection").Invoke(Session(window), null);
                    Assert.False(Ui.ById<Button>(window, "VideoEffectApplyButton").IsEnabled);
                    Assert.Equal("", System.Windows.Automation.AutomationProperties.GetItemStatus(Tile(window, catalog[0].Id)));
                });

                // 전환 효과는 [전환] 탭에(처음 열 때 만든다) — 그 탭에는 동영상 효과 목록이 없다
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "TransitionsTab"));
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "TransitionList"), Wait, "전환 목록");
                ui.Run(() =>
                {
                    var tab = Ui.ById<TabItem>(window, "TransitionsTab");
                    Assert.Equal("전환", tab.Header);
                    Assert.Same(tab.Content, Ui.ById<ScrollViewer>(window, "TransitionsTabScroll"));
                    Assert.NotNull(Ui.ById<ListBox>((DependencyObject)tab.Content, "TransitionList"));
                    Assert.Null(Ui.ById<ListBox>((DependencyObject)tab.Content, "VideoEffectList"));
                    Assert.False(Ui.ById<ListBox>(window, "VideoEffectList").IsVisible);
                    // 탭 차례: 미디어 · 폴더 · 글자 · 효과 · 전환
                    Assert.Equal(new object[] { "미디어", "폴더", "글자", "효과", "전환" }, Ui.ById<TabControl>(window, "MediaTabs").Items.Cast<TabItem>().Select(t => t.Header));
                    Ui.Snap(window, plugin.ShotFolder, "transitions-tab");
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                ForgetSeen(plugin);
            }
        }

        [EditorFact]
        public async Task TextClips_HaveNoEffectsTab_AndCannotTakeVideoEffects()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "효과 없음";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains("효과 없음")), Wait, "글자 클립");
                ui.Run(() => SelectClip(window, n => n.Contains("효과 없음")));
                await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "글자 속성");
                Assert.Null(ui.Run(() => Ui.ById<TabItem>(window, "ClipEffectsTab")));

                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "EffectsTab"));
                await ui.WaitFor(() => Ui.ById<Button>(window, "VideoEffectApplyButton"), Wait, "동영상 효과 단추");
                // 글자 클립만 골랐으면 [고른 클립에 넣기]가 꺼져 있고, 썸네일을 두 번 눌러도 넣지 않고 알린다
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("효과 없음"));
                    Assert.False(Ui.ById<Button>(window, "VideoEffectApplyButton").IsEnabled);
                    Tile(window, "scan-line").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                });
                await ui.WaitUntil(() => Status(window).StartsWith("동영상 효과를 넣을 동영상·사진 클립을", StringComparison.Ordinal), Wait, "글자 클립 거절 안내");
                Assert.Null(ui.Run(() => Get(ProjectClips(window).First(c => (string)Get(c, "Text") == "효과 없음"), "Effects")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task ShakeEffect_HasParamRows_ButNoPersonOnly()
        {
            // 흔들림·줌 효과는 그림 전체를 움직여 사람 자리만 따로 움직일 수 없다 — 속성 패널에 [사람에게만]이 없다
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await RedAtOneSecond(ui, window);
                ui.Run(() =>
                {
                    var canvas = Canvas(window);
                    var handler = (Delegate)canvas.GetType().GetField("VideoEffectDropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
                    handler.DynamicInvoke("camera-shake", Get(Red(window), "Id"));
                });
                await ui.WaitUntil(() => Effects(window)?.Count == 1, Wait, "카메라 흔들림 넣기");
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "Effect0_horizontal"), Wait, "가로 세기 줄");
                ui.Run(() =>
                {
                    Assert.Equal("camera-shake", Get(Effects(window)[0], "Id"));
                    Assert.Equal("18", Ui.ById<TextBox>(window, "Effect0_horizontal").Text);
                    Assert.NotNull(Ui.ById<TextBox>(window, "Effect0_rotate"));
                    Assert.Null(Ui.ById<CheckBox>(window, "EffectPersonOnly"));
                    Ui.Snap(window, plugin.ShotFolder, "video-effect-camera-shake");
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        // ---------- 새 효과("새로" 표시)·효과 칸 미리 보기 ----------

        private static void OpenEffectsTab(UiThread ui, Window window)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedItem = Ui.ById<TabItem>(window, "EffectsTab"));
        }

        private static void Enter(Window window, string id)
        {
            Tile(window, id).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        }

        private static void Leave(Window window, string id)
        {
            Tile(window, id).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        }

        private static Border HoverLabel(Window window)
        {
            return Ui.ById<Border>(window, "EffectHoverPreview");
        }

        [EditorFact]
        public async Task NewEffects_BadgesNoticeAndHeaderMark_ShowFilterAndDismiss_PersistAcrossReopening()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var catalog = Catalog(plugin);
            var groups = catalog.Select(c => c.Group).Distinct().ToList();
            // 화면 차례(묶음마다)의 새 효과: 스캔 라인 뒤에 더한 13개
            var news = catalog.Where(c => c.Since > 1).OrderBy(c => groups.IndexOf(c.Group)).ToList();
            Assert.Equal(13, news.Count);
            // 본 기록이 없는 사용자(예전부터 쓰던 사람)
            ForgetSeen(plugin);
            Window window = null;
            try
            {
                window = await Open(ui, plugin);
                // 효과 탭을 열기 전에도 머리글에 강조색 점(자동화 이름에 개수)
                ui.Run(() =>
                {
                    Assert.Equal("효과 (새 효과 13개)", EffectsHeader(window));
                    Assert.NotNull(Ui.ById<TextBlock>(window, "EffectsTabNewMark"));
                });
                OpenEffectsTab(ui, window);
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록");
                ui.Run(() =>
                {
                    Ui.ById<ScrollViewer>(window, "EffectsTabScroll").UpdateLayout();
                    // 13개 칸에 "새로"(스캔 라인 — 처음부터 있던 효과 — 에는 없음), 맨 위 알림 줄, 묶음에 "새로 추가됨"
                    Assert.Equal(news.Select(c => c.Id), Badged(window));
                    Assert.Equal(Visibility.Collapsed, Badge(window, "scan-line").Visibility);
                    Assert.Equal("새로 추가된 효과", System.Windows.Automation.AutomationProperties.GetHelpText(Tile(window, news[0].Id)));
                    Assert.Equal("", System.Windows.Automation.AutomationProperties.GetHelpText(Tile(window, "scan-line")));
                    var notice = Ui.ById<Border>(window, "VideoEffectNewNotice");
                    Assert.Equal(Visibility.Visible, notice.Visibility);
                    Assert.Equal("새 동영상 효과 13개가 추가되었습니다", Ui.ById<TextBlock>(window, "VideoEffectNewText").Text);
                    Assert.True(Ui.ById<ListBox>(window, "VideoEffectList").TranslatePoint(new Point(), notice).Y >= notice.ActualHeight, "알림 줄은 목록 위에");
                    Assert.Equal(new[] { "모든 효과", "새로 추가됨" }.Concat(groups), Ui.ById<ComboBox>(window, "VideoEffectGroup").Items.Cast<string>());
                    Assert.Equal("모든 효과", Ui.ById<ComboBox>(window, "VideoEffectGroup").SelectedItem);
                });
                await ui.WaitUntil(() => catalog.All(c => Thumbnail(window, c.Id) != null), Wait, "모든 썸네일");
                ui.Run(() =>
                {
                    Ui.Snap(window, plugin.ShotFolder, "video-effects-new");
                    SnapTab(window, plugin.ShotFolder, "video-effects-new-full");
                });

                // [보기]: 묶음이 "새로 추가됨" — 새 효과 13개만(묶음 제목은 원래 묶음), 고른 칸이 늘 있다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "VideoEffectNewShow")));
                await ui.WaitUntil(() => Ui.ById<ComboBox>(window, "VideoEffectGroup").SelectedItem as string == "새로 추가됨", Wait, "새로 추가됨 고르기");
                ui.Run(() =>
                {
                    Ui.ById<ScrollViewer>(window, "EffectsTabScroll").UpdateLayout();
                    Assert.Equal(news.Select(c => "VideoEffect_" + c.Id), Tiles(window));
                    Assert.Equal(news.Select(c => c.Group).Distinct(), GroupHeaders(window));
                    Assert.NotNull(Ui.ById<ListBox>(window, "VideoEffectList").SelectedItem);
                    Ui.Snap(window, plugin.ShotFolder, "video-effects-new-filter");
                    SnapTab(window, plugin.ShotFolder, "video-effects-new-filter-full");
                });

                // [확인]: 모두 본 것 — "새로"·알림 줄·머리글 점·"새로 추가됨"이 사라지고 모든 효과로 돌아간다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "VideoEffectNewDismiss")));
                await ui.WaitUntil(() => !Ui.ById<ComboBox>(window, "VideoEffectGroup").Items.Cast<string>().Contains("새로 추가됨"), Wait, "새로 추가됨 빼기");
                ui.Run(() =>
                {
                    Assert.Empty(Badged(window));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<Border>(window, "VideoEffectNewNotice").Visibility);
                    Assert.Equal("효과", EffectsHeader(window));
                    Assert.Equal("효과", Ui.ById<TabItem>(window, "EffectsTab").Header);
                    Assert.Null(Ui.ById<TextBlock>(window, "EffectsTabNewMark"));
                    Assert.Equal(new[] { "모든 효과" }.Concat(groups), Ui.ById<ComboBox>(window, "VideoEffectGroup").Items.Cast<string>());
                    Assert.Equal("모든 효과", Ui.ById<ComboBox>(window, "VideoEffectGroup").SelectedItem);
                    Assert.Equal(catalog.Count, Tiles(window).Count);
                    Ui.Snap(window, plugin.ShotFolder, "video-effects-new-dismissed");
                });
                // 설정에 남는다(모든 효과, 목록 차례)
                Assert.Equal("[" + string.Join(",", catalog.Select(c => "\"" + c.Id + "\"")) + "]", plugin.Manager.GetSetting(SeenKey));

                // 편집 창을 다시 열어도 그대로
                await CloseEditor(ui, window);
                window = null;
                window = await Open(ui, plugin);
                Assert.Equal("효과", ui.Run(() => EffectsHeader(window)));
                OpenEffectsTab(ui, window);
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록(다시 열기)");
                ui.Run(() =>
                {
                    Assert.Empty(Badged(window));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<Border>(window, "VideoEffectNewNotice").Visibility);
                    Assert.DoesNotContain("새로 추가됨", Ui.ById<ComboBox>(window, "VideoEffectGroup").Items.Cast<string>());
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                ForgetSeen(plugin);
            }
        }

        [EditorFact]
        public async Task NewEffects_ApplyingOneClearsOnlyItsBadge_ByDoubleClickButtonOrDrop()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var catalog = Catalog(plugin);
            var groups = catalog.Select(c => c.Group).Distinct().ToList();
            var expected = catalog.Where(c => c.Since > 1).OrderBy(c => groups.IndexOf(c.Group)).Select(c => c.Id).ToList();
            ForgetSeen(plugin);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, "red.png");
                OpenEffectsTab(ui, window);
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록");
                ui.Run(() => Assert.Equal(expected, Badged(window)));

                // 두 번 누르기 → 그 칸의 "새로"만 사라진다(알림 줄의 개수·머리글도 하나 줄어듦)
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("red.png"));
                    Tile(window, "rgb-split").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                });
                await ui.WaitUntil(() => Effects(window)?.Count == 1, Wait, "두 번 눌러 넣기");
                expected.Remove("rgb-split");
                ui.Run(() =>
                {
                    Assert.Equal(expected, Badged(window));
                    Assert.Equal("새 동영상 효과 12개가 추가되었습니다", Ui.ById<TextBlock>(window, "VideoEffectNewText").Text);
                    Assert.Equal("효과 (새 효과 12개)", EffectsHeader(window));
                });

                // [고른 클립에 넣기]
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("red.png"));
                    Ui.ById<ListBox>(window, "VideoEffectList").SelectedItem = Tile(window, "heartbeat");
                    Ui.Click(Ui.ById<Button>(window, "VideoEffectApplyButton"));
                });
                await ui.WaitUntil(() => Effects(window)?.Count == 2, Wait, "단추로 넣기");
                expected.Remove("heartbeat");
                ui.Run(() => Assert.Equal(expected, Badged(window)));

                // 타임라인의 클립에 끌어 놓기(끌어 놓기 이벤트를 만들 수 없어 처리기를 직접 부른다)
                ui.Run(() =>
                {
                    var canvas = Canvas(window);
                    var handler = (Delegate)canvas.GetType().GetField("VideoEffectDropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
                    handler.DynamicInvoke("glitch-scan", Get(Red(window), "Id"));
                });
                await ui.WaitUntil(() => Effects(window)?.Count == 3, Wait, "끌어 놓아 넣기");
                expected.Remove("glitch-scan");
                ui.Run(() =>
                {
                    Assert.Equal(expected, Badged(window));
                    Assert.Equal("새 동영상 효과 10개가 추가되었습니다", Ui.ById<TextBlock>(window, "VideoEffectNewText").Text);
                    Assert.Equal("효과 (새 효과 10개)", EffectsHeader(window));
                });
                // 설정에는 처음 판의 효과와 넣은 셋(목록 차례)
                Assert.Equal("[\"scan-line\",\"glitch-scan\",\"rgb-split\",\"heartbeat\"]", plugin.Manager.GetSetting(SeenKey));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                ForgetSeen(plugin);
            }
        }

        [EditorFact]
        public async Task HoverPreview_ShowsTheEffectOnTheSelectedClip_LeavingRestores_ProjectUntouched()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            ForgetSeen(plugin);
            var window = await Open(ui, plugin);
            try
            {
                // 빨간 사진(정지 그림)을 고르고 재생 헤드 1초 — 효과 탭을 연다
                await RedAtOneSecond(ui, window);
                OpenEffectsTab(ui, window);
                await ui.WaitFor(() => Ui.ById<ListBox>(window, "VideoEffectList"), Wait, "동영상 효과 목록");
                await Task.Delay(300);
                var snapshot = plugin.Assembly.GetType("VideoEditor.Core.ProjectFile", true).GetMethod("Snapshot");
                string Snapshot() => (string)snapshot.Invoke(null, new[] { Project(window) });
                var (original, source, before, canUndo, dirty) = ui.Run(() => (PreviewPixels(window), Ui.ById<Image>(window, "PreviewImage").Source, Snapshot(),
                    (bool)Get(Session(window), "CanUndo"), (bool)Get(Session(window), "Dirty")));
                Assert.NotNull(original);

                // SF 스캔 칸에 마우스를 올려 둔다: 머무는 동안은 그대로, 잠깐 뒤 미리 보기 화면이 사진에 효과를 얹은 그림(절반 크기)으로 바뀌고 위에 이름
                var watch = Stopwatch.StartNew();
                ui.Run(() =>
                {
                    Enter(window, "scifi-scan");
                    Assert.Equal(Visibility.Collapsed, HoverLabel(window).Visibility);
                    Assert.Same(source, Ui.ById<Image>(window, "PreviewImage").Source);
                });
                await ui.WaitUntil(() => HoverLabel(window).Visibility == Visibility.Visible, Wait, "효과 칸 미리 보기");
                var shown = watch.Elapsed;
                var first = ui.Run(() =>
                {
                    Assert.Equal("미리 보기: SF 스캔", System.Windows.Automation.AutomationProperties.GetName(HoverLabel(window)));
                    var picture = (BitmapSource)Ui.ById<Image>(window, "PreviewImage").Source;
                    Assert.NotSame(source, picture);
                    Assert.InRange(picture.PixelWidth, ((BitmapSource)source).PixelWidth / 2 - 2, ((BitmapSource)source).PixelWidth / 2);
                    Assert.True(Differs(original, PreviewPixels(window)));
                    return PreviewPixels(window);
                });
                // 정지 사진인데 장이 바뀐다 — 효과가 그려지고 움직인다
                await ui.WaitUntil(() => Differs(first, PreviewPixels(window)), Wait, "미리 보기 움직임");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "video-effect-hover-preview"));
                // 1초 넘게 보이면 본 효과 — 그 칸의 "새로"만 사라진다
                await ui.WaitUntil(() => Badge(window, "scifi-scan").Visibility == Visibility.Collapsed, Wait, "미리 본 효과");
                ui.Run(() => Assert.Equal(Visibility.Visible, Badge(window, "rgb-split").Visibility));

                // 마우스가 떠나면 바로 원래 미리 보기(같은 그림), 늦게 온 장이 다시 덮지 않는다
                ui.Run(() =>
                {
                    Leave(window, "scifi-scan");
                    Assert.Equal(Visibility.Collapsed, HoverLabel(window).Visibility);
                    Assert.Same(source, Ui.ById<Image>(window, "PreviewImage").Source);
                    Assert.False(Differs(original, PreviewPixels(window)));
                });
                await Task.Delay(600);
                ui.Run(() =>
                {
                    Assert.Equal(Visibility.Collapsed, HoverLabel(window).Visibility);
                    Assert.Same(source, Ui.ById<Image>(window, "PreviewImage").Source);
                    Assert.False(Differs(original, PreviewPixels(window)));
                    // 프로젝트·실행 취소·저장 안 함 표시는 그대로
                    Assert.Equal(before, Snapshot());
                    Assert.Equal(canUndo, (bool)Get(Session(window), "CanUndo"));
                    Assert.Equal(dirty, (bool)Get(Session(window), "Dirty"));
                    Assert.Null(Effects(window));
                });

                // 보이는 중에 고르기를 풀면 바로 끝난다
                ui.Run(() => Enter(window, "rgb-split"));
                await ui.WaitUntil(() => HoverLabel(window).Visibility == Visibility.Visible, Wait, "두 번째 미리 보기");
                ui.Run(() =>
                {
                    Session(window).GetType().GetMethod("ClearSelection").Invoke(Session(window), null);
                    Assert.Equal(Visibility.Collapsed, HoverLabel(window).Visibility);
                    Assert.Same(source, Ui.ById<Image>(window, "PreviewImage").Source);
                    Leave(window, "rgb-split");
                });

                // 고른 클립이 없으면 아무것도 하지 않는다
                ui.Run(() => Enter(window, "bad-tv"));
                await Task.Delay(1500);
                ui.Run(() =>
                {
                    Assert.Equal(Visibility.Collapsed, HoverLabel(window).Visibility);
                    Assert.Same(source, Ui.ById<Image>(window, "PreviewImage").Source);
                    Assert.Equal(Visibility.Visible, Badge(window, "bad-tv").Visibility);
                    Leave(window, "bad-tv");
                });

                // 글자 클립만 골랐을 때도(효과를 넣을 수 없는 클립)
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "미리 보기 없음";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.Contains("미리 보기 없음")), Wait, "글자 클립");
                OpenEffectsTab(ui, window);
                await Task.Delay(500);
                var withText = ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("미리 보기 없음"));
                    Enter(window, "bad-tv");
                    return Ui.ById<Image>(window, "PreviewImage").Source;
                });
                await Task.Delay(1500);
                ui.Run(() =>
                {
                    Assert.Equal(Visibility.Collapsed, HoverLabel(window).Visibility);
                    Assert.Same(withText, Ui.ById<Image>(window, "PreviewImage").Source);
                    Leave(window, "bad-tv");
                });
                File.WriteAllText(Path.Combine(plugin.ShotFolder, "video-effect-hover-preview.txt"),
                    "마우스를 올린 뒤 첫 장까지 " + Math.Round(shown.TotalMilliseconds) + "ms(머무는 350ms 포함)");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                ForgetSeen(plugin);
            }
        }
    }
}
