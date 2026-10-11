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

        /// <summary>
        /// 封面图片地址（详情页解析出来的，可能是相对路径）。
        /// 空 = 站点没给封面。图片本身在下载阶段落到书目录下的 封面.xxx，
        /// 这样导出 EPUB 时不需要联网。
        /// </summary>
        public string CoverUrl = "";

        /// <summary>
        /// 总字数。站点给了就用站点的（番茄有），没给就留 0，
        /// 由 <see cref="BookStats"/> 按已下载正文现算。
        /// </summary>
        public long WordCount = 0;

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

    /// <summary>
    /// 能直接算出一章正文 URL 的站点 —— 用于**批量并发抓取**（HttpBatch）。
    ///
    /// 为什么做成可选接口而不是塞进 ISite：
    /// 番茄那一路正文不是"一个 URL 一个 HTML"（要走第三方核心），
    /// 硬塞进 ISite 会逼它实现两个根本用不上的方法。
    ///
    /// ★ 关键前提：**下载阶段所有章节 id 事先都知道**（目录已经拿到了），
    ///   所以这里能"算 URL"而不是"猜 URL" —— 这正是批量抓取能成立的原因。
    ///   目录遍历那边没有这个前提（下一页地址只写在当前页 HTML 里），所以用不上批量。
    /// </summary>
    public interface IChapterUrlProvider
    {
        /// <summary>这一章的正文页 URL</summary>
        string ChapterUrlFor(BookInfo book, ChapterInfo chapter);
        /// <summary>批量抓取时要带的 Referer</summary>
        string ChapterRefererFor(BookInfo book);

        /// <summary>
        /// 从**已经抓下来的 HTML** 里提取正文。
        ///
        /// ★ 这个方法必须有：批量抓取拿到的是**原始 HTML**，
        ///   而正常路径（ISite.LoadChapter）拿到 HTML 之后还会做提取
        ///   （解 base64、去标签、过滤广告行……）。
        ///   我第一版忘了这一步，结果批量下载把**整页 HTML** 当正文写进了 txt ——
        ///   单章 2516 字符变成 9476 字符、产物从 193KB 涨到 298KB。
        ///   靠"同一批章节对比两份产物的逐章长度"才发现。
        ///
        /// 提取逻辑必须和 LoadChapter 用的是**同一份实现**，
        /// 否则两条路径会产出内容不一致的正文 —— 那比不批量更糟。
        /// </summary>
        string ChapterTextFromHtml(string html);

        /// <summary>
        /// 这个 HTML 是不是**确实属于**这一章。
        ///
        /// 用途：批量抓取是"一次交出多个 URL、一次收回多个文件"，
        /// 万一配对错位（把 A 章的 HTML 当成 B 章的），就会**静默地把别的章写进正文**。
        /// 这类错误单测抓不到（只在真实网络下出现），所以宁可多一道显式校验：
        /// 对不上号就丢掉这一条，让调用方逐章退回 —— 慢一点，但绝不会写错内容。
        ///
        /// 实现方式：页面里那段 `shuqian(bookId,chapterId,...)` 带的是**本章自己的 id**，
        /// 拿它和请求的章节 id 比即可（实测 8/8 严格对应）。
        /// </summary>
        bool HtmlMatchesChapter(string html, ChapterInfo chapter);
    }

    /// <summary>
    /// 站点探活：不抓内容，只回答"这个域名现在通不通、不通是什么原因"。
    ///
    /// 用途是**站点自动降级**：用户看不懂
    /// `schannel: failed to receive handshake`，但看得懂
    /// "移动版当前不可用（HTTPS 被站点拒绝），已改用 PC 版"。
    /// </summary>
    public interface IProbeable
    {
        /// <summary>探活一次。返回 null = 正常；否则返回一句给用户看的原因。</summary>
        string Probe();
    }
}
