// ============================================================
// 音乐模块 · LRC 歌词解析
// 支持：[mm:ss.xx] 与 [mm:ss.xxx]、一行多时间标签、[offset:+ms] 全局偏移、
//       元信息标签 [ti:]/[ar:]/[al:]（忽略）、排序去重。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AhoChatA.Music
{
    public class LrcLine
    {
        public long TimeMs;
        public string Text = "";
    }

    public class Lyrics
    {
        private static readonly Regex TagRx = new Regex(
            @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
            RegexOptions.Compiled);

        public List<LrcLine> Lines = new List<LrcLine>();
        public string Title = "", Artist = "", Album = "";

        public bool IsValid => Lines.Count > 0;

        public static Lyrics Parse(string text)
        {
            var r = new Lyrics();
            if (string.IsNullOrWhiteSpace(text)) return r;

            long offset = 0;
            foreach (var raw0 in text.Replace("\r\n", "\n").Split('\n'))
            {
                var raw = raw0.Trim();
                if (raw.Length == 0) continue;

                // 全局偏移 [offset:+500] / [offset:-300]
                if (raw.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) && raw.EndsWith("]"))
                {
                    if (long.TryParse(raw.Substring(8, raw.Length - 9).Trim(), out var o))
                        offset = -o;   // lrc 约定：正 offset 表示整体提前，所以取负
                    continue;
                }
                // 元信息
                if (raw.StartsWith("[ti:", StringComparison.OrdinalIgnoreCase))
                { r.Title = Inner(raw); continue; }
                if (raw.StartsWith("[ar:", StringComparison.OrdinalIgnoreCase))
                { r.Artist = Inner(raw); continue; }
                if (raw.StartsWith("[al:", StringComparison.OrdinalIgnoreCase))
                { r.Album = Inner(raw); continue; }

                // 找出行首全部时间标签
                var times = new List<long>();
                int pos = 0;
                while (pos < raw.Length && raw[pos] == '[')
                {
                    var m = TagRx.Match(raw, pos);
                    if (!m.Success || m.Index != pos) break;
                    int min = int.Parse(m.Groups[1].Value);
                    int sec = int.Parse(m.Groups[2].Value);
                    long ms = 0;
                    if (m.Groups[3].Success)
                    {
                        var frac = m.Groups[3].Value;         // 10ms 或 ms
                        ms = frac.Length == 1 ? int.Parse(frac) * 100
                           : frac.Length == 2 ? int.Parse(frac) * 10
                           : int.Parse(frac.Substring(0, 3));
                    }
                    times.Add((min * 60L + sec) * 1000 + ms);
                    pos = m.Index + m.Length;
                }
                if (times.Count == 0) continue;   // 纯文本行丢弃

                var body = raw.Substring(pos).Trim();
                foreach (var t in times)
                    r.Lines.Add(new LrcLine { TimeMs = Math.Max(0, t + offset), Text = body });
            }

            r.Lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            // 同一时间戳的多行 = 双语歌词（原文 + 译文），**必须全部保留**；
            // 只丢掉「时间与文本都完全相同」的重复行。
            var dedup = new List<LrcLine>(r.Lines.Count);
            foreach (var l in r.Lines)
            {
                bool dup = false;
                for (int k = dedup.Count - 1; k >= 0 && dedup[k].TimeMs == l.TimeMs; k--)
                {
                    if (dedup[k].Text == l.Text) { dup = true; break; }
                }
                if (!dup) dedup.Add(l);
            }
            r.Lines = dedup;
            return r;
        }

        private static string Inner(string raw)
        {
            int p = raw.IndexOf(']');
            return p > 0 ? raw.Substring(4, p - 4).Trim() : "";
        }

        /// <summary>当前播放进度对应的行号（-1 = 尚未开始唱）。</summary>
        public int LineAt(long ms)
        {
            int lo = 0, hi = Lines.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Lines[mid].TimeMs <= ms) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        /// <summary>
        /// 同时间戳行的组起始下标。双语歌词（原文 + 译文时间戳相同）会被归为同一组，
        /// 显示时整组一起高亮，而不是只亮最后一行。
        /// </summary>
        public int GroupStart(int idx)
        {
            if (idx < 0 || idx >= Lines.Count) return idx;
            long t = Lines[idx].TimeMs;
            int k = idx;
            while (k > 0 && Lines[k - 1].TimeMs == t) k--;
            return k;
        }

        /// <summary>同时间戳行的组结束下标（含）。</summary>
        public int GroupEnd(int idx)
        {
            if (idx < 0 || idx >= Lines.Count) return idx;
            long t = Lines[idx].TimeMs;
            int k = idx;
            while (k + 1 < Lines.Count && Lines[k + 1].TimeMs == t) k++;
            return k;
        }

        /// <summary>该行是否属于双语组（组内不止一行）。</summary>
        public bool IsGrouped(int idx)
        {
            if (idx < 0 || idx >= Lines.Count) return false;
            return GroupStart(idx) != GroupEnd(idx);
        }
    }
}
