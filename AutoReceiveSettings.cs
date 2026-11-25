using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.OS;

namespace AhoChatA
{
    /// <summary>
    /// 「自动接收局域网文件」配置：收到聊天里带文件体的消息后，
    /// 若开关开启，把文件按原始文件名另存一份到用户指定目录（默认公共 Download 根目录，即 /sdcard/Download）。
    /// 与四类媒体自动入库（MediaShare）并行、互不影响；这里覆盖任意扩展名的普通文件。
    /// 存储用既有 SharedPreferences "Mchat"，与 ListenTogether 等设置同文件。
    /// </summary>
    public static class AutoReceiveSettings
    {
        private const string PrefName = "Mchat";
        private const string KeyEnabled = "autoReceiveFiles";
        private const string KeyDir = "autoReceiveDir";

        private static ISharedPreferences Prefs
        {
            get { return Application.Context.GetSharedPreferences(PrefName, FileCreationMode.Private); }
        }

        /// <summary>是否自动接收并保存任意文件，默认关闭。</summary>
        public static bool Enabled
        {
            get
            {
                try { return Prefs.GetBoolean(KeyEnabled, false); }
                catch { return false; }
            }
            set
            {
                try { Prefs.Edit().PutBoolean(KeyEnabled, value).Apply(); }
                catch { }
            }
        }

        /// <summary>目标目录绝对路径；为空时返回默认公共下载目录。</summary>
        public static string Dir
        {
            get
            {
                try
                {
                    var d = Prefs.GetString(KeyDir, "");
                    if (!string.IsNullOrEmpty(d)) return d;
                }
                catch { }
                return DefaultDir;
            }
            set
            {
                try { Prefs.Edit().PutString(KeyDir, value ?? "").Apply(); }
                catch { }
            }
        }

        /// <summary>默认接收目录：公共 Download 根目录（/sdcard/Download）；拿不到时用应用私有 files/autoReceive。</summary>
        public static string DefaultDir
        {
            get
            {
                try
                {
                    var pub = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads);
                    if (pub != null && !string.IsNullOrEmpty(pub.AbsolutePath))
                        return pub.AbsolutePath;
                }
                catch { }
                return Path.Combine(Application.Context.FilesDir.AbsolutePath, "autoReceive");
            }
        }
    }
}
