using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoEditor.Core
{
    /// <summary>
    /// 클립에 넣은 동영상 효과 하나(값만): 효과 id, 숫자 값(매개변수 키 → 값), 색, 사람에게만.
    /// 그리는 방법은 플러그인 코드의 효과 목록(<see cref="VideoEffects"/>)에 있고 프로젝트 파일에는 값만 저장한다 — 필터 글은 받지 않는다(남이 만든 파일이 필터 그래프를 바꾸지 못하게).
    /// </summary>
    public sealed class ClipEffect
    {
        /// <summary>효과 id(<see cref="VideoEffectDef.Id"/>).</summary>
        public string Id { get; set; }
        /// <summary>숫자 값(매개변수 키 → 값). 없는 키는 효과 정의의 기본값으로 그린다.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, double> Values { get; set; }
        /// <summary>색(#RRGGBB) — 색이 있는 효과만. 없으면 정의의 기본 색.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Color { get; set; }
        /// <summary>사람에게만: 원본의 인물 마스크가 있으면 사람 자리에만 칠한다(없으면 클립 전체).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool PersonOnly { get; set; }

        /// <summary>깊은 복사(값 사전도 새로).</summary>
        public ClipEffect Clone()
        {
            return new ClipEffect
            {
                Id = Id,
                Values = Values == null ? null : new Dictionary<string, double>(Values, StringComparer.Ordinal),
                Color = Color,
                PersonOnly = PersonOnly
            };
        }
    }

    public enum EffectParamKind
    {
        /// <summary>숫자(슬라이더): 최소·최대·기본값·단위.</summary>
        Number,
        /// <summary>색(#RRGGBB) — 값은 <see cref="ClipEffect.Color"/>에 둔다(효과마다 색은 하나).</summary>
        Color,
        /// <summary>고르기(콤보): 값은 선택지 번호(0부터)를 숫자로 저장한다 — 숫자 슬라이더였던 예전 값도 가까운 번호로 그대로 열린다.</summary>
        Choice
    }

    /// <summary>효과 매개변수 하나(속성 패널의 한 줄).</summary>
    public sealed class EffectParam
    {
        /// <summary>프로젝트 파일에 저장되는 키(영문 소문자).</summary>
        public string Key { get; set; }
        /// <summary>속성 패널에 보일 이름.</summary>
        public string Name { get; set; }
        public EffectParamKind Kind { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Default { get; set; }
        /// <summary>단위(px·%·초).</summary>
        public string Unit { get; set; }
        /// <summary>색 매개변수의 기본 색(#RRGGBB).</summary>
        public string DefaultColor { get; set; }
        /// <summary>툴팁 설명.</summary>
        public string Hint { get; set; }
        /// <summary>고르기 매개변수의 선택지 이름(번호 순서). 다른 종류는 null.</summary>
        public IReadOnlyList<string> Options { get; set; }

        public static EffectParam Number(string key, string name, double min, double max, double value, string unit, string hint)
        {
            return new EffectParam { Key = key, Name = name, Kind = EffectParamKind.Number, Min = min, Max = max, Default = value, Unit = unit, Hint = hint };
        }

        /// <summary>고르기: 선택지 번호 0 ~ 개수 − 1, 기본은 value번째.</summary>
        public static EffectParam Choice(string key, string name, IReadOnlyList<string> options, int value, string hint)
        {
            return new EffectParam { Key = key, Name = name, Kind = EffectParamKind.Choice, Min = 0, Max = options.Count - 1, Default = value, Unit = "", Options = options, Hint = hint };
        }

        public static EffectParam ColorParam(string key, string name, string color, string hint)
        {
            return new EffectParam { Key = key, Name = name, Kind = EffectParamKind.Color, DefaultColor = color, Hint = hint };
        }
    }

    /// <summary>
    /// 동영상 효과 하나(코드에 든 정의): 보일 이름·묶음·설명, 매개변수, 그리는 방법(필터 조각을 만드는 함수).
    /// 효과를 더하려면 이 정의(<see cref="Since"/>는 다음 판 번호)와 그리는 함수를 만들어 <see cref="VideoEffects"/>의 목록에 한 줄 더한다(<see cref="ScanLine"/>가 본보기).
    /// </summary>
    public sealed class VideoEffectDef
    {
        public VideoEffectDef(string id, string name, string group, string description, int version, IReadOnlyList<EffectParam> parameters,
            Func<EffectRender, string> recipe, IReadOnlyDictionary<string, double> previewValues = null)
        {
            Id = id;
            Name = name;
            Group = group;
            Description = description;
            Version = version;
            Params = parameters;
            Recipe = recipe;
            PreviewValues = previewValues ?? new Dictionary<string, double>();
        }

        /// <summary>프로젝트에 저장되는 이름(영문 소문자·숫자·-). 바꾸지 않는다(저장된 프로젝트가 이 id로 연다).</summary>
        public string Id { get; }
        /// <summary>효과 탭·속성 패널에 보일 이름.</summary>
        public string Name { get; }
        /// <summary>효과 탭의 묶음.</summary>
        public string Group { get; }
        /// <summary>툴팁 설명.</summary>
        public string Description { get; }
        /// <summary>그리는 방법의 판(바꾸면 썸네일 캐시를 새로 그린다).</summary>
        public int Version { get; }
        public IReadOnlyList<EffectParam> Params { get; }
        /// <summary>필터 조각을 만든다 — <see cref="EffectRender"/>를 받아 지금 흐름에 이어 붙일 글(아래 <see cref="VideoEffects"/> 설명의 규칙).</summary>
        internal Func<EffectRender, string> Recipe { get; }
        /// <summary>썸네일에서만 바꿀 값(1.5초 띠 안에 움직임이 다 보이게 등).</summary>
        public IReadOnlyDictionary<string, double> PreviewValues { get; }
        /// <summary>
        /// [사람에게만]을 고를 수 있는지(기본 true). 그림 전체를 움직이는 효과(흔들림·줌)는 사람 자리만 따로 움직일 수 없어 false —
        /// 속성 패널에 [사람에게만]이 보이지 않고, 켜진 채 저장된 값은 열 때 끄며 그릴 때도 무시한다(마스크를 읽지 않음).
        /// </summary>
        public bool AllowsPersonOnly { get; init; } = true;
        /// <summary>
        /// 이 효과가 처음 나온 판(1 = 스캔 라인과 함께 처음부터, 2 = 스캔 계열·글리치·흔들림·줌 13개). 효과를 더할 때는 지금 가장 큰 값 + 1로 정한다(같이 나오는 효과는 같은 값).
        /// 효과 탭의 "새로" 표시가 이 값을 본다(<see cref="VideoEffectNews"/> — 본 기록이 없는 사용자에게는 1보다 큰 효과가 새로 보인다). 0(정하지 않음)이면 시험이 막는다.
        /// </summary>
        public int Since { get; init; }

        /// <summary>색 매개변수(없으면 null).</summary>
        public EffectParam ColorParam { get { return Params.FirstOrDefault(p => p.Kind == EffectParamKind.Color); } }

        public EffectParam Param(string key)
        {
            return Params.FirstOrDefault(p => p.Key == key);
        }
    }

    /// <summary>
    /// 효과 하나를 그릴 자리. 크기는 출력 픽셀이고, 길이 값(px 매개변수)은 <see cref="PixelScale"/>를 곱해 쓴다(프로젝트 픽셀 → 틀 크기의 출력 픽셀 — 크기 배율·재생 축소 포함).
    /// </summary>
    public sealed class EffectRender
    {
        public ClipEffect Effect { get; set; }
        public VideoEffectDef Def { get; set; }
        /// <summary>틀 크기(출력 픽셀) — 효과가 받는 그림은 이 크기다(맞춤의 빈 곳은 투명하게 채워 둠).</summary>
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>
        /// 틀 안의 그림 크기(출력 픽셀): [화면에 맞춤]이면 그림이 틀보다 작고 가운데에 있다(남는 곳은 투명 — pad와 같이 (틀 − 그림) ÷ 2를 버림한 자리).
        /// 0이면 틀 전체가 그림이다(채우기·늘이기·흐린 배경, 원본 크기를 모를 때, 썸네일). 그림을 움직이는 효과(흔들림·줌)가 빈 곳은 그대로 두고 그림 안에서만 움직이는 데 쓴다.
        /// </summary>
        public int PictureWidth { get; set; }
        public int PictureHeight { get; set; }
        /// <summary>프로젝트 픽셀 하나가 이 틀에서 몇 출력 픽셀인지(출력 배율 × 크기 배율 ÷ 재생 축소).</summary>
        public double PixelScale { get; set; }
        /// <summary>프레임 레이트 글("30/1")과 그 값.</summary>
        public string Rate { get; set; }
        public double Fps { get; set; }
        /// <summary>그리는 구간 길이(초).</summary>
        public double Seconds { get; set; }
        /// <summary>흐름의 0초가 클립 안 몇 초인지 — 효과 시각 = t + 이 값(어디서부터 그려도 같은 프레임은 같게).</summary>
        public double TimeOffset { get; set; }
        /// <summary>이 효과가 쓸 필터 라벨 앞머리(그래프 안에서 겹치지 않음).</summary>
        public string Label { get; set; }
        /// <summary>사람에게만: 틀 크기 흑백 마스크 흐름의 라벨(gbrp, 흰색 = 사람). 없으면 null(클립 전체).</summary>
        public string Mask { get; set; }

        /// <summary>숫자 값(범위 안, 없으면 기본값).</summary>
        public double Number(string key)
        {
            return VideoEffects.Number(Effect, Def, key);
        }

        /// <summary>색(#RRGGBB).</summary>
        public string Color { get { return VideoEffects.ColorOf(Effect, Def); } }
    }

    /// <summary>
    /// 동영상 효과 목록과 클립 효과 값 다루기. 효과는 클립 그림 위에 그리는 것(전환과 달리 한 클립 안에서)으로, 렌더 그래프가 클립의 틀 계산(맞춤·채우기) 뒤 회전 앞에 잇는다.
    /// 그리는 함수(<see cref="VideoEffectDef.Recipe"/>)의 규칙:
    /// - 받는 흐름은 틀 크기(<see cref="EffectRender.Width"/>×Height) gbrap이고, 돌려주는 글을 지금 체인 뒤에 그대로 붙인다 — 글이 끝난 곳의 흐름이 효과를 입힌 클립(같은 크기, 알파 그대로)이다.
    ///   그래서 글은 보통 "[라벨a];…;[…][라벨a]blend=…"처럼 지금 흐름에 라벨을 붙여 끊고, 마지막 필터의 출력에 라벨을 달지 않는다.
    /// - 라벨은 <see cref="EffectRender.Label"/>로 시작한다. 수식에 st()·ld()·random·time을 쓰지 않고(조각마다 값이 섞이거나 그릴 때마다 달라짐), 크기 바꾸기(scale)를 잇지 않는다.
    /// - 숫자는 <see cref="RenderGraph.N"/>으로만 쓴다(사용자 글은 넣지 않음 — 색도 #RRGGBB를 숫자로 바꿔 쓴다).
    /// </summary>
    public static class VideoEffects
    {
        /// <summary>클립 하나에 넣을 수 있는 효과 수.</summary>
        public const int MaxPerClip = 8;
        // 알 수 없는 효과(새 판에서 만든 것)의 값은 이만큼만 남긴다
        private const int MaxValues = 32;

        private static readonly Regex IdPattern = new Regex("^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant);
        private static readonly Regex KeyPattern = new Regex("^[a-z][a-z0-9]{0,23}$", RegexOptions.CultureInvariant);

        /// <summary>효과 탭에 보일 순서. 효과를 더하려면 여기에 한 줄(정의의 <see cref="VideoEffectDef.Since"/>는 다음 판 번호로 — 효과 탭에 "새로"로 보인다).</summary>
        public static IReadOnlyList<VideoEffectDef> All { get; } = new[]
        {
            ScanLine.Def,
            ShockwaveScan.Def,
            SciFiScan.Def,
            RayScan.Def,
            GlitchScan.Def,
            RgbSplit.Def,
            BlockGlitch.Def,
            LineSlip.Def,
            BadSignal.Def,
            ShakeZoom.CameraShake,
            ShakeZoom.Earthquake,
            ShakeZoom.Heartbeat,
            ShakeZoom.PunchZoom,
            ShakeZoom.ZoomBounce,
        };

        public static VideoEffectDef Find(string id)
        {
            return id == null ? null : All.FirstOrDefault(d => d.Id == id);
        }

        /// <summary>효과 탭의 묶음 이름(목록에 처음 나오는 차례 — 효과 탭이 이 차례로 묶음 제목과 묶음 고르기를 보인다).</summary>
        public static IReadOnlyList<string> Groups()
        {
            return All.Select(d => d.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
        }

        /// <summary>id 형식(영문 소문자로 시작하는 소문자·숫자·- 48자 이내)인지.</summary>
        public static bool IsValidId(string id)
        {
            return id != null && IdPattern.IsMatch(id);
        }

        /// <summary>효과를 넣을 수 있는 클립: 화면이 있는 파일 클립(동영상·사진). 글자·소리 클립은 아님.</summary>
        public static bool CanHave(Clip clip)
        {
            return clip != null && clip.Kind == ClipKind.Media && clip.HasVideo && !clip.AudioOnly;
        }

        /// <summary>효과 이름(모르는 효과면 "알 수 없는 효과").</summary>
        public static string Name(ClipEffect effect)
        {
            return Find(effect?.Id)?.Name ?? "알 수 없는 효과";
        }

        /// <summary>정의의 기본값으로 새 효과 값을 만든다(값을 모두 적어 둔다 — 나중에 기본값이 바뀌어도 넣은 효과는 그대로 보이게).</summary>
        public static ClipEffect Create(VideoEffectDef def)
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var param in def.Params.Where(p => p.Kind != EffectParamKind.Color))
                values[param.Key] = param.Default;
            return new ClipEffect { Id = def.Id, Values = values, Color = def.ColorParam?.DefaultColor };
        }

        /// <summary>숫자 값(고르기는 선택지 번호): 적힌 값(범위 안으로), 없거나 NaN이면 기본값. 매개변수가 없으면 0.</summary>
        public static double Number(ClipEffect effect, VideoEffectDef def, string key)
        {
            var param = def?.Param(key);
            if (param == null || param.Kind == EffectParamKind.Color)
                return 0;
            return effect?.Values != null && effect.Values.TryGetValue(key, out var value) ? Range(value, param) : param.Default;
        }

        /// <summary>색(#RRGGBB): 적힌 색, 잘못됐거나 없으면 정의의 기본 색(색이 없는 효과는 흰색).</summary>
        public static string ColorOf(ClipEffect effect, VideoEffectDef def)
        {
            var fallback = def?.ColorParam?.DefaultColor ?? "#FFFFFF";
            return Rgb(effect?.Color) ?? fallback;
        }

        /// <summary>숫자 값(고르기는 선택지 번호)을 바꾼다(범위 안으로). 클립 효과 고치기는 이것으로 — 실행 취소는 부르는 쪽의 EditClip.</summary>
        public static void SetNumber(ClipEffect effect, string key, double value)
        {
            var param = Find(effect?.Id)?.Param(key);
            if (param == null || param.Kind == EffectParamKind.Color)
                return;
            effect.Values ??= new Dictionary<string, double>(StringComparer.Ordinal);
            effect.Values[key] = Range(value, param);
        }

        /// <summary>색을 바꾼다(#RRGGBB로 — 알파는 빼고, 잘못된 값은 무시). 색이 없는 효과는 그대로.</summary>
        public static void SetColor(ClipEffect effect, string color)
        {
            var rgb = Rgb(color);
            if (effect != null && rgb != null && Find(effect.Id)?.ColorParam != null)
                effect.Color = rgb;
        }

        /// <summary>
        /// 클립에 효과를 하나 더한다(목록 끝 — 앞 효과 위에 그린다). 화면이 있는 파일 클립에만, 클립마다 <see cref="MaxPerClip"/>개까지. 더한 효과를 돌려준다.
        /// </summary>
        public static ClipEffect Add(Project project, Clip clip, VideoEffectDef def)
        {
            if (clip == null || def == null)
                throw new EditException("효과를 넣을 클립을 고르세요.");
            TimelineEdits.EnsureEditable(project.TrackOf(clip));
            if (!CanHave(clip))
                throw new EditException("동영상 효과는 동영상·사진 클립에만 넣을 수 있습니다.");
            if ((clip.Effects?.Count ?? 0) >= MaxPerClip)
                throw new EditException("클립 하나에 효과는 " + MaxPerClip + "개까지 넣을 수 있습니다.");
            var effect = Create(def);
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(effect);
            return effect;
        }

        /// <summary>클립의 index번째 효과를 뺀다(남은 효과가 없으면 목록을 비운다 — 저장할 때 빠지게).</summary>
        public static void Remove(Clip clip, int index)
        {
            if (clip?.Effects == null || index < 0 || index >= clip.Effects.Count)
                return;
            clip.Effects.RemoveAt(index);
            if (clip.Effects.Count == 0)
                clip.Effects = null;
        }

        /// <summary>
        /// 클립의 from번째 효과를 to번째 자리로 옮긴다(뒤 효과가 앞 효과 위에 그린다). 옮겼으면 true, 번호가 틀리거나 같으면 그대로 false.
        /// 실행 취소는 부르는 쪽의 EditClip.
        /// </summary>
        public static bool Move(Project project, Clip clip, int from, int to)
        {
            var effects = clip?.Effects;
            if (effects == null || from == to || from < 0 || to < 0 || from >= effects.Count || to >= effects.Count)
                return false;
            TimelineEdits.EnsureEditable(project.TrackOf(clip));
            var effect = effects[from];
            effects.RemoveAt(from);
            effects.Insert(to, effect);
            return true;
        }

        /// <summary>그릴 효과(목록에 있는 효과만 — 알 수 없는 효과는 남겨 두되 그리지 않는다)와 그 정의.</summary>
        public static List<(ClipEffect Effect, VideoEffectDef Def)> Renderable(Clip clip)
        {
            var result = new List<(ClipEffect, VideoEffectDef)>();
            if (!CanHave(clip) || clip.Effects == null)
                return result;
            foreach (var effect in clip.Effects)
            {
                var def = Find(effect?.Id);
                if (def != null)
                    result.Add((effect, def));
            }
            return result;
        }

        /// <summary>
        /// 읽은 클립의 효과를 고친 목록(없으면 null — 저장할 때 빠진다). 프로젝트 파일은 남이 만든 것일 수 있다:
        /// - 화면이 있는 파일 클립에만, <see cref="MaxPerClip"/>개까지, 빈 항목·id 형식이 아닌 항목은 버린다.
        /// - 아는 효과: 숫자는 매개변수 범위 안으로(NaN·무한대는 기본값), 모르는 키는 버리고, 색은 #RRGGBB로(잘못되면 기본 색, 색이 없는 효과는 null).
        /// - 모르는 효과(새 판에서 만든 효과 등): 그리지 않고 남겨 둔다(그 판에서 열면 다시 보이게) — 값은 형식이 맞는 키의 유한한 숫자 32개까지, 색은 #RRGGBB이거나 null.
        /// </summary>
        public static List<ClipEffect> Clean(Clip clip)
        {
            if (!CanHave(clip) || clip.Effects == null)
                return null;
            var result = new List<ClipEffect>();
            foreach (var raw in clip.Effects)
            {
                if (raw == null || !IsValidId(raw.Id) || result.Count >= MaxPerClip)
                    continue;
                var def = Find(raw.Id);
                // 사람에게만이 없는 효과(그림 전체를 움직임)는 켜진 채 저장됐어도 끈다
                var effect = new ClipEffect { Id = raw.Id, PersonOnly = raw.PersonOnly && (def == null || def.AllowsPersonOnly) };
                if (def != null)
                {
                    effect.Values = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var param in def.Params.Where(p => p.Kind != EffectParamKind.Color))
                        effect.Values[param.Key] = Number(raw, def, param.Key);
                    effect.Color = def.ColorParam != null ? ColorOf(raw, def) : null;
                }
                else
                {
                    var values = (raw.Values ?? new Dictionary<string, double>())
                        .Where(v => v.Key != null && KeyPattern.IsMatch(v.Key) && double.IsFinite(v.Value))
                        .Take(MaxValues).ToList();
                    effect.Values = values.Count == 0 ? null : values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
                    effect.Color = Rgb(raw.Color);
                }
                result.Add(effect);
            }
            return result.Count == 0 ? null : result;
        }

        private static double Range(double value, EffectParam param)
        {
            if (!double.IsFinite(value))
                return param.Default;
            // 고르기는 가까운 번호로(예전 슬라이더 값 — 그리기도 가까운 정수였다)
            if (param.Kind == EffectParamKind.Choice)
                value = Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Max(param.Min, Math.Min(param.Max, value));
        }

        /// <summary>"#RRGGBB"(대문자)로, "#AARRGGBB"는 알파를 뺀다. 잘못된 값은 null.</summary>
        private static string Rgb(string color)
        {
            var normalized = TextStyle.NormalizeColor(color, null);
            return normalized == null ? null : "#" + normalized.Substring(normalized.Length - 6);
        }

        /// <summary>"#RRGGBB" → 0~1 값 셋.</summary>
        internal static (double R, double G, double B) Channels(string color)
        {
            var hex = Rgb(color) ?? "#FFFFFF";
            return (Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0, Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0, Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);
        }

        // ---------- 효과 탭 썸네일 ----------

        /// <summary>효과 미리 보기 띠(PNG) 한 장의 칸 수·크기와 정지 썸네일로 보일 칸. 칸마다 0.1초.</summary>
        public const int PreviewFrames = 15;
        public const int PreviewWidth = 128;
        public const int PreviewHeight = 72;
        public const int PreviewStill = 6;
        // 썸네일 그림에서 프로젝트 픽셀 하나의 크기(1080 높이 화면을 썸네일보다 조금 크게 줄인 배율 — 가는 선도 보이게)
        private const double PreviewPixelScale = 0.16;

        /// <summary>
        /// 효과 미리 보기 띠의 바탕(FFmpeg lavfi 입력 — 합성 그림 한 장을 1.5초로 늘인 정지 화면). 효과가 잘 보이게 무늬와 밝고 어두운 곳을 섞었다:
        /// 위 72%는 남색에서 분홍으로 가는 하늘(왼쪽일수록 어두움)에 가는 격자, 아래는 흑백 바둑판 바닥, 오른쪽 위에 밝은 해(둘레에 빛), 가운데 왼쪽에 어두운 사람 실루엣(머리·목·어깨).
        /// 격자·바둑판은 흔들림·확대를, 해·실루엣의 또렷한 가장자리는 글리치·색 어긋남을, 어두운 곳(실루엣·왼쪽 하늘)은 어둠에 반응하는 효과(스캔 라인의 떨림)를 보여 준다.
        /// 수식(geq)은 한 장만 그리고 나머지는 tpad로 같은 그림을 늘인다. 바꾸면 썸네일 캐시 이름(<see cref="PreviewKey"/>)도 바뀐다.
        /// </summary>
        public static string PreviewSource { get; } = BuildPreviewSource();

        private static string BuildPreviewSource()
        {
            // 한 채널의 수식: 사람 > 해 > 바닥(바둑판) > 하늘(그라데이션 + 격자 + 해 둘레 빛) 차례로 덮는다
            string Plane(int skyTop, int skyBottom, int tileDark, int tileLight, int sun, int halo, int person, int grid)
            {
                const string personArea = "lt(hypot(X-0.4*W,Y-0.35*H),0.13*H)+lt(abs(X-0.4*W),0.04*W)*between(Y,0.4*H,0.7*H)+lt(hypot((X-0.4*W)/(0.22*W),(Y-H)/(0.4*H)),1)";
                const string sunDistance = "hypot(X-0.79*W,Y-0.3*H)/H";
                var sky = "(" + skyTop + "+" + (skyBottom - skyTop) + "*Y/(0.72*H))*(0.45+0.55*X/W)";
                var lines = grid + "*(lt(mod(X,12),1)+lt(mod(Y,12),1))";
                var floor = "if(mod(floor(X/8)+floor(Y/8),2)," + tileDark + "," + tileLight + ")";
                return "if(" + personArea + "," + person + ",if(lt(" + sunDistance + ",0.15)," + sun + ",if(gt(Y,0.72*H)," + floor
                    + ",min(255," + sky + "+" + lines + "+" + halo + "*max(0,1-" + sunDistance + "/0.32)))))";
            }
            return "color=c=black:s=" + PreviewWidth + "x" + PreviewHeight + ":r=10:d=0.1,format=gbrp,"
                + "geq=r='" + Plane(30, 205, 40, 150, 255, 90, 10, 34) + "':g='" + Plane(44, 96, 46, 158, 238, 60, 12, 34) + "':b='" + Plane(96, 132, 64, 176, 176, 20, 18, 34) + "',"
                + "tpad=stop_mode=clone:stop_duration=" + RenderGraph.N((PreviewFrames - 1) / 10.0);
        }

        /// <summary>
        /// 효과 미리 보기 띠를 그리는 FFmpeg 인수(PNG를 표준 출력으로). 바탕은 무늬·해·사람 실루엣을 넣은 합성 그림(<see cref="PreviewSource"/>)이다.
        /// 실제 그리기와 같은 필터 조각(<see cref="VideoEffectDef.Recipe"/>)에 썸네일용 값(<see cref="VideoEffectDef.PreviewValues"/>)을 넣어 1.5초를 15칸으로 그린다.
        /// </summary>
        public static IReadOnlyList<string> PreviewArguments(VideoEffectDef def)
        {
            var effect = Create(def);
            foreach (var value in def.PreviewValues)
                SetNumber(effect, value.Key, value.Value);
            var render = new EffectRender
            {
                Effect = effect,
                Def = def,
                Width = PreviewWidth,
                Height = PreviewHeight,
                PixelScale = PreviewPixelScale,
                Rate = "10/1",
                Fps = 10,
                Seconds = PreviewFrames / 10.0,
                TimeOffset = 0,
                Label = "p"
            };
            return new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", PreviewSource,
                "-filter_complex", "[0]format=gbrap" + def.Recipe(render) + ",format=rgb24,tile=" + PreviewFrames + "x1",
                "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "pipe:1"
            };
        }

        /// <summary>미리 보기 띠 캐시 이름(바탕 그림·효과·그리는 방법의 판·썸네일 값이 같으면 같은 이름 — 예전 판의 밋밋한 바탕 썸네일은 다시 쓰지 않는다).</summary>
        public static string PreviewKey(VideoEffectDef def)
        {
            var recipe = "fx2|" + PreviewSource + "|" + def.Id + "|" + def.Version + "|"
                + string.Join(",", def.Params.Select(p => p.Key + "=" + RenderGraph.N(p.Default) + (p.DefaultColor ?? "")))
                + "|" + string.Join(",", def.PreviewValues.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + RenderGraph.N(v.Value)));
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(recipe))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
        }
    }

    /// <summary>
    /// 효과 탭의 "새로" 표시: 사용자가 이미 본 효과 id 모음(플러그인 설정에 JSON 글 배열로)과 아직 보지 않은 새 효과.
    /// - 본 효과: 클립에 넣은 효과(넣기 단추·두 번 누르기·끌어 놓기), 고른 클립에 얹어 1초 넘게 미리 본 효과, 알림 줄의 [확인]을 누를 때 있던 모든 효과.
    /// - 처음 판의 효과(<see cref="VideoEffectDef.Since"/> ≤ <see cref="FirstRelease"/>)는 늘 본 것이다. 설정이 없거나 깨졌으면 그것만 본 것으로 —
    ///   예전부터 쓰던 사람에게 나중에 더한 효과가 새로 보인다.
    /// - 너그럽게 읽는다: 배열이 아니거나 너무 길면 없는 것으로, 글이 아닌 항목·모르는 id(다른 판의 효과·잘못된 글)는 버리고 앞의 <see cref="MaxIds"/>개만 본다.
    ///   쓸 때도 아는 효과만 목록 차례로 쓴다(<see cref="MaxIds"/>개까지).
    /// </summary>
    public static class VideoEffectNews
    {
        /// <summary>처음 판(이 판까지의 효과는 "새로"가 아니다).</summary>
        public const int FirstRelease = 1;
        /// <summary>설정 글이 이보다 길면 깨진 것으로 본다.</summary>
        public const int MaxSettingLength = 16384;
        /// <summary>읽고 쓰는 id 수 상한.</summary>
        public const int MaxIds = 256;

        /// <summary>
        /// 설정 글에서 본 효과 id(처음 판의 효과는 늘 들어 있다). all이 없으면 <see cref="VideoEffects.All"/>.
        /// 모르는 id라도 형식이 맞으면 남긴다 — 다른 판(새 판·예전 판)과 설정을 같이 쓸 때 그 판의 효과를 본 기록이 지워져 다시 "새로"가 되지 않게.
        /// </summary>
        public static HashSet<string> ReadSeen(string setting, IReadOnlyList<VideoEffectDef> all = null)
        {
            all ??= VideoEffects.All;
            var seen = new HashSet<string>(all.Where(d => d.Since <= FirstRelease).Select(d => d.Id), StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(setting) || setting.Length > MaxSettingLength)
                return seen;
            var known = new HashSet<string>(all.Select(d => d.Id), StringComparer.Ordinal);
            try
            {
                using (var document = JsonDocument.Parse(setting))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        return seen;
                    foreach (var element in document.RootElement.EnumerateArray().Take(MaxIds))
                    {
                        var id = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                        if (id != null && (known.Contains(id) || VideoEffects.IsValidId(id)))
                            seen.Add(id);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            {
                // 깨진 설정 — 처음 판의 효과만 본 것으로
            }
            return seen;
        }

        /// <summary>
        /// 본 효과 id를 설정 글로: 아는 효과를 목록 차례로, 그 뒤에 형식이 맞는 모르는 id(다른 판의 효과)를 이름 차례로 — 모두 <see cref="MaxIds"/>개까지.
        /// </summary>
        public static string WriteSeen(IEnumerable<string> seen, IReadOnlyList<VideoEffectDef> all = null)
        {
            all ??= VideoEffects.All;
            var set = new HashSet<string>((seen ?? Enumerable.Empty<string>()).Where(id => id != null), StringComparer.Ordinal);
            var known = all.Where(d => set.Contains(d.Id)).Select(d => d.Id).ToList();
            var knownIds = new HashSet<string>(all.Select(d => d.Id), StringComparer.Ordinal);
            var others = set.Where(id => !knownIds.Contains(id) && VideoEffects.IsValidId(id)).OrderBy(id => id, StringComparer.Ordinal);
            return JsonSerializer.Serialize(known.Concat(others).Take(MaxIds).ToList());
        }

        /// <summary>아직 보지 않은 새 효과(목록 차례). 처음 판의 효과는 seen에 없어도 새로가 아니다.</summary>
        public static List<VideoEffectDef> New(ISet<string> seen, IReadOnlyList<VideoEffectDef> all = null)
        {
            all ??= VideoEffects.All;
            return all.Where(d => d.Since > FirstRelease && (seen == null || !seen.Contains(d.Id))).ToList();
        }
    }

    /// <summary>
    /// 목록을 너그럽게 읽는다: 목록이 아니면 null, 항목 하나가 형식에 맞지 않으면 그 항목만 버린다 — 값 하나 때문에 프로젝트가 열리지 않거나 다른 항목까지 사라지지 않게.
    /// </summary>
    public sealed class LenientListConverter<T> : JsonConverter<List<T>> where T : class
    {
        public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var document = JsonDocument.ParseValue(ref reader))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return null;
                var result = new List<T>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    try
                    {
                        var item = element.ValueKind == JsonValueKind.Object ? element.Deserialize<T>(options) : null;
                        if (item != null)
                            result.Add(item);
                    }
                    catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is NotSupportedException || ex is FormatException)
                    {
                        // 이 항목만 버린다
                    }
                }
                return result;
            }
        }

        public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
