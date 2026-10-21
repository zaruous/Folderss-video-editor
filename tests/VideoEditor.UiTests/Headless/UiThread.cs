using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoEditor.UiTests.Headless
{
    /// <summary>
    /// 시험 프로세스의 WPF UI 스레드 하나(STA + Application). Folderss처럼 Application 리소스에 테마 색 사전과 컨트롤 스타일을 넣고,
    /// 숨은 데스크톱에서도 그려지게 소프트웨어 렌더링으로 둔다. 시험 코드는 다른 스레드에서 <see cref="Run{T}"/>로 UI를 만지고
    /// <see cref="WaitUntil"/>으로 기다린다(UI 스레드를 막지 않는다).
    /// </summary>
    internal sealed class UiThread
    {
        private static readonly Lazy<UiThread> Shared = new Lazy<UiThread>(() => new UiThread("Black"), LazyThreadSafetyMode.ExecutionAndPublication);
        private readonly List<Exception> _errors = new List<Exception>();

        private UiThread(string theme)
        {
            var ready = new ManualResetEventSlim();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    NativeThreadId = GetCurrentThreadId();
                    RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    // Folderss App.xaml과 같은 순서: 색 사전 → 컨트롤 스타일
                    app.Resources.MergedDictionaries.Add(LoadTheme(theme));
                    app.Resources.MergedDictionaries.Add(LoadTheme("Controls"));
                    // Folderss는 플러그인의 UI 예외를 잡아 알리고 계속 실행한다. 시험은 모아 두었다가 TakeErrors로 비어 있는지 본다
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        lock (_errors)
                            _errors.Add(e.Exception);
                        e.Handled = true;
                    };
                    Dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                ready.Set();
                if (failure == null)
                    Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "headless-ui";
            thread.Start();
            ready.Wait();
            if (failure != null)
                throw new InvalidOperationException("헤드리스 UI 스레드를 시작하지 못했습니다.", failure);
        }

        public static UiThread Instance { get { return Shared.Value; } }

        public Dispatcher Dispatcher { get; private set; }

        /// <summary>UI 스레드의 Win32 스레드 id(파일 대화상자처럼 이 스레드가 만든 Win32 창을 찾을 때).</summary>
        public uint NativeThreadId { get; private set; }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>UI 스레드에서 잡힌 처리되지 않은 예외(시험이 끝에 비어 있는지 본다).</summary>
        public List<Exception> TakeErrors()
        {
            lock (_errors)
            {
                var copy = new List<Exception>(_errors);
                _errors.Clear();
                return copy;
            }
        }

        public T Run<T>(Func<T> work)
        {
            return Dispatcher.Invoke(work);
        }

        public void Run(Action work)
        {
            Dispatcher.Invoke(work);
        }

        /// <summary>
        /// 기다리지 않고 UI 스레드에 맡긴다 — 모달 창(ShowDialog·파일 대화상자)을 여는 버튼은 이걸로 누르고, 뜬 창은 <see cref="WaitFor{T}"/>로 찾는다
        /// (<see cref="Run(Action)"/>으로 누르면 모달이 닫힐 때까지 시험 스레드가 묶인다).
        /// </summary>
        public void Post(Action work)
        {
            Dispatcher.BeginInvoke(work);
        }

        /// <summary>조건이 참이 될 때까지(UI 스레드에서 평가) 기다린다. 넘으면 무엇을 기다렸는지와 함께 TimeoutException.</summary>
        public async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (Run(condition))
                    return;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(what + " — " + timeout.TotalSeconds + "초 안에 되지 않았습니다.");
                await Task.Delay(100);
            }
        }

        /// <summary>값이 null이 아니게 될 때까지 기다려 돌려준다.</summary>
        public async Task<T> WaitFor<T>(Func<T> find, TimeSpan timeout, string what) where T : class
        {
            T found = null;
            await WaitUntil(() => (found = find()) != null, timeout, what);
            return found;
        }

        /// <summary>Folderss 테마 이름(Themes 폴더의 색 사전). 처음은 Black.</summary>
        public static readonly string[] Themes = { "Black", "Light", "Catppuccin", "Dracula", "GitHub", "Nord", "Solarized" };

        /// <summary>
        /// 테마 색 사전을 바꾼다 — Folderss ThemeManager.ApplyTheme처럼 맨 앞 사전(색)을 갈아 끼운다. DynamicResource로 묶인 색은 바로 따라온다.
        /// UI 스레드에서 부른다. 다른 시험이 같은 UI 스레드를 쓰므로 바꾼 시험은 끝에 "Black"으로 되돌린다.
        /// </summary>
        public static void ApplyTheme(string name)
        {
            Application.Current.Resources.MergedDictionaries[0] = LoadTheme(name);
        }

        /// <summary>Themes 폴더의 사전(Black·Light·… 또는 Controls). csproj가 "Themes.&lt;이름&gt;.xaml"로 넣는다.</summary>
        private static ResourceDictionary LoadTheme(string name)
        {
            var resource = "Themes." + name + ".xaml";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                    throw new InvalidOperationException("테마 리소스가 없습니다: " + resource);
                return (ResourceDictionary)XamlReader.Load(stream);
            }
        }
    }
}
