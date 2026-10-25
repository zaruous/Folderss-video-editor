using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>
    /// 내 스타일: 사용자가 이름을 붙여 저장한 꾸민 글자 스타일 목록 ↔ JSON(플러그인 설정 textStyles에 둔다).
    /// 설정 파일은 깨졌거나 남이 고친 것일 수 있으므로 읽을 때 예외 없이 규칙에 맞는 것만 받는다:
    /// 잘못된 JSON·너무 긴 글·너무 깊은 중첩이면 빈 목록, 앞에서부터 50개까지, 이름은 <see cref="CleanName"/>, 이름이 겹치면(대소문자 무시) 앞의 것,
    /// 스타일이 없거나 형식이 맞지 않는 항목은 건너뛰고, 스타일마다 <see cref="TextStyle.Normalize"/>. 쓸 때도 같은 규칙을 거친다.
    /// 모르는 열거 이름은 프로젝트 파일의 맞춤 방식처럼 첫 값(<see cref="LenientEnumConverter{T}"/>), "NaN"·"Infinity" 글은 받아서 정규화가 기본값으로 바꾼다.
    /// </summary>
    public static class TextStyleLibrary
    {
        public const int MaxCount = 50;
        public const int MaxNameLength = 40;
        /// <summary>
        /// 읽을 설정 글 길이 상한(UTF-16 단위). 50개를 가장 길게 써도(이름·글꼴 이름이 모두 \uXXXX로 바뀌어도) 10만 자가 안 되어 넉넉하다 —
        /// 넘으면 읽지 않는다(깨진 설정이 화면을 멈추지 않게).
        /// </summary>
        public const int MaxJsonLength = 256 * 1024;
        // 실제 깊이는 5(목록 → 항목 → 스타일 → 채우기 → 색 목록)
        private const int MaxDepth = 16;

        public const string NameRule = "이름은 1~40자로 적으세요. 줄 바꿈·제어 문자·보이지 않는 서식 문자(글 방향 바꾸기·폭 없는 공백 등)는 쓸 수 없습니다.";
        public const string FullMessage = "내 스타일은 50개까지 저장할 수 있습니다. 쓰지 않는 스타일을 오른쪽 클릭 [지우기]로 지운 뒤 다시 저장하세요.";

        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { MaxDepth = MaxDepth, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            options.Converters.Add(new LenientEnumConverter<TextAlign>());
            options.Converters.Add(new LenientEnumConverter<FillKind>());
            options.Converters.Add(new LenientEnumConverter<LineJoinKind>());
            options.Converters.Add(new LenientEnumConverter<WarpKind>());
            return options;
        }

        // 설정에 쓰는 항목 하나({"Name": …, "Style": {…}})
        private sealed class Entry
        {
            public string Name { get; set; }
            public TextStyle Style { get; set; }
        }

        /// <summary>설정 글을 읽는다. 받은 스타일은 새 객체(정규화함). 예외를 내지 않는다.</summary>
        public static List<(string Name, TextStyle Style)> Read(string json)
        {
            var styles = new List<(string Name, TextStyle Style)>();
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
                return styles;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            {
                // 깨진 JSON·너무 깊은 중첩·짝 없는 서로게이트
                return styles;
            }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return styles;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (styles.Count >= MaxCount)
                        break;
                    // 이름을 먼저 본다 — 겹친 이름은 스타일을 읽지도 않는다
                    var name = ReadName(element);
                    if (name == null || IndexOf(styles, name) >= 0)
                        continue;
                    var style = ReadStyle(element);
                    if (style != null)
                        styles.Add((name, style));
                }
            }
            return styles;
        }

        private static string ReadName(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String)
                return null;
            try
            {
                return CleanName(name.GetString());
            }
            catch (InvalidOperationException)
            {
                // \ud800처럼 짝 없는 서로게이트를 적은 글
                return null;
            }
        }

        private static TextStyle ReadStyle(JsonElement element)
        {
            if (!element.TryGetProperty("Style", out var value) || value.ValueKind != JsonValueKind.Object)
                return null;
            try
            {
                var style = value.Deserialize<TextStyle>(Options);
                style?.Normalize();
                return style;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is NotSupportedException || ex is FormatException || ex is ArgumentException)
            {
                // 형식이 맞지 않는 값(굵게 자리에 글 등) — 이 항목만 건너뛴다
                return null;
            }
        }

        /// <summary>목록을 설정 글로 쓴다. 읽을 때와 같은 규칙(이름·겹침·50개)을 거친 정규화한 사본을 쓰고, 받은 스타일은 고치지 않는다.</summary>
        public static string Write(IEnumerable<(string Name, TextStyle Style)> styles)
        {
            var entries = new List<Entry>();
            foreach (var (name, style) in styles ?? Enumerable.Empty<(string Name, TextStyle Style)>())
            {
                if (entries.Count >= MaxCount)
                    break;
                var clean = CleanName(name);
                if (clean == null || style == null || entries.Any(e => SameName(e.Name, clean)))
                    continue;
                entries.Add(new Entry { Name = clean, Style = Normalized(style) });
            }
            return JsonSerializer.Serialize(entries, Options);
        }

        /// <summary>
        /// 이름으로 넣는다: 같은 이름(대소문자 무시)이 있으면 그 자리의 스타일을 바꾸고(이름은 새로 적은 대로), 없으면 끝에 더한다.
        /// 넣는 것은 정규화한 깊은 복사다(클립의 스타일을 같이 쓰지 않게). 이름이 규칙에 맞지 않거나 50개가 차서 더할 수 없으면 알릴 글, 넣었으면 null.
        /// </summary>
        public static string AddOrReplace(List<(string Name, TextStyle Style)> styles, string name, TextStyle style)
        {
            if (styles == null)
                throw new ArgumentNullException(nameof(styles));
            if (style == null)
                throw new ArgumentNullException(nameof(style));
            var clean = CleanName(name);
            if (clean == null)
                return NameRule;
            var index = IndexOf(styles, clean);
            if (index >= 0)
            {
                styles[index] = (clean, Normalized(style));
                return null;
            }
            if (styles.Count >= MaxCount)
                return FullMessage;
            styles.Add((clean, Normalized(style)));
            return null;
        }

        /// <summary>이름(대소문자 무시)으로 지운다. 지웠으면 true.</summary>
        public static bool Remove(List<(string Name, TextStyle Style)> styles, string name)
        {
            var index = IndexOf(styles, name);
            if (index < 0)
                return false;
            styles.RemoveAt(index);
            return true;
        }

        /// <summary>이름(<see cref="CleanName"/>으로 다듬어 대소문자 무시로 견줌)의 자리. 없거나 규칙에 맞지 않는 이름이면 -1.</summary>
        public static int IndexOf(IReadOnlyList<(string Name, TextStyle Style)> styles, string name)
        {
            var clean = CleanName(name);
            if (clean == null || styles == null)
                return -1;
            for (var i = 0; i < styles.Count; i++)
            {
                if (SameName(styles[i].Name, clean))
                    return i;
            }
            return -1;
        }

        /// <summary>저장할 때 먼저 적어 둘 이름: "내 스타일 N"(N은 개수 + 1부터 겹치지 않는 수).</summary>
        public static string NewName(IReadOnlyList<(string Name, TextStyle Style)> styles)
        {
            var number = (styles?.Count ?? 0) + 1;
            while (IndexOf(styles, "내 스타일 " + number) >= 0)
                number++;
            return "내 스타일 " + number;
        }

        /// <summary>
        /// 규칙에 맞는 이름(앞뒤 공백을 다듬고 NFC로 모은 것), 맞지 않으면 null. 길이는 UTF-16 단위(string.Length)로 1~40 —
        /// 한글 완성형 한 글자가 1이라 "40자"와 맞고 저장 크기도 정확히 묶인다(이모지 하나는 2 이상).
        /// 제어 문자(줄 바꿈·탭·C1), 서식 문자(글 방향 바꾸기 U+202A~202E·U+2066~2069·U+200E/200F, 폭 없는 공백·이음 U+200B~200D, BOM, 태그 문자 등),
        /// 줄·문단 구분자, 짝 없는 서로게이트, 비문자(U+FFFE 등)가 있으면 받지 않는다 — 메뉴·툴팁에서 글 순서를 뒤집거나 같아 보이는 다른 이름을 만들 수 있고,
        /// 서로게이트 짝이 깨진 글은 JSON에 그대로 쓸 수 없다. 그 문자만 빼서 받지 않고 이름째 거절한다: 몰래 빼면 적은 것과 다른 이름이 되거나
        /// 다른 이름과 겹치기 때문이다(그래서 이모지를 ZWJ로 이은 묶음도 쓸 수 없다). NFC로 모아 조합형 한글처럼 같아 보이는 이름이 따로 남지 않게 한다.
        /// </summary>
        public static string CleanName(string name)
        {
            var trimmed = name?.Trim();
            // 조합형(NFD) 한글은 NFC로 모으면 짧아지므로 넉넉히 보고 거른 뒤 다시 잰다
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength * 4)
                return null;
            for (var i = 0; i < trimmed.Length; i++)
            {
                switch (CharUnicodeInfo.GetUnicodeCategory(trimmed, i))
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                    case UnicodeCategory.Surrogate:
                        return null;
                }
                // 서로게이트 짝은 위에서 한 글자로 보았다(짝이 깨졌으면 이미 거절)
                var code = char.IsHighSurrogate(trimmed[i]) ? char.ConvertToUtf32(trimmed[i], trimmed[++i]) : trimmed[i];
                // 비문자(평면마다 끝 둘, U+FDD0~FDEF) — NFC가 PC에 따라 예외를 내거나 그냥 두므로 먼저 거른다
                if ((code & 0xFFFE) == 0xFFFE || (code >= 0xFDD0 && code <= 0xFDEF))
                    return null;
            }
            string normalized;
            try
            {
                normalized = trimmed.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return null;
            }
            return normalized.Length <= MaxNameLength ? normalized : null;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static TextStyle Normalized(TextStyle style)
        {
            var copy = style.Clone();
            copy.Normalize();
            return copy;
        }
    }
}
