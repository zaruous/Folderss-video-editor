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
    /// 이 PC에서 인식한다(소리를 인터넷으로 보내지 않음). 대본을 주면 글은 대본에서, 시각은 소리에서 가져온다.
    /// 끝나면 결과를 <see cref="Result"/>로 돌려주고 닫힌다.
    /// </summary>
    internal sealed class SpeechToTextDialog : Window
    {
        /// <summary>whisper와 인식 설정(모델·말소리 구간 감지·말·낱말)으로 인식해 타임라인 시각의 큐를 돌려준다.</summary>
        public delegate Task<SpeechResult> Recognizer(WhisperTool whisper, SpeechOptions options, IProgress<FfmpegDownloader.Status> progress, CancellationToken cancellation);

        public const string ModelKey = "speechModel";
        public const string LanguageKey = "speechLanguage";
        public const string PromptKey = "speechPrompt";
        public const string HighlightKey = "speechHighlight";

        private readonly EditorServices _services;
        private readonly Recognizer _recognize;
        private readonly ComboBox _language = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBox _prompt = new TextBox { Margin = new Thickness(0, 4, 0, 0), MaxLength = SpeechText.MaxPromptLength };
        private readonly TextBox _script = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0), MaxLength = SubtitleFile.MaxBytes / 2
        };
        private Button _scriptFile;
        private readonly CheckBox _highlight = new CheckBox { Margin = new Thickness(0, 12, 0, 0) };
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

            var promptLabel = UiKit.Label("자주 나오는 낱말(선택) — 이름·용어를 쉼표로, 예: 맹자, 반구저기");
            promptLabel.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(promptLabel);
            AutomationProperties.SetAutomationId(_prompt, "SpeechPrompt");
            AutomationProperties.SetName(_prompt, "자주 나오는 낱말");
            _prompt.Text = services.Setting(PromptKey) ?? "";
            panel.Children.Add(_prompt);

            // 대본(선택): 글은 대본에서, 시각은 소리에서 — 대본의 장면 설명·해시태그처럼 말하지 않은 글은 저절로 빠진다
            var scriptRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            _scriptFile = UiKit.TextButton("대본 파일…", "SpeechScriptFileButton", PickScript);
            DockPanel.SetDock(_scriptFile, Dock.Right);
            scriptRow.Children.Add(_scriptFile);
            var scriptLabel = UiKit.Label("대본(선택) — 글은 대본에서, 시각은 소리에서. 붙여 넣거나 파일(.txt·.md)을 고르세요.");
            scriptLabel.TextWrapping = TextWrapping.Wrap;
            scriptLabel.VerticalAlignment = VerticalAlignment.Center;
            scriptRow.Children.Add(scriptLabel);
            panel.Children.Add(scriptRow);
            AutomationProperties.SetAutomationId(_script, "SpeechScript");
            AutomationProperties.SetName(_script, "대본");
            panel.Children.Add(_script);

            _highlight.Content = new TextBlock { Text = "말하는 단어를 노란색으로 강조(쇼츠 자막) — 자막 트랙 오른쪽 메뉴에서 나중에 켜고 끌 수도 있습니다", TextWrapping = TextWrapping.Wrap };
            _highlight.IsChecked = services.Setting(HighlightKey) == "true";
            AutomationProperties.SetAutomationId(_highlight, "SpeechHighlight");
            panel.Children.Add(_highlight);

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
                Text = "소리는 이 PC에서만 인식합니다(인터넷으로 보내지 않음). 처음 한 번 whisper.cpp(약 9MB, GitHub ggml-org/whisper.cpp)와 고른 모델, "
                       + "말소리 구간 감지 모델(0.9MB, Hugging Face)을 내려받아 SHA-256을 확인한 뒤 플러그인 데이터 폴더에 둡니다. "
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

        /// <summary>인식한 큐(타임라인 시각)와 대본 맞춤 결과. [자막 만들기]가 끝났을 때만.</summary>
        public SpeechResult Result { get; private set; }

        public SpeechModel Model { get; private set; }

        /// <summary>새 자막 트랙에서 말하는 단어를 강조할지.</summary>
        public bool HighlightWords { get; private set; }

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

        /// <summary>[대본 파일…]: 글 파일(1MB까지, UTF-8·UTF-16·CP949)을 읽어 대본 칸에.</summary>
        private void PickScript()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "대본 고르기", Filter = "대본(글 파일)|*.txt;*.md;*.markdown|모든 파일|*.*" };
            var folder = _services.Setting(EditorServices.LastFolderKey);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                dialog.InitialDirectory = folder;
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                var info = new System.IO.FileInfo(dialog.FileName);
                if (info.Length > 1024 * 1024)
                {
                    _status.Text = "대본 파일이 너무 큽니다(1MB까지).";
                    return;
                }
                _script.Text = SubtitleFile.Decode(System.IO.File.ReadAllBytes(dialog.FileName));
                var count = SpeechText.ScriptSentences(_script.Text).Count;
                _status.Text = "대본: " + info.Name + " — 문장 " + count + "개(말하지 않은 글은 맞춤에서 빠집니다).";
            }
            catch (Exception ex)
            {
                _status.Text = "대본을 읽지 못했습니다: " + ex.Message;
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
            var prompt = SpeechText.CleanPrompt(_prompt.Text);
            _services.SaveSetting(ModelKey, model.Id);
            _services.SaveSetting(LanguageKey, language);
            _services.SaveSetting(PromptKey, prompt);
            HighlightWords = _highlight.IsChecked == true;
            _services.SaveSetting(HighlightKey, HighlightWords ? "true" : null);
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
                var vad = await SpeechRecognizer.DownloadModel(_services.WhisperFolder, SpeechText.VadModel, progress, cancellation);
                var modelPath = await SpeechRecognizer.DownloadModel(_services.WhisperFolder, model, progress, cancellation);
                ShowModels();
                watch.Restart();
                var options = new SpeechOptions { Model = modelPath, Vad = vad, Language = language, Prompt = prompt, Script = _script.Text };
                Result = await _recognize(whisper, options, progress, cancellation);
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
            _prompt.IsEnabled = !busy;
            _script.IsEnabled = !busy;
            _highlight.IsEnabled = !busy;
            _scriptFile.IsEnabled = !busy;
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
