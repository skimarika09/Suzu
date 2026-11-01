using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Suzu.Core;

namespace Suzu.Tests
{
    /// <summary>每个 SSID 的"健康度"，决定假探测的返回值。</summary>
    public enum SsidHealth
    {
        /// <summary>网关通 + 外网通（Level 2）。</summary>
        Good,
        /// <summary>网关通，但外网探测检出 Portal 劫持（Level 1，校园网未认证）。</summary>
        Portal,
        /// <summary>网关 ping 不通且外网/Portal 全失败（Level 0，链路死）。</summary>
        GatewayDead
    }

    /// <summary>
    /// 假 WiFi 底层：测试线程随时改字段模拟环境变化（扫描列表、当前连接、能否关联），
    /// 然后 RaiseNotified() 唤醒引擎立即评估。Connect 只表示"发起成功"，
    /// 能否关联由 WaitForConnection（CanAssociate）决定 —— 与真实 WLAN API 的语义一致。
    /// </summary>
    public sealed class FakeWifiProvider : IWifiProvider
    {
        public readonly List<AvailableNetwork> Visible = new List<AvailableNetwork>();
        public string ConnectedSsid;
        public int ConnectedSignal = 80;
        public Func<string, bool> CanAssociate = delegate { return true; };
        public readonly List<string> ConnectLog = new List<string>();
        public int RequestScanCount;

        public event Action Notified;

        public bool IsOpen { get { return true; } }
        public uint LastScanError { get { return 0; } }
        public void Open() { }

        public ScanResult Scan(bool requestScan)
        {
            lock (Visible)
            {
                return new ScanResult
                {
                    Networks = Visible.ToList(),
                    Quality = ScanQuality.Fresh
                };
            }
        }

        public void RequestScan() { RequestScanCount++; }

        public ConnectionState GetConnection()
        {
            return new ConnectionState
            {
                Connected = !string.IsNullOrEmpty(ConnectedSsid),
                Ssid = ConnectedSsid,
                SignalQuality = ConnectedSignal
            };
        }

        public Dictionary<string, string> GetProfiles() { return new Dictionary<string, string>(); }

        public bool Connect(string ssid, out uint errorCode, out string errorText)
        {
            lock (ConnectLog) { ConnectLog.Add(ssid); }
            errorCode = 0;
            errorText = null;
            return true;
        }

        public bool WaitForConnection(string ssid, int timeoutMs, CancellationToken token)
        {
            if (!CanAssociate(ssid)) return false;
            ConnectedSsid = ssid; // 关联成功即视为当前连接（真实实现等接口状态，这里同步完成）
            return true;
        }

        public void Disconnect() { ConnectedSsid = null; }

        public void RaiseNotified()
        {
            var h = Notified;
            if (h != null) h();
        }

        public void Dispose() { }
    }

    /// <summary>
    /// 假连通性探测：按"当前连接的 SSID"查健康度表（缺省 Good），
    /// 语义与真实实现对齐 —— CheckLinkOnly 只测网关（好网关给 Level 1），
    /// Check 才是完整探测（Good=Level2 / Portal=Level1 / GatewayDead=Level0）。
    /// </summary>
    public sealed class FakeVerifier : INetworkVerifier
    {
        public readonly FakeWifiProvider Wifi;
        public readonly Dictionary<string, SsidHealth> Health = new Dictionary<string, SsidHealth>();

        public bool EthUp;
        public bool EthGatewayAlive;
        public bool EthUsable;
        public EthernetNetState EthState = EthernetNetState.Absent;

        public FakeVerifier(FakeWifiProvider wifi) { Wifi = wifi; }

        private SsidHealth Current
        {
            get
            {
                var ssid = Wifi.ConnectedSsid;
                if (string.IsNullOrEmpty(ssid)) return SsidHealth.GatewayDead;
                SsidHealth h;
                return Health.TryGetValue(ssid, out h) ? h : SsidHealth.Good;
            }
        }

        public NetworkLevel CheckLinkOnly(int timeoutMs)
        {
            if (string.IsNullOrEmpty(Wifi.ConnectedSsid)) return NetworkLevel.Level0;
            return Current == SsidHealth.GatewayDead ? NetworkLevel.Level0 : NetworkLevel.Level1;
        }

        public ConnectivityResult Check(int timeoutMs, int attempts, int retryIntervalMs)
        {
            switch (Current)
            {
                case SsidHealth.Good:
                    return new ConnectivityResult
                    {
                        GatewayOk = true, InternetOk = true, GatewayAddress = "192.168.1.1",
                        InternetTarget = "fake-204", Detail = "fake: good"
                    };
                case SsidHealth.Portal:
                    return new ConnectivityResult
                    {
                        GatewayOk = true, InternetOk = false, PortalDetected = true,
                        GatewayAddress = "192.168.1.1", Detail = "fake: portal"
                    };
                default:
                    return new ConnectivityResult
                    {
                        GatewayOk = false, InternetOk = false, PortalDetected = false,
                        GatewayAddress = null, Detail = "fake: gateway dead"
                    };
            }
        }

        public bool IsEthernetUp() { return EthUp; }
        public bool IsEthernetGatewayAlive(int timeoutMs) { return EthGatewayAlive; }
        public bool IsEthernetUsable(int timeoutMs) { return EthUsable; }
        public EthernetNetState ProbeEthernet(int timeoutMs) { return EthState; }
    }
}
