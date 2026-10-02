namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 标定数据全局共享：让"畸变FOV标定"算出的内参/畸变系数能被后续算子直接用，
    /// 不必让用户把 9 个数字手工抄到另一个算子里。
    ///
    /// 为什么是全局静态而不是实例状态：
    /// 流水线里每个步骤持有**独立的算子实例**（见 VisionTaskRegistry.CreateTask 的注释），
    /// 把标定结果放在标定算子实例上，下游算子根本拿不到。标定结果物理上就是
    /// "一次标定、整条线通用"的全局量，所以用静态存储是符合语义的，不是偷懒。
    ///
    /// 线程安全：标定/执行都在 UI 线程，用简单的 lock 保护即可。
    /// </summary>
    public static class CalibrationStore
    {
        private static readonly object _lock = new();
        private static bool _has;
        private static double _fx, _fy, _cx, _cy;
        private static double[] _dist = new double[5];

        /// <summary>是否已有可用标定</summary>
        public static bool Has
        {
            get { lock (_lock) return _has; }
        }

        /// <summary>像素当量（mm/px）：由棋盘格标定得出，供所有测量算子换算物理单位</summary>
        public static double MmPerPixel { get; private set; }

        /// <summary>
        /// 写入一次标定结果。dist 顺序为 [k1, k2, p1, p2, k3]。
        /// fx/fy 非有限或非正时拒绝写入 —— 退化标定（正对平面靶标）会给出
        /// 天文数字的焦距，把它存进来会让下游算出一片空白。
        /// </summary>
        public static bool Set(double fx, double fy, double cx, double cy,
            double[] dist, double mmPerPixel = 0)
        {
            if (!double.IsFinite(fx) || !double.IsFinite(fy) || fx <= 0 || fy <= 0)
                return false;
            if (!double.IsFinite(cx) || !double.IsFinite(cy))
                return false;

            var d = new double[5];
            if (dist != null)
                for (int i = 0; i < 5 && i < dist.Length; i++)
                    d[i] = double.IsFinite(dist[i]) ? dist[i] : 0;

            lock (_lock)
            {
                _fx = fx; _fy = fy; _cx = cx; _cy = cy;
                _dist = d;
                MmPerPixel = mmPerPixel > 0 && double.IsFinite(mmPerPixel) ? mmPerPixel : 0;
                _has = true;
            }
            return true;
        }

        /// <summary>读取标定结果；无可用标定返回 false</summary>
        public static bool TryGet(out double fx, out double fy, out double cx, out double cy, out double[] dist)
        {
            lock (_lock)
            {
                fx = _fx; fy = _fy; cx = _cx; cy = _cy;
                dist = (double[])_dist.Clone();
                return _has;
            }
        }

        /// <summary>清空（切换产品/重新标定时用）</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _has = false;
                _fx = _fy = _cx = _cy = 0;
                _dist = new double[5];
                MmPerPixel = 0;
            }
        }
    }
}
