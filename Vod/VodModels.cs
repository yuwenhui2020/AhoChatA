// ============================================================
// 在线影视 · 数据结构
//
// 移植自 C:\root\code\TV 的 VOD 模块，并按本项目习惯改为 Newtonsoft.Json + 字段式 POCO。
// 数据源：TVBox 配置（订阅 JSON）+ 苹果CMS(MacCMS V10) api.php/provide/vod/ 采集站。
//   type 0/1 = HTTP CMS JSON 接口（本程序支持）；type 3 = csp_ 爬虫 jar（跳过）。
// 播放串：vod_play_from "线路A$$$线路B"，vod_play_url "第01集$http://..#第02集$..$$$.."
// ============================================================
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AhoChatA.Vod
{
    /// <summary>一个采集站点（一条渠道）。</summary>
    public class VodSite
    {
        public string Key = "";
        public string Name = "";
        public string Api = "";
        public int Type = 1;
        /// <summary>来源渠道名：内置 / 订阅名 / 自定义。</summary>
        public string Origin = "";
        /// <summary>该站是否支持 wd= 搜索（运行时探测，探测失败自动关闭）。</summary>
        public bool Searchable = true;
        public bool Enabled = true;
        /// <summary>是否指向本程序自带的 CMS 对外 API（影视CMS:7777 / 海洋CMS:6666）。</summary>
        public bool IsCms = false;
        /// <summary>CMS 访问令牌（对应 CMS 后台设置的 api_token），随请求 ?token= 传递。</summary>
        public string Token = "";

        [JsonIgnore]
        public bool WdProbed = false;
    }

    /// <summary>一套自带的 CMS 服务（影视CMS / 海洋CMS 各自独立，端口不同）。</summary>
    public class VodCmsServer
    {
        public string Name = "";
        /// <summary>形如 127.0.0.1:7777</summary>
        public string Host = "";
        public string Token = "";
    }

    public class VodEpisode
    {
        public string Name = "";
        public string Url = "";
        /// <summary>嗅探/模板推断出的真实直链；为空表示 Url 本身可直接播。</summary>
        public string Real = "";

        [JsonIgnore]
        public string PlayUrl
        {
            get { return string.IsNullOrWhiteSpace(Real) ? Url : Real; }
        }
    }

    public class VodSource
    {
        public string Name = "";
        public List<VodEpisode> Episodes = new List<VodEpisode>();
        public int DirectCount = 0;
    }

    /// <summary>一部影片 / 剧集（列表卡片）。</summary>
    public class VodItem
    {
        public string Id = "";
        public string SiteKey = "";
        public string SiteName = "";
        public string Api = "";
        public string Name = "";
        public string Pic = "";
        public string Remarks = "";
        public string TypeName = "";
        public string Year = "";
        public string Area = "";
        public string Desc = "";
        public string Actor = "";
        public string Score = "";
        public string PlayFrom = "";
        public string PlayUrl = "";

        /// <summary>同名跨源合并后的其它站点变体（不持久化，避免递归序列化）。</summary>
        [JsonIgnore]
        public List<VodItem> Variants = new List<VodItem>();

        [JsonIgnore]
        public string Key
        {
            get { return SiteKey + "|" + Id; }
        }
    }

    /// <summary>分类。TypeIds 记录「同一分类名在各个站点里各自的 type_id」。</summary>
    public class VodCat
    {
        public string Name = "";
        public int Hits = 0;
        public Dictionary<string, string> TypeIds = new Dictionary<string, string>();
    }

    public class VodWatchRecord
    {
        public string Key = "";          // siteKey|vod_id
        public string Name = "";
        public string Pic = "";
        public string SiteKey = "";
        public string SiteName = "";
        public string Remarks = "";
        public string EpisodeName = "";
        public int EpisodeIndex = 0;
        public long PositionMs = 0;
        public long DurationMs = 0;
        public long LastTs = 0;
        public List<string> Urls = new List<string>();
        public List<string> Names = new List<string>();
    }

    /// <summary>一条 TVBox 订阅渠道。</summary>
    public class VodSub
    {
        public string Name = "";
        public string Url = "";
        public bool Enabled = true;
    }

    /// <summary>可编辑配置，运行时位于 files/vod/onlinevod.json。</summary>
    public class VodConfigDoc
    {
        /// <summary>builtin=内置实测源 / subs=订阅 / custom=粘贴 JSON / cms=本程序自带 CMS（影视CMS + 海洋CMS）。</summary>
        public string Mode = "builtin";
        public List<VodSub> Subs = new List<VodSub>();
        public string CustomJson = "";
        public List<VodSite> ExtraSites = new List<VodSite>();
        /// <summary>CMS 模式下的服务清单（影视CMS / 海洋CMS …）。</summary>
        public List<VodCmsServer> CmsServers = new List<VodCmsServer>();
        /// <summary>被用户停用的站点 key。</summary>
        public List<string> Disabled = new List<string>();
        public int MaxPages = 12;
        public int TimeoutMs = 9000;
        public int MaxSites = 32;
        /// <summary>统一 3 天（72 小时）；与 VodCache.CacheTtlMs 一致。</summary>
        public int HomeTtlHours = 72;
        /// <summary>配置结构版本，用于内置源扩充后的平滑迁移。</summary>
        public int Rev = 0;
    }

    /// <summary>传给 VideoActivity 的一集（Path 已是本地代理地址）。</summary>
    public class VodPlayItem
    {
        public string Name = "";
        public string Url = "";
    }

    /// <summary>给 App / 网页端用的站点状态视图。</summary>
    public class VodStatus
    {
        public string Mode = "";
        public int SiteCount = 0;
        public int EnabledCount = 0;
        public int CachedItems = 0;
        public int Pages = 0;
        public string Signature = "";
        public string Updated = "";
        public List<VodSub> Subs = new List<VodSub>();
        public List<VodSite> Sites = new List<VodSite>();
    }
}
