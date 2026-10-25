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
        public string Url { get { return SpeechText.ModelBaseUrl + File; } }
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

        /// <summary>whisper-cli 인수: 16kHz 모노 WAV를 읽어 outputBase.srt를 쓰고 진행률을 표준 오류로 알린다.</summary>
        public static IReadOnlyList<string> Arguments(string model, string wav, string outputBase, string language, int threads)
        {
            return new[]
            {
                "-m", model, "-f", wav, "-l", Language(language), "-t", Math.Max(1, threads).ToString(CultureInfo.InvariantCulture),
                "-osrt", "-of", outputBase, "-pp"
            };
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

        private static readonly Regex SentenceEnd = new Regex(@"(?<=[.?!,。？！，…])\s+", RegexOptions.CultureInvariant);

        /// <summary>
        /// 한 번에 읽기 긴 큐(두 문장 이상을 한 큐로 묶은 것)를 문장·쉼표 끝에서 나눈다: 조각을 maxChars자까지 이어 붙이고 시간은 글자 수에 비례해 나눈다.
        /// 나눌 곳이 없으면 그대로. 나눈 줄 끝의 쉼표는 지운다.
        /// </summary>
        public static List<SubtitleCue> SplitLong(IEnumerable<SubtitleCue> cues, int maxChars = 32)
        {
            var result = new List<SubtitleCue>();
            foreach (var cue in cues)
            {
                var text = (cue.Text ?? "").Trim();
                var lines = new List<string>();
                if (text.Length > maxChars)
                {
                    foreach (var piece in SentenceEnd.Split(text).Where(p => p.Length > 0))
                    {
                        if (lines.Count > 0 && lines[lines.Count - 1].Length + 1 + piece.Length <= maxChars)
                            lines[lines.Count - 1] += " " + piece;
                        else
                            lines.Add(piece);
                    }
                }
                if (lines.Count < 2)
                {
                    result.Add(new SubtitleCue { Start = cue.Start, End = cue.End, Text = text });
                    continue;
                }
                var total = (double)lines.Sum(l => l.Length);
                var at = cue.Start;
                for (var i = 0; i < lines.Count; i++)
                {
                    var end = i == lines.Count - 1 ? cue.End : at + (cue.End - cue.Start) * lines[i].Length / total;
                    result.Add(new SubtitleCue { Start = at, End = end, Text = lines[i].TrimEnd(',', '，') });
                    at = end;
                }
            }
            return result;
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
