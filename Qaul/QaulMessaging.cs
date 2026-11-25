using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>qaul 用户（对端账号）：id + 公钥 + 昵称。</summary>
    public sealed class QaulUser
    {
        public string Id;          // base58 PeerId
        public string Name;
        public string KeyHex;      // ed25519 公钥 32 字节
        public long UpdatedAt;
        public string Bio;

        /// <summary>
        /// true 表示这只是一条「节点机身身份」记录，不是一个聊天账号。
        ///
        /// libqaul 的用户账号用自己生成的密钥（`node/user_accounts.rs` 里
        /// `create_account` 现生成 ed25519 密钥），和节点的 libp2p PeerId 不是一回事。
        /// 早年把邻居节点 id 直接当联系人，于是同一台官方客户端会被登记两次：
        /// 一次是它真正的账号（带昵称、能收发），一次是它的节点身份（发过去石沉大海）。
        /// 这种记录只留作签名校验，界面上不显示为联系人。
        /// </summary>
        public bool Node;
    }

    /// <summary>qaul 群成员。</summary>
    public sealed class QaulMember
    {
        public string UserId;      // base58
        public int Role;           // 0 user / 255 admin
        public long JoinedAt;
        public int State;          // 0 invited / 1 activated
        public uint LastIndex;
    }

    /// <summary>qaul 群组（16 字节 UUID）。</summary>
    public sealed class QaulGroup
    {
        public string Id;          // hex 32
        public string Name;
        public long CreatedAt;
        public uint Revision;
        public List<QaulMember> Members = new List<QaulMember>();

        public bool IsMember(string userIdHexOrB58)
        {
            foreach (var m in Members)
                if (m.UserId == userIdHexOrB58) return true;
            return false;
        }
    }

    /// <summary>
    /// qaul 的消息服务（对应 libqaul 的 `services/messaging` + `services/group` + `services/chat`）。
    ///
    /// 线上格式（四层，缺一层对端都解不开）：
    ///   Container { signature(ed25519 over envelope), envelope }
    ///     Envelope { sender_id, receiver_id, payload = EnvelopPayload }
    ///       EnvelopPayload { encrypted = Encrypted{state, session_id, data[nonce, ct]} }
    ///         Messaging { common_message | confirmation | crypto_service | group_invite_message }
    ///
    /// 私聊与群聊走同一个 CommonMessage：私聊的 group_id = 双方 q8id 排序拼接（可预测），
    /// 群聊的 group_id 是群本身的 UUID；两者的载荷分别放在 chat_message / group_message。
    /// </summary>
    public sealed class QaulMessaging
    {
        private readonly LocalIdentity identity;
        private readonly QaulCrypto crypto;
        private readonly QaulNode node;
        private readonly string storeDir;

        /// <summary>已认识的用户（base58 → 用户）。</summary>
        public readonly Dictionary<string, QaulUser> Users = new Dictionary<string, QaulUser>(StringComparer.Ordinal);
        /// <summary>已加入/被邀请的群（hex → 群）。</summary>
        public readonly Dictionary<string, QaulGroup> Groups = new Dictionary<string, QaulGroup>(StringComparer.Ordinal);
        private readonly object gate = new object();

        /// <summary>未确认消息（签名 hex → 待重发容器）。</summary>
        private readonly Dictionary<string, Pending> unconfirmed = new Dictionary<string, Pending>(StringComparer.Ordinal);

        /// <summary>
        /// 还没拿到对端公钥时排队的私聊（peer → 文本）。
        /// 官方 qaul 节点刚被搜到、用户信息还没交换完时，消息不能直接丢掉。
        /// </summary>
        private readonly Dictionary<string, List<string>> pendingDm = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// message id 取号器的「已用最大值」（按 key 分桶），**持久化**。
        ///
        /// qaul 的取号是 `last_message_index + 1`，序号随成员记录一起落库
        /// （services/chat/message.rs:98-100 + :154-155）。我们原来只用「Unix 秒」且
        /// 只在内存里，两个后果：① 时钟回拨（NTP / 用户改表）后新 index 比已发出的还小，
        /// 与旧消息 id 撞车；② 重启后同一秒内重发同样撞车。撞车的后果很硬——
        /// 对端 ChatStore 按 id 跨渠道去重，整条消息被静默丢弃。
        /// 这里以「已用最大值」为硬下界，保证严格单调。
        /// </summary>
        private readonly Dictionary<string, uint> indexHigh = new Dictionary<string, uint>(StringComparer.Ordinal);

        /// <summary>60 秒内已处理过的容器签名（防重复派发；见 HandleContainer）。</summary>
        private readonly Dictionary<string, long> seenSigs = new Dictionary<string, long>(StringComparer.Ordinal);

        private sealed class Pending
        {
            public string Peer;
            public byte[] Container;
            /// <summary>信封签名（unconfirmed 的 key；对端 Confirmation 按它匹配）。</summary>
            public byte[] Sig;
            /// <summary>原始业务报文（Messaging）。会话随重连作废后，用它按新会话重新封包重投，
            /// 而不是把用废密钥加密的旧容器再发一遍（对端永远解不开）。</summary>
            public byte[] Messaging;
            /// <summary>当初发送时的 KK 会话 id；与当前会话不一致就说明该重新封包了。</summary>
            public uint SessionId;
            public long FirstSentAt;
            /// <summary>上次真正发出去的时刻；重投静默期按它算（qaul 的 last_sent）。</summary>
            public long LastSentAt;
            public int Tries;
        }

        /// <summary>重投次数上限（qaul retransmit.rs:162-173 用的是 10 次）。</summary>
        private const int MaxRetransmitTries = 10;

        /// <summary>未确认消息的整体过期时间：qaul 是 1 小时（retransmit.rs:79-90）。</summary>
        private const long RetransmitExpiryMs = 3600000;

        /// <summary>
        /// 第 n 次重投前要等多久：3s 起步、逐次翻倍、60s 封顶。
        /// qaul 是「last_sent + 3s」的固定静默 + 20s 调度超时；这里改成温和的指数退避，
        /// 既避免刚发出就重投造成双份，也不会像原来那样（固定 15s × 4 次）过早放弃。
        /// </summary>
        private static long RetransmitQuietMs(int tries)
        {
            long ms = 3000L << Math.Min(Math.Max(tries, 0), 4);   // 3→6→12→24→48s
            return ms > 60000 ? 60000 : ms;
        }

        // 事件
        public Action<string, string, byte[], long> OnChat;      // (peerB58, text, messageId, sentAt)
        public Action<string, string, byte[], long, string> OnGroupChat; // (groupIdHex, text, messageId, sentAt, senderB58)
        public Action<QaulGroup> OnGroupChanged;
        public Action<string, string> OnUserSeen;                // (peerB58, name)
        public Action<string> OnLog;

        public QaulMessaging(LocalIdentity identity, QaulCrypto crypto, QaulNode node, string storeDir)
        {
            this.identity = identity;
            this.crypto = crypto;
            this.node = node;
            this.storeDir = storeDir;

            crypto.GetRemotePublicKey = GetUserKey;
            crypto.SendCryptoFrame = (peer, enc) =>
            {
                SendEncrypted(peer, enc, null, false);
            };
            crypto.OnSessionReady = peer =>
            {
                foreach (var p in crypto.TakePending(peer))
                {
                    var m = ParseMessaging(p);
                    if (m != null) Resend(peer, p);
                }
            };

            LoadGroups();
            LoadUsers();
            LoadIndexes();
        }

        private void Log(string s)
        {
            Android.Util.Log.Info("QaulMsg", s);
            var cb = OnLog;
            if (cb != null) { try { cb(s); } catch { } }
        }

        /* ===================== 用户目录 ===================== */

        public string MyIdB58 { get { return identity.QaulPeerId; } }
        public byte[] MyIdBytes { get { return identity.QaulPeerIdBytes; } }

        public void UpsertUser(string idB58, string name, byte[] edPub)
        {
            UpsertUser(idB58, name, edPub, false);
        }

        /// <summary>
        /// 登记/更新一个用户。
        /// <paramref name="nodeIdentity"/> 为 true 时表示这只是某台设备的机身 PeerId，
        /// 不当作聊天账号（详见 QaulUser.Node 的说明）。
        /// </summary>
        public void UpsertUser(string idB58, string name, byte[] edPub, bool nodeIdentity)
        {
            if (string.IsNullOrEmpty(idB58)) return;
            if (idB58 == MyIdB58) return;
            lock (gate)
            {
                QaulUser u;
                if (!Users.TryGetValue(idB58, out u))
                {
                    u = new QaulUser { Id = idB58, Node = nodeIdentity };
                    Users[idB58] = u;
                }
                if (!string.IsNullOrEmpty(name)) { u.Name = name; u.Node = false; }
                else if (!nodeIdentity && u.Node && string.IsNullOrEmpty(u.Name)) u.Node = false;
                if (edPub != null && edPub.Length == 32) u.KeyHex = LocalIdentity.Hex(edPub);
                u.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            var cb = OnUserSeen;
            if (cb != null) { try { cb(idB58, name); } catch { } }

            // 用户目录要落库：否则重启后群还在、成员公钥没了，再邀请/重发都发不出去
            SaveUsers();

            // 刚拿到公钥 → 把之前排队的私聊补发出去
            if (HaveKey(idB58)) FlushPendingDm(idB58);
        }

        /// <summary>登记用户签名资料里的个性签名（bio）。</summary>
        public void SetBio(string idB58, string bio)
        {
            if (string.IsNullOrEmpty(idB58) || string.IsNullOrEmpty(bio)) return;
            lock (gate)
            {
                QaulUser u;
                if (Users.TryGetValue(idB58, out u)) u.Bio = bio;
            }
        }

        public byte[] GetUserKey(string idB58)
        {
            lock (gate)
            {
                QaulUser u;
                if (!Users.TryGetValue(idB58, out u) || string.IsNullOrEmpty(u.KeyHex)) return null;
                return LocalIdentity.Unhex(u.KeyHex);
            }
        }

        public bool HaveKey(string idB58)
        {
            return GetUserKey(idB58) != null;
        }

        /// <summary>某个 id 是不是「节点身份」而不是「可聊的聊天账号」。</summary>
        public bool IsNodeIdentity(string idB58)
        {
            if (string.IsNullOrEmpty(idB58) || idB58 == MyIdB58) return false;
            return node != null && node.IsNodePeerId(idB58);
        }

        /* ===================== 发送：私聊 / 群聊 ===================== */

        /// <summary>私聊：group_id = 双方 q8id 排序拼接（qaul 的 DM 就是这么算的）。</summary>
        public bool SendDm(string peerB58, string text)
        {
            byte[] mid;
            return SendDm(peerB58, text, out mid);
        }

        /// <summary>
        /// 发私聊，并把**真实 message id** 回传（qaul 的
        /// CRC64(group)‖CRC64(sender)‖index 派生 id）。
        ///
        /// 上层必须用这个 id 落库：老实现发送方本地落库用 Msg.New 的随机 GUID，
        /// 于是同一条消息在发送端与接收端 id 不同——接收端按容器里的真实 id 去重时
        /// 救不了发送端自己（中继绕回来 → 重影），MarkSeen 标的也是随机 id，
        /// 跨链路去重（帧直投 vs qaul 中继 vs bitchat）全部失效。
        /// </summary>
        public bool SendDm(string peerB58, string text, out byte[] messageId)
        {
            messageId = null;
            if (string.IsNullOrEmpty(peerB58) || peerB58 == MyIdB58) return false;

            // 还没学到对端公钥（刚搜到、用户信息还在路上）：
            // 先把消息排队，等 UpsertUser 拿到公钥后自动补发，而不是让用户以为发不出去。
            if (!HaveKey(peerB58))
            {
                lock (gate)
                {
                    List<string> q;
                    if (!pendingDm.TryGetValue(peerB58, out q)) { q = new List<string>(); pendingDm[peerB58] = q; }
                    q.Add(text ?? "");
                }
                Log(L10n.T("还没有 ") + Short(peerB58) + L10n.T(" 的公钥，消息已排队，公钥到手后自动发送"));
                return false;
            }

            var groupId = DirectGroupId(MyIdBytes, Base58.Decode(peerB58));
            var mid = MessageId(groupId, MyIdBytes, NextDmIndex(peerB58));
            messageId = mid;
            // 注意：发送方不触发 OnChat（那是「收到消息」的入口），
            // 否则上层既会落库一次、回调又落一次，消息会重影。
            return SendChat(peerB58, groupId, text, mid, false);
        }

        /// <summary>
        /// 把一段文本**按指定的 group_id** 发给某个对端。
        ///
        /// 中间层与频道的中继要用它：qaul 没有群播，频道消息只能给每个已知节点各发一份单播。
        /// 若用 <see cref="SendDm"/>，group_id 会被算成"双方私聊组"，对端收到后会落进
        /// qaul 私聊会话而不是原来的频道；这里显式带上频道的 group_id，
        /// 对端按 group_id 反查就能回到同一个聊天室（中间层的发现信标同理）。
        /// </summary>
        public bool SendToPeerInGroup(string peerB58, byte[] groupId, string text, uint index)
        {
            if (string.IsNullOrEmpty(peerB58) || peerB58 == MyIdB58) return false;
            if (groupId == null || groupId.Length != 16) return false;
            // 信标/中继不做排队：这一帧丢了下一周期还会再发，排队反而会堆积
            if (!HaveKey(peerB58)) return false;

            var mid = MessageId(groupId, MyIdBytes, index);
            return SendChat(peerB58, groupId, text, mid, false);
        }

        /// <summary>某个对端还有多少条排队待发的私聊。</summary>
        public int PendingDmCountOf(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return 0;
            lock (gate)
            {
                List<string> q;
                return pendingDm.TryGetValue(peerB58, out q) ? q.Count : 0;
            }
        }

        /// <summary>
        /// 某个对端有多少条私聊卡在「KK 半握手、等 msg2」队列里。
        ///
        /// 与 <see cref="PendingDmCountOf"/>（缺公钥排队）是两回事：这条队列在
        /// QaulCrypto 里，会话进入传输态后由 OnSessionReady 自动补发。上层据此
        /// 把消息显示成「排队中」而不是「发送失败」——老实现两种情况都返回 false，
        /// 用户以为消息丢了，其实稍后自己会到。
        /// </summary>
        public int SessionPendingOf(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58) || crypto == null) return 0;
            try { return crypto.PendingCount(peerB58); } catch { return 0; }
        }

        /// <summary>
        /// 分配下一条私聊的 message id index。
        ///
        /// qaul 的 message id = CRC64(group)‖CRC64(sender)‖index。index 若恒为 0，
        /// 同一发送方发出的每条私聊都会得到**完全相同**的 id，对端 ChatStore 按 id
        /// 跨渠道去重（见 ChatStore.Add「重复 id 会被忽略」），第二条起全部被丢弃——
        /// 真机 A/B 复现：频道中继 index 递增能正常到达，私聊却永远只到第一条。
        ///
        /// 这里用「Unix 秒」起步：既不与历史 index=0 的旧消息撞车，也天然单调、
        /// 重启不回退；同一秒内连发则 +1 续增。
        /// </summary>
        private uint NextDmIndex(string peerB58)
        {
            return NextIndex("dm:" + peerB58);
        }

        /// <summary>
        /// 分配下一条 message id 的 index（qaul 的 generate_message_id 第三字段）。
        ///
        /// 下界取 max(当前 Unix 秒, 已用最大值 + 1)：
        ///   · 「Unix 秒」起步是为了不与历史 index 恒为 0 的旧消息撞车；
        ///   · 「已用最大值 + 1」是硬下界——时钟回拨 / NTP 校正 / 重启后同秒重发时，
        ///     光靠时间戳会让新 id 与已发出的 id 重复，对端按 id 去重会把整条消息丢掉。
        /// 取号结果落盘（qaul_index.json），重启后接着往上走。
        /// </summary>
        public uint NextIndex(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "_";
            uint now = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x7FFFFFFF);
            uint result;
            string json;
            lock (gate)
            {
                uint used;
                if (!indexHigh.TryGetValue(key, out used)) used = 0;
                uint next = used + 1;
                if (next < now) next = now;
                indexHigh[key] = next;
                result = next;
                json = JsonConvert.SerializeObject(indexHigh);
            }
            SaveIndexes(json);
            return result;
        }

        public int PendingDmCount
        {
            get
            {
                lock (gate)
                {
                    int n = 0;
                    foreach (var kv in pendingDm) n += kv.Value.Count;
                    return n;
                }
            }
        }

        /// <summary>公钥到手后，把排队消息按顺序补发。</summary>
        private void FlushPendingDm(string peerB58)
        {
            List<string> queued = null;
            lock (gate)
            {
                List<string> q;
                if (pendingDm.TryGetValue(peerB58, out q) && q.Count > 0)
                {
                    queued = new List<string>(q);
                    pendingDm.Remove(peerB58);
                }
            }
            if (queued == null) return;

            Log(L10n.T("对端公钥已就绪，补发 ") + queued.Count + L10n.T(" 条排队的私聊 → ") + Short(peerB58));
            foreach (var text in queued)
            {
                var groupId = DirectGroupId(MyIdBytes, Base58.Decode(peerB58));
                var mid = MessageId(groupId, MyIdBytes, NextDmIndex(peerB58));
                SendChat(peerB58, groupId, text, mid, false);
            }
        }

        /// <summary>群聊：逐个成员点对点发送（qaul 没有群播，就是 N 份单播）。</summary>
        public bool SendGroupText(string groupIdHex, string text)
        {
            byte[] mid;
            return SendGroupText(groupIdHex, text, out mid);
        }

        /// <summary>
        /// 发群聊，并回传发送方本机应当使用的「真实 message id」。
        ///
        /// qaul 群发本质是给每个成员各发一份单播，但 group_id 与发送者固定，于是
        /// MessageId(group, sender, index) 对**所有成员是同一个值**——接收端按线上
        /// 容器里的 id 落库，彼此完全一致。唯独发送方本机此前用 Msg.New 的随机 GUID
        /// 落库，导致「自己看到的 id」与「别人看到的 id」对不上：群消息一旦要按 id
        /// 对账（撤回 / 编辑 / 跨链路去重）就会错乱。这里把发送方本该用的那个 id
        /// 一并回传，让上层 AlreadyStored 用它落库，群聊 id 即与接收端彻底统一。
        /// </summary>
        public bool SendGroupText(string groupIdHex, string text, out byte[] messageId)
        {
            messageId = null;
            QaulGroup g;
            lock (gate)
            {
                if (!Groups.TryGetValue(groupIdHex, out g)) return false;
            }

            var gid = LocalIdentity.Unhex(groupIdHex);
            bool any = false;
            uint index = 0;
            foreach (var m in g.Members)
            {
                if (m.UserId == MyIdB58) { index = m.LastIndex + 1; continue; }
                if (m.State != 1) continue;
                var mid = MessageId(gid, MyIdBytes, index);
                if (SendChat(m.UserId, gid, text, mid, true)) any = true;
            }

            // 发送方本机落库用的 id 必须与接收端一致：同为 MessageId(group, sender, index)。
            messageId = MessageId(gid, MyIdBytes, index);

            lock (gate)
            {
                foreach (var m in g.Members)
                    if (m.UserId == MyIdB58) m.LastIndex = index;
            }
            SaveGroups();

            return any;
        }

        /* ===================== 附件（qaul 的 ChatFile） ===================== */

        /// <summary>一个正在接收的附件（支持断点续传）。</summary>
        private sealed class IncomingFile
        {
            public ulong FileId;
            public string Name;
            public string Ext;
            public string Description;
            public uint Size;
            public uint ChunkSize;
            public uint MessageCount;
            public string Peer;
            public long SentAt;
            public int Received;
            /// <summary>qaul 群 id（hex）。群文件才带；空串表示这是一条 qaul 私聊文件，应归入私聊会话。</summary>
            public string GroupIdHex = "";
            public readonly Dictionary<uint, byte[]> Chunks = new Dictionary<uint, byte[]>();
            
            // 断点续传支持
            public uint StartPiece = 0;           // 起始分片索引（断点续传）
            public uint EndPiece = 0;             // 结束分片索引（断点续传）
            public long StartOffset = 0;          // 起始偏移字节
            public long EndOffset = 0;            // 结束偏移字节
            public readonly HashSet<uint> MissingPieces = new HashSet<uint>(); // 缺失的分片索引
            
            /// <summary>标记哪些分片已接收</summary>
            public void MarkPieceReceived(uint index)
            {
                MissingPieces.Remove(index);
            }
            
            /// <summary>计算缺失的分片索引</summary>
            public void CalculateMissingPieces()
            {
                MissingPieces.Clear();
                // 基于 MessageCount 计算预期的分片范围
                // message_count = 1(info) + data_pieces
                uint expectedPieces = MessageCount > 1 ? MessageCount - 1 : 0;
                for (uint i = 0; i < expectedPieces; i++)
                {
                    if (!Chunks.ContainsKey(i))
                        MissingPieces.Add(i);
                }
            }
            
            /// <summary>获取缺失分片的列表（用于请求重传）</summary>
            public uint[] GetMissingPiecesArray()
            {
                return MissingPieces.ToArray<uint>();
            }
        }

        /// <summary>分片接收中的附件：(发送者, file_id) → 状态。</summary>
        private readonly Dictionary<string, IncomingFile> incoming = new Dictionary<string, IncomingFile>(StringComparer.Ordinal);
        private long fileCounter;

        /// <summary>
        /// 收到完整附件后的回调：(发送者 base58, 文件名, 字节内容, 发送时间, qaul 群 id hex)。
        /// 末参 groupHex 非空（长度 32）表示这是一条 qaul 群文件，应归入对应群会话；
        /// 否则是 qaul 私聊文件，应归入「qaul:&lt;发送者&gt;」私聊会话。
        /// 上层把它落盘并当成图片/视频消息插进会话。
        /// </summary>
        public Action<string, string, byte[], long, string> OnFile;

        /// <summary>发送附件进度回调：(对方 base58, 文件名, 已发字节, 总字节)，每发一片调用一次。</summary>
        public Action<string, string, long, long> OnFileProgress;

        /// <summary>qaul 附件分片大小，与 libqaul 的 DEF_PACKAGE_SIZE 一致。</summary>
        private const uint FileChunkSize = 64000;

        /// <summary>
        /// 处理收到的 ChatFileContainer（qaul 的附件容器）。
        ///
        /// 线上结构（services/chat/file.rs + chatfile_net.proto）：
        ///   ChatFileContainer { file_info = 1 | file_data = 2 }
        ///     ChatFileInfo { file_id, file_name, file_extension, file_size,
        ///                    file_description, start_index, message_count, data_chunk_size }
        ///     ChatFileData  { file_id, start_index, message_count, data }
        /// 先到 ChatFileInfo 描述文件，随后若干 ChatFileData 分片；
        /// 分片齐了拼成完整文件再交给界面。
        /// </summary>
        private void HandleChatFileContainer(string senderB58, byte[] data, long sentAt, string groupIdHex)
        {
            if (data == null || data.Length < 2) return;

            var r = new PbReader(data);
            int field, wire;
            while (r.Next(out field, out wire))
            {
                if (field == 1)
                {
                    var info = r.Sub();
                    ulong fileId = 0;
                    string name = null, ext = null, desc = null, groupHex = null;
                    uint size = 0, count = 0, chunk = FileChunkSize;
                    int f2, w2;
                    while (info.Next(out f2, out w2))
                    {
                        if (f2 == 1) fileId = info.Varint();
                        else if (f2 == 2) name = info.Str();
                        else if (f2 == 3) ext = info.Str();
                        else if (f2 == 4) size = (uint)info.Varint();
                        else if (f2 == 5) desc = info.Str();
                        else if (f2 == 7) count = (uint)info.Varint();
                        else if (f2 == 8) chunk = (uint)info.Varint();
                        // field 9：qaul 群 id（hex）。群文件才带，用于把文件归入正确的群会话。
                        else if (f2 == 9) groupHex = info.Str();
                        else info.Skip(w2);
                    }
                    if (fileId == 0) continue;

                    var key = senderB58 + ":" + fileId;
                    IncomingFile f;
                    lock (gate)
                    {
                        if (!incoming.TryGetValue(key, out f))
                        {
                            f = new IncomingFile
                            {
                                FileId = fileId,
                                Peer = senderB58,
                                SentAt = sentAt,
                                ChunkSize = chunk > 0 ? chunk : FileChunkSize,
                                MessageCount = count,
                                Size = size,
                                Name = string.IsNullOrEmpty(name) ? ("file_" + fileId) : name,
                                Ext = ext ?? "",
                                Description = desc ?? "",
                                GroupIdHex = groupHex ?? ""
                            };
                            incoming[key] = f;
                            // 超过 10 分钟还没收完的半截传输直接丢弃，别占着内存
                            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var stale = new List<string>();
                            foreach (var kv in incoming) if (now - kv.Value.SentAt > 600000) stale.Add(kv.Key);
                            foreach (var k in stale) incoming.Remove(k);
                        }
                        // 数据分片若先于 info 到达，会先建一个占位 IncomingFile（无 Name/Ext/MessageCount）；
                        // info 包到达后必须把元数据回填，否则文件名/扩展名/分片总数都会缺失。
                        if (f.Size == 0 && size > 0) f.Size = size;
                        if (f.MessageCount == 0 && count > 0) f.MessageCount = count;
                        if (string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(name)) f.Name = name;
                        if (string.IsNullOrEmpty(f.Ext) && !string.IsNullOrEmpty(ext)) f.Ext = ext;
                        if (string.IsNullOrEmpty(f.Description) && !string.IsNullOrEmpty(desc)) f.Description = desc;
                        if (string.IsNullOrEmpty(f.GroupIdHex) && !string.IsNullOrEmpty(groupHex)) f.GroupIdHex = groupHex;
                        f.ChunkSize = chunk > 0 ? chunk : FileChunkSize;
                    }
                    Log(L10n.T("开始接收附件「") + f.Name + "」(" + f.Size + L10n.T(" 字节, ") + count + L10n.T(" 片) 来自 ") + Short(senderB58));
                    TryFinishFile(key);
                }
                else if (field == 2)
                {
                    var d = r.Sub();
                    ulong fileId = 0;
                    uint index = 0;
                    byte[] chunk = null;
                    int f3, w3;
                    while (d.Next(out f3, out w3))
                    {
                        if (f3 == 1) fileId = d.Varint();
                        else if (f3 == 2) index = (uint)d.Varint();
                        else if (f3 == 4) chunk = d.Bytes();
                        else d.Skip(w3);
                    }
                    if (fileId == 0 || chunk == null) continue;

                    var key = senderB58 + ":" + fileId;
                    IncomingFile f;
                    lock (gate)
                    {
                        if (!incoming.TryGetValue(key, out f))
                        {
                            // 官方的 info 包理论先行；万一乱序到达，用一大号先建占位
                            f = new IncomingFile { FileId = fileId, Peer = senderB58, SentAt = sentAt, ChunkSize = FileChunkSize };
                            incoming[key] = f;
                        }
                        if (!f.Chunks.ContainsKey(index)) 
                        { 
                            f.Chunks[index] = chunk; 
                            f.Received += chunk.Length; 
                            f.MarkPieceReceived(index); // 标记分片已接收
                        }
                    }
                    TryFinishFile(key);
                }
                else r.Skip(wire);
            }
        }

        private void TryFinishFile(string key)
        {
            IncomingFile f;
            lock (gate) { if (!incoming.TryGetValue(key, out f)) return; }

            bool complete;
            lock (gate)
            {
                // 官方语义：message_count = 1(信息包) + 数据分片数；完整 = 收齐全部数据分片。
                // 优先按总字节数判定（最稳，任何发送方都适用），再以分片数兜底。
                if (f.Size > 0 && f.Received >= f.Size) complete = true;
                else if (f.MessageCount > 1 && f.Chunks.Count >= f.MessageCount - 1) complete = true;
                else complete = false;
            }
            if (!complete)
            {
                // 计算缺失分片并请求重传
                f.CalculateMissingPieces();
                var missing = f.GetMissingPiecesArray();
                if (missing.Length > 0)
                {
                    RequestMissingPieces(f.Peer, f.FileId, missing);
                }
                return;
            }

            byte[] assembled;
            lock (gate)
            {
                incoming.Remove(key);
                int total = 0;
                foreach (var c in f.Chunks.Values) total += c.Length;
                assembled = new byte[total];
                int pos = 0;
                // 分片必须按 start_index 升序拼装，否则文件就是乱序的垃圾。
                var ordered = new List<uint>(f.Chunks.Keys);
                ordered.Sort();
                foreach (var idx in ordered)
                {
                    var chunk = f.Chunks[idx];
                    Buffer.BlockCopy(chunk, 0, assembled, pos, chunk.Length);
                    pos += chunk.Length;
                }
            }
            if (assembled.Length == 0) return;

            Log(L10n.T("接收附件完成「") + f.Name + "」(" + assembled.Length + L10n.T(" 字节)"));
            var cb = OnFile;
            if (cb != null) { try { cb(f.Peer, f.Name, assembled, f.SentAt, f.GroupIdHex); } catch { } }
        }

        /// <summary>
        /// 给某人发一个附件（图片/视频/任意文件）。
        /// 与官方对齐：先发一个 ChatFileInfo 描述文件，再按 64KB 一片发 ChatFileData。
        /// </summary>
        public bool SendFile(string peerB58, string fileName, byte[] data, string description = "")
        {
            if (string.IsNullOrEmpty(peerB58) || data == null || data.Length == 0) return false;
            if (peerB58 == MyIdB58) return false;
            if (!HaveKey(peerB58))
            {
                Log(L10n.T("还没有 ") + Short(peerB58) + L10n.T(" 的公钥，附件暂时发不出去"));
                return false;
            }

            byte[] peerBytes;
            try { peerBytes = Base58.Decode(peerB58); } catch { return false; }
            var groupId = DirectGroupId(MyIdBytes, peerBytes);
            return SendFileToPeer(peerB58, groupId, fileName, data, description);
        }

        /// <summary>
        /// 请求重传缺失的文件分片（断点续传/丢包重传）。
        /// </summary>
        public void RequestMissingPieces(string peerB58, ulong fileId, uint[] missingIndices)
        {
            if (string.IsNullOrEmpty(peerB58) || missingIndices == null || missingIndices.Length == 0) return;
            
            try
            {
                // 发送文件重传请求信令
                var request = new Dictionary<string, object>
                {
                    ["type"] = "file-retransmit-request",
                    ["file_id"] = (long)fileId,
                    ["missing_indices"] = missingIndices
                };
                var container = new PbWriter();
                var data = new PbWriter();
                data.Int(1, (long)fileId);
                var indices = new PbWriter();
                foreach (var idx in missingIndices)
                    indices.Int(1, (long)idx);
                data.Sub(2, indices);
                container.Sub(1, data);
                
                // 这里需要发送一个特殊的信令消息类型
                var msg = new PbWriter();
                msg.Sub(6, container); // CommonMessage.file_message = 5, 这里用自定义类型
                // 发送重传请求
                SendMessaging(peerB58, msg.ToArray(), null, true);
            }
            catch (Exception ex)
            {
                Log(L10n.T("请求重传分片失败: ") + ex.Message);
            }
        }

        /// <summary>
        /// 给某个 qaul 群发附件：群发其实是给每个成员各发一份（与官方「群发=广播给远程成员」一致）。
        /// 每个成员用其直连分组 id 加密，file_id 由 (该直连分组, 发送者, 名, 大小, 时间) 派生，互不干扰。
        /// </summary>
        public bool SendFileToGroup(string groupIdHex, string fileName, byte[] data, string description = "")
        {
            if (data == null || data.Length == 0) return false;
            QaulGroup g;
            lock (gate) { if (!Groups.TryGetValue(groupIdHex, out g)) return false; }

            bool any = false;
            foreach (var m in g.Members)
            {
                if (m.UserId == MyIdB58) continue;
                if (!HaveKey(m.UserId)) { Log(L10n.T("群发附件跳过（无公钥）：") + Short(m.UserId)); continue; }
                byte[] mb;
                try { mb = Base58.Decode(m.UserId); } catch { continue; }
                var gid = DirectGroupId(MyIdBytes, mb);
                // 把 qaul 群 id 带进附件元数据（field 9），接收端据此把文件归入群会话
                if (SendFileToPeer(m.UserId, gid, fileName, data, description, groupIdHex)) any = true;
            }
            return any;
        }

        /// <summary>把附件发给单个对端（给定分组 id）。SendFile / SendFileToGroup 共用。
        /// qaulGroupHex 非空时（群发）会写进 ChatFileInfo.field9，让接收端知道这是群文件。
        /// 支持断点续传：通过文件分片索引实现断点续传，支持断点续传恢复。
        /// </summary>
        private bool SendFileToPeer(string peerB58, byte[] groupId, string fileName, byte[] data, string description, string qaulGroupHex = null, long startOffset = 0, long? maxBytes = null)
        {
            var fileLength = data.Length;
            var transferLength = maxBytes ?? (data.Length - startOffset);
            if (transferLength <= 0) return false;

            var fileId = GenerateFileId(groupId, MyIdBytes, fileName, (uint)data.Length);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var mid = MessageId(groupId, MyIdBytes, (uint)(Interlocked.Increment(ref fileCounter) & 0x7FFFFFFF));

            uint totalPieces = (uint)((data.Length + FileChunkSize - 1) / FileChunkSize);
            uint startPiece = (uint)(startOffset / FileChunkSize);
            uint endPiece = (uint)Math.Min(totalPieces, (startOffset + transferLength + FileChunkSize - 1) / FileChunkSize);
            
            // 如果是续传，只发送需要的分片
            uint piecesToSend = endPiece - startPiece;
            if (piecesToSend == 0) return true; // 已经传完

            // 官方：message_count = 1(信息包) + 数据分片数。收端据此判定收齐。
            uint messageCount = (uint)(piecesToSend + 1);

            string ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && ext[0] == '.') ext = ext.Substring(1);

            // ChatFileInfo：增加断点续传字段
            var fi = new PbWriter();
            fi.Int(1, (long)fileId);
            fi.Str(2, Path.GetFileName(fileName));
            fi.Str(3, ext ?? "");
            fi.Int(4, data.Length); // 完整文件大小
            fi.Str(5, description ?? "");
            fi.Int(6, (int)startOffset); // 起始偏移（断点续传）
            fi.Int(7, messageCount);
            fi.Int(8, FileChunkSize);
            fi.Int(9, (int)startPiece); // 起始分片索引
            fi.Int(10, (int)endPiece); // 结束分片索引
            if (!string.IsNullOrEmpty(qaulGroupHex)) fi.Str(11, qaulGroupHex); // 群文件：携带 qaul 群 id

            var infoContainer = new PbWriter();
            infoContainer.Sub(1, fi);
            if (!SendFileMessage(peerB58, groupId, mid, nowMs, infoContainer.ToArray())) return false;

            // ChatFileData：只发送需要的分片
            for (uint idx = startPiece; idx < endPiece; idx++)
            {
                int pos = (int)(idx * FileChunkSize);
                int n = (int)Math.Min((long)FileChunkSize, data.Length - pos);
                var piece = new byte[n];
                Buffer.BlockCopy(data, pos, piece, 0, n);

                var fd = new PbWriter();
                fd.Int(1, (long)fileId);
                fd.Int(2, idx);
                fd.Int(3, messageCount);
                fd.Bytes(4, piece);
                // 兼容旧版本：发送实际分片索引

                var dataContainer = new PbWriter();
                dataContainer.Sub(2, fd);
                SendFileMessage(peerB58, groupId, mid, nowMs, dataContainer.ToArray());

                // 发送进度回调（每片一次）
                var prog = OnFileProgress;
                if (prog != null) 
                { 
                    try { prog(peerB58, fileName, pos + n, data.Length); } 
                    catch { } 
                }
            }
            Log(L10n.T("已发送附件「") + Path.GetFileName(fileName) + "」(" + data.Length + L10n.T(" 字节, ") + messageCount + L10n.T(" 片, 偏移:") + startOffset + ") → " + Short(peerB58));
            
            // 缓存文件用于断点续传重传
            CacheFileForRetransmission(fileId, fileName, data);
            // 存储发送上下文用于重传
            lock (_fileCacheLock)
            {
                if (_fileCache.TryGetValue(fileId, out var cached))
                {
                    cached.PeerId = peerB58;
                    cached.GroupId = groupId;
                    cached.MessageId = mid;
                    cached.SentAt = nowMs;
                }
            }
            
            return true;
        }

        /// <summary>把 ChatFileContainer 装进 CommonMessage.file_message 发出去。</summary>
        private bool SendFileMessage(string peerB58, byte[] groupId, byte[] messageId, long sentAt, byte[] container)
        {
            var fm = new PbWriter();
            fm.Bytes(1, container);                       // FileMessage { content }

            var cm = new PbWriter();
            cm.Bytes(1, messageId);
            cm.Bytes(2, groupId);
            cm.Int(3, sentAt);
            cm.Sub(5, fm);                                // CommonMessage.file_message = 5

            var msg = new PbWriter();
            msg.Sub(6, cm);
            return SendMessaging(peerB58, msg.ToArray(), null, true);
        }

                /// <summary>qaul 的 file_id：CRC64(GO-ISO) over group_id + sender + name + size + time。</summary>
        public static ulong GenerateFileId(byte[] groupId, byte[] sender, string fileName, uint size)
        {
            var nameBytes = Encoding.UTF8.GetBytes(fileName ?? "");
            var ts = BitConverter.GetBytes((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (BitConverter.IsLittleEndian) Array.Reverse(ts);
            var sizeBytes = BitConverter.GetBytes(size);
            if (BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);

            var buf = new byte[groupId.Length + sender.Length + nameBytes.Length + sizeBytes.Length + ts.Length];
            int p = 0;
            Buffer.BlockCopy(groupId, 0, buf, p, groupId.Length); p += groupId.Length;
            Buffer.BlockCopy(sender, 0, buf, p, sender.Length); p += sender.Length;
            Buffer.BlockCopy(nameBytes, 0, buf, p, nameBytes.Length); p += nameBytes.Length;
            Buffer.BlockCopy(sizeBytes, 0, buf, p, sizeBytes.Length); p += sizeBytes.Length;
            Buffer.BlockCopy(ts, 0, buf, p, ts.Length); p += ts.Length;
            return Crc64.GoIso(buf);
        }



        // 文件发送缓存：用于断点续传重传
        private readonly Dictionary<ulong, CachedFile> _fileCache = new Dictionary<ulong, CachedFile>();
        private readonly object _fileCacheLock = new object();
        private const int MaxFileCacheSize = 10; // 最多缓存 10 个文件
        private const long FileCacheTtlMs = 30 * 60 * 1000; // 30 分钟过期

        private sealed class CachedFile
        {
            public ulong FileId;
            public string FileName;
            public byte[] Data;
            public long CreatedAt;
            public long LastAccessed;
            public string PeerId;
            public byte[] GroupId;
            public byte[] MessageId;
            public long SentAt;
        }

        private void CacheFileForRetransmission(ulong fileId, string fileName, byte[] data)
        {
            lock (_fileCacheLock)
            {
                // 清理过期缓存
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var expired = _fileCache
                    .Where(kvp => now - kvp.Value.LastAccessed > FileCacheTtlMs)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in expired) _fileCache.Remove(key);

                // 限制缓存大小
                if (_fileCache.Count >= MaxFileCacheSize)
                {
                    var oldest = _fileCache.OrderBy(kvp => kvp.Value.LastAccessed).First();
                    _fileCache.Remove(oldest.Key);
                }

                _fileCache[fileId] = new CachedFile
                {
                    FileId = fileId,
                    FileName = Path.GetFileName(fileName),
                    Data = data,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastAccessed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
        }

        private void RetransmitFilePieces(ulong fileId, uint[] indices)
        {
            CachedFile cached;
            lock (_fileCacheLock)
            {
                if (!_fileCache.TryGetValue(fileId, out cached))
                {
                    Log(L10n.T("重传请求失败：文件不在缓存中 fileId=") + fileId);
                    return;
                }
                cached.LastAccessed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            var data = cached.Data;

            foreach (var idx in indices)
            {
                int pos = (int)(idx * FileChunkSize);
                int n = (int)Math.Min((long)FileChunkSize, data.Length - pos);
                if (n <= 0) continue;

                var piece = new byte[n];
                Buffer.BlockCopy(data, pos, piece, 0, n);

                var fd = new PbWriter();
                fd.Int(1, (long)fileId);
                fd.Int(2, idx);
                fd.Int(3, 1); // 单片重传时 messageCount=1
                fd.Bytes(4, piece);

                var dataContainer = new PbWriter();
                dataContainer.Sub(2, fd);
                SendFileMessage(cached.PeerId, cached.GroupId, cached.MessageId, cached.SentAt, dataContainer.ToArray());
            }

            Log(L10n.T("重传完成：fileId=") + fileId + L10n.T(", 片数=") + indices.Length);
        }

private bool SendChat(string peerB58, byte[] groupId, string text, byte[] messageId, bool isGroup)
        {
            // CommonMessage { message_id=1, group_id=2, sent_at=3, chat=4 | group=6 }
            var cm = new PbWriter();
            cm.Bytes(1, messageId);
            cm.Bytes(2, groupId);
            cm.Int(3, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var content = new PbWriter();
            content.Str(1, text ?? "");
            if (isGroup) cm.Sub(6, content);     // GroupMessage { content }
            else cm.Sub(4, content);             // ChatMessage { content }

            // Messaging { common_message = 6 }
            var msg = new PbWriter();
            msg.Sub(6, cm);
            return SendMessaging(peerB58, msg.ToArray(), messageId, true);
        }

        /* ===================== 群管理 ===================== */

        public QaulGroup CreateGroup(string name)
        {
            var idBytes = new byte[16];
            var rnd = Curve25519.RandomSeed();
            Buffer.BlockCopy(rnd, 0, idBytes, 0, 16);

            var g = new QaulGroup
            {
                Id = LocalIdentity.Hex(idBytes),
                Name = string.IsNullOrEmpty(name) ? L10n.T("新群组") : name,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Revision = 1
            };
            g.Members.Add(new QaulMember
            {
                UserId = MyIdB58,
                Role = 255,
                JoinedAt = g.CreatedAt,
                State = 1,
                LastIndex = 0
            });

            lock (gate) { Groups[g.Id] = g; }
            SaveGroups();
            var cb = OnGroupChanged;
            if (cb != null) { try { cb(g); } catch { } }
            return g;
        }

        /// <summary>邀请某人进群：发 GroupContainer{invite_member{group_info}}。</summary>
        public bool Invite(string groupIdHex, string peerB58)
        {
            // 没有对方公钥就发不出去（KK 建不了会话）。此时不要先把人写进成员表，
            // 否则群里会挂一个永远「待接受」的幽灵成员。
            if (!HaveKey(peerB58))
            {
                Log(L10n.T("邀请失败：还没有 ") + Short(peerB58) + L10n.T(" 的公钥（等对方上线/路由信息）"));
                return false;
            }

            QaulGroup g;
            lock (gate)
            {
                if (!Groups.TryGetValue(groupIdHex, out g)) return false;
                bool exists = false;
                foreach (var m in g.Members) if (m.UserId == peerB58) exists = true;
                if (!exists)
                {
                    g.Members.Add(new QaulMember
                    {
                        UserId = peerB58,
                        Role = 0,
                        JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        State = 0
                    });
                    g.Revision++;
                    SaveGroups();
                }
            }

            // GroupContainer { invite_member = 1 (InviteMember { group = 1 (GroupInfo) }) }
            var member = new PbWriter();
            member.Sub(1, GroupInfoProto(g));          // InviteMember.group
            var container = new PbWriter();
            container.Sub(1, member);                  // GroupContainer.invite_member

            // Messaging { group_invite_message = 5 { content = GroupContainer } }
            var invite = new PbWriter();
            invite.Bytes(1, container.ToArray());
            var msg = new PbWriter();
            msg.Sub(5, invite);

            Log(L10n.T("邀请 ") + peerB58.Substring(0, Math.Min(10, peerB58.Length)) + L10n.T(" 进群「") + g.Name + "」");
            return SendMessaging(peerB58, msg.ToArray(), null, true);
        }

        /// <summary>把群信息（成员表快照）群发给所有成员；<paramref name="except"/> 可跳过某人。</summary>
        private void BroadcastGroupInfo(QaulGroup g, string except = null)
        {
            List<QaulMember> members;
            lock (gate) { members = new List<QaulMember>(g.Members); }

            foreach (var m in members)
            {
                if (m.UserId == MyIdB58) continue;
                if (except != null && m.UserId == except) continue;
                SendGroupInfo(m.UserId, g);
            }
        }

        /// <summary>发 GroupContainer{ group_info }：成员变更广播（qaul 的 post_group_update）。</summary>
        private bool SendGroupInfo(string peerB58, QaulGroup g)
        {
            var container = new PbWriter();
            container.Sub(3, GroupInfoProto(g));       // GroupContainer.group_info

            var invite = new PbWriter();
            invite.Bytes(1, container.ToArray());
            var msg = new PbWriter();
            msg.Sub(5, invite);
            return SendMessaging(peerB58, msg.ToArray(), null, true);
        }

        /// <summary>退出群（仅本地移除；不通知其他人，和 qaul 的「静默退出」一致）。</summary>
        public bool LeaveGroup(string groupIdHex)
        {
            QaulGroup g;
            lock (gate)
            {
                if (!Groups.TryGetValue(groupIdHex, out g)) return false;
                Groups.Remove(groupIdHex);
                g.Members.RemoveAll(x => x.UserId == MyIdB58);
                g.Revision++;
            }
            SaveGroups();

            var cb = OnGroupChanged;
            if (cb != null) { try { cb(g); } catch { } }
            Log(L10n.T("退出群「") + g.Name + "」");
            return true;
        }

        /// <summary>把某人移出群：先通知本人 RemovedMember，再把新成员表广播给剩下的人。</summary>
        public bool RemoveMember(string groupIdHex, string peerB58)
        {
            QaulGroup g;
            lock (gate)
            {
                if (!Groups.TryGetValue(groupIdHex, out g)) return false;
                if (peerB58 == MyIdB58) return false;
                if (g.Members.RemoveAll(x => x.UserId == peerB58) == 0) return false;
                g.Revision++;
            }
            SaveGroups();

            // RemovedMember { group_id = 1 }
            var removed = new PbWriter();
            removed.Bytes(1, LocalIdentity.Unhex(groupIdHex));
            var container = new PbWriter();
            container.Sub(4, removed);                 // GroupContainer.removed

            var invite = new PbWriter();
            invite.Bytes(1, container.ToArray());
            var msg = new PbWriter();
            msg.Sub(5, invite);
            SendMessaging(peerB58, msg.ToArray(), null, true);

            BroadcastGroupInfo(g, peerB58);

            var cb = OnGroupChanged;
            if (cb != null) { try { cb(g); } catch { } }
            Log(L10n.T("把 ") + Short(peerB58) + L10n.T(" 移出群「") + g.Name + "」");
            return true;
        }

        private PbWriter GroupInfoProto(QaulGroup g)
        {
            var info = new PbWriter();
            info.Bytes(1, LocalIdentity.Unhex(g.Id));
            info.Str(2, g.Name);
            info.Int(3, g.CreatedAt);
            info.Int(4, g.Revision);

            foreach (var m in g.Members)
            {
                var mb = new PbWriter();
                mb.Bytes(1, Base58.Decode(m.UserId));
                mb.Int(2, m.Role);
                mb.Int(3, m.JoinedAt);
                mb.Int(4, m.State);
                mb.Int(5, m.LastIndex);
                info.Sub(5, mb);
            }
            return info;
        }

        /* ===================== 收发底层 ===================== */

        /// <summary>把 Messaging 加密成 Container 并交给节点发送。</summary>
        private bool SendMessaging(string peerB58, byte[] messagingBytes, byte[] messageId, bool needConfirm)
        {
            var enc = crypto.Seal(peerB58, messagingBytes);
            if (enc == null)
            {
                Log(L10n.T("会话尚未就绪（等 KK msg2），消息已排队 → ") + Short(peerB58));
                return false;
            }
            SendEncrypted(peerB58, enc, messageId, needConfirm, messagingBytes);
            return true;
        }

        /// <summary>把 Encrypted 包成 Container（签名信封）；<paramref name="sig"/> 输出信封签名（对端确认按它匹配）。</summary>
        private byte[] BuildContainer(string peerB58, EncryptedMsg enc, out byte[] sig)
        {
            // EnvelopPayload { encrypted = 1 }
            var ep = new PbWriter();
            ep.Sub(1, RawOf(enc.Encode()));

            // Envelope { sender_id=1, receiver_id=2, payload=3 }
            var env = new PbWriter();
            env.Bytes(1, MyIdBytes);
            env.Bytes(2, Base58.Decode(peerB58));
            env.Bytes(3, ep.ToArray());
            var envBytes = env.ToArray();

            // Container { signature=1, envelope=2 }
            sig = Curve25519.Ed25519Sign(identity.QaulSeed, envBytes);
            var container = new PbWriter();
            container.Bytes(1, sig);
            container.Bytes(2, envBytes);
            return container.ToArray();
        }

        private void SendEncrypted(string peerB58, EncryptedMsg enc, byte[] messageId, bool needConfirm, byte[] messagingBytes = null)
        {
            byte[] sig;
            var containerBytes = BuildContainer(peerB58, enc, out sig);

            if (needConfirm)
            {
                lock (gate)
                {
                    unconfirmed[LocalIdentity.Hex(sig)] = new Pending
                    {
                        Peer = peerB58,
                        Container = containerBytes,
                        Sig = sig,
                        Messaging = messagingBytes,
                        SessionId = enc.SessionId,
                        FirstSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        LastSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Tries = 0
                    };
                }
            }

            // 先按 qaul 的路由表走（直连 / 已知路由 / q8id 路由）。
            // 都不通时不要干等：把容器交给认识的邻居接力——这才是「用 qaul 中继传输」，
            // 两台不直接相连的设备靠的就是这种中间节点接力。
            if (node.SendQaulMessaging(peerB58, containerBytes)) return;
            node.ForwardQaulMessaging(peerB58, containerBytes, null);
        }

        /// <summary>PbWriter 没有「原样追加子消息」以外的方式，这里包一层。</summary>
        private static PbWriter RawOf(byte[] bytes)
        {
            var w = new PbWriter();
            w.Raw(bytes);
            return w;
        }

        /// <summary>收到 qaul_messaging 的原始容器（可能不是给我们的，需要转发）。</summary>
        public void HandleContainer(string fromNeighbourB58, byte[] data)
        {
            if (data == null || data.Length < 10) return;

            var r = new PbReader(data);
            byte[] sig = null, envBytes = null;
            int f, w;
            while (r.Next(out f, out w))
            {
                if (f == 1) sig = r.Bytes();
                else if (f == 2) envBytes = r.Bytes();
                else r.Skip(w);
            }
            if (envBytes == null) return;

            // Envelope
            var er = new PbReader(envBytes);
            byte[] sender = null, receiver = null, payload = null;
            while (er.Next(out f, out w))
            {
                if (f == 1) sender = er.Bytes();
                else if (f == 2) receiver = er.Bytes();
                else if (f == 3) payload = er.Bytes();
                else er.Skip(w);
            }
            if (sender == null || receiver == null || payload == null) return;

            // 不是给我们的：按路由转发（qaul 的 messaging 也是这个行为）。
            // 把自己认识的 **所有** 邻居都当作候选下一跳：本节点要能当中转节点，
            // 不能因为「这条路由还没学到」就把包丢了。
            if (!Curve25519.BytesEqual(receiver, MyIdBytes))
            {
                var dest = Base58.Encode(receiver);
                if (dest != MyIdB58) node.ForwardQaulMessaging(dest, data, fromNeighbourB58);
                return;
            }

            var senderB58 = Base58.Encode(sender);

            // 验签：用 sender_id 里内嵌的公钥（38 字节 PeerId = 0x0024 + 公钥 protobuf）
            if (sig != null && sender.Length == 38)
            {
                var pub = Curve25519.Slice(sender, 6, 32);
                if (!Curve25519.Ed25519Verify(pub, envBytes, sig))
                {
                    Log(L10n.T("容器验签失败，丢弃（可能冒充）"));
                    return;
                }

                // 验过了 → 这个人确实拥有这个 id 对应的私钥。**公钥到手，不用再等
                // UserInfo 互换**：这是 qaul 里最靠得住的一条「自动公钥学习」路径，
                // 官方客户端之所以「自动就能聊」，靠的就是容器自带的这套自证。
                node.SeedKeyFromPeerId(senderB58, false);
                node.NoteUserFrom(fromNeighbourB58, senderB58);
            }

            // EnvelopPayload { encrypted = 1 }
            var pr = new PbReader(payload);
            byte[] encBytes = null;
            while (pr.Next(out f, out w))
            {
                if (f == 1) encBytes = pr.Bytes();
                else pr.Skip(w);
            }
            if (encBytes == null) return;

            var enc = EncryptedMsg.Decode(encBytes);
            if (enc == null) return;

            var wasHandshake = enc.State == 1;
            var plain = crypto.Open(senderB58, enc);

            if (plain == null) { Log("解密失败（会话不匹配或未登记公钥）"); return; }
            if (plain.Length == 0) return;          // KK msg2 之类

            // 同一个容器可能经「直连 + 中继」两条路各到一次（签名完全相同）。
            // 这里按容器签名做 60 秒窗口去重：只在**成功解开之后**才登记，
            // 这样「第一次因缺公钥没解开的」不会被误杀——它的重投仍有机会成功。
            if (sig != null && !MarkSeen(sig)) return;

            // 只有「业务消息」才回确认；**确认本身绝不能再确认**，
            // 否则两端会互相确认到天荒地老（真机上就是这么刷屏的）。
            bool confirm = Dispatch(senderB58, plain);
            if (confirm && sig != null) SendConfirmation(senderB58, sig);
        }

        /// <summary>
        /// 登记一个「已处理」的容器签名。返回 false 表示 60 秒内见过（应丢弃）。
        /// qaul 只按 message_id 去重（chat/storage.rs:336-343）；我们多防一层容器级重复，
        /// 因为同一份容器会经直连与中继两条路径各到一次。
        /// </summary>
        private bool MarkSeen(byte[] sig)
        {
            if (sig == null || sig.Length == 0) return true;
            var hex = LocalIdentity.Hex(sig);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (gate)
            {
                long last;
                if (seenSigs.TryGetValue(hex, out last) && now - last < 60000) return false;
                seenSigs[hex] = now;
                if (seenSigs.Count > 500)
                {
                    var dead = new List<string>();
                    foreach (var kv in seenSigs) if (now - kv.Value >= 60000) dead.Add(kv.Key);
                    foreach (var k in dead) seenSigs.Remove(k);
                }
            }
            return true;
        }

        private void SendConfirmation(string peerB58, byte[] signature)
        {
            // Confirmation { signature=1, received_at=2 } → Messaging{confirmation=1}
            var conf = new PbWriter();
            conf.Bytes(1, signature);
            conf.Int(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var msg = new PbWriter();
            msg.Sub(1, conf);
            SendMessaging(peerB58, msg.ToArray(), null, false);
        }

        /// <summary>处理解密后的 Messaging，返回是否需要回确认。</summary>
        private bool Dispatch(string senderB58, byte[] plain)
        {
            bool needConfirm = false;
            var r = new PbReader(plain);
            int f, w;
            while (r.Next(out f, out w))
            {
                if (f == 1)
                {
                    // Confirmation：对端确认收到了我们的某条消息
                    var c = r.Sub();
                    byte[] sig = null;
                    int f2, w2;
                    while (c.Next(out f2, out w2))
                    {
                        if (f2 == 1) sig = c.Bytes();
                        else c.Skip(w2);
                    }
                    if (sig != null)
                    {
                        lock (gate) { unconfirmed.Remove(LocalIdentity.Hex(sig)); }
                    }
                    needConfirm = false;      // 确认消息不再确认
                }
                else if (f == 5)
                {
                    // GroupInviteMessage { content = GroupContainer }
                    var g = r.Sub();
                    byte[] content = null;
                    int f2, w2;
                    while (g.Next(out f2, out w2))
                    {
                        if (f2 == 1) content = g.Bytes();
                        else g.Skip(w2);
                    }
                    if (content != null) HandleGroupContainer(senderB58, content);
                    needConfirm = true;
                }
                else if (f == 6)
                {
                    HandleCommonMessage(senderB58, r.Sub());
                    needConfirm = true;
                }
                else if (f == 3)
                {
                    // crypto_service：内容只影响会话状态，密钥已在 Open 里处理
                    Log(L10n.T("收到 crypto service 帧（会话已建立）"));
                    needConfirm = true;
                }
                else r.Skip(w);
            }
            return needConfirm;
        }

        private void HandleCommonMessage(string senderB58, PbReader cm)
        {
            byte[] messageId = null, groupId = null;
            long sentAt = 0;
            string chat = null, groupText = null;
            byte[] fileBytes = null;
            int f, w;

            while (cm.Next(out f, out w))
            {
                if (f == 1) messageId = cm.Bytes();
                else if (f == 2) groupId = cm.Bytes();
                else if (f == 3) sentAt = (long)cm.Varint();
                else if (f == 4)
                {
                    var c = cm.Sub();
                    int f2, w2;
                    while (c.Next(out f2, out w2))
                    {
                        if (f2 == 1) chat = c.Str();
                        else c.Skip(w2);
                    }
                }
                else if (f == 5)
                {
                    // FileMessage { content = ChatFileContainer }：qaul 的图片/视频附件
                    var c = cm.Sub();
                    int f2, w2;
                    while (c.Next(out f2, out w2))
                    {
                        if (f2 == 1) fileBytes = c.Bytes();
                        else c.Skip(w2);
                    }
                }
                else if (f == 7)
                {
                    // FileRetransmitRequest { file_id, missing_indices[] }：请求重传缺失分片
                    var c = cm.Sub();
                    int f2, w2;
                    ulong fileId = 0;
                    List<uint> missingIndices = new List<uint>();
                    while (c.Next(out f2, out w2))
                    {
                        if (f2 == 1) fileId = c.Varint();
                        else if (f2 == 2)
                        {
                            var arr = c.Sub();
                            int f3, w3;
                            while (arr.Next(out f3, out w3))
                            {
                                if (f3 == 1) missingIndices.Add((uint)arr.Varint());
                            }
                        }
                        else c.Skip(w2);
                    }
                    if (fileId > 0 && missingIndices.Count > 0)
                    {
                        // 重传请求的分片
                        RetransmitFilePieces(fileId, missingIndices.ToArray());
                    }
                    return;
                }
                else if (f == 6)
                {
                    var c = cm.Sub();
                    int f2, w2;
                    while (c.Next(out f2, out w2))
                    {
                        if (f2 == 1)
                        {
                            // 注意：Bytes() 会推进游标，只能读一次。
                            var tb = c.Bytes();
                            groupText = tb == null ? null : Encoding.UTF8.GetString(tb);
                        }
                        else c.Skip(w2);
                    }
                }
                else cm.Skip(w);
            }

            if (sentAt == 0) sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (groupId == null) groupId = DirectGroupId(MyIdBytes, Base58.Decode(senderB58));

            if (fileBytes != null)
            {
                HandleChatFileContainer(senderB58, fileBytes, sentAt, LocalIdentity.Hex(groupId));
                return;
            }

            if (chat != null)
            {
                // 只有 group_id 恰好等于「双方 DirectGroupId」时才是真私聊，才走 OnChat。
                // 频道/房间中继（SendToPeerInGroup）也被打成 ChatMessage(isGroup=false)，
                // 但它的 group_id 是频道/房间的——必须按 group_id 走群路径，让上层解析回
                // 正确会话（chan:mesh 等）。否则会落进 qaul 私聊、拿私聊密钥去解频道密文，
                // 永远 [无法解密]，并把私聊会话刷满locked（错误1 的 870ae203 残因）。
                bool isRealDm = groupId != null && groupId.Length == 16 &&
                    BytesEq(groupId, DirectGroupId(MyIdBytes, Base58.Decode(senderB58)));
                if (isRealDm)
                {
                    var cb = OnChat;
                    if (cb != null) { try { cb(senderB58, chat, messageId, sentAt); } catch { } }
                }
                else if (groupId != null)
                {
                    var gid = LocalIdentity.Hex(groupId);
                    var cb = OnGroupChat;
                    if (cb != null) { try { cb(gid, chat, messageId, sentAt, senderB58); } catch { } }
                }
            }
            else if (groupText != null && groupId != null)
            {
                var gid = LocalIdentity.Hex(groupId);
                var cb = OnGroupChat;
                if (cb != null) { try { cb(gid, groupText, messageId, sentAt, senderB58); } catch { } }
            }
        }

        private void HandleGroupContainer(string senderB58, byte[] content)
        {
            var r = new PbReader(content);
            int f, w;
            while (r.Next(out f, out w))
            {
                if (f == 1)
                {
                    // invite_member { group }
                    var inv = r.Sub();
                    int f2, w2;
                    while (inv.Next(out f2, out w2))
                    {
                        if (f2 == 1) ApplyGroupInfo(inv.Sub(), true, senderB58);
                        else inv.Skip(w2);
                    }
                }
                else if (f == 2)
                {
                    // reply_invite { group_id, accept }
                    var rep = r.Sub();
                    byte[] gid = null;
                    bool accept = false;
                    int f2, w2;
                    while (rep.Next(out f2, out w2))
                    {
                        if (f2 == 1) gid = rep.Bytes();
                        else if (f2 == 2) accept = rep.Varint() != 0;
                        else rep.Skip(w2);
                    }
                    if (gid != null) OnReplyInvite(LocalIdentity.Hex(gid), senderB58, accept);
                }
                else if (f == 3)
                {
                    // group_info（成员变更广播）
                    ApplyGroupInfo(r.Sub(), false, senderB58);
                }
                else if (f == 4)
                {
                    // removed { group_id }
                    var rm = r.Sub();
                    byte[] gid = null;
                    int f2, w2;
                    while (rm.Next(out f2, out w2))
                    {
                        if (f2 == 1) gid = rm.Bytes();
                        else rm.Skip(w2);
                    }
                    if (gid != null)
                    {
                        var hex = LocalIdentity.Hex(gid);
                        lock (gate) { Groups.Remove(hex); }
                        SaveGroups();
                        Log(L10n.T("已被移出群组 ") + hex);
                    }
                }
                else r.Skip(w);
            }
        }

        private void ApplyGroupInfo(PbReader info, bool invited, string senderB58)
        {
            byte[] gid = null;
            string name = null;
            long createdAt = 0;
            uint revision = 0;
            var members = new List<QaulMember>();
            int f, w;

            while (info.Next(out f, out w))
            {
                if (f == 1) gid = info.Bytes();
                else if (f == 2) name = info.Str();
                else if (f == 3) createdAt = (long)info.Varint();
                else if (f == 4) revision = (uint)info.Varint();
                else if (f == 5)
                {
                    var m = info.Sub();
                    var mem = new QaulMember();
                    int f2, w2;
                    while (m.Next(out f2, out w2))
                    {
                        if (f2 == 1) mem.UserId = Base58.Encode(m.Bytes());
                        else if (f2 == 2) mem.Role = (int)m.Varint();
                        else if (f2 == 3) mem.JoinedAt = (long)m.Varint();
                        else if (f2 == 4) mem.State = (int)m.Varint();
                        else if (f2 == 5) mem.LastIndex = (uint)m.Varint();
                        else m.Skip(w2);
                    }
                    if (!string.IsNullOrEmpty(mem.UserId)) members.Add(mem);
                }
                else info.Skip(w);
            }
            if (gid == null) return;

            var hex = LocalIdentity.Hex(gid);
            QaulGroup g;
            lock (gate)
            {
                if (!Groups.TryGetValue(hex, out g))
                {
                    g = new QaulGroup { Id = hex };
                    Groups[hex] = g;
                }
                if (!string.IsNullOrEmpty(name)) g.Name = name;
                if (createdAt > 0) g.CreatedAt = createdAt;

                // qaul 的 GroupInfo 是「成员表快照」：版本不旧就以它为准整表替换，
                // 否则被移出的人会一直留在本地成员表里。
                if (members.Count > 0 && revision >= g.Revision)
                {
                    g.Revision = revision;
                    g.Members = members;
                }
                else
                {
                    if (revision > 0) g.Revision = revision;
                    foreach (var mem in members)
                    {
                        var idx = g.Members.FindIndex(x => x.UserId == mem.UserId);
                        if (idx >= 0) g.Members[idx] = mem;
                        else g.Members.Add(mem);
                    }
                }
                // 自己被邀请：标记为已加入（简化交互，自动接受）
                foreach (var mem in g.Members)
                {
                    if (mem.UserId == MyIdB58 && mem.State == 0) { mem.State = 1; }
                }
            }
            SaveGroups();

            var cb = OnGroupChanged;
            if (cb != null) { try { cb(g); } catch { } }

            if (invited)
            {
                Log(L10n.T("收到群邀请「") + g.Name + L10n.T("」，自动接受"));
                SendReplyInvite(senderB58, gid, true);
            }
        }

        private void OnReplyInvite(string groupIdHex, string peerB58, bool accept)
        {
            QaulGroup g;
            lock (gate)
            {
                if (!Groups.TryGetValue(groupIdHex, out g)) return;
                bool found = false;
                foreach (var m in g.Members)
                {
                    if (m.UserId == peerB58)
                    {
                        found = true;
                        if (accept) m.State = 1;
                        else g.Members.Remove(m);
                        break;
                    }
                }
                if (!found) return;
                g.Revision++;
            }
            SaveGroups();

            var cb = OnGroupChanged;
            if (cb != null) { try { cb(g); } catch { } }
            Log(Short(peerB58) + (accept ? L10n.T(" 接受") : L10n.T(" 拒绝")) + L10n.T("了群邀请"));

            // 成员表变了 → 广播给所有成员，让第三方也看到新成员
            BroadcastGroupInfo(g);
        }

        private bool SendReplyInvite(string peerB58, byte[] groupId, bool accept)
        {
            var reply = new PbWriter();
            reply.Bytes(1, groupId);
            reply.Bool(2, accept);

            var container = new PbWriter();
            container.Sub(2, reply);           // reply_invite

            var invite = new PbWriter();
            invite.Bytes(1, container.ToArray());
            var msg = new PbWriter();
            msg.Sub(5, invite);
            return SendMessaging(peerB58, msg.ToArray(), null, true);
        }

        private void Resend(string peerB58, byte[] messagingBytes)
        {
            SendMessaging(peerB58, messagingBytes, null, false);
        }

        /// <summary>重发未确认消息（qaul 的 retransmit 定时器）。</summary>
        public void Retransmit()
        {
            List<Pending> list;
            lock (gate) { list = new List<Pending>(unconfirmed.Values); }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var p in list)
            {
                // ① 整体过期：qaul 1 小时后不再重试（retransmit.rs:79-90）。
                // 原实现只按次数放弃，网络长时间不可用时表项会赖到下次重启。
                if (now - p.FirstSentAt > RetransmitExpiryMs)
                {
                    lock (gate) { unconfirmed.Remove(LocalIdentity.Hex(p.Sig)); }
                    Log(L10n.T("超过 1 小时仍未确认，放弃 → ") + Short(p.Peer));
                    continue;
                }

                // ② 静默期按「上次发送」算（qaul 的 last_sent + 3s），而不是首次发送：
                // 按首次算会让「早发出的 + 晚发出的」在同一 tick 里一起重投，形成突发。
                if (now - p.LastSentAt < RetransmitQuietMs(p.Tries)) continue;

                // ③ 次数上限
                if (p.Tries >= MaxRetransmitTries)
                {
                    // 老实现这里是 `lock (gate) { }` 空操作：放弃的消息永远留在表里，
                    // UnconfirmedCount 永不归零、每个 tick 还空转一遍。踢掉并记一笔。
                    lock (gate) { unconfirmed.Remove(LocalIdentity.Hex(p.Sig)); }
                    Log(L10n.T("重投 ") + MaxRetransmitTries + L10n.T(" 次仍未确认，放弃 → ") + Short(p.Peer));
                    continue;
                }

                // ④ 只在对端「在线」时才重投——qaul 只在路由表里有该用户时重发
                //    （retransmit.rs:92-94）。否则对离线对端的每次重试都会退化成
                //    向**所有**邻居广播兜底（ForwardQaulMessaging），白耗流量与 CPU，
                //    还会把同一份容器反复塞进中继链路。
                //    注意：这里只是跳过本轮，**不放弃**——路由/连接恢复后继续投。
                if (node != null && !node.HasRoute(p.Peer) && !node.IsConnected(p.Peer))
                    continue;

                p.Tries++;
                p.LastSentAt = now;
                RetransmitOne(p);
            }
        }

        /// <summary>
        /// 重投一条未确认消息。
        ///
        /// 会话若已随「对端重启/重连后的 ResetSession」作废，旧容器是用废密钥加密的，
        /// 原样重发对端永远解不开（真机复现：一端重启后，另一头的首条私聊必丢）。
        /// 这时改用当前会话重新封包：会话已重建就直接加密重投；还在半握手就排队，
        /// 等 OnSessionReady 自动补发。
        /// </summary>
        private void RetransmitOne(Pending p)
        {
            var s = crypto.SessionOf(p.Peer);
            bool sessionChanged = s == null || s.Phase != 1 ||
                                  (p.SessionId != 0 && s.SessionId != p.SessionId);

            if (sessionChanged && p.Messaging != null && p.Messaging.Length > 0)
            {
                var enc = crypto.Seal(p.Peer, p.Messaging);
                if (enc == null) return;   // 半握手中：排队，等 msg2 后 OnSessionReady 补发
                byte[] sig;
                var fresh = BuildContainer(p.Peer, enc, out sig);
                if (!node.SendQaulMessaging(p.Peer, fresh))
                    node.ForwardQaulMessaging(p.Peer, fresh, null);
                // 未确认表的 key 换成新签名，否则对端按新 sig 回确认时匹配不上
                lock (gate)
                {
                    unconfirmed.Remove(LocalIdentity.Hex(p.Sig));
                    p.Container = fresh;
                    p.Sig = sig;
                    p.SessionId = enc.SessionId;
                    p.LastSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    unconfirmed[LocalIdentity.Hex(sig)] = p;
                }
                Log(L10n.T("会话已重建，用新 KK 会话重新封包重投 → ") + Short(p.Peer));
                return;
            }

            // 先按路由表走；仍然不通（还没学到路由 / 对端不相邻）就交给中继兜底：
            // 把容器交给认识的邻居接力，这正是 qaul 的 store-and-forward 语义，
            // 两台不直接相连的设备就是靠这种中间节点接力才通得了信。
            if (!node.SendQaulMessaging(p.Peer, p.Container))
                node.ForwardQaulMessaging(p.Peer, p.Container, null);
        }

        public int UnconfirmedCount { get { lock (gate) { return unconfirmed.Count; } } }

        /* ===================== 工具 ===================== */

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            return s.Length <= 10 ? s : s.Substring(0, 10) + "…";
        }

        /// <summary>两个字节数组按内容比较（长度 + 逐字节），避免引用相等误判。</summary>
        private static bool BytesEq(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>私聊 group_id：两个 q8id 排序后拼接（qaul 的 GroupId::from_peers）。</summary>
        public static byte[] DirectGroupId(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length < 14 || b.Length < 14) return new byte[16];
            var qa = new byte[8]; var qb = new byte[8];
            Buffer.BlockCopy(a, 6, qa, 0, 8);
            Buffer.BlockCopy(b, 6, qb, 0, 8);

            int cmp = 0;
            for (int i = 0; i < 8 && cmp == 0; i++) cmp = qa[i].CompareTo(qb[i]);

            var r = new byte[16];
            if (cmp <= 0) { Buffer.BlockCopy(qa, 0, r, 0, 8); Buffer.BlockCopy(qb, 0, r, 8, 8); }
            else { Buffer.BlockCopy(qb, 0, r, 0, 8); Buffer.BlockCopy(qa, 0, r, 8, 8); }
            return r;
        }

        /// <summary>
        /// qaul 的 message id：CRC64-GO-ISO(group_id) ‖ CRC64-GO-ISO(sender) ‖ index，都是大端。
        /// </summary>
        public static byte[] MessageId(byte[] groupId, byte[] sender, uint index)
        {
            var r = new byte[20];
            var g = Crc64.GoIso(groupId);
            var s = Crc64.GoIso(sender);
            for (int i = 0; i < 8; i++)
            {
                r[i] = (byte)(g >> (8 * (7 - i)));
                r[8 + i] = (byte)(s >> (8 * (7 - i)));
            }
            r[16] = (byte)(index >> 24);
            r[17] = (byte)(index >> 16);
            r[18] = (byte)(index >> 8);
            r[19] = (byte)index;
            return r;
        }

        private PbReader ParseMessaging(byte[] bytes) { return bytes == null ? null : new PbReader(bytes); }

        /* ===================== 持久化 ===================== */

        private void LoadGroups()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                var p = Path.Combine(storeDir, "qaul_groups.json");
                if (!File.Exists(p)) return;
                var list = JsonConvert.DeserializeObject<List<QaulGroup>>(File.ReadAllText(p, Encoding.UTF8));
                if (list == null) return;
                lock (gate)
                {
                    foreach (var g in list) Groups[g.Id] = g;
                }
            }
            catch { }
        }

        private void LoadUsers()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                var p = Path.Combine(storeDir, "qaul_users.json");
                if (!File.Exists(p)) return;
                var list = JsonConvert.DeserializeObject<List<QaulUser>>(File.ReadAllText(p, Encoding.UTF8));
                if (list == null) return;
                lock (gate)
                {
                    foreach (var u in list)
                        if (!string.IsNullOrEmpty(u.Id) && u.Id != MyIdB58) Users[u.Id] = u;
                }
            }
            catch { }
        }

        /// <summary>载入持久化的 message id 取号水位（见 NextIndex）。</summary>
        private void LoadIndexes()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                var p = Path.Combine(storeDir, "qaul_index.json");
                if (!File.Exists(p)) return;
                var dict = JsonConvert.DeserializeObject<Dictionary<string, uint>>(File.ReadAllText(p, Encoding.UTF8));
                if (dict == null) return;
                lock (gate)
                {
                    foreach (var kv in dict)
                        if (!string.IsNullOrEmpty(kv.Key)) indexHigh[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        private void SaveIndexes(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir) || json == null) return;
                Directory.CreateDirectory(storeDir);
                File.WriteAllText(Path.Combine(storeDir, "qaul_index.json"), json, Encoding.UTF8);
            }
            catch { }
        }

        private void SaveUsers()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                Directory.CreateDirectory(storeDir);
                List<QaulUser> list;
                lock (gate)
                {
                    // 绝不持久化「节点身份」：它是 libp2p 传输身份（PeerId），不是聊天账号。
                    // 早期版本误把它当成联系人存盘，导致同一台官方客户端被登记两次
                    // （一次是真实账号、一次是它的节点身份），后者收发都石沉大海。
                    // 这里直接剔除，磁盘上的旧脏数据下次保存时自然被清掉。
                    list = new List<QaulUser>();
                    foreach (var u in Users.Values)
                        if (!u.Node && !IsNodeIdentity(u.Id)) list.Add(u);
                }
                File.WriteAllText(Path.Combine(storeDir, "qaul_users.json"),
                    JsonConvert.SerializeObject(list), Encoding.UTF8);
            }
            catch { }
        }

        private void SaveGroups()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                Directory.CreateDirectory(storeDir);
                List<QaulGroup> list;
                lock (gate) { list = new List<QaulGroup>(Groups.Values); }
                File.WriteAllText(Path.Combine(storeDir, "qaul_groups.json"),
                    JsonConvert.SerializeObject(list), Encoding.UTF8);
            }
            catch { }
        }

        public List<QaulGroup> GroupList()
        {
            lock (gate) { return new List<QaulGroup>(Groups.Values); }
        }

        public List<QaulUser> UserList()
        {
            lock (gate) { return new List<QaulUser>(Users.Values); }
        }
    }

    /// <summary>CRC-64/GO-ISO（poly=0x1B, init/xorout=全 1, 反射进/出）。</summary>
    public static class Crc64
    {
        private const ulong Poly = 0x000000000000001BUL;
        private const ulong Init = 0xFFFFFFFFFFFFFFFFUL;
        private const ulong XorOut = 0xFFFFFFFFFFFFFFFFUL;

        public static ulong GoIso(byte[] data)
        {
            ulong crc = Init;
            if (data != null)
            {
                foreach (var b in data)
                {
                    crc ^= b;
                    for (int i = 0; i < 8; i++)
                    {
                        if ((crc & 1) != 0) crc = (crc >> 1) ^ Reflect(Poly);
                        else crc >>= 1;
                    }
                }
            }
            return crc ^ XorOut;
        }

        private static ulong Reflect(ulong v)
        {
            ulong r = 0;
            for (int i = 0; i < 64; i++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            return r;
        }
    }
}
