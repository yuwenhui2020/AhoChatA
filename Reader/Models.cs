// ============================================================
// 阅读模块 · 运行时数据模型
// ============================================================
using System;
using System.Collections.Generic;

namespace AhoChatA.Reader
{
    /// <summary>一章：本地书 Url 为空、Content 直接是正文；在线书 Url 为章节地址，Content 按需抓取。</summary>
    public class Chapter
    {
        public int Index;
        public string Title = "";
        public string Url;          // 在线章节地址；本地书为 null
        public string Content;      // 正文；在线书首次阅读时填充
        public bool IsVolume;       // 分卷标题（仅作展示）
        public bool IsVip;          // 书源标记的 VIP 章
        public bool IsPay;          // 书源标记的付费章
        public string Tag;          // 更新时间等附加标记
    }

    /// <summary>一本书（本地导入或在线搜索得到）。</summary>
    public class Book
    {
        public string Id;           // 唯一 id（本地=路径哈希；在线=bookUrl）
        public string Name = "";
        public string Author = "";
        public string Intro = "";
        public string CoverUrl;
        public string Kind;
        public string LastChapter;      // 最新章节名（在线书详情页给出）
        public string Type = "local";   // local / online
        /// <summary>本地文件格式：txt / epub / pdf / fb2 / md。空 = 旧数据（按扩展名推断）。
        /// pdf 时 Chapters 是「一页一章」的占位表，正文由 PdfEngine 逐页渲染成图片。</summary>
        public string Format = "";
        public string SourceUrl;        // 在线书所属书源地址
        public string BookUrl;          // 在线书详情页地址
        public string TocUrl;           // 在线书目录地址
        public List<Chapter> Chapters = new List<Chapter>();
        public DateTime Added = DateTime.Now;
    }

    /// <summary>书架条目（持久化用，章节内容不入库，按需从源读取）。</summary>
    public class ShelfItem
    {
        public string Id;
        public string Name = "";
        public string Author = "";
        public string Type = "local";   // local / online
        /// <summary>书架分组（文件夹）名；为空 = 顶层（不分组的书）。
        /// 仅影响书架展示与归类，绝不改变本地文件真实路径。</summary>
        public string Folder = "";
        public string Path;             // 本地文件路径
        /// <summary>本地文件格式：txt / epub / pdf / fb2 / md（旧数据为空，按 Path 推断）。</summary>
        public string Format = "";
        public string SourceUrl;        // 在线书源
        public string BookUrl;
        public string TocUrl;
        public string CoverUrl;
        public DateTime Added = DateTime.Now;
        public DateTime Updated = DateTime.MinValue;  // 最近阅读时间（排序 / 继续阅读用）
        public int LastChapter = 0;     // 上次读到第几章
        public double Pct = 0;          // 章内滚动位置 0~1
        public int Total = 0;           // 章数（缓存，避免每次列表都重新解析）
        public double Progress = 0;     // 全书进度 0~1
    }

    /// <summary>阅读排版偏好（App 与网页端共用同一份，存 reader/prefs.json）。</summary>
    public class ReaderPrefs
    {
        public int FontSize = 19;
        public double LineHeight = 1.85;
        public bool Justify = true;
        public string Theme = "light";   // light / sepia / green / gray / dark / black
        public int Margin = 22;          // 左右页边距（pt）
        public int ParaSpace = 14;       // 段间距（pt）
        public int Indent = 2;           // 首行缩进（字）
        public string Font = "serif";    // serif / sans / system
        public bool Bold = false;        // 正文加粗
        public string PageAnim = "slide";// none / slide / cover / fade
        public bool FullScreen = true;   // 阅读时隐藏系统栏
        public bool AutoAppend = true;   // 连读：本章读到底自动拼接下一章
    }

    /// <summary>发现页的一个分类（来自 exploreUrl）。</summary>
    public class ExploreCategory
    {
        public string Title = "";
        public string Url = "";
        public string Group = "";
    }



    /// <summary>书签 / 阅读标记。</summary>
    public class Bookmark
    {
        public string Id = "";
        public string BookId;
        public int ChapterIndex;
        public string ChapterTitle = "";
        public double Pct = 0;
        public string Text = "";
        public DateTime Created = DateTime.Now;
    }
}
