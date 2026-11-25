using System;

namespace AhoChatA
{
    /// <summary>
    /// 阅读模块字体优化：注入统一的中文 fallback 字体栈与最小字号。
    /// 解决 docx/pptx/xlsx 里指定的「宋体/微软雅黑/Calibri」等字体在手机上缺失时
    /// fallback 到衬线或过小字号、中文发虚的问题。两处（原生查看器 + Web 阅读器）共用。
    /// </summary>
    public static class ReaderFonts
    {
        private const string Css = @"
<style id='mchat-reader-fonts'>
/* 中文优先用系统无衬线黑体，正文最小 13px，标题加粗——移动端可读性优化 */
html,body{font-family:'Noto Sans CJK SC','Source Han Sans SC','PingFang SC','Microsoft YaHei',system-ui,sans-serif}
#docx-container,#docx-container *,.w-t,.slide-text,table,td,th{font-family:'Noto Sans CJK SC','Source Han Sans SC','PingFang SC','Microsoft YaHei',system-ui,sans-serif}
p,li,td,th,span{font-size:max(13px,1em)}
h1,h2,h3{font-weight:700}
</style>";

        private const string Marker = "</head>";

        /// <summary>若 html 中尚无注入标记，则在 &lt;/head&gt; 前插入字体优化 CSS。</summary>
        public static string Inject(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;
            if (html.IndexOf("mchat-reader-fonts", StringComparison.OrdinalIgnoreCase) >= 0) return html;
            int i = html.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return Css + html;
            return html.Insert(i, Css);
        }
    }
}
