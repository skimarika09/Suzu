using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suzu.Core
{
        /// <summary>检测到可用有线时的策略。</summary>
        public enum EthernetHoldPolicy
        {
                /// <summary>自动：有线真有外网（Level 2）时挂起 WiFi 管理。</summary>
                Auto = 0,
                /// <summary>始终管理 WiFi：忽略有线（适合有线限速、WiFi 更快的场景）。</summary>
                AlwaysManage = 1
        }

        /// <summary>配置模型，持久化到 %APPDATA%\Suzu\config.json。</summary>
        public class AppConfig
        {
    
            /// <summary>
            /// 跨线程读写锁：UI 线程保存配置时在此锁内改字段，
            /// 引擎线程每轮 Tick 用 CloneForRead() 在锁内取快照，避免读到"半套"新值。
            /// </summary>
            public readonly object SyncRoot = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>主用 WiFi 的 SSID。</summary>
        public string PrimarySsid { get; set; } = "";

        /// <summary>备用 WiFi 列表，顺序即优先级（越靠前越先尝试）。</summary>
        public List<string> BackupSsids { get; set; } = new List<string>();

        /// <summary>旧版单一备用字段，仅用于兼容旧 config.json，读取后会迁移进 BackupSsids。</summary>
        [JsonPropertyName("backupSsid")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string LegacyBackupSsid { get; set; }

        /// <summary>主用稳定出现后延迟多少秒再切回；0 = 立即。</summary>
        public int ReturnDelaySeconds { get; set; } = 30;
        /// <summary>允许切回的最小信号百分比；0 = 只要求 SSID 存在。</summary>
        public int SignalThresholdPercent { get; set; } = 50;
        /// <summary>成功切换后的冷却秒数（掉线应急切换不受限）。</summary>
        public int CooldownSeconds { get; set; } = 60;
        /// <summary>失败退避开关。</summary>
        public bool BackoffEnabled { get; set; } = true;
        /// <summary>开机自启。</summary>
        public bool AutoStart { get; set; }
        /// <summary>暂停自动切换（重启后保留）。</summary>
        public bool Paused { get; set; }
        /// <summary>定时暂停的到期时间（TickCount64 毫秒；0 = 不定时，手动恢复）。
        /// TickCount64 跨重启不连续：重启后若已"到期"则直接视为恢复，方向安全。</summary>
        public long PauseUntilTick { get; set; }
        /// <summary>连续多少次扫描都发现主用才认为"稳定"。</summary>
        public int ConfirmScans { get; set; } = 3;
        /// <summary>兜底轮询间隔（秒）。</summary>
        public int PollIntervalSeconds { get; set; } = 10;

        /// <summary>检测到可用有线时的策略。</summary>
        public EthernetHoldPolicy HoldPolicy { get; set; } = EthernetHoldPolicy.Auto;

        /// <summary>设置窗口上次的位置与大小（0 = 未记录，用默认居中）。</summary>
        public int WindowLeft { get; set; }
        public int WindowTop { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }

        [JsonIgnore]
        public static string ConfigFilePath
        {
            get { return Path.Combine(Core.AppPaths.DataDirectory, "config.json"); }
        }

        /// <summary>开机自启实现（组合根注入；Core 不依赖注册表）。</summary>
        [JsonIgnore]
        public IAutoStart AutoStarter { get; set; }

        /// <summary>是否已同意免责声明（首次启动弹窗用）。</summary>
        public bool DisclaimerAccepted { get; set; }

        public static AppConfig Load()
        {
            var path = ConfigFilePath;
            if (!File.Exists(path))
            {
                var fresh = new AppConfig();
                fresh.Save();
                return fresh;
            }

            var cfg = TryRead(path);
            if (cfg != null)
            {
                cfg.Normalize();
                return cfg;
            }

            // 主文件坏了就试 .bak（Save 用 File.Replace，会留一份备份）
            var bakPath = path + ".bak";
            if (File.Exists(bakPath))
            {
                Core.AppLog.Warn("config.json 解析失败，尝试从 config.json.bak 恢复。");
                var recovered = TryRead(bakPath);
                if (recovered != null)
                {
                    recovered.Normalize();
                    recovered.Save();
                    Core.AppLog.Info("已从备份恢复配置。");
                    return recovered;
                }
            }

            Core.AppLog.Error("读取配置失败，使用默认值。");
            return new AppConfig();
        }

        private static AppConfig TryRead(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Core.AppLog.Error("解析配置文件失败: " + path, ex);
                return null;
            }
        }

        /// <summary>引擎线程取只读快照：MemberwiseClone 复制所有标量字段；
        /// BackupSsids 仍是共享引用，但 UI 侧保存时总是替换为新列表而不是原地改动。</summary>
        public AppConfig CloneForRead()
        {
            lock (SyncRoot)
            {
                return (AppConfig)MemberwiseClone();
            }
        }

        public void Save()
        {
            try
            {
                string json;
                // Normalize 会改写字段，序列化读字段 —— 都放进锁里，
                // 与引擎线程 CloneForRead() 的快照互斥，不会保存/读到"半套"值
                lock (SyncRoot)
                {
                    Normalize();
                    json = JsonSerializer.Serialize(this, JsonOptions);
                }
                var path = ConfigFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path))
                {
                    // 原子替换并留 .bak 备份；Delete+Move 在中间崩溃会直接丢配置
                    File.Replace(tmp, path, path + ".bak");
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Core.AppLog.Error("保存配置失败", ex);
            }
        }

        public void Normalize()
        {
            PrimarySsid = (PrimarySsid ?? "").Trim();

            // 备用列表：去空、去重（忽略大小写）、去主用，保持优先级顺序
            var cleaned = new List<string>();
            if (BackupSsids != null)
            {
                foreach (var raw in BackupSsids)
                {
                    var item = (raw ?? "").Trim();
                    if (item.Length == 0) continue;
                    if (string.Equals(item, PrimarySsid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cleaned.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase))) continue;
                    cleaned.Add(item);
                }
            }

            // 兼容旧版单一备用字段
            var legacy = (LegacyBackupSsid ?? "").Trim();
            if (cleaned.Count == 0 && legacy.Length > 0 &&
                !string.Equals(legacy, PrimarySsid, StringComparison.OrdinalIgnoreCase))
            {
                cleaned.Add(legacy);
            }
            BackupSsids = cleaned;
            LegacyBackupSsid = null;

            if (ReturnDelaySeconds < 0) ReturnDelaySeconds = 0;
            if (ReturnDelaySeconds > 3600) ReturnDelaySeconds = 3600;
            if (SignalThresholdPercent < 0) SignalThresholdPercent = 0;
            if (SignalThresholdPercent > 100) SignalThresholdPercent = 100;
            if (CooldownSeconds < 0) CooldownSeconds = 0;
            if (CooldownSeconds > 3600) CooldownSeconds = 3600;
            if (ConfirmScans < 1) ConfirmScans = 1;
            if (ConfirmScans > 10) ConfirmScans = 10;
            if (PollIntervalSeconds < 3) PollIntervalSeconds = 3;
            if (PollIntervalSeconds > 120) PollIntervalSeconds = 120;
        }

        [JsonIgnore]
        public bool HasPrimary { get { return !string.IsNullOrWhiteSpace(PrimarySsid); } }

        [JsonIgnore]
        public bool HasBackup { get { return BackupSsids != null && BackupSsids.Count > 0; } }

        /// <summary>该 SSID 是否在备用列表里。</summary>
        public bool IsBackupSsid(string ssid)
        {
            if (string.IsNullOrEmpty(ssid) || BackupSsids == null) return false;
            return BackupSsids.Any(b => string.Equals(b, ssid.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>返回 "备用 2/4：XXX" 这种带优先级的标签。</summary>
        public string DescribeBackup(string ssid)
        {
            if (BackupSsids == null || BackupSsids.Count == 0) return "备用：" + ssid;
            var idx = BackupSsids.FindIndex(b => string.Equals(b, (ssid ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return "备用：" + ssid;
            return string.Format("备用 {0}/{1}：{2}", idx + 1, BackupSsids.Count, ssid);
        }

        /// <summary>写入/删除开机自启（由组合根注入具体实现，Core 不碰注册表）。</summary>
        public void ApplyAutoStart()
        {
            if (AutoStarter == null)
            {
                Core.AppLog.Warn("未注入开机自启实现，跳过设置。");
                return;
            }

            try
            {
                AutoStarter.SetEnabled(AutoStart);
                Core.AppLog.Info(AutoStart ? "已开启开机自启" : "已关闭开机自启");
            }
            catch (Exception ex)
            {
                Core.AppLog.Error("设置开机自启失败", ex);
            }
        }

        /// <summary>查询开机自启是否已登记（未注入实现时返回 false）。</summary>
        public bool IsAutoStartRegistered()
        {
            if (AutoStarter == null) return false;
            try
            {
                return AutoStarter.IsEnabled();
            }
            catch
            {
                return false;
            }
        }
    }
}
