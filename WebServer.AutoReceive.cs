using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AhoChatA
{
    public sealed partial class WebServer
    {
        /// <summary>
        /// GET 读取自动接收配置；POST 修改 {enabled:bool, dir:string}。
        /// 供对外网页（webui）与 App 内网页统一使用。
        /// </summary>
        private void ServeAutoReceiveApi(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            try
            {
                if (string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string body;
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                        body = sr.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var o = JObject.Parse(body);
                        var en = o["enabled"];
                        if (en != null && (en.Type == JTokenType.Boolean))
                            AutoReceiveSettings.Enabled = (bool)en;
                        var dir = o["dir"];
                        if (dir != null && dir.Type == JTokenType.String)
                            AutoReceiveSettings.Dir = (string)dir;
                    }
                }
                var json = "{\"enabled\":" + (AutoReceiveSettings.Enabled ? "true" : "false") +
                           ",\"dir\":" + Json(AutoReceiveSettings.Dir) +
                           ",\"defaultDir\":" + Json(AutoReceiveSettings.DefaultDir) + "}";
                WriteJson(res, json);
            }
            catch (Exception ex)
            {
                WriteJson(res, "{\"ok\":false,\"error\":" + Json(ex.Message) + "}");
            }
        }
    }
}
