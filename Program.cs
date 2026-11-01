using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Suzu.Core;
using Suzu.Network;
using Suzu.UI;

namespace Suzu
{
    /// <summary>
    /// 组合根：在这里唯一一次 new 出 Windows 实现并注入 Core，
    /// Core / UI 只认 IWifiProvider / INetworkVerifier / IAutoStart。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = @"Global\Suzu_SingleInstance_Mutex";
        private const string AutoStartArg = "--autostart";

        [STAThread]
        private static void Main(string[] args)
        {
            // 开机自启时不弹任何窗口，安静进托盘
            var autoStart = args != null &&
                            args.Any(a => string.Equals(a, AutoStartArg, StringComparison.OrdinalIgnoreCase));

            bool configExisted = File.Exists(AppConfig.ConfigFilePath);

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已经有实例在跑：不新建进程窗口，而是让它把设置窗口调出来
                    var handled = SingleInstance.TrySendShowRequest();
                    if (!handled)
                    {
                        MessageBox.Show(
                            "铃波已经在运行了。\r\n请到系统托盘（右下角）双击那个 WiFi 图标打开设置。",
                            "铃波", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* 旧系统不支持 */ }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AppLog.Init();
                AppLog.Info("========== 启动 v" + typeof(Program).Assembly.GetName().Version +
                            (autoStart ? "（开机自启）" : "") + " ==========");

                if (!IsAdministrator())
                {
                    AppLog.Warn("当前进程不是管理员权限，WLAN 连接操作可能静默失败。");
                    MessageBox.Show("本程序需要管理员权限才能切换 WiFi。\r\n请右键 → 以管理员身份运行。",
                        "铃波", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // 顶层异常兜底，避免托盘程序静默退出
                Application.ThreadException += (s, e) => AppLog.Error("UI 线程异常: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => AppLog.Error("未处理异常: " + e.ExceptionObject);
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => AppLog.Error("后台任务异常: " + e.Exception);

                // ---- 组合根：唯一一处 new 具体实现 ----
                var autoStarter = new WindowsAutoStarter();
                var config = AppConfig.Load();
                config.AutoStarter = autoStarter;
                if (!configExisted)
                {
                    // 配置文件是新建的（比如用户删了配置），沿用注册表里真实的开机自启状态
                    config.AutoStart = autoStarter.IsEnabled();
                    config.Save();
                }

                // 配置里开了自启但注册表项可能指向旧路径/旧名字（升级场景）→ 启动时对齐一次
                if (config.AutoStart)
                {
                    config.ApplyAutoStart();
                }

                // 首次安装 / 还没配置主用 → 直接把设置页面弹出来（开机自启时除外）
                var needSetup = !configExisted || !config.HasPrimary;
                var openSettingsOnStart = needSetup && !autoStart;
                if (openSettingsOnStart)
                {
                    AppLog.Info(configExisted ? "主用未配置，启动时打开设置窗口。" : "首次运行，启动时打开设置窗口。");
                }

                var wlan = new WindowsWlanProvider();
                var verifier = new WindowsNetworkVerifier();
                var context = new TrayContext(config, wlan, verifier, openSettingsOnStart, autoStart);
                if (context.ExitRequested)
                {
                    // 免责声明里点了「退出」：不进托盘，直接结束
                    context.Dispose();
                    return;
                }

                Application.Run(context);
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
