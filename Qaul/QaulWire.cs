using System;
using System.Collections.Generic;
using System.Text;

namespace AhoChatA
{
    /// <summary>qaul 的 RPC 模块编号（与 qaul_rpc.proto 的 Modules 枚举一一对应）。</summary>
    public enum QaulModule
    {
        None = 0,
        Rpc = 1,
        Node = 2,
        UserAccounts = 3,
        Users = 4,
        Router = 5,
        Feed = 6,
        Connections = 7,
        Debug = 8,
        Group = 9,
        Chat = 10,
        ChatFile = 11,
        Ble = 12,
        Rtc = 13,
        Dtn = 14,
        Auth = 15,
        Subscribe = 16,
        Transports = 17,
        Crypto = 18,
        AccountManagement = 19
    }

    /// <summary>
    /// qaul 的 RPC 信封（qaul_rpc.proto）。
    ///
    /// 所有往来数据都包在这一层里：
    ///   module(1) / request_id(2) / user_id(3) / data(4)
    /// 这与 qauld-rpc 的 SocketTransport 完全一致——同一份字节流既能走
    /// qauld 的 Unix socket，也能走本实现的对等 TCP 消息端口。
    /// </summary>
    public class QaulEnvelope
    {
        public QaulModule Module = QaulModule.Chat;
        public string RequestId = "";
        public byte[] UserId = new byte[0];
        public byte[] Data = new byte[0];

        public byte[] Encode()
        {
            var w = new PbWriter();
            w.Int(1, (long)Module);
            if (!string.IsNullOrEmpty(RequestId)) w.Str(2, RequestId);
            if (UserId != null && UserId.Length > 0) w.Bytes(3, UserId);
            if (Data != null && Data.Length > 0) w.Bytes(4, Data);
            return w.ToArray();
        }

        public static QaulEnvelope Decode(byte[] raw)
        {
            var env = new QaulEnvelope();
            if (raw == null || raw.Length == 0) return env;
            var r = new PbReader(raw);
            int field, wire;
            while (r.Next(out field, out wire))
            {
                switch (field)
                {
                    case 1:
                        if (wire == 0) env.Module = (QaulModule)(int)r.Varint();
                        else r.Skip(wire);
                        break;
                    case 2:
                        if (wire == 2) env.RequestId = r.Str();
                        else r.Skip(wire);
                        break;
                    case 3:
                        if (wire == 2) env.UserId = r.Bytes();
                        else r.Skip(wire);
                        break;
                    case 4:
                        if (wire == 2) env.Data = r.Bytes();
                        else r.Skip(wire);
                        break;
                    default:
                        r.Skip(wire);
                        break;
                }
            }
            return env;
        }
    }

    /// <summary>qaul 的一条聊天消息（ChatMessage / 直发载荷的统一视图）。</summary>
    public class QaulChatMessage
    {
        public byte[] GroupId = new byte[0];
        public byte[] SenderId = new byte[0];
        public byte[] MessageId = new byte[0];
        public long SentAt;
        public long ReceivedAt;
        public ulong Index;
        public int Status;
        /// <summary>正文（ChatContentMessage.text）。</summary>
        public string Text = "";
        /// <summary>Mchat 扩展字段（ChatContent 的字段 100），非标准 qaul 客户端会忽略。</summary>
        public string Meta = "";
        /// <summary>是否为文件消息（FileContent）。</summary>
        public bool IsFile;
        public string FileName = "";
        public long FileId;
    }

    /// <summary>
    /// qaul 聊天/群组消息的编解码。
    ///
    /// 出站帧使用 ChatMessage 完整结构（携于 ChatConversationList 内），
    /// 这样 message_id / sender_id / sent_at / group_id 都落在 qaul 自己的
    /// schema 里，正文放在 ChatContentMessage.chat_content.text，Mchat 需要的
    /// 额外信息（会话键、昵称、富媒体类型、控制语义）放在 ChatContent 的
    /// 扩展字段 100（protobuf 允许未知字段，标准 qaul 端会安全忽略）。
    ///
    /// 入站同时兼容两种形态：
    ///   a) ChatConversationList（完整消息结构，本实现的对等帧）
    ///   b) ChatMessageSend（qaul 客户端「发送」的原生形态，纯文本）
    /// </summary>
    public static class QaulWire
    {
        /// <summary>ChatContent 中承载 Mchat 元数据的扩展字段号。</summary>
        public const int MetaField = 100;

        /* ===================== 出站 ===================== */

        public static byte[] EncodeChatFrame(QaulChatMessage msg, string requestId, byte[] senderId)
        {
            var chatMessage = new PbWriter();
            chatMessage.UInt(1, msg.Index);
            if (msg.SenderId.Length > 0) chatMessage.Bytes(2, msg.SenderId);
            if (msg.MessageId.Length > 0) chatMessage.Bytes(3, msg.MessageId);
            chatMessage.Int(4, msg.Status);
            if (msg.GroupId.Length > 0) chatMessage.Bytes(5, msg.GroupId);
            chatMessage.UInt(6, (ulong)msg.SentAt);
            chatMessage.UInt(7, (ulong)Math.Max(msg.ReceivedAt, msg.SentAt));
            chatMessage.Bytes(8, EncodeContent(msg.Text, msg.Meta));

            var list = new PbWriter();
            list.Bytes(1, msg.GroupId);
            list.Sub(2, chatMessage);

            var chat = new PbWriter();
            chat.Sub(4, list);          // Chat.conversation_list

            var env = new QaulEnvelope
            {
                Module = QaulModule.Chat,
                RequestId = requestId ?? "",
                UserId = senderId ?? new byte[0],
                Data = chat.ToArray()
            };
            return env.Encode();
        }

        /// <summary>编码 ChatContentMessage：{ chat_content: { text, lc(100) } }。</summary>
        public static byte[] EncodeContent(string text, string meta)
        {
            var content = new PbWriter();
            content.Str(1, text ?? "");
            if (!string.IsNullOrEmpty(meta))
                content.Bytes(MetaField, Encoding.UTF8.GetBytes(meta));

            var wrapper = new PbWriter();
            wrapper.Sub(1, content);
            return wrapper.ToArray();
        }

        public static byte[] EncodeContent(byte[] rawContent)
        {
            return rawContent;
        }

        /* ===================== 入站 ===================== */

        /// <summary>解析 Chat 模块载荷，返回其中携带的所有消息。</summary>
        public static List<QaulChatMessage> DecodeChat(byte[] data)
        {
            var result = new List<QaulChatMessage>();
            if (data == null || data.Length == 0) return result;

            var chat = new PbReader(data);
            int field, wire;
            while (chat.Next(out field, out wire))
            {
                if (field == 4 && wire == 2)
                {
                    var list = chat.Sub();
                    byte[] groupId = new byte[0];
                    var raws = new List<byte[]>();
                    int f2, w2;
                    while (list.Next(out f2, out w2))
                    {
                        if (f2 == 1 && w2 == 2) groupId = list.Bytes();
                        else if (f2 == 2 && w2 == 2) raws.Add(list.Bytes());
                        else list.Skip(w2);
                    }
                    foreach (var raw in raws)
                    {
                        var m = DecodeChatMessage(raw);
                        if (m != null)
                        {
                            if (m.GroupId.Length == 0) m.GroupId = groupId;
                            result.Add(m);
                        }
                    }
                }
                else if (field == 5 && wire == 2)
                {
                    // ChatMessageSend：qaul 客户端的原生发送形态（纯文本）
                    var send = chat.Sub();
                    var m = new QaulChatMessage();
                    int f2, w2;
                    while (send.Next(out f2, out w2))
                    {
                        if (f2 == 1 && w2 == 2) m.GroupId = send.Bytes();
                        else if (f2 == 2 && w2 == 2) m.Text = send.Str();
                        else send.Skip(w2);
                    }
                    if (m.GroupId.Length == 0) continue;
                    result.Add(m);
                }
                else
                {
                    chat.Skip(wire);
                }
            }
            return result;
        }

        public static QaulChatMessage DecodeChatMessage(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            var m = new QaulChatMessage();
            var r = new PbReader(raw);
            int field, wire;
            while (r.Next(out field, out wire))
            {
                switch (field)
                {
                    case 1: if (wire == 0) m.Index = r.Varint(); else r.Skip(wire); break;
                    case 2: if (wire == 2) m.SenderId = r.Bytes(); else r.Skip(wire); break;
                    case 3: if (wire == 2) m.MessageId = r.Bytes(); else r.Skip(wire); break;
                    case 4: if (wire == 0) m.Status = (int)r.Varint(); else r.Skip(wire); break;
                    case 5: if (wire == 2) m.GroupId = r.Bytes(); else r.Skip(wire); break;
                    case 6: if (wire == 0) m.SentAt = (long)r.Varint(); else r.Skip(wire); break;
                    case 7: if (wire == 0) m.ReceivedAt = (long)r.Varint(); else r.Skip(wire); break;
                    case 8:
                        if (wire == 2) DecodeContent(r.Bytes(), m);
                        else r.Skip(wire);
                        break;
                    default: r.Skip(wire); break;
                }
            }
            return m;
        }

        private static void DecodeContent(byte[] raw, QaulChatMessage m)
        {
            if (raw == null || raw.Length == 0) return;
            var wrapper = new PbReader(raw);
            int field, wire;
            while (wrapper.Next(out field, out wire))
            {
                if (field == 1 && wire == 2)
                {
                    var content = wrapper.Sub();
                    int f2, w2;
                    while (content.Next(out f2, out w2))
                    {
                        if (f2 == 1 && w2 == 2) m.Text = content.Str();
                        else if (f2 == MetaField && w2 == 2) m.Meta = content.Str();
                        else content.Skip(w2);
                    }
                }
                else if (field == 2 && wire == 2)
                {
                    m.IsFile = true;
                    var file = wrapper.Sub();
                    int f2, w2;
                    while (file.Next(out f2, out w2))
                    {
                        if (f2 == 1 && w2 == 0) m.FileId = (long)file.Varint();
                        else if (f2 == 2 && w2 == 2) m.FileName = file.Str();
                        else file.Skip(w2);
                    }
                }
                else
                {
                    wrapper.Skip(wire);
                }
            }
        }

        /* ===================== 群组（房间） ===================== */

        /// <summary>GroupCreateRequest：{ group_name }。</summary>
        public static byte[] EncodeGroupCreate(string roomName, string requestId, byte[] userId)
        {
            var request = new PbWriter();
            request.Str(1, roomName ?? "");

            var group = new PbWriter();
            group.Sub(1, request);

            return new QaulEnvelope
            {
                Module = QaulModule.Group,
                RequestId = requestId ?? "",
                UserId = userId ?? new byte[0],
                Data = group.ToArray()
            }.Encode();
        }

        /// <summary>解析 GroupCreateResponse，取出 group_id（失败返回 null）。</summary>
        public static byte[] DecodeGroupCreateResponse(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            var r = new PbReader(data);
            int field, wire;
            while (r.Next(out field, out wire))
            {
                if (field == 2 && wire == 2)
                {
                    var resp = r.Sub();
                    int f2, w2;
                    while (resp.Next(out f2, out w2))
                    {
                        if (f2 == 1 && w2 == 2) return resp.Bytes();
                        resp.Skip(w2);
                    }
                }
                else
                {
                    r.Skip(wire);
                }
            }
            return null;
        }

        /* ===================== 工具 ===================== */

        /// <summary>qaul 的 16 字节 UUID ↔ 十六进制串。</summary>
        public static string GroupIdHex(byte[] groupId)
        {
            return Convs.Hex(groupId);
        }
    }
}
