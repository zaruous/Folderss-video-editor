using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        private static ImageSource Thumbnail(Window window, string id)
        {
            var tile = Ui.ById<ListBox>(window, "VideoEffectList")?.Items.Cast<ListBoxItem>()
                .FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "VideoEffect_" + id);
            return tile == null ? null : ((Image)((Border)((StackPanel)tile.Content).Children[0]).Child).Source;
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
                ui.Run(() =>
                {
                    SelectClip(window, n => n.Contains("효과 없음"));
                    Ui.Click(Ui.ById<Button>(window, "VideoEffectApplyButton"));
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
    }
}
