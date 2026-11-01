using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Suzu.Core;

namespace Suzu.Network
{
    /// <summary>
    /// Windows 下的开机自启实现：注册计划任务（登录时触发、最高权限运行）。
    ///
    /// 为什么不用 HKCU\...\Run：本程序 manifest 是 requireAdministrator，
    /// Windows 登录时对 Run 项里需要提权的 exe 会**静默跳过**（不弹 UAC），
    /// 导致"注册表写对了、重启后程序却没启动"。计划任务的 /RL HIGHEST
    /// 可以在登录时静默以管理员身份启动，无需 UAC 弹窗，这是标准做法。
    /// 启动参数仍带 --autostart，程序据此静默运行、不弹任何窗口。
    /// </summary>
    public sealed class WindowsAutoStarter : IAutoStart
    {
        private const string TaskName = "Suzu";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        /// <summary>历史遗留：v1.1.0 早期曾用 Run 项注册自启，读取时兼容、写入时清理。</summary>
        private const string LegacyRunValueName = "WenLian";
        /// <summary>历史遗留：v1.1.x 时代的计划任务名（应用还叫 WenLian），升级时清理。</summary>
        private const string LegacyTaskName = "WenLian";

        public bool IsEnabled()
        {
            return TaskExists() || LegacyRunValueExists() || LegacyTaskExists();
        }

        public void SetEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    RemoveLegacyRunValue(); // 防止旧 Run 项和计划任务同时启动两个实例
                    RemoveLegacyTask();     // 同理清理更名前的旧计划任务
                    var exe = Environment.ProcessPath
                              ?? Path.Combine(AppContext.BaseDirectory, "Suzu.exe");

                    // /TR 的值整体含空格，必须外层带引号；exe 路径内嵌 \" 转义引号。
                    // 注意收尾只留一个外层引号定界符——多写一个会被 schtasks 原样存进任务，
                    // 变成 "...exe" --autostart""（结尾杂散引号），登录启动时解析失败。
                    var tr = "\\\"" + exe + "\\\" --autostart";
                    var args = "/Create /F /SC ONLOGON /RL HIGHEST /TN \"" + TaskName + "\" /TR \"" + tr + "\"";
                    if (RunSchTasks(args, "注册开机自启计划任务"))
                    {
                        AppLog.Info("已注册开机自启计划任务: " + exe);
                    }
                }
                else
                {
                    // 任务不存在时别再走 /Delete——之前会记一条"找不到指定的文件"的 WARN，让用户以为出了问题
                    if (TaskExists())
                    {
                        RunSchTasks("/Delete /F /TN \"" + TaskName + "\"", "删除开机自启计划任务");
                    }
                    else
                    {
                        AppLog.Info("开机自启任务本就不存在，无需删除。");
                    }
                    RemoveLegacyRunValue();
                    RemoveLegacyTask();
                    AppLog.Info("已关闭开机自启。");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("设置开机自启失败", ex);
            }
        }

        /// <summary>执行 schtasks 并返回是否成功；失败原因写日志。</summary>
        private static bool RunSchTasks(string args, string what)
        {
            try
            {
                using (var proc = new Process())
                {
                    proc.StartInfo = new ProcessStartInfo("schtasks.exe", args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    proc.Start();
                    var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    proc.WaitForExit(10000);
                    if (proc.ExitCode == 0) return true;
                    AppLog.Warn(what + "失败（exit=" + proc.ExitCode + "）：" + output.Trim());
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(what + "异常", ex);
                return false;
            }
        }

        private static bool TaskExists()
        {
            try
            {
                using (var proc = new Process())
                {
                    proc.StartInfo = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    proc.Start();
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>旧版用 HKCU Run 项注册过自启（对 requireAdministrator 程序无效且被系统跳过），清掉。</summary>
        private static void RemoveLegacyRunValue()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null && key.GetValue(LegacyRunValueName) != null)
                    {
                        key.DeleteValue(LegacyRunValueName, false);
                        AppLog.Info("已清理旧版 Run 项开机自启（改用计划任务）。");
                    }
                }
            }
            catch { }
        }

        private static bool LegacyRunValueExists()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(LegacyRunValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>更名前（应用还叫 WenLian）注册的计划任务，升级后清掉，防止同名任务指向旧 exe。</summary>
        private static void RemoveLegacyTask()
        {
            try
            {
                if (LegacyTaskExists())
                {
                    RunSchTasks("/Delete /F /TN \"" + LegacyTaskName + "\"", "清理旧版计划任务");
                    AppLog.Info("已清理旧版计划任务 " + LegacyTaskName + "。");
                }
            }
            catch { }
        }

        private static bool LegacyTaskExists()
        {
            try
            {
                using (var proc = new Process())
                {
                    proc.StartInfo = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + LegacyTaskName + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    proc.Start();
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
