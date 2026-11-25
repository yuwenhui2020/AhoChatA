using System;
using System.Collections.Generic;
using Android.Content;

namespace AhoChatA
{
    /// <summary>换指纹的范围：只换 qaul / 只换 bitchat / 全换。</summary>
    public enum IdentityScope
    {
        All = 0,
        Qaul = 1,
        Bitchat = 2
    }

    /// <summary>
    /// 身份的生成、持久化与「换指纹」。
    ///
    /// 设计要点：
    ///   · 私钥种子只存本机 SharedPreferences（应用私有目录，未 root 不可读）；
    ///   · 换指纹 = 重新生成对应种子并重算全部派生 id，随后由 MchatBridge 重新广播；
    ///   · 换 qaul 身份不影响 bitchat 身份，反之亦然（三把种子彼此独立）；
    ///   · 换 fingerprint 后旧会话历史保留（按会话键存），但新消息用新身份；
    ///     对端看到的是一个"新设备/新联系人"，这正是隐私诉求要的效果。
    /// </summary>
    public class IdentityStore
    {
        private const string KeyQaul = "id.qaul";
        private const string KeyNoise = "id.noise";
        private const string KeySign = "id.sign";
        private const string KeyGen = "id.gen";
        private const string KeyAt = "id.at";
        private const string KeyPeerId = "peerId";
        private const string KeyQaulHistory = "id.qaul.history";

        /// <summary>历代 qaul 账号 id 最多保留多少条（够回溯历史会话即可，别无限增长）。</summary>
        private const int MaxQaulHistory = 16;

        private readonly ISharedPreferences prefs;

        public LocalIdentity Current { get; private set; }

        /// <summary>
        /// 历代（含当前之前各代）的 qaul 账号 id（Base58 PeerId）。
        ///
        /// qaul 把用户账号长期保存在 user_accounts 里（rust/libqaul/src/node/user_accounts.rs），
        /// 换账号后旧会话仍属于旧 id、仍可解密。我们原来只认「当前」qaul PeerId：
        /// 一旦用户换指纹，旧私聊的 LC1 密钥（由双方 PeerId 排序派生）就算不出来了，
        /// 历史消息永久 [无法解密]。这里把历代 id 存档，解密时逐个试。
        /// 只存 PeerId 文本即可——LC1 的密钥派生只需要 id 字符串，不需要私钥。
        /// </summary>
        public List<string> QaulIdHistory { get; private set; } = new List<string>();

        /// <summary>身份变化通知（原生界面 / 网页端刷新）。</summary>
        public event Action<LocalIdentity> Changed;

        public IdentityStore(Context context)
        {
            prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
            Load();
        }

        private void Load()
        {
            try
            {
                var qaul = LocalIdentity.Unhex(prefs.GetString(KeyQaul, null));
                var noise = LocalIdentity.Unhex(prefs.GetString(KeyNoise, null));
                var sign = LocalIdentity.Unhex(prefs.GetString(KeySign, null));

                // 迁移：老版本只有 12 位随机 peerId，没有密钥材料。
                // 保留原 peerId 作为 Mchat 身份，同时补生成三把密钥，
                // 这样升级后不会突然变成"另一台设备"。
                var legacyPeerId = prefs.GetString(KeyPeerId, null);
                bool isNew = qaul == null && noise == null && sign == null;

                if (qaul == null) qaul = Curve25519.RandomSeed();
                if (noise == null) noise = Curve25519.RandomSeed();
                if (sign == null) sign = Curve25519.RandomSeed();

                Current = LocalIdentity.Create(qaul, noise, sign, legacyPeerId);
                Current.Generation = prefs.GetInt(KeyGen, 1);
                Current.CreatedAt = prefs.GetLong(KeyAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                QaulIdHistory = ParseHistory(prefs.GetString(KeyQaulHistory, null));

                if (isNew)
                {
                    // 首次生成：把身份落盘（包括由公钥派生的 Mchat id）
                    SaveSeeds(Current);
                    prefs.Edit()
                         .PutInt(KeyGen, Current.Generation)
                         .PutLong(KeyAt, Current.CreatedAt)
                         .PutString(KeyPeerId, Current.MchatId)
                         .Apply();
                }
                else if (string.IsNullOrEmpty(legacyPeerId))
                {
                    prefs.Edit().PutString(KeyPeerId, Current.MchatId).Apply();
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Identity", "加载身份失败: " + ex.Message);
                Current = LocalIdentity.Generate();
            }
        }

        private void SaveSeeds(LocalIdentity id)
        {
            prefs.Edit()
                 .PutString(KeyQaul, LocalIdentity.Hex(id.QaulSeed))
                 .PutString(KeyNoise, LocalIdentity.Hex(id.NoiseSeed))
                 .PutString(KeySign, LocalIdentity.Hex(id.SignSeed))
                 .Apply();
        }

        /// <summary>
        /// 更换指纹。返回新的身份。
        /// scope=Qaul 只换 qaul 的 Ed25519；Bitchat 换 Noise + 签名两把；All 全换并换 Mchat id。
        /// </summary>
        public LocalIdentity Rotate(IdentityScope scope)
        {
            var old = Current;

            var qaulSeed = scope == IdentityScope.Bitchat ? old.QaulSeed : Curve25519.RandomSeed();
            var noiseSeed = scope == IdentityScope.Qaul ? old.NoiseSeed : Curve25519.RandomSeed();
            var signSeed = scope == IdentityScope.Qaul ? old.SignSeed : Curve25519.RandomSeed();

            // Mchat 身份跟着签名密钥走：换了签名密钥就换一个全新的局域网 id，
            // 避免旧身份继续被关联到新指纹上。
            string MchatId = scope == IdentityScope.Qaul ? old.MchatId : null;

            var next = LocalIdentity.Create(qaulSeed, noiseSeed, signSeed, MchatId);
            next.Generation = old.Generation + 1;
            next.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 换了 qaul 账号就把旧 id 存档：否则旧私聊的 LC1 密钥再也算不出来，
            // 历史消息永久 locked（qaul 的 user_accounts 也是长期保留账号的）。
            if (scope != IdentityScope.Bitchat && !string.IsNullOrEmpty(old.QaulPeerId)
                && old.QaulPeerId != next.QaulPeerId)
            {
                if (!QaulIdHistory.Contains(old.QaulPeerId)) QaulIdHistory.Add(old.QaulPeerId);
                while (QaulIdHistory.Count > MaxQaulHistory) QaulIdHistory.RemoveAt(0);
            }

            Current = next;
            SaveSeeds(next);
            prefs.Edit()
                 .PutInt(KeyGen, next.Generation)
                 .PutLong(KeyAt, next.CreatedAt)
                 .PutString(KeyPeerId, next.MchatId)
                 .PutString(KeyQaulHistory, string.Join(",", QaulIdHistory))
                 .Apply();

            Android.Util.Log.Info("Identity",
                $"指纹已更换（scope={scope}, gen={next.Generation}, qaul={next.QaulPeerId.Substring(0, Math.Min(10, next.QaulPeerId.Length))}…, bitchat={next.BitchatPeerId}）");

            try { Changed?.Invoke(next); } catch { }
            return next;
        }

        private static List<string> ParseHistory(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var part in raw.Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0 && !list.Contains(s)) list.Add(s);
            }
            while (list.Count > MaxQaulHistory) list.RemoveAt(0);
            return list;
        }

        /// <summary>给界面用的摘要对象。</summary>
        public object Summary()
        {
            return Current.ToSummary();
        }
    }
}
