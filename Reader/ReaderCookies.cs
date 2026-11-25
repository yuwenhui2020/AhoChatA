// ============================================================
// 阅读模块 · Cookie 罐（legado enabledCookieJar 的对应实现）
//
// 书源里大量站点要求「首次访问种 cookie，后续请求带上」，
// 否则详情页/目录页会返回空壳或跳验证页（legado 用 OkHttp CookieJar 持久化）。
// 这里用一个进程级 CookieContainer，抓取链路上的 HttpClient 与
// java.ajax / java.getCookie 共用同一份。
// ============================================================
using System;
using System.Net;

namespace AhoChatA.Reader
{
    public static class ReaderCookies
    {
        private static readonly CookieContainer _jar = new CookieContainer();

        public static CookieContainer Jar => _jar;

        /// <summary>取某个 url 对应的 cookie 串（形如 a=1; b=2）。</summary>
        public static string Get(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                var uri = new Uri(url);
                var s = _jar.GetCookieHeader(uri);
                return s ?? "";
            }
            catch { return ""; }
        }

        /// <summary>写入 cookie（支持 "a=1; b=2" 或 "a=1" 形式）。</summary>
        public static void Put(string url, string cookie)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(cookie)) return;
            try
            {
                var uri = new Uri(url);
                _jar.SetCookies(uri, cookie);
            }
            catch
            {
                // 单条格式不合法时退化为逐条写入
                try
                {
                    var uri = new Uri(url);
                    foreach (var part in cookie.Split(';'))
                    {
                        var p = part.Trim();
                        if (p.Length == 0) continue;
                        try { _jar.SetCookies(uri, p); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>把上一跳响应里的 Set-Cookie 收进罐子（HttpClient 已自动处理，这里给手工场景用）。</summary>
        public static void PutSetCookie(string url, string setCookie)
        {
            if (string.IsNullOrWhiteSpace(setCookie)) return;
            var first = setCookie.Split(';')[0].Trim();
            if (first.Length > 0) Put(url, first);
        }
    }
}
