using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>최근 프로젝트 하나: 전체 경로와 마지막으로 열거나 저장한 시각(현지 시각).</summary>
    public sealed class RecentProject
    {
        [JsonPropertyName("path")]
        public string Path { get; set; }

        [JsonPropertyName("at")]
        public DateTime OpenedAt { get; set; }
    }

    /// <summary>
    /// 파일 › 최근 프로젝트 열기의 목록(플러그인 설정에 JSON 배열로 — 최근 검색어 <see cref="YtDlpText.AddRecent"/>와 같은 방식).
    /// 최근 것부터 <see cref="Max"/>개, 같은 경로(대소문자 무시)는 하나만. 깨진 값은 빈 목록으로 읽는다.
    /// </summary>
    public static class RecentProjects
    {
        public const int Max = 10;

        /// <summary>앞의 몇 개에 1~9 접근 키를 붙이는지.</summary>
        public const int AccessKeys = 9;

        /// <summary>경로를 맨 앞에 넣는다(같은 경로는 앞으로 옮기고 시각을 새로). 빈 경로면 그대로.</summary>
        public static string Add(string stored, string path, DateTime openedAt, int max = Max)
        {
            path = (path ?? "").Trim();
            var list = Read(stored);
            if (path.Length > 0)
            {
                list.RemoveAll(r => SamePath(r.Path, path));
                list.Insert(0, new RecentProject { Path = path, OpenedAt = openedAt });
            }
            return Write(list.Take(max));
        }

        /// <summary>경로 하나를 뺀다(옮기거나 지운 파일).</summary>
        public static string Remove(string stored, string path)
        {
            var list = Read(stored);
            list.RemoveAll(r => SamePath(r.Path, path));
            return Write(list);
        }

        public static List<RecentProject> Read(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return new List<RecentProject>();
            try
            {
                var list = JsonSerializer.Deserialize<List<RecentProject>>(stored) ?? new List<RecentProject>();
                // 손으로 고친 값 등: 빈 경로는 버리고 같은 경로는 앞의 것만
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return list.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path) && seen.Add(r.Path.Trim()))
                    .Select(r => new RecentProject { Path = r.Path.Trim(), OpenedAt = r.OpenedAt })
                    .Take(Max).ToList();
            }
            catch (JsonException)
            {
                return new List<RecentProject>();
            }
        }

        public static bool SamePath(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 메뉴에 보일 이름: 파일 이름(확장자 없이). 같은 이름이 둘 이상이면 " — 폴더 이름"을 붙이고, 그래도 같으면 폴더 전체 경로를 붙인다.
        /// </summary>
        public static List<string> Labels(IList<string> paths)
        {
            var names = paths.Select(p => Path.GetFileNameWithoutExtension(p ?? "")).ToList();
            var labels = new List<string>(names);
            for (var i = 0; i < paths.Count; i++)
            {
                if (names.Count(n => string.Equals(n, names[i], StringComparison.OrdinalIgnoreCase)) < 2)
                    continue;
                var folder = Path.GetDirectoryName(paths[i]) ?? "";
                labels[i] = names[i] + " — " + Path.GetFileName(folder);
            }
            var withFolder = new List<string>(labels);
            for (var i = 0; i < paths.Count; i++)
            {
                if (withFolder.Count(l => string.Equals(l, withFolder[i], StringComparison.OrdinalIgnoreCase)) >= 2)
                    labels[i] = names[i] + " — " + (Path.GetDirectoryName(paths[i]) ?? "");
            }
            return labels;
        }

        /// <summary>메뉴 머리글: 앞의 9개는 "_1 이름"(1~9 접근 키). 이름의 '_'는 접근 키로 읽히지 않게 겹친다.</summary>
        public static string MenuHeader(int index, string label)
        {
            var text = (label ?? "").Replace("_", "__");
            return index < AccessKeys ? "_" + (index + 1) + " " + text : (index + 1) + " " + text;
        }

        /// <summary>
        /// 네트워크 경로(\\서버\공유, //서버/공유)인지 — 있는지 확인이 몇 초씩 걸릴 수 있어 메뉴를 열 때 UI 스레드에서 확인하지 않는다.
        /// 연결한 네트워크 드라이브(Z: 등)는 드라이브 종류로 따로 가린다(화면 쪽).
        /// </summary>
        public static bool IsUncPath(string path)
        {
            path = path ?? "";
            return path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        }

        private static string Write(IEnumerable<RecentProject> list)
        {
            return JsonSerializer.Serialize(list.ToList());
        }
    }
}
