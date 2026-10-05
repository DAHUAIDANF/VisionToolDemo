using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenCvSharp;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// OpenCvSharp 的 Mat → Avalonia 可显示的位图（WriteableBitmap）。
    ///
    /// 性能上有三个关键点（界面卡顿主要就出在这类地方）：
    ///   1. **大图降采样**：1920x1080 转成 BGRA 要 8MB，4K 要 33MB。屏幕上反正显示不了那么多像素，
    ///      预览按"最多 ~240 万像素"缩一下，内存与耗时都降一个量级；
    ///   2. **同一个 Mat 不重复转**：选中切换、切页签会反复请求同一张图，这里做一层缓存；
    ///   3. **相机预览零分配**：每帧都 new byte[] + 新建位图（8MB/帧 × 12fps ≈ 96MB/s 垃圾）
    ///      会让 GC 频繁触发，表现就是"视频/预览一顿一顿"。这里提供 WriteIntoBitmap，
    ///      复用同一块缓冲与同一个 WriteableBitmap。
    ///
    /// 像素一律**拷贝后冻结**：直接引用 Mat 内存在 Mat 被释放后会出现黑块或崩。
    /// 【Avalonia 11 迁移】无 BitmapSource.Create/Freeze/WritePixels：
    ///   显示位图 = WriteableBitmap（实现 IImage，可作 Image.Source）；
    ///   写像素 = Lock() → ILockedFramebuffer.Address 上 Marshal.Copy → Dispose 提交。
    /// </summary>
    public static class MatImage
    {
        /// <summary>预览默认的像素上限（约 240 万像素，1600x1500 左右）；传 0 表示不缩</summary>
        public const int DefaultMaxPixels = 2_400_000;

        private static Mat _cacheKey;
        private static WriteableBitmap _cacheBmp;
        private static int _cacheMaxPixels;
        private static double _cacheScale = 1.0;   // 显示位图 → 原图 的倍率（降采样后 > 1）

        /// <summary>清掉转换缓存（释放大图、关闭页面时调用，避免缓存把大 Mat 一直留着）</summary>
        public static void ResetCache()
        {
            _cacheKey = null;
            _cacheBmp = null;
        }

        /// <summary>按像素尺寸新建 Bgra8888 可写位图（相机预览等复用位图场景用；96 DPI 逻辑像素）</summary>
        public static WriteableBitmap CreateWriteableBitmap(int width, int height)
            => new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        public static WriteableBitmap ToBitmapSource(Mat mat, int maxPixels = DefaultMaxPixels)
            => ToBitmapSource(mat, out _, maxPixels);

        /// <summary>
        /// 显示用位图。toOriginalScale 是"显示位图坐标 → 原图坐标"的倍率：
        /// 预览会降采样（大图缩到 ~240 万像素），**凡是要把鼠标位置换算成原图像素的地方
        /// （像素值显示、ROI 框选、从选区裁模板）都必须乘上这个倍率**，
        /// 否则就是"框选的位置和鼠标点对不上"。
        /// </summary>
        public static WriteableBitmap ToBitmapSource(Mat mat, out double toOriginalScale, int maxPixels = DefaultMaxPixels)
        {
            toOriginalScale = 1.0;
            if (mat == null || mat.Empty()) { ResetCache(); return null; }

            // 同一个 Mat 反复显示（选中切换/切页签/重复刷新）直接用上次的结果
            if (_cacheBmp != null && ReferenceEquals(_cacheKey, mat) && _cacheMaxPixels == maxPixels)
            {
                toOriginalScale = _cacheScale;
                return _cacheBmp;
            }

            WriteableBitmap bmp;
            double displayScale = 1.0;
            try { bmp = Convert(mat, maxPixels, out displayScale); }
            catch (ObjectDisposedException) { return null; }   // 已释放的图：显示空白总比崩好

            toOriginalScale = displayScale > 0 ? 1.0 / displayScale : 1.0;
            _cacheKey = mat;
            _cacheBmp = bmp;
            _cacheMaxPixels = maxPixels;
            _cacheScale = toOriginalScale;
            return bmp;
        }

        private static WriteableBitmap Convert(Mat mat, int maxPixels, out double displayScale)
        {
            int w = mat.Cols, h = mat.Rows;
            double scale = 1.0;
            long px = (long)w * h;
            if (maxPixels > 0 && px > maxPixels) scale = Math.Sqrt(maxPixels / (double)px);
            displayScale = scale;

            Mat work = null;              // 需要时自己持有（缩放过或从灰度/彩色转过）
            Mat bgra = null;              // 最终 BGRA 图（可能是 mat 本身）
            try
            {
                if (scale < 1.0)
                {
                    int nw = Math.Max(1, (int)Math.Round(w * scale));
                    int nh = Math.Max(1, (int)Math.Round(h * scale));
                    work = new Mat();
                    Cv2.Resize(mat, work, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Area);
                    bgra = ToBgra(work);
                }
                else bgra = ToBgra(mat);

                int bw = bgra.Cols, bh = bgra.Rows;
                var bmp = new WriteableBitmap(
                    new PixelSize(bw, bh),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                CopyBgraTo(bgra, bmp);
                return bmp;
            }
            finally
            {
                if (bgra != null && !ReferenceEquals(bgra, mat)) bgra.Dispose();
                work?.Dispose();
            }
        }

        /// <summary>把 BGRA Mat 拷进 WriteableBitmap（Lock → Marshal.Copy → 提交）</summary>
        private static void CopyBgraTo(Mat bgra, WriteableBitmap bmp)
        {
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            int stride = w * 4;
            using (var fb = bmp.Lock())
            {
                var dst = fb.Address;
                // 统一逐行拷：Mat 行间可能有 padding，整块拷会错位；逐行保证正确
                var row = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(bgra.Data + (y * (int)bgra.Step()), row, 0, stride);
                    Marshal.Copy(row, 0, dst + y * stride, stride);
                }
            }
        }

        /// <summary>转成 4 通道 BGRA（返回的对象可能是传入的 mat 本身，调用方按引用判断是否需要释放）</summary>
        private static Mat ToBgra(Mat mat)
        {
            if (mat.Channels() == 4) return mat;
            var bgra = new Mat();
            if (mat.Channels() == 3) Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
            else if (mat.Channels() == 1) Cv2.CvtColor(mat, bgra, ColorConversionCodes.GRAY2BGRA);
            else
            {
                bgra.Dispose();
                throw new InvalidOperationException("这张图的通道数不支持显示：" + mat.Channels());
            }
            return bgra;
        }

        /// <summary>
        /// 生成缩略图（最长边约 maxSide 像素）。
        /// 刻意**不走上面那个单槽缓存**：模板预览与主图像显示会互相挤掉缓存，
        /// 反而让主图每次都要重新转换。缩略图本身很小，随手转一份即可。
        /// </summary>
        public static WriteableBitmap ToThumbnail(Mat mat, int maxSide = 160)
        {
            if (mat == null || mat.Empty()) return null;
            int side = Math.Max(48, maxSide);
            try { return Convert(mat, side * side, out _); }
            catch (ObjectDisposedException) { return null; }
            catch { return null; }
        }

        /// <summary>
        /// 相机/视频预览专用：把 mat 写进同一个 WriteableBitmap，**不新建位图、不新分配缓冲**
        /// （scratch 由调用方持有，尺寸一致时会被复用）。
        /// 返回 false 表示这张图不适合预览（通道数不对）。
        /// </summary>
        public static bool WriteIntoBitmap(WriteableBitmap target, Mat mat, ref byte[] scratch)
        {
            if (target == null || mat == null || mat.Empty()) return false;
            if (mat.Cols != target.PixelSize.Width || mat.Rows != target.PixelSize.Height) return false;

            Mat bgra = null;
            try
            {
                if (mat.Channels() == 4) bgra = mat;
                else
                {
                    bgra = new Mat();
                    if (mat.Channels() == 3) Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
                    else if (mat.Channels() == 1) Cv2.CvtColor(mat, bgra, ColorConversionCodes.GRAY2BGRA);
                    else return false;
                }

                int w = target.PixelSize.Width, h = target.PixelSize.Height;
                int stride = w * 4;
                int need = stride * h;
                if (scratch == null || scratch.Length != need) scratch = new byte[need];

                if (bgra.IsContinuous()) Marshal.Copy(bgra.Data, scratch, 0, need);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(bgra.Data + (y * (int)bgra.Step()), scratch, y * stride, stride);

                using (var fb = target.Lock())
                {
                    Marshal.Copy(scratch, 0, fb.Address, need);
                }
                return true;
            }
            finally
            {
                if (bgra != null && !ReferenceEquals(bgra, mat)) bgra.Dispose();
            }
        }
    }
}
