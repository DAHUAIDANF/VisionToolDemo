namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 页面接入主窗口的钩子：切到该页时安装自己的工具条、刷新状态。
    ///
    /// 为什么需要：主窗口顶部工具条是共享的，切换页面时必须跟着换
    /// （否则切到"视觉"页看到的还是"工作流"页的运行/校验按钮，用户找不到"打开图片"）。
    /// </summary>
    public interface IShellPage
    {
        void OnShown(MainWindow shell);
    }
}
