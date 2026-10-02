using System;
using System.IO;
using System.Linq;
using ImageMagick;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 相机 RAW 照片加载（CR2/CR3/NEF/ARW/DNG/RAF/ORF/RW2/PEF/SRW 等）。
    /// 通过 ImageMagick 内置的 libraw 解码，统一输出 8 位 BGR 图（Q8 构建），
    /// 与现有 8 位图像处理流水线兼容。RAW 文件自描述（宽高/位深/色彩），无需参数对话框。
    /// </summary>
    public static class RawCameraLoader
    {
        /// <summary>支持的相机 RAW 扩展名（小写，不含点）</summary>
        public static readonly string[] Extensions =
        [
            "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "srw",
            "dng", "raf", "orf", "rw2", "pef", "x3f", "3fr", "kdc", "dcr",
            "mrw", "erf", "iiq"
        ];

        /// <summary>WinForms 打开对话框用的过滤器值（如 *.cr2;*.cr3;...）</summary>
        public static string FilterString =>
            "*." + string.Join(";*.", Extensions);

        public static bool IsCameraRaw(string path) =>
            !string.IsNullOrEmpty(path)
            && Extensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

        /// <summary>解码相机 RAW 为 Mat（8UC3 BGR）。失败返回 false 并给出原因。</summary>
        public static bool TryLoad(string path, out Mat mat, out string error)
        {
            mat = null;
            error = null;
            try
            {
                using MagickImage img = new(path);
                if (img.Width <= 0 || img.Height <= 0)
                {
                    error = "RAW 解码结果尺寸无效";
                    return false;
                }

                // 统一 8 位，经 PNG 无损中转后由 OpenCV 解码为 BGR 3 通道
                // （规避各 RAW 源色彩类型/通道数差异，保证下游 8 位兼容）
                img.Depth = 8;
                img.Format = MagickFormat.Png;
                byte[] png = img.ToByteArray();
                if (png == null || png.Length == 0)
                {
                    error = "RAW 像素转换失败";
                    return false;
                }

                mat = Cv2.ImDecode(png, ImreadModes.Color);
                if (mat.Empty())
                {
                    mat.Dispose();
                    mat = null;
                    error = "RAW 像素转换结果为空";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
