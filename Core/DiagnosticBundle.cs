using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Suzu.Core
{
    /// <summary>
    /// 一键诊断导出：把日志、脱敏配置、系统信息打成一个 zip 放到桌面。
    /// 纯文件 IO，不引用任何 Windows 专属 API（网卡明细由 UI/Network 层传入）。
    /// </summary>
    public static class DiagnosticBundle
    {
        /// <summary>
        /// 导出诊断包，返回 zip 的完整路径。
        /// systemInfoLines：由调用方（Network 层）准备好的系统/网卡信息，逐行写入。
        /// 失败时抛异常，由 UI 层提示。
        /// </summary>
        public static string Export(IEnumerable<string> systemInfoLines)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var workDir = Path.Combine(Path.GetTempPath(), "Suzu-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                // ---- 1. 日志 ----
                var logPath = AppLog.LogFilePath;
                if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                {
                    File.Copy(logPath, Path.Combine(workDir, "log.txt"), true);
                }

                var oldLog = string.IsNullOrEmpty(logPath)
                    ? null
                    : Path.Combine(Path.GetDirectoryName(logPath) ?? "", "log.old.txt");
                if (!string.IsNullOrEmpty(oldLog) && File.Exists(oldLog))
                {
                    File.Copy(oldLog, Path.Combine(workDir, "log.old.txt"), true);
                }

                // ---- 2. 脱敏配置 ----
                File.WriteAllText(Path.Combine(workDir, "config.json"), BuildMaskedConfig(), new UTF8Encoding(false));

                // ---- 3. 系统信息 ----
                var sb = new StringBuilder();
                sb.AppendLine("铃波（Suzu）诊断包");
                sb.AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("程序版本: " + GetAppVersion());
                sb.AppendLine();
                if (systemInfoLines != null)
                {
                    foreach (var line in systemInfoLines) sb.AppendLine(line);
                }
                File.WriteAllText(Path.Combine(workDir, "系统信息.txt"), sb.ToString(), new UTF8Encoding(false));

                // ---- 4. 说明 ----
                var readme = new StringBuilder();
                readme.AppendLine("这是什么：铃波（Suzu）的一键诊断包，用于把问题现场发给他人排查。");
                readme.AppendLine("隐私提醒：log.txt 里可能包含你连接过的 WiFi 名称（SSID），公开发布前请自行打码；config.json 已自动脱敏（SSID 显示为 NET-1/NET-2）。");
                readme.AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                readme.AppendLine("本包不含任何 WiFi 密码（配置文件里本来就没有密码，Windows 的 profile XML 也未被收集）。");
                File.WriteAllText(Path.Combine(workDir, "说明.txt"), readme.ToString(), new UTF8Encoding(false));

                // ---- 5. 打包到桌面（取不到/写不进就退回数据目录）----
                var targetDir = ResolveTargetDirectory();
                var fileName = "Suzu-诊断包-" + stamp + ".zip";
                var zipPath = Path.Combine(targetDir, fileName);
                try
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    ZipFile.CreateFromDirectory(workDir, zipPath, CompressionLevel.Optimal, false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    // 桌面被重定向到只读位置 / 受控环境：退回数据目录
                    var fallbackDir = AppPaths.DataDirectory;
                    Directory.CreateDirectory(fallbackDir);
                    zipPath = Path.Combine(fallbackDir, fileName);
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    ZipFile.CreateFromDirectory(workDir, zipPath, CompressionLevel.Optimal, false);
                }
                return zipPath;
            }
            finally
            {
                try { Directory.Delete(workDir, true); } catch { }
            }
        }

        /// <summary>配置文件脱敏后序列化（SSID 按顺序替换为 NET-1、NET-2…）。</summary>
        public static string BuildMaskedConfig()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            AppConfig cfg;
            try
            {
                cfg = File.Exists(AppConfig.ConfigFilePath)
                    ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppConfig.ConfigFilePath), options)
                    : new AppConfig();
            }
            catch
            {
                cfg = new AppConfig();
            }

            if (cfg == null) cfg = new AppConfig();

            var index = 1;
            if (!string.IsNullOrEmpty(cfg.PrimarySsid)) cfg.PrimarySsid = "NET-" + index++;
            if (cfg.BackupSsids != null)
            {
                for (int i = 0; i < cfg.BackupSsids.Count; i++)
                {
                    if (!string.IsNullOrEmpty(cfg.BackupSsids[i])) cfg.BackupSsids[i] = "NET-" + index++;
                }
            }
            cfg.LegacyBackupSsid = null;

            return JsonSerializer.Serialize(cfg, options);
        }

        private static string ResolveTargetDirectory()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
            }
            catch
            {
            }

            var fallback = AppPaths.DataDirectory;
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private static string GetAppVersion()
        {
            try
            {
                var asm = typeof(AppConfig).Assembly.GetName();
                return asm.Version == null ? asm.Name : (asm.Name + " " + asm.Version);
            }
            catch
            {
                return "未知";
            }
        }
    }
}
