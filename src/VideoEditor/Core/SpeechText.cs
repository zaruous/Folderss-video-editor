using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoEditor.Core
{
    /// <summary>음성 인식 모델(whisper.cpp ggml 파일) 하나. 크기·SHA-256은 고정해 두고 내려받은 파일과 맞춰 본다.</summary>
    public sealed class SpeechModel
    {
        public string Id { get; set; }
        public string File { get; set; }
        /// <summary>창에 보일 이름(빠름·보통·정확).</summary>
        public string Label { get; set; }
        public string Note { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        /// <summary>내려받을 곳(없으면 whisper.cpp 모델 저장소).</summary>
        public string BaseUrl { get; set; }
        public string Url { get { return (BaseUrl ?? SpeechText.ModelBaseUrl) + File; } }
    }

    /// <summary>
    /// 음성 인식 자막(whisper.cpp)에 쓰는 것: 내려받을 릴리스·모델 고르기, 명령 인수, 진행률 읽기, 인식한 큐를 타임라인 시각으로 옮기기.
    /// 프로세스·네트워크는 쓰지 않는다(Media.SpeechRecognizer가 돌린다).
    /// </summary>
    public static class SpeechText
    {
        public const string ReleasesApi = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=30";
        /// <summary>CPU 판 Windows 실행 파일 묶음(약 9MB).</summary>
        public const string BinaryAsset = "whisper-bin-x64.zip";
        public const string CliName = "whisper-cli.exe";
        public const string ModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
        /// <summary>Visual C++ 런타임이 없어 시작하지 못함(STATUS_DLL_NOT_FOUND).</summary>
        public const int DllNotFound = unchecked((int)0xC0000135);
        public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        /// <summary>고를 수 있는 모델(첫째가 기본). 크기·SHA-256은 Hugging Face ggerganov/whisper.cpp의 값.</summary>
        public static IReadOnlyList<SpeechModel> Models { get; } = new[]
        {
            new SpeechModel
            {
                Id = "small", File = "ggml-small-q5_1.bin", Label = "보통(추천)", Note = "190MB · 한국어도 대체로 맞음(가끔 틀림)",
                Size = 190085487, Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"
            },
            new SpeechModel
            {
                Id = "turbo", File = "ggml-large-v3-turbo-q5_0.bin", Label = "정확", Note = "574MB · 가장 정확, 보통보다 3~4배 느림",
                Size = 574041195, Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"
            },
            new SpeechModel
            {
                Id = "base", File = "ggml-base-q5_1.bin", Label = "빠름", Note = "60MB · 빠르지만 한국어는 자주 틀림",
                Size = 59707625, Sha256 = "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898"
            }
        };

        /// <summary>
        /// 말소리 구간 감지(Silero VAD, 약 0.9MB) — 말이 있는 곳만 인식해 음악·잡음 속에서 시각이 밀리거나 말을 지어내는 일을 줄인다.
        /// 배경 음악이 섞인 내레이션에서 첫 큐가 0초로 밀리던 것이 실제 말 시작(2초)에 맞는다.
        /// </summary>
        public static SpeechModel VadModel { get; } = new SpeechModel
        {
            Id = "vad", File = "ggml-silero-v6.2.0.bin", Label = "말소리 구간 감지", Note = "0.9MB",
            Size = 885098, Sha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
            BaseUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/"
        };

        /// <summary>인식할 말(whisper 언어 코드, 보일 이름). 첫째가 기본.</summary>
        public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new[]
        {
            ("ko", "한국어"), ("auto", "자동으로 알아보기"), ("en", "English"), ("ja", "日本語"), ("zh", "中文")
        };

        public static SpeechModel Model(string id)
        {
            return Models.FirstOrDefault(m => m.Id == id) ?? Models[0];
        }

        public static string Language(string code)
        {
            return Languages.Any(l => l.Code == code) ? code : Languages[0].Code;
        }

        /// <summary>
        /// 릴리스 목록 JSON에서 내려받을 실행 파일 묶음. whisper.cpp는 버전 태그(v1.x)에는 파일이 없고 같은 때 만든 빌드 릴리스(bNNNN)에 붙인다 —
        /// 최신 정식 버전과 같은 때(6시간 안) 만든 빌드를, 없으면 파일이 있는 가장 새 릴리스를 고른다.
        /// GitHub 주소이고 SHA-256(digest)이 적힌 파일만. 없으면 null.
        /// </summary>
        public static GitHubAsset ChooseRelease(string releasesJson, string assetName)
        {
            using (var document = JsonDocument.Parse(releasesJson))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return null;
                var releases = new List<(string Tag, DateTime Published, bool Prerelease, GitHubAsset Asset)>();
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft"))
                        continue;
                    DateTime.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published);
                    GitHubAsset found = null;
                    if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != assetName)
                                continue;
                            var url = Text(asset, "browser_download_url");
                            var digest = Text(asset, "digest") ?? "";
                            var sha = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7).ToLowerInvariant() : null;
                            if (FfmpegText.FromGitHub(url) && sha != null && Regex.IsMatch(sha, "^[0-9a-f]{64}$")
                                && asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number)
                                found = new GitHubAsset { Name = assetName, Url = url, Size = size.GetInt64(), Sha256 = sha };
                        }
                    }
                    releases.Add((Text(release, "tag_name") ?? "", published, Flag(release, "prerelease"), found));
                }
                var candidates = releases.Where(r => r.Asset != null).OrderByDescending(r => r.Published).ToList();
                if (candidates.Count == 0)
                    return null;
                var stable = releases.Where(r => !r.Prerelease && r.Tag.StartsWith("v", StringComparison.Ordinal)).OrderByDescending(r => r.Published).FirstOrDefault();
                if (stable.Tag != null)
                {
                    var match = candidates.FirstOrDefault(r => r.Published <= stable.Published.AddHours(6));
                    if (match.Asset != null)
                        return match.Asset;
                }
                return candidates[0].Asset;
            }
        }

        /// <summary>실행 파일 묶음(zip)에서 풀 파일: whisper-cli.exe와 그것이 쓰는 whisper·ggml DLL(폴더 없이 이름만).</summary>
        public static bool ShouldExtract(string entryName)
        {
            var name = Path.GetFileName(entryName ?? "");
            return name.Equals(CliName, StringComparison.OrdinalIgnoreCase)
                   || name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase)
                   || (name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                       && name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'));
        }

        /// <summary>
        /// whisper-cli 인수: 16kHz 모노 WAV를 읽어 outputBase.srt에 단어(어절)마다 한 큐를 쓰고(-ml 1 -sow) 진행률을 표준 오류로 알린다.
        /// vad가 있으면 말소리 구간만, prompt(자주 나오는 낱말)가 있으면 그 낱말을 앞 문맥으로 준다.
        /// </summary>
        public static IReadOnlyList<string> Arguments(string model, string wav, string outputBase, string language, int threads, string vad = null, string prompt = null)
        {
            var args = new List<string>
            {
                "-m", model, "-f", wav, "-l", Language(language), "-t", Math.Max(1, threads).ToString(CultureInfo.InvariantCulture),
                "-ml", "1", "-sow", "-osrt", "-of", outputBase, "-pp"
            };
            if (!string.IsNullOrEmpty(vad))
                args.AddRange(new[] { "--vad", "-vm", vad });
            var hint = CleanPrompt(prompt);
            if (hint.Length > 0)
                args.AddRange(new[] { "--prompt", hint });
            return args;
        }

        public const int MaxPromptLength = 200;

        /// <summary>자주 나오는 낱말(이름·용어): 줄바꿈 등 제어 문자를 공백으로, 200자까지.</summary>
        public static string CleanPrompt(string prompt)
        {
            var text = new string((prompt ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            return text.Length > MaxPromptLength ? text.Substring(0, MaxPromptLength) : text;
        }

        /// <summary>인식에 쓸 스레드 수(코어 하나는 남기고 8개까지 — 그보다 많으면 오히려 느려짐).</summary>
        public static int Threads(int processors)
        {
            return Math.Max(1, Math.Min(8, processors - 1));
        }

        private static readonly Regex ProgressPattern = new Regex(@"progress\s*=\s*(\d{1,3})\s*%", RegexOptions.CultureInvariant);

        /// <summary>"whisper_print_progress_callback: progress =  37%" → 37. 아니면 null.</summary>
        public static int? ParseProgress(string line)
        {
            var match = ProgressPattern.Match(line ?? "");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 100 ? value : (int?)null;
        }

        /// <summary>
        /// 인식한 큐(소리 파일 처음 기준 초)를 타임라인 초로: start + t / speed. [start, end) 밖은 자르고 버리며, 앞뒤 공백을 지우고
        /// 빈 큐와 말이 아닌 소리 표시(괄호로만 된 "[음악]"·"(박수)", ♪)는 뺀다 — 음악·잡음에서 모델이 지어내는 것.
        /// </summary>
        public static List<SubtitleCue> ToTimeline(IEnumerable<SubtitleCue> cues, double start, double speed, double end)
        {
            var scale = speed > 0 ? 1 / speed : 1;
            var result = new List<SubtitleCue>();
            foreach (var cue in cues)
            {
                var text = (cue.Text ?? "").Trim();
                var from = start + Math.Max(0, cue.Start) * scale;
                var to = Math.Min(end, start + Math.Max(0, cue.End) * scale);
                if (text.Length == 0 || IsNoise(text) || from >= end || to <= from)
                    continue;
                result.Add(new SubtitleCue { Start = from, End = to, Text = text });
            }
            return result;
        }

        private static readonly Regex NoisePattern = new Regex(@"^([\[(（【][^\])）】]*[\])）】]|[♪♫\s~.]+)$", RegexOptions.CultureInvariant);

        /// <summary>말이 아닌 소리 표시: 괄호 하나로 감싼 글 또는 음표·물결·점만.</summary>
        public static bool IsNoise(string text)
        {
            return NoisePattern.IsMatch((text ?? "").Trim());
        }

        private static readonly char[] SentenceEnds = { '.', '?', '!', '。', '？', '！', '…' };

        /// <summary>
        /// 단어(어절)를 자막 큐로 묶는다. 먼저 문장(문장 끝 . ? ! 또는 maxGap초 넘는 쉼까지)으로 모으고,
        /// maxChars자(두 줄 남짓)를 넘는 문장은 비슷한 길이로 나눈다(쉼표 뒤, 다음으로 이음 어미 -고·-며·-서 뒤를 먼저 고름 — 한 단어만 남는 꼬리가 생기지 않게).
        /// 큐는 첫 단어 시작부터 마지막 단어 끝까지이고, 다음 큐까지 짧게(linger초 미만) 비면 그 틈까지 늘여 깜빡이지 않게 한다.
        /// 큐 끝 쉼표는 지우고, 말이 아닌 소리 표시는 뺀다.
        /// </summary>
        public static List<SubtitleCue> Group(IReadOnlyList<SubtitleCue> words, int maxChars = 40, double maxGap = 0.6, double linger = 0.5)
        {
            var cues = new List<SubtitleCue>();
            var sentence = new List<SubtitleCue>();
            foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && !IsNoise(w.Text)))
            {
                if (sentence.Count > 0 && word.Start - sentence[sentence.Count - 1].End > maxGap)
                {
                    AddSentence(sentence, maxChars, cues);
                    sentence.Clear();
                }
                sentence.Add(word);
                if (SentenceEnds.Contains(word.Text[word.Text.Length - 1]))
                {
                    AddSentence(sentence, maxChars, cues);
                    sentence.Clear();
                }
            }
            AddSentence(sentence, maxChars, cues);
            for (var i = 0; i + 1 < cues.Count; i++)
            {
                if (cues[i + 1].Start - cues[i].End < linger)
                    cues[i].End = Math.Max(cues[i].End, cues[i + 1].Start);
            }
            return cues;
        }

        /// <summary>문장 하나를 maxChars자 이하 조각(가능하면)으로 나눠 큐로 더한다 — 조각 수를 늘려 가며 비슷한 길이로.</summary>
        private static void AddSentence(List<SubtitleCue> sentence, int maxChars, List<SubtitleCue> cues)
        {
            if (sentence.Count == 0)
                return;
            // ends[i] = 단어 0~i를 공백으로 이은 글자 수
            var ends = new int[sentence.Count];
            for (var i = 0; i < sentence.Count; i++)
                ends[i] = (i == 0 ? 0 : ends[i - 1] + 1) + sentence[i].Text.Length;
            List<int> breaks;
            for (var parts = Math.Max(1, (ends[ends.Length - 1] + maxChars - 1) / maxChars); ; parts++)
            {
                breaks = Balanced(sentence, ends, parts);
                if (parts >= sentence.Count || Parts(ends, breaks).All(length => length <= maxChars))
                    break;
            }
            var from = 0;
            foreach (var end in breaks.Concat(new[] { sentence.Count - 1 }))
            {
                var part = sentence.GetRange(from, end - from + 1);
                var text = string.Join(" ", part.Select(w => w.Text)).TrimEnd(',', '，');
                if (!IsNoise(text))
                    cues.Add(new SubtitleCue { Start = part[0].Start, End = part[part.Count - 1].End, Text = text, Words = part });
                from = end + 1;
            }
        }

        /// <summary>parts조각이 되게 끊을 단어 위치(그 단어 뒤에서 끊음): 길이 비율 k/parts에 가장 가까운 곳, 쉼표·이음 어미로 끝난 단어면 더 낫게 본다.</summary>
        private static List<int> Balanced(List<SubtitleCue> words, int[] ends, int parts)
        {
            var breaks = new List<int>();
            var total = ends[ends.Length - 1];
            var from = 0;
            for (var k = 1; k < parts; k++)
            {
                var target = total * k / (double)parts;
                var best = from;
                var bestScore = double.MaxValue;
                // 남은 조각마다 한 단어는 남긴다
                for (var i = from; i < words.Count - (parts - k); i++)
                {
                    var last = words[i].Text[words[i].Text.Length - 1];
                    var bonus = last == ',' || last == '，' ? total / (3.0 * parts) : ClauseEnds.IndexOf(last) >= 0 ? total / (4.0 * parts) : 0;
                    var score = Math.Abs(ends[i] - target) - bonus;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
                breaks.Add(best);
                from = best + 1;
            }
            return breaks;
        }

        // 한국어 이음 어미(잡고·내려놓으며·억울해서·보면·인데·지만) — 절이 끝나는 곳
        private const string ClauseEnds = "고며서면데만";

        private static IEnumerable<int> Parts(int[] ends, List<int> breaks)
        {
            var previous = -1;
            foreach (var end in breaks.Concat(new[] { ends.Length - 1 }))
            {
                yield return ends[end] - (previous < 0 ? 0 : ends[previous] + 1);
                previous = end;
            }
        }

        private static string Text(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool Flag(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }
    }
}
