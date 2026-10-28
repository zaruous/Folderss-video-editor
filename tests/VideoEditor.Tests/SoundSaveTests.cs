using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>사운드를 파일로 저장 — 진행률·취소, 미디어 패널에서 여러 개·오디오 파일(#34).</summary>
    public class SoundSaveTests
    {
        private readonly ITestOutputHelper _out;

        public SoundSaveTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Core ----------

        [Fact]
        public void HasSound_VideoWithSoundAndAudioFiles()
        {
            Assert.True(SoundExtract.HasSound(TestProjects.Video()));
            Assert.True(SoundExtract.HasSound(TestProjects.Audio()));
            Assert.False(SoundExtract.HasSound(TestProjects.Video(audio: false)));
            Assert.False(SoundExtract.HasSound(TestProjects.Image()));
            Assert.False(SoundExtract.HasSound(null));
        }

        [Fact]
        public void FileName_IsSourceNamePlusCodecExtension()
        {
            Assert.Equal("여행 사운드.m4a", SoundExtract.FileName(@"C:\영상\여행.mp4", "aac"));
            Assert.Equal("노래 사운드.mp3", SoundExtract.FileName(@"C:\음악\노래.mp3", "mp3"));
            Assert.Equal("녹음 사운드.wav", SoundExtract.FileName("녹음.wav", "pcm_s16le"));
            Assert.Equal("사운드.mka", SoundExtract.FileName("", null));
        }

        [Fact]
        public void Unique_AddsANumberWhileTaken()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a 사운드.m4a", "A 사운드 (2).M4A" };
            Assert.Equal("a 사운드 (3).m4a", SoundExtract.Unique("a 사운드.m4a", taken.Contains));
            Assert.Equal("b 사운드.m4a", SoundExtract.Unique("b 사운드.m4a", taken.Contains));
            Assert.Equal("c (2)", SoundExtract.Unique("c", name => name == "c"));
        }

        [Fact]
        public void OutputSeconds_RangeOrRestOfSource_DividedBySpeed()
        {
            Assert.Equal(2, SoundExtract.OutputSeconds(0, 2, 1, 0));
            Assert.Equal(4, SoundExtract.OutputSeconds(1, 0, 2, 9));
            Assert.Equal(1, SoundExtract.OutputSeconds(0, 0.5, 0.5, 100));
            // 모르면 0(진행률 없이)
            Assert.Equal(0, SoundExtract.OutputSeconds(0, 0, 1, 0));
            Assert.Equal(0, SoundExtract.OutputSeconds(0, 2, 0, 0));
            Assert.Equal(0, SoundExtract.OutputSeconds(0, double.NaN, 1, double.NaN));
            Assert.Equal(0, SoundExtract.OutputSeconds(5, 0, 1, 3));
        }

        // ---------- FFmpeg ----------

        /// <summary>알린 값을 그대로 모은다(Progress&lt;T&gt;와 달리 부른 스레드에서 바로 — 차례가 섞이지 않게). 하나를 받으면 cancel을 부를 수 있다.</summary>
        private sealed class Recorder : IProgress<double>
        {
            private readonly Action _first;
            public readonly List<double> Values = new List<double>();

            public Recorder(Action first = null)
            {
                _first = first;
            }

            public void Report(double value)
            {
                lock (Values)
                {
                    Values.Add(value);
                    if (Values.Count == 1)
                        _first?.Invoke();
                }
            }
        }

        private static string Folder()
        {
            var folder = Path.Combine(FfmpegMedia.Folder, "sound-save-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static readonly Lazy<string> LazyLong = new Lazy<string>(() =>
        {
            // 20분 8kHz 모노 PCM(약 19MB) — 4분의 1 속도로 다시 인코딩하면 80분이라 몇 초 걸린다
            var path = Path.Combine(FfmpegMedia.Folder, "long-tone.wav");
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfmpegPath,
                new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000:duration=1200", "-ac", "1", "-c:a", "pcm_s16le", path },
                TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.Error);
            return path;
        });

        [FfmpegFact]
        public async Task Extract_ReportsProgressUpToOne()
        {
            var folder = Folder();
            var recorder = new Recorder();
            var saved = await SoundExtractor.Extract(FfmpegMedia.Tool, FfmpegMedia.TestVideo, "aac", Path.Combine(folder, "a.m4a"), 0, 0, 1, CancellationToken.None,
                progress: recorder, sourceSeconds: 2);
            _out.WriteLine("진행률 " + string.Join(" ", recorder.Values.Select(v => v.ToString("0.00"))));
            Assert.Equal(Path.Combine(folder, "a.m4a"), saved);
            Assert.NotEmpty(recorder.Values);
            Assert.All(recorder.Values, v => Assert.InRange(v, 0, 1));
            Assert.Equal(1, recorder.Values[recorder.Values.Count - 1]);
            // 다 되면 임시 파일이 없다
            Assert.Equal(new[] { "a.m4a" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        }

        [FfmpegFact]
        public async Task Extract_CancelledMidway_KeepsTheOldFile_LeavesNoTemp()
        {
            var folder = Folder();
            var output = Path.Combine(folder, "slow.wav");
            // 덮어쓰기로 고른 원래 파일
            File.WriteAllText(output, "예전 파일");
            using (var cancel = new CancellationTokenSource())
            {
                var recorder = new Recorder(cancel.Cancel);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    SoundExtractor.Extract(FfmpegMedia.Tool, LazyLong.Value, "pcm_s16le", output, 0, 0, 0.25, cancel.Token, progress: recorder, sourceSeconds: 1200));
                _out.WriteLine("취소까지 " + watch.ElapsedMilliseconds + "ms, 알린 값 " + string.Join(" ", recorder.Values.Select(v => v.ToString("0.000"))));
            }
            Assert.Equal("예전 파일", File.ReadAllText(output));
            Assert.Equal(new[] { "slow.wav" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        }

        [FfmpegFact]
        public async Task Extract_CancelledBeforeStart_WritesNothing()
        {
            var folder = Folder();
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    SoundExtractor.Extract(FfmpegMedia.Tool, FfmpegMedia.TestVideo, "aac", Path.Combine(folder, "b.m4a"), 0, 0, 1, cancel.Token));
            }
            Assert.Empty(Directory.GetFiles(folder));
        }

        [FfmpegFact]
        public async Task Extract_AudioFile_CopiesItsSound()
        {
            // 미디어 패널의 오디오 파일: 소리를 그대로 꺼낸다(PCM → .wav)
            var folder = Folder();
            var name = SoundExtract.FileName(FfmpegMedia.Tone, "pcm_s16le");
            var saved = await SoundExtractor.Extract(FfmpegMedia.Tool, FfmpegMedia.Tone, "pcm_s16le", Path.Combine(folder, name), 0, 0, 1, CancellationToken.None, sourceSeconds: 3);
            Assert.Equal(Path.Combine(folder, "tone 사운드.wav"), saved);
            var probe = await MediaProbe.Probe(FfmpegMedia.Tool, saved, CancellationToken.None);
            Assert.Equal("pcm_s16le", probe.AudioCodec);
            Assert.InRange(probe.DurationSeconds, 2.9, 3.1);
        }
    }
}
