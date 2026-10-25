using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    /// [음성 인식으로 자막 만들기] 창: 말(언어)과 모델을 고르고 [자막 만들기]. 처음이면 whisper.cpp와 모델을 내려받고(SHA-256 확인),
    /// 이 PC에서 인식한다(소리를 인터넷으로 보내지 않음). 끝나면 인식한 큐를 <see cref="Cues"/>로 돌려주고 닫힌다.
    /// </summary>
    internal sealed class SpeechToTextDialog : Window
    {
        /// <summary>whisper·모델 경로·언어로 인식해 타임라인 시각의 큐를 돌려준다.</summary>
        public delegate Task<List<SubtitleCue>> Recognizer(WhisperTool whisper, string modelPath, string language, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation);

        public const string ModelKey = "speechModel";
        public const string LanguageKey = "speechLanguage";

        private readonly EditorServices _services;
        private readonly Recognizer _recognize;
        private readonly ComboBox _language = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        private readonly List<RadioButton> _models = new List<RadioButton>();
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button _run;
        private readonly Button _close;
        private CancellationTokenSource _cancel;

        public SpeechToTextDialog(EditorServices services, string source, bool mixed, Recognizer recognize)
        {
            _services = services;
            _recognize = recognize;
            Title = "음성 인식으로 자막 만들기";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "음성 인식으로 자막 만들기", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            var sourceText = new TextBlock { Text = "인식할 소리: " + source, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(sourceText, "SpeechSource");
            panel.Children.Add(sourceText);
            if (mixed)
            {
                var hint = UiKit.Label("배경 음악이 섞이면 정확도가 떨어집니다. 타임라인에서 내레이션 클립을 고르고 열면 그 클립만 인식합니다.", true);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(hint);
            }

            var languageLabel = UiKit.Label("말");
            languageLabel.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(languageLabel);
            AutomationProperties.SetAutomationId(_language, "SpeechLanguage");
            AutomationProperties.SetName(_language, "인식할 말");
            foreach (var (code, name) in SpeechText.Languages)
                _language.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            var savedLanguage = SpeechText.Language(services.Setting(LanguageKey));
            _language.SelectedItem = _language.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == savedLanguage);
            panel.Children.Add(_language);

            var modelLabel = UiKit.Label("모델");
            modelLabel.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(modelLabel);
            var savedModel = SpeechText.Model(services.Setting(ModelKey));
            foreach (var model in SpeechText.Models)
            {
                var radio = new RadioButton { Tag = model, Margin = new Thickness(0, 4, 0, 0), IsChecked = model == savedModel, GroupName = "SpeechModel" };
                AutomationProperties.SetAutomationId(radio, "SpeechModel_" + model.Id);
                _models.Add(radio);
                panel.Children.Add(radio);
            }
            ShowModels();

            var notice = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0) };
            notice.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            notice.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            notice.Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "소리는 이 PC에서만 인식합니다(인터넷으로 보내지 않음). 처음 한 번 whisper.cpp(약 9MB, GitHub ggml-org/whisper.cpp)와 고른 모델"
                       + "(Hugging Face ggerganov/whisper.cpp)을 내려받아 SHA-256을 확인한 뒤 플러그인 데이터 폴더에 둡니다. "
                       + "whisper.cpp를 실행하려면 Microsoft Visual C++ 재배포 패키지(x64)가 있어야 합니다.\n"
                       + "인식한 자막은 새 자막 트랙에 들어가고, 틀린 글은 큐를 골라 속성에서 고칩니다."
            };
            panel.Children.Add(notice);
            var folder = UiKit.Label("설치 위치: " + services.WhisperFolder, true);
            folder.Margin = new Thickness(0, 8, 0, 0);
            folder.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(folder);

            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "SpeechStatus");
            panel.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _run = UiKit.TextButton("자막 만들기", "SpeechRunButton", () => _ = Start());
            _run.IsDefault = true;
            _close = UiKit.TextButton("닫기", "SpeechCloseButton", CloseOrCancel);
            _close.IsCancel = true;
            buttons.Children.Add(_run);
            buttons.Children.Add(_close);
            foreach (FrameworkElement button in buttons.Children)
                button.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(buttons);
            Content = panel;
            Closing += (s, e) => _cancel?.Cancel();
        }

        /// <summary>인식한 큐(타임라인 시각). [자막 만들기]가 끝났을 때만.</summary>
        public List<SubtitleCue> Cues { get; private set; }

        public SpeechModel Model { get; private set; }

        public TimeSpan Elapsed { get; private set; }

        private SpeechModel Chosen()
        {
            return (SpeechModel)_models.First(r => r.IsChecked == true).Tag;
        }

        /// <summary>모델마다 이름·설명과 받아 두었는지.</summary>
        private void ShowModels()
        {
            foreach (var radio in _models)
            {
                var model = (SpeechModel)radio.Tag;
                var have = WhisperTool.HasModel(_services.WhisperFolder, model);
                radio.Content = new TextBlock { Text = model.Label + " — " + model.Note + (have ? " · 받아 둠" : ""), TextWrapping = TextWrapping.Wrap };
            }
        }

        private void CloseOrCancel()
        {
            if (_cancel != null)
                _cancel.Cancel();
            else
                Close();
        }

        private async Task Start()
        {
            var model = Chosen();
            var language = (string)((ComboBoxItem)_language.SelectedItem).Tag;
            _services.SaveSetting(ModelKey, model.Id);
            _services.SaveSetting(LanguageKey, language);
            _cancel = new CancellationTokenSource();
            var cancellation = _cancel.Token;
            SetBusy(true);
            var watch = Stopwatch.StartNew();
            var progress = new Progress<FfmpegDownloader.Status>(Show);
            try
            {
                var whisper = _services.Whisper;
                if (whisper == null)
                {
                    whisper = await SpeechRecognizer.DownloadTool(_services.WhisperFolder, progress, cancellation);
                    _services.Whisper = whisper;
                }
                var modelPath = await SpeechRecognizer.DownloadModel(_services.WhisperFolder, model, progress, cancellation);
                ShowModels();
                watch.Restart();
                var cues = await _recognize(whisper, modelPath, language, progress, cancellation);
                Cues = cues;
                Model = model;
                Elapsed = watch.Elapsed;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "취소했습니다.";
            }
            catch (Exception ex)
            {
                _status.Text = "자막을 만들지 못했습니다: " + ex.Message;
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                if (IsLoaded && DialogResult != true)
                    SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _run.IsEnabled = !busy;
            _language.IsEnabled = !busy;
            foreach (var radio in _models)
                radio.IsEnabled = !busy;
            ((TextBlock)_close.Content).Text = busy ? "취소" : "닫기";
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _progress.IsIndeterminate = busy;
            if (busy)
                _status.Text = "준비하는 중…";
        }

        private void Show(FfmpegDownloader.Status status)
        {
            if (_cancel == null)
                return;
            if (status.Total > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = Math.Min(100, 100.0 * status.Done / status.Total);
                // 내려받기는 바이트, 인식은 백분율
                _status.Text = status.Total == 100 && status.Done <= 100
                    ? status.Stage + " " + status.Done + "%"
                    : status.Stage + " " + (status.Done / 1048576.0).ToString("0.0") + " / " + (status.Total / 1048576.0).ToString("0.0") + "MB";
            }
            else
            {
                _progress.IsIndeterminate = true;
                _status.Text = status.Stage + "…";
            }
        }
    }
}
