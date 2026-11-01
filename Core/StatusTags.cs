using System;

namespace Suzu.Core
{
    /// <summary>
    /// 状态文本标签：WinForms 的 GDI 渲染不支持彩色 emoji（🟢🟡 会退化成单色小轮廓），
    /// 状态文本里的 emoji 统一在这里换成【文字标签】。
    /// 颜色映射在 UI 层（按标签给状态行着色），Core 不引用 System.Drawing。
    /// </summary>
    public static class StatusTags
    {
        public const string Normal = "【正常】";       // 原 🟢：网关通且外网通
        public const string Limited = "【受限】";      // 原 🟡：网关通但无 Internet（认证/VPN 异常）
        public const string Disconnecting = "【断开】"; // 原 🔴：链路断了，切换进行中
        public const string Fault = "【故障】";        // 原 ❌：TOTAL_FAILURE
        public const string Attention = "【注意】";    // 原 ⚠️：有线插着但到不了外网
        public const string Hold = "【挂起】";         // 原 🔌：有线可用，WiFi 切换挂起待命

        public static string ToTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("🟢", Normal)
                .Replace("🟡", Limited)
                .Replace("🔴", Disconnecting)
                .Replace("❌", Fault)
                .Replace("⚠️", Attention)
                .Replace("⚠", Attention)
                .Replace("🔌", Hold);
        }
    }
}
