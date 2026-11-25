using System;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 从文件管理器/ADB 以 ACTION_VIEW 打开 .user.js 时导入油猴脚本。
    /// 同名脚本会被更新而不是重复添加（方便重新载入本地文件来刷新元数据）。
    /// </summary>
    [Activity(Label = "@string/act_userscript", Exported = true,
        Theme = "@android:style/Theme.Translucent.NoTitleBar")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault },
        DataSchemes = new[] { "content", "file" },
        DataMimeTypes = new[] { "text/javascript", "application/javascript", "application/x-javascript", "text/plain" })]
    public class UserScriptImportActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            string message;
            try
            {
                var uri = Intent?.Data;
                if (uri == null) throw new Exception("没有文件 URI");
                string content;
                using (var input = ContentResolver.OpenInputStream(uri))
                using (var reader = new StreamReader(input))
                    content = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(content)) throw new Exception(L10n.T("文件为空"));

                UserScriptManager.Instance.Init(this);
                var parsed = UserScriptManager.Parse(content);
                Android.Util.Log.Info("Userscript", $"import: name={parsed.Name} requires={parsed.Requires.Count} existing={UserScriptManager.Instance.All.Count}");
                var existing = UserScriptManager.Instance.All
                    .FirstOrDefault(s => s.Name == parsed.Name && s.Code == parsed.Code);
                if (existing != null)
                {
                    parsed.Id = existing.Id;
                    parsed.Enabled = existing.Enabled;
                    parsed.AddedAt = existing.AddedAt;
                    UserScriptManager.Instance.Update(parsed);
                    message = L10n.T("已刷新: ") + parsed.Name;
                }
                else
                {
                    UserScriptManager.Instance.Add(parsed);
                    message = L10n.T("已导入: ") + parsed.Name;
                }
            }
            catch (Exception ex)
            {
                message = L10n.T("导入失败: ") + ex.Message;
                Android.Util.Log.Warn("Userscript", "import FAIL " + ex);
            }
            L10n.Toast(this, message, ToastLength.Long).Show();
            Finish();
        }
    }
}
