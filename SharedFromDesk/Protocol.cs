using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeskShare;

// 注意：本文件的命名空间刻意用「块级」写法而非 `namespace desk;`，
// 因为它是两端共享的同一份源码，而 LanChat（安卓）工程固定 langversion 8.0，
// 文件级命名空间需要 C# 10。同理本文件不得使用 record struct、顶级语句等新语法。
namespace desk
{

internal static class Protocol
{
	public enum MessageType : byte
	{
		LoginRequest = 1,
		LoginResponse,
		VideoFrame,
		MouseEvent,
		KeyEvent,
		Disconnect,
		ClipboardText,
		FileControl,
		FileData,
		QualitySettings,
		Ping,
		Pong,
		ForceKeyFrame,
		LogLine,
		PermissionInfo,
		CursorData,
		CursorPosition,
		ChatMessage,
		ControlAction,
		PrivacyMode,
		TestDelay,
		SupportedDecoding,
		SupportedEncoding,
		ClipboardData,
		AudioFormat,
		AudioFrame,
		AudioToggle,
		CliprdrFiles,
		PortForward,

		// ===== 加密远控握手（与 DeskShare.RdWire 常量一一对应，勿随意挪动数值）=====
		// 40/41/42 取自现有取值（最大 29=PortForward）之后的空档，留出扩展余地。
		RdHello = RdWire.MsgRdHello,
		RdHelloAck = RdWire.MsgRdHelloAck,
		SecurePayload = RdWire.MsgSecurePayload,

		// ===== v2 能力协商 / 扩展输入 =====
		// 显式赋值并追加在末尾：旧端遇到未知 type 会走 switch 的 default 分支忽略掉，
		// 不会断连；新端收不到 CapabilityAck 就自动降级为「旧端能力集」。
		// 这样 v1 与 v2 端点可以任意组合连接，只是 v1 组合下不启用 H264 / ABR / 扩展输入。
		Capability = 43,      // 查看端 -> 被控端：声明自己能解码什么、支持哪些机制
		CapabilityAck = 44,   // 被控端 -> 查看端：回协商结果（选中能力集）
		MouseEventEx = 45,    // 扩展鼠标：浮点归一化坐标 + displayId
		KeyEventEx = 46,      // 扩展键盘：scancode + modifiers + unicode

		// 浏览器 / 手机内置键盘 -> 被控端：UTF-16LE 文本（IME/中文/粘贴）。追加末尾，旧端 default 分支安全忽略。
		// 必须与 PC 端 Protocol.cs 保持同一取值；被控端 HandleUnicodeText 用 Encoding.Unicode 解析。
		UnicodeText = 50
	}

	[Flags]
	public enum Permission : byte
	{
		Keyboard = 1,
		Clipboard = 2,
		Audio = 4,
		File = 8,
		Restart = 0x10,
		Recording = 0x20,
		BlockInput = 0x40,
		PrivacyMode = 0x80
	}

	public enum ControlActionId : byte
	{
		CtrlAltDel = 1,
		LockScreen,
		BlockInputOn,
		BlockInputOff,
		RemoteRestart,

		// ===== 安卓被控专用（追加在末尾，不动前面既有取值，保证旧端仍能解析）=====
		// 手机没有键盘上的"返回/主页/最近任务/通知栏"，查看端工具栏直接下发这几个动作，
		// 由安卓被控端的无障碍服务 performGlobalAction 执行——这正是 rustdesk 的做法：
		// 鼠标右键/中间键在手机端映射成返回与最近任务，而不是硬塞一个不存在的键。
		AndroidBack,
		AndroidHome,
		AndroidRecents,
		AndroidNotifications,

		// 音量键：手机被控时查看端的"音量+/-"按钮走这里（不依赖 VK 码）
		AndroidVolumeUp,
		AndroidVolumeDown,

		// Web 端：读取被控端剪贴板文本，服务端用 ClipboardText(type=7) 回发。
		// 固定值 12，与上面安卓动作(6~11)错开；与 PC 端 Protocol.cs 保持一致。
		ReadClipboard = 12
	}

	[Flags]
	public enum CodecAbility : byte
	{
		None = 0,
		Vp8 = 1,
		Vp9 = 2,
		Av1 = 4,
		H265 = 0x10
	}

	/// <summary>
	/// 画质参数。原先用 `record struct` 的紧凑写法（需要 C# 10），
	/// 但 LanChat 的安卓工程固定 langversion 8.0，而本文件是两端共享的同一份源码，
	/// 因此改为传统只读结构体——C# 8 与 C# 12 都能编译，语义不变。
	/// </summary>
	public readonly struct QualityConfig
	{
		public EncodingMode Mode { get; }
		public int Quality { get; }
		public int MaxWidth { get; }
		public int Fps { get; }
		public int ResyncSec { get; }
		public int BlockSize { get; }
		/// <summary>目标码率（kbps，0 = 不限制）。仅在双方协商出 Abr 能力时由服务端闭环调整。</summary>
		public int BitrateKbps { get; }

		public QualityConfig (EncodingMode mode, int quality, int maxWidth, int fps, int resyncSec, int blockSize)
			: this (mode, quality, maxWidth, fps, resyncSec, blockSize, 0)
		{
		}

		public QualityConfig (EncodingMode mode, int quality, int maxWidth, int fps, int resyncSec, int blockSize, int bitrateKbps)
		{
			Mode = mode;
			Quality = quality;
			MaxWidth = maxWidth;
			Fps = fps;
			ResyncSec = resyncSec;
			BlockSize = blockSize;
			BitrateKbps = bitrateKbps;
		}

		/// <summary>按新码率派生一份配置（ABR 调整时用，原实例不可变）。</summary>
		public QualityConfig WithBitrate (int kbps) => new QualityConfig (Mode, Quality, MaxWidth, Fps, ResyncSec, BlockSize, kbps);

		/// <summary>13 字节扩展编码：前 9 字节与 v1 完全相同，尾部追加目标码率。</summary>
		public byte[] EncodeEx ()
		{
			byte[] b = Encode ();
			byte[] r = new byte[13];
			Buffer.BlockCopy (b, 0, r, 0, 9);
			WriteInt32 (r, 9, BitrateKbps);
			return r;
		}

		public static QualityConfig Default => new QualityConfig (EncodingMode.JpegRegions, 70, 1920, 60, 4, 32);

		public byte[] Encode ()
		{
			MemoryStream memoryStream = new MemoryStream (9);
			memoryStream.WriteByte ((byte)Mode);
			memoryStream.WriteByte ((byte)Math.Clamp (Quality, 1, 100));
			WriteInt32 (memoryStream, MaxWidth);
			memoryStream.WriteByte ((byte)Math.Clamp (Fps, 1, 60));
			memoryStream.WriteByte ((byte)Math.Clamp (ResyncSec, 1, 10));
			memoryStream.WriteByte ((byte)Math.Clamp (BlockSize, 8, 128));
			return memoryStream.ToArray ();
		}

		public static QualityConfig Decode (byte[] p)
		{
			if (p.Length < 9) {
				return Default;
			}
			// 兼容 9 字节(v1) 与 13 字节(v2，带码率) 两种负载
			int bitrate = p.Length >= 13 ? ReadInt32 (p, 9) : 0;
			return new QualityConfig ((EncodingMode)p [0], p [1], ReadInt32 (p, 2), p [6], p [7], p [8], bitrate);
		}
	}

	public enum EncodingMode : byte
	{
		JpegRegions,
		RawRegions,
		JpegFullFrame,
		H265,
		/// <summary>
		/// H.264 整帧硬编码（Media Foundation）。跨端首选：安卓 MediaCodec 必定支持，
		/// 带宽比整帧 JPEG 小一个数量级，代价是编码器有 9 帧前瞻（30fps 约 300ms 延迟）。
		/// 追加在末尾，避免破坏既有枚举值在两端协议里的对应关系。
		/// </summary>
		H264
	}

	/* ============================================================
	 * v2 能力协商（Capability handshake）
	 *
	 * 为什么不用扩展 RdHello：RdHello 的 33 字节负载在 v1 端有严格长度与
	 * Version==1 校验，改长度或升版本会让新旧端直接握手失败。改为在加密会话
	 * 建立之后，用新的 MessageType 走「可选能力协商」：
	 *   · 旧端收到 type=43/44 会落到 switch 的 default（忽略），握手照常完成；
	 *   · 新端若 1.5s 内收不到 CapabilityAck，按 LegacyCaps 降级。
	 * 同时交换 ProtocolHash：两端 Protocol.cs 若不同步（枚举值错位、帧格式改了
	 * 却漏改另一端），会当场发现并降级 + 提示升级，而不是「连得上但黑屏/秒断」。
	 * ============================================================ */

	/// <summary>本份协议的最低兼容版本与当前版本。</summary>
	public const byte ProtocolVerMin = 1;
	public const byte ProtocolVerCur = 2;

	/// <summary>
	/// 协议摘要：每次改动消息类型 / 帧格式 / 枚举取值都要手工 +1，
	/// 双端从同一份 Protocol.cs 取值，握手时交换校验。
	/// </summary>
	public const uint ProtocolHash = 0x00020001u;

	[Flags]
	public enum Capability : uint
	{
		None = 0,
		H264Decode = 1u << 0,     // 查看端能解 H264（Annex-B）
		H264Encode = 1u << 1,     // 被控端能编 H264
		H265Decode = 1u << 2,
		RegionFrame = 1u << 3,    // 支持 FLAG_REGION 脏矩形增量帧
		Abr = 1u << 4,            // 支持 TestDelay/QosFeedback 闭环自适应码率
		InputPriority = 1u << 5,  // 发送侧输入消息插队，画面帧可丢弃
		Heartbeat = 1u << 6,      // 带时间戳 Ping/Pong + 无数据超时自动重连
		MultiDisplay = 1u << 7,   // 支持 displayId 单屏选择
		UnicodeKey = 1u << 8,     // 支持 KeyEventEx（scancode/modifiers/unicode）
		NormalizedCoord = 1u << 9,// 支持 MouseEventEx（按帧宽高映射的归一化坐标）
		SecureOnly = 1u << 10     // 拒绝 v1 明文 LoginRequest
	}

	/// <summary>v1 端的能力集：只保证整帧 JPEG 能解，其它一律不启用。</summary>
	public const Capability LegacyCaps = Capability.None;

	public const int CapBodyLength = 20;
	public const int QosBodyLength = 32;

	/// <summary>VideoFrame.flags 的 QoS 位：置位时 14B 帧头后追加 [frameId:4][sendTs:8]。</summary>
	public const byte FLAG_QOS = 4;
	public const int QosFrameHeaderLength = 12;

	private const byte CapMagic0 = (byte)'M';
	private const byte CapMagic1 = (byte)'C';
	private const byte CapMagic2 = (byte)'A';
	private const byte CapMagic3 = (byte)'P';
	private const byte QosMagic0 = (byte)'Q';
	private const byte QosMagic1 = (byte)'O';
	private const byte QosMagic2 = (byte)'S';
	private const byte QosMagic3 = (byte)'1';

	/// <summary>构造能力协商负载（20 字节，固定长度便于 v1 端安全忽略）。</summary>
	public static byte[] EncodeCapability (Capability caps, ushort maxFps)
	{
		byte[] p = new byte[CapBodyLength];
		p[0] = CapMagic0; p[1] = CapMagic1; p[2] = CapMagic2; p[3] = CapMagic3;
		p[4] = ProtocolVerMin;
		p[5] = ProtocolVerCur;
		WriteInt32 (p, 6, (int)(uint)caps);
		WriteInt32 (p, 10, (int)ProtocolHash);
		p[14] = (byte)(maxFps & 0xFF);
		p[15] = (byte)(maxFps >> 8);
		return p;
	}

	/// <summary>
	/// 解析能力负载。返回 false 表示「对端不是 v2 或协议摘要不一致」，
	/// 调用方应降级为 LegacyCaps 并提示升级，而不是断开连接。
	/// </summary>
	public static bool TryDecodeCapability (byte[] p, out byte verMin, out byte verCur,
		out Capability caps, out uint hash, out ushort maxFps)
	{
		verMin = 0; verCur = 0; caps = Capability.None; hash = 0; maxFps = 0;
		if (p == null || p.Length < CapBodyLength) return false;
		if (p[0] != CapMagic0 || p[1] != CapMagic1 || p[2] != CapMagic2 || p[3] != CapMagic3) return false;
		verMin = p[4]; verCur = p[5];
		caps = (Capability)(uint)ReadInt32 (p, 6);
		hash = (uint)ReadInt32 (p, 10);
		maxFps = (ushort)(p[14] | (p[15] << 8));
		return verCur >= ProtocolVerMin && verMin <= ProtocolVerCur;
	}

	/// <summary>两端能力取交集（协商结果）。</summary>
	public static Capability Negotiate (Capability a, Capability b) => a & b;

	/// <summary>构造 TestDelay / QoS 反馈负载（32 字节）。旧端会把这类负载原样回显，不解析。</summary>
	public static byte[] EncodeQos (int frameId, long sendTs, long recvTs, int rttMs, int bitrateKbps)
	{
		byte[] p = new byte[QosBodyLength];
		p[0] = QosMagic0; p[1] = QosMagic1; p[2] = QosMagic2; p[3] = QosMagic3;
		WriteInt32 (p, 4, frameId);
		WriteInt64 (p, 8, sendTs);
		WriteInt64 (p, 16, recvTs);
		WriteInt32 (p, 24, rttMs);
		WriteInt32 (p, 28, bitrateKbps);
		return p;
	}

	public static bool TryDecodeQos (byte[] p, out int frameId, out long sendTs,
		out long recvTs, out int rttMs, out int bitrateKbps)
	{
		frameId = 0; sendTs = 0; recvTs = 0; rttMs = 0; bitrateKbps = 0;
		if (p == null || p.Length < QosBodyLength) return false;
		if (p[0] != QosMagic0 || p[1] != QosMagic1 || p[2] != QosMagic2 || p[3] != QosMagic3) return false;
		frameId = ReadInt32 (p, 4);
		sendTs = ReadInt64 (p, 8);
		recvTs = ReadInt64 (p, 16);
		rttMs = ReadInt32 (p, 24);
		bitrateKbps = ReadInt32 (p, 28);
		return true;
	}

	public static void WriteInt64 (byte[] buf, int offset, long value)
	{
		for (int i = 0; i < 8; i++) buf[offset + i] = (byte)(value >> (8 * i));
	}

	public static long ReadInt64 (ReadOnlySpan<byte> buf, int offset)
	{
		long v = 0;
		for (int i = 7; i >= 0; i--) v = (v << 8) | buf[offset + i];
		return v;
	}

	public const string ClipFileRoot = "__clipboard__";

	public const int RestartGraceMinutes = 5;

	public const byte MOUSE_RELATIVE = 4;

	/// <summary>远控默认端口（单一来源：RdWire.DefaultRemotePort，当前 9012）。</summary>
	public const int RdDefaultPort = RdWire.DefaultRemotePort;

	/// <summary>
	/// 鼠标按键编码（MouseEvent 负载里的 button 字节）。
	///
	/// 【必须统一，且以 Windows 侧为准】
	/// 这个取值不是随便定的：Windows 查看端由低级鼠标钩子直接给出 1/2/3
	/// （WM_LBUTTONDOWN→1、WM_RBUTTONDOWN→2、WM_MBUTTONDOWN→3），
	/// 网页端 app.js 也是 BTN_LEFT=1/BTN_RIGHT=2/BTN_MIDDLE=3。
	/// 安卓端早期错用了 0=左/1=右，结果手机发左键到 Windows 变成"没有按键"，
	/// Windows 发左键到手机则被当成右键长按——这是"点不动、拖不动"的根因。
	/// </summary>
	public static class MouseButton
	{
		public const byte Left = 1;
		public const byte Right = 2;
		public const byte Middle = 3;
	}

	public const int MaxMessageBytes = 67108864;

	public const int ReadIdleTimeoutMs = 30000;

	public const int SendTimeoutMs = 20000;

	public const byte FLAG_REGION = 1;

	public const byte FLAG_JPEG_BLOCK = 2;

	public const byte CODEC_JPEG = 0;

	public const byte CODEC_RAW = 2;

	public const byte CODEC_H265 = 3;

	/// <summary>
	/// H.264（Annex-B 裸流，关键帧自带 SPS/PPS）。
	/// 取 4，避开 0=JPEG / 2=RAW / 3=H.265。
	/// </summary>
	public const byte CODEC_H264 = 4;

	public const byte FILE_OFFER = 1;

	public const byte FILE_ACCEPT = 2;

	public const byte FILE_REJECT = 3;

	public const byte FILE_DONE = 4;

	public const byte FILE_CANCEL = 5;

	public static byte[] Encode (MessageType type, byte[]? payload)
	{
		if (payload == null) {
			payload = Array.Empty<byte> ();
		}
		int num = 1 + payload.Length;
		byte[] array = new byte[4 + num];
		array [0] = (byte)(num & 0xFF);
		array [1] = (byte)((num >> 8) & 0xFF);
		array [2] = (byte)((num >> 16) & 0xFF);
		array [3] = (byte)((num >> 24) & 0xFF);
		array [4] = (byte)type;
		if (payload.Length != 0) {
			Buffer.BlockCopy (payload, 0, array, 5, payload.Length);
		}
		return array;
	}

	public static async Task<(MessageType type, byte[] payload)?> ReadMessageAsync (NetworkStream stream, CancellationToken ct)
	{
		return await ReadMessageAsync (stream, ct, 30000);
	}

	/// <summary>
	/// 读一条报文，允许指定空闲超时。
	/// <para><paramref name="idleTimeoutMs"/> &gt; 0：等这么久还没数据就取消（用于握手等一问一答场景）。</para>
	/// <para><paramref name="idleTimeoutMs"/> &lt;= 0：不设空闲超时，只在外部 ct 取消时退出。
	/// 长连接的读循环**必须**用这一档 —— 远控画面只在桌面有变化时才发帧，
	/// 查看端也可能长时间不动键盘鼠标，用 30 秒超时会把一条完全健康的连接整条拆掉。</para>
	/// </summary>
	public static async Task<(MessageType type, byte[] payload)?> ReadMessageAsync (NetworkStream stream, CancellationToken ct, int idleTimeoutMs)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource (ct);
		if (idleTimeoutMs > 0) {
			timeout.CancelAfter (idleTimeoutMs);
		}
		CancellationToken t = timeout.Token;
		byte[] lenBuf = new byte[4];
		if (!(await ReadExactAsync (stream, lenBuf, 0, 4, t))) {
			return null;
		}
		int bodyLen = lenBuf [0] | (lenBuf [1] << 8) | (lenBuf [2] << 16) | (lenBuf [3] << 24);
		if (bodyLen < 1 || bodyLen > 67108864) {
			throw new IOException ($"非法报文长度 {bodyLen}，流已错位，断开并重连");
		}
		byte[] body = new byte[bodyLen];
		if (!(await ReadExactAsync (stream, body, 0, bodyLen, t))) {
			return null;
		}
		MessageType type = (MessageType)body [0];
		byte[] payload = new byte[bodyLen - 1];
		if (payload.Length != 0) {
			Buffer.BlockCopy (body, 1, payload, 0, payload.Length);
		}
		return (type, payload);
	}

	public static async Task<bool> ReadExactAsync (NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
	{
		int n;
		for (int read = 0; read < count; read += n) {
			n = await stream.ReadAsync (buffer, offset + read, count - read, ct);
			if (n == 0) {
				return false;
			}
		}
		return true;
	}
	public static async Task SendAsync (NetworkStream stream, MessageType type, byte[]? payload, CancellationToken ct = default(CancellationToken), int timeoutMs = 20000)
	{
		byte[] data = Encode (type, payload);
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource (ct);
		cts.CancelAfter (timeoutMs);
		await stream.WriteAsync (data, cts.Token);
	}

	public static void WriteString (MemoryStream ms, string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes (s);
		WriteInt32 (ms, bytes.Length);
		ms.Write (bytes, 0, bytes.Length);
	}

	public static string ReadString (ReadOnlySpan<byte> buf, ref int offset)
	{
		int num = ReadInt32 (buf, offset);
		offset += 4;
		string result = Encoding.UTF8.GetString (buf.Slice (offset, num));
		offset += num;
		return result;
	}

	public static void WriteInt32 (MemoryStream ms, int value)
	{
		ms.Write (BitConverter.GetBytes (value), 0, 4);
	}

	/// <summary>往定长缓冲区的指定偏移写 4 字节小端整数（两端共用）。</summary>
	public static void WriteInt32 (byte[] buf, int offset, int value)
	{
		buf [offset] = (byte)value;
		buf [offset + 1] = (byte)(value >> 8);
		buf [offset + 2] = (byte)(value >> 16);
		buf [offset + 3] = (byte)(value >> 24);
	}

	public static int ReadInt32 (ReadOnlySpan<byte> buf, int offset)
	{
		return buf [offset] | (buf [offset + 1] << 8) | (buf [offset + 2] << 16) | (buf [offset + 3] << 24);
	}
}
}
