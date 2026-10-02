using System;
using System.IO;
using System.Linq;
using OpenCvSharp;
using VisionToolDemo.Vision;
using Xunit;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// 人脸辅助增强（FaceDetector）单元测试：
    /// 覆盖 Haar 级联模型路径解析、空/非法输入防御、真实人脸检出、无人脸图返回空、
    /// 以及「取最大脸」排序逻辑 —— 训练页「人脸辅助/检测预填」与目标跟踪「人脸追踪」
    /// 都复用它，防止回归。
    /// </summary>
    public class FaceDetectorTests
    {
        /// <summary>测试输出目录里的 Haar 模型（csproj 已配置 PreserveNewest 拷贝）</summary>
        private static string ModelPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "haarcascade_frontalface_default.xml"));

        private static string LenaPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lena.jpg"));

        // ---------- 模型路径解析 ----------

        [Fact]
        public void ResolveModelPath_显式路径存在_返回该路径()
        {
            Assert.True(File.Exists(ModelPath), "测试输出目录应包含模型文件（csproj 已配置拷贝）");
            Assert.Equal(ModelPath, FaceDetector.ResolveModelPath(ModelPath));
        }

        [Fact]
        public void ResolveModelPath_显式路径不存在_回退默认查找()
        {
            // 显式路径传错时设计上回退自动查找（输出目录有模型 → 命中），不抛异常
            var p = FaceDetector.ResolveModelPath(@"Z:\不存在\haarcascade_frontalface_default.xml");
            Assert.NotNull(p);
            Assert.True(File.Exists(p), "回退结果必须是真实存在的模型文件");
        }

        [Fact]
        public void ResolveModelPath_空字符串_回退自动查找()
        {
            // 输出目录里有模型 → 自动查找应命中（与 Available 一致）
            var p = FaceDetector.ResolveModelPath(null);
            if (File.Exists(ModelPath))
                Assert.NotNull(p);
        }

        // ---------- 空/非法输入防御（训练页人脸辅助绝不因异常崩溃） ----------

        [Fact]
        public void Detect_空Mat_返回空数组不崩溃()
        {
            using var empty = new Mat();
            var r = FaceDetector.Detect(empty, ModelPath);
            Assert.Empty(r);
        }

        [Fact]
        public void Detect_nullMat_返回空数组不崩溃()
        {
            var r = FaceDetector.Detect(null, ModelPath);
            Assert.Empty(r);
        }

        [Fact]
        public void Detect_模型路径不存在_返回空数组不崩溃()
        {
            using var m = new Mat(120, 120, MatType.CV_8UC3, new Scalar(80, 80, 80));
            var r = FaceDetector.Detect(m, @"Z:\不存在\xx.xml");
            Assert.Empty(r);
        }

        // ---------- 真实检测 ----------

        [Fact]
        public void Detect_真人脸图_检出至少一个框且在图像内()
        {
            Assert.True(File.Exists(LenaPath), "测试资源 lena.jpg 应存在（csproj 已配置拷贝）");
            using var bgr = Cv2.ImRead(LenaPath, ImreadModes.Color);
            Assert.False(bgr.Empty(), "lena.jpg 应能被 OpenCvSharp 解码");
            var faces = FaceDetector.Detect(bgr, ModelPath);
            Assert.NotEmpty(faces);
            foreach (var f in faces)
            {
                Assert.InRange(f.X, 0, bgr.Cols - 1);
                Assert.InRange(f.Y, 0, bgr.Rows - 1);
                Assert.True(f.Width >= 16 && f.Height >= 16, "过小框应被过滤");
                Assert.True(f.X + f.Width <= bgr.Cols, "框不得超出图像右边界");
                Assert.True(f.Y + f.Height <= bgr.Rows, "框不得超出图像下边界");
            }
        }

        [Fact]
        public void Detect_无人脸图_返回空数组()
        {
            using var m = new Mat(200, 200, MatType.CV_8UC3, new Scalar(80, 80, 80));
            Cv2.Rectangle(m, new OpenCvSharp.Point(50, 50), new OpenCvSharp.Point(150, 150),
                new Scalar(255, 255, 255), -1);
            var faces = FaceDetector.Detect(m, ModelPath);
            Assert.Empty(faces);
        }

        // ---------- 取最大脸（最近/最清晰目标） ----------

        [Fact]
        public void Biggest_返回面积最大的框()
        {
            var faces = new[]
            {
                new OpenCvSharp.Rect(0, 0, 20, 20),   // 400
                new OpenCvSharp.Rect(0, 0, 50, 40),   // 2000 ← 最大
                new OpenCvSharp.Rect(0, 0, 30, 30),   // 900
            };
            var b = FaceDetector.Biggest(faces);
            Assert.Equal(50, b.Width);
            Assert.Equal(40, b.Height);
        }

        [Fact]
        public void Biggest_空输入_返回default()
        {
            Assert.Equal(default, FaceDetector.Biggest(Array.Empty<OpenCvSharp.Rect>()));
            Assert.Equal(default, FaceDetector.Biggest(null));
        }

        [Fact]
        public void Biggest_面积相等时取首个()
        {
            var faces = new[]
            {
                new OpenCvSharp.Rect(1, 2, 30, 30),
                new OpenCvSharp.Rect(9, 9, 30, 30),
            };
            var b = FaceDetector.Biggest(faces);
            Assert.Equal(1, b.X);
            Assert.Equal(2, b.Y);
        }
    }
}
