using System;
using System.Collections.Generic;

namespace Suzu.Core
{
    /// <summary>
    /// 跨平台的纯数据模型。这里不允许出现任何 Windows API / P-Invoke 调用，
    /// 未来 macOS/Linux 实现只要产出同样的数据即可复用整个 Core。
    /// </summary>

    /// <summary>一次扫描里看到的可用网络（去重后按信号降序）。</summary>
    public sealed class AvailableNetwork
    {
        public string Ssid { get; set; }
        public int SignalQuality { get; set; }
        public bool Connectable { get; set; }
        public bool HasProfile { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1}%{2})", Ssid, SignalQuality, Connectable ? "" : ", 不可连接");
        }
    }

    /// <summary>当前连接状态快照。</summary>
    public sealed class ConnectionState
    {
        public bool Connected { get; set; }
        public string Ssid { get; set; }
        public string ProfileName { get; set; }
        public int SignalQuality { get; set; }
        /// <summary>平台自定义的接口状态码（Windows 下是 WLAN_INTERFACE_STATE）。</summary>
        public int InterfaceState { get; set; }
        public uint LastError { get; set; }

        public override string ToString()
        {
            return Connected ? (Ssid + " " + SignalQuality + "%") : "未连接";
        }
    }

    /// <summary>一次扫描读取结果的可信度。</summary>
    public enum ScanQuality
    {
        /// <summary>本次读到了新的可用网络列表。</summary>
        Fresh,
        /// <summary>列表可用但数据偏旧。</summary>
        Stale,
        /// <summary>读取失败，返回的是上一次成功结果的副本。</summary>
        Failed
    }

    /// <summary>扫描结果包装：既给列表，也告诉调用方这份数据能不能当真。</summary>
    public sealed class ScanResult
    {
        /// <summary>可用网络列表，永不为 null（读取失败时是上次成功结果的副本或空表）。</summary>
        public List<AvailableNetwork> Networks { get; set; }

        /// <summary>本次读取的可信度。</summary>
        public ScanQuality Quality { get; set; }
    }

    /// <summary>网络可用性分级：分级只用来决定"要不要切走"和状态文案。</summary>
    public enum NetworkLevel
    {
        /// <summary>链路断：取不到网关，或网关 ping 不通（网卡假死 / 路由器卡死）。</summary>
        Level0,
        /// <summary>链路正常：网关 ping 通，外网可能不通（校园网未认证 / VPN 节点挂了）。</summary>
        Level1,
        /// <summary>外网正常：网关通 + 外网探测通。</summary>
        Level2
    }

    /// <summary>有线侧链路状态（双链路状态文案用：WiFi 与有线并存时，分别描述各自状态）。</summary>
    public enum EthernetNetState
    {
        /// <summary>没有可用的有线网卡（未插或全是虚拟网卡）。</summary>
        Absent,
        /// <summary>有载波但取不到网关 / 网关 ping 不通。</summary>
        LinkDown,
        /// <summary>网关通但外网探测失败（含 Portal 拦截、没额度的随身WiFi）。</summary>
        NoInternet,
        /// <summary>网关通且外网探测通过。</summary>
        InternetOk
    }

    /// <summary>一次连通性探测的结果。</summary>
    public sealed class ConnectivityResult
    {
        public bool GatewayOk { get; set; }
        public bool InternetOk { get; set; }
        /// <summary>检测到网页认证拦截（Portal）：TCP 能通但 HTTP 被重定向到认证页，外网实际不可用。</summary>
        public bool PortalDetected { get; set; }
        public string GatewayAddress { get; set; }
        public string InternetTarget { get; set; }
        public string Detail { get; set; }

        public bool Ok { get { return InternetOk; } }

        /// <summary>
        /// 链路是否通。判据是"取到了无线网卡网关 且 （网关 ping 得通 或 外网探测通 或 检测到 Portal）"：
        /// 有些 AP/路由器会直接丢弃到网关的 ICMP（实测本机宿舍 AP 就是），此时网关 ping 不通
        /// 但外网明明可用 —— 不能因此判成"链路断了"；Portal 劫持也证明链路层活着（只是需要认证），
        /// 必须判 Level 1 保持连接等待用户认证，绝不能判 Level 0 触发切换。
        /// </summary>
        public bool LinkOk
        {
            get { return !string.IsNullOrEmpty(GatewayAddress) && (GatewayOk || InternetOk || PortalDetected); }
        }

        /// <summary>本次探测对应的分级。</summary>
        public NetworkLevel Level
        {
            get { return LinkOk ? (InternetOk ? NetworkLevel.Level2 : NetworkLevel.Level1) : NetworkLevel.Level0; }
        }
    }
}
