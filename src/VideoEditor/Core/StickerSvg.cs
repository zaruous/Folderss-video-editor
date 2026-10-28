using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace VideoEditor.Core
{
    /// <summary>스티커 경로 조각: 직선 또는 3차 베지어(점은 viewBox 좌표, 변환을 이미 적용함).</summary>
    public readonly struct StickerSegment
    {
        public StickerSegment(bool cubic, double x1, double y1, double x2, double y2, double x, double y)
        {
            Cubic = cubic;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            X = x;
            Y = y;
        }

        public bool Cubic { get; }
        public double X1 { get; }
        public double Y1 { get; }
        public double X2 { get; }
        public double Y2 { get; }
        public double X { get; }
        public double Y { get; }
    }

    /// <summary>스티커 경로의 한 그림(시작점 + 조각들, 닫힘 여부).</summary>
    public sealed class StickerFigure
    {
        public double StartX { get; set; }
        public double StartY { get; set; }
        public bool Closed { get; set; }
        public List<StickerSegment> Segments { get; } = new List<StickerSegment>();
    }

    /// <summary>스티커의 칠한 경로 하나(그리는 순서대로): 채우기·선 색은 "#AARRGGBB"(없으면 null), 선 두께는 viewBox 단위.</summary>
    public sealed class StickerPath
    {
        public List<StickerFigure> Figures { get; } = new List<StickerFigure>();
        public string Fill { get; set; }
        public bool EvenOdd { get; set; }
        public string Stroke { get; set; }
        public double StrokeWidth { get; set; } = 1;
        /// <summary>선 끝: butt·round·square.</summary>
        public string LineCap { get; set; } = "butt";
        /// <summary>선 이음: miter·round·bevel.</summary>
        public string LineJoin { get; set; } = "miter";
        public double MiterLimit { get; set; } = 4;
    }

    /// <summary>읽은 스티커: viewBox(왼쪽 위·크기), 제목(svg 바로 아래 title — 기본 스티커의 이름), 경로들.</summary>
    public sealed class Sticker
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Title { get; set; }
        public List<StickerPath> Paths { get; } = new List<StickerPath>();
    }

    /// <summary>스티커 SVG 읽기 결과: 스티커(실패하면 null)와 알릴 오류, 지원하지 않아 무시한 것들(요소·참조 이름).</summary>
    public sealed class StickerParse
    {
        public Sticker Sticker { get; set; }
        public string Error { get; set; }
        public List<string> Ignored { get; } = new List<string>();
    }

    /// <summary>
    /// 스티커 SVG(제한된 SVG를 직접 읽음 — WPF에는 SVG 해석기가 없다). 사용자 SVG는 믿을 수 없는 입력으로 다룬다:
    /// - DTD는 무시하고(외부 DTD를 받지 않음, 엔티티 정의 없음 — 정의되지 않은 엔티티가 있으면 읽지 못함), 외부 자원을 읽는 해석기도 두지 않는다.
    /// - 읽는 것: svg·g·path·rect·circle·ellipse·line·polyline·polygon과 칠하기 속성(fill·stroke·불투명도·선 모양)·transform. style 속성의 같은 선언도.
    /// - 무시하는 것(알림): script·style·image·use·foreignObject·text·defs·그라데이션·a 등 그 밖의 요소와 그 안, href·url() 참조(url 칠하기는 대체 색, 없으면 회색).
    /// - 상한: 글 256KB, 요소 2,000개, 숫자 10만 개, 깊이 32, 좌표 ±100만 — 넘으면 오류로 거절한다.
    /// 결과는 경로만 남긴 깨끗한 SVG(<see cref="Write"/>)로 다시 써서 저장·프로젝트에 넣는다.
    /// </summary>
    public static class StickerSvg
    {
        public const int MaxChars = 256 * 1024;
        public const int MaxElements = 2000;
        public const int MaxNumbers = 100_000;
        public const int MaxDepth = 32;
        public const double MaxCoordinate = 1_000_000;
        public const int MaxTitle = 40;
        private const string SvgNamespace = "http://www.w3.org/2000/svg";

        private static readonly HashSet<string> Shapes = new HashSet<string>(StringComparer.Ordinal) { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon" };
        // 그리지 않지만 알리지도 않는 설명 요소
        private static readonly HashSet<string> Quiet = new HashSet<string>(StringComparer.Ordinal) { "title", "desc", "metadata" };

        private static readonly Dictionary<string, string> Named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = "000000", ["white"] = "FFFFFF", ["red"] = "FF0000", ["green"] = "008000", ["blue"] = "0000FF", ["yellow"] = "FFFF00",
            ["orange"] = "FFA500", ["purple"] = "800080", ["gray"] = "808080", ["grey"] = "808080", ["pink"] = "FFC0CB", ["brown"] = "A52A2A",
            ["cyan"] = "00FFFF", ["aqua"] = "00FFFF", ["magenta"] = "FF00FF", ["fuchsia"] = "FF00FF", ["lime"] = "00FF00", ["navy"] = "000080",
            ["teal"] = "008080", ["maroon"] = "800000", ["olive"] = "808000", ["silver"] = "C0C0C0", ["gold"] = "FFD700", ["skyblue"] = "87CEEB",
            ["darkgray"] = "A9A9A9", ["darkgrey"] = "A9A9A9", ["lightgray"] = "D3D3D3", ["lightgrey"] = "D3D3D3", ["crimson"] = "DC143C", ["tomato"] = "FF6347"
        };

        /// <summary>읽기 상태: 상속하는 칠하기 값과 변환.</summary>
        private sealed class Paint
        {
            public string Fill = "FF000000";
            public double FillOpacity = 1;
            public bool EvenOdd;
            public string Stroke;
            public double StrokeOpacity = 1;
            public double StrokeWidth = 1;
            public string LineCap = "butt";
            public string LineJoin = "miter";
            public double MiterLimit = 4;
            public string Color = "FF000000";
            public double Opacity = 1;
            public bool Hidden;
            public Matrix Transform = Matrix.Identity;

            public Paint Copy()
            {
                return (Paint)MemberwiseClone();
            }
        }

        /// <summary>2차원 아핀 변환(x' = a x + c y + e, y' = b x + d y + f).</summary>
        private readonly struct Matrix
        {
            public Matrix(double a, double b, double c, double d, double e, double f)
            {
                A = a;
                B = b;
                C = c;
                D = d;
                E = e;
                F = f;
            }

            public static Matrix Identity { get { return new Matrix(1, 0, 0, 1, 0, 0); } }
            public double A { get; }
            public double B { get; }
            public double C { get; }
            public double D { get; }
            public double E { get; }
            public double F { get; }

            /// <summary>this ∘ other(other를 먼저 적용).</summary>
            public Matrix Then(Matrix other)
            {
                return new Matrix(A * other.A + C * other.B, B * other.A + D * other.B, A * other.C + C * other.D, B * other.C + D * other.D,
                    A * other.E + C * other.F + E, B * other.E + D * other.F + F);
            }

            public (double X, double Y) Apply(double x, double y)
            {
                return (A * x + C * y + E, B * x + D * y + F);
            }

            public double Scale { get { return Math.Sqrt(Math.Abs(A * D - B * C)); } }
        }

        private sealed class ParseFailure : Exception
        {
            public ParseFailure(string message) : base(message)
            {
            }
        }

        /// <summary>읽기 진행: 상한 셈과 무시한 것들.</summary>
        private sealed class Budget
        {
            public int Elements;
            public int Numbers;
            public readonly List<string> Ignored = new List<string>();

            public void Note(string what)
            {
                if (!Ignored.Contains(what) && Ignored.Count < 20)
                    Ignored.Add(what);
            }

            public void CountNumber()
            {
                if (++Numbers > MaxNumbers)
                    throw new ParseFailure("SVG가 너무 복잡합니다(숫자 " + MaxNumbers.ToString("N0", CultureInfo.InvariantCulture) + "개 넘음).");
            }
        }

        /// <summary>SVG 글을 읽는다. 상한을 넘거나 그릴 것이 없으면 Error(사용자에게 보일 글)와 null 스티커.</summary>
        public static StickerParse Parse(string svg)
        {
            var result = new StickerParse();
            if (string.IsNullOrWhiteSpace(svg))
            {
                result.Error = "빈 파일입니다.";
                return result;
            }
            if (svg.Length > MaxChars)
            {
                result.Error = "SVG가 너무 큽니다(" + (MaxChars / 1024) + "KB 넘음).";
                return result;
            }
            var budget = new Budget();
            try
            {
                var settings = new XmlReaderSettings
                {
                    // DTD는 읽지 않고 건너뛴다(엔티티 정의·외부 DTD 없음), 외부 자원을 읽는 해석기도 없다
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 1024,
                    MaxCharactersInDocument = MaxChars * 2L,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    IgnoreWhitespace = true
                };
                using (var reader = XmlReader.Create(new StringReader(svg), settings))
                {
                    reader.MoveToContent();
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "svg" || !(reader.NamespaceURI == SvgNamespace || reader.NamespaceURI.Length == 0))
                        throw new ParseFailure("SVG 파일이 아닙니다(맨 바깥이 svg 요소가 아님).");
                    result.Sticker = ReadRoot(reader, budget);
                }
            }
            catch (ParseFailure e)
            {
                result.Sticker = null;
                result.Error = e.Message;
            }
            catch (XmlException e)
            {
                result.Sticker = null;
                result.Error = "SVG를 읽지 못했습니다: " + e.Message;
            }
            result.Ignored.AddRange(budget.Ignored);
            if (result.Sticker != null && result.Sticker.Paths.Count == 0)
            {
                result.Sticker = null;
                result.Error = "그릴 모양이 없습니다(경로·기본 도형만 읽습니다).";
            }
            return result;
        }

        private static Sticker ReadRoot(XmlReader reader, Budget budget)
        {
            var sticker = new Sticker();
            var attributes = Attributes(reader, budget);
            var box = Numbers(Get(attributes, "viewBox"), budget);
            if (box.Count == 4 && box[2] > 0 && box[3] > 0)
            {
                (sticker.MinX, sticker.MinY, sticker.Width, sticker.Height) = (box[0], box[1], box[2], box[3]);
            }
            else
            {
                var width = Length(Get(attributes, "width"));
                var height = Length(Get(attributes, "height"));
                if (!(width > 0) || !(height > 0))
                    throw new ParseFailure("viewBox나 크기(width·height)가 없는 SVG입니다.");
                (sticker.Width, sticker.Height) = (width, height);
            }
            if (sticker.Width > MaxCoordinate || sticker.Height > MaxCoordinate || Math.Abs(sticker.MinX) > MaxCoordinate || Math.Abs(sticker.MinY) > MaxCoordinate)
                throw new ParseFailure("SVG 크기가 너무 큽니다.");
            var paint = Apply(new Paint(), attributes, budget, false);
            if (reader.IsEmptyElement)
                return sticker;
            reader.Read();
            ReadChildren(reader, sticker, paint, budget, 1, true);
            return sticker;
        }

        /// <summary>자식들을 읽는다: 읽는 곳은 첫 자식(또는 부모의 끝 태그)이고, 부모의 끝 태그 다음에서 돌아온다.</summary>
        private static void ReadChildren(XmlReader reader, Sticker sticker, Paint parent, Budget budget, int depth, bool root)
        {
            if (depth > MaxDepth)
                throw new ParseFailure("SVG가 너무 깊게 겹쳐 있습니다(" + MaxDepth + "단계 넘음).");
            while (!reader.EOF)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.EndElement:
                        reader.Read();
                        return;
                    case XmlNodeType.EntityReference:
                        throw new ParseFailure("엔티티가 든 SVG는 읽지 않습니다.");
                    case XmlNodeType.Element:
                        ReadElement(reader, sticker, parent, budget, depth, root);
                        break;
                    default:
                        reader.Read();
                        break;
                }
            }
        }

        /// <summary>
        /// 요소 하나: g면 자식들을 같은 칠하기·변환으로, 도형이면 경로 하나, 맨 바깥 바로 아래 title은 제목, 그 밖은 건너뛰고 알린다.
        /// 읽는 곳은 요소 시작이고, 요소 끝 다음에서 돌아온다.
        /// </summary>
        private static void ReadElement(XmlReader reader, Sticker sticker, Paint parent, Budget budget, int depth, bool root)
        {
            if (++budget.Elements > MaxElements)
                throw new ParseFailure("SVG 요소가 너무 많습니다(" + MaxElements.ToString("N0", CultureInfo.InvariantCulture) + "개 넘음).");
            var name = reader.LocalName;
            var svgElement = reader.NamespaceURI == SvgNamespace || reader.NamespaceURI.Length == 0;
            if (svgElement && root && name == "title" && sticker.Title == null)
            {
                if (reader.IsEmptyElement)
                    reader.Skip();
                else
                    sticker.Title = CleanTitle(reader.ReadInnerXml());
                return;
            }
            if (!svgElement || name != "g" && !Shapes.Contains(name))
            {
                if (!svgElement || !Quiet.Contains(name))
                    budget.Note(name);
                reader.Skip();
                return;
            }
            var empty = reader.IsEmptyElement;
            var attributes = Attributes(reader, budget);
            var paint = Apply(parent.Copy(), attributes, budget, true);
            var hidden = string.Equals(Get(attributes, "display")?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
            if (name == "g")
            {
                if (empty || hidden)
                {
                    reader.Skip();
                    return;
                }
                reader.Read();
                ReadChildren(reader, sticker, paint, budget, depth + 1, false);
                return;
            }
            // 도형의 자식(title·animate 등)은 쓰지 않는다
            reader.Skip();
            if (hidden || paint.Hidden)
                return;
            var figures = Shape(name, attributes, budget);
            if (figures.Count == 0)
                return;
            var path = new StickerPath
            {
                Fill = Color(paint.Fill, paint.FillOpacity * paint.Opacity),
                EvenOdd = paint.EvenOdd,
                Stroke = paint.Stroke == null || !(paint.StrokeWidth > 0) ? null : Color(paint.Stroke, paint.StrokeOpacity * paint.Opacity),
                StrokeWidth = Math.Min(MaxCoordinate, paint.StrokeWidth * paint.Transform.Scale),
                LineCap = paint.LineCap,
                LineJoin = paint.LineJoin,
                MiterLimit = paint.MiterLimit
            };
            // 선(line)은 채우지 않는다
            if (name == "line")
                path.Fill = null;
            if (path.Fill == null && path.Stroke == null)
                return;
            foreach (var figure in figures)
                path.Figures.Add(Transform(figure, paint.Transform));
            sticker.Paths.Add(path);
        }

        private static string CleanTitle(string inner)
        {
            // 제목 안의 태그는 버리고 글만(엔티티는 기본 다섯 개만 — XmlReader가 이미 풀었다)
            var text = new StringBuilder();
            var tag = false;
            foreach (var ch in inner ?? "")
            {
                if (ch == '<')
                    tag = true;
                else if (ch == '>')
                    tag = false;
                else if (!tag && !char.IsControl(ch))
                    text.Append(ch);
            }
            var clean = text.ToString().Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&").Trim();
            if (clean.Length == 0)
                return null;
            return clean.Length > MaxTitle ? clean.Substring(0, MaxTitle) : clean;
        }

        // ---------- 속성·칠하기 ----------

        /// <summary>요소의 속성(이름은 로컬 이름 — 이름공간이 있는 href 등은 참조라 적어 두고 버린다). style 속성은 같은 이름의 선언으로 덮어쓴다.</summary>
        private static Dictionary<string, string> Attributes(XmlReader reader, Budget budget)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            string style = null;
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    var local = reader.LocalName;
                    if (local == "href" || reader.Prefix == "xlink")
                    {
                        budget.Note("href");
                        continue;
                    }
                    if (reader.Prefix.Length > 0 || reader.NamespaceURI.Length > 0)
                        continue;
                    if (local == "style")
                        style = reader.Value;
                    else
                        attributes[local] = reader.Value;
                }
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            if (style != null)
            {
                foreach (var declaration in style.Split(';'))
                {
                    var colon = declaration.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var key = declaration.Substring(0, colon).Trim();
                    var value = declaration.Substring(colon + 1).Replace("!important", "").Trim();
                    if (key.Length > 0)
                        attributes[key] = value;
                }
            }
            return attributes;
        }

        private static string Get(Dictionary<string, string> attributes, string name)
        {
            return attributes.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>칠하기 속성을 상속값에 적용한다. element면 transform·opacity도(svg 맨 바깥은 transform을 쓰지 않음).</summary>
        private static Paint Apply(Paint paint, Dictionary<string, string> attributes, Budget budget, bool element)
        {
            var color = Get(attributes, "color");
            if (color != null && ParseColor(color, paint.Color, budget) is string current)
                paint.Color = current;
            var fill = Get(attributes, "fill");
            if (fill != null)
                paint.Fill = PaintValue(fill, paint.Fill, paint.Color, budget);
            var stroke = Get(attributes, "stroke");
            if (stroke != null)
                paint.Stroke = PaintValue(stroke, paint.Stroke, paint.Color, budget);
            paint.FillOpacity = Fraction(Get(attributes, "fill-opacity"), paint.FillOpacity);
            paint.StrokeOpacity = Fraction(Get(attributes, "stroke-opacity"), paint.StrokeOpacity);
            var rule = Get(attributes, "fill-rule")?.Trim();
            if (rule == "evenodd")
                paint.EvenOdd = true;
            else if (rule == "nonzero")
                paint.EvenOdd = false;
            var width = Length(Get(attributes, "stroke-width"));
            if (width >= 0 && double.IsFinite(width))
                paint.StrokeWidth = Math.Min(MaxCoordinate, width);
            var cap = Get(attributes, "stroke-linecap")?.Trim();
            if (cap == "butt" || cap == "round" || cap == "square")
                paint.LineCap = cap;
            var join = Get(attributes, "stroke-linejoin")?.Trim();
            if (join == "miter" || join == "round" || join == "bevel")
                paint.LineJoin = join;
            var miter = Length(Get(attributes, "stroke-miterlimit"));
            if (miter >= 1 && miter <= 100)
                paint.MiterLimit = miter;
            var visibility = Get(attributes, "visibility")?.Trim();
            if (visibility == "hidden" || visibility == "collapse")
                paint.Hidden = true;
            else if (visibility == "visible")
                paint.Hidden = false;
            if (element)
            {
                paint.Opacity *= Fraction(Get(attributes, "opacity"), 1);
                var transform = Get(attributes, "transform");
                if (transform != null)
                    paint.Transform = paint.Transform.Then(ParseTransform(transform, budget));
            }
            return paint;
        }

        /// <summary>fill·stroke 값: none → null, currentColor → color 값, url(…) 참조는 버리고 대체 색(없으면 회색), 색은 "AARRGGBB".</summary>
        private static string PaintValue(string value, string inherited, string current, Budget budget)
        {
            var text = value.Trim();
            if (text == "inherit")
                return inherited;
            if (text == "none" || text == "transparent")
                return null;
            if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                budget.Note("url()");
                var close = text.IndexOf(')');
                var fallback = close >= 0 ? text.Substring(close + 1).Trim() : "";
                if (fallback == "none")
                    return null;
                return fallback.Length > 0 ? ParseColor(fallback, "FF808080", budget) : "FF808080";
            }
            if (text == "currentColor")
                return current;
            return ParseColor(text, inherited, budget);
        }

        /// <summary>색 글 → "AARRGGBB": #rgb·#rgba·#rrggbb·#rrggbbaa, rgb()/rgba(), 이름(자주 쓰는 것). 모르면 fallback(알림).</summary>
        private static string ParseColor(string value, string fallback, Budget budget)
        {
            var text = value.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                var hex = text.Substring(1);
                if (hex.Length > 0 && hex.All(Uri.IsHexDigit))
                {
                    if (hex.Length == 3 || hex.Length == 4)
                        hex = string.Concat(hex.Select(c => new string(c, 2)));
                    if (hex.Length == 6)
                        return "FF" + hex.ToUpperInvariant();
                    if (hex.Length == 8)
                        return (hex.Substring(6, 2) + hex.Substring(0, 6)).ToUpperInvariant();
                }
            }
            else if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var open = text.IndexOf('(');
                var close = text.IndexOf(')');
                if (open > 0 && close > open)
                {
                    var parts = text.Substring(open + 1, close - open - 1).Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        int Channel(string part)
                        {
                            var percent = part.EndsWith("%", StringComparison.Ordinal);
                            double.TryParse(part.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                            var channel = percent ? number * 2.55 : number;
                            return (int)Math.Round(Math.Max(0, Math.Min(255, double.IsFinite(channel) ? channel : 0)));
                        }
                        var alpha = parts.Length >= 4 ? Fraction(parts[3], 1) : 1;
                        return ((int)Math.Round(alpha * 255)).ToString("X2", CultureInfo.InvariantCulture) + Channel(parts[0]).ToString("X2", CultureInfo.InvariantCulture)
                            + Channel(parts[1]).ToString("X2", CultureInfo.InvariantCulture) + Channel(parts[2]).ToString("X2", CultureInfo.InvariantCulture);
                    }
                }
            }
            else if (Named.TryGetValue(text, out var named))
            {
                return "FF" + named;
            }
            budget.Note("색 '" + (text.Length > 20 ? text.Substring(0, 20) : text) + "'");
            return fallback;
        }

        /// <summary>"AARRGGBB"에 불투명도를 곱해 "#AARRGGBB"로. 완전히 투명하면 null(칠하지 않음).</summary>
        private static string Color(string color, double opacity)
        {
            if (color == null)
                return null;
            var alpha = Convert.ToInt32(color.Substring(0, 2), 16) * Math.Max(0, Math.Min(1, opacity));
            var a = (int)Math.Round(alpha);
            return a <= 0 ? null : "#" + a.ToString("X2", CultureInfo.InvariantCulture) + color.Substring(2);
        }

        /// <summary>0~1 값(숫자 또는 %). 읽지 못하면 fallback.</summary>
        private static double Fraction(string value, double fallback)
        {
            if (value == null)
                return fallback;
            var text = value.Trim();
            var percent = text.EndsWith("%", StringComparison.Ordinal);
            if (!double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                return fallback;
            return Math.Max(0, Math.Min(1, percent ? number / 100 : number));
        }

        /// <summary>길이(앞의 숫자만 — 단위는 무시). 없거나 읽지 못하면 NaN.</summary>
        private static double Length(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return double.NaN;
            var text = value.Trim();
            var i = 0;
            return TryNumber(text, ref i, out var number) && double.IsFinite(number) ? number : double.NaN;
        }

        // ---------- 숫자·변환 ----------

        private static bool IsDigit(char ch)
        {
            return ch >= '0' && ch <= '9';
        }

        private static void SkipSeparators(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
            if (i < s.Length && s[i] == ',')
            {
                i++;
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
            }
        }

        /// <summary>SVG 숫자 하나("-1.5e3", ".5" — "1.5.5"는 1.5와 .5). 숫자가 아니면 false(i는 그대로).</summary>
        private static bool TryNumber(string s, ref int i, out double value)
        {
            value = 0;
            var save = i;
            SkipSeparators(s, ref i);
            var start = i;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                i++;
            var digits = false;
            while (i < s.Length && IsDigit(s[i]))
            {
                i++;
                digits = true;
            }
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && IsDigit(s[i]))
                {
                    i++;
                    digits = true;
                }
            }
            if (!digits)
            {
                i = save;
                return false;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                var mark = i;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                    i++;
                if (i < s.Length && IsDigit(s[i]))
                {
                    while (i < s.Length && IsDigit(s[i]))
                        i++;
                }
                else
                {
                    i = mark;
                }
            }
            return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>숫자 목록(viewBox·points).</summary>
        private static List<double> Numbers(string value, Budget budget)
        {
            var result = new List<double>();
            if (value == null)
                return result;
            var i = 0;
            while (TryNumber(value, ref i, out var number))
            {
                budget.CountNumber();
                result.Add(Checked(number));
            }
            return result;
        }

        private static double Checked(double value)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > MaxCoordinate)
                throw new ParseFailure("SVG 좌표가 너무 큽니다(±" + MaxCoordinate.ToString("N0", CultureInfo.InvariantCulture) + " 넘음).");
            return value;
        }

        /// <summary>transform 목록(matrix·translate·scale·rotate·skewX·skewY — 왼쪽부터 곱함). 모르는 것은 그 뒤를 버린다(알림).</summary>
        private static Matrix ParseTransform(string value, Budget budget)
        {
            var result = Matrix.Identity;
            var i = 0;
            while (i < value.Length)
            {
                while (i < value.Length && (char.IsWhiteSpace(value[i]) || value[i] == ','))
                    i++;
                if (i >= value.Length)
                    break;
                var start = i;
                while (i < value.Length && char.IsLetter(value[i]))
                    i++;
                var name = value.Substring(start, i - start);
                while (i < value.Length && char.IsWhiteSpace(value[i]))
                    i++;
                if (i >= value.Length || value[i] != '(')
                {
                    budget.Note("transform");
                    break;
                }
                i++;
                var close = value.IndexOf(')', i);
                if (close < 0)
                {
                    budget.Note("transform");
                    break;
                }
                var args = Numbers(value.Substring(i, close - i), budget);
                i = close + 1;
                Matrix step;
                switch (name)
                {
                    case "matrix" when args.Count == 6:
                        step = new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                        break;
                    case "translate" when args.Count == 1 || args.Count == 2:
                        step = new Matrix(1, 0, 0, 1, args[0], args.Count == 2 ? args[1] : 0);
                        break;
                    case "scale" when args.Count == 1 || args.Count == 2:
                        step = new Matrix(args[0], 0, 0, args.Count == 2 ? args[1] : args[0], 0, 0);
                        break;
                    case "rotate" when args.Count == 1 || args.Count == 3:
                    {
                        var radians = args[0] * Math.PI / 180;
                        double cos = Math.Cos(radians), sin = Math.Sin(radians);
                        step = new Matrix(cos, sin, -sin, cos, 0, 0);
                        if (args.Count == 3)
                            step = new Matrix(1, 0, 0, 1, args[1], args[2]).Then(step).Then(new Matrix(1, 0, 0, 1, -args[1], -args[2]));
                        break;
                    }
                    case "skewX" when args.Count == 1:
                        step = new Matrix(1, 0, Math.Tan(args[0] * Math.PI / 180), 1, 0, 0);
                        break;
                    case "skewY" when args.Count == 1:
                        step = new Matrix(1, Math.Tan(args[0] * Math.PI / 180), 0, 1, 0, 0);
                        break;
                    default:
                        budget.Note("transform");
                        return result;
                }
                result = result.Then(step);
            }
            return result;
        }

        private static StickerFigure Transform(StickerFigure figure, Matrix matrix)
        {
            var (sx, sy) = matrix.Apply(figure.StartX, figure.StartY);
            var result = new StickerFigure { StartX = Checked(sx), StartY = Checked(sy), Closed = figure.Closed };
            foreach (var segment in figure.Segments)
            {
                var (x1, y1) = matrix.Apply(segment.X1, segment.Y1);
                var (x2, y2) = matrix.Apply(segment.X2, segment.Y2);
                var (x, y) = matrix.Apply(segment.X, segment.Y);
                result.Segments.Add(new StickerSegment(segment.Cubic, Checked(x1), Checked(y1), Checked(x2), Checked(y2), Checked(x), Checked(y)));
            }
            return result;
        }

        // ---------- 도형 ----------

        private static double Attribute(Dictionary<string, string> attributes, string name, Budget budget)
        {
            var value = Length(Get(attributes, name));
            if (double.IsNaN(value))
                return 0;
            budget.CountNumber();
            return Checked(value);
        }

        private const double Kappa = 0.5522847498307936;

        /// <summary>도형 하나의 그림들(변환 전 좌표).</summary>
        private static List<StickerFigure> Shape(string name, Dictionary<string, string> attributes, Budget budget)
        {
            var figures = new List<StickerFigure>();
            switch (name)
            {
                case "path":
                    PathData(Get(attributes, "d") ?? "", figures, budget);
                    break;
                case "rect":
                {
                    double x = Attribute(attributes, "x", budget), y = Attribute(attributes, "y", budget);
                    double w = Attribute(attributes, "width", budget), h = Attribute(attributes, "height", budget);
                    if (!(w > 0) || !(h > 0))
                        break;
                    var rx = Attribute(attributes, "rx", budget);
                    var ry = Attribute(attributes, "ry", budget);
                    if (!attributes.ContainsKey("rx"))
                        rx = ry;
                    if (!attributes.ContainsKey("ry"))
                        ry = rx;
                    rx = Math.Max(0, Math.Min(rx, w / 2));
                    ry = Math.Max(0, Math.Min(ry, h / 2));
                    var figure = new StickerFigure { StartX = x + rx, StartY = y, Closed = true };
                    void LineTo(double px, double py) => figure.Segments.Add(new StickerSegment(false, 0, 0, 0, 0, px, py));
                    void Corner(double fromX, double fromY, double cornerX, double cornerY, double toX, double toY) =>
                        figure.Segments.Add(new StickerSegment(true, fromX + (cornerX - fromX) * Kappa, fromY + (cornerY - fromY) * Kappa,
                            toX + (cornerX - toX) * Kappa, toY + (cornerY - toY) * Kappa, toX, toY));
                    LineTo(x + w - rx, y);
                    if (rx > 0 && ry > 0)
                        Corner(x + w - rx, y, x + w, y, x + w, y + ry);
                    LineTo(x + w, y + h - ry);
                    if (rx > 0 && ry > 0)
                        Corner(x + w, y + h - ry, x + w, y + h, x + w - rx, y + h);
                    LineTo(x + rx, y + h);
                    if (rx > 0 && ry > 0)
                        Corner(x + rx, y + h, x, y + h, x, y + h - ry);
                    LineTo(x, y + ry);
                    if (rx > 0 && ry > 0)
                        Corner(x, y + ry, x, y, x + rx, y);
                    figures.Add(figure);
                    break;
                }
                case "circle":
                case "ellipse":
                {
                    double cx = Attribute(attributes, "cx", budget), cy = Attribute(attributes, "cy", budget);
                    double rx, ry;
                    if (name == "circle")
                        rx = ry = Attribute(attributes, "r", budget);
                    else
                    {
                        rx = Attribute(attributes, "rx", budget);
                        ry = Attribute(attributes, "ry", budget);
                    }
                    if (!(rx > 0) || !(ry > 0))
                        break;
                    figures.Add(Ellipse(cx, cy, rx, ry));
                    break;
                }
                case "line":
                {
                    var figure = new StickerFigure { StartX = Attribute(attributes, "x1", budget), StartY = Attribute(attributes, "y1", budget) };
                    figure.Segments.Add(new StickerSegment(false, 0, 0, 0, 0, Attribute(attributes, "x2", budget), Attribute(attributes, "y2", budget)));
                    figures.Add(figure);
                    break;
                }
                case "polyline":
                case "polygon":
                {
                    var points = Numbers(Get(attributes, "points"), budget);
                    if (points.Count < 4)
                        break;
                    var figure = new StickerFigure { StartX = points[0], StartY = points[1], Closed = name == "polygon" };
                    for (var i = 2; i + 1 < points.Count; i += 2)
                        figure.Segments.Add(new StickerSegment(false, 0, 0, 0, 0, points[i], points[i + 1]));
                    figures.Add(figure);
                    break;
                }
            }
            return figures;
        }

        private static StickerFigure Ellipse(double cx, double cy, double rx, double ry)
        {
            var figure = new StickerFigure { StartX = cx + rx, StartY = cy, Closed = true };
            double kx = rx * Kappa, ky = ry * Kappa;
            figure.Segments.Add(new StickerSegment(true, cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry));
            figure.Segments.Add(new StickerSegment(true, cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy));
            figure.Segments.Add(new StickerSegment(true, cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry));
            figure.Segments.Add(new StickerSegment(true, cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy));
            return figure;
        }

        /// <summary>
        /// 경로 데이터(M L H V C S Q T A Z, 소문자는 상대)를 그림들로 — 2차 베지어·호는 3차 베지어로 바꾼다. 잘못된 곳을 만나면 거기까지만 쓴다(브라우저와 같음).
        /// </summary>
        private static void PathData(string d, List<StickerFigure> figures, Budget budget)
        {
            var i = 0;
            var command = '\0';
            double x = 0, y = 0, startX = 0, startY = 0;
            // 앞 조각의 둘째 조절점(S·T에서 비춤)
            double lastControlX = 0, lastControlY = 0;
            var lastCubic = false;
            var lastQuad = false;
            StickerFigure figure = null;
            bool Number(out double value)
            {
                if (!TryNumber(d, ref i, out value))
                    return false;
                budget.CountNumber();
                value = Checked(value);
                return true;
            }
            bool Flag(out bool value)
            {
                value = false;
                SkipSeparators(d, ref i);
                if (i >= d.Length || d[i] != '0' && d[i] != '1')
                    return false;
                value = d[i] == '1';
                i++;
                budget.CountNumber();
                return true;
            }
            StickerFigure Current()
            {
                if (figure == null)
                {
                    figure = new StickerFigure { StartX = x, StartY = y };
                    figures.Add(figure);
                }
                return figure;
            }
            void Line(double toX, double toY)
            {
                Current().Segments.Add(new StickerSegment(false, 0, 0, 0, 0, toX, toY));
                x = toX;
                y = toY;
            }
            void Cubic(double x1, double y1, double x2, double y2, double toX, double toY)
            {
                Current().Segments.Add(new StickerSegment(true, x1, y1, x2, y2, toX, toY));
                x = toX;
                y = toY;
            }
            while (true)
            {
                while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ','))
                    i++;
                if (i >= d.Length)
                    break;
                var ch = d[i];
                if (char.IsLetter(ch))
                {
                    if ("MmLlHhVvCcSsQqTtAaZz".IndexOf(ch) < 0)
                        return;
                    command = ch;
                    i++;
                }
                else if (command == '\0' || command == 'Z' || command == 'z')
                {
                    return;
                }
                var relative = char.IsLower(command);
                double ox = relative ? x : 0, oy = relative ? y : 0;
                var cubic = false;
                var quad = false;
                switch (char.ToUpperInvariant(command))
                {
                    case 'M':
                    {
                        if (!Number(out var mx) || !Number(out var my))
                            return;
                        x = startX = ox + mx;
                        y = startY = oy + my;
                        figure = null;
                        // 뒤따르는 좌표 쌍은 L·l
                        command = relative ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                    {
                        if (!Number(out var lx) || !Number(out var ly))
                            return;
                        Line(ox + lx, oy + ly);
                        break;
                    }
                    case 'H':
                    {
                        if (!Number(out var hx))
                            return;
                        Line(ox + hx, y);
                        break;
                    }
                    case 'V':
                    {
                        if (!Number(out var vy))
                            return;
                        Line(x, oy + vy);
                        break;
                    }
                    case 'C':
                    {
                        if (!Number(out var x1) || !Number(out var y1) || !Number(out var x2) || !Number(out var y2) || !Number(out var cx) || !Number(out var cy))
                            return;
                        Cubic(ox + x1, oy + y1, ox + x2, oy + y2, ox + cx, oy + cy);
                        (lastControlX, lastControlY, cubic) = (ox + x2, oy + y2, true);
                        break;
                    }
                    case 'S':
                    {
                        if (!Number(out var x2) || !Number(out var y2) || !Number(out var sx) || !Number(out var sy))
                            return;
                        double x1 = lastCubic ? 2 * x - lastControlX : x, y1 = lastCubic ? 2 * y - lastControlY : y;
                        Cubic(x1, y1, ox + x2, oy + y2, ox + sx, oy + sy);
                        (lastControlX, lastControlY, cubic) = (ox + x2, oy + y2, true);
                        break;
                    }
                    case 'Q':
                    {
                        if (!Number(out var qx) || !Number(out var qy) || !Number(out var tx) || !Number(out var ty))
                            return;
                        Quadratic(ox + qx, oy + qy, ox + tx, oy + ty);
                        (lastControlX, lastControlY, quad) = (ox + qx, oy + qy, true);
                        break;
                    }
                    case 'T':
                    {
                        if (!Number(out var tx) || !Number(out var ty))
                            return;
                        double qx = lastQuad ? 2 * x - lastControlX : x, qy = lastQuad ? 2 * y - lastControlY : y;
                        Quadratic(qx, qy, ox + tx, oy + ty);
                        (lastControlX, lastControlY, quad) = (qx, qy, true);
                        break;
                    }
                    case 'A':
                    {
                        if (!Number(out var rx) || !Number(out var ry) || !Number(out var angle) || !Flag(out var large) || !Flag(out var sweep)
                            || !Number(out var ax) || !Number(out var ay))
                            return;
                        Arc(x, y, rx, ry, angle, large, sweep, ox + ax, oy + ay);
                        break;
                    }
                    case 'Z':
                    {
                        if (figure != null)
                            figure.Closed = true;
                        x = startX;
                        y = startY;
                        figure = null;
                        break;
                    }
                }
                lastCubic = cubic;
                lastQuad = quad;
            }

            void Quadratic(double qx, double qy, double toX, double toY)
            {
                Cubic(x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), toX + 2.0 / 3 * (qx - toX), toY + 2.0 / 3 * (qy - toY), toX, toY);
            }

            // SVG 호(끝점 표기) → 가운데 표기 → 90° 이하 조각마다 3차 베지어(SVG 구현 안내 F.6)
            void Arc(double x1, double y1, double rx, double ry, double degrees, bool large, bool sweep, double x2, double y2)
            {
                if (x1 == x2 && y1 == y2)
                    return;
                rx = Math.Abs(rx);
                ry = Math.Abs(ry);
                if (rx < 1e-12 || ry < 1e-12)
                {
                    Line(x2, y2);
                    return;
                }
                var phi = degrees * Math.PI / 180;
                double cos = Math.Cos(phi), sin = Math.Sin(phi);
                double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
                double px = cos * dx + sin * dy, py = -sin * dx + cos * dy;
                var lambda = px * px / (rx * rx) + py * py / (ry * ry);
                if (lambda > 1)
                {
                    var root = Math.Sqrt(lambda);
                    rx *= root;
                    ry *= root;
                }
                var numerator = rx * rx * ry * ry - rx * rx * py * py - ry * ry * px * px;
                var denominator = rx * rx * py * py + ry * ry * px * px;
                var coefficient = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, denominator > 0 ? numerator / denominator : 0));
                double cxp = coefficient * rx * py / ry, cyp = -coefficient * ry * px / rx;
                double cx = cos * cxp - sin * cyp + (x1 + x2) / 2, cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
                double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
                var theta = Angle(1, 0, (px - cxp) / rx, (py - cyp) / ry);
                var delta = Angle((px - cxp) / rx, (py - cyp) / ry, (-px - cxp) / rx, (-py - cyp) / ry);
                if (!sweep && delta > 0)
                    delta -= 2 * Math.PI;
                else if (sweep && delta < 0)
                    delta += 2 * Math.PI;
                var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
                var step = delta / count;
                var t = 4.0 / 3 * Math.Tan(step / 4);
                (double X, double Y) Point(double a) => (cx + rx * Math.Cos(a) * cos - ry * Math.Sin(a) * sin, cy + rx * Math.Cos(a) * sin + ry * Math.Sin(a) * cos);
                (double X, double Y) Tangent(double a) => (-rx * Math.Sin(a) * cos - ry * Math.Cos(a) * sin, -rx * Math.Sin(a) * sin + ry * Math.Cos(a) * cos);
                for (var k = 0; k < count; k++)
                {
                    var a1 = theta + k * step;
                    var a2 = a1 + step;
                    var p1 = Point(a1);
                    var p2 = k == count - 1 ? (x2, y2) : Point(a2);
                    var d1 = Tangent(a1);
                    var d2 = Tangent(a2);
                    Cubic(p1.X + t * d1.X, p1.Y + t * d1.Y, p2.Item1 - t * d2.X, p2.Item2 - t * d2.Y, p2.Item1, p2.Item2);
                }
            }
        }

        // ---------- 쓰기·해시 ----------

        /// <summary>
        /// 읽은 스티커를 경로만 남긴 깨끗한 SVG로 쓴다(viewBox, 제목, path마다 d·채우기·선) — 내 스티커 파일과 프로젝트의 아트 클립에 넣는 글.
        /// 다시 읽으면 같은 스티커다(숫자는 소수 셋째 자리까지).
        /// </summary>
        public static string Write(Sticker sticker)
        {
            var text = new StringBuilder();
            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false, Encoding = new UTF8Encoding(false) };
            using (var writer = XmlWriter.Create(text, settings))
            {
                writer.WriteStartElement("svg", SvgNamespace);
                writer.WriteAttributeString("viewBox", string.Join(" ", new[] { sticker.MinX, sticker.MinY, sticker.Width, sticker.Height }.Select(N)));
                if (!string.IsNullOrEmpty(sticker.Title))
                    writer.WriteElementString("title", SvgNamespace, sticker.Title);
                foreach (var path in sticker.Paths)
                {
                    writer.WriteStartElement("path", SvgNamespace);
                    var d = new StringBuilder();
                    foreach (var figure in path.Figures)
                    {
                        d.Append('M').Append(N(figure.StartX)).Append(' ').Append(N(figure.StartY));
                        foreach (var s in figure.Segments)
                        {
                            if (s.Cubic)
                                d.Append('C').Append(N(s.X1)).Append(' ').Append(N(s.Y1)).Append(' ').Append(N(s.X2)).Append(' ').Append(N(s.Y2)).Append(' ');
                            else
                                d.Append('L');
                            d.Append(N(s.X)).Append(' ').Append(N(s.Y));
                        }
                        if (figure.Closed)
                            d.Append('Z');
                    }
                    writer.WriteAttributeString("d", d.ToString());
                    WritePaint(writer, "fill", path.Fill ?? "none");
                    if (path.EvenOdd)
                        writer.WriteAttributeString("fill-rule", "evenodd");
                    if (path.Stroke != null)
                    {
                        WritePaint(writer, "stroke", path.Stroke);
                        writer.WriteAttributeString("stroke-width", N(path.StrokeWidth));
                        if (path.LineCap != "butt")
                            writer.WriteAttributeString("stroke-linecap", path.LineCap);
                        if (path.LineJoin != "miter")
                            writer.WriteAttributeString("stroke-linejoin", path.LineJoin);
                        if (path.MiterLimit != 4)
                            writer.WriteAttributeString("stroke-miterlimit", N(path.MiterLimit));
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            return text.ToString();
        }

        private static void WritePaint(XmlWriter writer, string name, string color)
        {
            if (color == "none")
            {
                writer.WriteAttributeString(name, "none");
                return;
            }
            writer.WriteAttributeString(name, "#" + color.Substring(3));
            var alpha = Convert.ToInt32(color.Substring(1, 2), 16);
            if (alpha < 255)
                writer.WriteAttributeString(name + "-opacity", N(alpha / 255.0));
        }

        private static string N(double value)
        {
            var text = Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        /// <summary>SVG 글의 SHA-256(소문자 16진 64자) — 그림 캐시 키·파일 이름에.</summary>
        public static string Hash(string svg)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(svg ?? ""))).ToLowerInvariant();
        }

        private static readonly ConcurrentDictionary<string, Sticker> Parsed = new ConcurrentDictionary<string, Sticker>(StringComparer.Ordinal);

        /// <summary>같은 글은 한 번만 읽는다(그리개·화면이 여러 번 부름). 읽지 못하면 null. 너무 쌓이면 비운다.</summary>
        public static Sticker Cached(string svg)
        {
            if (string.IsNullOrEmpty(svg) || svg.Length > MaxChars)
                return null;
            var key = Hash(svg);
            if (Parsed.TryGetValue(key, out var known))
                return known;
            var sticker = Parse(svg).Sticker;
            if (Parsed.Count >= 64)
                Parsed.Clear();
            Parsed[key] = sticker;
            return sticker;
        }
    }
}
