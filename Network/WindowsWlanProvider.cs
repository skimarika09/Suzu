using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using Suzu.Core;
using static Suzu.Network.NativeMethods;

namespace Suzu.Network
{
    /// <summary>wlanapi 的高层封装：接口选择、扫描、连接、通知。</summary>
    public sealed class WindowsWlanProvider : IWifiProvider
    {
        private readonly object _sync = new object();
        private IntPtr _handle = IntPtr.Zero;
        private WlanNotificationCallback _callback; // 必须保持引用，否则回调被 GC 回收
        private bool _disposed;

        /// <summary>上一次成功读取到的可用网络列表（读取失败时拿它兜底，绝不用空表冒充"什么都没扫到"）。</summary>
        private List<AvailableNetwork> _lastGoodNets = new List<AvailableNetwork>();
        private int _lastLoggedScanCount = -1;   // 上一次写进日志的网络数量（-1 = 还没记过，首次必记）
        private long _lastScanLogMs;             // 上一次扫描日志的时间（TickCount64 毫秒），每小时心跳用

        public Guid InterfaceGuid { get; private set; }
        public string InterfaceDescription { get; private set; }
        public bool IsOpen { get { return _handle != IntPtr.Zero; } }

        /// <summary>最近一次扫描返回的错误码（0 表示成功）。非 0 时扫描结果不可信。</summary>
        public uint LastScanError { get; private set; }

        public const uint ErrorAccessDenied = 5;
        /// <summary>服务未启动：本机没有 WLAN 硬件或 WLAN AutoConfig 未运行（对无网卡机器属永久性失败）。</summary>
        public const uint ErrorServiceNotActive = 1062;

        /// <summary>WLAN 通知（任意线程）——仅作为"该检查了"的唤醒信号。</summary>
        public event Action Notified;

        public void Open()
        {
            lock (_sync)
            {
                if (_handle != IntPtr.Zero) return;

                uint negotiated;
                IntPtr handle;
                var rc = WlanOpenHandle(WLAN_CLIENT_VERSION_VISTA, IntPtr.Zero, out negotiated, out handle);
                if (rc != ERROR_SUCCESS)
                {
                    throw new WlanException("WlanOpenHandle 失败，rc=" + rc + "（" + WlanError.Describe(rc) + "）", rc);
                }
                _handle = handle;

                SelectInterface();

                _callback = OnNotification;
                uint prev;
                rc = WlanRegisterNotification(_handle, WLAN_NOTIFICATION_SOURCE_ALL, true, _callback, IntPtr.Zero, IntPtr.Zero, out prev);
                if (rc != ERROR_SUCCESS)
                {
                    // 通知注册失败不致命（还有轮询兜底）
                    Core.AppLog.Warn("WlanRegisterNotification 失败 rc=" + rc + "，仅依赖轮询。");
                }

                Core.AppLog.Info("WLAN 接口: " + InterfaceDescription + " {" + InterfaceGuid + "}");
            }
        }

        /// <summary>选择第一个可用（优先已连接）的无线接口。</summary>
        private void SelectInterface()
        {
            IntPtr listPtr;
            var rc = WlanEnumInterfaces(_handle, IntPtr.Zero, out listPtr);
            if (rc != ERROR_SUCCESS)
            {
                throw new WlanException("WlanEnumInterfaces 失败 rc=" + rc, rc);
            }

            try
            {
                var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(listPtr);
                var infos = ReadArray<WLAN_INTERFACE_INFO>(listPtr, header.dwNumberOfItems);
                if (infos.Length == 0)
                {
                    throw new WlanException("系统中没有找到无线网卡。", 0);
                }

                // 候选接口全记一遍，跨设备排查"为什么识别不到网络"时必须靠这段
                Core.AppLog.Info("无线接口共 " + infos.Length + " 个：");
                foreach (var info in infos)
                {
                    Core.AppLog.Info("  - " + info.strInterfaceDescription
                                     + "  {" + info.InterfaceGuid + "}"
                                     + "  状态=" + info.isState);
                }

                var pick = infos.FirstOrDefault(i => i.isState == WLAN_INTERFACE_STATE.wlan_interface_state_connected);
                if (string.IsNullOrEmpty(pick.strInterfaceDescription))
                {
                    pick = infos[0];
                }

                InterfaceGuid = pick.InterfaceGuid;
                InterfaceDescription = pick.strInterfaceDescription;
                Core.AppLog.Info("选中接口: " + pick.strInterfaceDescription + "（状态=" + pick.isState + "）");
            }
            finally
            {
                WlanFreeMemory(listPtr);
            }
        }

        public ConnectionState GetConnection()
        {
            lock (_sync)
            {
                var result = new ConnectionState { InterfaceState = (int)WLAN_INTERFACE_STATE.wlan_interface_state_disconnected };
                if (_handle == IntPtr.Zero) return result;

                var guid = InterfaceGuid;
                uint size;
                IntPtr dataPtr;
                uint opcodeType;
                var rc = WlanQueryInterface(_handle, ref guid, WLAN_INTF_OPCODE_CURRENT_CONNECTION,
                    IntPtr.Zero, out size, out dataPtr, out opcodeType);

                if (rc != ERROR_SUCCESS)
                {
                    result.LastError = rc;
                    result.Connected = false;
                    // ERROR_INVALID_STATE = 当前没有连接，属于正常情况
                    return result;
                }

                try
                {
                    var attrs = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                    result.InterfaceState = (int)attrs.isState;
                    result.ProfileName = attrs.strProfileName;
                    result.Ssid = attrs.wlanAssociationAttributes.dot11Ssid.ToSsidString();
                    result.SignalQuality = (int)attrs.wlanAssociationAttributes.wlanSignalQuality;
                    result.Connected = attrs.isState == WLAN_INTERFACE_STATE.wlan_interface_state_connected
                                        && !string.IsNullOrEmpty(result.Ssid);
                    return result;
                }
                finally
                {
                    WlanFreeMemory(dataPtr);
                }
            }
        }

        /// <summary>
        /// 只发起一次扫描，不去读结果。扫描是异步的，结果要下一轮才能读到；
        /// 这样调用方可以先读上一轮结果、再请求下一轮，避免"刚发起扫描就立刻读"
        /// 读到被驱动清空/半更新的网络列表。
        /// </summary>
        public void RequestScan()
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero) return;
                var guid = InterfaceGuid;
                var rc = WlanScan(_handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc != ERROR_SUCCESS && rc != 1167 /*ERROR_BUSY*/)
                {
                    Core.AppLog.Warn("WlanScan rc=" + rc + "（" + WlanError.Describe(rc) + "）");
                }
            }
        }

        /// <summary>
        /// 扫描并读取可用网络列表。
        /// requestScan=true 时顺便发起一次新扫描（异步，结果下一轮才更新）。
        /// 读取失败时返回上一次成功结果的副本并标记 Quality=Failed，绝不用空表冒充"什么都没扫到"。
        /// </summary>
        public ScanResult Scan(bool requestScan)
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero)
                {
                    return new ScanResult { Networks = new List<AvailableNetwork>(), Quality = ScanQuality.Failed };
                }

                var guid = InterfaceGuid;
                if (requestScan)
                {
                    var scanRc = WlanScan(_handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (scanRc != ERROR_SUCCESS && scanRc != 1167 /*ERROR_BUSY*/)
                    {
                        Core.AppLog.Warn("WlanScan rc=" + scanRc + "（" + WlanError.Describe(scanRc) + "）");
                    }
                }

                IntPtr listPtr;
                var rc = WlanGetAvailableNetworkList(_handle, ref guid,
                    WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES, IntPtr.Zero, out listPtr);
                if (rc != ERROR_SUCCESS)
                {
                    LastScanError = rc;
                    Core.AppLog.Warn("WlanGetAvailableNetworkList rc=" + rc + "（" + WlanError.Describe(rc) + "）");
                    return new ScanResult
                    {
                        Networks = new List<AvailableNetwork>(_lastGoodNets),
                        Quality = ScanQuality.Failed
                    };
                }
                LastScanError = 0;

                try
                {
                    var header = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK_LIST>(listPtr);
                    var items = ReadArray<WLAN_AVAILABLE_NETWORK>(listPtr, header.dwNumberOfItems);
                    var map = new Dictionary<string, AvailableNetwork>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        var ssid = item.dot11Ssid.ToSsidString();
                        if (string.IsNullOrEmpty(ssid)) continue; // 隐藏网络或其他 BSS 的空 SSID

                        var net = new AvailableNetwork
                        {
                            Ssid = ssid,
                            SignalQuality = (int)item.wlanSignalQuality,
                            Connectable = item.bNetworkConnectable != 0,
                            HasProfile = !string.IsNullOrEmpty(item.strProfileName)
                        };

                        AvailableNetwork existing;
                        if (map.TryGetValue(ssid, out existing))
                        {
                            if (net.SignalQuality > existing.SignalQuality)
                            {
                                existing.SignalQuality = net.SignalQuality;
                                existing.Connectable = net.Connectable;
                                existing.HasProfile = existing.HasProfile || net.HasProfile;
                            }
                        }
                        else
                        {
                            map[ssid] = net;
                        }
                    }

                    var list = map.Values.OrderByDescending(n => n.SignalQuality).ToList();
                    _lastGoodNets = new List<AvailableNetwork>(list); // 缓存，供读取失败时兜底

                    // 扫描结果日志降噪（实测 93% 的日志是这条在刷屏）：数量与上一次相同就沉默，
                    // 变化时记一条；另外每小时强制记一次心跳，证明扫描还在工作
                    var now = Environment.TickCount64;
                    if (list.Count != _lastLoggedScanCount || now - _lastScanLogMs >= 3600000)
                    {
                        Core.AppLog.Info("扫描完成：原始条目 " + items.Length + " 个，去重后 " + list.Count
                                         + " 个可用网络" + (_lastLoggedScanCount >= 0 ? "（上一次 " + _lastLoggedScanCount + " 个）" : "") + "。");
                        _lastLoggedScanCount = list.Count;
                        _lastScanLogMs = now;
                    }
                    return new ScanResult { Networks = list, Quality = ScanQuality.Fresh };
                }
                finally
                {
                    WlanFreeMemory(listPtr);
                }
            }
        }

        /// <summary>枚举已保存的配置文件（SSID → profileName）。</summary>
        public Dictionary<string, string> GetProfiles()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                if (_handle == IntPtr.Zero) return result;

                var guid = InterfaceGuid;
                IntPtr listPtr;
                var rc = WlanGetProfileList(_handle, ref guid, IntPtr.Zero, out listPtr);
                if (rc != ERROR_SUCCESS)
                {
                    Core.AppLog.Warn("WlanGetProfileList rc=" + rc + "（" + WlanError.Describe(rc) + "）");
                    return result;
                }

                try
                {
                    var header = Marshal.PtrToStructure<WLAN_PROFILE_INFO_LIST>(listPtr);
                    var infos = ReadArray<WLAN_PROFILE_INFO>(listPtr, header.dwNumberOfItems);
                    Core.AppLog.Info("已保存配置文件 " + infos.Length + " 个（接口 " + InterfaceDescription + "）。");
                    foreach (var info in infos)
                    {
                        var ssid = GetProfileSsid(info.strProfileName);
                        if (!string.IsNullOrEmpty(ssid) && !result.ContainsKey(ssid))
                        {
                            result[ssid] = info.strProfileName;
                        }
                    }
                }
                finally
                {
                    WlanFreeMemory(listPtr);
                }
            }
            return result;
        }

        private string GetProfileSsid(string profileName)
        {
            var guid = InterfaceGuid;
            uint flags = 0;
            uint access;
            IntPtr xmlPtr;
            var rc = WlanGetProfile(_handle, ref guid, profileName, IntPtr.Zero, out xmlPtr, ref flags, out access);
            if (rc != ERROR_SUCCESS || xmlPtr == IntPtr.Zero)
            {
                Core.AppLog.Warn("WlanGetProfile(\"" + profileName + "\") rc=" + rc
                                 + "（" + WlanError.Describe(rc) + "），跳过该配置文件。");
                return null;
            }

            try
            {
                var xml = System.Runtime.InteropServices.Marshal.PtrToStringUni(xmlPtr);
                if (string.IsNullOrEmpty(xml)) return null;
                var doc = XDocument.Parse(xml);
                var ssidNode = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "SSID");
                if (ssidNode == null) return null;
                var nameNode = ssidNode.Elements().FirstOrDefault(e => e.Name.LocalName == "name");
                return nameNode == null ? null : nameNode.Value;
            }
            catch (Exception ex)
            {
                Core.AppLog.Warn("解析配置文件 \"" + profileName + "\" 的 SSID 失败：" + ex.Message);
                return null;
            }
            finally
            {
                WlanFreeMemory(xmlPtr);
            }
        }

        /// <summary>连接指定 SSID（要求该 SSID 已有保存的配置文件）。</summary>
        public bool Connect(string ssid, out uint errorCode, out string errorText)
        {
            errorCode = 0;
            errorText = null;

            string profileName;
            if (TryResolveProfile(ssid, out profileName) == false)
            {
                errorText = "找不到 SSID 为 \"" + ssid + "\" 的已保存配置文件，请先手动连接一次并勾选自动连接。";
                return false;
            }

            lock (_sync)
            {
                if (_handle == IntPtr.Zero)
                {
                    errorText = "WLAN 句柄未打开。";
                    return false;
                }

                var guid = InterfaceGuid;
                var parms = new WLAN_CONNECTION_PARAMETERS
                {
                    wlanConnectionMode = WLAN_CONNECTION_MODE.wlan_connection_mode_profile,
                    strProfile = profileName,
                    pDot11Ssid = IntPtr.Zero,
                    pDesiredBssidList = IntPtr.Zero,
                    dot11BssType = DOT11_BSS_TYPE.dot11_BSS_type_any,
                    dwFlags = 0
                };

                var rc = WlanConnect(_handle, ref guid, ref parms, IntPtr.Zero);
                errorCode = rc;
                if (rc != ERROR_SUCCESS)
                {
                    errorText = WlanError.Describe(rc);
                    Core.AppLog.Warn("WlanConnect(\"" + ssid + "\"/" + profileName + ") rc=" + rc + "（" + errorText + "）");
                    return false;
                }
            }

            Core.AppLog.Info("已发起连接: " + ssid + "（配置文件 " + profileName + "）");
            return true;
        }

        public bool TryResolveProfile(string ssid, out string profileName)
        {
            profileName = null;
            if (string.IsNullOrWhiteSpace(ssid)) return false;
            var profiles = GetProfiles();
            if (profiles.TryGetValue(ssid.Trim(), out profileName)) return true;

            // 兜底：某些网卡的 profile 名与 SSID 不完全一致，但通常一致；再按同名尝试一次
            var conn = GetConnection();
            if (conn.Connected && string.Equals(conn.Ssid, ssid.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                profileName = conn.ProfileName;
                return !string.IsNullOrEmpty(profileName);
            }
            return false;
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero) return;
                var guid = InterfaceGuid;
                WlanDisconnect(_handle, ref guid, IntPtr.Zero);
            }
        }

        /// <summary>等待接口连上指定 SSID，成功返回 true。</summary>
        public bool WaitForConnection(string ssid, int timeoutMs, CancellationToken token = default(CancellationToken))
        {
            // TickCount64：单调时钟且无 int 回绕问题（int 版约 24.9 天回绕一次）
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (token.IsCancellationRequested) return false;
                var conn = GetConnection();
                if (conn.Connected && string.Equals(conn.Ssid, ssid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                Thread.Sleep(600);
            }
            return false;
        }

        private void OnNotification(ref WLAN_NOTIFICATION_DATA data, IntPtr context)
        {
            var handler = Notified;
            if (handler == null) return;
            try { handler(); } catch { }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_handle != IntPtr.Zero)
                {
                    try
                    {
                        uint prev;
                        WlanRegisterNotification(_handle, 0, true, null, IntPtr.Zero, IntPtr.Zero, out prev);
                    }
                    catch { }
                    try { WlanCloseHandle(_handle, IntPtr.Zero); } catch { }
                    _handle = IntPtr.Zero;
                }
            }
        }
    }

    public sealed class WlanException : Exception
    {
        public uint Code { get; private set; }
        public WlanException(string message, uint code) : base(message) { Code = code; }
    }

    internal static class WlanError
    {
        public static string Describe(uint code)
        {
            switch (code)
            {
                case 0: return "成功";
                case 5: return "拒绝访问（请以管理员身份运行）";
                case 87: return "参数无效";
                case 1167: return "设备忙";
                case 1168: return "找不到元素";
                case 5023: return "当前状态无效（未连接）";
                case 1062: return "服务未启动（WLAN AutoConfig 是否运行？）";
                default: return "错误码 " + code;
            }
        }
    }
}
