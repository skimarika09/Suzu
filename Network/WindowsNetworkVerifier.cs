using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Suzu.Core;

namespace Suzu.Network
{
    /// <summary>
    /// 连通性验证：ping 网关 + 探测外网，全部判定都必须"走无线网卡"。
    ///
    /// 主判定用**源地址绑定的 TCP 探测**：Windows Vista 以后默认强主机模型
    /// （strong host model），套接字绑定某接口的源 IP 后，流量只能从拥有该 IP
    /// 的接口出去，路由表里其它接口（以太网 / VPN 隧道）的默认路由抢不走。
    /// 因此"绑定 WLAN 的 IPv4 再 connect 外网"能真实回答"这条 WiFi 有没有网"，
    /// 与默认路由归属无关。而 v1.0.1 的"ping 通了再查 GetBestRoute 出口"是反过来的
    /// 思路——在多出口环境里会把好用的 WiFi 判成没网（有线/VPN 抢走默认路由时）。
    ///
    /// 兜底：万一绑定探测被安全软件拦掉，退回原来的 ICMP + 路由出口校验，
    /// 至少不比 v1.0.1 差。
    /// </summary>
    public sealed class WindowsNetworkVerifier : INetworkVerifier
    {
        private static readonly string[] InternetTargets = { "223.5.5.5", "114.114.114.114", "8.8.8.8" };
        private const int TcpProbePort = 53;

        /// <summary>源绑定探测目标：国内外 DNS/HTTPS 端口，多试几个提高命中率。</summary>
        private sealed class ProbeTarget
        {
            public string Host { get; private set; }
            public int Port { get; private set; }
            public string Path { get; private set; }
            public bool UseTls { get; private set; }
            public ProbeTarget(string host, int port) { Host = host; Port = port; Path = "/generate_204"; }
            public ProbeTarget(string host, int port, string path) { Host = host; Port = port; Path = path; }
            public ProbeTarget(string host, int port, string path, bool useTls) { Host = host; Port = port; Path = path; UseTls = useTls; }
        }

        private static readonly ProbeTarget[] BoundProbeTargets =
        {
            new ProbeTarget("223.5.5.5", 53),
            new ProbeTarget("223.5.5.5", 443),
            new ProbeTarget("114.114.114.114", 53),
            new ProbeTarget("8.8.8.8", 53),
            new ProbeTarget("8.8.8.8", 443)
        };

        /// <summary>
        /// HTTP 204 校验目标（业界标准的 generate_204 探测）：真外网返回 204，
        /// 被 Portal 劫持时返回 302/200 跳认证页。DNS 即使被劫持也无妨——
        /// 劫持解析出的认证服务器同样会给出 302，判定依然成立。
        /// ⚠️ 运营商/路由器的透明 HTTP 劫持（对 80 端口任意请求插 302 广告页）会把
        /// "网络正常"误判成 Portal——HTTPS 目标（TLS 证书校验，劫持者无法伪造）一旦
        /// 204 成功即可推翻单点 Portal 判定，见 CheckOnce 的多目标佐证逻辑。
        /// </summary>
        private static readonly ProbeTarget[] Http204Targets =
        {
            new ProbeTarget("connect.rom.miui.com", 80),
            new ProbeTarget("wifi.vivo.com.cn", 80),
            new ProbeTarget("connectivitycheck.platform.hicloud.com", 80),
            new ProbeTarget("www.msftconnecttest.com", 80, "/connecttest.txt"),
            new ProbeTarget("www.msftconnecttest.com", 443, "/connecttest.txt", true)
        };

        /// <summary>
        /// 检查一次连通性。<paramref name="attempts"/> 用于网络刚连上、DHCP 还没好的场景。
        /// </summary>
        public ConnectivityResult Check(int timeoutMs = 2000, int attempts = 1, int retryIntervalMs = 1500)
        {
            var last = new ConnectivityResult { Detail = "未检测" };
            var n = Math.Max(1, attempts);

            for (int i = 0; i < n; i++)
            {
                last = CheckOnce(timeoutMs);
                if (last.InternetOk) return last;
                if (i < n - 1) Thread.Sleep(retryIntervalMs);
            }
            return last;
        }

        /// <summary>
        /// 有线侧独立探测：绑定有线网卡自己的 IP，走与 WLAN 侧同一套 HTTP 204 判定。
        /// 供双链路状态文案用——"有没有网"以任一链路为准，但得知道每条各自什么状态。
        /// </summary>
        public EthernetNetState ProbeEthernet(int timeoutMs = 1200)
        {
            try
            {
                // 物化一次：结尾的 LinkDown/Absent 判断若重新枚举，多网卡热插拔时两次结果可能不一致
                var candidates = EthCandidates().ToList();
                foreach (var ni in candidates)
                {
                    var gateway = EthGatewayOf(ni);
                    if (gateway == null) continue;
                    if (!PingHost(gateway, timeoutMs)) continue; // 这块网卡链路不通，看下一块

                    var sourceIp = EthSourceIpOf(ni);
                    if (sourceIp == null) continue;

                    foreach (var target in Http204Targets)
                    {
                        string statusLine;
                        var verdict = HttpProbeBound(sourceIp, target, timeoutMs, out statusLine);
                        if (verdict == HttpVerdict.RealInternet) return EthernetNetState.InternetOk;
                        // Portal/Indeterminate → 试下一个目标（与 WLAN 侧多目标佐证规则一致：
                        // 单点 Portal 可能是运营商劫持，继续给其它目标翻案机会）
                    }
                    // HTTP 全部无法确认：与 WLAN 侧同规则，按受限处理
                    return EthernetNetState.NoInternet;
                }
                // 有候选但网关全不通，或根本没有候选
                return candidates.Count > 0 ? EthernetNetState.LinkDown : EthernetNetState.Absent;
            }
            catch
            {
                return EthernetNetState.Absent;
            }
        }

        private ConnectivityResult CheckOnce(int timeoutMs)
        {
            var result = new ConnectivityResult();

            // 1) 无线网卡的 IPv4 源地址：源绑定探测的前提
            var wlanIp = GetWlanSourceAddress();
            if (wlanIp == null)
            {
                result.Detail = "无线网卡未就绪（无 IPv4 配置），判定失败";
                return result;
            }

            // 2) 网关链路：只认无线网卡的网关
            var wlanIndex = GetWlanInterfaceIndex();
            var gateway = GetGatewayAddress();
            result.GatewayAddress = gateway;
            if (!string.IsNullOrEmpty(gateway))
            {
                result.GatewayOk = PingHost(gateway, timeoutMs);
            }

            // 3) 主判定：源绑定 + HTTP generate_204 校验。
            // 只看 TCP 握手成功在 Portal 环境不可靠（实测：未认证校园网劫持所有 TCP 连接，
            // 认证服务器替 223.5.5.5 应答 SYN，握手"成功"被误判为有外网 → 状态错显【正常】）。
            // HTTP 层校验响应码：204=真外网，302/重定向=Portal，都无法确认才走纯 TCP 兜底。
            // ⚠️ v1.2.1 误报修复：Portal 判定需要多目标佐证——运营商透明劫持（对 80 端口插
            // 302 广告页）会让单个目标误报 Portal，此前首个 Portal 就 break 导致"网络正常
            // 却显示受限"。现在：任一目标 RealInternet 直接成功；Portal 证据 <2 个不置位，
            // 由 HTTPS 目标（TLS 证书校验，劫持无法伪造）最终裁决。
            int portalCount = 0;
            string portalEvidence = null;
            foreach (var target in Http204Targets)
            {
                string statusLine;
                var verdict = HttpProbeBound(wlanIp, target, timeoutMs, out statusLine);
                if (verdict == HttpVerdict.RealInternet)
                {
                    result.InternetOk = true;
                    result.PortalDetected = false;
                    result.InternetTarget = target.Host + ":" + target.Port + " (HTTP " + (statusLine ?? "?")
                                            + (target.UseTls ? " TLS" : "") + " 绑定无线网卡)";
                    break;
                }
                if (verdict == HttpVerdict.Portal)
                {
                    portalCount++;
                    portalEvidence = target.Host + " (HTTP " + (statusLine ?? "?") + "，Portal 页面)";
                }
                // Indeterminate → 试下一个目标
            }

            if (result.InternetOk)
            {
                result.PortalDetected = false;
            }
            else if (portalCount >= 2)
            {
                // 多目标一致给出 Portal 响应 → 可信的认证拦截
                result.PortalDetected = true;
                result.InternetTarget = portalEvidence;
            }
            else
            {
                // 单点 Portal 证据（可能是运营商劫持）或全部无法确认 → 受限但不宣称 Portal
                result.PortalDetected = false;
                if (portalCount == 1)
                {
                    result.InternetTarget = portalEvidence + "，另有单点劫持嫌疑，不足以判定 Portal";
                }
            }

            // 3b) 兜底：HTTP 全部无法确认（且未确认 Portal）时退回纯 TCP 握手。
            // ⚠️ 实测教训（校园网 DNS 未认证时直接丢弃而非劫持）：HTTP 全 Indeterminate +
            // TCP 握手"成功"恰恰是 Portal 劫持的典型特征（认证服务器替目标应答 SYN），
            // 此时绝不能按"有外网"处理——改为按受限处理（PortalDetected=true，Level 1 保持连接）。
            // 代价：DNS 完全坏掉但 IP 直连能上网的网络会显示受限——可接受，浏览器同样解析不了域名。
            if (!result.InternetOk && !result.PortalDetected)
            {
                foreach (var target in BoundProbeTargets)
                {
                    if (!TcpProbeBound(wlanIp, target.Host, target.Port, timeoutMs)) continue;
                    result.PortalDetected = true;
                    result.InternetTarget = target.Host + ":" + target.Port + " (TCP 通但 HTTP 校验未完成，按受限处理)";
                    break;
                }
            }
            // HTTP 明确检出 Portal 后不再让纯 TCP 翻案（PortalDetected 已置位，直接跳过上面）

            // 4) 兜底一：ICMP ping + 路由出口校验（绑定探测被安全软件拦掉时的退路）
            if (!result.InternetOk)
            {
                foreach (var target in InternetTargets)
                {
                    if (!PingHost(target, timeoutMs)) continue;
                    if (wlanIndex == null || !RouteLeavesVia(target, wlanIndex.Value))
                    {
                        // ping 通了，但流量走的是有线/VPN 等其它网卡 —— 不能算这个 WiFi 有网
                        result.InternetTarget = target + " (ICMP，但出口不是无线网卡)";
                        continue;
                    }
                    result.InternetOk = true;
                    result.InternetTarget = target + " (ICMP via 路由校验)";
                    break;
                }
            }

            // 5) 兜底二：ICMP 被拦时，未绑定的 TCP + 路由校验
            if (!result.InternetOk && wlanIndex != null)
            {
                foreach (var target in InternetTargets)
                {
                    if (!TcpProbe(target, TcpProbePort, timeoutMs)) continue;
                    if (!RouteLeavesVia(target, wlanIndex.Value)) continue;
                    result.InternetOk = true;
                    result.InternetTarget = target + ":" + TcpProbePort + " (TCP via 路由校验)";
                    break;
                }
            }

            result.Detail = string.Format("网关 {0}={1}，外网={2}{3}",
                string.IsNullOrEmpty(gateway) ? "(无)" : gateway,
                result.GatewayOk ? "通" : "不通",
                result.InternetOk ? "通" : "不通",
                DescribeOutcome(result, gateway));
            return result;
        }

        private static string DescribeOutcome(ConnectivityResult result, string gateway)
        {
            if (result.InternetOk) return " via " + result.InternetTarget;

            // 失败时把原因说清楚，方便看日志排障
            if (result.PortalDetected) return "（检出 Portal 认证拦截：" + (result.InternetTarget ?? "HTTP 被重定向") + "）";
            if (string.IsNullOrEmpty(gateway)) return "（没有取到无线网卡的网关）";
            if (result.GatewayOk) return "（网关通但外网探测失败：路由器活着、出口断了）";
            return "（网关不通，链路可能断了）";
        }

        public static bool PingHost(string host, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            try
            {
                using (var ping = new Ping())
                {
                    var reply = ping.Send(host, timeoutMs, new byte[32], new PingOptions(64, true));
                    return reply != null && reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 绑定源地址的 TCP 探测。绑定后走强主机模型，只会从拥有该 IP 的网卡发出。
        /// 绑定失败、连接失败、超时一律返回 false，不抛异常。
        /// 注意：超时后 using 会 Dispose 掉 TcpClient，在途连接以 SocketException 995 中止，
        /// 这个异常落在没人观察的 Task 上，会被 finalizer 在 GC 时翻出来刷进日志
        /// （实测一天 1170 条 ERROR）。返回前必须 ContinueWith 观察掉，防刷屏。
        /// </summary>
        private static bool TcpProbeBound(IPAddress sourceIp, string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    client.Client.Bind(new IPEndPoint(sourceIp, 0));
                    var task = client.ConnectAsync(host, port);
                    if (!task.Wait(timeoutMs))
                    {
                        Observe(task); // 吞掉稍后到达的中止异常，防止 UnobservedTaskException 刷日志
                        return false;
                    }
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TcpProbe(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(host, port);
                    if (!task.Wait(timeoutMs))
                    {
                        Observe(task);
                        return false;
                    }
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>观察掉 Task 上的异常（不管成败），杜绝 UnobservedTaskException 被 finalizer 翻出。</summary>
        private static void Observe(Task task)
        {
            task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private enum HttpVerdict { RealInternet, Portal, Indeterminate }

        /// <summary>
        /// 源绑定 HTTP 探测：连接目标端口发探测请求，按响应判定真外网 / Portal。
        /// 判定规则（实测校园网认证页对任意 GET 都回 200 + 登录页，绝不能把 200 一律当真外网）：
        ///   /generate_204 目标：204 = 真外网；其它任何状态码（200/302/…）= Portal；
        ///   /connecttest.txt 目标：200 且响应体含 "Microsoft Connect Test" = 真外网；否则 Portal；
        /// 连不上/超时/响应异常 = Indeterminate（交给调用方走兜底）。
        /// useTls=true 时先做 TLS 握手（SslStream 校验服务器证书——劫持者拿不出真证书，
        /// 握手失败按 Indeterminate 而不是 Portal，这正是 HTTPS 能推翻单点 Portal 误报的原因）。
        /// DNS 解析不做源绑定——即使 DNS 被劫持到认证服务器，其 Portal 响应同样给出正确判定。
        /// statusLine 返回原始状态行，供日志定位。
        /// </summary>
        private static HttpVerdict HttpProbeBound(IPAddress sourceIp, ProbeTarget target, int timeoutMs, out string statusLine)
        {
            statusLine = null;
            var host = target.Host;
            var port = target.Port;
            var targetPath = target.Path;
            try
            {
                // DNS 解析限时（校园网未认证时常直接丢弃 DNS 包，GetHostAddresses 默认会慢速
                // 超时十几秒，4 个目标能把一次 Check 拖到分钟级）——超时按 Indeterminate 处理
                var resolveTask = Task.Run(() => Dns.GetHostAddresses(host));
                if (!resolveTask.Wait(timeoutMs))
                {
                    Observe(resolveTask);
                    return HttpVerdict.Indeterminate;
                }
                var addresses = resolveTask.Result;
                var targetIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (targetIp == null) return HttpVerdict.Indeterminate;

                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    client.Client.Bind(new IPEndPoint(sourceIp, 0));
                    var task = client.ConnectAsync(targetIp, port);
                    if (!task.Wait(timeoutMs))
                    {
                        Observe(task);
                        return HttpVerdict.Indeterminate;
                    }
                    if (!client.Connected) return HttpVerdict.Indeterminate;

                    using (var stream = client.GetStream())
                    {
                        // TLS（若启用）必须在读写期间保持存活——不能放进 using 块后在外部使用
                        System.IO.Stream io = stream;
                        System.Net.Security.SslStream ssl = null;
                        if (target.UseTls)
                        {
                            // 证书校验失败（劫持者拿不出真证书）会在这里抛异常 → Indeterminate；
                            // 释放顺序由最外层 client.Dispose() 统一兜底
                            ssl = new System.Net.Security.SslStream(stream, false);
                            ssl.AuthenticateAsClient(host);
                            io = ssl;
                        }
                        try
                        {
                        io.ReadTimeout = timeoutMs;
                        io.WriteTimeout = timeoutMs;
                        var request = Encoding.ASCII.GetBytes(
                            "GET " + targetPath + " HTTP/1.1\r\n" +
                            "Host: " + host + "\r\n" +
                            "User-Agent: Suzu-ConnectivityCheck\r\n" +
                            "Connection: close\r\n\r\n");
                        io.Write(request, 0, request.Length);

                        var buffer = new byte[2048];
                        var read = io.Read(buffer, 0, buffer.Length);
                        if (read <= 0) return HttpVerdict.Indeterminate;
                        var response = Encoding.ASCII.GetString(buffer, 0, read);
                        if (!response.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                        {
                            return HttpVerdict.Indeterminate;
                        }

                        // 提取状态行（如 "HTTP/1.1 204 No Content"）
                        var lineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
                        statusLine = lineEnd > 0 ? response.Substring(0, lineEnd) : response;

                        bool isMsft = targetPath == "/connecttest.txt";
                        if (isMsft)
                        {
                            // 微软 NCSI：200 + 响应体含固定校验串才算真外网（Portal 会 302 或返回自己的页面）
                            if (statusLine.Contains(" 200 ") && response.Contains("Microsoft Connect Test"))
                            {
                                return HttpVerdict.RealInternet;
                            }
                            return HttpVerdict.Portal;
                        }

                        // generate_204 目标：只有 204 是真外网；200（登录页）/302（跳转）/其它全是 Portal
                        if (statusLine.Contains(" 204 ")) return HttpVerdict.RealInternet;
                        return HttpVerdict.Portal;
                        }
                        finally
                        {
                            if (ssl != null) ssl.Dispose();
                        }
                    }
                }
            }
            catch
            {
                return HttpVerdict.Indeterminate;
            }
        }

        /// <summary>
        /// 只测网关（Level 1 与否），不碰外网。专供"已连接状态下每轮分级"用，timeoutMs 建议 1000。
        /// </summary>
        public NetworkLevel CheckLinkOnly(int timeoutMs = 1000)
        {
            var gateway = GetGatewayAddress();
            if (string.IsNullOrEmpty(gateway)) return NetworkLevel.Level0;
            if (!PingHost(gateway, timeoutMs)) return NetworkLevel.Level0;
            return NetworkLevel.Level1;
        }

        /// <summary>
        /// 是否存在真实的、已连接（有载波）的以太网网卡。
        /// 注意排除 VPN / Hyper-V / VMware 之类的虚拟网卡 —— 否则开着 VPN 时
        /// "虚拟以太网"会让程序误以为插了网线，从而永久暂停 WiFi 切换。
        /// </summary>
        public bool IsEthernetUp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (IsVirtualAdapter(ni)) continue;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// 有线网关是否可达（轻量）：只 ping 有线网卡自己的默认网关，不碰外网。
        /// 供挂起状态下的高频复验用；"这根线能不能上网"的完整判定走 IsEthernetUsable。
        /// </summary>
        public bool IsEthernetGatewayAlive(int timeoutMs = 1000)
        {
            try
            {
                foreach (var ni in EthCandidates())
                {
                    var gateway = EthGatewayOf(ni);
                    if (gateway == null) continue;
                    if (PingHost(gateway, timeoutMs)) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// 有线是否**真的值得托付**（Level 2）：网关可达 且 源绑定（绑有线网卡自己的 IP）
        /// 外网探测通过。强主机模型同样适用于有线网卡 —— 绑有线的 IP 探测，流量只能从
        /// 有线出去，WiFi/VPN/路由表都干扰不了对"这根线自己能不能上网"的判定。
        /// 反例：插着没额度的随身WiFi/需认证的校园网时，网关 ping 得通但外网探测必然失败，
        /// 此时不允许挂起 WiFi（v1.1.2 只验网关，用户会被锁死在断网状态）。
        /// </summary>
        public bool IsEthernetUsable(int timeoutMs = 1000)
        {
            try
            {
                foreach (var ni in EthCandidates())
                {
                    var gateway = EthGatewayOf(ni);
                    if (gateway == null) continue;
                    if (!PingHost(gateway, timeoutMs)) continue; // 网关都不通，别浪费外网探测

                    var sourceIp = EthSourceIpOf(ni);
                    if (sourceIp == null) continue;

                    foreach (var target in BoundProbeTargets)
                    {
                        if (TcpProbeBound(sourceIp, target.Host, target.Port, timeoutMs)) return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>非虚拟、Up 状态的物理以太网网卡（多块时任一可用即算可用）。</summary>
        private static IEnumerable<NetworkInterface> EthCandidates()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (IsVirtualAdapter(ni)) continue;
                yield return ni;
            }
        }

        /// <summary>以太网网卡自己的 IPv4 网关（不能复用 GetGatewayAddress，那个只认 WLAN）。</summary>
        private static string EthGatewayOf(NetworkInterface ni)
        {
            var gateway = ni.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a != null
                                     && a.AddressFamily == AddressFamily.InterNetwork
                                     && !a.Equals(IPAddress.Any));
            return gateway == null ? null : gateway.ToString();
        }

        /// <summary>以太网网卡上配置的 IPv4 单播地址（源绑定探测用）。</summary>
        private static IPAddress EthSourceIpOf(NetworkInterface ni)
        {
            return ni.GetIPProperties().UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a != null
                                     && a.AddressFamily == AddressFamily.InterNetwork
                                     && !a.Equals(IPAddress.Any)
                                     && !IPAddress.IsLoopback(a));
        }

        /// <summary>
        /// 判断网卡是不是虚拟/隧道网卡（VPN、Hyper-V、VMware、Wi-Fi Direct 等）。
        /// 描述里带这些关键字的一律排除，避免把 VPN 隧道当成本机物理网卡。
        /// </summary>
        private static bool IsVirtualAdapter(NetworkInterface ni)
        {
            string text;
            try
            {
                text = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
            }
            catch
            {
                return false;
            }

            string[] keywords =
            {
                "virtual", "vpn", "tap-", "tap ", "wintun", "wireguard", "openvpn",
                "tunnel", "teredo", "isatap", "loopback", "hyper-v", "vmware",
                "virtualbox", "wi-fi direct", "wifi direct", "pseudo", "miniport",
                "sangfor", "easyconnect", "fortinet", "anyconnect", "clash", "shadow",
                "vgate" // 实测某 VPN 客户端的虚拟网卡名就叫 vgate0，类型还是非标准的 53
            };

            foreach (var keyword in keywords)
            {
                if (text.IndexOf(keyword, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>是否是物理无线网卡（排除虚拟 WiFi 适配器）。</summary>
        private static bool IsPhysicalWlan(NetworkInterface ni)
        {
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return false;
            if (ni.OperationalStatus != OperationalStatus.Up) return false;
            return !IsVirtualAdapter(ni);
        }

        /// <summary>无线网卡上配置的第一个 IPv4 单播地址；没有则返回 null。</summary>
        private static IPAddress GetWlanSourceAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsPhysicalWlan(ni)) continue;

                    var address = ni.GetIPProperties().UnicastAddresses
                        .Select(a => a.Address)
                        .FirstOrDefault(a => a != null
                                             && a.AddressFamily == AddressFamily.InterNetwork
                                             && !a.Equals(IPAddress.Any)
                                             && !IPAddress.IsLoopback(a));
                    if (address != null) return address;
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>无线网卡的 IPv4 接口索引；没有无线网卡或没拿到 IPv4 配置时返回 null。</summary>
        private static uint? GetWlanInterfaceIndex()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsPhysicalWlan(ni)) continue;
                    var ipv4 = ni.GetIPProperties().GetIPv4Properties();
                    if (ipv4 != null) return (uint)ipv4.Index;
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>取无线网卡的默认网关（IPv4）。只看物理无线网卡，绝不回退到有线/VPN 隧道。</summary>
        public static string GetGatewayAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsPhysicalWlan(ni)) continue;

                    var gw = ni.GetIPProperties().GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => a != null && a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                    if (gw != null) return gw.ToString();
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>查系统路由表：到 target 的流量是否从指定接口出去（仅兜底路径使用）。</summary>
        private static bool RouteLeavesVia(string target, uint interfaceIndex)
        {
            try
            {
                IPAddress addr;
                if (!IPAddress.TryParse(target, out addr) || addr.AddressFamily != AddressFamily.InterNetwork)
                {
                    return false;
                }

                var bytes = addr.GetAddressBytes();
                var dest = BitConverter.ToUInt32(bytes, 0); // 网络字节序，与 in_addr 内部表示一致
                var row = new MIB_IPFORWARDROW();
                var rc = GetBestRoute(dest, 0, out row);
                if (rc != 0)
                {
                    return false; // 查不到路由就当不可用，宁可误判失败也不误判成功
                }
                return row.dwForwardIfIndex == interfaceIndex;
            }
            catch
            {
                return false;
            }
        }

        #region iphlpapi 路由查询（仅兜底路径使用）

        /// <summary>MIB_IPFORWARDROW：与 iphlpapi.h 字段顺序/尺寸严格一致。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_IPFORWARDROW
        {
            public uint dwForwardDest;
            public uint dwForwardMask;
            public uint dwForwardPolicy;
            public uint dwForwardNextHop;
            public uint dwForwardIfIndex;
            public uint dwForwardType;
            public uint dwForwardProto;
            public uint dwForwardAge;
            public uint dwForwardNextHopAS;
            public int dwForwardMetric1;
            public int dwForwardMetric2;
            public int dwForwardMetric3;
            public int dwForwardMetric4;
            public int dwForwardMetric5;
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MIB_IPFORWARDROW pBestRoute);

        #endregion
    }
}
