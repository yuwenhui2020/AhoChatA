// ============================================================
// 阅读模块 · 书源数据模型（legado BookSource 的 C# 忠实移植）
//
// 字段名与 legado 导出的 JSON 键一一对应（snake_case），
// 因此标准 legado 书源 JSON 可直接反序列化。
// 详见 sample/legado-master/.../data/entities/BookSource.kt
// ============================================================
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AhoChatA.Reader
{
    /// <summary>书源类型：0 文本 / 1 音频 / 2 图片 / 3 文件（仅下载）。</summary>
    public class BookSource
    {
        [JsonProperty("bookSourceUrl")]      public string BookSourceUrl { get; set; } = "";
        [JsonProperty("bookSourceName")]     public string BookSourceName { get; set; } = "";
        [JsonProperty("bookSourceGroup")]    public string BookSourceGroup { get; set; }
        [JsonProperty("bookSourceType")]     public int BookSourceType { get; set; } = 0;
        [JsonProperty("bookUrlPattern")]     public string BookUrlPattern { get; set; }
        [JsonProperty("customOrder")]        public int CustomOrder { get; set; } = 0;
        [JsonProperty("enabled")]            public bool Enabled { get; set; } = true;
        [JsonProperty("enabledExplore")]     public bool EnabledExplore { get; set; } = true;
        [JsonProperty("jsLib")]              public string JsLib { get; set; }
        [JsonProperty("enabledCookieJar")]   public bool? EnabledCookieJar { get; set; } = true;
        [JsonProperty("concurrentRate")]     public string ConcurrentRate { get; set; }
        [JsonProperty("header")]             public string Header { get; set; }     // JSON 字符串：请求头
        [JsonProperty("loginUrl")]           public string LoginUrl { get; set; }
        [JsonProperty("loginUi")]            public string LoginUi { get; set; }
        [JsonProperty("loginCheckJs")]       public string LoginCheckJs { get; set; }
        [JsonProperty("coverDecodeJs")]      public string CoverDecodeJs { get; set; }
        [JsonProperty("bookSourceComment")]  public string BookSourceComment { get; set; }
        [JsonProperty("variableComment")]    public string VariableComment { get; set; }
        [JsonProperty("lastUpdateTime")]     public long LastUpdateTime { get; set; }
        [JsonProperty("respondTime")]        public long RespondTime { get; set; } = 180000;
        [JsonProperty("weight")]             public int Weight { get; set; } = 0;
        [JsonProperty("exploreUrl")]         public string ExploreUrl { get; set; }
        [JsonProperty("exploreScreen")]      public string ExploreScreen { get; set; }
        [JsonProperty("ruleExplore")]        [JsonConverter(typeof(RuleJsonConverter))] public ExploreRule ExploreRule { get; set; }
        [JsonProperty("searchUrl")]          public string SearchUrl { get; set; }
        [JsonProperty("ruleSearch")]         [JsonConverter(typeof(RuleJsonConverter))] public SearchRule SearchRule { get; set; }
        [JsonProperty("ruleBookInfo")]       [JsonConverter(typeof(RuleJsonConverter))] public BookInfoRule BookInfoRule { get; set; }
        [JsonProperty("ruleToc")]            [JsonConverter(typeof(RuleJsonConverter))] public TocRule TocRule { get; set; }
        [JsonProperty("ruleContent")]        [JsonConverter(typeof(RuleJsonConverter))] public ContentRule ContentRule { get; set; }
        [JsonProperty("ruleReview")]         [JsonConverter(typeof(RuleJsonConverter))] public ReviewRule ReviewRule { get; set; }

        /// <summary>书源变量（legado 的 source.getVariable()），暂不支持自定义变量，返回空串。</summary>
        public string getVariable() { return ""; }
        /// <summary>书源标识（legado 的 source.getKey()）。</summary>
        public string getKey() { return BookSourceUrl ?? ""; }
        public string getName() { return BookSourceName ?? ""; }
        public string getTag() { return BookSourceGroup ?? ""; }

        /// <summary>把 header 的 JSON 字符串解析为键值对（供 HTTP 请求头使用）。</summary>
        public Dictionary<string, string> ParseHeader()
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(Header)) return d;
            try
            {
                var j = JsonConvert.DeserializeObject<Dictionary<string, string>>(Header);
                if (j != null) foreach (var kv in j) d[kv.Key] = kv.Value;
            }
            catch { }
            return d;
        }
    }

    /// <summary>
    /// 规则对象的容错反序列化：legado 导出里 ruleExplore/ruleSearch 等常见写成
    /// <c>[]</c>、<c>""</c> 或 <c>false</c>（空规则），严格反序列化会让整份书源文件解析失败。
    /// 这里凡是「不是 JSON 对象」的值一律当作 null（= 无规则）。
    /// </summary>
    public class RuleJsonConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType)
        {
            return typeof(ExploreRule).IsAssignableFrom(objectType)
                || typeof(SearchRule).IsAssignableFrom(objectType)
                || typeof(BookInfoRule).IsAssignableFrom(objectType)
                || typeof(TocRule).IsAssignableFrom(objectType)
                || typeof(ContentRule).IsAssignableFrom(objectType)
                || typeof(ReviewRule).IsAssignableFrom(objectType);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType != JsonToken.StartObject)
            {
                reader.Skip();
                return null;
            }
            var jo = JObject.Load(reader);
            // 用默认序列化器，避免本转换器自我递归
            try { return JsonConvert.DeserializeObject(jo.ToString(Formatting.None), objectType); }
            catch { return null; }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>发现（explore）规则。</summary>
    public class ExploreRule
    {
        [JsonProperty("exploreUrl")]      public string ExploreUrl { get; set; }
        [JsonProperty("bookList")]        public string BookList { get; set; }
        [JsonProperty("name")]            public string Name { get; set; }
        [JsonProperty("author")]          public string Author { get; set; }
        [JsonProperty("intro")]           public string Intro { get; set; }
        [JsonProperty("kind")]            public string Kind { get; set; }
        [JsonProperty("lastChapter")]     public string LastChapter { get; set; }
        [JsonProperty("updateTime")]      public string UpdateTime { get; set; }
        [JsonProperty("bookUrl")]         public string BookUrl { get; set; }
        [JsonProperty("coverUrl")]        public string CoverUrl { get; set; }
        [JsonProperty("wordCount")]       public string WordCount { get; set; }
    }

    /// <summary>搜索结果处理规则。</summary>
    public class SearchRule
    {
        [JsonProperty("checkKeyWord")] public string CheckKeyWord { get; set; }
        [JsonProperty("bookList")]    public string BookList { get; set; }
        [JsonProperty("name")]        public string Name { get; set; }
        [JsonProperty("author")]      public string Author { get; set; }
        [JsonProperty("intro")]       public string Intro { get; set; }
        [JsonProperty("kind")]        public string Kind { get; set; }
        [JsonProperty("lastChapter")] public string LastChapter { get; set; }
        [JsonProperty("updateTime")]  public string UpdateTime { get; set; }
        [JsonProperty("bookUrl")]     public string BookUrl { get; set; }
        [JsonProperty("coverUrl")]    public string CoverUrl { get; set; }
        [JsonProperty("wordCount")]   public string WordCount { get; set; }
    }

    /// <summary>书籍详情页规则。</summary>
    public class BookInfoRule
    {
        [JsonProperty("init")]        public string Init { get; set; }
        [JsonProperty("name")]        public string Name { get; set; }
        [JsonProperty("author")]      public string Author { get; set; }
        [JsonProperty("intro")]       public string Intro { get; set; }
        [JsonProperty("kind")]        public string Kind { get; set; }
        [JsonProperty("lastChapter")] public string LastChapter { get; set; }
        [JsonProperty("updateTime")]  public string UpdateTime { get; set; }
        [JsonProperty("coverUrl")]    public string CoverUrl { get; set; }
        [JsonProperty("tocUrl")]      public string TocUrl { get; set; }
        [JsonProperty("wordCount")]   public string WordCount { get; set; }
        [JsonProperty("canReName")]   public string CanReName { get; set; }
        [JsonProperty("downloadUrls")] public string DownloadUrls { get; set; }
    }

    /// <summary>目录页规则。</summary>
    public class TocRule
    {
        [JsonProperty("preUpdateJs")] public string PreUpdateJs { get; set; }
        [JsonProperty("chapterList")] public string ChapterList { get; set; }
        [JsonProperty("chapterName")] public string ChapterName { get; set; }
        [JsonProperty("chapterUrl")]  public string ChapterUrl { get; set; }
        [JsonProperty("formatJs")]    public string FormatJs { get; set; }
        [JsonProperty("isVolume")]    public string IsVolume { get; set; }
        [JsonProperty("isVip")]       public string IsVip { get; set; }
        [JsonProperty("isPay")]       public string IsPay { get; set; }
        [JsonProperty("updateTime")]  public string UpdateTime { get; set; }
        [JsonProperty("nextTocUrl")]  public string NextTocUrl { get; set; }
    }

    /// <summary>正文页规则。</summary>
    public class ContentRule
    {
        [JsonProperty("content")]       public string Content { get; set; }
        [JsonProperty("title")]         public string Title { get; set; }
        [JsonProperty("nextContentUrl")] public string NextContentUrl { get; set; }
        [JsonProperty("webJs")]         public string WebJs { get; set; }
        [JsonProperty("sourceRegex")]   public string SourceRegex { get; set; }
        [JsonProperty("replaceRegex")]  public string ReplaceRegex { get; set; }
        [JsonProperty("imageStyle")]    public string ImageStyle { get; set; }
        [JsonProperty("imageDecode")]   public string ImageDecode { get; set; }
        [JsonProperty("payAction")]     public string PayAction { get; set; }
    }

    /// <summary>段评规则（占位，保持模型完整）。</summary>
    public class ReviewRule { }
}
