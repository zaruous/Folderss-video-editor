using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoEditor.Core
{
    /// <summary>
    /// 전환 효과 하나(데이터). 그리는 방법은 FFmpeg xfade의 내장 효과 이름(<see cref="Xfade"/>) 또는 수식(<see cref="Expr"/>) 중 하나다.
    /// 만든 뒤에는 바꾸지 않는다(클립끼리 같은 정의를 나눠 쓴다).
    /// </summary>
    public sealed class TransitionDef
    {
        /// <summary>프로젝트에 저장되는 이름(영문으로 시작, 영문·숫자·_·-).</summary>
        public string Id { get; set; }
        /// <summary>[전환] 탭·타임라인에 보일 이름.</summary>
        public string Name { get; set; }
        /// <summary>[전환] 탭의 묶음(페이드·닦아내기·모양…).</summary>
        public string Group { get; set; }
        /// <summary>xfade 내장 효과 이름(wipeleft 등).</summary>
        public string Xfade { get; set; }
        /// <summary>xfade 수식(transition=custom). P는 1(시작)에서 0(끝)으로 가고 A는 앞 화면, B는 새 화면.</summary>
        public string Expr { get; set; }
        /// <summary>필요한 FFmpeg 주 버전(0이면 최소 지원 버전).</summary>
        public int Ffmpeg { get; set; }
        /// <summary>
        /// 수식 효과의 움직임(이징): in = 천천히 시작, out = 천천히 끝, inout = 둘 다. 없으면 일정(linear — 검사가 linear를 없음으로 바꾼다).
        /// 그릴 때 수식의 P를 이징을 건 P로 바꿔 넣는다(<see cref="Transitions.XfadeOption"/>).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Ease { get; set; }

        /// <summary>플러그인에 든 기본 효과인지(아니면 사용자 효과 — 클립에 정의를 함께 저장한다).</summary>
        [JsonIgnore]
        public bool BuiltIn { get; internal set; }

        /// <summary>디졸브(xfade fade): 예전처럼 뒤 클립의 알파 페이드로 그린다.</summary>
        [JsonIgnore]
        public bool IsDissolve { get { return Expr == null && Xfade == "fade"; } }

        /// <summary>수식 효과는 픽셀마다 수식을 풀어 내장 효과보다 몇 배 느리다.</summary>
        [JsonIgnore]
        public bool IsExpression { get { return Expr != null; } }
    }

    /// <summary>
    /// 전환 효과 목록. 기본 효과는 transitions.json(플러그인에 포함), 사용자 효과는 데이터 폴더\transitions\*.json — 데이터만 더하면 효과가 늘어난다.
    /// 효과 파일과 프로젝트 파일은 남이 만든 것일 수 있으므로 <see cref="Check"/>를 거친 정의만 쓴다
    /// (수식은 정해 둔 변수·함수만, 필터 그래프를 벗어나는 글자와 반복·상태 함수는 받지 않는다).
    /// </summary>
    public static class Transitions
    {
        /// <summary>새로 넣는 전환의 기본 길이(초).</summary>
        public const double DefaultSeconds = 0.5;
        /// <summary>디졸브(기본 효과, 예전 크로스페이드).</summary>
        public const string DefaultId = "Fade";
        /// <summary>최소 지원 FFmpeg 주 버전.</summary>
        public const int BaseFfmpeg = 5;
        /// <summary>효과 파일 하나의 최대 크기와 사용자 효과 최대 개수.</summary>
        public const int MaxFileBytes = 1 << 20;
        public const int MaxUserTransitions = 500;
        public const int MaxExprLength = 1500;

        // xfade 내장 효과와 처음 들어간 FFmpeg 주 버전(5보다 낮은 것은 최소 지원 버전 5로).
        // squeezev는 FFmpeg 빌드에 따라 접근 위반으로 죽어 받지 않는다(기본 효과의 같은 모양은 수식으로)
        private static readonly Dictionary<string, int> XfadeNames = BuildXfadeNames();

        // 수식에 쓸 수 있는 변수·함수: xfade 변수(X Y W H P PLANE A B)·앞뒤 화면 값(a0~b3), 계산 함수.
        // st/ld(스레드가 같이 쓰는 상태라 조각마다 값이 섞임), while·root·taylor(반복), random·time·print는 받지 않는다
        private static readonly HashSet<string> ExprWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "X", "Y", "W", "H", "P", "PLANE", "A", "B", "PI", "E", "PHI",
            "a0", "a1", "a2", "a3", "b0", "b1", "b2", "b3",
            "if", "ifnot", "lt", "lte", "gt", "gte", "eq", "between", "clip", "not",
            "abs", "min", "max", "hypot", "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "sinh", "cosh", "tanh",
            "sqrt", "pow", "exp", "log", "floor", "ceil", "round", "trunc", "mod", "sgn", "squish", "gauss", "isnan", "isinf"
        };

        private static readonly Regex IdPattern = new Regex("^[A-Za-z][A-Za-z0-9_-]{0,47}$", RegexOptions.CultureInvariant);
        private static readonly Regex ExprCharacters = new Regex(@"^[A-Za-z0-9_.,+\-*/^() ]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Word = new Regex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions FileOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly IReadOnlyList<TransitionDef> BuiltIns = LoadBuiltIns();
        private static IReadOnlyList<TransitionDef> _all = BuiltIns;

        /// <summary>[전환] 탭에 보일 순서(기본 효과 → 사용자 효과).</summary>
        public static IReadOnlyList<TransitionDef> All { get { return _all; } }

        /// <summary>디졸브.</summary>
        public static TransitionDef Dissolve { get { return BuiltIns[0]; } }

        /// <summary>
        /// 썸네일을 그릴 차례: 사용자 효과 먼저, 그다음 기본 효과(각각 보이는 차례 그대로). 기본 효과는 한 번 그리면 데이터 폴더 캐시에 남지만
        /// 새로 넣은 사용자 효과는 처음 그려야 하므로, 기본 효과 64개 뒤에서 기다리지 않게.
        /// </summary>
        public static IEnumerable<TransitionDef> PreviewOrder(IEnumerable<TransitionDef> shown)
        {
            var list = shown.ToList();
            return list.Where(d => !d.BuiltIn).Concat(list.Where(d => d.BuiltIn));
        }

        /// <summary>id 형식(영문으로 시작하는 영문·숫자·_·- 48자 이내)인지.</summary>
        public static bool IsValidId(string id)
        {
            return id != null && IdPattern.IsMatch(id);
        }

        public static TransitionDef Find(string id)
        {
            return id == null ? null : _all.FirstOrDefault(d => d.Id == id);
        }

        /// <summary>
        /// 클립의 전환을 그릴 정의: 목록 → 클립에 함께 저장된 정의(그 효과 파일이 없는 PC) → 디졸브.
        /// ffmpegMajor(0이면 모름)가 효과에 필요한 버전보다 낮으면 디졸브로 그린다.
        /// </summary>
        public static TransitionDef Resolve(Clip clip, int ffmpegMajor = 0)
        {
            var def = Find(clip.Transition ?? DefaultId);
            if (def == null && clip.TransitionDef != null && clip.TransitionDef.Id == clip.Transition)
                def = clip.TransitionDef;
            if (def == null || (ffmpegMajor > 0 && def.Ffmpeg > ffmpegMajor))
                return Dissolve;
            return def;
        }

        /// <summary>클립 전환의 보일 이름(모르는 효과면 저장된 이름).</summary>
        public static string Name(Clip clip)
        {
            var def = Find(clip.Transition ?? DefaultId);
            if (def == null && clip.TransitionDef != null && clip.TransitionDef.Id == clip.Transition)
                def = clip.TransitionDef;
            return def?.Name ?? clip.Transition ?? Dissolve.Name;
        }

        /// <summary>클립에 효과를 정한다(길이는 그대로). 사용자 효과는 정의를 클립에 함께 저장해 그 파일이 없는 PC에서도 같게 그린다.</summary>
        public static void Assign(Clip clip, TransitionDef def)
        {
            clip.Transition = def.Id;
            clip.TransitionDef = def.BuiltIn ? null : def;
        }

        /// <summary>xfade의 효과 옵션: transition=wipeleft 또는 transition=custom:expr='…'(이징이 있으면 P를 바꿔 넣은 수식).</summary>
        public static string XfadeOption(TransitionDef def)
        {
            return def.Expr != null ? "transition=custom:expr=" + RenderGraph.FilterValue(EasedExpr(def)) : "transition=" + def.Xfade;
        }

        // 이징: P(1 → 0)를 대신할 수식 — 진행 u = 1 − P에 in = u², out = 1 − (1 − u)², inout = u²(3 − 2u)(smoothstep)를 건 것을 P로 되돌린 꼴
        private static readonly Dictionary<string, string> EaseTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["in"] = "(1-(1-P)*(1-P))",
            ["out"] = "(P*P)",
            ["inout"] = "(P*P*(3-2*P))"
        };

        /// <summary>그릴 수식: 이징이 없으면 그대로, 있으면 낱말 P만(PLANE·PI·PHI는 그대로) 이징을 건 P로 바꾼다.</summary>
        private static string EasedExpr(TransitionDef def)
        {
            if (def.Ease == null || !EaseTexts.TryGetValue(def.Ease, out var eased))
                return def.Expr;
            return Word.Replace(def.Expr, m => m.Value == "P" ? eased : m.Value);
        }

        /// <summary>이징을 건 P(수식과 같은 계산 — 시험·설명용). p는 1(시작) → 0(끝).</summary>
        public static double EasedProgress(string ease, double p)
        {
            switch (ease)
            {
                case "in":
                    return 1 - (1 - p) * (1 - p);
                case "out":
                    return p * p;
                case "inout":
                    return p * p * (3 - 2 * p);
                default:
                    return p;
            }
        }

        /// <summary>효과 미리 보기 띠(PNG) 한 장의 칸 수·크기·전환이 시작하는 칸. 칸마다 0.1초.</summary>
        public const int PreviewFrames = 15;
        public const int PreviewWidth = 128;
        public const int PreviewHeight = 72;
        /// <summary>정지 썸네일로 보일 칸(전환이 절반쯤 진행된 곳).</summary>
        public const int PreviewStill = 8;

        /// <summary>
        /// 효과 미리 보기 띠를 그리는 FFmpeg 인수(PNG를 표준 출력으로). 앞 화면은 파랑에 흰 사각(왼쪽), 새 화면은 주황에 진한 사각(오른쪽)이고
        /// 0.25초부터 1초 동안 바뀐다 — 칸 0~2 앞 화면, 3~12 전환, 13~14 새 화면. 실제 그리기와 같은 형식(gbrap)으로 섞는다.
        /// </summary>
        public static IReadOnlyList<string> PreviewArguments(TransitionDef def)
        {
            var size = PreviewWidth + "x" + PreviewHeight;
            return new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=0x2563eb:s=" + size + ":r=10:d=1.5,drawbox=x=10:y=10:w=40:h=52:c=white:t=fill",
                "-f", "lavfi", "-i", "color=c=0xf97316:s=" + size + ":r=10:d=1.5,drawbox=x=78:y=10:w=40:h=52:c=0x7c2d12:t=fill",
                "-filter_complex", "[0]format=gbrap[a];[1]format=gbrap[b];[a][b]xfade=" + XfadeOption(def) + ":duration=1:offset=0.25,format=rgb24,tile=" + PreviewFrames + "x1",
                "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "pipe:1"
            };
        }

        /// <summary>미리 보기 띠 캐시 이름(그리는 방법이 같으면 같은 이름).</summary>
        public static string PreviewKey(TransitionDef def)
        {
            var recipe = "v1|" + PreviewWidth + "x" + PreviewHeight + "|" + (def.Expr != null ? "expr:" + EasedExpr(def) : "xfade:" + def.Xfade);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(recipe))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
        }

        // ---------- 정의 읽기·검사 ----------

        /// <summary>
        /// 정의를 검사해 고친 사본을 돌려준다(쓸 수 없으면 null과 이유). 이름은 앞뒤 공백을 지우고, 필요한 FFmpeg 버전은 xfade 효과가 들어간 버전 이상으로 올린다.
        /// </summary>
        public static TransitionDef Check(TransitionDef def, out string error)
        {
            error = null;
            if (def == null)
            {
                error = "빈 정의";
                return null;
            }
            if (!IsValidId(def.Id))
            {
                error = "id는 영문으로 시작하는 영문·숫자·_·- 48자 이내여야 합니다";
                return null;
            }
            var name = (def.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 40 || name.Any(char.IsControl))
            {
                error = def.Id + ": name은 1~40자여야 합니다";
                return null;
            }
            var group = (def.Group ?? "").Trim();
            if (group.Length > 20 || group.Any(char.IsControl))
            {
                error = def.Id + ": group은 20자 이내여야 합니다";
                return null;
            }
            if ((def.Xfade == null) == (def.Expr == null))
            {
                error = def.Id + ": xfade와 expr 중 하나만 적어야 합니다";
                return null;
            }
            var ffmpeg = Math.Max(BaseFfmpeg, Math.Min(99, def.Ffmpeg));
            if (def.Xfade != null)
            {
                if (!XfadeNames.TryGetValue(def.Xfade, out var since))
                {
                    error = def.Id + ": 모르는 xfade 효과 '" + Shorten(def.Xfade) + "'";
                    return null;
                }
                ffmpeg = Math.Max(ffmpeg, since);
            }
            else if (!CheckExpr(def.Expr, out var why))
            {
                error = def.Id + ": " + why;
                return null;
            }
            var ease = def.Ease?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(ease) || ease == "linear")
                ease = null;
            else if (!EaseTexts.ContainsKey(ease))
            {
                error = def.Id + ": 모르는 ease '" + Shorten(def.Ease) + "'(linear·in·out·inout)";
                return null;
            }
            else if (def.Xfade != null)
            {
                error = def.Id + ": ease는 expr 효과에만 쓸 수 있습니다";
                return null;
            }
            return new TransitionDef
            {
                Id = def.Id,
                Name = name,
                Group = group.Length == 0 ? null : group,
                Xfade = def.Xfade,
                Expr = def.Expr?.Trim(),
                Ffmpeg = ffmpeg,
                Ease = ease,
                BuiltIn = false
            };
        }

        /// <summary>수식은 정해 둔 글자·변수·함수만, 괄호가 맞고 1500자 이내.</summary>
        private static bool CheckExpr(string expr, out string why)
        {
            why = null;
            var text = (expr ?? "").Trim();
            if (text.Length == 0 || text.Length > MaxExprLength)
            {
                why = "expr는 1~" + MaxExprLength + "자여야 합니다";
                return false;
            }
            if (!ExprCharacters.IsMatch(text))
            {
                why = "expr에 쓸 수 없는 글자가 있습니다(영문·숫자·공백과 . , + - * / ^ ( )만)";
                return false;
            }
            var depth = 0;
            foreach (var ch in text)
            {
                depth += ch == '(' ? 1 : ch == ')' ? -1 : 0;
                if (depth < 0)
                    break;
            }
            if (depth != 0)
            {
                why = "expr의 괄호가 맞지 않습니다";
                return false;
            }
            foreach (Match word in Word.Matches(text))
            {
                // 숫자에 붙은 글자(1e3, 2k)도 막는다 — 단위 접미사는 쓰지 않는다
                if (!ExprWords.Contains(word.Value) || (word.Index > 0 && char.IsDigit(text[word.Index - 1])))
                {
                    why = "expr에 쓸 수 없는 이름 '" + Shorten(word.Value) + "'";
                    return false;
                }
            }
            return true;
        }

        private static string Shorten(string value)
        {
            return value.Length > 24 ? value.Substring(0, 24) + "…" : value;
        }

        private sealed class TransitionFile
        {
            public List<TransitionDef> Transitions { get; set; }
        }

        /// <summary>효과 파일(JSON: { "transitions": [ { id, name, group, xfade 또는 expr, ffmpeg } ] })을 읽는다. 잘못된 정의는 errors에 적고 건너뛴다.</summary>
        public static List<TransitionDef> Parse(string json, string source, List<string> errors)
        {
            var result = new List<TransitionDef>();
            TransitionFile file;
            try
            {
                file = JsonSerializer.Deserialize<TransitionFile>(json, FileOptions);
            }
            catch (JsonException ex)
            {
                errors.Add(source + ": JSON 형식이 아닙니다(" + Shorten(ex.Message) + ")");
                return result;
            }
            if (file?.Transitions == null)
            {
                errors.Add(source + ": \"transitions\" 목록이 없습니다");
                return result;
            }
            foreach (var raw in file.Transitions)
            {
                var def = Check(raw, out var error);
                if (def == null)
                    errors.Add(source + ": " + error);
                else
                    result.Add(def);
            }
            return result;
        }

        /// <summary>
        /// 사용자 효과 폴더(*.json, 이름 순)를 다시 읽어 목록을 바꾼다. 기본 효과와 같은 id, 앞 파일과 같은 id는 건너뛴다. 읽지 못한 까닭을 돌려준다.
        /// 폴더가 없으면 사용자 효과 없이 기본 효과만.
        /// </summary>
        public static IReadOnlyList<string> LoadFolder(string folder)
        {
            var errors = new List<string>();
            var user = new List<TransitionDef>();
            var ids = new HashSet<string>(BuiltIns.Select(d => d.Id), StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (var path in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var source = Path.GetFileName(path);
                    string json;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length > MaxFileBytes)
                        {
                            errors.Add(source + ": 파일이 너무 큽니다(1MB까지)");
                            continue;
                        }
                        json = File.ReadAllText(path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        errors.Add(source + ": 읽지 못했습니다(" + ex.Message + ")");
                        continue;
                    }
                    foreach (var def in Parse(json, source, errors))
                    {
                        if (user.Count >= MaxUserTransitions)
                        {
                            errors.Add(source + ": 사용자 효과는 " + MaxUserTransitions + "개까지입니다");
                            break;
                        }
                        if (!ids.Add(def.Id))
                        {
                            errors.Add(source + ": id '" + def.Id + "'는 이미 있는 효과입니다");
                            continue;
                        }
                        def.Group = def.Group ?? "내 효과";
                        user.Add(def);
                    }
                }
            }
            _all = BuiltIns.Concat(user).ToList();
            return errors;
        }

        private static IReadOnlyList<TransitionDef> LoadBuiltIns()
        {
            using (var stream = typeof(Transitions).Assembly.GetManifestResourceStream("VideoEditor.Core.transitions.json"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("기본 전환 효과 데이터(transitions.json)가 어셈블리에 없습니다.")))
            {
                var errors = new List<string>();
                var defs = Parse(reader.ReadToEnd(), "transitions.json", errors);
                if (errors.Count > 0 || defs.Count == 0 || defs[0].Id != DefaultId)
                    throw new InvalidOperationException("기본 전환 효과 데이터가 잘못되었습니다: " + string.Join("; ", errors));
                foreach (var def in defs)
                    def.BuiltIn = true;
                return defs;
            }
        }

        private static Dictionary<string, int> BuildXfadeNames()
        {
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in new[]
            {
                "fade", "wipeleft", "wiperight", "wipeup", "wipedown", "slideleft", "slideright", "slideup", "slidedown",
                "circlecrop", "rectcrop", "distance", "fadeblack", "fadewhite", "radial", "smoothleft", "smoothright", "smoothup", "smoothdown",
                "circleopen", "circleclose", "vertopen", "vertclose", "horzopen", "horzclose", "dissolve", "pixelize",
                "diagtl", "diagtr", "diagbl", "diagbr", "hlslice", "hrslice", "vuslice", "vdslice", "hblur", "fadegrays",
                "wipetl", "wipetr", "wipebl", "wipebr", "squeezeh", "zoomin"
            })
                names[name] = BaseFfmpeg;
            foreach (var name in new[] { "fadefast", "fadeslow" })
                names[name] = 6;
            foreach (var name in new[] { "hlwind", "hrwind", "vuwind", "vdwind", "coverleft", "coverright", "coverup", "coverdown", "revealleft", "revealright", "revealup", "revealdown" })
                names[name] = 7;
            return names;
        }
    }

    /// <summary>
    /// 프로젝트 파일의 값 하나를 너그럽게 읽는다: 형식이 맞지 않으면(문자열 자리에 숫자 등) 예외 대신 null — 그 값 하나 때문에 프로젝트 전체가 열리지 않는 일이 없게.
    /// </summary>
    public sealed class LenientConverter<T> : JsonConverter<T> where T : class
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var document = JsonDocument.ParseValue(ref reader))
            {
                try
                {
                    if (typeof(T) == typeof(string))
                        return document.RootElement.ValueKind == JsonValueKind.String ? (T)(object)document.RootElement.GetString() : null;
                    return document.RootElement.Deserialize<T>(options);
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    return null;
                }
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    /// <summary>열거 값을 너그럽게 읽는다: 모르는 이름·숫자·다른 형식이면 예외 대신 기본값(첫 값) — 값 하나 때문에 프로젝트가 열리지 않는 일이 없게.</summary>
    public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var document = JsonDocument.ParseValue(ref reader))
            {
                var element = document.RootElement;
                if (element.ValueKind == JsonValueKind.String && Enum.TryParse<T>(element.GetString(), false, out var named) && Enum.IsDefined(named)
                    && !int.TryParse(element.GetString(), out _))
                    return named;
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
                    return (T)Enum.ToObject(typeof(T), number);
                return default;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
