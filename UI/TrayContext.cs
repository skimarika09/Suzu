using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Suzu.Core;
using Suzu.Network;

namespace Suzu.UI
{
    /// <summary>托盘常驻上下文：图标、菜单、气泡通知、单实例握手、设置窗口。</summary>
    public sealed class TrayContext : ApplicationContext
    {
        private readonly AppConfig _config;
        private readonly IWifiProvider _wlan;
        private readonly INetworkVerifier _verifier;
        private readonly SwitchEngine _engine;
        private readonly SynchronizationContext _ui;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _pauseMenu;    // 暂停子菜单（30 分钟 / 1 小时 / 4 小时 / 不定时）
        private readonly ToolStripMenuItem _resumeItem;   // 恢复自动切换
        private readonly Icon _iconNormal;   // 正常状态（取自 Assets/app.ico）
        private readonly Icon _iconGray;     // TOTAL_FAILURE 状态（同一图标的灰色变体）
        private readonly bool _startedByAutoStart;

        private SettingsForm _settingsForm;
        private HistoryForm _historyForm;
        private Thread _pipeThread;
        private volatile bool _disposed;

        /// <summary>用户在免责声明里点了「退出」时为 true，组合根据此不进消息循环。</summary>
        public bool ExitRequested { get; private set; }

        public TrayContext(AppConfig config, IWifiProvider wlan, INetworkVerifier verifier,
                           bool openSettingsOnStart, bool startedByAutoStart)
        {
            _config = config ?? AppConfig.Load();
            _wlan = wlan;
            _verifier = verifier;
            _startedByAutoStart = startedByAutoStart;
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_ui);

            try
            {
                _wlan.Open();
            }
            catch (Exception ex)
            {
                AppLog.Error("初始化 WLAN 失败", ex);
                MessageBox.Show("无法访问 WLAN 服务：\r\n" + ex.Message + "\r\n\r\n请确认：\r\n1) 已用管理员权限运行；\r\n2) WLAN AutoConfig 服务已启动；\r\n3) 本机有无线网卡。",
                    "铃波", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            _engine = new SwitchEngine(_config, _wlan, _verifier);
            _engine.StatusChanged += () => Post(RefreshStatus);
            _engine.Notify += (title, msg) => Post(() => ShowBalloon(title, msg, ToolTipIcon.Info));

            _iconNormal = LoadTrayIcon(false);
            _iconGray = LoadTrayIcon(true);
            _menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("状态：启动中…") { Enabled = false };

            // 暂停改为子菜单：定时档 + 不定时；恢复单独一项
            _pauseMenu = new ToolStripMenuItem("暂停自动切换");
            foreach (var minutes in new[] { 30, 60, 240 })
            {
                var label = minutes >= 60 ? "暂停 " + (minutes / 60) + " 小时" : "暂停 " + minutes + " 分钟";
                var captured = minutes;
                var item = new ToolStripMenuItem(label);
                item.Click += (s, e) => PauseFor(captured);
                _pauseMenu.DropDownItems.Add(item);
            }
            var pauseIndefinite = new ToolStripMenuItem("不定时（手动恢复）");
            pauseIndefinite.Click += (s, e) => PauseFor(0);
            _pauseMenu.DropDownItems.Add(pauseIndefinite);

            _resumeItem = new ToolStripMenuItem("恢复自动切换") { Enabled = _config.Paused };
            _resumeItem.Click += (s, e) => ResumePause();

            var settingsItem = new ToolStripMenuItem("打开设置…");
            settingsItem.Click += (s, e) => ShowSettings();

            var checkItem = new ToolStripMenuItem("立即检查 / 切换");
            checkItem.Click += (s, e) => { _engine.CheckNow(); ShowBalloon("已手动触发", "正在检查网络状态…", ToolTipIcon.Info); };

            var histItem = new ToolStripMenuItem("切换历史");
            histItem.Click += (s, e) => ShowHistory();

            var logItem = new ToolStripMenuItem("打开日志文件");
            logItem.Click += (s, e) => OpenLog();

            var diagItem = new ToolStripMenuItem("导出诊断包");
            diagItem.Click += (s, e) => ExportDiagnostics();

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => ExitApp();

            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(settingsItem);
            _menu.Items.Add(checkItem);
            _menu.Items.Add(_pauseMenu);
            _menu.Items.Add(_resumeItem);
            _menu.Items.Add(histItem);
            _menu.Items.Add(logItem);
            _menu.Items.Add(diagItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _tray = new NotifyIcon
            {
                Icon = _iconNormal,
                Text = "铃波",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _tray.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowSettings();
            };

            StartPipeServer();

            _engine.Start();
            AppLog.Info("托盘已就绪。");
            RefreshStatus();

            // 免责声明：非开机自启时首次运行必弹（弹在设置窗口之前）；
            // 开机自启场景不打扰用户，推迟到他第一次手动打开设置时再弹。
            if (!_config.DisclaimerAccepted && !_startedByAutoStart)
            {
                if (!ShowDisclaimer())
                {
                    AppLog.Info("用户未同意免责声明，退出程序。");
                    ExitRequested = true;
                    DisposeResources();
                    return;
                }
            }

            if (openSettingsOnStart)
            {
                // 首次运行 / 还没配置：直接把设置页面弹出来
                BeginInvoke(() => ShowSettings());
            }
        }

        /// <summary>弹出免责声明；返回 false 表示用户选择退出。</summary>
        private bool ShowDisclaimer()
        {
            try
            {
                using (var form = new DisclaimerForm())
                {
                    if (form.ShowDialog() != DialogResult.OK)
                    {
                        return false;
                    }
                }

                lock (_config.SyncRoot)
                {
                    _config.DisclaimerAccepted = true;
                }
                _config.Save();
                AppLog.Info("用户已确认免责声明。");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("显示免责声明失败", ex);
                return true; // 弹窗本身出问题不该把用户挡在门外
            }
        }

        private void Post(Action action)
        {
            try
            {
                _ui.Post(_ => action(), null);
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        private void BeginInvoke(Action action)
        {
            try { _ui.Post(_ => action(), null); }
            catch { try { action(); } catch { } }
        }

        // ---------------- 单实例握手 ----------------

        /// <summary>监听命名管道：另一个 exe 被双击时，由这里负责把设置窗口调出来。</summary>
        private void StartPipeServer()
        {
            _pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "SingleInstancePipe" };
            _pipeThread.Start();
        }

        private void PipeLoop()
        {
            while (!_disposed)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(SingleInstance.PipeName, PipeDirection.InOut,
                               1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        using (var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true })
                        {
                            var line = reader.ReadLine();
                            if (string.Equals(line, SingleInstance.ShowCommand, StringComparison.Ordinal))
                            {
                                // 先回包让对方进程立刻退出，再把窗口操作抛到 UI 线程
                                writer.WriteLine("OK");
                                Post(ShowSettingsFromSecondInstance);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_disposed) return;
                    AppLog.Warn("单实例管道异常: " + ex.Message);
                    Thread.Sleep(800);
                }
            }
        }

        /// <summary>处理"又双击了一次 exe"：设置已开着就提示，否则打开设置。</summary>
        private void ShowSettingsFromSecondInstance()
        {
            AppLog.Info("检测到重复启动，转到设置窗口。");

            if (_settingsForm != null && !_settingsForm.IsDisposed && _settingsForm.Visible)
            {
                try
                {
                    if (_settingsForm.WindowState == FormWindowState.Minimized)
                    {
                        _settingsForm.WindowState = FormWindowState.Normal;
                    }
                    _settingsForm.Activate();
                    _settingsForm.BringToFront();
                }
                catch { }

                MessageBox.Show(_settingsForm, "设置窗口已经打开了，请在这里修改。", "铃波",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                AppLog.Info("设置窗口已经打开，已提示用户。");
                return;
            }

            ShowSettings();
        }

        // ---------------- 托盘与窗口 ----------------

        private void RefreshStatus()
        {
            if (_disposed) return;
            var text = _engine.StatusText;
            _statusItem.Text = "状态：" + text;
            var tip = "铃波｜" + text;
            _tray.Text = tip.Length > 120 ? tip.Substring(0, 120) : tip;
            _pauseMenu.Text = _config.Paused ? "已暂停自动切换" : "暂停自动切换";
            _resumeItem.Enabled = _config.Paused;

            // TOTAL_FAILURE 时图标变灰，一眼看出"所有网络都不可用"
            try
            {
                var wantGray = _engine.State == EngineState.TotalFailure;
                var icon = wantGray ? _iconGray : _iconNormal;
                if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
            }
            catch { }
        }

        /// <summary>暂停自动切换。minutes &gt; 0 为定时暂停（到期引擎自动恢复），0 = 不定时（手动恢复）。</summary>
        private void PauseFor(int minutes)
        {
            lock (_config.SyncRoot)
            {
                _config.Paused = true;
                _config.PauseUntilTick = minutes > 0 ? Environment.TickCount64 + minutes * 60000L : 0;
            }
            _config.Save();
            AppLog.Info(minutes > 0
                ? "用户暂停自动切换 " + minutes + " 分钟（到期自动恢复）。"
                : "用户暂停自动切换（不定时，手动恢复）。");
            _engine.Wake();
            RefreshStatus();
            ShowBalloon("已暂停自动切换",
                minutes > 0 ? minutes + " 分钟后自动恢复。" : "右键菜单可随时恢复。",
                ToolTipIcon.Info);
        }

        /// <summary>手动恢复自动切换（清除定时暂停）。</summary>
        private void ResumePause()
        {
            lock (_config.SyncRoot)
            {
                _config.Paused = false;
                _config.PauseUntilTick = 0;
            }
            _config.Save();
            AppLog.Info("用户恢复了自动切换。");
            _engine.Wake();
            RefreshStatus();
            ShowBalloon("已恢复自动切换", "程序将按规则自动管理 WiFi。", ToolTipIcon.Info);
        }

        /// <summary>切换历史面板（单实例，与设置窗口同套唤起逻辑）。</summary>
        private void ShowHistory()
        {
            if (_historyForm != null && !_historyForm.IsDisposed)
            {
                _historyForm.Activate();
                _historyForm.BringToFront();
                return;
            }
            _historyForm = new HistoryForm(_engine.History);
            _historyForm.FormClosed += (s, e) => _historyForm = null;
            _historyForm.Show();
        }

        /// <summary>非模态打开设置窗口；已经开着就激活它。</summary>
        private void ShowSettings()
        {
            try
            {
                if (_settingsForm != null && !_settingsForm.IsDisposed)
                {
                    if (!_settingsForm.Visible) _settingsForm.Show();
                    if (_settingsForm.WindowState == FormWindowState.Minimized)
                    {
                        _settingsForm.WindowState = FormWindowState.Normal;
                    }
                    _settingsForm.Activate();
                    _settingsForm.BringToFront();
                    return;
                }

                // 开机自启时没弹过的免责声明，这里补上（在设置窗口之前）
                if (!_config.DisclaimerAccepted)
                {
                    if (!ShowDisclaimer())
                    {
                        AppLog.Info("用户未同意免责声明，退出程序。");
                        ExitApp();
                        return;
                    }
                }

                var form = new SettingsForm(_config, _wlan, _engine);
                form.FormClosed += (s, e) =>
                {
                    _settingsForm = null;
                    RefreshStatus();
                    _engine.Wake();
                };
                _settingsForm = form;
                form.Show();
                form.Activate();
                form.BringToFront();
            }
            catch (Exception ex)
            {
                AppLog.Error("打开设置窗口失败", ex);
                MessageBox.Show("打开设置失败：" + ex.Message, "铃波",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowBalloon(string title, string message, ToolTipIcon icon)
        {
            if (_disposed) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = message;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(4000);
            }
            catch { }
        }

        /// <summary>托盘菜单：导出诊断包（后台打包，完成后气泡提示路径）。</summary>
        private void ExportDiagnostics()
        {
            try
            {
                var lines = Suzu.Network.SystemSnapshot.BuildLines();
                var zipPath = DiagnosticBundle.Export(lines);
                AppLog.Info("已导出诊断包: " + zipPath);
                ShowBalloon("诊断包已导出", zipPath, ToolTipIcon.Info);
                try
                {
                    Process.Start("explorer.exe", "/select,\"" + zipPath + "\"");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("打开资源管理器失败：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("导出诊断包失败", ex);
                ShowBalloon("导出失败", ex.Message, ToolTipIcon.Error);
            }
        }
        private void OpenLog()
        {
            try
            {
                if (File.Exists(AppLog.LogFilePath))
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppLog.LogFilePath + "\"") { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show("日志文件还不存在。", "铃波");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开日志失败：" + ex.Message, "铃波");
            }
        }

        private void ExitApp()
        {
            AppLog.Info("用户退出程序。");
            DisposeResources();
            ExitThread();
        }

        private void DisposeResources()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_settingsForm != null && !_settingsForm.IsDisposed)
                {
                    _settingsForm.Dispose();
                    _settingsForm = null;
                }
            }
            catch { }

            try
            {
                if (_historyForm != null && !_historyForm.IsDisposed)
                {
                    _historyForm.Dispose();
                    _historyForm = null;
                }
            }
            catch { }

            try { _engine.Dispose(); } catch { }
            try { _wlan.Dispose(); } catch { }
            try
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch { }
            try { _menu.Dispose(); } catch { }
            try { _iconNormal.Dispose(); } catch { }
            try { _iconGray.Dispose(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeResources();
            base.Dispose(disposing);
        }

        /// <summary>
        /// 托盘图标统一取自 Assets/app.ico（与 exe 图标同一份，保证托盘/任务栏/资源管理器一致）。
        /// 灰色变体不另做一份 ico：把同一张图转换成灰度再打包成 Icon，形状与正常图标完全相同。
        /// 各窗体（设置/免责/历史）的标题栏与任务栏图标也走这里，保证全局统一。
        /// </summary>
        private static Icon _formIcon;

        internal static Icon GetFormIcon()
        {
            if (_formIcon == null) _formIcon = LoadTrayIcon(false);
            return _formIcon;
        }

        internal static Icon LoadTrayIcon(bool gray)
        {
            try
            {
                var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("Suzu.Assets.app.ico");
                if (stream != null)
                {
                    using (stream)
                    using (var baseIcon = new Icon(stream, 32, 32))
                    {
                        if (!gray) return (Icon)baseIcon.Clone();
                        return MakeGrayIcon(baseIcon);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("加载内置图标失败，退回运行时绘制：" + ex.Message);
            }

            return DrawTrayIcon(gray);
        }

        /// <summary>把图标转成灰度（用于 TOTAL_FAILURE）。</summary>
        private static Icon MakeGrayIcon(Icon source)
        {
            using (var bmp = source.ToBitmap())
            using (var gray = new Bitmap(bmp.Width, bmp.Height))
            {
                using (var g = Graphics.FromImage(gray))
                using (var attributes = new System.Drawing.Imaging.ImageAttributes())
                {
                    var matrix = new System.Drawing.Imaging.ColorMatrix(
                        new[]
                        {
                            new float[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                            new float[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                            new float[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                            new float[] { 0, 0, 0, 0.75f, 0 },
                            new float[] { 0, 0, 0, 0, 1 }
                        });
                    attributes.SetColorMatrix(matrix);
                    g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height),
                        0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attributes);
                }

                var hIcon = gray.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        /// <summary>兜底：内置图标不可用时现场画一个（正常/灰色都支持）。</summary>
        private static Icon DrawTrayIcon(bool gray)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var bg = new SolidBrush(gray ? Color.FromArgb(128, 128, 128) : Color.FromArgb(0, 122, 204)))
                    {
                        g.FillEllipse(bg, 0, 0, 31, 31);
                    }
                    using (var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawArc(pen, 6, 9, 20, 20, 225, 90);
                        g.DrawArc(pen, 11, 14, 10, 10, 225, 90);
                    }
                    using (var dot = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(dot, 14, 21, 4.5f, 4.5f);
                    }
                }

                var hIcon = bmp.GetHicon();
                Icon icon;
                try
                {
                    icon = (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    // FromHandle 不拥有句柄，Clone 之后原件要还给系统
                    DestroyIcon(hIcon);
                }
                return icon;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}

