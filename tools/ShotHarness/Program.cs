using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Suzu.Core;
using Suzu.Network;
using Suzu.UI;

namespace ShotHarness
{
    /// <summary>
    /// 文档截图工具：实例化真实的 SettingsForm / DisclaimerForm，
    /// 用反射设定引擎状态后 DrawToBitmap 出图，保证与真实渲染 100% 一致。
    /// 产出：docs/states/1..8 编号全窗图、states-statuslines.png（状态行一览深色合成）、
    /// states-overview.png（全窗网格）、settings-window.png（主展示图）、disclaimer.png。
    /// 用法：ShotHarness.exe &lt;输出目录&gt;
    /// </summary>
    internal static class Program
    {
        private static SwitchEngine _engine;
        private static AppConfig _config;

        // (引擎状态, 状态文本, 文件名, 合成图标题, 合成图说明, 标题色)
        private static readonly (string State, string Text, string File, string Title, string Desc, int Rgb)[] States =
        {
            ("Normal",        "【正常】连接正常：主用 Home-5G（信号 86%）", "1-normal", "正常", "在线。网关与外网都通", 0x2E8B57),
            ("OnBackup",      "【受限】已连接，但无 Internet 访问（可能需要网页认证或 VPN 异常）：主用 Home-5G", "2-limited", "受限", "链路在、但出不了网（校园网未认证 / VPN 异常）", 0xB8860B),
            ("WatchingReturn","【正常】已连接 备用 1/2：Phone-Hotspot｜主用已出现，25 秒后切回", "3-watching", "待切回", "主用回来了，先观察满 30 秒再动手", 0x2E8B57),
            ("OnBackup",      "【断开】网络已断开，正在切换 备用 1/3：Phone-Hotspot（信号 72%）…", "4-switching", "切换中", "主用断线，正在按优先级换备用", 0xFF4500),
            ("EthernetHold",  "【挂起】有线网络连接正常，WiFi 切换已挂起待命", "5-hold", "挂起", "有线可用，Wi-Fi 切换让路待命", 0x4682B4),
            ("Normal",        "【正常】连接正常：主用 Home-5G（信号 86%）｜【注意】有线未通，已放行 WiFi 切换", "6-attention", "注意", "网线插着但没网，Wi-Fi 继续正常切换", 0xB8860B),
            ("TotalFailure",  "【故障】所有已知网络均不可用，请检查您的手机热点或路由器是否正常开启。", "7-fault", "故障", "所有已知网络都不可用，降频等待恢复", 0xCD3333),
            ("Paused",        "已暂停自动切换（右键菜单恢复）", "8-paused", "暂停", "用户手动暂停，程序只看不动作", 0x808080),
        };

        [STAThread]
        private static void Main(string[] args)
        {
            var outDir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(outDir);
            var statesDir = Path.Combine(outDir, "states");
            Directory.CreateDirectory(statesDir);

            // ⚠️ 隔离数据目录必须最先做：SettingsForm 关闭时会 SaveWindowBounds → config.Save()，
            // 若不隔离，假数据（Home-5G 等）会覆盖用户真实的 %APPDATA%\Suzu\config.json。
            var sandbox = Path.Combine(Path.GetTempPath(), "Suzu-ShotHarness-Data");
            Directory.CreateDirectory(sandbox);
            Environment.SetEnvironmentVariable("SUZU_DATA_DIR", sandbox);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 假数据：文档截图不能出现真实 SSID
            _config = new AppConfig
            {
                PrimarySsid = "Home-5G",
                BackupSsids = new List<string> { "Phone-Hotspot", "Office-WiFi" },
            };
            var wlan = new WindowsWlanProvider();
            _engine = new SwitchEngine(_config, wlan, new WindowsNetworkVerifier());

            var form = new SettingsForm(_config, wlan, _engine);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(40, 40);
            form.Show();
            Application.DoEvents(); // 触发 Shown → ReloadNetworks

            // 挡掉真实网络数据：文档图统一用假统计
            SetField(form, "_networkInfo", "备用 2 个 · 已知网络 4 个 · 附近可见 7 个");

            var shots = new List<(string File, Bitmap Bmp, Rectangle StatusRect)>();

            foreach (var s in States)
            {
                SetEngine(s.State, s.Text);
                InvokeUpdateStatusLine(form);
                Application.DoEvents();

                using var full = Snap(form);
                var statusRect = GetLabelRect(form);
                shots.Add((s.File, new Bitmap(full), statusRect));

                // 全尺寸保存（半尺寸会模糊；文档/宣传需要高清源图）
                full.Save(Path.Combine(statesDir, s.File + ".png"), ImageFormat.Png);
                Console.WriteLine("已保存: " + Path.Combine(statesDir, s.File + ".png"));
            }

            // 状态行一览（深色合成图）：左侧编号+说明，右侧为真实窗体裁出的状态行
            MakeStatusLines(Path.Combine(outDir, "states-statuslines.png"), shots);
            // 全窗网格（2×4，全尺寸窗口）
            MakeOverview(Path.Combine(outDir, "states-overview.png"), shots);
            // 每个板块单独切割：彩色标题 + 全尺寸窗口，社媒/文档可直接单发
            var cardsDir = Path.Combine(outDir, "overview-cards");
            Directory.CreateDirectory(cardsDir);
            MakeCards(cardsDir, shots);

            // 主展示图复用"倒计时切回"这个最有信息量的状态
            SetEngine("WatchingReturn", States[2].Text);
            InvokeUpdateStatusLine(form);
            Application.DoEvents();
            using (var main = Snap(form))
            {
                main.Save(Path.Combine(outDir, "settings-window.png"), ImageFormat.Png);
            }
            Console.WriteLine("已保存: " + Path.Combine(outDir, "settings-window.png"));
            form.Close();

            // 免责声明窗（真实窗体）
            var disclaimer = new DisclaimerForm();
            disclaimer.StartPosition = FormStartPosition.Manual;
            disclaimer.Location = new Point(40, 40);
            disclaimer.Show();
            Application.DoEvents();
            using (var dbmp = Snap(disclaimer))
            {
                dbmp.Save(Path.Combine(outDir, "disclaimer.png"), ImageFormat.Png);
            }
            Console.WriteLine("已保存: " + Path.Combine(outDir, "disclaimer.png"));
            disclaimer.Close();

            foreach (var s in shots) s.Bmp.Dispose();
        }

        private static void SetEngine(string state, string statusText)
        {
            var t = typeof(SwitchEngine);
            t.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(_engine, Enum.Parse(t.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance).FieldType, state));
            t.GetField("_statusText", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(_engine, statusText);
        }

        private static void SetField(Form form, string name, object value)
        {
            typeof(SettingsForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(form, value);
        }

        private static void InvokeUpdateStatusLine(Form form)
        {
            typeof(SettingsForm).GetMethod("UpdateStatusLine",
                BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(form, null);
        }

        /// <summary>全窗截图（DrawToBitmap 与真实渲染一致）。</summary>
        private static Bitmap Snap(Form form)
        {
            var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            return bmp;
        }

        /// <summary>状态行 Label 在全窗截图中的矩形（含少量留白）。</summary>
        private static Rectangle GetLabelRect(Form form)
        {
            var label = (Label)GetField(form, "_lblStatus");
            var screen = form.PointToScreen(new Point(label.Bounds.X, label.Bounds.Y));
            var client = form.PointToScreen(Point.Empty);
            var nonClientX = client.X - form.Left;
            var nonClientY = client.Y - form.Top;
            return Rectangle.Inflate(
                new Rectangle(screen.X - client.X + nonClientX - 4, screen.Y - client.Y + nonClientY - 3,
                              label.Width + 8, label.Height + 6), 2, 2);
        }

        private static object GetField(Form form, string name)
        {
            return typeof(SettingsForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(form);
        }

        /// <summary>状态行一览：深色底，左列编号+说明，右列真实窗体裁出的状态行。</summary>
        private static void MakeStatusLines(string path, List<(string File, Bitmap Bmp, Rectangle StatusRect)> shots)
        {
            int width = 880, rowH = 74, top = 90;
            var bmp = new Bitmap(width, top + rowH * shots.Count + 30);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(26, 28, 34));

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var titleFont = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold);
            using var subFont = new Font("Microsoft YaHei UI", 9f);
            using var nameFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            using var descFont = new Font("Microsoft YaHei UI", 8.5f);
            g.DrawString("状态行一览（颜色即状态）", titleFont, Brushes.White, 28, 22);
            g.DrawString("设置窗口底部与托盘提示用的是同一行文字，颜色按状态分级。", subFont,
                new SolidBrush(Color.FromArgb(170, 175, 185)), 30, 58);

            for (int i = 0; i < shots.Count; i++)
            {
                var s = States[i];
                int y = top + i * rowH;
                var titleColor = Color.FromArgb(255, (s.Rgb >> 16) & 0xFF, (s.Rgb >> 8) & 0xFF, s.Rgb & 0xFF);
                g.DrawString($"{i + 1}", nameFont, new SolidBrush(titleColor), 30, y + 4);
                g.DrawString(s.Title, nameFont, new SolidBrush(titleColor), 52, y + 2);
                g.DrawString(s.Desc, descFont, new SolidBrush(Color.FromArgb(150, 155, 165)), 52, y + 24);

                // 真实窗体裁出的状态行（白底、真实渲染），缩放统一宽度
                using (var crop = shots[i].Bmp.Clone(shots[i].StatusRect, shots[i].Bmp.PixelFormat))
                {
                    int pw = width - 300, ph = crop.Height * pw / crop.Width;
                    var pill = new Rectangle(285, y - 2, pw, ph + 6);
                    using var pillPath = RoundRect(pill, 6);
                    using var pillBrush = new SolidBrush(Color.FromArgb(244, 245, 247));
                    g.FillPath(pillBrush, pillPath);
                    g.DrawImage(crop, new Rectangle(pill.X + 6, pill.Y + 3, pw - 12, ph));
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }

        /// <summary>全窗网格：2 列 × 4 行，全尺寸窗口（高清）+ 编号角标。</summary>
        private static void MakeOverview(string path, List<(string File, Bitmap Bmp, Rectangle StatusRect)> shots)
        {
            int cellW = shots[0].Bmp.Width + 20, cellH = shots[0].Bmp.Height + 44;
            var bmp = new Bitmap(cellW * 2 + 30, cellH * 4 + 20);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(26, 28, 34));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var nameFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);

            for (int i = 0; i < shots.Count; i++)
            {
                int col = i % 2, row = i / 2;
                int x = 10 + col * cellW, y = 8 + row * cellH;
                var s = States[i];
                g.DrawString($"{i + 1} · {s.Title}", nameFont,
                    new SolidBrush(Color.FromArgb(255, (s.Rgb >> 16) & 0xFF, (s.Rgb >> 8) & 0xFF, s.Rgb & 0xFF)), x + 10, y + 8);
                g.DrawImage(shots[i].Bmp, x + 10, y + 42);
            }
            bmp.Save(path, ImageFormat.Png);
        }

        /// <summary>板块切割：每个状态一张独立卡片（深色底 + 彩色标题 + 全尺寸窗口）。</summary>
        private static void MakeCards(string cardsDir, List<(string File, Bitmap Bmp, Rectangle StatusRect)> shots)
        {
            for (int i = 0; i < shots.Count; i++)
            {
                var s = States[i];
                using var card = new Bitmap(shots[i].Bmp.Width + 20, shots[i].Bmp.Height + 48);
                using (var g = Graphics.FromImage(card))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.FromArgb(26, 28, 34));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    using var nameFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
                    g.DrawString($"{i + 1} · {s.Title}", nameFont,
                        new SolidBrush(Color.FromArgb(255, (s.Rgb >> 16) & 0xFF, (s.Rgb >> 8) & 0xFF, s.Rgb & 0xFF)),
                        10, 8);
                    g.DrawImage(shots[i].Bmp, 10, 42);
                }
                card.Save(Path.Combine(cardsDir, s.File + ".png"), ImageFormat.Png);
                Console.WriteLine("已保存: " + Path.Combine(cardsDir, s.File + ".png"));
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
