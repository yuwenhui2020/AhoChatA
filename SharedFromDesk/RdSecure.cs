using System;
using System.Security.Cryptography;
using System.Text;
using AhoChatA;

namespace DeskShare
{
	/// <summary>
	/// desk（Windows 远程桌面）与 LanChat（安卓）共用的远控链路常量。
	///
	/// 本文件只有 **一份源码**：放在 desk/Shared/ 下，由 LanChat 的 AhoChatA.csproj 通过
	/// &lt;Compile Include="..\..\desk\Shared\RdSecure.cs" Link="Shared\RdSecure.cs" /&gt; 链接进来；
	/// desk 侧则由 SDK 风格工程自动通配包含。
	/// 这样「统一」是编译期强制的：任何一端改了算法或帧格式，另一端必须同步，不可能悄悄漂移。
	///
	/// X25519 直接复用 LanChat 已有的 Identity/Curve25519.cs（RFC 7748 官方测试向量已校验），
	/// desk 侧同理链接同一份文件，避免两端各写一份、算法实现出现偏差。
	/// </summary>
	public static class RdWire
	{
		/// <summary>握手版本。后续若要改帧结构，先升此值以便对端识别。</summary>
		public const byte Version = 1;

		// 挂在 desk 既有 Protocol.MessageType 尾部（现有取值到 29 = PortForward），预留空档便于以后补塞。
		public const byte MsgRdHello = 40;         // C→S：版本号 + 32 字节临时 X25519 公钥
		public const byte MsgRdHelloAck = 41;      // S→C：32 字节临时 X25519 公钥
		public const byte MsgSecurePayload = 42;   // 双向：加密后的应用层帧

		public const int HelloBodyLength = 1 + 32;
		public const int HelloAckBodyLength = 32;

		/// <summary>desk 远控默认端口（与 Settings.HostPort 默认值保持一致）。</summary>
		// 远控主通道的默认端口。**两端必须一致**：desk 被控端、安卓被控端、
		// 各自的查看端默认值、以及 hello 里通告的端口，全部以这里为准。
		// 早期桌面端用 9000、Web 端用 9012，手机端按 9000 猜电脑，常常对接不上；
		// 现在统一成 9012，并把桌面 Web 远控挪到 9013，避免同一台机器上两个监听撞端口。
		public const int DefaultRemotePort = 9012;

		/// <summary>局域网直连标记：查看端已由 LAN 设备发现拿到对端地址，
		/// 在密文握手里用它替代「设备码 + 密码」。被控端只在确认来访 IP 属于
		/// 本机同一私网网段、且用户开启了 AllowLanDirect 时才放行。</summary>
		public const string LanDirectToken = "LAN-DIRECT";

		/// <summary>本地地址与来访地址是否同属一个私网 /24。直连免密只对同网段生效，
		/// 防止公网或别的网段扫到端口就直接进门。</summary>
		public static bool IsSameLanSubnet (System.Net.IPAddress local, System.Net.IPAddress remote)
		{
			if (local == null || remote == null) return false;
			if (local.AddressFamily != remote.AddressFamily) return false;
			byte [] a = local.GetAddressBytes ();
			byte [] b = remote.GetAddressBytes ();
			if (a.Length != 4 || b.Length != 4) return false;
			bool priv = a [0] == 10
				|| (a [0] == 172 && a [1] >= 16 && a [1] <= 31)
				|| (a [0] == 192 && a [1] == 168)
				|| (a [0] == 169 && a [1] == 254);
			return priv && a [0] == b [0] && a [1] == b [1] && a [2] == b [2];
		}

		/// <summary>LanChat hello 发现包里通告「我可被远控」的字段名：值为远控 TCP 端口。</summary>
		public const string HelloFieldRemotePort = "rd";

		/// <summary>LanChat hello 发现包里通告「我支持加密远控」的字段名：1 = 支持。</summary>
		public const string HelloFieldRemoteSecure = "rs";

		/// <summary>局域网聊天消息归属的默认会话。安卓侧缺失 conv 时会回退 room:mesh，
		/// 用户若不在该房间就看不到消息——所以发送时必须显式带上。</summary>
		public const string LanChatConv = "chan:mesh";
	}

	/// <summary>
	/// 应用层帧的加解封。desk（Windows 端）与 LanChat（安卓端）共用这一份实现——
	/// 「明文帧怎么塞进密文帧」如果在两端各写一遍，迟早会有一端改了另一端没跟上，
	/// 症状是握手成功但连上就秒断，且极难排查。
	///
	/// 密文帧内一律承载：[1 字节 MessageType][原始 payload]。
	/// </summary>
	// 用 internal 而不用 public：desk.Protocol 是 internal 类型，
	// public 方法签名里出现 internal 类型会直接编译不过（CS0051）。
	// 两端各自把本文件编进自己的程序集，internal 完全够用。
	internal static class RdFrame
	{
		/// <summary>把一条明文帧打成密文帧<｜hy_place▁holder▁no▁813｜>的 payload。</summary>
		public static byte [] Pack (desk.Protocol.MessageType type, byte [] payload)
		{
			byte [] inner = new byte [1 + payload.Length];
			inner [0] = (byte)type;
			if (payload.Length > 0) {
				Buffer.BlockCopy (payload, 0, inner, 1, payload.Length);
			}
			return inner;
		}

		/// <summary>把密文解开后的内容还原成明文帧。</summary>
		public static bool Unpack (byte [] inner, out desk.Protocol.MessageType type, out byte [] payload)
		{
			type = 0;
			payload = Array.Empty<byte> ();
			if (inner == null || inner.Length < 1) return false;
			type = (desk.Protocol.MessageType)inner [0];
			payload = new byte [inner.Length - 1];
			if (payload.Length > 0) {
				Buffer.BlockCopy (inner, 1, payload, 0, payload.Length);
			}
			return true;
		}

		/// <summary>
		/// 加密会话内的收帧解封；明文会话原样返回。
		/// 返回 null 代表必须断开：密文校验失败，或在加密会话里收到了明文帧（降级攻击）。
		/// </summary>
		public static (desk.Protocol.MessageType type, byte [] payload)? Unwrap (RdSecureChannel channel, desk.Protocol.MessageType type, byte [] payload)
		{
			if (channel == null) {
				return (type, payload);
			}
			if (type != (desk.Protocol.MessageType)RdWire.MsgSecurePayload) {
				return null;
			}
			if (!channel.TryOpen (payload, out byte [] inner)) {
				return null;
			}
			if (!Unpack (inner, out desk.Protocol.MessageType innerType, out byte [] innerPayload)) {
				return null;
			}
			return (innerType, innerPayload);
		}
	}

	/// <summary>HKDF-SHA256（RFC 5869）最小实现。两端必须用同一套派生方式。</summary>
	public static class RdHkdf
	{
		public static byte[] Extract (byte[] salt, byte[] ikm)
		{
			using (HMACSHA256 h = new HMACSHA256 (salt ?? Array.Empty<byte> ())) {
				return h.ComputeHash (ikm);
			}
		}

		public static byte[] Expand (byte[] prk, byte[] info, int length)
		{
			if (length <= 0 || length > 255 * 32) throw new ArgumentException (L10n.T("length 超出 HKDF 输出上限"));
			byte[] result = new byte[length];
			byte[] block = Array.Empty<byte> ();
			int offset = 0;
			byte counter = 1;
			using (HMACSHA256 h = new HMACSHA256 (prk)) {
				while (offset < length) {
					byte[] input = Concat (info, block, new byte[] { counter });
					block = h.ComputeHash (input);
					int take = Math.Min (32, length - offset);
					Buffer.BlockCopy (block, 0, result, offset, take);
					offset += take;
					counter++;
				}
			}
			return result;
		}

		private static byte[] Concat (byte[] a, byte[] b, byte[] c)
		{
			byte[] r = new byte[a.Length + b.Length + c.Length];
			Buffer.BlockCopy (a, 0, r, 0, a.Length);
			Buffer.BlockCopy (b, 0, r, a.Length, b.Length);
			Buffer.BlockCopy (c, 0, r, a.Length + b.Length, c.Length);
			return r;
		}
	}

	/// <summary>一次连接的临时 X25519 密钥对。用完即弃，换来前向保密：
	/// 长期密钥（设备码/连接密码）泄露也无法解密过去录下的流量。</summary>
	public sealed class RdKeypair
	{
		public byte[] Seed { get; }
		public byte[] PublicKey { get; }

		private RdKeypair (byte[] seed)
		{
			Seed = seed;
			PublicKey = Curve25519.X25519PublicKey (seed);
		}

		public static RdKeypair NewEphemeral ()
		{
			return new RdKeypair (RandomBytes (32));
		}

		public byte[] SharedSecret (byte[] peerPublicKey)
		{
			return Curve25519.X25519SharedSecret (Seed, peerPublicKey);
		}

		public static byte[] RandomBytes (int count)
		{
			byte[] buf = new byte[count];
			using (RandomNumberGenerator rng = RandomNumberGenerator.Create ()) {
				rng.GetBytes (buf);
			}
			return buf;
		}
	}

	/// <summary>
	/// 握手派生的会话密钥。**双向各一组**（c2s / s2c），
	/// 目的：杜绝"把对方刚发来的密文原封不动发回去"的反射攻击——
	/// 单向密钥下这种回显是合法的，双向密钥下会因 MAC 校验失败被拒。
	/// </summary>
	public sealed class RdSessionKeys
	{
		public byte[] SendEncKey { get; }
		public byte[] SendMacKey { get; }
		public byte[] RecvEncKey { get; }
		public byte[] RecvMacKey { get; }
		public byte[] TranscriptHash { get; }

		private RdSessionKeys (byte[] sendEnc, byte[] sendMac, byte[] recvEnc, byte[] recvMac, byte[] transcript)
		{
			SendEncKey = sendEnc;
			SendMacKey = sendMac;
			RecvEncKey = recvEnc;
			RecvMacKey = recvMac;
			TranscriptHash = transcript;
		}

		/// <summary>由 X25519 共享密钥派生。C 与 S 传入相同的 shared/clientPub/serverPub，
		/// 仅 isServer 不同，配出的发送/接收方向自然相反。</summary>
		public static RdSessionKeys Derive (byte[] sharedSecret, byte[] clientPub, byte[] serverPub, bool isServer)
		{
			// salt 绑死握手转录：中间人改任何一个公钥都会导致两端派生出不同密钥，握手随即失败。
			byte[] transcript = Concat (clientPub, serverPub);
			byte[] prk = RdHkdf.Extract (transcript, sharedSecret);
			byte[] okm = RdHkdf.Expand (prk, Encoding.UTF8.GetBytes ("desk-remote-v1"), 128);

			byte[] c2sEnc = Slice (okm, 0, 32);
			byte[] c2sMac = Slice (okm, 32, 32);
			byte[] s2cEnc = Slice (okm, 64, 32);
			byte[] s2cMac = Slice (okm, 96, 32);

			byte[] hash = SHA256.Create ().ComputeHash (transcript);
			return isServer
				? new RdSessionKeys (s2cEnc, s2cMac, c2sEnc, c2sMac, hash)
				: new RdSessionKeys (c2sEnc, c2sMac, s2cEnc, s2cMac, hash);
		}

		/// <summary>短认证串（6 位数字）。首次配对时两端各自显示，人工核对一致即说明没有中间人。</summary>
		public string Sas ()
		{
			byte[] h;
			using (HMACSHA256 mac = new HMACSHA256 (Encoding.UTF8.GetBytes ("desk-sas"))) {
				h = mac.ComputeHash (TranscriptHash);
			}
			int v = (h [0] << 16) | (h [1] << 8) | h [2];
			return (v % 1000000).ToString ("D6");
		}

		private static byte[] Concat (byte[] a, byte[] b)
		{
			byte[] r = new byte[a.Length + b.Length];
			Buffer.BlockCopy (a, 0, r, 0, a.Length);
			Buffer.BlockCopy (b, 0, r, a.Length, b.Length);
			return r;
		}

		private static byte[] Slice (byte[] src, int offset, int len)
		{
			byte[] r = new byte[len];
			Buffer.BlockCopy (src, offset, r, 0, len);
			return r;
		}
	}

	/// <summary>
	/// 加密帧通道：AES-256-CBC + HMAC-SHA256 的 encrypt-then-MAC。
	///
	/// 帧布局：[16B IV][32B MAC][变长密文]
	/// MAC 输入 = [8B 发送序号][16B IV][密文]。序号不在线上传输、双方各自累加，
	/// 因此重放、重排、丢帧重灌都会被 MAC 直接判死——这是 CBC 模式自身不具备的保障。
	///
	/// 没用 AES-GCM 是因为要在 .NET 8 与 Xamarin.Android(Mono) 两个 BCL 上都能跑，
	/// AES+HMAC 是两边都百分之百可用且无平台差异的组合。
	/// </summary>
	public sealed class RdSecureChannel
	{
		private readonly byte[] _encKey;
		private readonly byte[] _macKey;
		private readonly byte[] _recvKey;
		private readonly byte[] _recvMacKey;
		private long _sendSeq;
		private long _recvSeq;

		public RdSecureChannel (RdSessionKeys keys)
		{
			_encKey = keys.SendEncKey;
			_macKey = keys.SendMacKey;
			_recvKey = keys.RecvEncKey;
			_recvMacKey = keys.RecvMacKey;
		}

		// 【发送序号必须与密文严格同步】序号不在线上传输，接收端按自己的计数逐帧校验，
		// 一旦乱序就判定为攻击并断连。而位图推送线程、输入回执线程、文件发送线程
		// 都会调 Seal —— 不加锁时两个线程可能拿到同一个序号，或者序号小的先算完、
		// 却后写进 socket，于是对端看到"序号乱序 → 密文校验失败 → 断开"。
		// 这是"传文件传到一半连接突然断"的根因，因此 Seal 自己必须原子。
		private readonly object _sealGate = new object ();

		public byte[] Seal (byte[] plain)
		{
			lock (_sealGate) {
				return SealCore (plain);
			}
		}

		private byte[] SealCore (byte[] plain)
		{
			byte[] iv = RdKeypair.RandomBytes (16);
			byte[] cipher;
			using (Aes aes = Aes.Create ()) {
				aes.Key = _encKey;
				aes.IV = iv;
				aes.Mode = CipherMode.CBC;
				aes.Padding = PaddingMode.PKCS7;
				using ICryptoTransform enc = aes.CreateEncryptor ();
				cipher = enc.TransformFinalBlock (plain, 0, plain.Length);
			}
			byte[] mac = ComputeMac (_macKey, _sendSeq, iv, cipher);
			_sendSeq++;

			byte[] frame = new byte[16 + 32 + cipher.Length];
			Buffer.BlockCopy (iv, 0, frame, 0, 16);
			Buffer.BlockCopy (mac, 0, frame, 16, 32);
			Buffer.BlockCopy (cipher, 0, frame, 48, cipher.Length);
			return frame;
		}

		public bool TryOpen (byte[] frame, out byte[] plain)
		{
			plain = Array.Empty<byte> ();
			if (frame == null || frame.Length < 48 + 16) return false;
			byte[] iv = new byte[16];
			byte[] mac = new byte[32];
			int cipherLen = frame.Length - 48;
			byte[] cipher = new byte[cipherLen];
			Buffer.BlockCopy (frame, 0, iv, 0, 16);
			Buffer.BlockCopy (frame, 16, mac, 0, 32);
			Buffer.BlockCopy (frame, 48, cipher, 0, cipherLen);

			byte[] expect = ComputeMac (_recvMacKey, _recvSeq, iv, cipher);
			if (!ConstantTimeEquals (expect, mac)) return false;

			try {
				using (Aes aes = Aes.Create ()) {
					aes.Key = _recvKey;
					aes.IV = iv;
					aes.Mode = CipherMode.CBC;
					aes.Padding = PaddingMode.PKCS7;
					using (ICryptoTransform dec = aes.CreateDecryptor ()) {
						plain = dec.TransformFinalBlock (cipher, 0, cipher.Length);
					}
				}
			} catch (CryptographicException) {
				return false;
			}
			_recvSeq++;
			return true;
		}

		private static byte[] ComputeMac (byte[] key, long seq, byte[] iv, byte[] cipher)
		{
			byte[]buf = new byte[8 + 16 + cipher.Length];
			for (int i = 0; i < 8; i++) {
				buf [i] = (byte)((seq >> (8 * (7 - i))) & 0xFF);
			}
			Buffer.BlockCopy (iv, 0, buf, 8, 16);
			Buffer.BlockCopy (cipher, 0, buf, 24, cipher.Length);
			using (HMACSHA256 h = new HMACSHA256 (key)) {
				return h.ComputeHash (buf);
			}
		}

		private static bool ConstantTimeEquals (byte[] a, byte[] b)
		{
			if (a == null || b == null || a.Length != b.Length) return false;
			int diff = 0;
			for (int i = 0; i < a.Length; i++) {
				diff |= a [i] ^ b [i];
			}
			return diff == 0;
		}
	}

	/// <summary>
	/// 设备码：9 位数字，被控端生成并长期稳定（重启不变）。
	///
	/// 为什么要有它 —— 原来只有「连接密码」这一个共享秘密，任何人拿到密码就能连，
	/// 且被控端无法区分来访者。设备码 + 连接密码构成双因素：
	/// 设备码标识"连哪台"，连接密码标识"谁在连"，可以对不同来访者发不同密码。
	/// </summary>
	public static class RdDeviceCode
	{
		public const int Length = 9;

		public static string New ()
		{
			byte[] buf = RdKeypair.RandomBytes (8);
			ulong v = 0;
			for (int i = 0; i < 8; i++) {
				v = (v << 8) | buf [i];
			}
			v %= 1000000000UL;
			return v.ToString ("D9");
		}

		/// <summary>去掉用户输入里的空格、横线等干扰字符。</summary>
		public static string Normalize (string s)
		{
			if (string.IsNullOrEmpty (s)) return "";
			StringBuilder sb = new StringBuilder ();
			foreach (char c in s) {
				if (c >= '0' && c <= '9') sb.Append (c);
			}
			return sb.ToString ();
		}

		public static bool IsValid (string s)
		{
			string n = Normalize (s);
			return n.Length == Length;
		}

		/// <summary>显示为 3-3-3 便于口头核对。</summary>
		public static string Format (string s)
		{
			string n = Normalize (s);
			if (n.Length != Length) return n;
			return n.Substring (0, 3) + " " + n.Substring (3, 3) + " " + n.Substring (6, 3);
		}
	}
}
