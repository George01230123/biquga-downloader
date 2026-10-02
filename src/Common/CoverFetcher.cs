using System;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 封面抓取。
    ///
    /// 为什么值得单独做：EPUB 没有封面在手机阅读器的书架上就是一格灰方块，
    /// 而 EpubWriter 其实**早就支持封面**（coverBytes/coverExt 参数齐全），
    /// 只是一直被传 null —— 缺的从来不是写 EPUB 的能力，而是「把图抓下来」这一步。
    ///
    /// 设计要点：
    ///   1. 图片落地成书目录下的 `封面.xxx`，**导出 EPUB 时不联网**。
    ///      这样离线模式、断网导出都成立，和项目「已下载的正文不依赖网络」一致。
    ///   2. 必须在 LoadBook 阶段抓：站点普遍有防盗链，请求要带**详情页当 Referer**，
    ///      而那个上下文只有加载目录时才有。
    ///   3. **封面失败绝不影响下载**：所有异常都吞掉返回 null，只记一行日志。
    ///      一张图不值得让整本书失败。
    /// </summary>
    public static class CoverFetcher
    {
        /// <summary>封面文件名前缀（扩展名按真实格式补）</summary>
        public const string BaseName = "封面";

        /// <summary>封面体积上限：超过就丢弃。正常封面 20~200KB，2MB 已经很宽松。</summary>
        private const int MaxBytes = 2 * 1024 * 1024;

        /// <summary>
        /// 抓封面并落到 bookDir 下。成功返回落地的完整路径，失败/没封面返回 null。
        /// </summary>
        /// <param name="coverUrl">详情页解析出的封面地址，可为空</param>
        /// <param name="bookDir">书目录（下载目录下以书名命名的那一层）</param>
        /// <param name="referer">防盗链用的 Referer，一般传详情页地址</param>
        public static string Ensure(string coverUrl, string bookDir, string referer, Action<string> log)
        {
            try
            {
                if (string.IsNullOrEmpty(coverUrl) || string.IsNullOrEmpty(bookDir)) return null;

                // 已经抓过就直接用（重跑下载/更新新章节时不重复请求）
                var existing = Find(bookDir);
                if (existing != null) return existing;

                var bytes = Http.GetBytes(coverUrl, referer);
                if (bytes == null || bytes.Length == 0)
                {
                    if (log != null) log("  封面下载失败（不影响正文）");
                    return null;
                }
                if (bytes.Length > MaxBytes)
                {
                    if (log != null) log("  封面过大，已跳过（" + (bytes.Length / 1024) + " KB）");
                    return null;
                }

                var ext = SniffExt(bytes, coverUrl);
                if (ext == null)
                {
                    if (log != null) log("  封面格式不认识，已跳过");
                    return null;
                }

                if (!Directory.Exists(bookDir)) Directory.CreateDirectory(bookDir);
                var path = Path.Combine(bookDir, BaseName + ext);

                // 先写临时文件再换名：避免下载中途被杀留下半张图，
                // 而 Find() 会把半张图当成"已抓过"，之后永远修不好。
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                if (log != null)
                    log(string.Format("  封面已保存：{0}（{1} KB）", Path.GetFileName(path), bytes.Length / 1024));
                return path;
            }
            catch (Exception ex)
            {
                if (log != null) log("  封面处理失败：" + ex.Message + "（不影响正文）");
                return null;
            }
        }

        /// <summary>书目录里已有的封面文件，没有返回 null</summary>
        public static string Find(string bookDir)
        {
            try
            {
                if (string.IsNullOrEmpty(bookDir) || !Directory.Exists(bookDir)) return null;
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" })
                {
                    var p = Path.Combine(bookDir, BaseName + ext);
                    if (File.Exists(p) && new FileInfo(p).Length > 0) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>读封面字节 + 给 EPUB 用的扩展名。没有封面返回 null。</summary>
        public static byte[] Read(string bookDir, out string ext)
        {
            ext = null;
            try
            {
                var p = Find(bookDir);
                if (p == null) return null;
                ext = Path.GetExtension(p);
                return File.ReadAllBytes(p);
            }
            catch { return null; }
        }

        /// <summary>
        /// 按**魔术字节**判断图片格式 —— 不信任 URL 里的扩展名
        /// （很多站点的封面 URL 是 `...?id=123` 或者干脆给错扩展名）。
        /// 认不出来返回 null，宁可不出封面也不要往 EPUB 里塞一张坏图。
        /// </summary>
        public static string SniffExt(byte[] b, string urlHint)
        {
            if (b == null || b.Length < 4) return null;
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
            if (b[0] == 0x42 && b[1] == 0x4D) return ".bmp";
            // WEBP: "RIFF" .... "WEBP"
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
                return ".webp";

            // 魔术字节没命中时，退回看 URL 后缀（有些站点给的是 BMP 变体或直接漏了头）
            if (!string.IsNullOrEmpty(urlHint))
            {
                var low = urlHint.ToLowerInvariant();
                foreach (var e in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" })
                    if (low.Contains(e)) return e == ".jpeg" ? ".jpg" : e;
            }
            return null;
        }

        /// <summary>扩展名 → MIME（EpubWriter 用）</summary>
        public static string MimeOf(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return "image/jpeg";
            switch (ext.ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                default: return "image/jpeg";
            }
        }

        /// <summary>
        /// 把详情页里解析出来的相对地址补成绝对地址。
        /// （站点的封面常见写法：`/files/article/image/10/10333/10333s.jpg`）
        /// </summary>
        public static string Absolutize(string url, string pageUrl)
        {
            if (string.IsNullOrEmpty(url)) return "";
            url = Http.HtmlDecode(url).Trim().Trim('"', '\'');
            if (url.Length == 0) return "";
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("//")) return "https:" + url;

            try
            {
                var b = new Uri(pageUrl);
                if (url.StartsWith("/")) return b.Scheme + "://" + b.Host + url;
                return b.Scheme + "://" + b.Host + "/" + url.TrimStart('.', '/');
            }
            catch { return url; }
        }
    }
}
