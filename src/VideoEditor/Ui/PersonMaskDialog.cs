using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Core;
using VideoEditor.Media;

namespace VideoEditor.Ui
{
    /// <summary>
    /// [인물 마스크 만들기] 진행 창: 뜨자마자 만들기 시작해 프레임 진행률·남은 시간을 보이고, 다 되면 <see cref="Result"/>(마스크 경로)를 남기고 닫힌다.
    /// [취소]하면 FFmpeg를 끝내고 만들던 파일을 지운 뒤 닫힌다. 실패하면 까닭을 보이고 [닫기]를 기다린다.
    /// </summary>
    internal sealed class PersonMaskDialog : Window
    {
        /// <summary>진행률을 알리며 마스크를 만들어 경로를 돌려준다.</summary>
        public delegate Task<string> Builder(IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation);

        private readonly Builder _build;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 12, 0, 0), IsIndeterminate = true };
        private readonly Button _close;
        private readonly Stopwatch _watch = new Stopwatch();
        private readonly Stopwatch _frames = new Stopwatch();
        private CancellationTokenSource _cancel;

        public PersonMaskDialog(MediaInfo media, Builder build)
        {
            _build = build;
            Title = "인물 마스크 만들기";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "인물 마스크 만들기", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            var source = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "원본: " + media.Name + " — " + Describe(media) };
            AutomationProperties.SetAutomationId(source, "PersonMaskSource");
            panel.Children.Add(source);
            var hint = UiKit.Label("사람만 따낸 흑백 마스크를 이 PC에서 만듭니다(인터넷으로 보내지 않음). 한 번 만들면 원본 파일이 바뀌기 전까지 다시 만들지 않습니다.", true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(hint);
            AutomationProperties.SetAutomationId(_progress, "PersonMaskProgress");
            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "PersonMaskStatus");
            _status.Text = "준비하는 중…";
            panel.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _close = UiKit.TextButton("취소", "PersonMaskCancelButton", CloseOrCancel);
            _close.IsCancel = true;
            buttons.Children.Add(_close);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (s, e) => _ = Start();
            Closing += (s, e) => _cancel?.Cancel();
        }

        /// <summary>만든 마스크 경로(다 만들었을 때만).</summary>
        public string Result { get; private set; }

        /// <summary>실패했으면 까닭(취소는 null).</summary>
        public string Error { get; private set; }

        public TimeSpan Elapsed { get; private set; }

        private static string Describe(MediaInfo media)
        {
            var size = media.Width + "×" + media.Height;
            return media.Kind == MediaKind.Image ? "사진 " + size : "동영상 " + size + ", " + Seconds((int)Math.Round(media.DurationSeconds));
        }

        private static string Seconds(int whole)
        {
            return whole >= 60 ? whole / 60 + "분 " + whole % 60 + "초" : whole + "초";
        }

        private async Task Start()
        {
            // Loaded 처리 밖에서 시작한다 — 이미 만들어 둔 마스크라 바로 끝나도 창이 다 뜬 뒤에 닫히게
            await Task.Yield();
            if (!IsVisible)
                return;
            _cancel = new CancellationTokenSource();
            var cancellation = _cancel.Token;
            _watch.Start();
            try
            {
                Result = await _build(new Progress<FfmpegDownloader.Status>(Show), cancellation);
                Elapsed = _watch.Elapsed;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                // [취소]·창 닫기 — 만들던 파일은 지웠다
                if (IsVisible)
                    Close();
            }
            catch (Exception ex)
            {
                Error = "인물 마스크를 만들지 못했습니다: " + ex.Message;
                _status.Text = Error;
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                if (IsVisible && DialogResult != true)
                {
                    _progress.Visibility = Visibility.Collapsed;
                    ((TextBlock)_close.Content).Text = "닫기";
                }
            }
        }

        private void Show(FfmpegDownloader.Status status)
        {
            if (_cancel == null)
                return;
            if (status.Total <= 0)
            {
                _progress.IsIndeterminate = true;
                _status.Text = status.Stage + "…";
                return;
            }
            // 남은 시간은 프레임을 흘리기 시작한 때부터 잰다(모델을 여는 시간은 빼고)
            if (status.Done == 0)
                _frames.Restart();
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Min(100, 100.0 * status.Done / status.Total);
            var text = status.Stage + " — 프레임 " + status.Done + " / " + status.Total + " (" + (int)(100 * status.Done / status.Total) + "%)";
            // 지금까지 한 프레임당 걸린 시간으로(처음 몇 프레임은 들쭉날쭉해 건너뜀)
            if (status.Done >= 5 && status.Done < status.Total && _frames.IsRunning)
                text += " · 남은 시간 약 " + Seconds((int)Math.Ceiling(_frames.Elapsed.TotalSeconds / status.Done * (status.Total - status.Done)));
            _status.Text = text;
        }

        private void CloseOrCancel()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                ((TextBlock)_close.Content).Text = "취소하는 중…";
                _close.IsEnabled = false;
            }
            else
            {
                Close();
            }
        }
    }
}
