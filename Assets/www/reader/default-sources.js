/* ===================== 阅读模块 · 内置示例书源 =====================
 * 注意：书源指向第三方网站，站点结构会变动，且受网络/版权影响。
 * 这里仅内置「示例」书源（默认关闭），用于演示书源格式；
 * 真正可用的书源请在「书源管理 → 导入书源 JSON」中粘贴你自己的 legado 书源。
 * 所有联网抓取都经本机 /api/proxy 中转，不依赖 legado 官方服务器，离线可控。
 */
(function () {
    'use strict';

    var EXAMPLES = [
        {
            bookSourceComment: "示例·笔趣阁风格（默认关闭，请按实际站点调整规则后启用）",
            bookSourceGroup: "示例",
            bookSourceName: "示例·笔趣阁",
            bookSourceType: 0,
            bookSourceUrl: "https://www.example-bqg.example",
            enabled: false,
            enabledExplore: false,
            header: "{\"User-Agent\":\"Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/120.0 Mobile\"}",
            searchUrl: "https://www.example-bqg.example/search.php?q=searchKey",
            ruleSearch: {
                bookList: "ul.list > li",
                name: "a.bookname",
                author: "span.author",
                bookUrl: "a.bookname@href",
                coverUrl: "img@src",
                intro: "p.intro",
                lastChapter: "span.chapter"
            },
            ruleBookInfo: {
                name: "div.info > h1",
                author: "div.info > p.author",
                intro: "div.intro",
                coverUrl: "div.cover > img@src",
                tocUrl: "a.toc@href",
                lastChapter: "div.update > a"
            },
            ruleToc: {
                chapterList: "div.chapter-list > a",
                chapterName: "text",
                chapterUrl: "@href"
            },
            ruleContent: {
                content: "div.content",
                replaceRegex: "",
                webJs: ""
            }
        }
    ];

    window.DefaultSources = EXAMPLES;
})();
