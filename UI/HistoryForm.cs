using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Suzu.Core;

namespace Suzu.UI
{
    /// <summary>
    /// 切换历史面板：最近 50 条切换/降级/挂起记录（内存缓存，重启清零）。
    /// 缩放方案与 SettingsForm 相同：AutoScaleMode.None + DeviceDpi/96 手动等比
    /// （AutoScaleMode.Dpi 在 125%/150% 下坐标与字体缩放不同步，实测不可用，勿改回）。
    /// </summary>
    public sealed class HistoryForm : Form
    {
        public HistoryForm(SwitchHistory history)
        {
            Icon = TrayContext.GetFormIcon(); // 标题栏 + 任务栏图标与托盘/exe 统一
            AutoScaleMode = AutoScaleMode.None;
            float s = DeviceDpi / 96f;
            Func<int, int> U = v => (int)Math.Round(v * s);

            Text = "铃波 — 切换历史";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = true;
            ClientSize = new Size(U(700), U(430));
            MinimumSize = new Size(U(560), U(300));
            Font = new Font("Microsoft YaHei UI", 9f);

            var top = new Label
            {
                Text = "最近 50 条（内存缓存，重启清零）。要回看更早或完整过程，请通过托盘菜单「打开日志文件」。",
                Dock = DockStyle.Top,
                Height = U(26),
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(U(4), 0, 0, 0)
            };

            var list = new ListView
            {
                View = View.Details,
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            list.Columns.Add("时间", U(135));
            list.Columns.Add("动作", U(85));
            list.Columns.Add("从", U(120));
            list.Columns.Add("到", U(120));
            list.Columns.Add("原因", U(200));

            foreach (var e in history.Snapshot())
            {
                var item = new ListViewItem(e.Time.ToString("MM-dd HH:mm:ss"));
                item.SubItems.Add(e.Action);
                item.SubItems.Add(e.From);
                item.SubItems.Add(e.To);
                item.SubItems.Add(e.Reason);
                list.Items.Add(item);
            }
            if (list.Items.Count == 0)
            {
                var empty = new ListViewItem("（暂无记录——程序运行以来还没有发生切换/降级/挂起）");
                empty.ForeColor = Color.Gray;
                list.Items.Add(empty);
            }

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                Height = U(40),
                Padding = new Padding(U(6), U(6), U(6), 0)
            };
            var btnCopy = new Button
            {
                Text = "复制全部",
                Width = U(88),
                Height = U(28)
            };
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(history.ToText());
                    btnCopy.Text = "已复制 ✓";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "复制失败：" + ex.Message, "铃波",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            var btnClose = new Button
            {
                Text = "关闭",
                Width = U(72),
                Height = U(28)
            };
            btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnCopy);
            bottom.Controls.Add(btnClose);

            Controls.Add(list);
            Controls.Add(bottom);
            Controls.Add(top);
        }
    }
}
