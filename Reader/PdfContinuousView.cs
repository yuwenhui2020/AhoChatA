// ============================================================
// 阅读模块 · PDF 连续纵向拼页视图
//
// 行为（用户明确要求）：
//   · 打开 PDF 后把所有页「自动拼页」成一条可上下滑动的长卷，
//     但仍然「每页内容单独显示、互不干扰」——每一页都是独立的 ImageView / Bitmap，
//     不被拼成一张大长图，也不做文本重排；
//   · 页与页之间**间距为 0**（无 margin、无 padding、无分隔线），
//     整卷连续无缝，可平滑上下滑动浏览；
//   · 内存安全：只保留当前可视窗口（前后各缓存 buffer 页）的位图，
//     滚出窗口的页立即回收，避免几百页 PDF 直接 OOM；
//   · 页图命中 PdfCache 磁盘缓存，不重复打开/渲染 PDF；
//   · 点击屏幕任意处：呼出 / 收起工具栏（连续浏览下不再用左右点击翻页）。
//
// 转译自 Areada 1.1.4 的「连续页模式」，但按本项目约束用系统 PdfRenderer 逐页位图实现。
// ============================================================
using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace AhoChatA.Reader
{
    public sealed class PdfContinuousView : FrameLayout
    {
        private ScrollView _sv;
        private LinearLayout _col;

        private string _id, _path;
        private int _count;
        private int _bg = unchecked((int)0xFFF4F5F7);

        private ImageView[] _views;
        private Bitmap[] _bmps;
        private int[] _pageGen;          // 每页加载代号，回收时自增以作废在途异步回调
        private int[] _tops;             // 每页顶部偏移
        private int[] _heights;          // 每页显示高度
        private double[] _aspects;       // 每页宽高比（PDF point）
        private int _totalH;
        private int _builtWidth;

        private int _winFirst = -1, _winLast = -1;
        private int _lastReported = -1;
        private int _decW;
        private int _loadToken;          // 每次 Load 自增，作废旧 Load 的后置 Build
        private int _buildToken;
        private int _pendingStart = -1;
        private bool _built;

        /// <summary>当前最顶部可见页变化时回调（用于进度保存 / 标题更新）。</summary>
        public Action<int> PageChanged;
        /// <summary>点击屏幕（非拖拽）回调，用于呼出 / 收起工具栏。</summary>
        public Action Tap;

        public PdfContinuousView(Context c) : base(c) { Init(c); }
        public PdfContinuousView(Context c, IAttributeSet a) : base(c, a) { Init(c); }

        private void Init(Context c)
        {
            _sv = new ScrollView(c);
            _sv.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            _sv.VerticalScrollBarEnabled = false;
            _sv.OverScrollMode = OverScrollMode.Never;
            _sv.FillViewport = true;

            _col = new LinearLayout(c) { Orientation = Orientation.Vertical };
            _col.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            _col.ShowDividers = ShowDividers.None;
            _sv.AddView(_col);

            AddView(_sv);

            _sv.ScrollChange += (s, e) => OnScroll();
            _sv.SetOnTouchListener(new TapGuard(this));
            _col.ViewTreeObserver.GlobalLayout += (s, e) => MaybeRelayout();
        }

        public bool IsLoadedFor(string id) => _built && _id == id;

        /* ------------------------------------------------------------
         * 外部加载入口
         * ------------------------------------------------------------ */
        public void Load(string pdfPath, string bookId, int pageCount, int bgArgb, int startIdx)
        {
            Release();
            _path = pdfPath;
            _id = bookId;
            _count = Math.Max(0, pageCount);
            _bg = bgArgb;
            _pendingStart = Clamp(startIdx);
            Recolor();
            if (_count == 0) return;

            _buildToken = ++_loadToken;
            int token = _buildToken;

            // 后台读取每页宽高比（仅读取元数据，不渲染），避免 UI 卡顿
            Task.Run(() =>
            {
                double[] aspects = new double[_count];
                for (int i = 0; i < _count; i++) aspects[i] = 1.4;
                try
                {
                    using (var eng = new PdfEngine(_path))
                        for (int i = 0; i < _count; i++) aspects[i] = eng.PageAspect(i);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("MchatPdf", "读取页比例失败: " + ex.Message);
                }
                Post(() =>
                {
                    if (token == _buildToken) Build(aspects);
                });
            });
        }

        private void Build(double[] aspects)
        {
            if (_col == null || _count == 0) return;

            _col.RemoveAllViews();
            int w = _sv.Width > 0 ? _sv.Width : ReaderLibrary.ScreenWidth;
            _builtWidth = w;
            _decW = w;

            _aspects = aspects;
            _views = new ImageView[_count];
            _bmps = new Bitmap[_count];
            _pageGen = new int[_count];
            _tops = new int[_count];
            _heights = new int[_count];

            int top = 0;
            for (int i = 0; i < _count; i++)
            {
                double a = (_aspects != null && i < _aspects.Length && _aspects[i] > 0) ? _aspects[i] : 1.4;
                int h = (int)Math.Max(1, Math.Round(w * a));
                _heights[i] = h;
                _tops[i] = top;
                top += h;

                var iv = new ImageView(Context);
                iv.LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, h);
                iv.SetScaleType(ImageView.ScaleType.FitCenter);
                iv.SetBackgroundColor(new Color(_bg));
                _col.AddView(iv);
                _views[i] = iv;
            }
            _totalH = top;
            _built = true;
            _winFirst = -1;
            _winLast = -1;
            _lastReported = -1;

            Post(() =>
            {
                UpdateWindow(true);
                if (_pendingStart >= 0)
                {
                    _sv.ScrollTo(0, _tops[Clamp(_pendingStart)]);
                    _pendingStart = -1;
                    UpdateWindow(true);
                }
                PageChanged?.Invoke(CurrentPage());
            });
        }

        /// <summary>跳转到第 idx 页顶部（用于恢复进度 / 目录跳转 / 上下页按钮）。</summary>
        public void ShowAtPage(int idx)
        {
            if (_count == 0) { _pendingStart = Clamp(idx); return; }
            Post(() =>
            {
                _sv.ScrollTo(0, _tops[Clamp(idx)]);
                UpdateWindow(true);
                _lastReported = -1;
                PageChanged?.Invoke(CurrentPage());
            });
        }

        public int CurrentPage()
        {
            if (_count == 0 || _sv == null) return 0;
            return FindPage(_sv.ScrollY);
        }

        public void SetPageBg(int argb)
        {
            _bg = argb;
            Recolor();
        }

        private void Recolor()
        {
            try
            {
                SetBackgroundColor(new Color(_bg));
                _sv?.SetBackgroundColor(new Color(_bg));
                _col?.SetBackgroundColor(new Color(_bg));
                if (_views != null)
                    for (int i = 0; i < _views.Length; i++)
                        _views[i]?.SetBackgroundColor(new Color(_bg));
            }
            catch { }
        }

        /* ------------------------------------------------------------
         * 窗口化加载：只保留可视区域附近的页图，其余回收
         * ------------------------------------------------------------ */
        private void OnScroll()
        {
            if (!_built) return;
            UpdateWindow(false);
            int pg = CurrentPage();
            if (pg != _lastReported) { _lastReported = pg; PageChanged?.Invoke(pg); }
        }

        private void UpdateWindow(bool force)
        {
            if (!_built || _count == 0 || _sv == null) return;
            int vh = _sv.Height;
            if (vh <= 0) vh = Context.Resources.DisplayMetrics.HeightPixels;

            int sy = _sv.ScrollY;
            int visTop = sy;
            int visBot = sy + vh;

            int first = FindPage(visTop - 50);
            int last = FindPage(visBot + 50);
            if (first < 0) first = 0;
            if (last < 0) last = _count - 1;

            const int buf = 2;
            first = Math.Max(0, first - buf);
            last = Math.Min(_count - 1, last + buf);

            if (!force && first == _winFirst && last == _winLast) return;
            _winFirst = first;
            _winLast = last;

            // 回收窗口外的页
            for (int i = 0; i < _count; i++)
            {
                if ((i < first || i > last) && _bmps[i] != null)
                {
                    var v = _views[i];
                    if (v != null) v.SetImageBitmap(null);
                    Recycle(i);
                }
            }
            // 加载窗口内的页
            for (int i = first; i <= last; i++)
                if (_bmps[i] == null) LoadPageAsync(i);
        }

        private void LoadPageAsync(int i)
        {
            if (i < 0 || i >= _count) return;
            int myGen = _pageGen[i];
            string id = _id, path = _path;
            int width = _decW;
            Task.Run(() =>
            {
                Bitmap bmp = null;
                try
                {
                    byte[] jpg = PdfCache.PageJpeg(id, path, i, width, 1f);
                    bmp = BitmapFactory.DecodeByteArray(jpg, 0, jpg.Length);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("MchatPdf", "页图解码失败 p" + i + ": " + ex.Message);
                }
                var v = _views[i];
                if (v == null || bmp == null)
                {
                    if (bmp != null) { try { bmp.Recycle(); bmp.Dispose(); } catch { } }
                    return;
                }
                v.Post(() =>
                {
                    if (_pageGen[i] != myGen || i < _winFirst || i > _winLast || v != _views[i])
                    {
                        try { bmp.Recycle(); bmp.Dispose(); } catch { }
                        return;
                    }
                    if (_bmps[i] != null && !ReferenceEquals(_bmps[i], bmp))
                    {
                        try { _bmps[i].Recycle(); _bmps[i].Dispose(); } catch { }
                    }
                    _bmps[i] = bmp;
                    v.SetImageBitmap(bmp);
                });
            });
        }

        private void Recycle(int i)
        {
            _pageGen[i]++;
            if (_bmps[i] != null)
            {
                try { _bmps[i].Recycle(); _bmps[i].Dispose(); } catch { }
                _bmps[i] = null;
            }
        }

        /* ------------------------------------------------------------
         * 旋转 / 宽度变化：按新宽度重算每页高度并重新加载
         * ------------------------------------------------------------ */
        private void MaybeRelayout()
        {
            if (!_built || _sv == null || _count == 0) return;
            int w = _sv.Width;
            if (w <= 0 || w == _builtWidth) return;
            _builtWidth = w;
            _decW = w;

            int cur = CurrentPage();
            int top = 0;
            for (int i = 0; i < _count; i++)
            {
                double a = (_aspects != null && i < _aspects.Length && _aspects[i] > 0) ? _aspects[i] : 1.4;
                int h = (int)Math.Max(1, Math.Round(w * a));
                _heights[i] = h;
                _tops[i] = top;
                top += h;
                var iv = _views[i];
                if (iv != null)
                    iv.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, h);
            }
            _totalH = top;

            // 旧宽度的位图不再匹配，整体回收后按新宽度重载
            for (int i = 0; i < _count; i++) Recycle(i);
            _winFirst = -1;
            _winLast = -1;
            _sv.Post(() =>
            {
                _sv.ScrollTo(0, _tops[Clamp(cur)]);
                UpdateWindow(true);
            });
        }

        /* ------------------------------------------------------------
         * 工具
         * ------------------------------------------------------------ */
        private int Clamp(int i) => _count <= 0 ? 0 : Math.Max(0, Math.Min(_count - 1, i));

        /// <summary>二分查找 y 落在哪一页（_tops 有序、页不重叠）。</summary>
        private int FindPage(int y)
        {
            if (_count == 0) return 0;
            if (y <= 0) return 0;
            int lo = 0, hi = _count - 1, ans = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_tops[mid] <= y) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        public void Release()
        {
            _loadToken++;
            _built = false;
            _pendingStart = -1;
            _winFirst = -1;
            _winLast = -1;
            _lastReported = -1;

            if (_bmps != null)
            {
                for (int i = 0; i < _bmps.Length; i++)
                {
                    if (_bmps[i] != null)
                    {
                        try { _bmps[i].Recycle(); _bmps[i].Dispose(); } catch { }
                        _bmps[i] = null;
                    }
                }
            }
            if (_pageGen != null)
                for (int i = 0; i < _pageGen.Length; i++) _pageGen[i]++;

            try { _col?.RemoveAllViews(); } catch { }
            _views = null;
            _bmps = null;
            _pageGen = null;
            _tops = null;
            _heights = null;
            _aspects = null;
            _count = 0;
        }

        protected override void OnDetachedFromWindow()
        {
            base.OnDetachedFromWindow();
            try { Release(); } catch { }
        }

        /* ------------------------------------------------------------
         * 点击检测：轻点（非拖拽）触发 Tap；拖拽仍交给 ScrollView 滚动
         * ------------------------------------------------------------ */
        private sealed class TapGuard : Java.Lang.Object, View.IOnTouchListener
        {
            private readonly PdfContinuousView _o;
            private float _dx, _dy;
            private long _t;

            public TapGuard(PdfContinuousView o) { _o = o; }

            public bool OnTouch(View v, MotionEvent e)
            {
                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                        _dx = e.GetX();
                        _dy = e.GetY();
                        _t = SystemClock.ElapsedRealtime();
                        return false;
                    case MotionEventActions.Up:
                        float dx = Math.Abs(e.GetX() - _dx);
                        float dy = Math.Abs(e.GetY() - _dy);
                        long dt = SystemClock.ElapsedRealtime() - _t;
                        if (dx < 10 && dy < 10 && dt < 250) _o.Tap?.Invoke();
                        return false;
                    default:
                        return false;
                }
            }
        }
    }
}
