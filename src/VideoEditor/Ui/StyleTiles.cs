using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 꾸민 글자 스타일 단추(속성 패널 [꾸미기]의 스타일 모음·내 스타일, 미디어 패널 [글자] 탭의 스타일 고르기):
    /// 어두운 틀에 이름 글자를 두고, 그리개로 그린 썸네일("가나Ab" — 아트 요소면 도형 견본)이 오면 그것으로 바꾼다.
    /// </summary>
    internal static class StyleTiles
    {
        // 스타일 모음 단추: 어두운 바탕(흰 글자·속 빈 글자도 보이게)에 썸네일을 줄여 놓는다(배율은 모두 같고, 칠한 곳이 틀보다 크면 그것만 더 줄임).
        // 16개라 좁은 속성 패널(안쪽 약 200px)에도 한 줄에 셋씩 들어가게 — 단추 한 칸 = 틀 56 + 안쪽 여백·테두리 4 + 사이 3 = 63px
        public const double TileWidth = 56;
        public const double TileHeight = 38;
        private const double ThumbnailScale = 0.34;
        // 칠한 곳을 잴 때 이 알파 이하(옅은 흐림·광선 끝)는 빼고 잰다 — 틀 밖으로 나가면 잘린다
        private const byte ThumbnailInkAlpha = 24;
        private static readonly Brush Backdrop = FrozenBrush(Color.FromRgb(0x50, 0x50, 0x50));

        /// <summary>스타일 단추 하나(속성 패널 스타일 모음·내 스타일, 미디어 패널 [글자] 탭 공용 — 어두운 틀 + 이름 글자, 썸네일은 <see cref="ShowThumbnail"/>로 틀에 넣는다). 화면 낭독 이름·툴팁 = 스타일 이름.</summary>
        public static (Button Button, Border Frame) Tile(string name, string automationId)
        {
            var fallback = new TextBlock
            {
                Text = name,
                Foreground = Brushes.White,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
            var frame = new Border { Width = TileWidth, Height = TileHeight, Background = Backdrop, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = fallback };
            var button = new Button { Content = frame, Padding = new Thickness(1), Margin = new Thickness(0, 0, 3, 3), ToolTip = name };
            AutomationProperties.SetAutomationId(button, automationId);
            AutomationProperties.SetName(button, name);
            return (button, frame);
        }

        /// <summary>
        /// 썸네일을 단추 틀(Width·Height를 정한 Border)에 넣는다(이미 그려 둔 것이면 바로, 아니면 다 그린 뒤 UI 스레드에서). 그리개가 없거나 실패하면 이름 글자를 그대로 둔다.
        /// 썸네일 PNG의 가운데는 배치 상자 가운데라 반사·휘기는 칠한 곳이 한쪽으로 치우친다 — 칠한 곳의 가운데를 틀 가운데에 두고,
        /// 칠한 곳이 틀(양옆 2px 남김)보다 크면 그만큼 더 줄인다(기본 배율보다 키우지는 않음). art를 주면 글자 대신 그 모양 견본을 그린다.
        /// </summary>
        public static void ShowThumbnail(EditorServices services, Border frame, TextStyle style, string automationId, ArtShape art = null)
        {
            void Show(BitmapSource picture)
            {
                var ink = InkBounds(picture);
                var scale = Math.Min(ThumbnailScale, Math.Min((frame.Width - 4) / ink.Width, (frame.Height - 4) / ink.Height));
                var image = new Image
                {
                    Source = picture,
                    Width = picture.PixelWidth * scale,
                    Height = picture.PixelHeight * scale,
                    Stretch = Stretch.Fill
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                AutomationProperties.SetAutomationId(image, automationId);
                // Canvas는 틀보다 큰 그림을 자기 자리로 자르지 않는다 — 넘치는 곳은 틀(ClipToBounds)이 자른다
                Canvas.SetLeft(image, frame.Width / 2 - (ink.X + ink.Width / 2) * scale);
                Canvas.SetTop(image, frame.Height / 2 - (ink.Y + ink.Height / 2) * scale);
                frame.Child = new Canvas { Children = { image } };
            }
            if (services == null)
                return;
            var task = services.TextStylePreview(style, art);
            if (task.IsCompleted)
            {
                if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                    Show(task.Result);
                return;
            }
            task.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result == null)
                    return;
                frame.Dispatcher.BeginInvoke(new Action(() => Show(t.Result)));
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// 내 스타일 줄(속성 패널 [꾸미기]·미디어 패널 [글자]): 내 스타일이 바뀌면(이 창이든 다른 편집 창이든 — <see cref="EditorServices.TextStylesChanged"/>)
        /// owner의 Dispatcher에서 rebuild로 다시 읽는다(#34). owner가 화면에 붙어 있는 동안만 듣는다(떼어 낸 패널이 남아 불리지 않게).
        /// </summary>
        public static void ListenToMine(EditorServices services, FrameworkElement owner, Action rebuild)
        {
            if (services == null)
                return;
            EventHandler changed = (s, e) => owner.Dispatcher.BeginInvoke(rebuild);
            owner.Loaded += (s, e) =>
            {
                services.TextStylesChanged -= changed;
                services.TextStylesChanged += changed;
            };
            owner.Unloaded += (s, e) => services.TextStylesChanged -= changed;
        }

        /// <summary>썸네일에서 알파가 <see cref="ThumbnailInkAlpha"/>를 넘는 픽셀의 경계(픽셀 단위). 그런 픽셀이 없으면 그림 전체.</summary>
        // 썸네일마다 칠한 곳은 한 번만 잰다(탭을 볼 때마다 UI 스레드에서 다시 재지 않게) — 썸네일 그림이 캐시에서 빠지면 함께 사라진다
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, object> InkCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, object>();

        private static Rect InkBounds(BitmapSource picture)
        {
            return (Rect)InkCache.GetValue(picture, p => MeasureInk(p));
        }

        private static Rect MeasureInk(BitmapSource picture)
        {
            var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            int left = width, top = height, right = -1, bottom = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[(y * width + x) * 4 + 3] <= ThumbnailInkAlpha)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            return right < 0 ? new Rect(0, 0, width, height) : new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
