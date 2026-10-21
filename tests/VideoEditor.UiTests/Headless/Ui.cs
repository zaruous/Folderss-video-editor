using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoEditor.UiTests.Headless
{
    /// <summary>
    /// 화면 찾기·조작(UI 스레드에서만 부른다). 마우스를 쓰지 않고 키 입력도 보내지 않는다(SendInput·SendKeys 없음) — 자동화 피어(Invoke·Toggle·Selection·ExpandCollapse)와
    /// 속성, 요소에 직접 내는 키 이벤트로 조작하므로 숨은 데스크톱에서도, 창이 가려져도 같고 사용자 화면의 다른 창에는 닿지 않는다.
    /// 우클릭 메뉴는 ContextMenu.IsOpen으로 열어 항목을 Invoke한다.
    /// </summary>
    internal static class Ui
    {
        // ---------- 창 ----------

        public static List<Window> Windows()
        {
            return Application.Current.Windows.Cast<Window>().ToList();
        }

        /// <summary>아직 열려 있는 창만 닫는다(사람이 이미 닫은 창, null은 건너뜀). 시험 끝 finally에서 부른다.</summary>
        public static void Close(params System.Windows.Window[] windows)
        {
            var open = Windows();
            foreach (var window in windows)
                if (window != null && open.Contains(window))
                    window.Close();
        }

        /// <summary>제목이 이 글로 시작하는 보이는 창(없으면 null).</summary>
        public static Window Window(string titleStartsWith)
        {
            return Windows().FirstOrDefault(w => w.IsVisible && (w.Title ?? "").StartsWith(titleStartsWith, StringComparison.Ordinal));
        }

        // ---------- 트리 ----------

        /// <summary>화면 트리(보이는 것) + 논리 트리(접힌 메뉴 항목 등)를 함께 훑는다. 같은 요소는 한 번만.</summary>
        public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var seen = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node == null || !seen.Add(node))
                    continue;
                yield return node;
                // 스택이라 나중에 넣은 것이 먼저 나온다: 오른쪽 메뉴·논리 자식(접힌 메뉴 항목 등)을 먼저 넣고, 화면 자식을 거꾸로 넣어
                // 화면 순서(위 → 아래)대로 나오게 한다. 같은 요소는 seen으로 한 번만
                var element = node as FrameworkElement;
                if (element != null && element.ContextMenu != null)
                    stack.Push(element.ContextMenu);
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().Reverse())
                    stack.Push(child);
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                {
                    var count = VisualTreeHelper.GetChildrenCount(node);
                    for (var i = count - 1; i >= 0; i--)
                        stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
        }

        public static T Find<T>(DependencyObject root, Func<T, bool> match = null) where T : DependencyObject
        {
            return Descendants(root).OfType<T>().FirstOrDefault(e => match == null || match(e));
        }

        public static List<T> FindAll<T>(DependencyObject root, Func<T, bool> match = null) where T : DependencyObject
        {
            return Descendants(root).OfType<T>().Where(e => match == null || match(e)).ToList();
        }

        public static T ById<T>(DependencyObject root, string automationId) where T : DependencyObject
        {
            return Find<T>(root, e => AutomationProperties.GetAutomationId(e) == automationId);
        }

        /// <summary>글(내용·AutomationProperties.Name)이 정확히 같은 버튼.</summary>
        public static ButtonBase Button(DependencyObject root, string text)
        {
            return Find<ButtonBase>(root, b => b.IsVisible && (ContentText(b.Content) == text || AutomationProperties.GetName(b) == text));
        }

        /// <summary>보이는 TextBlock 글(화면 순서).</summary>
        public static List<string> Texts(DependencyObject root)
        {
            return FindAll<TextBlock>(root, t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text).ToList();
        }

        public static string ContentText(object content)
        {
            if (content == null)
                return null;
            var text = content as string;
            if (text != null)
                return text;
            var block = content as TextBlock;
            if (block != null)
                return block.Text;
            var element = content as DependencyObject;
            return element == null ? content.ToString() : string.Concat(FindAll<TextBlock>(element).Select(t => t.Text));
        }

        /// <summary>메뉴 머리글(접근 키 '_' 뺌).</summary>
        public static string HeaderText(MenuItem item)
        {
            return (ContentText(item.Header) ?? "").Replace("_", "");
        }

        // ---------- 조작 ----------

        public static void Click(ButtonBase button)
        {
            if (button == null)
                throw new InvalidOperationException("누를 버튼이 없습니다.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        /// <summary>
        /// 체크 상자를 사람이 누른 것처럼(값이 바뀌고 Click 처리기가 돈다). 자동화 Toggle 패턴은 Click을 내지 않아 Click에 묶인 저장이 돌지 않는다.
        /// </summary>
        public static void ClickToggle(ToggleButton toggle)
        {
            if (toggle == null)
                throw new InvalidOperationException("누를 체크 상자가 없습니다.");
            typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(toggle, null);
        }

        public static void Toggle(ToggleButton toggle, bool on)
        {
            if (toggle.IsChecked != on)
                toggle.IsChecked = on;
        }

        public static void Invoke(MenuItem item)
        {
            if (item == null)
                throw new InvalidOperationException("누를 메뉴 항목이 없습니다.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(item);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        /// <summary>메뉴 막대의 메뉴 경로(머리글이 이 글로 시작)를 따라 마지막 항목을 누른다: "도구", "일일 점검".</summary>
        public static void Menu(DependencyObject root, params string[] path)
        {
            ItemsControl current = Find<System.Windows.Controls.Menu>(root);
            for (var i = 0; i < path.Length; i++)
            {
                var item = current.Items.OfType<MenuItem>().FirstOrDefault(m => HeaderText(m).StartsWith(path[i], StringComparison.Ordinal));
                if (item == null)
                    throw new InvalidOperationException("메뉴 '" + path[i] + "'가 없습니다. 있는 것: " + string.Join(" · ", current.Items.OfType<MenuItem>().Select(HeaderText)));
                if (i == path.Length - 1)
                {
                    Invoke(item);
                    return;
                }
                item.IsSubmenuOpen = true;
                current = item;
            }
        }

        /// <summary>요소의 오른쪽 메뉴를 열고(Opened 처리기가 돈다) 머리글이 이 글로 시작하는 항목을 누른다. 항목 목록을 돌려준다.</summary>
        public static List<string> ContextMenu(FrameworkElement element, string itemStartsWith)
        {
            var menu = element.ContextMenu;
            if (menu == null)
                throw new InvalidOperationException("오른쪽 메뉴가 없는 요소입니다.");
            menu.PlacementTarget = element;
            menu.IsOpen = true;
            var items = menu.Items.OfType<MenuItem>().Where(m => m.Visibility == Visibility.Visible).ToList();
            var names = items.Select(HeaderText).ToList();
            if (itemStartsWith != null)
            {
                var item = items.FirstOrDefault(m => HeaderText(m).StartsWith(itemStartsWith, StringComparison.Ordinal));
                if (item == null)
                {
                    menu.IsOpen = false;
                    throw new InvalidOperationException("메뉴 항목 '" + itemStartsWith + "'가 없습니다. 있는 것: " + string.Join(" · ", names));
                }
                if (!item.IsEnabled)
                {
                    menu.IsOpen = false;
                    throw new InvalidOperationException("메뉴 항목 '" + itemStartsWith + "'가 꺼져 있습니다.");
                }
                menu.IsOpen = false;
                Invoke(item);
            }
            else
            {
                menu.IsOpen = false;
            }
            return names;
        }

        public static void Select(Selector selector, int index)
        {
            selector.SelectedIndex = index;
        }

        /// <summary>콤보의 항목 가운데 글이 이것인 것을 고른다.</summary>
        public static void Choose(ComboBox combo, string text)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i];
                var label = item is ComboBoxItem cbi ? ContentText(cbi.Content) : ContentText(item);
                if (label == text)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            throw new InvalidOperationException("콤보에 '" + text + "'가 없습니다.");
        }

        // ---------- 키 ----------
        // 키 입력을 보내지 않고(SendInput 아님) 요소에 키 이벤트를 직접 낸다. Ctrl+C·X·V처럼 클립보드를 쓰는 키는 시험하지 않는다 —
        // 클립보드는 사용자 세션과 같이 쓰므로 사용자가 복사해 둔 것을 덮는다.

        /// <summary>
        /// 요소에 키보드 포커스를 준다(창 활성화 + Keyboard.Focus — 숨은 데스크톱에서도 된다). TextBox의 Tab·Enter 처리처럼
        /// IsKeyboardFocused를 보는 처리기는 포커스가 없으면 조용히 아무것도 하지 않으므로, 키를 내기 전에 부른다.
        /// </summary>
        public static void Focus(UIElement element)
        {
            System.Windows.Window.GetWindow(element)?.Activate();
            Keyboard.Focus(element);
        }

        /// <summary>
        /// 키를 누른 것처럼 PreviewKeyDown(터널) → 처리되지 않았으면 KeyDown(버블)을 그 요소에 낸다. 처리됐으면 true.
        /// 수정 키(Ctrl·Shift·Alt)는 이 스레드의 키 상태 표로 흉내 내므로 처리기의 Keyboard.Modifiers가 따라온다.
        /// </summary>
        public static bool Press(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            var handled = false;
            WithModifiers(modifiers, () =>
            {
                var source = PresentationSource.FromVisual(target);
                var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(preview);
                handled = preview.Handled;
                if (!handled)
                {
                    var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                    target.RaiseEvent(down);
                    handled = down.Handled;
                }
            });
            return handled;
        }

        /// <summary>글자 입력(TextInput)을 그 요소에 낸다 — TextBox에 사람이 친 것처럼 들어간다(Text 속성을 바로 바꾸는 것과 달리 입력 처리기가 돈다).</summary>
        public static void TypeText(UIElement target, string text)
        {
            target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, text))
            {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
        }

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] state);

        [DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] state);

        /// <summary>
        /// 이 스레드의 키 상태 표에서 Ctrl·Shift·Alt를 누른 것으로 두고 press를 부른 뒤 되돌린다. 이 스레드가 읽는 상태만 바꾸므로
        /// 다른 창·사용자 입력에는 영향이 없다. WPF Keyboard.Modifiers는 GetKeyState로 이 표를 읽는다.
        /// </summary>
        private static void WithModifiers(ModifierKeys modifiers, Action press)
        {
            if (modifiers == ModifierKeys.None)
            {
                press();
                return;
            }
            var saved = new byte[256];
            GetKeyboardState(saved);
            var state = (byte[])saved.Clone();
            if ((modifiers & ModifierKeys.Control) != 0)
                state[0x11] = state[0xA2] = 0x80;
            if ((modifiers & ModifierKeys.Shift) != 0)
                state[0x10] = state[0xA0] = 0x80;
            if ((modifiers & ModifierKeys.Alt) != 0)
                state[0x12] = state[0xA4] = 0x80;
            SetKeyboardState(state);
            try
            {
                press();
            }
            finally
            {
                SetKeyboardState(saved);
            }
        }

        // ---------- 캡처 ----------

        /// <summary>창 내용을 PNG로(창 배경 위에 그린다 — RenderTargetBitmap은 화면 합성과 무관해 숨은 데스크톱에서도 같다).</summary>
        public static string Snap(Window window, string folder, string name)
        {
            // 창 템플릿의 맨 위 요소(클라이언트 영역 전체) — 내용만 그리면 내용의 Margin이 잘려 Folderss 창에서 보이는 것과 달라진다
            var client = VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;
            if (client == null)
                throw new InvalidOperationException("창이 아직 그려지지 않았습니다.");
            client.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(client.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(client.ActualHeight));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(client) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(folder, name + ".png");
            using (var stream = File.Create(path))
                encoder.Save(stream);
            return path;
        }
    }
}
