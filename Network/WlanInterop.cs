using System;
using System.Runtime.InteropServices;

namespace Suzu.Network
{
    /// <summary>
    /// wlanapi.dll 的原生声明与结构体。字段顺序/尺寸必须与 wlanapi.h 严格一致。
    /// 注意：所有含 WCHAR 数组的结构体都必须显式声明 CharSet = Unicode，
    /// 否则 ByValTStr 会按 ANSI 计算尺寸（字符数≠字节数），指针数组偏移就会全错。
    /// 参考：https://learn.microsoft.com/windows/win32/api/wlanapi/
    /// </summary>
    internal static class NativeMethods
    {
        public const int DOT11_SSID_MAX_LENGTH = 32;
        public const int WLAN_MAX_NAME_LENGTH = 256;   // WCHAR 个数
        public const int WLAN_MAX_PHY_TYPE_NUMBER = 8;

        public const uint WLAN_CLIENT_VERSION_VISTA = 2;
        public const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES = 0x00000002;
        public const uint WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;
        public const uint WLAN_NOTIFICATION_SOURCE_ALL = 0x0000FFFF;

        public const uint ERROR_SUCCESS = 0;
        public const uint ERROR_ACCESS_DENIED = 5;
        public const uint ERROR_BUSY = 1167;
        public const uint ERROR_INVALID_STATE = 5023;
        public const uint ERROR_NOT_FOUND = 1168;

        public enum WLAN_INTERFACE_STATE : uint
        {
            wlan_interface_state_not_ready = 0,
            wlan_interface_state_connected = 1,
            wlan_interface_state_ad_hoc_network_formed = 2,
            wlan_interface_state_disconnecting = 3,
            wlan_interface_state_disconnected = 4,
            wlan_interface_state_authenticating = 5
        }

        public enum WLAN_CONNECTION_MODE : uint
        {
            wlan_connection_mode_profile = 0,
            wlan_connection_mode_temporary_profile = 1,
            wlan_connection_mode_discovery_secure = 2,
            wlan_connection_mode_discovery_unsecure = 3,
            wlan_connection_mode_auto = 4,
            wlan_connection_mode_invalid = 5
        }

        public enum DOT11_BSS_TYPE : uint
        {
            dot11_BSS_type_infrastructure = 1,
            dot11_BSS_type_independent = 2,
            dot11_BSS_type_any = 3
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = DOT11_SSID_MAX_LENGTH)]
            public byte[] ucSSID;

            public string ToSsidString()
            {
                if (ucSSID == null) return string.Empty;
                var len = (int)Math.Min(uSSIDLength, (uint)ucSSID.Length);
                if (len <= 0) return string.Empty;
                return System.Text.Encoding.UTF8.GetString(ucSSID, 0, len);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WLAN_MAX_NAME_LENGTH)]
            public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
            // 紧跟 WLAN_INTERFACE_INFO[dwNumberOfItems]
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WLAN_MAX_NAME_LENGTH)]
            public string strProfileName;
            public DOT11_SSID dot11Ssid;
            public DOT11_BSS_TYPE dot11BssType;
            public uint uNumberOfBssids;
            public int bNetworkConnectable;
            public uint wlanNotConnectableReason;
            public uint uNumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = WLAN_MAX_PHY_TYPE_NUMBER)]
            public uint[] dot11PhyTypes;
            public int bMorePhyTypes;
            public uint wlanSignalQuality;
            public int bSecurityEnabled;
            public uint dot11DefaultAuthAlgorithm;
            public uint dot11DefaultCipherAlgorithm;
            public uint dwFlags;
            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_AVAILABLE_NETWORK_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
            // 紧跟 WLAN_AVAILABLE_NETWORK[dwNumberOfItems]
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public DOT11_BSS_TYPE dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public uint dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_SECURITY_ATTRIBUTES
        {
            public int bSecurityEnabled;
            public int bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public WLAN_CONNECTION_MODE wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WLAN_MAX_NAME_LENGTH)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_PARAMETERS
        {
            public WLAN_CONNECTION_MODE wlanConnectionMode;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public DOT11_BSS_TYPE dot11BssType;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_PROFILE_INFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WLAN_MAX_NAME_LENGTH)]
            public string strProfileName;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_PROFILE_INFO_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_NOTIFICATION_DATA
        {
            public uint NotificationSource;
            public uint NotificationCode;
            public Guid InterfaceGuid;
            public uint dwDataSize;
            public IntPtr pData;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void WlanNotificationCallback(ref WLAN_NOTIFICATION_DATA data, IntPtr context);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
            out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint opCode,
            IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, out uint pWlanOpcodeValueType);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid,
            IntPtr pIeData, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            IntPtr pReserved, out IntPtr ppProfileList);

        [DllImport("wlanapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint WlanGetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            string strProfileName, IntPtr pReserved, out IntPtr pstrProfileXml,
            ref uint pdwFlags, out uint pdwGrantedAccess);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanRegisterNotification(IntPtr hClientHandle, uint dwNotifSource,
            bool bIgnoreDuplicate, WlanNotificationCallback funcCallback, IntPtr pCallbackContext,
            IntPtr pReserved, out uint pdwPrevNotifSource);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr pMemory);

        /// <summary>
        /// 变长结构数组读取：头部是 (dwNumberOfItems, dwIndex) 两个 DWORD，
        /// 之后紧跟 count 个定长结构体。不能直接用 PtrToStructure 拷整个 list。
        /// </summary>
        public static T[] ReadArray<T>(IntPtr listPtr, uint count) where T : struct
        {
            var result = new T[count];
            if (listPtr == IntPtr.Zero || count == 0) return result;
            int itemSize = Marshal.SizeOf(typeof(T));
            IntPtr basePtr = IntPtr.Add(listPtr, 8);
            for (int i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStructure<T>(IntPtr.Add(basePtr, i * itemSize));
            }
            return result;
        }
    }
}
