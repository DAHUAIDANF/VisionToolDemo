using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 在结果图上绘制文字的工具，**支持中文**。
    ///
    /// 背景：OpenCv 的 Cv2.PutText 只有 Hershey 字体，画不了中文（中文变成 ? 或乱码）。
    /// 这里用 GDI+（System.Drawing，本项目已依赖）把文字画到透明位图，再按 alpha
    /// 混合回 Mat 的对应区域 —— 只遍历文字实际占用的矩形，大图上不慢。
    ///
    /// 用法与 Cv2.PutText 对齐：就地修改 dst；x/y 是文字左上角；color 是 BGR（OpenCv 习惯）。
    /// </summary>
    public static class MatDraw
    {
        /// <summary>
        /// 在图上画文字（支持中文与任意 Unicode）。dst 就地修改；绘制失败不影响主流程。
        /// </summary>
        /// <param name="dst">目标图（3 通道 BGR 或 1 通道灰度）</param>
        /// <param name="text">要画的文字（空串直接返回）</param>
        /// <param name="x">左上角 X</param>
        /// <param name="y">左上角 Y</param>
        /// <param name="bgr">OpenCv 颜色（B,G,R）</param>
        /// <param name="fontPx">字号（像素，默认 14）</param>
        public static void DrawText(Mat dst, string text, int x, int y, Scalar bgr, int fontPx = 14)
        {
            if (dst == null || dst.Empty() || string.IsNullOrEmpty(text)) return;
            try
            {
                using var bmp = new Bitmap(dst.Cols, dst.Rows, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float px = Math.Max(8, fontPx);
                    using var font = new Font("Microsoft YaHei UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(Color.FromArgb(
                        (int)Math.Clamp(bgr.Val2, 0, 255),      // R
                        (int)Math.Clamp(bgr.Val1, 0, 255),      // G
                        (int)Math.Clamp(bgr.Val0, 0, 255)));    // B
                    g.DrawString(text, font, brush, x, y);
                }

                // 只拷贝文字区域（多留 2px 边），避免逐像素扫整张图
                using var measure = Graphics.FromImage(bmp);
                using var mFont = new Font("Microsoft YaHei UI", Math.Max(8, fontPx), FontStyle.Regular, GraphicsUnit.Pixel);
                var sz = measure.MeasureString(text, mFont);
                int x0 = Math.Max(0, x - 2), y0 = Math.Max(0, y - 2);
                int x1 = Math.Min(dst.Cols, (int)Math.Ceiling(x + sz.Width) + 2);
                int y1 = Math.Min(dst.Rows, (int)Math.Ceiling(y + sz.Height) + 2);
                if (x1 <= x0 || y1 <= y0) return;

                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)data.Scan0;
                        if (dst.Channels() == 3)
                        {
                            for (int row = y0; row < y1; row++)
                            {
                                byte* srow = src + row * data.Stride;
                                for (int col = x0; col < x1; col++)
                                {
                                    int alpha = srow[col * 4 + 3];
                                    if (alpha == 0) continue;
                                    double a = alpha / 255.0, inv = 1 - a;
                                    var p = dst.At<Vec3b>(row, col);
                                    p.Item0 = (byte)Math.Round(p.Item0 * inv + srow[col * 4 + 0] * a);
                                    p.Item1 = (byte)Math.Round(p.Item1 * inv + srow[col * 4 + 1] * a);
                                    p.Item2 = (byte)Math.Round(p.Item2 * inv + srow[col * 4 + 2] * a);
                                    dst.Set(row, col, p);
                                }
                            }
                        }
                        else if (dst.Channels() == 1)
                        {
                            for (int row = y0; row < y1; row++)
                            {
                                byte* srow = src + row * data.Stride;
                                for (int col = x0; col < x1; col++)
                                {
                                    int alpha = srow[col * 4 + 3];
                                    if (alpha == 0) continue;
                                    double a = alpha / 255.0, inv = 1 - a;
                                    byte gray = (byte)Math.Round(
                                        srow[col * 4 + 0] * 0.114 + srow[col * 4 + 1] * 0.587 + srow[col * 4 + 2] * 0.299);
                                    var p = dst.At<byte>(row, col);
                                    dst.Set(row, col, (byte)Math.Round(p * inv + gray * a));
                                }
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            catch
            {
                // 绘制失败不能影响识别/检测主流程
            }
        }
    }
}
