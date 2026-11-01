using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Suzu.Core
{
    public enum EngineState
    {
        Starting,
        Normal,          // 在主用上，一切正常
        OnBackup,        // 在备用上（或等待切备用）
        WatchingReturn,  // 主用已连续出现，倒计时中
        Returning,       // 正在切回主用
        Paused,          // 用户手动暂停
        EthernetHold,    // 检测到有线连接，自动暂停 WiFi 切换（与用户暂停语义不同）
        TotalFailure,    // 所有已知网络连续多轮都不可用，进入终极降级
        Faulted
    }

    /// <summary>
    /// 核心状态机：主用掉线 → 切备用 → 主用恢复 → 延迟切回 → 验证/回滚。
    /// 所有 WLAN/网络操作都在独立线程上执行，避免卡住界面。
    /// 计时全部用 Environment.TickCount64（单调时钟），系统校时/时区跳变不影响。
    /// </summary>
    public sealed class SwitchEngine : IDisposable
    {
        private readonly AppConfig _config;
        private readonly IWifiProvider _wlan;
        private readonly INetworkVerifier _checker;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _manualCheck;
        private volatile string _statusText = "正在初始化…";
        private volatile EngineState _state = EngineState.Starting;
        private volatile string _currentSsid;

        // 引擎线程每轮 Tick 开始时对配置做一次快照，
        // 避免 UI 线程保存配置时引擎读到"半套"新值
        private AppConfig _cfg;

        // 运行时状态（只在引擎线程读写）。时间均为 TickCount64 毫秒；-1 表示未开始。
        private int _hitStreak;
        private int _missStreak;                       // 连续"新扫描里没看到主用"的次数
        private long _missSince = -1;                  // 第一次没看到主用的时间（TickCount64 毫秒，-1 = 未开始）
        private long _lastMissMs;                      // 上一次计入"漏扫"的时间，用于按扫描节奏节流

        // 主用"消失"的判定阈值：连续 3 次扫描、或持续 30 秒没看到，才算真的消失
        private const int MissConfirmScans = 3;
        private const long MissConfirmWindowMs = 30000;
        private long _hitSince = -1;
        private long _cooldownUntil;          // 0 = 无冷却
        private long _retryUntil;             // 0 = 无退避
        private long _nextBackupAttempt;      // 0 = 立即可试
        private long _lastScanRequest = -60000;
        private long _lastStreakMs;           // 上一次确认计数的时间，用于按扫描节流
        private int _returnFailCount;
        private int _backupFailCount;
        private bool _pausedAnnounced;
        private string _lastBackupSsid;
        private string _backupFailReason = "备用不可用";

        // ---- v1.0.4 分级容灾相关 ----
        private NetworkLevel _lastLevel = NetworkLevel.Level0; // 最近一次分级结果（引擎线程读写）
        private long _lastInternetProbeMs;                     // 上一次外网探测的时间（TickCount64 毫秒），外网探测降频用
        private int _gwFailStreak;                             // 连续多少轮网关不可达（主用假死判定）
        private long _level1SinceMs = -1;                      // 首次进入 Level 1 的时间（-1 = 未进入），5 分钟提示用
        private bool _level1Notified;                          // Level 1 持续提示是否已发过
        private bool _ethHoldNotified;                         // 有线暂停是否已记过日志/提示

        // ---- 双链路状态文案 ----
        private EthernetNetState? _ethSideState;               // 有线侧最近一次探测结果
        private long _ethSideProbedMs;                         // 有线侧探测时间（降频 90 秒）
        private bool _ethBadNotified;                          // 假死网线（有载波但网关不通）是否已记过日志
        private bool _ethBadActive;                            // 当前是否处于"假死网线"状态（状态文本要挂提示）
        private long _ethNetProbeDueMs;                        // 有线外网的下次完整复验时间（挂起态降频用，0 = 立即可验）
        private long _brokenLinkNextAttempt;                   // 连着网关不通的网络时，下一轮切换尝试的时间（慢轮询）
        private long _tfSeenSince = -1;                        // TotalFailure 下首次扫到已知网络的时间（稳定确认用，-1 = 未发现）
        private long _tfReenterHoldUntil;                      // TotalFailure 解除后不允许再次进入的截止时间（防震荡迟滞）

        /// <summary>假死网线的状态提示（逐字，勿改）。</summary>
        private const string TextEthernetBad = "【注意】有线未通，已放行 WiFi 切换";

        /// <summary>网关连续不可达多少轮后视同掉线（默认轮询 10 秒 → 约 20 秒）。</summary>
        private const int GwFailRoundsToOffline = 2;
        /// <summary>外网探测的降频间隔：网关通时最多每 60 秒探一次外网。</summary>
        private const long InternetProbeIntervalMs = 60000;
        /// <summary>Level 1（无外网）持续多久提醒一次用户。</summary>
        private const long Level1NotifyAfterMs = 300000;
        /// <summary>挂起状态下每隔多久对有线做一次完整外网复验（期间只轻量 ping 网关）。</summary>
        private const long EthNetReprobeIntervalMs = 30000;
        /// <summary>双链路文案：有线侧独立探测的降频间隔（最坏 5 目标 × 1.2s，不能每轮做）。</summary>
        private const long EthSideProbeIntervalMs = 90000;
        /// <summary>连着网关不通的网络时，切换尝试的降频间隔（原地慢轮询，不进 TotalFailure）。</summary>
        private const long BrokenLinkRetryIntervalMs = 60000;
        /// <summary>TotalFailure 解除前，已知网络需持续可见的时长（防"弱信号幽灵 SSID"震荡）。</summary>
        private const long TotalFailureStableMs = 15000;
        /// <summary>TotalFailure 解除后的再进入迟滞窗口（防进入→解除→进入 0.1 秒循环）。</summary>
        private const long TotalFailureReenterHoldMs = 180000;

        public SwitchEngine(AppConfig config, IWifiProvider wlan, INetworkVerifier verifier)
        {
            _config = config;
            _cfg = config;
            _wlan = wlan;
            _checker = verifier;
            _wlan.Notified += () => _wake.Set();
        }

        /// <summary>状态文本（托盘 tooltip 用）。</summary>
        public string StatusText { get { return _statusText; } }
        public EngineState State { get { return _state; } }
        public string CurrentSsid { get { return _currentSsid; } }

        /// <summary>切换/降级/挂起历史（内存环形缓冲，重启清零；历史面板用）。</summary>
        public SwitchHistory History { get; } = new SwitchHistory();

        /// <summary>状态变化（任意线程触发）。</summary>
        public event Action StatusChanged;
        /// <summary>需要气泡通知的关键动作：(标题, 内容)。</summary>
        public event Action<string, string> Notify;

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SwitchEngine" };
            _thread.Start();
        }

        /// <summary>唤醒引擎立即评估一次（也用于配置变更）。</summary>
        public void Wake()
        {
            _wake.Set();
        }

        /// <summary>手动"立即检查/切换"：忽略延迟与退避。</summary>
        public void CheckNow()
        {
            _manualCheck = true;
            _wake.Set();
        }

        private static long NowMs()
        {
            return Environment.TickCount64;
        }

        private void Loop()
        {
            AppLog.Info("状态机线程已启动。");
            int wlanOpenFailCount = 0;

            // 重启后 TickCount64 重新从小值计时，旧的定时暂停到期时间在新开机周期里没有意义
            //（实际效果会是暂停被随机延长）。按方案书接受的语义：重启 = 取消定时暂停，立即恢复。
            if (_config.Paused && _config.PauseUntilTick > 0)
            {
                lock (_config.SyncRoot)
                {
                    _config.Paused = false;
                    _config.PauseUntilTick = 0;
                }
                _config.Save();
                AppLog.Info("检测到重启前的定时暂停，已自动恢复（定时暂停不跨重启）。");
            }

            while (!_stop)
            {
                try
                {
                    if (!_wlan.IsOpen)
                    {
                        try
                        {
                            _wlan.Open();
                            wlanOpenFailCount = 0;
                        }
                        catch (Exception ex)
                        {
                            wlanOpenFailCount++;
                            // 无 WLAN 硬件/服务的机器（rc=1062）上失败是永久性的：
                            // 首错完整记录并给排查提示，同类后续错误 5 分钟才记一次，重试也放缓到 5 分钟
                            //（手动「立即检查」会立即唤醒重试，不影响恢复速度）。其余错误维持 15 秒快节奏。
                            bool permanent = (ex is Network.WlanException we) && we.Code == Network.WindowsWlanProvider.ErrorServiceNotActive;
                            if (wlanOpenFailCount == 1)
                                AppLog.Error("打开 WLAN 服务失败", ex);
                            else if (wlanOpenFailCount % 20 == 0)
                                AppLog.Warn("打开 WLAN 服务仍失败（累计 " + wlanOpenFailCount + " 次）：" + ex.Message);
                            SetStatus("WLAN 不可用：" + ex.Message);
                            SetState(EngineState.Faulted);
                            WaitMs(permanent ? 300000 : 15000);
                            continue;
                        }
                    }

                    bool manual = _manualCheck;
                    _manualCheck = false;
                    Tick(manual);
                }
                catch (Exception ex)
                {
                    AppLog.Error("状态机异常", ex);
                    SetStatus("内部错误：" + ex.Message);
                    SetState(EngineState.Faulted);
                }

                WaitMs(ComputeWaitMs());
            }
            AppLog.Info("状态机线程已退出。");
        }

        private void WaitMs(int ms)
        {
            if (ms < 200) ms = 200;
            // 提前唤醒时最多等待 ms；被唤醒则立即重跑
            _wake.WaitOne(ms);
        }

        private int ComputeWaitMs()
        {
            if (_cfg.Paused) return 2000;
            if (_state == EngineState.EthernetHold) return 10000;   // 有线在时 10 秒探一次是否拔出
            if (_state == EngineState.TotalFailure) return 60000;    // 终极降级下低功耗轮询
            if (_state == EngineState.Returning) return 500;
            if (_state == EngineState.WatchingReturn) return 1000;
            if (_hitStreak >= Math.Max(1, _cfg.ConfirmScans) && _hitSince >= 0) return 1000;
            return Math.Max(3, _cfg.PollIntervalSeconds) * 1000;
        }

        private void Tick(bool manual)
        {
            _cfg = _config.CloneForRead();

            // 定时暂停到期：自动恢复（写配置与 UI 同锁），刷新快照让本轮直接走正常判定
            if (_cfg.Paused && _cfg.PauseUntilTick > 0 && NowMs() >= _cfg.PauseUntilTick)
            {
                lock (_config.SyncRoot)
                {
                    _config.Paused = false;
                    _config.PauseUntilTick = 0;
                }
                _config.Save();
                AppLog.Info("定时暂停到期，自动恢复自动切换。");
                RaiseNotify("定时暂停到期", "已自动恢复自动切换。");
                _cfg = _config.CloneForRead();
            }

            if (_cfg.Paused && !manual)
            {
                SetState(EngineState.Paused);
                if (_cfg.PauseUntilTick > 0)
                {
                    int remainMin = (int)Math.Ceiling(Math.Max(0, _cfg.PauseUntilTick - NowMs()) / 60000.0);
                    SetStatus(string.Format("已暂停自动切换（剩余 {0} 分钟，右键菜单恢复）", remainMin));
                }
                else
                {
                    SetStatus("已暂停自动切换（右键菜单恢复）");
                }
                if (!_pausedAnnounced)
                {
                    _pausedAnnounced = true;
                    AppLog.Info("自动切换已暂停。");
                }
                return;
            }
            _pausedAnnounced = false;

            // ---- 有线接入挂起：网线要"插着 + 网关可达 + 真有外网"三条件，才允许挂起 WiFi ----
            // v1.1.2 只验网关，结果插着"没额度的随身WiFi"时网关活着但上不了网，用户被锁死在断网状态；
            // 挂起的门槛必须是有线达到 Level 2（源绑定外网探测通过）。
            // v1.2：策略为「继续管理」时跳过全部有线探测（省开销），直接走正常状态机。
            bool ethernetManaged = _cfg.HoldPolicy != EthernetHoldPolicy.AlwaysManage;
            bool ethernetUp = false;
            if (!ethernetManaged)
            {
                if (_ethHoldNotified || _ethBadNotified || _ethBadActive)
                {
                    _ethHoldNotified = false;
                    _ethBadNotified = false;
                    _ethBadActive = false;
                    AppLog.Info("有线策略为「继续管理」，忽略有线连接。");
                }
            }
            else
            {
                ethernetUp = _checker.IsEthernetUp();
                if (ethernetUp && !manual)
                {
                    var ethNowMs = NowMs();
                    bool usable;
                    if (!_checker.IsEthernetGatewayAlive(1000))
                    {
                        usable = false; // 网关都不通（坏网线/对端断电），不用浪费外网探测
                    }
                    else if (_state == EngineState.EthernetHold && ethNowMs < _ethNetProbeDueMs)
                    {
                        usable = true; // 挂起中且刚完整复验过外网，间隔期内只看网关，别把轮询变重
                    }
                    else
                    {
                        usable = _checker.IsEthernetUsable(1500); // 完整判定：网关 + 源绑定外网探测
                        if (usable) _ethNetProbeDueMs = ethNowMs + EthNetReprobeIntervalMs;
                    }

                    if (usable)
                    {
                        if (!_ethHoldNotified)
                        {
                            _ethHoldNotified = true;
                            _ethBadNotified = false;
                            AppLog.Info("检测到可用有线连接（网关通且外网可达），挂起 WiFi 切换。");
                            History.Add("挂起", "-", "-", "有线网关通且外网可达（Level 2）");
                        }
                        SetState(EngineState.EthernetHold);
                        SetStatus("🔌 有线网络连接正常，WiFi 切换已挂起待命");
                        _ethBadActive = false;
                        return;
                    }

                    // 有线插着但到不了外网（坏网线 / 网关活着但没网）：放行 WiFi 切换，不能把用户锁死
                    if (!_ethBadNotified)
                    {
                        _ethBadNotified = true;
                        AppLog.Warn("检测到有线连接但无法访问外网（网关不通或链路没网），WiFi 切换继续。");
                    }
                    if (_ethHoldNotified)
                    {
                        _ethHoldNotified = false;
                        AppLog.Info("有线链路劣化，恢复 WiFi 切换。");
                    }
                    _ethBadActive = true;
                    SetStatus(TextEthernetBad);
                    // 注意：这里**不 return**，继续走下方正常状态机
                }
                if (!ethernetUp)
                {
                    _ethBadNotified = false;
                    _ethBadActive = false;
                    if (_ethHoldNotified)
                    {
                        _ethHoldNotified = false;
                        AppLog.Info("有线已断开，恢复 WiFi 切换逻辑。");
                        History.Add("恢复管理", "-", "-", "有线已断开");
                        RaiseNotify("有线已断开", "恢复 WiFi 自动切换。");
                    }
                }
            }

            var nowMs = NowMs();
            var conn = _wlan.GetConnection();

            bool inTotalFailure = (_state == EngineState.TotalFailure);
            // TOTAL_FAILURE 下仍然要偶尔发起扫描，否则缓存的 BSS 列表永远不刷新、看不到网络回来；
            // 只是把频率从 5 秒放宽到 60 秒（低功耗）
            bool requestScan = (nowMs - _lastScanRequest) >= (inTotalFailure ? 60000 : 5000);
            // 先读上一轮扫描结果做判定，再请求下一轮扫描：
            // 若先请求再读，部分驱动会把 BSS 列表清空/半更新，读到的结果里主用会"凭空消失"一次
            var scan = _wlan.Scan(false);
            if (requestScan)
            {
                _lastScanRequest = nowMs;
                _wlan.RequestScan();
            }

            UpdateCurrentSsid(conn);

            // 非管理员进程在 Win11 上会被 WLAN API 直接拒绝，这里给出明确提示而不是反复"切换失败"
            if (conn.LastError == NetworkErrorCodes.AccessDenied || _wlan.LastScanError == NetworkErrorCodes.AccessDenied)
            {
                SetState(EngineState.Faulted);
                SetStatus("没有管理员权限，无法读取/切换 WiFi（请右键「以管理员身份运行」）");
                return;
            }

            if (!_cfg.HasPrimary)
            {
                SetState(EngineState.Faulted);
                SetStatus("正在等待配置：请在设置中选择主用 WiFi 并保存");
                return;
            }

            // 扫描读取失败：本轮不做任何"主用在不在"的判断，也不动确认计数，等下一轮
            if (scan.Quality == ScanQuality.Failed)
            {
                AppLog.Warn("本轮扫描读取失败，跳过判定（沿用上次结果）。");
                return;
            }
            var nets = scan.Networks;

            // ---- TOTAL_FAILURE：低功耗轮询；已知网络要连续确认稳定才解除 ----
            // （只看到 SSID 就解除是实测的震荡根因：弱信号 AP 的 SSID 一直躺在扫描缓存里，
            //   解除后连不上又立即进 TotalFailure，0.1 秒一个循环刷日志）
            if (inTotalFailure)
            {
                var back = FirstKnownNetworkInRange(nets);
                if (back == null)
                {
                    _tfSeenSince = -1;
                    SetStatus("❌ 所有已知网络均不可用，请检查您的手机热点或路由器是否正常开启。");
                    return;
                }
                if (_tfSeenSince < 0) _tfSeenSince = nowMs;
                // 必须持续可见 ≥ 15 秒才解除（TotalFailure 下 60 秒一轮扫描，即连续两轮看到）
                if ((nowMs - _tfSeenSince) < TotalFailureStableMs)
                {
                    SetStatus("❌ 所有已知网络均不可用｜检测到 " + back + "，确认稳定中…");
                    return;
                }
                AppLog.Warn("TOTAL_FAILURE 解除（" + back + " 稳定可见）。");
                History.Add("解除降级", "-", back, "已知网络持续可见 ≥15 秒");
                _tfSeenSince = -1;
                _backupFailCount = 0;
                _nextBackupAttempt = 0;
                // 迟滞：3 分钟内不再允许进入，防止"解除→连不上→再进入"震荡
                _tfReenterHoldUntil = nowMs + TotalFailureReenterHoldMs;
                SetState(EngineState.OnBackup);
                SetStatus("网络已恢复，正在重新连接…");
                RaiseNotify("网络已恢复", "检测到 " + back + "，恢复自动切换。");
                // 落到正常流程，下面会走切换链路
            }

            // ---- 分级：网关每轮都测，外网探测降频 ----
            var level = UpdateLevel(conn);
            _lastLevel = level;

            bool onPrimary = conn.Connected && SsidEquals(conn.Ssid, _cfg.PrimarySsid);
            bool onBackup = conn.Connected && _cfg.IsBackupSsid(conn.Ssid);

            if (onPrimary)
            {
                _hitStreak = 0;
                _hitSince = -1;
                _lastStreakMs = 0;
                _missStreak = 0;
                _missSince = -1;
                _lastMissMs = 0;
                _returnFailCount = 0;
                _retryUntil = 0;
                _backupFailCount = 0;
                _nextBackupAttempt = 0;

                if (level == NetworkLevel.Level0)
                {
                    // 主用连着但网关死了（网卡假死 / 路由器卡死）——连续两轮才动手，防单次丢包
                    _gwFailStreak++;
                    AppLog.Warn("主用网关不可达（第 " + _gwFailStreak + "/" + GwFailRoundsToOffline + " 轮）。");
                    if (_gwFailStreak < GwFailRoundsToOffline)
                    {
                        SetState(EngineState.Normal);
                        SetStatus(string.Format("🔴 主用 {0} 网关不可达（{1}/{2} 轮，继续观察）",
                            conn.Ssid, _gwFailStreak, GwFailRoundsToOffline));
                        return;
                    }

                    AppLog.Warn("主用网关连续不可达，视同掉线。");
                    _gwFailStreak = 0;
                    // 注意传真实 conn（而不是合成"未连接"状态）：HandleOffPrimary 的切换逻辑
                    // 只看 candidates，与 conn.Connected 无关；而 TryEnterTotalFailure 依赖
                    // conn.Connected 拦住"还连着就不进终极降级"。合成假断开状态会绕过这道防线，
                    // 实测造成 TotalFailure 0.1 秒进出的震荡循环。
                    HandleOffPrimary(conn, nets, manual, NetworkLevel.Level0, conn.Ssid);
                    return;
                }

                _gwFailStreak = 0;
                SetState(EngineState.Normal);
                if (level == NetworkLevel.Level2)
                {
                    SetStatus(string.Format("🟢 连接正常：主用 {0}（信号 {1}%）", conn.Ssid, conn.SignalQuality));
                }
                else
                {
                    SetStatus(string.Format("🟡 已连接，但无 Internet 访问（可能需要网页认证或 VPN 异常）：主用 {0}", conn.Ssid));
                }
                MaybeAnnounceLevel1(level);
                return;
            }

            if (onBackup)
            {
                _lastBackupSsid = conn.Ssid;
                EvaluateReturn(conn, nets, manual, level);
                return;
            }

            // 既不在主用也不在备用：掉线或连了第三个网络
            HandleOffPrimary(conn, nets, manual, level, null);
        }

        /// <summary>扫描结果里第一个"已知网络"（主用或任一备用）的 SSID；没有返回 null。</summary>
        private string FirstKnownNetworkInRange(List<AvailableNetwork> nets)
        {
            if (Find(nets, _cfg.PrimarySsid) != null) return _cfg.PrimarySsid;
            foreach (var b in _cfg.BackupSsids)
            {
                if (Find(nets, b) != null) return b;
            }
            return null;
        }

        /// <summary>
        /// 更新分级：网关每轮都测（最多阻塞 1 秒）；外网探测降频到 60 秒一次
        /// （或上次判定为"无外网"时每轮重试，方便认证完成后尽快升级到 Level 2）。
        /// </summary>
        private NetworkLevel UpdateLevel(ConnectionState conn)
        {
            if (!conn.Connected) return NetworkLevel.Level0;

            var link = _checker.CheckLinkOnly(1000);
            if (link == NetworkLevel.Level0)
            {
                // 网关 ping 不通有两种可能：路由器/网卡真死了，或者这个 AP 干脆不响应 ICMP。
                // 用源绑定外网探测区分：外网通 → 链路其实是好的（Level 2）；检出 Portal →
                // 链路活着但需要认证（Level 1，保持连接等用户认证，绝不切走）；否则才是 Level 0。
                var probe = _checker.Check(2000, 1, 0);
                _lastInternetProbeMs = NowMs();
                if (probe.InternetOk)
                {
                    AppLog.Info("网关不响应 ICMP，但外网可达，判定为 Level 2。" + probe.InternetTarget);
                    return NetworkLevel.Level2;
                }
                if (probe.PortalDetected)
                {
                    AppLog.Info("网关不响应 ICMP，检出 Portal 劫持（HTTP 被重定向），判定为 Level 1（待认证）。");
                    return NetworkLevel.Level1;
                }
                return NetworkLevel.Level0;
            }

            var nowMs = NowMs();
            // Level 1（无外网）时想尽快发现"认证完成/VPN 恢复"，但完整外网探测最坏要 20 多秒，
            // 每轮都做会把引擎循环拖慢一个数量级 —— 重探间隔至少 15 秒。
            // 上轮是 Level 0（网关或探测瞬时失败）时也必须立即重探：否则网关恢复后这一轮
            // 会凭空返回缓存的 Level 1（【受限】），把能正常上网的网络错标最长 60 秒（v1.2.0 实测缺陷）。
            bool needProbe = (nowMs - _lastInternetProbeMs) >= InternetProbeIntervalMs
                             || _lastLevel == NetworkLevel.Level0
                             || (_lastLevel == NetworkLevel.Level1 && (nowMs - _lastInternetProbeMs) >= 15000);
            if (!needProbe) return _lastLevel == NetworkLevel.Level2 ? NetworkLevel.Level2 : NetworkLevel.Level1;

            var result = _checker.Check(2000, 1, 0);
            _lastInternetProbeMs = NowMs();
            // 判定依据全量落日志：网关可达路径此前除 Portal 外全部静默，
            // "为什么持续判受限"在诊断包里无迹可寻（v1.2.0 实测盲区）
            AppLog.Info(string.Format("网关可达，完整探测判定为 {0}。{1}", result.Level, result.Detail));
            return result.Level;
        }

        /// <summary>Level 1 持续超过 5 分钟，提醒一次（Level 1↔2 切换本身不打扰用户）。</summary>
        private void MaybeAnnounceLevel1(NetworkLevel level)
        {
            if (level != NetworkLevel.Level1)
            {
                _level1SinceMs = -1;
                _level1Notified = false;
                return;
            }

            if (_level1SinceMs < 0) _level1SinceMs = NowMs();
            if (!_level1Notified && (NowMs() - _level1SinceMs) >= Level1NotifyAfterMs)
            {
                _level1Notified = true;
                AppLog.Info("Level 1（无外网）已持续 5 分钟，提示用户。");
                RaiseNotify("当前网络无 Internet 访问", "若为校园网请完成网页认证。");
            }
        }

        /// <summary>
        /// 双链路状态尾部（在 SetStatus 统一调用，覆盖主用/备用/倒计时等所有状态）：
        /// 有线策略为「继续管理」且有线在插时，对有线侧做低频独立探测并追加描述——
        /// "我明明有网为什么显示受限"多来自"状态只描述了 WLAN 侧，而系统流量实际走另一条链路"。
        /// 探测最坏 5 目标 × 1.2 秒，降频到 90 秒一次；挂起态（Auto 策略）有自己的复验，不进这里。
        /// </summary>
        private string AppendEthernetTail(string text)
        {
            if (_cfg.HoldPolicy != EthernetHoldPolicy.AlwaysManage) return text;
            if (text.IndexOf("有线：", StringComparison.Ordinal) >= 0) return text; // 已带尾部，防重复
            if (!_checker.IsEthernetUp()) return text;

            var ethNowMs = NowMs();
            if (_ethSideState == null || (ethNowMs - _ethSideProbedMs) >= EthSideProbeIntervalMs)
            {
                var st = _checker.ProbeEthernet(1200);
                _ethSideState = st;
                _ethSideProbedMs = ethNowMs;
            }

            string ethText;
            switch (_ethSideState.Value)
            {
                case EthernetNetState.InternetOk:
                    ethText = "有线：联网正常";
                    break;
                case EthernetNetState.NoInternet:
                    ethText = "有线：待认证或无外网";
                    break;
                case EthernetNetState.LinkDown:
                    ethText = "有线：链路不通";
                    break;
                default:
                    return text; // Absent：没有有线（IsEthernetUp 刚为 true 但随即拔掉的竞态），不加尾巴
            }

            // WiFi 侧受限/断开但有线可上网时，明确告诉用户系统流量还有出路。
            // 此处 text 仍是 emoji 形态（SetStatus 在 ToTags 之前调用本方法）。
            bool wlanNotOk = text.IndexOf("🟢", StringComparison.Ordinal) < 0;
            if (wlanNotOk && _ethSideState.Value == EthernetNetState.InternetOk)
            {
                return text + "｜" + ethText + "（系统流量可走有线）";
            }
            return text + "｜" + ethText;
        }

        private void UpdateCurrentSsid(ConnectionState conn)
        {
            var ssid = conn.Connected ? conn.Ssid : null;
            var prev = _currentSsid;
            if (!string.Equals(prev, ssid, StringComparison.OrdinalIgnoreCase))
            {
                _currentSsid = ssid;
                AppLog.Info(string.IsNullOrEmpty(ssid) ? "当前未连接任何 WiFi" : ("当前 WiFi: " + ssid));
            }
        }

        /// <summary>在备用上：判断是否该切回主用。</summary>
        private void EvaluateReturn(ConnectionState conn, List<AvailableNetwork> nets, bool manual, NetworkLevel level)
        {
            var nowMs = NowMs();
            var primaryNet = Find(nets, _cfg.PrimarySsid);
            bool primaryVisible = primaryNet != null;
            bool signalOk = primaryVisible && SignalAccepted(primaryNet.SignalQuality);

            // 分级前缀：备用链路本身也要分级展示（Level 2 绿、Level 1 黄）
            var head = (level == NetworkLevel.Level2 ? "🟢 已连接 " : "🟡 已连接 ") + _cfg.DescribeBackup(conn.Ssid);

            // 备用自身网关死了：连续两轮就视同"这个备用不能用"，去试下一个备用
            if (level == NetworkLevel.Level0)
            {
                _gwFailStreak++;
                AppLog.Warn("当前备用网关不可达（第 " + _gwFailStreak + "/" + GwFailRoundsToOffline + " 轮）。");
                if (_gwFailStreak >= GwFailRoundsToOffline)
                {
                    AppLog.Warn("备用网关连续不可达，换下一个备用。");
                    _gwFailStreak = 0;
                    // 传真实 conn：还连着这个坏备用时，TryEnterTotalFailure 的"连着就不进"
                    // 防线必须生效（实测合成假断开状态绕过它导致 TotalFailure 震荡）。
                    // skipSsid 已保证切换时跳过当前这个坏掉的 SSID。
                    HandleOffPrimary(conn, nets, manual, NetworkLevel.Level0, conn.Ssid);
                    return;
                }
                SetState(EngineState.OnBackup);
                SetStatus(string.Format("🔴 {0} 网关不可达（{1}/{2} 轮，继续观察）",
                    _cfg.DescribeBackup(conn.Ssid), _gwFailStreak, GwFailRoundsToOffline));
                return;
            }
            _gwFailStreak = 0;

            if (!primaryVisible || !signalOk)
            {
                bool freshMiss = (primaryNet == null); // 信号不足走另一条分支，不做防抖

                if (freshMiss)
                {
                    // 消失防抖：与"出现确认"对称。一次扫描没看到不足以判定"主用没了"，
                    // 连续 3 次、或持续 30 秒没看到，才算真的消失。
                    // 计数同样按扫描节奏节流：WLAN 通知可能一秒唤醒好几次，
                    // 不节流的话"连续 3 次扫描"会退化成"3 次唤醒"。
                    var missGapMs = Math.Max(3000, _cfg.PollIntervalSeconds * 800);
                    if (nowMs - _lastMissMs >= missGapMs)
                    {
                        if (_missSince < 0) _missSince = nowMs;
                        _missStreak++;
                        _lastMissMs = nowMs;
                    }
                    if (_missSince < 0) _missSince = nowMs;

                    bool confirmed = _missStreak >= MissConfirmScans
                                     || (nowMs - _missSince) >= MissConfirmWindowMs;

                    if (!confirmed)
                    {
                        AppLog.Info("扫描里暂时没看到主用 " + _cfg.PrimarySsid +
                                    "（" + _missStreak + "/" + MissConfirmScans + " 次确认，先保持现状）");
                        if (_hitStreak >= Math.Max(1, _cfg.ConfirmScans) && _hitSince >= 0)
                        {
                            SetState(EngineState.WatchingReturn);
                            SetStatus(string.Format("{0}｜主用暂时未扫到（{1}/{2} 次确认，{3} 秒后仍会尝试切回）",
                                head, _missStreak, MissConfirmScans,
                                RemainSeconds(nowMs, _hitSince + Math.Max(0, _cfg.ReturnDelaySeconds) * 1000L)));
                        }
                        else
                        {
                            SetState(EngineState.OnBackup);
                            SetStatus(string.Format("{0}｜主用暂时未扫到（{1}/{2} 次确认，不急于下结论）",
                                head, _missStreak, MissConfirmScans));
                        }
                        return;
                    }
                }

                _hitStreak = 0;
                _hitSince = -1;
                _lastStreakMs = 0;
                // 注意：这里**不**重置 _missStreak/_missSince —— 一旦确认"真的消失"，就一直保持
                // 已确认状态，直到主用重新出现（可见分支会清零）。否则每轮都会从 1/3 重新数一遍，
                // 状态文本会在"暂时未扫到"和"不在扫描范围"之间反复横跳。
                SetState(EngineState.OnBackup);

                if (!primaryVisible)
                {
                    // 手机热点关闭时 SSID 会直接消失 —— 这里绝不能触发切回
                    SetStatus(string.Format("{0}｜主用 {1} 不在扫描范围（热点可能已关闭）",
                        head, _cfg.PrimarySsid));
                }
                else
                {
                    SetStatus(string.Format("{0}｜主用信号 {1}% 低于阈值 {2}%",
                        head, primaryNet.SignalQuality, _cfg.SignalThresholdPercent));
                }
                return;
            }

            // 主用出现：清掉消失计数
            _missStreak = 0;
            _missSince = -1;
            _lastMissMs = 0;

            // 确认计数按"扫描节奏"节流：WLAN 通知可能一次扫描触发多次唤醒，
            // 若不节流，"连续 3 次扫描确认"会退化成"几次通知就确认"。
            // 同一份扫描结果（间隔不足 pollInterval 的 80%，至少 3 秒）最多计 1 次。
            var streakGapMs = Math.Max(3000, _cfg.PollIntervalSeconds * 800);
            if (nowMs - _lastStreakMs >= streakGapMs)
            {
                _hitStreak++;
                _lastStreakMs = nowMs;
            }
            var required = Math.Max(1, _cfg.ConfirmScans);

            if (_hitStreak < required)
            {
                SetState(EngineState.OnBackup);
                SetStatus(string.Format("{0}｜正在确认主用（{1}/{2}，信号 {3}%）",
                    head, _hitStreak, required, primaryNet.SignalQuality));
                return;
            }

            // 冷却期 / 失败退避（手动触发可跳过）
            if (!manual && nowMs < _cooldownUntil)
            {
                SetState(EngineState.OnBackup);
                SetStatus(string.Format("{0}｜冷却中，{1} 秒后才允许切回",
                    head, RemainSeconds(nowMs, _cooldownUntil)));
                return;
            }

            if (!manual && nowMs < _retryUntil)
            {
                SetState(EngineState.OnBackup);
                SetStatus(string.Format("{0}｜切回失败退避中，{1} 秒后重试",
                    head, RemainSeconds(nowMs, _retryUntil)));
                return;
            }

            if (_hitSince < 0) _hitSince = nowMs;

            var delayMs = Math.Max(0, _cfg.ReturnDelaySeconds) * 1000L;
            var dueMs = _hitSince + delayMs;

            if (manual || nowMs >= dueMs)
            {
                ReturnToPrimary(manual);
                return;
            }

            SetState(EngineState.WatchingReturn);
            SetStatus(string.Format("{0}｜主用已出现，{1} 秒后切回",
                head, RemainSeconds(nowMs, dueMs)));
        }

        /// <summary>切回主用 → 验证 → 失败则回滚备用并退避。</summary>
        private void ReturnToPrimary(bool manual)
        {
            // 动手前用一次全新扫描再确认：扫描缓存里的"幽灵条目"会让程序为一个并不存在的热点
            // 断开当前正常连接，白断网二十几秒。
            if (!ConfirmPrimaryBroadcasting())
            {
                AppLog.Info("切回前复查：最新扫描里没有 " + _cfg.PrimarySsid + "，本次不切，继续留在备用。");
                History.Add("放弃切回", _lastBackupSsid ?? "-", "-", "切回前复查：主用已不在广播中");
                _hitStreak = 0;
                _hitSince = -1;
                _lastStreakMs = 0;
                _missStreak = 0;
                SetState(EngineState.OnBackup);
                SetStatus("主用刚从扫描里消失，本次不切回，继续观察");
                return;
            }

            SetState(EngineState.Returning);
            SetStatus("正在切回主用 " + _cfg.PrimarySsid + " …");
            AppLog.Info("开始切回主用: " + _cfg.PrimarySsid + (manual ? "（手动）" : ""));

            var outcome = ConnectAndVerify(_cfg.PrimarySsid, "切回主用");
            if (outcome.AssociatedOk && outcome.LinkOk)
            {
                if (!outcome.InternetOk)
                {
                    RaiseNotify("已连接但无互联网",
                        _cfg.PrimarySsid + " 的网关正常，但探测不到公网。若是公共 WiFi 请先完成认证。");
                }
                _hitStreak = 0;
                _hitSince = -1;
                _lastStreakMs = 0;
                _missStreak = 0;
                _missSince = -1;
                _lastMissMs = 0;
                _returnFailCount = 0;
                _retryUntil = 0;
                _cooldownUntil = NowMs() + _cfg.CooldownSeconds * 1000L;
                SetState(EngineState.Normal);
                SetStatus("主用已连接：" + _cfg.PrimarySsid +
                          (outcome.InternetOk ? "（联网正常）" : "（已连接但无互联网）"));
                AppLog.Info("切回主用成功" + (outcome.InternetOk ? "（联网正常）" : "（仅网关通，无互联网）") +
                            "，已进入 " + _cfg.CooldownSeconds + " 秒冷却期。");
                History.Add("切回主用", _lastBackupSsid ?? "-", _cfg.PrimarySsid,
                            outcome.InternetOk ? "联网正常" : "网关通但无互联网");
                RaiseNotify("已切回主用", _cfg.PrimarySsid + (outcome.InternetOk ? " 连接正常" : " 已连接（暂无互联网）"));
                return;
            }

            _returnFailCount++;
            _hitStreak = 0;
            _hitSince = -1;
            _lastStreakMs = 0;
            _missStreak = 0;
            _missSince = -1;
            _lastMissMs = 0;
            var backoff = BackoffSeconds(_returnFailCount, 30, 600);
            _retryUntil = _cfg.BackoffEnabled ? NowMs() + backoff * 1000L : 0;
            AppLog.Warn("切回主用失败（第 " + _returnFailCount + " 次），准备回滚备用。");

            SetStatus("切回主用失败，正在回滚备用…");
            var rolledBackTo = RollbackToSomeBackup();
            bool rolledBack = rolledBackTo != null;

            _cooldownUntil = NowMs() + _cfg.CooldownSeconds * 1000L;
            SetState(EngineState.OnBackup);

            if (rolledBack)
            {
                SetStatus(_cfg.BackoffEnabled
                    ? string.Format("已回滚 {0}｜切回失败 {1} 次，{2} 秒后重试", _cfg.DescribeBackup(rolledBackTo), _returnFailCount, backoff)
                    : string.Format("已回滚 {0}｜切回失败 {1} 次", _cfg.DescribeBackup(rolledBackTo), _returnFailCount));
                History.Add("回滚备用", _cfg.PrimarySsid, rolledBackTo, "切回主用失败（第 " + _returnFailCount + " 次）");
                RaiseNotify("切回主用失败", "主用连上了但无法上网，已自动回滚到 " + rolledBackTo);
            }
            else
            {
                SetStatus(string.Format("切回失败，且所有备用都连不上（第 {0} 次）", _returnFailCount));
                History.Add("回滚失败", _cfg.PrimarySsid, "-", "切回主用失败且所有备用都连不上（第 " + _returnFailCount + " 次）");
                RaiseNotify("网络异常", "主用切回失败，备用网络也都连不上，请检查网络");
            }
        }

        /// <summary>
        /// 切回前用一次全新扫描确认主用真的在广播。
        /// 扫描缓存里的"幽灵条目"（热点已关但驱动还留着旧 BSS）会让程序断开当前正常连接去连一个
        /// 并不存在的热点，白白断网二十几秒之后再回滚。
        /// </summary>
        private bool ConfirmPrimaryBroadcasting()
        {
            try
            {
                _wlan.RequestScan();
                Thread.Sleep(2500); // 等驱动完成一次扫描
                var latest = _wlan.Scan(false);
                if (latest.Quality == ScanQuality.Failed) return false; // 读不到就当没确认，不冒险切
                var net = Find(latest.Networks, _cfg.PrimarySsid);
                if (net == null) return false;
                return SignalAccepted(net.SignalQuality);
            }
            catch (Exception ex)
            {
                AppLog.Warn("切回前复查扫描失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>回滚：先试刚才用的那个备用，再按优先级试其它在范围内的备用。</summary>
        private string RollbackToSomeBackup()
        {
            var targets = new List<string>();
            if (!string.IsNullOrWhiteSpace(_lastBackupSsid)) targets.Add(_lastBackupSsid);
            foreach (var b in _cfg.BackupSsids)
            {
                if (!targets.Any(t => SsidEquals(t, b))) targets.Add(b);
            }
            if (targets.Count == 0) return null;

            var rollbackScan = _wlan.Scan(false);
            if (rollbackScan.Quality == ScanQuality.Failed)
            {
                AppLog.Warn("回滚失败：扫描读取失败，无法确认备用是否在范围内。");
                return null;
            }
            var nets = rollbackScan.Networks;
            foreach (var target in targets)
            {
                if (_stop) return null;
                if (Find(nets, target) == null)
                {
                    AppLog.Info("回滚跳过 " + target + "（不在扫描范围内）");
                    continue;
                }
                var rollbackOutcome = ConnectAndVerify(target, "回滚备用");
                if (rollbackOutcome.AssociatedOk && rollbackOutcome.LinkOk)
                {
                    _lastBackupSsid = target;
                    AppLog.Info("已回滚到备用: " + target);
                    return target;
                }
            }
            return null;
        }

        /// <summary>
        /// 不在主用上（掉线 / 第三方网络）：尝试切到备用。
        /// level：当前所处网络的分级；skipSsid：跳过某个 SSID（备用自身网关死了，没必要连回自己）。
        /// </summary>
        private void HandleOffPrimary(ConnectionState conn, List<AvailableNetwork> nets, bool manual,
                                      NetworkLevel level, string skipSsid)
        {
            var nowMs = NowMs();
            SetState(EngineState.OnBackup);

            // 连着第三方网络但网关正常（Level ≥ 1）→ 保持现状，不去折腾
            if (conn.Connected && level != NetworkLevel.Level0)
            {
                SetStatus((level == NetworkLevel.Level2 ? "🟢" : "🟡") +
                          string.Format(" 已连接 {0}（非主备列表，网关正常，不切换）", conn.Ssid));
                return;
            }

            var backups = _cfg.BackupSsids ?? new List<string>();
            if (backups.Count == 0 && Find(nets, _cfg.PrimarySsid) == null)
            {
                SetStatus(conn.Connected
                    ? string.Format("已连接 {0}（非主用，且未配置备用网络）", conn.Ssid)
                    : "未连接 WiFi（未配置备用网络）");
                return;
            }

            if (!manual && nowMs < _nextBackupAttempt)
            {
                SetStatus(string.Format("{0}｜{1}，{2} 秒后重试",
                    conn.Connected ? "已连接 " + conn.Ssid : "WiFi 已断开",
                    _backupFailReason, RemainSeconds(nowMs, _nextBackupAttempt)));
                return;
            }

            // 连着网关不通的网络时，降低尝试频率：每 60 秒试一轮切换，
            // 且永远不进 TOTAL_FAILURE——还连着就不算"所有网络都不可用"。
            // （实测连着网关死掉的备用时，旧逻辑把它当"已断开"高频重试，还会误进 TotalFailure）
            bool holdingBrokenLink = conn.Connected;
            if (!manual && holdingBrokenLink && _backupFailCount >= 1 && nowMs < _brokenLinkNextAttempt)
            {
                SetStatus(string.Format("已连接 {0}（网关不可达）｜{1}，{2} 秒后重试",
                    conn.Ssid, _backupFailReason, RemainSeconds(nowMs, _brokenLinkNextAttempt)));
                return;
            }

            // 按优先级挑出此刻在范围内的候选（跳过刚判定为坏的那个）。
            // 主用本身就是最高优先级候选：掉线/连着死网时主用在范围内应直连主用。
            // 尤其主备全关进入 TOTAL_FAILURE 后**只有主用回来**（如重新打开手机热点）的场景，
            // 旧逻辑只试备用会永远连不上主用——README 承诺的是"重开任一网络自动恢复"。
            var candidates = new List<AvailableNetwork>();
            bool skipPrimary = !string.IsNullOrEmpty(skipSsid) && SsidEquals(_cfg.PrimarySsid, skipSsid);
            if (!skipPrimary)
            {
                var primaryNet = Find(nets, _cfg.PrimarySsid);
                if (primaryNet != null && SignalAccepted(primaryNet.SignalQuality)) candidates.Add(primaryNet);
            }
            foreach (var b in backups)
            {
                if (!string.IsNullOrEmpty(skipSsid) && SsidEquals(b, skipSsid)) continue;
                var net = Find(nets, b);
                if (net != null) candidates.Add(net);
            }

            if (candidates.Count == 0)
            {
                var wait = BackoffSeconds(++_backupFailCount, 30, 300);
                _nextBackupAttempt = NowMs() + wait * 1000L;
                _backupFailReason = string.Format("{0} 个备用都不在扫描范围", backups.Count);
                SetStatus(string.Format("{0}｜{1}，{2} 秒后重试",
                    conn.Connected ? "已连接 " + conn.Ssid : "WiFi 已断开", _backupFailReason, wait));
                AppLog.Info("所有备用网络都不在扫描结果中，等待重试。");
                TryEnterTotalFailure(conn);
                return;
            }

            AppLog.Info("主用不可用，按优先级尝试 " + candidates.Count + " 个在范围内的备用…");
            if (_cfg.HoldPolicy == EthernetHoldPolicy.AlwaysManage && _checker.IsEthernetUp())
            {
                AppLog.Info("检测到有线连接，但策略为「继续管理」，按主备规则继续切换。");
            }
            // 闸门在这里才扣：前面 candidates 为空提前返回时不算一次"尝试"，不消耗 60 秒窗口
            if (holdingBrokenLink)
            {
                _brokenLinkNextAttempt = NowMs() + BrokenLinkRetryIntervalMs;
            }
            foreach (var net in candidates)
            {
                if (_stop) return;
                bool toPrimary = SsidEquals(net.Ssid, _cfg.PrimarySsid);
                var label = toPrimary ? ("主用 " + net.Ssid) : _cfg.DescribeBackup(net.Ssid);
                SetStatus("🔴 网络已断开，正在切换 " + label + "（信号 " + net.SignalQuality + "%）…");
                AppLog.Info("尝试 " + label + "，信号 " + net.SignalQuality + "%");

                var outcome = ConnectAndVerify(net.Ssid, "切换备用");
                if (outcome.AssociatedOk && outcome.LinkOk)
                {
                    if (!outcome.InternetOk)
                    {
                        RaiseNotify("已连接但无互联网",
                            label + " 的网关正常，但探测不到公网。若是公共 WiFi 请先完成认证。");
                    }
                    _backupFailCount = 0;
                    _nextBackupAttempt = 0;
                    _lastBackupSsid = net.Ssid;
                    _cooldownUntil = NowMs() + _cfg.CooldownSeconds * 1000L;
                    _hitStreak = 0;
                    _hitSince = -1;
                    _lastStreakMs = 0;
                    _missStreak = 0;
                    _missSince = -1;
                    _lastMissMs = 0;
                    SetStatus((outcome.InternetOk ? "🟢 已连接 " : "🟡 已连接 ") + label +
                              (outcome.InternetOk ? "（联网正常）" : "，但无 Internet 访问（可能需要网页认证）"));
                    AppLog.Info("已切换到: " + net.Ssid);
                    History.Add(toPrimary ? "重连主用" : "切换备用", conn.Connected ? conn.Ssid : "未连接", net.Ssid,
                                "网络不可用，按优先级直连（候选信号 " + net.SignalQuality + "%）");
                    RaiseNotify(toPrimary ? "已连上主用" : "已切换到备用网络", label);
                    return;
                }

                AppLog.Warn(net.Ssid + " 不可用，换下一个候选。");
            }

            _backupFailCount++;
            var backoff = BackoffSeconds(_backupFailCount, 30, 300);
            _nextBackupAttempt = NowMs() + backoff * 1000L;
            _backupFailReason = "在范围内的备用都连不上";
            SetStatus(string.Format("🔴 网络已断开，{0}（第 {1} 次），{2} 秒后重试", _backupFailReason, _backupFailCount, backoff));
            AppLog.Warn("所有备用网络尝试失败（第 " + _backupFailCount + " 次），" + backoff + " 秒后重试。");
            TryEnterTotalFailure(conn);
        }

        /// <summary>
        /// 终极降级判定：只有"确实没连上任何 WiFi"且备用链路连续 3 轮全失败，才进入 TOTAL_FAILURE。
        /// 连着哪怕 Level 1 的网络都不算终极失败；连着网关不通的网络也不进（走慢轮询）。
        /// 迟滞防震荡：刚从 TotalFailure 解除后的 3 分钟内不允许再次进入
        /// （实测扫描缓存里的弱信号 SSID 会让"进入→看到→解除→再进入"0.1 秒循环）。
        /// </summary>
        private void TryEnterTotalFailure(ConnectionState conn)
        {
            if (conn != null && conn.Connected) return;
            if (_backupFailCount < 3) return;
            var nowMs = NowMs();
            if (nowMs < _tfReenterHoldUntil)
            {
                AppLog.Info("满足 TOTAL_FAILURE 条件，但处于解除后的稳定期内，保持低频重试。");
                return;
            }

            AppLog.Warn("进入 TOTAL_FAILURE 状态。");
            History.Add("进入降级", conn == null || !conn.Connected ? "未连接" : conn.Ssid, "-",
                        "所有已知网络均不可用（第 " + _backupFailCount + " 轮失败）");
            SetState(EngineState.TotalFailure);
            SetStatus("❌ 所有已知网络均不可用，请检查您的手机热点或路由器是否正常开启。");
            RaiseNotify("所有已知网络均不可用", "请检查您的手机热点或路由器是否正常开启。");
        }

        /// <summary>一次连接尝试的判定结果：关联 / 网关链路 / 外网三层分开记，避免"网关通但没公网"被误判成失败。</summary>
        private sealed class VerifyOutcome
        {
            public bool AssociatedOk;   // 关联成功（接口状态 connected 且 SSID 匹配）
            public bool LinkOk;         // 网关 ping 通
            public bool InternetOk;     // 外网探测通过（源绑定 TCP / 兜底路径）
        }

        /// <summary>连接（最多 2 次尝试）+ 等待关联 + 连通性验证。</summary>
        private VerifyOutcome ConnectAndVerify(string ssid, string action)
        {
            var outcome = new VerifyOutcome();

            for (int attempt = 1; attempt <= 2 && !_stop; attempt++)
            {
                uint code;
                string text;
                if (!_wlan.Connect(ssid, out code, out text))
                {
                    AppLog.Warn(action + "：第 " + attempt + " 次发起连接失败 —— " + text);
                    continue;
                }

                if (_wlan.WaitForConnection(ssid, 20000))
                {
                    outcome.AssociatedOk = true;
                    break;
                }

                AppLog.Warn(action + "：第 " + attempt + " 次尝试 20 秒内未关联上 " + ssid);
            }

            if (!outcome.AssociatedOk)
            {
                AppLog.Warn(action + "：" + ssid + " 两次连接尝试均未关联成功。");
                return outcome;
            }

            AppLog.Info(action + "：已关联 " + ssid + "，等待 DHCP。");
            Thread.Sleep(1500); // 给 DHCP 一点时间

            var result = _checker.Check(2000, 3, 2500);
            outcome.LinkOk = result.LinkOk;
            outcome.InternetOk = result.InternetOk;
            AppLog.Info(action + "：连通性 " + result.Detail);

            if (!outcome.LinkOk)
            {
                AppLog.Warn(action + "：" + ssid + " 网关不通，判定失败。" + result.Detail);
            }
            else if (!outcome.InternetOk)
            {
                AppLog.Warn(action + "：" + ssid + " 网关通但无互联网（可能需要认证或出口故障），仍视为连接成功。");
            }

            return outcome;
        }

        private AvailableNetwork Find(List<AvailableNetwork> nets, string ssid)
        {
            if (nets == null || string.IsNullOrWhiteSpace(ssid)) return null;
            return nets.FirstOrDefault(n => string.Equals(n.Ssid, ssid.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>信号阈值：0 表示只要求 SSID 存在。</summary>
        private bool SignalAccepted(int quality)
        {
            var threshold = _cfg.SignalThresholdPercent;
            return threshold <= 0 || quality >= threshold;
        }

        private static bool SsidEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>30s → 1m → 2m → 4m …，带上限。</summary>
        private static int BackoffSeconds(int attempt, int baseSeconds, int capSeconds)
        {
            if (attempt <= 0) attempt = 1;
            double factor = Math.Pow(2, Math.Min(attempt - 1, 10));
            var value = baseSeconds * factor;
            if (value > capSeconds) value = capSeconds;
            return (int)value;
        }

        private static int RemainSeconds(long nowMs, long targetMs)
        {
            var s = (int)Math.Ceiling((targetMs - nowMs) / 1000.0);
            return s < 0 ? 0 : s;
        }

        private static string ShortTime(DateTime t)
        {
            return t.ToString("HH:mm:ss");
        }

        private void SetStatus(string text)
        {
            // 假死网线时，把提示挂到状态文本末尾：否则本轮后段的状态机会立刻覆盖掉它，
            // 用户根本看不到"有线插着但没网"的提示。
            if (_ethBadActive && text.IndexOf(TextEthernetBad, StringComparison.Ordinal) < 0)
            {
                text = text + "｜" + TextEthernetBad;
            }

            // 双链路尾部：有线策略为「继续管理」且有线在插时，所有状态（主用/备用/倒计时…）
            // 统一在这里追加有线侧描述——比在每处 SetStatus 调用点手工拼更不容易漏（v1.2.0
            // 首版就漏了备用路径）。探测降频 90 秒，不拖慢引擎。
            // 注意：此时 text 还是 emoji 形态（🟢），判断也要用 emoji；标签转换在下一步统一做。
            text = AppendEthernetTail(text);

            // WinForms 的 GDI 渲染不支持彩色 emoji（🟢🟡 会退化成单色小轮廓，看不出区别），
            // 统一换成文字标签 —— 设置窗口还会按标签给状态行着色，托盘 tooltip 里也一目了然
            text = StatusTags.ToTags(text);

            if (string.Equals(_statusText, text, StringComparison.Ordinal)) return;
            _statusText = text;
            var handler = StatusChanged;
            if (handler != null)
            {
                try { handler(); } catch { }
            }
        }

        private void SetState(EngineState state)
        {
            if (_state == state) return;
            var old = _state;
            _state = state;
            AppLog.Info("状态: " + old + " → " + state);
        }

        private void RaiseNotify(string title, string message)
        {
            var handler = Notify;
            if (handler != null)
            {
                try { handler(title, message); } catch { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _wake.Set(); } catch { }
            try
            {
                if (_thread != null && !_thread.Join(3000))
                {
                    AppLog.Warn("状态机线程未能及时退出。");
                }
            }
            catch { }
        }
    }
}
