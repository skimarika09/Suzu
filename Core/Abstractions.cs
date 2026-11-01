using System;
using System.Collections.Generic;

namespace Suzu.Core
{
    /// <summary>
    /// 跨平台抽象。Core 只依赖这些接口，不依赖任何 Windows 实现；
    /// 想支持 macOS/Linux，实现 IWifiProvider + INetworkVerifier 即可。
    /// </summary>

    /// <summary>WiFi 读写底层（扫描 / 连接 / 配置文件）。</summary>
    public interface IWifiProvider : IDisposable
    {
        /// <summary>底层句柄是否已就绪。</summary>
        bool IsOpen { get; }

        /// <summary>最近一次扫描读取的错误码（0 = 成功）。</summary>
        uint LastScanError { get; }

        /// <summary>平台侧状态变化通知（仅作为"该检查了"的唤醒信号）。</summary>
        event Action Notified;

        /// <summary>打开底层句柄并注册状态通知。</summary>
        void Open();

        /// <summary>读取（必要时发起）可用网络列表。</summary>
        ScanResult Scan(bool requestScan);

        /// <summary>只发起一次扫描，不读结果。</summary>
        void RequestScan();

        /// <summary>读取当前连接状态。</summary>
        ConnectionState GetConnection();

        /// <summary>枚举已保存的配置文件（SSID → 配置文件名称）。</summary>
        Dictionary<string, string> GetProfiles();

        /// <summary>连接指定 SSID（要求已有保存的配置文件）。</summary>
        bool Connect(string ssid, out uint errorCode, out string errorText);

        /// <summary>等待接口连上指定 SSID。</summary>
        bool WaitForConnection(string ssid, int timeoutMs, System.Threading.CancellationToken token = default(System.Threading.CancellationToken));

        /// <summary>断开当前连接。</summary>
        void Disconnect();
    }

    /// <summary>连通性验证（网关 / 外网 / 有线判定）。</summary>
    public interface INetworkVerifier
    {
        /// <summary>轻量探测：只测网关，判断 Level 1 与否。</summary>
        NetworkLevel CheckLinkOnly(int timeoutMs);

        /// <summary>完整探测：网关 + 外网。</summary>
        ConnectivityResult Check(int timeoutMs, int attempts, int retryIntervalMs);

        /// <summary>是否存在真实的、已连接的以太网网卡。</summary>
        bool IsEthernetUp();

        /// <summary>
        /// 有线网关是否可达（轻量：只 ping 有线网卡自己的网关，不碰外网）。
        /// 用于挂起状态下的高频复验；完整判定用 IsEthernetUsable。
        /// </summary>
        bool IsEthernetGatewayAlive(int timeoutMs);

        /// <summary>
        /// 有线是否**真的值得托付**：网关可达 且 源绑定（绑有线网卡自己的 IP）外网探测通过，
        /// 即有线达到 Level 2。"网线是坏的/对端断电/网关活着但上不了网（如没额度的随身WiFi、
        /// 需认证的校园网）"都返回 false —— 挂起 WiFi 的门槛必须是有线真有外网，
        /// 否则会把用户锁死在断网状态。
        /// </summary>
        bool IsEthernetUsable(int timeoutMs);

        /// <summary>有线侧独立探测（双链路状态文案用）：返回 EthernetNetState。</summary>
        EthernetNetState ProbeEthernet(int timeoutMs);
    }

    /// <summary>开机自启（Windows 下是计划任务（登录触发、最高权限），其它平台可换实现）。</summary>
    public interface IAutoStart
    {
        /// <summary>当前是否已登记开机自启。</summary>
        bool IsEnabled();

        /// <summary>写入 / 移除开机自启。</summary>
        void SetEnabled(bool enabled);
    }

    /// <summary>跨平台的网络错误码常量（与 Windows WLAN API 取值一致）。</summary>
    public static class NetworkErrorCodes
    {
        /// <summary>拒绝访问（未以管理员身份运行）。</summary>
        public const uint AccessDenied = 5;
    }
}
