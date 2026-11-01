using System;
using System.IO;

namespace Suzu.Core
{
    /// <summary>
    /// 数据目录解析：优先 %APPDATA%\Suzu，若不可写则逐级回退。
    /// 可用环境变量 SUZU_DATA_DIR 强制指定（便于测试/绿色部署，兼容更名前的 WENLIAN_DATA_DIR / AUTOWIFISWITCH_DATA_DIR）。
    /// 首次运行会自动从旧名（AutoWiFiSwitch → WenLian）的数据目录迁移用户数据。
    /// </summary>
    public static class AppPaths
    {
        public const string AppFolderName = "Suzu";
        /// <summary>上一代应用的数据目录名（迁移来源；更名链 AutoWiFiSwitch → WenLian → Suzu）。</summary>
        private const string LegacyFolderName = "WenLian";

        private static readonly object Sync = new object();
        private static string _dataDir;

        public static string DataDirectory
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                lock (Sync)
                {
                    if (_dataDir != null) return _dataDir;
                    _dataDir = Resolve();
                    var legacy = LegacyDataDirectory;
                    if (!string.IsNullOrEmpty(legacy) && _dataDir != null && MigrateIfNeeded(legacy)) { }
                    return _dataDir;
                }
            }
        }

        /// <summary>旧版（AutoWiFiSwitch）的数据目录，仅用于迁移。</summary>
        public static string LegacyDataDirectory
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static string Resolve()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("SUZU_DATA_DIR"),
                Environment.GetEnvironmentVariable("WENLIAN_DATA_DIR"), // 更名前的环境变量，兼容保留
                Environment.GetEnvironmentVariable("AUTOWIFISWITCH_DATA_DIR"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName),
                Path.Combine(AppContext.BaseDirectory, "data")
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (IsWritable(candidate)) return candidate;
            }

            // 全都不行时返回最后一项，后续写入会失败但不会崩溃
            return candidates[candidates.Length - 1];
        }

        /// <summary>
        /// 老用户升级：新目录里还没有 config.json 而旧目录里有 → 整个目录复制过来。
        /// 旧目录保留不删（由用户自行清理）。
        /// </summary>
        private static bool MigrateIfNeeded(string legacyDir)
        {
            try
            {
                if (!Directory.Exists(legacyDir)) return false;
                var newConfig = Path.Combine(_dataDir, "config.json");
                if (File.Exists(newConfig)) return false;

                var legacyConfig = Path.Combine(legacyDir, "config.json");
                Directory.CreateDirectory(_dataDir);

                foreach (var file in Directory.GetFiles(legacyDir))
                {
                    var target = Path.Combine(_dataDir, Path.GetFileName(file));
                    if (File.Exists(target)) continue;
                    try { File.Copy(file, target, false); } catch { }
                }

                if (File.Exists(legacyConfig))
                {
                    AppLog.Info("已从旧版本数据目录迁移用户数据。");
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, DateTime.Now.ToString("O"));
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
