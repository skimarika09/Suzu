using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Suzu.Core;
using Suzu.Network;

namespace Suzu.UI
{
    /// <summary>单窗体设置界面。</summary>
    public sealed class SettingsForm : Form
    {
        private readonly AppConfig _config;
        private readonly IWifiProvider _wlan;
        private readonly SwitchEngine _engine;

        private ToolTip _tip;

        private ComboBox _cmbPrimary;
        private ListBox _lstBackups;
        private ComboBox _cmbAddBackup;
        private ComboBox _cmbEthPolicy;
        private Button _btnAddBackup;
        private Button _btnImportAll;
        private Button _btnRemove;
        private Button _btnUp;
        private Button _btnDown;
        private NumericUpDown _numDelay;
        private NumericUpDown _numSignal;
        private NumericUpDown _numCooldown;
        private NumericUpDown _numConfirm;
        private NumericUpDown _numPoll;
        private CheckBox _chkBackoff;
        private CheckBox _chkAutoStart;
        private Label _lblStatus;
        private Label _lblInfo;
        private Button _btnScan;
        private Button _btnSave;
        private Button _btnCancel;
        private Button _btnCheck;
        private Button _btnDiag;
        private Button _btnHistory;
        private TableLayoutPanel _table;
        private Timer _statusTimer;

        // 操作反馈（点按钮后给用户一个明确回应）
        private string _feedbackText;
        private DateTime _feedbackUntil = DateTime.MinValue;
        private bool _feedbackError;

        private HashSet<string> _savedSsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _visibleSsids = new List<string>();
        private string _networkInfo = "正在读取 Windows 已保存网络…";

        public SettingsForm(AppConfig config, IWifiProvider wlan, SwitchEngine engine)
        {
            Icon = TrayContext.GetFormIcon(); // 标题栏 + 任务栏图标与托盘/exe 统一
            _config = config;
            _wlan = wlan;
            _engine = engine;

            BuildUi();
            LoadValues();
            RestoreWindowBounds();
            FormClosing += (s, e) => SaveWindowBounds();
        }

        private void BuildUi()
        {
            // 高 DPI 适配：不用 WinForms 的 AutoScale（实测 PerMonitorV2 + 125%/150% 下
            // 坐标缩放与字体缩放不一致，会挤成一团），改为按 DeviceDpi/96 的确定比例
            // 手动缩放所有固定像素尺寸；字体用 pt 声明，系统会按 DPI 自动放大。
            AutoScaleMode = AutoScaleMode.None;
            float s = DeviceDpi / 96f;
            Func<int, int> U = v => (int)Math.Round(v * s);

            Text = "铃波 — 设置";
            StartPosition = FormStartPosition.CenterScreen;
            // 可手动拉伸：高 DPI 若仍有局部挤压，用户把窗口拉大即可自救；布局自适应见下方 percent 列
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            ShowInTaskbar = true;
            ClientSize = new Size(U(600), U(634));
            MinimumSize = new Size(U(600) + 16, U(480));
            Font = new Font("Microsoft YaHei UI", 9f);

            _tip = new ToolTip
            {
                AutoPopDelay = 30000,
                InitialDelay = 250,
                ReshowDelay = 80,
                ShowAlways = true,
                UseFading = false,
                UseAnimation = false
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 17,
                Padding = new Padding(U(14), U(12), U(14), U(12)),
                AutoSize = false
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, U(132)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, U(24)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, U(104)));

            int[] heights = { 32, 32, 92, 32, 28, 32, 32, 32, 32, 32, 32, 32, 32, 32, 52, 46, 40 };
            for (int r = 0; r < heights.Length; r++)
            {
                // 弹性/自适应行：
                //  第 2 行（备用列表）→ Percent 100，拉伸窗口时吃掉全部富余高度，列表变高最实用；
                //  第 14/15 行（状态行、提示行）→ AutoSize，内容换行时自动长高，不会截断。
                // 其余行保持 Absolute（按 DPI 缩放），缩到最小尺寸时布局也不会乱。
                if (r == 2)
                {
                    table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                }
                else if (r == 14 || r == 15)
                {
                    table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                }
                else
                {
                    table.RowStyles.Add(new RowStyle(SizeType.Absolute, U(heights[r])));
                }
            }

            // 最小高度按内容算：固定行高之和 + 上下内边距 + 非客户区，再允许备用列表从 92 缩到 60。
            // 否则用户把窗口拉得太小时，TableLayoutPanel 会把固定行压扁，出现文字重叠。
            int neededHeight = 0;
            foreach (var h in heights) neededHeight += U(h);
            MinimumSize = new Size(U(600) + U(16), neededHeight - U(32) + U(24) + U(39));

            var header = MakeHeader("主用掉线自动切备用；主用恢复后自动切回。下拉只列已保存网络，点 ⓘ 看解释。");
            table.Controls.Add(header, 0, 0);
            table.SetColumnSpan(header, 4);

            // ---- 主用 ----
            _cmbPrimary = MakeSsidCombo();
            _cmbPrimary.TextChanged += (s, e) => RefreshAddCombo();
            table.Controls.Add(MakeLabel("主用 WiFi"), 0, 1);
            table.Controls.Add(MakeInfo("主用 WiFi",
                "平时最想用的那个 WiFi（比如家里的路由器）。\r\n它一掉线，程序会自动帮你换到备用网络；它回来了，程序再帮你切回来。\r\n注意：不支持隐藏 SSID 的网络（关闭广播的热点扫不到，恢复检测会失效），\r\n请让主用/备用网络保持广播开启。"), 1, 1);
            table.Controls.Add(_cmbPrimary, 2, 1);

            _btnScan = new Button { Text = "刷新", Dock = DockStyle.Fill, Margin = new Padding(4, 3, 0, 3) };
            _btnScan.Click += (s, e) => ScanNetworks();
            _tip.SetToolTip(_btnScan, "重新读一遍 Windows 已保存的网络，并统计附近能扫到几个。\r\n只是刷新信息，不会把附近没连过的网络加进列表。");
            table.Controls.Add(_btnScan, 3, 1);

            // ---- 备用列表 ----
            _lstBackups = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Margin = new Padding(0, 3, 4, 3),
                SelectionMode = SelectionMode.One
            };
            var lblBackups = MakeLabel("备用网络");
            lblBackups.TextAlign = ContentAlignment.TopLeft;
            lblBackups.Padding = new Padding(0, U(4), 0, 0);
            table.Controls.Add(lblBackups, 0, 2);
            var infoBackups = MakeInfo("备用网络",
                "主用掉线时按顺序顶上的网络（手机热点、第二个路由器等）。\r\n列表从上到下就是尝试顺序：第一个连不上就试第二个，以此类推。");
            infoBackups.TextAlign = ContentAlignment.TopCenter;
            infoBackups.Padding = new Padding(0, U(4), 0, 0);
            table.Controls.Add(infoBackups, 1, 2);
            table.Controls.Add(_lstBackups, 2, 2);

            var ops = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(4, 3, 0, 3)
            };
            _btnRemove = MakeSmallButton("移除");
            _btnUp = MakeSmallButton("上移");
            _btnDown = MakeSmallButton("下移");
            _btnRemove.Click += (s, e) => RemoveSelectedBackup();
            _btnUp.Click += (s, e) => MoveSelectedBackup(-1);
            _btnDown.Click += (s, e) => MoveSelectedBackup(1);
            _tip.SetToolTip(_btnRemove, "把选中的网络从备用列表里删掉。");
            _tip.SetToolTip(_btnUp, "把选中的网络往上挪，优先级更高（先尝试）。");
            _tip.SetToolTip(_btnDown, "把选中的网络往下挪，优先级更低（后尝试）。");
            ops.Controls.Add(_btnRemove);
            ops.Controls.Add(_btnUp);
            ops.Controls.Add(_btnDown);
            table.Controls.Add(ops, 3, 2);

            // ---- 添加备用 ----
            _cmbAddBackup = MakeSsidCombo();
            _btnAddBackup = new Button { Text = "添加备用网络", Dock = DockStyle.Fill, Margin = new Padding(4, 3, 0, 3) };
            _btnAddBackup.Click += (s, e) => AddBackupFromCombo();
            _tip.SetToolTip(_btnAddBackup, "把下拉框里选中的网络加到备用列表末尾。\r\n下拉框只会出现「已保存、还没加进来、也不是主用」的网络。");

            table.Controls.Add(MakeLabel("添加备用"), 0, 3);
            table.Controls.Add(MakeInfo("添加备用",
                "从 Windows 已经保存过密码的网络里挑一个加进备用列表。\r\n加进来之后它就会从下拉框里消失，不会重复添加。"), 1, 3);
            table.Controls.Add(_cmbAddBackup, 2, 3);
            table.Controls.Add(_btnAddBackup, 3, 3);

            var hint = MakeHint("列表从上到下 = 优先级顺序，用右侧「上移 / 下移」调整。");
            table.Controls.Add(hint, 0, 4);
            table.SetColumnSpan(hint, 4);

            var importHint = MakeHint("选好主用后，可一键把其余所有已保存网络导入列表，再调优先级。");
            table.Controls.Add(importHint, 0, 5);
            table.SetColumnSpan(importHint, 3);

            _btnImportAll = new Button { Text = "一键导入", Dock = DockStyle.Fill, Margin = new Padding(4, 3, 0, 3) };
            _btnImportAll.Click += (s, e) => ImportAllSaved();
            _tip.SetToolTip(_btnImportAll, "把 Windows 已保存的其余网络一次性全部加进备用列表（自动跳过主用和已在列表里的）。\r\n导入后可以用上移/下移调整顺序。");
            table.Controls.Add(_btnImportAll, 3, 5);

            // ---- 参数 ----
            _numDelay = MakeNumber(0, 3600, 0);
            _numSignal = MakeNumber(0, 100, 0);
            _numCooldown = MakeNumber(0, 3600, 0);
            _numConfirm = MakeNumber(1, 10, 0);
            _numPoll = MakeNumber(3, 120, 0);

            table.Controls.Add(MakeLabel("切回延迟（秒）"), 0, 6);
            table.Controls.Add(MakeInfo("切回延迟",
                "主用重新出现后，先观察多少秒再切回去。\r\n设 30，就是要求主用先稳定 30 秒，避免刚连上又掉、来回横跳；\r\n设 0 表示一出现就立刻切回。"), 1, 6);
            table.Controls.Add(_numDelay, 2, 6);
            table.Controls.Add(MakeHint("0 = 立即切回"), 3, 6);
            _tip.SetToolTip(_numDelay, "主用出现后等多少秒再切回。0 = 立即。");

            table.Controls.Add(MakeLabel("信号阈值（%）"), 0, 7);
            table.Controls.Add(MakeInfo("信号阈值",
                "主用信号强度至少要到百分之多少，才算\"真的能用\"。\r\n设 50 就要求信号 ≥ 50%；设 0 表示只要扫到这个名字就算数（不挑信号）。"), 1, 7);
            table.Controls.Add(_numSignal, 2, 7);
            table.Controls.Add(MakeHint("0 = 只要求可见"), 3, 7);
            _tip.SetToolTip(_numSignal, "要求主用的最低信号百分比。0 = 只要求 SSID 可见。");

            table.Controls.Add(MakeLabel("冷却时间（秒）"), 0, 8);
            table.Controls.Add(MakeInfo("冷却时间",
                "每成功切换一次后，多少秒内不再自动切来切去，给网络一点稳定时间。\r\n掉线应急切换不受这个限制（保证随时有网）。"), 1, 8);
            table.Controls.Add(_numCooldown, 2, 8);
            table.Controls.Add(MakeHint("切换后静默"), 3, 8);
            _tip.SetToolTip(_numCooldown, "切换成功后暂停自动切换的秒数。");

            table.Controls.Add(MakeLabel("连续确认次数"), 0, 9);
            table.Controls.Add(MakeInfo("连续确认次数",
                "扫描到几次主用，才认定它\"真的回来了\"。\r\n默认 3 次（约 20~30 秒）。手机热点刚开时信号常常忽有忽无，多确认几次能避免误切。"), 1, 9);
            table.Controls.Add(_numConfirm, 2, 9);
            table.Controls.Add(MakeHint("默认 3 次"), 3, 9);
            _tip.SetToolTip(_numConfirm, "连续几次扫描都看到主用才认为它恢复了。");

            table.Controls.Add(MakeLabel("轮询间隔（秒）"), 0, 10);
            table.Controls.Add(MakeInfo("轮询间隔",
                "没有收到 Windows 通知时，程序多久主动检查一次状态。\r\n默认 10 秒；调小反应更快，但会更频繁地扫描、略费电。"), 1, 10);
            table.Controls.Add(_numPoll, 2, 10);
            table.Controls.Add(MakeHint("默认 10 秒"), 3, 10);
            _tip.SetToolTip(_numPoll, "没事件时多久主动检查一次。默认 10 秒。");

            table.Controls.Add(MakeLabel("有线策略"), 0, 11);
            table.Controls.Add(MakeInfo("有线策略",
                "默认「自动挂起」：有线真有外网（网关通且外网探测通过）时，WiFi 交给有线。\r\n选「继续管理」则忽略有线，始终按主备规则管理 WiFi\r\n——适合有线限速、WiFi 更快，或想让程序持续盯着 WiFi 的场景。"), 1, 11);
            _cmbEthPolicy = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 4, 4)
            };
            _cmbEthPolicy.Items.Add("自动挂起 WiFi 管理（默认）");
            _cmbEthPolicy.Items.Add("继续管理 WiFi（忽略有线）");
            _tip.SetToolTip(_cmbEthPolicy, "检测到可用有线网络时的处理方式。默认自动挂起；有线限速或想常驻 WiFi 的选「继续管理」。");
            table.Controls.Add(_cmbEthPolicy, 2, 11);
            table.Controls.Add(MakeHint("默认自动挂起"), 3, 11);

            _chkBackoff = new CheckBox { Text = "切回失败时启用指数退避（30s → 1m → 2m …，上限 10 分钟）", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 2) };
            _tip.SetToolTip(_chkBackoff, "切回主用失败（连上了但上不了网）后，不要马上重试，而是越等越久：\r\n30 秒 → 1 分钟 → 2 分钟 … 最多 10 分钟，避免反复横跳。");
            table.Controls.Add(_chkBackoff, 2, 12);
            table.SetColumnSpan(_chkBackoff, 2);

            _chkAutoStart = new CheckBox { Text = "开机自动启动（注册 Windows 计划任务）", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
            _tip.SetToolTip(_chkAutoStart, "开机后自动在托盘里跑起来，不用每次手动打开。\r\n通过 Windows 计划任务在登录时静默启动（需管理员权限的程序用 Run 项会被系统跳过）。");
            table.Controls.Add(_chkAutoStart, 2, 13);
            table.SetColumnSpan(_chkAutoStart, 2);

            // 状态行拆成两行：上行是引擎状态（可换行），下行是网络数量/操作反馈。
            // 两行都用 AutoSize（行样式也改成 AutoSize），这样长状态/诊断提示换行时不会截断，
            // 宽度上限由 UpdateStatusLabelsWidth() 在每次布局时按当前窗口宽度重设。
            _lblStatus = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                Text = "状态：" + _engine.StatusText,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 2, 0, 0)
            };
            table.Controls.Add(_lblStatus, 0, 14);
            table.SetColumnSpan(_lblStatus, 4);

            _lblInfo = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                Text = _networkInfo,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 0, 0, 0)
            };
            table.Controls.Add(_lblInfo, 0, 15);
            table.SetColumnSpan(_lblInfo, 4);
            _table = table;

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 0, 0)
            };

            float bs = DeviceDpi / 96f;
            Func<int, int> UB = v => (int)Math.Round(v * bs);

            _btnSave = new Button { Text = "保存", Height = UB(28) };
            _btnSave.Click += (s, e) => SaveSettings();
            _tip.SetToolTip(_btnSave, "保存并立即生效（窗口不会关闭，可以继续调）。");

            _btnCancel = new Button { Text = "取消", Height = UB(28) };
            _btnCancel.Click += (s, e) => RevertChanges();
            _tip.SetToolTip(_btnCancel, "把界面恢复到上次保存的状态（窗口不会关闭）。要关闭窗口请点右上角 ×。");

            _btnCheck = new Button { Text = "立即检查", Height = UB(28) };
            _btnCheck.Click += (s, e) =>
            {
                _engine.CheckNow();
                Flash("已触发一次检查，结果会显示在下方状态里");
                PulseButton(_btnCheck, "已触发 ✓");
            };
            _tip.SetToolTip(_btnCheck, "马上按当前规则检查一遍并执行一次（会忽略延迟和退避）。");

            _btnDiag = new Button { Text = "遇到问题?", Height = UB(28) };
            _btnDiag.Click += (s, e) => ExportDiagnostics();
            _tip.SetToolTip(_btnDiag, "打包日志和系统信息到桌面（会自动脱敏配置），发给别人帮忙排查时用这个。");

            _btnHistory = new Button { Text = "历史", Height = UB(28) };
            _btnHistory.Click += (s, e) =>
            {
                using (var f = new HistoryForm(_engine.History))
                {
                    f.ShowDialog(this);
                }
            };
            _tip.SetToolTip(_btnHistory, "查看最近 50 次切换/降级/挂起记录（内存缓存，重启清零）。");

            // 整行等宽：以最长文字（"立即检查 / 切换"）所需宽度为准，五个按钮大小完全一致
            var rowButtons = new[] { _btnSave, _btnCancel, _btnCheck, _btnDiag, _btnHistory };
            int uniformWidth = UB(88);
            foreach (var b in rowButtons)
            {
                uniformWidth = Math.Max(uniformWidth, UiMetrics.ButtonWidth(b.Text, Font, 0));
            }
            foreach (var b in rowButtons)
            {
                b.Width = uniformWidth;
            }

            buttons.Controls.Add(_btnSave);
            buttons.Controls.Add(_btnCancel);
            buttons.Controls.Add(_btnCheck);
            buttons.Controls.Add(_btnDiag);
            buttons.Controls.Add(_btnHistory);
            table.Controls.Add(buttons, 0, 16);
            table.SetColumnSpan(buttons, 4);

            // 五个等宽按钮加间距必须放得下一行，否则 FlowLayoutPanel 会把最后加的"历史"换行裁掉
            buttons.WrapContents = false;

            Controls.Add(table);
            AcceptButton = _btnSave;
            CancelButton = _btnCancel;

            _statusTimer = new Timer { Interval = 700 };
            _statusTimer.Tick += (s, e) => UpdateStatusLine();
            _statusTimer.Start();
            UpdateStatusLine();
            UpdateStatusLabelsWidth();

            Shown += (s, e) => ReloadNetworks(false);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateStatusLabelsWidth();
        }

        /// <summary>
        /// 状态行/提示行是 AutoSize 的（能随内容长高），但必须给个宽度上限才会换行；
        /// 拉伸窗口后要重算，否则文字会一直按初始宽度折行。
        /// </summary>
        private void UpdateStatusLabelsWidth()
        {
            if (_table == null) return;
            int width = _table.ClientSize.Width - _table.Padding.Horizontal;
            if (width <= 100) return;

            if (_lblStatus != null && _lblStatus.MaximumSize.Width != width)
            {
                _lblStatus.MaximumSize = new Size(width, 0);
            }
            if (_lblInfo != null && _lblInfo.MaximumSize.Width != width)
            {
                _lblInfo.MaximumSize = new Size(width, 0);
            }
        }

        // ---------------- 诊断导出 ----------------

        /// <summary>把日志/脱敏配置/系统信息打包到桌面，并在资源管理器里选中。</summary>
        private void ExportDiagnostics()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                var lines = Suzu.Network.SystemSnapshot.BuildLines();
                var zipPath = DiagnosticBundle.Export(lines);
                AppLog.Info("已导出诊断包: " + zipPath);
                Flash("诊断包已导出：" + zipPath);
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zipPath + "\"");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("打开资源管理器失败：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("导出诊断包失败", ex);
                Flash("导出失败：" + ex.Message, true);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        // ---------------- 窗口位置记忆 ----------------

        private void RestoreWindowBounds()
        {
            try
            {
                var c = _config;
                if (c.WindowWidth <= 0 || c.WindowHeight <= 0) return;

                var vs = SystemInformation.VirtualScreen;
                var bounds = new Rectangle(c.WindowLeft, c.WindowTop, c.WindowWidth, c.WindowHeight);

                // 屏幕边界钳制：保存时副屏还在、恢复时副屏没了，窗口就会跑到看不见的地方
                var visible = Rectangle.Intersect(bounds, vs);
                if (visible.Width < 120 || visible.Height < 80)
                {
                    return; // 基本不可见，放弃恢复，保持默认居中
                }

                int width = Math.Max(bounds.Width, MinimumSize.Width);
                int height = Math.Max(bounds.Height, MinimumSize.Height);

                int left = bounds.Left;
                int top = bounds.Top;
                if (left < vs.Left) left = vs.Left;
                if (top < vs.Top) top = vs.Top;
                if (left + width > vs.Right) left = Math.Max(vs.Left, vs.Right - width);
                if (top + height > vs.Bottom) top = Math.Max(vs.Top, vs.Bottom - height);

                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(left, top, width, height);
            }
            catch (Exception ex)
            {
                AppLog.Warn("恢复窗口位置失败：" + ex.Message);
            }
        }

        private void SaveWindowBounds()
        {
            try
            {
                if (WindowState != FormWindowState.Normal) return;   // 最大化/最小化不记
                lock (_config.SyncRoot)
                {
                    _config.WindowLeft = Left;
                    _config.WindowTop = Top;
                    _config.WindowWidth = Width;
                    _config.WindowHeight = Height;
                }
                _config.Save();
            }
            catch (Exception ex)
            {
                AppLog.Warn("保存窗口位置失败：" + ex.Message);
            }
        }
        // ---------------- 控件工厂 ----------------

        private static Label MakeHeader(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 6)
            };
        }

        private static Label MakeLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 3, 0, 3)
            };
        }

        private static Label MakeHint(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(4, 3, 0, 3)
            };
        }

        private Button MakeSmallButton(string text)
        {
            // 右列按钮统一宽度（与上方「刷新」按钮同宽同起点，整列视觉对齐）；
            // 100 逻辑像素对两字按钮在任何 DPI 下都有充足余量，不会被裁字
            return new Button
            {
                Text = text,
                Width = (int)Math.Round(100 * (DeviceDpi / 96f)),
                Height = Math.Max(24, Font.Height + 8),
                Margin = new Padding(4, 1, 0, 1)
            };
        }

        /// <summary>「ⓘ」按钮：鼠标移上去显示大白话解释，点一下也会弹出来（方便不习惯悬停的人）。</summary>
        private Label MakeInfo(string title, string explanation)
        {
            var info = new Label
            {
                Text = "ⓘ",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(0, 122, 204),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 2, 3)
            };

            _tip.SetToolTip(info, explanation);
            info.Click += (s, e) =>
            {
                MessageBox.Show(this, explanation, "什么是「" + title + "」？",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            return info;
        }

        private ComboBox MakeSsidCombo()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown,
                Margin = new Padding(0, 3, 4, 3),
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };
        }

        private static NumericUpDown MakeNumber(int min, int max, int decimals)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 4, 4),
                TextAlign = HorizontalAlignment.Left
            };
        }

        private void LoadValues()
        {
            _cmbPrimary.Text = _config.PrimarySsid;
            SetBackupList(_config.BackupSsids ?? new List<string>());
            _numDelay.Value = Math.Max(0, Math.Min(3600, _config.ReturnDelaySeconds));
            _numSignal.Value = Math.Max(0, Math.Min(100, _config.SignalThresholdPercent));
            _numCooldown.Value = Math.Max(0, Math.Min(3600, _config.CooldownSeconds));
            _numConfirm.Value = Math.Max(1, Math.Min(10, _config.ConfirmScans));
            _numPoll.Value = Math.Max(3, Math.Min(120, _config.PollIntervalSeconds));
            _chkBackoff.Checked = _config.BackoffEnabled;
            _chkAutoStart.Checked = _config.AutoStart || _config.IsAutoStartRegistered();
            _cmbEthPolicy.SelectedIndex = _config.HoldPolicy == EthernetHoldPolicy.AlwaysManage ? 1 : 0;
        }

        // ---------------- 备用列表操作 ----------------

        private List<string> GetBackupList()
        {
            var list = new List<string>();
            foreach (var item in _lstBackups.Items)
            {
                var s = (item == null ? "" : item.ToString()).Trim();
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }

        private void SetBackupList(IEnumerable<string> ssids)
        {
            var cleaned = new List<string>();
            if (ssids != null)
            {
                foreach (var raw in ssids)
                {
                    var s = (raw ?? "").Trim();
                    if (s.Length == 0) continue;
                    if (cleaned.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) continue;
                    cleaned.Add(s);
                }
            }

            _lstBackups.BeginUpdate();
            _lstBackups.Items.Clear();
            foreach (var s in cleaned) _lstBackups.Items.Add(s);
            _lstBackups.EndUpdate();

            RefreshAddCombo();
            UpdateBackupCount();
        }

        /// <summary>刷新「添加备用」下拉框：只放已保存、未加入列表、且不是主用的网络。</summary>
        private void RefreshAddCombo()
        {
            if (_cmbAddBackup == null) return;

            var added = GetBackupList();
            var primary = (_cmbPrimary.Text ?? "").Trim();

            var items = _savedSsids
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Where(s => !string.Equals(s, primary, StringComparison.OrdinalIgnoreCase))
                .Where(s => !added.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s, StringComparer.CurrentCulture)
                .ToList();

            var text = _cmbAddBackup.Text;
            _cmbAddBackup.BeginUpdate();
            _cmbAddBackup.Items.Clear();
            foreach (var s in items) _cmbAddBackup.Items.Add(s);
            _cmbAddBackup.EndUpdate();

            var keep = !string.IsNullOrWhiteSpace(text) &&
                       items.Any(x => string.Equals(x, text.Trim(), StringComparison.OrdinalIgnoreCase));
            _cmbAddBackup.Text = keep ? text : string.Empty;

            if (string.IsNullOrWhiteSpace(_cmbAddBackup.Text) && _cmbAddBackup.Items.Count > 0)
            {
                _cmbAddBackup.SelectedIndex = 0;
            }

            _btnAddBackup.Enabled = _cmbAddBackup.Items.Count > 0;
        }

        private void AddBackupFromCombo()
        {
            var ssid = (_cmbAddBackup.Text ?? "").Trim();
            if (ssid.Length == 0)
            {
                _networkInfo = "请先在左侧下拉框里选一个已保存的网络";
                UpdateStatusLine();
                return;
            }

            var primary = (_cmbPrimary.Text ?? "").Trim();
            if (string.Equals(ssid, primary, StringComparison.OrdinalIgnoreCase))
            {
                _networkInfo = "「" + ssid + "」已经是主用，不能同时当备用";
                UpdateStatusLine();
                return;
            }

            if (GetBackupList().Any(x => string.Equals(x, ssid, StringComparison.OrdinalIgnoreCase)))
            {
                _networkInfo = "「" + ssid + "」已经在备用列表里了";
                UpdateStatusLine();
                return;
            }

            if (!_savedSsids.Contains(ssid))
            {
                _networkInfo = "「" + ssid + "」不在 Windows 已知网络里（没有配置文件，加了也连不上）";
                UpdateStatusLine();
                return;
            }

            _lstBackups.Items.Add(ssid);
            _lstBackups.SelectedIndex = _lstBackups.Items.Count - 1;
            _cmbAddBackup.Text = string.Empty;
            RefreshAddCombo();
            UpdateBackupCount();
            Flash("已添加备用：" + ssid + "（当前共 " + _lstBackups.Items.Count + " 个）");
            AppLog.Info("备用列表加入: " + ssid);
        }

        /// <summary>一键导入：把其余所有已保存网络按名称加入列表末尾（跳过主用和已在列表里的）。</summary>
        private void ImportAllSaved()
        {
            var primary = (_cmbPrimary.Text ?? "").Trim();
            if (primary.Length == 0)
            {
                Flash("请先选择主用 WiFi，再一键导入", true);
                return;
            }

            var list = GetBackupList();
            int added = 0;
            foreach (var s in _savedSsids.OrderBy(x => x, StringComparer.CurrentCulture))
            {
                var item = (s ?? "").Trim();
                if (item.Length == 0) continue;
                if (string.Equals(item, primary, StringComparison.OrdinalIgnoreCase)) continue;
                if (list.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(item);
                added++;
            }

            SetBackupList(list);

            if (added > 0)
            {
                if (_lstBackups.Items.Count > 0) _lstBackups.SelectedIndex = _lstBackups.Items.Count - 1;
                Flash(string.Format("已导入 {0} 个已保存网络，备用共 {1} 个（可用上移/下移排序）", added, list.Count));
                AppLog.Info("一键导入备用网络 " + added + " 个，当前备用列表: " + string.Join(" → ", list));
            }
            else
            {
                Flash("没有可导入的新网络（其余已保存网络都在列表里了）", true);
            }
        }

        private void RemoveSelectedBackup()
        {
            var index = _lstBackups.SelectedIndex;
            if (index < 0) return;
            _lstBackups.Items.RemoveAt(index);
            if (_lstBackups.Items.Count > 0)
            {
                _lstBackups.SelectedIndex = Math.Min(index, _lstBackups.Items.Count - 1);
            }
            RefreshAddCombo();
            UpdateBackupCount();
        }

        private void MoveSelectedBackup(int delta)
        {
            var index = _lstBackups.SelectedIndex;
            if (index < 0) return;
            var target = index + delta;
            if (target < 0 || target >= _lstBackups.Items.Count) return;

            var item = _lstBackups.Items[index];
            _lstBackups.Items.RemoveAt(index);
            _lstBackups.Items.Insert(target, item);
            _lstBackups.SelectedIndex = target;
            UpdateBackupCount();
        }

        private void UpdateBackupCount()
        {
            if (_networkInfo == null) return;
            _networkInfo = string.Format("备用 {0} 个 · 已知网络 {1} 个 · 附近可见 {2} 个",
                GetBackupList().Count, _savedSsids.Count, _visibleSsids.Count);
            UpdateStatusLine();
        }

        // ---------------- 网络来源 ----------------

        /// <summary>读取 Windows 已保存（已知）网络，填进主用下拉框 / 添加备用下拉框。</summary>
        private void ReloadNetworks(bool requestScan)
        {
            var nets = new List<AvailableNetwork>();
            var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var scan = _wlan.Scan(requestScan);
                // 读取失败时 Scan 会返回上次成功结果的副本，界面照样能显示"附近可见"，不必特殊处理
                if (scan.Networks != null) nets = scan.Networks;
            }
            catch (Exception ex) { AppLog.Error("读取可见网络失败", ex); }

            try { profiles = _wlan.GetProfiles(); }
            catch (Exception ex) { AppLog.Error("读取已保存网络失败", ex); }

            _savedSsids = new HashSet<string>(profiles.Keys, StringComparer.OrdinalIgnoreCase);
            _visibleSsids = nets.Select(n => n.Ssid).Where(s => !string.IsNullOrEmpty(s)).ToList();

            // 主用下拉框只放 Windows 已保存（已知）网络 —— 附近没连过的没有配置文件，本来也连不上
            var savedList = profiles.Keys
                .OrderBy(s => s, StringComparer.CurrentCulture)
                .ToList();
            ApplyItems(_cmbPrimary, savedList);

            RefreshAddCombo();

            if (_wlan.LastScanError == NetworkErrorCodes.AccessDenied)
            {
                _networkInfo = string.Format("备用 {0} 个 · 已知网络 {1} 个；附近网络读取被拒（需管理员权限）",
                    GetBackupList().Count, profiles.Count);
            }
            else
            {
                _networkInfo = string.Format("备用 {0} 个 · 已知网络 {1} 个 · 附近可见 {2} 个",
                    GetBackupList().Count, profiles.Count, nets.Count);

                // 两边都是 0 说明本机根本读不到网络信息（跨设备排查用），给一行指引
                if (profiles.Count == 0 && nets.Count == 0)
                {
                    _networkInfo += "\r\n（若确有已保存 WiFi：请查 log.txt 日志，并确认管理员运行 / WLAN 服务状态）";
                }
            }
            UpdateStatusLine();
        }

        private static void ApplyItems(ComboBox combo, List<string> items)
        {
            var text = combo.Text;
            combo.BeginUpdate();
            combo.Items.Clear();
            foreach (var s in items) combo.Items.Add(s);
            combo.EndUpdate();
            combo.Text = text;
        }

        private void UpdateStatusLine()
        {
            if (_lblStatus == null) return;

            if (_feedbackUntil > DateTime.Now && !string.IsNullOrEmpty(_feedbackText))
            {
                _lblStatus.ForeColor = _feedbackError ? Color.Firebrick : Color.SeaGreen;
                _lblStatus.Text = _feedbackText;
            }
            else
            {
                var text = "状态：" + _engine.StatusText;
                _lblStatus.ForeColor = PickStatusColor(_engine.StatusText);
                _lblStatus.Text = text;
            }

            if (_lblInfo != null)
            {
                _lblInfo.Text = _networkInfo;
            }
        }

        /// <summary>
        /// 按状态标签给状态行着色（emoji 在 GDI 下显不出彩色，颜色改由整行文字承担，
        /// 和托盘图标变灰、TOTAL_FAILURE 红字同一套语义）。
        /// </summary>
        private static Color PickStatusColor(string statusText)
        {
            if (string.IsNullOrEmpty(statusText)) return Color.DimGray;
            if (statusText.IndexOf(StatusTags.Fault, StringComparison.Ordinal) >= 0) return Color.Firebrick;
            if (statusText.IndexOf(StatusTags.Disconnecting, StringComparison.Ordinal) >= 0) return Color.OrangeRed;
            if (statusText.IndexOf(StatusTags.Attention, StringComparison.Ordinal) >= 0) return Color.DarkGoldenrod;
            if (statusText.IndexOf(StatusTags.Limited, StringComparison.Ordinal) >= 0) return Color.DarkGoldenrod;
            if (statusText.IndexOf(StatusTags.Normal, StringComparison.Ordinal) >= 0) return Color.SeaGreen;
            if (statusText.IndexOf(StatusTags.Hold, StringComparison.Ordinal) >= 0) return Color.SteelBlue;
            return Color.DimGray;
        }

        /// <summary>短暂在状态行显示操作结果（绿色成功 / 红色提示），几秒后自动恢复常规状态。</summary>
        private void Flash(string message, bool error = false)
        {
            _feedbackText = (error ? "⚠ " : "✓ ") + message;
            _feedbackError = error;
            _feedbackUntil = DateTime.Now.AddSeconds(6);
            UpdateStatusLine();
        }

        /// <summary>按钮临时改文案 + 变灰，1.5 秒后恢复，给点击一个明确回应。</summary>
        private void PulseButton(Button button, string temporaryText)
        {
            if (button == null) return;
            var original = button.Tag as string ?? button.Text;
            button.Tag = original;
            button.Text = temporaryText;
            button.Enabled = false;

            var timer = new Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                try
                {
                    if (!button.IsDisposed)
                    {
                        button.Text = button.Tag as string ?? original;
                        button.Enabled = true;
                    }
                }
                catch { }
            };
            timer.Start();
        }

        private void ScanNetworks()
        {
            Cursor = Cursors.WaitCursor;
            _btnScan.Enabled = false;
            _btnScan.Text = "刷新中…";

            // 扫描 + 等结果要一两秒，放后台线程，UI 线程只做界面更新
            System.Threading.Tasks.Task.Run(() =>
            {
                string error = null;
                try
                {
                    _wlan.RequestScan();                 // 只发起扫描，不去读（读到的会是旧缓存）
                    System.Threading.Thread.Sleep(1200); // 等驱动扫完
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    AppLog.Error("刷新失败", ex);
                }

                try
                {
                    if (IsDisposed) return; // 窗口已关，不再投递回调
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed) return;
                        try
                        {
                        if (error == null)
                        {
                            ReloadNetworks(false);

                            if (_wlan.LastScanError == NetworkErrorCodes.AccessDenied)
                            {
                                Flash("已刷新，但读取附近网络被拒绝（需要管理员权限）", true);
                            }
                            else
                            {
                                Flash(string.Format("已刷新：已知网络 {0} 个，附近可见 {1} 个，备用 {2} 个",
                                    _savedSsids.Count, _visibleSsids.Count, GetBackupList().Count));
                            }
                        }
                        else
                        {
                            Flash("刷新失败：" + error, true);
                        }
                    }
                    finally
                    {
                        Cursor = Cursors.Default;
                        _btnScan.Text = "刷新";
                        _btnScan.Enabled = true;
                    }
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        /// <summary>保存配置并立即生效，但**不关闭**窗口（要关闭请点右上角 ×）。</summary>
        private void SaveSettings()
        {
            // 先把界面值读出来并校验，校验不过就绝不动 _config ——
            // 否则引擎会立刻看到"没有主用"并进入 Faulted 状态
            var primary = (_cmbPrimary.Text ?? "").Trim();
            if (primary.Length == 0)
            {
                MessageBox.Show("请先选择或填写主用 WiFi 的 SSID。", "铃波",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var backups = GetBackupList();
            var dupPrimary = backups.Any(b => string.Equals(b, primary, StringComparison.OrdinalIgnoreCase));
            backups.RemoveAll(b => string.Equals(b, primary, StringComparison.OrdinalIgnoreCase));

            var delay = (int)_numDelay.Value;
            var signal = (int)_numSignal.Value;
            var cooldown = (int)_numCooldown.Value;
            var confirm = (int)_numConfirm.Value;
            var poll = (int)_numPoll.Value;
            var backoff = _chkBackoff.Checked;
            var autoStart = _chkAutoStart.Checked;

            // 整套赋值放进锁里，引擎线程每轮 Tick 取快照时不会读到"半套"新配置
            lock (_config.SyncRoot)
            {
                _config.PrimarySsid = primary;
                _config.BackupSsids = backups; // 替换为新列表（引擎快照持有的旧列表不受影响）
                _config.ReturnDelaySeconds = delay;
                _config.SignalThresholdPercent = signal;
                _config.CooldownSeconds = cooldown;
                _config.ConfirmScans = confirm;
                _config.PollIntervalSeconds = poll;
                _config.BackoffEnabled = backoff;
                _config.AutoStart = autoStart;
                _config.HoldPolicy = _cmbEthPolicy.SelectedIndex == 1
                    ? EthernetHoldPolicy.AlwaysManage
                    : EthernetHoldPolicy.Auto;
            }

            if (dupPrimary)
            {
                AppLog.Warn("备用列表里的 " + primary + " 与主用相同，已被忽略。");
            }

            // Save 内部会在锁内 Normalize + 序列化，这里不再锁外调 Normalize（避免与引擎快照竞态）
            _config.Save();
            _config.ApplyAutoStart();

            if (!_savedSsids.Contains(_config.PrimarySsid))
            {
                AppLog.Warn("主用 " + _config.PrimarySsid + " 还没有已保存的配置文件，程序无法主动连接它（请先在 Windows 里手动连一次）。");
            }
            foreach (var b in _config.BackupSsids)
            {
                if (!_savedSsids.Contains(b))
                {
                    AppLog.Warn("备用 " + b + " 还没有已保存的配置文件，程序无法主动连接它（请先在 Windows 里手动连一次）。");
                }
            }

            AppLog.Info(string.Format("配置已保存：主用={0}，备用=[{1}]，延迟={2}s，阈值={3}%，冷却={4}s，退避={5}",
                _config.PrimarySsid, string.Join(" → ", _config.BackupSsids), _config.ReturnDelaySeconds,
                _config.SignalThresholdPercent, _config.CooldownSeconds, _config.BackoffEnabled));

            // CheckNow（而非 Wake）：manual tick 会立即跳过有线挂起分支、无视退避，
            // 让有线策略等配置改动在保存后立刻生效，而不是等挂起态 10 秒的下一轮
            _engine.CheckNow();

            Flash(string.Format("已保存并生效：主用 {0}，备用 {1} 个", _config.PrimarySsid, _config.BackupSsids.Count));
            PulseButton(_btnSave, "已保存 ✓");
        }

        /// <summary>取消：把界面恢复到上次保存的状态，不关闭窗口。</summary>
        private void RevertChanges()
        {
            LoadValues();
            ApplyItems(_cmbPrimary, _savedSsids.OrderBy(s => s, StringComparer.CurrentCulture).ToList());
            RefreshAddCombo();
            UpdateBackupCount();
            Flash("已放弃本次修改，界面已恢复到上次保存的状态");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Dispose();
                _statusTimer = null;
            }
            if (_tip != null)
            {
                _tip.Dispose();
                _tip = null;
            }
            base.OnFormClosed(e);
        }
    }
}
