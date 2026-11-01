using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;

namespace Suzu.Network
{
    /// <summary>
    /// 系统/网卡快照（供诊断包使用）。放在 Network 层，因为这里要用 Windows 专属 API；
    /// Core 的 DiagnosticBundle 只接收字符串行，保持零 Windows 依赖。
    /// 注意：只输出网卡名称/类型/状态/网关，**不输出 SSID、不输出任何密码**。
    /// </summary>
    public static class SystemSnapshot
    {
        public static List<string> BuildLines()
        {
            var lines = new List<string>();

            try
            {
                lines.Add(string.Format("操作系统: {0}（{1}）",
                    Environment.OSVersion.VersionString,
                    Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
                lines.Add("运行时: .NET " + Environment.Version);
                lines.Add("进程位数: " + (Environment.Is64BitProcess ? "64 位" : "32 位"));
                lines.Add("管理员运行: " + (IsElevated() ? "是" : "否"));
                lines.Add("机器名: " + Environment.MachineName);
                lines.Add("数据目录: " + Core.AppPaths.DataDirectory);
                lines.Add("");
                lines.Add("---- 网卡 ----");
            }
            catch (Exception ex)
            {
                lines.Add("系统信息收集失败: " + ex.Message);
            }

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var sb = new StringBuilder();
                    sb.Append(ni.Name);
                    sb.Append(" | 类型=").Append(ni.NetworkInterfaceType);
                    sb.Append(" | 状态=").Append(ni.OperationalStatus);
                    sb.Append(" | 速率=").Append(ni.Speed > 0 ? (ni.Speed / 1000000) + "Mbps" : "未知");

                    try
                    {
                        var props = ni.GetIPProperties();
                        var addresses = new List<string>();
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address != null && ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                addresses.Add(ua.Address.ToString());
                            }
                        }
                        if (addresses.Count > 0) sb.Append(" | IPv4=").Append(string.Join(",", addresses.ToArray()));

                        var gateways = new List<string>();
                        foreach (var gw in props.GatewayAddresses)
                        {
                            if (gw.Address != null && !gw.Address.Equals(System.Net.IPAddress.Any))
                            {
                                gateways.Add(gw.Address.ToString());
                            }
                        }
                        if (gateways.Count > 0) sb.Append(" | 网关=").Append(string.Join(",", gateways.ToArray()));
                    }
                    catch
                    {
                        sb.Append(" | 地址信息读取失败");
                    }

                    lines.Add(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                lines.Add("网卡枚举失败: " + ex.Message);
            }

            return lines;
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
