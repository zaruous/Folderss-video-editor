using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 사운드 분리(이슈 #5): 이름(사운드 분리 · Ctrl+Alt+D), 미디어 패널 [사운드 분리해서 배치], 링크(함께 고르기·옮기기, Alt로 따로),
    /// 링크 해제·어긋남 표시·싱크 맞추기, [사운드 합치기], [사운드를 파일로 저장…](원본 코덱 그대로 · 클립 구간 WAV).
    /// 타임라인 오른쪽 메뉴는 마우스 없이 EditorView.ClipMenu로 만들어 누른다.
    /// </summary>
    public class SoundSplitTests
    {
        private readonly ITestOutputHelper _out;

        public SoundSplitTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- 도우미 ----------

        private static async Task Import(UiThread ui, Window window, string path)
        {
            var name = Path.GetFileName(path);
            ui.Post(() => Key(window, System.Windows.Input.Key.I, ModifierKeys.Control));
            await NativeDialogs.Open(ui, path, Wait);
            await ui.WaitUntil(() => MediaItem(window, name) != null, Wait, name + " 가져오기");
        }

        private static FrameworkElement MediaItem(Window window, string name)
        {
            return Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetName(i) == name);
        }

        private static async Task AddToTimeline(UiThread ui, Window window, string name)
        {
            var before = ui.Run(() => Clips(window).Count);
            ui.Run(() =>
            {
                Ui.ById<ListBox>(window, "MediaList").SelectedItem = MediaItem(window, name);
                Ui.Click(Ui.ById<Button>(window, "AddToTimelineButton"));
            });
            await ui.WaitUntil(() => Clips(window).Count == before + 1, Wait, name + " 클립 추가");
        }

        private static object VideoClip(Window window)
        {
            return ProjectClips(window).First(c => !(bool)Get(c, "AudioOnly"));
        }

        private static object SoundClip(Window window)
        {
            return ProjectClips(window).First(c => (bool)Get(c, "AudioOnly"));
        }

        private static int SelectedCount(Window window)
        {
            return ((IEnumerable)Get(Session(window), "SelectedIds")).Cast<object>().Count();
        }

        /// <summary>타임라인 오른쪽 메뉴(오른쪽 클릭과 같은 EditorView.ClipMenu)를 그 클립으로 만든다. 고르기는 시험이 먼저 한다.</summary>
        private static ContextMenu ClipMenu(Window window, object clip)
        {
            var view = Ui.ById<FrameworkElement>(window, "VideoEditorView");
            var project = Project(window);
            var track = project.GetType().GetMethod("TrackOf").Invoke(project, new[] { clip });
            return (ContextMenu)view.GetType().GetMethod("ClipMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, new[] { clip, track, (object)0L });
        }

        private static List<string> MenuNames(ContextMenu menu)
        {
            return menu.Items.OfType<MenuItem>().Select(Ui.HeaderText).ToList();
        }

        /// <summary>메뉴를 열고 머리글이 이 글로 시작하는 항목을 누른다(Ui.ContextMenu와 같은 순서 — 열고, 닫고, 누름).</summary>
        private static void InvokeMenu(ContextMenu menu, string startsWith)
        {
            menu.IsOpen = true;
            var item = menu.Items.OfType<MenuItem>().FirstOrDefault(m => Ui.HeaderText(m).StartsWith(startsWith, StringComparison.Ordinal));
            menu.IsOpen = false;
            if (item == null)
                throw new InvalidOperationException("메뉴 항목 '" + startsWith + "'가 없습니다. 있는 것: " + string.Join(" · ", MenuNames(menu)));
            Ui.Invoke(item);
        }

        /// <summary>프레임 수 → 타임라인 픽셀(지금 확대 수준).</summary>
        private static double Px(Window window, long frames)
        {
            var canvas = Canvas(window);
            var method = canvas.GetType().GetMethod("FrameToX");
            return (double)method.Invoke(canvas, new object[] { frames }) - (double)method.Invoke(canvas, new object[] { 0L });
        }

        private static Point Center(Window window, object clip)
        {
            var bounds = ClipBounds(window, clip);
            return new Point(bounds.Left + Math.Round(bounds.Width / 2), bounds.Top + bounds.Height / 2);
        }

        /// <summary>Alt를 누른 채 끌기 시작한다(그 한 번만 링크된 짝 없이 이 클립만, 붙기도 꺼짐).</summary>
        private static void DragAlone(Window window, Point from, Point to)
        {
            var canvas = Canvas(window);
            var type = canvas.GetType();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetMethod("PointerDown", flags).Invoke(canvas, new object[] { from, ModifierKeys.Alt });
            type.GetMethod("PointerMove", flags).Invoke(canvas, new object[] { new Point((from.X + to.X) / 2, from.Y), ModifierKeys.Alt });
            type.GetMethod("PointerMove", flags).Invoke(canvas, new object[] { to, ModifierKeys.Alt });
            type.GetMethod("PointerUp", flags).Invoke(canvas, new object[] { to, ModifierKeys.Alt });
        }

        /// <summary>미디어 파일을 타임라인 트랙의 frame에 끌어 놓은 것처럼(TimelineCanvas.MediaDropped 처리기를 직접 부른다).</summary>
        private static void DropMedia(Window window, string path, object track, long frame)
        {
            var canvas = Canvas(window);
            var handler = (Delegate)canvas.GetType().GetField("MediaDropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
            handler.DynamicInvoke(new[] { path }, track, frame);
        }

        private static object AudioTrack(Window window)
        {
            return ((IList)Get(Project(window), "AudioTracks"))[0];
        }

        /// <summary>타임라인 이름에서 " · 링크"부터 끝(링크·어긋남 표시)을 뗀다.</summary>
        private static string LinkSuffix(string name)
        {
            var at = name.IndexOf(" · 링크", StringComparison.Ordinal);
            return at < 0 ? "" : name.Substring(at);
        }

        /// <summary>오른쪽 옆을 frames만큼 끈다(음수면 왼쪽). 끄는 동안 Alt(붙기 끔).</summary>
        private static void TrimEnd(Window window, object clip, long frames)
        {
            var bounds = ClipBounds(window, clip);
            var from = new Point(bounds.Right - 2, bounds.Top + bounds.Height / 2);
            Drag(window, from, new Point(from.X + Px(window, frames), from.Y));
        }

        /// <summary>ffmpeg volumedetect의 평균 음량(dB).</summary>
        private static double MeanVolume(string path)
        {
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(FfmpegFolder, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-nostats", "-i", path, "-af", "volumedetect", "-f", "null", "-" })
                info.ArgumentList.Add(arg);
            using (var process = System.Diagnostics.Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                var match = System.Text.RegularExpressions.Regex.Match(error, @"mean_volume:\s*(-?[\d.]+) dB");
                Assert.True(match.Success, "volumedetect: " + error);
                return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static async Task DetachWithShortcut(UiThread ui, Window window)
        {
            ui.Run(() =>
            {
                SelectClip(window, n => n.Contains("testsrc.mp4"));
                Key(window, System.Windows.Input.Key.D, ModifierKeys.Control | ModifierKeys.Alt);
            });
            await ui.WaitUntil(() => Clips(window).Count == 2, Wait, "Ctrl+Alt+D로 사운드 분리");
        }

        // ---------- 시험 ----------

        /// <summary>미디어 패널 [사운드 분리해서 배치]: 영상은 동영상 1, 사운드는 오디오 1의 재생 헤드에 — 여러 개면 차례로. 실행 취소 한 번에 모두 사라진다.</summary>
        [EditorFact]
        public async Task MediaPanel_PlaceSoundSplit_PutsVideoAndSound_OneUndoRemovesAll()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var second = Path.Combine(plugin.Root, "second.mp4");
            File.Copy(Path.Combine(MediaFolder, "testsrc.mp4"), second, true);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await Import(ui, window, Path.Combine(MediaFolder, "red.png"));
                await Import(ui, window, second);
                ui.Run(() =>
                {
                    // 소리가 없는 사진에는 사운드 항목이 없다
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = MediaItem(window, "red.png");
                    var names = Ui.ContextMenu(list, null);
                    Assert.DoesNotContain(names, n => n.StartsWith("사운드", StringComparison.Ordinal));
                    list.SelectedItem = MediaItem(window, "testsrc.mp4");
                    names = Ui.ContextMenu(list, null);
                    Assert.Contains("사운드 분리해서 배치", names);
                    Assert.Contains("사운드를 파일로 저장…", names);
                });

                // 하나: 재생 헤드(0)에 영상·사운드
                ui.Run(() => Ui.ContextMenu(Ui.ById<ListBox>(window, "MediaList"), "사운드 분리해서 배치"));
                await ui.WaitUntil(() => Clips(window).Count == 2, Wait, "영상·사운드 놓임");
                var clips = ui.Run(() => Clips(window));
                _out.WriteLine(string.Join("\n", clips));
                Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00 · 링크", clips);
                Assert.Contains("오디오 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00 · 링크", clips);
                ui.Run(() =>
                {
                    Assert.True((bool)Get(VideoClip(window), "AudioDetached"));
                    // 놓은 짝이 함께 골라져 있다
                    Assert.Equal(2, SelectedCount(window));
                    Ui.Snap(window, plugin.ShotFolder, "sound-split-placed");
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => Clips(window).Count == 0, Wait, "실행 취소 한 번에 둘 다 사라짐");

                // 여럿(사진 포함): 소리가 있는 동영상만, 고른 순서대로 이어서
                ui.Run(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItems.Clear();
                    list.SelectedItems.Add(MediaItem(window, "testsrc.mp4"));
                    list.SelectedItems.Add(MediaItem(window, "red.png"));
                    list.SelectedItems.Add(MediaItem(window, "second.mp4"));
                    Ui.ContextMenu(list, "사운드 분리해서 배치");
                });
                await ui.WaitUntil(() => Clips(window).Count == 4, Wait, "둘씩 차례로 놓임");
                clips = ui.Run(() => Clips(window));
                _out.WriteLine(string.Join("\n", clips));
                Assert.Contains("동영상 1 · second.mp4 · 00:00:03:00–00:00:06:00 · 링크", clips);
                Assert.Contains("오디오 1 · second.mp4 · 00:00:03:00–00:00:06:00 · 링크", clips);
                Assert.Contains("건너뛰었습니다", ui.Run(() => Status(window)));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Clips(window).Count == 0, Wait, "실행 취소 한 번에 넷 다 사라짐");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>이름이 "사운드 분리"(메뉴·도구 막대·속성 안내)이고 Ctrl+Alt+D가 그대로 동작한다. [사운드 합치기]로 되돌리고 실행 취소하면 다시 나뉜다.</summary>
        [EditorFact]
        public async Task Shortcut_RenamedEverywhere_RejoinRestoresVideoSound()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await AddToTimeline(ui, window, "testsrc.mp4");
                ui.Run(() =>
                {
                    var edit = Ui.Find<Menu>(window).Items.OfType<MenuItem>().First(m => Ui.HeaderText(m) == "편집");
                    var item = edit.Items.OfType<MenuItem>().First(m => Ui.HeaderText(m) == "사운드 분리");
                    Assert.Equal("Ctrl+Alt+D", item.InputGestureText);
                    Assert.DoesNotContain(edit.Items.OfType<MenuItem>(), m => Ui.HeaderText(m).Contains("오디오 분리"));
                    Assert.Equal("사운드 분리 (Ctrl+Alt+D)", (string)Ui.ById<Button>(window, "DetachAudioButton").ToolTip);
                    SelectClip(window, n => n.Contains("testsrc.mp4"));
                    Assert.Contains("사운드 분리", MenuNames(ClipMenu(window, VideoClip(window))));
                });
                await DetachWithShortcut(ui, window);
                Assert.StartsWith("사운드를 분리했습니다", ui.Run(() => Status(window)));
                ui.Run(() =>
                {
                    Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00 · 링크", Clips(window));
                    Assert.Contains("오디오 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00 · 링크", Clips(window));
                    // 사운드를 누르면 속성 패널은 사운드(둘이 골라져도)
                    SelectClip(window, n => n.StartsWith("오디오 1", StringComparison.Ordinal));
                    Assert.Equal(2, SelectedCount(window));
                });
                await ui.WaitUntil(() => (Ui.ById<TextBlock>(window, "ClipTimingText")?.Text ?? "").StartsWith("분리한 사운드", StringComparison.Ordinal), Wait, "속성: 분리한 사운드");
                ui.Run(() => SelectClip(window, n => n.StartsWith("동영상 1", StringComparison.Ordinal)));
                await ui.WaitUntil(() => Ui.FindAll<TextBlock>(window, t => (t.Text ?? "").StartsWith("사운드를 분리해 오디오 트랙에 두었습니다", StringComparison.Ordinal)).Count > 0, Wait, "속성 패널 안내");

                // 오른쪽 메뉴: 분리는 이미 했으므로 없고 합치기·링크 해제·파일로 저장
                var names = ui.Run(() => MenuNames(ClipMenu(window, VideoClip(window))));
                _out.WriteLine(string.Join(" · ", names));
                Assert.DoesNotContain("사운드 분리", names);
                Assert.Contains("사운드 합치기", names);
                Assert.Contains("링크 해제", names);
                Assert.Contains("사운드를 파일로 저장…", names);
                ui.Run(() => InvokeMenu(ClipMenu(window, VideoClip(window)), "사운드 합치기"));
                await ui.WaitUntil(() => Clips(window).Count == 1, Wait, "사운드 합치기");
                ui.Run(() =>
                {
                    var video = ProjectClips(window).Single();
                    Assert.False((bool)Get(video, "AudioDetached"));
                    Assert.Null(Get(video, "LinkId"));
                    Assert.Equal("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00", Clips(window).Single());
                    Assert.StartsWith("사운드를 합쳤습니다", Status(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => Clips(window).Count == 2, Wait, "실행 취소로 다시 나뉨");
                ui.Run(() => Assert.Contains("오디오 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00 · 링크", Clips(window)));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>
        /// 링크: 사운드만 눌러도 둘 다 고르고, 영상을 끌면 사운드도 함께 옮긴다. Alt로 끌기 시작하면 그 한 번만 따로(어긋남 표시).
        /// [링크 해제] 뒤에는 따로 고르고 따로 움직이며, [싱크 맞추기]로 되돌리고 [다시 링크]로 묶는다.
        /// </summary>
        [EditorFact]
        public async Task LinkedClips_MoveTogether_AltMovesAlone_UnlinkOffsetAndSync()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await AddToTimeline(ui, window, "testsrc.mp4");
                await DetachWithShortcut(ui, window);
                // 마그네틱을 끄고 주 트랙에서 자유롭게 옮긴다
                ui.Run(() => Ui.ClickToggle(Ui.ById<ToggleButton>(window, "MagneticToggle")));
                await ui.WaitUntil(() => !(bool)Get(Project(window), "Magnetic"), Wait, "마그네틱 끔");
                ui.Run(() =>
                {
                    SelectClip(window, n => n.StartsWith("오디오 1", StringComparison.Ordinal));
                    Assert.Equal(2, SelectedCount(window));
                    // 영상을 1초(30프레임) 오른쪽으로 — Drag는 끄는 동안 Alt(붙기만 끔), 시작은 Alt 없이
                    var from = Center(window, VideoClip(window));
                    Drag(window, from, new Point(from.X + Px(window, 30), from.Y));
                });
                await ui.WaitUntil(() => Clips(window).Contains("동영상 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크"), Wait, "영상 옮김");
                ui.Run(() => Assert.Contains("오디오 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크", Clips(window)));

                // Alt로 끌기 시작: 사운드만 5프레임 → 두 클립에 어긋남
                ui.Run(() =>
                {
                    var from = Center(window, SoundClip(window));
                    DragAlone(window, from, new Point(from.X + Px(window, 5), from.Y));
                });
                await ui.WaitUntil(() => Clips(window).Contains("오디오 1 · testsrc.mp4 · 00:00:01:05–00:00:04:05 · 링크 · 어긋남 +5f"), Wait, "Alt로 사운드만");
                ui.Run(() => Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크 · 어긋남 −5f", Clips(window)));

                // 링크 해제 → 사운드만 고르고 옮긴다
                ui.Run(() => InvokeMenu(ClipMenu(window, VideoClip(window)), "링크 해제"));
                await ui.WaitUntil(() => Clips(window).Contains("동영상 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크 해제 · 어긋남 −5f"), Wait, "링크 해제");
                ui.Run(() =>
                {
                    SelectClip(window, n => n.StartsWith("오디오 1", StringComparison.Ordinal));
                    Assert.Equal(1, SelectedCount(window));
                    var from = Center(window, SoundClip(window));
                    Drag(window, from, new Point(from.X + Px(window, 15), from.Y));
                });
                await ui.WaitUntil(() => Clips(window).Contains("오디오 1 · testsrc.mp4 · 00:00:01:20–00:00:04:20 · 링크 해제 · 어긋남 +20f"), Wait, "따로 옮김");
                ui.Run(() =>
                {
                    Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크 해제 · 어긋남 −20f", Clips(window));
                    Ui.Snap(window, plugin.ShotFolder, "sound-split-offset");
                });

                // 싱크 맞추기(메뉴에 어긋남이 보인다) → 다시 링크
                var names = ui.Run(() => MenuNames(ClipMenu(window, SoundClip(window))));
                Assert.Contains("싱크 맞추기 (+20f)", names);
                Assert.Contains("다시 링크", names);
                ui.Run(() => InvokeMenu(ClipMenu(window, SoundClip(window)), "싱크 맞추기"));
                await ui.WaitUntil(() => Clips(window).Contains("오디오 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크 해제"), Wait, "싱크 맞춤");
                ui.Run(() => InvokeMenu(ClipMenu(window, SoundClip(window)), "다시 링크"));
                await ui.WaitUntil(() => Clips(window).Contains("오디오 1 · testsrc.mp4 · 00:00:01:00–00:00:04:00 · 링크"), Wait, "다시 링크");
                ui.Run(() =>
                {
                    SelectClip(window, n => n.StartsWith("오디오 1", StringComparison.Ordinal));
                    Assert.Equal(2, SelectedCount(window));
                    // 삭제도 함께
                    Key(window, System.Windows.Input.Key.Delete);
                });
                await ui.WaitUntil(() => Clips(window).Count == 0, Wait, "링크된 둘 다 삭제");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>
        /// 짝 다듬기(#31, Windows 실측): 링크된 영상의 끝을 끌어 늘일 때 사운드가 오디오 트랙의 다음 소리에 막히면 둘 다 그 자리에서 멈춘다(길이가 같고 어긋남 없음).
        /// </summary>
        [EditorFact]
        public async Task LinkedTrim_PartnerBlockedByNextSound_BothStopTogether()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await AddToTimeline(ui, window, "testsrc.mp4");
                await DetachWithShortcut(ui, window);
                // 영상 끝을 1초 줄이면 사운드도 함께 0~2초
                ui.Run(() => TrimEnd(window, VideoClip(window), -30));
                await ui.WaitUntil(() => Clips(window).Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:02:00 · 링크")
                                         && Clips(window).Contains("오디오 1 · testsrc.mp4 · 00:00:00:00–00:00:02:00 · 링크"), Wait, "함께 줄임");
                // 오디오 1의 2초 10프레임(70)에 다른 소리
                ui.Run(() => DropMedia(window, Path.Combine(MediaFolder, "tone.wav"), AudioTrack(window), 70));
                await ui.WaitUntil(() => Clips(window).Any(n => n.StartsWith("오디오 1 · tone.wav · 00:00:02:10–", StringComparison.Ordinal)), Wait, "다음 소리");
                // 영상 끝을 다시 1초 늘인다 → 사운드가 70에 막혀 둘 다 70에서 멈춤
                ui.Run(() =>
                {
                    SelectClip(window, n => n.StartsWith("동영상 1 · testsrc.mp4", StringComparison.Ordinal));
                    TrimEnd(window, VideoClip(window), 30);
                });
                await ui.WaitUntil(() => Clips(window).Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:02:10 · 링크"), Wait, "영상이 막힌 자리에서 멈춤");
                ui.Run(() =>
                {
                    Assert.Contains("오디오 1 · testsrc.mp4 · 00:00:00:00–00:00:02:10 · 링크", Clips(window));
                    Assert.DoesNotContain(Clips(window), n => n.Contains("어긋남"));
                    Ui.Snap(window, plugin.ShotFolder, "sound-split-trim-blocked");
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>
        /// 붙여 넣기 경계(#31, Windows 실측): 사운드가 영상보다 앞선 링크 짝을 잘라 마그네틱 주 트랙 0초에 붙여 넣으면
        /// 영상은 0으로 당겨지고, 사운드는 0 앞 부분이 잘려 0부터 놓인다. 원래의 어긋남(값)은 그대로이고 새 어긋남은 생기지 않는다.
        /// </summary>
        [EditorFact]
        public async Task Paste_SoundBeforeZero_CutsTheHead_KeepsTheOffset()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await AddToTimeline(ui, window, "testsrc.mp4");
                await DetachWithShortcut(ui, window);
                ui.Run(() => Ui.ClickToggle(Ui.ById<ToggleButton>(window, "MagneticToggle")));
                await ui.WaitUntil(() => !(bool)Get(Project(window), "Magnetic"), Wait, "마그네틱 끔");
                // Alt로 영상만 15프레임 오른쪽 → 사운드가 영상보다 15프레임 앞선 짝
                ui.Run(() =>
                {
                    var from = Center(window, VideoClip(window));
                    DragAlone(window, from, new Point(from.X + Px(window, 15), from.Y));
                });
                await ui.WaitUntil(() => Clips(window).Any(n => n.StartsWith("동영상 1 · testsrc.mp4 · 00:00:00:15–00:00:03:15 · 링크 · 어긋남", StringComparison.Ordinal)), Wait, "영상만 옮김");
                var (videoSuffix, soundSuffix) = ui.Run(() => (LinkSuffix(Clips(window).First(n => n.StartsWith("동영상 1", StringComparison.Ordinal))),
                                                               LinkSuffix(Clips(window).First(n => n.StartsWith("오디오 1", StringComparison.Ordinal)))));
                // 둘 다 잘라 내고, 마그네틱을 켠 뒤 0초에 붙여 넣는다
                ui.Run(() =>
                {
                    SelectClip(window, n => n.StartsWith("오디오 1", StringComparison.Ordinal));
                    Assert.Equal(2, SelectedCount(window));
                    Key(window, System.Windows.Input.Key.X, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => Clips(window).Count == 0, Wait, "잘라 내기");
                ui.Run(() => Ui.ClickToggle(Ui.ById<ToggleButton>(window, "MagneticToggle")));
                await ui.WaitUntil(() => (bool)Get(Project(window), "Magnetic"), Wait, "마그네틱 켬");
                ui.Run(() =>
                {
                    Key(window, System.Windows.Input.Key.Home);
                    Key(window, System.Windows.Input.Key.V, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => Clips(window).Count == 2, Wait, "붙여 넣기");
                ui.Run(() =>
                {
                    Assert.Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:03:00" + videoSuffix, Clips(window));
                    // 0 앞 15프레임을 잘라 0~2초 15프레임, 원본 시작은 15프레임 뒤
                    Assert.Contains("오디오 1 · testsrc.mp4 · 00:00:00:00–00:00:02:15" + soundSuffix, Clips(window));
                    Assert.Equal(15L, (long)Get(SoundClip(window), "SourceIn"));
                    Ui.Snap(window, plugin.ShotFolder, "sound-split-paste-before-zero");
                });
                _out.WriteLine("붙여 넣기 전후 어긋남: 영상" + videoSuffix + " / 사운드" + soundSuffix);
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>
        /// 음량·페이드 넣어 저장(#31, Windows 실측): 음량을 바꾼 클립의 [사운드를 파일로 저장…]은 넣을지 묻는다.
        /// [넣어 저장] → WAV(평균 음량이 원본보다 약 6dB 낮음), [원본 소리 그대로] → 원본 코덱(AAC) 그대로.
        /// </summary>
        [EditorFact]
        public async Task SaveClipSound_WithVolume_AsksAndBothChoicesWork()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                await AddToTimeline(ui, window, "testsrc.mp4");
                ui.Run(() => SelectClip(window, n => n.StartsWith("동영상 1 · testsrc.mp4", StringComparison.Ordinal)));
                await ui.WaitFor(() => Ui.ById<TabControl>(window, "PropertyTabs"), Wait, "속성 패널");
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").Items.OfType<TabItem>().First(t => (string)t.Header == "소리").IsSelected = true);
                var volume = await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropVolume") is TextBox shown && shown.IsVisible ? shown : null, Wait, "음량 칸");
                ui.Run(() =>
                {
                    Ui.Focus(volume);
                    volume.Text = "50";
                    Ui.Press(volume, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(VideoClip(window), "Volume") - 0.5) < 1e-9, Wait, "음량 50%");

                async Task<string> SaveWith(int button, string extension)
                {
                    var path = Path.Combine(plugin.Root, "volume-" + button + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + extension);
                    ui.Post(() => InvokeMenu(ClipMenu(window, VideoClip(window)), "사운드를 파일로 저장"));
                    var ask = await ui.WaitFor(() => Ui.Windows().FirstOrDefault(w => w.IsVisible && w.Title == "사운드를 파일로 저장"), Wait, "넣어 저장할지 묻는 창");
                    ui.Run(() =>
                    {
                        Assert.Contains("음량·페이드를 넣어 저장할까요?", string.Join(" ", Ui.Texts(ask)));
                        Assert.Contains("넣어 저장", Ui.Texts(Ui.ById<Button>(ask, "AskButton0")));
                        Assert.Contains("원본 소리 그대로", Ui.Texts(Ui.ById<Button>(ask, "AskButton1")));
                        if (button == 0)
                            Ui.Snap(ask, plugin.ShotFolder, "sound-save-ask-volume");
                        Ui.Click(Ui.ById<Button>(ask, "AskButton" + button));
                    });
                    await NativeDialogs.SaveAs(ui, path, Wait);
                    await ui.WaitUntil(() => Ui.ById<ListBox>(window, "MediaList").Items.Cast<FrameworkElement>().Any(i => System.Windows.Automation.AutomationProperties.GetName(i) == Path.GetFileName(path)), Wait, "저장한 사운드가 미디어에");
                    return path;
                }

                var shaped = await SaveWith(0, ".wav");
                var original = await SaveWith(1, ".m4a");
                Assert.Equal("pcm_s16le", RunTool("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", shaped).Trim());
                Assert.Equal("aac", RunTool("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", original).Trim());
                var difference = MeanVolume(shaped) - MeanVolume(original);
                _out.WriteLine("평균 음량 차이(넣어 저장 − 원본): " + difference.ToString("0.00") + "dB, 길이 " + Duration(shaped) + "초");
                Assert.InRange(difference, -7.5, -4.5);
                Assert.InRange(Duration(shaped), 2.9, 3.1);
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        /// <summary>[사운드를 파일로 저장…]: 미디어 패널은 원본 코덱 그대로(AAC → .m4a), 타임라인 클립은 그 구간을 속도대로(2배속 → WAV 1.5초). 저장한 파일은 미디어 목록에.</summary>
        [EditorFact]
        public async Task SaveSoundToFile_KeepsCodec_ClipRangeWithSpeed_AddsToMedia()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                await Import(ui, window, Path.Combine(MediaFolder, "testsrc.mp4"));
                var m4a = Path.Combine(plugin.Root, "sound-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".m4a");
                ui.Post(() =>
                {
                    var list = Ui.ById<ListBox>(window, "MediaList");
                    list.SelectedItem = MediaItem(window, "testsrc.mp4");
                    Ui.ContextMenu(list, "사운드를 파일로 저장");
                });
                await NativeDialogs.SaveAs(ui, m4a, Wait);
                await ui.WaitUntil(() => MediaItem(window, Path.GetFileName(m4a)) != null, Wait, "저장한 사운드가 미디어 목록에");
                Assert.StartsWith("사운드를 저장하고 미디어에 더했습니다", ui.Run(() => Status(window)));
                Assert.Equal("aac", RunTool("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", m4a).Trim());
                Assert.Equal("", RunTool("ffprobe", "-v", "error", "-select_streams", "v", "-show_entries", "stream=codec_name", "-of", "csv=p=0", m4a).Trim());

                // 타임라인 클립을 2배속으로 → 그 구간만 WAV(원본 3초 → 1.5초)
                await AddToTimeline(ui, window, "testsrc.mp4");
                ui.Run(() => SelectClip(window, n => n.StartsWith("동영상 1 · testsrc.mp4", StringComparison.Ordinal)));
                await ui.WaitFor(() => Ui.ById<TabControl>(window, "PropertyTabs"), Wait, "속성 패널");
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").Items.OfType<TabItem>().First(t => (string)t.Header == "속도").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<Button>(window, "PropSpeedPreset20") is Button shown && shown.IsVisible ? shown : null, Wait, "속도 단추");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "PropSpeedPreset20")));
                await ui.WaitUntil(() => Clips(window).Contains("동영상 1 · testsrc.mp4 · 00:00:00:00–00:00:01:15"), Wait, "2배속");
                var wav = Path.Combine(plugin.Root, "clip-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                ui.Post(() => InvokeMenu(ClipMenu(window, VideoClip(window)), "사운드를 파일로 저장"));
                await NativeDialogs.SaveAs(ui, wav, Wait);
                await ui.WaitUntil(() => MediaItem(window, Path.GetFileName(wav)) != null, Wait, "클립 사운드가 미디어 목록에");
                var seconds = Duration(wav);
                _out.WriteLine("클립 사운드 " + seconds + "초");
                Assert.InRange(seconds, 1.4, 1.6);
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }
    }
}
