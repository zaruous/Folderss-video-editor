using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;
using static VideoEditor.UiTests.EditorTestKit;

namespace VideoEditor.UiTests
{
    /// <summary>
    /// 꾸민 글자(이슈 #3·#13·#14·#15·#16·#17·#18·#19·#21·#22): WPF 그리개의 픽셀, 꾸미기 탭(스타일 모음·내 스타일·글꼴·채우기·윤곽선·그림자·광선·반사·휘기), 미리 보기 = 내보내기.
    /// 그리개·요청은 플러그인 형식이라 리플렉션으로 만들고, 요청은 JSON을 플러그인의 TextImageRequest로 역직렬화해 만든다.
    /// </summary>
    public class StyledTextTests
    {
        private const string Fill = "#FF3366";
        private readonly ITestOutputHelper _out;

        public StyledTextTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- 그리개 픽셀 ----------

        /// <summary>디코드한 PNG(BGRA, 알파는 곱하지 않은 값).</summary>
        private sealed class Png
        {
            public int Width;
            public int Height;
            public double Dpi;
            public byte[] Bgra;

            public int Alpha(int x, int y) { return Bgra[(y * Width + x) * 4 + 3]; }

            public (int R, int G, int B, int A) Pixel(int x, int y)
            {
                var i = (y * Width + x) * 4;
                return (Bgra[i + 2], Bgra[i + 1], Bgra[i], Bgra[i + 3]);
            }

            /// <summary>가장자리 1px 줄(위·아래·왼쪽·오른쪽)에서 조금이라도 칠한 픽셀 수.</summary>
            public int BorderInk()
            {
                var count = 0;
                for (var x = 0; x < Width; x++)
                    count += (Alpha(x, 0) > 0 ? 1 : 0) + (Alpha(x, Height - 1) > 0 ? 1 : 0);
                for (var y = 1; y < Height - 1; y++)
                    count += (Alpha(0, y) > 0 ? 1 : 0) + (Alpha(Width - 1, y) > 0 ? 1 : 0);
                return count;
            }

            public bool Painted(int x, int y) { return Alpha(x, y) > 128; }

            public int PaintedCount()
            {
                var count = 0;
                for (var i = 3; i < Bgra.Length; i += 4)
                {
                    if (Bgra[i] > 128)
                        count++;
                }
                return count;
            }

            /// <summary>조금이라도 칠한(알파 &gt; 0) 픽셀 수.</summary>
            public int InkCount()
            {
                var count = 0;
                for (var i = 3; i < Bgra.Length; i += 4)
                {
                    if (Bgra[i] > 0)
                        count++;
                }
                return count;
            }

            /// <summary>어두운(그림자) 또는 밝은(글자) 픽셀의 알파 가중 무게중심(x, y)과 알파 합.</summary>
            public (double X, double Y, double Weight) Centroid(bool dark)
            {
                double sumX = 0, sumY = 0, weight = 0;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var p = Pixel(x, y);
                        if (p.A == 0 || ((p.R + p.G + p.B) / 3 < 128) != dark)
                            continue;
                        sumX += (double)x * p.A;
                        sumY += (double)y * p.A;
                        weight += p.A;
                    }
                }
                return weight > 0 ? (sumX / weight, sumY / weight, weight) : (double.NaN, double.NaN, 0);
            }

            /// <summary>어두운(그림자) 또는 밝은(글자) 픽셀 가운데 가장 큰 알파.</summary>
            public int MaxAlpha(bool dark)
            {
                var max = 0;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var p = Pixel(x, y);
                        if (p.A > 0 && ((p.R + p.G + p.B) / 3 < 128) == dark)
                            max = Math.Max(max, p.A);
                    }
                }
                return max;
            }

            /// <summary>[top, bottom) 줄들에서 칠한 픽셀의 가장 왼쪽·오른쪽 x(없으면 -1).</summary>
            public (int Left, int Right) Extent(int top, int bottom)
            {
                int left = int.MaxValue, right = -1;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        if (!Painted(x, y))
                            continue;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
                return (right < 0 ? -1 : left, right);
            }

            /// <summary>[left, right) 열들에서 칠한 픽셀의 평균 y(무게중심, 없으면 NaN).</summary>
            public double CenterY(int left, int right)
            {
                double sum = 0;
                var count = 0;
                for (var x = left; x < right; x++)
                {
                    for (var y = 0; y < Height; y++)
                    {
                        if (!Painted(x, y))
                            continue;
                        sum += y;
                        count++;
                    }
                }
                return count == 0 ? double.NaN : sum / count;
            }

            /// <summary>y 줄에서 칠한 픽셀이 이어진 토막 수(글자 속 구멍이 있으면 글자 하나가 토막 둘).</summary>
            public int Runs(int y)
            {
                var runs = 0;
                for (var x = 0; x < Width; x++)
                {
                    if (Painted(x, y) && (x == 0 || !Painted(x - 1, y)))
                        runs++;
                }
                return runs;
            }

            /// <summary>칠한 픽셀이 있는 가장 위·아래 줄(없으면 -1).</summary>
            public (int Top, int Bottom) Rows()
            {
                int top = -1, bottom = -1;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        if (!Painted(x, y))
                            continue;
                        if (top < 0)
                            top = y;
                        bottom = y;
                        break;
                    }
                }
                return (top, bottom);
            }
        }

        private static string Folder()
        {
            var folder = Path.Combine(PluginUnderTest.Load(UiThread.Instance).Root, "styled-text-tests");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static object NewRasterizer()
        {
            var type = PluginUnderTest.Load(UiThread.Instance).Assembly.GetType("VideoEditor.Ui.StyledTextRasterizer", true);
            return Activator.CreateInstance(type);
        }

        private static object Request(object rasterizer, string text, int canvasWidth = 1280, int canvasHeight = 720, double fontSize = 80, string style = null, double effectScale = 1.0)
        {
            var json = JsonSerializer.Serialize(new
            {
                Text = text,
                FontPixelSize = fontSize,
                EffectScale = effectScale,
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight
            });
            var styleJson = "{\"FontFamily\":\"Malgun Gothic\",\"Fill\":{\"Kind\":\"Solid\",\"Colors\":[\"" + Fill + "\"]}" + (style == null ? "" : "," + style) + "}";
            json = json.TrimEnd('}') + ",\"Style\":" + styleJson + "}";
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            return JsonSerializer.Deserialize(json, rasterizer.GetType().Assembly.GetType("VideoEditor.Core.TextImageRequest", true), options);
        }

        /// <summary>STA가 아닌 스레드(Task.Run)에서 그리게 하고 PNG를 읽는다.</summary>
        private static async Task<Png> Render(object rasterizer, object request, string name)
        {
            var path = Path.Combine(Folder(), name + ".png");
            var render = rasterizer.GetType().GetMethod("Render");
            await Task.Run(() =>
            {
                Assert.NotEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                try
                {
                    render.Invoke(rasterizer, new[] { request, path, (object)CancellationToken.None });
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException;
                }
            });
            return UiThread.Instance.Run(() =>
            {
                using (var stream = File.OpenRead(path))
                {
                    var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    var png = new Png { Width = bgra.PixelWidth, Height = bgra.PixelHeight, Dpi = frame.DpiX, Bgra = new byte[bgra.PixelWidth * bgra.PixelHeight * 4] };
                    bgra.CopyPixels(png.Bgra, png.Width * 4, 0);
                    return png;
                }
            });
        }

        /// <summary>(x, y) 줄에서 x에 가장 가까운 칠한 픽셀의 색(R, G, B) — 글자 사이 빈틈을 피한다.</summary>
        private static (int R, int G, int B) ColorNear(Png png, int x, int y)
        {
            for (var d = 0; d < png.Width; d++)
            {
                foreach (var probe in new[] { x - d, x + d })
                {
                    if (probe >= 0 && probe < png.Width && png.Painted(probe, y))
                    {
                        var i = (y * png.Width + probe) * 4;
                        return (png.Bgra[i + 2], png.Bgra[i + 1], png.Bgra[i]);
                    }
                }
            }
            return (-1, -1, -1);
        }

        [UiFact]
        public async Task Rasterizer_LinearGradient_RedLeftBlueRight_FourColorsInOrder()
        {
            // 0° 선형(빨강 → 파랑): 글자 모양의 왼쪽 끝은 빨강 쪽, 오른쪽 끝은 파랑 쪽
            var rasterizer = NewRasterizer();
            var two = await Render(rasterizer, Request(rasterizer, "■■■■", fontSize: 120,
                style: "\"Bold\":true,\"Fill\":{\"Kind\":\"Linear\",\"Colors\":[\"#FF0000\",\"#0000FF\"],\"Angle\":0}"), "fill-linear");
            var middle = two.Height / 2;
            var (left, right) = two.Extent(middle, middle + 1);
            var start = ColorNear(two, left + 2, middle);
            var end = ColorNear(two, right - 2, middle);
            _out.WriteLine("왼쪽 " + start + " 오른쪽 " + end);
            Assert.True(start.R > 200 && start.B < 60, "왼쪽 " + start);
            Assert.True(end.B > 200 && end.R < 60, "오른쪽 " + end);

            // 4색(빨강·노랑·초록·파랑)이 순서대로 — 정지점 0·⅓·⅔·1 사이를 1/8·3/8·5/8·7/8에서 본다
            var four = await Render(rasterizer, Request(rasterizer, "■■■■", fontSize: 120,
                style: "\"Bold\":true,\"Fill\":{\"Kind\":\"Linear\",\"Colors\":[\"#FF0000\",\"#FFFF00\",\"#00FF00\",\"#0000FF\"],\"Angle\":0}"), "fill-linear-4");
            (left, right) = four.Extent(middle, middle + 1);
            var samples = new[] { 1, 3, 5, 7 }.Select(k => ColorNear(four, left + (right - left) * k / 8, middle)).ToArray();
            _out.WriteLine("4색: " + string.Join(" ", samples));
            Assert.True(samples[0].R > 200 && samples[0].R > samples[0].G && samples[0].B < 60, "1/8 빨강~주황 " + samples[0]);
            Assert.True(samples[1].R > 150 && samples[1].G > 150 && samples[1].B < 60, "3/8 노랑 " + samples[1]);
            Assert.True(samples[2].G > 200 && samples[2].R < 100 && samples[2].B < 60, "5/8 초록 " + samples[2]);
            Assert.True(samples[3].B > samples[3].G && samples[3].R < 60, "7/8 파랑 쪽 " + samples[3]);
        }

        [UiFact]
        public async Task Rasterizer_RadialGradient_CenterIsFirstColor_NoneFillPaintsNothing()
        {
            var rasterizer = NewRasterizer();
            var radial = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200,
                style: "\"Fill\":{\"Kind\":\"Radial\",\"Colors\":[\"#FFFF00\",\"#0000FF\"]}"), "fill-radial");
            // 원형의 가운데는 글자 모양(칠한 곳)의 가운데 — PNG 가운데와 다를 수 있다
            var (left, right) = radial.Extent(0, radial.Height);
            var column = (left + right) / 2;
            var rows = Enumerable.Range(0, radial.Height).Where(y => radial.Painted(column, y)).ToList();
            var middle = (rows.First() + rows.Last()) / 2;
            var center = ColorNear(radial, column, middle);
            var edge = ColorNear(radial, left + 1, middle);
            _out.WriteLine("가운데 " + center + " 가장자리 " + edge);
            Assert.True(center.R > 200 && center.G > 200 && center.B < 60, "가운데는 색1(노랑) " + center);
            Assert.True(edge.B > center.B + 100, "가장자리는 색2(파랑) 쪽 " + edge);

            // 채우기 없음(윤곽선도 없음): 칠한 픽셀이 없다
            var none = await Render(rasterizer, Request(rasterizer, "가나다 ABC", style: "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]}"), "fill-none");
            Assert.Equal(0, none.PaintedCount());
        }

        private const string WhiteFill = "\"Fill\":{\"Kind\":\"Solid\",\"Colors\":[\"#FFFFFF\"]}";

        private static string Outline(double width, string join = "Round")
        {
            return "\"Outline\":{\"Enabled\":true,\"Color\":\"#000000\",\"Width\":" + width.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"Join\":\"" + join + "\"}";
        }

        [UiFact]
        public async Task Rasterizer_Outline_BlackBandOutsideTheFill_HollowWithoutFill()
        {
            var rasterizer = NewRasterizer();
            var plain = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill), "outline-off");
            var outlined = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Outline(8)), "outline-on");
            var middle = outlined.Height / 2;
            var (plainLeft, plainRight) = plain.Extent(plain.Height / 2, plain.Height / 2 + 1);
            var (left, right) = outlined.Extent(middle, middle + 1);
            _out.WriteLine("윤곽선 없음 폭 " + (plainRight - plainLeft) + " · 두께 8 폭 " + (right - left));
            // 칠한 폭이 양쪽으로 두께(8px)씩 넓어지고, 바깥 띠는 검정·그 안(글자)은 흰색
            Assert.InRange(right - left - (plainRight - plainLeft), 16 - 2, 16 + 2);
            var band = outlined.Pixel(left + 3, middle);
            var inside = outlined.Pixel(left + 8 + 4, middle);
            _out.WriteLine("띠 " + band + " · 안 " + inside);
            Assert.True(band.A == 255 && band.R < 40 && band.G < 40 && band.B < 40, "바깥 띠는 검정 " + band);
            Assert.True(inside.A == 255 && inside.R > 230 && inside.G > 230 && inside.B > 230, "글자 안은 흰색 " + inside);

            // 효과 배율 2(예: 1080p 프로젝트를 4K로 내보낼 때)면 띠도 두 배(16px) — 글자 크기는 그대로 두고 배율만 바꾼다
            var doubled = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Outline(8), effectScale: 2), "outline-scale-2");
            var (doubledLeft, doubledRight) = doubled.Extent(doubled.Height / 2, doubled.Height / 2 + 1);
            _out.WriteLine("배율 2 폭 " + (doubledRight - doubledLeft));
            Assert.InRange(doubledRight - doubledLeft - (plainRight - plainLeft), 32 - 2, 32 + 2);
            Assert.Equal(0, doubled.BorderInk());

            // 채우기 없음 + 윤곽선: 바깥 가장자리는 같고 속은 비었다(투명)
            var hollow = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200,
                style: "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]}," + Outline(8)), "outline-hollow");
            Assert.Equal(outlined.Width, hollow.Width);
            var (hollowLeft, hollowRight) = hollow.Extent(middle, middle + 1);
            Assert.InRange(hollowLeft - left, -1, 1);
            Assert.InRange(hollowRight - right, -1, 1);
            Assert.True(hollow.Pixel(hollowLeft + 3, middle).A == 255, "바깥 띠는 칠함");
            Assert.Equal(0, hollow.Alpha(hollowLeft + 8 + 4, middle));
            Assert.Equal(0, hollow.Alpha((hollowLeft + hollowRight) / 2, middle));

            // 반투명 채우기: 윤곽선 안쪽 절반이 비쳐 어두워지지 않는다(글자 밖으로만 그림)
            var glass = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200,
                style: "\"Fill\":{\"Kind\":\"Solid\",\"Colors\":[\"#80FFFFFF\"]}," + Outline(8)), "outline-glass");
            var glassInside = glass.Pixel(left + 8 + 4, middle);
            _out.WriteLine("반투명 안 " + glassInside);
            Assert.InRange(glassInside.A, 100, 160);
            Assert.True(glassInside.R > 230 && glassInside.G > 230 && glassInside.B > 230, "반투명 흰색 그대로 " + glassInside);
        }

        [UiFact]
        public async Task Rasterizer_OutlineJoins_DifferAtCorners_ThickOutlineStaysInside()
        {
            // "W"의 뾰족한 꼭짓점: 각지게(한도 2) > 둥글게 > 깎아서 순으로 칠한 넓이가 크다
            var rasterizer = NewRasterizer();
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var join in new[] { "Round", "Miter", "Bevel" })
            {
                var png = await Render(rasterizer, Request(rasterizer, "W", fontSize: 200, style: WhiteFill + "," + Outline(12, join)), "outline-join-" + join);
                counts[join] = png.PaintedCount();
                Assert.Equal(0, png.BorderInk());
            }
            _out.WriteLine("칠한 픽셀 둥글게 " + counts["Round"] + " · 각지게 " + counts["Miter"] + " · 깎아서 " + counts["Bevel"]);
            Assert.True(counts["Miter"] > counts["Round"] + 20, "각지게 > 둥글게");
            Assert.True(counts["Round"] > counts["Bevel"] + 20, "둥글게 > 깎아서");

            // 가장 두꺼운 50px도(각지게 모서리 끝까지) 그림 가장자리 1px 줄에 닿지 않는다
            foreach (var join in new[] { "Miter", "Round" })
            {
                var thick = await Render(rasterizer, Request(rasterizer, "WAV가", fontSize: 120, style: WhiteFill + "," + Outline(50, join)), "outline-50-" + join);
                _out.WriteLine("두께 50 " + join + " " + thick.Width + "×" + thick.Height + " · 칠한 픽셀 " + thick.PaintedCount());
                Assert.True(thick.PaintedCount() > 1000);
                Assert.Equal(0, thick.BorderInk());
            }
        }

        private static string Shadow(double distance, double angle, double blur, double opacity = 1, string color = "#000000")
        {
            string N(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "\"Shadow\":{\"Enabled\":true,\"Color\":\"" + color + "\",\"Distance\":" + N(distance) + ",\"Angle\":" + N(angle)
                + ",\"Blur\":" + N(blur) + ",\"Opacity\":" + N(opacity) + "}";
        }

        private static string WhiteOutline(double width, string join = "Round")
        {
            return Outline(width, join).Replace("\"#000000\"", "\"#FFFFFF\"");
        }

        // 가는 획("I") 사이를 띄워 둔 글 — 거리 20px 그림자가 옆 글자에 가려지지 않아 그림자 전체가 보인다
        private const string Stems = "I I I";

        /// <summary>y 줄에서 가장 오른쪽 흰(불투명한 밝은) 픽셀의 x — 그림자(검정)는 건너뛴다.</summary>
        private static int WhiteEdge(Png png, int y)
        {
            for (var x = png.Width - 1; x >= 0; x--)
            {
                var p = png.Pixel(x, y);
                if (p.A > 200 && p.R > 200)
                    return x;
            }
            return -1;
        }

        [UiFact]
        public async Task Rasterizer_Shadow_MovesByDistanceAndAngle()
        {
            // 흰 글자 + 검정 그림자(거리 20, 흐림 0): 그림자 픽셀의 무게중심이 글자 무게중심에서 0°면 오른쪽으로 20px, 225°면 왼쪽 위로 (-14.1, -14.1)
            var rasterizer = NewRasterizer();
            foreach (var (angle, dx, dy) in new[] { (0.0, 20.0, 0.0), (225.0, -14.14, -14.14) })
            {
                var png = await Render(rasterizer, Request(rasterizer, Stems, fontSize: 120, style: WhiteFill + "," + Shadow(20, angle, 0)), "shadow-angle-" + angle);
                var text = png.Centroid(false);
                var shadow = png.Centroid(true);
                _out.WriteLine(angle + "°: 글자 (" + text.X.ToString("F2") + ", " + text.Y.ToString("F2") + ") · 그림자 (" + shadow.X.ToString("F2") + ", " + shadow.Y.ToString("F2") + ")");
                Assert.True(shadow.Weight > 0, "그림자가 있어야 한다");
                Assert.InRange(shadow.X - text.X, dx - 1.5, dx + 1.5);
                Assert.InRange(shadow.Y - text.Y, dy - 1.5, dy + 1.5);
                Assert.Equal(0, png.BorderInk());
            }
            // 끄면 그림자 픽셀이 없다
            var off = await Render(rasterizer, Request(rasterizer, Stems, fontSize: 120, style: WhiteFill), "shadow-off");
            Assert.Equal(0, off.Centroid(true).Weight);
        }

        [UiFact]
        public async Task Rasterizer_ShadowBlur_SpreadsMore_OpacityHalvesAlpha()
        {
            var rasterizer = NewRasterizer();
            var sharp = await Render(rasterizer, Request(rasterizer, Stems, fontSize: 120, style: WhiteFill + "," + Shadow(20, 0, 0)), "shadow-blur-0");
            var blurred = await Render(rasterizer, Request(rasterizer, Stems, fontSize: 120, style: WhiteFill + "," + Shadow(20, 0, 20)), "shadow-blur-20");
            var half = await Render(rasterizer, Request(rasterizer, Stems, fontSize: 120, style: WhiteFill + "," + Shadow(20, 0, 0, 0.5)), "shadow-opacity-50");
            _out.WriteLine("알파 > 0 픽셀 흐림 0 " + sharp.InkCount() + " · 흐림 20 " + blurred.InkCount()
                + " · 그림자 최대 알파 100% " + sharp.MaxAlpha(true) + " · 50% " + half.MaxAlpha(true));
            // 흐림을 키우면 그림자가 퍼져 칠한 픽셀이 늘고, 가장 진한 곳은 옅어진다
            Assert.True(blurred.InkCount() > sharp.InkCount() * 3 / 2, "흐림 20이 더 퍼져야 한다");
            Assert.True(blurred.MaxAlpha(true) < sharp.MaxAlpha(true));
            Assert.Equal(0, blurred.BorderInk());
            // 불투명도 50%면 그림자 최대 알파가 절반쯤, 글자는 그대로 불투명
            Assert.Equal(255, sharp.MaxAlpha(true));
            Assert.InRange(half.MaxAlpha(true), 120, 136);
            Assert.Equal(255, half.MaxAlpha(false));
            // 그림자 색의 알파(#80…)도 불투명도처럼 곱한다
            var tinted = await Render(rasterizer, Request(rasterizer, Stems, fontSize: 120, style: WhiteFill + "," + Shadow(20, 0, 0, 1, "#80000000")), "shadow-color-alpha");
            Assert.InRange(tinted.MaxAlpha(true), 120, 136);
        }

        [UiFact]
        public async Task Rasterizer_Shadow_IsWhatTheBodyPaints_MaxStaysInside()
        {
            // 흰 채우기 + 흰 윤곽선 8 + 검정 그림자(30, 0°, 흐림 0, 50%): 가운데 줄에서 윤곽선 바깥 가장자리 E의 오른쪽을 본다.
            // 그림자 = 글자(E+22까지) + 윤곽선(E+14~E+30) — 글자만(E+8)·겹친 곳(E+18)·윤곽선만(E+26) 모두 같은 알파(겹친 곳이 더 진하지 않음)
            var rasterizer = NewRasterizer();
            var filled = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + WhiteOutline(8) + "," + Shadow(30, 0, 0, 0.5)), "shadow-filled");
            var middle = filled.Height / 2;
            var edge = WhiteEdge(filled, middle);
            var alphas = new[] { 8, 18, 26 }.Select(d => filled.Pixel(edge + d, middle)).ToArray();
            _out.WriteLine("채움: 가장자리 " + edge + " · E+8 " + alphas[0] + " · E+18 " + alphas[1] + " · E+26 " + alphas[2] + " · E+34 " + filled.Pixel(edge + 34, middle));
            foreach (var p in alphas)
                Assert.True(p.A >= 120 && p.A <= 136 && p.R < 40, "그림자 50% 검정 " + p);
            Assert.Equal(0, filled.Alpha(edge + 34, middle));

            // 채우기 없음 + 윤곽선: 그림자도 윤곽선 고리만 — 글자 속(E+8)과 글자 가운데는 비었고 고리 그림자(E+26)만 있다
            var hollow = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200,
                style: "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]}," + WhiteOutline(8) + "," + Shadow(30, 0, 0)), "shadow-hollow");
            middle = hollow.Height / 2;
            var left = hollow.Extent(middle, middle + 1).Left;
            var ring = WhiteEdge(hollow, middle);
            _out.WriteLine("속 빈 글자: 고리 " + ring + " · E+8 " + hollow.Pixel(ring + 8, middle) + " · E+26 " + hollow.Pixel(ring + 26, middle));
            Assert.Equal(0, hollow.Alpha(ring + 8, middle));
            Assert.Equal(0, hollow.Alpha((left + ring) / 2, middle));
            var shadowRing = hollow.Pixel(ring + 26, middle);
            Assert.True(shadowRing.A > 200 && shadowRing.R < 40, "고리 그림자 " + shadowRing);

            // 가장 먼 그림자(200) + 가장 큰 흐림(100) + 가장 굵은 윤곽선(각지게 50)도 그림 가장자리 1px 줄에 닿지 않는다(캔버스가 충분히 클 때)
            foreach (var angle in new[] { 0.0, 45, 135, 270 })
            {
                var max = await Render(rasterizer, Request(rasterizer, "WAV가", 3840, 2160, 120, WhiteFill + "," + WhiteOutline(50, "Miter") + "," + Shadow(200, angle, 100)), "shadow-max-" + angle);
                _out.WriteLine(angle + "° 최대 " + max.Width + "×" + max.Height + " · 그림자 알파 합 " + max.Centroid(true).Weight);
                Assert.True(max.Centroid(true).Weight > 0, "그림자가 있어야 한다");
                Assert.Equal(0, max.BorderInk());
            }
            // 효과 배율 2: 거리·흐림 반지름 모두 두 배(흐림 상한 100은 곱하기 전 — #26)
            var scaled = await Render(rasterizer, Request(rasterizer, "WAV가", 3840, 2160, 240, WhiteFill + "," + Shadow(200, 30, 100), 2), "shadow-max-scale-2");
            Assert.Equal(0, scaled.BorderInk());
        }

        private static string Glow(double size, double opacity = 1, string color = "#FFFF00", bool enabled = true)
        {
            string N(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "\"Glow\":{\"Enabled\":" + (enabled ? "true" : "false") + ",\"Color\":\"" + color + "\",\"Size\":" + N(size) + ",\"Opacity\":" + N(opacity) + "}";
        }

        private const string NoFill = "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]}";

        /// <summary>노랑 계열 광선 픽셀: 조금이라도 칠했고(알파 &gt; 0) 빨강·초록이 밝고 파랑이 어둡다(흰 글자와 구별).</summary>
        private static bool Yellowish((int R, int G, int B, int A) p)
        {
            return p.A > 0 && p.R > 180 && p.G > 180 && p.B < 100;
        }

        /// <summary>노랑 계열 픽셀 수와 그 가운데 가장 큰 알파.</summary>
        private static (int Count, int MaxAlpha) Yellow(Png png)
        {
            int count = 0, max = 0;
            for (var y = 0; y < png.Height; y++)
            {
                for (var x = 0; x < png.Width; x++)
                {
                    var p = png.Pixel(x, y);
                    if (!Yellowish(p))
                        continue;
                    count++;
                    max = Math.Max(max, p.A);
                }
            }
            return (count, max);
        }

        /// <summary>y 줄에서 가장 오른쪽 흰(불투명하고 파랑까지 밝은) 픽셀의 x — 노랑 광선은 건너뛴다.</summary>
        private static int WhiteRight(Png png, int y)
        {
            for (var x = png.Width - 1; x >= 0; x--)
            {
                var p = png.Pixel(x, y);
                if (p.A > 200 && p.R > 200 && p.G > 200 && p.B > 200)
                    return x;
            }
            return -1;
        }

        /// <summary>y 줄에서 조금이라도 칠한(알파 &gt; 0) 가장 오른쪽 x.</summary>
        private static int LastInk(Png png, int y)
        {
            for (var x = png.Width - 1; x >= 0; x--)
            {
                if (png.Alpha(x, y) > 0)
                    return x;
            }
            return -1;
        }

        [UiFact]
        public async Task Rasterizer_Glow_RingOutsideTheGlyph_WiderWithSize()
        {
            // 흰 글자 + 노랑 광선(100%): 가운데 줄에서 글자 가장자리 E 바깥 5~15px 고리가 노랑 계열(알파 > 0)
            var rasterizer = NewRasterizer();
            var small = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Glow(20)), "glow-20");
            var large = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Glow(40)), "glow-40");
            var edges = new System.Collections.Generic.List<int>();
            foreach (var (png, size) in new[] { (small, 20), (large, 40) })
            {
                var middle = png.Height / 2;
                var edge = WhiteRight(png, middle);
                edges.Add(edge);
                _out.WriteLine("광선 " + size + ": " + png.Width + "×" + png.Height + " · 가장자리 " + edge + " · E+5 " + png.Pixel(edge + 5, middle) + " · E+15 " + png.Pixel(edge + 15, middle)
                    + " · 마지막 칠한 픽셀 E+" + (LastInk(png, middle) - edge) + " · 노랑 픽셀 " + Yellow(png).Count + " · 알파 > 0 " + png.InkCount());
                for (var d = 5; d <= 15; d++)
                    Assert.True(Yellowish(png.Pixel(edge + d, middle)), "광선 " + size + " E+" + d + " " + png.Pixel(edge + d, middle));
                // 글자는 광선 위에 그대로(가운데는 흰색)
                var center = png.Pixel(png.Width / 2, middle);
                Assert.True(center.A == 255 && center.B > 230, "글자 가운데 " + center);
                Assert.Equal(0, png.BorderInk());
            }
            // 광선은 크기 + 흐림 반지름(크기 / 2)까지 번진다: 20이면 E+35에 없고, 40이면 E+35도 반 넘게 진하다
            Assert.Equal(0, small.Alpha(edges[0] + 35, small.Height / 2));
            Assert.True(large.Alpha(edges[1] + 35, large.Height / 2) > 128);
            // 크기 40이면 고리가 넓다 — 노랑 픽셀·알파 > 0 픽셀이 늘어난다
            Assert.True(Yellow(large).Count > Yellow(small).Count * 3 / 2, "노랑 픽셀 20 " + Yellow(small).Count + " · 40 " + Yellow(large).Count);
            Assert.True(large.InkCount() > small.InkCount() * 5 / 4);

            // 끄면 노랑 픽셀이 없다
            var off = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill), "glow-none");
            Assert.Equal(0, Yellow(off).Count);
        }

        [UiFact]
        public async Task Rasterizer_GlowOff_ByteIdenticalToNoGlow()
        {
            var rasterizer = NewRasterizer();
            const string text = "가나다 ABC\n광선";
            // 광선 값이 없는 요청(#17까지와 같은 결과)
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4)), "glow-base");
            var expected = File.ReadAllBytes(Path.Combine(Folder(), "glow-base.png"));
            // 꺼져 있으면 색·크기·불투명도가 달라도, 켜져 있어도 크기가 0이면 광선 없이 그린 것과 바이트까지 같다
            foreach (var (name, glow) in new[] { ("glow-disabled", Glow(77, 0.3, "#FF0000", false)), ("glow-disabled-max", Glow(100, 1, "#00FF00", false)), ("glow-size-0", Glow(0)) })
            {
                await Render(rasterizer, Request(rasterizer, text, style: Outline(4) + "," + glow), name);
                Assert.True(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), name + ".png"))), name + "가 광선 없음과 다르다");
            }
            // 켜면 다르다(비교가 헛돌지 않는지)
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4) + "," + Glow(12)), "glow-on");
            Assert.False(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "glow-on.png"))));
        }

        [UiFact]
        public async Task Rasterizer_Glow_SurroundsOutline_ScalesWithEffect_MaxStaysInside()
        {
            // 흰 채우기 + 빨간 윤곽선 8 + 노랑 광선 20: 광선은 윤곽선 바깥 가장자리부터 — E+2~E+6은 빨간 윤곽선 그대로, E+10~E+24는 노랑
            var rasterizer = NewRasterizer();
            var plain = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Glow(20)), "glow-plain");
            var outlined = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200,
                style: WhiteFill + "," + Outline(8).Replace("\"#000000\"", "\"#FF0000\"") + "," + Glow(20)), "glow-outline");
            var middle = outlined.Height / 2;
            var edge = WhiteRight(outlined, middle);
            _out.WriteLine("윤곽선 + 광선: 가장자리 " + edge + " · E+4 " + outlined.Pixel(edge + 4, middle) + " · E+12 " + outlined.Pixel(edge + 12, middle));
            for (var d = 2; d <= 6; d++)
            {
                var p = outlined.Pixel(edge + d, middle);
                Assert.True(p.A == 255 && p.R > 200 && p.G < 60 && p.B < 60, "윤곽선 E+" + d + " " + p);
            }
            for (var d = 10; d <= 24; d++)
                Assert.True(Yellowish(outlined.Pixel(edge + d, middle)), "광선 E+" + d + " " + outlined.Pixel(edge + d, middle));
            // 윤곽선이 있으면 광선이 윤곽선 두께(8px)만큼 더 바깥까지 번진다
            var plainReach = LastInk(plain, plain.Height / 2) - WhiteRight(plain, plain.Height / 2);
            var outlinedReach = LastInk(outlined, middle) - edge;
            _out.WriteLine("번지는 거리: 광선만 " + plainReach + " · 윤곽선 8 + 광선 " + outlinedReach);
            Assert.InRange(outlinedReach - plainReach, 8 - 2, 8 + 2);

            // 효과 배율 2(예: 1080p 프로젝트를 4K로 내보낼 때)면 펜 두께·흐림이 모두 두 배 — 광선 20 × 배율 2 = 광선 40 × 배율 1(글자 크기는 그대로)
            await Render(rasterizer, Request(rasterizer, "■ A", fontSize: 120, style: WhiteFill + "," + Glow(20), effectScale: 2), "glow-20-scale-2");
            await Render(rasterizer, Request(rasterizer, "■ A", fontSize: 120, style: WhiteFill + "," + Glow(40)), "glow-40-scale-1");
            await Render(rasterizer, Request(rasterizer, "■ A", fontSize: 120, style: WhiteFill + "," + Glow(20)), "glow-20-scale-1");
            var doubled = File.ReadAllBytes(Path.Combine(Folder(), "glow-20-scale-2.png"));
            Assert.True(doubled.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "glow-40-scale-1.png"))), "배율 2 = 크기 두 배");
            Assert.False(doubled.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "glow-20-scale-1.png"))));

            // 가장 큰 광선(100) + 가장 굵은 윤곽선(각지게·둥글게 50), 속 빈 글자, 효과 배율 2도 그림 가장자리 1px 줄에 닿지 않는다(캔버스가 충분히 클 때)
            foreach (var (name, style, scale) in new[]
            {
                ("glow-max-miter", WhiteFill + "," + Outline(50, "Miter") + "," + Glow(100), 1.0),
                ("glow-max-round", WhiteFill + "," + Outline(50, "Round") + "," + Glow(100), 1.0),
                ("glow-max-hollow", NoFill + "," + WhiteOutline(50, "Miter") + "," + Glow(100), 1.0),
                ("glow-max-scale-2", WhiteFill + "," + Outline(50, "Miter") + "," + Glow(100), 2.0)
            })
            {
                var max = await Render(rasterizer, Request(rasterizer, "WAV가", 3840, 2160, 120 * scale, style, scale), name);
                _out.WriteLine(name + " " + max.Width + "×" + max.Height + " · 노랑 픽셀 " + Yellow(max).Count);
                Assert.True(Yellow(max).Count > 1000, name + " 광선이 있어야 한다");
                Assert.Equal(0, max.BorderInk());
            }
        }

        [UiFact]
        public async Task Rasterizer_GlowOpacity_HalvesAlpha_HollowGlowsAroundTheRing()
        {
            // 불투명도 50%면 광선 최대 알파가 절반쯤 — 펜과 채우기가 겹친 곳도 더 진하지 않다. 광선 색의 알파(#80…)도 불투명도처럼 곱한다
            var rasterizer = NewRasterizer();
            var full = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Glow(20)), "glow-opacity-100");
            var half = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Glow(20, 0.5)), "glow-opacity-50");
            var tinted = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: WhiteFill + "," + Glow(20, 1, "#80FFFF00")), "glow-color-alpha");
            _out.WriteLine("광선 최대 알파 100% " + Yellow(full).MaxAlpha + " · 50% " + Yellow(half).MaxAlpha + " · 색 알파 #80 " + Yellow(tinted).MaxAlpha);
            Assert.True(Yellow(full).MaxAlpha >= 250);
            Assert.InRange(Yellow(half).MaxAlpha, 120, 136);
            Assert.InRange(Yellow(tinted).MaxAlpha, 120, 136);
            Assert.Equal(255, half.Pixel(half.Width / 2, half.Height / 2).A);

            // 속 빈 글자(채우기 없음 + 흰 윤곽선 8) + 광선 20(50%): 광선은 고리 둘레만 — 고리 바깥(R+3)과 글자 속(고리 안쪽 가장자리에서 10px 안)은 노랑 50%
            // (글자 밖은 두 펜이 겹쳐도 더 진하지 않음), 글자 가운데는 비었다
            var hollow = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: NoFill + "," + WhiteOutline(8) + "," + Glow(20, 0.5)), "glow-hollow");
            var middle = hollow.Height / 2;
            var ring = WhiteRight(hollow, middle);
            var outside = hollow.Pixel(ring + 3, middle);
            var inside = hollow.Pixel(ring - 8 - 10, middle);
            _out.WriteLine("속 빈 글자: 고리 바깥 가장자리 " + ring + " · R+3 " + outside + " · 글자 속 " + inside + " · 가운데 " + hollow.Pixel(hollow.Width / 2, middle));
            Assert.True(Yellowish(outside) && outside.A >= 120 && outside.A <= 136, "고리 바깥 " + outside);
            Assert.True(Yellowish(inside) && inside.A >= 120 && inside.A <= 136, "글자 속 " + inside);
            Assert.Equal(0, hollow.Alpha(hollow.Width / 2, middle));
            Assert.Equal(0, hollow.BorderInk());

            // 채우기도 윤곽선도 없으면(칠하는 것이 없으면) 광선도 없다
            var none = await Render(rasterizer, Request(rasterizer, "■", fontSize: 200, style: NoFill + "," + Glow(20)), "glow-nothing");
            Assert.Equal(0, none.InkCount());
        }

        private static string Reflection(double size, double gap, double opacity = 1, bool enabled = true)
        {
            string N(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "\"Reflection\":{\"Enabled\":" + (enabled ? "true" : "false") + ",\"Size\":" + N(size) + ",\"Gap\":" + N(gap) + ",\"Opacity\":" + N(opacity) + "}";
        }

        /// <summary>조금이라도 칠한(알파 &gt; 0) 줄이 이어진 토막들 [Top, Bottom] — 글자와 반사 사이 빈 줄이 있으면 둘.</summary>
        private static System.Collections.Generic.List<(int Top, int Bottom)> InkBands(Png png)
        {
            var bands = new System.Collections.Generic.List<(int Top, int Bottom)>();
            var start = -1;
            for (var y = 0; y <= png.Height; y++)
            {
                var ink = y < png.Height && Enumerable.Range(0, png.Width).Any(x => png.Alpha(x, y) > 0);
                if (ink && start < 0)
                    start = y;
                else if (!ink && start >= 0)
                {
                    bands.Add((start, y - 1));
                    start = -1;
                }
            }
            return bands;
        }

        /// <summary>
        /// y 줄에서 그 줄 가장 큰 알파의 절반 이상인 픽셀의 가장 왼쪽·오른쪽 x(없으면 -1) — 반사는 줄마다 같은 비율로 흐려지므로
        /// 흐린 정도와 상관없이 글자 줄과 모양을 견줄 수 있다.
        /// </summary>
        private static (int Left, int Right) RowSpan(Png png, int y)
        {
            var max = Enumerable.Range(0, png.Width).Max(x => png.Alpha(x, y));
            if (max == 0)
                return (-1, -1);
            var inside = Enumerable.Range(0, png.Width).Where(x => png.Alpha(x, y) * 2 >= max).ToList();
            return (inside.First(), inside.Last());
        }

        [UiFact]
        public async Task Rasterizer_Reflection_GapRowsThenMirroredShape()
        {
            // 흰 "▲"(아래로 갈수록 넓다) + 반사(크기 100%, 간격 20, 불투명도 100%):
            // 글자 아래 간격만큼 알파 0인 줄이 있고, 그 아래에 위아래가 뒤집힌 같은 모양(반사 첫 줄 ≈ 글자 마지막 줄)
            var rasterizer = NewRasterizer();
            var png = await Render(rasterizer, Request(rasterizer, "▲", 1920, 1080, 200, WhiteFill + "," + Reflection(100, 20)), "reflection-mirror");
            var bands = InkBands(png);
            _out.WriteLine(png.Width + "×" + png.Height + " · 토막 " + string.Join(" ", bands.Select(b => b.Top + "~" + b.Bottom)));
            Assert.Equal(2, bands.Count);
            var (glyph, mirror) = (bands[0], bands[1]);
            // 간격 20px: 글자 아래 끝이 픽셀 경계에 있지 않으면 걸친 줄을 빼고 19줄
            Assert.InRange(mirror.Top - glyph.Bottom - 1, 19, 20);
            var height = glyph.Bottom - glyph.Top + 1;
            Assert.InRange(mirror.Bottom - mirror.Top + 1, height - 2, height + 1);
            foreach (var k in new[] { 0, 1, 2, height / 4, height / 2, height * 3 / 4 })
            {
                var g = RowSpan(png, glyph.Bottom - k);
                var m = RowSpan(png, mirror.Top + k);
                _out.WriteLine("아래에서 " + k + "줄: 글자 " + g + " · 반사 위에서 " + k + "줄 " + m);
                Assert.InRange(m.Left - g.Left, -2, 2);
                Assert.InRange(m.Right - g.Right, -2, 2);
            }
            // 뒤집혔다: 반사 위쪽은 글자 아래쪽처럼 넓고, 글자 위쪽은 좁다
            var top = RowSpan(png, glyph.Top + 2);
            var first = RowSpan(png, mirror.Top + 2);
            Assert.True(first.Right - first.Left > 5 * (top.Right - top.Left + 1), "반사 위쪽 폭 " + (first.Right - first.Left) + " · 글자 위쪽 폭 " + (top.Right - top.Left));
            Assert.Equal(0, png.BorderInk());
        }

        [UiFact]
        public async Task Rasterizer_Reflection_FadesDownward_SizeAndOpacity_ShadowNotMirrored()
        {
            var rasterizer = NewRasterizer();
            // "■" + 반사(100%, 간격 10): 반사를 위에서 아래로 여덟 띠로 나누면 칠한 픽셀의 평균 알파가 띠마다 준다
            var square = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200, WhiteFill + "," + Reflection(100, 10)), "reflection-fade");
            var bands = InkBands(square);
            Assert.Equal(2, bands.Count);
            var mirror = bands[1];
            var band = (mirror.Bottom - mirror.Top + 1) / 8;
            var means = new double[8];
            for (var b = 0; b < 8; b++)
            {
                long sum = 0;
                var count = 0;
                for (var y = mirror.Top + b * band; y < mirror.Top + (b + 1) * band; y++)
                {
                    for (var x = 0; x < square.Width; x++)
                    {
                        var alpha = square.Alpha(x, y);
                        if (alpha == 0)
                            continue;
                        sum += alpha;
                        count++;
                    }
                }
                means[b] = count == 0 ? 0 : (double)sum / count;
            }
            _out.WriteLine("띠별 평균 알파 " + string.Join(" ", means.Select(m => m.ToString("F1"))));
            for (var b = 1; b < 8; b++)
                Assert.True(means[b] < means[b - 1] - 10, "띠 " + b + "가 위 띠보다 옅어야 한다");
            Assert.True(means[0] > 200 && means[7] < 40, "위쪽은 진하고 아래쪽은 거의 투명");

            // 불투명도 50%: 반사의 가장 진한 곳이 절반쯤, 글자는 그대로 불투명
            var half = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200, WhiteFill + "," + Reflection(100, 10, 0.5)), "reflection-opacity-50");
            var halfBands = InkBands(half);
            var mirrorMax = Enumerable.Range(halfBands[1].Top, halfBands[1].Bottom - halfBands[1].Top + 1).Max(y => Enumerable.Range(0, half.Width).Max(x => half.Alpha(x, y)));
            _out.WriteLine("불투명도 50% 반사 최대 알파 " + mirrorMax + " · 글자 " + half.MaxAlpha(false));
            Assert.InRange(mirrorMax, 120, 136);
            Assert.Equal(255, half.MaxAlpha(false));

            // 크기 30%: 반사 높이가 글자 높이의 30%
            var small = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200, WhiteFill + "," + Reflection(30, 10)), "reflection-size-30");
            var smallBands = InkBands(small);
            var glyphHeight = smallBands[0].Bottom - smallBands[0].Top + 1;
            var mirrorHeight = smallBands[1].Bottom - smallBands[1].Top + 1;
            _out.WriteLine("크기 30%: 글자 " + glyphHeight + "줄 · 반사 " + mirrorHeight + "줄");
            Assert.InRange((double)mirrorHeight, glyphHeight * 0.3 - 2, glyphHeight * 0.3 + 2);

            // 그림자는 비추지 않는다: 오른쪽으로 멀리(150px) 둔 빨간 그림자는 글자 줄에만 있고 반사 줄에는 없다(흰 반사만)
            var shadowed = await Render(rasterizer, Request(rasterizer, "I", 1920, 1080, 200, WhiteFill + "," + Shadow(150, 0, 0, 1, "#FF0000") + "," + Reflection(100, 10)), "reflection-shadow");
            var shadowBands = InkBands(shadowed);
            Assert.Equal(2, shadowBands.Count);
            int Count((int Top, int Bottom) rows, Func<(int R, int G, int B, int A), bool> match)
            {
                var count = 0;
                for (var y = rows.Top; y <= rows.Bottom; y++)
                {
                    for (var x = 0; x < shadowed.Width; x++)
                        count += match(shadowed.Pixel(x, y)) ? 1 : 0;
                }
                return count;
            }
            bool Red((int R, int G, int B, int A) p) => p.A > 0 && p.R > 100 && p.G < 60;
            bool White((int R, int G, int B, int A) p) => p.A > 0 && p.R > 200 && p.G > 200 && p.B > 200;
            _out.WriteLine("빨강 글자 줄 " + Count(shadowBands[0], Red) + " · 반사 줄 " + Count(shadowBands[1], Red) + " · 흰 반사 " + Count(shadowBands[1], White));
            Assert.True(Count(shadowBands[0], Red) > 500, "그림자가 있어야 한다");
            Assert.Equal(0, Count(shadowBands[1], Red));
            Assert.True(Count(shadowBands[1], White) > 500, "글자 반사가 있어야 한다");
        }

        [UiFact]
        public async Task Rasterizer_ReflectionOff_ByteIdentical_MaxStaysInside()
        {
            var rasterizer = NewRasterizer();
            const string text = "가나다 ABC\n반사";
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4)), "reflection-base");
            var expected = File.ReadAllBytes(Path.Combine(Folder(), "reflection-base.png"));
            // 꺼져 있으면 크기·간격·불투명도를 바꿔도 반사 없이 그린 것과 바이트까지 같다
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4) + "," + Reflection(100, 100, 1, false)), "reflection-off");
            Assert.True(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "reflection-off.png"))), "꺼진 반사가 그림을 바꿨다");
            // 켜면 다르다(비교가 헛돌지 않는지)
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4) + "," + Reflection(50, 4, 0.4)), "reflection-on");
            Assert.False(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "reflection-on.png"))));

            // 가장 큰 반사(100%, 간격 100) + 가장 굵은 윤곽선(50)도 그림 가장자리 1px 줄에 닿지 않고 캔버스 이하(캔버스가 충분히 클 때) —
            // 내림 글자·원 휘기·좁은 행간의 여러 줄·효과 배율 2도
            foreach (var (name, line, fontSize, style, scale) in new[]
            {
                ("miter", "WAV가", 120.0, WhiteOutline(50, "Miter"), 1.0),
                ("round", "WAV가", 120.0, WhiteOutline(50), 1.0),
                ("descender", "gjpqy 가", 120.0, WhiteOutline(50, "Miter"), 1.0),
                ("circle", "휘기 ABC", 120.0, WhiteOutline(20) + "," + Warp("Circle", 100), 1.0),
                ("lines", "가나다\n둘째 줄\n셋", 120.0, WhiteOutline(50, "Miter") + ",\"LineSpacing\":0.5", 1.0),
                ("scale-2", "WAV가", 240.0, WhiteOutline(50, "Miter"), 2.0)
            })
            {
                var max = await Render(rasterizer, Request(rasterizer, line, 3840, 2160, fontSize, WhiteFill + "," + style + "," + Reflection(100, 100), scale), "reflection-max-" + name);
                var bands = InkBands(max);
                _out.WriteLine(name + " " + max.Width + "×" + max.Height + " · 토막 " + string.Join(" ", bands.Select(b => b.Top + "~" + b.Bottom)));
                Assert.True(max.Width <= 3840 && max.Height <= 2160, name);
                Assert.Equal(2, bands.Count);
                Assert.Equal(0, max.BorderInk());
            }
        }

        [UiFact]
        public async Task Rasterizer_GlowIsMirroredInTheReflection_ShadowIsNot()
        {
            // 반사는 앞쪽 층(광선·본문)을 비춘다: 글자 옆으로 번진 노란 광선이 반사 자리에도 보인다(광선·반사는 따로 만들어 병합 — 둘을 같이 켠 시험)
            var rasterizer = NewRasterizer();
            const int gap = 40;
            var reference = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200, WhiteFill + "," + Glow(20)), "glow-only");
            // PNG 가운데 = 배치 상자 가운데라 두 그림에서 글자 아래 끝은 가운데로부터 같은 거리
            var lastWhite = Enumerable.Range(0, reference.Height).Last(y => Enumerable.Range(0, reference.Width).Any(x =>
            {
                var p = reference.Pixel(x, y);
                return p.A == 255 && p.R > 230 && p.G > 230 && p.B > 230;
            }));
            var below = lastWhite - reference.Height / 2;
            int YellowRows(Png png)
            {
                var top = png.Height / 2 + below + gap + 4;
                var count = 0;
                for (var y = top; y < Math.Min(png.Height, top + 60); y++)
                {
                    for (var x = 0; x < png.Width; x++)
                        count += Yellowish(png.Pixel(x, y)) ? 1 : 0;
                }
                return count;
            }
            var both = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200, WhiteFill + "," + Glow(20) + "," + Reflection(100, gap)), "glow-reflection");
            var plain = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200, WhiteFill + "," + Reflection(100, gap)), "reflection-only");
            var shadowed = await Render(rasterizer, Request(rasterizer, "■", 1920, 1080, 200,
                WhiteFill + ",\"Shadow\":{\"Enabled\":true,\"Color\":\"#FFFF00\",\"Distance\":0,\"Blur\":20,\"Opacity\":1}," + Reflection(100, gap)), "shadow-reflection");
            _out.WriteLine("반사 자리 노랑 픽셀: 광선 " + YellowRows(both) + " · 광선 없음 " + YellowRows(plain) + " · 노란 그림자 " + YellowRows(shadowed));
            Assert.True(YellowRows(both) > 200, "광선이 반사에 비친다");
            Assert.Equal(0, YellowRows(plain));
            Assert.Equal(0, YellowRows(shadowed));
            Assert.Equal(0, both.BorderInk());
        }

        [UiFact]
        public async Task Rasterizer_FromWorkerThread_TransparentCorners_FillColorInMiddle()
        {
            var rasterizer = NewRasterizer();
            var png = await Render(rasterizer, Request(rasterizer, "가나다 ABC"), "basic");
            _out.WriteLine("크기 " + png.Width + "×" + png.Height + " · DPI " + png.Dpi + " · 칠한 픽셀 " + png.PaintedCount());
            Assert.Equal(96, png.Dpi, 1);
            Assert.Equal(0, png.Width % 2);
            Assert.Equal(0, png.Height % 2);
            Assert.Equal(0, png.Alpha(0, 0));
            Assert.Equal(0, png.Alpha(png.Width - 1, 0));
            Assert.Equal(0, png.Alpha(0, png.Height - 1));
            Assert.Equal(0, png.Alpha(png.Width - 1, png.Height - 1));
            // 불투명한 픽셀은 채움 색이고, 가운데 띠에 있다
            var solid = 0;
            for (var y = png.Height / 4; y < png.Height * 3 / 4; y++)
            {
                for (var x = 0; x < png.Width; x++)
                {
                    var i = (y * png.Width + x) * 4;
                    if (png.Bgra[i + 3] == 255 && png.Bgra[i + 2] == 0xFF && png.Bgra[i + 1] == 0x33 && png.Bgra[i] == 0x66)
                        solid++;
                }
            }
            Assert.True(solid > 200, "채움 색 픽셀 " + solid);
        }

        [UiFact]
        public async Task Rasterizer_BoldPaintsMore_LetterSpacingWidens()
        {
            var rasterizer = NewRasterizer();
            var normal = await Render(rasterizer, Request(rasterizer, "가나다 ABC"), "normal");
            var bold = await Render(rasterizer, Request(rasterizer, "가나다 ABC", style: "\"Bold\":true"), "bold");
            var spaced = await Render(rasterizer, Request(rasterizer, "가나다 ABC", style: "\"LetterSpacing\":500"), "spaced");
            _out.WriteLine("칠한 픽셀 보통 " + normal.PaintedCount() + " · 굵게 " + bold.PaintedCount() + " · 폭 보통 " + normal.Width + " · 자간 500 " + spaced.Width);
            Assert.True(bold.PaintedCount() > normal.PaintedCount() * 1.1);
            // 글자 7개(빈칸 포함) 사이 6칸 × 0.5em(40px)
            Assert.InRange(spaced.Width - normal.Width, 6 * 40 - 8, 6 * 40 + 8);
        }

        [UiFact]
        public async Task Rasterizer_TwoLines_LeftAndRightAlign()
        {
            var rasterizer = NewRasterizer();
            // 두 줄 모두 '가'로 시작하고 '바'로 끝나서 글자 옆 여백이 같다
            const string text = "가나다라바\n가바";
            var left = await Render(rasterizer, Request(rasterizer, text, style: "\"Align\":\"Left\""), "align-left");
            var right = await Render(rasterizer, Request(rasterizer, text, style: "\"Align\":\"Right\""), "align-right");
            var center = await Render(rasterizer, Request(rasterizer, text, style: "\"Align\":\"Center\""), "align-center");
            var l1 = left.Extent(0, left.Height / 2);
            var l2 = left.Extent(left.Height / 2, left.Height);
            var r1 = right.Extent(0, right.Height / 2);
            var r2 = right.Extent(right.Height / 2, right.Height);
            var c1 = center.Extent(0, center.Height / 2);
            var c2 = center.Extent(center.Height / 2, center.Height);
            _out.WriteLine("왼쪽 " + l1 + " / " + l2 + " · 오른쪽 " + r1 + " / " + r2 + " · 가운데 " + c1 + " / " + c2);
            Assert.InRange(l2.Left - l1.Left, -2, 2);
            Assert.True(l2.Right < l1.Right - 100, "왼쪽 정렬이면 짧은 줄의 오른쪽 끝이 안쪽");
            Assert.InRange(r2.Right - r1.Right, -2, 2);
            Assert.True(r2.Left > r1.Left + 100, "오른쪽 정렬이면 짧은 줄의 왼쪽 끝이 안쪽");
            Assert.InRange((c2.Left + c2.Right) - (c1.Left + c1.Right), -4, 4);
        }

        [UiFact]
        public async Task Rasterizer_MissingFont_StillDrawsHangul()
        {
            var rasterizer = NewRasterizer();
            var installed = (bool)rasterizer.GetType().GetMethod("IsInstalled").Invoke(null, new object[] { "없는글꼴XYZ" });
            Assert.False(installed);
            var png = await Render(rasterizer, Request(rasterizer, "가나다", style: "\"FontFamily\":\"없는글꼴XYZ\""), "missing-font");
            Assert.True(png.PaintedCount() > 500, "칠한 픽셀 " + png.PaintedCount());
        }

        [UiFact]
        public async Task Rasterizer_4KCanvas_FastAndNotLargerThanCanvas()
        {
            var rasterizer = NewRasterizer();
            // 그리기 스레드·글꼴 준비는 처음 한 번 — 재지 않는다
            await Render(rasterizer, Request(rasterizer, "준비"), "warm-up");
            var request = Request(rasterizer, "가나다 ABC\n두 번째 줄", 3840, 2160, 400, "\"Shadow\":{\"Enabled\":true,\"Distance\":40,\"Blur\":30}");
            var watch = Stopwatch.StartNew();
            var png = await Render(rasterizer, request, "uhd");
            _out.WriteLine("4K 요청 " + watch.ElapsedMilliseconds + "ms · " + png.Width + "×" + png.Height);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "걸린 시간 " + watch.Elapsed);
            Assert.True(png.Width <= 3840 && png.Height <= 2160);
            // 아주 큰 글자도 캔버스로 잘린다
            var huge = await Render(rasterizer, Request(rasterizer, "가나다라마바사", 640, 360, 300), "huge");
            Assert.Equal(640, huge.Width);
            Assert.Equal(360, huge.Height);
        }

        private static string Warp(string kind, double amount)
        {
            return "\"Warp\":{\"Kind\":\"" + kind + "\",\"Amount\":" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>왼쪽 끝·가운데·오른쪽 끝 열(칠한 폭의 5%·50%·95%, 폭의 2% 띠)에서 칠한 픽셀의 무게중심 y.</summary>
        private static (double Left, double Middle, double Right) ColumnCenters(Png png)
        {
            var (left, right) = png.Extent(0, png.Height);
            var span = right - left;
            var band = Math.Max(2, span / 50);
            double At(double fraction)
            {
                var x = left + (int)(span * fraction) - band / 2;
                return png.CenterY(x, x + band);
            }
            return (At(0.05), At(0.5), At(0.95));
        }

        [UiFact]
        public async Task Rasterizer_WarpArchUp_RaisesMiddleColumn_OutlineBendsToo()
        {
            var rasterizer = NewRasterizer();
            // 휘기 없음: 가로줄 다섯 개의 열 무게중심이 모두 같은 높이
            var flat = await Render(rasterizer, Request(rasterizer, "ㅡㅡㅡㅡㅡ"), "warp-flat");
            var level = ColumnCenters(flat);
            _out.WriteLine("없음 왼쪽 " + level.Left + " · 가운데 " + level.Middle + " · 오른쪽 " + level.Right);
            Assert.InRange(level.Middle - level.Left, -2, 2);
            Assert.InRange(level.Middle - level.Right, -2, 2);

            // 아치 위(80): 가운데 열이 양 끝 열보다 위(화면 y가 작다)
            var arch = await Render(rasterizer, Request(rasterizer, "ㅡㅡㅡㅡㅡ", style: Warp("ArchUp", 80)), "warp-arch-up");
            var bent = ColumnCenters(arch);
            _out.WriteLine("아치 위 80 왼쪽 " + bent.Left + " · 가운데 " + bent.Middle + " · 오른쪽 " + bent.Right + " · " + arch.Width + "×" + arch.Height);
            Assert.True(bent.Middle < bent.Left - 10, "가운데가 왼쪽 끝보다 위");
            Assert.True(bent.Middle < bent.Right - 10, "가운데가 오른쪽 끝보다 위");
            Assert.Equal(0, arch.BorderInk());

            // 윤곽선도 휘어진 윤곽에 그린다(채우기 없음 + 윤곽선 = 속 빈 글자도 아치)
            var outline = await Render(rasterizer, Request(rasterizer, "ㅡㅡㅡㅡㅡ",
                style: "\"Fill\":{\"Kind\":\"None\",\"Colors\":[\"#FFFFFF\"]}," + Outline(6) + "," + Warp("ArchUp", 80)), "warp-arch-up-outline");
            var ring = ColumnCenters(outline);
            _out.WriteLine("윤곽선만 왼쪽 " + ring.Left + " · 가운데 " + ring.Middle + " · 오른쪽 " + ring.Right);
            Assert.True(outline.PaintedCount() > 500);
            Assert.True(ring.Middle < ring.Left - 10 && ring.Middle < ring.Right - 10, "윤곽선도 가운데가 위");
            Assert.Equal(0, outline.BorderInk());
        }

        [UiFact]
        public async Task Rasterizer_WarpAmountZero_ByteIdenticalToNone()
        {
            var rasterizer = NewRasterizer();
            const string text = "가나다 ABC\n휘기";
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4)), "warp-base");
            var expected = File.ReadAllBytes(Path.Combine(Folder(), "warp-base.png"));
            // 정도 0이면 종류와 상관없이, 없음이면 정도와 상관없이 휘기 없이 그린 것과 바이트까지 같다
            foreach (var (kind, amount) in new[] { ("Circle", 0.0), ("ArchUp", 0.0), ("Stairs", 0.0), ("None", 80.0), ("None", -100.0) })
            {
                var name = "warp-" + kind + "-" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Render(rasterizer, Request(rasterizer, text, style: Outline(4) + "," + Warp(kind, amount)), name);
                Assert.True(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), name + ".png"))), kind + " " + amount + "가 휘기 없음과 다르다");
            }
            // 휘기가 있으면 다르다(비교가 헛돌지 않는지)
            await Render(rasterizer, Request(rasterizer, text, style: Outline(4) + "," + Warp("Wave", 40)), "warp-wave-40");
            Assert.False(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "warp-wave-40.png"))));
        }

        [UiFact]
        public async Task Rasterizer_WarpCircle100_FitsCanvas_EveryKindKeepsInkInside()
        {
            var rasterizer = NewRasterizer();
            // 원(100): 기준선을 한 바퀴 감아도 PNG는 캔버스 이하이고 잘리지 않는다(가장자리 1px 줄 알파 0)
            var circle = await Render(rasterizer, Request(rasterizer, "휘기 ABC 가나다", style: Warp("Circle", 100)), "warp-circle-100");
            _out.WriteLine("원 100 " + circle.Width + "×" + circle.Height + " · 칠한 픽셀 " + circle.PaintedCount());
            Assert.True(circle.Width <= 1280 && circle.Height <= 720);
            Assert.True(circle.PaintedCount() > 1000);
            Assert.Equal(0, circle.BorderInk());

            // 모든 종류 ±100 + 각지게 윤곽선 10px도 가장자리에 닿지 않는다(PNG 크기는 휘어진 윤곽의 경계로)
            foreach (var kind in new[] { "ArchUp", "ArchDown", "Circle", "Wave", "Bulge", "Pinch", "Slant", "Stairs" })
            {
                foreach (var amount in new[] { 100.0, -100.0 })
                {
                    var png = await Render(rasterizer, Request(rasterizer, "휘기 ABC 가나다", style: Outline(10, "Miter") + "," + Warp(kind, amount)), "warp-all-" + kind + amount);
                    _out.WriteLine(kind + " " + amount + ": " + png.Width + "×" + png.Height + " · 칠한 픽셀 " + png.PaintedCount());
                    Assert.True(png.Width <= 1280 && png.Height <= 720, kind + " " + amount);
                    Assert.True(png.PaintedCount() > 1000, kind + " " + amount);
                    Assert.Equal(0, png.BorderInk());
                }
            }
        }

        [UiFact]
        public async Task Rasterizer_Warp_KeepsHolesInLetters()
        {
            // 채우기 규칙을 지키면 'O'·'ㅇ' 속 구멍이 그대로 — 가운데 줄에서 글자마다 칠한 토막이 둘(구멍이 메워지면 하나)
            var rasterizer = NewRasterizer();
            foreach (var style in new[] { (string)null, Warp("Bulge", 60), Warp("Slant", 40), Warp("Pinch", 60) })
            {
                var png = await Render(rasterizer, Request(rasterizer, "Oㅇ", fontSize: 200, style: style), "warp-hole-" + (style == null ? "none" : style.Split('"')[5]));
                var (top, bottom) = png.Rows();
                var runs = png.Runs((top + bottom) / 2);
                _out.WriteLine((style ?? "없음") + ": 가운데 줄 토막 " + runs);
                Assert.True(runs >= 4, (style ?? "없음") + " 토막 " + runs);
            }
        }

        /// <summary>플러그인의 스타일 모음(이름, 스타일 객체) — 부를 때마다 새 사본.</summary>
        private static System.Collections.Generic.List<(string Name, object Style)> Presets()
        {
            var all = (System.Collections.IEnumerable)PluginUnderTest.Load(UiThread.Instance).Assembly
                .GetType("VideoEditor.Core.TextStylePresets", true).GetProperty("All").GetValue(null);
            return all.Cast<object>()
                .Select(p => ((string)p.GetType().GetField("Item1").GetValue(p), p.GetType().GetField("Item2").GetValue(p)))
                .ToList();
        }

        /// <summary>스타일 객체의 JSON(값 비교용, 없으면 "null").</summary>
        private static string StyleJson(object style)
        {
            if (style == null)
                return "null";
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            return JsonSerializer.Serialize(style, style.GetType(), options);
        }

        /// <summary>스타일 객체의 묶음(Glow·Reflection·Warp 등) 속성 하나를 바꾼다 — enum 속성은 이름(string)으로 준다.</summary>
        private static void SetPart(object style, string part, string property, object value)
        {
            var target = Get(style, part);
            var info = target.GetType().GetProperty(property);
            info.SetValue(target, value is string name && info.PropertyType.IsEnum ? Enum.Parse(info.PropertyType, name) : value);
        }

        /// <summary>칠한 폭의 fraction 위치 열(폭의 2% 띠)에서 칠한 픽셀의 무게중심 y.</summary>
        private static double CenterAt(Png png, double fraction)
        {
            var (left, right) = png.Extent(0, png.Height);
            var span = right - left;
            var band = Math.Max(2, span / 50);
            var x = left + (int)(span * fraction) - band / 2;
            return png.CenterY(x, x + band);
        }

        [UiFact]
        public async Task Rasterizer_StylePresets_EachDrawsItsThumbnail()
        {
            // 갤러리와 같은 요청(EditorServices.StylePreviewRequest: "가나Ab" 48px)으로 16개를 그린다
            var rasterizer = NewRasterizer();
            var services = rasterizer.GetType().Assembly.GetType("VideoEditor.Ui.EditorServices", true);
            var presets = Presets();
            Assert.Equal(16, presets.Count);
            var drawn = new System.Collections.Generic.List<Png>();
            for (var i = 0; i < presets.Count; i++)
            {
                var request = services.GetMethod("StylePreviewRequest").Invoke(null, new[] { presets[i].Style });
                Assert.Equal("가나Ab", (string)Get(request, "Text"));
                var png = await Render(rasterizer, request, "preset-" + i);
                _out.WriteLine(presets[i].Name + " " + png.Width + "×" + png.Height + " · 칠한 픽셀 " + png.PaintedCount());
                Assert.True(png.PaintedCount() > 300, presets[i].Name + " 칠한 픽셀 " + png.PaintedCount());
                // 캔버스보다 작다 = 효과까지 잘리지 않고 다 들어갔다
                Assert.True(png.Width < (int)Get(request, "CanvasWidth") && png.Height < (int)Get(request, "CanvasHeight"), presets[i].Name + " 잘림");
                drawn.Add(png);
            }
            // 모두 다른 그림이다
            for (var a = 0; a < drawn.Count; a++)
            {
                for (var b = a + 1; b < drawn.Count; b++)
                    Assert.True(drawn[a].Width != drawn[b].Width || drawn[a].Height != drawn[b].Height || !drawn[a].Bgra.AsSpan().SequenceEqual(drawn[b].Bgra), presets[a].Name + " = " + presets[b].Name);
            }

            // 노란 자막은 노란 글자, 빨간 강조는 빨간 글자
            int Count(Png png, Func<(int R, int G, int B, int A), bool> match)
            {
                var count = 0;
                for (var y = 0; y < png.Height; y++)
                {
                    for (var x = 0; x < png.Width; x++)
                        count += match(png.Pixel(x, y)) ? 1 : 0;
                }
                return count;
            }
            var yellow = drawn[presets.FindIndex(p => p.Name == "노란 자막")];
            var red = drawn[presets.FindIndex(p => p.Name == "빨간 강조")];
            Assert.True(Count(yellow, p => p.A == 255 && p.R > 220 && p.G > 180 && p.B < 60) > 100, "노란 픽셀");
            Assert.True(Count(yellow, p => p.A == 255 && p.R < 40 && p.G < 40 && p.B < 40) > 100, "검정 윤곽선 픽셀");
            Assert.True(Count(red, p => p.A == 255 && p.R > 200 && p.G < 100 && p.B < 100) > 100, "빨간 픽셀");
            // 속 빈 글자: 칠한 픽셀은 흰 윤곽선뿐이다
            var hollow = drawn[presets.FindIndex(p => p.Name == "속 빈 글자")];
            Assert.Equal(0, Count(hollow, p => p.A > 128 && (p.R < 200 || p.G < 200 || p.B < 200)));

            // 2단계의 8개(#22): 썸네일에 이름이 말하는 효과가 보인다 — 같은 스타일에서 그 효과만 끄고 그린 것과 견준다(PNG 가운데 = 배치 상자 가운데)
            Png Drawn(string name) => drawn[presets.FindIndex(p => p.Name == name)];
            async Task<Png> Without(string name, string part, string property, object value)
            {
                var index = presets.FindIndex(p => p.Name == name);
                var style = Presets()[index].Style;
                SetPart(style, part, property, value);
                return await Render(rasterizer, services.GetMethod("StylePreviewRequest").Invoke(null, new[] { style }), "preset-" + index + "-without-" + part);
            }

            // 네온: 광선 색(#0EA5E9 — 윤곽선 #BAE6FD보다 빨강이 훨씬 적다) 픽셀이 광선을 끈 그림에서 비어 있는 곳(글자 윤곽선 밖)에 있다
            var neon = Drawn("네온");
            var unlit = await Without("네온", "Glow", "Enabled", false);
            var glowOutside = 0;
            for (var y = 0; y < neon.Height; y++)
            {
                for (var x = 0; x < neon.Width; x++)
                {
                    var p = neon.Pixel(x, y);
                    if (p.A < 64 || p.R > 100 || p.B < 150)
                        continue;
                    var ux = x - (neon.Width - unlit.Width) / 2;
                    var uy = y - (neon.Height - unlit.Height) / 2;
                    if (ux < 0 || uy < 0 || ux >= unlit.Width || uy >= unlit.Height || unlit.Alpha(ux, uy) == 0)
                        glowOutside++;
                }
            }
            _out.WriteLine("네온: 윤곽선 밖 광선 색 픽셀 " + glowOutside + " · 광선 끔 " + Count(unlit, p => p.A >= 64 && p.R <= 100 && p.B >= 150));
            Assert.True(glowOutside > 300, "네온 광선 " + glowOutside);
            Assert.Equal(0, Count(unlit, p => p.A >= 64 && p.R <= 100 && p.B >= 150));

            // 유리 반사·금빛 반사: 반사를 끈 그림의 글자 아래 끝보다 아래(간격·가장자리 줄은 건너뜀)에 칠한 줄이 이어진다
            foreach (var name in new[] { "유리 반사", "금빛 반사" })
            {
                var mirrored = Drawn(name);
                var plain = await Without(name, "Reflection", "Enabled", false);
                var start = mirrored.Height / 2 + (plain.Rows().Bottom - plain.Height / 2) + 4;
                var below = 0;
                for (var y = start; y < mirrored.Height; y++)
                {
                    for (var x = 0; x < mirrored.Width; x++)
                        below += mirrored.Alpha(x, y) > 0 ? 1 : 0;
                }
                var last = InkBands(mirrored).Last().Bottom;
                _out.WriteLine(name + ": 글자 아래 칠한 픽셀 " + below + " · " + (last - start) + "줄 더");
                Assert.True(below > 300, name + " 반사 픽셀 " + below);
                Assert.True(last - start >= 8, name + " 반사 높이 " + (last - start));
            }

            // 아치·원·물결·계단: 휘기를 끄고 그린 것과 견준 열 무게중심 y의 차이 — 이 차이를 열끼리 견주므로 글자 모양과 PNG 높이 차이는 빠진다(화면 y는 아래가 +)
            async Task<double[]> Shift(string name, params double[] fractions)
            {
                var bent = Drawn(name);
                var flat = await Without(name, "Warp", "Kind", "None");
                var shift = fractions.Select(f => CenterAt(bent, f) - CenterAt(flat, f)).ToArray();
                _out.WriteLine(name + ": 열 " + string.Join("/", fractions) + " 높이 차이 " + string.Join(", ", shift.Select(s => s.ToString("F1"))));
                return shift;
            }
            var arch = await Shift("아치 제목", 0.05, 0.5, 0.95);
            Assert.True(arch[1] < arch[0] - 6 && arch[1] < arch[2] - 6, "아치: 가운데가 양 끝보다 올라가야 한다");
            // 원은 양 끝 글자가 기울어 맨 끝 열에는 기운 글자의 위아래가 함께 잡힌다 — 조금 안쪽 열로 본다
            var circle = await Shift("원형 배지", 0.15, 0.5, 0.85);
            Assert.True(circle[1] < circle[0] - 6 && circle[1] < circle[2] - 6, "원: 가운데가 양 끝보다 올라가야 한다");
            var wave = await Shift("물결 글자", 0.25, 0.75);
            Assert.True(wave[0] > wave[1] + 6, "물결: 왼쪽 ¼은 내려가고 오른쪽 ¼은 올라가야 한다");
            var stairs = await Shift("계단 글자", 0.05, 0.5, 0.95);
            Assert.True(stairs[0] > stairs[1] + 6 && stairs[1] > stairs[2] + 6, "계단: 오른쪽으로 갈수록 올라가야 한다");

            // 무지개 그라데이션: 왼쪽 빨강부터 오른쪽 파랑까지(0° 선형 4색)
            var rainbow = Drawn("무지개 그라데이션");
            Assert.True(Count(rainbow, p => p.A == 255 && p.R > 180 && p.G < 120 && p.B < 120) > 30, "빨간 픽셀");
            Assert.True(Count(rainbow, p => p.A == 255 && p.B > 180 && p.R < 120) > 30, "파란 픽셀");
            ((IDisposable)rasterizer).Dispose();
        }

        // ---------- 글자 상자(줄 나눔·세로 정렬) ----------

        /// <summary>글자 상자를 정한 요청: <see cref="Request"/>에 상자 너비(WrapWidth)·높이(BoxHeight)·세로 정렬(VerticalAlign — 이름)을 더한다.</summary>
        private static object BoxRequest(object rasterizer, string text, double wrapWidth, double boxHeight = 0, string align = null, double fontSize = 80, string style = null,
            int canvasWidth = 1920, int canvasHeight = 1080)
        {
            var request = Request(rasterizer, text, canvasWidth, canvasHeight, fontSize, style);
            var type = request.GetType();
            type.GetProperty("WrapWidth").SetValue(request, wrapWidth);
            type.GetProperty("BoxHeight").SetValue(request, boxHeight);
            if (align != null)
                type.GetProperty("VerticalAlign").SetValue(request, Enum.Parse(type.GetProperty("VerticalAlign").PropertyType, align));
            return request;
        }

        /// <summary>그리개의 재기(<c>MeasureBox</c>): 상자 너비·높이와 글 높이(fontSize와 같은 단위).</summary>
        private static (double Width, double Height, double TextHeight) MeasureBox(object rasterizer, string text, object style, double fontSize, double boxWidth, double boxHeight)
        {
            var result = rasterizer.GetType().GetMethod("MeasureBox").Invoke(null, new[] { text, style, fontSize, boxWidth, (object)boxHeight });
            var type = result.GetType();
            return ((double)type.GetField("Item1").GetValue(result), (double)type.GetField("Item2").GetValue(result), (double)type.GetField("Item3").GetValue(result));
        }

        [UiFact]
        public async Task Rasterizer_WrapWidth_WrapsAtSpacesInsideTheBox()
        {
            var rasterizer = NewRasterizer();
            const string text = "가나다라 마바사 아자차카 타파하 가나다라 마바사 아자차카 타파하";
            var single = await Render(rasterizer, Request(rasterizer, text, 1920, 1080), "box-none");
            var wrapped = await Render(rasterizer, BoxRequest(rasterizer, text, 600), "box-wrap-600");
            var singleBands = InkBands(single);
            var bands = InkBands(wrapped);
            _out.WriteLine("상자 없음 " + single.Width + "×" + single.Height + " · 토막 " + singleBands.Count + " / 너비 600 " + wrapped.Width + "×" + wrapped.Height + " · 토막 " + string.Join(" ", bands.Select(b => b.Top + "~" + b.Bottom)));
            Assert.Single(singleBands);
            // 여덟 낱말(한 줄에 둘)이 네 줄로, PNG 폭 = 상자 + 가장자리 여백(1px씩), 칠한 곳은 상자 안
            Assert.Equal(4, bands.Count);
            Assert.Equal(602, wrapped.Width);
            var (left, right) = wrapped.Extent(0, wrapped.Height);
            Assert.True(left >= 1 && right <= 600, "칠한 폭 " + left + "~" + right);
            Assert.Equal(0, wrapped.BorderInk());

            // 한국어는 빈칸에서 나눈다: "가나다라마바 사아"(480 + 빈칸 + 160 > 600)는 첫 줄이 "가나다라마바"뿐이다 —
            // 글자 단위로 나눴다면 첫 줄에 "사"까지 들어가(약 585px) 더 넓다
            var words = await Render(rasterizer, BoxRequest(rasterizer, "가나다라마바 사아", 600, style: "\"Align\":\"Left\""), "box-wrap-words");
            var wordBands = InkBands(words);
            Assert.Equal(2, wordBands.Count);
            var first = words.Extent(wordBands[0].Top, wordBands[0].Bottom + 1);
            var second = words.Extent(wordBands[1].Top, wordBands[1].Bottom + 1);
            _out.WriteLine("첫 줄 " + first + " · 둘째 줄 " + second);
            Assert.InRange(first.Right - first.Left, 440, 500);
            Assert.InRange(second.Right - second.Left, 130, 175);
            // 나눈 자리의 빈칸은 둘째 줄 앞에 남지 않는다(왼쪽 맞춤 — 두 줄의 왼쪽 끝이 같다)
            Assert.InRange(second.Left - first.Left, -3, 3);

            // 자간이 있어도(글자 단위 조각) 상자 안에서 나눈다 — 줄이 더 많아진다
            var spaced = await Render(rasterizer, BoxRequest(rasterizer, text, 600, style: "\"LetterSpacing\":200"), "box-wrap-spaced");
            var spacedBands = InkBands(spaced);
            var spacedExtent = spaced.Extent(0, spaced.Height);
            _out.WriteLine("자간 200 토막 " + spacedBands.Count + " · 칠한 폭 " + spacedExtent);
            Assert.True(spacedBands.Count > bands.Count);
            Assert.True(spacedExtent.Left >= 1 && spacedExtent.Right <= 600);
            Assert.Equal(0, spaced.BorderInk());
        }

        [UiFact]
        public async Task Rasterizer_WrapWidth_AlignsInsideTheBox()
        {
            var rasterizer = NewRasterizer();
            // 두 줄 "가나다라마바" / "사아" — 상자(600)는 두 줄보다 넓다
            const string text = "가나다라마바 사아";
            var pngs = new System.Collections.Generic.Dictionary<string, Png>();
            foreach (var align in new[] { "Left", "Center", "Right" })
                pngs[align] = await Render(rasterizer, BoxRequest(rasterizer, text, 600, style: "\"Align\":\"" + align + "\""), "box-align-" + align);
            (int Left, int Right) Line(Png png, int index)
            {
                var band = InkBands(png)[index];
                return png.Extent(band.Top, band.Bottom + 1);
            }
            foreach (var pair in pngs)
                _out.WriteLine(pair.Key + " " + pair.Value.Width + " · " + Line(pair.Value, 0) + " / " + Line(pair.Value, 1));
            // 상자가 PNG 폭(602)을 정한다 — 정렬은 그 상자 안에서
            Assert.All(pngs.Values, p => Assert.Equal(602, p.Width));
            var left = pngs["Left"];
            Assert.InRange(Line(left, 0).Left, 1, 15);
            Assert.InRange(Line(left, 1).Left - Line(left, 0).Left, -3, 3);
            var right = pngs["Right"];
            Assert.InRange(Line(right, 0).Right, 586, 600);
            Assert.InRange(Line(right, 1).Right - Line(right, 0).Right, -3, 3);
            var center = pngs["Center"];
            foreach (var index in new[] { 0, 1 })
            {
                var line = Line(center, index);
                Assert.InRange(line.Left + line.Right - center.Width, -6, 6);
            }
        }

        [UiFact]
        public async Task Rasterizer_ZeroBox_ByteIdenticalToNoBoxFields()
        {
            var rasterizer = NewRasterizer();
            const string text = "가나다 ABC\n두 번째 줄  ";
            foreach (var (name, style) in new[] { ("plain", (string)null), ("effects", Outline(6, "Miter") + "," + Shadow(10, 45, 6) + ",\"Align\":\"Right\""), ("warp", Warp("ArchUp", 60)), ("spaced", "\"LetterSpacing\":300") })
            {
                await Render(rasterizer, Request(rasterizer, text, style: style), "box-zero-base-" + name);
                await Render(rasterizer, BoxRequest(rasterizer, text, 0, 0, "Bottom", style: style, canvasWidth: 1280, canvasHeight: 720), "box-zero-" + name);
                var expected = File.ReadAllBytes(Path.Combine(Folder(), "box-zero-base-" + name + ".png"));
                Assert.True(expected.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "box-zero-" + name + ".png"))), name + ": 상자 0/0이 그림을 바꿨다");
            }
            // 상자를 정하면 다르다(비교가 헛돌지 않는지)
            await Render(rasterizer, BoxRequest(rasterizer, text, 300, canvasWidth: 1280, canvasHeight: 720), "box-zero-on");
            Assert.False(File.ReadAllBytes(Path.Combine(Folder(), "box-zero-base-plain.png")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder(), "box-zero-on.png"))));
        }

        [UiFact]
        public async Task Rasterizer_WordWiderThanTheBox_BreaksInside()
        {
            var rasterizer = NewRasterizer();
            // 빈칸 없는 14글자(약 1,120px)를 500px 상자에: 글자 단위로 들어가는 만큼씩(6·6·2) 세 줄, 모두 상자 안
            var png = await Render(rasterizer, BoxRequest(rasterizer, "가나다라마바사아자차카타파하", 500, style: "\"Align\":\"Left\""), "box-long-word");
            var bands = InkBands(png);
            _out.WriteLine(png.Width + "×" + png.Height + " · 토막 " + string.Join(" ", bands.Select(b => b.Top + "~" + b.Bottom + " " + png.Extent(b.Top, b.Bottom + 1))));
            Assert.Equal(3, bands.Count);
            Assert.Equal(502, png.Width);
            Assert.True(png.Extent(0, png.Height).Right <= 500);
            var last = png.Extent(bands[2].Top, bands[2].Bottom + 1);
            Assert.InRange(last.Right - last.Left, 130, 175);
            // 오른쪽→왼쪽 글자가 든 줄도 빈칸에서 나눠 그린다(줄마다 통째로)
            var rtl = await Render(rasterizer, BoxRequest(rasterizer, "שלום עולם שלום עולם مرحبا بالعالم", 400), "box-rtl");
            Assert.True(InkBands(rtl).Count >= 2);
            Assert.True(rtl.Extent(0, rtl.Height).Right <= 400);
            Assert.Equal(0, rtl.BorderInk());
        }

        [UiFact]
        public async Task Rasterizer_BoxHeight_PlacesTheTextTopMiddleBottom()
        {
            var rasterizer = NewRasterizer();
            // 흰 "■"(100px, 줄 높이 120) 하나를 높이 500 상자에: 위·가운데·아래가 (500 - 120) / 2 = 190px씩 내려간다
            var tops = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var align in new[] { "Top", "Middle", "Bottom" })
            {
                var png = await Render(rasterizer, BoxRequest(rasterizer, "■", 0, 500, align, 100, WhiteFill), "box-height-" + align);
                Assert.Equal(502, png.Height);
                tops[align] = Assert.Single(InkBands(png)).Top;
            }
            _out.WriteLine("위 " + tops["Top"] + " · 가운데 " + tops["Middle"] + " · 아래 " + tops["Bottom"]);
            Assert.InRange(tops["Middle"] - tops["Top"], 189, 191);
            Assert.InRange(tops["Bottom"] - tops["Middle"], 189, 191);
            // 위 정렬은 상자 위 끝(PNG 1px 여백) 가까이에서 시작한다
            Assert.InRange(tops["Top"], 1, 60);
        }

        [UiFact]
        public async Task Rasterizer_TextTallerThanTheBox_OverflowsFullyDrawn()
        {
            var rasterizer = NewRasterizer();
            // 다섯 줄(600px)을 높이 150 상자에: 가운데는 위아래로, 위는 아래로 넘쳐 모두 그린다(잘리지 않음)
            const string text = "가\n나\n다\n라\n마";
            var middle = await Render(rasterizer, BoxRequest(rasterizer, text, 0, 150, "Middle", 100, WhiteFill), "box-overflow-middle");
            var top = await Render(rasterizer, BoxRequest(rasterizer, text, 0, 150, "Top", 100, WhiteFill), "box-overflow-top");
            _out.WriteLine("가운데 " + middle.Width + "×" + middle.Height + " · 위 " + top.Width + "×" + top.Height);
            Assert.Equal(5, InkBands(middle).Count);
            Assert.Equal(5, InkBands(top).Count);
            Assert.Equal(0, middle.BorderInk());
            Assert.Equal(0, top.BorderInk());
            // PNG 가운데 = 상자 가운데: 위 정렬의 첫 줄은 상자 위 끝(가운데 - 75) 바로 아래, 아래로 넘친 만큼 PNG가 위아래로 커진다
            var first = InkBands(top)[0].Top;
            Assert.InRange(first - (top.Height / 2 - 75), 0, 30);
            Assert.True(top.Height > middle.Height);
        }

        [UiFact]
        public async Task Rasterizer_MeasureBox_EqualsTheBoxDrawUses()
        {
            var rasterizer = NewRasterizer();
            const string text = "가나다라 마바사 아자차카 타파하 가나다라 마바사 아자차카 타파하";
            var request = BoxRequest(rasterizer, text, 600);
            var style = Get(request, "Style");
            // 재기: 너비 = 상자, 높이 = 네 줄 × 80 × 1.2. 그린 PNG는 그 상자 + 가장자리 여백(1px씩, 짝수로 올림) — 칠한 곳이 상자 안이라 상자가 PNG 크기를 정한다
            var box = MeasureBox(rasterizer, text, style, 80, 600, 0);
            _out.WriteLine("잰 상자 " + box);
            Assert.Equal(600, box.Width, 6);
            Assert.Equal(4 * 80 * 1.2, box.Height, 6);
            Assert.Equal(box.Height, box.TextHeight, 6);
            var png = await Render(rasterizer, request, "box-measure");
            Assert.Equal(602, png.Width);
            Assert.Equal((int)Math.Ceiling(box.Height + 2) + (int)Math.Ceiling(box.Height + 2) % 2, png.Height);
            // 높이를 정하면 상자 높이 = 그 값, 글 높이는 그대로(넘침 안내에 씀)
            var tall = MeasureBox(rasterizer, text, style, 80, 600, 500);
            Assert.Equal((600.0, 500.0), (tall.Width, tall.Height));
            Assert.Equal(box.TextHeight, tall.TextHeight, 6);
            Assert.Equal(502, (await Render(rasterizer, BoxRequest(rasterizer, text, 600, 500), "box-measure-tall")).Height);
            var low = MeasureBox(rasterizer, text, style, 80, 600, 100);
            Assert.True(low.TextHeight > low.Height, "넘침");
            // 프로젝트 픽셀로 재도(글자 크기·상자 절반) 같은 줄 나눔 — 상자도 절반
            var half = MeasureBox(rasterizer, text, style, 40, 300, 0);
            Assert.Equal(box.Width / 2, half.Width, 6);
            Assert.Equal(box.Height / 2, half.Height, 6);
            // 상자가 없으면 글을 감싼 크기 — 한 줄
            var auto = MeasureBox(rasterizer, text, style, 80, 0, 0);
            Assert.True(auto.Width > 1500);
            Assert.Equal(80 * 1.2, auto.Height, 6);
        }

        // ---------- 화면 ----------

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

        private static async Task<object> AddStyledText(UiThread ui, Window window, string text)
        {
            ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
            await ui.WaitFor(() => Ui.ById<TextBox>(window, "NewTextBox"), Wait, "글자 탭");
            ui.Run(() =>
            {
                Ui.ById<TextBox>(window, "NewTextBox").Text = text;
                Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
            });
            await ui.WaitUntil(() => Clips(window).Any(n => n.Contains(text)), Wait, "글자 클립");
            await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "꾸미기 탭");
            ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
            await ui.WaitFor(() => Ui.ById<ComboBox>(window, "StyleFontBox"), Wait, "글꼴 칸");
            return ui.Run(() => Get(ProjectClips(window).First(c => (string)Get(c, "Text") == text), "Id"));
        }

        private static object StyleOf(Window window, object id)
        {
            var clip = ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
            return Get(clip, "Style");
        }

        [EditorFact]
        public async Task StyleTab_FontChangesPreview_AndSliderDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "가나다 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기(drawtext)");
                Assert.Null(ui.Run(() => StyleOf(window, id)));
                Assert.Equal("Collapsed", ui.Run(() => Ui.ById<TextBlock>(window, "StyleFontWarning").Visibility.ToString()));

                // 굵게 → 스타일이 생기고 그리개로 그린다
                var before = ui.Run(() => PreviewPixels(window));
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleBold")));
                await ui.WaitUntil(() => StyleOf(window, id) is object style && (bool)Get(style, "Bold"), Wait, "굵게");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "굵게 미리 보기");
                await Task.Delay(500);
                var bold = ui.Run(() => PreviewPixels(window));

                // 글꼴 바꾸기 → 미리 보기가 바뀐다
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "StyleFontBox"), "Arial"));
                await ui.WaitUntil(() => (string)Get(StyleOf(window, id), "FontFamily") == "Arial", Wait, "글꼴 Arial");
                await ui.WaitUntil(() => Differs(bold, PreviewPixels(window)), Wait, "글꼴 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-font"));

                // 자간 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleLetterSpacingSlider");
                    foreach (var value in new[] { 100.0, 200.0, 300.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(StyleOf(window, id), "LetterSpacing") - 300) < 1e-6, Wait, "자간 300");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(StyleOf(window, id), "LetterSpacing")) < 1e-6, Wait, "실행 취소 한 번에 자간 0");
                Assert.Equal("Arial", ui.Run(() => (string)Get(StyleOf(window, id), "FontFamily")));

                // 클립을 다시 고르고 자간 칸을 지나가기만 하면 편집이 아니다 — 다음 실행 취소는 글꼴 변경을 되돌린다
                ui.Run(() =>
                {
                    var session = Session(window);
                    var clipType = PluginUnderTest.Load(ui).Assembly.GetType("VideoEditor.Core.Clip", true);
                    session.GetType().GetMethod("Select").Invoke(session, new object[] { Array.CreateInstance(clipType, 0), false });
                });
                await ui.WaitUntil(() => Ui.ById<TabItem>(window, "StyleTab") == null, Wait, "선택 풀림");
                ui.Run(() => SelectClip(window, n => n.Contains("가나다 ABC")));
                await ui.WaitFor(() => Ui.ById<TabItem>(window, "StyleTab"), Wait, "다시 고름");
                ui.Run(() => Ui.ById<TabItem>(window, "StyleTab").IsSelected = true);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "StyleLetterSpacing") is TextBox shown && shown.IsVisible ? shown : null, Wait, "자간 칸");
                ui.Run(() =>
                {
                    Ui.Focus(Ui.ById<TextBox>(window, "StyleLetterSpacing"));
                    Ui.Focus(Canvas(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => (string)Get(StyleOf(window, id), "FontFamily") == "Malgun Gothic", Wait, "실행 취소가 글꼴 변경을 되돌림");
                Assert.True(ui.Run(() => (bool)Get(StyleOf(window, id), "Bold")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StyleTab_FillKind_ChangesPreview_ColorsAndAngle_UndoInOneStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "채우기 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<ComboBox>(window, "StyleFillKind"), Wait, "채우기 칸");
                var before = ui.Run(() =>
                {
                    // 스타일이 없을 때: 단색, 색 1만 보이고(글자 색) 그라데이션 칸은 숨김·각도는 꺼짐
                    Assert.Equal("단색", Ui.ById<ComboBox>(window, "StyleFillKind").SelectedItem);
                    Assert.True(Ui.ById<TextBox>(window, "StyleFillColor1").IsVisible);
                    Assert.Equal("#FFFFFF", Ui.ById<TextBox>(window, "StyleFillColor1").Text);
                    Assert.False(Ui.ById<TextBox>(window, "StyleFillColor2").IsVisible);
                    Assert.False(Ui.ById<Button>(window, "StyleFillAdd").IsVisible);
                    Assert.False(Ui.ById<TextBox>(window, "StyleFillAngle").IsEnabled);
                    return PreviewPixels(window);
                });

                // 선형 그라데이션 → 둘째 색(흰색과 대비되는 파랑)이 더해지고 미리 보기가 바뀐다
                ui.Run(() => Ui.ById<ComboBox>(window, "StyleFillKind").SelectedIndex = 1);
                await ui.WaitUntil(() => StyleOf(window, id) is object style && Get(Get(style, "Fill"), "Kind").ToString() == "Linear", Wait, "선형");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "그라데이션 미리 보기");
                ui.Run(() =>
                {
                    var colors = ((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors")).Cast<string>().ToList();
                    Assert.Equal(new[] { "#FFFFFF", "#3B82F6" }, colors);
                    Assert.True(Ui.ById<TextBox>(window, "StyleFillColor2").IsVisible);
                    Assert.True(Ui.ById<TextBox>(window, "StyleFillAngle").IsEnabled);
                    Assert.False(Ui.ById<Button>(window, "StyleFillRemove").IsEnabled);
                    Ui.Snap(window, plugin.ShotFolder, "style-tab-fill-linear");
                });

                // 색 추가 → 3색, 16진 칸에 색을 적으면 그 색, 잘못 적으면 되돌린다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StyleFillAdd")));
                await ui.WaitUntil(() => ((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors")).Count == 3, Wait, "색 추가");
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "StyleFillColor3");
                    Ui.Focus(hex);
                    hex.Text = "22c55e";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (string)((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors"))[2] == "#22C55E", Wait, "색3 초록");
                ui.Run(() =>
                {
                    // 적용하면 포커스가 남아 있어도 정리한 값으로 보이고, 팔레트는 그 색(이름으로 읽힘)
                    var hex = Ui.ById<TextBox>(window, "StyleFillColor3");
                    Assert.Equal("#22C55E", hex.Text);
                    Assert.Equal("초록", Ui.ById<ComboBox>(window, "StyleFillColor3Palette").SelectedItem.ToString());
                    hex.Text = "초록";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                    Assert.Equal("#22C55E", hex.Text);
                });

                // 칸에 포커스를 둔 채 메뉴로 실행 취소 → 칸에 되돌린 값이 보이고, 칸을 떠나도 옛 값(초록)을 다시 적용하지 않는다
                ui.Run(() => Ui.Menu(window, "편집", "실행 취소"));
                await ui.WaitUntil(() => (string)((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors"))[2] == "#3B82F6", Wait, "색3 실행 취소");
                ui.Run(() =>
                {
                    Assert.Equal("#3B82F6", Ui.ById<TextBox>(window, "StyleFillColor3").Text);
                    Ui.Focus(Canvas(window));
                });
                await Task.Delay(300);
                Assert.Equal("#3B82F6", ui.Run(() => (string)((System.Collections.IList)Get(Get(StyleOf(window, id), "Fill"), "Colors"))[2]));
                Assert.True(ui.Run(() => (bool)Get(Session(window), "CanRedo")), "다시 실행이 남아 있어야 한다");

                // 실행 취소: 색 추가 → 선형(스타일 없던 처음으로) — 접히는 색3 칸이 포커스를 잃어도 색을 다시 만들지 않는다
                foreach (var step in new[] { "색 추가", "선형" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StyleTab_Outline_ChangesPreview_WidthDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "윤곽선 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<CheckBox>(window, "StyleOutlineOn"), Wait, "윤곽선 칸");
                var before = ui.Run(() =>
                {
                    // 꺼져 있으면 색·두께·이음은 고칠 수 없다
                    Assert.False(Ui.ById<CheckBox>(window, "StyleOutlineOn").IsChecked == true);
                    Assert.False(Ui.ById<TextBox>(window, "StyleOutlineWidth").IsEnabled);
                    Assert.False(Ui.ById<TextBox>(window, "StyleOutlineColor").IsEnabled);
                    Assert.False(Ui.ById<ComboBox>(window, "StyleOutlineJoin").IsEnabled);
                    Assert.Equal("둥글게", Ui.ById<ComboBox>(window, "StyleOutlineJoin").SelectedItem);
                    return PreviewPixels(window);
                });

                // 켜기 → 스타일이 생기고(검정 4px) 그리개로 그린다 — 미리 보기 바탕도 검정이라 윤곽선 자체는 아직 안 보인다
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleOutlineOn")));
                await ui.WaitUntil(() => StyleOf(window, id) is object style && (bool)Get(Get(style, "Outline"), "Enabled"), Wait, "윤곽선 켜기");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "그리개 미리 보기");
                await Task.Delay(500);
                var on = ui.Run(() =>
                {
                    Assert.True(Ui.ById<TextBox>(window, "StyleOutlineWidth").IsEnabled);
                    Assert.True(Ui.ById<TextBox>(window, "StyleOutlineColor").IsEnabled);
                    Assert.True(Ui.ById<ComboBox>(window, "StyleOutlineJoin").IsEnabled);
                    Assert.Equal("#000000", Ui.ById<TextBox>(window, "StyleOutlineColor").Text);
                    Assert.Equal("4", Ui.ById<TextBox>(window, "StyleOutlineWidth").Text);
                    return PreviewPixels(window);
                });

                // 색을 빨강으로 → 검정 바탕에서 윤곽선이 보인다
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "StyleOutlineColor");
                    Ui.Focus(hex);
                    hex.Text = "ef4444";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (string)Get(Get(StyleOf(window, id), "Outline"), "Color") == "#EF4444", Wait, "빨간 윤곽선");
                // 칸을 떠나도(적용한 값 그대로) 편집이 더 생기지 않는다 — 실행 취소 키는 글 칸이 아닌 편집기로
                ui.Run(() => Ui.Focus(Canvas(window)));
                await ui.WaitUntil(() => Differs(on, PreviewPixels(window)), Wait, "빨간 윤곽선 미리 보기");
                await Task.Delay(500);
                var red = ui.Run(() => PreviewPixels(window));

                // 두께 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계 — 미리 보기는 더 두꺼워진다
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleOutlineWidthSlider");
                    foreach (var value in new[] { 10.0, 20.0, 30.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Outline"), "Width") - 30) < 1e-6, Wait, "두께 30");
                await ui.WaitUntil(() => Differs(red, PreviewPixels(window)), Wait, "두께 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-outline"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Outline"), "Width") - 4) < 1e-6, Wait, "실행 취소 한 번에 두께 4");
                Assert.True(ui.Run(() => (bool)Get(Get(StyleOf(window, id), "Outline"), "Enabled")));
                Assert.Equal("#EF4444", ui.Run(() => (string)Get(Get(StyleOf(window, id), "Outline"), "Color")));
                await ui.WaitUntil(() => !Differs(red, PreviewPixels(window)), Wait, "두께 4 미리 보기로 돌아옴");

                // 이음 각지게 → 값이 바뀌고, 실행 취소 두 번(이음·색)
                ui.Run(() => Ui.ById<ComboBox>(window, "StyleOutlineJoin").SelectedIndex = 1);
                await ui.WaitUntil(() => Get(Get(StyleOf(window, id), "Outline"), "Join").ToString() == "Miter", Wait, "각지게");
                foreach (var step in new[] { "이음", "색" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => (string)Get(Get(StyleOf(window, id), "Outline"), "Color") == "#000000", Wait, "색 실행 취소");

                // 색 칸에 적기만 하고(Enter 없이) 메뉴로 켜기를 실행 취소 → 칸이 꺼진 뒤 포커스를 잃어도 적던 색을 적용하지 않는다(스타일 없던 처음으로)
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "StyleOutlineColor");
                    Ui.Focus(hex);
                    hex.Text = "00ff00";
                    Ui.Menu(window, "편집", "실행 취소");
                });
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                ui.Run(() => Ui.Focus(Canvas(window)));
                await Task.Delay(300);
                Assert.Null(ui.Run(() => StyleOf(window, id)));
                Assert.True(ui.Run(() => (bool)Get(Session(window), "CanRedo")), "다시 실행이 남아 있어야 한다");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                Assert.False(ui.Run(() => Ui.ById<TextBox>(window, "StyleOutlineWidth").IsEnabled));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        private static string WarpKind(Window window, object id)
        {
            return Get(Get(StyleOf(window, id), "Warp"), "Kind").ToString();
        }

        private static double WarpAmount(Window window, object id)
        {
            return (double)Get(Get(StyleOf(window, id), "Warp"), "Amount");
        }

        [EditorFact]
        public async Task StyleTab_WarpKind_ChangesPreview_AmountDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "휘기 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<ComboBox>(window, "StyleWarpKind"), Wait, "휘기 칸");
                var before = ui.Run(() =>
                {
                    // 스타일이 없을 때: 없음, 정도는 기본값(50)이 보이되 꺼져 있다 — 보이기만 해서는 스타일이 생기지 않는다
                    Assert.Equal("없음", Ui.ById<ComboBox>(window, "StyleWarpKind").SelectedItem);
                    Assert.Equal("50", Ui.ById<TextBox>(window, "StyleWarpAmount").Text);
                    Assert.False(Ui.ById<TextBox>(window, "StyleWarpAmount").IsEnabled);
                    Assert.Null(StyleOf(window, id));
                    return PreviewPixels(window);
                });

                // 아치 위 → 스타일이 생기고(정도 50) 미리 보기가 바뀐다, 정도 칸이 켜진다
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "StyleWarpKind"), "아치 위"));
                await ui.WaitUntil(() => StyleOf(window, id) != null && WarpKind(window, id) == "ArchUp", Wait, "아치 위");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "아치 미리 보기");
                await Task.Delay(500);
                var arch = ui.Run(() =>
                {
                    Assert.True(Ui.ById<TextBox>(window, "StyleWarpAmount").IsEnabled);
                    Assert.Equal(50, WarpAmount(window, id), 6);
                    return PreviewPixels(window);
                });

                // 원 → 보이는 이름대로 Circle이 되고 미리 보기가 또 바뀐다
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "StyleWarpKind"), "원"));
                await ui.WaitUntil(() => WarpKind(window, id) == "Circle", Wait, "원");
                await ui.WaitUntil(() => Differs(arch, PreviewPixels(window)), Wait, "원 미리 보기");
                await Task.Delay(500);
                var circle = ui.Run(() => PreviewPixels(window));

                // 정도 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계 — 미리 보기도 바뀐다
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleWarpAmountSlider");
                    foreach (var value in new[] { 60.0, 80.0, 100.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs(WarpAmount(window, id) - 100) < 1e-6, Wait, "정도 100");
                await ui.WaitUntil(() => Differs(circle, PreviewPixels(window)), Wait, "정도 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-warp"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs(WarpAmount(window, id) - 50) < 1e-6, Wait, "실행 취소 한 번에 정도 50");
                Assert.Equal("Circle", ui.Run(() => WarpKind(window, id)));
                await ui.WaitUntil(() => !Differs(circle, PreviewPixels(window)), Wait, "정도 50 미리 보기로 돌아옴");

                // 실행 취소 두 번(원·아치 위) → 스타일 없던 처음, 정도 칸은 다시 꺼진다
                foreach (var step in new[] { "원", "아치 위" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                ui.Run(() =>
                {
                    Assert.Equal("없음", Ui.ById<ComboBox>(window, "StyleWarpKind").SelectedItem);
                    Assert.False(Ui.ById<TextBox>(window, "StyleWarpAmount").IsEnabled);
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StyleTab_Shadow_ChangesPreview_DistanceDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "그림자 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<CheckBox>(window, "StyleShadowOn"), Wait, "그림자 칸");
                var fields = new[] { "StyleShadowDistance", "StyleShadowAngle", "StyleShadowBlur", "StyleShadowOpacity" };
                var before = ui.Run(() =>
                {
                    // 꺼져 있으면 색·거리·방향·흐림·불투명도는 고칠 수 없고, 기본값만 보인다(보이기만 해서는 스타일이 생기지 않음)
                    Assert.False(Ui.ById<CheckBox>(window, "StyleShadowOn").IsChecked == true);
                    Assert.False(Ui.ById<TextBox>(window, "StyleShadowColor").IsEnabled);
                    foreach (var field in fields)
                        Assert.False(Ui.ById<TextBox>(window, field).IsEnabled, field);
                    Assert.Equal(new[] { "6", "45", "6", "60" }, fields.Select(f => Ui.ById<TextBox>(window, f).Text).ToArray());
                    return PreviewPixels(window);
                });
                Assert.Null(ui.Run(() => StyleOf(window, id)));

                // 켜기 → 스타일이 생기고(검정 60%) 그리개로 그린다 — 미리 보기 바탕도 검정이라 그림자 자체는 아직 안 보인다
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleShadowOn")));
                await ui.WaitUntil(() => StyleOf(window, id) is object style && (bool)Get(Get(style, "Shadow"), "Enabled"), Wait, "그림자 켜기");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "그리개 미리 보기");
                await Task.Delay(500);
                var on = ui.Run(() =>
                {
                    Assert.True(Ui.ById<TextBox>(window, "StyleShadowColor").IsEnabled);
                    foreach (var field in fields)
                        Assert.True(Ui.ById<TextBox>(window, field).IsEnabled, field);
                    Assert.Equal("#000000", Ui.ById<TextBox>(window, "StyleShadowColor").Text);
                    return PreviewPixels(window);
                });

                // 색을 빨강으로 → 검정 바탕에서 그림자가 보인다
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "StyleShadowColor");
                    Ui.Focus(hex);
                    hex.Text = "ef4444";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (string)Get(Get(StyleOf(window, id), "Shadow"), "Color") == "#EF4444", Wait, "빨간 그림자");
                ui.Run(() => Ui.Focus(Canvas(window)));
                await ui.WaitUntil(() => Differs(on, PreviewPixels(window)), Wait, "빨간 그림자 미리 보기");
                await Task.Delay(500);
                var red = ui.Run(() => PreviewPixels(window));

                // 거리 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계 — 미리 보기는 그림자가 더 멀어진다
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleShadowDistanceSlider");
                    foreach (var value in new[] { 20.0, 40.0, 60.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Shadow"), "Distance") - 60) < 1e-6, Wait, "거리 60");
                await ui.WaitUntil(() => Differs(red, PreviewPixels(window)), Wait, "거리 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-shadow"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Shadow"), "Distance") - 6) < 1e-6, Wait, "실행 취소 한 번에 거리 6");
                Assert.True(ui.Run(() => (bool)Get(Get(StyleOf(window, id), "Shadow"), "Enabled")));
                Assert.Equal("#EF4444", ui.Run(() => (string)Get(Get(StyleOf(window, id), "Shadow"), "Color")));
                await ui.WaitUntil(() => !Differs(red, PreviewPixels(window)), Wait, "거리 6 미리 보기로 돌아옴");

                // 불투명도 칸에 100(%) → 1.0, 방향 칸에 180(°) → 왼쪽, 흐림 슬라이더 끌기 → 미리 보기가 바뀐다
                foreach (var (field, text) in new[] { ("StyleShadowOpacity", "100"), ("StyleShadowAngle", "180") })
                {
                    ui.Run(() =>
                    {
                        var box = Ui.ById<TextBox>(window, field);
                        Ui.Focus(box);
                        box.Text = text;
                        Ui.Press(box, System.Windows.Input.Key.Enter);
                    });
                }
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Shadow"), "Opacity") - 1) < 1e-6, Wait, "불투명도 100%");
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Shadow"), "Angle") - 180) < 1e-6, Wait, "방향 180°");
                ui.Run(() =>
                {
                    Ui.Focus(Canvas(window));
                    var slider = Ui.ById<Slider>(window, "StyleShadowBlurSlider");
                    foreach (var value in new[] { 10.0, 20.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(Get(StyleOf(window, id), "Shadow"), "Blur") - 20) < 1e-6, Wait, "흐림 20");
                await ui.WaitUntil(() => Differs(red, PreviewPixels(window)), Wait, "불투명도·방향·흐림 미리 보기");

                // 실행 취소 네 번(흐림 끌기·방향·불투명도·색) → 검정 그림자, 한 번 더(켜기) → 스타일 없던 처음으로
                foreach (var step in new[] { "흐림", "방향", "불투명도", "색" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => (string)Get(Get(StyleOf(window, id), "Shadow"), "Color") == "#000000", Wait, "색 실행 취소");
                ui.Run(() =>
                {
                    var shadow = Get(StyleOf(window, id), "Shadow");
                    Assert.Equal(6.0, (double)Get(shadow, "Blur"), 6);
                    Assert.Equal(45.0, (double)Get(shadow, "Angle"), 6);
                    Assert.Equal(0.6, (double)Get(shadow, "Opacity"), 6);
                });
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                ui.Run(() =>
                {
                    Assert.False(Ui.ById<CheckBox>(window, "StyleShadowOn").IsChecked == true);
                    Assert.False(Ui.ById<TextBox>(window, "StyleShadowDistance").IsEnabled);
                });
                Assert.True(ui.Run(() => (bool)Get(Session(window), "CanRedo")), "다시 실행이 남아 있어야 한다");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        private static object GlowOf(Window window, object id)
        {
            return Get(StyleOf(window, id), "Glow");
        }

        [EditorFact]
        public async Task StyleTab_Glow_ChangesPreview_SizeDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "광선 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<CheckBox>(window, "StyleGlowOn"), Wait, "광선 칸");
                var fields = new[] { "StyleGlowSize", "StyleGlowOpacity" };
                var before = ui.Run(() =>
                {
                    // 꺼져 있으면 색·크기·불투명도는 고칠 수 없고, 기본값만 보인다(보이기만 해서는 스타일이 생기지 않음)
                    Assert.False(Ui.ById<CheckBox>(window, "StyleGlowOn").IsChecked == true);
                    Assert.False(Ui.ById<TextBox>(window, "StyleGlowColor").IsEnabled);
                    foreach (var field in fields)
                        Assert.False(Ui.ById<TextBox>(window, field).IsEnabled, field);
                    Assert.Equal("#FFD54F", Ui.ById<TextBox>(window, "StyleGlowColor").Text);
                    Assert.Equal(new[] { "12", "80" }, fields.Select(f => Ui.ById<TextBox>(window, f).Text).ToArray());
                    return PreviewPixels(window);
                });
                Assert.Null(ui.Run(() => StyleOf(window, id)));

                // 켜기 → 스타일이 생기고(노랑 #FFD54F 12px 80%) 그리개로 그린다 — 기본 광선 색은 검정 미리 보기 바탕에서도 보인다
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleGlowOn")));
                await ui.WaitUntil(() => StyleOf(window, id) is object style && (bool)Get(Get(style, "Glow"), "Enabled"), Wait, "광선 켜기");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "광선 미리 보기");
                await Task.Delay(500);
                var on = ui.Run(() =>
                {
                    Assert.True(Ui.ById<TextBox>(window, "StyleGlowColor").IsEnabled);
                    foreach (var field in fields)
                        Assert.True(Ui.ById<TextBox>(window, field).IsEnabled, field);
                    Assert.Equal("#FFD54F", (string)Get(GlowOf(window, id), "Color"));
                    return PreviewPixels(window);
                });

                // 크기 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계 — 미리 보기는 광선이 더 넓어진다
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleGlowSizeSlider");
                    foreach (var value in new[] { 20.0, 30.0, 40.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(GlowOf(window, id), "Size") - 40) < 1e-6, Wait, "크기 40");
                await ui.WaitUntil(() => Differs(on, PreviewPixels(window)), Wait, "크기 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-glow"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(GlowOf(window, id), "Size") - 12) < 1e-6, Wait, "실행 취소 한 번에 크기 12");
                Assert.True(ui.Run(() => (bool)Get(GlowOf(window, id), "Enabled")));
                await ui.WaitUntil(() => !Differs(on, PreviewPixels(window)), Wait, "크기 12 미리 보기로 돌아옴");

                // 불투명도 칸에 50(%) → 0.5, 색을 초록으로 → 미리 보기가 바뀐다
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "StyleGlowOpacity");
                    Ui.Focus(box);
                    box.Text = "50";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(GlowOf(window, id), "Opacity") - 0.5) < 1e-6, Wait, "불투명도 50%");
                ui.Run(() =>
                {
                    var hex = Ui.ById<TextBox>(window, "StyleGlowColor");
                    Ui.Focus(hex);
                    hex.Text = "22c55e";
                    Ui.Press(hex, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => (string)Get(GlowOf(window, id), "Color") == "#22C55E", Wait, "초록 광선");
                ui.Run(() => Ui.Focus(Canvas(window)));
                await ui.WaitUntil(() => Differs(on, PreviewPixels(window)), Wait, "불투명도·색 미리 보기");

                // 실행 취소 두 번(색·불투명도) → 처음 켠 값, 한 번 더(켜기) → 스타일 없던 처음으로
                foreach (var step in new[] { "색", "불투명도" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs((double)Get(GlowOf(window, id), "Opacity") - 0.8) < 1e-6, Wait, "불투명도 실행 취소");
                Assert.Equal("#FFD54F", ui.Run(() => (string)Get(GlowOf(window, id), "Color")));
                await ui.WaitUntil(() => !Differs(on, PreviewPixels(window)), Wait, "켠 미리 보기로 돌아옴");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                ui.Run(() =>
                {
                    Assert.False(Ui.ById<CheckBox>(window, "StyleGlowOn").IsChecked == true);
                    Assert.False(Ui.ById<TextBox>(window, "StyleGlowSize").IsEnabled);
                });
                Assert.True(ui.Run(() => (bool)Get(Session(window), "CanRedo")), "다시 실행이 남아 있어야 한다");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        private static double ReflectionValue(Window window, object id, string name)
        {
            return (double)Get(Get(StyleOf(window, id), "Reflection"), name);
        }

        [EditorFact]
        public async Task StyleTab_Reflection_ChangesPreview_SizeDragIsOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "반사 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                await ui.WaitFor(() => Ui.ById<CheckBox>(window, "StyleReflectionOn"), Wait, "반사 칸");
                var fields = new[] { "StyleReflectionSize", "StyleReflectionGap", "StyleReflectionOpacity" };
                var before = ui.Run(() =>
                {
                    // 꺼져 있으면 크기·간격·불투명도는 고칠 수 없고, 기본값만 보인다(보이기만 해서는 스타일이 생기지 않음)
                    Assert.False(Ui.ById<CheckBox>(window, "StyleReflectionOn").IsChecked == true);
                    foreach (var field in fields)
                        Assert.False(Ui.ById<TextBox>(window, field).IsEnabled, field);
                    Assert.Equal(new[] { "50", "4", "40" }, fields.Select(f => Ui.ById<TextBox>(window, f).Text).ToArray());
                    return PreviewPixels(window);
                });
                Assert.Null(ui.Run(() => StyleOf(window, id)));

                // 켜기 → 스타일이 생기고(크기 50%·간격 4·불투명도 40%) 흰 글자 아래에 흐린 반사가 생긴다 — 검정 미리 보기 바탕에서도 보인다
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleReflectionOn")));
                await ui.WaitUntil(() => StyleOf(window, id) is object style && (bool)Get(Get(style, "Reflection"), "Enabled"), Wait, "반사 켜기");
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "반사 미리 보기");
                await Task.Delay(500);
                var on = ui.Run(() =>
                {
                    foreach (var field in fields)
                        Assert.True(Ui.ById<TextBox>(window, field).IsEnabled, field);
                    return PreviewPixels(window);
                });

                // 크기 슬라이더를 끄는 동안(값 여러 번)은 실행 취소 한 단계 — 미리 보기는 반사가 길어진다
                ui.Run(() =>
                {
                    var slider = Ui.ById<Slider>(window, "StyleReflectionSizeSlider");
                    foreach (var value in new[] { 60.0, 80.0, 100.0 })
                        slider.Value = value;
                });
                await ui.WaitUntil(() => Math.Abs(ReflectionValue(window, id, "Size") - 100) < 1e-6, Wait, "크기 100");
                await ui.WaitUntil(() => Differs(on, PreviewPixels(window)), Wait, "크기 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-tab-reflection"));
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs(ReflectionValue(window, id, "Size") - 50) < 1e-6, Wait, "실행 취소 한 번에 크기 50");
                Assert.True(ui.Run(() => (bool)Get(Get(StyleOf(window, id), "Reflection"), "Enabled")));
                await ui.WaitUntil(() => !Differs(on, PreviewPixels(window)), Wait, "크기 50 미리 보기로 돌아옴");

                // 간격 칸에 30(px), 불투명도 칸에 100(%) → 1.0 — 미리 보기가 바뀐다
                foreach (var (field, text) in new[] { ("StyleReflectionGap", "30"), ("StyleReflectionOpacity", "100") })
                {
                    ui.Run(() =>
                    {
                        var box = Ui.ById<TextBox>(window, field);
                        Ui.Focus(box);
                        box.Text = text;
                        Ui.Press(box, System.Windows.Input.Key.Enter);
                    });
                }
                await ui.WaitUntil(() => Math.Abs(ReflectionValue(window, id, "Gap") - 30) < 1e-6, Wait, "간격 30");
                await ui.WaitUntil(() => Math.Abs(ReflectionValue(window, id, "Opacity") - 1) < 1e-6, Wait, "불투명도 100%");
                ui.Run(() => Ui.Focus(Canvas(window)));
                await ui.WaitUntil(() => Differs(on, PreviewPixels(window)), Wait, "간격·불투명도 미리 보기");

                // 실행 취소 두 번(불투명도·간격) → 켠 직후 값, 한 번 더(켜기) → 스타일 없던 처음으로
                foreach (var step in new[] { "불투명도", "간격" })
                    ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => Math.Abs(ReflectionValue(window, id, "Gap") - 4) < 1e-6, Wait, "간격 실행 취소");
                Assert.Equal(0.4, ui.Run(() => ReflectionValue(window, id, "Opacity")), 6);
                await ui.WaitUntil(() => !Differs(on, PreviewPixels(window)), Wait, "켠 직후 미리 보기로 돌아옴");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                ui.Run(() =>
                {
                    Assert.False(Ui.ById<CheckBox>(window, "StyleReflectionOn").IsChecked == true);
                    Assert.False(Ui.ById<TextBox>(window, "StyleReflectionSize").IsEnabled);
                });
                Assert.True(ui.Run(() => (bool)Get(Session(window), "CanRedo")), "다시 실행이 남아 있어야 한다");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StylePresets_ThumbnailsLoad_ClickAppliesInOneUndoStep()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "모음 ABC");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                var presets = Presets();
                Assert.Equal(16, presets.Count);
                // 꾸미기 탭 맨 위에 단추 16개 — 화면 낭독·글자로 찾기는 스타일 이름으로
                await ui.WaitFor(() => Ui.ById<Button>(window, "StylePreset" + (presets.Count - 1)), Wait, "스타일 모음 단추");
                ui.Run(() =>
                {
                    for (var i = 0; i < presets.Count; i++)
                    {
                        var button = Ui.ById<Button>(window, "StylePreset" + i);
                        Assert.NotNull(button);
                        Assert.Equal(presets[i].Name, System.Windows.Automation.AutomationProperties.GetName(button));
                    }
                    Assert.Null(Ui.ById<Button>(window, "StylePreset" + presets.Count));
                });
                // 썸네일이 시간 안에 채워진다(배경에서 그림)
                await ui.WaitUntil(() => Enumerable.Range(0, presets.Count).All(i => Ui.ById<Image>(window, "StylePreset" + i + "Image")?.Source != null), Wait, "썸네일");
                ui.Run(() =>
                {
                    // 16개라 좁은 속성 패널(시험 창 1280px에서 안쪽 약 200px)에도 한 줄에 셋 이상
                    var gallery = Ui.ById<WrapPanel>(window, "StylePresets");
                    gallery.UpdateLayout();
                    double Top(int i) => Ui.ById<Button>(window, "StylePreset" + i).TranslatePoint(new Point(0, 0), gallery).Y;
                    Assert.True(Math.Abs(Top(2) - Top(0)) < 1, "갤러리 폭 " + gallery.ActualWidth + "px — 한 줄에 셋이 들어가야 한다");
                    // 썸네일은 틀 안 가운데: 반사·휘기처럼 칠한 곳이 치우친 것도 그림 가운데가 틀 밖으로 크게 벗어나지 않는다
                    foreach (var name in new[] { "유리 반사", "원형 배지", "계단 글자" })
                    {
                        var image = Ui.ById<Image>(window, "StylePreset" + presets.FindIndex(p => p.Name == name) + "Image");
                        var frame = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(image));
                        var center = image.TranslatePoint(new Point(image.ActualWidth / 2, image.ActualHeight / 2), frame);
                        _out.WriteLine(name + ": 그림 " + image.ActualWidth.ToString("F1") + "×" + image.ActualHeight.ToString("F1") + " · 가운데 " + center + " · 틀 " + frame.ActualWidth + "×" + frame.ActualHeight);
                        Assert.InRange(center.X, 0, frame.ActualWidth);
                        Assert.InRange(center.Y, 0, frame.ActualHeight);
                        Assert.True(image.ActualWidth > frame.ActualWidth * 0.6, name + " 썸네일이 너무 작다");
                    }
                });
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-presets"));
                // 썸네일을 보이는 것만으로 스타일이 생기지 않는다
                Assert.Null(ui.Run(() => StyleOf(window, id)));
                var clipBefore = ui.Run(() => ProjectClips(window).First(c => Equals(Get(c, "Id"), id)));
                var fontSize = ui.Run(() => (double)Get(clipBefore, "FontSize"));
                var before = ui.Run(() => PreviewPixels(window));

                // "노란 자막" → 스타일이 통째로 그 스타일, 글자 내용·크기는 그대로, [글자] 탭 글자 색 = 채우기 색 1, 미리 보기가 바뀐다
                var yellow = presets.FindIndex(p => p.Name == "노란 자막");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset" + yellow)));
                await ui.WaitUntil(() => StyleOf(window, id) != null, Wait, "노란 자막");
                ui.Run(() =>
                {
                    var style = StyleOf(window, id);
                    Assert.Equal(StyleJson(presets[yellow].Style), StyleJson(style));
                    Assert.Equal("#FACC15", (string)((System.Collections.IList)Get(Get(style, "Fill"), "Colors"))[0]);
                    var clip = ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
                    Assert.Equal("모음 ABC", (string)Get(clip, "Text"));
                    Assert.Equal(fontSize, (double)Get(clip, "FontSize"));
                    Assert.Equal("#FACC15", (string)Get(clip, "TextColor"));
                    // 꾸미기 탭 칸도 새 값(굵게·윤곽선 켜짐·두께 6)
                    Assert.True(Ui.ById<CheckBox>(window, "StyleBold").IsChecked == true);
                    Assert.True(Ui.ById<CheckBox>(window, "StyleOutlineOn").IsChecked == true);
                    Assert.Equal("6", Ui.ById<TextBox>(window, "StyleOutlineWidth").Text);
                });
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "노란 자막 미리 보기");
                await Task.Delay(500);
                var applied = ui.Run(() => PreviewPixels(window));
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-presets-yellow"));

                // 다른 스타일("속 빈 글자" — 흰 윤곽선은 검정 미리 보기 바탕에서도 보인다) → 실행 취소 한 번에 노란 자막, 한 번 더면 스타일 없던 처음으로
                var hollow = presets.FindIndex(p => p.Name == "속 빈 글자");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset" + hollow)));
                await ui.WaitUntil(() => StyleJson(StyleOf(window, id)) == StyleJson(presets[hollow].Style), Wait, "속 빈 글자");
                await ui.WaitUntil(() => Differs(applied, PreviewPixels(window)), Wait, "속 빈 글자 미리 보기");
                ui.Run(() =>
                {
                    Ui.Focus(Canvas(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => StyleJson(StyleOf(window, id)) == StyleJson(presets[yellow].Style), Wait, "실행 취소 한 번에 노란 자막");
                await ui.WaitUntil(() => !Differs(applied, PreviewPixels(window)), Wait, "노란 자막 미리 보기로 돌아옴");
                ui.Run(() => Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control));
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                Assert.Equal("#FFFFFF", ui.Run(() => (string)Get(ProjectClips(window).First(c => Equals(Get(c, "Id"), id)), "TextColor")));
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StylePresets_GlowReflectionWarp_FillTheirSections_UndoOneByOne()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "모음 둘째");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                var presets = Presets();
                await ui.WaitFor(() => Ui.ById<Button>(window, "StylePreset" + (presets.Count - 1)), Wait, "스타일 모음 단추");
                var shown = ui.Run(() => PreviewPixels(window));
                var steps = new System.Collections.Generic.List<(int Index, byte[] Pixels)>();

                // 2단계 스타일을 차례로 누른다: 스타일이 통째로 바뀌고 미리 보기가 바뀌며, 그 효과의 묶음 칸이 켜진다(검정 미리 보기 바탕에서도 보이는 색들)
                foreach (var name in new[] { "네온", "유리 반사", "아치 제목" })
                {
                    var index = presets.FindIndex(p => p.Name == name);
                    ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset" + index)));
                    await ui.WaitUntil(() => StyleJson(StyleOf(window, id)) == StyleJson(presets[index].Style), Wait, name);
                    var previous = shown;
                    await ui.WaitUntil(() => Differs(previous, PreviewPixels(window)), Wait, name + " 미리 보기");
                    await Task.Delay(500);
                    shown = ui.Run(() => PreviewPixels(window));
                    steps.Add((index, shown));
                    ui.Run(() =>
                    {
                        Assert.Equal(name == "네온", Ui.ById<CheckBox>(window, "StyleGlowOn").IsChecked == true);
                        Assert.Equal(name == "유리 반사", Ui.ById<CheckBox>(window, "StyleReflectionOn").IsChecked == true);
                        Assert.Equal(name == "아치 제목" ? "아치 위" : "없음", Ui.ById<ComboBox>(window, "StyleWarpKind").SelectedItem);
                        Assert.Equal(name == "아치 제목", Ui.ById<TextBox>(window, "StyleWarpAmount").IsEnabled);
                        // [글자] 탭 글자 색 = 채우기 색 1(네온은 채우기 없음이어도 색 1을 남겨 둔다)
                        var clip = ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
                        Assert.Equal((string)((System.Collections.IList)Get(Get(presets[index].Style, "Fill"), "Colors"))[0], (string)Get(clip, "TextColor"));
                    });
                    if (name == "네온")
                        Assert.Equal("#0EA5E9", ui.Run(() => Ui.ById<TextBox>(window, "StyleGlowColor").Text));
                    if (name == "아치 제목")
                        Assert.Equal("60", ui.Run(() => Ui.ById<TextBox>(window, "StyleWarpAmount").Text));
                }
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "style-presets-arch"));

                // 실행 취소 한 번에 한 스타일씩 거꾸로(아치 → 유리 반사 → 네온 → 스타일 없음)
                for (var k = steps.Count - 2; k >= -1; k--)
                {
                    ui.Run(() =>
                    {
                        Ui.Focus(Canvas(window));
                        Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                    });
                    if (k < 0)
                    {
                        await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "처음으로");
                        break;
                    }
                    var (index, pixels) = steps[k];
                    await ui.WaitUntil(() => StyleJson(StyleOf(window, id)) == StyleJson(presets[index].Style), Wait, "실행 취소 → " + presets[index].Name);
                    await ui.WaitUntil(() => !Differs(pixels, PreviewPixels(window)), Wait, presets[index].Name + " 미리 보기로 돌아옴");
                }
                ui.Run(() =>
                {
                    Assert.False(Ui.ById<CheckBox>(window, "StyleGlowOn").IsChecked == true);
                    Assert.Equal("없음", Ui.ById<ComboBox>(window, "StyleWarpKind").SelectedItem);
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task StyledText_PreviewFrame_EqualsExportedMp4()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var window = await Open(ui, plugin);
            try
            {
                var id = await AddStyledText(ui, window, "가나다 ABC");
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleBold")));
                await ui.WaitUntil(() => StyleOf(window, id) != null, Wait, "스타일");
                // 글자 크기는 [글자] 탭(보이는 칸에 키를 보낸다)
                ui.Run(() => Ui.ById<TabControl>(window, "PropertyTabs").SelectedIndex = 0);
                await ui.WaitFor(() => Ui.ById<TextBox>(window, "PropFontSize") is TextBox shown && shown.IsVisible ? shown : null, Wait, "글자 크기 칸");
                ui.Run(() =>
                {
                    var box = Ui.ById<TextBox>(window, "PropFontSize");
                    Ui.Focus(box);
                    box.Text = "300";
                    Ui.Press(box, System.Windows.Input.Key.Enter);
                });
                await ui.WaitUntil(() => Math.Abs((double)Get(ProjectClips(window).First(c => Equals(Get(c, "Id"), id)), "FontSize") - 300) < 1e-6, Wait, "글자 크기 300");

                var assembly = plugin.Assembly;
                var tool = assembly.GetType("VideoEditor.Media.FfmpegTool", true).GetMethod("Locate").Invoke(null, new object[] { FfmpegFolder, null });
                var projectFile = assembly.GetType("VideoEditor.Core.ProjectFile", true);
                var copy = ui.Run(() => projectFile.GetMethod("FromSnapshot").Invoke(null, new[] { projectFile.GetMethod("Snapshot").Invoke(null, new[] { Project(window) }) }));
                var rasterizer = NewRasterizer();

                var frame = assembly.GetType("VideoEditor.Media.PreviewRenderer", true).GetMethod("Frame");
                var preview = await (Task<byte[]>)frame.Invoke(null, new[] { tool, copy, (object)15L, 320, 180, CancellationToken.None, rasterizer });
                Assert.NotNull(preview);
                var bright = 0;
                for (var i = 0; i < preview.Length; i += 4)
                {
                    if (preview[i] > 200 && preview[i + 1] > 200 && preview[i + 2] > 200)
                        bright++;
                }
                Assert.True(bright > 300, "흰 글자 픽셀 " + bright);

                var output = Path.Combine(Folder(), "styled-" + Guid.NewGuid().ToString("N") + ".mp4");
                var settingsType = assembly.GetType("VideoEditor.Media.ExportSettings", true);
                var settings = Activator.CreateInstance(settingsType);
                settingsType.GetProperty("OutputPath").SetValue(settings, output);
                settingsType.GetProperty("Width").SetValue(settings, 320);
                settingsType.GetProperty("Height").SetValue(settings, 180);
                settingsType.GetProperty("Encoder").SetValue(settings, "mpeg4");
                settingsType.GetProperty("Quality").SetValue(settings, 0);
                settingsType.GetProperty("From").SetValue(settings, 0L);
                settingsType.GetProperty("To").SetValue(settings, (long)Get(copy, "Duration"));
                var export = assembly.GetType("VideoEditor.Media.Exporter", true).GetMethod("Export");
                await (Task)export.Invoke(null, new[] { tool, copy, settings, null, CancellationToken.None, rasterizer });

                var raw = output + ".bgra";
                RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", "0.5", "-i", output, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", raw);
                var exported = File.ReadAllBytes(raw);
                Assert.Equal(preview.Length, exported.Length);
                long diff = 0;
                for (var i = 0; i < preview.Length; i++)
                {
                    if (i % 4 != 3)
                        diff += Math.Abs(preview[i] - exported[i]);
                }
                var mean = diff / (preview.Length * 0.75);
                _out.WriteLine("흰 글자 픽셀 " + bright + " · 미리 보기와 내보내기의 평균 차이 " + mean);
                Assert.True(mean < 4, "평균 차이 " + mean);
                ((IDisposable)rasterizer).Dispose();
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
            }
        }

        [EditorFact]
        public async Task TextTab_StyleGallery_AddsWithChosenStyle_DoubleClickAddsAtOnce()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            // 내 스타일 하나를 넣어 두면 [글자] 탭 아래에도 보인다
            plugin.Manager.SetSetting("textStyles", JsonSerializer.Serialize(new[] { new { Name = "내 하늘", Style = new { Fill = new { Kind = "Solid", Colors = new[] { "#38BDF8" } } } } }));
            Window window = null;
            try
            {
                window = await Open(ui, plugin);
                ui.Run(() => Ui.ById<TabControl>(window, "MediaTabs").SelectedIndex = 2);
                await ui.WaitFor(() => Ui.ById<Button>(window, "NewTextStyle1"), Wait, "글자 탭 스타일");
                // 썸네일이 그려지고, 처음엔 꾸미지 않음
                await ui.WaitUntil(() => Enumerable.Range(0, 16).All(i => Ui.ById<Image>(window, "NewTextStyle" + i + "Image") != null), Wait, "스타일 썸네일");
                await ui.WaitFor(() => Ui.ById<Button>(window, "NewTextMine0"), Wait, "내 스타일 단추");
                var presets = Presets();
                ui.Run(() =>
                {
                    Assert.Equal("스타일: 꾸미지 않음", Ui.ById<TextBlock>(window, "NewTextStyleNote").Text);
                    Assert.Equal(presets[1].Name, System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "NewTextStyle1")));
                    Ui.Snap(window, plugin.ShotFolder, "text-tab-styles");
                });
                object ClipOf(string text) => ProjectClips(window).FirstOrDefault(c => (string)Get(c, "Text") == text);

                // 노란 자막을 고르고 [글자 추가] → 그 스타일의 사본, 글자 색 = 채우기 색 1
                // 자동화 클릭은 디스패처에 맡겨지므로 누른 뒤 결과를 기다린다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "NewTextStyle1")));
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "NewTextStyleNote").Text == "스타일: " + presets[1].Name, Wait, "노란 자막 고름");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "노란 글자";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => ClipOf("노란 글자") != null, Wait, "노란 글자 클립");
                ui.Run(() =>
                {
                    var clip = ClipOf("노란 글자");
                    Assert.Equal(StyleJson(presets[1].Style), StyleJson(Get(clip, "Style")));
                    Assert.NotSame(presets[1].Style, Get(clip, "Style"));
                    Assert.Equal((string)((System.Collections.IList)Get(Get(Get(clip, "Style"), "Fill"), "Colors"))[0], (string)Get(clip, "TextColor"));
                });

                // 고른 단추를 다시 누르면 풀린다 → 꾸미지 않은 글자
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "NewTextStyle1")));
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "NewTextStyleNote").Text == "스타일: 꾸미지 않음", Wait, "고르기 풀림");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "보통 글자";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => ClipOf("보통 글자") != null, Wait, "보통 글자 클립");
                Assert.Null(ui.Run(() => Get(ClipOf("보통 글자"), "Style")));

                // 두 번 누르면 그 스타일로 바로 넣는다 — 실행 취소 한 번에 그 글자만 사라진다
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "바로 넣기";
                    var tile = Ui.ById<Button>(window, "NewTextStyle2");
                    tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                });
                await ui.WaitUntil(() => ClipOf("바로 넣기") != null, Wait, "두 번 눌러 넣기");
                Assert.Equal(ui.Run(() => StyleJson(presets[2].Style)), ui.Run(() => StyleJson(Get(ClipOf("바로 넣기"), "Style"))));
                ui.Run(() =>
                {
                    Ui.Focus(Canvas(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => ClipOf("바로 넣기") == null, Wait, "실행 취소");
                Assert.NotNull(ui.Run(() => ClipOf("보통 글자")));

                // 내 스타일도 같은 방식
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "NewTextMine0")));
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "NewTextStyleNote").Text == "스타일: 내 하늘", Wait, "내 스타일 고름");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(window, "NewTextBox").Text = "내 스타일 글자";
                    Ui.Click(Ui.ById<Button>(window, "AddTextButton"));
                });
                await ui.WaitUntil(() => ClipOf("내 스타일 글자") != null, Wait, "내 스타일 글자");
                Assert.Equal("#38BDF8", ui.Run(() => (string)Get(ClipOf("내 스타일 글자"), "TextColor")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                plugin.Manager.SetSetting("textStyles", null);
                if (window != null)
                    await CloseEditor(ui, window);
            }
        }

        // ---------- 내 스타일(이슈 #22) ----------

        // 플러그인 설정 키(EditorServices.TextStylesKey). 시험용 설정은 프로세스 안에서 남으므로 시험 앞뒤로 비운다(다른 시험으로 새지 않게)
        private const string MyStylesKey = "textStyles";

        /// <summary>제목이 정확히 이것인 보이는 창(UI 스레드, 없으면 null) — "내 스타일"은 "내 스타일로 저장"·"내 스타일 덮어쓰기"와 앞이 같아 Ui.Window로는 가를 수 없다.</summary>
        private static Window DialogTitled(string title)
        {
            return Ui.Windows().FirstOrDefault(w => w.IsVisible && w.Title == title);
        }

        /// <summary>설정 textStyles에 저장된 이름들(없으면 빈 목록).</summary>
        private static System.Collections.Generic.List<string> SavedNames(PluginUnderTest plugin)
        {
            var json = plugin.Manager.GetSetting(MyStylesKey);
            if (json == null)
                return new System.Collections.Generic.List<string>();
            using (var document = JsonDocument.Parse(json))
                return document.RootElement.EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToList();
        }

        /// <summary>설정 textStyles에 저장된 이름의 스타일 JSON.</summary>
        private static JsonElement SavedStyle(PluginUnderTest plugin, string name)
        {
            using (var document = JsonDocument.Parse(plugin.Manager.GetSetting(MyStylesKey)))
                return document.RootElement.EnumerateArray().First(e => e.GetProperty("Name").GetString() == name).GetProperty("Style").Clone();
        }

        /// <summary>[내 스타일로 저장]을 누르고 이름 창에 name을 적어 [확인]한다. 이름 창에 먼저 적혀 있던 글을 돌려준다(덮어쓰기·알림 창은 부른 쪽이 처리).</summary>
        private static async Task<string> SaveMine(UiThread ui, Window window, string name)
        {
            // 이름 창은 모달이라 Post로 누르고 뜬 창을 찾는다
            ui.Post(() => Ui.Click(Ui.ById<Button>(window, "StyleSaveMine")));
            var input = await ui.WaitFor(() => DialogTitled("내 스타일로 저장"), Wait, "이름 창");
            var initial = ui.Run(() =>
            {
                var box = Ui.ById<TextBox>(input, "DialogInputBox");
                var text = box.Text;
                box.Text = name;
                Ui.Click(Ui.ById<Button>(input, "DialogOkButton"));
                return text;
            });
            await ui.WaitUntil(() => DialogTitled("내 스타일로 저장") == null, Wait, "이름 창 닫힘");
            return initial;
        }

        /// <summary>확인 창(UiKit.Ask)이 뜨기를 기다려 글을 읽고 index번 단추를 누른다. 창의 글을 돌려준다.</summary>
        private static async Task<string> Answer(UiThread ui, string title, int index)
        {
            var dialog = await ui.WaitFor(() => DialogTitled(title), Wait, title + " 창");
            var message = ui.Run(() =>
            {
                var text = string.Join(" ", Ui.Texts(dialog));
                Ui.Click(Ui.ById<Button>(dialog, "AskButton" + index));
                return text;
            });
            await ui.WaitUntil(() => DialogTitled(title) == null, Wait, title + " 창 닫힘");
            return message;
        }

        [EditorFact]
        public async Task MyStyles_SaveShowsButton_SurvivesReopen_RemoveDeletes()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SetSetting(MyStylesKey, null);
            Window window = null;
            try
            {
                window = await Open(ui, plugin);
                var id = await AddStyledText(ui, window, "내 스타일 ABC");
                await ui.WaitFor(() => Ui.ById<Button>(window, "StyleSaveMine"), Wait, "[내 스타일로 저장]");
                ui.Run(() =>
                {
                    // 비어 있으면 빈 줄 대신 안내
                    Assert.Null(Ui.ById<Button>(window, "StyleMine0"));
                    Assert.Equal(Visibility.Visible, Ui.ById<TextBlock>(window, "StyleMineHint").Visibility);
                    Assert.Equal(Visibility.Collapsed, Ui.ById<WrapPanel>(window, "StyleMine").Visibility);
                });

                // 스타일이 없는 글자: 지금 색을 이어받은 스타일(왼쪽 맞춤)을 저장 — 저장은 편집이 아니라 클립에는 스타일이 생기지 않는다. 이름은 앞뒤 공백을 다듬는다
                Assert.Equal("내 스타일 1", await SaveMine(ui, window, "  흰 바탕  "));
                await ui.WaitFor(() => Ui.ById<Button>(window, "StyleMine0"), Wait, "내 스타일 단추");
                Assert.Equal(new[] { "흰 바탕" }, SavedNames(plugin));
                var plain = SavedStyle(plugin, "흰 바탕");
                Assert.Equal("Left", plain.GetProperty("Align").GetString());
                Assert.Equal("#FFFFFF", plain.GetProperty("Fill").GetProperty("Colors")[0].GetString());
                await ui.WaitUntil(() => Status(window) == "내 스타일에 저장했습니다: 흰 바탕", Wait, "저장 알림");
                ui.Run(() =>
                {
                    Assert.Null(StyleOf(window, id));
                    var button = Ui.ById<Button>(window, "StyleMine0");
                    Assert.Equal("흰 바탕", System.Windows.Automation.AutomationProperties.GetName(button));
                    Assert.Equal("흰 바탕", button.ToolTip);
                    Assert.Equal(Visibility.Collapsed, Ui.ById<TextBlock>(window, "StyleMineHint").Visibility);
                });

                // "노란 자막"을 입힌 뒤 저장 → 둘째 단추. 저장한 것은 사본이라 클립을 고쳐도 설정은 그대로
                var presets = Presets();
                var yellow = presets.FindIndex(p => p.Name == "노란 자막");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset" + yellow)));
                await ui.WaitUntil(() => StyleOf(window, id) != null, Wait, "노란 자막");
                Assert.Equal("내 스타일 2", await SaveMine(ui, window, "노랑 제목"));
                await ui.WaitFor(() => Ui.ById<Button>(window, "StyleMine1"), Wait, "둘째 단추");
                Assert.Equal(new[] { "흰 바탕", "노랑 제목" }, SavedNames(plugin));
                Assert.True(SavedStyle(plugin, "노랑 제목").GetProperty("Bold").GetBoolean());
                Assert.Equal("#FACC15", SavedStyle(plugin, "노랑 제목").GetProperty("Fill").GetProperty("Colors")[0].GetString());
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "StyleBold")));
                await ui.WaitUntil(() => !(bool)Get(StyleOf(window, id), "Bold"), Wait, "굵게 끔");
                Assert.True(SavedStyle(plugin, "노랑 제목").GetProperty("Bold").GetBoolean());
                // 썸네일은 스타일 모음과 같이 그린다
                await ui.WaitUntil(() => Ui.ById<Image>(window, "StyleMine0Image")?.Source != null && Ui.ById<Image>(window, "StyleMine1Image")?.Source != null, Wait, "내 스타일 썸네일");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "my-styles"));

                // 편집 창을 닫았다 열어도 남아 있다
                await CloseEditor(ui, window);
                window = null;
                window = await Open(ui, plugin);
                await AddStyledText(ui, window, "다시 연 창");
                await ui.WaitFor(() => Ui.ById<Button>(window, "StyleMine1"), Wait, "다시 연 창의 내 스타일");
                ui.Run(() =>
                {
                    Assert.Equal("흰 바탕", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "StyleMine0")));
                    Assert.Equal("노랑 제목", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "StyleMine1")));
                    Assert.Null(Ui.ById<Button>(window, "StyleMine2"));
                });

                // 오른쪽 클릭 [지우기] — 설정이라 실행 취소에 남지 않음을 알린다
                ui.Run(() => Ui.ContextMenu(Ui.ById<Button>(window, "StyleMine0"), "지우기"));
                await ui.WaitUntil(() => Ui.ById<Button>(window, "StyleMine1") == null, Wait, "지우기");
                Assert.Equal(new[] { "노랑 제목" }, SavedNames(plugin));
                await ui.WaitUntil(() => Status(window).StartsWith("내 스타일에서 지웠습니다: 흰 바탕", StringComparison.Ordinal) && Status(window).Contains("실행 취소"), Wait, "지우기 알림");
                Assert.Equal("노랑 제목", ui.Run(() => System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "StyleMine0"))));
                ui.Run(() => Ui.ContextMenu(Ui.ById<Button>(window, "StyleMine0"), "지우기"));
                await ui.WaitUntil(() => Ui.ById<Button>(window, "StyleMine0") == null, Wait, "모두 지움");
                Assert.Null(plugin.Manager.GetSetting(MyStylesKey));
                Assert.Equal(Visibility.Visible, ui.Run(() => Ui.ById<TextBlock>(window, "StyleMineHint").Visibility));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                plugin.Manager.SetSetting(MyStylesKey, null);
            }
        }

        [EditorFact]
        public async Task MyStyles_ClickAppliesInOneUndoStep_OverwriteAsks_51stAndBadNameRefused()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            // 악성·넘치는 설정: 제어 문자 이름·겹친 이름·형식이 틀린 스타일·쓰레기 항목은 건너뛰고 앞에서부터 50개
            var entries = new System.Collections.Generic.List<object>
            {
                new { Name = "하늘 광선", Style = new { Bold = true, Fill = new { Kind = "Solid", Colors = new[] { "#38BDF8" } }, Glow = new { Enabled = true, Color = "#38BDF8", Size = 12.0, Opacity = 0.8 } } },
                new { Name = "줄\n바꿈", Style = new { } },
                new { Name = " 하늘 광선 ", Style = new { } },
                new { Name = "굵게 자리에 글", Style = new { Bold = "yes" } },
                "쓰레기"
            };
            // 채움 스타일은 모두 달라야 썸네일도 따로 그린다(같으면 캐시 키 하나 — 많은 썸네일이 스레드 풀을 막던 문제를 못 잡는다)
            for (var i = 1; i <= 49; i++)
                entries.Add(new { Name = "채움 " + i, Style = new { Outline = new { Enabled = true, Color = "#EF4444", Width = (double)i } } });
            entries.Add(new { Name = "넘침", Style = new { } });
            var seeded = JsonSerializer.Serialize(entries);
            plugin.Manager.SetSetting(MyStylesKey, seeded);
            Window window = null;
            try
            {
                window = await Open(ui, plugin);
                var id = await AddStyledText(ui, window, "광선 ABC");
                await ui.WaitFor(() => Ui.ById<Button>(window, "StyleMine49"), Wait, "내 스타일 50개");
                ui.Run(() =>
                {
                    Assert.Null(Ui.ById<Button>(window, "StyleMine50"));
                    Assert.Equal("하늘 광선", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "StyleMine0")));
                    Assert.Equal("채움 1", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "StyleMine1")));
                    Assert.Equal("채움 49", System.Windows.Automation.AutomationProperties.GetName(Ui.ById<Button>(window, "StyleMine49")));
                });
                // 썸네일 66개(스타일 모음 16 + 내 스타일 50)가 시간 안에 모두 뜨고, 그동안 스레드 풀이 막히지 않는다
                var thumbnails = Stopwatch.StartNew();
                long slowest = 0;
                await ui.WaitUntil(() =>
                {
                    var probe = Stopwatch.StartNew();
                    Task.Run(() => 0).Wait();
                    slowest = Math.Max(slowest, probe.ElapsedMilliseconds);
                    return Enumerable.Range(0, 50).All(i => Ui.ById<Image>(window, "StyleMine" + i + "Image") != null)
                        && Enumerable.Range(0, 16).All(i => Ui.ById<Image>(window, "StylePreset" + i + "Image") != null);
                }, TimeSpan.FromSeconds(20), "썸네일 66개");
                _out.WriteLine("썸네일 66개 " + thumbnails.ElapsedMilliseconds + "ms · 스레드 풀 응답 최대 " + slowest + "ms");
                Assert.True(slowest < 1000, "스레드 풀 응답 " + slowest + "ms");
                await ui.WaitUntil(() => PreviewHasPicture(window), Wait, "글자 미리 보기");
                Assert.Null(ui.Run(() => StyleOf(window, id)));
                var fontSize = ui.Run(() => (double)Get(ProjectClips(window).First(c => Equals(Get(c, "Id"), id)), "FontSize"));
                var before = ui.Run(() => PreviewPixels(window));

                // 누르면 스타일이 통째로 그 스타일(하늘색 채우기 + 광선), 글자 내용·크기는 그대로, [글자] 탭 글자 색 = 채우기 색 1 — 하늘색은 검정 미리 보기 바탕에서 보인다
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StyleMine0")));
                await ui.WaitUntil(() => StyleOf(window, id) != null, Wait, "하늘 광선");
                ui.Run(() =>
                {
                    var style = StyleOf(window, id);
                    Assert.True((bool)Get(style, "Bold"));
                    Assert.Equal("#38BDF8", (string)((System.Collections.IList)Get(Get(style, "Fill"), "Colors"))[0]);
                    Assert.True((bool)Get(Get(style, "Glow"), "Enabled"));
                    var clip = ProjectClips(window).First(c => Equals(Get(c, "Id"), id));
                    Assert.Equal("광선 ABC", (string)Get(clip, "Text"));
                    Assert.Equal(fontSize, (double)Get(clip, "FontSize"));
                    Assert.Equal("#38BDF8", (string)Get(clip, "TextColor"));
                    Assert.True(Ui.ById<CheckBox>(window, "StyleGlowOn").IsChecked == true);
                });
                await ui.WaitUntil(() => Differs(before, PreviewPixels(window)), Wait, "하늘 광선 미리 보기");
                ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "my-styles-applied"));

                // 실행 취소 한 번에 스타일 없던 처음으로
                ui.Run(() =>
                {
                    Ui.Focus(Canvas(window));
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => StyleOf(window, id) == null, Wait, "실행 취소 한 번에 처음으로");
                Assert.Equal("#FFFFFF", ui.Run(() => (string)Get(ProjectClips(window).First(c => Equals(Get(c, "Id"), id)), "TextColor")));
                await ui.WaitUntil(() => !Differs(before, PreviewPixels(window)), Wait, "처음 미리 보기");

                // 50개가 찼으면 새 이름은 알리고 저장하지 않는다(조용히 버리지 않음) — 설정은 넣어 둔 글 그대로
                Assert.Equal("내 스타일 51", await SaveMine(ui, window, "내 스타일 51"));
                Assert.Contains("50개까지", await Answer(ui, "내 스타일", 0));
                Assert.Equal(seeded, plugin.Manager.GetSetting(MyStylesKey));
                ui.Run(() =>
                {
                    Assert.NotNull(Ui.ById<Button>(window, "StyleMine49"));
                    Assert.Null(Ui.ById<Button>(window, "StyleMine50"));
                });

                // 같은 이름은 덮어쓸지 묻는다: [취소]면 그대로, [덮어쓰기]면 그 자리에서 바뀐다(50개가 차도 됨)
                var presets = Presets();
                var yellow = presets.FindIndex(p => p.Name == "노란 자막");
                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "StylePreset" + yellow)));
                await ui.WaitUntil(() => StyleOf(window, id) != null, Wait, "노란 자막");
                await SaveMine(ui, window, "하늘 광선");
                Assert.Contains("하늘 광선", await Answer(ui, "내 스타일 덮어쓰기", 1));
                Assert.Equal(seeded, plugin.Manager.GetSetting(MyStylesKey));
                await SaveMine(ui, window, "하늘 광선");
                await Answer(ui, "내 스타일 덮어쓰기", 0);
                await ui.WaitUntil(() => plugin.Manager.GetSetting(MyStylesKey) != seeded, Wait, "덮어씀");
                // 다시 쓴 설정에는 규칙에 맞는 50개만 남는다(악성 항목은 빠짐)
                var names = SavedNames(plugin);
                Assert.Equal(50, names.Count);
                Assert.Equal("하늘 광선", names[0]);
                Assert.Equal("채움 49", names[49]);
                Assert.Equal("#FACC15", SavedStyle(plugin, "하늘 광선").GetProperty("Fill").GetProperty("Colors")[0].GetString());
                Assert.False(SavedStyle(plugin, "하늘 광선").GetProperty("Glow").GetProperty("Enabled").GetBoolean());

                // 빈 이름은 알리고 저장하지 않는다
                var stored = plugin.Manager.GetSetting(MyStylesKey);
                await SaveMine(ui, window, "   ");
                Assert.Contains("1~40자", await Answer(ui, "내 스타일", 0));
                Assert.Equal(stored, plugin.Manager.GetSetting(MyStylesKey));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                await CloseEditor(ui, window);
                plugin.Manager.SetSetting(MyStylesKey, null);
            }
        }
    }
}
