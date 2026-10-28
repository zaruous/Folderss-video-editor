using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace VideoEditor.Ui
{
    /// <summary>
    /// [사운드를 파일로 저장…] 진행 창(#34): 뜨자마자 저장을 하나씩 시작해 진행률(파일이 여럿이면 전체와 지금 파일)을 보이고, 다 되면 닫힌다(<see cref="Saved"/>).
    /// [취소]·창 닫기는 지금 FFmpeg를 끝내고 쓰던 임시 파일을 지운 뒤 닫는다 — 그 전에 다 저장한 파일은 남는다(<see cref="Cancelled"/>).
    /// 실패한 파일은 건너뛰고 다음 파일을 저장하며, 실패가 있으면 다 끝난 뒤 까닭을 보이고 [닫기]를 기다린다(<see cref="Errors"/>).
    /// </summary>
    internal sealed class SoundSaveDialog : Window
    {
        /// <summary>저장 하나: 보일 이름과, 진행률(0~1)을 알리며 저장해 실제로 쓴 경로를 돌려주는 일.</summary>
        public sealed class Job
        {
            public Job(string name, Func<IProgress<double>, CancellationToken, Task<string>> run)
            {
                Name = name;
                Run = run;
            }

            public string Name { get; }
            public Func<IProgress<double>, CancellationToken, Task<string>> Run { get; }
        }

        private readonly IReadOnlyList<Job> _jobs;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Margin = new Thickness(0, 12, 0, 0), Maximum = 100, IsIndeterminate = true };
        private readonly Button _close;
        private CancellationTokenSource _cancel;
        private int _index;
        private double _fraction = -1;

        public SoundSaveDialog(IReadOnlyList<Job> jobs, string folder)
        {
            _jobs = jobs;
            Title = "사운드 저장";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = jobs.Count == 1 ? "사운드를 파일로 저장하는 중" : "사운드 " + jobs.Count + "개를 파일로 저장하는 중",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            var where = UiKit.Label("저장할 곳: " + folder, true);
            where.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(where, "SoundSaveFolder");
            panel.Children.Add(where);
            AutomationProperties.SetAutomationId(_progress, "SoundSaveProgress");
            AutomationProperties.SetName(_progress, "저장 진행률");
            panel.Children.Add(_progress);
            AutomationProperties.SetAutomationId(_status, "SoundSaveStatus");
            _status.Text = "준비하는 중…";
            panel.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _close = UiKit.TextButton("취소", "SoundSaveCancelButton", CloseOrCancel);
            _close.IsCancel = true;
            buttons.Children.Add(_close);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (s, e) => _ = Start();
            // 저장하는 중에 창을 닫으면 [취소]와 같다 — 지금 FFmpeg가 멈추고 임시 파일을 지운 뒤에 닫힌다(부른 쪽이 그 전에 결과를 읽지 않게)
            Closing += (s, e) =>
            {
                if (_cancel == null)
                    return;
                e.Cancel = true;
                CloseOrCancel();
            };
        }

        /// <summary>다 저장한 파일(실제로 쓴 경로 — 그대로 담지 못해 WAV로 쓴 것도).</summary>
        public List<string> Saved { get; } = new List<string>();

        /// <summary>저장하지 못한 파일과 까닭("이름: 까닭").</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>[취소]하거나 창을 닫아 남은 저장을 그만뒀는지.</summary>
        public bool Cancelled { get; private set; }

        private async Task Start()
        {
            // Loaded 처리 밖에서 시작한다 — 바로 끝나도 창이 다 뜬 뒤에 닫히게
            await Task.Yield();
            if (!IsVisible)
                return;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            try
            {
                for (_index = 0; _index < _jobs.Count; _index++)
                {
                    var job = _jobs[_index];
                    _fraction = -1;
                    ShowProgress();
                    try
                    {
                        var index = _index;
                        Saved.Add(await job.Run(new Progress<double>(value =>
                        {
                            // 늦게 온 알림(끝났거나 다음 파일로 넘어간 뒤)은 버린다
                            if (_cancel != null && index == _index)
                            {
                                _fraction = value;
                                ShowProgress();
                            }
                        }), cancel.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Errors.Add(job.Name + ": " + ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // [취소]·창 닫기 — FFmpeg를 끝냈고 쓰던 임시 파일은 지웠다
                Cancelled = true;
            }
            finally
            {
                _cancel = null;
                cancel.Dispose();
            }
            if (Cancelled)
            {
                Close();
                return;
            }
            if (Errors.Count == 0)
            {
                DialogResult = true;
                return;
            }
            // 실패한 것이 있으면 까닭을 보이고 [닫기]를 기다린다
            _status.Text = (Saved.Count > 0 ? Saved.Count + "개를 저장했습니다. " : "") + "저장하지 못한 것:\n" + string.Join("\n", Errors);
            _progress.Visibility = Visibility.Collapsed;
            ((TextBlock)_close.Content).Text = "닫기";
            _close.IsEnabled = true;
        }

        /// <summary>진행률·글: 전체 = (끝낸 수 + 지금 파일의 진행률) / 수. 지금 파일의 길이를 모르면(진행률이 오지 않음) 막대가 흐른다.</summary>
        private void ShowProgress()
        {
            var job = _jobs[Math.Min(_index, _jobs.Count - 1)];
            var head = _jobs.Count > 1 ? (_index + 1) + " / " + _jobs.Count + " · " : "";
            if (_fraction < 0)
            {
                _progress.IsIndeterminate = _jobs.Count == 1;
                _progress.Value = 100.0 * _index / _jobs.Count;
                _status.Text = head + job.Name + "…";
                return;
            }
            _progress.IsIndeterminate = false;
            _progress.Value = 100.0 * (_index + _fraction) / _jobs.Count;
            _status.Text = head + job.Name + " — " + (int)(_fraction * 100) + "%";
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
