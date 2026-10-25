using System;
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
    /// 인물 마스크를 처음 만들 때 뜨는 창: 인물 분리 모델(MODNet)의 크기·라이선스·출처를 보이고 [내려받기]를 눌러야 받는다
    /// (Hugging Face의 리비전을 고정한 주소, 크기·SHA-256 확인). 받는 동안 [취소]하면 받던 파일을 지운다.
    /// 모든 처리는 이 PC에서 — 동영상·사진은 인터넷으로 보내지 않는다.
    /// </summary>
    internal sealed class PersonMaskSetupDialog : Window
    {
        public const string Privacy = "모든 처리는 이 PC에서 합니다 — 동영상·사진을 인터넷으로 보내지 않습니다. 인터넷은 모델 파일을 처음 한 번 받을 때만 씁니다.";

        private readonly EditorServices _services;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button _download;
        private readonly Button _close;
        private CancellationTokenSource _cancel;

        private PersonMaskSetupDialog(EditorServices services)
        {
            _services = services;
            var model = PersonMaskText.Model;
            Title = "인물 분리 모델 내려받기";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "인물 분리 모델이 필요합니다", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "인물 마스크(사람만 따낸 흑백 마스크 — \"사람에게만\" 효과가 씀)를 만들려면 인물 분리 모델을 처음 한 번 내려받아야 합니다. "
                       + "받은 뒤에는 인터넷 없이 만듭니다."
            });

            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            details.Text = "모델: " + model.Name + " — 사진 인물 매팅(머리카락 가장자리까지 부드럽게 따냄)\n"
                           + "크기: 약 " + Math.Round(model.Size / 1048576.0) + "MB(" + model.Size.ToString("N0") + "바이트) · 받은 뒤 SHA-256을 확인합니다\n"
                           + "라이선스: " + model.License + "\n"
                           + "출처: " + model.Source + "\n"
                           + "저작자 표시: " + model.Attribution;
            AutomationProperties.SetAutomationId(details, "PersonMaskModelDetails");
            panel.Children.Add(details);

            var notice = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0) };
            notice.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            notice.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            notice.Child = new TextBlock { Text = Privacy, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(notice);
            var folder = UiKit.Label("설치 위치: " + services.PersonMasks.ModelFolder, true);
            folder.Margin = new Thickness(0, 8, 0, 0);
            folder.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(folder);

            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "PersonMaskSetupStatus");
            panel.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _download = UiKit.TextButton("내려받기", "PersonMaskDownloadButton", () => _ = Download());
            _download.IsDefault = true;
            _close = UiKit.TextButton("취소", "PersonMaskSetupCancelButton", CloseOrCancel);
            _close.IsCancel = true;
            buttons.Children.Add(_download);
            buttons.Children.Add(_close);
            foreach (FrameworkElement button in buttons.Children)
                button.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(buttons);
            Content = panel;
            Closing += (s, e) => _cancel?.Cancel();
        }

        /// <summary>받은 모델 경로([내려받기]가 끝났을 때만).</summary>
        public string ModelPath { get; private set; }

        /// <summary>모델이 있으면 그 경로, 없으면 창을 띄워 받는다. 받지 않았으면(취소·실패) null.</summary>
        public static string Ensure(EditorServices services, DependencyObject owner)
        {
            var path = services.PersonMasks.ModelPath();
            if (path != null)
                return path;
            var dialog = new PersonMaskSetupDialog(services);
            var window = UiKit.OwnerOf(owner);
            if (window != null && window.IsLoaded)
                dialog.Owner = window;
            return dialog.ShowDialog() == true ? dialog.ModelPath : null;
        }

        private async Task Download()
        {
            _cancel = new CancellationTokenSource();
            var cancellation = _cancel.Token;
            _download.IsEnabled = false;
            ((TextBlock)_close.Content).Text = "취소";
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = true;
            _status.Text = "준비하는 중…";
            try
            {
                ModelPath = await _services.PersonMasks.DownloadModel(new Progress<FfmpegDownloader.Status>(Show), cancellation);
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "취소했습니다.";
            }
            catch (Exception ex)
            {
                _status.Text = "내려받지 못했습니다: " + ex.Message;
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                if (IsLoaded && DialogResult != true)
                {
                    _download.IsEnabled = true;
                    _progress.Visibility = Visibility.Collapsed;
                    ((TextBlock)_close.Content).Text = "닫기";
                }
            }
        }

        private void Show(FfmpegDownloader.Status status)
        {
            if (_cancel == null)
                return;
            if (status.Total > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = Math.Min(100, 100.0 * status.Done / status.Total);
                _status.Text = status.Stage + " " + (status.Done / 1048576.0).ToString("0.0") + " / " + (status.Total / 1048576.0).ToString("0.0") + "MB";
            }
            else
            {
                _progress.IsIndeterminate = true;
                _status.Text = status.Stage + "…";
            }
        }

        private void CloseOrCancel()
        {
            if (_cancel != null)
                _cancel.Cancel();
            else
                DialogResult = false;
        }
    }
}
