// ============================================================
// 影音 · ExoPlayer 2.x 封装（参考 C:\root\code\TV 的 ExoEngine / Media3 逻辑）
//
// 用 ExoPlayer 替换系统 VideoView（系统 MediaPlayer 对 HLS/m3u8 支持差，
// 正是「点开剧集黑屏一直加载」的根因）。直连已解析的直链，靠浏览器 UA + 跨协议重定向
// 拿到 CDN 数据；缓冲交给 DefaultLoadControl 内存前瞻缓冲（非持久，关闭即清）。
// 说明：本项目是经典 Xamarin.Android（monoandroid13.0），Media3 与 ExoPlayer 2.19 均已不支持
//       monoandroid，故固定到最后一个仍支持 monoandroid 的 ExoPlayer 2.16.1。
// ============================================================
using System;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Com.Google.Android.Exoplayer2;
using Com.Google.Android.Exoplayer2.Source;
using Com.Google.Android.Exoplayer2.Source.Hls;
using Com.Google.Android.Exoplayer2.UI;
using Com.Google.Android.Exoplayer2.Upstream;
using AhoChatA.Vod;

namespace AhoChatA
{
    public class ExoVideoPlayer : Java.Lang.Object
    {
        // ExoPlayer.Player 的播放状态常量（Player 类型不在本绑定的 Core 程序集，直接用稳定常量值）
        const int StateReady = 3, StateEnded = 4;

        readonly Context _ctx;
        readonly SimpleExoPlayer _p;
        readonly PlayerView _view;
        readonly Handler _h;
        readonly Java.Lang.Runnable _poll;
        bool _prepared, _ended, _errored, _pollRunning;
        int _pendingResume;
        // 失败自动重试一次：本地缓存代理偶发异常时，绕开代理直连原地址。
        string _lastUrl;
        bool? _lastHls;
        int _retryCount;

        public event EventHandler Prepared;
        public event EventHandler Completion;
        public event EventHandler Error;

        public ExoVideoPlayer(Context ctx)
        {
            _ctx = ctx;
            _view = new PlayerView(ctx);
            _view.UseController = false;
            _view.SetBackgroundColor(Color.Black);
            _view.ResizeMode = AspectRatioFrameLayout.ResizeModeFit;
            try
            {
                // 缓冲策略：1.5s 即可起播（秒开），之后持续囤到 240s 抗抖动；回看缓冲 60s
                var load = new DefaultLoadControl.Builder()
                    .SetBufferDurationsMs(20000, 240000, 1500, 5000)
                    .SetBackBuffer(60000, true)
                    .SetPrioritizeTimeOverSizeThresholds(true)
                    .Build();
                // 解码器自动回退：某格式硬件解不了时自动退软解，无需整段重加载
                var renderers = new DefaultRenderersFactory(ctx).SetEnableDecoderFallback(true);
                _p = new SimpleExoPlayer.Builder(ctx, renderers)
                    .SetLoadControl(load)
                    .Build();
                _p.PlayWhenReady = true;
                _view.Player = _p;
            }
            catch (Exception e)
            {
                Android.Util.Log.Debug("MchatVod", "exo build err " + e.Message);
                _p = null;
            }
            _h = new Handler(Looper.MainLooper);
            _poll = new Java.Lang.Runnable(Poll);
        }

        public View SurfaceView { get { return _view; } }

        public void Load(string url, long resumeMs, bool? hlsHint = null)
        {
            if (_p == null) { _errored = true; Error?.Invoke(this, EventArgs.Empty); return; }
            _lastUrl = url; _lastHls = hlsHint; _retryCount = 0;
            PrepareSource(url, resumeMs, hlsHint, true);
        }

        /// <summary>真正建源；useProxy=false 时绕开本地缓存代理直连原地址（失败重试用）。</summary>
        private void PrepareSource(string url, long resumeMs, bool? hlsHint, bool useProxy)
        {
            _prepared = false; _ended = false; _errored = false; _pendingResume = (int)resumeMs;
            try
            {
                var origLow = string.IsNullOrEmpty(url) ? "" : url.ToLowerInvariant();
                bool isHls = hlsHint ?? origLow.Contains(".m3u8");
                bool localLike = origLow.StartsWith("file:") || origLow.StartsWith("/") ||
                    origLow.Contains("://127.0.0.1") || origLow.Contains("://localhost");
                string finalUrl = url;
                // 远端直链走本地回环缓存代理：多线程落盘缓冲 + 精确字节统计（遥测用）。
                // 失败重试（useProxy=false）或代理不可用时直连原地址。
                if (useProxy && !localLike && origLow.StartsWith("http") && VideoCacheProxy.Instance.Enabled)
                    finalUrl = VideoCacheProxy.ProxyPlayUrl(url);
                if (localLike && !url.Contains("://")) finalUrl = "file://" + url;

                var plain = new DefaultDataSource.Factory(_ctx, VodExoCache.HttpFactory());
                var uri = Android.Net.Uri.Parse(finalUrl);
                IMediaSource src = isHls
                    ? new HlsMediaSource.Factory(plain).SetAllowChunklessPreparation(true).CreateMediaSource(uri)
                    : new ProgressiveMediaSource.Factory(plain).CreateMediaSource(uri);

                _p.Stop();
                _p.SetMediaSource(src);
                _p.Prepare();
                _p.PlayWhenReady = true;
            }
            catch (Exception e)
            {
                Android.Util.Log.Debug("MchatVod", "exo load err " + e.Message);
                _errored = true;
                Error?.Invoke(this, EventArgs.Empty);
                return;
            }
            StartPoll();
        }

        void StartPoll() { if (_pollRunning) return; _pollRunning = true; _h.PostDelayed(_poll, 120); }

        void Poll()
        {
            if (!_pollRunning) return;
            try
            {
                if (_p != null)
                {
                    if (!_prepared && _p.PlayerError == null && _p.PlaybackState == StateReady)
                    {
                        _prepared = true;
                        if (_pendingResume > 0) { try { _p.SeekTo(_pendingResume); } catch { } _pendingResume = 0; }
                        Prepared?.Invoke(this, EventArgs.Empty);
                    }
                    else if (_p.PlayerError != null && !_errored)
                    {
                        _errored = true;
                        // 第一次失败：绕开本地缓存代理直连原地址重试，不打扰用户；
                        // 第二次仍失败才上报，由 Activity 给出明确提示。
                        if (_retryCount < 1 && !string.IsNullOrEmpty(_lastUrl))
                        {
                            _retryCount++;
                            Android.Util.Log.Info("MchatVod", "播放失败，绕开缓存代理重试：" + _lastUrl);
                            PrepareSource(_lastUrl, _pendingResume > 0 ? _pendingResume : 0, _lastHls, false);
                        }
                        else Error?.Invoke(this, EventArgs.Empty);
                    }
                    else if (_p.PlaybackState == StateEnded && !_ended)
                    {
                        _ended = true;
                        Completion?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch { }
            if (_pollRunning) _h.PostDelayed(_poll, 200);
        }

        public void Start() { try { if (_p != null) _p.PlayWhenReady = true; } catch { } }
        public void Pause() { try { if (_p != null) _p.PlayWhenReady = false; } catch { } }
        public void Stop() { try { if (_p != null) { _p.Stop(); _p.ClearMediaItems(); } } catch { } }
        public void SeekTo(long ms) { try { if (_p != null) _p.SeekTo(Math.Max(0, ms)); } catch { } }
        public long PositionMs { get { try { return _p == null ? 0 : _p.CurrentPosition; } catch { return 0; } } }
        public long BufferedPositionMs { get { try { return _p == null ? 0 : _p.BufferedPosition; } catch { return 0; } } }
        public long DurationMs { get { try { long d = _p == null ? 0 : _p.Duration; return d < 0 ? 0 : d; } catch { return 0; } } }
        public bool IsPlaying { get { try { return _p != null && _p.IsPlaying; } catch { return false; } } }
        public int VideoWidth { get { try { return _p == null ? 0 : _p.VideoSize.Width; } catch { return 0; } } }
        public int VideoHeight { get { try { return _p == null ? 0 : _p.VideoSize.Height; } catch { return 0; } } }
        public void SetSpeed(float r) { try { if (_p != null) _p.SetPlaybackSpeed(r); } catch { } }
        public void SetCrop(bool crop)
        {
            try { _view.ResizeMode = crop ? AspectRatioFrameLayout.ResizeModeZoom : AspectRatioFrameLayout.ResizeModeFit; }
            catch { }
        }
        public void Release()
        {
            try { _pollRunning = false; _h.RemoveCallbacks(_poll); _p?.Release(); } catch { }
        }
    }
}
