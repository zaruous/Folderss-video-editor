using System;
using System.IO;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>파일 › 최근 프로젝트 열기의 목록(#41): 더하기·같은 경로 앞으로·상한·대소문자, 깨진 설정 값, 메뉴 이름.</summary>
    public class RecentProjectsTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "recent-projects");
        private static readonly DateTime At = new DateTime(2026, 10, 28, 9, 30, 0);

        private static string P(params string[] parts)
        {
            return Path.Combine(new[] { Root }.Concat(parts).ToArray());
        }

        [Fact]
        public void Add_NewestFirstWithTime()
        {
            var stored = RecentProjects.Add(null, P("a.fvproj"), At);
            stored = RecentProjects.Add(stored, P("b.fvproj"), At.AddMinutes(5));
            var list = RecentProjects.Read(stored);
            Assert.Equal(new[] { P("b.fvproj"), P("a.fvproj") }, list.Select(r => r.Path));
            Assert.Equal(At.AddMinutes(5), list[0].OpenedAt);
            Assert.Equal(At, list[1].OpenedAt);
        }

        [Fact]
        public void Add_SamePathMovesToFrontOnce_IgnoringCase()
        {
            var stored = RecentProjects.Add(null, P("a.fvproj"), At);
            stored = RecentProjects.Add(stored, P("b.fvproj"), At.AddMinutes(1));
            stored = RecentProjects.Add(stored, " " + P("A.FVPROJ") + " ", At.AddMinutes(2));
            var list = RecentProjects.Read(stored);
            Assert.Equal(2, list.Count);
            Assert.Equal(P("A.FVPROJ"), list[0].Path);
            Assert.Equal(At.AddMinutes(2), list[0].OpenedAt);
            Assert.Equal(P("b.fvproj"), list[1].Path);
        }

        [Fact]
        public void Add_KeepsTenNewest()
        {
            string stored = null;
            for (var i = 0; i < 12; i++)
                stored = RecentProjects.Add(stored, P("p" + i + ".fvproj"), At.AddMinutes(i));
            var list = RecentProjects.Read(stored);
            Assert.Equal(RecentProjects.Max, list.Count);
            Assert.Equal(P("p11.fvproj"), list[0].Path);
            Assert.Equal(P("p2.fvproj"), list[9].Path);
            Assert.DoesNotContain(list, r => r.Path == P("p1.fvproj"));
        }

        [Fact]
        public void Add_EmptyPathLeavesListAlone()
        {
            var stored = RecentProjects.Add(null, P("a.fvproj"), At);
            Assert.Single(RecentProjects.Read(RecentProjects.Add(stored, "  ", At)));
            Assert.Single(RecentProjects.Read(RecentProjects.Add(stored, null, At)));
        }

        [Fact]
        public void Remove_DropsPathIgnoringCase()
        {
            var stored = RecentProjects.Add(null, P("a.fvproj"), At);
            stored = RecentProjects.Add(stored, P("b.fvproj"), At);
            stored = RecentProjects.Remove(stored, P("A.fvproj"));
            Assert.Equal(new[] { P("b.fvproj") }, RecentProjects.Read(stored).Select(r => r.Path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"path\":\"x\"}")]
        [InlineData("[1, 2]")]
        [InlineData("[{\"path\":\"x\",\"at\":\"어제\"}]")]
        public void Read_BrokenSettingIsEmpty(string stored)
        {
            Assert.Empty(RecentProjects.Read(stored));
        }

        [Fact]
        public void Read_SkipsBlankAndDuplicatePaths()
        {
            var json = "[{\"path\":\"\"},null,{\"path\":\"x.fvproj\"},{\"path\":\"X.fvproj\"},{\"path\":\"y.fvproj\"}]";
            Assert.Equal(new[] { "x.fvproj", "y.fvproj" }, RecentProjects.Read(json).Select(r => r.Path));
        }

        [Fact]
        public void Labels_NameWithoutExtension_FolderOnlyForSameNames()
        {
            var paths = new[] { P("work", "intro.fvproj"), P("old", "intro.fvproj"), P("work", "trip.fvproj") };
            Assert.Equal(new[] { "intro — work", "intro — old", "trip" }, RecentProjects.Labels(paths));

            // 폴더 이름까지 같으면 폴더 전체 경로
            var deep = new[] { P("a", "same", "x.fvproj"), P("b", "same", "x.fvproj") };
            Assert.Equal(new[] { "x — " + P("a", "same"), "x — " + P("b", "same") }, RecentProjects.Labels(deep));
        }

        [Fact]
        public void MenuHeader_AccessKeysForFirstNine_EscapesUnderscore()
        {
            Assert.Equal("_1 intro", RecentProjects.MenuHeader(0, "intro"));
            Assert.Equal("_9 my__clip", RecentProjects.MenuHeader(8, "my_clip"));
            Assert.Equal("10 last", RecentProjects.MenuHeader(9, "last"));
        }

        [Theory]
        [InlineData(@"\\server\share\a.fvproj", true)]
        [InlineData("//server/share/a.fvproj", true)]
        [InlineData(@"D:\work\a.fvproj", false)]
        [InlineData("/home/a.fvproj", false)]
        [InlineData(null, false)]
        public void IsUncPath(string path, bool unc)
        {
            Assert.Equal(unc, RecentProjects.IsUncPath(path));
        }
    }
}
