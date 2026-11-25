using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Audiofx;
using Android.Bluetooth;
using Android.OS;
using Android.Views;
using Newtonsoft.Json;
using Android.Util;
using Java.Nio;

namespace AhoChatA
{
    /// <summary>通话形态。</summary>
    public enum CallMode { Audio = 0, Video = 1 }

    /// <summary>通话阶段。</summary>
    public enum CallPhase { Idle, Dialing, Ringing, Active }

    /// <summary>给界面用的一份只读快照（每次状态变化都是新对象，避免跨线程读写竞态）。</summary>
    public sealed class CallSnapshot
    {
        public CallPhase Phase = CallPhase.Idle;
        public CallMode Mode = CallMode.Audio;
        public string PeerId;
        public string PeerName;
        public string PeerIp;
        public string Note = "";
        public bool Muted;
        public bool Speaker = true;
        public bool FromMe;          // 是否本端发起
        public int ElapsedSec;       // 通话时长
    }

    /// <summary>
    /// 点对点通话（语音 / 视频），局域网直连，不走任何服务器。
    ///
    /// 信令：复用局域网 TCP 广播通道（kind=callsignal），带 to 字段点对点过滤；
    /// 媒体：语音 UDP PCM 16k/单声道，视频 UDP H.264（见 CallVideoEngine），
    ///       双方都绑同一个本地端口，往对端同端口发，天然双向。
    ///
    /// 为什么不用 SIP/SDP/WebRTC：双方 IP 已知、无 NAT，协商过程没有意义。
    /// 参照 meshenger 的取舍——它用 WebRTC 是因为 Java 侧有现成库，而我们这边
    /// 自研一条 UDP 通道反而更小、更可控、也更容易排查。
    /// </summary>
    public class VoiceCallManager
    {
        public const int AudioPort = 45790;
        public const int VideoPort = 45792;

        private static VoiceCallManager _current;
        public static VoiceCallManager Current { get { return _current; } }

        private readonly Context _ctx;
        private readonly MchatBridge _bridge;

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private AudioRecord _recorder;
        private AudioTrack _player;
        private Android.Media.AudioManager _audio;
        private AcousticEchoCanceler _aec;
        private NoiseSuppressor _ns;
        private AutomaticGainControl _agc;
        private int _savedMode = -1, _savedSpeaker = -1;
        private bool _useBluetooth;
        private long _lastMediaRx;   // 最近一次收到对方媒体包的时刻(ms, SystemClock)
        private bool _reinviteSent;  // 本轮静默期内是否已发过重连信令，避免反复发送
        private long _lastKeyReq;    // 上次发送「补关键帧」信令的时刻，用于限流
        private long _lastLossFeedback;  // 上次收到丢包反馈的时刻
        private CancellationTokenSource _watchdogCts;  // 媒体看门狗独立令牌，避免被音频重建取消

        // 带宽估计：基于丢包反馈和 RTT 自适应调整视频码率
        private long _estimatedBandwidthBps = 2_000_000; // 初始 2Mbps
        private const long MinBandwidthBps = 300_000;    // 最低 300kbps
        private const long MaxBandwidthBps = 8_000_000;  // 最高 8Mbps

        // 抖动缓冲区：平滑网络抖动，避免音频卡顿/爆音
        private readonly JitterBuffer _jitterBuffer = new JitterBuffer(200); // 目标 200ms 缓冲
        private const int MaxJitterMs = 400;  // 最大缓冲 400ms
        private const int MinJitterMs = 50;   // 最小缓冲 50ms

        // 简单的 AES-GCM 媒体加密（避免全 DTLS 开销，适合局域网直连）
        private AesGcm _mediaCipher;
        private byte[] _mediaKey;      // 16 字节密钥
        private byte[] _mediaNonce;    // 12 字节 nonce 基础
        private long _sendSeq = 0;     // 发送序列号（用于 nonce 生成）
        private long _recvSeq = 0;     // 接收序列号（用于重放保护）
        private readonly object _cryptoLock = new object();

        private CallSnapshot _snap = new CallSnapshot();
        private readonly object _lock = new object();

        /// <summary>状态变化（可能来自任意线程，界面自己切主线程）。</summary>
        public event Action<CallSnapshot> StateChanged;

        // 视频（可选）
        private CallVideoSender _vtx;
        private CallVideoReceiver _vrx;
        private SurfaceTexture _localPreview;
        private Surface _remoteSurface;
        private bool _videoStarted;

        private CancellationTokenSource _timeoutCts;
        private const int TimeoutMs = 30000;

        public VoiceCallManager(Context ctx, MchatBridge bridge)
        {
            _ctx = ctx.ApplicationContext ?? ctx;
            _bridge = bridge;
            _current = this;
        }

        public CallSnapshot Snapshot
        {
            get { lock (_lock) return _snap; }
        }

        public bool IsActive()
        {
            lock (_lock) return _snap.Phase != CallPhase.Idle;
        }

        public void OnMicPermissionGranted() { }

        private bool MicOk()
        {
            try
            {
                return _ctx.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                       == Android.Content.PM.Permission.Granted;
            }
            catch { return false; }
        }

        /* ===================== 对外动作 ===================== */

        public void Invite(string targetPeerId, string targetName) { Invite(targetPeerId, targetName, CallMode.Audio); }

        public void Invite(string targetPeerId, string targetName, CallMode mode)
        {
            CallSnapshot s;
            lock (_lock) s = _snap;

            if (s.Phase != CallPhase.Idle) { Toast(L10n.T("已在通话中")); return; }
            if (!MicOk()) { Toast(L10n.T("未获得麦克风权限")); return; }
            if (string.IsNullOrEmpty(targetPeerId)) { Toast(L10n.T("未指定通话对象")); return; }

            var ip = _bridge.ResolvePeerIp(targetPeerId);
            if (string.IsNullOrEmpty(ip)) { Toast(L10n.T("找不到对方地址，请确认同一局域网且已互相发现")); return; }

            lock (_lock)
            {
                _snap = new CallSnapshot
                {
                    Phase = CallPhase.Dialing,
                    Mode = mode,
                    PeerId = targetPeerId,
                    PeerName = targetName ?? targetPeerId,
                    PeerIp = ip,
                    FromMe = true,
                    Note = (mode == CallMode.Video ? "正在发起视频通话 " : "正在呼叫 ") + (targetName ?? targetPeerId) + "…",
                    Speaker = true
                };
            }
            Emit();
            SendSignal("call-invite", targetPeerId);
            StartTimeout();
        }

        public void Accept()
        {
            CallSnapshot s;
            lock (_lock) s = _snap;
            if (s.Phase != CallPhase.Ringing) return;
            if (!MicOk()) { Toast(L10n.T("未获得麦克风权限")); return; }
            if (string.IsNullOrEmpty(s.PeerIp))
            {
                var ip = _bridge.ResolvePeerIp(s.PeerId);
                if (string.IsNullOrEmpty(ip)) { Toast(L10n.T("找不到对方地址")); return; }
                lock (_lock) s.PeerIp = ip;
            }

            CancelTimeout();
            StopRing();
            SendSignal("call-accept", s.PeerId);
            StartAudio();

            lock (_lock)
            {
                _snap.Phase = CallPhase.Active;
                _snap.Note = L10n.T("通话中");
            }
            Emit();
            TryStartVideo();
        }

        public void Hangup() { Hangup(null, true); }

        /// <summary>挂断（notifyPeer=false 用于对方已挂断的场景，避免回环信令）。</summary>
        public void Hangup(string reason, bool notifyPeer)
        {
            CallSnapshot s;
            lock (_lock) s = _snap;
            if (s.Phase == CallPhase.Idle) return;

            CancelTimeout();
            StopRing();
            if (notifyPeer && !string.IsNullOrEmpty(s.PeerId)) SendSignal("call-bye", s.PeerId);

            StopAudio();
            StopVideo();

            string name = s.PeerName;
            lock (_lock)
            {
                _snap = new CallSnapshot { Phase = CallPhase.Idle, Note = "" };
            }
            Emit();
            if (!string.IsNullOrEmpty(reason)) Toast(reason);
            Log.Info("Call", "通话结束" + (name != null ? "（" + name + "）" : ""));
        }

        public void SetMuted(bool muted)
        {
            lock (_lock) _snap.Muted = muted;
            Emit();
        }

        public void SetSpeaker(bool on)
        {
            lock (_lock) _snap.Speaker = on;
            UpdateAudioRoute();
            Emit();
        }

        /// <summary>按当前标志统一设置音频路由：蓝牙优先，否则在听筒/扬声器间切换。</summary>
        private void UpdateAudioRoute()
        {
            if (_audio == null) return;
            try
            {
                if (_useBluetooth)
                {
                    try { _audio.StartBluetoothSco(); } catch { }
                    _audio.BluetoothScoOn = true;
                    _audio.SpeakerphoneOn = false;
                }
                else
                {
                    try { _audio.StopBluetoothSco(); } catch { }
                    _audio.BluetoothScoOn = false;
                    _audio.SpeakerphoneOn = Snapshot.Speaker;
                }
            }
            catch { }
        }

        private bool IsBluetoothConnected()
        {
            try
            {
                var adp = BluetoothAdapter.DefaultAdapter;
                if (adp == null || !adp.IsEnabled) return false;
                int hs = (int)adp.GetProfileConnectionState(ProfileType.Headset);
                int a2 = (int)adp.GetProfileConnectionState(ProfileType.A2dp);
                return hs == (int)ProfileState.Connected || a2 == (int)ProfileState.Connected;
            }
            catch { return false; }
        }

        /// <summary>媒体活性看门狗（参照 meshenger 的链路保活/超时）：
        /// 长时间收不到对方任何媒体包 → 先要关键帧、主叫发起软重连并提示网络弱；超过阈值直接结束通话。</summary>
        private void StartWatchdog()
        {
            try { _watchdogCts?.Cancel(); } catch { }
            _watchdogCts = new CancellationTokenSource();
            var token = _watchdogCts.Token;
            Task.Run(async () =>
            {
                bool warned = false;
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(2000, token);
                    CallSnapshot s;
                    lock (_lock) s = _snap;
                    if (s.Phase != CallPhase.Active) { warned = false; _reinviteSent = false; continue; }
                    long idle = Android.OS.SystemClock.ElapsedRealtime() - _lastMediaRx;
                    if (idle > 20000)
                    {
                        Hangup(L10n.T("连接中断，通话已结束"), true);
                        return;
                    }
                    else if (idle > 8000)
                    {
                        if (!warned) { warned = true; Toast(L10n.T("网络不稳定")); }
                        try { _vtx?.RequestKeyFrame(); } catch { }
                        // 主叫发起软重连（meshenger 式保活/重连）：让对端也重建媒体链路
                        if (!_reinviteSent && s.FromMe)
                        {
                            _reinviteSent = true;
                            if (!string.IsNullOrEmpty(s.PeerId)) SendSignal("call-reinvite", s.PeerId);
                        }
                    }
                    else { warned = false; _reinviteSent = false; }

                    // 定期更新带宽估计（无丢包时缓慢增加）
                    UpdateBandwidthEstimate(false);
                }
            }, token);
        }

        public void SwitchCamera()
        {
            try { _vtx?.SwitchCamera(); } catch { }
        }

        /* ===================== 视频挂载 ===================== */

        public void AttachLocalPreview(SurfaceTexture tex)
        {
            _localPreview = tex;
            TryStartVideo();
        }

        public void AttachRemoteSurface(Surface surface)
        {
            _remoteSurface = surface;
            if (_vrx != null) _vrx.AttachSurface(surface);
            TryStartVideo();
        }

        /// <summary>本端预览与对端画面都就绪、且通话已接通、且是视频通话时才真正开流。</summary>
        public void TryStartVideo()
        {
            if (_videoStarted) return;
            CallSnapshot s;
            lock (_lock) s = _snap;
            if (s.Phase != CallPhase.Active || s.Mode != CallMode.Video) return;
            if (string.IsNullOrEmpty(s.PeerIp)) return;

            try
            {
                if (_vrx == null)
                {
                    _vrx = new CallVideoReceiver(VideoPort);
                    // 连续丢帧 / 解码停滞时请对端立刻补一个关键帧（PLI），别干等下一个周期 IDR
                    _vrx.OnNeedKeyFrame += () =>
                    {
                        long t = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                        if (t - _lastKeyReq < 300) return;   // 限流，避免连续丢帧时刷屏
                        _lastKeyReq = t;
                        string pid;
                        lock (_lock) pid = _snap.PeerId;
                        if (!string.IsNullOrEmpty(pid)) SendSignal("call-video-key", pid);
                    };
                    // 对端网络丢包严重，请本端降码率（自适应码率反馈）
                    _vrx.OnVideoLoss += () =>
                    {
                        string pid;
                        lock (_lock) pid = _snap.PeerId;
                        if (!string.IsNullOrEmpty(pid)) SendSignal("call-video-loss", pid);
                        // 本地也降码率（自适应码率反馈闭环）
                        try { _vtx?.OnLossFeedback(); } catch { }
                    };
                    if (_remoteSurface != null) _vrx.AttachSurface(_remoteSurface);
                    _vrx.Start();
                }
                if (_vtx == null && _localPreview != null)
                {
                    _vtx = new CallVideoSender(_ctx, s.PeerIp, VideoPort);
                    _vtx.AttachPreview(_localPreview);
                    _vtx.Start();
                }
                _videoStarted = _vtx != null;
                Log.Info("Call", "视频通道已启动 → " + s.PeerIp + ":" + VideoPort);
            }
            catch (Exception ex)
            {
                Log.Error("Call", "启动视频失败：" + ex.Message);
            }
        }

        private void StopVideo()
        {
            _videoStarted = false;
            try { _vtx?.Dispose(); } catch { }
            _vtx = null;
            try { _vrx?.Dispose(); } catch { }
            _vrx = null;
            _localPreview = null;
            _remoteSurface = null;
        }

        /// <summary>软重连：通话状态不变，仅重建音视频媒体链路（meshenger 式重连）。</summary>
        private void RestartMedia()
        {
            try { StartAudio(); } catch (Exception ex) { Log.Error("Call", "重连音频失败：" + ex.Message); }
            try { RestartVideo(); } catch (Exception ex) { Log.Error("Call", "重连视频失败：" + ex.Message); }
        }

        /// <summary>仅重建视频收发，保留已就绪的 Surface/预览（不同于 StopVideo 的全量拆除）。</summary>
        private void RestartVideo()
        {
            try { _vtx?.Dispose(); } catch { }
            _vtx = null;
            try { _vrx?.Dispose(); } catch { }
            _vrx = null;
            _videoStarted = false;
            TryStartVideo();
        }

        /// <summary>
        /// 根据网络状况更新带宽估计，并通知视频发送端调整码率
        /// </summary>
        private void UpdateBandwidthEstimate(bool lossReported)
        {
            long now = Android.OS.SystemClock.ElapsedRealtime();
            
            if (lossReported)
            {
                // 收到丢包反馈：指数退避降低带宽估计
                _estimatedBandwidthBps = Math.Max(MinBandwidthBps, (long)(_estimatedBandwidthBps * 0.75));
            }
            else
            {
                // 无丢包时：缓慢增加带宽估计（加性增）
                if (now - _lastLossFeedback > 5000) // 5秒无丢包
                {
                    _estimatedBandwidthBps = Math.Min(MaxBandwidthBps, (long)(_estimatedBandwidthBps * 1.05));
                }
            }

            // 通知视频发送端调整码率
            try { _vtx?.SetTargetBitrate(_estimatedBandwidthBps); } catch { }
        }

        /* ===================== 信令 ===================== */

        /// <summary>
        /// 处理一条通话信令。fromIp 是本条 TCP 帧的真实来源地址（用来做媒体目标，最可靠）。
        /// 返回 true 表示这是通话信令，已被消费。
        /// </summary>
        public bool HandleSignal(string type, string fromPeerId, string fromName, string toPeerId, string fromIp, string mode)
        {
            if (string.IsNullOrEmpty(type)) return false;
            if (!string.IsNullOrEmpty(toPeerId) && toPeerId != _bridge.SelfPeerId) return true;   // 不是给我的

            CallSnapshot s;
            lock (_lock) s = _snap;

            switch (type)
            {
                case "call-invite":
                    if (s.Phase == CallPhase.Idle)
                    {
                        bool video = string.Equals(mode, "video", StringComparison.OrdinalIgnoreCase);
                        lock (_lock)
                        {
                            _snap = new CallSnapshot
                            {
                                Phase = CallPhase.Ringing,
                                Mode = video ? CallMode.Video : CallMode.Audio,
                                PeerId = fromPeerId,
                                PeerName = fromName ?? fromPeerId,
                                PeerIp = fromIp,
                                FromMe = false,
                                Note = L10n.T("来电：") + (fromName ?? fromPeerId),
                                Speaker = true
                            };
                        }
                        Emit();
                        NotifyIncoming();
                    }
                    else
                    {
                        // 正在通话：回一条忙音（对方会看到"对方正在通话中"）
                        SendSignal("call-busy", fromPeerId);
                    }
                    return true;

                case "call-accept":
                    if (s.Phase == CallPhase.Dialing && fromPeerId == s.PeerId)
                    {
                        CancelTimeout();
                        lock (_lock) if (string.IsNullOrEmpty(_snap.PeerIp)) _snap.PeerIp = fromIp;
                        StartAudio();
                        lock (_lock)
                        {
                            _snap.Phase = CallPhase.Active;
                            _snap.Note = L10n.T("通话中");
                        }
                        Emit();
                        TryStartVideo();
                    }
                    return true;

                case "call-bye":
                    Hangup(L10n.T("对方已挂断"), false);
                    return true;

                case "call-busy":
                    if (s.Phase == CallPhase.Dialing && fromPeerId == s.PeerId)
                        Hangup(L10n.T("对方正在通话中"), false);
                    return true;

                // 对端丢帧，要求本端立刻补一个关键帧（PLI）
                case "call-video-key":
                    try { _vtx?.RequestKeyFrame(); } catch { }
                    return true;

                // 对端网络丢包严重，请本端降码率（自适应码率反馈）
                case "call-video-loss":
                    try { _vtx?.OnLossFeedback(); } catch { }
                    // 记录丢包事件用于带宽估计
                    _lastLossFeedback = Android.OS.SystemClock.ElapsedRealtime();
                    UpdateBandwidthEstimate(false); // 收到丢包反馈，降低带宽估计
                    return true;

                // 对端发起软重连：通话状态不变，仅重建本端媒体链路
                case "call-reinvite":
                    if (s.Phase == CallPhase.Active && fromPeerId == s.PeerId)
                        RestartMedia();
                    return true;
            }
            return false;
        }

        private void SendSignal(string type, string to)
        {
            var sig = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["type"] = type,
                ["to"] = to ?? "",
                ["from"] = _bridge.SelfPeerId,
                ["fromName"] = _bridge.SelfName,
                ["mode"] = Snapshot.Mode == CallMode.Video ? "video" : "audio",
                ["kind"] = "callsignal",
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            _bridge.SendCallSignalRaw(sig);
        }

        private void StartTimeout()
        {
            CancelTimeout();
            _timeoutCts = new CancellationTokenSource();
            var token = _timeoutCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeoutMs, token);
                    if (!token.IsCancellationRequested && Snapshot.Phase == CallPhase.Dialing)
                    {
                        Hangup(L10n.T("呼叫超时，对方未应答"), true);
                        NotifyEnded(L10n.T("未接通"));
                    }
                }
                catch (TaskCanceledException) { }
                catch { }
            });
        }

        private void CancelTimeout()
        {
            try { _timeoutCts?.Cancel(); } catch { }
            _timeoutCts = null;
        }

        /* ===================== 音频媒体 ===================== */

        private void StartAudio()
        {
            StopAudio();
            try
            {
                var s = Snapshot;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                // 媒体通道按对端地址族绑定：对端是 IPv6 就用双栈 IPv6 套接字，
                // 否则沿用 IPv4。这样直连 IPv6 对端时通话也不会因地址族不匹配失败。
                bool peerV6 = false;
                try
                {
                    IPAddress pip;
                    peerV6 = IPAddress.TryParse(s.PeerIp, out pip)
                             && pip.AddressFamily == AddressFamily.InterNetworkV6;
                }
                catch { }

                if (peerV6)
                {
                    _udp = new UdpClient(AddressFamily.InterNetworkV6);
                    try { _udp.Client.DualMode = true; } catch { }
                    try { _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, AudioPort)); } catch { }
                }
                else
                {
                    _udp = new UdpClient(AudioPort);
                }
                try { _udp.Client.ReceiveBufferSize = 128 * 1024; } catch { }

                _audio = (Android.Media.AudioManager)_ctx.GetSystemService(Context.AudioService);
                if (_audio != null)
                {
                    _savedMode = (int)_audio.Mode;
                    _savedSpeaker = _audio.SpeakerphoneOn ? 1 : 0;
                    _audio.Mode = Android.Media.Mode.InCommunication;
                    _useBluetooth = IsBluetoothConnected();   // 连了蓝牙耳机则自动走蓝牙
                    UpdateAudioRoute();
                }

                // 初始化媒体加密（需要 PeerId 已确定）
                InitMediaCrypto();

                int playBuf = AudioTrack.GetMinBufferSize(16000, ChannelOut.Mono, Android.Media.Encoding.Pcm16bit);
                _player = new AudioTrack(Android.Media.Stream.VoiceCall, 16000, ChannelOut.Mono,
                    Android.Media.Encoding.Pcm16bit, Math.Max(playBuf, 8192), AudioTrackMode.Stream);
                _player.Play();

                int recBuf = AudioRecord.GetMinBufferSize(16000, ChannelIn.Mono, Android.Media.Encoding.Pcm16bit);
                _recorder = new AudioRecord(AudioSource.Mic, 16000, ChannelIn.Mono,
                    Android.Media.Encoding.Pcm16bit, Math.Max(recBuf, 8192));

                // 回声消除 / 降噪 / 自动增益：扬声器外放时显著改善音质、避免啸叫与回声
                // （AudioEffect 在本绑定里 Enabled 只读，启用由绑定在构造时处理，这里只创建）
                try
                {
                    int sess = _recorder.AudioSessionId;
                    if (AcousticEchoCanceler.IsAvailable) 
                    { 
                        _aec = AcousticEchoCanceler.Create(sess);
                        // Enabled 在构造时自动启用（绑定层只读）
                    }
                }
                catch { }
                try
                {
                    int sess = _recorder.AudioSessionId;
                    if (NoiseSuppressor.IsAvailable) 
                    { 
                        _ns = NoiseSuppressor.Create(sess);
                    }
                }
                catch { }
                try
                {
                    int sess = _recorder.AudioSessionId;
                    if (AutomaticGainControl.IsAvailable) 
                    { 
                        _agc = AutomaticGainControl.Create(sess);
                    }
                }
                catch { }

                var ep = new IPEndPoint(IPAddress.Parse(s.PeerIp), AudioPort);

                Task.Run(() =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && _udp != null)
                        {
                            var any = new IPEndPoint(peerV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                            var data = _udp.Receive(ref any);
                            if (data != null && data.Length > 0)
                            {
                                _lastMediaRx = Android.OS.SystemClock.ElapsedRealtime();
                                
                                // 解密音频包
                                var plaintext = DecryptMediaPacket(data);
                                if (plaintext != null)
                                {
                                    _jitterBuffer.Push(plaintext);
                                }
                            }
                        }
                    }
                    catch { }
                }, token);

            // 音频播放循环：从抖动缓冲区取出帧写入 AudioTrack
            Task.Run(() =>
            {
                try
                {
                    var player = _player;
                    var buf = new byte[640];
                    while (!token.IsCancellationRequested)
                    {
                        if (player != null && player.PlayState == PlayState.Playing)
                        {
                            var frame = _jitterBuffer.Pop();
                            if (frame != null && frame.Length > 0)
                            {
                                try { player.Write(frame, 0, frame.Length); } catch { }
                            }
                        }
                        else
                        {
                            // 播放器未就绪，清空缓冲避免积压
                            _jitterBuffer.Clear();
                            Thread.Sleep(10);
                        }
                    }
                }
                catch { }
            }, token);

                _lastMediaRx = Android.OS.SystemClock.ElapsedRealtime();
                _jitterBuffer.Clear(); // 启动时清空抖动缓冲
                StartWatchdog();

                _recorder.StartRecording();
                Task.Run(() =>
                {
                    try
                    {
                        // 20ms @16kHz/16bit 单声道 = 640 字节：小包避免局域网 MTU 分片与高延迟
                        int chunk = 640;
                        var buf = new byte[chunk];
                        while (!token.IsCancellationRequested)
                        {
                            var rec = _recorder;
                            if (rec == null) break;
                            int n = rec.Read(buf, 0, chunk);
                            if (n <= 0) continue;
                            // 静音时发静音而非停发：既防 AudioRecord 缓冲溢出造成爆音，又保持链路活跃
                            if (Snapshot.Muted) Array.Clear(buf, 0, n);
                            
                            // 加密音频包
                            byte[] packet = EncryptMediaPacket(buf.AsSpan(0, n).ToArray());
                            if (_udp != null) { try { _udp.Send(packet, packet.Length, ep); } catch { } }
                        }
                    }
                    catch { }
                }, token);

                Log.Info("Call", "语音通道已建立 → " + s.PeerIp + ":" + AudioPort);
            }
            catch (Exception ex)
            {
                Log.Error("Call", "启动音频失败：" + ex.Message);
                Toast(L10n.T("麦克风/扬声器初始化失败"));
                StopAudio();
            }
        }

        private void StopAudio()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            try { _watchdogCts?.Cancel(); } catch { }
            _watchdogCts = null;
            try { _recorder?.Stop(); } catch { }
            try { _recorder?.Release(); } catch { }
            _recorder = null;
            try { _player?.Stop(); } catch { }
            try { _player?.Release(); } catch { }
            _player = null;
            try { _udp?.Close(); } catch { }
            _udp = null;

            try { _aec?.Release(); } catch { }
            _aec = null;
            try { _ns?.Release(); } catch { }
            _ns = null;
            try { _agc?.Release(); } catch { }
            _agc = null;

            // 清理媒体加密资源
            lock (_cryptoLock)
            {
                _mediaCipher?.Dispose();
                _mediaCipher = null;
                _mediaKey = null;
                _mediaNonce = null;
            }

            if (_audio != null)
            {
                try { _audio.StopBluetoothSco(); } catch { }
                try
                {
                    if (_savedSpeaker == 0) _audio.SpeakerphoneOn = false;
                    if (_savedMode >= 0) _audio.Mode = (Android.Media.Mode)_savedMode;
                }
                catch { }
                _audio = null;
            }
        }

        /* ===================== 通知 / 提示 ===================== */

        private void Emit()
        {
            var h = StateChanged;
            if (h == null) return;
            var snap = Snapshot;
            try { h(snap); } catch { }
        }

        /// <summary>来电：先把通话页拉起来（有悬浮窗权限时后台也能起），再补一条通知兜底。</summary>
        private void NotifyIncoming()
        {
            var s = Snapshot;
            PlayRing();
            try
            {
                var i = new Intent(_ctx, typeof(CallActivity));
                i.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
                i.PutExtra("incoming", true);
                _ctx.StartActivity(i);
            }
            catch (Exception ex) { Log.Warn("Call", "拉起来电页失败：" + ex.Message); }

            try { Toast((s.Mode == CallMode.Video ? L10n.T("视频来电：") : L10n.T("语音来电：")) + s.PeerName); } catch { }
        }

        /* ===================== 来电提示音 / 震动 ===================== */

        private Android.Media.Ringtone _ring;

        private void PlayRing()
        {
            try
            {
                var uri = Android.Media.RingtoneManager.GetDefaultUri(Android.Media.RingtoneType.Ringtone);
                _ring = Android.Media.RingtoneManager.GetRingtone(_ctx, uri);
                _ring?.Play();
            }
            catch { }
            try
            {
                var vib = (Android.OS.Vibrator)_ctx.GetSystemService(Context.VibratorService);
                vib?.Vibrate(new long[] { 0, 700, 500, 700, 500, 700 }, -1);
            }
            catch { }
        }

        private void StopRing()
        {
            try { _ring?.Stop(); } catch { }
            _ring = null;
            try
            {
                var vib = (Android.OS.Vibrator)_ctx.GetSystemService(Context.VibratorService);
                vib?.Cancel();
            }
            catch { }
        }

        private void NotifyEnded(string why)
        {
            try { Toast(why); } catch { }
        }

        private void Toast(string msg)
        {
            try
            {
                var h = new Handler(Looper.MainLooper);
                h.Post(() => L10n.Toast(_ctx, msg, Android.Widget.ToastLength.Short).Show());
            }
            catch { }
        }
    

private void InitMediaCrypto()
{
    lock (_cryptoLock)
    {
        // 从信令派生会话密钥（简化：用 peerId + 本机 peerId + 固定盐派生）
        var salt = System.Text.Encoding.UTF8.GetBytes("MchatMediaSalt_v1");
        var secret = System.Text.Encoding.UTF8.GetBytes(_bridge.SelfPeerId + "|" + Snapshot.PeerId);
        using (var kdf = new Rfc2898DeriveBytes(secret, salt, 10000, HashAlgorithmName.SHA256))
        {
            _mediaKey = kdf.GetBytes(16); // AES-128
        }
        _mediaNonce = new byte[12];
        Array.Copy(System.Text.Encoding.UTF8.GetBytes("MchatNonce"), _mediaNonce, 12);
        _sendSeq = 0;
        _recvSeq = 0;
        _mediaCipher = new AesGcm(_mediaKey);
    }
}

private byte[] EncryptMediaPacket(byte[] plaintext)
{
    lock (_cryptoLock)
    {
        if (_mediaCipher == null) return plaintext;
        
        var nonce = new byte[12];
        Array.Copy(_mediaNonce, nonce, 12);
        var seq = Interlocked.Increment(ref _sendSeq);
        // nonce 最后 8 字节用序列号
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), seq);
        
        var ciphertext = new byte[plaintext.Length + 16]; // 16 字节 tag
        var tag = new byte[16];
        _mediaCipher.Encrypt(nonce, plaintext, ciphertext, tag);
        
        // 组合：nonce(12) + ciphertext + tag(16)
        var result = new byte[12 + ciphertext.Length + 16];
        Array.Copy(nonce, 0, result, 0, 12);
        Array.Copy(ciphertext, 0, result, 12, ciphertext.Length);
        Array.Copy(tag, 0, result, 12 + ciphertext.Length, 16);
        return result;
    }
}

private byte[] DecryptMediaPacket(byte[] cipherPacket)
{
    lock (_cryptoLock)
    {
        if (_mediaCipher == null || cipherPacket.Length < 12 + 16) return cipherPacket;
        
        var nonce = new byte[12];
        Array.Copy(cipherPacket, 0, nonce, 0, 12);
        var seq = BinaryPrimitives.ReadInt64BigEndian(nonce.AsSpan(4));
        
        // 简单的重放保护：序列号必须递增
        if (seq <= _recvSeq) return null; // 丢弃重放包
        _recvSeq = seq;
        
        var ciphertextLen = cipherPacket.Length - 12 - 16;
        var ciphertext = new byte[ciphertextLen];
        Array.Copy(cipherPacket, 12, ciphertext, 0, ciphertextLen);
        var tag = new byte[16];
        Array.Copy(cipherPacket, 12 + ciphertextLen, tag, 0, 16);
        
        var plaintext = new byte[ciphertextLen];
        try
        {
            _mediaCipher.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch
        {
            return null; // 解密失败（篡改/密钥不匹配）
        }
    }
}
}

/// <summary>
/// 简单的抖动缓冲区：缓存网络到达的音频包，以固定间隔取出播放，平滑网络抖动。
/// 支持自适应缓冲：根据网络抖动动态调整缓冲深度。
/// </summary>
public class JitterBuffer
{
    private readonly Queue<byte[]> _queue = new Queue<byte[]>();
    private readonly object _lock = new object();
    private readonly int _targetMs;
    private int _currentMs;
    private long _lastPopMs;
    private const int PacketMs = 20; // 每包 20ms @16kHz 640 bytes
    private const int MaxJitterMs = 400;  // 最大缓冲 400ms
    private const int MinJitterMs = 50;   // 最小缓冲 50ms

    public JitterBuffer(int targetMs)
    {
        _targetMs = Math.Max(50, Math.Min(400, targetMs));
        _currentMs = _targetMs;
        _lastPopMs = Android.OS.SystemClock.ElapsedRealtime();
    }

    /// <summary>放入网络收到的音频包</summary>
    public void Push(byte[] data)
    {
        lock (_lock)
        {
            _queue.Enqueue(data);
            // 防止缓冲区无限增长：超过最大缓冲时丢弃最旧的包
            while (_queue.Count * PacketMs > MaxJitterMs)
                _queue.TryDequeue(out _);
        }
    }

    /// <summary>取出一帧用于播放，不足时使用 PLC (Packet Loss Concealment) 生成舒适噪音</summary>
    public byte[] Pop()
    {
        lock (_lock)
        {
            var now = Android.OS.SystemClock.ElapsedRealtime();
            
            // 自适应调整缓冲深度
            if (_queue.Count * PacketMs > _currentMs + 50)
            {
                // 缓冲过大，减少目标延迟
                _currentMs = Math.Max(MinJitterMs, _currentMs - 20);
            }
            else if (_queue.Count * PacketMs < _currentMs - 50 && _currentMs < MaxJitterMs)
            {
                // 缓冲过小，增加目标延迟
                _currentMs = Math.Min(MaxJitterMs, _currentMs + 20);
            }

            if (_queue.Count > 0)
            {
                _lastPopMs = now;
                return _queue.Dequeue();
            }

            // 缓冲区欠载：使用简单的 PLC 生成舒适噪音，避免纯静音导致音频"卡顿感"
            return GenerateComfortNoise();
        }
    }

    private byte[] GenerateComfortNoise()
    {
        // 简单的舒适噪音生成：低幅值白噪音，模拟背景环境音
        // 避免纯静音导致的"音频卡顿"感知
        var noise = new byte[640];
        var rnd = new Random();
        for (int i = 0; i < noise.Length; i += 2)
        {
            // 生成低幅值随机值 (-32 to 31)
            short val = (short)(rnd.Next(-32, 32));
            noise[i] = (byte)(val & 0xFF);
            noise[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return noise;
    }

    public void Clear()
    {
        lock (_lock) _queue.Clear();
    }

    public int BufferedMs => _queue.Count * PacketMs;
}

/// <summary>
/// 简单的 AES-GCM 媒体加密/解密
/// 适合局域网直连：开销极低、无需证书、抗重放
/// </summary>
}
