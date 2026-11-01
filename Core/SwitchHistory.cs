using System;
using System.Collections.Generic;
using System.Text;

namespace Suzu.Core
{
    /// <summary>一条切换/降级/挂起记录（不可变）。</summary>
    public sealed class HistoryEntry
    {
        public DateTime Time { get; private set; }
        public string Action { get; private set; }
        public string From { get; private set; }
        public string To { get; private set; }
        public string Reason { get; private set; }

        public HistoryEntry(DateTime time, string action, string from, string to, string reason)
        {
            Time = time;
            Action = action ?? "";
            From = from ?? "";
            To = to ?? "";
            Reason = Truncate(reason);
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 80 ? s : s.Substring(0, 80) + "…";
        }
    }

    /// <summary>
    /// 切换历史：内存环形缓冲（容量 50，最新的在最前），只服务"免翻日志"的快速回看。
    /// 不持久化——日志文件才是持久层，这里重启清零是预期行为。
    /// </summary>
    public sealed class SwitchHistory
    {
        private readonly object _sync = new object();
        private readonly List<HistoryEntry> _items = new List<HistoryEntry>();
        private const int Capacity = 50;

        public void Add(string action, string from, string to, string reason)
        {
            lock (_sync)
            {
                _items.Insert(0, new HistoryEntry(DateTime.Now, action, from, to, reason));
                if (_items.Count > Capacity)
                {
                    _items.RemoveRange(Capacity, _items.Count - Capacity);
                }
            }
        }

        /// <summary>快照（最新在前）。返回副本，遍历不受后续写入影响。</summary>
        public IList<HistoryEntry> Snapshot()
        {
            lock (_sync)
            {
                return new List<HistoryEntry>(_items);
            }
        }

        /// <summary>把全部条目拼成可读文本（"复制全部"按钮用）。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("铃波 切换历史（最近 " + Capacity + " 条，内存缓存）");
            sb.AppendLine("时间                  动作          从 → 到 ｜ 原因");
            foreach (var e in Snapshot())
            {
                sb.AppendLine(string.Format("{0:yyyy-MM-dd HH:mm:ss}  [{1}]  {2} → {3}  ｜ {4}",
                    e.Time, e.Action, e.From, e.To, e.Reason));
            }
            return sb.ToString();
        }
    }
}
