using System;
using System.Collections.Generic;

namespace TomatoBiquga
{
    /// <summary>一本书的元信息</summary>
    public class BookInfo
    {
        public string Site = "";          // "biquga" 或 "fanqie"
        public string Title = "";
        public string Author = "";
        public string Category = "";
        public string Status = "";
        public string Desc = "";
        public string Url = "";           // 详情页地址
        public string Dir = "";           // biquga 的书籍目录，如 /10_10333
        public string BookId = "";        // 番茄的 book_id
        public List<ChapterInfo> Chapters = new List<ChapterInfo>();

        public override string ToString()
        {
            return string.Format("《{0}》  作者：{1}  [{2}]  {3}  共 {4} 章",
                Title, string.IsNullOrEmpty(Author) ? "未知" : Author,
                string.IsNullOrEmpty(Category) ? "未知" : Category, Status, Chapters.Count);
        }
    }

    public class ChapterInfo
    {
        public string Id = "";          // biquga: cid；番茄: itemId
        public string Title = "";
        public string Text = "";        // 下载后填充
        public bool Selected = true;
        public int Order = 0;
        public bool IsVolume = false;   // 番茄的分卷标题行
    }

    /// <summary>站点接口</summary>
    public interface ISite
    {
        string Name { get; }
        List<BookInfo> Search(string keyword, Action<string> log);
        BookInfo LoadBook(BookInfo item, Action<string> log);
        string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log);
    }

    /// <summary>
    /// 能提供“已经抓到的正文”的站点（目录遍历/并发预抓阶段顺手抓下来的），
    /// 下载器会优先用它，避免重复请求站点。
    /// </summary>
    public interface ITextCacheProvider
    {
        string GetCachedText(string cid);
    }
}
