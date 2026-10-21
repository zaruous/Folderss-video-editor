using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class FrameRateTests
    {
        [Theory]
        [InlineData("30/1", 30, 1)]
        [InlineData("30000/1001", 30000, 1001)]
        [InlineData("60000/2002", 30000, 1001)]
        [InlineData("29.97", 30000, 1001)]
        [InlineData("23.976", 24000, 1001)]
        [InlineData("25", 25, 1)]
        [InlineData("12.5", 25, 2)]
        public void Parse(string text, int num, int den)
        {
            Assert.True(FrameRate.TryParse(text, out var rate));
            Assert.Equal(num, rate.Numerator);
            Assert.Equal(den, rate.Denominator);
        }

        [Theory]
        [InlineData("")]
        [InlineData("0/0")]
        [InlineData("abc")]
        [InlineData("-5")]
        public void Parse_Rejects(string text)
        {
            Assert.False(FrameRate.TryParse(text, out _));
        }

        [Fact]
        public void Seconds_And_Frames_RoundTrip()
        {
            var ntsc = new FrameRate(30000, 1001);
            Assert.Equal(1001.0 / 30000 * 30, ntsc.ToSeconds(30), 9);
            Assert.Equal(30, ntsc.ToFrames(ntsc.ToSeconds(30)));
            Assert.Equal("1.001", ntsc.SecondsText(30));
            Assert.Equal(150, FrameRate.Default.ToFrames(5));
        }

        [Theory]
        [InlineData(0, "00:00:00:00")]
        [InlineData(29, "00:00:00:29")]
        [InlineData(30, "00:00:01:00")]
        [InlineData(30 * 3661 + 5, "01:01:01:05")]
        public void Timecode(long frames, string expected)
        {
            Assert.Equal(expected, FrameRate.Default.Timecode(frames));
        }

        [Theory]
        [InlineData("00:00:01:00", 30)]
        [InlineData("01:00", 30)]
        [InlineData("1:02:03:04", (3600 + 120 + 3) * 30 + 4)]
        [InlineData("45", 45)]
        [InlineData("00:00:01.5", 45)]
        public void ParseTimecode(string text, long expected)
        {
            Assert.True(FrameRate.Default.TryParseTimecode(text, out var frames));
            Assert.Equal(expected, frames);
        }

        [Theory]
        [InlineData("00:00:00:30")]
        [InlineData("aa:bb")]
        [InlineData("")]
        public void ParseTimecode_Rejects(string text)
        {
            Assert.False(FrameRate.Default.TryParseTimecode(text, out _));
        }

        [Fact]
        public void Timecode_Ntsc_RoundTrips()
        {
            var ntsc = new FrameRate(30000, 1001);
            for (long frames = 0; frames < 5000; frames += 7)
            {
                Assert.True(ntsc.TryParseTimecode(ntsc.Timecode(frames), out var back));
                Assert.Equal(frames, back);
            }
        }
    }
}
