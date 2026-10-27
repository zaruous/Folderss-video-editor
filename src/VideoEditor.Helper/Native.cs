using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VideoEditor.Helper
{
    /// <summary>FFmpeg 호출이 실패함 — 오류 코드와 그때까지의 FFmpeg 기록.</summary>
    internal sealed class FfmpegException : Exception
    {
        public FfmpegException(string what, int code)
            : base(what + " 실패(" + code + ": " + Native.ErrorText(code) + ")" + Log.Suffix())
        {
            Code = code;
        }

        public int Code { get; }
    }

    internal static unsafe class Native
    {
        public static int Check(int result, string what)
        {
            if (result < 0)
                throw new FfmpegException(what, result);
            return result;
        }

        public static string ErrorText(int code)
        {
            var buffer = stackalloc byte[256];
            ffmpeg.av_strerror(code, buffer, 256);
            return Marshal.PtrToStringUTF8((IntPtr)buffer);
        }

        public static string Text(byte* text)
        {
            return text == null ? null : Marshal.PtrToStringUTF8((IntPtr)text);
        }

        public static bool IsAgain(int result)
        {
            return result == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        }

        public static AVRational Q(int num, int den)
        {
            return new AVRational { num = num, den = den };
        }

        public static string N(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 필터 옵션을 새 이름(FFmpeg 7.1부터의 목록 옵션 — "bgra", "s16", "48000", "stereo" 같은 글)으로 주고, 없으면 옛 이름(이진 배열)으로 준다.
        /// </summary>
        public static void SetList(AVFilterContext* context, string name, string value, string legacyName, void* legacyValue, int legacySize)
        {
            var result = ffmpeg.av_opt_set(context, name, value, ffmpeg.AV_OPT_SEARCH_CHILDREN);
            if (result == ffmpeg.AVERROR_OPTION_NOT_FOUND && legacyName != null)
                result = ffmpeg.av_opt_set_bin(context, legacyName, (byte*)legacyValue, legacySize, ffmpeg.AV_OPT_SEARCH_CHILDREN);
            Check(result, "옵션 " + name);
        }

        public static void SetText(AVFilterContext* context, string name, string value, string legacyName)
        {
            var result = ffmpeg.av_opt_set(context, name, value, ffmpeg.AV_OPT_SEARCH_CHILDREN);
            if (result == ffmpeg.AVERROR_OPTION_NOT_FOUND && legacyName != null)
                result = ffmpeg.av_opt_set(context, legacyName, value, ffmpeg.AV_OPT_SEARCH_CHILDREN);
            Check(result, "옵션 " + name);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectoryW(string path);
    }

    /// <summary>FFmpeg 기록(경고 이상)을 마지막 몇 줄만 들고 있다가 오류 메시지에 붙인다.</summary>
    internal static unsafe class Log
    {
        private const int Keep = 12;
        private static readonly object Gate = new object();
        private static readonly LinkedList<string> Lines = new LinkedList<string>();
        // GC가 거두지 않게 붙들어 둔다
        private static av_log_set_callback_callback _callback;

        public static void Install()
        {
            _callback = Callback;
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            ffmpeg.av_log_set_callback(_callback);
        }

        private static void Callback(void* context, int level, string format, byte* arguments)
        {
            if (level > ffmpeg.AV_LOG_WARNING)
                return;
            try
            {
                var buffer = stackalloc byte[1024];
                var prefix = 1;
                ffmpeg.av_log_format_line(context, level, format, arguments, buffer, 1024, &prefix);
                var text = Marshal.PtrToStringUTF8((IntPtr)buffer)?.Trim();
                if (string.IsNullOrEmpty(text))
                    return;
                lock (Gate)
                {
                    Lines.AddLast(text);
                    while (Lines.Count > Keep)
                        Lines.RemoveFirst();
                }
            }
            catch (Exception)
            {
                // 기록은 보조 수단이다
            }
        }

        public static string Recent()
        {
            lock (Gate)
                return string.Join("\n", Lines);
        }

        public static string Suffix()
        {
            var recent = Recent();
            return recent.Length == 0 ? "" : "\n" + recent;
        }

        public static void Clear()
        {
            lock (Gate)
                Lines.Clear();
        }
    }
}
