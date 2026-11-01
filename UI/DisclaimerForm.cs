using System;
using System.Drawing;
using System.Windows.Forms;

namespace Suzu.UI
{
    /// <summary>
    /// 首次启动免责声明。文案按方案书一字不差，请勿润色。
    /// 「我已了解并同意」= OK；「退出」和右上角 × 都等于不授权，程序退出。
    ///
    /// 布局用纵向 TableLayoutPanel（图标+标题 / 正文 / 按钮三行），全部 AutoSize，
    /// 这样 125%/150% 缩放时不会出现图标压住标题、正文被挤掉的问题。
    /// </summary>
    public sealed class DisclaimerForm : Form
    {
        private const string BodyText =
            "温馨提示：\r\n" +
            "本软件仅负责 WiFi 链路层的连接与切换（OSI 二层/三层），不包含、也永远不会包含任何网页认证（Portal）、" +
            "802.1x 认证的绕过或保活功能。遇到\"无 Internet\"提示时，请确保你的网络本身是可用的。\r\n" +
            "\r\n" +
            "个人开源不易，恳请各位大佬不要将其打包用于任何形式的商业售卖。仅供学习交流与个人使用。";

        public DisclaimerForm()
        {
            Icon = TrayContext.GetFormIcon(); // 标题栏 + 任务栏图标与托盘/exe 统一
            // 布局全部 AutoSize 跟随字体，字体用 pt 声明会随 DPI 自动放大，
            // 所以缩放模式必须关掉——Dpi 模式会把坐标再乘一次，导致二次放大挤成一团
            AutoScaleMode = AutoScaleMode.None;

            Text = "铃波 — 使用前请阅读";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = new Font("Microsoft YaHei UI", 9f);

            // 正文基准宽度（96 DPI 设计值），按实际 DPI 等比放大，保持与字体的比例
            float ds = DeviceDpi / 96f;
            int innerWidth = (int)Math.Round(480 * ds);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(18, 18, 18, 14),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, innerWidth));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // ---- 第一行：图标 + 标题（图标 32×32 固定，标题 AutoSize，永不重叠）----
            var header = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 12),
                Anchor = AnchorStyles.Left
            };

            var iconBox = new PictureBox
            {
                Image = SystemIcons.Information.ToBitmap(),
                SizeMode = PictureBoxSizeMode.StretchImage,
                Size = new Size((int)Math.Round(32 * ds), (int)Math.Round(32 * ds)),
                Margin = new Padding(0, 0, 10, 0)
            };
            var title = new Label
            {
                Text = "使用前请阅读",
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            header.Controls.Add(iconBox);
            header.Controls.Add(title);
            layout.Controls.Add(header, 0, 0);

            // ---- 第二行：正文（AutoSize + 固定最大宽度，自动换行）----
            var body = new Label
            {
                Text = BodyText,
                AutoSize = true,
                MaximumSize = new Size(innerWidth, 0),
                MinimumSize = new Size(innerWidth, 0),
                TextAlign = ContentAlignment.TopLeft,
                ForeColor = Color.FromArgb(60, 60, 60),
                Margin = new Padding(0, 0, 0, 14)
            };
            layout.Controls.Add(body, 0, 1);

            // ---- 第三行：按钮（右对齐，宽度按文字实测）----
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };

            var btnAgree = new Button
            {
                Text = "我已了解并同意",
                Width = UiMetrics.ButtonWidth("我已了解并同意", Font, 120),
                Height = UiMetrics.LabelHeight(Font, 1) + 8,
                DialogResult = DialogResult.OK,
                Margin = new Padding(8, 0, 0, 0)
            };
            var btnExit = new Button
            {
                Text = "退出",
                Width = UiMetrics.ButtonWidth("退出", Font, 84),
                Height = btnAgree.Height,
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(0, 0, 0, 0)
            };

            buttons.Controls.Add(btnAgree);
            buttons.Controls.Add(btnExit);
            layout.Controls.Add(buttons, 0, 2);

            Controls.Add(layout);
            AcceptButton = btnAgree;
            CancelButton = btnExit;

            // 窗体尺寸完全跟着内容走（高 DPI 下自动等比放大，不会挤压正文）
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }
    }
}
