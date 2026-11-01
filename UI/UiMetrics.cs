using System;
using System.Drawing;
using System.Windows.Forms;

namespace Suzu.UI
{
    /// <summary>
    /// 控件尺寸小工具：按实测文字宽度算按钮/标签尺寸，
    /// 这样在 125%/150% 缩放下不会出现"按钮字被裁"。
    /// </summary>
    public static class UiMetrics
    {
        /// <summary>按文字实测宽度取按钮宽度（含左右内边距，并保证不小于 minWidth）。</summary>
        public static int ButtonWidth(string text, Font font, int minWidth)
        {
            int measured = TextRenderer.MeasureText(text ?? "", font ?? SystemFonts.DefaultFont).Width;
            return Math.Max(minWidth, measured + 22);
        }

        /// <summary>按文字实测高度取标签高度（行数 × 行高 + 余量）。</summary>
        public static int LabelHeight(Font font, int lines)
        {
            var f = font ?? SystemFonts.DefaultFont;
            return f.Height * Math.Max(1, lines) + 6;
        }
    }
}
