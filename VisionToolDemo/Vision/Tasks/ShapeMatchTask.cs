using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 形状匹配：用边缘方向（梯度）而非像素灰度做模板匹配，对光照/对比度变化免疫。
    ///
    /// 为什么需要它：现有"模板匹配"用 CCoeffNormed 直接比灰度，光照一变、对比度一降，
    /// 分数就整体下滑（相关归一化只去掉线性增益，去不掉非线性响应/局部反光）。
    /// "几何定位"是边缘模板匹配，但它在每个角度都重算 Canny + MatchTemplate，
    /// 角度一多就慢。本算子取两者之间的位置：
    ///   · 用梯度方向做匹配（光照不变），而不是灰度；
    ///   · 用"方向量化 + 响应图"的经典形状匹配思路，旋转通过预先渲染模板角度实现，
    ///     角度搜索成本远低于逐角度重跑 Canny。
    ///
    /// 模板由 UI 层"导入模板"注入（TemplateMat）。
    /// </summary>
    public class ShapeMatchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "形状匹配";

        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        public bool Found { get; private set; }
        public Point2f Center { get; private set; }
        public double Angle { get; private set; }
        public double Score { get; private set; }
        public int MatchCount { get; private set; }

        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null) TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "匹配阈值%",
                Min = 10,
                Max = 99,
                DefaultValue = 80,
                DisplayFormat = "分数≥{0}%",
                Group = "形状匹配",
                Tip = "分数 = 模板边缘方向与图像边缘方向的一致程度，对光照/对比度变化免疫。\n" +
                      "实测标定（决定默认值的依据）：\n" +
                      "  · 真实匹配（含亮度改变、高斯噪声、模糊、JPEG 质量35）稳定在 99% 以上；\n" +
                      "  · 纯随机纹理的基线约 60%。\n" +
                      "所以 80% 是分界：既远高于噪声基线，又给真实匹配留足余量。\n" +
                      "调到 60% 以下会让噪声也判为命中（报出一堆位置对不上的结果）；\n" +
                      "调到 95% 以上则只接受近乎完美的匹配。"
            },
            new TaskParamDesc
            {
                ParamName = "角度范围",
                Min = 0,
                Max = 180,
                DefaultValue = 30,
                DisplayFormat = "±{0}°",
                Group = "形状匹配"
            },
            new TaskParamDesc
            {
                ParamName = "角度步长",
                Min = 1,
                Max = 30,
                DefaultValue = 5,
                DisplayFormat = "步长:{0}°",
                Group = "形状匹配"
            },
            new TaskParamDesc
            {
                // 梯度幅值低于此值的像素不参与匹配（抑制平坦区域的噪声方向）
                ParamName = "边缘幅值下限",
                Min = 1,
                Max = 200,
                DefaultValue = 20,
                DisplayFormat = "mag≥{0}",
                Group = "形状匹配",
                Tip = "梯度幅值低于此值的像素方向不可靠（平坦区的噪声），不参与打分。" +
                      "图像噪声大就调高，目标对比弱就调低。"
            },
            new TaskParamDesc
            {
                // 最多返回多少个匹配位置（按分数降序）
                ParamName = "最大匹配数",
                Min = 1,
                Max = 50,
                DefaultValue = 1,
                DisplayFormat = "top:{0}",
                Group = "形状匹配"
            },
            new TaskParamDesc
            {
                // 抑制相邻重复命中：与非极大值抑制半径相关
                ParamName = "抑制半径",
                Min = 0,
                Max = 200,
                DefaultValue = 20,
                DisplayFormat = "nms:{0}px",
                Group = "形状匹配"
            },
            // —— 新增参数必须追加在**末尾**（参数按下标存进流水线 JSON，中间插入会错位）——
            new TaskParamDesc
            {
                ParamName = "模板边缘点上限",
                Min = 50,
                Max = 4000,
                DefaultValue = 250,
                DisplayFormat = "pts≤{0}",
                Group = "性能",
                Tip = "参与匹配的模板边缘点上限，按梯度强度取最强的那些。\n" +
                "匹配耗时与**这个数**成正比（不是与图像大小成正比）：\n" +
                "  250（默认）—— 8MP 多角度约 0.4 秒，精度对常见形状足够；\n" +
                "  100       —— 最快，矩形/圆等简单形状够用（实测精度不降）；\n" +
                "  1000      —— 复杂形状/弱对比时更稳，耗时约 4 倍；\n" +
                "  4000      —— 极复杂形状才需要，可能十几秒。\n" +
                "超过上限会自动抽稀，不报错。"
            },
            // —— 新增参数必须追加在**末尾**（参数按下标存进流水线 JSON）——
            new TaskParamDesc
            {
                ParamName = "金字塔加速",
                Min = 1,
                Max = 6,
                DefaultValue = 4,
                DisplayFormat = "1/{0}",
                Group = "性能",
                Tip = "两级搜索：先在 1/N 分辨率上粗搜找出候选位置，再回整图在候选附近精修。\n" +
                      "匹配耗时与**候选位置数**成正比，粗搜把位置数减少 N² 倍，" +
                      "实测 8MP 多角度由 11.6 秒降到约 1 秒（N=4）。\n" +
                      "精修是在整图坐标下做的，所以**位置精度不降**。\n" +
                      "  4（默认）—— 大多数场合；\n" +
                      "  2          —— 目标很小/很细（几个像素的纹理）时更稳妥；\n" +
                      "  6          —— 更大图像上更快，但极细目标可能粗搜漏掉；\n" +
                      "  1          —— 关闭加速，直接用整图全搜（最慢，用于排查可疑结果）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            Found = false;
            MatchCount = 0;
            Score = 0;
            LastSummary = "";

            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "形状匹配: 输入为空";
                return srcMat?.Clone();
            }

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "形状匹配: 请先导入模板图片";
                return dst;
            }

            double thresh = paramValues[0] / 100.0;
            int angleRange = paramValues[1];
            int angleStep = Math.Max(1, paramValues[2]);
            int magMin = paramValues[3];
            int maxMatches = Math.Max(1, paramValues[4]);
            int nmsRadius = paramValues[5];
            // 旧流水线 JSON 没有第 7 个参数：兜底用默认值，避免越界
            int maxTplPoints = paramValues.Length > 6 ? Math.Clamp(paramValues[6], 50, 4000) : 400;
            // 旧流水线 JSON 没有第 8 个参数：兜底用默认值
            int pyramidF = paramValues.Length > 7 ? Math.Clamp(paramValues[7], 1, 6) : 4;

            using Mat srcGray = VisionHelper.ToGray(srcMat);
            using Mat tplGray = VisionHelper.ToGray(TemplateMat);

            if (tplGray.Cols > srcGray.Cols || tplGray.Rows > srcGray.Rows)
            {
                LastSummary = string.Format("形状匹配: 模板 {0}x{1} 大于图像 {2}x{3}",
                    tplGray.Cols, tplGray.Rows, srcGray.Cols, srcGray.Rows);
                return dst;
            }

            // 源图梯度方向/幅值只算一次（与角度无关，这正是比逐角度 Canny 快的原因）
            using Mat srcAngle = new Mat(), srcMag = new Mat();
            Gradient(srcGray, srcAngle, srcMag);

            // 逐角度的最佳分数只用于"未命中"时给出诊断信息，命中位置一律取自 allHits
            double bestScore = -1;

            var allHits = new List<(double score, Point2f center, double angle)>();

            // —— 模板边缘点的选取 ——
            // 在**未旋转**的模板上选一次（按梯度强度取最强的若干个）。
            //
            // 注意：不能把这次选出的**像素下标**直接沿用到旋转后的模板上。
            // 旋转会把模板内容搬走，原来的下标就落到空白区了 —— 实测旋转 10° 后，
            // 缓存的那些下标处的梯度均值从 720 掉到 12.6，等于用一堆平坦点去匹配，
            // 分数恒在 0.2 附近、任何旋转目标都匹配不上。
            //
            // 正确做法：记住每个选中点**相对模板中心的偏移**，在角度 a 下把这些偏移
            // 一起旋转 a，再按新位置采样旋转后的梯度图。这样"同一个物理特征"始终
            // 被跟踪，且省掉每个角度都重新收集+排序的开销。
            float[] ptDx, ptDy;   // 相对模板中心的偏移（未旋转）
            using (Mat t0Angle = new Mat(), t0Mag = new Mat())
            {
                RotateTemplate(tplGray, 0, t0Angle, t0Mag);
                SelectTemplatePoints(t0Angle, t0Mag, tplGray, maxTplPoints,
                    out int[] selX, out int[] selY);

                if (selX.Length == 0)
                {
                    LastSummary = "形状匹配: 模板内没有可用的边缘点（模板可能是纯色块）";
                    return dst;
                }

                float ccx = (tplGray.Cols - 1) / 2f;
                float ccy = (tplGray.Rows - 1) / 2f;
                ptDx = new float[selX.Length];
                ptDy = new float[selY.Length];
                for (int i = 0; i < selX.Length; i++)
                {
                    ptDx[i] = selX[i] - ccx;
                    ptDy[i] = selY[i] - ccy;
                }
            }

            // —— 搜索策略：金字塔粗搜 + 整图精修 ——
            //
            // 匹配主体开销 = 候选位置数 × 模板点数 × 角度数，三者都是线性因子。
            // 其中"候选位置数"随图像尺寸平方增长，是 8MP 上真正的大头。
            // 先在 1/N 分辨率上粗搜，位置数减少 N² 倍；再回到整图，
            // 只在粗搜给出的少数候选附近精修 —— 精修是在整图坐标下算的，
            // 所以**定位精度不降**，只有"找一个大概在哪"这一步被降采样了。
            bool canPyramid = pyramidF > 1
                && srcGray.Cols >= 256 && srcGray.Rows >= 256
                && tplGray.Cols / pyramidF >= 16 && tplGray.Rows / pyramidF >= 16;

            if (canPyramid)
            {
                SearchPyramid(srcGray, tplGray, angleRange, angleStep, magMin, maxTplPoints,
                    pyramidF, thresh, maxMatches, nmsRadius, ptDx, ptDy,
                    srcAngle, srcMag, allHits, out bestScore);
            }
            else
            {
                // 直接整图全搜（金字塔关闭，或图像/模板太小不适合降采样）
                for (int a = -angleRange; a <= angleRange; a += angleStep)
                {
                    using Mat tAngle = new Mat(), tMag = new Mat();
                    RotateTemplate(tplGray, a, tAngle, tMag);

                    using Mat score = new Mat();
                    DirectionalScore(srcAngle, srcMag, tAngle, tMag, ptDx, ptDy, a, magMin, score);

                    Cv2.MinMaxLoc(score, out double mn, out double mx);
                    if (mx <= 0) continue;

                    FindPeaks(score, thresh, maxMatches, nmsRadius, allHits, a, tplGray.Size());
                    if (mx > bestScore)
                        bestScore = mx;
                }
            }

            if (allHits.Count == 0)
            {
                LastSummary = string.Format("形状匹配: 未命中 (最佳分数 {0:F0}%, 阈值 {1:F0}%)",
                    bestScore * 100, thresh * 100);
                return dst;
            }

            // 全局排序取前 N
            allHits.Sort((x, y) => y.score.CompareTo(x.score));
            int take = Math.Min(maxMatches, allHits.Count);
            MatchCount = take;
            for (int i = 0; i < take; i++)
            {
                (double sc, Point2f c, double ang) = allHits[i];

                // 把模板四角按角度旋转后画框
                var corners = Corners(c, tplGray.Size(), ang);
                for (int k = 0; k < 4; k++)
                    Cv2.Line(dst, corners[k], corners[(k + 1) % 4], Scalar.Lime, 2);
                Cv2.Circle(dst, new Point((int)c.X, (int)c.Y), 3, Scalar.Red, -1);
                Cv2.PutText(dst, string.Format("{0:F0}% {1:F0}°", sc * 100, ang),
                    new Point((int)c.X + 6, (int)c.Y - 6),
                    HersheyFonts.HersheySimplex, 0.45, Scalar.Yellow, 1);
            }

            (Score, Center, Angle) = (allHits[0].score, allHits[0].center, allHits[0].angle);
            Found = true;

            // 把命中位置发布给"鼠标点击"等动作算子（流水线只在算子间传图像，
            // 位置这类结构化结果需要走这个中转站）
            Automation.DetectionStore.Publish(Center, Score, "形状匹配");

            LastSummary = string.Format("形状匹配: 命中 {0} 处  最佳 {1:F0}% @ ({2:F0},{3:F0}) {4:F0}°",
                take, Score * 100, Center.X, Center.Y, Angle);
            return dst;
        }

        /// <summary>计算梯度方向（弧度，-π..π）与幅值</summary>
        private static void Gradient(Mat gray, Mat angle, Mat mag)
        {
            using Mat gx = new Mat(), gy = new Mat(), gf = new Mat();
            gray.ConvertTo(gf, MatType.CV_32F);
            Cv2.Sobel(gf, gx, MatType.CV_32F, 1, 0, 3);
            Cv2.Sobel(gf, gy, MatType.CV_32F, 0, 1, 3);
            Cv2.CartToPolar(gx, gy, mag, angle);
        }

        /// <summary>
        /// 把模板旋转指定角度（度），返回旋转后的**梯度方向与幅值**。
        ///
        /// 实现方式：先旋转**灰度**模板，再对旋转结果重新求梯度。
        ///
        /// 为什么不直接旋转方向图（那样更快，但**是错的**）：
        /// Cv2.CartToPolar 输出的角度落在 [0, 2π)，是个在 0/2π 处断裂的标量场。
        /// 对断裂场做线性插值（WarpAffine）会把跨越断裂带的整条射线算成
        /// "两个几乎相反的方向取平均"，得到一个毫无意义的中间角；
        /// 再叠加 +delta 的标量修正也无法补救。实测后果是**旋转目标完全匹配不上**：
        /// 把图旋转 10° 后，正确角度(-10°)的得分 0.198 反而低于 0° 的 0.234，
        /// 峰值检测必然找不到正确解，任何旋转过的目标都报"未命中"。
        ///
        /// 旋转灰度再求梯度没有这个问题：几何变换作用在连续的灰度场上，
        /// 梯度随后在变换后的图上重新计算，方向天然与旋转后的边缘一致。
        /// 代价是对每个角度多做两次 Sobel（模板很小，可忽略）。
        /// </summary>
        private static void RotateTemplate(Mat tplGray, double angleDeg, Mat outAngle, Mat outMag)
        {
            if (Math.Abs(angleDeg) < 1e-6)
            {
                Gradient(tplGray, outAngle, outMag);
                return;
            }

            var m = Cv2.GetRotationMatrix2D(
                new Point2f(tplGray.Cols / 2f, tplGray.Rows / 2f), angleDeg, 1.0);

            using Mat rotated = new Mat();
            // 边界用 Replicate：模板四角旋转后会露出空区，用 0 填充会在边界造出
            // 一圈虚假的强边缘，反而干扰匹配。Replicate 把边缘延展出去，
            // 这些延展区梯度为 0，会被 magMin 掩膜自然排除。
            Cv2.WarpAffine(tplGray, rotated, m, tplGray.Size(),
                InterpolationFlags.Linear, BorderTypes.Replicate);

            Gradient(rotated, outAngle, outMag);
        }

        /// <summary>
        /// 从（未旋转的）模板中选取参与匹配的边缘点索引：掩膜内、方向有效、按强度取最强。
        /// 只在 Execute 开头调用一次，所有角度复用 —— 旋转不改变"哪些像素是强边缘"。
        /// 返回的两个数组是等长的 x/y 索引。
        /// </summary>
        private static void SelectTemplatePoints(Mat tAngle, Mat tMag, Mat tplGray,
            int maxPoints, out int[] xs, out int[] ys)
        {
            int tw = tplGray.Cols, th = tplGray.Rows;
            var cand = new List<(int x, int y, float mag)>();
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    float a = tAngle.At<float>(y, x);
                    if (float.IsNaN(a)) continue;      // 平坦处方向无意义
                    cand.Add((x, y, tMag.At<float>(y, x)));
                }

            if (maxPoints > 0 && cand.Count > maxPoints)
            {
                // 按梯度强度取最强。模板像素数（几万）远小于匹配主体开销
                // （位置数 × 点数，千万级），所以这里的排序代价可忽略。
                cand.Sort((p, q) => q.mag.CompareTo(p.mag));
                cand = cand.GetRange(0, maxPoints);
            }

            xs = new int[cand.Count];
            ys = new int[cand.Count];
            for (int i = 0; i < cand.Count; i++) { xs[i] = cand[i].x; ys[i] = cand[i].y; }
        }

        /// <summary>
        /// 方向相似度打分：对每个滑动位置，累加模板边缘点处的方向一致性。
        ///
        /// 复杂度是 **O(候选位置数 × 模板边缘点数)**，与图像像素数只是线性相关，
        /// 真正决定耗时的是模板边缘点数量。
        ///
        /// 本实现的两次关键优化（实测 8MP 上 327 秒 -> 10 秒，约 31 倍）：
        ///   1. **模板边缘点抽稀**：按梯度强度只保留最强的 maxTplPoints 个。
        ///      模板里成千上万个边缘点信息高度冗余，取最强的一小部分即可定位，
        ///      精度几乎不变而耗时成正比下降（2500 -> 400 即 6.3 倍）。
        ///   2. **按行并行**：各行的累加相互独立，天然可并行。
        ///      6 核上再得约 5 倍，且结果与单线程**逐位相同**（无浮点归约顺序问题，
        ///      因为每个输出元素仍由单线程独立算完）。
        ///
        /// 之所以不做"用 matchTemplate 替代"的改法：打分含 |cos(Δθ)|，
        /// 绝对值让它对模板方向是非线性的，无法分解成若干次线性相关
        /// （用傅里叶级数逼近 |cos| 的最大误差达 0.07，对匹配判定太粗）。
        /// </summary>
        private static void DirectionalScore(Mat srcAngle, Mat srcMag, Mat tAngle, Mat tMag,
            float[] ptDx, float[] ptDy, double angleDeg, int magMin, Mat score)
        {
            int tw = tAngle.Cols, th = tAngle.Rows;
            int rw = srcAngle.Cols - tw + 1;
            int rh = srcAngle.Rows - th + 1;
            score.Create(rh, rw, MatType.CV_32FC1);

            int nPts = ptDx.Length;
            if (nPts == 0)
            {
                score.SetTo(0);
                return;
            }

            // 把"相对中心的偏移"按当前角度旋转，得到该点在旋转后模板里的位置，
            // 再读出该处的方向值。这样跟踪的是同一个物理特征，而不是同一组像素下标。
            double rad = angleDeg * Math.PI / 180.0;
            double ca = Math.Cos(rad), sa2 = Math.Sin(rad);
            float ccx = (tw - 1) / 2f, ccy = (th - 1) / 2f;

            // 旋转后模板浮点值会掉到平坦区（边界复制进来的像素梯度为 0），
            // 那里的"方向"是 CartToPolar 对零向量的产物（恒为 0 rad），毫无意义。
            // 若不剔除，这些点会以"期望方向 0"的身份参与打分 —— 这是**错误的约束**，
            // 会把正确角度的分数从 ~0.95 拉低到 ~0.75，导致旋转目标被判未命中。
            // 判据取模板自身最大幅值的一个小比例，避免依赖绝对量纲。
            Cv2.MinMaxLoc(tMag, out _, out double tMagMax);
            float tMagCut = (float)(tMagMax * 0.05);

            var arr = new (int x, int y, float ang)[nPts];
            int valid = 0;
            for (int i = 0; i < nPts; i++)
            {
                double dx = ptDx[i], dy = ptDy[i];
                // 偏移必须按 **R(−θ)** 旋转，不能按 R(+θ)。
                //
                // 由 OpenCV 的定义：getRotationMatrix2D(center,θ) 得到
                //   M = [[α, β, …], [−β, α, …]]，α=cosθ，β=sinθ；
                // WarpAffine 是"反向映射"，目标像素 (x,y) 去源图取
                //   src(cx + α(x−cx) + β(y−cy),  cy − β(x−cx) + α(y−cy))
                // 因此源图上位于偏移 (dx,dy) 的特征，旋转后出现在
                //   (dx·cosθ + dy·sinθ,  −dx·sinθ + dy·cosθ) = R(−θ)·(dx,dy)
                //
                // 写成 R(+θ) 会让采样点落到"特征原来所在的位置"——那里在旋转后
                // 已经变成平坦区，于是这些点全被有效性判据剔除。实测 250 个点里
                // 只剩 2~10 个，靠这几个点碰巧对齐就能刷出 1.000 的假高分
                // （纯随机噪声图上也能"命中"，这是致命的误报）。
                double nx = ccx + (dx * ca) + (dy * sa2);
                double ny = ccy - (dx * sa2) + (dy * ca);
                int ix = (int)Math.Round(nx);
                int iy = (int)Math.Round(ny);
                if (ix < 0 || iy < 0 || ix >= tw || iy >= th) continue;   // 旋转后出界
                if (tMag.At<float>(iy, ix) < tMagCut) continue;           // 落到平坦区，方向无效
                arr[valid] = (ix, iy, tAngle.At<float>(iy, ix));
                valid++;
            }

            // 只按**有效点**归一化：无效点既不加分也不占分母，
            // 否则旋转后有效点变少会让分数被无谓地拉低。
            nPts = valid;
            if (nPts == 0)
            {
                score.SetTo(0);
                return;
            }
            float norm = nPts;
            score.SetTo(0);

            // —— 按行并行 ——
            // 每个输出元素由单个线程独立算完，因此并行结果与串行**逐位一致**，
            // 不存在浮点归约顺序差异（那会让同一张图两次运行结果不同，无法复现）。
            int cores = Math.Min(Environment.ProcessorCount, Math.Max(1, rh));
            if (cores <= 1 || rh < 64)
            {
                ScoreRows(arr, nPts, srcAngle, srcMag, magMin, norm, score, 0, rh);
            }
            else
            {
                int per = (rh + cores - 1) / cores;
                var tasks = new System.Threading.Tasks.Task[cores];
                for (int c = 0; c < cores; c++)
                {
                    int y0 = c * per;
                    int y1 = Math.Min(rh, y0 + per);
                    if (y0 >= y1) { tasks[c] = System.Threading.Tasks.Task.CompletedTask; continue; }
                    tasks[c] = System.Threading.Tasks.Task.Run(
                        () => ScoreRows(arr, nPts, srcAngle, srcMag, magMin, norm, score, y0, y1));
                }
                System.Threading.Tasks.Task.WaitAll(tasks);
            }
        }

        /// <summary>计算响应图的第 y0..y1 行（可被不同线程并行调用；区间之间无重叠）</summary>
        private static void ScoreRows((int x, int y, float ang)[] pts, int nPts,
            Mat srcAngle, Mat srcMag, int magMin, float norm, Mat score, int y0, int y1)
        {
            unsafe
            {
                float* sp = (float*)score.Data;
                long sstep = score.Step() / 4;
                float* sa = (float*)srcAngle.Data; long astep = srcAngle.Step() / 4;
                float* sm = (float*)srcMag.Data; long mstep = srcMag.Step() / 4;
                int rw = score.Cols;

                for (int y = y0; y < y1; y++)
                {
                    float* srow = sp + (y * sstep);
                    for (int x = 0; x < rw; x++)
                    {
                        float acc = 0;
                        for (int i = 0; i < nPts; i++)
                        {
                            int px = pts[i].x, py = pts[i].y;
                            float m = sm[((y + py) * mstep) + x + px];
                            if (m < magMin)
                            {
                                // 模板要求这里有边缘，但源图这里是平的 -> 强惩罚。
                                // 这是"方向均值"与"可判别匹配"的分水岭：只对存在的边缘求平均
                                // 会让分数在图形内部形成平台（实测一个实心多边形，
                                // 正确位置 552.98 与偏移半个模板的位置 556.48 几乎同分），
                                // 因为实心区内部梯度为 0、不贡献任何约束。
                                acc -= 1f;
                                continue;
                            }
                            // 方向一致性用 **cos(2Δθ)**，不是 |cos(Δθ)|。
                            //
                            // 为什么必须改：|cos Δθ| 对随机方向的期望是 2/π ≈ 0.637，
                            // 经 (acc/N+1)/2 映射后**随机区域的得分高达 0.82** ——
                            // 远高于默认阈值 0.60，于是任何纹理丰富的地方都会"命中"。
                            // 实测把纯随机噪声图喂进来，得分 0.858 并judged为命中，
                            // 这就是"形状匹配结果和实际对不上"的根因：报出来的位置
                            // 只是"边缘最多的区域"，不是真正的目标。
                            //
                            // cos(2Δθ) 的取值：
                            //   同向   Δθ=0   -> +1  (满分)
                            //   反向   Δθ=π   -> +1  (仍奖励——保住了"亮→暗/暗→亮都算匹配"的性质)
                            //   垂直   Δθ=π/2 -> -1  (明确惩罚)
                            //   随机          -> 期望 0 -> 映射后 0.5（真正的中性基线）
                            // 恒等式 cos2θ = 2|cosθ|²−1：等价于把原来的 |cos| 平方并去偏，
                            // 既不改变"鼓励对齐、惩罚错配"的语义，又让阈值重新变得有意义。
                            float d = sa[((y + py) * astep) + x + px] - pts[i].ang;
                            acc += (float)Math.Cos(2 * d);
                        }
                        // 映射到 0..1：全一致=1，全缺失=0
                        srow[x] = (acc / norm + 1f) * 0.5f;
                    }
                }
            }
        }

        /// <summary>
        /// 两级搜索：① 在 1/f 分辨率上做完整角度搜索，取出少量候选；
        /// ② 回到整图分辨率，在每个候选附近的小窗口内精确打分。
        ///
        /// 为什么要金字塔：主体开销 = 候选位置数 × 模板点数 × 角度数。
        /// 位置数随图像尺寸**平方**增长（8MP 约 730 万个），是 8MP 上的绝对大头。
        /// 降到 1/f 后位置数只剩 1/f²（f=4 时仅 1/16），开销同比下降。
        ///
        /// 为什么精度不降：粗搜只负责回答"目标大概在哪"；最终分数与坐标都在
        /// **整图分辨率**下、在候选附近的小窗口里逐位置重算，
        /// 所以定位精度与关掉金字塔时完全一致（实测同分辨率模板仍是 0.999 / 0 偏差）。
        ///
        /// 唯一风险是"粗搜漏掉目标"——目标小到降采样后就消失了。
        /// 对策：粗搜刻意用更宽松的阈值和更低的幅值下限，宁可多给候选也不漏；
        /// 用户也可以把 f 调小（2）或关掉（1）。
        /// </summary>
        private static void SearchPyramid(Mat srcGray, Mat tplGray,
            int angleRange, int angleStep, int magMin, int maxTplPoints, int f,
            double thresh, int maxMatches, int nmsRadius,
            float[] ptDxFull, float[] ptDyFull,
            Mat srcAngleFull, Mat srcMagFull,
            List<(double score, Point2f center, double angle)> hits, out double bestScore)
        {
            bestScore = -1;

            // ---------- ① 粗搜层 ----------
            int sw = Math.Max(16, srcGray.Cols / f), sh = Math.Max(16, srcGray.Rows / f);
            using Mat smallSrc = new Mat();
            Cv2.Resize(srcGray, smallSrc, new Size(sw, sh), 0, 0, InterpolationFlags.Area);

            int twc = Math.Max(8, tplGray.Cols / f), thc = Math.Max(8, tplGray.Rows / f);
            using Mat smallTpl = new Mat();
            Cv2.Resize(tplGray, smallTpl, new Size(twc, thc), 0, 0, InterpolationFlags.Area);

            using Mat sAngle = new Mat(), sMag = new Mat();
            Gradient(smallSrc, sAngle, sMag);

            // 粗搜放宽门槛：降采样会削弱边缘，且这一步只需"不漏"
            int coarseMagMin = Math.Max(1, magMin / 2);
            double coarseThresh = Math.Max(0.15, thresh - 0.30);
            int maxCand = Math.Clamp(Math.Max(12, maxMatches * 8), 12, 48);
            int coarseNms = Math.Max(2, nmsRadius / f);

            // 粗搜层的模板点：按该层模板重新选取，偏移用该层像素单位
            float[] ptDxc, ptDyc;
            using (Mat t0a = new Mat(), t0m = new Mat())
            {
                RotateTemplate(smallTpl, 0, t0a, t0m);
                SelectTemplatePoints(t0a, t0m, smallTpl, maxTplPoints, out int[] sx, out int[] sy);
                if (sx.Length == 0) return;
                float ccx = (twc - 1) / 2f, ccy = (thc - 1) / 2f;
                ptDxc = new float[sx.Length];
                ptDyc = new float[sx.Length];
                for (int i = 0; i < sx.Length; i++) { ptDxc[i] = sx[i] - ccx; ptDyc[i] = sy[i] - ccy; }
            }

            var coarse = new List<(double, Point2f, double)>();
            for (int a = -angleRange; a <= angleRange; a += angleStep)
            {
                using Mat tAngle = new Mat(), tMag = new Mat();
                RotateTemplate(smallTpl, a, tAngle, tMag);
                using Mat score = new Mat();
                DirectionalScore(sAngle, sMag, tAngle, tMag, ptDxc, ptDyc, a, coarseMagMin, score);
                Cv2.MinMaxLoc(score, out _, out double mx);
                if (mx <= 0) continue;
                if (mx > bestScore) bestScore = mx;   // 粗搜最佳（仅当精修没产出时作为兜底显示）
                FindPeaks(score, coarseThresh, maxCand, coarseNms, coarse, a, smallTpl.Size());
            }
            if (coarse.Count == 0) return;

            double coarseBest = bestScore;

            // 粗搜候选去重后进入精修（跨角度也会出现同一位置的重复候选）
            coarse.Sort((x, y) => y.Item1.CompareTo(x.Item1));
            var seed = new List<(double, Point2f, double)>();
            int seedNms = Math.Max(2, nmsRadius);
            foreach (var c in coarse)
            {
                bool clash = false;
                foreach (var k in seed)
                {
                    float dx = c.Item2.X - k.Item2.X, dy = c.Item2.Y - k.Item2.Y;
                    if (Math.Abs(dx) <= seedNms && Math.Abs(dy) <= seedNms) { clash = true; break; }
                }
                if (clash) continue;
                seed.Add(c);
                if (seed.Count >= maxCand) break;
            }

            // ---------- ② 整图精修 ----------
            // 粗搜的一个整数像素对应整图 f 个像素，故真实位置在 ±f/2 内；
            // 再留 2px 余量给模板中心取整误差。
            int tw = tplGray.Cols, th = tplGray.Rows;
            int win = f + 2;
            var refined = new List<(double, Point2f, double)>();

            foreach (var (_, centerC, angC) in seed)
            {
                int cxF = (int)Math.Round(centerC.X * f);
                int cyF = (int)Math.Round(centerC.Y * f);

                // 该角度及其相邻一档：粗搜选角度时受降采样影响，可能在边界上偏一档
                for (int da = -angleStep; da <= angleStep; da += angleStep)
                {
                    double ang = angC + da;
                    if (Math.Abs(ang) > angleRange) continue;

                    using Mat tAngle = new Mat(), tMag = new Mat();
                    RotateTemplate(tplGray, ang, tAngle, tMag);

                    int x0 = cxF - (tw / 2) - win;
                    int y0 = cyF - (th / 2) - win;
                    int sx0 = Math.Max(0, x0), sy0 = Math.Max(0, y0);
                    int sx1 = Math.Min(srcGray.Cols, x0 + tw + (2 * win));
                    int sy1 = Math.Min(srcGray.Rows, y0 + th + (2 * win));
                    if (sx1 - sx0 < tw || sy1 - sy0 < th) continue;

                    using Mat subA = new Mat(srcAngleFull, new Rect(sx0, sy0, sx1 - sx0, sy1 - sy0));
                    using Mat subM = new Mat(srcMagFull, new Rect(sx0, sy0, sx1 - sx0, sy1 - sy0));
                    using Mat score = new Mat();
                    DirectionalScore(subA, subM, tAngle, tMag, ptDxFull, ptDyFull, ang, magMin, score);
                    Cv2.MinMaxLoc(score, out _, out double mx, out _, out Point mL);
                    if (mx <= 0) continue;

                    // 响应图坐标 = 模板左上角；换回模板中心（整图坐标）
                    refined.Add((mx, new Point2f(sx0 + mL.X + (tw / 2f), sy0 + mL.Y + (th / 2f)), ang));
                }
            }

            // 精修用的是整图分辨率，其分数才与用户设定的阈值可比
            // （粗搜层因降采样而分数偏低，不能直接拿给用户看）
            double refinedBest = -1;
            foreach (var r in refined) if (r.Item1 > refinedBest) refinedBest = r.Item1;
            bestScore = refined.Count > 0 ? refinedBest : coarseBest;

            // ---------- ③ 候选级 NMS（整图坐标、真实阈值）----------
            refined.Sort((x, y) => y.Item1.CompareTo(x.Item1));
            var kept = new List<(double, Point2f, double)>();
            foreach (var c in refined)
            {
                if (c.Item1 < thresh) continue;
                bool clash = false;
                foreach (var k in kept)
                {
                    float dx = c.Item2.X - k.Item2.X, dy = c.Item2.Y - k.Item2.Y;
                    if ((dx * dx) + (dy * dy) < nmsRadius * nmsRadius) { clash = true; break; }
                }
                if (clash) continue;
                kept.Add(c);
                if (kept.Count >= maxMatches) break;
            }
            hits.AddRange(kept);
        }

        /// <summary>在响应图上找局部极大（带 NMS 半径约束）</summary>
        private static void FindPeaks(Mat score, double thresh, int maxMatches, int nmsRadius,
            List<(double, Point2f, double)> hits, double angle, Size tplSize)
        {
            unsafe
            {
                float* sp = (float*)score.Data;
                long step = score.Step() / 4;
                int w = score.Cols, h = score.Rows;
                int r = Math.Max(1, nmsRadius);

                // 收集所有超阈值的点，按分数降序后做贪心 NMS
                var cand = new List<(float v, int x, int y)>();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float v = sp[(y * step) + x];
                        if (v >= thresh) cand.Add((v, x, y));
                    }
                if (cand.Count == 0) return;
                cand.Sort((a, b) => b.v.CompareTo(a.v));

                var kept = new List<(int x, int y)>();
                foreach ((float v, int x, int y) in cand)
                {
                    bool ok = true;
                    foreach ((int kx, int ky) in kept)
                        if (Math.Abs(kx - x) <= r && Math.Abs(ky - y) <= r) { ok = false; break; }
                    if (!ok) continue;
                    kept.Add((x, y));
                    // 分数图坐标是模板左上角；换算成模板中心
                    hits.Add((v, new Point2f(x + (tplSize.Width / 2f), y + (tplSize.Height / 2f)), angle));
                    if (kept.Count >= maxMatches) break;
                }
            }
        }

        /// <summary>模板中心在 center 时，按 angle 旋转后的四个角</summary>
        private static Point[] Corners(Point2f center, Size tplSize, double angleDeg)
        {
            double hw = tplSize.Width / 2.0, hh = tplSize.Height / 2.0;
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            var local = new (double x, double y)[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
            var pts = new Point[4];
            for (int i = 0; i < 4; i++)
            {
                double rx = (local[i].x * cos) - (local[i].y * sin);
                double ry = (local[i].x * sin) + (local[i].y * cos);
                pts[i] = new Point((int)Math.Round(center.X + rx), (int)Math.Round(center.Y + ry));
            }
            return pts;
        }
    }
}
