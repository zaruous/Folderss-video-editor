using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 저장하지 않은 편집을 복구용으로 써 둔다(플러그인 데이터 폴더\recovery\&lt;창 Id&gt;.fvproj + 원래 파일 경로 .txt).
    /// Folderss를 끄면 플러그인 창도 함께 닫혀 "저장할까요?"를 물을 수 없으므로, 그때와 몇 분마다 써 두고 다음에 열 때 복구를 묻는다.
    /// 저장하거나 "저장 안 함"을 고르면 지운다.
    /// </summary>
    internal sealed class RecoveryStore
    {
        // 지금 열려 있는 편집 창의 Id(다른 창의 복구 파일을 가져가지 않게)
        private static readonly HashSet<string> Live = new HashSet<string>();
        private readonly string _folder;

        public RecoveryStore(EditorServices services)
        {
            _folder = Path.Combine(services.Manager.DataDirectory, "recovery");
            Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            lock (Live)
                Live.Add(Id);
        }

        public string Id { get; }

        private string ProjectPath { get { return Path.Combine(_folder, Id + ProjectFile.Extension); } }
        private string InfoPath { get { return Path.Combine(_folder, Id + ".txt"); } }

        /// <summary>지금 프로젝트를 복구 파일로 쓴다. 실패해도 편집은 계속한다(디스크 등).</summary>
        public void Write(EditorSession session)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                ProjectFile.Save(session.Project, ProjectPath);
                File.WriteAllText(InfoPath, session.FilePath ?? "");
            }
            catch (Exception)
            {
                // 복구용 사본은 보조 수단 — 못 써도 편집을 막지 않는다
            }
        }

        public void Clear()
        {
            Delete(ProjectPath, InfoPath);
        }

        /// <summary>창이 닫힘(복구 파일은 남겨 둔다 — 다음에 열 때 물음).</summary>
        public void Detach()
        {
            lock (Live)
                Live.Remove(Id);
        }

        public sealed class Entry
        {
            public string ProjectPath { get; set; }
            public string InfoPath { get; set; }
            /// <summary>원래 프로젝트 파일(없으면 새 프로젝트).</summary>
            public string OriginalPath { get; set; }
            public DateTime SavedAt { get; set; }

            public void Delete()
            {
                RecoveryStore.Delete(ProjectPath, InfoPath);
            }
        }

        /// <summary>열려 있는 창의 것이 아닌 복구 파일(새것부터).</summary>
        public List<Entry> Pending()
        {
            try
            {
                if (!Directory.Exists(_folder))
                    return new List<Entry>();
                List<string> live;
                lock (Live)
                    live = Live.ToList();
                return Directory.EnumerateFiles(_folder, "*" + ProjectFile.Extension)
                    .Where(path => !live.Contains(Path.GetFileNameWithoutExtension(path)))
                    .Select(path =>
                    {
                        var info = Path.ChangeExtension(path, ".txt");
                        return new Entry
                        {
                            ProjectPath = path,
                            InfoPath = info,
                            OriginalPath = File.Exists(info) ? File.ReadAllText(info).Trim() : "",
                            SavedAt = File.GetLastWriteTime(path)
                        };
                    })
                    .OrderByDescending(e => e.SavedAt)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<Entry>();
            }
        }

        private static void Delete(params string[] paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception)
                {
                    // 다음 정리 때
                }
            }
        }
    }
}
