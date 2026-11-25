using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.Util;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>
    /// qaul 的 `Encrypted` 报文（messaging.proto）：
    /// Encrypted { state = 1, session_id = 2, data = 3 [ Data { nonce = 1, data = 2 } ] }
    /// </summary>
    public sealed class EncryptedMsg
    {
        public int State;          // 0 NONE / 1 HANDSHAKE / 2 TRANSPORT
        public uint SessionId;
        public ulong Nonce;
        public byte[] Data;

        public byte[] Encode()
        {
            var d = new PbWriter();
            d.UInt(1, Nonce);
            d.Bytes(2, Data);

            var w = new PbWriter();
            w.Int(1, State);
            w.Int(2, SessionId);
            w.Sub(3, d);
            return w.ToArray();
        }

        public static EncryptedMsg Decode(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            var r = new PbReader(raw);
            var e = new EncryptedMsg();
            int f, w;
            while (r.Next(out f, out w))
            {
                if (f == 1) e.State = (int)r.Varint();
                else if (f == 2) e.SessionId = (uint)r.Varint();
                else if (f == 3)
                {
                    var d = r.Sub();
                    int f2, w2;
                    while (d.Next(out f2, out w2))
                    {
                        if (f2 == 1) e.Nonce = d.Varint();
                        else if (f2 == 2) e.Data = d.Bytes();
                        else d.Skip(w2);
                    }
                }
                else r.Skip(w);
            }
            return e.Data == null ? null : e;
        }
    }

    /// <summary>一条用户对用户的加密会话（KK）。</summary>
    public sealed class QaulSession
    {
        public uint SessionId;
        public bool Initiator;
        /// <summary>0 = 只发了 msg1（等对端 msg2），1 = 传输态。</summary>
        public int Phase;
        public string SendKey;
        public string RecvKey;
        public ulong SendNonce;
        public long EstablishedAt;

        public byte[] SendKeyBytes() { return LocalIdentity.Unhex(SendKey); }
        public byte[] RecvKeyBytes() { return LocalIdentity.Unhex(RecvKey); }
    }

    /// <summary>
    /// qaul 端到端加密服务（对应 libqaul 的 `services/crypto`）。
    ///
    /// 每个对端一条会话；首次发消息时把业务数据塞进 KK msg1，对端回 msg2 后才进入传输态。
    /// 会话（密钥 + 发送计数器）持久化，重启后继续用同一会话，对端不会当成新设备。
    /// </summary>
    public sealed class QaulCrypto
    {
        private readonly LocalIdentity identity;
        private readonly Dictionary<string, QaulSession> sessions = new Dictionary<string, QaulSession>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private readonly string storePath;

        /// <summary>KK 半握手（只发了 msg1、等不到 msg2）超过这个时长就作废并重发 msg1。
        /// 否则一旦 msg2 丢了（连接抖动/容器没送达），会话会永久卡在 Phase 0，
        /// 之后所有私聊都排队发不出去——这就是「稳定互连」失败的根因。15 秒足够正常握手完成。</summary>
        private const long StaleHandshakeMs = 15000;

        /// <summary>半握手期间排队等待 msg2 的明文（qaul 的 enqueue_pending_plaintext）。</summary>
        private readonly Dictionary<string, List<byte[]>> pending = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        /// <summary>查对端用户公钥（ed25519，32 字节）；返回 null 表示还不认识这个人。</summary>
        public Func<string, byte[]> GetRemotePublicKey;

        /// <summary>对端要求「回一条 crypto 消息」（也就是 KK msg2）。</summary>
        public Action<string, EncryptedMsg> SendCryptoFrame;

        /// <summary>会话刚进入传输态（把排队消息发出去）。</summary>
        public Action<string> OnSessionReady;

        private static void Log(string s)
        {
            try { Android.Util.Log.Info("QaulCrypto", s); } catch { }
        }

        private static string Short(string b58)
        {
            if (string.IsNullOrEmpty(b58)) return "";
            return b58.Length <= 10 ? b58 : b58.Substring(0, 10) + "…";
        }

        /// <summary>
        /// 重连到一个邻居时调用：丢弃与该对端的所有旧会话（含已建立的），
        /// 强制下一次 Seal/Open 重新协商 KK，避免复用随旧连接失效的密钥/计数。
        /// 排队中的明文保留，进入传输态后会由 OnSessionReady 补发。
        /// </summary>
        public void ResetSession(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return;
            lock (gate) { sessions.Remove(peer); }
            lock (_pendingKk) { _pendingKk.Remove(peer); }
        }


        public QaulCrypto(LocalIdentity identity, string storeDir)
        {
            this.identity = identity;
            if (!string.IsNullOrEmpty(storeDir))
            {
                try
                {
                    Directory.CreateDirectory(storeDir);
                    storePath = Path.Combine(storeDir, "qaul_sessions.json");
                    Load();
                }
                catch { storePath = null; }
            }
        }

        private byte[] MyXPriv()
        {
            return Curve25519.Ed25519ToX25519Private(identity.QaulSeed);
        }

        private byte[] MyXPub()
        {
            return Curve25519.Ed25519ToX25519Public(identity.QaulPublic);
        }

        private QaulSession Get(string peer)
        {
            lock (gate)
            {
                QaulSession s;
                return sessions.TryGetValue(peer, out s) ? s : null;
            }
        }

        private void Put(string peer, QaulSession s)
        {
            lock (gate) { sessions[peer] = s; }
            Save();
        }

        public int SessionCount { get { lock (gate) { return sessions.Count; } } }

        /// <summary>某个对端是否已经有可用的传输态会话（诊断用）。</summary>
        public bool HasTransport(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return false;
            var s = Get(peer);
            return s != null && s.Phase == 1;
        }

        /// <summary>取对端当前会话快照（无则 null）。重试重投前用它判断会话是否已变。</summary>
        public QaulSession SessionOf(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return null;
            return Get(peer);
        }

        public int TransportCount
        {
            get
            {
                lock (gate)
                {
                    int n = 0;
                    foreach (var s in sessions.Values) if (s.Phase == 1) n++;
                    return n;
                }
            }
        }

        /// <summary>
        /// 加密一条业务数据。返回 null 表示会话还在半握手（消息已排队，等 msg2 后自动补发）。
        /// </summary>
        public EncryptedMsg Seal(string peer, byte[] plaintext)
        {
            var s = Get(peer);
            if (s != null && s.Phase == 1)
            {
                var nonce = s.SendNonce;
                var key = s.SendKeyBytes();
                var ct = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, new byte[0]);
                s.SendNonce = nonce + 1;
                Put(peer, s);
                return new EncryptedMsg { State = 2, SessionId = s.SessionId, Nonce = nonce, Data = ct };
            }

            if (s != null && s.Phase == 0)
            {
                // 半握手卡住（msg2 没回来，多半是连接抖动/容器没送达）超过阈值就作废，
                // 落到下面的「全新会话」分支重发 msg1，自愈握手；否则会永久卡在"等 KK msg2"。
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.EstablishedAt > StaleHandshakeMs)
                {
                    lock (gate) { sessions.Remove(peer); }
                    lock (_pendingKk) { _pendingKk.Remove(peer); }
                    Log(L10n.T("KK 半握手超时，作废并重发 msg1 → ") + Short(peer));
                    s = null;
                }
                else
                {
                    // 等对端 msg2，先排队（进入传输态后会由 OnSessionReady 补发）
                    lock (gate)
                    {
                        List<byte[]> list;
                        if (!pending.TryGetValue(peer, out list)) { list = new List<byte[]>(); pending[peer] = list; }
                        list.Add(plaintext);
                    }
                    return null;
                }
            }

            // 全新会话：把数据放进 KK msg1
            var remoteEd = GetRemotePublicKey == null ? null : GetRemotePublicKey(peer);
            if (remoteEd == null || remoteEd.Length != 32) return null;
            var remoteX = Curve25519.Ed25519ToX25519Public(remoteEd);
            if (remoteX == null) return null;

            byte[] msg1;
            var kk = QaulKk.BeginInitiator(MyXPriv(), MyXPub(), remoteX, plaintext, out msg1);

            var session = new QaulSession
            {
                SessionId = NewSessionId(),
                Initiator = true,
                Phase = 0,
                SendNonce = 0,
                EstablishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            lock (_pendingKk) { _pendingKk[peer] = kk; }
            Put(peer, session);

            return new EncryptedMsg { State = 1, SessionId = session.SessionId, Nonce = 0, Data = msg1 };
        }

        private readonly Dictionary<string, QaulKk> _pendingKk = new Dictionary<string, QaulKk>(StringComparer.Ordinal);

        /// <summary>
        /// 主动发起一次 KK 握手（空业务载荷的 msg1）。
        ///
        /// 用于「会话失步自愈」：本端发现对端的会话 id 变了、或 AEAD 校验不过时，
        /// 光丢弃自己的旧会话不够——对端还以为会话有效，继续用旧密钥发。
        /// 主动发一条 msg1，对端收到会以 responder 建新会话并回 msg2，
        /// 本端收 msg2 后入传输态，OnSessionReady 把排队的消息补发出去。
        /// </summary>
        private void Renegotiate(string peer)
        {
            try
            {
                var remoteEd = GetRemotePublicKey == null ? null : GetRemotePublicKey(peer);
                if (remoteEd == null || remoteEd.Length != 32) return;
                var remoteX = Curve25519.Ed25519ToX25519Public(remoteEd);
                if (remoteX == null) return;

                byte[] msg1;
                var kk = QaulKk.BeginInitiator(MyXPriv(), MyXPub(), remoteX, Array.Empty<byte>(), out msg1);

                var session = new QaulSession
                {
                    SessionId = NewSessionId(),
                    Initiator = true,
                    Phase = 0,
                    SendNonce = 0,
                    EstablishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                lock (_pendingKk) { _pendingKk[peer] = kk; }
                Put(peer, session);

                var send = SendCryptoFrame;
                if (send != null)
                {
                    send(peer, new EncryptedMsg { State = 1, SessionId = session.SessionId, Nonce = 0, Data = msg1 });
                }
            }
            catch { }
        }

        /// <summary>取走排队的明文（进入传输态后由上层补发）。</summary>
        public List<byte[]> TakePending(string peer)
        {
            lock (gate)
            {
                List<byte[]> list;
                if (!pending.TryGetValue(peer, out list)) return new List<byte[]>();
                pending.Remove(peer);
                return list;
            }
        }

        /// <summary>某个对端有多少条明文卡在「等 KK msg2」队列里（诊断 / UI 排队提示用）。</summary>
        public int PendingCount(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return 0;
            lock (gate)
            {
                List<byte[]> list;
                return pending.TryGetValue(peer, out list) ? list.Count : 0;
            }
        }

        /// <summary>解密一条 Encrypted；返回 null 表示解不开/不是给我们的。</summary>
        public byte[] Open(string peer, EncryptedMsg msg)
        {
            if (msg == null) return null;

            if (msg.State == 2)
            {
                var s = Get(peer);
                if (s == null || s.Phase != 1)
                {
                    // 对端按传输态发消息，本端却没有可用会话——最常见是本端重启/重连后
                    // ResetSession 丢了会话，对端还蒙在鼓里继续用旧会话发。此时必须主动
                    // 重协商（发一条空载荷 msg1），否则双方一直用错密钥，私聊永久解不开。
                    //
                    // 防抖：已有未超时的半握手会话（Phase 0）就安静等 msg2，不要每条旧消息
                    // 都重协一遍——对端未确认消息会重投，反复重协会让双方会话反复重建（振荡）。
                    if (s != null && s.Phase == 0 &&
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.EstablishedAt <= StaleHandshakeMs)
                        return null;
                    Log(L10n.T("收到传输态消息但本端无可用 KK 会话，主动重协商 → ") + Short(peer));
                    ResetSession(peer);
                    Renegotiate(peer);
                    return null;
                }
                if (msg.SessionId != 0 && msg.SessionId != s.SessionId)
                {
                    // 对端换了会话 id（本端也重建过会话）：这只是条迟到的旧消息，丢弃即可。
                    // 不在这里重协——本端下次发消息时 Seal 会走 msg1 自然重新握手。
                    Log(L10n.T("丢弃会话 id 不匹配的旧 KK 消息 → ") + Short(peer));
                    return null;
                }
                try
                {
                    return ChaCha20Poly1305.Decrypt(s.RecvKeyBytes(), msg.Nonce, msg.Data, new byte[0]);
                }
                catch
                {
                    // AEAD 校验不通过：密钥或 nonce 计数失步的旧消息，同样丢弃，
                    // 由本端发送侧的 msg1 完成自愈。
                    Log(L10n.T("KK AEAD 校验不通过，丢弃旧消息 → ") + Short(peer));
                    return null;
                }
            }

            // HANDSHAKE
            var existing = Get(peer);
            var remoteEd = GetRemotePublicKey == null ? null : GetRemotePublicKey(peer);
            if (remoteEd == null || remoteEd.Length != 32) return null;
            var remoteX = Curve25519.Ed25519ToX25519Public(remoteEd);
            if (remoteX == null) return null;

            // 只有 session_id 对得上，才真是「我们发起的会话的 msg2」。
            // 两端同时重协时，对方另起会话的 msg1 也会落到这里；若不比对 id 就
            // CompleteAsInitiator，会把对方的 msg1 当 msg2 解，必然失败。
            if (existing != null && existing.Initiator && existing.Phase == 0 &&
                (msg.SessionId == 0 || msg.SessionId == existing.SessionId))
            {
                // 我们发起的会话，收到 msg2
                QaulKk kk;
                lock (_pendingKk)
                {
                    if (!_pendingKk.TryGetValue(peer, out kk)) kk = null;
                    _pendingKk.Remove(peer);
                }
                if (kk == null) return null;

                kk.CompleteAsInitiator(msg.Data);
                existing.Phase = 1;
                existing.SendKey = LocalIdentity.Hex(kk.SendKey);
                existing.RecvKey = LocalIdentity.Hex(kk.RecvKey);
                Put(peer, existing);

                var cb = OnSessionReady;
                if (cb != null) { try { cb(peer); } catch { } }
                return new byte[0];   // msg2 的载荷是 crypto 服务的确认，没有业务数据
            }

            // 对端发起的新会话：读 msg1 + 回 msg2。
            //
            // 注意：**无论本端当前是什么状态都要能走到这里并被正确覆盖**——
            //   · 本端 initiator 未完成（Phase 0）：对端也重协了，接受对方；
            //   · 本端已是传输态（Phase 1）：对端单边重建了会话（换身份 /
            //     ResetSession 对端无感 / 重启丢会话），必须跟着切。
            // 老实现漏了清理本端未完成的发起态：_pendingKk 里留着旧 kk，
            // 随后对端针对我们旧 msg1 的 msg2 回来，会被上面的 395 分支用
            // **旧 kk** 去 complete，密钥错位 → AEAD 失败 → 双方会话反复重建、
            // 消息持续解不开（真机现场：换身份 / 单边重启后双方刷「KK 解密失败」）。
            lock (_pendingKk) { _pendingKk.Remove(peer); }

            // 同时发起（双方都无会话、各自发 msg1）的 race：如果双方都无条件切
            // responder，最后两边都是 responder、没有 initiator，握手永远完不成。
            // tie-break：session_id 大的一方坚持发起方（对端执行同一规则，必然
            // 只有一方让位）；id 相等时按 PeerId 字节序，小的一方让位。
            var existingNow = Get(peer);
            if (existingNow != null && existingNow.Initiator)
            {
                bool peerWins;
                if (existingNow.SessionId != msg.SessionId)
                    peerWins = msg.SessionId > existingNow.SessionId;
                else
                {
                    var myBytes = identity.QaulPeerIdBytes ?? new byte[0];
                    var peerBytes = Base58.Decode(peer) ?? new byte[0];
                    int n = Math.Min(myBytes.Length, peerBytes.Length);
                    int cmp = 0;
                    for (int i = 0; i < n; i++) { cmp = myBytes[i].CompareTo(peerBytes[i]); if (cmp != 0) break; }
                    if (cmp == 0) cmp = myBytes.Length.CompareTo(peerBytes.Length);
                    peerWins = cmp < 0;   // 本端 id 更小 → 本端让位
                }
                if (!peerWins)
                {
                    Log(L10n.T("双方同时发起 KK，本端 session 占优，坚持发起方等 msg2 → ") + Short(peer));
                    return null;
                }
                // 让位前把未完成的发起态清掉（上面已 Remove，这里再确保会话也重置）
                ResetSession(peer);
            }

            byte[] payload;
            QaulKk responder;
            try
            {
                responder = QaulKk.ReadAsResponder(MyXPriv(), MyXPub(), remoteX, msg.Data, out payload);
            }
            catch (Exception)
            {
                // 最常见：对端针对我们**旧** msg1 的 msg2 迟到（我们已被对方的新 msg1
                // 切成 responder）。这是旧会话的残帧，丢掉即可——不重协、不刷错误日志，
                // 对端 alive 的话它的下一条消息会带新 msg1 自然重建。
                Log(L10n.T("丢弃迟到的旧 KK 握手帧 → ") + Short(peer));
                return null;
            }

            var session = new QaulSession
            {
                SessionId = msg.SessionId,
                Initiator = false,
                Phase = 1,
                SendNonce = 0,
                EstablishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var msg2Payload = BuildSecondHandshake(Array.Empty<byte>());
            var msg2 = responder.BuildResponderMessage(msg2Payload);
            session.SendKey = LocalIdentity.Hex(responder.SendKey);
            session.RecvKey = LocalIdentity.Hex(responder.RecvKey);
            Put(peer, session);

            var send = SendCryptoFrame;
            if (send != null)
            {
                try
                {
                    send(peer, new EncryptedMsg
                    {
                        State = 1,
                        SessionId = session.SessionId,
                        Nonce = 0,
                        Data = msg2
                    });
                }
                catch { }
            }

            return payload;
        }

        /// <summary>qaul 的 CryptoserviceContainer{SecondHandshake{signature, received_at}}。</summary>
        public static byte[] BuildSecondHandshake(byte[] signature)
        {
            var inner = new PbWriter();
            inner.Bytes(1, signature ?? new byte[0]);
            inner.Int(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var wrap = new PbWriter();
            wrap.Sub(1, inner);          // CryptoserviceContainer.second_handshake

            // Messaging { crypto_service = 3 }
            var svc = new PbWriter();
            svc.Bytes(1, wrap.ToArray());
            var msg = new PbWriter();
            msg.Sub(3, svc);
            return msg.ToArray();
        }

        private static uint NewSessionId()
        {
            var b = Curve25519.RandomSeed();
            return BitConverter.ToUInt32(b, 0);
        }

        /* ===================== 持久化 ===================== */

        private void Load()
        {
            try
            {
                if (storePath == null || !File.Exists(storePath)) return;
                var json = File.ReadAllText(storePath, Encoding.UTF8);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, QaulSession>>(json);
                if (dict == null) return;
                lock (gate)
                {
                    foreach (var kv in dict)
                    {
                        // 半握手（Phase 0）是「只发了 msg1、等 msg2」的中间态，绝不能跨重启保留，
                        // 否则重启后该对端会永久卡在「等 KK msg2」。只恢复已建立的传输态会话。
                        if (kv.Value != null && kv.Value.Phase == 1)
                            sessions[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                if (storePath == null) return;
                string json;
                lock (gate) { json = JsonConvert.SerializeObject(sessions); }
                File.WriteAllText(storePath, json, Encoding.UTF8);
            }
            catch { }
        }
    }
}
