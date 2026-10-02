using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VisionToolDemo.Vision.Train;
using System.Windows.Media;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 训练页面：小样本图像分类训练（纯 C# CNN，无第三方训练框架）。
    /// 流程：选训练集目录（子文件夹=类别）→ 扫描统计 → 调参数 → 训练（后台线程可停止）
    /// → 验证单张图 → 导出 ONNX（供「深度学习推理」算子直接加载）。
    /// </summary>
    /// <summary>文件路径 → 缩略图（标注图片列表用；按短边解码避免大图占内存）</summary>
    public sealed class FileToImageConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 120;   // 缩略图，避免原图全尺寸解码
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class TrainPage : UserControl
    {
        /// <summary>训练器实例（页面参数每次训练都会重建，保持干净状态）</summary>
        private DeepTrainer _trainer;

        /// <summary>当前训练出的模型（训练完成、导出/验证共用）</summary>
        private DeepTrainer.TrainedModel _model;

        /// <summary>后台训练任务（用于取消/等待）</summary>
        private Task _trainTask;

        /// <summary>训练进行中标记（按钮状态/停止按钮联动）</summary>
        private volatile bool _training;

        /// <summary>训练监控：每轮 loss/acc 历史（曲线用）与计时</summary>
        private readonly List<float> _lossHist = new();
        private readonly List<float> _accHist = new();
        private System.Diagnostics.Stopwatch _trainWatch = new();

        /// <summary>标注条目：图片文件名 + 框（像素） + 标签名</summary>
        private sealed record AnnotItem(string Image, int X, int Y, int W, int H, string Label);

        /// <summary>当前目录已标注的全部条目（内存态；「保存标注」写 标注.json）</summary>
        private readonly List<AnnotItem> _annotItems = [];

        /// <summary>当前预览的图片文件名（相对训练目录）</summary>
        private string _annotCurImg = "";

        /// <summary>拖框起点/终点（AnnotOverlay 显示坐标）</summary>
        private Point _annotDragStart;
        private Point _annotDragEnd;
        private bool _annotDragging;

        /// <summary>拖框得到的图像像素矩形（显示坐标换算后；0 尺寸=无）</summary>
        private OpenCvSharp.Rect _annotDraftPx;

        /// <summary>人脸辅助：勾选后切换图片自动检测人脸预填框</summary>
        private bool _annotFaceAuto;

        /// <summary>人脸预填框（像素坐标；Overlay 显示半透明黄框，待「添加全部预填」入库）</summary>
        private readonly List<OpenCvSharp.Rect> _annotFaceDrafts = [];

        /// <summary>当前图已入库标注框的选中下标（-1=未选中；图上高亮/移动/缩放/删除用）</summary>
        private int _annotSel = -1;

        /// <summary>图上编辑状态：None=空闲 / Draw=拖新框 / Move=移动选中框 / Resize=缩放选中框</summary>
        private enum AnnotEditMode { None, Draw, Move, Resize }
        private AnnotEditMode _annotEditMode = AnnotEditMode.None;

        /// <summary>缩放中的角点：0=左上 1=右上 2=左下 3=右下</summary>
        private int _annotResizeCorner;

        /// <summary>移动/缩放开始时的显示坐标与原始像素矩形（拖拽期间基准）</summary>
        private Point _annotEditStart;
        private OpenCvSharp.Rect _annotEditStartRect;

        /// <summary>标签池（历史标签去重；下拉可选，手输新标签自动加入）</summary>
        private readonly List<string> _annotLabelPool = [];

        /// <summary>列表选中 ↔ 图上选中同步时的递归抑制</summary>
        private bool _suppressAnnotSync;

        /// <summary>训练集加载方式：true=从 标注.json 加载（标注框+标签）；false=目录分类</summary>
        private bool _useAnnotated;

        /// <summary>ViewModel：命令与状态（标注画布/曲线绘制/训练线程等视图与服务交互留在本类）</summary>
        internal readonly ViewModels.TrainViewModel Vm = new();

        public TrainPage()
        {
            InitializeComponent();
            DataContext = Vm;
            Vm.TrainRequested += () => BtnTrain_Click(null, null);
            Vm.StopRequested += () => BtnStop_Click(null, null);
            Vm.ValidateRequested += () => BtnValidate_Click(null, null);
            Vm.ExportRequested += () => BtnExport_Click(null, null);
            Vm.SaveModelRequested += () => BtnSaveModel_Click(null, null);
            Vm.LoadModelRequested += () => BtnLoadModel_Click(null, null);
            Vm.BrowseDirRequested += () => BtnBrowseDir_Click(null, null);
            Vm.ScanDirRequested += () => BtnScanDir_Click(null, null);
            Vm.PickAnnotRequested += () => BtnPickAnnotImg_Click(null, null);
            Vm.RefreshAnnotRequested += () => BtnRefreshAnnotList_Click(null, null);
            Vm.SaveAnnotRequested += () => BtnSaveAnnot_Click(null, null);
            Vm.ClearAnnotRequested += () => BtnClearAnnot_Click(null, null);
            Vm.AddAnnotRequested += () => BtnAddAnnot_Click(null, null);
            Vm.DelAnnotRequested += () => BtnDelAnnot_Click(null, null);
            Vm.DetectFacesRequested += () => BtnDetectFaces_Click(null, null);
            Vm.AddFaceDraftsRequested += () => BtnAddFaceDrafts_Click(null, null);
            // 人脸辅助勾选状态 → 视图逻辑（勾选且当前有图则立即检测预填）
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModels.TrainViewModel.FaceAuto)) FaceAutoChanged();
                if (e.PropertyName == nameof(ViewModels.TrainViewModel.LogText)) TrainLog.ScrollToEnd();
            };
            InitThemeDots();
            BuildConfusion(null, null);   // 空态提示（尚未训练）
            ThemeManager.RegisterPage(this);
        }

        /// <summary>顶栏主题色点：颜色取各主题强调色，当前主题用粗边框高亮（与全软件统一）</summary>
        private void InitThemeDots()
            => ThemeUi.RefreshDots(ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);

        /// <summary>主题色点点击：应用主题并刷新高亮</summary>
        private void ThemeDot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => ThemeUi.ApplyFromClick(sender as Border, ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);



        /// <summary>浏览选择训练集目录（.NET 8 自带文件夹选择器）</summary>
        private void BtnBrowseDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择训练集目录（子文件夹=类别）" };
            if (dlg.ShowDialog() == true) TrainDirBox.Text = dlg.FolderName;
        }

        /// <summary>扫描目录：统计类别与样本数，校验是否满足训练条件</summary>
        private void BtnScanDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = TrainDirBox.Text.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                ScanInfo.Text = "目录不存在：" + dir;
                return;
            }
            try
            {
                var trainer = new DeepTrainer();
                var stats = trainer.LoadFolder(dir);   // 返回 类别名 -> 样本数
                // 扫描子文件夹 = 明确切换到「子文件夹=类别」模式，退出标注模式（_useAnnotated=false）。
                // 之前点过「从标注训练集加载」的话，不重置这里会一直按标注模式训练（这就是"取消不了"的根因）。
                _useAnnotated = false;
                if (stats.Count == 0)
                {
                    ScanInfo.Text = "该目录下没有找到任何图片（子文件夹=类别，支持 png/jpg/jpeg/bmp/webp）";
                    return;
                }
                ScanInfo.Text = "已切换到「子文件夹=类别」模式（取消标注）。类别 " + stats.Count + " 个，共 " + stats.Values.Sum() + " 张："
                    + string.Join("，", stats.Select(kv => kv.Key + "×" + kv.Value));
                var bad = stats.Where(kv => kv.Value < 2).Select(kv => kv.Key).ToList();
                if (bad.Count > 0) ScanInfo.Text += "\n注意：以下类别不足 2 张，训练会失败：" + string.Join("、", bad);
            }
            catch (Exception ex)
            {
                ScanInfo.Text = "扫描失败：" + ex.Message;
            }
        }

        // ============================== 参数读取 ==============================

        /// <summary>从界面读取参数并构造训练器；参数非法时抛出带提示的异常</summary>
        private DeepTrainer BuildTrainer()
        {
            var t = new DeepTrainer();
            if (!int.TryParse(SideBox.Text.Trim(), out int side)) throw new InvalidOperationException("输入边长必须是整数");
            if (!int.TryParse(EpochsBox.Text.Trim(), out int epochs)) throw new InvalidOperationException("训练轮数必须是整数");
            if (!float.TryParse(LrBox.Text.Trim(), out float lr)) throw new InvalidOperationException("学习率必须是数字");
            if (!int.TryParse(BatchBox.Text.Trim(), out int batch)) throw new InvalidOperationException("批大小必须是整数");
            if (!long.TryParse(SeedBox.Text.Trim(), out long seed)) throw new InvalidOperationException("随机种子必须是整数");
            t.InputSide = side;
            t.Epochs = epochs;
            t.Lr = lr;
            t.Batch = Math.Max(1, batch);
            t.Seed = seed;
            // 数据增强开关（默认关：实测表面纹理数据通常负收益，用户可打开对比）；
            // 学习率余弦衰减默认开（固定 lr 后期震荡不收敛，实测 72%→91%）
            t.Augment = AugCheck.IsChecked == true;
            t.Log = m => Ui(() => AppendLog(m));
            // 校验：输入边长必须是偶数（网络按 /2 下采样两次）
            if (t.InputSide < 16 || t.InputSide > 256 || t.InputSide % 2 != 0)
                throw new InvalidOperationException("输入边长需为 16~256 的偶数（默认 32）");
            if (t.Epochs < 1 || t.Epochs > 100000) throw new InvalidOperationException("训练轮数需在 1~100000");
            if (t.Lr <= 0 || t.Lr > 1) throw new InvalidOperationException("学习率需在 (0,1]");
            var dir = TrainDirBox.Text.Trim();
            if (!Directory.Exists(dir)) throw new InvalidOperationException("训练集目录不存在：" + dir);
            if (_useAnnotated)
            {
                // 标注模式：从 标注.json 加载（框选目标+标签，人像姓名/产品NG类型识别用）
                t.LoadAnnotated(dir);
                if (t.Labels.Count < 2) throw new InvalidOperationException("至少需要 2 个标签（如 张三/李四，或 良品/划痕）");
            }
            else
            {
                t.LoadFolder(dir);
                if (t.Labels.Count < 2) throw new InvalidOperationException("至少需要 2 个类别（子文件夹）");
            }
            return t;
        }

        // ============================== 训练 ==============================

        /// <summary>开始训练：后台线程执行，日志实时回流，可停止</summary>
        private void BtnTrain_Click(object sender, RoutedEventArgs e)
        {
            if (_training) return;
            DeepTrainer trainer;
            try { trainer = BuildTrainer(); }
            catch (Exception ex) { TrainStatus.Text = "参数错误：" + ex.Message; AppendLog("[错误] " + ex.Message); return; }

            _trainer = trainer;
            _training = true;
            Vm.IsTrainRunning = true;
            Vm.IsModelReady = false;
            // _model 保持（加载过 .vtmodel 时=继续训练的种子）；首次训练为空=从头开始
            TrainStatus.Text = "训练中…（轮数 " + trainer.Epochs + "，学习率 " + trainer.Lr + "）";
            // 训练监控初始化：清空曲线、进度条按总轮数、摘要重置、开始计时
            _lossHist.Clear();
            _accHist.Clear();
            ChartLoss.Children.Clear();
            ChartAcc.Children.Clear();
            TrainProgress.Maximum = trainer.Epochs;
            TrainProgress.Value = 0;
            ProgText.Text = "0/" + trainer.Epochs;
            SumAcc.Text = "训练中…";
            SumLoss.Text = "—";
            SumTime.Text = "—";
            SumData.Text = "—";
            _trainWatch.Restart();
            // 每轮回调：进度条 + loss/acc 曲线实时刷新
            trainer.OnEpoch = (ep, loss, acc) => Ui(() =>
            {
                _lossHist.Add(loss);
                _accHist.Add(acc);
                TrainProgress.Value = ep;
                ProgText.Text = ep + "/" + trainer.Epochs;
                DrawChart(ChartLoss, _lossHist, Color.FromRgb(240, 101, 67));   // 红橙=loss
                DrawChart(ChartAcc, _accHist, Color.FromRgb(52, 168, 83));      // 绿=acc
            });
            AppendLog("==== 开始训练：类别 " + string.Join("/", trainer.Labels)
                + "，样本 " + trainer.Samples.Count + "，边长 " + trainer.InputSide + " ====");

            _trainTask = Task.Run(() =>
            {
                try
                {
                    // 传 _model 作种子：加载过模型 = 以它权重继续训练；null = 从头初始化
                    var model = trainer.Train(_model);
                    return model;
                }
                catch (Exception ex)
                {
                    Ui(() => { TrainStatus.Text = "训练失败：" + ex.Message; AppendLog("[错误] " + ex.Message); });
                    return null;
                }
            }).ContinueWith(prev =>
            {
                var model = prev.Status == TaskStatus.RanToCompletion ? prev.Result : null;
                Ui(() => OnTrainDone(model));
            }, TaskScheduler.Default);
        }

        /// <summary>训练结束（成功或失败）统一收尾：更新状态、恢复按钮</summary>
        private void OnTrainDone(DeepTrainer.TrainedModel model)
        {
            _training = false;
            Vm.IsTrainRunning = false;
            if (model != null)
            {
                _model = model;
                Vm.IsModelReady = true;
                TrainStatus.Text = string.Format("训练完成：第 {0} 轮，训练集准确率 {1:P0}，loss {2:F4}",
                    model.FinalEpoch, model.TrainAcc, model.FinalLoss);
                _trainWatch.Stop();
                SumAcc.Text = string.Format("{0:P1}", model.TrainAcc);
                SumLoss.Text = string.Format("{0:F4}", model.FinalLoss);
                SumTime.Text = string.Format("{0:F1} 秒", _trainWatch.Elapsed.TotalSeconds);
                SumData.Text = string.Format("{0} / {1}", _trainer?.Samples.Count ?? 0, model.Labels.Count);
                BuildConfusion(model.Confusion, model.Labels);
                AppendLog(string.Format("==== 训练完成：第 {0} 轮，准确率 {1:P0}，loss {2:F4} ====",
                    model.FinalEpoch, model.TrainAcc, model.FinalLoss));
            }
            else
            {
                TrainStatus.Text = "训练已停止或失败";
            }
        }

        /// <summary>请求停止训练（训练器在轮与轮之间检查标记）</summary>
        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_trainer != null) _trainer.StopRequested = true;
            AppendLog("[提示] 已请求停止，当前轮结束后生效");
        }

        // ============================== 验证 ==============================

        /// <summary>选择一张图片用当前模型预测，显示各类别概率</summary>
        private void BtnValidate_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择一张验证图片",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var rgb = DeepTrainer.LoadImageAsRgb(dlg.FileName, _model.InputSide);
                if (rgb == null) { AppendLog("[错误] 图片读取失败（非常规尺寸？）" + dlg.FileName); return; }
                var probs = _model.Predict(rgb);
                var lines = new List<string>();
                for (int i = 0; i < probs.Length; i++)
                    lines.Add(string.Format("  {0}（{1}）：{2:P1}", _model.Labels[i], i, probs[i]));
                lines.Insert(0, "验证 " + Path.GetFileName(dlg.FileName) + " → 预测类别：" + _model.Labels[ArgMax(probs)]);
                AppendLog(string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                AppendLog("[错误] 验证失败：" + ex.Message);
            }
        }

        /// <summary>最大概率下标（预测类别）</summary>
        private static int ArgMax(float[] a)
        {
            int bi = 0;
            for (int i = 1; i < a.Length; i++) if (a[i] > a[bi]) bi = i;
            return bi;
        }

        // ============================== 导出 ONNX ==============================

        /// <summary>导出 ONNX + labels.txt：默认输出到 程序目录\train_output\，可自选目录</summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;
            var dlg = new OpenFolderDialog
            {
                Title = "选择导出目录（将写入 model.onnx 与 labels.txt）",
                InitialDirectory = Path.Combine(AppContext.BaseDirectory, "train_output")
            };
            string outDir = dlg.ShowDialog() == true ? dlg.FolderName
                : Path.Combine(AppContext.BaseDirectory, "train_output");
            Directory.CreateDirectory(outDir);
            try
            {
                string onnxPath = Path.Combine(outDir, "model.onnx");
                byte[] bytes = TrainOnnxExporter.Export(_model, onnxPath);
                File.WriteAllBytes(onnxPath, bytes);
                File.WriteAllText(Path.Combine(outDir, "labels.txt"), string.Join("\n", _model.Labels) + "\n");
                AppendLog("[导出成功] " + onnxPath + "\n  " + Path.Combine(outDir, "labels.txt"));
                TrainStatus.Text = "已导出：" + onnxPath;
            }
            catch (Exception ex)
            {
                AppendLog("[错误] 导出失败：" + ex.Message);
                TrainStatus.Text = "导出失败：" + ex.Message;
            }
        }

        // ============================== 标注（人像/NG 类型识别） ==============================

        /// <summary>刷新图片列表：列出训练目录里的图片（排除隐藏/子目录）</summary>
        private void BtnRefreshAnnotList_Click(object sender, RoutedEventArgs e)
        {
            var dir = TrainDirBox.Text.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                ScanInfo.Text = "目录不存在：" + dir;
                return;
            }
            var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => DeepTrainer.IsImageExt(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFileName)
                .ToList();
            AnnotImgList.ItemsSource = files;
            LoadAnnotItems(dir);   // 刷新后从 标注.json 载入历史标注（自动保存→刷新不丢）
            AnnotList.ItemsSource = null;
            if (files.Count == 0)
            {
                ScanInfo.Text = "目录里没有图片，无法标注";
            }
            else
            {
                ScanInfo.Text = "标注：目录里有 " + files.Count + " 张图，点选缩略图开始框选标注";
            }
        }

        /// <summary>
        /// 选择一张图片开始标注：弹文件选择器；若所选图片不在训练目录，自动复制进去
        /// （标注.json 里的 image 字段是相对训练目录的文件名，复制后「从标注训练集加载」才能读到）。
        /// </summary>
        private void BtnPickAnnotImg_Click(object sender, RoutedEventArgs e)
        {
            var dir = TrainDirBox.Text.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                ScanInfo.Text = "先在上方「训练集目录」填好目录（标注图片会自动复制进去）";
                return;
            }
            var dlg = new OpenFileDialog
            {
                Title = "选择要标注的图片（自动复制到训练目录）",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                if (!DeepTrainer.IsImageExt(ext)) { ScanInfo.Text = "不支持该图片格式：" + ext; return; }
                // 若不在训练目录则复制（重名自动加序号）
                string name = Path.GetFileName(dlg.FileName);
                string dest = Path.Combine(dir, name);
                if (!string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    int i = 1;
                    while (File.Exists(dest))
                    {
                        name = Path.GetFileNameWithoutExtension(dlg.FileName) + "_" + (i++) + ext;
                        dest = Path.Combine(dir, name);
                    }
                    File.Copy(dlg.FileName, dest);
                }
                _useAnnotated = true;   // 正在做标注：后续训练按标注模式走
                // 刷新列表并选中该图
                BtnRefreshAnnotList_Click(sender, e);
                var files = AnnotImgList.ItemsSource as System.Collections.IEnumerable;
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        if ((string)f == name)
                        {
                            AnnotImgList.SelectedItem = f;
                            break;
                        }
                    }
                }
                ScanInfo.Text = "已加载标注图片：" + name + "（图上拖框 → 填标签 → 添加标注）";
            }
            catch (Exception ex)
            {
                ScanInfo.Text = "选择图片失败：" + ex.Message;
            }
        }

        /// <summary>选图：先自动保存上一张的标注（参考 labelimage 切图即存），再显示新图并清空拖框</summary>
        private void AnnotImgList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AutoSaveAnnot();   // 切图自动保存上一张（有标注才写）
            _annotSel = -1;
            _annotEditMode = AnnotEditMode.None;
            _annotCurImg = AnnotImgList.SelectedItem as string ?? "";
            AnnotCurImgName.Text = string.IsNullOrEmpty(_annotCurImg) ? "（未选图）" : Path.GetFileName(_annotCurImg);
            _annotDraftPx = default;
            _annotFaceDrafts.Clear();
            AnnotStaticLayer.Children.Clear();
            AnnotDynLayer.Children.Clear();
            AnnotDraftInfo.Text = "未框选";
            AnnotFaceStat.Text = "预填 0 框";
            if (string.IsNullOrEmpty(_annotCurImg)) { AnnotImg.Source = null; AnnotList.ItemsSource = null; return; }
            try
            {
                var src = new BitmapImage();
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.UriSource = new Uri(Path.Combine(TrainDirBox.Text.Trim(), _annotCurImg));
                src.EndInit();
                AnnotImg.Source = src;
                RefreshAnnotList();   // 显示该图已有标注框（来自内存/标注.json）
                RedrawOverlay();
                // 人脸辅助勾选时：切图自动检测人脸并预填框
                if (_annotFaceAuto) DetectFacesAndPrefill();
            }
            catch (Exception ex)
            {
                AnnotImg.Source = null;
                AnnotDraftInfo.Text = "图片读取失败：" + ex.Message;
            }
        }

        /// <summary>
        /// Overlay 按下（参考 labelimage 标注交互）：
        /// 1) 命中选中框角点 → 缩放；2) 命中已入库框 → 选中+移动；3) 空白 → 拖新框。
        /// </summary>
        private void AnnotOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (AnnotImg.Source == null) return;
            AnnotOverlay.Focus();
            var pos = e.GetPosition(AnnotOverlay);
            var items = CurAnnotItems();
            // 1) 选中框的四角手柄 → 缩放
            if (_annotSel >= 0 && _annotSel < items.Count && HitResizeHandle(pos, out int corner))
            {
                _annotEditMode = AnnotEditMode.Resize;
                _annotResizeCorner = corner;
                _annotEditStart = pos;
                _annotEditStartRect = ItemRect(items[_annotSel]);
                AnnotOverlay.CaptureMouse();
                e.Handled = true;
                return;
            }
            // 2) 命中某个已入库框 → 选中并准备移动
            int hit = HitAnnotItem(pos);
            if (hit >= 0)
            {
                _annotSel = hit;
                _annotEditMode = AnnotEditMode.Move;
                _annotEditStart = pos;
                _annotEditStartRect = ItemRect(items[hit]);
                _annotDraftPx = default;   // 开始移动选中框：未落定草稿不再显示
                RefreshAnnotList();
                RedrawOverlay();
                AnnotOverlay.CaptureMouse();
                e.Handled = true;
                return;
            }
            // 3) 空白处 → 拖新框（同时取消选中）
            _annotSel = -1;
            RefreshAnnotList();
            _annotEditMode = AnnotEditMode.Draw;
            _annotDragging = true;
            _annotDragStart = pos;
            _annotDragEnd = pos;
            _annotDraftPx = default;
            RedrawOverlay();
            AnnotOverlay.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>Overlay 拖动：按当前编辑状态更新（拖新框 / 移动 / 缩放）</summary>
        private void AnnotOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(AnnotOverlay);
            // 性能：MouseMove 每帧只更新动态层（1-2 个元素），不重建静态层（2n 个元素）
            if (_annotEditMode == AnnotEditMode.Draw && _annotDragging)
            {
                _annotDragEnd = pos;
                UpdateDynLayer();
                e.Handled = true;
                return;
            }
            if (_annotEditMode == AnnotEditMode.Move && _annotSel >= 0)
            {
                var items = CurAnnotItems();
                if (_annotSel >= items.Count) { _annotSel = -1; return; }
                var r = _annotEditStartRect;
                if (DisplayDeltaToPixel(pos.X - _annotEditStart.X, pos.Y - _annotEditStart.Y, out int dx, out int dy))
                {
                    var nr = ClampRect(new OpenCvSharp.Rect(r.X + dx, r.Y + dy, r.Width, r.Height));
                    UpdateAnnotItem(_annotSel, nr);
                    UpdateDynLayer();
                }
                e.Handled = true;
                return;
            }
            if (_annotEditMode == AnnotEditMode.Resize && _annotSel >= 0)
            {
                var items = CurAnnotItems();
                if (_annotSel >= items.Count) { _annotSel = -1; return; }
                var r = _annotEditStartRect;
                if (DisplayDeltaToPixel(pos.X - _annotEditStart.X, pos.Y - _annotEditStart.Y, out int dx, out int dy))
                {
                    var nr = ResizeRect(r, dx, dy, _annotResizeCorner);
                    if (nr.Width >= 4 && nr.Height >= 4)
                    {
                        UpdateAnnotItem(_annotSel, nr);
                        UpdateDynLayer();
                    }
                }
                e.Handled = true;
                return;
            }
        }

        /// <summary>Overlay 松手：按编辑状态收尾（拖框 → 生成草稿；移动/缩放 → 落定）</summary>
        private void AnnotOverlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_annotEditMode == AnnotEditMode.Draw && _annotDragging)
            {
                _annotEditMode = AnnotEditMode.None;
                _annotDragging = false;
                AnnotOverlay.ReleaseMouseCapture();
                _annotDragEnd = e.GetPosition(AnnotOverlay);
                RedrawOverlay();
                if (DisplayToPixel(_annotDragStart, out int x0, out int y0)
                    && DisplayToPixel(_annotDragEnd, out int x1, out int y1))
                {
                    int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
                    int w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
                    if (w >= 4 && h >= 4)
                    {
                        _annotDraftPx = new OpenCvSharp.Rect(x, y, w, h);
                        AnnotDraftInfo.Text = string.Format("已框选 ({0},{1}) {2}x{3}px，选标签后点「添加标注」", x, y, w, h);
                    }
                    else
                    {
                        _annotDraftPx = default;
                        AnnotDraftInfo.Text = "框太小（至少 4x4px），重新拖";
                    }
                }
                else
                {
                    _annotDraftPx = default;
                    AnnotDraftInfo.Text = "框超出了图片范围，重新拖";
                }
                e.Handled = true;
                return;
            }
            if (_annotEditMode == AnnotEditMode.Move || _annotEditMode == AnnotEditMode.Resize)
            {
                _annotEditMode = AnnotEditMode.None;
                AnnotOverlay.ReleaseMouseCapture();
                RefreshAnnotList();
                RedrawOverlay();
                AnnotDraftInfo.Text = "已更新框位置/大小（可拖角点微调，Delete 或「删除选中」移除）";
                e.Handled = true;
            }
        }

        /// <summary>Overlay 键盘：Delete 删除选中框</summary>
        private void AnnotOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _annotSel >= 0)
            {
                DeleteSelectedAnnot();
                e.Handled = true;
            }
        }

        /// <summary>黄框矩形（显示坐标）</summary>
        private static System.Windows.Shapes.Rectangle MakeAnnotRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            var r = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, Math.Abs(b.X - a.X)),
                Height = Math.Max(1, Math.Abs(b.Y - a.Y)),
                Stroke = System.Windows.Media.Brushes.Yellow,
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            return r;
        }

        /// <summary>当前视图变换：适应窗口=Uniform 居中；1:1=左上角对齐（与 BtnViewFit 联动）</summary>
        private (double Scale, double Ox, double Oy) ViewTransform(BitmapSource bs)
        {
            double ow = AnnotOverlay.ActualWidth, oh = AnnotOverlay.ActualHeight;
            if (BtnViewFit.IsChecked == true)
            {
                double scale = Math.Min(ow / bs.PixelWidth, oh / bs.PixelHeight);
                return (scale, (ow - bs.PixelWidth * scale) / 2.0, (oh - bs.PixelHeight * scale) / 2.0);
            }
            return (1.0, 0.0, 0.0);
        }

        /// <summary>Overlay 显示坐标 → 图像像素坐标（适应当前视图：Uniform 或 1:1）</summary>
        private bool DisplayToPixel(Point d, out int px, out int py)
        {
            px = py = 0;
            if (AnnotImg.Source is not BitmapSource bs || bs.PixelWidth <= 0 || bs.PixelHeight <= 0) return false;
            if (AnnotOverlay.ActualWidth <= 1 || AnnotOverlay.ActualHeight <= 1) return false;
            var (scale, ox, oy) = ViewTransform(bs);
            if (scale <= 0) return false;
            double fx = (d.X - ox) / scale, fy = (d.Y - oy) / scale;
            if (fx < -2 || fy < -2 || fx > bs.PixelWidth + 2 || fy > bs.PixelHeight + 2) return false;
            px = (int)Math.Round(fx); py = (int)Math.Round(fy);
            return true;
        }

        /// <summary>图像像素坐标 → Overlay 显示坐标（与 DisplayToPixel 相反，画已入库框用）</summary>
        private bool PixelToDisplay(int px, int py, out double dx, out double dy)
        {
            dx = dy = 0;
            if (AnnotImg.Source is not BitmapSource bs || bs.PixelWidth <= 0 || bs.PixelHeight <= 0) return false;
            if (AnnotOverlay.ActualWidth <= 1 || AnnotOverlay.ActualHeight <= 1) return false;
            var (scale, ox, oy) = ViewTransform(bs);
            dx = ox + px * scale; dy = oy + py * scale;
            return true;
        }

        /// <summary>显示坐标位移 → 像素位移（按当前缩放换算）</summary>
        private bool DisplayDeltaToPixel(double dxD, double dyD, out int dx, out int dy)
        {
            dx = dy = 0;
            if (AnnotImg.Source is not BitmapSource bs || bs.PixelWidth <= 0) return false;
            var (scale, _, _) = ViewTransform(bs);
            if (scale <= 0) return false;
            dx = (int)Math.Round(dxD / scale); dy = (int)Math.Round(dyD / scale);
            return true;
        }

        /// <summary>当前图已入库标注框（与 _annotSel 同序）</summary>
        private List<AnnotItem> CurAnnotItems() => _annotItems.Where(a => a.Image == _annotCurImg).ToList();

        /// <summary>AnnotItem → 像素矩形</summary>
        private static OpenCvSharp.Rect ItemRect(AnnotItem a) => new(a.X, a.Y, a.W, a.H);

        /// <summary>更新当前图第 idx 个标注框的矩形（保持 Image/Label 不变）</summary>
        private void UpdateAnnotItem(int idx, OpenCvSharp.Rect r)
        {
            var items = CurAnnotItems();
            if (idx < 0 || idx >= items.Count) return;
            var a = items[idx];
            int at = _annotItems.IndexOf(a);
            if (at < 0) return;
            _annotItems[at] = new AnnotItem(a.Image, r.X, r.Y, r.Width, r.Height, a.Label);
        }

        /// <summary>把矩形限制在图像范围内</summary>
        private OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect r)
        {
            if (AnnotImg.Source is not BitmapSource bs) return r;
            int pw = bs.PixelWidth, ph = bs.PixelHeight;
            int x = Math.Clamp(r.X, 0, Math.Max(0, pw - 1)), y = Math.Clamp(r.Y, 0, Math.Max(0, ph - 1));
            int w = Math.Min(r.Width, pw - x), h = Math.Min(r.Height, ph - y);
            return new OpenCvSharp.Rect(x, y, Math.Max(0, w), Math.Max(0, h));
        }

        /// <summary>按角点缩放矩形：0=左上 1=右上 2=左下 3=右下（拖拽位移 = 对角点位移）</summary>
        private static OpenCvSharp.Rect ResizeRect(OpenCvSharp.Rect r, int dx, int dy, int corner)
        {
            int x0 = r.X, y0 = r.Y, x1 = r.X + r.Width, y1 = r.Y + r.Height;
            switch (corner)
            {
                case 0: x0 += dx; y0 += dy; break;
                case 1: x1 += dx; y0 += dy; break;
                case 2: x0 += dx; y1 += dy; break;
                case 3: x1 += dx; y1 += dy; break;
            }
            int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
            return new OpenCvSharp.Rect(x, y, Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        }

        /// <summary>命中测试：显示坐标点落在哪个已入库框内（返回当前图下标；-1=未命中）</summary>
        private int HitAnnotItem(Point pos)
        {
            if (!DisplayToPixel(pos, out int px, out int py)) return -1;
            var items = CurAnnotItems();
            for (int i = 0; i < items.Count; i++)
            {
                var r = ItemRect(items[i]);
                if (px >= r.X && px <= r.X + r.Width && py >= r.Y && py <= r.Y + r.Height) return i;
            }
            return -1;
        }

        /// <summary>命中测试：显示坐标点是否落在选中框的角点手柄（8px）上</summary>
        private bool HitResizeHandle(Point pos, out int corner)
        {
            corner = -1;
            var items = CurAnnotItems();
            if (_annotSel < 0 || _annotSel >= items.Count) return false;
            var r = ItemRect(items[_annotSel]);
            if (!PixelToDisplay(r.X, r.Y, out double x0, out double y0)
                || !PixelToDisplay(r.X + r.Width, r.Y + r.Height, out double x1, out double y1)) return false;
            double h = 8;
            (double X, double Y)[] corners = [(x0, y0), (x1, y0), (x0, y1), (x1, y1)];
            for (int i = 0; i < corners.Length; i++)
                if (Math.Abs(pos.X - corners[i].X) <= h && Math.Abs(pos.Y - corners[i].Y) <= h) { corner = i; return true; }
            return false;
        }

        /// <summary>给选中框画四角缩放手柄（实心方块）</summary>
        private void AddResizeHandles(AnnotItem a)
        {
            var r = ItemRect(a);
            if (!PixelToDisplay(r.X, r.Y, out double x0, out double y0)
                || !PixelToDisplay(r.X + r.Width, r.Y + r.Height, out double x1, out double y1)) return;
            var handle = (double x, double y) => new System.Windows.Shapes.Rectangle
            {
                Width = 8, Height = 8,
                Fill = FindResource("Accent") as Brush ?? Brushes.OrangeRed,
                Stroke = Brushes.White, StrokeThickness = 1,
            };
            foreach (var (cx, cy) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
            {
                var h = handle(cx, cy);
                Canvas.SetLeft(h, cx - 4); Canvas.SetTop(h, cy - 4);
                AnnotOverlay.Children.Add(h);
            }
        }

        /// <summary>左上角标签文字（半透明黑底白字，参考 labelimage 框上标签）</summary>
        private System.Windows.Controls.Border MakeAnnotLabel(string label, OpenCvSharp.Rect r)
        {
            if (!PixelToDisplay(r.X, r.Y, out double x0, out double y0)) return null;
            var tb = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(2, 0, 2, 0) };
            var b = new System.Windows.Controls.Border
            {
                Child = tb,
                Background = new SolidColorBrush(Color.FromArgb(170, 20, 20, 20)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 0, 2),
            };
            Canvas.SetLeft(b, x0); Canvas.SetTop(b, Math.Max(0, y0 - 18));
            return b;
        }

        /// <summary>标签加入标签池（去重，排序稳定）</summary>
        private void AddLabelToPool(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return;
            if (!_annotLabelPool.Contains(label)) _annotLabelPool.Add(label);
            RefreshLabelPool();
        }

        /// <summary>刷新标签池下拉（历史标签；无标签时显示空）</summary>
        private void RefreshLabelPool()
        {
            if (AnnotLabelBox == null) return;
            var cur = AnnotLabelBox.Text ?? "";
            var old = AnnotLabelBox.SelectedItem as string;
            AnnotLabelBox.ItemsSource = _annotLabelPool.ToList();
            if (_annotLabelPool.Contains(cur)) AnnotLabelBox.Text = cur;
            else if (old != null && _annotLabelPool.Contains(old)) AnnotLabelBox.SelectedItem = old;
        }

        /// <summary>上一张图片（缩略图条联动）</summary>
        private void BtnPrevAnnot_Click(object sender, RoutedEventArgs e)
        {
            if (AnnotImgList.SelectedIndex > 0) AnnotImgList.SelectedIndex--;
        }

        /// <summary>下一张图片（缩略图条联动）</summary>
        private void BtnNextAnnot_Click(object sender, RoutedEventArgs e)
        {
            int n = AnnotImgList.Items.Count;
            if (n > 0 && AnnotImgList.SelectedIndex < n - 1) AnnotImgList.SelectedIndex++;
        }

        /// <summary>视图切换：适应窗口 ↔ 1:1 实际像素（Image 对齐与坐标换算联动）</summary>
        private void BtnViewFit_Changed(object sender, RoutedEventArgs e)
        {
            // XAML 加载期 IsChecked="True" 会先于 AnnotImg 创建触发 Checked，必须跳过
            if (AnnotImg == null) return;
            bool fit = BtnViewFit.IsChecked == true;
            AnnotImg.Stretch = fit ? System.Windows.Media.Stretch.Uniform : System.Windows.Media.Stretch.None;
            AnnotImg.HorizontalAlignment = fit ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            AnnotImg.VerticalAlignment = fit ? VerticalAlignment.Center : VerticalAlignment.Top;
            _annotSel = -1;   // 视图变化后命中坐标失效，取消选中
            RefreshAnnotList();
            RedrawOverlay();
        }

        /// <summary>标注列表选中 → 图上选中联动（单击列表项高亮图上框）</summary>
        private void AnnotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAnnotSync) return;
            _annotSel = AnnotList.SelectedIndex;
            RedrawOverlay();
        }

        /// <summary>删除当前图选中的标注框（列表/图上/键盘 Delete 共用）</summary>
        private void DeleteSelectedAnnot()
        {
            if (_annotSel < 0) { AnnotDraftInfo.Text = "先单击选中一个框再删除"; return; }
            var items = CurAnnotItems();
            if (_annotSel >= items.Count) { _annotSel = -1; return; }
            var a = items[_annotSel];
            _annotItems.Remove(a);
            _annotSel = -1;
            RefreshAnnotList();
            RedrawOverlay();
            AnnotDraftInfo.Text = "已删除标注：" + a.Label;
        }

        /// <summary>从 标注.json 载入全部标注到内存（刷新列表/启动时调用；无文件或解析失败保持空）</summary>
        private void LoadAnnotItems(string dir)
        {
            _annotItems.Clear();
            string annPath = Path.Combine(dir, "标注.json");
            if (!File.Exists(annPath)) return;
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(annPath));
                if (root["items"] is not Newtonsoft.Json.Linq.JArray arr) return;
                foreach (var t in arr)
                {
                    var img = t["image"]?.ToString() ?? "";
                    int x = (int)(t["x"] ?? 0), y = (int)(t["y"] ?? 0);
                    int w = (int)(t["w"] ?? 0), h = (int)(t["h"] ?? 0);
                    var label = t["label"]?.ToString() ?? "";
                    if (img.Length > 0 && w > 0 && h > 0)
                    {
                        _annotItems.Add(new AnnotItem(img, x, y, w, h, label));
                        if (label.Length > 0 && !_annotLabelPool.Contains(label)) _annotLabelPool.Add(label);
                    }
                }
            }
            catch { /* 标注文件损坏时忽略，保持空态可重新标注 */ }
        }

        /// <summary>自动保存（切图时调用）：当前图有标注才写 标注.json（参考 labelimage 切图即存）</summary>
        private void AutoSaveAnnot()
        {
            try
            {
                if (string.IsNullOrEmpty(_annotCurImg)) return;
                var dir = TrainDirBox.Text.Trim();
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                if (!_annotItems.Any(a => a.Image == _annotCurImg)) return;
                SaveAnnotNow(dir, quiet: true);
            }
            catch { /* 自动保存失败不打断切图 */ }
        }

        /// <summary>添加标注：当前框 + 标签名 入列表</summary>
        private void BtnAddAnnot_Click(object sender, RoutedEventArgs e)
        {
            if (_annotDraftPx.Width < 4 || _annotDraftPx.Height < 4) { AnnotDraftInfo.Text = "先在图上拖一个框"; return; }
            string label = AnnotLabelBox.Text.Trim();
            if (string.IsNullOrEmpty(label)) { AnnotDraftInfo.Text = "先填标签名（如 张三 / 划痕）"; return; }
            if (string.IsNullOrEmpty(_annotCurImg)) { AnnotDraftInfo.Text = "先选一张图"; return; }
            _annotItems.Add(new AnnotItem(_annotCurImg, _annotDraftPx.X, _annotDraftPx.Y, _annotDraftPx.Width, _annotDraftPx.Height, label));
            AddLabelToPool(label);
            RefreshAnnotList();
            _annotDraftPx = default;
            _annotSel = CurAnnotItems().Count - 1;   // 选中刚添加的框（图上高亮+可编辑）
            RedrawOverlay();
            AnnotDraftInfo.Text = "已添加 1 条：" + label;
        }

        /// <summary>刷新标注列表显示（类别+坐标；当前图统计行同步更新；标签池同步下拉）</summary>
        private void RefreshAnnotList()
        {
            var items = CurAnnotItems();
            // 重建 ItemsSource 会触发 SelectionChanged 把选中清空：整段抑制，再恢复图上选中
            _suppressAnnotSync = true;
            AnnotList.ItemsSource = items
                .Select(a => string.Format("{0}  ({1},{2} {3}x{4})", a.Label, a.X, a.Y, a.W, a.H))
                .ToList();
            int want = Math.Min(_annotSel, items.Count - 1);
            if (_annotSel >= 0 && want >= 0 && AnnotList.SelectedIndex != want)
                AnnotList.SelectedIndex = want;
            _suppressAnnotSync = false;
            AnnotStat.Text = $"当前图 {items.Count} 框 · 共 {_annotItems.Count} 框";
            RefreshLabelPool();
        }

        /// <summary>删除选中的标注条目（列表选中或图上选中均可）</summary>
        private void BtnDelAnnot_Click(object sender, RoutedEventArgs e)
            => DeleteSelectedAnnot();

        /// <summary>清除全部标注：清空列表、当前框与草稿（加错了想重新标，一键取消）</summary>
        private void BtnClearAnnot_Click(object sender, RoutedEventArgs e)
        {
            if (_annotItems.Count == 0) { AnnotDraftInfo.Text = "还没有标注条目"; return; }
            _annotItems.Clear();
            _annotDraftPx = default;
            _annotFaceDrafts.Clear();
            _annotSel = -1;
            AnnotFaceStat.Text = "预填 0 框";
            RedrawOverlay();
            RefreshAnnotList();
            AnnotDraftInfo.Text = "已清除全部标注（可重新框选标注）";
        }

        /// <summary>检测当前图所有人脸并预填标注框（Haar 级联；多人脸各一框，Overlay 显示半透明黄框）</summary>
        private void DetectFacesAndPrefill()
        {
            _annotFaceDrafts.Clear();
            if (string.IsNullOrEmpty(_annotCurImg)) { AnnotDraftInfo.Text = "先选一张图片"; return; }
            string path = Path.Combine(TrainDirBox.Text.Trim(), _annotCurImg);
            using var mat = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Color);
            if (mat == null || mat.Empty()) { AnnotDraftInfo.Text = "图片读取失败，无法检测人脸"; return; }
            var faces = VisionToolDemo.Vision.FaceDetector.Detect(mat);
            _annotFaceDrafts.AddRange(faces);
            RefreshAnnotFace();
            AnnotDraftInfo.Text = faces.Length > 0
                ? string.Format("已检测到 {0} 张人脸并预填（黄框），点「添加全部预填」入库（标签=当前标签框）", faces.Length)
                : "未检测到人脸（模型文件缺失或图中无人脸；可关闭人脸辅助手动拖框）";
        }

        /// <summary>刷新人脸预填框显示与统计</summary>
        private void RefreshAnnotFace()
        {
            AnnotFaceStat.Text = $"预填 {_annotFaceDrafts.Count} 框";
            RedrawOverlay();
        }

        /// <summary>人脸辅助勾选状态变化（绑定 FaceAuto → 本方法）：勾选且当前有图则立即检测预填</summary>
        private void FaceAutoChanged()
        {
            _annotFaceAuto = Vm.FaceAuto;
            if (_annotFaceAuto && !string.IsNullOrEmpty(_annotCurImg) && AnnotImg.Source != null)
                DetectFacesAndPrefill();
        }

        /// <summary>「检测人脸预填框」：手动检测当前图人脸并预填</summary>
        private void BtnDetectFaces_Click(object sender, RoutedEventArgs e)
            => DetectFacesAndPrefill();

        /// <summary>「添加全部预填」：把全部人脸预填框按当前标签入库</summary>
        private void BtnAddFaceDrafts_Click(object sender, RoutedEventArgs e)
        {
            if (_annotFaceDrafts.Count == 0) { AnnotDraftInfo.Text = "还没有预填框，先点「检测人脸预填框」"; return; }
            string label = AnnotLabelBox.Text.Trim();
            if (string.IsNullOrEmpty(label)) { AnnotDraftInfo.Text = "先填标签名（如 张三 / 划痕）"; return; }
            if (string.IsNullOrEmpty(_annotCurImg)) { AnnotDraftInfo.Text = "先选一张图片"; return; }
            int n = _annotFaceDrafts.Count;
            foreach (var r in _annotFaceDrafts)
                _annotItems.Add(new AnnotItem(_annotCurImg, r.X, r.Y, r.Width, r.Height, label));
            AddLabelToPool(label);
            _annotFaceDrafts.Clear();
            RefreshAnnotFace();
            RefreshAnnotList();
            AnnotDraftInfo.Text = string.Format("已入库 {0} 条人脸标注（标签={1}），可继续拖框补充", n, label);
        }

        /// <summary>重绘标注覆盖层：人脸预填框（半透明黄）+ 当前拖框（黄实线）</summary>
        /// <summary>
        /// 全量重绘标注覆盖层（性能：静态层=已入库框/标签/人脸预填，仅状态变化重建；
        /// 动态层=拖拽中的框/选中高亮/手柄，MouseMove 每帧只更新动态层）。
        /// </summary>
        private void RedrawOverlay()
        {
            RebuildStaticLayer();
            UpdateDynLayer();
        }

        /// <summary>重建静态层：已入库框（黄实线+左上角标签）+ 人脸预填框（半透明）。状态变化时调用。</summary>
        private void RebuildStaticLayer()
        {
            AnnotStaticLayer.Children.Clear();
            var items = CurAnnotItems();
            for (int i = 0; i < items.Count; i++)
            {
                // 选中框完全由动态层画（高亮+手柄）：静态层跳过，避免拖拽移动/缩放时旧位置黄框重影
                if (i == _annotSel) continue;
                var a = items[i];
                var r = ItemRect(a);
                AnnotStaticLayer.Children.Add(MakeAnnotRectPx(r, 0.85));
                var tag = MakeAnnotLabel(a.Label, r);
                if (tag != null) AnnotStaticLayer.Children.Add(tag);
            }
            foreach (var r in _annotFaceDrafts)
                AnnotStaticLayer.Children.Add(MakeAnnotRectPx(r, 0.45));
        }

        /// <summary>更新动态层：拖拽中的框/草稿 + 选中框高亮与四角手柄。MouseMove 每帧只调这个（1-2 个元素）。</summary>
        private void UpdateDynLayer()
        {
            AnnotDynLayer.Children.Clear();
            var items = CurAnnotItems();
            bool hasSel = _annotSel >= 0 && _annotSel < items.Count;
            // 1) 拖拽中的选中框（Move/Resize）：实时位置高亮框 + 标签 + 四角手柄（优先）
            if ((_annotEditMode == AnnotEditMode.Move || _annotEditMode == AnnotEditMode.Resize) && hasSel)
            {
                var r = ItemRect(items[_annotSel]);
                var hl = MakeAnnotRectPx(r, 1.0);
                hl.Stroke = FindResource("Accent") as Brush ?? Brushes.OrangeRed;
                hl.StrokeThickness = 2.2;
                AnnotDynLayer.Children.Add(hl);
                var tag = MakeAnnotLabel(items[_annotSel].Label, r);
                if (tag != null) AnnotDynLayer.Children.Add(tag);
                AddResizeHandles(items[_annotSel]);
                return;
            }
            // 2) 拖框草稿（显示坐标，实时跟随）
            if (_annotEditMode == AnnotEditMode.Draw && _annotDragging)
            {
                AnnotDynLayer.Children.Add(MakeAnnotRect(_annotDragStart, _annotDragEnd));
                return;
            }
            // 3) 已落定的草稿（等待添加）
            if (_annotDraftPx.Width > 0 && _annotDraftPx.Height > 0)
            {
                AnnotDynLayer.Children.Add(MakeAnnotRectPx(_annotDraftPx, 0.9));
                return;
            }
            // 4) 空闲但选中了框：静态层已跳过该框，动态层补高亮框 + 标签 + 手柄
            if (hasSel && _annotEditMode == AnnotEditMode.None)
            {
                var r = ItemRect(items[_annotSel]);
                var hl = MakeAnnotRectPx(r, 1.0);
                hl.Stroke = FindResource("Accent") as Brush ?? Brushes.OrangeRed;
                hl.StrokeThickness = 2.2;
                AnnotDynLayer.Children.Add(hl);
                var tag = MakeAnnotLabel(items[_annotSel].Label, r);
                if (tag != null) AnnotDynLayer.Children.Add(tag);
                AddResizeHandles(items[_annotSel]);
            }
        }

        /// <summary>像素矩形 → 覆盖层黄框（显示坐标，带透明度，预填框半透明可区分）</summary>
        private System.Windows.Shapes.Rectangle MakeAnnotRectPx(OpenCvSharp.Rect r, double opacity)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Stroke = System.Windows.Media.Brushes.Yellow,
                StrokeThickness = 1.5,
                Opacity = opacity,
            };
            if (PixelToDisplay(r.X, r.Y, out double x0, out double y0)
                && PixelToDisplay(r.X + r.Width, r.Y + r.Height, out double x1, out double y1))
            {
                rect.Width = Math.Max(1, x1 - x0);
                rect.Height = Math.Max(1, y1 - y0);
                Canvas.SetLeft(rect, x0);
                Canvas.SetTop(rect, y0);
            }
            return rect;
        }

        /// <summary>保存标注到 标注.json（训练目录内；覆盖旧文件前保留 .bak）</summary>
        private void BtnSaveAnnot_Click(object sender, RoutedEventArgs e)
        {
            var dir = TrainDirBox.Text.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { ScanInfo.Text = "目录不存在：" + dir; return; }
            if (_annotItems.Count == 0) { ScanInfo.Text = "还没有标注条目，先添加再保存"; return; }
            SaveAnnotNow(dir, quiet: false);
        }

        /// <summary>写 标注.json（手动保存 / 切图自动保存共用）；quiet=true 时不刷新 ScanInfo</summary>
        private void SaveAnnotNow(string dir, bool quiet)
        {
            if (_annotItems.Count == 0) return;
            string annPath = Path.Combine(dir, "标注.json");
            if (File.Exists(annPath)) File.Copy(annPath, annPath + ".bak", true);
            var j = new Newtonsoft.Json.Linq.JObject
            {
                ["format"] = "vision-annotations-v1",
                ["items"] = new Newtonsoft.Json.Linq.JArray(_annotItems.Select(a =>
                    new Newtonsoft.Json.Linq.JObject
                    {
                        ["image"] = a.Image, ["x"] = a.X, ["y"] = a.Y,
                        ["w"] = a.W, ["h"] = a.H, ["label"] = a.Label,
                    })),
            };
            File.WriteAllText(annPath, j.ToString(Newtonsoft.Json.Formatting.Indented));
            // 保存标注后即进入标注模式（后续「开始训练」按 标注.json 加载），避免用户标完却按文件夹模式训练
            _useAnnotated = true;
            if (!quiet) ScanInfo.Text = $"已保存 {_annotItems.Count} 条标注到 标注.json，已切换到标注模式（直接点「开始训练」即可）";
        }

        /// <summary>从标注训练集加载：训练器按标注框+标签构建样本，可直接训练</summary>
        private void BtnLoadAnnot_Click(object sender, RoutedEventArgs e)
        {
            var dir = TrainDirBox.Text.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { ScanInfo.Text = "目录不存在：" + dir; return; }
            try
            {
                var trainer = new DeepTrainer();
                var stats = trainer.LoadAnnotated(dir);
                _useAnnotated = true;
                ScanInfo.Text = "标注训练集：标签 " + stats.Count + " 个，样本 " + stats.Values.Sum() + " 个："
                    + string.Join("，", stats.Select(kv => kv.Key + "×" + kv.Value))
                    + "\n识别时：视觉页「深度学习推理」任务类型选 2=滑窗多人/多目标识别";
            }
            catch (Exception ex)
            {
                ScanInfo.Text = "加载标注训练集失败：" + ex.Message;
            }
        }

        // ============================== 模型检查点（继续训练） ==============================

        /// <summary>保存当前模型为 .vtmodel（之后可「加载模型继续训练」，不用每次从头训）</summary>
        private void BtnSaveModel_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;
            var dlg = new SaveFileDialog
            {
                Title = "保存模型检查点（.vtmodel）",
                Filter = "模型检查点|*.vtmodel",
                FileName = "model.vtmodel",
                InitialDirectory = Path.Combine(AppContext.BaseDirectory, "train_output"),
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                DeepTrainer.SaveModel(_model, dlg.FileName);
                AppendLog("[保存成功] " + dlg.FileName);
                TrainStatus.Text = "模型已保存：" + dlg.FileName + "（以后可加载继续训练）";
            }
            catch (Exception ex)
            {
                AppendLog("[错误] 保存失败：" + ex.Message);
            }
        }

        /// <summary>加载 .vtmodel 作继续训练种子：显示已训轮数，点「开始训练」即接着跑</summary>
        private void BtnLoadModel_Click(object sender, RoutedEventArgs e)
        {
            if (_training) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择模型检查点（.vtmodel，本软件训练页保存的）",
                Filter = "模型检查点|*.vtmodel|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var m = DeepTrainer.LoadModel(dlg.FileName);
                _model = m;
                Vm.IsModelReady = true;
                if (_trainer != null && _trainer.Labels.Count > 0 && _trainer.Labels.Count != m.Labels.Count)
                    TrainStatus.Text = "注意：加载模型的类别数与当前训练集不一致，点「开始训练」会从头训";
                else
                    TrainStatus.Text = string.Format("已加载模型（{0} 轮，acc {1:P0}）：点「开始训练」继续训练，不重头开始。",
                        m.FinalEpoch, m.TrainAcc);
                AppendLog("[加载模型] " + dlg.FileName + "（已训 " + m.FinalEpoch + " 轮，类别："
                    + string.Join("/", m.Labels) + "）");
            }
            catch (Exception ex)
            {
                AppendLog("[错误] 加载模型失败：" + ex.Message);
                TrainStatus.Text = "加载模型失败：" + ex.Message;
            }
        }

        // ============================== 工具 ==============================

        /// <summary>切到 UI 线程执行（训练日志从后台线程回流）</summary>
        private void Ui(Action a) => Dispatcher.BeginInvoke(a, DispatcherPriority.Background);

        /// <summary>追加一行日志（自动滚动到底部）</summary>
        /// <summary>构建混淆矩阵表格：corner + 类别表头 + NxN 色块格（对角绿/少量混淆黄/明显混淆橙红，色深按计数归一化）。
        /// 结构格（corner/表头/行标签/零值格）跟随主题背景与文字色；数据格保留语义色（对角绿/混淆黄/橙红）+ 白字。</summary>
        private void BuildConfusion(int[][] conf, List<string> labels)
        {
            CmGrid.Children.Clear();
            CmGrid.RowDefinitions.Clear();
            CmGrid.ColumnDefinitions.Clear();
            int n = conf?.Length ?? 0;
            // 表头行与首列：corner + 类别名
            CmGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            for (int c = 0; c < Math.Max(1, n); c++)
                CmGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r <= n; r++)
                CmGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 结构格背景/文字跟随当前主题（亮色主题=浅底黑字，深色主题=深底白字）
            Brush panelBg = (Brush)FindResource("PanelBg") ?? Brushes.Transparent;
            Brush fg = (Brush)FindResource("Fg") ?? Brushes.Black;

            void Cell(int row, int col, string text, Brush bg, Brush fgColor, bool diag = false)
            {
                var b = new Border
                {
                    Background = bg,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = 12,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = fgColor,
                        Margin = new Thickness(0, 6, 0, 6),
                        TextWrapping = TextWrapping.Wrap,          // 长标签/空态提示自动换行，防横向裁剪
                        TextAlignment = TextAlignment.Center,
                    },
                };
                if (diag) b.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 192, 138));
                if (diag) b.BorderThickness = new Thickness(2);
                Grid.SetRow(b, row);
                Grid.SetColumn(b, col);
                CmGrid.Children.Add(b);
            }

            // 空态提示
            if (conf == null || labels == null || n == 0)
            {
                Cell(0, 0, "（尚未训练，训练完成后自动生成各类别混淆统计）", panelBg, fg);
                return;
            }

            // corner + 类别表头 + 行标签：结构格跟随主题
            Cell(0, 0, "真实 预测", panelBg, fg);
            for (int c = 0; c < n; c++)
                Cell(0, c + 1, labels[c], panelBg, fg);
            for (int r = 0; r < n; r++)
            {
                Cell(r + 1, 0, labels[r], panelBg, fg);
                int total = 0;
                for (int c = 0; c < n; c++) total += conf[r][c];
                for (int c = 0; c < n; c++)
                {
                    int v = conf[r][c];
                    Brush bg;
                    if (v == 0) { bg = panelBg; Cell(r + 1, c + 1, "0", bg, fg); continue; }
                    else if (r == c)
                    {
                        float k = Math.Clamp(0.30f + 0.60f * v / Math.Max(1, total), 0.30f, 1f);
                        bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(70 * k + 40), (byte)(190 * k + 30), (byte)(130 * k + 40)));
                    }
                    else if (v <= 2)
                    {
                        float k = 0.30f + 0.30f * v;
                        bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(210 * k + 40), (byte)(150 * k + 40), (byte)(50 * k + 30)));
                    }
                    else
                    {
                        float k = Math.Min(1f, 0.45f + 0.30f * v / 5f);
                        bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(230 * k + 20), (byte)(80 * k + 30), (byte)(60 * k + 30)));
                    }
                    Cell(r + 1, c + 1, v.ToString(), bg, Brushes.White, r == c);
                }
            }
        }


        /// <summary>在 Canvas 上画训练曲线：归一化折线 + 上界/下界/末值刻度文字。</summary>
        private static void DrawChart(Canvas cv, List<float> hist, Color line)
        {
            cv.Children.Clear();
            if (hist.Count == 0) return;
            float w = (float)cv.ActualWidth, h = (float)cv.ActualHeight;
            if (w < 20 || h < 20) return;
            float mn = hist.Min(), mx = hist.Max();
            float span = Math.Max(1e-6f, mx - mn);
            var pts = new PointCollection();
            for (int i = 0; i < hist.Count; i++)
            {
                double x = 8 + i * (w - 16) / Math.Max(1, hist.Count - 1);
                double y = h - 14 - (hist[i] - mn) / span * (h - 26);
                pts.Add(new Point(x, y));
            }
            cv.Children.Add(new System.Windows.Shapes.Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(line),
                StrokeThickness = 2,
            });
            void Txt(string text, double x, double y)
            {
                var tb = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.Gray };
                Canvas.SetLeft(tb, x);
                Canvas.SetTop(tb, y);
                cv.Children.Add(tb);
            }
            Txt(mx.ToString(mx >= 10 ? "0.0" : "0.000"), 2, 0);
            Txt(mn.ToString(mn >= 10 ? "0.0" : "0.000"), 2, h - 13);
            Txt(hist[hist.Count - 1].ToString(hist[hist.Count - 1] >= 10 ? "0.0" : "0.000"), w - 46, 0);
        }

        private void AppendLog(string text)
        {
            Vm.AppendLog(text);
            TrainLog.ScrollToEnd();
        }
    }
}
