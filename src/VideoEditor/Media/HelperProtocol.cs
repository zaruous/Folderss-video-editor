using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoEditor.Media
{
    /// <summary>
    /// 플러그인과 미리 보기 도우미 프로세스가 주고받는 메시지(도우미 프로젝트가 이 파일을 링크해 같이 쓴다).
    /// 요청(플러그인 → 도우미 표준 입력): [길이 4][JSON]. 응답(도우미 → 플러그인 표준 출력): [길이 4][id 4][종류 1][내용].
    /// 길이는 뒤따르는 바이트 수(응답은 id·종류 포함). 숫자는 리틀 엔디언.
    /// </summary>
    public static class HelperProtocol
    {
        /// <summary>메시지 형식 판 — 도우미의 준비 응답에 들어 있어, 다른 판이면 플러그인이 도우미를 쓰지 않는다.</summary>
        public const int Version = 1;

        public const string OpFrame = "frame";
        public const string OpStream = "stream";
        public const string OpStop = "stop";
        public const string OpQuit = "quit";

        public const byte KindReady = (byte)'R';
        public const byte KindFrame = (byte)'F';
        public const byte KindDone = (byte)'D';
        public const byte KindError = (byte)'E';
        public const byte KindLog = (byte)'L';

        /// <summary>메시지 하나의 상한(4K BGRA 한 장 약 33MB에 여유).</summary>
        public const int MaxMessage = 256 << 20;

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void WriteRequest(Stream stream, HelperRequest request)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            var header = new byte[4];
            BitConverter.TryWriteBytes(header, body.Length);
            stream.Write(header, 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>요청 하나를 읽는다. 흐름이 끝났으면 null.</summary>
        public static HelperRequest ReadRequest(Stream stream)
        {
            var header = new byte[4];
            if (!ReadExactly(stream, header, 4))
                return null;
            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxMessage)
                throw new InvalidDataException("요청 길이가 잘못되었습니다: " + length);
            var body = new byte[length];
            if (!ReadExactly(stream, body, length))
                return null;
            return JsonSerializer.Deserialize<HelperRequest>(body, Json);
        }

        public static void WriteResponse(Stream stream, int id, byte kind, ReadOnlySpan<byte> payload)
        {
            var header = new byte[9];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), payload.Length + 5);
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), id);
            header[8] = kind;
            stream.Write(header, 0, 9);
            if (payload.Length > 0)
                stream.Write(payload);
            stream.Flush();
        }

        public static void WriteResponse(Stream stream, int id, byte kind, string text)
        {
            WriteResponse(stream, id, kind, Encoding.UTF8.GetBytes(text ?? ""));
        }

        /// <summary>응답 하나를 읽는다. 흐름이 끝났으면 false.</summary>
        public static bool ReadResponse(Stream stream, out int id, out byte kind, out byte[] payload)
        {
            id = 0;
            kind = 0;
            payload = null;
            var header = new byte[9];
            if (!ReadExactly(stream, header, 9))
                return false;
            var length = BitConverter.ToInt32(header, 0);
            if (length < 5 || length > MaxMessage)
                throw new InvalidDataException("응답 길이가 잘못되었습니다: " + length);
            id = BitConverter.ToInt32(header, 4);
            kind = header[8];
            payload = new byte[length - 5];
            return payload.Length == 0 || ReadExactly(stream, payload, payload.Length);
        }

        public static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        /// <summary>화면 한 장 응답: [너비 4][높이 4][BGRA].</summary>
        public static byte[] FramePayload(int width, int height, byte[] bgra)
        {
            var payload = new byte[8 + bgra.Length];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), width);
            BitConverter.TryWriteBytes(payload.AsSpan(4, 4), height);
            Buffer.BlockCopy(bgra, 0, payload, 8, bgra.Length);
            return payload;
        }
    }

    /// <summary>플러그인 → 도우미 요청.</summary>
    public sealed class HelperRequest
    {
        public string Op { get; set; }
        public int Id { get; set; }
        /// <summary>frame·stream: 그릴 계획.</summary>
        public HelperPlan Plan { get; set; }
        /// <summary>stream: 결과를 쓸 명명 파이프 이름(플러그인이 서버, 도우미가 붙는다).</summary>
        public string Pipe { get; set; }
    }

    /// <summary>
    /// 그릴 계획: <see cref="VideoEditor.Core.RenderPlan"/>의 입력 인수 묶음과 필터 그래프(FFmpeg 명령줄과 같은 것)에 출력 조건을 더한 것.
    /// 화면이면 [vout]을 Width×Height BGRA로, 소리면 [aout]을 SampleRate 16비트 스테레오 PCM으로 낸다.
    /// </summary>
    public sealed class HelperPlan
    {
        public List<List<string>> Inputs { get; set; } = new List<List<string>>();
        public string Filter { get; set; }
        public bool Video { get; set; } = true;
        public int Width { get; set; }
        public int Height { get; set; }
        public int SampleRate { get; set; } = 48000;
        /// <summary>낼 화면 장수(0 = 끝까지). 한 장 미리 보기는 1.</summary>
        public int MaxFrames { get; set; }
    }
}
