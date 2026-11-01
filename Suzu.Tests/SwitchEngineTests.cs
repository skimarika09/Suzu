using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Suzu.Core;
using Xunit;

namespace Suzu.Tests
{
    /// <summary>
    /// SwitchEngine 状态机场景测试：用假的 IWifiProvider / INetworkVerifier 驱动**真实的**引擎线程，
    /// 只通过公开 API（Start / CheckNow / Wake / StatusText / State / History / Notify）观察行为。
    /// 配置取最快节奏（切回延迟 0、确认 1 次、无冷却、无退避），让每条判定路径在几秒内走完；
    /// 引擎里少数硬编码的真实 Sleep（切回前复查 2.5s、DHCP 等待 1.5s、降级解除确认 15s）按原时长等待。
    /// 每个用例对应 README「均已做本机验证」或版本记录里一条真机修出来的行为。
    /// </summary>
    public sealed class SwitchEngineTests : IDisposable
    {
        private const string Primary = "Home";
        private const string Backup1 = "Backup1";
        private const string Backup2 = "Backup2";

        private readonly FakeWifiProvider _wifi = new FakeWifiProvider();
        private readonly FakeVerifier _verifier;
        private readonly AppConfig _config;
        private SwitchEngine _engine;
        private readonly List<string[]> _notifications = new List<string[]>(); // [title, message]

        public SwitchEngineTests()
        {
            _verifier = new FakeVerifier(_wifi);
            _config = new AppConfig
            {
                PrimarySsid = Primary,
                BackupSsids = new System.Collections.Generic.List<string> { Backup1, Backup2 },
                ReturnDelaySeconds = 0,       // 出现即切回
                ConfirmScans = 1,             // 一次确认即算稳定
                CooldownSeconds = 0,
                BackoffEnabled = false,
                SignalThresholdPercent = 0,   // 只要求 SSID 可见
                PollIntervalSeconds = 3,      // 引擎的下限
                HoldPolicy = EthernetHoldPolicy.Auto
            };
        }

        public void Dispose()
        {
            if (_engine != null) _engine.Dispose();
        }

        // ---------------- 便捷方法 ----------------

        private void Start()
        {
            _engine = new SwitchEngine(_config, _wifi, _verifier);
            _engine.Notify += delegate (string t, string m) { lock (_notifications) { _notifications.Add(new[] { t, m }); } };
            _engine.Start();
        }

        private bool WaitUntil(Func<bool> condition, int timeoutMs, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return condition();
        }

        private string Diag()
        {
            string hist = "";
            string notif = "";
            if (_engine != null)
            {
                hist = string.Join("; ", _engine.History.Snapshot()
                    .Select(h => h.Action + " " + h.From + "->" + h.To));
            }
            lock (_notifications)
            {
                notif = string.Join("; ", _notifications.Select(n => n[0]));
            }
            string conn;
            lock (_wifi.ConnectLog) { conn = string.Join(",", _wifi.ConnectLog.ToArray()); }
            var state = _engine == null ? "<null>" : _engine.State.ToString();
            var status = _engine == null ? "<null>" : _engine.StatusText;
            var cur = _engine == null ? "<null>" : (_engine.CurrentSsid ?? "<未连接>");
            return string.Format("State={0} Status=\"{1}\" Current={2} Connects=[{3}] History=[{4}] Notify=[{5}]",
                state, status, cur, conn, hist, notif);
        }

        private void AssertEventually(Func<bool> condition, int timeoutMs, string what)
        {
            if (!WaitUntil(condition, timeoutMs, what))
            {
                Assert.Fail("等待超时（" + timeoutMs + "ms）：" + what + "。" + Diag());
            }
        }

        private bool HasNotification(string titleContains)
        {
            lock (_notifications)
            {
                return _notifications.Any(n => n[0] != null && n[0].Contains(titleContains));
            }
        }

        private bool HistoryHas(string actionContains)
        {
            return _engine.History.Snapshot().Any(h => h.Action.Contains(actionContains));
        }

        private static AvailableNetwork Net(string ssid, int signal)
        {
            return new AvailableNetwork { Ssid = ssid, SignalQuality = signal, Connectable = true, HasProfile = true };
        }

        // ---------------- 用例 ----------------

        [Fact]
        public void 主用正常_保持不动_不发起任何连接()
        {
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.ConnectedSsid = Primary;
            Start();

            AssertEventually(() => _engine.State == EngineState.Normal && _engine.StatusText.Contains("连接正常"),
                8000, "进入【正常】");

            Assert.Equal(Primary, _engine.CurrentSsid);
            lock (_wifi.ConnectLog) { Assert.Empty(_wifi.ConnectLog); }
        }

        [Fact]
        public void 主用网关假死_两轮后切到优先级最高的备用()
        {
            // ConfirmScans 拉满：切到备用后不会立刻尝试切回，用例只验证"切走"这一步
            _config.ConfirmScans = 10;
            _wifi.Visible.Add(Net(Primary, 70));
            _wifi.Visible.Add(Net(Backup1, 60));
            _wifi.ConnectedSsid = Primary;
            _verifier.Health[Primary] = SsidHealth.GatewayDead;
            Start();

            // 第一轮只观察（1/2），补一次手动检查推动第二轮判定
            _engine.CheckNow();
            AssertEventually(() => _engine.CurrentSsid == Backup1 && _engine.State == EngineState.OnBackup,
                15000, "切到备用 " + Backup1);

            Assert.True(HistoryHas("切换备用"), "历史应记录切换备用。" + Diag());
            Assert.True(HistoryHas("切换备用") &&
                        _engine.History.Snapshot().First(h => h.Action.Contains("切换备用")).To == Backup1,
                "应切到优先级最高的 " + Backup1 + "。" + Diag());
        }

        [Fact]
        public void 备用上主用出现并达标_切回主用()
        {
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.Visible.Add(Net(Backup1, 60));
            _wifi.ConnectedSsid = Backup1;
            Start();

            // 确认 1 次 + 延迟 0 → 首轮即切回；含切回前复查扫描 2.5s + DHCP 1.5s
            AssertEventually(() => _engine.State == EngineState.Normal && _engine.CurrentSsid == Primary,
                20000, "切回主用");

            Assert.True(HistoryHas("切回主用"), "历史应记录切回主用。" + Diag());
            Assert.True(HasNotification("已切回主用"), "应弹切回通知。" + Diag());
        }

        [Fact]
        public void 热点关闭主用消失_留在备用_绝不发起连接()
        {
            _wifi.Visible.Add(Net(Backup1, 60));
            _wifi.ConnectedSsid = Backup1;
            Start();

            // 消失防抖期间显示"暂时未扫到"；确认消失后显示"不在扫描范围"——两者都不允许切回
            AssertEventually(() => _engine.State == EngineState.OnBackup &&
                                   (_engine.StatusText.Contains("主用暂时未扫到") ||
                                    _engine.StatusText.Contains("不在扫描范围")),
                8000, "出现主用消失的状态文案");

            Thread.Sleep(500); // 给引擎再跑几轮的机会，确保它确实没有动手
            lock (_wifi.ConnectLog) { Assert.Empty(_wifi.ConnectLog); }
            Assert.Equal(Backup1, _engine.CurrentSsid);
        }

        [Fact]
        public void 切回主用关联失败_自动回滚备用并通知()
        {
            // Home 在扫描里、Connect 发起成功，但永远关联不上（模拟密码错/AP 拒绝）
            _wifi.CanAssociate = delegate (string ssid) { return ssid != Primary; };
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.Visible.Add(Net(Backup1, 60));
            _wifi.ConnectedSsid = Backup1;
            Start();

            AssertEventually(() => HistoryHas("回滚备用") && _engine.CurrentSsid == Backup1,
                20000, "切回失败后回滚备用");

            Assert.True(HasNotification("切回主用失败"), "应弹切回失败通知。" + Diag());
            var entry = _engine.History.Snapshot().First(h => h.Action.Contains("回滚备用"));
            Assert.Equal(Primary, entry.From);
            Assert.Equal(Backup1, entry.To);
        }

        [Fact]
        public void 主用检出Portal受限_显示待认证_绝不切走()
        {
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.ConnectedSsid = Primary;
            _verifier.Health[Primary] = SsidHealth.Portal;
            Start();

            AssertEventually(() => _engine.State == EngineState.Normal &&
                                   _engine.StatusText.Contains("无 Internet 访问"),
                8000, "显示【受限】");

            lock (_wifi.ConnectLog) { Assert.Empty(_wifi.ConnectLog); }
            Assert.Equal(Primary, _engine.CurrentSsid);
        }

        [Fact]
        public void 连着健康的第三方网络_保持不折腾()
        {
            _wifi.Visible.Add(Net("Cafe", 70));
            _wifi.ConnectedSsid = "Cafe";
            Start();

            AssertEventually(() => _engine.State == EngineState.OnBackup &&
                                   _engine.StatusText.Contains("非主备列表"),
                8000, "识别第三方网络并保持");

            lock (_wifi.ConnectLog) { Assert.Empty(_wifi.ConnectLog); }
        }

        [Fact]
        public void 可用有线插入_挂起WiFi管理_拔线自动恢复()
        {
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.ConnectedSsid = Primary;
            _verifier.EthUp = true;
            _verifier.EthGatewayAlive = true;
            _verifier.EthUsable = true;
            Start();

            AssertEventually(() => _engine.State == EngineState.EthernetHold &&
                                   _engine.StatusText.Contains("挂起待命"),
                8000, "进入有线挂起");
            Assert.True(HistoryHas("挂起"), "历史应记录挂起。" + Diag());

            // 拔线：环境变化 → 通知唤醒 → 退出挂起恢复管理
            _verifier.EthUp = false;
            _wifi.RaiseNotified();
            AssertEventually(() => _engine.State == EngineState.Normal &&
                                   _engine.StatusText.Contains("连接正常"),
                10000, "拔线后恢复 WiFi 管理");

            Assert.True(HistoryHas("恢复管理"), "历史应记录恢复管理。" + Diag());
            Assert.True(HasNotification("有线已断开"), "应弹有线断开通知。" + Diag());
        }

        [Fact]
        public void 假死网线_放行WiFi切换_不挂起不锁死()
        {
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.ConnectedSsid = Primary;
            _verifier.EthUp = true;
            _verifier.EthGatewayAlive = false; // 网关不通 → 不可托付
            _verifier.EthUsable = false;
            Start();

            AssertEventually(() => _engine.State == EngineState.Normal &&
                                   _engine.StatusText.Contains("连接正常") &&
                                   _engine.StatusText.Contains("有线未通"),
                8000, "显示【注意】有线未通且继续管理 WiFi");

            Assert.False(HistoryHas("挂起"), "假死网线绝不能挂起 WiFi。" + Diag());
        }

        [Fact]
        public void 主备全消失_进入终极降级_网络回来稳定后自动恢复()
        {
            _wifi.ConnectedSsid = null; // 掉线，且扫描里什么都没有
            Start();

            // 自然节奏下每轮失败间隔 30 秒退避；手动检查可跳过退避立即推进。
            // 注意 _manualCheck 是布尔标志不是计数器：两次 CheckNow 落在同一轮 Tick 前会被合并，
            // 所以每推一轮都先等状态变化再按下一次（退避文案的秒数即失败轮数的观测点）。
            AssertEventually(() => _engine.StatusText.Contains("秒后重试"), 5000, "第一轮失败退避（30 秒）");
            _engine.CheckNow();
            AssertEventually(() => _engine.StatusText.Contains("60 秒后重试"), 5000, "第二轮失败退避（60 秒）");
            _engine.CheckNow();
            AssertEventually(() => _engine.State == EngineState.TotalFailure, 8000, "进入 TOTAL_FAILURE");
            Assert.True(HasNotification("所有已知网络均不可用"), "应弹降级通知。" + Diag());

            // 主用回来了：先"确认稳定中"，持续可见 ≥15 秒才解除（防弱信号幽灵 SSID 震荡）
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.RaiseNotified();
            AssertEventually(() => _engine.StatusText.Contains("确认稳定中"), 5000, "看到网络并开始确认稳定");
            Thread.Sleep(16000);
            _wifi.RaiseNotified();

            // 解除降级后立即落回正常流程：Home 在范围内 → 直连主用（不再只试备用列表）
            AssertEventually(() => _engine.CurrentSsid == Primary, 15000, "解除降级并连上 Home");
            Assert.True(HistoryHas("解除降级"), "历史应记录解除降级。" + Diag());
            Assert.True(HasNotification("网络已恢复"), "应弹恢复通知。" + Diag());
        }

        [Fact]
        public void 主用可见但信号不足_降级不解除_信号恢复后才连()
        {
            // 复查发现并修复的震荡路径：TF 解除判定若不看信号阈值，
            // 弱信号主用会造成 解除→连不上→再进降级 的循环（每轮进降级都弹气泡）。
            _config.SignalThresholdPercent = 50;
            _wifi.ConnectedSsid = null;
            Start();

            AssertEventually(() => _engine.StatusText.Contains("秒后重试"), 5000, "第一轮失败退避（30 秒）");
            _engine.CheckNow();
            AssertEventually(() => _engine.StatusText.Contains("60 秒后重试"), 5000, "第二轮失败退避（60 秒）");
            _engine.CheckNow();
            AssertEventually(() => _engine.State == EngineState.TotalFailure, 8000, "进入 TOTAL_FAILURE");

            // 主用回来了但信号只有 30%（低于阈值 50%）：不得解除降级，更不得发起连接
            _wifi.Visible.Add(Net(Primary, 30));
            _wifi.RaiseNotified();
            Thread.Sleep(2000);
            Assert.Equal(EngineState.TotalFailure, _engine.State);
            lock (_wifi.ConnectLog) { Assert.Empty(_wifi.ConnectLog); }

            // 信号恢复到 80%：先唤醒一次让引擎看到（起算 15 秒稳定窗），期满再唤醒触发解除
            _wifi.Visible.Clear();
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.RaiseNotified();
            AssertEventually(() => _engine.StatusText.Contains("确认稳定中"), 5000, "看到恢复的信号并开始确认稳定");
            Thread.Sleep(16000);
            _wifi.RaiseNotified();
            AssertEventually(() => _engine.CurrentSsid == Primary, 15000, "信号恢复后解除降级并连上主用");
        }

        [Fact]
        public void 手动暂停_状态显示已暂停_到期自动恢复并落盘()
        {
            _wifi.Visible.Add(Net(Primary, 80));
            _wifi.ConnectedSsid = Primary;
            Start();
            AssertEventually(() => _engine.State == EngineState.Normal, 8000, "先进入正常");

            // 定时暂停 600ms（走运行中改配置的路径：Loop 启动时会把"重启前残留"的定时暂停清掉）
            lock (_config.SyncRoot)
            {
                _config.Paused = true;
                _config.PauseUntilTick = Environment.TickCount64 + 600;
            }
            _engine.Wake();
            AssertEventually(() => _engine.State == EngineState.Paused, 8000, "进入暂停");

            AssertEventually(() => _engine.State == EngineState.Normal && !_config.Paused &&
                                   HasNotification("定时暂停到期"),
                10000, "到期自动恢复");
            Assert.True(File.Exists(Path.Combine(TestSetup.DataDir, "config.json")),
                "暂停到期应把配置落盘。" + Diag());
        }
    }
}
