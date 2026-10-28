using System;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>
    /// 스티커 모음의 이름·키 규칙(파일은 Ui가 다룸): 기본 스티커는 플러그인 폴더 stickers\의 SVG, 내 스티커는 데이터 폴더 stickers\의 깨끗한 SVG·PNG.
    /// 내 스티커 파일 이름은 "이름-해시8.확장자"(같은 그림은 한 번만), 키는 "builtin:파일" · "mine:파일" — 키로 다른 폴더의 파일을 가리킬 수 없게 파일 이름만 받는다.
    /// </summary>
    public static class StickerLibrary
    {
        public const string BuiltinPrefix = "builtin:";
        public const string MinePrefix = "mine:";
        /// <summary>내 스티커 수 상한.</summary>
        public const int MaxMine = 200;
        /// <summary>그림(PNG) 스티커의 파일 크기·가로세로 상한.</summary>
        public const long MaxPictureBytes = 10 * 1024 * 1024;
        public const int MaxPicturePixels = 4096;

        /// <summary>파일 이름에 쓸 이름: 경로·예약 문자와 제어 문자를 빼고 앞뒤 점·빈칸을 자르고 40자까지(비면 "스티커").</summary>
        public static string FileStem(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            var text = new StringBuilder();
            foreach (var ch in name ?? "")
            {
                if (!char.IsControl(ch) && !invalid.Contains(ch))
                    text.Append(ch);
            }
            var stem = text.ToString().Trim().Trim('.').Trim();
            if (stem.Length > StickerSvg.MaxTitle)
                stem = stem.Substring(0, StickerSvg.MaxTitle).Trim();
            return stem.Length == 0 ? "스티커" : stem;
        }

        /// <summary>내 스티커 파일 이름: 이름-해시 앞 8자.확장자(.svg·.png).</summary>
        public static string MineFileName(string name, string hash, string extension)
        {
            var ext = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".svg";
            var tag = new string((hash ?? "").Where(Uri.IsHexDigit).Take(8).ToArray()).ToLowerInvariant();
            return FileStem(name) + "-" + tag.PadRight(8, '0') + ext;
        }

        /// <summary>내 스티커 파일 이름 → 보이는 이름(뒤의 -해시8과 확장자를 뺌).</summary>
        public static string NameFromFile(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
            var dash = stem.LastIndexOf('-');
            if (dash > 0 && stem.Length - dash - 1 == 8 && stem.Substring(dash + 1).All(Uri.IsHexDigit))
                stem = stem.Substring(0, dash);
            return stem.Length == 0 ? "스티커" : stem;
        }

        /// <summary>그림(PNG) 스티커 파일인지.</summary>
        public static bool IsPicture(string fileName)
        {
            return string.Equals(Path.GetExtension(fileName), ".png", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>스티커 파일로 쓸 수 있는 이름인지: 폴더·드라이브·상위 폴더가 없는 파일 이름이고 .svg(기본·내 것)나 .png(내 것만).</summary>
        public static bool IsFileName(string fileName, bool mine)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 120 || fileName != Path.GetFileName(fileName)
                || fileName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || fileName.StartsWith(".", StringComparison.Ordinal) || fileName.Any(char.IsControl))
                return false;
            var ext = Path.GetExtension(fileName);
            return string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase) || mine && string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);
        }

        public static string Key(bool mine, string fileName)
        {
            return (mine ? MinePrefix : BuiltinPrefix) + fileName;
        }

        /// <summary>키 → 내 것인지·파일 이름. 규칙에 맞지 않으면 false.</summary>
        public static bool TryParseKey(string key, out bool mine, out string fileName)
        {
            mine = false;
            fileName = null;
            if (key == null)
                return false;
            if (key.StartsWith(MinePrefix, StringComparison.Ordinal))
            {
                mine = true;
                fileName = key.Substring(MinePrefix.Length);
            }
            else if (key.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
            {
                fileName = key.Substring(BuiltinPrefix.Length);
            }
            else
            {
                return false;
            }
            if (IsFileName(fileName, mine))
                return true;
            fileName = null;
            return false;
        }
    }
}
